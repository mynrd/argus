using System.Runtime.InteropServices;
using Argus.Server.Interop;

namespace Argus.Server.Windows;

public static class WindowResizer
{
    private const int MinSide = 160;
    private const int MaxSide = 16384;

    /// <summary>
    /// Resizes a window to a requested *visible* size, leaving it where it is.
    ///
    /// The requested size is the size of the stream, not the size Windows reports. Those differ:
    /// GetWindowRect includes the invisible DWM resize border, so asking SetWindowPos for 1920x1080
    /// produces a frame around 1906x1075. The border is measured and added back, so the picture you
    /// end up watching really is the resolution you picked.
    /// </summary>
    public static (bool Ok, string? Reason) Resize(long handle, int width, int height)
    {
        var hwnd = (nint)handle;

        if (!NativeMethods.IsWindow(hwnd)) return (false, "That window is gone");
        if (width < MinSide || height < MinSide || width > MaxSide || height > MaxSide)
        {
            return (false, "That size is out of range");
        }

        // SetWindowPos on a maximised window is ignored - it stays maximised at the monitor's size.
        if (NativeMethods.IsIconic(hwnd) || NativeMethods.IsZoomed(hwnd))
        {
            NativeMethods.ShowWindow(hwnd, NativeMethods.SW_RESTORE);
        }

        var (padX, padY) = BorderPadding(hwnd);

        bool ok = NativeMethods.SetWindowPos(
            hwnd,
            nint.Zero,
            0, 0,
            width + padX,
            height + padY,
            NativeMethods.SWP_NOMOVE | NativeMethods.SWP_NOZORDER | NativeMethods.SWP_NOACTIVATE);

        // Apps are free to clamp this in WM_GETMINMAXINFO, so report what actually happened rather
        // than assuming the request was honoured.
        return ok ? (true, null) : (false, "Windows refused to resize that window");
    }

    /// <summary>How much bigger the window rect is than the visible frame, per axis.</summary>
    private static (int X, int Y) BorderPadding(nint hwnd)
    {
        if (!NativeMethods.GetWindowRect(hwnd, out var windowRect)) return (0, 0);

        if (NativeMethods.DwmGetWindowAttribute(
                hwnd, NativeMethods.DWMWA_EXTENDED_FRAME_BOUNDS,
                out NativeMethods.RECT frame, Marshal.SizeOf<NativeMethods.RECT>()) != 0)
        {
            return (0, 0);
        }

        if (frame.Width <= 0 || frame.Height <= 0) return (0, 0);

        return (
            Math.Max(0, windowRect.Width - frame.Width),
            Math.Max(0, windowRect.Height - frame.Height));
    }
}
