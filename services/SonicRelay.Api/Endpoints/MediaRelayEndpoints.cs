using System.Security.Claims;
using SonicRelay.Api.Features;
using SonicRelay.Api.Services;

namespace SonicRelay.Api.Endpoints;

public static class MediaRelayEndpoints
{
    public sealed record RedeemRequest(string Grant, Guid ConnectionId);
    public sealed record LeaseRequest(Guid LeaseId, Guid ConnectionId);
    public static IEndpointRouteBuilder MapMediaRelayEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapPost("/api/sessions/{sessionId:guid}/media-grants", async (Guid sessionId, ClaimsPrincipal user,
            MediaRelayGrantService service, CancellationToken ct) =>
            await service.IssueUploadAsync(sessionId, user, ct) is { } result ? Results.Ok(result) : Results.NotFound())
            .RequireAuthorization("session:create").AddEndpointFilter(new FeatureEndpointFilter(RelayFeatures.DiscordWebSocketMedia));
        var group = app.MapGroup("/api/internal/media-relay").RequireAuthorization("media-relay:service");
        group.MapPost("/redeem", async (RedeemRequest request, MediaRelayGrantService service, CancellationToken ct) =>
            await service.RedeemAsync(request.Grant, request.ConnectionId, ct) is { } result ? Results.Ok(result) : Results.Unauthorized());
        group.MapPost("/renew", async (LeaseRequest request, MediaRelayGrantService service, CancellationToken ct) =>
            await service.RenewAsync(request.LeaseId, request.ConnectionId, ct) is { } result ? Results.Ok(result) : Results.Unauthorized());
        group.MapPost("/release", async (LeaseRequest request, MediaRelayGrantService service, CancellationToken ct) =>
        { await service.ReleaseAsync(request.LeaseId, request.ConnectionId, ct); return Results.NoContent(); });
        return app;
    }
}
