using Microsoft.EntityFrameworkCore;
using SonicRelay.Domain.Sessions;
using SonicRelay.Infrastructure.Persistence;

namespace SonicRelay.Api.Services;

public static class ActivityPresenceService
{
    public static async Task<bool> IsAuthorizedAsync(AppDbContext db, IDiscordActivityValidator discord,
        LaunchCapability capability, DateTimeOffset now, CancellationToken ct)
    {
        var authorized = await db.LaunchCapabilities.AnyAsync(x =>
            x.Kind == "activity" && x.InstanceId == capability.InstanceId && x.SessionId == capability.SessionId
            && x.GuildId == capability.GuildId && x.ChannelId == capability.ChannelId && x.ExpiresAt > now, ct);
        authorized = authorized && await db.StreamSessions.AnyAsync(x => x.Id == capability.SessionId
            && x.Status != SessionStatuses.Ended && x.Status != SessionStatuses.Expired, ct);
        var capabilities = await db.LaunchCapabilities.Where(x => x.Kind != "activity" && x.InstanceId == capability.InstanceId
            && x.SessionId == capability.SessionId).ToListAsync(ct);
        var absent = new HashSet<string>();
        foreach (var user in capabilities.Where(x => x.ExpiresAt > now).DistinctBy(x => x.UserId))
        {
            var present = authorized;
            try
            {
                if (present) present = await discord.IsPresentAsync(new(user.UserId, user.InstanceId!, user.GuildId, user.ChannelId), ct);
            }
            catch (Exception ex) when (ex is HttpRequestException or System.Text.Json.JsonException or KeyNotFoundException
                or InvalidOperationException or TaskCanceledException) { present = false; }
            if (!present) absent.Add(user.UserId);
        }
        var revoked = capabilities.Where(x => absent.Contains(x.UserId) || x.ExpiresAt <= now).ToList();
        foreach (var cap in revoked) cap.ExpiresAt = now;
        var participants = revoked.Where(x => x.ParticipantId != null).Select(x => x.ParticipantId!.Value).ToArray();
        foreach (var participant in await db.SessionParticipants.Where(x => participants.Contains(x.Id)).ToListAsync(ct))
        {
            participant.Status = ParticipantStatuses.Disconnected;
            participant.LeftAt = now;
        }
        if (revoked.Count > 0) await db.SaveChangesAsync(ct);
        return capability.ExpiresAt > now && authorized && !absent.Contains(capability.UserId);
    }
}
