using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using SonicRelay.Domain.Devices;
using SonicRelay.Domain.Sessions;
using SonicRelay.Infrastructure.Persistence;
using Xunit;

namespace SonicRelay.Api.IntegrationTests;

public sealed class SignalingGrantEndpointsTests : IClassFixture<SonicRelayApiFactory>
{
    private readonly SonicRelayApiFactory _factory;

    public SignalingGrantEndpointsTests(SonicRelayApiFactory factory) => _factory = factory;

    [Fact]
    public async Task Grant_requires_a_device_bearer_token()
    {
        using var client = _factory.CreateClient();
        using var response = await client.SendAsync(GrantRequest(Guid.NewGuid()));

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Grant_rejects_an_unknown_session()
    {
        var viewer = await CreatePermittedViewerAsync("unknown-session");
        using var response = await viewer.Client.SendAsync(GrantRequest(Guid.NewGuid(), viewer.AccessToken));

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Theory]
    [InlineData(SessionStatuses.Ended)]
    [InlineData(SessionStatuses.Expired)]
    public async Task Grant_rejects_a_terminal_session(string status)
    {
        var viewer = await CreatePermittedViewerAsync($"terminal-{status}");
        await SetSessionStatusAsync(viewer.SessionId, status);

        using var response = await viewer.Client.SendAsync(GrantRequest(viewer.SessionId, viewer.AccessToken));

        Assert.Equal(HttpStatusCode.Gone, response.StatusCode);
    }

    [Fact]
    public async Task Grant_rejects_a_viewer_that_is_not_connected()
    {
        var viewer = await CreatePermittedViewerAsync("disconnected-viewer");
        await SetParticipantStatusAsync(viewer.ParticipantId, ParticipantStatuses.Disconnected);

        using var response = await viewer.Client.SendAsync(GrantRequest(viewer.SessionId, viewer.AccessToken));

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task Grant_rejects_a_viewer_without_receive_permission()
    {
        var viewer = await CreatePermittedViewerAsync("receive-disabled-viewer");
        await SetViewerReceivePermissionAsync(viewer.ParticipantId, false);

        using var response = await viewer.Client.SendAsync(GrantRequest(viewer.SessionId, viewer.AccessToken));

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task Grant_issues_a_short_lived_secure_cookie_for_a_permitted_viewer()
    {
        var viewer = await CreatePermittedViewerAsync("permitted-viewer");
        using var response = await viewer.Client.SendAsync(GrantRequest(viewer.SessionId, viewer.AccessToken));

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        Assert.Equal(string.Empty, body);

        var cookie = response.Headers.GetValues("Set-Cookie").Single();
        Assert.Contains("sonicrelay_signaling=", cookie, StringComparison.Ordinal);
        Assert.Contains("HttpOnly", cookie, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Secure", cookie, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("SameSite=Strict", cookie, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Path=/ws/signaling", cookie, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Max-Age=60", cookie, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Domain=", cookie, StringComparison.OrdinalIgnoreCase);

        var token = cookie.Split(';')[0].Split('=', 2)[1];
        Assert.DoesNotContain(token, body, StringComparison.Ordinal);
        Assert.All(response.Headers.Where(header => !string.Equals(header.Key, "Set-Cookie", StringComparison.OrdinalIgnoreCase))
            .SelectMany(header => header.Value), headerValue =>
                Assert.DoesNotContain(token, headerValue, StringComparison.Ordinal));
    }

    private static HttpRequestMessage GrantRequest(Guid sessionId, string? accessToken = null)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/api/signaling/grant")
        {
            Content = JsonContent.Create(new { sessionId })
        };
        if (accessToken is not null) request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        return request;
    }

    private async Task<TestViewer> CreatePermittedViewerAsync(string prefix)
    {
        var client = _factory.CreateClient();
        var device = await DeviceIdentityTestHelper.BootstrapAndAuthorizeAsync(
            client, DeviceTypes.FlutterViewer, DevicePlatforms.Android, $"{prefix} device");
        var sessionId = Guid.NewGuid();
        var participantId = Guid.NewGuid();

        await using var scope = _factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        db.StreamSessions.Add(new StreamSession
        {
            Id = sessionId,
            SourceDeviceId = device.DeviceId,
            Status = SessionStatuses.Active,
            MaxViewers = 1,
            CodeExpiresAt = DateTimeOffset.UtcNow.AddMinutes(5),
            CreatedAt = DateTimeOffset.UtcNow
        });
        db.SessionParticipants.Add(new SessionParticipant
        {
            Id = participantId,
            SessionId = sessionId,
            DeviceId = device.DeviceId,
            Role = ParticipantRoles.Viewer,
            Status = ParticipantStatuses.Connected,
            CanReceiveAudio = true,
            JoinedAt = DateTimeOffset.UtcNow
        });
        await db.SaveChangesAsync();
        return new TestViewer(device.Client, device.AccessToken, sessionId, participantId);
    }

    private async Task SetSessionStatusAsync(Guid sessionId, string status)
    {
        await using var scope = _factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var session = await db.StreamSessions.SingleAsync(x => x.Id == sessionId);
        session.Status = status;
        await db.SaveChangesAsync();
    }

    private async Task SetParticipantStatusAsync(Guid participantId, string status)
    {
        await using var scope = _factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var participant = await db.SessionParticipants.SingleAsync(x => x.Id == participantId);
        participant.Status = status;
        await db.SaveChangesAsync();
    }

    private async Task SetViewerReceivePermissionAsync(Guid participantId, bool canReceiveAudio)
    {
        await using var scope = _factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var participant = await db.SessionParticipants.SingleAsync(x => x.Id == participantId);
        participant.CanReceiveAudio = canReceiveAudio;
        await db.SaveChangesAsync();
    }

    private sealed record TestViewer(HttpClient Client, string AccessToken, Guid SessionId, Guid ParticipantId);
}
