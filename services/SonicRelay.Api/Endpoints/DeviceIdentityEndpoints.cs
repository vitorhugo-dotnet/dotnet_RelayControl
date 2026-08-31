using System.Security.Claims;
using System.IdentityModel.Tokens.Jwt;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;
using SonicRelay.Api.Contracts;
using SonicRelay.Api.Services;
using SonicRelay.Domain.DeviceIdentities;
using SonicRelay.Domain.Devices;
using SonicRelay.Infrastructure.Persistence;

namespace SonicRelay.Api.Endpoints;

public static class DeviceIdentityEndpoints
{
    public static IEndpointRouteBuilder MapDeviceIdentityEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/devices").WithTags("DeviceIdentity");
        group.MapPost("/bootstrap", BootstrapAsync).RequireRateLimiting("device-bootstrap");
        group.MapPost("/token", TokenAsync).RequireRateLimiting("device-token");
        group.MapPost("/rotate-credential", RotateAsync).RequireAuthorization("device:manage");
        group.MapPost("/revoke", RevokeAsync).RequireAuthorization("device:manage");
        return app;
    }

    private static async Task<IResult> BootstrapAsync(BootstrapDeviceRequest request,
        DeviceCredentialService credentials, AppDbContext db, TimeProvider time, CancellationToken ct)
    {
        if (!ValidName(request.Name) || !ValidTypePlatform(request.DeviceType, request.Platform))
            return Results.BadRequest(new { error = "Invalid device name, type, or platform." });

        var (plaintext, hash) = credentials.GenerateCredential();
        var device = new DeviceIdentity
        {
            Id = Guid.NewGuid(),
            Name = request.Name!.Trim(),
            DeviceType = request.DeviceType!,
            Platform = request.Platform!,
            CredentialSecretHash = hash,
            CredentialVersion = 1,
            Status = DeviceIdentityStatuses.Active,
            CreatedAt = time.GetUtcNow()
        };
        db.DeviceIdentities.Add(device);
        await db.SaveChangesAsync(ct);
        return Results.Created($"/api/devices/{device.Id}",
            new BootstrapDeviceResponse(device.Id, plaintext, device.CredentialVersion));
    }

    private static async Task<IResult> TokenAsync(DeviceTokenRequest request,
        DeviceCredentialService credentials, DeviceIdentityRotationService rotation,
        AppDbContext db, TimeProvider time, CancellationToken ct)
    {
        var device = await db.DeviceIdentities.SingleOrDefaultAsync(x => x.Id == request.DeviceId, ct);
        if (device is null || device.Status != DeviceIdentityStatuses.Active
            || !credentials.VerifySecret(request.CredentialSecret ?? string.Empty, device.CredentialSecretHash))
        {
            return Results.Unauthorized();
        }

        device.LastSeenAt = time.GetUtcNow();

        // Token exchange is the one call every device makes regularly, which makes it the place
        // to retire an identifier that is approaching the retention ceiling. On rotation the
        // caller is handed a new device id and credential and the old identity is gone
        // (issue #44, docs/data-retention.md).
        var rotated = await rotation.RotateIfDueAsync(device, ct);
        if (rotated is not { } outcome) return Results.Unauthorized();

        await db.SaveChangesAsync(ct);
        var (token, expiresAt) = credentials.IssueAccessToken(outcome.Device);
        return Results.Ok(new DeviceTokenResponse(
            token,
            expiresAt,
            DeviceCredentialService.ScopesFor(outcome.Device.DeviceType),
            outcome.Device.Id,
            outcome.Device.CredentialVersion,
            outcome.RotatedCredentialSecret));
    }

    private static async Task<IResult> RotateAsync(RotateCredentialRequest request, ClaimsPrincipal principal,
        DeviceCredentialService credentials, AppDbContext db, CancellationToken ct)
    {
        var device = await RequireDeviceAsync(principal, db, ct);
        if (device is null) return Results.Unauthorized();
        if (!credentials.VerifySecret(request.CurrentCredentialSecret ?? string.Empty, device.CredentialSecretHash))
            return Results.Unauthorized();

        var (plaintext, hash) = credentials.GenerateCredential();
        device.CredentialSecretHash = hash;
        device.CredentialVersion += 1;
        await db.SaveChangesAsync(ct);
        return Results.Ok(new RotateCredentialResponse(plaintext, device.CredentialVersion));
    }

    private static async Task<IResult> RevokeAsync(ClaimsPrincipal principal,
        AppDbContext db, TimeProvider time, CancellationToken ct)
    {
        var device = await RequireDeviceAsync(principal, db, ct);
        if (device is null) return Results.Unauthorized();
        if (device.Status != DeviceIdentityStatuses.Revoked)
        {
            device.Status = DeviceIdentityStatuses.Revoked;
            device.RevokedAt = time.GetUtcNow();
            await db.SaveChangesAsync(ct);
        }
        return Results.NoContent();
    }

    internal static async Task<DeviceIdentity?> RequireDeviceAsync(
        ClaimsPrincipal principal, AppDbContext db, CancellationToken ct)
    {
        if (!Guid.TryParse(principal.FindFirstValue(JwtRegisteredClaimNames.Sub), out var deviceId)) return null;
        return await db.DeviceIdentities.SingleOrDefaultAsync(x => x.Id == deviceId, ct);
    }

    private static bool ValidName(string? name) => !string.IsNullOrWhiteSpace(name) && name.Trim().Length <= 120;

    private static bool ValidTypePlatform(string? type, string? platform) =>
        (type == DeviceTypes.WindowsPublisher && platform == DevicePlatforms.Windows)
        || (type == DeviceTypes.WindowsDesktop && platform == DevicePlatforms.Windows)
        || (type == DeviceTypes.FlutterViewer
            && platform is DevicePlatforms.Android or DevicePlatforms.Ios or DevicePlatforms.Web);
}
