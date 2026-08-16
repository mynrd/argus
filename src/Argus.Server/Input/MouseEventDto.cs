namespace Argus.Server.Input;

public enum MouseAction
{
    Move = 0,
    Down = 1,
    Up = 2,
    /// <summary>Down and up in one go - what a tap on a phone sends.</summary>
    Click = 3,
}

public enum MouseButton
{
    Left = 0,
    Right = 1,
    Middle = 2,
}

/// <summary>
/// One mouse event from a viewer.
///
/// X and Y are fractions of the captured frame (0..1), not pixels. The viewer has no idea where the
/// window sits on the host's desktop, what DPI it is at, or how far the frame was downscaled for
/// its quality tier - and all three change while it is watching. Fractions survive all of that; the
/// server turns them into screen coordinates against the window's current bounds.
/// </summary>
public sealed class MouseEventDto
{
    public string WindowId { get; set; } = string.Empty;
    public MouseAction Action { get; set; }
    public MouseButton Button { get; set; }
    public double X { get; set; }
    public double Y { get; set; }
}
