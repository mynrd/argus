using System.Buffers.Binary;
using Argus.Server.Capture;
using Argus.Server.Streaming;

namespace Argus.Server.Tests;

public class FrameProtocolTests
{
    [Fact]
    public void Encode_writes_the_documented_header_layout()
    {
        var jpeg = new byte[] { 0xFF, 0xD8, 0xFF, 0xE0, 0x01, 0x02 };
        var payload = FrameProtocol.Encode(0x1234_5678L, sequence: 42, new EncodedFrame(jpeg, 640, 480));

        Assert.Equal(FrameProtocol.HeaderSize + jpeg.Length, payload.Length);
        Assert.Equal(0x1234_5678L, BinaryPrimitives.ReadInt64LittleEndian(payload.AsSpan(0, 8)));
        Assert.Equal(42, BinaryPrimitives.ReadInt32LittleEndian(payload.AsSpan(8, 4)));
        Assert.Equal(640, BinaryPrimitives.ReadUInt16LittleEndian(payload.AsSpan(12, 2)));
        Assert.Equal(480, BinaryPrimitives.ReadUInt16LittleEndian(payload.AsSpan(14, 2)));
        Assert.Equal(jpeg, payload[FrameProtocol.HeaderSize..]);
    }

    [Fact]
    public void Encode_round_trips_the_way_the_browser_reads_it()
    {
        // Mirrors ArgusService.dispatchFrame - if this drifts, tiles render against the wrong window.
        var jpeg = new byte[] { 1, 2, 3, 4, 5 };
        var payload = FrameProtocol.Encode(987654321L, 7, new EncodedFrame(jpeg, 1280, 720));

        long handle = BinaryPrimitives.ReadInt64LittleEndian(payload.AsSpan(0, 8));
        int sequence = BinaryPrimitives.ReadInt32LittleEndian(payload.AsSpan(8, 4));
        int width = BinaryPrimitives.ReadUInt16LittleEndian(payload.AsSpan(12, 2));
        int height = BinaryPrimitives.ReadUInt16LittleEndian(payload.AsSpan(14, 2));

        Assert.Equal(987654321L, handle);
        Assert.Equal(7, sequence);
        Assert.Equal(1280, width);
        Assert.Equal(720, height);
    }

    [Fact]
    public void Encode_clamps_dimensions_that_do_not_fit_the_header()
    {
        // The header stores dimensions as uint16; an absurd value must not corrupt later fields.
        var payload = FrameProtocol.Encode(1, 1, new EncodedFrame([0xFF], 100_000, -5));

        Assert.Equal(ushort.MaxValue, BinaryPrimitives.ReadUInt16LittleEndian(payload.AsSpan(12, 2)));
        Assert.Equal(0, BinaryPrimitives.ReadUInt16LittleEndian(payload.AsSpan(14, 2)));
    }

    [Fact]
    public void Encode_handles_an_empty_payload()
    {
        var payload = FrameProtocol.Encode(5, 5, new EncodedFrame([], 0, 0));
        Assert.Equal(FrameProtocol.HeaderSize, payload.Length);
    }
}
