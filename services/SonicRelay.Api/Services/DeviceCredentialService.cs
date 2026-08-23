using System.Globalization;
using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;
using SonicRelay.Domain.DeviceIdentities;
using SonicRelay.Domain.Devices;

namespace SonicRelay.Api.Services;

public sealed class DeviceCredentialService(IOptions<DeviceIdentityOptions> options, TimeProvider time)
{
    public (string PlaintextSecret, string SecretHash) GenerateCredential()
    {
        var plaintext = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
        return (plaintext, HashSecret(plaintext));
    }

    public string HashSecret(string plaintextSecret)
    {
        var key = RequireKey(options.Value.CredentialHmacKey, nameof(DeviceIdentityOptions.CredentialHmacKey));
        var hash = HMACSHA256.HashData(Encoding.UTF8.GetBytes(key), Encoding.UTF8.GetBytes(plaintextSecret));
        return Convert.ToHexString(hash);
    }

    public bool VerifySecret(string plaintextSecret, string secretHash)
    {
        var computed = Convert.FromHexString(HashSecret(plaintextSecret));
        var expected = Convert.FromHexString(secretHash);
        return CryptographicOperations.FixedTimeEquals(computed, expected);
    }

    public (string AccessToken, DateTimeOffset ExpiresAt) IssueAccessToken(DeviceIdentity device)
    {
        var settings = options.Value;
        var key = RequireKey(settings.TokenSigningKey, nameof(DeviceIdentityOptions.TokenSigningKey));
        var now = time.GetUtcNow();
        var expiresAt = now.AddMinutes(settings.AccessTokenMinutes);
        var scopes = string.Join(' ', ScopesFor(device.DeviceType));

        var claims = new[]
        {
            new Claim(JwtRegisteredClaimNames.Sub, device.Id.ToString()),
            new Claim(JwtRegisteredClaimNames.Jti, Guid.NewGuid().ToString()),
            new Claim("device_type", device.DeviceType),
            new Claim("scope", scopes),
            new Claim("cv", device.CredentialVersion.ToString(CultureInfo.InvariantCulture))
        };
        var credentials = new SigningCredentials(
            new SymmetricSecurityKey(Encoding.UTF8.GetBytes(key)), SecurityAlgorithms.HmacSha256);
        var token = new JwtSecurityToken(
            settings.Issuer, settings.Audience, claims, now.UtcDateTime, expiresAt.UtcDateTime, credentials);

        return (new JwtSecurityTokenHandler().WriteToken(token), expiresAt);
    }

    public static IReadOnlyList<string> ScopesFor(string deviceType) => deviceType switch
    {
        DeviceTypes.WindowsPublisher =>
        [
            "device:read", "device:manage", "pairing:create", "pairing:revoke",
            "session:create", "session:end", "signaling:connect", "turn:credentials"
        ],
        // The desktop app is publisher and viewer at once: it shares its own screen and views
        // another machine's. Rather than widening windows_publisher — which would hand new
        // privileges to an app that never asked for them — this type carries the union.
        DeviceTypes.WindowsDesktop =>
        [
            "device:read", "device:manage",
            "pairing:create", "pairing:complete", "pairing:revoke",
            "session:create", "session:join", "session:end",
            "signaling:connect", "turn:credentials"
        ],
        DeviceTypes.FlutterViewer =>
        [
            "device:read", "device:manage", "pairing:complete", "pairing:revoke",
            "session:join", "signaling:connect", "turn:credentials"
        ],
        _ => []
    };

    internal static string RequireKey(string? value, string name) =>
        string.IsNullOrWhiteSpace(value)
            ? throw new InvalidOperationException($"DeviceIdentity:{name} must be configured.")
            : value;
}
