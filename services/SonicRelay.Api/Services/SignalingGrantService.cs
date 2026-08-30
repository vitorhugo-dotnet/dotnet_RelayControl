using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;

namespace SonicRelay.Api.Services;

public sealed record SignalingGrant(string Token, DateTimeOffset ExpiresAt);

public sealed class SignalingGrantService
{
    private readonly DeviceIdentityOptions _settings;
    private readonly TimeProvider _time;
    private readonly SigningCredentials _credentials;

    public SignalingGrantService(IOptions<DeviceIdentityOptions> options, TimeProvider time)
    {
        _settings = options.Value;
        _time = time;
        var key = DeviceCredentialService.RequireKey(
            _settings.TokenSigningKey, nameof(DeviceIdentityOptions.TokenSigningKey));
        var signingKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(key));
        _credentials = new SigningCredentials(signingKey, SecurityAlgorithms.HmacSha256);
        ValidationParameters = new TokenValidationParameters
        {
            ValidIssuer = _settings.Issuer,
            ValidAudience = $"{_settings.Audience}:signaling",
            IssuerSigningKey = signingKey,
            ValidateIssuer = true,
            ValidateAudience = true,
            ValidateLifetime = true,
            ValidateIssuerSigningKey = true,
            ClockSkew = TimeSpan.Zero,
            LifetimeValidator = (notBefore, expires, _, _) =>
            {
                var now = _time.GetUtcNow().UtcDateTime;
                return expires is not null && expires > now && (notBefore is null || notBefore <= now);
            }
        };
    }

    public TokenValidationParameters ValidationParameters { get; }

    public SignalingGrant Issue(Guid deviceId, Guid sessionId, Guid participantId)
    {
        var now = _time.GetUtcNow();
        var expiresAt = now.AddSeconds(DeviceIdentityOptions.SignalingGrantLifetimeSeconds);
        var claims = new[]
        {
            new Claim(JwtRegisteredClaimNames.Sub, deviceId.ToString()),
            new Claim("session_id", sessionId.ToString()),
            new Claim("participant_id", participantId.ToString()),
            new Claim("purpose", "signaling"),
            new Claim(JwtRegisteredClaimNames.Jti, Guid.NewGuid().ToString())
        };
        var token = new JwtSecurityToken(
            _settings.Issuer,
            $"{_settings.Audience}:signaling",
            claims,
            now.UtcDateTime,
            expiresAt.UtcDateTime,
            _credentials);

        return new SignalingGrant(new JwtSecurityTokenHandler().WriteToken(token), expiresAt);
    }
}
