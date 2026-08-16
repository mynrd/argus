using System.Buffers.Binary;
using Argus.Server.Capture;

namespace Argus.Server.Streaming;

/// <summary>
/// Wire format for binary frames on /ws/frames. A fixed 16-byte header followed by raw JPEG.
///
/// Frames deliberately do not travel over SignalR: its JSON protocol base64-encodes byte[], which
/// would add ~33% to every frame of every tile, and the MessagePack protocol pulls in a package
/// with open advisories. A dedicated socket also keeps frame delivery off the control channel, so
/// a burst of frames cannot delay a keypress.
///
///   offset 0  int64   window handle
///   offset 8  int32   sequence number (per window, wraps)
///   offset 12 uint16  frame width
///   offset 14 uint16  frame height
///   offset 16 ...     JPEG bytes
/// </summary>
public static class FrameProtocol
{
    public const int HeaderSize = 16;

    public static byte[] Encode(long windowHandle, int sequence, EncodedFrame frame)
    {
        var buffer = new byte[HeaderSize + frame.Jpeg.Length];
        var span = buffer.AsSpan();

        BinaryPrimitives.WriteInt64LittleEndian(span[..8], windowHandle);
        BinaryPrimitives.WriteInt32LittleEndian(span.Slice(8, 4), sequence);
        BinaryPrimitives.WriteUInt16LittleEndian(span.Slice(12, 2), (ushort)Math.Clamp(frame.Width, 0, ushort.MaxValue));
        BinaryPrimitives.WriteUInt16LittleEndian(span.Slice(14, 2), (ushort)Math.Clamp(frame.Height, 0, ushort.MaxValue));

        frame.Jpeg.CopyTo(span[HeaderSize..]);
        return buffer;
    }
}
