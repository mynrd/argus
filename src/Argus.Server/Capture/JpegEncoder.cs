using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;

namespace Argus.Server.Capture;

/// <summary>Scales a captured bitmap to a preset's box and encodes it as JPEG.</summary>
public static class JpegEncoder
{
    private static readonly ImageCodecInfo Codec =
        ImageCodecInfo.GetImageEncoders().First(c => c.MimeType == "image/jpeg");

    public static EncodedFrame Encode(Bitmap source, QualityPreset preset)
    {
        var (width, height) = preset.Fit(source.Width, source.Height);

        Bitmap? scaled = null;
        try
        {
            var toEncode = source;
            if (width != source.Width || height != source.Height)
            {
                scaled = new Bitmap(width, height, PixelFormat.Format24bppRgb);
                using var g = Graphics.FromImage(scaled);
                g.CompositingMode = CompositingMode.SourceCopy;
                g.CompositingQuality = CompositingQuality.HighSpeed;
                g.InterpolationMode = InterpolationMode.Bilinear;
                g.PixelOffsetMode = PixelOffsetMode.Half;
                g.SmoothingMode = SmoothingMode.None;
                g.DrawImage(source, new Rectangle(0, 0, width, height));
                toEncode = scaled;
            }

            // EncoderParameters is allocated per call rather than cached: GDI+ marshals it to
            // native memory inside Save, and sessions encode on their own threads.
            using var parameters = new EncoderParameters(1);
            parameters.Param[0] = new EncoderParameter(Encoder.Quality, (long)Math.Clamp(preset.JpegQuality, 1, 100));

            using var ms = new MemoryStream(capacity: 64 * 1024);
            toEncode.Save(ms, Codec, parameters);
            return new EncodedFrame(ms.ToArray(), width, height);
        }
        finally
        {
            scaled?.Dispose();
        }
    }
}

public readonly record struct EncodedFrame(byte[] Jpeg, int Width, int Height);
