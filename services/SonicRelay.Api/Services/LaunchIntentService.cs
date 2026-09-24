using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using SonicRelay.Application.Abstractions;
using SonicRelay.Domain.LaunchIntents;
using SonicRelay.Domain.Sessions;
using SonicRelay.Infrastructure.Persistence;

namespace SonicRelay.Api.Services;

public sealed class LaunchIntentService(
    AppDbContext db,
    ISessionCodeStore sessionCodeStore,
    IConfiguration configuration,
    IOptions<LaunchIntentOptions> options,
    TimeProvider timeProvider)
{
    private readonly LaunchIntentOptions _options = options.Value;

    public async Task<(ShareLaunchIntent Intent, string LaunchUrl)> CreateShareAsync(
        string provider, string guildId, string channelId, string requestedByUserId, int? ttlSeconds,
        CancellationToken ct)
    {
        if (!string.Equals(provider, "discord", StringComparison.OrdinalIgnoreCase)
            || string.IsNullOrWhiteSpace(guildId) || string.IsNullOrWhiteSpace(channelId)
            || string.IsNullOrWhiteSpace(requestedByUserId)
            || guildId.Length > 64 || channelId.Length > 64 || requestedByUserId.Length > 64)
            throw new LaunchIntentException("invalid_request");

        var ttl = ttlSeconds ?? _options.ShareTtlSeconds;
        if (ttl is < 30 or > 3600) throw new LaunchIntentException("invalid_ttl");
        var token = NewToken();
        var now = timeProvider.GetUtcNow();
        var intent = new ShareLaunchIntent
        {
            Id = Guid.NewGuid(),
            TokenHash = HashToken(token),
            Provider = "discord",
            GuildId = guildId,
            ChannelId = channelId,
            RequestedByUserId = requestedByUserId,
            CreatedAt = now,
            ExpiresAt = now.AddSeconds(ttl)
        };
        db.ShareLaunchIntents.Add(intent);
        await db.SaveChangesAsync(ct);
        return (intent, PublicUrl($"/open/share/{token}"));
    }

    public async Task<ShareLaunchIntent> ConsumeShareAsync(string token, Guid deviceId, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(token) || token.Length > 128)
            throw new LaunchIntentException("invalid_or_expired_intent");
        var intent = await db.ShareLaunchIntents.SingleOrDefaultAsync(x => x.TokenHash == HashToken(token), ct);
        var now = timeProvider.GetUtcNow();
        if (intent is null || intent.Status != ShareLaunchIntentStatuses.Pending || intent.ExpiresAt <= now)
            throw new LaunchIntentException("invalid_or_expired_intent");

        intent.Status = ShareLaunchIntentStatuses.Consumed;
        intent.ConsumedByDeviceId = deviceId;
        intent.ConsumedAt = now;
        intent.Version++;
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateConcurrencyException)
        {
            throw new LaunchIntentException("invalid_or_expired_intent");
        }
        return intent;
    }

    public async Task CompleteShareAsync(Guid intentId, Guid deviceId, Guid sessionId, CancellationToken ct)
    {
        var intent = await db.ShareLaunchIntents.SingleOrDefaultAsync(x => x.Id == intentId, ct);
        var session = await db.StreamSessions.SingleOrDefaultAsync(x => x.Id == sessionId, ct);
        var now = timeProvider.GetUtcNow();
        if (intent?.Status == ShareLaunchIntentStatuses.Ready && intent.SessionId == sessionId
            && intent.ConsumedByDeviceId == deviceId && session?.SourceDeviceId == deviceId
            && session.Mode == SessionModes.ScreenShare
            && session.Status is not (SessionStatuses.Ended or SessionStatuses.Expired))
        {
            return;
        }
        if (intent is null || session is null || intent.Status != ShareLaunchIntentStatuses.Consumed
            || intent.ExpiresAt <= now || intent.ConsumedByDeviceId != deviceId || session.SourceDeviceId != deviceId
            || session.Mode != SessionModes.ScreenShare || session.Status is SessionStatuses.Ended or SessionStatuses.Expired)
            throw new LaunchIntentException("intent_session_mismatch");

        intent.Status = ShareLaunchIntentStatuses.Ready;
        intent.SessionId = sessionId;
        intent.CompletedAt = now;
        intent.Version++;
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateConcurrencyException)
        {
            throw new LaunchIntentException("intent_session_mismatch");
        }
    }

    public async Task<IReadOnlyList<ShareLaunchIntent>> ListRecoverableAsync(CancellationToken ct) =>
        await db.ShareLaunchIntents.AsNoTracking()
            .Where(x => x.PublishedAt == null && (x.Status == ShareLaunchIntentStatuses.Ready
                || ((x.Status == ShareLaunchIntentStatuses.Pending || x.Status == ShareLaunchIntentStatuses.Consumed)
                    && x.ExpiresAt > timeProvider.GetUtcNow())))
            .OrderBy(x => x.CreatedAt)
            .Take(100)
            .ToListAsync(ct);

    public async Task<(ShareLaunchIntent Intent, string? WatchLaunchUrl, DateTimeOffset? WatchExpiresAt)> GetStatusAsync(
        Guid intentId, int? watchTtlSeconds, CancellationToken ct)
    {
        var intent = await db.ShareLaunchIntents.SingleOrDefaultAsync(x => x.Id == intentId, ct)
            ?? throw new LaunchIntentException("intent_not_found");
        if (intent.Status is ShareLaunchIntentStatuses.Pending or ShareLaunchIntentStatuses.Consumed
            && intent.ExpiresAt <= timeProvider.GetUtcNow())
        {
            intent.Status = ShareLaunchIntentStatuses.Expired;
            intent.Version++;
            await db.SaveChangesAsync(ct);
        }
        if (intent.Status != ShareLaunchIntentStatuses.Ready || intent.SessionId is not { } sessionId)
            return (intent, null, null);
        var capability = await CreateWatchCapabilityForSessionAsync(sessionId, watchTtlSeconds, ct);
        return (intent,
            capability is null ? null : PublicUrl($"/open/watch/{capability.Value.Token}"),
            capability?.ExpiresAt);
    }

    public async Task MarkPublishedAsync(Guid intentId, string? messageId, CancellationToken ct)
    {
        var intent = await db.ShareLaunchIntents.SingleOrDefaultAsync(x => x.Id == intentId, ct)
            ?? throw new LaunchIntentException("intent_not_found");
        if (intent.Status != ShareLaunchIntentStatuses.Ready) throw new LaunchIntentException("intent_not_ready");
        intent.PublishedAt = timeProvider.GetUtcNow();
        intent.PublishedMessageId = messageId;
        intent.Version++;
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateConcurrencyException)
        {
            // A retry after a successful acknowledgement is idempotent.
            db.ChangeTracker.Clear();
            var current = await db.ShareLaunchIntents.AsNoTracking().SingleAsync(x => x.Id == intentId, ct);
            if (current.PublishedAt is null) throw;
        }
    }

    public async Task<(string Token, DateTimeOffset ExpiresAt)> CreateWatchCapabilityAsync(
        string? code, int? ttlSeconds, CancellationToken ct)
    {
        var normalized = (code ?? string.Empty).Trim().ToUpperInvariant();
        if (normalized.Length != 6 || normalized.Any(c => !char.IsAsciiLetterOrDigit(c)))
            throw new LaunchIntentException("invalid_code");
        var sessionId = await sessionCodeStore.RedeemAsync(HashSessionCode(normalized), ct);
        if (sessionId is null) throw new LaunchIntentException("invalid_code");
        var token = await CreateWatchCapabilityForSessionAsync(sessionId.Value, ttlSeconds, ct);
        if (token is null) throw new LaunchIntentException("session_unavailable");
        return token.Value;
    }

    public async Task<Guid> ResolveWatchCapabilityAsync(string? token, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(token) || token.Length > 128)
            throw new LaunchIntentException("invalid_or_expired_watch_link");
        var hash = HashToken(token);
        var capability = await db.WatchLaunchCapabilities.AsNoTracking()
            .SingleOrDefaultAsync(x => x.TokenHash == hash, ct);
        var now = timeProvider.GetUtcNow();
        if (capability is null || capability.ExpiresAt <= now)
            throw new LaunchIntentException("invalid_or_expired_watch_link");
        var session = await db.StreamSessions.AsNoTracking().SingleOrDefaultAsync(x => x.Id == capability.SessionId, ct);
        if (session is null || session.Status is SessionStatuses.Ended or SessionStatuses.Expired)
            throw new LaunchIntentException("session_unavailable");
        return session.Id;
    }

    private async Task<(string Token, DateTimeOffset ExpiresAt)?> CreateWatchCapabilityForSessionAsync(
        Guid sessionId, int? ttlSeconds, CancellationToken ct)
    {
        var session = await db.StreamSessions.AsNoTracking().SingleOrDefaultAsync(x => x.Id == sessionId, ct);
        if (session is null || session.Status is not (SessionStatuses.Waiting or SessionStatuses.Active)) return null;
        var viewers = await db.SessionParticipants.CountAsync(x => x.SessionId == sessionId
            && x.Role == ParticipantRoles.Viewer && x.Status == ParticipantStatuses.Connected, ct);
        if (viewers >= session.MaxViewers) throw new LaunchIntentException("session_full");
        var token = NewToken();
        var now = timeProvider.GetUtcNow();
        var ttl = Math.Clamp(ttlSeconds ?? _options.WatchTtlSeconds, 60, 86_400);
        var capability = new WatchLaunchCapability
        {
            Id = Guid.NewGuid(),
            TokenHash = HashToken(token),
            SessionId = session.Id,
            CreatedAt = now,
            ExpiresAt = now.AddSeconds(ttl)
        };
        db.WatchLaunchCapabilities.Add(capability);
        await db.SaveChangesAsync(ct);
        return (token, capability.ExpiresAt);
    }

    private string HashSessionCode(string code)
    {
        var key = configuration["Sessions:CodeHmacKey"];
        if (string.IsNullOrWhiteSpace(key)) throw new InvalidOperationException("Sessions:CodeHmacKey must be configured.");
        return Convert.ToHexString(HMACSHA256.HashData(Encoding.UTF8.GetBytes(key), Encoding.ASCII.GetBytes(code)));
    }

    private string PublicUrl(string path) => _options.PublicBaseUrl.TrimEnd('/') + path;
    private static string NewToken() => Convert.ToBase64String(RandomNumberGenerator.GetBytes(32))
        .TrimEnd('=').Replace('+', '-').Replace('/', '_');
    private static string HashToken(string token) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token)));
}

public sealed class LaunchIntentException(string code) : Exception(code)
{
    public string Code { get; } = code;
}
