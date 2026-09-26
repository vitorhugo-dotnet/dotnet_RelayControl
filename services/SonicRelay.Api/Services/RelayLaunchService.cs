using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using SonicRelay.Domain.Sessions;
using SonicRelay.Infrastructure.Persistence;

namespace SonicRelay.Api.Services;

public sealed class RelayLaunchOptions
{
    public string? ServiceToken { get; set; }
    public string PublicBaseUrl { get; set; } = "";
    public string DownloadUrl { get; set; } = "https://github.com";
    public string? DiscordClientId { get; set; }
    public string? DiscordClientSecret { get; set; }
    public string? DiscordBotToken { get; set; }
    public string? DiscordRedirectUri { get; set; }
}

public sealed record DiscordActivityIdentity(string UserId, string InstanceId, string GuildId, string ChannelId);
public interface IDiscordActivityValidator
{
    Task<DiscordActivityIdentity?> AuthorizeAsync(string code, string instanceId, CancellationToken ct);
    Task<bool> IsPresentAsync(DiscordActivityIdentity identity, CancellationToken ct);
}

public sealed class DiscordActivityValidator(HttpClient http, Microsoft.Extensions.Options.IOptions<RelayLaunchOptions> options, DiscordActivityInstanceCache instances)
    : IDiscordActivityValidator
{
    public async Task<DiscordActivityIdentity?> AuthorizeAsync(string code, string instanceId, CancellationToken ct)
    {
        var settings = options.Value;
        if (string.IsNullOrWhiteSpace(settings.DiscordClientId) || string.IsNullOrWhiteSpace(settings.DiscordClientSecret)
            || string.IsNullOrWhiteSpace(settings.DiscordBotToken) || code is not { Length: > 0 and <= 2048 } || instanceId is not { Length: > 0 and <= 256 })
            return null;
        var form = new Dictionary<string, string>
        {
            ["client_id"] = settings.DiscordClientId, ["client_secret"] = settings.DiscordClientSecret,
            ["grant_type"] = "authorization_code", ["code"] = code
        };
        if (!string.IsNullOrEmpty(settings.DiscordRedirectUri)) form["redirect_uri"] = settings.DiscordRedirectUri;
        using var response = await http.PostAsync("https://discord.com/api/oauth2/token", new FormUrlEncodedContent(form), ct);
        if (!response.IsSuccessStatusCode) return null;
        using var token = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
        if (!token.RootElement.TryGetProperty("access_token", out var access)) return null;
        using var request = new HttpRequestMessage(HttpMethod.Get, "https://discord.com/api/v10/users/@me");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", access.GetString());
        using var userResponse = await http.SendAsync(request, ct);
        if (!userResponse.IsSuccessStatusCode) return null;
        using var user = JsonDocument.Parse(await userResponse.Content.ReadAsStringAsync(ct));
        var userId = user.RootElement.GetProperty("id").GetString()!;
        return await GetInstanceAsync(userId, instanceId, ct);
    }

    public async Task<bool> IsPresentAsync(DiscordActivityIdentity identity, CancellationToken ct) =>
        await GetInstanceAsync(identity.UserId, identity.InstanceId, ct) == identity;

    private async Task<DiscordActivityIdentity?> GetInstanceAsync(string userId, string instanceId, CancellationToken ct)
    {
        var snapshot = await instances.GetAsync(instanceId, ct);
        return snapshot is not null && snapshot.Users.Contains(userId)
            ? new(userId, instanceId, snapshot.GuildId, snapshot.ChannelId) : null;
    }
}

/// <summary>One coalesced REST request per instance per five seconds, including negative responses.</summary>
public sealed class DiscordActivityInstanceCache(IHttpClientFactory clients,
    Microsoft.Extensions.Options.IOptions<RelayLaunchOptions> options, TimeProvider time)
{
    public sealed record Snapshot(string GuildId, string ChannelId, HashSet<string> Users);
    private sealed class Entry
    {
        public readonly SemaphoreSlim Gate = new(1);
        public Snapshot? Value;
        public DateTimeOffset Until;
    }
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, Entry> _entries = new();
    public async Task<Snapshot?> GetAsync(string instanceId, CancellationToken ct)
    {
        if (instanceId is not { Length: > 0 and <= 256 }) return null;
        foreach (var old in _entries.Where(x => x.Value.Until != default && x.Value.Gate.CurrentCount == 1 && x.Value.Until < time.GetUtcNow().AddMinutes(-1)))
            _entries.TryRemove(old);
        var entry = _entries.GetOrAdd(instanceId, _ => new Entry());
        await entry.Gate.WaitAsync(ct);
        try
        {
            if (entry.Until > time.GetUtcNow()) return entry.Value;
            entry.Value = null;
            try
            {
                var settings = options.Value;
                if (!string.IsNullOrWhiteSpace(settings.DiscordClientId) && !string.IsNullOrWhiteSpace(settings.DiscordBotToken))
                {
                    using var request = new HttpRequestMessage(HttpMethod.Get,
                        $"https://discord.com/api/v10/applications/{Uri.EscapeDataString(settings.DiscordClientId)}/activity-instances/{Uri.EscapeDataString(instanceId)}");
                    request.Headers.Authorization = new AuthenticationHeaderValue("Bot", settings.DiscordBotToken);
                    using var response = await clients.CreateClient("DiscordActivityInstances").SendAsync(request, ct);
                    if (response.IsSuccessStatusCode)
                    {
                        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
                        var root = json.RootElement;
                        var location = root.GetProperty("location");
                        if (root.GetProperty("application_id").GetString() == settings.DiscordClientId && root.GetProperty("instance_id").GetString() == instanceId && location.GetProperty("kind").GetString() == "gc")
                            entry.Value = new(location.GetProperty("guild_id").GetString()!, location.GetProperty("channel_id").GetString()!,
                                root.GetProperty("users").EnumerateArray().Select(x => x.GetString()!).ToHashSet());
                    }
                }
            }
            catch (Exception ex) when (ex is HttpRequestException or JsonException or KeyNotFoundException or InvalidOperationException or TaskCanceledException) { entry.Value = null; }
            entry.Until = time.GetUtcNow().AddSeconds(5);
            return entry.Value;
        }
        finally { entry.Gate.Release(); }
    }
}

public static class RelayCapability
{
    public static string NewToken() => Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant();
    public static string Hash(string token) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token)));
    public static LaunchCapability Create(string kind, string token, DateTimeOffset now, int ttl) =>
        new() { Kind = kind, TokenHash = Hash(token), CreatedAt = now, ExpiresAt = now.AddSeconds(Math.Clamp(ttl, 30, 900)) };
    public static async Task<LaunchCapability?> FindAsync(AppDbContext db, string token, string kind, DateTimeOffset now, CancellationToken ct) =>
        token is { Length: 64 } ? await db.LaunchCapabilities.SingleOrDefaultAsync(x => x.TokenHash == Hash(token)
            && x.Kind == kind && x.ExpiresAt > now && x.ConsumedAt == null, ct) : null;
}
