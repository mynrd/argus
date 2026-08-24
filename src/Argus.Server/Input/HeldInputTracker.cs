using System.Collections.Concurrent;

namespace Argus.Server.Input;

/// <summary>
/// Remembers what each viewer has pressed and not yet released, so a disconnect can undo exactly
/// that much.
///
/// The Release keys button is the manual escape hatch; this is the reason you should rarely need
/// it. Closing the browser tab with Ctrl locked on the key pad sends no key-up at all, and the key
/// stays physically down on the host until someone notices - the same failure people hit in
/// TeamViewer. SignalR does tell us the connection went away, so the cleanup can be automatic.
///
/// Only SendInput-delivered input is tracked. A posted WM_KEYDOWN never touched the global key
/// state, so releasing it globally would aim a key-up at whatever window happens to be in front
/// rather than at the app that has the key stuck.
/// </summary>
public sealed class HeldInputTracker
{
    // A set per connection. Concurrent both ways: hub calls for one viewer can overlap, and viewers
    // connect and drop while others are typing.
    private readonly ConcurrentDictionary<string, ConcurrentDictionary<ushort, byte>> _held =
        new(StringComparer.Ordinal);

    private readonly KeyReleaser _releaser;
    private readonly ILogger<HeldInputTracker> _log;

    public HeldInputTracker(KeyReleaser releaser, ILogger<HeldInputTracker> log)
    {
        _releaser = releaser;
        _log = log;
    }

    /// <summary>VK for a viewer's mouse button, or 0 for anything unrecognised.</summary>
    public static ushort VkFor(MouseButton button) => button switch
    {
        MouseButton.Left => 0x01,
        MouseButton.Right => 0x02,
        MouseButton.Middle => 0x04,
        _ => 0,
    };

    /// <summary>Records a key or button as down for this viewer. Ignores VK 0 (unmapped keys).</summary>
    public void Down(string connectionId, ushort vk)
    {
        if (vk == 0) return;
        _held.GetOrAdd(connectionId, _ => new ConcurrentDictionary<ushort, byte>())[vk] = 0;
    }

    /// <summary>Forgets a key or button, because its release has just been delivered.</summary>
    public void Up(string connectionId, ushort vk)
    {
        if (vk == 0) return;
        if (_held.TryGetValue(connectionId, out var set)) set.TryRemove(vk, out _);
    }

    /// <summary>
    /// Drops what we think this viewer is holding without sending anything.
    ///
    /// Used after Release keys, which has already cleared the host: releasing the same VKs again on
    /// disconnect would be a second set of key-ups aimed at whatever is in front by then.
    /// </summary>
    public void Forget(string connectionId) => _held.TryRemove(connectionId, out _);

    /// <summary>What this viewer is currently holding, in VK order. For tests and logging.</summary>
    public IReadOnlyList<ushort> HeldBy(string connectionId) =>
        _held.TryGetValue(connectionId, out var set) ? [.. set.Keys.Order()] : [];

    /// <summary>
    /// Releases everything this viewer left down and forgets it. Called from OnDisconnectedAsync,
    /// which is the one place that fires whether the tab was closed, the network dropped, or the
    /// browser was killed.
    /// </summary>
    public ReleaseReport ReleaseFor(string connectionId)
    {
        if (!_held.TryRemove(connectionId, out var set) || set.IsEmpty) return new ReleaseReport([]);

        var report = _releaser.Release([.. set.Keys.Order()]);
        _log.LogInformation("Viewer {ConnectionId} left {Keys} down - released", connectionId,
            string.Join(", ", report.Released));

        return report;
    }
}
