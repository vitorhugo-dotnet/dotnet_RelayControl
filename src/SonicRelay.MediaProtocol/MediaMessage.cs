namespace SonicRelay.MediaProtocol;

public readonly record struct MediaMessage(byte Type, ushort Flags, uint Generation, uint Sequence,
    long TimestampUs, long DurationUs, ReadOnlyMemory<byte> Payload);
