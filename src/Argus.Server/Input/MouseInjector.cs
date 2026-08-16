using System.Runtime.InteropServices;
using Argus.Server.Interop;
using Argus.Server.Windows;

namespace Argus.Server.Input;

/// <summary>
/// Moves the real cursor and clicks with it.
///
/// SetCursorPos then SendInput, deliberately: the button events carry no coordinates of their own,
/// so they land wherever the cursor actually is. That means the pointer visibly jumps to the spot
/// before clicking - which is the honest behaviour here, because the click is real and whatever is
/// under it on the host receives it exactly as if you were sitting at the machine.
///
/// PostMessage-style synthetic clicks were not an option: the same apps that discard posted key
/// messages (Chrome, Electron, Windows Terminal) discard posted mouse messages too, and a click
/// that silently does nothing is worse than no click at all.
/// </summary>
public sealed class MouseInjector
{
    private readonly ForegroundInjector _foreground;
    private readonly ILogger<MouseInjector> _log;

    public MouseInjector(ForegroundInjector foreground, ILogger<MouseInjector> log)
    {
        _foreground = foreground;
        _log = log;
    }

    public InjectionResult Send(WindowInfo target, MouseEventDto mouseEvent)
    {
        var hwnd = (nint)target.Handle;
        if (!NativeMethods.IsWindow(hwnd)) return new InjectionResult(false, "mouse", "That window is gone");
        if (NativeMethods.IsIconic(hwnd)) return new InjectionResult(false, "mouse", "That window is minimised");

        // Raising the window first is what makes the click land on the app you are looking at
        // rather than on whatever is stacked above it at those screen coordinates.
        if (!_foreground.Focus(hwnd))
        {
            return new InjectionResult(false, "mouse", "Could not bring that window to the front");
        }

        if (!TryMapToScreen(hwnd, mouseEvent.X, mouseEvent.Y, out int x, out int y))
        {
            return new InjectionResult(false, "mouse", "Could not work out where that is on screen");
        }

        if (!NativeMethods.SetCursorPos(x, y))
        {
            return new InjectionResult(false, "mouse", "Windows refused to move the cursor");
        }

        var flags = ButtonFlags(mouseEvent.Action, mouseEvent.Button);
        if (flags.Length == 0) return new InjectionResult(true, "mouse");   // a plain move is done

        var inputs = flags.Select(flag => new NativeMethods.INPUT
        {
            Type = NativeMethods.INPUT_MOUSE,
            Union = new NativeMethods.InputUnion
            {
                Mouse = new NativeMethods.MOUSEINPUT { Flags = flag },
            },
        }).ToArray();

        uint sent = NativeMethods.SendInput(
            (uint)inputs.Length, inputs, Marshal.SizeOf<NativeMethods.INPUT>());

        if (sent != inputs.Length)
        {
            _log.LogWarning("SendInput delivered {Sent}/{Total} mouse events to {Title}",
                sent, inputs.Length, target.Title);
            return new InjectionResult(false, "mouse", "not delivered");
        }

        return new InjectionResult(true, "mouse");
    }

    private static uint[] ButtonFlags(MouseAction action, MouseButton button)
    {
        var (down, up) = button switch
        {
            MouseButton.Right => (NativeMethods.MOUSEEVENTF_RIGHTDOWN, NativeMethods.MOUSEEVENTF_RIGHTUP),
            MouseButton.Middle => (NativeMethods.MOUSEEVENTF_MIDDLEDOWN, NativeMethods.MOUSEEVENTF_MIDDLEUP),
            _ => (NativeMethods.MOUSEEVENTF_LEFTDOWN, NativeMethods.MOUSEEVENTF_LEFTUP),
        };

        return action switch
        {
            MouseAction.Down => [down],
            MouseAction.Up => [up],
            MouseAction.Click => [down, up],
            _ => [],
        };
    }

    /// <summary>
    /// Turns a 0..1 point on the frame into a screen pixel.
    ///
    /// The rect has to be the one the capture was cropped to, or every click lands offset by the
    /// invisible DWM resize border. This mirrors GdiWindowCaptureSource.CropToFrameBounds exactly,
    /// including when it rejects the DWM bounds and keeps the raw window rect.
    /// </summary>
    internal static bool TryMapToScreen(nint hwnd, double normalX, double normalY, out int x, out int y)
    {
        x = 0;
        y = 0;

        if (!NativeMethods.GetWindowRect(hwnd, out var windowRect)) return false;

        var rect = windowRect;
        if (NativeMethods.DwmGetWindowAttribute(
                hwnd, NativeMethods.DWMWA_EXTENDED_FRAME_BOUNDS,
                out NativeMethods.RECT frame, Marshal.SizeOf<NativeMethods.RECT>()) == 0)
        {
            int left = frame.Left - windowRect.Left;
            int top = frame.Top - windowRect.Top;

            bool usable = left >= 0
                && top >= 0
                && frame.Width > 0
                && frame.Height > 0
                && left + frame.Width <= windowRect.Width
                && top + frame.Height <= windowRect.Height;

            if (usable) rect = frame;
        }

        if (rect.Width <= 0 || rect.Height <= 0) return false;

        // Width - 1: a fraction of 1.0 is the last pixel inside the window, not the first one past
        // its right edge, which would click whatever is next door.
        x = rect.Left + (int)Math.Round(Math.Clamp(normalX, 0, 1) * (rect.Width - 1));
        y = rect.Top + (int)Math.Round(Math.Clamp(normalY, 0, 1) * (rect.Height - 1));
        return true;
    }
}
