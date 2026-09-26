using System.Security.Claims;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using SonicRelay.Api.Services;
using SonicRelay.Domain.Sessions;
using SonicRelay.Infrastructure.Persistence;

namespace SonicRelay.Api.Authorization;

public sealed class ActivityAuthenticationHandler(IOptionsMonitor<AuthenticationSchemeOptions> options,
    ILoggerFactory logger, UrlEncoder encoder, AppDbContext db, TimeProvider time, IDiscordActivityValidator discord)
    : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    protected override async Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        if (Request.Path != "/ws/signaling") return AuthenticateResult.NoResult();
        var protocol = Request.Headers.SecWebSocketProtocol.ToString().Split(',').Select(x => x.Trim()).FirstOrDefault(x => x.StartsWith("token."));
        if (protocol is null) return AuthenticateResult.NoResult();
        if (!Request.Headers.SecWebSocketProtocol.ToString().Split(',').Any(x => x.Trim() == "framerelay")) return AuthenticateResult.Fail("Missing Activity protocol.");
        var cap = await RelayCapability.FindAsync(db, protocol[6..], "signaling", time.GetUtcNow(), Context.RequestAborted);
        if (cap?.SessionId is not { } sessionId || cap.DeviceId is null || Request.Query["sessionId"] != sessionId.ToString()) return AuthenticateResult.Fail("Invalid Activity credential.");
        if (!await db.StreamSessions.AnyAsync(x => x.Id == sessionId && x.Status != SessionStatuses.Ended && x.Status != SessionStatuses.Expired, Context.RequestAborted)) return AuthenticateResult.Fail("Session ended.");
        if (!await ActivityPresenceService.IsAuthorizedAsync(db, discord, cap, time.GetUtcNow(), Context.RequestAborted)) return AuthenticateResult.Fail("Activity membership revoked.");
        cap.ConsumedAt = time.GetUtcNow();
        try { await db.SaveChangesAsync(Context.RequestAborted); } catch (DbUpdateConcurrencyException) { return AuthenticateResult.Fail("Credential consumed."); }
        var claims = new[] { new Claim("sub", cap.DeviceId.Value.ToString()), new Claim("activity_session", sessionId.ToString()), new Claim("activity_expiry", cap.ExpiresAt.ToUnixTimeSeconds().ToString()), new Claim("activity_capability", cap.Id.ToString()) };
        return AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(new ClaimsIdentity(claims, Scheme.Name)), Scheme.Name));
    }
}
