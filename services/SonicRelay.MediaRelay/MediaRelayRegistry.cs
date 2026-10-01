using System.Net.WebSockets;
using System.Text.Json;
using SonicRelay.MediaProtocol;

namespace SonicRelay.MediaRelay;

public sealed class MediaRelayRegistry(IConfiguration configuration)
{
    private sealed class Connection(Guid id, WebSocket socket)
    {
        public readonly Guid Id = id;
        public readonly WebSocket Socket = socket;
        public readonly MediaConnectionQueue Queue = new();
        public bool AwaitKey = true;
        public DateTimeOffset RecoveryWindow;
        public int RecoveryFailures;
    }
    private sealed class Session
    {
        public Connection? Upload;
        public readonly Dictionary<Guid, Connection> Viewers = new();
        public MediaMessage? Config;
        public uint Generation, Sequence;
        public bool AwaitKey = true;
        public DateTimeOffset LastRequest;
    }
    private readonly object gate = new();
    private readonly Dictionary<Guid, Session> sessions = new();
    private int uploads, viewers;

    public async Task AttachAsync(MediaLease lease, Guid connectionId, WebSocket socket, RelayControlLeaseClient api, CancellationToken ct)
    {
        var connection = new Connection(connectionId, socket); Session session;
        lock (gate)
        {
            if (!sessions.TryGetValue(lease.SessionId, out session!)) sessions[lease.SessionId] = session = new();
            if (lease.Role == "upload")
            {
                if (session.Upload is not null || uploads >= configuration.GetValue("MediaRelay:MaxUploads", 64)) throw new InvalidOperationException("Upload limit.");
                session.Upload = connection; session.Config = null; session.Generation = 0; session.AwaitKey = true; uploads++;
                foreach (var viewer in session.Viewers.Values) { viewer.AwaitKey = true; viewer.Queue.Clear(); }
            }
            else
            {
                if (viewers >= configuration.GetValue("MediaRelay:MaxViewers", 256)) throw new InvalidOperationException("Viewer limit.");
                session.Viewers.Add(connectionId, connection); viewers++;
                if (session.Config is { } config) connection.Queue.TryEnqueue(config);
                RequestKey(session);
            }
        }
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(ct);
        try
        {
            var tasks = new[] { ReceiveLoopAsync(session, connection, lease.Role, lifetime.Token), SendLoopAsync(connection, lifetime.Token), LeaseLoopAsync(lease, connectionId, api, lifetime.Token) };
            await Task.WhenAny(tasks); lifetime.Cancel(); socket.Abort();
            try { await Task.WhenAll(tasks); } catch (Exception ex) when (ex is OperationCanceledException or WebSocketException or InvalidDataException or HttpRequestException or JsonException) { }
        }
        finally
        {
            lifetime.Cancel();
            lock (gate)
            {
                if (lease.Role == "upload" && session.Upload == connection)
                {
                    session.Upload = null; session.Config = null; uploads--;
                    foreach (var viewer in session.Viewers.Values) { viewer.AwaitKey = true; viewer.Queue.Clear(); }
                }
                else if (session.Viewers.Remove(connectionId)) viewers--;
                if (session.Upload is null && session.Viewers.Count == 0) sessions.Remove(lease.SessionId);
            }
        }
    }

    private static async Task LeaseLoopAsync(MediaLease initial, Guid id, RelayControlLeaseClient api, CancellationToken ct)
    {
        var lease = initial;
        while (true)
        {
            var remaining = lease.ExpiresAt - DateTimeOffset.UtcNow;
            if (remaining <= TimeSpan.Zero) return;
            await Task.Delay(remaining < TimeSpan.FromSeconds(10) ? remaining : TimeSpan.FromSeconds(10), ct);
            if (lease.ExpiresAt <= DateTimeOffset.UtcNow) return;
            using var renewal = CancellationTokenSource.CreateLinkedTokenSource(ct);
            renewal.CancelAfter(lease.ExpiresAt - DateTimeOffset.UtcNow);
            var next = await api.RenewAsync(lease.LeaseId, id, renewal.Token);
            if (next is null || next.LeaseId != lease.LeaseId || next.SessionId != lease.SessionId || next.Role != lease.Role) return;
            lease = next;
        }
    }

