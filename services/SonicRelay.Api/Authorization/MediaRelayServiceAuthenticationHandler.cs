using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Options;

namespace SonicRelay.Api.Authorization;

public sealed class MediaRelayServiceAuthenticationHandler(IOptionsMonitor<AuthenticationSchemeOptions> options,
    ILoggerFactory logger, UrlEncoder encoder, IConfiguration configuration)
    : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    public const string SchemeName = "MediaRelayService";
    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        var secret = configuration["MediaRelay:ServiceToken"];
        var header = Request.Headers.Authorization.ToString();
        if (string.IsNullOrWhiteSpace(secret) || !header.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
            return Task.FromResult(AuthenticateResult.NoResult());
        if (!CryptographicOperations.FixedTimeEquals(SHA256.HashData(Encoding.UTF8.GetBytes(secret)),
            SHA256.HashData(Encoding.UTF8.GetBytes(header[7..]))))
            return Task.FromResult(AuthenticateResult.Fail("Invalid media service credential."));
        return Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(
            new ClaimsIdentity(new[] { new Claim(ClaimTypes.NameIdentifier, "media-relay") }, SchemeName)), SchemeName)));
    }
}
