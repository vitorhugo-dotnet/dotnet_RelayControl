namespace SonicRelay.Domain.LaunchIntents;

public sealed class ShareLaunchIntent
{
    public Guid Id { get; set; }
    public string TokenHash { get; set; } = string.Empty;
    public string Provider { get; set; } = string.Empty;
    public string GuildId { get; set; } = string.Empty;
    public string ChannelId { get; set; } = string.Empty;
    public string RequestedByUserId { get; set; } = string.Empty;
    public string Status { get; set; } = ShareLaunchIntentStatuses.Pending;
    public Guid? ConsumedByDeviceId { get; set; }
    public DateTimeOffset? ConsumedAt { get; set; }
    public Guid? SessionId { get; set; }
    public DateTimeOffset? CompletedAt { get; set; }
    public DateTimeOffset? PublishedAt { get; set; }
    public string? PublishedMessageId { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset ExpiresAt { get; set; }
    public int Version { get; set; }
}

public static class ShareLaunchIntentStatuses
{
    public const string Pending = "pending";
    public const string Consumed = "consumed";
    public const string Ready = "session_ready";
    public const string Failed = "failed";
    public const string Expired = "expired";
}
