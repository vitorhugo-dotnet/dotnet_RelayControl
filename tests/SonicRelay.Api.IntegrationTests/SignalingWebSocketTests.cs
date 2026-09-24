using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using SonicRelay.Domain.DeviceIdentities;
using SonicRelay.Domain.Devices;
using SonicRelay.Domain.Sessions;
using SonicRelay.Infrastructure.Persistence;
using Xunit;

namespace SonicRelay.Api.IntegrationTests;

public sealed class SignalingWebSocketTests : IClassFixture<SonicRelayApiFactory>
{
    private const string AllowedWebOrigin = "https://sonicrelay.hugodotnet.dev";
    private readonly SonicRelayApiFactory _factory;

    public SignalingWebSocketTests(SonicRelayApiFactory factory) => _factory = factory;

    [Fact]
    public async Task Signaling_rejects_an_unauthenticated_websocket_upgrade()
    {
        var client = _factory.Server.CreateWebSocketClient();

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => client.ConnectAsync(
            new Uri($"ws://localhost/ws/signaling?sessionId={Guid.NewGuid()}"),
            CancellationToken.None));

        Assert.Contains("401", exception.Message);
    }

    [Fact]
    public async Task Signaling_accepts_a_browser_grant_from_the_exact_allowed_origin()
    {
        await using var factory = CreateBrowserFactory();
        var publisher = await CreateParticipantAsync("browser-grant-publisher", factory);
        var viewer = await CreateViewerAsync(publisher, "browser-grant-viewer", factory);
        var cookie = await IssueGrantCookieAsync(viewer, factory);

        using var socket = await ConnectWithGrantAsync(viewer.SessionId, cookie, AllowedWebOrigin, factory);
        var joined = await ReceiveAsync(socket);

        AssertEnvelope(joined, "session.joined", viewer.SessionId);
        Assert.Equal(viewer.ParticipantId, joined.GetProperty("payload").GetProperty("participantId").GetGuid());
    }

    [Theory]
    [InlineData(null)]
    [InlineData("null")]
    [InlineData("https://evil.sonicrelay.hugodotnet.dev")]
    public async Task Signaling_rejects_a_browser_grant_without_an_exact_allowed_origin(string? origin)
    {
        await using var factory = CreateBrowserFactory();
        var publisher = await CreateParticipantAsync($"browser-origin-publisher-{Guid.NewGuid():N}", factory);
        var viewer = await CreateViewerAsync(publisher, $"browser-origin-viewer-{Guid.NewGuid():N}", factory);
        var cookie = await IssueGrantCookieAsync(viewer, factory);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            ConnectWithGrantAsync(viewer.SessionId, cookie, origin, factory));

        Assert.Contains("403", exception.Message);
    }

    [Fact]
    public async Task Signaling_rejects_a_browser_grant_bound_to_another_session()
    {
        await using var factory = CreateBrowserFactory();
        var publisher = await CreateParticipantAsync("browser-wrong-session-publisher", factory);
        var viewer = await CreateViewerAsync(publisher, "browser-wrong-session-viewer", factory);
        var otherSession = await CreateParticipantAsync("browser-wrong-session-other", factory);
        var cookie = await IssueGrantCookieAsync(viewer, factory);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            ConnectWithGrantAsync(otherSession.SessionId, cookie, AllowedWebOrigin, factory));

        Assert.Contains("403", exception.Message);
    }

    [Fact]
    public async Task Signaling_rejects_an_expired_browser_grant()
    {
        var clock = new TestTimeProvider(DateTimeOffset.UtcNow);
        await using var factory = CreateBrowserFactory(clock);
        var publisher = await CreateParticipantAsync("browser-expired-publisher", factory);
        var viewer = await CreateViewerAsync(publisher, "browser-expired-viewer", factory);
        var cookie = await IssueGrantCookieAsync(viewer, factory);
        clock.Advance(TimeSpan.FromSeconds(61));

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            ConnectWithGrantAsync(viewer.SessionId, cookie, AllowedWebOrigin, factory));

        Assert.Contains("401", exception.Message);
    }

    [Fact]
    public async Task Signaling_rejects_a_tampered_browser_grant()
    {
        await using var factory = CreateBrowserFactory();
        var publisher = await CreateParticipantAsync("browser-tampered-publisher", factory);
        var viewer = await CreateViewerAsync(publisher, "browser-tampered-viewer", factory);
        var cookie = await IssueGrantCookieAsync(viewer, factory);
        var cookieParts = cookie.Split('=', 2);
        var tokenParts = cookieParts[1].Split('.');
        tokenParts[2] = $"{(tokenParts[2][0] == 'A' ? 'B' : 'A')}{tokenParts[2][1..]}";
        var tamperedCookie = $"{cookieParts[0]}={string.Join('.', tokenParts)}";

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            ConnectWithGrantAsync(viewer.SessionId, tamperedCookie, AllowedWebOrigin, factory));

        Assert.Contains("401", exception.Message);
    }

    [Fact]
    public async Task Signaling_revalidates_browser_grant_device_revocation_before_upgrade()
    {
        await using var factory = CreateBrowserFactory();
        var publisher = await CreateParticipantAsync("browser-revoked-publisher", factory);
        var viewer = await CreateViewerAsync(publisher, "browser-revoked-viewer", factory);
        var cookie = await IssueGrantCookieAsync(viewer, factory);
        await SetDeviceRevokedAsync(viewer.DeviceId, factory);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            ConnectWithGrantAsync(viewer.SessionId, cookie, AllowedWebOrigin, factory));

        Assert.Contains("403", exception.Message);
    }

    [Fact]
    public async Task Signaling_revalidates_browser_participant_status_before_upgrade()
    {
        await using var factory = CreateBrowserFactory();
        var publisher = await CreateParticipantAsync("browser-disconnected-publisher", factory);
        var viewer = await CreateViewerAsync(publisher, "browser-disconnected-viewer", factory);
        var cookie = await IssueGrantCookieAsync(viewer, factory);
        await SetParticipantStatusAsync(viewer.ParticipantId, ParticipantStatuses.Disconnected, factory);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            ConnectWithGrantAsync(viewer.SessionId, cookie, AllowedWebOrigin, factory));

        Assert.Contains("403", exception.Message);
    }

    [Fact]
    public async Task Signaling_revalidates_browser_receive_permission_before_upgrade()
    {
        await using var factory = CreateBrowserFactory();
        var publisher = await CreateParticipantAsync("browser-receive-publisher", factory);
        var viewer = await CreateViewerAsync(publisher, "browser-receive-viewer", factory);
        var cookie = await IssueGrantCookieAsync(viewer, factory);
        await SetViewerReceivePermissionAsync(viewer.ParticipantId, false, factory);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            ConnectWithGrantAsync(viewer.SessionId, cookie, AllowedWebOrigin, factory));

        Assert.Contains("403", exception.Message);
    }

    [Fact]
    public async Task Signaling_keeps_accepting_device_bearer_without_an_origin()
    {
        var participant = await CreateParticipantAsync("bearer-without-origin");

        using var socket = await ConnectAsync(participant);
        var joined = await ReceiveAsync(socket);

        AssertEnvelope(joined, "session.joined", participant.SessionId);
    }

    [Fact]
    public async Task Signaling_does_not_route_to_a_participant_in_another_session()
    {
        var sender = await CreateParticipantAsync("sender");
        var receiver = await CreateParticipantAsync("receiver");
        using var senderSocket = await ConnectAsync(sender);
        using var receiverSocket = await ConnectAsync(receiver);
        await ReceiveAsync(senderSocket);
        await ReceiveAsync(receiverSocket);

        await SendAsync(senderSocket, new
        {
            type = "webrtc.offer",
            to = receiver.ParticipantId,
            payload = new { sdp = "sensitive-test-sdp" }
        });

        var error = await ReceiveAsync(senderSocket);
        Assert.Equal("error", error.GetProperty("type").GetString());
        Assert.Equal("participant_not_found", error.GetProperty("payload").GetProperty("code").GetString());

        using var timeout = new CancellationTokenSource(TimeSpan.FromMilliseconds(250));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => ReceiveAsync(receiverSocket, timeout.Token));
    }

    [Fact]
    public async Task Signaling_rejects_an_unknown_session()
    {
        var participant = await CreateParticipantAsync("invalid-admission");
        var client = _factory.Server.CreateWebSocketClient();
        client.ConfigureRequest = request => request.Headers.Authorization = $"Bearer {participant.AccessToken}";

        var missingSession = await Assert.ThrowsAsync<InvalidOperationException>(() => client.ConnectAsync(
            new Uri($"ws://localhost/ws/signaling?sessionId={Guid.NewGuid()}"),
            CancellationToken.None));
        Assert.Contains("404", missingSession.Message);
    }

    [Fact]
    public async Task Signaling_rejects_a_device_that_is_not_a_participant_of_the_session()
    {
        var publisher = await CreateParticipantAsync("not-a-participant-publisher");
        var stranger = await CreateParticipantAsync("not-a-participant-stranger");
        var client = _factory.Server.CreateWebSocketClient();
        client.ConfigureRequest = request => request.Headers.Authorization = $"Bearer {stranger.AccessToken}";

        var forbidden = await Assert.ThrowsAsync<InvalidOperationException>(() => client.ConnectAsync(
            new Uri($"ws://localhost/ws/signaling?sessionId={publisher.SessionId}"),
            CancellationToken.None));
        Assert.Contains("403", forbidden.Message);
    }

    [Fact]
    public async Task Signaling_normalizes_the_envelope_and_overwrites_client_metadata()
    {
        var participant = await CreateParticipantAsync("normalized");
        using var socket = await ConnectAsync(participant);
        var joined = await ReceiveAsync(socket);
        AssertEnvelope(joined, "session.joined", participant.SessionId);

        var messageId = Guid.NewGuid();
        await SendAsync(socket, new
        {
            type = "webrtc.offer",
            messageId,
            sessionId = Guid.NewGuid(),
            from = Guid.NewGuid(),
            to = participant.ParticipantId,
            timestamp = DateTimeOffset.UnixEpoch,
            payload = new { sdp = "opaque-sdp" }
        });

        var routed = await ReceiveAsync(socket);
        AssertEnvelope(routed, "webrtc.offer", participant.SessionId);
        Assert.Equal(messageId, routed.GetProperty("messageId").GetGuid());
        Assert.Equal(participant.ParticipantId, routed.GetProperty("from").GetGuid());
        Assert.Equal(participant.ParticipantId, routed.GetProperty("to").GetGuid());
        Assert.Equal("opaque-sdp", routed.GetProperty("payload").GetProperty("sdp").GetString());
        Assert.NotEqual(DateTimeOffset.UnixEpoch, routed.GetProperty("timestamp").GetDateTimeOffset());
    }

    [Fact]
    public async Task Receiver_stats_are_forwarded_with_authenticated_envelope_metadata()
    {
        using var testTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var sender = await CreateParticipantAsync("receiver-stats-sender");
        var receiver = await CreateViewerAsync(sender, "receiver-stats-receiver");
        using var senderSocket = await ConnectAsync(sender, ct: testTimeout.Token);
        using var receiverSocket = await ConnectAsync(receiver, ct: testTimeout.Token);
        await ReceiveAsync(senderSocket, testTimeout.Token);
        await ReceiveAsync(receiverSocket, testTimeout.Token);
        await ReceiveAsync(senderSocket, testTimeout.Token); // receiver joined
        await ReceiveAsync(receiverSocket, testTimeout.Token); // existing sender capability roster entry

        var messageId = Guid.NewGuid();
        await SendAsync(senderSocket, new
        {
            type = "video.receiver_stats",
            messageId,
            sessionId = Guid.NewGuid(),
            from = Guid.NewGuid(),
            to = receiver.ParticipantId,
            timestamp = DateTimeOffset.UnixEpoch,
            payload = new
            {
                version = 1,
                intervalMs = 2000,
                rtpPacketsReceived = 90,
                rtpPacketsLost = 10,
                accessUnitsReceived = 30,
                incompleteAccessUnits = 2,
                decodedFrames = 24,
                targetFramesPerSecond = 30
            }
        }, testTimeout.Token);

        var routed = await ReceiveReceiverStatsAsync(receiverSocket, testTimeout.Token);
        AssertEnvelope(routed, "video.receiver_stats", sender.SessionId);
        Assert.Equal(messageId, routed.GetProperty("messageId").GetGuid());
        Assert.Equal(sender.ParticipantId, routed.GetProperty("from").GetGuid());
        Assert.Equal(receiver.ParticipantId, routed.GetProperty("to").GetGuid());
        AssertReceiverStatsPayload(routed);
    }

    private static void AssertReceiverStatsPayload(JsonElement routed)
    {
        var payload = routed.GetProperty("payload");
        Assert.Equal(1, payload.GetProperty("version").GetInt32());
        Assert.Equal(2000, payload.GetProperty("intervalMs").GetInt32());
        Assert.Equal(90, payload.GetProperty("rtpPacketsReceived").GetInt32());
        Assert.Equal(10, payload.GetProperty("rtpPacketsLost").GetInt32());
        Assert.Equal(30, payload.GetProperty("accessUnitsReceived").GetInt32());
        Assert.Equal(2, payload.GetProperty("incompleteAccessUnits").GetInt32());
        Assert.Equal(24, payload.GetProperty("decodedFrames").GetInt32());
        Assert.Equal(30, payload.GetProperty("targetFramesPerSecond").GetInt32());
    }

    private static async Task<JsonElement> ReceiveReceiverStatsAsync(WebSocket socket, CancellationToken ct)
    {
        var message = await ReceiveAsync(socket, ct);
        while (message.GetProperty("type").GetString() == "participant.capabilities")
            message = await ReceiveAsync(socket, ct);
        return message;
    }

    [Fact]
    public async Task Signaling_announces_a_new_participant_to_existing_session_peers()
    {
        var publisher = await CreateParticipantAsync("join-announcement-publisher");
        var viewer = await CreateViewerAsync(publisher, "join-announcement-viewer");
        using var publisherSocket = await ConnectAsync(publisher);
        await ReceiveAsync(publisherSocket);

        using var viewerSocket = await ConnectAsync(viewer);
        var viewerJoined = await ReceiveAsync(viewerSocket);
        Assert.Equal(viewer.ParticipantId, viewerJoined.GetProperty("payload").GetProperty("participantId").GetGuid());
        Assert.Equal(ParticipantRoles.Viewer, viewerJoined.GetProperty("payload").GetProperty("role").GetString());

        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        var announced = await ReceiveAsync(publisherSocket, timeout.Token);
        AssertEnvelope(announced, "session.joined", publisher.SessionId);
        Assert.Equal(viewer.ParticipantId, announced.GetProperty("from").GetGuid());
        Assert.Equal(publisher.ParticipantId, announced.GetProperty("to").GetGuid());
        Assert.Equal(viewer.ParticipantId, announced.GetProperty("payload").GetProperty("participantId").GetGuid());
        Assert.Equal(ParticipantRoles.Viewer, announced.GetProperty("payload").GetProperty("role").GetString());
    }

    [Fact]
    public async Task Signaling_reconnecting_within_the_grace_period_reports_reconnection_not_departure()
    {
        await using var factory = new SonicRelayApiFactory(new Dictionary<string, string?>
        {
            ["Sessions:ParticipantDisconnectGraceSeconds"] = "10"
        });
        var publisher = await CreateParticipantAsync("grace-reconnect-publisher", factory);
        var viewer = await CreateViewerAsync(publisher, "grace-reconnect-viewer", factory);
        using var publisherSocket = await ConnectAsync(publisher, factory);
        await ReceiveAsync(publisherSocket);

        var viewerSocket = await ConnectAsync(viewer, factory);
        await ReceiveAsync(viewerSocket);
        var joinAnnouncement = await ReceiveAsync(publisherSocket);
        Assert.Equal("session.joined", joinAnnouncement.GetProperty("type").GetString());

        viewerSocket.Dispose();

        using var disconnectTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var disconnected = await ReceiveAsync(publisherSocket, disconnectTimeout.Token);
        AssertEnvelope(disconnected, "participant.disconnected", publisher.SessionId);
        Assert.Equal(viewer.ParticipantId, disconnected.GetProperty("payload").GetProperty("participantId").GetGuid());

        using var reconnectedViewerSocket = await ConnectAsync(viewer, factory);
        await ReceiveAsync(reconnectedViewerSocket);

        using var reconnectTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var reconnected = await ReceiveAsync(publisherSocket, reconnectTimeout.Token);
        AssertEnvelope(reconnected, "participant.reconnected", publisher.SessionId);
        Assert.Equal(viewer.ParticipantId, reconnected.GetProperty("payload").GetProperty("participantId").GetGuid());

        await using var assertScope = factory.Services.CreateAsyncScope();
        var db = assertScope.ServiceProvider.GetRequiredService<AppDbContext>();
        var participantRow = await db.SessionParticipants.SingleAsync(x => x.Id == viewer.ParticipantId);
        Assert.Equal(ParticipantStatuses.Connected, participantRow.Status);
    }

    [Fact]
    public async Task Signaling_finalizes_as_left_after_the_grace_period_elapses_without_reconnecting()
    {
        await using var factory = new SonicRelayApiFactory(new Dictionary<string, string?>
        {
            ["Sessions:ParticipantDisconnectGraceSeconds"] = "1"
        });
        var publisher = await CreateParticipantAsync("grace-expiry-publisher", factory);
        var viewer = await CreateViewerAsync(publisher, "grace-expiry-viewer", factory);
        using var publisherSocket = await ConnectAsync(publisher, factory);
        await ReceiveAsync(publisherSocket);

        var viewerSocket = await ConnectAsync(viewer, factory);
        await ReceiveAsync(viewerSocket);
        await ReceiveAsync(publisherSocket); // session.joined announcement

        viewerSocket.Dispose();

        using var disconnectTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var disconnected = await ReceiveAsync(publisherSocket, disconnectTimeout.Token);
        AssertEnvelope(disconnected, "participant.disconnected", publisher.SessionId);

        using var leftTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var left = await ReceiveAsync(publisherSocket, leftTimeout.Token);
        AssertEnvelope(left, "session.left", publisher.SessionId);
        Assert.Equal(viewer.ParticipantId, left.GetProperty("payload").GetProperty("participantId").GetGuid());

        await using var assertScope = factory.Services.CreateAsyncScope();
        var db = assertScope.ServiceProvider.GetRequiredService<AppDbContext>();
        var participantRow = await db.SessionParticipants.SingleAsync(x => x.Id == viewer.ParticipantId);
        Assert.Equal(ParticipantStatuses.Disconnected, participantRow.Status);
        Assert.NotNull(participantRow.LeftAt);
    }

    [Fact]
    public async Task Signaling_finalizes_a_participant_that_rejoined_over_http_but_never_reopened_the_socket()
    {
        await using var factory = new SonicRelayApiFactory(new Dictionary<string, string?>
        {
            ["Sessions:ParticipantDisconnectGraceSeconds"] = "1"
        });
        var publisher = await CreateParticipantAsync("http-rejoin-publisher", factory);
        var viewer = await CreateViewerAsync(publisher, "http-rejoin-viewer", factory);
        using var publisherSocket = await ConnectAsync(publisher, factory);
        await ReceiveAsync(publisherSocket);

        var viewerSocket = await ConnectAsync(viewer, factory);
        await ReceiveAsync(viewerSocket);
        await ReceiveAsync(publisherSocket); // session.joined announcement

        viewerSocket.Dispose();
        using var disconnectTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await ReceiveAsync(publisherSocket, disconnectTimeout.Token); // participant.disconnected

        // The documented full-reconnect flow calls POST /api/sessions/join before reopening the
        // WebSocket (SessionEndpoints.JoinAsync's existing-viewer branch sets Status back to
        // Connected without touching ConnectionId). Simulate that HTTP leg completing while the
        // client crashes before ever reopening the socket, so ConnectionId stays null.
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var participantRow = await db.SessionParticipants.SingleAsync(x => x.Id == viewer.ParticipantId);
            participantRow.Status = ParticipantStatuses.Connected;
            participantRow.LeftAt = null;
            await db.SaveChangesAsync();
        }

        // The grace timer must still finalize this participant once it expires — a null
        // ConnectionId is not a live socket that "claimed" the row.
        using var leftTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var left = await ReceiveAsync(publisherSocket, leftTimeout.Token);
        AssertEnvelope(left, "session.left", publisher.SessionId);
        Assert.Equal(viewer.ParticipantId, left.GetProperty("payload").GetProperty("participantId").GetGuid());

        await using var assertScope = factory.Services.CreateAsyncScope();
        var assertDb = assertScope.ServiceProvider.GetRequiredService<AppDbContext>();
        var finalRow = await assertDb.SessionParticipants.SingleAsync(x => x.Id == viewer.ParticipantId);
        Assert.Equal(ParticipantStatuses.Disconnected, finalRow.Status);
    }

    [Theory]
    [InlineData("unsupported.type", "unsupported_message_type")]
    [InlineData("session.ended", "unsupported_message_type")]
    public async Task Signaling_returns_a_structured_error_for_unsupported_client_types(string type, string code)
    {
        var participant = await CreateParticipantAsync($"unsupported-{Guid.NewGuid():N}");
        using var socket = await ConnectAsync(participant);
        await ReceiveAsync(socket);

        await SendAsync(socket, new { type });

        var error = await ReceiveAsync(socket);
        AssertEnvelope(error, "error", participant.SessionId);
        Assert.Equal(participant.ParticipantId, error.GetProperty("to").GetGuid());
        Assert.Equal(code, error.GetProperty("payload").GetProperty("code").GetString());
    }

    [Fact]
    public async Task Signaling_returns_a_structured_error_for_invalid_json()
    {
        var participant = await CreateParticipantAsync("invalid-json");
        using var socket = await ConnectAsync(participant);
        await ReceiveAsync(socket);

        await SendTextAsync(socket, "{not-json");

        var error = await ReceiveAsync(socket);
        AssertEnvelope(error, "error", participant.SessionId);
        Assert.Equal("invalid_message", error.GetProperty("payload").GetProperty("code").GetString());
    }

    [Theory]
    [InlineData(SessionStatuses.Ended)]
    [InlineData(SessionStatuses.Expired)]
    public async Task Signaling_rejects_terminal_sessions(string status)
    {
        var participant = await CreateParticipantAsync($"terminal-{status}");
        await SetSessionStateAsync(participant.SessionId, status, DateTimeOffset.UtcNow.AddMinutes(5));
        var client = _factory.Server.CreateWebSocketClient();
        client.ConfigureRequest = request => request.Headers.Authorization = $"Bearer {participant.AccessToken}";

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => client.ConnectAsync(
            new Uri($"ws://localhost/ws/signaling?sessionId={participant.SessionId}"),
            CancellationToken.None));

        Assert.Contains("410", exception.Message);
    }

    [Fact]
    public async Task Signaling_accepts_an_active_session_whose_join_code_has_elapsed()
    {
        // The join code only gates new joins; a live session must keep signaling past the
        // code TTL, otherwise a running broadcast dies ten minutes after it was created.
        var participant = await CreateParticipantAsync("active-elapsed-code");
        await SetSessionStateAsync(participant.SessionId, SessionStatuses.Active,
            DateTimeOffset.UtcNow.AddMinutes(-1));

        using var socket = await ConnectAsync(participant);
        var joined = await ReceiveAsync(socket);

        AssertEnvelope(joined, "session.joined", participant.SessionId);
    }

    [Fact]
    public async Task Signaling_does_not_route_a_frame_after_the_session_ends()
    {
        var participant = await CreateParticipantAsync("terminal-route");
        using var socket = await ConnectAsync(participant);
        await ReceiveAsync(socket);
        await SetSessionStateAsync(participant.SessionId, SessionStatuses.Ended, DateTimeOffset.UtcNow.AddMinutes(5));

        await SendAsync(socket, new
        {
            type = "webrtc.offer",
            to = participant.ParticipantId,
            payload = new { sdp = "must-not-route" }
        });

        var response = await ReceiveAsync(socket);
        Assert.Equal("session.ended", response.GetProperty("type").GetString());
        var buffer = new byte[64];
        var close = await socket.ReceiveAsync(buffer, CancellationToken.None);
        Assert.Equal(WebSocketMessageType.Close, close.MessageType);
    }

    [Fact]
    public async Task Signaling_rejects_reconnecting_with_a_token_from_a_device_revoked_after_the_first_connection()
    {
        var participant = await CreateParticipantAsync("revoked-mid-session");
        using (var firstSocket = await ConnectAsync(participant))
        {
            await ReceiveAsync(firstSocket);
        }
        await SetDeviceRevokedAsync(participant.DeviceId);
        var client = _factory.Server.CreateWebSocketClient();
        client.ConfigureRequest = request => request.Headers.Authorization = $"Bearer {participant.AccessToken}";

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => client.ConnectAsync(
            new Uri($"ws://localhost/ws/signaling?sessionId={participant.SessionId}"),
            CancellationToken.None));

        // The token itself still validates (signature/expiry unchanged), so authentication
        // succeeds; DeviceScopeAuthorizationHandler's live revocation check is what then fails
        // the "signaling:connect" policy requirement, and ASP.NET Core returns 403 (not 401) for
        // an authenticated caller that fails an authorization requirement — matching the revoked
        // publisher HTTP case in SessionEndpointsTests.
        Assert.Contains("403", exception.Message);
    }

    private async Task<TestParticipant> CreateParticipantAsync(string prefix, SonicRelayApiFactory? factory = null)
    {
        factory ??= _factory;
        var http = factory.CreateClient();
        var session = await DeviceIdentityTestHelper.BootstrapAndAuthorizeAsync(
            http, DeviceTypes.WindowsPublisher, DevicePlatforms.Windows, $"{prefix} device");

        var sessionId = Guid.NewGuid();
        var participantId = Guid.NewGuid();
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        db.StreamSessions.Add(new StreamSession
        {
            Id = sessionId,
            SourceDeviceId = session.DeviceId,
            Status = SessionStatuses.Active,
            MaxViewers = 1,
            CodeExpiresAt = DateTimeOffset.UtcNow.AddMinutes(5),
            CreatedAt = DateTimeOffset.UtcNow
        });
        db.SessionParticipants.Add(new SessionParticipant
        {
            Id = participantId,
            SessionId = sessionId,
            DeviceId = session.DeviceId,
            Role = ParticipantRoles.Publisher,
            Status = ParticipantStatuses.Connected,
            JoinedAt = DateTimeOffset.UtcNow
        });
        await db.SaveChangesAsync();
        return new TestParticipant(session.AccessToken, sessionId, session.DeviceId, participantId);
    }

    private async Task<TestParticipant> CreateViewerAsync(TestParticipant publisher, string prefix,
        SonicRelayApiFactory? factory = null)
    {
        factory ??= _factory;
        var http = factory.CreateClient();
        var session = await DeviceIdentityTestHelper.BootstrapAndAuthorizeAsync(
            http, DeviceTypes.FlutterViewer, DevicePlatforms.Android, $"{prefix} device");

        var participantId = Guid.NewGuid();
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        db.SessionParticipants.Add(new SessionParticipant
        {
            Id = participantId,
            SessionId = publisher.SessionId,
            DeviceId = session.DeviceId,
            Role = ParticipantRoles.Viewer,
            Status = ParticipantStatuses.Connected,
            JoinedAt = DateTimeOffset.UtcNow
        });
        await db.SaveChangesAsync();
        return new TestParticipant(session.AccessToken, publisher.SessionId, session.DeviceId, participantId);
    }

    private async Task<WebSocket> ConnectAsync(TestParticipant participant, SonicRelayApiFactory? factory = null,
        CancellationToken ct = default)
    {
        var client = (factory ?? _factory).Server.CreateWebSocketClient();
        client.ConfigureRequest = request =>
            request.Headers.Authorization = $"Bearer {participant.AccessToken}";
        return await client.ConnectAsync(
            new Uri($"ws://localhost/ws/signaling?sessionId={participant.SessionId}"),
            ct);
    }

    private async Task<WebSocket> ConnectAsync(TestParticipant participant, CancellationToken ct)
    {
        var client = _factory.Server.CreateWebSocketClient();
        client.ConfigureRequest = request =>
            request.Headers.Authorization = $"Bearer {participant.AccessToken}";
        return await client.ConnectAsync(
            new Uri($"ws://localhost/ws/signaling?sessionId={participant.SessionId}"), ct);
    }

    private async Task SetSessionStateAsync(Guid sessionId, string status, DateTimeOffset codeExpiresAt)
    {
        await using var scope = _factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var session = await db.StreamSessions.SingleAsync(x => x.Id == sessionId);
        session.Status = status;
        session.CodeExpiresAt = codeExpiresAt;
        await db.SaveChangesAsync();
    }

    private async Task SetDeviceRevokedAsync(Guid deviceId, SonicRelayApiFactory? factory = null)
    {
        await using var scope = (factory ?? _factory).Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var device = await db.DeviceIdentities.SingleAsync(x => x.Id == deviceId);
        device.Status = DeviceIdentityStatuses.Revoked;
        await db.SaveChangesAsync();
    }

    private async Task SetParticipantStatusAsync(Guid participantId, string status,
        SonicRelayApiFactory? factory = null)
    {
        await using var scope = (factory ?? _factory).Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var participant = await db.SessionParticipants.SingleAsync(x => x.Id == participantId);
        participant.Status = status;
        await db.SaveChangesAsync();
    }

    private async Task SetViewerReceivePermissionAsync(Guid participantId, bool canReceiveAudio,
        SonicRelayApiFactory? factory = null)
    {
        await using var scope = (factory ?? _factory).Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var participant = await db.SessionParticipants.SingleAsync(x => x.Id == participantId);
        participant.CanReceiveAudio = canReceiveAudio;
        await db.SaveChangesAsync();
    }

    private static SonicRelayApiFactory CreateBrowserFactory(TimeProvider? timeProvider = null) =>
        new(new Dictionary<string, string?>
        {
            ["Cors:AllowedOrigins:0"] = AllowedWebOrigin
        })
        {
            TimeProviderOverride = timeProvider
        };

    private static async Task<string> IssueGrantCookieAsync(TestParticipant participant,
        SonicRelayApiFactory factory)
    {
        using var client = factory.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/signaling/grant")
        {
            Content = JsonContent.Create(new { sessionId = participant.SessionId })
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", participant.AccessToken);
        using var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        return response.Headers.GetValues("Set-Cookie").Single().Split(';')[0];
    }

    private static async Task<WebSocket> ConnectWithGrantAsync(Guid sessionId, string cookie, string? origin,
        SonicRelayApiFactory factory)
    {
        var client = factory.Server.CreateWebSocketClient();
        client.ConfigureRequest = request =>
        {
            request.Headers.Cookie = cookie;
            if (origin is not null) request.Headers.Origin = origin;
        };
        return await client.ConnectAsync(
            new Uri($"ws://localhost/ws/signaling?sessionId={sessionId}"),
            CancellationToken.None);
    }

    private static async Task SendAsync(WebSocket socket, object message, CancellationToken ct = default)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(message);
        await socket.SendAsync(bytes, WebSocketMessageType.Text, true, ct);
    }

    private static async Task SendTextAsync(WebSocket socket, string message)
    {
        var bytes = Encoding.UTF8.GetBytes(message);
        await socket.SendAsync(bytes, WebSocketMessageType.Text, true, CancellationToken.None);
    }

    private static void AssertEnvelope(JsonElement message, string type, Guid sessionId)
    {
        Assert.Equal(type, message.GetProperty("type").GetString());
        Assert.NotEqual(Guid.Empty, message.GetProperty("messageId").GetGuid());
        Assert.Equal(sessionId, message.GetProperty("sessionId").GetGuid());
        Assert.Equal(JsonValueKind.String, message.GetProperty("timestamp").ValueKind);
        Assert.True(message.TryGetProperty("from", out _));
        Assert.True(message.TryGetProperty("to", out _));
        Assert.True(message.TryGetProperty("payload", out _));
    }

    private static async Task<JsonElement> ReceiveAsync(WebSocket socket, CancellationToken ct = default)
    {
        var buffer = new byte[4096];
        var result = await socket.ReceiveAsync(buffer, ct);
        Assert.Equal(WebSocketMessageType.Text, result.MessageType);
        using var document = JsonDocument.Parse(buffer.AsMemory(0, result.Count));
        return document.RootElement.Clone();
    }

    private sealed record TestParticipant(string AccessToken, Guid SessionId, Guid DeviceId, Guid ParticipantId);
}
