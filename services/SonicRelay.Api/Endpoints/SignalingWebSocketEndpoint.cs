using System.Net.WebSockets;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using SonicRelay.Application.Abstractions;
using SonicRelay.Api.Services;
using SonicRelay.Domain.Sessions;
using SonicRelay.Infrastructure.Persistence;

namespace SonicRelay.Api.Endpoints;

public static class SignalingWebSocketEndpoint
{
    private const int MaxMessageBytes = 64 * 1024;
    private static readonly HashSet<string> RoutedMessageTypes =
    [
        "publisher.ready", "viewer.ready", "webrtc.offer", "webrtc.answer",
        "webrtc.ice_candidate", "webrtc.renegotiate", "video.receiver_stats", "pong"
    ];

    /// <summary>
    /// Client messages that describe the sender's own state rather than addressing one peer.
    /// They carry no "to": the server validates them, persists the resulting state and
    /// broadcasts the authoritative version to the whole session, so every peer sees the same
    /// capability and mute state without the sender having to fan it out itself.
    /// </summary>
    private const string CapabilitiesMessageType = "participant.capabilities";
    private const string AudioStateMessageType = "participant.audio_state_changed";
    private static readonly HashSet<string> SessionScopedMessageTypes =
    [
        CapabilitiesMessageType, AudioStateMessageType
    ];

    public static IEndpointRouteBuilder MapSignalingWebSocketEndpoint(this IEndpointRouteBuilder app)
    {
        app.MapGet("/ws/signaling", HandleAsync)
            .RequireAuthorization("signaling:connect");
        return app;
    }

