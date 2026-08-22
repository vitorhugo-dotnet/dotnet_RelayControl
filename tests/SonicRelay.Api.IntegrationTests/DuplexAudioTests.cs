using System.Net;
using System.Net.Http.Json;
using System.Net.WebSockets;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using SonicRelay.Domain.DeviceIdentities;
using SonicRelay.Domain.Devices;
using SonicRelay.Domain.Sessions;
using SonicRelay.Infrastructure.Persistence;
using Xunit;

namespace SonicRelay.Api.IntegrationTests;

/// <summary>
/// Bidirectional audio (issue #22): duplex sessions, backend-owned publish permission, capability
/// and mute propagation over signaling, and the guarantee that one-way broadcast sessions keep
/// behaving exactly as they did before duplex existed.
/// </summary>
public sealed class DuplexAudioTests : IClassFixture<SonicRelayApiFactory>
{
    private readonly SonicRelayApiFactory _factory;

    public DuplexAudioTests(SonicRelayApiFactory factory) => _factory = factory;

    [Fact]
    public async Task Create_defaults_to_broadcast_and_leaves_the_publisher_send_only()
    {
        var (_, sessionId, _) = await CreateSessionAsync();

        var publisher = await GetParticipantAsync(sessionId, ParticipantRoles.Publisher);
        Assert.Equal(SessionModes.Broadcast, await GetSessionModeAsync(sessionId));
        Assert.True(publisher.AudioSendAllowed);
        Assert.True(publisher.CanSendAudio);
        Assert.False(publisher.CanReceiveAudio);
    }

    [Fact]
    public async Task Create_in_duplex_mode_lets_the_publisher_both_send_and_receive()
    {
        var (client, deviceId) = await BootstrapAsync(DeviceTypes.WindowsPublisher, DevicePlatforms.Windows);

        var response = await client.PostAsJsonAsync("/api/sessions", new { maxViewers = 2, mode = "Duplex " });
        var body = await ReadJsonAsync(response);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        Assert.Equal(SessionModes.Duplex, body.GetProperty("mode").GetString());
        Assert.Equal(deviceId, body.GetProperty("sourceDeviceId").GetGuid());
        var publisher = await GetParticipantAsync(body.GetProperty("id").GetGuid(), ParticipantRoles.Publisher);
        Assert.True(publisher.AudioSendAllowed);
        Assert.True(publisher.CanSendAudio);
        Assert.True(publisher.CanReceiveAudio);
    }

    [Fact]
    public async Task Create_rejects_an_unknown_session_mode()
    {
        var (client, _) = await BootstrapAsync(DeviceTypes.WindowsPublisher, DevicePlatforms.Windows);

        var response = await client.PostAsJsonAsync("/api/sessions", new { mode = "mesh" });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("invalid_session_mode", (await ReadJsonAsync(response)).GetProperty("code").GetString());
    }

    [Fact]
    public async Task Joining_a_duplex_session_authorizes_the_new_participant_to_publish_audio()
    {
        var (_, sessionId, code) = await CreateSessionAsync(SessionModes.Duplex);
        var viewerClient = await JoinAsync(sessionId, code);

        var viewer = await GetParticipantAsync(sessionId, ParticipantRoles.Viewer);
        Assert.True(viewer.AudioSendAllowed);
        Assert.True(viewer.CanSendAudio);
        Assert.True(viewer.CanReceiveAudio);
        Assert.NotNull(viewerClient);
    }

    [Fact]
    public async Task Joining_a_broadcast_session_leaves_the_new_participant_receive_only()
    {
        var (_, sessionId, code) = await CreateSessionAsync();
        await JoinAsync(sessionId, code);

        var viewer = await GetParticipantAsync(sessionId, ParticipantRoles.Viewer);
        Assert.False(viewer.AudioSendAllowed);
        Assert.False(viewer.CanSendAudio);
        Assert.True(viewer.CanReceiveAudio);
    }

