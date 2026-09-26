using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Net.WebSockets;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using SonicRelay.Api.Services;
using SonicRelay.Domain.Devices;
using SonicRelay.Domain.Sessions;
using SonicRelay.Infrastructure.Persistence;
using Xunit;

namespace SonicRelay.Api.IntegrationTests;

public sealed class LaunchIntentActivityTests
{
    private sealed class InstanceHttp : HttpMessageHandler, IHttpClientFactory
    {
        public int Calls;
        public HttpClient CreateClient(string name) => new(this, disposeHandler: false);
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Interlocked.Increment(ref Calls);
            Assert.Equal("Bot", request.Headers.Authorization!.Scheme);
            Assert.EndsWith("/applications/app/activity-instances/instance", request.RequestUri!.AbsoluteUri);
            await Task.Delay(20, ct);
            return new(HttpStatusCode.OK) { Content = JsonContent.Create(new { application_id = "app", instance_id = "instance", users = new[] { "3", "4" }, location = new { kind = "gc", guild_id = "1", channel_id = "2" } }) };
        }
    }
    private sealed class Discord : IDiscordActivityValidator
    {
        public volatile bool Present = true;
        public Task<DiscordActivityIdentity?> AuthorizeAsync(string code, string instanceId, CancellationToken ct) =>
            Task.FromResult<DiscordActivityIdentity?>(code == "valid" ? new("3", instanceId, "1", "2") : code == "other" ? new("4", instanceId, "1", "2") : null);
        public Task<bool> IsPresentAsync(DiscordActivityIdentity identity, CancellationToken ct) => Task.FromResult(Present);
    }
    private static SonicRelayApiFactory Factory() => new(new Dictionary<string, string?>
        { ["LaunchIntents:ServiceToken"] = "bot-secret", ["RelayLaunch:PublicBaseUrl"] = "https://relay.example" });
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
    public async Task Discord_instance_poll_is_coalesced_for_all_viewers_and_refreshes_after_five_seconds()
    {
        var transport = new InstanceHttp(); var clock = new TestTimeProvider();
        var cache = new DiscordActivityInstanceCache(transport, Options.Create(new RelayLaunchOptions { DiscordClientId = "app", DiscordBotToken = "secret" }), clock);
        var results = await Task.WhenAll(Enumerable.Range(0, 12).Select(_ => cache.GetAsync("instance", CancellationToken.None)));
        Assert.Equal(1, transport.Calls); Assert.All(results, x => Assert.Contains("4", x!.Users));
        clock.Advance(TimeSpan.FromSeconds(6)); await cache.GetAsync("instance", CancellationToken.None);
        Assert.Equal(2, transport.Calls);
    }

    [Fact]
    public async Task Bot_is_restricted_and_desktop_redemption_is_single_use_and_owner_bound()
    {
        using var factory = Factory(); using var bot = Bot(factory);
        Assert.Equal(HttpStatusCode.Unauthorized, (await factory.CreateClient().PostAsJsonAsync("/api/launch-intents/share", new { provider = "discord", guildId = "1", channelId = "2", requestedByUserId = "3" })).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await bot.PostAsJsonAsync("/api/sessions/", new { })).StatusCode);
        var intent = await Json(await bot.PostAsJsonAsync("/api/launch-intents/share", new { provider = "discord", guildId = "1", channelId = "2", requestedByUserId = "3" }));
        var pending = await Json(await bot.GetAsync("/api/launch-intents/pending"));
        Assert.Equal(JsonValueKind.Array, pending.ValueKind);
        Assert.Equal(intent.GetProperty("id").GetGuid(), pending[0].GetProperty("id").GetGuid());
        Assert.Equal("pending", pending[0].GetProperty("status").GetString());
        var launchUrl = intent.GetProperty("launchUrl").GetString()!;
        var token = launchUrl[(launchUrl.LastIndexOf('/') + 1)..];
        using var host = factory.CreateClient(); await DeviceIdentityTestHelper.BootstrapAndAuthorizeAsync(host, DeviceTypes.WindowsDesktop, DevicePlatforms.Windows);
        Assert.Equal(HttpStatusCode.OK, (await host.PostAsJsonAsync("/api/launch-intents/share/consume", new { token })).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await host.PostAsJsonAsync("/api/launch-intents/share/consume", new { token })).StatusCode);
        var session = await Screen(host);
        var id = intent.GetProperty("id").GetGuid();
        Assert.Equal(HttpStatusCode.NoContent, (await host.PostAsJsonAsync($"/api/launch-intents/share/{id}/complete", new { sessionId = session.GetProperty("id").GetGuid() })).StatusCode);
        var polling = await Json(await bot.GetAsync($"/api/launch-intents/{id}?watchTtlSeconds=0"));
        Assert.Equal("session_ready", polling.GetProperty("status").GetString());
        Assert.Equal(session.GetProperty("id").GetGuid(), polling.GetProperty("sessionId").GetGuid());
        Assert.Equal(JsonValueKind.String, polling.GetProperty("watchLaunchUrl").ValueKind);
        var ready = await Json(await bot.GetAsync($"/api/launch-intents/{id}?watchTtlSeconds=60"));
        Assert.Equal("session_ready", ready.GetProperty("status").GetString());
        Assert.Equal(JsonValueKind.String, ready.GetProperty("watchLaunchUrl").ValueKind);
        var defaultWatch = await Json(await bot.GetAsync($"/api/launch-intents/{id}"));
        Assert.Equal(JsonValueKind.String, defaultWatch.GetProperty("watchLaunchUrl").ValueKind);
        var readyItems = await Json(await bot.GetAsync("/api/launch-intents/pending"));
        Assert.Equal("session_ready", readyItems[0].GetProperty("status").GetString());
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
        using var factory = new SonicRelayApiFactory(new Dictionary<string, string?> { ["LaunchIntents:ServiceToken"] = "bot-secret" }) { TimeProviderOverride = clock };
        using var bot = Bot(factory);
        var intent = await Json(await bot.PostAsJsonAsync("/api/launch-intents/share", new { provider = "discord", guildId = "1", channelId = "2", requestedByUserId = "3", ttlSeconds = 30 }));
        var launchUrl = intent.GetProperty("launchUrl").GetString()!;
        var token = launchUrl[(launchUrl.LastIndexOf('/') + 1)..];
        using var host = factory.CreateClient(); await DeviceIdentityTestHelper.BootstrapAndAuthorizeAsync(host, DeviceTypes.WindowsPublisher, DevicePlatforms.Windows);
        clock.Advance(TimeSpan.FromSeconds(31));
        Assert.Equal(HttpStatusCode.Conflict, (await host.PostAsJsonAsync("/api/launch-intents/share/consume", new { token })).StatusCode);
    }

    [Fact]
    public async Task Concurrent_first_binds_keep_one_session_and_database_has_unique_instance_backstop()
    {
        using var root = Factory(); using var factory = root.WithWebHostBuilder(builder => builder.ConfigureServices(services =>
        { services.RemoveAll<IDiscordActivityValidator>(); services.AddSingleton<IDiscordActivityValidator, Discord>(); }));
        using var host = factory.CreateClient(); await DeviceIdentityTestHelper.BootstrapAndAuthorizeAsync(host, DeviceTypes.WindowsDesktop, DevicePlatforms.Windows);
        var first = await Screen(host); var second = await Screen(host);
        using var bot = Bot(factory); using var viewer = factory.CreateClient();
        foreach (var item in new[] { (Screen: first, User: "3"), (Screen: second, User: "4") })
            await Json(await bot.PostAsJsonAsync("/api/launch-intents/activity", new { code = item.Screen.GetProperty("code").GetString(), guildId = "1", channelId = "2", requestedByUserId = item.User }));
        var responses = await Task.WhenAll(viewer.PostAsJsonAsync("/api/discord/activity/authorize", new { code = "valid", instanceId = "instance" }),
            viewer.PostAsJsonAsync("/api/discord/activity/authorize", new { code = "other", instanceId = "instance" }));
        Assert.Single(responses, x => x.StatusCode == HttpStatusCode.OK);
        Assert.Single(responses, x => x.StatusCode == HttpStatusCode.Conflict);
        using var scope = factory.Services.CreateScope(); var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        Assert.Equal(1, await db.LaunchCapabilities.CountAsync(x => x.Kind == "activity" && x.InstanceId == "instance"));
        Assert.Contains(db.Model.FindEntityType(typeof(LaunchCapability))!.GetIndexes(), x => x.IsUnique && x.Properties.Single().Name == "InstanceId" && x.GetFilter()!.Contains("activity"));
    }

    [Fact]
    public async Task New_watch_in_busy_instance_conflicts_and_can_bind_after_previous_session_ends()
    {
        using var root = Factory(); using var factory = root.WithWebHostBuilder(builder => builder.ConfigureServices(services =>
        { services.RemoveAll<IDiscordActivityValidator>(); services.AddSingleton<IDiscordActivityValidator, Discord>(); }));
        using var host = factory.CreateClient(); await DeviceIdentityTestHelper.BootstrapAndAuthorizeAsync(host, DeviceTypes.WindowsDesktop, DevicePlatforms.Windows);
        var first = await Screen(host); var second = await Screen(host);
        using var bot = Bot(factory); using var viewer = factory.CreateClient();
        await Json(await bot.PostAsJsonAsync("/api/launch-intents/activity", new { code = first.GetProperty("code").GetString(), guildId = "1", channelId = "2", requestedByUserId = "3" }));
        var old = await Json(await viewer.PostAsJsonAsync("/api/discord/activity/authorize", new { code = "valid", instanceId = "instance" }));
        await Json(await bot.PostAsJsonAsync("/api/launch-intents/activity", new { code = second.GetProperty("code").GetString(), guildId = "1", channelId = "2", requestedByUserId = "3" }));
        Assert.Equal(HttpStatusCode.Conflict, (await viewer.PostAsJsonAsync("/api/discord/activity/authorize", new { code = "valid", instanceId = "instance" })).StatusCode);
        await host.PostAsJsonAsync("/api/sessions/" + first.GetProperty("id").GetGuid() + "/end", new { });
        var fresh = await Json(await viewer.PostAsJsonAsync("/api/discord/activity/authorize", new { code = "valid", instanceId = "instance" }));
        viewer.DefaultRequestHeaders.Authorization = new("Bearer", old.GetProperty("accessToken").GetString());
        Assert.Equal(HttpStatusCode.Unauthorized, (await viewer.PostAsync("/api/discord/activity/viewer-grants", null)).StatusCode);
        viewer.DefaultRequestHeaders.Authorization = new("Bearer", fresh.GetProperty("accessToken").GetString());
        var grant = await Json(await viewer.PostAsync("/api/discord/activity/viewer-grants", null));
        var admitted = await Json(await viewer.PostAsJsonAsync("/api/discord/activity/viewer-grants/redeem", new { grant = grant.GetProperty("grant").GetString() }));
        Assert.Equal(second.GetProperty("id").GetGuid(), admitted.GetProperty("sessionId").GetGuid());
    }

    [Fact]
    public async Task Observed_discord_departure_closes_socket_and_revokes_outstanding_credentials()
    {
        var discord = new Discord();
        using var root = Factory(); using var factory = root.WithWebHostBuilder(builder => builder.ConfigureServices(services =>
        { services.RemoveAll<IDiscordActivityValidator>(); services.AddSingleton<IDiscordActivityValidator>(discord); }));
        using var host = factory.CreateClient(); await DeviceIdentityTestHelper.BootstrapAndAuthorizeAsync(host, DeviceTypes.WindowsDesktop, DevicePlatforms.Windows);
        var screen = await Screen(host); using var bot = Bot(factory); using var viewer = factory.CreateClient();
        await Json(await bot.PostAsJsonAsync("/api/launch-intents/activity", new { code = screen.GetProperty("code").GetString(), guildId = "1", channelId = "2", requestedByUserId = "3" }));
        var identity = await Json(await viewer.PostAsJsonAsync("/api/discord/activity/authorize", new { code = "valid", instanceId = "instance" }));
        viewer.DefaultRequestHeaders.Authorization = new("Bearer", identity.GetProperty("accessToken").GetString());
        var grant = await Json(await viewer.PostAsync("/api/discord/activity/viewer-grants", null));
        var admitted = await Json(await viewer.PostAsJsonAsync("/api/discord/activity/viewer-grants/redeem", new { grant = grant.GetProperty("grant").GetString() }));
        var outstanding = await Json(await viewer.PostAsync("/api/discord/activity/viewer-grants", null));
        var client = factory.Server.CreateWebSocketClient();
        client.ConfigureRequest = request => request.Headers["Sec-WebSocket-Protocol"] = "framerelay,token." + admitted.GetProperty("signalingToken").GetString();
        using var socket = await client.ConnectAsync(new Uri("ws://localhost/ws/signaling?sessionId=" + admitted.GetProperty("sessionId").GetGuid()), CancellationToken.None);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(12));
        var buffer = new byte[65536];
        await socket.ReceiveAsync(new ArraySegment<byte>(buffer), deadline.Token);
        discord.Present = false;
        WebSocketReceiveResult frame;
        do { frame = await socket.ReceiveAsync(new ArraySegment<byte>(buffer), deadline.Token); } while (frame.MessageType != WebSocketMessageType.Close);
        Assert.Equal(HttpStatusCode.Unauthorized, (await viewer.PostAsJsonAsync("/api/discord/activity/viewer-grants/redeem", new { grant = outstanding.GetProperty("grant").GetString() })).StatusCode);
        using var scope = factory.Services.CreateScope(); var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        Assert.Equal(ParticipantStatuses.Disconnected, (await db.SessionParticipants.FindAsync(admitted.GetProperty("participantId").GetGuid()))!.Status);
        await socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "done", deadline.Token);
    }
}
