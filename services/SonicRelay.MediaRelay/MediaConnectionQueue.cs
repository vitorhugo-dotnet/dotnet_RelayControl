using System.Threading.Channels;
using SonicRelay.MediaProtocol;

namespace SonicRelay.MediaRelay;

public sealed class MediaConnectionQueue
{
    private readonly Channel<MediaMessage> channel = Channel.CreateUnbounded<MediaMessage>(new UnboundedChannelOptions { SingleReader = true });
    private readonly object gate = new();
    private int count, bytes;
    public bool TryEnqueue(MediaMessage message)
    {
        lock (gate)
        {
            var size = MediaWireCodec.HeaderSize + message.Payload.Length;
            if (count >= 64 || size + bytes > 16 * 1024 * 1024) return false;
            if (!channel.Writer.TryWrite(message)) return false;
            count++; bytes += size; return true;
        }
    }
    public async ValueTask<MediaMessage> ReadAsync(CancellationToken ct)
    {
        while (await channel.Reader.WaitToReadAsync(ct))
        {
            lock (gate)
            {
                if (!channel.Reader.TryRead(out var message)) continue;
                count--; bytes -= MediaWireCodec.HeaderSize + message.Payload.Length; return message;
            }
        }
        throw new OperationCanceledException(ct);
    }
    public void Clear()
    {
        lock (gate) { while (channel.Reader.TryRead(out _)) { } count = bytes = 0; }
    }
}
