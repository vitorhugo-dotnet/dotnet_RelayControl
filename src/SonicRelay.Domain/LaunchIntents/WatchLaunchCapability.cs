namespace SonicRelay.Domain.LaunchIntents;

public sealed class WatchLaunchCapability
{
    public Guid Id { get; set; }
    public string TokenHash { get; set; } = string.Empty;
    public Guid SessionId { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset ExpiresAt { get; set; }
}