    private static async Task HandleAsync(HttpContext context, AppDbContext db, IConnectionRegistry registry,
        IParticipantReconnectTracker reconnectTracker, IServiceScopeFactory scopeFactory, IConfiguration configuration,
        ILoggerFactory loggerFactory, Observability.SonicRelayMetrics metrics)
    {
        var logger = loggerFactory.CreateLogger("SonicRelay.Signaling");
        if (!context.WebSockets.IsWebSocketRequest)
        {
            context.Response.StatusCode = StatusCodes.Status400BadRequest;
            return;
        }

        if (!Guid.TryParse(context.Request.Query["sessionId"], out var sessionId))
        {
            context.Response.StatusCode = StatusCodes.Status400BadRequest;
            return;
        }

        var device = await DeviceIdentityEndpoints.RequireDeviceAsync(context.User, db, context.RequestAborted);
        if (device is null)
        {
            context.Response.StatusCode = StatusCodes.Status401Unauthorized;
            return;
        }

        var session = await db.StreamSessions.AsNoTracking()
            .SingleOrDefaultAsync(x => x.Id == sessionId, context.RequestAborted);
        if (session is null)
        {
            context.Response.StatusCode = StatusCodes.Status404NotFound;
            return;
        }
        // Code expiry only gates *new joins*; an established session stays alive until it is
        // ended or expired, so a publisher streaming past the code TTL is not cut off.
        if (session.Status is SessionStatuses.Ended or SessionStatuses.Expired)
        {
            logger.LogInformation("Rejected signaling connection to terminal session {SessionId} with status {SessionStatus}",
                sessionId, session.Status);
            context.Response.StatusCode = StatusCodes.Status410Gone;
            return;
        }

        // Oldest-first FirstOrDefault, not SingleOrDefault: duplicates are ruled out going
        // forward by the unique index on (SessionId, DeviceId, Role), but a row pair written
        // before it existed must not turn every reconnect of that device into a 500 — the
        // device has no way to recover from that, and the session is still perfectly alive.
        var participant = await db.SessionParticipants
            .Where(x => x.SessionId == sessionId && x.DeviceId == device.Id)
            .OrderBy(x => x.JoinedAt)
            .FirstOrDefaultAsync(context.RequestAborted);
        if (participant is null)
        {
            context.Response.StatusCode = StatusCodes.Status403Forbidden;
            return;
        }

        using var socket = await context.WebSockets.AcceptWebSocketAsync();
        using var socketSendLock = new SemaphoreSlim(1, 1);
        async Task SendFrameAsync(ReadOnlyMemory<byte> message, CancellationToken ct)
        {
            await socketSendLock.WaitAsync(ct);
            try
            {
                await socket.SendAsync(message, WebSocketMessageType.Text, true, ct);
            }
            finally
            {
                socketSendLock.Release();
            }
        }
        // A reconnect within the grace period reuses the same participant row (no duplicate),
        // so peers should be told this is a resumed connection rather than a brand-new join.
        var isGracePeriodReconnect = reconnectTracker.TryCancelGracePeriod(participant.Id);

        var connectionId = Guid.NewGuid().ToString("N");
        await registry.RegisterAsync(new ConnectionDescriptor(
            connectionId, sessionId, participant.Id, device.Id, participant.Role,
            DateTimeOffset.UtcNow,
            SendFrameAsync),
            context.RequestAborted);

        participant.ConnectionId = connectionId;
        participant.Status = ParticipantStatuses.Connected;
        participant.LeftAt = null;
        await db.SaveChangesAsync(context.RequestAborted);
        metrics.ConnectionOpened(sessionId);
        logger.LogInformation(
            "Connected signaling participant {ParticipantId} to session {SessionId} with connection {ConnectionId} (graceReconnect={IsGraceReconnect})",
            participant.Id, sessionId, connectionId, isGracePeriodReconnect);

        try
        {
            // The join payload carries the participant's audio capabilities alongside the
            // long-standing participantId/role pair, so a client knows whether it may publish
            // audio (and what its peers may do) before any SDP is exchanged. Clients written
            // before duplex existed simply ignore the extra fields.
            await SendEnvelopeAsync(SendFrameAsync, "session.joined", sessionId, null, participant.Id,
                CapabilitiesPayload(participant, session.Mode), context.RequestAborted);
            var peerAnnouncementType = isGracePeriodReconnect ? "participant.reconnected" : "session.joined";
            await BroadcastAsync(registry, sessionId, participant.Id, peerAnnouncementType, participant.Id,
                CapabilitiesPayload(participant, session.Mode), context.RequestAborted);
            await SendPeerRosterAsync(SendFrameAsync, db, registry, session, participant.Id, context.RequestAborted);
            await ReceiveLoopAsync(socket, SendFrameAsync, session, participant.Id, db, registry, logger, metrics,
                context.RequestAborted);
        }
        finally
        {
            metrics.ConnectionClosed(sessionId);
            await registry.UnregisterAsync(connectionId, CancellationToken.None);
            await HandleDisconnectAsync(db, registry, reconnectTracker, scopeFactory, configuration, logger,
                sessionId, participant.Id, connectionId);

            if (socket.State is WebSocketState.Open or WebSocketState.CloseReceived)
            {
                try
                {
                    await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "signaling closed", CancellationToken.None);
                }
                catch (WebSocketException)
                {
                    // The peer disconnected before completing the close handshake.
                }
            }
        }
    }

    /// <summary>
    /// On socket drop, a still-live session gets a reconnect grace period: peers are told the
    /// participant is transiently disconnected (not gone), and "session.left" only fires if the
    /// participant hasn't reconnected once the grace period elapses. A terminal session (or a
    /// zero/negative grace period) finalizes immediately, matching the prior behavior.
    /// </summary>
    private static async Task HandleDisconnectAsync(AppDbContext db, IConnectionRegistry registry,
        IParticipantReconnectTracker reconnectTracker, IServiceScopeFactory scopeFactory,
        IConfiguration configuration, ILogger logger, Guid sessionId, Guid participantId, string connectionId)
    {
        var sessionStillLive = !await SessionEndedAsync(db, sessionId, CancellationToken.None);
        var graceDuration = TimeSpan.FromSeconds(
            Math.Max(0, configuration.GetValue("Sessions:ParticipantDisconnectGraceSeconds", 15)));

        if (!sessionStillLive || graceDuration <= TimeSpan.Zero)
        {
            await FinalizeDisconnectAsync(db, participantId, connectionId);
            logger.LogInformation(
                "Disconnected signaling participant {ParticipantId} from session {SessionId} with connection {ConnectionId}",
                participantId, sessionId, connectionId);
            await BroadcastAsync(registry, sessionId, participantId, "session.left", participantId,
                new { participantId }, CancellationToken.None);
            return;
        }

        await MarkReconnectingAsync(db, participantId, connectionId);
        logger.LogInformation(
            "Participant {ParticipantId} disconnected from session {SessionId} with connection {ConnectionId}; starting a {GraceSeconds}s reconnect grace period",
            participantId, sessionId, connectionId, graceDuration.TotalSeconds);
        await BroadcastAsync(registry, sessionId, participantId, "participant.disconnected", participantId,
            new { participantId }, CancellationToken.None);

        reconnectTracker.BeginGracePeriod(participantId, graceDuration, async () =>
        {
            // The request's scoped AppDbContext is disposed by the time this fires, so a fresh
            // scope is required; the connection registry is a singleton and safe to reuse.
            await using var scope = scopeFactory.CreateAsyncScope();
            var scopedDb = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var finalized = await FinalizeDisconnectAsync(scopedDb, participantId, connectionId);
            if (!finalized) return;

            logger.LogInformation(
                "Participant {ParticipantId} did not reconnect within the grace period for session {SessionId}; marking as left",
                participantId, sessionId);
            await BroadcastAsync(registry, sessionId, participantId, "session.left", participantId,
                new { participantId }, CancellationToken.None);
        });
    }

    private static async Task ReceiveLoopAsync(WebSocket socket,
        Func<ReadOnlyMemory<byte>, CancellationToken, Task> sendAsync, StreamSession session, Guid participantId,
        AppDbContext db, IConnectionRegistry registry, ILogger logger, Observability.SonicRelayMetrics metrics,
        CancellationToken ct)
    {
        var sessionId = session.Id;
        while (socket.State == WebSocketState.Open && !ct.IsCancellationRequested)
        {
            using var receiveCancellation = CancellationTokenSource.CreateLinkedTokenSource(ct);
            var receiveTask = ReceiveMessageAsync(socket, receiveCancellation.Token);
            while (!receiveTask.IsCompleted)
            {
                var completed = await Task.WhenAny(receiveTask, Task.Delay(TimeSpan.FromSeconds(1), ct));
                if (completed != receiveTask && await SessionEndedAsync(db, sessionId, ct))
                {
                    await SendEnvelopeAsync(sendAsync, "session.ended", sessionId, null, participantId, null, ct);
                    await receiveCancellation.CancelAsync();
                    try { await receiveTask; } catch (OperationCanceledException) { }
                    return;
                }
            }

            var message = await receiveTask;
            if (message is null) return;
            if (!await HandleMessageAsync(sendAsync, session, participantId, message, db, registry, logger, metrics, ct))
                return;
        }
    }

    private static async Task<bool> HandleMessageAsync(Func<ReadOnlyMemory<byte>, CancellationToken, Task> sendAsync,
        StreamSession session, Guid participantId, byte[] message, AppDbContext db, IConnectionRegistry registry, ILogger logger,
        Observability.SonicRelayMetrics metrics, CancellationToken ct)
    {
        var sessionId = session.Id;
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(message);
        }
        catch (JsonException)
        {
            await SendErrorAsync(metrics, sendAsync, sessionId, participantId, "invalid_message", ct);
            return true;
        }

        using (document)
        {
            var root = document.RootElement;
            if (!root.TryGetProperty("type", out var typeElement) || typeElement.ValueKind != JsonValueKind.String)
            {
                await SendErrorAsync(metrics, sendAsync, sessionId, participantId, "invalid_message", ct);
                return true;
            }

            var type = typeElement.GetString()!;
            if (type == "ping")
            {
                metrics.RecordMessage("ping");
                await SendEnvelopeAsync(sendAsync, "pong", sessionId, null, participantId, null, ct);
                return true;
            }
            if (!RoutedMessageTypes.Contains(type) && !SessionScopedMessageTypes.Contains(type))
            {
                await SendErrorAsync(metrics, sendAsync, sessionId, participantId, "unsupported_message_type", ct);
                return true;
            }
            // `type` is now guaranteed to be one of the bounded message-type sets, so it is
            // safe to use as a metric label without risking cardinality blow-up.
            metrics.RecordMessage(type);

            if (SessionScopedMessageTypes.Contains(type))
            {
                var declaredPayload = root.TryGetProperty("payload", out var stateElement)
                    ? stateElement
                    : (JsonElement?)null;
                if (await SessionEndedAsync(db, sessionId, ct))
                {
                    logger.LogInformation("Closing signaling for terminal session {SessionId} and participant {ParticipantId}",
                        sessionId, participantId);
                    await SendEnvelopeAsync(sendAsync, "session.ended", sessionId, null, participantId, null, ct);
                    return false;
                }
                await HandleParticipantStateAsync(sendAsync, session, participantId, type, declaredPayload, db,
                    registry, logger, metrics, ct);
                return true;
            }

            if (!root.TryGetProperty("to", out var toElement) || !toElement.TryGetGuid(out var toParticipantId))
            {
                await SendErrorAsync(metrics, sendAsync, sessionId, participantId, "invalid_recipient", ct);
                return true;
            }

            if (await SessionEndedAsync(db, sessionId, ct))
            {
                logger.LogInformation("Closing signaling for terminal session {SessionId} and participant {ParticipantId}",
                    sessionId, participantId);
                await SendEnvelopeAsync(sendAsync, "session.ended", sessionId, null, participantId, null, ct);
                return false;
            }

            var messageId = root.TryGetProperty("messageId", out var messageIdElement)
                && messageIdElement.TryGetGuid(out var suppliedMessageId)
                ? suppliedMessageId
                : Guid.NewGuid();
            // SDP and ICE are intentionally opaque here: SDP describes the peer session, while ICE candidates
            // are network paths discovered by the peers. The signaling server only forwards this JSON.
            var payload = root.TryGetProperty("payload", out var payloadElement)
                ? payloadElement.Clone()
                : (JsonElement?)null;
            // The authenticated socket owns the sender identity, so client-supplied `from` is overwritten.
            var routed = SerializeEnvelope(type, messageId, sessionId, participantId, toParticipantId, payload);
            var delivered = await registry.SendToParticipantAsync(sessionId, toParticipantId, routed, ct);
            if (!delivered)
            {
                await SendErrorAsync(metrics, sendAsync, sessionId, participantId, "participant_not_found", ct);
                return true;
            }

            // SDP and ICE can expose media and network details, so only envelope metadata is logged.
            logger.LogDebug("Routed signaling message {MessageType} in session {SessionId} from {FromParticipantId} to {ToParticipantId} with message {MessageId}",
                type, sessionId, participantId, toParticipantId, messageId);
            return true;
        }
    }

    /// <summary>
    /// Applies a participant's own capability or mute announcement and republishes the result to
    /// the whole session, sender included. The server is the authority here: a participant that
    /// the backend has not authorized to publish audio cannot talk its way into
    /// <c>canSendAudio: true</c>, and every peer learns the same state from the server rather
    /// than from each other.
    /// </summary>
    private static async Task HandleParticipantStateAsync(
        Func<ReadOnlyMemory<byte>, CancellationToken, Task> sendAsync, StreamSession session, Guid participantId,
        string type, JsonElement? payload, AppDbContext db, IConnectionRegistry registry, ILogger logger,
        Observability.SonicRelayMetrics metrics, CancellationToken ct)
    {
        var participant = await db.SessionParticipants.SingleOrDefaultAsync(x => x.Id == participantId, ct);
        if (participant is not null)
        {
            // This AppDbContext is scoped to the whole WebSocket connection, so its tracked copy
            // of the row can be minutes old — long enough for the session owner to have revoked
            // this participant's publish permission over HTTP in the meantime. Without a reload
            // the identity map would answer the authorization check with the permission the
            // participant had when it connected, and revocation would not take effect until the
            // socket was reopened.
            await db.Entry(participant).ReloadAsync(ct);
            if (db.Entry(participant).State == EntityState.Detached) participant = null;
        }
        if (participant is null)
        {
            await SendErrorAsync(metrics, sendAsync, session.Id, participantId, "participant_not_found", ct);
            return;
        }

        if (type == CapabilitiesMessageType)
        {
            var requestedSend = ReadBoolean(payload, "canSendAudio");
            var requestedReceive = ReadBoolean(payload, "canReceiveAudio");
            if (requestedSend is null && requestedReceive is null)
            {
                await SendErrorAsync(metrics, sendAsync, session.Id, participantId, "invalid_message", ct);
                return;
            }
            // The one hard rule of duplex: publishing is a backend decision. Refusing the whole
            // message (rather than silently clamping it) is what lets a client tell "the server
            // says no" apart from "the server accepted what I asked for".
            if (requestedSend == true && !participant.AudioSendAllowed)
            {
                logger.LogInformation(
                    "Rejected an audio publish capability from participant {ParticipantId} in session {SessionId}: not authorized",
                    participantId, session.Id);
                await SendErrorAsync(metrics, sendAsync, session.Id, participantId, "audio_send_not_authorized", ct);
                return;
            }
            if (requestedSend.HasValue) participant.CanSendAudio = requestedSend.Value;
            if (requestedReceive.HasValue) participant.CanReceiveAudio = requestedReceive.Value;
        }
        else
        {
            var muted = ReadBoolean(payload, "muted");
            if (muted is null)
            {
                await SendErrorAsync(metrics, sendAsync, session.Id, participantId, "invalid_message", ct);
                return;
            }
            participant.AudioMuted = muted.Value;
        }

        await db.SaveChangesAsync(ct);
        // Nothing about the media itself is inspected or logged here — only which participant
        // changed state, exactly as the routing logs do for SDP and ICE.
        logger.LogDebug(
            "Applied {MessageType} for participant {ParticipantId} in session {SessionId}",
            type, participantId, session.Id);
        await BroadcastToSessionAsync(registry, session.Id, type, participantId,
            CapabilitiesPayload(participant, session.Mode), ct);
    }

    /// <summary>
    /// Tells a freshly connected participant what the peers already in the session can do. Peers
    /// that join later announce themselves through <c>session.joined</c>, but the ones already
    /// connected would otherwise never be described to the newcomer — which in a duplex session
    /// is exactly what it needs to know before deciding whether to expect audio from them.
    /// </summary>
    private static async Task SendPeerRosterAsync(Func<ReadOnlyMemory<byte>, CancellationToken, Task> sendAsync,
        AppDbContext db, IConnectionRegistry registry, StreamSession session, Guid participantId, CancellationToken ct)
    {
        var connections = await registry.ListBySessionAsync(session.Id, ct);
        var peerIds = connections.Select(x => x.ParticipantId).Where(x => x != participantId).Distinct().ToList();
        if (peerIds.Count == 0) return;

        var peers = await db.SessionParticipants.AsNoTracking()
            .Where(x => peerIds.Contains(x.Id))
            .OrderBy(x => x.JoinedAt)
            .ToListAsync(ct);
        foreach (var peer in peers)
        {
            await SendEnvelopeAsync(sendAsync, CapabilitiesMessageType, session.Id, peer.Id, participantId,
                CapabilitiesPayload(peer, session.Mode), ct);
        }
    }

    /// <summary>
    /// Republishes a participant's audio state after the session owner changed it over HTTP
    /// (<c>POST /api/sessions/{id}/participants/{participantId}/audio-permission</c>). The
    /// revoked participant is deliberately included: it has to stop publishing, and finding out
    /// only on its next capability announcement would leave audio flowing in the meantime.
    /// </summary>
    internal static Task BroadcastCapabilitiesAsync(IConnectionRegistry registry, StreamSession session,
        SessionParticipant participant, CancellationToken ct) =>
        BroadcastToSessionAsync(registry, session.Id, CapabilitiesMessageType, participant.Id,
            CapabilitiesPayload(participant, session.Mode), ct);

    /// <summary>
    /// The single payload shape for everything that describes a participant:
    /// <c>session.joined</c>, <c>participant.reconnected</c>, <c>participant.capabilities</c> and
    /// <c>participant.audio_state_changed</c>. <c>participantId</c> and <c>role</c> keep the
    /// meaning they had before duplex existed, so older clients are unaffected by the rest.
    /// </summary>
    private static object CapabilitiesPayload(SessionParticipant participant, string sessionMode) => new
    {
        participantId = participant.Id,
        role = participant.Role,
        sessionMode,
        audioSendAllowed = participant.AudioSendAllowed,
        canSendAudio = participant.CanSendAudio,
        canReceiveAudio = participant.CanReceiveAudio,
        audioMuted = participant.AudioMuted
    };

    private static bool? ReadBoolean(JsonElement? payload, string property) =>
        payload is { ValueKind: JsonValueKind.Object } element
            && element.TryGetProperty(property, out var value)
            && value.ValueKind is JsonValueKind.True or JsonValueKind.False
                ? value.GetBoolean()
                : null;

    private static async Task<byte[]?> ReceiveMessageAsync(WebSocket socket, CancellationToken ct)
    {
        using var stream = new MemoryStream();
        var buffer = new byte[4096];
        while (true)
        {
            var result = await socket.ReceiveAsync(buffer, ct);
            if (result.MessageType == WebSocketMessageType.Close) return null;
            if (result.MessageType != WebSocketMessageType.Text)
                throw new WebSocketException(WebSocketError.InvalidMessageType);
            if (stream.Length + result.Count > MaxMessageBytes)
                throw new WebSocketException(WebSocketError.HeaderError, "Signaling message is too large.");
            await stream.WriteAsync(buffer.AsMemory(0, result.Count), ct);
            if (result.EndOfMessage) return stream.ToArray();
        }
    }

    private static async Task<bool> SessionEndedAsync(AppDbContext db, Guid sessionId, CancellationToken ct)
    {
        var state = await db.StreamSessions.AsNoTracking()
            .Where(x => x.Id == sessionId)
            .Select(x => new { x.Status })
            .SingleOrDefaultAsync(ct);
        return state is null
            || state.Status is SessionStatuses.Ended or SessionStatuses.Expired;
    }

    private static async Task MarkReconnectingAsync(AppDbContext db, Guid participantId, string connectionId)
    {
        try
        {
            var participant = await db.SessionParticipants.SingleOrDefaultAsync(x => x.Id == participantId);
            if (participant?.ConnectionId != connectionId) return;
            participant.ConnectionId = null;
            participant.Status = ParticipantStatuses.Reconnecting;
            await db.SaveChangesAsync();
        }
        catch (Exception exception) when (exception is DbUpdateException or OperationCanceledException)
        {
            // Connection cleanup must not mask the socket termination.
        }
    }

    /// <summary>Marks a participant as finally left. Returns false if a newer connection already claimed the row.</summary>
    private static async Task<bool> FinalizeDisconnectAsync(AppDbContext db, Guid participantId, string connectionId)
    {
        try
        {
            var participant = await db.SessionParticipants.SingleOrDefaultAsync(x => x.Id == participantId);
            if (participant is null) return false;
            // Already finalized (idempotent) or a different, still-live connection has since
            // claimed this participant; that connection's own lifecycle now owns its fate. A
            // null ConnectionId does NOT count as "claimed" here: the HTTP rejoin path
            // (SessionEndpoints.JoinAsync) sets Status back to Connected without touching
            // ConnectionId, so a client that rejoins over HTTP but crashes before reopening the
            // WebSocket would otherwise look "claimed" forever and never get finalized.
            if (participant.Status == ParticipantStatuses.Disconnected) return false;
            if (participant.Status == ParticipantStatuses.Connected
                && participant.ConnectionId is not null && participant.ConnectionId != connectionId)
                return false;
            participant.ConnectionId = null;
            participant.Status = ParticipantStatuses.Disconnected;
            participant.LeftAt = DateTimeOffset.UtcNow;
            await db.SaveChangesAsync();
            return true;
        }
        catch (Exception exception) when (exception is DbUpdateException or OperationCanceledException)
        {
            // Connection cleanup must not mask the socket termination.
            return false;
        }
    }

    /// <summary>Broadcast to every connection in the session, including the originating one.</summary>
    private static Task BroadcastToSessionAsync(IConnectionRegistry registry, Guid sessionId, string type,
        Guid? fromParticipantId, object? payload, CancellationToken ct) =>
        // Guid.Empty is never a participant id, so nothing is excluded.
        BroadcastAsync(registry, sessionId, Guid.Empty, type, fromParticipantId, payload, ct);

    private static async Task BroadcastAsync(IConnectionRegistry registry, Guid sessionId, Guid excludedParticipantId,
        string type, Guid? fromParticipantId, object? payload, CancellationToken ct)
    {
        var connections = await registry.ListBySessionAsync(sessionId, ct);
        foreach (var connection in connections.Where(x => x.ParticipantId != excludedParticipantId))
        {
            try
            {
                var bytes = SerializeEnvelope(type, Guid.NewGuid(), sessionId, fromParticipantId,
                    connection.ParticipantId, payload);
                await registry.SendToParticipantAsync(sessionId, connection.ParticipantId, bytes, ct);
            }
            catch (WebSocketException)
            {
                // A concurrent disconnect will be cleaned up by that connection's handler.
            }
        }
    }

    private static Task SendErrorAsync(Observability.SonicRelayMetrics metrics,
        Func<ReadOnlyMemory<byte>, CancellationToken, Task> sendAsync,
        Guid sessionId, Guid participantId, string code, CancellationToken ct)
    {
        metrics.RecordError(code);
        return SendEnvelopeAsync(sendAsync, "error", sessionId, null, participantId, new { code }, ct);
    }

    private static Task SendEnvelopeAsync(Func<ReadOnlyMemory<byte>, CancellationToken, Task> sendAsync,
        string type, Guid sessionId, Guid? fromParticipantId, Guid? toParticipantId, object? payload,
        CancellationToken ct) =>
        sendAsync(SerializeEnvelope(type, Guid.NewGuid(), sessionId, fromParticipantId, toParticipantId, payload), ct);

    private static byte[] SerializeEnvelope(string type, Guid messageId, Guid sessionId, Guid? fromParticipantId,
        Guid? toParticipantId, object? payload) => JsonSerializer.SerializeToUtf8Bytes(new
        {
            type,
            messageId,
            sessionId,
            from = fromParticipantId,
            to = toParticipantId,
            timestamp = DateTimeOffset.UtcNow,
            payload
        });
}
