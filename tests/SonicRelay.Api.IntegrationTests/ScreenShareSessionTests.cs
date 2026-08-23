using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using SonicRelay.Api.Contracts;
using SonicRelay.Domain.DeviceIdentities;
using SonicRelay.Domain.Devices;
using SonicRelay.Domain.Sessions;
using SonicRelay.Infrastructure.Persistence;
using Xunit;

namespace SonicRelay.Api.IntegrationTests;

/// <summary>
/// Screen-share sessions (issue #30, phase 0): the windows_desktop device type, the
/// screen_share session mode, device-type-gated admission and auto-pairing on join — plus
/// the guarantee that the audio device types and session modes behave exactly as before.
/// </summary>
public sealed class ScreenShareSessionTests : IClassFixture<SonicRelayApiFactory>
{
    private readonly SonicRelayApiFactory _factory;

    public ScreenShareSessionTests(SonicRelayApiFactory factory) => _factory = factory;

    [Fact]
    public async Task Windows_desktop_bootstraps_on_the_windows_platform()
    {
        var client = _factory.CreateClient();

        var response = await client.PostAsJsonAsync("/api/devices/bootstrap",
            new BootstrapDeviceRequest("Desk PC", DeviceTypes.WindowsDesktop, DevicePlatforms.Windows));

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
    }

