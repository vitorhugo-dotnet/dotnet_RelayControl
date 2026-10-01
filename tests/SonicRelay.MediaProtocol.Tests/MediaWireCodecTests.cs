using SonicRelay.MediaProtocol;

namespace SonicRelay.MediaProtocol.Tests;

public sealed class MediaWireCodecTests
{
    [Theory]
    [InlineData(1)] [InlineData(2)] [InlineData(3)] [InlineData(4)]
    public void Roundtrip_matches_header(int type)
    {
        var payload = type == 4 ? Array.Empty<byte>() : new byte[] { 1, 2, 3 };
        var sample = new MediaMessage((byte)type, (ushort)(type == 2 ? 1 : 0), 1, 3, 1000000, 16667, payload);
        var wire = MediaWireCodec.Encode(sample);
        Assert.Equal("FRM1", System.Text.Encoding.ASCII.GetString(wire, 0, 4));
        Assert.Equal(payload.Length + 40, wire.Length);
        var decoded = MediaWireCodec.Decode(wire);
        Assert.Equal(sample.TimestampUs, decoded.TimestampUs);
        Assert.Equal(sample.Payload.ToArray(), decoded.Payload.ToArray());
    }

    [Theory]
    [InlineData(4, 2)] [InlineData(5, 9)] [InlineData(6, 2)] [InlineData(8, 0)] [InlineData(16, 9)] [InlineData(20, 1)]
    public void Rejects_invalid_headers(int offset, byte value)
    {
        var wire = MediaWireCodec.Encode(new MediaMessage(2, 1, 1, 0, 0, 1, new byte[] { 7 }));
        wire[offset] = value;
        Assert.Throws<InvalidDataException>(() => MediaWireCodec.Decode(wire));
    }

    [Fact]
    public void Rejects_unsafe_time_and_oversized_payload()
    {
        Assert.Throws<InvalidDataException>(() => MediaWireCodec.Decode(new byte[39]));
        Assert.Throws<InvalidDataException>(() => MediaWireCodec.Encode(new MediaMessage(2, 0, 1, 0, long.MaxValue, 0, Array.Empty<byte>())));
        Assert.Throws<InvalidDataException>(() => MediaWireCodec.Encode(new MediaMessage(2, 0, 1, 0, 0, 0, new byte[8 * 1024 * 1024])));
    }
    [Fact]
    public void Shared_vectors_decode_and_reencode_exact_bytes()
    {
        using var json = System.Text.Json.JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "discord-media-v1-vectors.json")));
        foreach (var vector in json.RootElement.EnumerateArray())
        {
            var wire = Convert.FromHexString(vector.GetProperty("hex").GetString()!);
            if (vector.TryGetProperty("invalid", out _)) Assert.Throws<InvalidDataException>(() => MediaWireCodec.Decode(wire));
            else Assert.Equal(wire, MediaWireCodec.Encode(MediaWireCodec.Decode(wire)));
        }
    }
}
