using System.Diagnostics;
using Argus.Server.Interop;

namespace Argus.Server.Windows;

/// <summary>
/// Shuts down an app on the host, either politely or not.
///
/// The two are genuinely different operations, not a retry of each other. Close asks; the app can
/// refuse, or put up a "save changes?" prompt that then sits on the host desktop waiting for an
/// answer. Kill does not ask and unsaved work is gone.
/// </summary>
public static class WindowCloser
{
    /// <summary>Exactly what clicking the window's X does - the app decides what happens next.</summary>
    public static (bool Ok, string? Reason) Close(long handle)
    {
        var hwnd = (nint)handle;
        if (!NativeMethods.IsWindow(hwnd)) return (false, "That window is already gone");

        if (!NativeMethods.PostMessage(hwnd, NativeMethods.WM_CLOSE, nint.Zero, nint.Zero))
        {
            return (false, "Windows would not deliver the close request");
        }

        // Posted, not acted on: an app with unsaved work will still be on screen with a prompt.
        return (true, null);
    }

    /// <summary>
    /// Terminates the process behind the window, and its children with it - a browser or an editor
    /// spawns helpers that would otherwise be left running with no window to reach them by.
    /// </summary>
    public static (bool Ok, string? Reason) Kill(long handle)
    {
        var hwnd = (nint)handle;
        if (!NativeMethods.IsWindow(hwnd)) return (false, "That window is already gone");

        NativeMethods.GetWindowThreadProcessId(hwnd, out uint pid);
        if (pid == 0) return (false, "Could not find the process for that window");

        // The window hosting Argus is in its own list, and killing it would take the server down
        // mid-request with no way to restart it from the browser.
        if (pid == Environment.ProcessId) return (false, "That is Argus itself");

        try
        {
            using var process = Process.GetProcessById((int)pid);
            process.Kill(entireProcessTree: true);
            return (true, null);
        }
        catch (ArgumentException)
        {
            return (true, null);   // exited between the lookup and the kill - the goal is met
        }
        catch (Exception ex)
        {
            return (false, ex.Message);
        }
    }
}
