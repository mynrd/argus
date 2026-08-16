namespace Argus.Server.Windows;

/// <summary>A top-level window that Argus can attach to.</summary>
public sealed record WindowInfo
{
    /// <summary>Native HWND. Not stable across app restarts - see <see cref="MatchKey"/>.</summary>
    public required long Handle { get; init; }

    public required string Title { get; init; }
    public required string ProcessName { get; init; }
    public required int ProcessId { get; init; }
    public required string ClassName { get; init; }
    public required int Width { get; init; }
    public required int Height { get; init; }

    /// <summary>True for classic conhost windows, which ignore posted key messages.</summary>
    public required bool IsConsole { get; init; }

    /// <summary>
    /// False when this window is known to discard posted key messages, so keyboard input needs
    /// <see cref="Input.InjectionMode.Focus"/>. PostMessage still returns success for these - it
    /// only reports that the message was queued - so without this flag the UI would report keys as
    /// delivered while nothing happens on screen.
    /// </summary>
    public required bool SupportsBackgroundInput { get; init; }

    /// <summary>
    /// Identity that survives the target app being closed and reopened. Used by the watchdog
    /// to re-attach, and by the settings screen to persist a selection across restarts.
    /// </summary>
    public string MatchKey => $"{ProcessName}|{Title}";

    public string Id => Handle.ToString();
}
