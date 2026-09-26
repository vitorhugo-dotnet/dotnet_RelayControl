using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;
using SonicRelay.Api.Services;

namespace SonicRelay.Api.Authorization;

public sealed class SignalingGrantAuthenticationHandler(
    IOptionsMonitor<AuthenticationSchemeOptions> options,
    ILoggerFactory logger,
    UrlEncoder encoder,
    SignalingGrantService grants)
    : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    public const string SchemeName = "SignalingGrant";
    public const string CookieName = "sonicrelay_signaling";

    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        // The grant is scoped to the WebSocket route. Cookie Path is enforced by browsers,
        // but the server repeats that boundary for non-browser clients that construct headers.
        if (Request.Path != "/ws/signaling"
            || !Request.Cookies.TryGetValue(CookieName, out var token)
            || string.IsNullOrWhiteSpace(token))
        {
            return Task.FromResult(AuthenticateResult.NoResult());
        }

        try
        {
            var tokenHandler = new JwtSecurityTokenHandler { MapInboundClaims = false };
            var validated = tokenHandler.ValidateToken(token, grants.ValidationParameters, out _);
            var identity = new ClaimsIdentity(validated.Claims, SchemeName);
            return Task.FromResult(AuthenticateResult.Success(
                new AuthenticationTicket(new ClaimsPrincipal(identity), SchemeName)));
        }
        catch (Exception exception) when (exception is SecurityTokenException or ArgumentException)
        {
            // Deliberately exclude the cookie and exception detail: token-validation exceptions
            // can contain attacker-controlled token material.
            return Task.FromResult(AuthenticateResult.Fail("Invalid signaling grant."));
        }
    }
}

/// <summary>
/// Provides the grant-side alternative for the signaling scope requirement. Authorization
/// requirements succeed when any registered handler succeeds: DeviceScopeAuthorizationHandler
/// retains the live DeviceBearer check, while this handler accepts only the dedicated grant
/// scheme with its fixed purpose claim.
/// </summary>
public sealed class SignalingGrantAuthorizationHandler : AuthorizationHandler<DeviceScopeRequirement>
{
    protected override Task HandleRequirementAsync(
        AuthorizationHandlerContext context, DeviceScopeRequirement requirement)
    {
        if (requirement.Scope != "signaling:connect") return Task.CompletedTask;

        var grantIdentity = context.User.Identities.FirstOrDefault(identity =>
            identity.IsAuthenticated
            && identity.AuthenticationType == SignalingGrantAuthenticationHandler.SchemeName);
        if (grantIdentity?.HasClaim("purpose", "signaling") == true)
        {
            context.Succeed(requirement);
        }

        return Task.CompletedTask;
    }
}
