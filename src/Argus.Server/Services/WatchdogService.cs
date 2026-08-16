using Argus.Server.Capture;
using Argus.Server.Hubs;
using Argus.Server.Windows;
using Microsoft.AspNetCore.SignalR;

namespace Argus.Server.Services;

/// <summary>
/// The health loop. Every tick it answers the question the capture loops cannot answer for
/// themselves: is this still actually working?
///
///   - Attached window closed          -> mark it dead, keep watching for it to come back.
///   - Attached window came back        -> re-attach to the new HWND, keep the same tile.
///   - Selected app started for the first time -> attach it.
///   - Capture running but silent       -> rebuild the capture source.
///   - Title or size changed            -> push it so tiles do not show stale labels.
///
/// A heartbeat goes out every tick regardless, so a browser that stops hearing from the server can
/// show "agent offline" rather than a frozen last frame.
/// </summary>
public sealed class WatchdogService : BackgroundService
{
    private static readonly TimeSpan TickInterval = TimeSpan.FromSeconds(2);

    /// <summary>How long a subscribed session may produce nothing before its source is rebuilt.</summary>
    private static readonly TimeSpan SilenceBeforeRestart = TimeSpan.FromSeconds(6);

    private readonly CaptureManager _capture;
    private readonly SelectionStore _selection;
    private readonly IHubContext<ArgusHub> _hub;
    private readonly ILogger<WatchdogService> _log;

    public WatchdogService(
        CaptureManager capture,
        SelectionStore selection,
        IHubContext<ArgusHub> hub,
        ILogger<WatchdogService> log)
    {
        _capture = capture;
        _selection = selection;
        _hub = hub;
        _log = log;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Attach anything that was selected in a previous run and is already open.
        RestoreSelection();

        using var timer = new PeriodicTimer(TickInterval);
        while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false))
        {
            try
            {
                await TickAsync(stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                _log.LogError(ex, "Watchdog tick failed");
            }
        }
    }

    private void RestoreSelection()
    {
        var available = WindowEnumerator.Enumerate();
        foreach (var matchKey in _selection.Selected)
        {
            var window = WindowEnumerator.FindByMatchKey(matchKey, available);
            if (window is not null) _capture.Attach(window);
        }
    }

    private async Task TickAsync(CancellationToken token)
    {
        var available = WindowEnumerator.Enumerate();
        var byHandle = available.ToDictionary(w => w.Handle);

        foreach (var session in _capture.Sessions)
        {
            if (byHandle.TryGetValue(session.Handle, out var current))
            {
                session.UpdateInfo(current);

                // A session nobody is watching is idle on purpose, not stalled.
                if (session.SubscriberCount > 0 && session.IsSilentFor(SilenceBeforeRestart))
                {
                    session.RestartSource();
                    _capture.RaiseStatus(session);
                }
                else if (session.SubscriberCount > 0)
                {
                    // Status is otherwise only pushed when it *changes*, which leaves the measured
                    // frame rate frozen at whatever it was before the first frame landed - the
                    // dashboard would sit on "Streaming - 0 fps" forever.
                    _capture.RaiseStatus(session);
                }
                continue;
            }

            // The HWND is gone. Look for the same app under a new handle before giving up.
            var replacement = WindowEnumerator.FindByMatchKey(session.MatchKey, available);
            if (replacement is not null && _capture.Find(replacement.Handle) is null)
            {
                _capture.Reattach(session.Handle, replacement);
            }
            else
            {
                session.MarkClosed("The app is no longer running.");
            }
        }

        // Selected apps that were not running when Argus started, or when they were picked.
        foreach (var matchKey in _selection.Selected)
        {
            if (_capture.FindByMatchKey(matchKey) is not null) continue;

            var window = WindowEnumerator.FindByMatchKey(matchKey, available);
            if (window is not null) _capture.Attach(window);
        }

        await _hub.Clients.All.SendAsync("Heartbeat", new
        {
            utc = DateTimeOffset.UtcNow,
            attached = _capture.Sessions.Count,
        }, token).ConfigureAwait(false);
    }
}
