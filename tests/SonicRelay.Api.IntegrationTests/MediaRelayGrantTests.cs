using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection.Extensions;
using SonicRelay.Api.Services;
using SonicRelay.Domain.Sessions;
using SonicRelay.Infrastructure.Persistence;
using Xunit;

namespace SonicRelay.Api.IntegrationTests;

public sealed class MediaRelayGrantTests
{
    private sealed class Clock : TimeProvider
    {
        public DateTimeOffset Now = DateTimeOffset.UtcNow;
        public override DateTimeOffset GetUtcNow() => Now;
    }
    [Fact]
    public async Task Upload_expiry_and_source_revocation_reject_renewal_without_disconnecting_rtc()
    {
        var clock = new Clock();
        await using var factory = new SonicRelayApiFactory(new Dictionary<string, string?> {
            ["FeatureManagement:DiscordWebSocketMedia"] = "true", ["MediaRelay:ServiceToken"] = "media-test" }) { TimeProviderOverride = clock };
        using var owner = factory.CreateClient(); var device = await DeviceIdentityTestHelper.BootstrapAndAuthorizeAsync(owner, "windows_desktop", "windows");
        var session = await Json(await owner.PostAsJsonAsync("/api/sessions", new { mode = "screen_share" }));
        var id = session.GetProperty("id").GetGuid();
        using var stranger = factory.CreateClient(); await DeviceIdentityTestHelper.BootstrapAndAuthorizeAsync(stranger, "windows_desktop", "windows");
        Assert.Equal(HttpStatusCode.NotFound, (await stranger.PostAsync($"/api/sessions/{id}/media-grants", null)).StatusCode);
        var normal = await Json(await owner.PostAsJsonAsync("/api/sessions", new { mode = "broadcast" }));
        Assert.Equal(HttpStatusCode.NotFound, (await owner.PostAsync($"/api/sessions/{normal.GetProperty("id").GetGuid()}/media-grants", null)).StatusCode);
        var grant = await Json(await owner.PostAsync($"/api/sessions/{id}/media-grants", null));
        var connectionId = Guid.NewGuid();
        Assert.Equal(HttpStatusCode.Unauthorized, (await owner.PostAsJsonAsync("/api/internal/media-relay/redeem", new { grant = grant.GetProperty("grant").GetString(), connectionId })).StatusCode);
        using var relay = factory.CreateClient(); relay.DefaultRequestHeaders.Authorization = new("Bearer", "media-test");
        var lease = await Json(await relay.PostAsJsonAsync("/api/internal/media-relay/redeem", new { grant = grant.GetProperty("grant").GetString(), connectionId }));
        var leaseId = lease.GetProperty("leaseId").GetGuid(); clock.Now = clock.Now.AddSeconds(31);
        Assert.Equal(HttpStatusCode.Unauthorized, (await relay.PostAsJsonAsync("/api/internal/media-relay/renew", new { leaseId, connectionId })).StatusCode);
        using var scope = factory.Services.CreateScope(); var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        Assert.Equal(ParticipantStatuses.Connected, (await db.SessionParticipants.SingleAsync(x => x.SessionId == id && x.Role == ParticipantRoles.Publisher)).Status);
        var fresh = await Json(await owner.PostAsync($"/api/sessions/{id}/media-grants", null));
        var freshLease = await Json(await relay.PostAsJsonAsync("/api/internal/media-relay/redeem", new { grant = fresh.GetProperty("grant").GetString(), connectionId }));
        var identity = await db.DeviceIdentities.SingleAsync(x => x.Id == device.DeviceId); identity.CredentialVersion++; await db.SaveChangesAsync();
        Assert.Equal(HttpStatusCode.Unauthorized, (await relay.PostAsJsonAsync("/api/internal/media-relay/renew", new { leaseId = freshLease.GetProperty("leaseId").GetGuid(), connectionId })).StatusCode);
    }
    private sealed class Discord : IDiscordActivityValidator
    {
        public bool Present = true;
        public Task<DiscordActivityIdentity?> AuthorizeAsync(string code, string instanceId, CancellationToken ct) => Task.FromResult<DiscordActivityIdentity?>(new("3", instanceId, "1", "2"));
        public Task<bool> IsPresentAsync(DiscordActivityIdentity identity, CancellationToken ct) => Task.FromResult(Present);
    }

