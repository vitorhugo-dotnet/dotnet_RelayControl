using System.Collections.Concurrent;
using Prometheus;

namespace SonicRelay.Api.Observability;

/// <summary>
/// SonicRelay's Prometheus metrics for WebRTC/TURN observability (issue #21). Exposed at
/// <c>/metrics</c>. Deliberately avoids high-cardinality labels: no session ids, IPs, SDP
/// or ICE candidates are ever used as label values — only bounded enums (message type,
/// transport mode, participant role, error reason).
/// </summary>
public sealed class SonicRelayMetrics
{
    // Refcount of live signaling connections per session so sessions_active reflects the
    // number of sessions with at least one connected participant, without a session-id label.
    private readonly ConcurrentDictionary<Guid, int> _sessionConnections = new();

    private readonly Counter _signalingMessages = Metrics.CreateCounter(
        "sonicrelay_signaling_messages_total",
        "Signaling messages handled, by type.",
        new CounterConfiguration { LabelNames = ["type"] });

    private readonly Counter _signalingErrors = Metrics.CreateCounter(
        "sonicrelay_signaling_errors_total",
        "Signaling errors returned to clients, by reason.",
        new CounterConfiguration { LabelNames = ["reason"] });

    private readonly Counter _iceRestarts = Metrics.CreateCounter(
        "sonicrelay_session_ice_restarts_total",
        "ICE restarts reported by clients.");

    private readonly Counter _transportMode = Metrics.CreateCounter(
        "sonicrelay_session_transport_mode_total",
        "Selected WebRTC transport mode reported by clients.",
        new CounterConfiguration { LabelNames = ["mode"] });

    private readonly Gauge _connectionsActive = Metrics.CreateGauge(
        "sonicrelay_signaling_connections_active",
        "Active signaling WebSocket connections.");

    private readonly Gauge _sessionsActive = Metrics.CreateGauge(
        "sonicrelay_sessions_active",
        "Sessions with at least one connected participant.");

    private readonly Histogram _packetLoss = Metrics.CreateHistogram(
        "sonicrelay_session_packet_loss_ratio",
        "Inbound audio packet loss ratio reported by clients (0..1).",
        new HistogramConfiguration
        {
            LabelNames = ["role"],
            Buckets = [0.001, 0.005, 0.01, 0.02, 0.05, 0.1, 0.2]
        });

    private readonly Histogram _jitterMs = Metrics.CreateHistogram(
        "sonicrelay_session_jitter_ms",
        "Inbound audio jitter in milliseconds reported by clients.",
        new HistogramConfiguration
        {
            LabelNames = ["role"],
            Buckets = [1, 5, 10, 20, 30, 50, 100, 200]
        });

    private readonly Histogram _rttMs = Metrics.CreateHistogram(
        "sonicrelay_session_rtt_ms",
        "WebRTC round-trip time in milliseconds reported by clients.",
        new HistogramConfiguration
        {
            LabelNames = ["role"],
            Buckets = [10, 25, 50, 100, 150, 200, 300, 500, 1000]
        });

    private readonly Counter _screenShareSessionsCreated = Metrics.CreateCounter(
        "sonicrelay_screen_share_sessions_created_total",
        "Screen-share sessions created.");

    private readonly Counter _screenShareJoinRejected = Metrics.CreateCounter(
        "sonicrelay_screen_share_join_rejected_total",
        "Join attempts refused on a screen-share session, by reason.",
        new CounterConfiguration { LabelNames = ["reason"] });

    private readonly Counter _screenShareAutoPairings = Metrics.CreateCounter(
        "sonicrelay_screen_share_auto_pairings_created_total",
        "Device pairings created implicitly by a screen-share join.");

    public void RecordMessage(string type) => _signalingMessages.WithLabels(Bounded(type)).Inc();

    public void RecordError(string reason) => _signalingErrors.WithLabels(Bounded(reason)).Inc();

    public void ScreenShareSessionCreated() => _screenShareSessionsCreated.Inc();

    /// <summary>Reason must be a bounded enum value — today only "device_type".</summary>
    public void ScreenShareJoinRejected(string reason) => _screenShareJoinRejected.WithLabels(reason).Inc();

    public void ScreenShareAutoPairingCreated() => _screenShareAutoPairings.Inc();

    /// <summary>Marks a signaling connection opened for a session.</summary>
    public void ConnectionOpened(Guid sessionId)
    {
        _connectionsActive.Inc();
        _sessionConnections.AddOrUpdate(sessionId, 1, (_, count) => count + 1);
        _sessionsActive.Set(_sessionConnections.Count);
    }

    /// <summary>Marks a signaling connection closed for a session.</summary>
    public void ConnectionClosed(Guid sessionId)
    {
        _connectionsActive.Dec();
        _sessionConnections.AddOrUpdate(sessionId, 0, (_, count) => count - 1);
        if (_sessionConnections.TryGetValue(sessionId, out var remaining) && remaining <= 0)
        {
            _sessionConnections.TryRemove(sessionId, out _);
        }
        _sessionsActive.Set(_sessionConnections.Count);
    }

    /// <summary>Records a client WebRTC stats snapshot into the histograms/counters.</summary>
    public void RecordStats(
        string role,
        string? transportMode,
        double? packetLossRatio,
        double? jitterMs,
        double? rttMs,
        bool iceRestarted)
    {
        var safeRole = role is "publisher" or "viewer" ? role : "unknown";
        if (!string.IsNullOrWhiteSpace(transportMode))
        {
            _transportMode.WithLabels(BoundedMode(transportMode)).Inc();
        }
        if (packetLossRatio is { } loss and >= 0)
        {
            _packetLoss.WithLabels(safeRole).Observe(loss);
        }
        if (jitterMs is { } jitter and >= 0)
        {
            _jitterMs.WithLabels(safeRole).Observe(jitter);
        }
        if (rttMs is { } rtt and >= 0)
        {
            _rttMs.WithLabels(safeRole).Observe(rtt);
        }
        if (iceRestarted)
        {
            _iceRestarts.Inc();
        }
    }

    // Guard against unbounded label cardinality from unexpected client input.
    private static string Bounded(string value) => string.IsNullOrWhiteSpace(value) ? "unknown" : value;

    private static string BoundedMode(string mode) => mode switch
    {
        "direct" or "stun" or "turn_udp" or "turn_tcp" or "turn_tls" => mode,
        _ => "unknown"
    };
}
