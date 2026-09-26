using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using SonicRelay.Api.Services;
using SonicRelay.Domain.Devices;
using Xunit;

namespace SonicRelay.Api.IntegrationTests;

public sealed class LaunchIntentActivityTests
{
    private sealed class Discord : IDiscordActivityValidator
    {
        public Task<DiscordActivityIdentity?> AuthorizeAsync(string code, string instanceId, CancellationToken ct) =>
            Task.FromResult<DiscordActivityIdentity?>(code == "valid" ? new("3", instanceId, "1", "2") : code == "other" ? new("4", instanceId, "1", "2") : null);
        public Task<bool> IsPresentAsync(DiscordActivityIdentity identity, CancellationToken ct) => Task.FromResult(true);
    }
    private static SonicRelayApiFactory Factory() => new(new Dictionary<string, string?>
        { ["RelayLaunch:ServiceToken"] = "bot-secret", ["RelayLaunch:PublicBaseUrl"] = "https://relay.example" });
    private static HttpClient Bot(Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactory<Program> factory)
    {
        var bot = factory.CreateClient(); bot.DefaultRequestHeaders.Authorization = new("Bearer", "bot-secret"); return bot;
    }
    private static async Task<JsonElement> Json(HttpResponseMessage response)
    {
        response.EnsureSuccessStatusCode(); return (await response.Content.ReadFromJsonAsync<JsonElement>()).Clone();
    }
    private static async Task<JsonElement> Screen(HttpClient host) => await Json(await host.PostAsJsonAsync("/api/sessions/", new { mode = "screen_share", maxViewers = 1 }));

