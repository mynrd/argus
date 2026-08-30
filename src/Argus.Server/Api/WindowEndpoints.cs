using Argus.Server.Capture;
using Argus.Server.Input;
using Argus.Server.Services;
using Argus.Server.Windows;

namespace Argus.Server.Api;

public sealed record SelectionRequest(string[] MatchKeys);

public sealed record ResizeRequest(int Width, int Height);

public sealed record RunRequest(string Command);

/// <summary>
/// Everything about windows that is a question or a one-off command: what exists, what is
/// attached, attach, detach, close, kill, focus, resize, and run something.
///
/// Not on the hub, deliberately. SignalR runs one invocation per connection at a time - the input
/// path depends on that ordering - so anything slow that shares the queue stalls typing. These are
/// plain request-response calls with no ordering relationship to each other, and Kestrel handles
/// them in parallel. The hub keeps the frame stream, the status pushes and input, which are the
/// only things that need a live connection.
/// </summary>
public static class WindowEndpoints
{
    public static void MapWindowEndpoints(this WebApplication app)
    {
        // Every window Argus could attach to, plus whether it is currently selected.
        app.MapGet("/api/windows", (CaptureManager capture, SelectionStore selection) =>
        {
            var attached = capture.Sessions.Select(s => s.MatchKey).ToHashSet(StringComparer.OrdinalIgnoreCase);

            return Results.Ok(WindowEnumerator.Enumerate().Select(w => new
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
                selected = selection.IsSelected(w.MatchKey) || attached.Contains(w.MatchKey),
            }));
        });

        app.MapGet("/api/statuses", (CaptureManager capture) =>
            Results.Ok(capture.Sessions.Select(s => s.Snapshot())));

        // Replaces the whole selection: attaches what is new, detaches what was dropped.
        app.MapPost("/api/selection", (SelectionRequest body, CaptureManager capture, SelectionStore selection) =>
        {
            var wanted = new HashSet<string>(body.MatchKeys ?? [], StringComparer.OrdinalIgnoreCase);
            selection.Set(wanted);

            var available = WindowEnumerator.Enumerate();

            foreach (var session in capture.Sessions)
            {
                if (!wanted.Contains(session.MatchKey)) capture.Detach(session.Handle);
            }

            foreach (var matchKey in wanted)
            {
                if (capture.FindByMatchKey(matchKey) is not null) continue;

                var window = WindowEnumerator.FindByMatchKey(matchKey, available);
                if (window is not null) capture.Attach(window);
            }

            return Results.Ok(capture.Sessions.Select(s => s.Snapshot()));
        });

        app.MapPost("/api/windows/{handle:long}/attach", (long handle, CaptureManager capture, SelectionStore selection) =>
        {
            var window = WindowEnumerator.Describe(handle);
            if (window is null) return Results.NotFound(new { error = "No such window" });

            selection.Add(window.MatchKey);
            return Results.Ok(capture.Attach(window).Snapshot());
        });

        app.MapPost("/api/windows/{handle:long}/detach",
            (long handle, CaptureManager capture, SelectionStore selection, InputRouter input) =>
        {
            if (capture.Find(handle) is { } session) selection.Remove(session.MatchKey);
            input.Release(handle);
            capture.Detach(handle);
            return Results.NoContent();
        });

        // Asks the app to close, as clicking its X would. It may prompt about unsaved work.
        app.MapPost("/api/windows/{handle:long}/close", (long handle, ILoggerFactory loggers) =>
        {
            // gone is not the same failure as "the app refused": the window is already not there,
            // so the caller's row is stale and should be dropped rather than reported as an error.
            if (WindowEnumerator.Describe(handle) is not { } window)
            {
                return Results.Ok(new { closed = false, gone = true, reason = "No such window" });
            }

            var (ok, reason) = WindowCloser.Close(handle);
            if (ok) loggers.CreateLogger("windows").LogInformation("Asked '{Title}' to close", window.Title);

            return Results.Ok(new { closed = ok, reason });
        });

        // Terminates the app behind the window. Unsaved work in it is lost.
        app.MapPost("/api/windows/{handle:long}/kill", (long handle, ILoggerFactory loggers) =>
        {
            if (WindowEnumerator.Describe(handle) is not { } window)
            {
                return Results.Ok(new { closed = false, gone = true, reason = "No such window" });
            }

            var (ok, reason) = WindowCloser.Kill(handle);
            if (ok)
            {
                loggers.CreateLogger("windows")
                    .LogWarning("Force killed '{Title}' ({Process})", window.Title, window.ProcessName);
            }

            return Results.Ok(new { closed = ok, reason });
        });

        // Brings the window to the front of the host desktop so typed keys land in it.
        app.MapPost("/api/windows/{handle:long}/focus", (long handle, CaptureManager capture, InputRouter input) =>
        {
            var session = capture.Find(handle);
            if (session is null) return Results.Ok(new { focused = false, reason = "That window is not attached" });

            bool focused = input.Focus(session.Info);
            return Results.Ok(new
            {
                focused,
                reason = focused ? null : "Windows would not bring that window to the front",
            });
        });

        // Resizes the window to a given visible size, leaving it where it is on screen.
        app.MapPost("/api/windows/{handle:long}/resize",
            (long handle, ResizeRequest body, CaptureManager capture, ILoggerFactory loggers) =>
        {
            var session = capture.Find(handle);
            if (session is null) return Results.Ok(new { resized = false, reason = "That window is not attached" });

            var (ok, reason) = WindowResizer.Resize(handle, body.Width, body.Height);
            if (ok)
            {
                loggers.CreateLogger("windows").LogInformation(
                    "Resized '{Title}' to {Width}x{Height}", session.Info.Title, body.Width, body.Height);
            }

            return Results.Ok(new { resized = ok, reason });
        });

        // Which backend would handle input for this window, so the UI can warn in advance.
        app.MapGet("/api/windows/{handle:long}/input",
            (long handle, InjectionMode mode, CaptureManager capture, InputRouter input) =>
        {
            var session = capture.Find(handle);
            if (session is null) return Results.Ok(new { available = false, reason = "That window is not attached" });

            return Results.Ok(new
            {
                available = true,
                backend = input.Describe(session.Info, mode),
                warning = mode == InjectionMode.Background
                    ? InputRouter.BackgroundUnsupportedReason(session.Info)
                    : null,
            });
        });

        // Runs a command on the host, the way the Windows Run dialog does.
        app.MapPost("/api/run", (RunRequest body, ApplicationLauncher launcher) =>
        {
            var result = launcher.Run(body.Command);
            return Results.Ok(new { started = result.Started, reason = result.Reason });
        });
    }
}
