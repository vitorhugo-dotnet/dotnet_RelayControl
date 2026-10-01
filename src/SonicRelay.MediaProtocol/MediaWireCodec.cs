using System.Buffers.Binary;

namespace SonicRelay.MediaProtocol;

public static class MediaWireCodec
{
    public const int HeaderSize = 40;
    public const int MaxMessageSize = 8 * 1024 * 1024;
    public const long MaxSafeInteger = 9007199254740991;

    public static byte[] Encode(MediaMessage message)
    {
        Validate(message);
        var bytes = new byte[HeaderSize + message.Payload.Length];
        "FRM1"u8.CopyTo(bytes); bytes[4] = 1; bytes[5] = message.Type;
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(6), message.Flags);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(8), message.Generation);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(12), message.Sequence);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(16), (uint)message.Payload.Length);
        BinaryPrimitives.WriteInt64LittleEndian(bytes.AsSpan(24), message.TimestampUs);
        BinaryPrimitives.WriteInt64LittleEndian(bytes.AsSpan(32), message.DurationUs);
        message.Payload.Span.CopyTo(bytes.AsSpan(HeaderSize));
        return bytes;
    }

    public static MediaMessage Decode(ReadOnlyMemory<byte> bytes)
    {
        var s = bytes.Span;
        if (s.Length < HeaderSize || s.Length > MaxMessageSize || !s[..4].SequenceEqual("FRM1"u8)
            || s[4] != 1 || BinaryPrimitives.ReadUInt32LittleEndian(s[20..]) != 0
            || BinaryPrimitives.ReadUInt32LittleEndian(s[16..]) != s.Length - HeaderSize)
            throw new InvalidDataException("Invalid media header.");
        var message = new MediaMessage(s[5], BinaryPrimitives.ReadUInt16LittleEndian(s[6..]),
            BinaryPrimitives.ReadUInt32LittleEndian(s[8..]), BinaryPrimitives.ReadUInt32LittleEndian(s[12..]),
            BinaryPrimitives.ReadInt64LittleEndian(s[24..]), BinaryPrimitives.ReadInt64LittleEndian(s[32..]), bytes[HeaderSize..]);
        Validate(message); return message;
    }

    private static void Validate(MediaMessage message)
    {
        if (message.Type is < 1 or > 4 || message.Generation == 0 || message.Flags > 1
            || (message.Flags != 0 && message.Type != 2)
            || message.TimestampUs is < 0 or > MaxSafeInteger || message.DurationUs is < 0 or > MaxSafeInteger
            || message.Payload.Length > MaxMessageSize - HeaderSize || (message.Type == 4 && message.Payload.Length != 0))
            throw new InvalidDataException("Invalid media message.");
    }
}
