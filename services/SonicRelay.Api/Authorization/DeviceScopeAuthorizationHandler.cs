using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.EntityFrameworkCore;
using SonicRelay.Domain.DeviceIdentities;
using SonicRelay.Infrastructure.Persistence;

namespace SonicRelay.Api.Authorization;

public sealed class DeviceScopeAuthorizationHandler(AppDbContext db) : AuthorizationHandler<DeviceScopeRequirement>
{
    protected override async Task HandleRequirementAsync(
        AuthorizationHandlerContext context, DeviceScopeRequirement requirement)
    {
        if (requirement.Scope == "signaling:connect" && context.User.Identities.Any(x => x.AuthenticationType == "Activity"))
        {
            context.Succeed(requirement);
            return;
        }
        if (requirement.Scope is not null)
        {
            var scopes = context.User.FindFirstValue("scope")?.Split(' ', StringSplitOptions.RemoveEmptyEntries)
                ?? [];
            if (!scopes.Contains(requirement.Scope)) return;
        }

        if (!Guid.TryParse(context.User.FindFirstValue(JwtRegisteredClaimNames.Sub), out var deviceId)) return;
        if (!int.TryParse(context.User.FindFirstValue("cv"), out var tokenCredentialVersion)) return;

        var device = await db.DeviceIdentities.AsNoTracking()
            .Where(x => x.Id == deviceId)
            .Select(x => new { x.Status, x.CredentialVersion })
            .SingleOrDefaultAsync();

        if (device is null) return;
        if (device.Status != DeviceIdentityStatuses.Active) return;
        if (device.CredentialVersion != tokenCredentialVersion) return;

        context.Succeed(requirement);
    }
}