    private static async Task SendLoopAsync(Connection connection, CancellationToken ct)
    {
        while (true)
        {
            var message = await connection.Queue.ReadAsync(ct);
            using var send = CancellationTokenSource.CreateLinkedTokenSource(ct); send.CancelAfter(TimeSpan.FromSeconds(5));
            await connection.Socket.SendAsync(MediaWireCodec.Encode(message).AsMemory(), WebSocketMessageType.Binary, true, send.Token);
        }
    }

    private async Task ReceiveLoopAsync(Session session, Connection connection, string role, CancellationToken ct)
    {
        while (await MediaSocketEndpoint.ReceiveAsync(connection.Socket, WebSocketMessageType.Binary, MediaWireCodec.MaxMessageSize, ct) is { } bytes)
        {
            var message = MediaWireCodec.Decode(bytes);
            lock (gate)
            {
                if (role == "view")
                {
                    if (message.Type != 4) throw new InvalidDataException("Viewer can only request keyframes.");
                    connection.AwaitKey = true; connection.Queue.Clear();
                    if (session.Config is { } config) connection.Queue.TryEnqueue(config);
                    RequestKey(session); continue;
                }
                if (message.Type == 4) throw new InvalidDataException("Invalid upload control.");
                if (message.Type == 1)
                {
                    if (message.Generation <= session.Generation || message.Sequence != 0) throw new InvalidDataException("Invalid configuration generation.");
                    ValidateConfig(message.Payload);
                    session.Config = message; session.Generation = message.Generation; session.Sequence = 0; session.AwaitKey = true;
                    foreach (var viewer in session.Viewers.Values) { viewer.Queue.Clear(); viewer.AwaitKey = true; viewer.Queue.TryEnqueue(message); }
                    RequestKey(session); continue;
                }
                if (session.Config is null || message.Generation != session.Generation || message.Sequence <= session.Sequence)
                    throw new InvalidDataException("Invalid media generation or sequence.");
                if (message.Sequence != session.Sequence + 1)
                { session.AwaitKey = true; foreach (var viewer in session.Viewers.Values) viewer.AwaitKey = true; RequestKey(session); }
                session.Sequence = message.Sequence;
                var key = message.Type == 2 && message.Flags == 1;
                if (session.AwaitKey && !key) continue;
                if (key) session.AwaitKey = false;
                foreach (var viewer in session.Viewers.Values)
                {
                    if (viewer.AwaitKey && !key) continue;
                    if (key) viewer.AwaitKey = false;
                    if (!viewer.Queue.TryEnqueue(message))
                    {
                        var now = DateTimeOffset.UtcNow;
                        if (now - viewer.RecoveryWindow > TimeSpan.FromSeconds(10))
                        { viewer.RecoveryWindow = now; viewer.RecoveryFailures = 0; }
                        if (++viewer.RecoveryFailures >= 3)
                        { viewer.Socket.Abort(); continue; }
                        viewer.Queue.Clear(); viewer.AwaitKey = true;
                        viewer.Queue.TryEnqueue(session.Config.Value); RequestKey(session);
                    }
                }
            }
        }
    }

    private static void ValidateConfig(ReadOnlyMemory<byte> bytes)
    {
        using var json = JsonDocument.Parse(bytes); var video = json.RootElement.GetProperty("video");
        var codec = video.GetProperty("codec").GetString();
        if (codec is null || !System.Text.RegularExpressions.Regex.IsMatch(codec, "^avc1\\.[a-fA-F0-9]{6}$")
            || video.GetProperty("format").GetString() != "annexb"
            || video.GetProperty("width").GetInt32() is < 1 or > 8192 || video.GetProperty("height").GetInt32() is < 1 or > 8192)
            throw new InvalidDataException("Invalid video configuration.");
        var audio = json.RootElement.GetProperty("audio");
        if (audio.ValueKind != JsonValueKind.Null && (audio.GetProperty("codec").GetString() != "opus"
            || audio.GetProperty("sampleRate").GetInt32() != 48000 || audio.GetProperty("channels").GetInt32() is < 1 or > 2))
            throw new InvalidDataException("Invalid audio configuration.");
    }

    private static void RequestKey(Session session)
    {
        if (session.Upload is null || DateTimeOffset.UtcNow - session.LastRequest < TimeSpan.FromSeconds(1)) return;
        session.LastRequest = DateTimeOffset.UtcNow;
        if (!session.Upload.Queue.TryEnqueue(new MediaMessage(4, 0, Math.Max(1, session.Generation), 0, 0, 0, ReadOnlyMemory<byte>.Empty))) session.Upload.Socket.Abort();
    }
}
