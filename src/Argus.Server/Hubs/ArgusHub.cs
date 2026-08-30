using Argus.Server.Capture;
using Argus.Server.Input;
using Argus.Server.Services;
using Microsoft.AspNetCore.SignalR;

namespace Argus.Server.Hubs;

/// <summary>
/// The live channel: frame subscriptions, status pushes, and input.
///
/// Nothing else belongs here. SignalR runs one invocation per connection at a time, and this app
/// needs that - two keystrokes must not overtake each other, or a Ctrl-down can outlive its
/// Ctrl-up and strand the modifier on the host. The cost of that guarantee is a single queue, so
/// anything slow sharing it stalls typing. Questions and one-off commands are plain HTTP under
/// /api instead, where Kestrel runs them in parallel and they cannot block this.
///
/// The frames themselves are not here either - they go over /ws/frames as raw binary.
/// </summary>
public sealed class ArgusHub : Hub
{
    /// <summary>Longest block of text one SendText call will type.</summary>
    private const int MaxTextLength = 10_000;

    private readonly CaptureManager _capture;
    private readonly InputRouter _input;
    private readonly KeyReleaser _keyReleaser;
    private readonly HeldInputTracker _held;
    private readonly ILogger<ArgusHub> _log;

    public ArgusHub(
        CaptureManager capture,
        InputRouter input,
        KeyReleaser keyReleaser,
        HeldInputTracker held,
        ILogger<ArgusHub> log)
    {
        _capture = capture;
        _input = input;
        _keyReleaser = keyReleaser;
        _held = held;
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

    /// <summary>
    /// Fires whether the tab was closed, the network dropped or the browser was killed - which is
    /// exactly when nothing sent the key-ups. Anything this viewer left down comes back up here.
    /// </summary>
    public override Task OnDisconnectedAsync(Exception? exception)
    {
        _log.LogInformation("Viewer {ConnectionId} disconnected", Context.ConnectionId);
        _held.ReleaseFor(Context.ConnectionId);
        _capture.UnsubscribeAll(Context.ConnectionId);
        return base.OnDisconnectedAsync(exception);
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

        // Only what actually went through SendInput: a posted WM_KEYDOWN never touched the global
        // key state, so there would be nothing for the disconnect cleanup to lift.
        if (result.Delivered && result.Backend == ForegroundInjector.BackendName)
        {
            ushort vk = KeyMapper.Map(keyEvent.Code).VirtualKey;
            if (keyEvent.Type == KeyEventType.Down) _held.Down(Context.ConnectionId, vk);
            else _held.Up(Context.ConnectionId, vk);
        }

        return new { delivered = result.Delivered, backend = result.Backend, reason = result.Reason };
    }

    /// <summary>
    /// Types a block of text into the window in one go, optionally pressing Enter after it.
    ///
    /// Sending it as text rather than as a stream of SendKey calls is what makes it usable from a
    /// phone: one round trip for a whole command line instead of one per character, and the app
    /// cannot be interrupted half way through by something else grabbing the foreground.
    /// </summary>
    public object SendText(string windowId, string? text, bool submit)
    {
        if (!long.TryParse(windowId, out long handle))
        {
            return new { delivered = false, reason = "Bad window id" };
        }

        var session = _capture.Find(handle);
        if (session is null)
        {
            return new { delivered = false, reason = "That window is not attached" };
        }

        string body = text ?? string.Empty;
        if (body.Length == 0 && !submit)
        {
            return new { delivered = false, reason = "Nothing to type" };
        }

        // A cap rather than no limit: every character is two SendInput events on the host, and a
        // paste of a whole file would hold the desktop hostage for minutes with no way to stop it.
        if (body.Length > MaxTextLength)
        {
            return new
            {
                delivered = false,
                reason = $"That is more than {MaxTextLength} characters - send it in smaller pieces",
            };
        }

        var result = _input.SendText(session.Info, body, submit);
        _log.LogInformation("Typed {Length} characters into '{Title}' (enter: {Submit}): {Delivered}",
            body.Length, session.Info.Title, submit, result.Delivered);

        return new { delivered = result.Delivered, backend = result.Backend, reason = result.Reason };
    }

    /// <summary>
    /// Lifts every modifier off the host keyboard, plus anything else found physically down.
    ///
    /// No window id on purpose. A key-up clears the global key state whatever is in front, so
    /// there is nothing to aim it at - and the case you most want this in is the one where the
    /// window that stranded the key is already gone, or nothing is attached at all.
    /// </summary>
    public object ReleaseKeys()
    {
        var report = _keyReleaser.ReleaseAll();

        // The host is clear now, so this viewer is holding nothing. Without this, its disconnect
        // would fire a second set of key-ups at whatever window is in front by then.
        _held.Forget(Context.ConnectionId);

        return new { released = report.Released, reason = report.Reason };
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

        // A Click is a down and an up in the same call, so only a bare Down leaves a button held.
        if (result.Delivered)
        {
            ushort vk = HeldInputTracker.VkFor(mouseEvent.Button);
            if (mouseEvent.Action == MouseAction.Down) _held.Down(Context.ConnectionId, vk);
            else if (mouseEvent.Action is MouseAction.Up or MouseAction.Click) _held.Up(Context.ConnectionId, vk);
        }

        return new { delivered = result.Delivered, backend = result.Backend, reason = result.Reason };
    }
}
