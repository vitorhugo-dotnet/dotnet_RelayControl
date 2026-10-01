using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using SonicRelay.Api.Services;
using SonicRelay.Domain.Sessions;
using SonicRelay.Infrastructure.Persistence;
using Xunit;

namespace SonicRelay.Api.IntegrationTests;

public sealed class FeatureFlagEndpointsTests
{
    [Fact]
    public async Task PublicRooms_false_hides_endpoint_without_seeding()
    {
        await using var factory = new SonicRelayApiFactory(new Dictionary<string, string?>
        { ["FeatureManagement:PublicRooms"] = "false", ["PublicRoom:Enabled"] = "true" });
        using var client = factory.CreateClient();
        await DeviceIdentityTestHelper.BootstrapAndAuthorizeAsync(client, "windows_desktop", "windows");
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync("/api/public-room")).StatusCode);
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        Assert.False(await db.StreamSessions.AnyAsync(x => x.Id == PublicRoomSeeder.PublicSessionId));
    }

    [Theory]
    [InlineData("/api/discord/activity/authorize", false)]
    [InlineData("/api/discord/activity/viewer-grants", false)]
    [InlineData("/api/discord/activity/viewer-grants/redeem", false)]
    [InlineData("/api/launch-intents/activity", true)]
    public async Task DiscordActivity_false_hides_activity_routes(string path, bool bot)
    {
        await using var factory = new SonicRelayApiFactory(new Dictionary<string, string?>
        { ["FeatureManagement:DiscordActivity"] = "false" });
        using var client = factory.CreateClient();
        if (bot) client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "integration-test-discord-service-token");
        var response = await client.PostAsJsonAsync(path, new
        { code = "TEST", instanceId = "instance", grant = "grant", guildId = "1", channelId = "2", requestedByUserId = "3", ttlSeconds = 60 });
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Theory]
    [InlineData("DuplexAudio", "duplex")]
    [InlineData("ScreenShare", "screen_share")]
    public async Task Disabled_mode_rejects_create_while_broadcast_works(string feature, string mode)
    {
        await using var factory = new SonicRelayApiFactory(new Dictionary<string, string?>
        { [$"FeatureManagement:{feature}"] = "false" });
        using var client = factory.CreateClient();
        await DeviceIdentityTestHelper.BootstrapAndAuthorizeAsync(client, "windows_desktop", "windows");
        var response = await client.PostAsJsonAsync("/api/sessions", new { mode });
        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Contains("feature_disabled", await response.Content.ReadAsStringAsync());
        Assert.Equal(HttpStatusCode.Created, (await client.PostAsJsonAsync("/api/sessions", new { mode = "broadcast" })).StatusCode);
        using var scope = factory.Services.CreateScope();
        Assert.False(await scope.ServiceProvider.GetRequiredService<AppDbContext>().StreamSessions.AnyAsync(x => x.Mode == mode));
    }

    [Theory]
    [InlineData("ScreenShare", "screen_share")]
    [InlineData("DuplexAudio", "duplex")]
    public async Task Disabled_mode_blocks_join_and_preserves_session_end(string feature, string mode)
    {
        await using var factory = new SonicRelayApiFactory(new Dictionary<string, string?>
        { [$"FeatureManagement:{feature}"] = "false" });
        using var owner = factory.CreateClient();
        var identity = await DeviceIdentityTestHelper.BootstrapAndAuthorizeAsync(owner, "windows_desktop", "windows");
        var session = new StreamSession
        { Id = Guid.NewGuid(), SourceDeviceId = identity.DeviceId, Mode = mode, CodeExpiresAt = DateTimeOffset.UtcNow.AddMinutes(10), CreatedAt = DateTimeOffset.UtcNow };
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            db.StreamSessions.Add(session);
            await db.SaveChangesAsync();
        }
        var codeHash = Convert.ToHexString(System.Security.Cryptography.HMACSHA256.HashData(
            System.Text.Encoding.UTF8.GetBytes("integration-test-session-code-key"), System.Text.Encoding.ASCII.GetBytes("ABC123")));
        await factory.Services.GetRequiredService<SonicRelay.Application.Abstractions.ISessionCodeStore>()
            .StoreAsync(codeHash, session.Id, TimeSpan.FromMinutes(10), CancellationToken.None);
        Assert.Equal(HttpStatusCode.Conflict, (await owner.PostAsJsonAsync("/api/sessions/join", new { code = "ABC123" })).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await owner.PostAsJsonAsync($"/api/sessions/{session.Id}/join", new { })).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await owner.PostAsJsonAsync($"/api/sessions/{session.Id}/end", new { })).StatusCode);
    }

    [Fact]
    public async Task Discord_disable_preserves_desktop_session_and_ice_routes()
    {
        await using var factory = new SonicRelayApiFactory(new Dictionary<string, string?>
        { ["FeatureManagement:DiscordActivity"] = "false" });
        using var client = factory.CreateClient();
        await DeviceIdentityTestHelper.BootstrapAndAuthorizeAsync(client, "windows_desktop", "windows");
        Assert.Equal(HttpStatusCode.Created, (await client.PostAsJsonAsync("/api/sessions", new { mode = "screen_share" })).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/webrtc/ice-servers")).StatusCode);
    }

    [Fact]
    public async Task PublicRooms_false_starts_no_publisher_work()
    {
        await using var factory = new SonicRelayApiFactory(new Dictionary<string, string?> {
            ["PublicRoom:Enabled"] = "true", ["FeatureManagement:PublicRooms"] = "false" });
        using var client = factory.CreateClient();
        var service = factory.Services.GetRequiredService<PublicRoomPublisherService>();
        await service.ExecuteTask!.WaitAsync(TimeSpan.FromSeconds(3));
        using var scope = factory.Services.CreateScope();
        Assert.False(await scope.ServiceProvider.GetRequiredService<AppDbContext>().StreamSessions.AnyAsync());
    }
}
