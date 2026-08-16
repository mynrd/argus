using System.Diagnostics;
using Argus.Server.Interop;

namespace Argus.Server.Windows;

/// <summary>
/// Enumerates the top-level windows a user would recognise as "an open app".
/// </summary>
public static class WindowEnumerator
{
    /// <summary>
    /// UWP/system shell windows that are technically visible top-level windows but are never
    /// something the user means by "an app I want to watch".
    /// </summary>
    private static readonly HashSet<string> ExcludedClasses = new(StringComparer.OrdinalIgnoreCase)
    {
        "Progman",                       // desktop
        "WorkerW",                       // desktop wallpaper host
        "Shell_TrayWnd",                 // taskbar
        "Shell_SecondaryTrayWnd",
        "Windows.UI.Core.CoreWindow",    // suspended UWP shells
        "ApplicationFrameWindow_Hidden",
        "MultitaskingViewFrame",
        "ForegroundStaging",
        "XamlExplorerHostIslandWindow",
        "TaskListThumbnailWnd",
        "NarratorHelperWindow",
        "EdgeUiInputTopWndClass",
        "Xaml_WindowedPopupClass",
    };

    private static readonly HashSet<string> ConsoleClasses = new(StringComparer.OrdinalIgnoreCase)
    {
        "ConsoleWindowClass",   // classic conhost (powershell.exe, cmd.exe launched directly)
    };

    /// <summary>
    /// Window classes measured to swallow posted key messages. Both were verified by injecting and
    /// screenshotting: PostMessage reports success for every key and nothing reaches the app.
    ///   - Windows Terminal routes input through a XAML island into a ConPTY.
    ///   - Chromium (Chrome, Edge, Electron, VS Code) runs its own input pipeline; the focusable
    ///     HWND is an "Intermediate D3D Window" that does not translate key messages.
    /// </summary>
    private static readonly HashSet<string> NoBackgroundInputClasses = new(StringComparer.OrdinalIgnoreCase)
    {
        "CASCADIA_HOSTING_WINDOW_CLASS",
        "Chrome_WidgetWin_1",
    };

    /// <summary>Minimum size, in physical pixels, before a window is considered real.</summary>
    private const int MinDimension = 80;

    /// <summary>Whether a window of this class can be typed into without taking desktop focus.</summary>
    internal static bool SupportsBackgroundInput(string className) =>
        !NoBackgroundInputClasses.Contains(className);

    internal static bool IsConsoleClass(string className) => ConsoleClasses.Contains(className);

    public static IReadOnlyList<WindowInfo> Enumerate()
    {
        var results = new List<WindowInfo>();
        var shell = NativeMethods.GetShellWindow();

        NativeMethods.EnumWindows((hwnd, _) =>
        {
            if (TryDescribe(hwnd, shell, out var info))
            {
                results.Add(info);
            }
            return true;
        }, nint.Zero);

        return results
            .OrderBy(w => w.ProcessName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(w => w.Title, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>Re-reads a single window. Returns null if the handle is gone or no longer eligible.</summary>
    public static WindowInfo? Describe(long handle)
    {
        var hwnd = (nint)handle;
        return TryDescribe(hwnd, NativeMethods.GetShellWindow(), out var info) ? info : null;
    }

    /// <summary>
    /// Finds a window matching a previously saved <see cref="WindowInfo.MatchKey"/>. Used by the
    /// watchdog to re-attach after the target app is closed and reopened, and its HWND changes.
    /// Exact title match wins; otherwise falls back to any window of the same process.
    /// </summary>
    public static WindowInfo? FindByMatchKey(string matchKey, IReadOnlyList<WindowInfo>? candidates = null)
    {
        candidates ??= Enumerate();

        var exact = candidates.FirstOrDefault(w =>
            string.Equals(w.MatchKey, matchKey, StringComparison.OrdinalIgnoreCase));
        if (exact is not null) return exact;

        int sep = matchKey.IndexOf('|');
        if (sep <= 0) return null;
        var processName = matchKey[..sep];

        return candidates.FirstOrDefault(w =>
            string.Equals(w.ProcessName, processName, StringComparison.OrdinalIgnoreCase));
    }

    internal static bool TryDescribe(nint hwnd, nint shellWindow, out WindowInfo info)
    {
        info = null!;

        if (hwnd == nint.Zero || hwnd == shellWindow) return false;
        if (!NativeMethods.IsWindow(hwnd)) return false;
        if (!NativeMethods.IsWindowVisible(hwnd)) return false;

        // Only true top-level windows: skip children and owned popups/dialogs' owners duplicating.
        if (NativeMethods.GetAncestor(hwnd, NativeMethods.GA_ROOT) != hwnd) return false;

        var exStyle = (long)NativeMethods.GetWindowLongPtr(hwnd, NativeMethods.GWL_EXSTYLE);
        if ((exStyle & NativeMethods.WS_EX_TOOLWINDOW) != 0) return false;

        var style = (long)NativeMethods.GetWindowLongPtr(hwnd, NativeMethods.GWL_STYLE);
        if ((style & NativeMethods.WS_CHILD) != 0) return false;

        // DWM-cloaked windows are the big one: every suspended UWP app has a visible top-level
        // window that is invisible on screen. Without this the list is full of ghosts.
        if (NativeMethods.DwmGetWindowAttribute(hwnd, NativeMethods.DWMWA_CLOAKED, out int cloaked, sizeof(int)) == 0
            && cloaked != 0)
        {
            return false;
        }

        var title = NativeMethods.GetWindowTitle(hwnd);
        if (string.IsNullOrWhiteSpace(title)) return false;

        var className = NativeMethods.GetWindowClass(hwnd);
        if (ExcludedClasses.Contains(className)) return false;

        if (!NativeMethods.GetWindowRect(hwnd, out var rect)) return false;
        if (rect.Width < MinDimension || rect.Height < MinDimension) return false;

        NativeMethods.GetWindowThreadProcessId(hwnd, out uint pid);
        if (pid == 0) return false;

        info = new WindowInfo
        {
            Handle = hwnd,
            Title = title,
            ProcessName = SafeProcessName((int)pid),
            ProcessId = (int)pid,
            ClassName = className,
            Width = rect.Width,
            Height = rect.Height,
            IsConsole = IsConsoleClass(className),
            SupportsBackgroundInput = SupportsBackgroundInput(className),
        };
        return true;
    }

    private static string SafeProcessName(int pid)
    {
        try
        {
            using var p = Process.GetProcessById(pid);
            return p.ProcessName;
        }
        catch (ArgumentException)
        {
            return "unknown";      // process exited between EnumWindows and here
        }
        catch (InvalidOperationException)
        {
            return "unknown";
        }
    }
}
