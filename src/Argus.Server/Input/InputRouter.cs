using Argus.Server.Windows;

namespace Argus.Server.Input;

public sealed record InjectionResult(bool Delivered, string Backend, string? Reason = null);

/// <summary>
/// Picks the injection backend for a window and mode. The choice matters a lot in practice:
/// the wrong backend fails silently rather than loudly, which is exactly what makes remote input
/// feel broken.
/// </summary>
public sealed class InputRouter
{
    private readonly WindowMessageInjector _postMessage;
    private readonly ForegroundInjector _foreground;
    private readonly ConsoleInputInjector _console;
    private readonly MouseInjector _mouse;
    private readonly ILogger<InputRouter> _log;

    public InputRouter(
        WindowMessageInjector postMessage,
        ForegroundInjector foreground,
        ConsoleInputInjector console,
        MouseInjector mouse,
        ILogger<InputRouter> log)
    {
        _postMessage = postMessage;
        _foreground = foreground;
        _console = console;
        _mouse = mouse;
        _log = log;
    }

    /// <summary>
    /// Human-readable reason background input cannot work for this window, or null if it can.
    /// </summary>
    public static string? BackgroundUnsupportedReason(WindowInfo target) =>
        target.SupportsBackgroundInput || target.IsConsole
            ? null
            : $"{target.ProcessName} ignores posted keyboard messages - switch to Focus mode to type into it.";

    public InjectionResult Send(WindowInfo target, KeyEventDto keyEvent, InjectionMode mode)
    {
        if (mode == InjectionMode.Background && BackgroundUnsupportedReason(target) is { } reason)
        {
            // Fail loudly rather than reporting the PostMessage queueing result as a delivery.
            return new InjectionResult(false, "unsupported", reason);
        }

        var injector = Select(target, mode);
        try
        {
            bool ok = injector.TrySend(target, keyEvent);
            if (!ok)
            {
                _log.LogDebug("{Backend} could not deliver {Code} to {Title}",
                    injector.Name, keyEvent.Code, target.Title);
            }
            return new InjectionResult(ok, injector.Name, ok ? null : "not delivered");
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "{Backend} threw delivering {Code} to {Title}",
                injector.Name, keyEvent.Code, target.Title);
            return new InjectionResult(false, injector.Name, ex.Message);
        }
    }

    /// <summary>
    /// Reports which backend would be used, so the UI can warn before the user types rather than
    /// after nothing happens.
    /// </summary>
    public string Describe(WindowInfo target, InjectionMode mode) => Select(target, mode).Name;

    /// <summary>Raises the window and hands it the desktop's keyboard focus.</summary>
    public bool Focus(WindowInfo target) => _foreground.Focus((nint)target.Handle);

    /// <summary>
    /// Mouse has no backend choice to make - there is exactly one way to click that every app
    /// honours, so this goes straight to the injector rather than through Select().
    /// </summary>
    public InjectionResult SendMouse(WindowInfo target, MouseEventDto mouseEvent)
    {
        try
        {
            return _mouse.Send(target, mouseEvent);
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Mouse injection threw for {Title}", target.Title);
            return new InjectionResult(false, "mouse", ex.Message);
        }
    }

    private IInputInjector Select(WindowInfo target, InjectionMode mode)
    {
        if (mode == InjectionMode.Focus) return _foreground;
        if (_console.CanHandle(target)) return _console;
        if (_postMessage.CanHandle(target)) return _postMessage;
        return _foreground;
    }

    public void Release(long handle) => _console.Release(handle);
}
