using System.Security.Claims;
using System.Text.Json;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;
using SonicRelay.Api.Contracts;
using SonicRelay.Api.Services;
using SonicRelay.Domain.DeviceIdentities;
using SonicRelay.Infrastructure.Persistence;

namespace SonicRelay.Api.Endpoints;

public static class PairingEndpoints
{
    public static IEndpointRouteBuilder MapPairingEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapPost("/api/pairings/challenges", CreateChallengeAsync)
            .RequireAuthorization("pairing:create")
            .RequireRateLimiting("pairing-create")
            .WithTags("Pairing");

        app.MapPost("/api/pairings/complete", CompleteAsync)
            .RequireAuthorization("pairing:complete")
            .RequireRateLimiting("pairing-complete")
            .WithTags("Pairing");

        app.MapGet("/api/devices/{deviceId:guid}/pairings", ListAsync)
            .RequireAuthorization("device:read")
            .WithTags("Pairing");

        app.MapDelete("/api/pairings/{pairingId:guid}", RevokeAsync)
            .RequireAuthorization("pairing:revoke")
            .WithTags("Pairing");

        return app;
    }

    // No device-type check here: the "pairing:create" policy is the gate, and both device
    // types that hold that scope (windows_publisher and windows_desktop) may legitimately
    // issue a pairing code.
    private static async Task<IResult> CreateChallengeAsync(ClaimsPrincipal principal,
        PairingChallengeService challenges, AppDbContext db, TimeProvider time, CancellationToken ct)
    {
        var publisher = await DeviceIdentityEndpoints.RequireDeviceAsync(principal, db, ct);
        if (publisher is null || publisher.Status != DeviceIdentityStatuses.Active) return Results.Unauthorized();

        var code = challenges.GenerateCode();
        var challenge = new PairingChallenge
        {
            Id = Guid.NewGuid(),
            PublisherDeviceId = publisher.Id,
            CodeHash = challenges.HashCode(code),
            ExpiresAt = challenges.NewExpiry(),
            MaxAttempts = challenges.MaxAttempts,
            CreatedAt = time.GetUtcNow()
        };
        db.PairingChallenges.Add(challenge);
        await db.SaveChangesAsync(ct);

        var qrPayload = JsonSerializer.Serialize(new { challengeId = challenge.Id, code });
        return Results.Created($"/api/pairings/{challenge.Id}",
            new CreateChallengeResponse(challenge.Id, code, qrPayload, challenge.ExpiresAt));
    }

    // No device-type check here either, mirroring CreateChallengeAsync: "pairing:complete" is
    // held by flutter_viewer and windows_desktop, and both may legitimately redeem a code.
    private static async Task<IResult> CompleteAsync(CompletePairingRequest request, ClaimsPrincipal principal,
        PairingChallengeService challenges, AppDbContext db, TimeProvider time, CancellationToken ct)
    {
        var viewer = await DeviceIdentityEndpoints.RequireDeviceAsync(principal, db, ct);
        if (viewer is null || viewer.Status != DeviceIdentityStatuses.Active) return Results.Unauthorized();

        var challenge = await db.PairingChallenges.SingleOrDefaultAsync(x => x.Id == request.ChallengeId, ct);
        if (!IsUsable(challenge, time.GetUtcNow()))
            return Results.BadRequest(new { error = "Invalid or expired pairing code." });

        if (challenges.HashCode(request.Code ?? string.Empty) != challenge!.CodeHash)
        {
            challenge.AttemptCount += 1;
            await db.SaveChangesAsync(ct);
            return Results.BadRequest(new { error = "Invalid or expired pairing code." });
        }

        challenge.ConsumedAt = time.GetUtcNow();
        var pairing = new DevicePairing
        {
            Id = Guid.NewGuid(),
            PublisherDeviceId = challenge.PublisherDeviceId,
            ViewerDeviceId = viewer.Id,
            Status = DevicePairingStatuses.Active,
            CreatedAt = time.GetUtcNow(),
            LastUsedAt = time.GetUtcNow()
        };
        db.DevicePairings.Add(pairing);
        await db.SaveChangesAsync(ct);

        var publisherName = await db.DeviceIdentities.AsNoTracking()
            .Where(x => x.Id == challenge.PublisherDeviceId)
            .Select(x => x.Name)
            .SingleOrDefaultAsync(ct) ?? string.Empty;
        return Results.Ok(ToResponse(pairing, publisherName, viewer.Name));
    }

    private static async Task<IResult> ListAsync(Guid deviceId, ClaimsPrincipal principal,
        AppDbContext db, CancellationToken ct)
    {
        var caller = await DeviceIdentityEndpoints.RequireDeviceAsync(principal, db, ct);
        if (caller is null || caller.Id != deviceId) return Results.Unauthorized();

        var pairings = await (
            from pairing in db.DevicePairings.AsNoTracking()
            where (pairing.PublisherDeviceId == deviceId || pairing.ViewerDeviceId == deviceId)
                && pairing.Status == DevicePairingStatuses.Active
            join publisherJoin in db.DeviceIdentities.AsNoTracking()
                on pairing.PublisherDeviceId equals publisherJoin.Id into publishers
            from publisher in publishers.DefaultIfEmpty()
            join viewerJoin in db.DeviceIdentities.AsNoTracking()
                on pairing.ViewerDeviceId equals viewerJoin.Id into viewers
            from viewer in viewers.DefaultIfEmpty()
            orderby pairing.CreatedAt descending
            select new
            {
                Pairing = pairing,
                PublisherName = publisher != null ? publisher.Name : string.Empty,
                ViewerName = viewer != null ? viewer.Name : string.Empty
            })
            .ToListAsync(ct);
        return Results.Ok(pairings.Select(x => ToResponse(x.Pairing, x.PublisherName, x.ViewerName)));
    }

    private static async Task<IResult> RevokeAsync(Guid pairingId, ClaimsPrincipal principal,
        AppDbContext db, TimeProvider time, CancellationToken ct)
    {
        var caller = await DeviceIdentityEndpoints.RequireDeviceAsync(principal, db, ct);
        if (caller is null) return Results.Unauthorized();

        var pairing = await db.DevicePairings.SingleOrDefaultAsync(x =>
            x.Id == pairingId && (x.PublisherDeviceId == caller.Id || x.ViewerDeviceId == caller.Id), ct);
        if (pairing is null) return Results.NotFound();

        if (pairing.Status != DevicePairingStatuses.Revoked)
        {
            pairing.Status = DevicePairingStatuses.Revoked;
            pairing.RevokedAt = time.GetUtcNow();
            await db.SaveChangesAsync(ct);
        }
        return Results.NoContent();
    }

    private static bool IsUsable(PairingChallenge? challenge, DateTimeOffset now) =>
        challenge is not null
        && challenge.ConsumedAt is null
        && challenge.ExpiresAt > now
        && challenge.AttemptCount < challenge.MaxAttempts;

    internal static PairingResponse ToResponse(DevicePairing pairing,
        string publisherDeviceName, string viewerDeviceName) => new(
        pairing.Id, pairing.PublisherDeviceId, pairing.ViewerDeviceId, pairing.Status,
        pairing.CreatedAt, pairing.LastUsedAt, publisherDeviceName, viewerDeviceName);
}
