using System.Security.Claims;
using Microsoft.EntityFrameworkCore;
using Microsoft.FeatureManagement;
using SonicRelay.Api.Contracts;
using SonicRelay.Api.Endpoints;
using SonicRelay.Api.Features;
using SonicRelay.Application.Abstractions;
using SonicRelay.Domain.DeviceIdentities;
using SonicRelay.Domain.Sessions;
using SonicRelay.Infrastructure.Persistence;

namespace SonicRelay.Api.Services;

public sealed class MediaRelayGrantService(AppDbContext db, IVariantFeatureManager features, TimeProvider time,
    IParticipantAdmissionLock locks, IDiscordActivityValidator discord, IConfiguration configuration)
{
    private async Task<bool> EnabledAsync(bool viewer = false) =>
        await features.IsEnabledAsync(RelayFeatures.DiscordWebSocketMedia)
        && await features.IsEnabledAsync(RelayFeatures.ScreenShare)
        && (!viewer || await features.IsEnabledAsync(RelayFeatures.DiscordActivity));

    private Task<StreamSession?> LiveAsync(Guid id, CancellationToken ct) => db.StreamSessions.SingleOrDefaultAsync(x =>
        x.Id == id && x.Mode == SessionModes.ScreenShare && x.Status != SessionStatuses.Ended && x.Status != SessionStatuses.Expired, ct);

    public async Task<MediaAdmission?> IssueUploadAsync(Guid sessionId, ClaimsPrincipal user, CancellationToken ct)
    {
        if (!await EnabledAsync()) return null;
        var device = await DeviceIdentityEndpoints.RequireDeviceAsync(user, db, ct);
        if (device is null) return null;
        using var gate = await locks.AcquireAsync(sessionId, Guid.Empty, ct);
        var session = await LiveAsync(sessionId, ct);
        if (session?.SourceDeviceId != device.Id) return null;
        var source = await db.SessionParticipants.SingleOrDefaultAsync(x => x.SessionId == sessionId
            && x.DeviceId == device.Id && x.Role == ParticipantRoles.Publisher && x.Status == ParticipantStatuses.Connected, ct);
        if (source is null) return null;
        var token = RelayCapability.NewToken(); var cap = RelayCapability.Create("media-upload", token, time.GetUtcNow(), 30);
        cap.SessionId = sessionId; cap.DeviceId = device.Id;
        cap.Code = device.CredentialVersion.ToString(System.Globalization.CultureInfo.InvariantCulture);
        // No ParticipantId: this capability never owns RTC cleanup.
        db.LaunchCapabilities.Add(cap); await db.SaveChangesAsync(ct);
        return Admission(cap, token, source.Id);
    }

    public async Task<MediaAdmission?> IssueViewAsync(string grantToken, string identityToken, CancellationToken ct)
    {
        if (!await EnabledAsync(true)) return null;
        var now = time.GetUtcNow();
        var grant = await RelayCapability.FindAsync(db, grantToken, "grant", now, ct);
        var identity = await RelayCapability.FindAsync(db, identityToken, "identity", now, ct);
        if (grant?.SessionId is not { } sessionId || identity is null || grant.Code != identity.Id.ToString("N")) return null;
        using var gate = await locks.AcquireAsync(sessionId, Guid.Empty, ct);
        await db.Entry(grant).ReloadAsync(ct);
        if (grant.ConsumedAt != null || grant.ExpiresAt <= time.GetUtcNow()
            || !await ActivityPresenceService.IsAuthorizedAsync(db, discord, identity, now, ct)) return null;
        var session = await LiveAsync(sessionId, ct); if (session is null) return null;
        await using var transaction = db.Database.IsRelational()
            ? await db.Database.BeginTransactionAsync(System.Data.IsolationLevel.Serializable, ct) : null;
        var old = await db.LaunchCapabilities.Where(x => x.Kind == "media-view" && x.SessionId == sessionId
            && x.InstanceId == identity.InstanceId && x.UserId == identity.UserId).ToListAsync(ct);
        var participantId = old.FirstOrDefault(x => x.ParticipantId != null)?.ParticipantId;
        var participant = participantId.HasValue ? await db.SessionParticipants.FindAsync(new object[] { participantId.Value }, ct) : null;
        if (participant?.Status != ParticipantStatuses.Connected && await db.SessionParticipants.CountAsync(x =>
            x.SessionId == sessionId && x.Role == ParticipantRoles.Viewer
            && (x.Status == ParticipantStatuses.Connected || x.Status == ParticipantStatuses.Reconnecting), ct) >= session.MaxViewers)
            throw new MediaViewerLimitException();
        if (participant is null)
        {
            var device = new DeviceIdentity { Id = Guid.NewGuid(), Name = "Discord media viewer", DeviceType = "discord_activity", Platform = "web", CreatedAt = now };
            participant = new SessionParticipant { Id = Guid.NewGuid(), DeviceId = device.Id, SessionId = sessionId, JoinedAt = now };
            SessionAudioPolicy.ApplyDefaults(participant, SessionModes.ScreenShare);
            db.DeviceIdentities.Add(device); db.SessionParticipants.Add(participant);
        }
        participant.Status = ParticipantStatuses.Connected; participant.LeftAt = null;
        foreach (var previous in old) { previous.ExpiresAt = now; previous.ParticipantId = null; }
        var token = RelayCapability.NewToken(); var cap = RelayCapability.Create("media-view", token, now, 30);
        cap.SessionId = sessionId; cap.DeviceId = participant.DeviceId; cap.ParticipantId = participant.Id;
        cap.InstanceId = identity.InstanceId; cap.UserId = identity.UserId; cap.GuildId = identity.GuildId; cap.ChannelId = identity.ChannelId;
        cap.Code = identity.Id.ToString("N"); grant.ConsumedAt = now;
        session.Status = SessionStatuses.Active; session.StartedAt ??= now;
        db.LaunchCapabilities.Add(cap);
        try { await db.SaveChangesAsync(ct); } catch (DbUpdateConcurrencyException) { return null; }
        if (transaction is not null) await transaction.CommitAsync(ct);
        return Admission(cap, token, participant.Id);
    }

    private MediaAdmission Admission(LaunchCapability cap, string token, Guid participant) =>
        new(cap.Id, cap.SessionId!.Value, participant, token, cap.ExpiresAt, configuration["MediaRelay:PublicBaseUrl"] ?? "/media/ws/media");

    public async Task<MediaLease?> RedeemAsync(string token, Guid connectionId, CancellationToken ct)
    {
        if (connectionId == Guid.Empty || token is not { Length: 64 }) return null;
        var hash = RelayCapability.Hash(token);
        var cap = await db.LaunchCapabilities.SingleOrDefaultAsync(x => x.TokenHash == hash
            && (x.Kind == "media-upload" || x.Kind == "media-view"), ct);
        if (cap?.SessionId is not { } sessionId) return null;
        using var gate = await locks.AcquireAsync(sessionId, Guid.Empty, ct);
        await db.Entry(cap).ReloadAsync(ct);
        if (cap.ConsumedAt != null || !await ValidAsync(cap, ct)) return null;
        cap.ConsumedAt = time.GetUtcNow(); cap.MessageId = connectionId.ToString("N"); cap.ExpiresAt = time.GetUtcNow().AddSeconds(30);
        try { await db.SaveChangesAsync(ct); } catch (DbUpdateConcurrencyException) { return null; }
        return Lease(cap);
    }

    public async Task<MediaLease?> RenewAsync(Guid leaseId, Guid connectionId, CancellationToken ct)
    {
        var cap = await db.LaunchCapabilities.FindAsync(new object[] { leaseId }, ct);
        if (cap?.SessionId is not { } sessionId) return null;
        using var gate = await locks.AcquireAsync(sessionId, Guid.Empty, ct);
        await db.Entry(cap).ReloadAsync(ct);
        if (cap.ConsumedAt == null || cap.MessageId != connectionId.ToString("N") || !await ValidAsync(cap, ct)) return null;
        cap.ExpiresAt = time.GetUtcNow().AddSeconds(30); await db.SaveChangesAsync(ct); return Lease(cap);
    }

    private async Task<bool> ValidAsync(LaunchCapability cap, CancellationToken ct)
    {
        var view = cap.Kind == "media-view";
        if ((!view && cap.Kind != "media-upload") || cap.ExpiresAt <= time.GetUtcNow()
            || !await EnabledAsync(view) || cap.SessionId is not { } id) return false;
        var session = await LiveAsync(id, ct); if (session is null) return false;
        var credentialVersion = int.TryParse(cap.Code, out var version) ? version : -1;
        if (!view) return session.SourceDeviceId == cap.DeviceId
            && await db.DeviceIdentities.AnyAsync(x => x.Id == cap.DeviceId && x.Status == DeviceIdentityStatuses.Active
                && x.RevokedAt == null && x.CredentialVersion == credentialVersion, ct)
            && await db.SessionParticipants.AnyAsync(x => x.SessionId == id && x.DeviceId == cap.DeviceId
                && x.Role == ParticipantRoles.Publisher && x.Status == ParticipantStatuses.Connected, ct);
        if (!Guid.TryParseExact(cap.Code, "N", out var identityId)) return false;
        var identity = await db.LaunchCapabilities.FindAsync(new object[] { identityId }, ct);
        return identity is { Kind: "identity" } && identity.ExpiresAt > time.GetUtcNow()
            && cap.ParticipantId.HasValue && await db.SessionParticipants.AnyAsync(x => x.Id == cap.ParticipantId
                && x.Status == ParticipantStatuses.Connected, ct)
            && await ActivityPresenceService.IsAuthorizedAsync(db, discord, cap, time.GetUtcNow(), ct);
    }

    public async Task<bool> ReleaseAsync(Guid leaseId, Guid connectionId, CancellationToken ct)
    {
        var cap = await db.LaunchCapabilities.FindAsync(new object[] { leaseId }, ct);
        if (cap?.SessionId is not { } id || cap.Kind is not ("media-view" or "media-upload")) return false;
        using var gate = await locks.AcquireAsync(id, Guid.Empty, ct); await db.Entry(cap).ReloadAsync(ct);
        if (cap.MessageId != connectionId.ToString("N")) return false;
        await ReleaseCoreAsync(cap, ct); return true;
    }

    public async Task<bool> ReleaseAdmissionAsync(Guid id, LaunchCapability identity, CancellationToken ct)
    {
        var cap = await db.LaunchCapabilities.FindAsync(new object[] { id }, ct);
        if (cap is null) return true;
        if (cap?.SessionId is not { } sessionId || cap.Kind != "media-view" || cap.UserId != identity.UserId
            || cap.InstanceId != identity.InstanceId || cap.SessionId != identity.SessionId) return false;
        using var gate = await locks.AcquireAsync(sessionId, Guid.Empty, ct); await db.Entry(cap).ReloadAsync(ct);
        await ReleaseCoreAsync(cap, ct); return true;
    }

    private async Task ReleaseCoreAsync(LaunchCapability cap, CancellationToken ct)
    {
        cap.ExpiresAt = time.GetUtcNow();
        if (cap.ParticipantId is { } id && await db.SessionParticipants.FindAsync(new object[] { id }, ct) is { } participant)
        { participant.Status = ParticipantStatuses.Disconnected; participant.LeftAt = time.GetUtcNow(); }
        cap.ParticipantId = null; await db.SaveChangesAsync(ct);
    }

    private static MediaLease Lease(LaunchCapability cap) => new(cap.Id, cap.SessionId!.Value, cap.ParticipantId,
        cap.Kind == "media-upload" ? "upload" : "view", cap.ExpiresAt);
}

public sealed class MediaViewerLimitException : Exception;
