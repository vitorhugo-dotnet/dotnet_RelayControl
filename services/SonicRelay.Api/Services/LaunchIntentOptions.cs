namespace SonicRelay.Api.Services;

public sealed class LaunchIntentOptions
{
    public const string SectionName = "LaunchIntents";
    public string? ServiceToken { get; set; }
    public string PublicBaseUrl { get; set; } = "https://framerelay.hugojava.dev";
    public int ShareTtlSeconds { get; set; } = 600;
    public int WatchTtlSeconds { get; set; } = 1800;
}
