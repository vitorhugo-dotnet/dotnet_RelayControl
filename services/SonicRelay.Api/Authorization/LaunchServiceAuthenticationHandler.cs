using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Options;
using SonicRelay.Api.Services;

namespace SonicRelay.Api.Authorization;

public sealed class LaunchServiceAuthenticationHandler(
    IOptionsMonitor<AuthenticationSchemeOptions> options,
    ILoggerFactory logger,
    UrlEncoder encoder,
    IOptions<LaunchIntentOptions> launchOptions)
    : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    public const string SchemeName = "LaunchService";

    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        var configured = launchOptions.Value.ServiceToken;
        var header = Request.Headers.Authorization.ToString();
        if (string.IsNullOrWhiteSpace(configured) || !header.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
            return Task.FromResult(AuthenticateResult.NoResult());

        var suppliedHash = SHA256.HashData(Encoding.UTF8.GetBytes(header[7..].Trim()));
        var configuredHash = SHA256.HashData(Encoding.UTF8.GetBytes(configured));
        if (!CryptographicOperations.FixedTimeEquals(suppliedHash, configuredHash))
            return Task.FromResult(AuthenticateResult.Fail("Invalid service credential."));

        var claims = new[]
        {
            new Claim(ClaimTypes.NameIdentifier, "discord-bot"),
            new Claim("scope", "launch-intents")
        };
        var identity = new ClaimsIdentity(claims, SchemeName);
        return Task.FromResult(AuthenticateResult.Success(
            new AuthenticationTicket(new ClaimsPrincipal(identity), SchemeName)));
    }
}
