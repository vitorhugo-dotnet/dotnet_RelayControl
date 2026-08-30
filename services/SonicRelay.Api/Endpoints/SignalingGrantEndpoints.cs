using System.Security.Claims;
using Microsoft.EntityFrameworkCore;
using SonicRelay.Api.Services;
using SonicRelay.Domain.Sessions;
using SonicRelay.Infrastructure.Persistence;

namespace SonicRelay.Api.Endpoints;

public static class SignalingGrantEndpoints
{
    public static IEndpointRouteBuilder MapSignalingGrantEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapPost("/api/signaling/grant", IssueAsync)
            .RequireAuthorization("signaling:connect");
        return app;
    }

    private static async Task<IResult> IssueAsync(SignalingGrantRequest request, ClaimsPrincipal principal,
        AppDbContext db, SignalingGrantService grants, HttpContext context, CancellationToken ct)
    {
        var device = await DeviceIdentityEndpoints.RequireDeviceAsync(principal, db, ct);
        if (device is null) return Results.Unauthorized();

        var session = await db.StreamSessions.AsNoTracking()
            .SingleOrDefaultAsync(x => x.Id == request.SessionId, ct);
        if (session is null) return Results.NotFound();
        if (session.Status is SessionStatuses.Ended or SessionStatuses.Expired) return Results.StatusCode(StatusCodes.Status410Gone);
        if (session.Status != SessionStatuses.Active) return Results.NotFound();

        var participant = await db.SessionParticipants.AsNoTracking()
            .Where(x => x.SessionId == request.SessionId && x.DeviceId == device.Id && x.Role == ParticipantRoles.Viewer)
            .OrderBy(x => x.JoinedAt)
            .FirstOrDefaultAsync(ct);
        if (participant is null || participant.Status != ParticipantStatuses.Connected || !participant.CanReceiveAudio)
            return Results.Forbid();

        var grant = grants.Issue(device.Id, session.Id, participant.Id);
        context.Response.Cookies.Append("sonicrelay_signaling", grant.Token, new CookieOptions
        {
            HttpOnly = true,
            Secure = true,
            SameSite = SameSiteMode.Strict,
            Path = "/ws/signaling",
            MaxAge = TimeSpan.FromSeconds(DeviceIdentityOptions.SignalingGrantLifetimeSeconds)
        });
        return Results.NoContent();
    }

    private sealed record SignalingGrantRequest(Guid SessionId);
}
