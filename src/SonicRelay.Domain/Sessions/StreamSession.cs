namespace SonicRelay.Domain.Sessions;

public sealed class StreamSession
{
    public Guid Id { get; set; }
    public Guid SourceDeviceId { get; set; }
    public string Status { get; set; } = SessionStatuses.Waiting;

    /// <summary>
    /// Whether audio flows one way (<see cref="SessionModes.Broadcast"/>) or both ways
    /// (<see cref="SessionModes.Duplex"/>). Chosen at creation and never changed afterwards:
    /// participants derive their audio permissions from it when they join, so flipping it
    /// mid-session would leave older participants with permissions from the previous mode.
    /// </summary>
    public string Mode { get; set; } = SessionModes.Broadcast;

    public int MaxViewers { get; set; }
    public DateTimeOffset CodeExpiresAt { get; set; }
    public DateTimeOffset? StartedAt { get; set; }
    public DateTimeOffset? EndedAt { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}

public sealed class SessionParticipant
{
    public Guid Id { get; set; }
    public Guid SessionId { get; set; }
    public Guid DeviceId { get; set; }
    public string Role { get; set; } = ParticipantRoles.Viewer;
    public string? ConnectionId { get; set; }
    public string Status { get; set; } = ParticipantStatuses.Connected;

    /// <summary>
    /// Server-side authorization to publish audio. Seeded from the session mode and the
    /// participant's role on join, and afterwards only the session owner can change it. A
    /// client can never raise it — <see cref="CanSendAudio"/> is clamped to this value — which
    /// is what makes "who may transmit" a backend decision rather than a client claim.
    /// </summary>
    public bool AudioSendAllowed { get; set; }

    /// <summary>
    /// Whether the participant currently intends to publish audio. Client-declared through the
    /// <c>participant.capabilities</c> signaling message and clamped to
    /// <see cref="AudioSendAllowed"/>.
    /// </summary>
    public bool CanSendAudio { get; set; }

    /// <summary>
    /// Whether the participant wants to receive audio. Client-declared; unlike sending, this
    /// carries no authorization weight — a participant choosing not to render remote audio is
    /// its own business.
    /// </summary>
    public bool CanReceiveAudio { get; set; } = true;

    /// <summary>
    /// Last mute state the participant announced through <c>participant.audio_state_changed</c>.
    /// Peers get the same value broadcast to them; the API never inspects the media itself.
    /// </summary>
    public bool AudioMuted { get; set; }

    public DateTimeOffset JoinedAt { get; set; }
    public DateTimeOffset? LeftAt { get; set; }
}

public static class SessionStatuses
{
    public const string Waiting = "waiting";
    public const string Active = "active";
    public const string Ended = "ended";
    public const string Expired = "expired";
}

public static class SessionModes
{
    /// <summary>One participant publishes audio and the others only receive it.</summary>
    public const string Broadcast = "broadcast";

    /// <summary>Every authorized participant may publish and receive audio on the same peer connection.</summary>
    public const string Duplex = "duplex";

    /// <summary>
    /// The source shares a screen (video) plus its system audio; the others only receive.
    /// Audio permissions match <see cref="Broadcast"/>; what the mode adds is a way for the
    /// backend and the clients to recognise a screen session without parsing SDP, which
    /// ADR 0001 forbids.
    /// </summary>
    public const string ScreenShare = "screen_share";

    public static bool IsSupported(string mode) => mode is Broadcast or Duplex or ScreenShare;

    /// <summary>
    /// Trims and lowercases a client-supplied mode, returning null when it is not a supported
    /// mode. A null or blank input normalizes to <see cref="Broadcast"/> so clients written
    /// before duplex existed keep creating one-way sessions.
    /// </summary>
    public static string? Normalize(string? mode)
    {
        if (string.IsNullOrWhiteSpace(mode)) return Broadcast;
        var normalized = mode.Trim().ToLowerInvariant();
        return IsSupported(normalized) ? normalized : null;
    }
}

/// <summary>
/// The audio permissions and defaults a participant starts with, derived from the session mode
/// and the participant's role. Kept in one place because both join paths, the signaling
/// endpoint and the public-room seeder all have to agree on them.
/// </summary>
public static class SessionAudioPolicy
{
    public readonly record struct AudioDefaults(bool SendAllowed, bool CanSendAudio, bool CanReceiveAudio);

    /// <summary>
    /// In broadcast the publisher transmits and viewers listen, exactly as before duplex
    /// existed. Screen sharing is one-way too, and says so explicitly rather than falling
    /// through to the broadcast branch — so a future change to one mode cannot silently
    /// change the other. In duplex every participant may do both; the session owner can
    /// still revoke an individual participant's send permission afterwards.
    /// </summary>
    public static AudioDefaults DefaultsFor(string sessionMode, string role) =>
        sessionMode == SessionModes.Duplex
            ? new AudioDefaults(SendAllowed: true, CanSendAudio: true, CanReceiveAudio: true)
            : role == ParticipantRoles.Publisher
                ? new AudioDefaults(SendAllowed: true, CanSendAudio: true, CanReceiveAudio: false)
                : new AudioDefaults(SendAllowed: false, CanSendAudio: false, CanReceiveAudio: true);

    public static void ApplyDefaults(SessionParticipant participant, string sessionMode)
    {
        var defaults = DefaultsFor(sessionMode, participant.Role);
        participant.AudioSendAllowed = defaults.SendAllowed;
        participant.CanSendAudio = defaults.CanSendAudio;
        participant.CanReceiveAudio = defaults.CanReceiveAudio;
        participant.AudioMuted = false;
    }
}

public static class ParticipantRoles
{
    public const string Publisher = "publisher";
    public const string Viewer = "viewer";
}

public static class ParticipantStatuses
{
    public const string Connected = "connected";
    public const string Disconnected = "disconnected";

    /// <summary>
    /// The participant's socket dropped but the reconnect grace period has not elapsed yet.
    /// The participant is still considered part of the session and can resume without
    /// creating a duplicate row.
    /// </summary>
    public const string Reconnecting = "reconnecting";
}
