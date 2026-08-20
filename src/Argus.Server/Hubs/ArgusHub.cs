using Argus.Server.Capture;
using Argus.Server.Input;
using Argus.Server.Services;
using Argus.Server.Windows;
using Microsoft.AspNetCore.SignalR;

namespace Argus.Server.Hubs;

/// <summary>
/// Control channel. Carries the window list, attach/detach, subscriptions, quality changes,
/// status pushes and keystrokes - everything except the frames themselves, which go over
/// /ws/frames as raw binary.
/// </summary>
public sealed class ArgusHub : Hub
{
    private readonly CaptureManager _capture;
    private readonly InputRouter _input;
    private readonly SelectionStore _selection;
    private readonly ApplicationLauncher _launcher;
    private readonly OpenWithLauncher _openWith;
    private readonly ILogger<ArgusHub> _log;

    public ArgusHub(
        CaptureManager capture,
        InputRouter input,
        SelectionStore selection,
        ApplicationLauncher launcher,
        OpenWithLauncher openWith,
        ILogger<ArgusHub> log)
    {
        _capture = capture;
        _input = input;
        _selection = selection;
        _launcher = launcher;
        _openWith = openWith;
        _log = log;
    }

    public override async Task OnConnectedAsync()
    {
        _log.LogInformation("Viewer {ConnectionId} connected", Context.ConnectionId);
        await Clients.Caller.SendAsync("Hello", new
        {
            clientId = Context.ConnectionId,
            statuses = _capture.Sessions.Select(s => s.Snapshot()),
        });
        await base.OnConnectedAsync();
    }

    public override Task OnDisconnectedAsync(Exception? exception)
    {
        _log.LogInformation("Viewer {ConnectionId} disconnected", Context.ConnectionId);
        _capture.UnsubscribeAll(Context.ConnectionId);
        return base.OnDisconnectedAsync(exception);
    }

    /// <summary>Every window Argus could attach to, plus whether it is currently selected.</summary>
    public IEnumerable<object> ListWindows()
    {
        var attached = _capture.Sessions.Select(s => s.MatchKey).ToHashSet(StringComparer.OrdinalIgnoreCase);

        return WindowEnumerator.Enumerate().Select(w => new
        {
            windowId = w.Id,
            handle = w.Handle,
            title = w.Title,
            processName = w.ProcessName,
            className = w.ClassName,
            width = w.Width,
            height = w.Height,
            isConsole = w.IsConsole,
            supportsBackgroundInput = w.SupportsBackgroundInput,
            backgroundInputNote = InputRouter.BackgroundUnsupportedReason(w),
            matchKey = w.MatchKey,
            selected = _selection.IsSelected(w.MatchKey) || attached.Contains(w.MatchKey),
        });
    }

    public IEnumerable<WindowStatusUpdate> ListStatuses() =>
        _capture.Sessions.Select(s => s.Snapshot());

    /// <summary>Replaces the whole selection: attaches what is new, detaches what was dropped.</summary>
    public IEnumerable<WindowStatusUpdate> SetSelection(string[] matchKeys)
    {
        var wanted = new HashSet<string>(matchKeys ?? [], StringComparer.OrdinalIgnoreCase);
        _selection.Set(wanted);

        var available = WindowEnumerator.Enumerate();

        foreach (var session in _capture.Sessions)
        {
            if (!wanted.Contains(session.MatchKey)) _capture.Detach(session.Handle);
        }

        foreach (var matchKey in wanted)
        {
            if (_capture.FindByMatchKey(matchKey) is not null) continue;

            var window = WindowEnumerator.FindByMatchKey(matchKey, available);
            if (window is not null) _capture.Attach(window);
        }

        return _capture.Sessions.Select(s => s.Snapshot());
    }

    public WindowStatusUpdate? Attach(long handle)
    {
        var window = WindowEnumerator.Describe(handle);
        if (window is null) return null;

        _selection.Add(window.MatchKey);
        return _capture.Attach(window).Snapshot();
    }

    public void Detach(long handle)
    {
        if (_capture.Find(handle) is { } session) _selection.Remove(session.MatchKey);
        _input.Release(handle);
        _capture.Detach(handle);
    }

    /// <summary>
    /// Starts or changes this viewer's stream of a window. Preview for tiles, higher tiers for the
    /// full-screen view; the session captures at the fastest rate anyone asked for.
    /// </summary>
    public bool Subscribe(long handle, QualityLevel quality) =>
        _capture.Subscribe(Context.ConnectionId, handle, quality);

    public void Unsubscribe(long handle) => _capture.Unsubscribe(Context.ConnectionId, handle);

    public object SendKey(KeyEventDto keyEvent, InjectionMode mode)
    {
        if (!long.TryParse(keyEvent.WindowId, out long handle))
        {
            return new { delivered = false, reason = "Bad window id" };
        }

        var session = _capture.Find(handle);
        if (session is null)
        {
            return new { delivered = false, reason = "That window is not attached" };
        }

