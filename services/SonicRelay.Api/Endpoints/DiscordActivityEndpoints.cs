using Microsoft.EntityFrameworkCore;
using SonicRelay.Api.Services;
using SonicRelay.Application.Abstractions;
using SonicRelay.Domain.DeviceIdentities;
using SonicRelay.Domain.Sessions;
using SonicRelay.Infrastructure.Persistence;

namespace SonicRelay.Api.Endpoints;

public static class DiscordActivityEndpoints
{
    public sealed record AuthorizeRequest(string Code, string InstanceId);
    public sealed record GrantRequest(string Grant);
    public static IEndpointRouteBuilder MapDiscordActivityEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/discord/activity").RequireRateLimiting("join-session");
        group.MapPost("/authorize", async (AuthorizeRequest request, IDiscordActivityValidator discord, AppDbContext db, TimeProvider time, CancellationToken ct) =>
        {
            DiscordActivityIdentity? identity;
            try { identity = await discord.AuthorizeAsync(request.Code, request.InstanceId, ct); }
            catch (Exception ex) when (ex is HttpRequestException or System.Text.Json.JsonException or KeyNotFoundException or InvalidOperationException or TaskCanceledException) { return Results.Unauthorized(); }
            if (identity is null) return Results.Unauthorized();
            var now = time.GetUtcNow();
            // An existing instance accepts separately authenticated current participants. Only the
            // original invoking user can bind a fresh bootstrap to an instance.
            var intent = await db.LaunchCapabilities.FirstOrDefaultAsync(x => x.Kind == "activity" && x.InstanceId == identity.InstanceId && x.GuildId == identity.GuildId && x.ChannelId == identity.ChannelId && x.ExpiresAt > now, ct);
            if (intent is null)
            {
                intent = await db.LaunchCapabilities.Where(x => x.Kind == "activity" && x.InstanceId == null && x.ConsumedAt == null && x.UserId == identity.UserId && x.GuildId == identity.GuildId && x.ChannelId == identity.ChannelId && x.ExpiresAt > now).OrderByDescending(x => x.CreatedAt).FirstOrDefaultAsync(ct);
                if (intent is null) return Results.Unauthorized();
                intent.ConsumedAt = now; intent.InstanceId = identity.InstanceId; intent.ExpiresAt = now.AddMinutes(15);
            }
            if (!await LiveAsync(db, intent.SessionId, ct)) return Results.Unauthorized();
            var token = RelayCapability.NewToken();
            var credential = RelayCapability.Create("identity", token, now, 900);
            CopyContext(intent, credential); credential.UserId = identity.UserId;
            db.LaunchCapabilities.Add(credential);
            try { await db.SaveChangesAsync(ct); } catch (DbUpdateConcurrencyException) { return Results.Conflict(); }
            return Results.Ok(new { accessToken = token, identity.UserId, identity.InstanceId, credential.ExpiresAt });
        });
        group.MapPost("/viewer-grants", async (HttpContext context, AppDbContext db, IDiscordActivityValidator discord, TimeProvider time, CancellationToken ct) =>
        {
            var header = context.Request.Headers.Authorization.ToString();
            var identity = await RelayCapability.FindAsync(db, header.StartsWith("Bearer ") ? header[7..] : "", "identity", time.GetUtcNow(), ct);
            if (identity is null || !await LiveAsync(db, identity.SessionId, ct)) return Results.Unauthorized();
            if (!await PresentAsync(discord, new(identity.UserId, identity.InstanceId!, identity.GuildId, identity.ChannelId), ct)) return Results.Unauthorized();
            var token = RelayCapability.NewToken();
            var grant = RelayCapability.Create("grant", token, time.GetUtcNow(), 60);
            CopyContext(identity, grant); db.LaunchCapabilities.Add(grant); await db.SaveChangesAsync(ct);
            return Results.Ok(new { grant = token, grant.ExpiresAt });
        });
        group.MapPost("/viewer-grants/redeem", async (GrantRequest request, AppDbContext db, IDiscordActivityValidator discord,
            IParticipantAdmissionLock admissionLock, TurnCredentialService turn, TimeProvider time, CancellationToken ct) =>
        {
            var now = time.GetUtcNow();
            var grant = await RelayCapability.FindAsync(db, request.Grant, "grant", now, ct);
            if (grant?.SessionId is not { } sessionId) return Results.Unauthorized();
            using var admission = await admissionLock.AcquireAsync(sessionId, Guid.Empty, ct);
            await using var transaction = db.Database.IsRelational()
                ? await db.Database.BeginTransactionAsync(System.Data.IsolationLevel.Serializable, ct) : null;
            if (!await PresentAsync(discord, new(grant.UserId, grant.InstanceId!, grant.GuildId, grant.ChannelId), ct)) return Results.Unauthorized();
            var session = await db.StreamSessions.SingleOrDefaultAsync(x => x.Id == sessionId, ct);
            if (session is null || session.Status is SessionStatuses.Ended or SessionStatuses.Expired) return Results.Unauthorized();
            if (await db.SessionParticipants.CountAsync(x => x.SessionId == sessionId && x.Role == ParticipantRoles.Viewer && (x.Status == ParticipantStatuses.Connected || x.Status == ParticipantStatuses.Reconnecting), ct) >= session.MaxViewers) return Results.Conflict(new { code = "viewer_limit" });
            var device = new DeviceIdentity { Id = Guid.NewGuid(), Name = "Discord Activity viewer", DeviceType = "discord_activity", Platform = "web", CreatedAt = now };
            var participant = new SessionParticipant { Id = Guid.NewGuid(), DeviceId = device.Id, SessionId = sessionId, JoinedAt = now };
            SessionAudioPolicy.ApplyDefaults(participant, SessionModes.ScreenShare);
            grant.ConsumedAt = now;
            var token = RelayCapability.NewToken();
            var signal = RelayCapability.Create("signaling", token, now, 900);
            CopyContext(grant, signal); signal.DeviceId = device.Id; signal.ParticipantId = participant.Id;
            db.DeviceIdentities.Add(device); db.SessionParticipants.Add(participant); db.LaunchCapabilities.Add(signal);
            session.Status = SessionStatuses.Active; session.StartedAt ??= now;
            try { await db.SaveChangesAsync(ct); } catch (DbUpdateConcurrencyException) { return Results.Conflict(); }
            if (transaction is not null) await transaction.CommitAsync(ct);
            var ice = await turn.BuildAsync(device.Id.ToString(), ct);
            return Results.Ok(new { sessionId, participantId = participant.Id, signalingToken = token, signal.ExpiresAt, ice.IceServers });
        });
        return app;
    }
    private static Task<bool> LiveAsync(AppDbContext db, Guid? id, CancellationToken ct) => db.StreamSessions.AnyAsync(x => x.Id == id && x.Mode == SessionModes.ScreenShare && x.Status != SessionStatuses.Ended && x.Status != SessionStatuses.Expired, ct);
    private static async Task<bool> PresentAsync(IDiscordActivityValidator discord, DiscordActivityIdentity identity, CancellationToken ct)
    {
        try { return await discord.IsPresentAsync(identity, ct); }
        catch (Exception ex) when (ex is HttpRequestException or System.Text.Json.JsonException or KeyNotFoundException or InvalidOperationException or TaskCanceledException) { return false; }
    }
    private static void CopyContext(LaunchCapability source, LaunchCapability target)
    {
        target.SessionId = source.SessionId; target.UserId = source.UserId; target.GuildId = source.GuildId;
        target.ChannelId = source.ChannelId; target.InstanceId = source.InstanceId;
    }
}
