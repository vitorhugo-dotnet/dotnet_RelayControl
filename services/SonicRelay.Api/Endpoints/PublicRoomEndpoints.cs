using SonicRelay.Api.Features;
using System.Security.Claims;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using SonicRelay.Api.Contracts;
using SonicRelay.Api.Services;
using SonicRelay.Domain.DeviceIdentities;
using SonicRelay.Infrastructure.Persistence;

namespace SonicRelay.Api.Endpoints;

public static class PublicRoomEndpoints
{
    public static IEndpointRouteBuilder MapPublicRoomEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/public-room", GetAsync)
            .RequireAuthorization("DeviceAuthenticated")
            .WithTags("PublicRoom")
            .AddEndpointFilter(new FeatureEndpointFilter(RelayFeatures.PublicRooms));
        return app;
    }

    private static async Task<IResult> GetAsync(
        ClaimsPrincipal principal, AppDbContext db, IOptions<PublicRoomOptions> options,
        PublicRoomSeeder seeder, TimeProvider time, CancellationToken ct)
    {
        var settings = options.Value;
        if (!settings.Enabled)
            return Results.Ok(new PublicRoomResponse(Enabled: false, SessionId: null, MaxViewers: 0));

        var device = await DeviceIdentityEndpoints.RequireDeviceAsync(principal, db, ct);
        if (device is null) return Results.Unauthorized();

        var session = await seeder.EnsureSeededAsync(db, time, ct);

        var hasActivePairing = await db.DevicePairings.AsNoTracking().AnyAsync(x =>
            x.PublisherDeviceId == PublicRoomSeeder.VirtualPublisherDeviceId
            && x.ViewerDeviceId == device.Id
            && x.Status == DevicePairingStatuses.Active, ct);
        if (!hasActivePairing)
        {
            db.DevicePairings.Add(new DevicePairing
            {
                Id = Guid.NewGuid(),
                PublisherDeviceId = PublicRoomSeeder.VirtualPublisherDeviceId,
                ViewerDeviceId = device.Id,
                Status = DevicePairingStatuses.Active,
                CreatedAt = time.GetUtcNow(),
                LastUsedAt = time.GetUtcNow()
            });
            await db.SaveChangesAsync(ct);
        }

        return Results.Ok(new PublicRoomResponse(Enabled: true, SessionId: session.Id, settings.EffectiveMaxViewers));
    }
}
