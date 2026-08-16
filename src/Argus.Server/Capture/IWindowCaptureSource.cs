using System.Drawing;

namespace Argus.Server.Capture;

/// <summary>
/// One attached window's pixel source. Implementations are NOT thread-safe: each instance is
/// owned by exactly one <see cref="CaptureSession"/> loop thread.
/// </summary>
public interface IWindowCaptureSource : IDisposable
{
    /// <summary>Diagnostic name shown in the UI so it is obvious which backend is in play.</summary>
    string BackendName { get; }

    /// <summary>
    /// Grabs the current window contents. Returns null when the window is gone, minimised, or the
    /// backend produced nothing usable this tick. The returned bitmap is owned by the caller.
    /// </summary>
    Bitmap? TryCapture();
}
