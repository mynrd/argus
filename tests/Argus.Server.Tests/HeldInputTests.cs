using Argus.Server.Input;
using Argus.Server.Interop;
using Microsoft.Extensions.Logging.Abstractions;

namespace Argus.Server.Tests;

/// <summary>
/// The per-connection bookkeeping behind the automatic cleanup: what a viewer is holding, and what
/// a disconnect therefore has to lift. The SendInput call itself is not exercised here - only the
/// set of virtual keys it would be handed.
/// </summary>
public class HeldInputTests
{
    private const ushort VkLControl = 0xA2;
    private const ushort VkLMenu = 0xA4;
    private const ushort VkTab = 0x09;
    private const ushort VkLButton = 0x01;
    private const ushort VkRButton = 0x02;
    private const ushort VkMButton = 0x04;

    private const string Viewer = "connection-1";
    private const string Other = "connection-2";

    /// <summary>Every INPUT the tracker's release would have handed to SendInput, in order.</summary>
    private static HeldInputTracker NewTracker(List<NativeMethods.INPUT>? sent = null)
    {
        var releaser = new KeyReleaser(NullLogger<KeyReleaser>.Instance);

        // Never the real SendInput: the suite must not fire key-ups and button-ups at the machine
        // running it.
        releaser.Sender = inputs =>
        {
            sent?.AddRange(inputs);
            return (uint)inputs.Length;
        };

        return new HeldInputTracker(releaser, NullLogger<HeldInputTracker>.Instance);
    }

    [Fact]
    public void A_key_that_came_back_up_is_not_held()
    {
        var tracker = NewTracker();

        tracker.Down(Viewer, VkLControl);
        tracker.Down(Viewer, VkTab);
        tracker.Up(Viewer, VkTab);

        Assert.Equal([VkLControl], tracker.HeldBy(Viewer));
    }

    [Fact]
    public void A_locked_modifier_is_still_held_after_the_key_it_wrapped()
    {
        // Alt+Tab+Tab: Alt stays down across both Tabs, which is the whole point of locking it -
        // and exactly what is stranded if the tab closes mid-walk.
        var tracker = NewTracker();

        tracker.Down(Viewer, VkLMenu);
        tracker.Down(Viewer, VkTab);
        tracker.Up(Viewer, VkTab);
        tracker.Down(Viewer, VkTab);
        tracker.Up(Viewer, VkTab);

        Assert.Equal([VkLMenu], tracker.HeldBy(Viewer));
    }

    [Fact]
    public void Viewers_do_not_release_each_others_keys()
    {
        var tracker = NewTracker();

        tracker.Down(Viewer, VkLControl);
        tracker.Down(Other, VkLMenu);

        var report = tracker.ReleaseFor(Viewer);

        Assert.Equal(["Ctrl"], report.Released);
        Assert.Equal([VkLMenu], tracker.HeldBy(Other));
    }

    [Fact]
    public void A_stranded_drag_is_dropped_before_its_modifier_is_lifted()
    {
        var sent = new List<NativeMethods.INPUT>();
        var tracker = NewTracker(sent);

        tracker.Down(Viewer, VkLControl);
        tracker.Down(Viewer, VkLButton);

        var report = tracker.ReleaseFor(Viewer);

        Assert.Contains("Ctrl", report.Released);
        Assert.Contains("Left button", report.Released);

        // Order matters: lifting Ctrl first would turn a Ctrl-drag into a plain drop, which is not
        // the gesture the viewer was making when it vanished.
        Assert.Equal(NativeMethods.INPUT_MOUSE, sent[0].Type);
        Assert.Equal(NativeMethods.MOUSEEVENTF_LEFTUP, sent[0].Union.Mouse.Flags);
        Assert.Contains(sent.Skip(1), i => i.Union.Keyboard.Vk == VkLControl);
    }

    [Fact]
    public void Only_what_the_viewer_left_down_is_released()
    {
        // Not the full sweep: a disconnect must not lift keys the person at the machine is holding.
        var sent = new List<NativeMethods.INPUT>();
        var tracker = NewTracker(sent);

        tracker.Down(Viewer, VkLControl);
        tracker.ReleaseFor(Viewer);

        var only = Assert.Single(sent);
        Assert.Equal(VkLControl, only.Union.Keyboard.Vk);
        Assert.True((only.Union.Keyboard.Flags & NativeMethods.KEYEVENTF_KEYUP) != 0);
    }

    [Fact]
    public void Every_button_is_tracked_by_its_own_vk()
    {
        Assert.Equal(VkLButton, HeldInputTracker.VkFor(MouseButton.Left));
        Assert.Equal(VkRButton, HeldInputTracker.VkFor(MouseButton.Right));
        Assert.Equal(VkMButton, HeldInputTracker.VkFor(MouseButton.Middle));
    }

    [Fact]
    public void Unmapped_keys_are_not_tracked()
    {
        // KeyMapper returns VK 0 for a code it does not know; recording that would put a key-up for
        // VK 0 in the disconnect batch.
        var tracker = NewTracker();

        tracker.Down(Viewer, 0);

        Assert.Empty(tracker.HeldBy(Viewer));
    }

    [Fact]
    public void Releasing_twice_only_sends_once()
    {
        var tracker = NewTracker();
        tracker.Down(Viewer, VkLControl);

        Assert.Equal(["Ctrl"], tracker.ReleaseFor(Viewer).Released);
        Assert.Empty(tracker.ReleaseFor(Viewer).Released);
    }

    [Fact]
    public void Forget_drops_the_set_without_releasing_it()
    {
        // What Release keys does: the host is already clear, so the disconnect must not fire a
        // second set of key-ups at whatever window is in front by then.
        var tracker = NewTracker();
        tracker.Down(Viewer, VkLControl);

        tracker.Forget(Viewer);

        Assert.Empty(tracker.HeldBy(Viewer));
        Assert.Empty(tracker.ReleaseFor(Viewer).Released);
    }

    [Fact]
    public void A_viewer_that_held_nothing_releases_nothing()
    {
        Assert.Empty(NewTracker().ReleaseFor(Viewer).Released);
    }
}