    [Fact]
    public async Task Participants_endpoint_reports_the_mode_and_every_participants_capabilities()
    {
        var (owner, sessionId, code) = await CreateSessionAsync(SessionModes.Duplex);
        await JoinAsync(sessionId, code);

        var response = await owner.GetAsync($"/api/sessions/{sessionId}/participants");
        var body = await ReadJsonAsync(response);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(SessionModes.Duplex, body.GetProperty("mode").GetString());
        var participants = body.GetProperty("participants").EnumerateArray().ToList();
        Assert.Equal(2, participants.Count);
        Assert.All(participants, participant =>
        {
            Assert.True(participant.GetProperty("audioSendAllowed").GetBoolean());
            Assert.True(participant.GetProperty("canSendAudio").GetBoolean());
            Assert.False(participant.GetProperty("audioMuted").GetBoolean());
            // Device ids are never projected: participant ids are what signaling addresses.
            Assert.False(participant.TryGetProperty("deviceId", out _));
        });
        Assert.Single(participants, x => x.GetProperty("isSelf").GetBoolean());
    }

    [Fact]
    public async Task Participants_endpoint_hides_a_session_the_caller_does_not_take_part_in()
    {
        var (_, sessionId, _) = await CreateSessionAsync(SessionModes.Duplex);
        var (stranger, _) = await BootstrapAsync(DeviceTypes.FlutterViewer, DevicePlatforms.Android);

        var response = await stranger.GetAsync($"/api/sessions/{sessionId}/participants");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Owner_can_revoke_a_duplex_participants_permission_to_publish_audio()
    {
        var (owner, sessionId, code) = await CreateSessionAsync(SessionModes.Duplex);
        await JoinAsync(sessionId, code);
        var viewer = await GetParticipantAsync(sessionId, ParticipantRoles.Viewer);

        var response = await owner.PostAsJsonAsync(
            $"/api/sessions/{sessionId}/participants/{viewer.Id}/audio-permission", new { canSendAudio = false });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await ReadJsonAsync(response);
        Assert.False(body.GetProperty("audioSendAllowed").GetBoolean());
        Assert.False(body.GetProperty("canSendAudio").GetBoolean());
        var stored = await GetParticipantAsync(sessionId, ParticipantRoles.Viewer);
        Assert.False(stored.AudioSendAllowed);
        Assert.False(stored.CanSendAudio);
    }

    [Fact]
    public async Task Audio_permission_is_rejected_on_a_broadcast_session()
    {
        var (owner, sessionId, code) = await CreateSessionAsync();
        await JoinAsync(sessionId, code);
        var viewer = await GetParticipantAsync(sessionId, ParticipantRoles.Viewer);

        var response = await owner.PostAsJsonAsync(
            $"/api/sessions/{sessionId}/participants/{viewer.Id}/audio-permission", new { canSendAudio = true });

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal("session_not_duplex", (await ReadJsonAsync(response)).GetProperty("code").GetString());
        Assert.False((await GetParticipantAsync(sessionId, ParticipantRoles.Viewer)).AudioSendAllowed);
    }

    [Fact]
    public async Task Audio_permission_is_rejected_for_a_device_that_does_not_own_the_session()
    {
        var (_, sessionId, code) = await CreateSessionAsync(SessionModes.Duplex);
        await JoinAsync(sessionId, code);
        var viewer = await GetParticipantAsync(sessionId, ParticipantRoles.Viewer);
        var (otherPublisher, _) = await BootstrapAsync(DeviceTypes.WindowsPublisher, DevicePlatforms.Windows);

        var response = await otherPublisher.PostAsJsonAsync(
            $"/api/sessions/{sessionId}/participants/{viewer.Id}/audio-permission", new { canSendAudio = false });

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.True((await GetParticipantAsync(sessionId, ParticipantRoles.Viewer)).AudioSendAllowed);
    }

    [Fact]
    public async Task Session_joined_reports_the_session_mode_and_the_participants_audio_capabilities()
    {
        var (_, sessionId, _) = await CreateSessionAsync(SessionModes.Duplex);
        var publisher = await ConnectAsync(sessionId, ParticipantRoles.Publisher);

        var joined = await ReceiveAsync(publisher.Socket);

        Assert.Equal("session.joined", joined.GetProperty("type").GetString());
        var payload = joined.GetProperty("payload");
        Assert.Equal(publisher.ParticipantId, payload.GetProperty("participantId").GetGuid());
        Assert.Equal(ParticipantRoles.Publisher, payload.GetProperty("role").GetString());
        Assert.Equal(SessionModes.Duplex, payload.GetProperty("sessionMode").GetString());
        Assert.True(payload.GetProperty("audioSendAllowed").GetBoolean());
        Assert.True(payload.GetProperty("canSendAudio").GetBoolean());
        Assert.True(payload.GetProperty("canReceiveAudio").GetBoolean());
        Assert.False(payload.GetProperty("audioMuted").GetBoolean());
        publisher.Socket.Dispose();
    }

    [Fact]
    public async Task A_connecting_participant_learns_the_capabilities_of_the_peers_already_connected()
    {
        var (_, sessionId, code) = await CreateSessionAsync(SessionModes.Duplex);
        await JoinAsync(sessionId, code);
        var publisher = await ConnectAsync(sessionId, ParticipantRoles.Publisher);
        await ReceiveAsync(publisher.Socket);

        var viewer = await ConnectAsync(sessionId, ParticipantRoles.Viewer);
        await ReceiveAsync(viewer.Socket); // its own session.joined

        var roster = await ReceiveAsync(viewer.Socket);
        Assert.Equal("participant.capabilities", roster.GetProperty("type").GetString());
        Assert.Equal(publisher.ParticipantId, roster.GetProperty("from").GetGuid());
        Assert.Equal(publisher.ParticipantId, roster.GetProperty("payload").GetProperty("participantId").GetGuid());
        Assert.True(roster.GetProperty("payload").GetProperty("audioSendAllowed").GetBoolean());
        publisher.Socket.Dispose();
        viewer.Socket.Dispose();
    }

    [Fact]
    public async Task Capabilities_from_an_authorized_duplex_participant_are_persisted_and_broadcast()
    {
        var (_, sessionId, code) = await CreateSessionAsync(SessionModes.Duplex);
        await JoinAsync(sessionId, code);
        var publisher = await ConnectAsync(sessionId, ParticipantRoles.Publisher);
        await ReceiveAsync(publisher.Socket);
        var viewer = await ConnectAsync(sessionId, ParticipantRoles.Viewer);
        await ReceiveAsync(viewer.Socket);
        await ReceiveAsync(viewer.Socket); // publisher roster
        await ReceiveAsync(publisher.Socket); // viewer's session.joined announcement

        await SendAsync(viewer.Socket, new
        {
            type = "participant.capabilities",
            payload = new { canSendAudio = true, canReceiveAudio = false }
        });

        var announced = await ReceiveAsync(publisher.Socket);
        Assert.Equal("participant.capabilities", announced.GetProperty("type").GetString());
        Assert.Equal(viewer.ParticipantId, announced.GetProperty("from").GetGuid());
        Assert.True(announced.GetProperty("payload").GetProperty("canSendAudio").GetBoolean());
        Assert.False(announced.GetProperty("payload").GetProperty("canReceiveAudio").GetBoolean());

        // The sender gets the authoritative copy too, so it never has to assume its own request
        // was applied verbatim.
        var echoed = await ReceiveAsync(viewer.Socket);
        Assert.Equal("participant.capabilities", echoed.GetProperty("type").GetString());
        Assert.True(echoed.GetProperty("payload").GetProperty("canSendAudio").GetBoolean());

        var stored = await GetParticipantAsync(sessionId, ParticipantRoles.Viewer);
        Assert.True(stored.CanSendAudio);
        Assert.False(stored.CanReceiveAudio);
        publisher.Socket.Dispose();
        viewer.Socket.Dispose();
    }

    [Fact]
    public async Task A_broadcast_viewer_cannot_declare_itself_able_to_publish_audio()
    {
        var (_, sessionId, code) = await CreateSessionAsync();
        await JoinAsync(sessionId, code);
        var viewer = await ConnectAsync(sessionId, ParticipantRoles.Viewer);
        await ReceiveAsync(viewer.Socket);

        await SendAsync(viewer.Socket, new
        {
            type = "participant.capabilities",
            payload = new { canSendAudio = true }
        });

        var error = await ReceiveAsync(viewer.Socket);
        Assert.Equal("error", error.GetProperty("type").GetString());
        Assert.Equal("audio_send_not_authorized", error.GetProperty("payload").GetProperty("code").GetString());
        Assert.False((await GetParticipantAsync(sessionId, ParticipantRoles.Viewer)).CanSendAudio);
        viewer.Socket.Dispose();
    }

    [Fact]
    public async Task A_participant_whose_permission_was_revoked_can_no_longer_declare_it()
    {
        var (owner, sessionId, code) = await CreateSessionAsync(SessionModes.Duplex);
        await JoinAsync(sessionId, code);
        var viewer = await ConnectAsync(sessionId, ParticipantRoles.Viewer);
        await ReceiveAsync(viewer.Socket);

        var revoked = await owner.PostAsJsonAsync(
            $"/api/sessions/{sessionId}/participants/{viewer.ParticipantId}/audio-permission",
            new { canSendAudio = false });
        Assert.Equal(HttpStatusCode.OK, revoked.StatusCode);

        // The revocation reaches the revoked participant itself, not just its peers: it has to
        // stop publishing without waiting to be told by the peer that stopped hearing it.
        var notified = await ReceiveAsync(viewer.Socket);
        Assert.Equal("participant.capabilities", notified.GetProperty("type").GetString());
        Assert.False(notified.GetProperty("payload").GetProperty("audioSendAllowed").GetBoolean());
        Assert.False(notified.GetProperty("payload").GetProperty("canSendAudio").GetBoolean());

        await SendAsync(viewer.Socket, new
        {
            type = "participant.capabilities",
            payload = new { canSendAudio = true }
        });

        var error = await ReceiveAsync(viewer.Socket);
        Assert.Equal("error", error.GetProperty("type").GetString());
        Assert.Equal("audio_send_not_authorized", error.GetProperty("payload").GetProperty("code").GetString());
        viewer.Socket.Dispose();
    }

    [Fact]
    public async Task Mute_and_unmute_are_persisted_and_propagated_to_peers()
    {
        var (_, sessionId, code) = await CreateSessionAsync(SessionModes.Duplex);
        await JoinAsync(sessionId, code);
        var publisher = await ConnectAsync(sessionId, ParticipantRoles.Publisher);
        await ReceiveAsync(publisher.Socket);
        var viewer = await ConnectAsync(sessionId, ParticipantRoles.Viewer);
        await ReceiveAsync(viewer.Socket);
        await ReceiveAsync(viewer.Socket); // publisher roster
        await ReceiveAsync(publisher.Socket); // viewer's session.joined announcement

        await SendAsync(viewer.Socket, new
        {
            type = "participant.audio_state_changed",
            payload = new { muted = true }
        });

        var muted = await ReceiveAsync(publisher.Socket);
        Assert.Equal("participant.audio_state_changed", muted.GetProperty("type").GetString());
        Assert.Equal(viewer.ParticipantId, muted.GetProperty("from").GetGuid());
        Assert.True(muted.GetProperty("payload").GetProperty("audioMuted").GetBoolean());
        await ReceiveAsync(viewer.Socket); // echo of its own state
        Assert.True((await GetParticipantAsync(sessionId, ParticipantRoles.Viewer)).AudioMuted);

        await SendAsync(viewer.Socket, new
        {
            type = "participant.audio_state_changed",
            payload = new { muted = false }
        });

        var unmuted = await ReceiveAsync(publisher.Socket);
        Assert.False(unmuted.GetProperty("payload").GetProperty("audioMuted").GetBoolean());
        await ReceiveAsync(viewer.Socket);
        Assert.False((await GetParticipantAsync(sessionId, ParticipantRoles.Viewer)).AudioMuted);
        publisher.Socket.Dispose();
        viewer.Socket.Dispose();
    }

    [Theory]
    [InlineData("participant.capabilities")]
    [InlineData("participant.audio_state_changed")]
    public async Task A_participant_state_message_without_a_usable_payload_is_an_invalid_message(string type)
    {
        var (_, sessionId, _) = await CreateSessionAsync(SessionModes.Duplex);
        var publisher = await ConnectAsync(sessionId, ParticipantRoles.Publisher);
        await ReceiveAsync(publisher.Socket);

        await SendAsync(publisher.Socket, new { type, payload = new { unrelated = "value" } });

        var error = await ReceiveAsync(publisher.Socket);
        Assert.Equal("error", error.GetProperty("type").GetString());
        Assert.Equal("invalid_message", error.GetProperty("payload").GetProperty("code").GetString());
        publisher.Socket.Dispose();
    }

    [Fact]
    public async Task Renegotiation_requests_are_routed_to_the_addressed_peer_without_recreating_the_session()
    {
        var (_, sessionId, code) = await CreateSessionAsync(SessionModes.Duplex);
        await JoinAsync(sessionId, code);
        var publisher = await ConnectAsync(sessionId, ParticipantRoles.Publisher);
        await ReceiveAsync(publisher.Socket);
        var viewer = await ConnectAsync(sessionId, ParticipantRoles.Viewer);
        await ReceiveAsync(viewer.Socket);
        await ReceiveAsync(viewer.Socket); // publisher roster
        await ReceiveAsync(publisher.Socket); // viewer's session.joined announcement

        await SendAsync(viewer.Socket, new
        {
            type = "webrtc.renegotiate",
            to = publisher.ParticipantId,
            payload = new { reason = "adding-microphone-track" }
        });

        var routed = await ReceiveAsync(publisher.Socket);
        Assert.Equal("webrtc.renegotiate", routed.GetProperty("type").GetString());
        Assert.Equal(viewer.ParticipantId, routed.GetProperty("from").GetGuid());
        Assert.Equal(publisher.ParticipantId, routed.GetProperty("to").GetGuid());
        Assert.Equal("adding-microphone-track", routed.GetProperty("payload").GetProperty("reason").GetString());
        Assert.Equal(SessionStatuses.Active, await GetSessionStatusAsync(sessionId));
        publisher.Socket.Dispose();
        viewer.Socket.Dispose();
    }

    [Fact]
    public async Task Two_authorized_participants_negotiate_audio_in_both_directions_over_one_session()
    {
        var (_, sessionId, code) = await CreateSessionAsync(SessionModes.Duplex);
        await JoinAsync(sessionId, code);
        var publisher = await ConnectAsync(sessionId, ParticipantRoles.Publisher);
        await ReceiveAsync(publisher.Socket);
        var viewer = await ConnectAsync(sessionId, ParticipantRoles.Viewer);
        await ReceiveAsync(viewer.Socket);
        await ReceiveAsync(viewer.Socket); // publisher roster
        await ReceiveAsync(publisher.Socket); // viewer's session.joined announcement

        // Both sides declare that they will send and receive on the same peer connection.
        foreach (var participant in new[] { publisher, viewer })
        {
            await SendAsync(participant.Socket, new
            {
                type = "participant.capabilities",
                payload = new { canSendAudio = true, canReceiveAudio = true }
            });
            var own = await ReceiveAsync(participant.Socket);
            Assert.Equal("participant.capabilities", own.GetProperty("type").GetString());
            Assert.True(own.GetProperty("payload").GetProperty("canSendAudio").GetBoolean());
            var peer = await ReceiveAsync(participant == publisher ? viewer.Socket : publisher.Socket);
            Assert.Equal(participant.ParticipantId, peer.GetProperty("from").GetGuid());
        }

        // One sendrecv negotiation carries both directions: the offer and the answer travel over
        // the single session, and the API forwards the SDP without inspecting it.
        await SendAsync(publisher.Socket, new
        {
            type = "webrtc.offer",
            to = viewer.ParticipantId,
            payload = new { type = "offer", sdp = "opaque-sendrecv-offer" }
        });
        var offer = await ReceiveAsync(viewer.Socket);
        Assert.Equal("webrtc.offer", offer.GetProperty("type").GetString());
        Assert.Equal(publisher.ParticipantId, offer.GetProperty("from").GetGuid());
        Assert.Equal("opaque-sendrecv-offer", offer.GetProperty("payload").GetProperty("sdp").GetString());

        await SendAsync(viewer.Socket, new
        {
            type = "webrtc.answer",
            to = publisher.ParticipantId,
            payload = new { type = "answer", sdp = "opaque-sendrecv-answer" }
        });
        var answer = await ReceiveAsync(publisher.Socket);
        Assert.Equal("webrtc.answer", answer.GetProperty("type").GetString());
        Assert.Equal(viewer.ParticipantId, answer.GetProperty("from").GetGuid());
        Assert.Equal("opaque-sendrecv-answer", answer.GetProperty("payload").GetProperty("sdp").GetString());

        Assert.True((await GetParticipantAsync(sessionId, ParticipantRoles.Publisher)).CanSendAudio);
        Assert.True((await GetParticipantAsync(sessionId, ParticipantRoles.Viewer)).CanSendAudio);
        publisher.Socket.Dispose();
        viewer.Socket.Dispose();
    }

    private async Task<(HttpClient Client, Guid DeviceId)> BootstrapAsync(string deviceType, string platform)
    {
        var client = _factory.CreateClient();
        var session = await DeviceIdentityTestHelper.BootstrapAndAuthorizeAsync(client, deviceType, platform);
        _tokensByDeviceId[session.DeviceId] = session.AccessToken;
        return (client, session.DeviceId);
    }

    private async Task<(HttpClient Owner, Guid SessionId, string Code)> CreateSessionAsync(
        string? mode = null, int maxViewers = 2)
    {
        var (client, _) = await BootstrapAsync(DeviceTypes.WindowsPublisher, DevicePlatforms.Windows);
        var response = await client.PostAsJsonAsync("/api/sessions", new { maxViewers, mode });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var body = await ReadJsonAsync(response);
        return (client, body.GetProperty("id").GetGuid(), body.GetProperty("code").GetString()!);
    }

    private async Task<HttpClient> JoinAsync(Guid sessionId, string code)
    {
        var (client, viewerDeviceId) = await BootstrapAsync(DeviceTypes.FlutterViewer, DevicePlatforms.Android);
        await using (var scope = _factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var publisherDeviceId = await db.StreamSessions.Where(x => x.Id == sessionId)
                .Select(x => x.SourceDeviceId).SingleAsync();
            db.DevicePairings.Add(new DevicePairing
            {
                Id = Guid.NewGuid(),
                PublisherDeviceId = publisherDeviceId,
                ViewerDeviceId = viewerDeviceId,
                Status = DevicePairingStatuses.Active,
                CreatedAt = DateTimeOffset.UtcNow
            });
            await db.SaveChangesAsync();
        }

        var response = await client.PostAsJsonAsync("/api/sessions/join", new { code });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return client;
    }

    private async Task<ConnectedParticipant> ConnectAsync(Guid sessionId, string role)
    {
        var participant = await GetParticipantAsync(sessionId, role);
        var client = _factory.Server.CreateWebSocketClient();
        client.ConfigureRequest = request =>
            request.Headers.Authorization = $"Bearer {_tokensByDeviceId[participant.DeviceId]}";
        var socket = await client.ConnectAsync(
            new Uri($"ws://localhost/ws/signaling?sessionId={sessionId}"), CancellationToken.None);
        return new ConnectedParticipant(socket, participant.Id);
    }

    private async Task<SessionParticipant> GetParticipantAsync(Guid sessionId, string role)
    {
        await using var scope = _factory.Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<AppDbContext>().SessionParticipants
            .AsNoTracking().SingleAsync(x => x.SessionId == sessionId && x.Role == role);
    }

    private async Task<string> GetSessionModeAsync(Guid sessionId)
    {
        await using var scope = _factory.Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<AppDbContext>().StreamSessions
            .Where(x => x.Id == sessionId).Select(x => x.Mode).SingleAsync();
    }

    private async Task<string> GetSessionStatusAsync(Guid sessionId)
    {
        await using var scope = _factory.Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<AppDbContext>().StreamSessions
            .Where(x => x.Id == sessionId).Select(x => x.Status).SingleAsync();
    }

    private static async Task SendAsync(WebSocket socket, object message)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(message);
        await socket.SendAsync(bytes, WebSocketMessageType.Text, true, CancellationToken.None);
    }

    private static async Task<JsonElement> ReceiveAsync(WebSocket socket)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var buffer = new byte[8192];
        var result = await socket.ReceiveAsync(buffer, timeout.Token);
        Assert.Equal(WebSocketMessageType.Text, result.MessageType);
        using var document = JsonDocument.Parse(buffer.AsMemory(0, result.Count));
        return document.RootElement.Clone();
    }

    private static async Task<JsonElement> ReadJsonAsync(HttpResponseMessage response)
    {
        var document = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync());
        return document.RootElement.Clone();
    }

    private readonly Dictionary<Guid, string> _tokensByDeviceId = [];

    private sealed record ConnectedParticipant(WebSocket Socket, Guid ParticipantId);
}
