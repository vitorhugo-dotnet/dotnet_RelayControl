namespace SonicRelay.Api.Contracts;

public sealed record CreateShareLaunchIntentRequest(
    string Provider, string GuildId, string ChannelId, string RequestedByUserId, int? TtlSeconds = null);

public sealed record CreateShareLaunchIntentResponse(Guid Id, string LaunchUrl, DateTimeOffset ExpiresAt);
public sealed record ConsumeShareLaunchIntentRequest(string Token);
public sealed record ConsumeShareLaunchIntentResponse(Guid Id, DateTimeOffset ExpiresAt);
public sealed record CompleteShareLaunchIntentRequest(Guid SessionId);
public sealed record ShareLaunchIntentStatusResponse(
    Guid Id, string Status, Guid? SessionId, string? WatchLaunchUrl, DateTimeOffset ExpiresAt);
public sealed record PendingShareLaunchIntentResponse(
    Guid Id, string GuildId, string ChannelId, string RequestedByUserId, string Status, DateTimeOffset ExpiresAt);
public sealed record CreateWatchLaunchRequest(string Code, int? TtlSeconds = null);
public sealed record CreateWatchLaunchResponse(string LaunchUrl, DateTimeOffset ExpiresAt);
public sealed record ResolveWatchLaunchRequest(string Token);
public sealed record ResolveWatchLaunchResponse(Guid SessionId);
public sealed record MarkSharePublishedRequest(string? MessageId);
