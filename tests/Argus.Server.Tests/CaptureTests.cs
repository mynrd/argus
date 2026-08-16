using System.Drawing;
using System.Drawing.Imaging;
using Argus.Server.Capture;
using Argus.Server.Windows;
using Microsoft.Extensions.Logging.Abstractions;

namespace Argus.Server.Tests;

public class CaptureTests
{
    [Fact]
    public void Encode_produces_a_real_jpeg_at_the_preset_size()
    {
        using var source = SolidBitmap(1920, 1080, Color.CornflowerBlue);

        var frame = JpegEncoder.Encode(source, QualityPreset.Medium);

        Assert.Equal(1280, frame.Width);
        Assert.Equal(720, frame.Height);
        Assert.True(frame.Jpeg.Length > 0);
        // SOI marker - proves this is a JPEG and not an empty buffer.
        Assert.Equal(0xFF, frame.Jpeg[0]);
        Assert.Equal(0xD8, frame.Jpeg[1]);
    }

    [Fact]
    public void Lower_quality_produces_a_smaller_payload()
    {
        using var source = NoisyBitmap(800, 600);

        var preview = JpegEncoder.Encode(source, QualityPreset.Preview);
        var high = JpegEncoder.Encode(source, QualityPreset.High);

        Assert.True(preview.Jpeg.Length < high.Jpeg.Length,
            $"preview {preview.Jpeg.Length} was not smaller than high {high.Jpeg.Length}");
    }

    [Fact]
    public void Encoding_a_small_window_does_not_upscale_it()
    {
        using var source = SolidBitmap(320, 200, Color.Black);

        var frame = JpegEncoder.Encode(source, QualityPreset.High);

        Assert.Equal(320, frame.Width);
        Assert.Equal(200, frame.Height);
    }

    [Fact]
    public void A_black_bitmap_is_detected_as_blank()
    {
        using var black = SolidBitmap(200, 200, Color.Black);
        Assert.True(GdiWindowCaptureSource.IsEffectivelyBlank(black));
    }

    [Fact]
    public void A_bitmap_with_content_is_not_detected_as_blank()
    {
        using var painted = SolidBitmap(200, 200, Color.Black);
        using (var g = Graphics.FromImage(painted))
        {
            g.FillRectangle(Brushes.White, 40, 40, 120, 120);
        }

        Assert.False(GdiWindowCaptureSource.IsEffectivelyBlank(painted));
    }

    [Fact]
    public void Capturing_a_dead_handle_returns_nothing_rather_than_throwing()
    {
        using var source = new GdiWindowCaptureSource(0x7FFFFFFF, NullLogger.Instance);
        Assert.Null(source.TryCapture());
    }

    [Fact]
    public void Capturing_a_real_window_produces_a_bitmap()
    {
        var window = WindowEnumerator.Enumerate().FirstOrDefault(w => w.Width > 200 && w.Height > 200);
        if (window is null) return;   // headless machine

        using var source = new GdiWindowCaptureSource(window.Handle, NullLogger.Instance);
        using var bitmap = source.TryCapture();

        // A window can legitimately be un-capturable (minimised between enumerate and capture),
        // so only assert on the shape when something did come back.
        if (bitmap is null) return;

        Assert.True(bitmap.Width > 0);
        Assert.True(bitmap.Height > 0);

        var frame = JpegEncoder.Encode(bitmap, QualityPreset.Preview);
        Assert.True(frame.Jpeg.Length > 0);
        Assert.True(frame.Width <= QualityPreset.Preview.MaxWidth);
        Assert.True(frame.Height <= QualityPreset.Preview.MaxHeight);
    }

    private static Bitmap SolidBitmap(int width, int height, Color color)
    {
        var bitmap = new Bitmap(width, height, PixelFormat.Format32bppArgb);
        using var g = Graphics.FromImage(bitmap);
        g.Clear(color);
        return bitmap;
    }

    /// <summary>Noise, so the JPEG encoder cannot compress both tiers down to the same few bytes.</summary>
    private static Bitmap NoisyBitmap(int width, int height)
    {
        var bitmap = new Bitmap(width, height, PixelFormat.Format32bppArgb);
        var random = new Random(1234);
        for (int y = 0; y < height; y += 2)
        {
            for (int x = 0; x < width; x += 2)
            {
                bitmap.SetPixel(x, y, Color.FromArgb(random.Next(256), random.Next(256), random.Next(256)));
            }
        }
        return bitmap;
    }
}