    [Fact]
    public async Task Bot_is_restricted_and_desktop_redemption_is_single_use_and_owner_bound()
    {
        using var factory = Factory(); using var bot = Bot(factory);
        Assert.Equal(HttpStatusCode.Unauthorized, (await factory.CreateClient().PostAsJsonAsync("/api/launch-intents/share", new { provider = "discord", guildId = "1", channelId = "2", requestedByUserId = "3" })).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await bot.PostAsJsonAsync("/api/sessions/", new { })).StatusCode);
        var intent = await Json(await bot.PostAsJsonAsync("/api/launch-intents/share", new { provider = "discord", guildId = "1", channelId = "2", requestedByUserId = "3" }));
        var token = new Uri(intent.GetProperty("launchUrl").GetString()!).Fragment[1..];
        using var host = factory.CreateClient(); await DeviceIdentityTestHelper.BootstrapAndAuthorizeAsync(host, DeviceTypes.WindowsDesktop, DevicePlatforms.Windows);
        var redeemed = await Json(await host.PostAsJsonAsync("/api/launch-intents/redeem", new { token }));
        Assert.Equal("share", redeemed.GetProperty("kind").GetString());
        Assert.Equal(HttpStatusCode.NotFound, (await host.PostAsJsonAsync("/api/launch-intents/redeem", new { token })).StatusCode);
        var session = await Screen(host);
        using var other = factory.CreateClient(); await DeviceIdentityTestHelper.BootstrapAndAuthorizeAsync(other, DeviceTypes.WindowsDesktop, DevicePlatforms.Windows);
        var id = intent.GetProperty("id").GetGuid();
        Assert.Equal(HttpStatusCode.NotFound, (await other.PostAsJsonAsync($"/api/launch-intents/{id}/bind", new { sessionId = session.GetProperty("id").GetGuid() })).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await host.PostAsJsonAsync($"/api/launch-intents/{id}/bind", new { sessionId = session.GetProperty("id").GetGuid() })).StatusCode);
        var ready = await Json(await bot.GetAsync($"/api/launch-intents/{id}?watchTtlSeconds=60")); Assert.Equal("ready", ready.GetProperty("status").GetString());
    }

    [Fact]
    public async Task Activity_requires_invoker_bootstrap_and_enforces_single_use_grants_and_viewer_limit()
    {
        using var root = Factory(); using var factory = root.WithWebHostBuilder(builder => builder.ConfigureServices(services =>
        { services.RemoveAll<IDiscordActivityValidator>(); services.AddSingleton<IDiscordActivityValidator, Discord>(); }));
        using var host = factory.CreateClient(); await DeviceIdentityTestHelper.BootstrapAndAuthorizeAsync(host, DeviceTypes.WindowsDesktop, DevicePlatforms.Windows);
        var screen = await Screen(host); using var bot = Bot(factory); using var viewer = factory.CreateClient();
        await Json(await bot.PostAsJsonAsync("/api/launch-intents/activity", new { code = screen.GetProperty("code").GetString(), guildId = "1", channelId = "2", requestedByUserId = "3" }));
        Assert.Equal(HttpStatusCode.Unauthorized, (await viewer.PostAsJsonAsync("/api/discord/activity/authorize", new { code = "other", instanceId = "instance" })).StatusCode);
        var identity = await Json(await viewer.PostAsJsonAsync("/api/discord/activity/authorize", new { code = "valid", instanceId = "instance" }));
        Assert.Equal(HttpStatusCode.Unauthorized, (await viewer.PostAsJsonAsync("/api/discord/activity/authorize", new { code = "valid", instanceId = "other-instance" })).StatusCode);
        await Json(await viewer.PostAsJsonAsync("/api/discord/activity/authorize", new { code = "other", instanceId = "instance" }));
        viewer.DefaultRequestHeaders.Authorization = new("Bearer", identity.GetProperty("accessToken").GetString());
        Assert.Equal(HttpStatusCode.Unauthorized, (await viewer.PostAsJsonAsync("/api/sessions/", new { })).StatusCode);
        var grant = await Json(await viewer.PostAsync("/api/discord/activity/viewer-grants", null));
        var admitted = await Json(await viewer.PostAsJsonAsync("/api/discord/activity/viewer-grants/redeem", new { grant = grant.GetProperty("grant").GetString() }));
        Assert.Equal(screen.GetProperty("id").GetGuid(), admitted.GetProperty("sessionId").GetGuid());
        Assert.False(admitted.TryGetProperty("accessToken", out _)); Assert.True(admitted.TryGetProperty("signalingToken", out _));
        using var signal = factory.CreateClient();
        signal.DefaultRequestHeaders.Add("Sec-WebSocket-Protocol", "framerelay,token." + admitted.GetProperty("signalingToken").GetString());
        Assert.Equal(HttpStatusCode.Unauthorized, (await signal.GetAsync("/ws/signaling?sessionId=" + Guid.NewGuid())).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await signal.GetAsync("/ws/signaling?sessionId=" + admitted.GetProperty("sessionId").GetGuid())).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await signal.GetAsync("/ws/signaling?sessionId=" + admitted.GetProperty("sessionId").GetGuid())).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await viewer.PostAsJsonAsync("/api/discord/activity/viewer-grants/redeem", new { grant = grant.GetProperty("grant").GetString() })).StatusCode);
        var another = await Json(await viewer.PostAsync("/api/discord/activity/viewer-grants", null));
        Assert.Equal(HttpStatusCode.Conflict, (await viewer.PostAsJsonAsync("/api/discord/activity/viewer-grants/redeem", new { grant = another.GetProperty("grant").GetString() })).StatusCode);
        await host.PostAsJsonAsync("/api/sessions/" + screen.GetProperty("id").GetGuid() + "/end", new { });
        Assert.Equal(HttpStatusCode.Unauthorized, (await viewer.PostAsync("/api/discord/activity/viewer-grants", null)).StatusCode);
    }

    [Fact]
    public async Task Expired_launch_capability_cannot_be_redeemed()
    {
        var clock = new TestTimeProvider();
        using var factory = new SonicRelayApiFactory(new Dictionary<string, string?> { ["RelayLaunch:ServiceToken"] = "bot-secret" }) { TimeProviderOverride = clock };
        using var bot = Bot(factory);
        var intent = await Json(await bot.PostAsJsonAsync("/api/launch-intents/share", new { provider = "discord", guildId = "1", channelId = "2", requestedByUserId = "3", ttlSeconds = 30 }));
        var token = intent.GetProperty("launchUrl").GetString()!.Split('#')[1];
        using var host = factory.CreateClient(); await DeviceIdentityTestHelper.BootstrapAndAuthorizeAsync(host, DeviceTypes.WindowsDesktop, DevicePlatforms.Windows);
        clock.Advance(TimeSpan.FromSeconds(31));
        Assert.Equal(HttpStatusCode.NotFound, (await host.PostAsJsonAsync("/api/launch-intents/redeem", new { token })).StatusCode);
    }
}
