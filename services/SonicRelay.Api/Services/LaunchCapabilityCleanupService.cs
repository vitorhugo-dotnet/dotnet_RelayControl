using Microsoft.EntityFrameworkCore;
using SonicRelay.Application.Abstractions;
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
            var locks = scope.ServiceProvider.GetRequiredService<IParticipantAdmissionLock>();
            foreach (var cap in expired.Where(x => x.Kind is "media-view" or "media-upload").ToArray())
            {
                if (cap.SessionId is not { } sessionId) continue;
                using var gate = await locks.AcquireAsync(sessionId, Guid.Empty, stoppingToken);
                await db.Entry(cap).ReloadAsync(stoppingToken);
                if (db.Entry(cap).State == EntityState.Detached || cap.ExpiresAt > time.GetUtcNow()) continue;
                if (cap.ParticipantId is { } participantId && await db.SessionParticipants.FindAsync(new object[] { participantId }, stoppingToken) is { } viewer)
                { viewer.Status = ParticipantStatuses.Disconnected; viewer.LeftAt = now; }
                db.LaunchCapabilities.Remove(cap); await db.SaveChangesAsync(stoppingToken);
            }
            expired = expired.Where(x => x.Kind is not ("media-view" or "media-upload")).ToList();
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
