namespace SonicRelay.Api.Services;

public sealed class SignalingOriginOptions
{
    public const string SectionName = "Signaling";

    public string[] AllowedWebOrigins { get; set; } = [];
}
