namespace Argus.Server.Capture;

public enum WindowStatus
{
    /// <summary>Attached, capture loop starting, no frame produced yet.</summary>
    Starting,

    /// <summary>Frames are being produced at roughly the requested rate.</summary>
    Streaming,

    /// <summary>Attached and the window still exists, but no frame has arrived recently.</summary>
    Stalled,

    /// <summary>Window is minimised. Nothing can be captured until it is restored.</summary>
    Minimised,

    /// <summary>The window is gone. The watchdog keeps looking for a replacement.</summary>
    Closed,
}

/// <summary>Live per-window state pushed to the dashboard.</summary>
public sealed record WindowStatusUpdate
{
    public required string WindowId { get; init; }
    public required long Handle { get; init; }
    public required string Title { get; init; }
    public required string ProcessName { get; init; }
    public required WindowStatus Status { get; init; }
    public required string Backend { get; init; }
    public required double ActualFps { get; init; }
    public required int Subscribers { get; init; }
    public required bool SupportsBackgroundInput { get; init; }
    public string? Note { get; init; }
}
