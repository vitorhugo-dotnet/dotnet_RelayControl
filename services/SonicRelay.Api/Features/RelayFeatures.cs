using Microsoft.Extensions.Configuration.Memory;

namespace SonicRelay.Api.Features;

public static class RelayFeatures
{
    public const string PublicRooms = "PublicRooms";
    public const string DiscordActivity = "DiscordActivity";
    public const string DuplexAudio = "DuplexAudio";
    public const string ScreenShare = "ScreenShare";
    public const string DiscordWebSocketMedia = "DiscordWebSocketMedia";

    public static void AddDefaults(ConfigurationManager configuration)
    {
        configuration.Sources.Insert(0, new MemoryConfigurationSource
        {
            InitialData = new Dictionary<string, string?>
            {
                [$"FeatureManagement:{PublicRooms}"] = "true",
                [$"FeatureManagement:{DiscordActivity}"] = "true",
                [$"FeatureManagement:{DuplexAudio}"] = "true",
                [$"FeatureManagement:{ScreenShare}"] = "true",
                [$"FeatureManagement:{DiscordWebSocketMedia}"] = "false"
            }
        });
    }

    public static IResult Disabled(string feature) => Results.Conflict(new
    {
        error = "The requested feature is disabled.", code = "feature_disabled", feature
    });
}
