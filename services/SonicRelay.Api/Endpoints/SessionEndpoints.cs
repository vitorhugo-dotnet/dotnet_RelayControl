using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;
using SonicRelay.Api.Services;
using SonicRelay.Application.Abstractions;
using SonicRelay.Domain.DeviceIdentities;
using SonicRelay.Domain.Devices;
using SonicRelay.Domain.Sessions;
using SonicRelay.Infrastructure.Persistence;

namespace SonicRelay.Api.Endpoints;

public static class SessionEndpoints
{
    public static IEndpointRouteBuilder MapSessionEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/sessions").WithTags("Sessions");
        group.MapPost("/", CreateAsync).RequireAuthorization("session:create").RequireRateLimiting("create-session");
        group.MapGet("/active", GetActiveAsync).RequireAuthorization("DeviceAuthenticated");
        group.MapGet("/discoverable", GetDiscoverableAsync).RequireAuthorization("session:join");
        group.MapGet("/{sessionId:guid}", GetAsync).RequireAuthorization("DeviceAuthenticated");
        group.MapPost("/{sessionId:guid}/end", EndAsync).RequireAuthorization("session:end");
        group.MapPost("/{sessionId:guid}/rotate-code", RotateCodeAsync).RequireAuthorization("session:end").RequireRateLimiting("rotate-code");
        group.MapPost("/join", JoinAsync).RequireAuthorization("session:join").RequireRateLimiting("join-session");
        group.MapPost("/{sessionId:guid}/join", JoinByIdAsync).RequireAuthorization("session:join").RequireRateLimiting("join-session");
        group.MapGet("/{sessionId:guid}/participants", GetParticipantsAsync).RequireAuthorization("DeviceAuthenticated");
        // Owner-only, like end and rotate-code: "session:end" is the scope the API already
        // grants exclusively to the device that publishes a session, so it doubles as the
        // session-owner management scope rather than minting a near-duplicate one.
        group.MapPost("/{sessionId:guid}/participants/{participantId:guid}/audio-permission", SetAudioPermissionAsync)
            .RequireAuthorization("session:end");
        return app;
    }

    private static async Task<IResult> CreateAsync(CreateSessionRequest request,
        ClaimsPrincipal principal, AppDbContext db, ISessionCodeStore codeStore, IConfiguration configuration,
        ILoggerFactory loggerFactory, CancellationToken ct)
    {
        var device = await DeviceIdentityEndpoints.RequireDeviceAsync(principal, db, ct);
        if (device is null) return Results.Unauthorized();
        var maxViewers = request.MaxViewers ?? configuration.GetValue("Sessions:MaxViewersPerSession", 3);
        if (maxViewers < 1) return Results.BadRequest(new { error = "MaxViewers must be at least one." });
        var mode = SessionModes.Normalize(request.Mode);
        if (mode is null)
            return Results.BadRequest(new
            {
                error = $"Mode must be '{SessionModes.Broadcast}', '{SessionModes.Duplex}' or '{SessionModes.ScreenShare}'.",
                code = "invalid_session_mode"
            });

        var now = DateTimeOffset.UtcNow;
        var ttl = CodeTtl(configuration);
        var session = new StreamSession
        {
            Id = Guid.NewGuid(),
            SourceDeviceId = device.Id,
            Mode = mode,
            MaxViewers = maxViewers,
            CodeExpiresAt = now.Add(ttl),
            CreatedAt = now
        };
        db.StreamSessions.Add(session);
        var publisher = new SessionParticipant
        {
            Id = Guid.NewGuid(),
            SessionId = session.Id,
            DeviceId = device.Id,
            Role = ParticipantRoles.Publisher,
            Status = ParticipantStatuses.Connected,
            JoinedAt = now
        };
        SessionAudioPolicy.ApplyDefaults(publisher, session.Mode);
        db.SessionParticipants.Add(publisher);
        await db.SaveChangesAsync(ct);

        var code = GenerateCode();
        await codeStore.StoreAsync(HashCode(code, configuration), session.Id, ttl, ct);
        loggerFactory.CreateLogger("SonicRelay.Sessions").LogInformation(
            "Created session {SessionId} from device {DeviceId}", session.Id, device.Id);
        return Results.Created($"/api/sessions/{session.Id}", ToResponse(session, code));
    }

    private static async Task<IResult> GetActiveAsync(ClaimsPrincipal principal, AppDbContext db, CancellationToken ct)
    {
        var device = await DeviceIdentityEndpoints.RequireDeviceAsync(principal, db, ct);
        if (device is null) return Results.Unauthorized();
        var sessions = await db.StreamSessions
            .Where(x => (x.Status == SessionStatuses.Waiting || x.Status == SessionStatuses.Active)
                && (x.SourceDeviceId == device.Id || db.SessionParticipants.Any(p => p.SessionId == x.Id && p.DeviceId == device.Id)))
            .OrderByDescending(x => x.CreatedAt)
            .Select(x => new
            {
                x.Id,
                x.SourceDeviceId,
                x.Status,
                x.Mode,
                x.MaxViewers,
                x.CodeExpiresAt,
                x.StartedAt,
                x.EndedAt,
                x.CreatedAt,
                ViewerCount = db.SessionParticipants.Count(p => p.SessionId == x.Id && p.Role == ParticipantRoles.Viewer
                    && p.Status == ParticipantStatuses.Connected)
            }).ToListAsync(ct);
        return Results.Ok(sessions);
    }

    // Sessions a paired viewer is allowed to join without a code. The pairing is the
    // authorization; the join code only ever proved the viewer could read the publisher's
    // screen, which an active pairing establishes more strongly. No code is projected here
    // — it is a separate short-lived secret and discovery must not become a way to read it.
    private static async Task<IResult> GetDiscoverableAsync(ClaimsPrincipal principal, AppDbContext db,
        CancellationToken ct)
    {
        var device = await DeviceIdentityEndpoints.RequireDeviceAsync(principal, db, ct);
        if (device is null) return Results.Unauthorized();

        var sessions = await db.StreamSessions.AsNoTracking()
            .Where(x => (x.Status == SessionStatuses.Waiting || x.Status == SessionStatuses.Active)
                && db.DevicePairings.Any(p => p.PublisherDeviceId == x.SourceDeviceId
                    && p.ViewerDeviceId == device.Id
                    && p.Status == DevicePairingStatuses.Active))
            .OrderByDescending(x => x.CreatedAt)
            .Select(x => new
            {
                SessionId = x.Id,
                PublisherDeviceId = x.SourceDeviceId,
                PublisherDeviceName = db.DeviceIdentities
                    .Where(d => d.Id == x.SourceDeviceId).Select(d => d.Name).FirstOrDefault(),
                x.Status,
                x.Mode,
                x.MaxViewers,
                x.CreatedAt,
                ViewerCount = db.SessionParticipants.Count(p => p.SessionId == x.Id
                    && p.Role == ParticipantRoles.Viewer
                    && p.Status == ParticipantStatuses.Connected)
            })
            .ToListAsync(ct);

        return Results.Ok(sessions);
    }

    private static async Task<IResult> GetAsync(Guid sessionId, ClaimsPrincipal principal, AppDbContext db, CancellationToken ct)
    {
        var device = await DeviceIdentityEndpoints.RequireDeviceAsync(principal, db, ct);
        if (device is null) return Results.Unauthorized();
        var session = await db.StreamSessions.SingleOrDefaultAsync(x => x.Id == sessionId, ct);
        if (session is null) return Results.NotFound();
        var canAccess = session.SourceDeviceId == device.Id
            || await db.SessionParticipants.AnyAsync(x => x.SessionId == sessionId && x.DeviceId == device.Id, ct);
        return canAccess ? Results.Ok(ToResponse(session)) : Results.NotFound();
    }

    private static async Task<IResult> EndAsync(Guid sessionId, ClaimsPrincipal principal, AppDbContext db,
        ISessionCodeStore codeStore, IParticipantReconnectTracker reconnectTracker, ILoggerFactory loggerFactory,
        CancellationToken ct)
    {
        var device = await DeviceIdentityEndpoints.RequireDeviceAsync(principal, db, ct);
        if (device is null) return Results.Unauthorized();
        var session = await db.StreamSessions.SingleOrDefaultAsync(x => x.Id == sessionId && x.SourceDeviceId == device.Id, ct);
        if (session is null) return Results.NotFound();
        if (session.Status != SessionStatuses.Ended)
        {
            var now = DateTimeOffset.UtcNow;
            session.Status = SessionStatuses.Ended;
            session.EndedAt = now;
            // Includes participants mid-reconnect-grace-period: an owner-initiated end must win
            // immediately over a pending grace timer, which we also cancel so it can't fire a
            // stale "session.left" broadcast afterwards.
            var connected = await db.SessionParticipants.Where(x => x.SessionId == sessionId
                && (x.Status == ParticipantStatuses.Connected || x.Status == ParticipantStatuses.Reconnecting))
                .ToListAsync(ct);
            foreach (var participant in connected)
            {
                participant.Status = ParticipantStatuses.Disconnected;
                participant.ConnectionId = null;
                participant.LeftAt = now;
                reconnectTracker.TryCancelGracePeriod(participant.Id);
            }
            await db.SaveChangesAsync(ct);
            await codeStore.RemoveAsync(sessionId, ct);
            loggerFactory.CreateLogger("SonicRelay.Sessions").LogInformation(
                "Ended session {SessionId} from device {DeviceId}; disconnected {ParticipantCount} participants",
                sessionId, device.Id, connected.Count);
        }
        return Results.Ok(ToResponse(session));
    }

    private static async Task<IResult> RotateCodeAsync(Guid sessionId, ClaimsPrincipal principal, AppDbContext db,
        ISessionCodeStore codeStore, IConfiguration configuration, ILoggerFactory loggerFactory, CancellationToken ct)
    {
        var device = await DeviceIdentityEndpoints.RequireDeviceAsync(principal, db, ct);
        if (device is null) return Results.Unauthorized();
        var session = await db.StreamSessions.SingleOrDefaultAsync(x => x.Id == sessionId && x.SourceDeviceId == device.Id, ct);
        if (session is null) return Results.NotFound();
        if (session.Status is SessionStatuses.Ended or SessionStatuses.Expired) return Results.Conflict();

        var code = GenerateCode();
        var ttl = CodeTtl(configuration);
        session.CodeExpiresAt = DateTimeOffset.UtcNow.Add(ttl);
        await db.SaveChangesAsync(ct);
        await codeStore.StoreAsync(HashCode(code, configuration), session.Id, ttl, ct);
        loggerFactory.CreateLogger("SonicRelay.Sessions").LogInformation(
            "Rotated join code for session {SessionId} from device {DeviceId}", session.Id, device.Id);
        return Results.Ok(ToResponse(session, code));
    }

    private static async Task<IResult> JoinAsync(JoinSessionRequest request, ClaimsPrincipal principal, AppDbContext db,
        ISessionCodeStore codeStore, IConfiguration configuration, IParticipantAdmissionLock admissionLock,
        ILoggerFactory loggerFactory, CancellationToken ct)
    {
        var device = await DeviceIdentityEndpoints.RequireDeviceAsync(principal, db, ct);
        if (device is null) return Results.Unauthorized();
        var normalizedCode = request.Code?.Trim().ToUpperInvariant() ?? string.Empty;
        if (normalizedCode.Length != 6 || normalizedCode.Any(c => !char.IsAsciiLetterOrDigit(c)))
            return InvalidCode();

        var sessionId = await codeStore.RedeemAsync(HashCode(normalizedCode, configuration), ct);
        if (sessionId is null) return InvalidCode();
        var session = await db.StreamSessions.SingleOrDefaultAsync(x => x.Id == sessionId.Value, ct);
        var now = DateTimeOffset.UtcNow;
        if (session is null || session.CodeExpiresAt <= now
            || session.Status is SessionStatuses.Ended or SessionStatuses.Expired)
        {
            // Only a session still waiting for its first viewer dies with its code; an active
            // session outlives the code (the stale code just stops admitting new viewers).
            if (session is not null && session.CodeExpiresAt <= now && session.Status == SessionStatuses.Waiting)
            {
                session.Status = SessionStatuses.Expired;
                await db.SaveChangesAsync(ct);
                await codeStore.RemoveAsync(session.Id, ct);
                loggerFactory.CreateLogger("SonicRelay.Sessions").LogInformation(
                    "Marked session {SessionId} expired during join", session.Id);
            }
            return InvalidCode();
        }

        return await AdmitViewerAsync(session, device, db, admissionLock, loggerFactory, ct);
    }

    // Code-free join for a session the caller found through /discoverable. It runs exactly the
    // checks the code path runs after redemption; the active pairing is the authorization.
    // A session the caller cannot see is reported as invalid_code rather than not_paired, so
    // this endpoint cannot be used to probe which session ids exist.
    private static async Task<IResult> JoinByIdAsync(Guid sessionId, ClaimsPrincipal principal, AppDbContext db,
        IParticipantAdmissionLock admissionLock, ILoggerFactory loggerFactory, CancellationToken ct)
    {
        var device = await DeviceIdentityEndpoints.RequireDeviceAsync(principal, db, ct);
        if (device is null) return Results.Unauthorized();

        var session = await db.StreamSessions.SingleOrDefaultAsync(x => x.Id == sessionId, ct);
        if (session is null || session.Status is SessionStatuses.Ended or SessionStatuses.Expired)
            return InvalidCode();

        return await AdmitViewerAsync(session, device, db, admissionLock, loggerFactory, ct);
    }

    // Shared by both join paths (code and session id): everything that happens once a live
    // session has been resolved. Keeping it in one place is what stops the two entry points
    // from drifting on pairing, viewer-limit or reconnect semantics.
    private static async Task<IResult> AdmitViewerAsync(StreamSession session, DeviceIdentity device,
        AppDbContext db, IParticipantAdmissionLock admissionLock, ILoggerFactory loggerFactory, CancellationToken ct)
    {
        // Admission is read-then-insert, so two joins racing each other would otherwise both see
        // "no participant yet" and both insert one. That is not a hypothetical: a device coming
        // back from a network loss legitimately fires several joins at once (the automatic
        // recovery plus a manual retry, or attempts either side of an interface handover), and a
        // duplicate row eats a viewer slot and splits signaling routing across two participant
        // ids. The unique index on (SessionId, DeviceId, Role) is the cross-instance backstop;
        // this lock keeps the single-instance case off the constraint-violation path entirely.
        using var admission = await admissionLock.AcquireAsync(session.Id, device.Id, ct);
        try
        {
            return await AdmitViewerCoreAsync(session, device, db, loggerFactory, ct);
        }
        catch (DbUpdateException)
        {
            // Another API instance won the insert. Its row is the participant now; adopt it
            // rather than reporting a failure the client could only answer by retrying.
            db.ChangeTracker.Clear();
            var winner = await FindViewerParticipantAsync(db, session.Id, device.Id, ct);
            if (winner is null) throw;
            return await ResumeParticipantAsync(winner, session, device, db, loggerFactory, ct);
        }
    }

    private static async Task<IResult> AdmitViewerCoreAsync(StreamSession session, DeviceIdentity device,
        AppDbContext db, ILoggerFactory loggerFactory, CancellationToken ct)
    {
        var now = DateTimeOffset.UtcNow;
        var logger = loggerFactory.CreateLogger("SonicRelay.Sessions");

        var existing = await FindViewerParticipantAsync(db, session.Id, device.Id, ct);
        if (existing is not null)
        {
            return await ResumeParticipantAsync(existing, session, device, db, loggerFactory, ct);
        }

        // A screen session carries a video track. A device type that cannot render video would
        // be handed an offer with a video m-line it does not understand, so the gate is on the
        // server rather than a hope that older clients withdraw politely. It also runs before
        // the pairing check: being paired must never be a way around it.
        if (session.Mode == SessionModes.ScreenShare && device.DeviceType != DeviceTypes.WindowsDesktop)
            return DeviceTypeNotAllowed();

        // The public radio room's virtual publisher is intentionally open: any authenticated
        // device may listen without ever pairing with it, real-device pairing only gates a real
        // publisher's session. Requiring a pairing here would make this dependent on the
        // best-effort auto-pair side effect in PublicRoomEndpoints.GetAsync running first —
        // fragile under retries, offline joins, or a client that reaches this session id without
        // ever calling GET /api/public-room.
        var isPublicRoomSession = session.SourceDeviceId == PublicRoomSeeder.VirtualPublisherDeviceId;
        if (!isPublicRoomSession && !await HasActivePairingAsync(db, session.SourceDeviceId, device.Id, ct))
        {
            // A screen session is entered with one secret: its join code. Holding a valid code
            // is what establishes the pairing, rather than the pairing being a prerequisite the
            // user would have to satisfy with a second code. The row is still created — it is
            // what carries revocation, the pairings listing and code-free rejoin — so the only
            // thing dropped is the extra step, not the record. Audio sessions are untouched:
            // there, a missing pairing is still a refusal.
            if (session.Mode != SessionModes.ScreenShare) return NotPaired();

            db.DevicePairings.Add(new DevicePairing
            {
                Id = Guid.NewGuid(),
                PublisherDeviceId = session.SourceDeviceId,
                ViewerDeviceId = device.Id,
                Status = DevicePairingStatuses.Active,
                CreatedAt = now,
                LastUsedAt = now
            });
        }

        // Viewers mid-reconnect-grace-period still hold their slot, otherwise a new viewer
        // could take it during the grace window and leave a maxViewers=1 session with two
        // viewers once the original one's WebSocket reconnects.
        var viewerCount = await CountDistinctActiveViewerDevicesAsync(db, session.Id, ct);
        if (viewerCount >= session.MaxViewers) return Results.Conflict(new { error = "Session viewer limit reached." });

        var participant = new SessionParticipant
        {
            Id = Guid.NewGuid(),
            SessionId = session.Id,
            DeviceId = device.Id,
            Role = ParticipantRoles.Viewer,
            Status = ParticipantStatuses.Connected,
            JoinedAt = now
        };
        // In a duplex session a joining participant is a peer, not an audience member: the
        // "viewer" role stays as the routing/capacity concept it has always been, and the
        // session mode is what decides whether the participant may publish audio.
        SessionAudioPolicy.ApplyDefaults(participant, session.Mode);
        db.SessionParticipants.Add(participant);
        if (session.Status == SessionStatuses.Waiting)
        {
            session.Status = SessionStatuses.Active;
            session.StartedAt = now;
        }
        await db.SaveChangesAsync(ct);
        logger.LogInformation(
            "Joined session {SessionId} as participant {ParticipantId} from device {DeviceId}",
            session.Id, participant.Id, device.Id);
        return Results.Ok(ToResponse(session));
    }

    private static async Task<IResult> ResumeParticipantAsync(SessionParticipant participant, StreamSession session,
        DeviceIdentity device, AppDbContext db, ILoggerFactory loggerFactory, CancellationToken ct)
    {
        participant.Status = ParticipantStatuses.Connected;
        participant.LeftAt = null;
        await db.SaveChangesAsync(ct);
        loggerFactory.CreateLogger("SonicRelay.Sessions").LogInformation(
            "Reconnected participant {ParticipantId} to session {SessionId} from device {DeviceId}",
            participant.Id, session.Id, device.Id);
        return Results.Ok(ToResponse(session));
    }

    /// <summary>
    /// Presence and audio capabilities of everyone in a session, for any device that takes part
    /// in it. Duplex clients need this to know which peers may publish audio before any SDP is
    /// exchanged; broadcast clients get the same view of the publisher. Device ids are
    /// deliberately not projected — participant ids are what signaling addresses, and a device
    /// id is a durable identifier that peers have no reason to learn from each other.
    /// </summary>
    private static async Task<IResult> GetParticipantsAsync(Guid sessionId, ClaimsPrincipal principal,
        AppDbContext db, CancellationToken ct)
    {
        var device = await DeviceIdentityEndpoints.RequireDeviceAsync(principal, db, ct);
        if (device is null) return Results.Unauthorized();
        var session = await db.StreamSessions.AsNoTracking().SingleOrDefaultAsync(x => x.Id == sessionId, ct);
        if (session is null) return Results.NotFound();
        var canAccess = session.SourceDeviceId == device.Id
            || await db.SessionParticipants.AnyAsync(x => x.SessionId == sessionId && x.DeviceId == device.Id, ct);
        if (!canAccess) return Results.NotFound();

        var participants = await db.SessionParticipants.AsNoTracking()
            .Where(x => x.SessionId == sessionId)
            .OrderBy(x => x.JoinedAt)
            .Select(x => new
            {
                ParticipantId = x.Id,
                x.Role,
                x.Status,
                x.AudioSendAllowed,
                x.CanSendAudio,
                x.CanReceiveAudio,
                x.AudioMuted,
                x.JoinedAt,
                x.LeftAt,
                IsSelf = x.DeviceId == device.Id
            })
            .ToListAsync(ct);

        return Results.Ok(new { sessionId, mode = session.Mode, participants });
    }

    /// <summary>
    /// Grants or revokes one participant's permission to publish audio. Only the session's own
    /// device may call it, and only on a duplex session: a broadcast session is one-way by
    /// definition, so silently promoting a viewer there would make the mode meaningless.
    /// Revoking also clears the participant's declared <c>canSendAudio</c> and tells every
    /// connected peer, so they stop expecting audio from it instead of waiting for the offender
    /// to volunteer the change.
    /// </summary>
    private static async Task<IResult> SetAudioPermissionAsync(Guid sessionId, Guid participantId,
        AudioPermissionRequest request, ClaimsPrincipal principal, AppDbContext db, IConnectionRegistry registry,
        ILoggerFactory loggerFactory, CancellationToken ct)
    {
        var device = await DeviceIdentityEndpoints.RequireDeviceAsync(principal, db, ct);
        if (device is null) return Results.Unauthorized();
        var session = await db.StreamSessions
            .SingleOrDefaultAsync(x => x.Id == sessionId && x.SourceDeviceId == device.Id, ct);
        if (session is null) return Results.NotFound();
        if (session.Status is SessionStatuses.Ended or SessionStatuses.Expired)
            return Results.Conflict(new { error = "Session is no longer live.", code = "session_terminal" });
        if (session.Mode != SessionModes.Duplex)
            return Results.Conflict(new
            {
                error = "Audio publish permission can only be changed on a duplex session.",
                code = "session_not_duplex"
            });

        var participant = await db.SessionParticipants
            .SingleOrDefaultAsync(x => x.Id == participantId && x.SessionId == sessionId, ct);
        if (participant is null) return Results.NotFound();

        participant.AudioSendAllowed = request.CanSendAudio;
        if (!request.CanSendAudio) participant.CanSendAudio = false;
        await db.SaveChangesAsync(ct);

        await SignalingWebSocketEndpoint.BroadcastCapabilitiesAsync(registry, session, participant, ct);
        loggerFactory.CreateLogger("SonicRelay.Sessions").LogInformation(
            "Set audio publish permission to {AudioSendAllowed} for participant {ParticipantId} in session {SessionId}",
            request.CanSendAudio, participantId, sessionId);
        return Results.Ok(new
        {
            participantId = participant.Id,
            participant.Role,
            participant.AudioSendAllowed,
            participant.CanSendAudio,
            participant.CanReceiveAudio,
            participant.AudioMuted
        });
    }

    /// <summary>
    /// The one viewer participant a device owns in a session, or null. Deliberately
    /// <c>FirstOrDefault</c> over the oldest row rather than <c>SingleOrDefault</c>: the unique
    /// index makes duplicates impossible going forward, but rows written before it exists must
    /// not wedge rejoin on a 500 the device can never recover from on its own.
    /// </summary>
    private static Task<SessionParticipant?> FindViewerParticipantAsync(AppDbContext db, Guid sessionId,
        Guid deviceId, CancellationToken ct) =>
        db.SessionParticipants
            .Where(x => x.SessionId == sessionId && x.DeviceId == deviceId && x.Role == ParticipantRoles.Viewer)
            .OrderBy(x => x.JoinedAt)
            .FirstOrDefaultAsync(ct);

    /// <summary>
    /// Viewer slots in use, counted by distinct device. Counting rows instead would let a
    /// pre-existing duplicate consume a session's whole viewer budget and lock its own device
    /// out of the session it is trying to rejoin.
    /// </summary>
    private static async Task<int> CountDistinctActiveViewerDevicesAsync(AppDbContext db, Guid sessionId,
        CancellationToken ct) =>
        (await db.SessionParticipants
            .Where(x => x.SessionId == sessionId && x.Role == ParticipantRoles.Viewer
                && (x.Status == ParticipantStatuses.Connected || x.Status == ParticipantStatuses.Reconnecting))
            .Select(x => x.DeviceId)
            .Distinct()
            .ToListAsync(ct)).Count;

    private static IResult InvalidCode() =>
        Results.NotFound(new { error = "Invalid or expired session code.", code = "invalid_code" });

    private static IResult NotPaired() =>
        Results.Json(new
        {
            error = "This device is not paired with the publisher of that session.",
            code = "not_paired"
        }, statusCode: StatusCodes.Status403Forbidden);

    private static IResult DeviceTypeNotAllowed() =>
        Results.Json(new
        {
            error = "This session type is not available for this device.",
            code = "device_type_not_allowed"
        }, statusCode: StatusCodes.Status403Forbidden);

    private static Task<bool> HasActivePairingAsync(AppDbContext db,
        Guid publisherId, Guid viewerId, CancellationToken ct) =>
        db.DevicePairings.AsNoTracking().AnyAsync(x =>
            x.PublisherDeviceId == publisherId
            && x.ViewerDeviceId == viewerId
            && x.Status == DevicePairingStatuses.Active, ct);

    private static object ToResponse(StreamSession session, string? code = null) => new
    {
        session.Id,
        session.SourceDeviceId,
        session.Status,
        session.Mode,
        session.MaxViewers,
        session.CodeExpiresAt,
        session.StartedAt,
        session.EndedAt,
        session.CreatedAt,
        code
    };

    private static TimeSpan CodeTtl(IConfiguration configuration) =>
        TimeSpan.FromMinutes(configuration.GetValue("Sessions:CodeTtlMinutes", 10));

    private static string HashCode(string code, IConfiguration configuration)
    {
        var key = configuration["Sessions:CodeHmacKey"];
        if (string.IsNullOrWhiteSpace(key))
            throw new InvalidOperationException("Sessions:CodeHmacKey must be configured.");
        return Convert.ToHexString(HMACSHA256.HashData(Encoding.UTF8.GetBytes(key), Encoding.ASCII.GetBytes(code)));
    }

    private static string GenerateCode()
    {
        const string alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789";
        return string.Create(6, alphabet, static (span, chars) =>
        {
            for (var i = 0; i < span.Length; i++) span[i] = chars[RandomNumberGenerator.GetInt32(chars.Length)];
        });
    }

    // SourceDeviceId/DeviceId are no longer client-supplied: the caller's own device identity
    // (from the DeviceBearer token) is always the publisher of a created session and always the
    // viewer that joins, so there is nothing left for the client to assert about which device it is.
    private sealed record CreateSessionRequest(int? MaxViewers, string? Mode);
    private sealed record AudioPermissionRequest(bool CanSendAudio);
    private sealed record JoinSessionRequest(string Code);
}
