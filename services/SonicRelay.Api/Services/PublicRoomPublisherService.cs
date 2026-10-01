using Microsoft.FeatureManagement;
using SonicRelay.Api.Features;
using System.Collections.Concurrent;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using SonicRelay.Domain.Sessions;
using SonicRelay.Infrastructure.Persistence;
using SonicRelay.Infrastructure.VirtualPublisher.Audio;
using SonicRelay.Infrastructure.VirtualPublisher.WebRtc;
using SIPSorcery.Net;

namespace SonicRelay.Api.Services;

/// <summary>
/// Public radio room virtual publisher (docs/superpowers/specs/2026-08-19-public-radio-room-design.md):
/// seeds the well-known publisher device/session, connects to this API's own
/// /ws/signaling as a headless client, and streams the looping MP3 playlist to
/// every viewer who joins. Disabled by default; a false PublicRoom:Enabled means
/// this does nothing at all (same pattern as DataRetentionService).
///
/// The outer loop follows the same house pattern as DataRetentionService /
/// SessionCleanupService: nothing that can go wrong here (a DB blip at boot, the
/// signaling connection dropping, a decode failure) is allowed to escape
/// ExecuteAsync, because an unhandled exception there is host-fatal
/// (BackgroundServiceExceptionBehavior.StopHost, the ASP.NET Core default, and
/// nothing in this codebase overrides it). Any non-cancellation failure is logged
/// and the whole connect+stream sequence is retried after a short delay, so the
/// radio recovers on its own instead of staying dead until the process restarts.
/// </summary>
public sealed class PublicRoomPublisherService(
    IServiceScopeFactory scopeFactory,
    IOptions<PublicRoomOptions> options,
    PublicRoomSeeder seeder,
    DeviceCredentialService credentials,
    IServer server,
    TimeProvider time,
    IVariantFeatureManager features,
    ILogger<PublicRoomPublisherService> logger) : BackgroundService
{
    private static readonly TimeSpan ReconnectDelay = TimeSpan.FromSeconds(5);

    // Only used when the server exposes no usable address at all; matches the container's
    // ASPNETCORE_URLS port in docker-compose.
    private const int DefaultLoopbackPort = 8080;

    // ConcurrentDictionary, not Dictionary: AddViewerAsync/RemoveViewerAsync run on the
    // un-awaited receive-loop task while the frame-send loop concurrently enumerates
    // .Values — a plain Dictionary is not safe for that and can throw mid-iteration.
    private readonly ConcurrentDictionary<Guid, VirtualPublisherPeerConnection> peersByParticipantId = new();

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!options.Value.Enabled || !await features.IsEnabledAsync(RelayFeatures.PublicRooms))
        {
            logger.LogInformation("Public radio room is disabled; PublicRoomPublisherService is a no-op");
            return;
        }

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                if (!await features.IsEnabledAsync(RelayFeatures.PublicRooms)) return;
                await RunOnceAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception exception)
            {
                logger.LogError(exception,
                    "Public radio room publisher failed; retrying in {RetryDelaySeconds}s",
                    ReconnectDelay.TotalSeconds);
            }

            if (stoppingToken.IsCancellationRequested) return;
            try
            {
                await Task.Delay(ReconnectDelay, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    /// <summary>
    /// One attempt at seeding, minting a token, connecting to signaling and streaming until
    /// either cancellation, or something goes wrong (connect failure, the signaling receive
    /// loop dying because the server dropped the socket, or an exception from the streaming
    /// loop). Any of the latter returns/throws back to <see cref="ExecuteAsync"/>'s retry loop,
    /// which reconnects from scratch — re-seeding is idempotent and re-minting a token is cheap.
    /// </summary>
    private async Task RunOnceAsync(CancellationToken stoppingToken)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var session = await seeder.EnsureSeededAsync(db, time, stoppingToken);
        // EnsureSeededAsync only sets MaxViewers on first creation (see its comment); every
        // subsequent startup must still push the currently configured cap onto the row, since
        // SessionEndpoints' join-time enforcement (session.MaxViewers, not PublicRoomOptions)
        // is what actually gates HTTP joins. Without this, changing PublicRoom:MaxViewers after
        // the row already exists would silently have no effect on real viewer admission.
        if (session.MaxViewers != options.Value.EffectiveMaxViewers)
        {
            session.MaxViewers = options.Value.EffectiveMaxViewers;
            await db.SaveChangesAsync(stoppingToken);
        }
        var (token, _) = await seeder.IssuePublisherTokenAsync(db, credentials, time, stoppingToken);

        var baseUrl = ResolveLoopbackBaseUrl();
        await using var signaling = new PublicRoomSignalingClient(baseUrl, token);
        signaling.MessageReceived += (from, type, payload, ct) => HandleSignalingMessageAsync(signaling, from, type, payload, ct);

        await signaling.ConnectAsPublisherAsync(PublicRoomSeeder.PublicSessionId, stoppingToken);

        // The signaling server only announces NEW arrivals: a viewer that was already connected
        // (or is mid-reconnect-grace-period) when this publisher connects never produces a
        // session.joined/participant.reconnected for us, so it would never be offered a stream.
        // That is exactly what happens on every publisher reconnect — the socket drops, the retry
        // loop above reconnects, and every viewer that stayed put would be silently stranded.
        // Re-announcing to the participants the DB already knows about closes that hole; a viewer
        // that receives a duplicate publisher.ready simply answers viewer.ready again, and
        // AddViewerAsync is idempotent per participant id.
        await AnnounceReadyToExistingViewersAsync(signaling, db, stoppingToken);

        var decoder = new NLayerMp3Decoder();
        var trackLogger = scope.ServiceProvider.GetRequiredService<ILogger<Mp3TrackSource>>();
        var tracks = new Mp3TrackSource(options.Value.TracksPath, decoder, trackLogger);

        // A linked token lets either side promptly stop the other: if the receive loop dies
        // (server dropped the socket) the streaming loop must not keep running forever in the
        // background while ExecuteAsync's retry loop reconnects from scratch, and vice versa.
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
        var receiveLoop = signaling.RunReceiveLoopAsync(linkedCts.Token);
        var streamTask = StreamTracksAsync(tracks, linkedCts.Token);

        try
        {
            var completed = await Task.WhenAny(streamTask, receiveLoop);
            if (!linkedCts.IsCancellationRequested) await linkedCts.CancelAsync();

            await AwaitQuietlyAsync(streamTask);
            await AwaitQuietlyAsync(receiveLoop);

            // RunReceiveLoopAsync only returns (rather than staying open until cancellation) when
            // the socket was closed by the server/network — that's a lost connection, not a clean
            // shutdown, unless we were cancelled at the same moment.
            if (completed == receiveLoop && !stoppingToken.IsCancellationRequested)
            {
                throw new InvalidOperationException(
                    "Public radio room signaling connection was lost while streaming.");
            }
        }
        finally
        {
            // Best-effort per peer: one peer's DisposeAsync throwing must not stop the rest from
            // being cleaned up, nor prevent the dictionary clear below.
            foreach (var peer in peersByParticipantId.Values)
            {
                try
                {
                    await peer.DisposeAsync();
                }
                catch (Exception exception)
                {
                    logger.LogWarning(exception,
                        "Failed to dispose public room viewer peer connection for {ViewerParticipantId} during cleanup",
                        peer.ViewerParticipantId);
                }
            }
            peersByParticipantId.Clear();
        }
    }

    private static async Task AwaitQuietlyAsync(Task task)
    {
        try
        {
            await task;
        }
        catch (OperationCanceledException)
        {
            // Expected: this task was stopped by the linked cancellation above, not a real fault.
        }
    }

    /// <summary>
    /// Decodes and sends frames at roughly real-time speed. Without this, decoding runs at full
    /// CPU speed and floods RtpPacketPacer's ~200ms buffer far faster than audio can play,
    /// causing it to drop the vast majority of frames before they ever reach a viewer. This is a
    /// simple software audio clock: it tracks how much audio has been handed off so far and
    /// sleeps off the difference against a wall-clock start, no drift-correction needed for an
    /// MP3 playlist loop.
    /// </summary>
    private async Task StreamTracksAsync(Mp3TrackSource tracks, CancellationToken stoppingToken)
    {
        var clockStart = time.GetTimestamp();
        var playedDuration = TimeSpan.Zero;

        foreach (var frame in tracks.ReadForever(stoppingToken))
        {
            if (stoppingToken.IsCancellationRequested) break;
            var bytes = new byte[frame.Samples.Length * 2];
            Buffer.BlockCopy(frame.Samples, 0, bytes, 0, bytes.Length);
            // frame.SampleRate/Channels come from the MP3's real native format
            // (NLayerMp3Decoder reports it), not a hardcoded 48kHz/stereo assumption —
            // OpusFrameAccumulator (inside VirtualPublisherPeerConnection) resamples
            // and up/down-mixes from whatever this actually is.
            var audioFrame = new WebRtcAudioFrame(bytes, frame.SampleRate, frame.Channels);
            foreach (var peer in peersByParticipantId.Values)
            {
                try
                {
                    await peer.SendAudioFrameAsync(audioFrame, stoppingToken);
                }
                catch (Exception exception)
                {
                    logger.LogWarning(exception,
                        "Failed to send an audio frame to public room viewer {ViewerParticipantId}",
                        peer.ViewerParticipantId);
                }
            }

            if (frame.Channels > 0 && frame.SampleRate > 0)
            {
                var samplesPerChannel = frame.Samples.Length / frame.Channels;
                playedDuration += TimeSpan.FromSeconds(samplesPerChannel / (double)frame.SampleRate);
            }

            var delay = playedDuration - time.GetElapsedTime(clockStart);
            if (delay > TimeSpan.Zero)
            {
                await Task.Delay(delay, stoppingToken);
            }
        }
    }

    internal async Task HandleSignalingMessageAsync(
        PublicRoomSignalingClient signaling, Guid fromParticipantId, string type,
        System.Text.Json.JsonElement payload, CancellationToken ct)
    {
        try
        {
            switch (type)
            {
                // First half of the handshake (docs/protocol.md, "Fluxo do Publisher" step 4):
                // the publisher only learns a viewer exists from the server's announcement, and
                // the viewer only learns the publisher's participant id from the authenticated
                // `from` of a publisher.ready. Without answering these two announcements the whole
                // flow deadlocks: the viewer waits for publisher.ready, and the viewer.ready case
                // below (which is what actually creates the peer connection and sends the offer)
                // never fires. session.joined covers a fresh join, participant.reconnected the
                // same viewer coming back inside the disconnect grace period — both carry
                // `{ participantId, role }` with `from` set to the announced participant.
                case "session.joined":
                case "participant.reconnected":
                    await HandleParticipantAnnouncedAsync(signaling, fromParticipantId, payload, ct);
                    break;
                case "viewer.ready":
                    await AddViewerAsync(signaling, fromParticipantId, ct);
                    break;
                case "webrtc.answer":
                    if (peersByParticipantId.TryGetValue(fromParticipantId, out var answerPeer))
                    {
                        var sdp = payload.GetProperty("sdp").GetString()!;
                        await answerPeer.ApplyAnswerAsync(new WebRtcSessionDescription("answer", sdp), ct);
                    }
                    break;
                case "webrtc.ice_candidate":
                    if (peersByParticipantId.TryGetValue(fromParticipantId, out var icePeer))
                    {
                        var candidate = payload.GetProperty("candidate").GetString()!;
                        var sdpMid = payload.TryGetProperty("sdpMid", out var m) ? m.GetString() : null;
                        var sdpMLineIndex = payload.TryGetProperty("sdpMLineIndex", out var idx) ? idx.GetInt32() : (int?)null;
                        await icePeer.AddRemoteIceCandidateAsync(
                            new WebRtcIceCandidate(candidate, sdpMid, sdpMLineIndex), ct);
                    }
                    break;
                // SignalingWebSocketEndpoint.HandleDisconnectAsync broadcasts one of these to every
                // other participant (including this publisher) whenever a viewer's signaling
                // socket drops: "participant.disconnected" when a reconnect grace period starts,
                // "session.left" once the participant is finalized as gone (either immediately, or
                // after the grace period elapses without a reconnect). Both carry
                // `{ participantId }` as payload with `from` set to the departed participant — see
                // SignalingWebSocketEndpoint.cs. Neither was previously handled, which permanently
                // leaked that viewer's peer connection (RTP pacer task, Opus encoder, ICE sockets)
                // and left a stale entry in peersByParticipantId that blocked both new viewers
                // (once EffectiveMaxViewers had ever been reached cumulatively) and that same
                // viewer's own reconnect (AddViewerAsync's ContainsKey short-circuits on the stale
                // entry). Disposing and removing the peer here for both message types lets a
                // grace-period reconnect (which typically re-negotiates WebRTC from the browser
                // anyway) and a final departure both free the slot and clear the way for a fresh
                // offer on the next "viewer.ready".
                case "session.left":
                case "participant.disconnected":
                    await RemoveViewerAsync(fromParticipantId);
                    break;
            }
        }
        catch (Exception exception)
        {
            logger.LogWarning(exception, "Error handling {MessageType} from viewer {ViewerParticipantId}",
                type, fromParticipantId);
        }
    }

    /// <summary>
    /// Answers a server announcement about another participant with a publisher.ready addressed
    /// to them, so the viewer can learn this publisher's participant id and reply viewer.ready.
    /// Announcements about a publisher (this service's own self-announcement, or in principle any
    /// other publisher row) are ignored — only viewers get offered a stream.
    /// </summary>
    private async Task HandleParticipantAnnouncedAsync(
        PublicRoomSignalingClient signaling, Guid fromParticipantId,
        System.Text.Json.JsonElement payload, CancellationToken ct)
    {
        // The self-announcement carries `from: null` (Guid.Empty here), and there is no one to
        // address a publisher.ready to in that case. ConnectAsPublisherAsync normally consumes
        // that first frame before the receive loop starts, so this is belt-and-braces.
        if (fromParticipantId == Guid.Empty) return;
        if (payload.ValueKind != System.Text.Json.JsonValueKind.Object) return;
        var role = payload.TryGetProperty("role", out var roleProperty) ? roleProperty.GetString() : null;
        if (!string.Equals(role, ParticipantRoles.Viewer, StringComparison.Ordinal)) return;

        logger.LogInformation("Public radio room announcing publisher.ready to viewer {ViewerParticipantId}",
            fromParticipantId);
        await signaling.SendAsync(fromParticipantId, "publisher.ready", new { }, ct);
    }

    /// <summary>
    /// Sends publisher.ready to every viewer the DB still considers part of the public session
    /// (connected, or reconnecting inside the grace period). See the call site in
    /// <see cref="RunOnceAsync"/> for why this is needed on every connect, not just the first.
    /// </summary>
    internal async Task AnnounceReadyToExistingViewersAsync(
        PublicRoomSignalingClient signaling, AppDbContext db, CancellationToken ct)
    {
        var viewerParticipantIds = await db.SessionParticipants.AsNoTracking()
            .Where(x => x.SessionId == PublicRoomSeeder.PublicSessionId
                && x.Role == ParticipantRoles.Viewer
                && (x.Status == ParticipantStatuses.Connected || x.Status == ParticipantStatuses.Reconnecting))
            .Select(x => x.Id)
            .ToListAsync(ct);

        foreach (var viewerParticipantId in viewerParticipantIds)
        {
            try
            {
                await signaling.SendAsync(viewerParticipantId, "publisher.ready", new { }, ct);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                // A stale row (participant already gone server-side) must not abort the rest of
                // the announcements — the server just answers with an `error` frame we ignore.
                logger.LogWarning(exception,
                    "Failed to re-announce publisher.ready to existing public room viewer {ViewerParticipantId}",
                    viewerParticipantId);
            }
        }
    }

    private async Task AddViewerAsync(PublicRoomSignalingClient signaling, Guid viewerParticipantId, CancellationToken ct)
    {
        if (peersByParticipantId.ContainsKey(viewerParticipantId)) return;
        if (peersByParticipantId.Count >= options.Value.EffectiveMaxViewers) return; // SessionEndpoints already enforced this at join time

        // Without ICE servers SIPSorcery only gathers host candidates — inside Docker that means
        // the bridge subnet, which is unreachable from any real viewer and which coturn is
        // configured to refuse relaying to. Reusing TurnCredentialService (the same source the
        // /api/webrtc/ice-servers endpoint serves to real devices) gives this publisher the same
        // STUN + time-limited-TURN set every other peer gets.
        var configuration = new RTCConfiguration { iceServers = await BuildIceServersAsync(ct) };
        var connection = new RTCPeerConnection(configuration);
        var peer = new VirtualPublisherPeerConnection(viewerParticipantId.ToString(), connection);
        peer.LocalIceCandidateReady += async (candidate, candidateCt) =>
            await signaling.SendAsync(viewerParticipantId, "webrtc.ice_candidate",
                new { candidate = candidate.Candidate, sdpMid = candidate.SdpMid, sdpMLineIndex = candidate.SdpMLineIndex },
                candidateCt);

        peersByParticipantId[viewerParticipantId] = peer;
        var offer = await peer.CreateOfferAsync(ct);
        await signaling.SendAsync(viewerParticipantId, "webrtc.offer", new { sdp = offer.Sdp }, ct);
    }

    private async Task RemoveViewerAsync(Guid viewerParticipantId)
    {
        if (!peersByParticipantId.TryRemove(viewerParticipantId, out var peer)) return;
        try
        {
            await peer.DisposeAsync();
        }
        catch (Exception exception)
        {
            logger.LogWarning(exception,
                "Failed to dispose public room viewer peer connection for {ViewerParticipantId} after disconnect",
                viewerParticipantId);
        }
    }

    /// <summary>
    /// Builds the STUN/TURN list for one viewer's peer connection. Deliberately called per
    /// viewer-add rather than once at startup: coturn's REST credentials are time-limited
    /// (TurnOptions.CredentialTtlSeconds), and the radio is expected to run for far longer than
    /// one TTL, so a set captured at startup would go stale and silently stop relaying.
    /// </summary>
    private async Task<List<RTCIceServer>> BuildIceServersAsync(CancellationToken ct)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var turn = scope.ServiceProvider.GetRequiredService<TurnCredentialService>();
        var iceServers = await turn.BuildAsync(PublicRoomSeeder.VirtualPublisherDeviceId.ToString("D"), ct);

        // IceServerEntry carries a URL list per entry; SIPSorcery's RTCIceServer.urls is a single
        // string, so each URL becomes its own entry (sharing the entry's credentials).
        return [.. iceServers.IceServers
            .SelectMany(entry => entry.Urls
                .Where(url => !string.IsNullOrWhiteSpace(url))
                .Select(url => new RTCIceServer
                {
                    urls = url,
                    username = entry.Username,
                    credential = entry.Credential
                }))];
    }

    /// <summary>
    /// The address this process' own HTTP server is listening on, rewritten as a directly dialable
    /// loopback URL. Kestrel's registered addresses are bind specifications, not dial targets:
    /// they are commonly wildcards (`http://+:8080`, `http://0.0.0.0:8080`, `http://[::]:8080`)
    /// whose host part cannot be connected to as-is, and an HTTPS entry would need a certificate
    /// this in-process client has no reason to validate. So only the port is taken (preferring an
    /// http:// binding), and the host is always 127.0.0.1.
    /// </summary>
    private Uri ResolveLoopbackBaseUrl()
    {
        var addresses = server.Features.Get<IServerAddressesFeature>()?.Addresses ?? [];
        var httpAddress = addresses.FirstOrDefault(address =>
            address.StartsWith(Uri.UriSchemeHttp + "://", StringComparison.OrdinalIgnoreCase));
        var port = TryParsePort(httpAddress)
            ?? addresses.Select(TryParsePort).FirstOrDefault(parsed => parsed is not null);

        if (port is null)
        {
            logger.LogWarning(
                "No usable http:// server address was found ({AddressCount} registered); " +
                "falling back to the default public room signaling port {FallbackPort}",
                addresses.Count, DefaultLoopbackPort);
        }

        return new Uri(FormattableString.Invariant($"http://127.0.0.1:{port ?? DefaultLoopbackPort}/"));
    }

    /// <summary>
    /// Extracts the port from a Kestrel address string without going through <see cref="Uri"/>,
    /// which rejects the wildcard hosts Kestrel legitimately registers (`+`, `*`).
    /// </summary>
    private static int? TryParsePort(string? address)
    {
        if (string.IsNullOrWhiteSpace(address)) return null;
        var authority = address.AsSpan();
        var schemeEnd = authority.IndexOf("://", StringComparison.Ordinal);
        if (schemeEnd >= 0) authority = authority[(schemeEnd + 3)..];
        var pathStart = authority.IndexOf('/');
        if (pathStart >= 0) authority = authority[..pathStart];
        var portStart = authority.LastIndexOf(':');
        if (portStart < 0) return null;
        return int.TryParse(authority[(portStart + 1)..], out var port) && port is > 0 and <= 65535
            ? port
            : null;
    }
}