        var result = _input.Send(session.Info, keyEvent, mode);
        return new { delivered = result.Delivered, backend = result.Backend, reason = result.Reason };
    }

    /// <summary>
    /// Moves the real cursor to a point on the frame and clicks. Coordinates are 0..1 fractions of
    /// the captured frame - see MouseEventDto for why they are not pixels.
    /// </summary>
    public object SendMouse(MouseEventDto mouseEvent)
    {
        if (!long.TryParse(mouseEvent.WindowId, out long handle))
        {
            return new { delivered = false, reason = "Bad window id" };
        }

        var session = _capture.Find(handle);
        if (session is null)
        {
            return new { delivered = false, reason = "That window is not attached" };
        }

        var result = _input.SendMouse(session.Info, mouseEvent);
        return new { delivered = result.Delivered, backend = result.Backend, reason = result.Reason };
    }

    /// <summary>Asks the app to close, as clicking its X would. It may prompt about unsaved work.</summary>
    public object CloseWindow(long handle)
    {
        // gone is not the same failure as "the app refused": the window is already not there, so
        // the caller's row is stale and should be dropped rather than reported as an error.
        if (WindowEnumerator.Describe(handle) is not { } window)
        {
            return new { closed = false, gone = true, reason = "No such window" };
        }

        var (ok, reason) = WindowCloser.Close(handle);
        if (ok) _log.LogInformation("Asked '{Title}' to close", window.Title);

        return new { closed = ok, reason };
    }

    /// <summary>Terminates the app behind the window. Unsaved work in it is lost.</summary>
    public object KillWindow(long handle)
    {
        if (WindowEnumerator.Describe(handle) is not { } window)
        {
            return new { closed = false, gone = true, reason = "No such window" };
        }

        var (ok, reason) = WindowCloser.Kill(handle);
        if (ok) _log.LogWarning("Force killed '{Title}' ({Process})", window.Title, window.ProcessName);

        return new { closed = ok, reason };
    }

    /// <summary>Resizes the window to a given visible size, leaving it where it is on screen.</summary>
    public object ResizeWindow(long handle, int width, int height)
    {
        var session = _capture.Find(handle);
        if (session is null) return new { resized = false, reason = "That window is not attached" };

        var (ok, reason) = WindowResizer.Resize(handle, width, height);
        if (ok) _log.LogInformation("Resized '{Title}' to {Width}x{Height}", session.Info.Title, width, height);

        return new { resized = ok, reason };
    }

    /// <summary>Runs a command on the host, the way the Windows Run dialog does.</summary>
    public object RunApplication(string command)
    {
        var result = _launcher.Run(command);
        return new { started = result.Started, reason = result.Reason };
    }

    /// <summary>
    /// One directory of the host filesystem, for the Browse dialog. A file picker in the browser
    /// would list the phone's files and hand back no path at all, so the listing has to come from
    /// the machine being controlled.
    /// </summary>
    public object Browse(string? path) => Listing(HostFileBrowser.List(path, runnableOnly: true));

    /// <summary>The Explorer page's listing - every file, not just the launchable ones.</summary>
    public object Explore(string? path) => Listing(HostFileBrowser.List(path, runnableOnly: false));

    /// <summary>The apps the Explorer page offers, so the list lives in one place.</summary>
    public IEnumerable<object> OpenWithApps() => OpenWithLauncher.Apps.Select(a => new
    {
        key = a.Key,
        label = a.Label,
        handlesFolders = a.HandlesFolders,
        handlesFiles = a.HandlesFiles,
    });

    /// <summary>Opens a file or folder on the host, in a named app or its default one.</summary>
    public object OpenWith(string path, string? app)
    {
        var result = _openWith.Open(path, app);
        return new { started = result.Started, reason = result.Reason };
    }

    private static object Listing(BrowseListing listing)
    {
        return new
        {
            path = listing.Path,
            label = listing.Label,
            parent = listing.Parent,
            error = listing.Error,
            entries = listing.Entries.Select(e => new
            {
                name = e.Name,
                path = e.Path,
                isDirectory = e.IsDirectory,
            }),
        };
    }

    /// <summary>Brings the window to the front of the host desktop so typed keys land in it.</summary>
    public object FocusWindow(long handle)
    {
        var session = _capture.Find(handle);
        if (session is null) return new { focused = false, reason = "That window is not attached" };

        bool focused = _input.Focus(session.Info);
        return new
        {
            focused,
            reason = focused ? null : "Windows would not bring that window to the front",
        };
    }

    /// <summary>Which backend would handle input for this window, so the UI can warn in advance.</summary>
    public object DescribeInput(long handle, InjectionMode mode)
    {
        var session = _capture.Find(handle);
        if (session is null) return new { available = false, reason = "That window is not attached" };

        return new
        {
            available = true,
            backend = _input.Describe(session.Info, mode),
            warning = mode == InjectionMode.Background
                ? InputRouter.BackgroundUnsupportedReason(session.Info)
                : null,
        };
    }
}
