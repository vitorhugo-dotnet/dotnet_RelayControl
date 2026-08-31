namespace SonicRelay.Api.Services;

public sealed class DeviceIdentityOptions
{
    public const int SignalingGrantLifetimeSeconds = 60;

    public string? CredentialHmacKey { get; set; }
    public string? PairingCodeHmacKey { get; set; }
    public string? TokenSigningKey { get; set; }
    public string Issuer { get; set; } = "sonicrelay";
    public string Audience { get; set; } = "sonicrelay-devices";
    public int AccessTokenMinutes { get; set; } = 5;
    public int PairingCodeTtlMinutes { get; set; } = 5;
    public int PairingMaxAttempts { get; set; } = 5;
}
