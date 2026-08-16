namespace Argus.Server.Input;

public enum KeyEventType
{
    Down,
    Up,
}

/// <summary>
/// One browser KeyboardEvent, forwarded verbatim. <c>Code</c> is the physical key
/// ("KeyA", "Enter", "ArrowLeft") and is what drives the VK mapping; <c>Key</c> is the produced
/// character ("a", "A", "!") and is what drives WM_CHAR for printable input.
/// </summary>
public sealed record KeyEventDto
{
    public required string WindowId { get; init; }
    public required KeyEventType Type { get; init; }
    public required string Code { get; init; }
    public required string Key { get; init; }
    public bool Ctrl { get; init; }
    public bool Shift { get; init; }
    public bool Alt { get; init; }
    public bool Meta { get; init; }

    /// <summary>True when this event should produce a literal character in the target.</summary>
    public bool IsPrintable =>
        !Ctrl && !Alt && !Meta
        && Key.Length == 1
        && !char.IsControl(Key[0]);
}

/// <summary>How keystrokes reach the target window.</summary>
public enum InjectionMode
{
    /// <summary>
    /// PostMessage / WriteConsoleInput. Does not steal desktop focus, so the machine stays usable,
    /// but modifier combos are unreliable - see <see cref="WindowMessageInjector"/>.
    /// </summary>
    Background,

    /// <summary>
    /// Brings the target to the foreground and uses SendInput. Fully faithful, including
    /// Ctrl/Alt combos, but it takes over the physical desktop while you type.
    /// </summary>
    Focus,
}
