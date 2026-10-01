using System.Net.WebSockets;
using System.Text.Json;

namespace SonicRelay.MediaRelay;

public static class MediaSocketEndpoint
{
    public static async Task<byte[]?> ReceiveAsync(WebSocket socket, WebSocketMessageType expected, int limit, CancellationToken ct)
    {
        using var data = new MemoryStream(); var chunk = new byte[16384];
        while (true)
        {
            var received = await socket.ReceiveAsync(chunk.AsMemory(), ct);
            if (received.MessageType == WebSocketMessageType.Close) return null;
            if (received.MessageType != expected || received.Count > limit - data.Length) throw new InvalidDataException("Invalid socket message.");
            data.Write(chunk, 0, received.Count);
            if (received.EndOfMessage) return data.ToArray();
        }
    }
    public static async Task HandleAsync(HttpContext context, RelayControlLeaseClient api, MediaRelayRegistry registry)
    {
        if (!context.WebSockets.IsWebSocketRequest || !context.WebSockets.WebSocketRequestedProtocols.Contains("framerelay-media-v1"))
        { context.Response.StatusCode = 400; return; }
        using var socket = await context.WebSockets.AcceptWebSocketAsync("framerelay-media-v1");
        var connectionId = Guid.NewGuid(); MediaLease? lease = null;
        try
        {
            using var handshake = CancellationTokenSource.CreateLinkedTokenSource(context.RequestAborted); handshake.CancelAfter(TimeSpan.FromSeconds(5));
            var bytes = await ReceiveAsync(socket, WebSocketMessageType.Text, 1024, handshake.Token);
            if (bytes is null) return;
            using var doc = JsonDocument.Parse(bytes);
            var grant = doc.RootElement.GetProperty("grant").GetString();
            if (grant is not { Length: 64 }) return;
            lease = await api.RedeemAsync(grant, connectionId, handshake.Token);
            if (lease is null || lease.ExpiresAt <= DateTimeOffset.UtcNow || lease.Role is not ("upload" or "view")) return;
            await registry.AttachAsync(lease, connectionId, socket, api, context.RequestAborted);
        }
        catch (Exception ex) when (ex is WebSocketException or OperationCanceledException or InvalidDataException
            or JsonException or KeyNotFoundException or HttpRequestException or InvalidOperationException) { }
        finally
        {
            socket.Abort();
            if (lease is not null)
            {
                using var release = new CancellationTokenSource(TimeSpan.FromSeconds(3));
                try { await api.ReleaseAsync(lease.LeaseId, connectionId, release.Token); }
                catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException) { }
            }
        }
    }
}
