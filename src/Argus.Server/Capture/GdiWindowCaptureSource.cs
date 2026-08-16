using System.Drawing;
using System.Drawing.Imaging;
using Argus.Server.Interop;

namespace Argus.Server.Capture;

/// <summary>
/// Pure-GDI window capture. Zero dependencies and works for classic Win32 / GDI apps
/// (Notepad++, conhost, WinForms/WPF) including when the window is behind others.
///
/// Known limits, which is why <see cref="GraphicsCaptureSource"/> exists:
///   - Some GPU-composited windows (Windows Terminal, some Electron builds) render black.
///   - Minimised windows cannot be captured at all; the last good frame is reused by the session.
/// The all-black probe below detects the first case and reports it so the session can switch.
/// </summary>
public sealed class GdiWindowCaptureSource : IWindowCaptureSource
{
    private enum Strategy
    {
        PrintWindowFullContent,
        PrintWindowPlain,
        BitBltWindowDc,
    }

    private readonly nint _hwnd;
    private readonly ILogger _log;
    private Strategy _strategy = Strategy.PrintWindowFullContent;
    private int _probesRemaining = 3;   // re-probe a few times: some apps are black only while loading

    public GdiWindowCaptureSource(long handle, ILogger log)
    {
        _hwnd = (nint)handle;
        _log = log;
    }

    public string BackendName => $"gdi/{_strategy}";

    /// <summary>Set once the GDI path has been proven to only ever produce blank frames.</summary>
    public bool ProducesBlankFrames { get; private set; }

    public Bitmap? TryCapture()
    {
        if (!NativeMethods.IsWindow(_hwnd)) return null;
        if (NativeMethods.IsIconic(_hwnd)) return null;
        if (!NativeMethods.GetWindowRect(_hwnd, out var windowRect)) return null;

        int width = windowRect.Width;
        int height = windowRect.Height;
        if (width <= 0 || height <= 0) return null;

        // Guard against absurd sizes from a window mid-resize.
        if (width > 16384 || height > 16384) return null;

        Bitmap? bmp = null;
        try
        {
            bmp = new Bitmap(width, height, PixelFormat.Format32bppArgb);
            if (!Render(bmp, width, height))
            {
                bmp.Dispose();
                return null;
            }

            var cropped = CropToFrameBounds(bmp, windowRect);
            if (!ReferenceEquals(cropped, bmp)) bmp.Dispose();
            bmp = cropped;

            if (_probesRemaining > 0)
            {
                _probesRemaining--;
                if (IsEffectivelyBlank(bmp))
                {
                    if (!Escalate())
                    {
                        ProducesBlankFrames = true;
                        _log.LogWarning(
                            "GDI capture for HWND {Hwnd} is blank on every strategy; needs the WGC backend.",
                            _hwnd);
                    }
                }
                else
                {
                    _probesRemaining = 0;   // this strategy works, stop probing
                }
            }

            return bmp;
        }
        catch (Exception ex) when (ex is ArgumentException or OutOfMemoryException or InvalidOperationException)
        {
            // GDI+ throws OutOfMemoryException for plain invalid-parameter cases too.
            bmp?.Dispose();
            _log.LogDebug(ex, "GDI capture failed for HWND {Hwnd}", _hwnd);
            return null;
        }
    }

    private bool Render(Bitmap target, int width, int height)
    {
        using var g = Graphics.FromImage(target);
        nint hdc = g.GetHdc();
        try
        {
            return _strategy switch
            {
                Strategy.PrintWindowFullContent =>
                    NativeMethods.PrintWindow(_hwnd, hdc, NativeMethods.PW_RENDERFULLCONTENT),
                Strategy.PrintWindowPlain =>
                    NativeMethods.PrintWindow(_hwnd, hdc, 0),
                Strategy.BitBltWindowDc =>
                    BlitFromWindowDc(hdc, width, height),
                _ => false,
            };
        }
        finally
        {
            g.ReleaseHdc(hdc);
        }
    }

    private bool BlitFromWindowDc(nint destDc, int width, int height)
    {
        nint srcDc = NativeMethods.GetWindowDC(_hwnd);
        if (srcDc == nint.Zero) return false;
        try
        {
            return NativeMethods.BitBlt(destDc, 0, 0, width, height, srcDc, 0, 0,
                                        NativeMethods.SRCCOPY | NativeMethods.CAPTUREBLT);
        }
        finally
        {
            NativeMethods.ReleaseDC(_hwnd, srcDc);
        }
    }

    private bool Escalate()
    {
        switch (_strategy)
        {
            case Strategy.PrintWindowFullContent:
                _strategy = Strategy.PrintWindowPlain;
                _probesRemaining = 2;
                return true;
            case Strategy.PrintWindowPlain:
                _strategy = Strategy.BitBltWindowDc;
                _probesRemaining = 2;
                return true;
            default:
                return false;
        }
    }

    /// <summary>
    /// GetWindowRect includes the invisible DWM resize border (roughly 7px each side on Win10+),
    /// so a raw capture has dead margins. DWMWA_EXTENDED_FRAME_BOUNDS gives the visible frame.
    /// </summary>
    private Bitmap CropToFrameBounds(Bitmap source, NativeMethods.RECT windowRect)
    {
        if (NativeMethods.DwmGetWindowAttribute(
                _hwnd, NativeMethods.DWMWA_EXTENDED_FRAME_BOUNDS,
                out NativeMethods.RECT frame, System.Runtime.InteropServices.Marshal.SizeOf<NativeMethods.RECT>()) != 0)
        {
            return source;
        }

        int left = frame.Left - windowRect.Left;
        int top = frame.Top - windowRect.Top;
        int width = frame.Width;
        int height = frame.Height;

        if (left < 0 || top < 0 || width <= 0 || height <= 0) return source;
        if (left + width > source.Width || top + height > source.Height) return source;
        if (left == 0 && top == 0 && width == source.Width && height == source.Height) return source;

        var cropped = new Bitmap(width, height, PixelFormat.Format32bppArgb);
        using var g = Graphics.FromImage(cropped);
        g.DrawImage(source, new Rectangle(0, 0, width, height),
                    new Rectangle(left, top, width, height), GraphicsUnit.Pixel);
        return cropped;
    }

    /// <summary>
    /// Samples a coarse grid rather than every pixel - this runs on the capture thread and a full
    /// scan of a 4K frame would cost more than the capture itself.
    /// </summary>
    internal static bool IsEffectivelyBlank(Bitmap bmp)
    {
        const int Steps = 16;
        var data = bmp.LockBits(new Rectangle(0, 0, bmp.Width, bmp.Height),
                                ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        try
        {
            unsafe
            {
                byte* scan0 = (byte*)data.Scan0;
                int stepX = Math.Max(1, bmp.Width / Steps);
                int stepY = Math.Max(1, bmp.Height / Steps);

                for (int y = 0; y < bmp.Height; y += stepY)
                {
                    byte* row = scan0 + (long)y * data.Stride;
                    for (int x = 0; x < bmp.Width; x += stepX)
                    {
                        byte* px = row + (long)x * 4;
                        // Any non-black BGR value means the backend produced real content.
                        if (px[0] > 8 || px[1] > 8 || px[2] > 8) return false;
                    }
                }
            }
            return true;
        }
        finally
        {
            bmp.UnlockBits(data);
        }
    }

    public void Dispose()
    {
        // Nothing retained: every capture allocates and hands off its own bitmap.
    }
}