    [Fact]
    public async Task Reconnection_reuses_capacity_and_old_release_cannot_disconnect_new_admission()
    {
        var discord = new Discord();
        await using var root = new SonicRelayApiFactory(new Dictionary<string, string?> {
            ["FeatureManagement:DiscordWebSocketMedia"] = "true", ["MediaRelay:ServiceToken"] = "media-test", ["LaunchIntents:ServiceToken"] = "bot-test" });
        await using var factory = root.WithWebHostBuilder(builder => builder.ConfigureServices(services => {
            services.RemoveAll<IDiscordActivityValidator>(); services.AddSingleton<IDiscordActivityValidator>(discord); }));
        using var host = factory.CreateClient(); await DeviceIdentityTestHelper.BootstrapAndAuthorizeAsync(host, "windows_desktop", "windows");
        var session = await Json(await host.PostAsJsonAsync("/api/sessions", new { mode = "screen_share", maxViewers = 1 }));
        using var bot = factory.CreateClient(); bot.DefaultRequestHeaders.Authorization = new("Bearer", "bot-test");
        Assert.Equal(HttpStatusCode.OK, (await bot.PostAsJsonAsync("/api/launch-intents/activity", new { code = session.GetProperty("code").GetString(), guildId = "1", channelId = "2", requestedByUserId = "3" })).StatusCode);
        using var viewer = factory.CreateClient();
        var identity = await Json(await viewer.PostAsJsonAsync("/api/discord/activity/authorize", new { code = "valid", instanceId = "instance" }));
        viewer.DefaultRequestHeaders.Authorization = new("Bearer", identity.GetProperty("accessToken").GetString());
        async Task<JsonElement> Admit()
        {
            var grant = await Json(await viewer.PostAsync("/api/discord/activity/viewer-grants", null));
            return await Json(await viewer.PostAsJsonAsync("/api/discord/activity/viewer-grants/redeem", new { grant = grant.GetProperty("grant").GetString(), transport = "websocket" }));
        }
        var first = await Admit(); var firstId = first.GetProperty("admissionId").GetGuid();
        Assert.False(first.TryGetProperty("iceServers", out _)); Assert.False(first.TryGetProperty("signalingToken", out _));
        using var relay = factory.CreateClient(); relay.DefaultRequestHeaders.Authorization = new("Bearer", "media-test");
        var connectionId = Guid.NewGuid();
        await Json(await relay.PostAsJsonAsync("/api/internal/media-relay/redeem", new { grant = first.GetProperty("grant").GetString(), connectionId }));
        var second = await Admit(); var secondId = second.GetProperty("admissionId").GetGuid();
        Assert.Equal(first.GetProperty("participantId").GetGuid(), second.GetProperty("participantId").GetGuid());
        await relay.PostAsJsonAsync("/api/internal/media-relay/release", new { leaseId = firstId, connectionId });
        Assert.Equal(HttpStatusCode.NoContent, (await viewer.PostAsync($"/api/discord/activity/media-admissions/{firstId}/release", null)).StatusCode);
        using (var scope = factory.Services.CreateScope()) {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            Assert.Equal(ParticipantStatuses.Connected, (await db.SessionParticipants.SingleAsync(x => x.Id == second.GetProperty("participantId").GetGuid())).Status);
            Assert.Equal(1, await db.SessionParticipants.CountAsync(x => x.Role == ParticipantRoles.Viewer && x.Status == ParticipantStatuses.Connected));
            db.LaunchCapabilities.Remove(await db.LaunchCapabilities.SingleAsync(x => x.Id == firstId));
            await db.SaveChangesAsync();
        }
        Assert.Equal(HttpStatusCode.NoContent, (await viewer.PostAsync($"/api/discord/activity/media-admissions/{firstId}/release", null)).StatusCode);
        var secondConnection = Guid.NewGuid(); await Json(await relay.PostAsJsonAsync("/api/internal/media-relay/redeem", new { grant = second.GetProperty("grant").GetString(), connectionId = secondConnection }));
        discord.Present = false;
        Assert.Equal(HttpStatusCode.Unauthorized, (await relay.PostAsJsonAsync("/api/internal/media-relay/renew", new { leaseId = secondId, connectionId = secondConnection })).StatusCode);
        using var finalScope = factory.Services.CreateScope();
        Assert.Equal(ParticipantStatuses.Disconnected, (await finalScope.ServiceProvider.GetRequiredService<AppDbContext>().SessionParticipants.SingleAsync(x => x.Id == second.GetProperty("participantId").GetGuid())).Status);
    }
    private static async Task<JsonElement> Json(HttpResponseMessage response) { response.EnsureSuccessStatusCode(); return await response.Content.ReadFromJsonAsync<JsonElement>(); }
    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task Upload_is_opt_in_single_use_and_never_owns_rtc_participant(bool enabled)
    {
        await using var factory = new SonicRelayApiFactory(new Dictionary<string, string?> {
            ["FeatureManagement:DiscordWebSocketMedia"] = enabled.ToString(),
            ["MediaRelay:ServiceToken"] = "media-test", ["MediaRelay:PublicBaseUrl"] = "wss://media.example/ws/media" });
        using var host = factory.CreateClient();
        await DeviceIdentityTestHelper.BootstrapAndAuthorizeAsync(host, "windows_desktop", "windows");
        var session = await (await host.PostAsJsonAsync("/api/sessions", new { mode = "screen_share" })).Content.ReadFromJsonAsync<JsonElement>();
        var id = session.GetProperty("id").GetGuid();
        var issued = await host.PostAsJsonAsync($"/api/sessions/{id}/media-grants", new { });
        if (!enabled) { Assert.Equal(HttpStatusCode.NotFound, issued.StatusCode); return; }
        Assert.Equal(HttpStatusCode.OK, issued.StatusCode);
        var admission = await issued.Content.ReadFromJsonAsync<JsonElement>();
        using var relay = factory.CreateClient(); relay.DefaultRequestHeaders.Authorization = new("Bearer", "media-test");
        var connectionId = Guid.NewGuid(); var grant = admission.GetProperty("grant").GetString();
        var redeemed = await relay.PostAsJsonAsync("/api/internal/media-relay/redeem", new { grant, connectionId });
        Assert.Equal(HttpStatusCode.OK, redeemed.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await relay.PostAsJsonAsync("/api/internal/media-relay/redeem", new { grant, connectionId })).StatusCode);
        var lease = await redeemed.Content.ReadFromJsonAsync<JsonElement>();
        var leaseId = lease.GetProperty("leaseId").GetGuid();
        Assert.Equal("upload", lease.GetProperty("role").GetString());
        Assert.Equal(HttpStatusCode.Unauthorized, (await relay.PostAsJsonAsync("/api/internal/media-relay/renew", new { leaseId, connectionId = Guid.NewGuid() })).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await relay.PostAsJsonAsync("/api/internal/media-relay/renew", new { leaseId, connectionId })).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await relay.PostAsJsonAsync("/api/internal/media-relay/release", new { leaseId, connectionId })).StatusCode);
        using var scope = factory.Services.CreateScope(); var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        Assert.Null((await db.LaunchCapabilities.SingleAsync(x => x.Id == leaseId)).ParticipantId);
        Assert.Equal(ParticipantStatuses.Connected, (await db.SessionParticipants.SingleAsync(x => x.SessionId == id)).Status);
    }
}
