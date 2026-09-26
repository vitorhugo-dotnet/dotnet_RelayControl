namespace SonicRelay.Domain.Sessions;

/// <summary>Only token hashes are stored. ConsumedAt is the optimistic concurrency guard.</summary>
public sealed class LaunchCapability
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Kind { get; set; } = "share";
    public string TokenHash { get; set; } = "";
    public string GuildId { get; set; } = "";
    public string ChannelId { get; set; } = "";
    public string UserId { get; set; } = "";
    public string? InstanceId { get; set; }
    public Guid? SessionId { get; set; }
    public Guid? DeviceId { get; set; }
    public Guid? ParticipantId { get; set; }
    public string? Code { get; set; }
    public string? MessageId { get; set; }
    public DateTimeOffset ExpiresAt { get; set; }
    public DateTimeOffset? ConsumedAt { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}