    [Theory]
    [InlineData("android")]
    [InlineData("ios")]
    public async Task Windows_desktop_is_rejected_on_non_windows_platforms(string platform)
    {
        var client = _factory.CreateClient();

        var response = await client.PostAsJsonAsync("/api/devices/bootstrap",
            new BootstrapDeviceRequest("Desk PC", DeviceTypes.WindowsDesktop, platform));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Windows_desktop_token_carries_exactly_the_union_of_publisher_and_viewer_scopes()
    {
        var client = _factory.CreateClient();
        var session = await DeviceIdentityTestHelper.BootstrapAndAuthorizeAsync(
            client, DeviceTypes.WindowsDesktop, DevicePlatforms.Windows);

        var scopes = ScopesInToken(session.AccessToken);

        Assert.Equal(
            new[]
            {
                "device:manage", "device:read",
                "pairing:complete", "pairing:create", "pairing:revoke",
                "session:create", "session:end", "session:join",
                "signaling:connect", "turn:credentials"
            },
            scopes.Order().ToArray());
    }

    [Fact]
    public async Task Create_in_screen_share_mode_returns_the_mode_and_a_send_only_publisher()
    {
        var (client, _) = await BootstrapAsync(DeviceTypes.WindowsDesktop, DevicePlatforms.Windows);

        var response = await client.PostAsJsonAsync("/api/sessions",
            new { maxViewers = 3, mode = " Screen_Share " });

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var body = await ReadJsonAsync(response);
        Assert.Equal("screen_share", body.GetProperty("mode").GetString());

        var publisher = await GetParticipantAsync(body.GetProperty("id").GetGuid(), ParticipantRoles.Publisher);
        Assert.True(publisher.AudioSendAllowed);
        Assert.True(publisher.CanSendAudio);
        Assert.False(publisher.CanReceiveAudio);
    }

    [Fact]
    public async Task An_unknown_mode_is_still_rejected_as_invalid_session_mode()
    {
        var (client, _) = await BootstrapAsync(DeviceTypes.WindowsDesktop, DevicePlatforms.Windows);

        var response = await client.PostAsJsonAsync("/api/sessions", new { maxViewers = 1, mode = "remote_desktop" });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var body = await ReadJsonAsync(response);
        Assert.Equal("invalid_session_mode", body.GetProperty("code").GetString());
    }

    [Fact]
    public async Task An_omitted_mode_still_means_broadcast()
    {
        var (client, _) = await BootstrapAsync(DeviceTypes.WindowsDesktop, DevicePlatforms.Windows);

        var response = await client.PostAsJsonAsync("/api/sessions", new { maxViewers = 1 });

        var body = await ReadJsonAsync(response);
        Assert.Equal(SessionModes.Broadcast, body.GetProperty("mode").GetString());
    }

    [Fact]
    public async Task Audio_permission_on_a_screen_share_session_is_rejected_as_not_duplex()
    {
        var (owner, sessionId, _) = await CreateScreenShareSessionAsync();
        var publisher = await GetParticipantAsync(sessionId, ParticipantRoles.Publisher);

        var response = await owner.PostAsJsonAsync(
            $"/api/sessions/{sessionId}/participants/{publisher.Id}/audio-permission",
            new { canSendAudio = false });

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        var body = await ReadJsonAsync(response);
        Assert.Equal("session_not_duplex", body.GetProperty("code").GetString());
    }

    [Fact]
    public async Task A_flutter_viewer_cannot_join_a_screen_share_session_even_when_paired()
    {
        var (_, sessionId, code) = await CreateScreenShareSessionAsync();
        var (viewer, viewerDeviceId) = await BootstrapAsync(DeviceTypes.FlutterViewer, DevicePlatforms.Android);
        await PairWithSessionSourceAsync(sessionId, viewerDeviceId);

        var response = await viewer.PostAsJsonAsync("/api/sessions/join", new { code });

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        var body = await ReadJsonAsync(response);
        Assert.Equal("device_type_not_allowed", body.GetProperty("code").GetString());
    }

    [Fact]
    public async Task A_windows_publisher_cannot_join_a_screen_share_session()
    {
        var (_, sessionId, code) = await CreateScreenShareSessionAsync();
        var (publisher, publisherDeviceId) = await BootstrapAsync(DeviceTypes.WindowsPublisher, DevicePlatforms.Windows);
        await PairWithSessionSourceAsync(sessionId, publisherDeviceId);

        var response = await publisher.PostAsJsonAsync("/api/sessions/join", new { code });

        // windows_publisher has no session:join scope, so it is stopped by authorization
        // before admission ever runs. Asserting 403 either way is the point: this device
        // type must never reach a screen session.
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task A_windows_desktop_device_joins_a_screen_share_session()
    {
        var (_, sessionId, code) = await CreateScreenShareSessionAsync();
        var (viewer, viewerDeviceId) = await BootstrapAsync(DeviceTypes.WindowsDesktop, DevicePlatforms.Windows);
        // Paired up front so this test isolates the device-type gate: it is the mirror of the
        // two refusals above, with the device type as the only difference. The unpaired case is
        // Joining_a_screen_share_session_creates_the_pairing.
        await PairWithSessionSourceAsync(sessionId, viewerDeviceId);

        var response = await viewer.PostAsJsonAsync("/api/sessions/join", new { code });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task Joining_a_screen_share_session_creates_the_pairing()
    {
        var (_, sessionId, code) = await CreateScreenShareSessionAsync();
        var (viewer, viewerDeviceId) = await BootstrapAsync(DeviceTypes.WindowsDesktop, DevicePlatforms.Windows);

        var response = await viewer.PostAsJsonAsync("/api/sessions/join", new { code });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.True(await HasActivePairingAsync(sessionId, viewerDeviceId));
    }

    [Fact]
    public async Task Rejoining_a_screen_share_session_does_not_duplicate_the_pairing()
    {
        var (_, sessionId, code) = await CreateScreenShareSessionAsync();
        var (viewer, viewerDeviceId) = await BootstrapAsync(DeviceTypes.WindowsDesktop, DevicePlatforms.Windows);

        await viewer.PostAsJsonAsync("/api/sessions/join", new { code });
        await viewer.PostAsJsonAsync("/api/sessions/join", new { code });

        Assert.Equal(1, await CountActivePairingsAsync(sessionId, viewerDeviceId));
    }

    [Fact]
    public async Task Joining_a_broadcast_session_without_a_pairing_is_still_refused()
    {
        var (owner, _) = await BootstrapAsync(DeviceTypes.WindowsPublisher, DevicePlatforms.Windows);
        var created = await owner.PostAsJsonAsync("/api/sessions", new { maxViewers = 1, mode = SessionModes.Broadcast });
        var createdBody = await ReadJsonAsync(created);
        var sessionId = createdBody.GetProperty("id").GetGuid();
        var code = createdBody.GetProperty("code").GetString()!;
        var (viewer, viewerDeviceId) = await BootstrapAsync(DeviceTypes.FlutterViewer, DevicePlatforms.Android);

        var response = await viewer.PostAsJsonAsync("/api/sessions/join", new { code });

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        var body = await ReadJsonAsync(response);
        Assert.Equal("not_paired", body.GetProperty("code").GetString());
        Assert.False(await HasActivePairingAsync(sessionId, viewerDeviceId));
    }

    [Fact]
    public async Task Joining_a_duplex_session_without_a_pairing_is_still_refused()
    {
        var (owner, _) = await BootstrapAsync(DeviceTypes.WindowsPublisher, DevicePlatforms.Windows);
        var created = await owner.PostAsJsonAsync("/api/sessions", new { maxViewers = 1, mode = SessionModes.Duplex });
        var createdBody = await ReadJsonAsync(created);
        var sessionId = createdBody.GetProperty("id").GetGuid();
        var code = createdBody.GetProperty("code").GetString()!;
        var (viewer, viewerDeviceId) = await BootstrapAsync(DeviceTypes.FlutterViewer, DevicePlatforms.Android);

        var response = await viewer.PostAsJsonAsync("/api/sessions/join", new { code });

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.False(await HasActivePairingAsync(sessionId, viewerDeviceId));
    }

    [Fact]
    public async Task The_viewer_limit_still_applies_to_screen_share_sessions()
    {
        var (_, _, code) = await CreateScreenShareSessionAsync(maxViewers: 1);
        var (first, _) = await BootstrapAsync(DeviceTypes.WindowsDesktop, DevicePlatforms.Windows);
        var (second, _) = await BootstrapAsync(DeviceTypes.WindowsDesktop, DevicePlatforms.Windows);

        Assert.Equal(HttpStatusCode.OK, (await first.PostAsJsonAsync("/api/sessions/join", new { code })).StatusCode);
        var response = await second.PostAsJsonAsync("/api/sessions/join", new { code });

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
    }

    private async Task<bool> HasActivePairingAsync(Guid sessionId, Guid viewerDeviceId) =>
        await CountActivePairingsAsync(sessionId, viewerDeviceId) > 0;

    private async Task<int> CountActivePairingsAsync(Guid sessionId, Guid viewerDeviceId)
    {
        await using var scope = _factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var sourceDeviceId = await db.StreamSessions.Where(x => x.Id == sessionId)
            .Select(x => x.SourceDeviceId).SingleAsync();
        return await db.DevicePairings.CountAsync(x =>
            x.PublisherDeviceId == sourceDeviceId
            && x.ViewerDeviceId == viewerDeviceId
            && x.Status == DevicePairingStatuses.Active);
    }

    private async Task PairWithSessionSourceAsync(Guid sessionId, Guid viewerDeviceId)
    {
        await using var scope = _factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var sourceDeviceId = await db.StreamSessions.Where(x => x.Id == sessionId)
            .Select(x => x.SourceDeviceId).SingleAsync();
        db.DevicePairings.Add(new DevicePairing
        {
            Id = Guid.NewGuid(),
            PublisherDeviceId = sourceDeviceId,
            ViewerDeviceId = viewerDeviceId,
            Status = DevicePairingStatuses.Active,
            CreatedAt = DateTimeOffset.UtcNow
        });
        await db.SaveChangesAsync();
    }

    private async Task<(HttpClient Client, Guid DeviceId)> BootstrapAsync(string deviceType, string platform)
    {
        var client = _factory.CreateClient();
        var session = await DeviceIdentityTestHelper.BootstrapAndAuthorizeAsync(client, deviceType, platform);
        return (client, session.DeviceId);
    }

    private async Task<(HttpClient Owner, Guid SessionId, string Code)> CreateScreenShareSessionAsync(
        int maxViewers = 3)
    {
        var (client, _) = await BootstrapAsync(DeviceTypes.WindowsDesktop, DevicePlatforms.Windows);
        var response = await client.PostAsJsonAsync("/api/sessions",
            new { maxViewers, mode = SessionModes.ScreenShare });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var body = await ReadJsonAsync(response);
        return (client, body.GetProperty("id").GetGuid(), body.GetProperty("code").GetString()!);
    }

    private async Task<SessionParticipant> GetParticipantAsync(Guid sessionId, string role)
    {
        await using var scope = _factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        return await db.SessionParticipants.AsNoTracking()
            .SingleAsync(x => x.SessionId == sessionId && x.Role == role);
    }

    private static async Task<JsonElement> ReadJsonAsync(HttpResponseMessage response) =>
        JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement.Clone();

    // The token is the contract under test, so the scopes are read out of the JWT itself
    // rather than trusted from the response body.
    private static string[] ScopesInToken(string accessToken)
    {
        var payload = accessToken.Split('.')[1];
        var padded = payload.PadRight(payload.Length + (4 - payload.Length % 4) % 4, '=')
            .Replace('-', '+').Replace('_', '/');
        using var document = System.Text.Json.JsonDocument.Parse(Convert.FromBase64String(padded));
        return document.RootElement.GetProperty("scope").GetString()!.Split(' ');
    }
}
