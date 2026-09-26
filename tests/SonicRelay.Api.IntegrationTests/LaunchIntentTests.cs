using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using SonicRelay.Infrastructure.Persistence;
using SonicRelay.Domain.Devices;
using Xunit;

namespace SonicRelay.Api.IntegrationTests;

public sealed class LaunchIntentTests
{
    [Fact]
    public async Task Create_share_intent_returns_https_link_and_persists_only_token_hash()
    {
        using var factory = new SonicRelayApiFactory(new Dictionary<string, string?>
        {
            ["LaunchIntents:ServiceToken"] = "test-discord-service-token"
        });
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "test-discord-service-token");

        var response = await client.PostAsJsonAsync("/api/launch-intents/share", new
        {
            provider = "discord",
            guildId = "guild-1",
            channelId = "channel-2",
            requestedByUserId = "user-3"
        });

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        var launchUrl = body.GetProperty("launchUrl").GetString()!;
        Assert.StartsWith("https://framerelay.hugojava.dev/open/share/", launchUrl, StringComparison.Ordinal);
        var token = launchUrl[(launchUrl.LastIndexOf('/') + 1)..];
        await using var scope = factory.Services.CreateAsyncScope();
        var intent = await scope.ServiceProvider.GetRequiredService<AppDbContext>()
            .ShareLaunchIntents.SingleAsync();
        Assert.NotEqual(token, intent.TokenHash);
        Assert.Equal(Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token))), intent.TokenHash);
        Assert.Equal("guild-1", intent.GuildId);
        Assert.Equal("channel-2", intent.ChannelId);
        Assert.Equal("user-3", intent.RequestedByUserId);
        Assert.Equal("pending", intent.Status);
    }

    [Fact]
    public async Task Create_share_intent_requires_the_service_credential()
    {
        using var factory = new SonicRelayApiFactory(new Dictionary<string, string?>
        {
            ["LaunchIntents:ServiceToken"] = "test-discord-service-token"
        });
        using var client = factory.CreateClient();

        var response = await client.PostAsJsonAsync("/api/launch-intents/share", new
        {
            provider = "discord",
            guildId = "guild-1",
            channelId = "channel-2",
            requestedByUserId = "user-3"
        });

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Publisher_consumes_once_creates_normal_session_and_completes_intent()
    {
        using var factory = new SonicRelayApiFactory();
        using var bot = factory.CreateClient();
        bot.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "Bearer", "integration-test-discord-service-token");
        var createdIntent = await bot.PostAsJsonAsync("/api/launch-intents/share", new
        {
            provider = "discord", guildId = "guild-1", channelId = "channel-2", requestedByUserId = "user-3"
        });
        createdIntent.EnsureSuccessStatusCode();
        var launch = await createdIntent.Content.ReadFromJsonAsync<JsonElement>();
        var token = launch.GetProperty("launchUrl").GetString()![("https://framerelay.hugojava.dev/open/share/".Length)..];

        using var publisher = factory.CreateClient();
        await DeviceIdentityTestHelper.BootstrapAndAuthorizeAsync(
            publisher, DeviceTypes.WindowsPublisher, DevicePlatforms.Windows);
        var consumed = await publisher.PostAsJsonAsync("/api/launch-intents/share/consume", new { token });
        Assert.Equal(HttpStatusCode.OK, consumed.StatusCode);
        var secondConsume = await publisher.PostAsJsonAsync("/api/launch-intents/share/consume", new { token });
        Assert.Equal(HttpStatusCode.Conflict, secondConsume.StatusCode);

        var sessionResponse = await publisher.PostAsJsonAsync("/api/sessions", new { maxViewers = 3, mode = "screen_share" });
        sessionResponse.EnsureSuccessStatusCode();
        var session = await sessionResponse.Content.ReadFromJsonAsync<JsonElement>();
        var completed = await publisher.PostAsJsonAsync(
            $"/api/launch-intents/share/{launch.GetProperty("id").GetGuid()}/complete",
            new { sessionId = session.GetProperty("id").GetGuid() });
        Assert.Equal(HttpStatusCode.NoContent, completed.StatusCode);
        var retriedCompletion = await publisher.PostAsJsonAsync(
            $"/api/launch-intents/share/{launch.GetProperty("id").GetGuid()}/complete",
            new { sessionId = session.GetProperty("id").GetGuid() });
        Assert.Equal(HttpStatusCode.NoContent, retriedCompletion.StatusCode);

        var ready = await bot.GetFromJsonAsync<JsonElement>($"/api/launch-intents/{launch.GetProperty("id").GetGuid()}");
        Assert.Equal("session_ready", ready.GetProperty("status").GetString());
        Assert.Equal(session.GetProperty("id").GetGuid(), ready.GetProperty("sessionId").GetGuid());
        Assert.StartsWith("https://framerelay.hugojava.dev/open/watch/",
            ready.GetProperty("watchLaunchUrl").GetString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Viewer_resolves_watch_capability_with_join_scope_only()
    {
        using var factory = new SonicRelayApiFactory();
        using var publisher = factory.CreateClient();
        await DeviceIdentityTestHelper.BootstrapAndAuthorizeAsync(
            publisher, DeviceTypes.WindowsPublisher, DevicePlatforms.Windows);
        var created = await publisher.PostAsJsonAsync("/api/sessions", new { maxViewers = 3, mode = "screen_share" });
        var session = await created.Content.ReadFromJsonAsync<JsonElement>();
        var code = session.GetProperty("code").GetString()!;

        using var bot = factory.CreateClient();
        bot.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "Bearer", "integration-test-discord-service-token");
        var watch = await bot.PostAsJsonAsync("/api/launch-intents/watch", new { code = $" {code.ToLowerInvariant()} " });
        Assert.Equal(HttpStatusCode.OK, watch.StatusCode);
        var launch = await watch.Content.ReadFromJsonAsync<JsonElement>();
        var token = launch.GetProperty("launchUrl").GetString()![("https://framerelay.hugojava.dev/open/watch/".Length)..];

        using var viewer = factory.CreateClient();
        await DeviceIdentityTestHelper.BootstrapAndAuthorizeAsync(
            viewer, DeviceTypes.FlutterViewer, DevicePlatforms.Android);
        var resolved = await viewer.PostAsJsonAsync("/api/launch-intents/watch/resolve", new { token });
        Assert.Equal(HttpStatusCode.OK, resolved.StatusCode);
        var resolution = await resolved.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(session.GetProperty("id").GetGuid(), resolution.GetProperty("sessionId").GetGuid());
        var joined = await viewer.PostAsync($"/api/sessions/{resolution.GetProperty("sessionId").GetGuid()}/join", null);
        Assert.Equal(HttpStatusCode.Forbidden, joined.StatusCode);
    }

    [Fact]
    public async Task Landing_page_uses_custom_protocol_and_safe_headers()
    {
        using var factory = new SonicRelayApiFactory();
        using var client = factory.CreateClient();
        var token = new string('a', 48);

        using var response = await client.GetAsync($"/open/watch/{token}");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("no-store", response.Headers.CacheControl!.ToString());
        Assert.Equal("no-referrer", response.Headers.GetValues("Referrer-Policy").Single());
        var html = await response.Content.ReadAsStringAsync();
        Assert.Contains($"framerelay://open/watch/{token}", html, StringComparison.Ordinal);
        Assert.Contains("Install FrameRelay", html, StringComparison.Ordinal);
    }
}
