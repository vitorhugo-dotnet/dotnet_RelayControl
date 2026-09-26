using Microsoft.EntityFrameworkCore;
using SonicRelay.Domain.Sessions;
using SonicRelay.Infrastructure.Persistence;

namespace SonicRelay.Api.Services;

public sealed class LaunchCapabilityCleanupService(IServiceScopeFactory scopes, TimeProvider time) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(30), time);
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            using var scope = scopes.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var now = time.GetUtcNow();
            var expired = await db.LaunchCapabilities.Where(x => x.ExpiresAt <= now).ToListAsync(stoppingToken);
            var ids = expired.Where(x => x.Kind == "signaling" && x.ParticipantId != null).Select(x => x.ParticipantId!.Value).ToArray();
            var participants = await db.SessionParticipants.Where(x => ids.Contains(x.Id)).ToListAsync(stoppingToken);
            foreach (var participant in participants)
            {
                participant.Status = ParticipantStatuses.Disconnected; participant.LeftAt = now;
            }
            db.LaunchCapabilities.RemoveRange(expired);
            await db.SaveChangesAsync(stoppingToken);
        }
    }
}
