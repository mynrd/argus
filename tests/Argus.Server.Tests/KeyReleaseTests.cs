using Argus.Server.Input;
using Argus.Server.Interop;

namespace Argus.Server.Tests;

/// <summary>
/// What the Release keys button turns a stuck-key scan into, before any of it reaches SendInput -
/// see KeyReleaser.BuildReleaseInputs.
/// </summary>
public class KeyReleaseTests
{
    private const ushort VkShift = 0x10;
    private const ushort VkControl = 0x11;
    private const ushort VkMenu = 0x12;
    private const ushort VkLShift = 0xA0;
    private const ushort VkRShift = 0xA1;
    private const ushort VkLControl = 0xA2;
    private const ushort VkRControl = 0xA3;
    private const ushort VkLMenu = 0xA4;
    private const ushort VkRMenu = 0xA5;
    private const ushort VkLWin = 0x5B;
    private const ushort VkRWin = 0x5C;
    private const ushort VkF13 = 0x7C;
    private const ushort VkCapsLock = 0x14;
    private const ushort VkNumLock = 0x90;
    private const ushort VkScrollLock = 0x91;

    [Fact]
    public void Everything_is_a_key_up_except_the_f13_spacer()
    {
        var inputs = KeyReleaser.BuildReleaseInputs(KeyReleaser.AlwaysReleased);

        Assert.All(inputs, i => Assert.Equal(NativeMethods.INPUT_KEYBOARD, i.Type));

        // The one key-down in the batch is the F13 press, and it is paired with its own release.
        var downs = inputs.Where(i => !IsUp(i)).ToArray();
        var spacer = Assert.Single(downs);
        Assert.Equal(VkF13, spacer.Union.Keyboard.Vk);
        Assert.Equal(2, inputs.Count(i => i.Union.Keyboard.Vk == VkF13));
    }

    [Fact]
    public void Both_sides_of_every_modifier_are_released()
    {
        var vks = KeyReleaser.BuildReleaseInputs(KeyReleaser.AlwaysReleased)
            .Where(IsUp)
            .Select(i => i.Union.Keyboard.Vk)
            .ToArray();

        // A stuck Right Shift is invisible if only Left is released.
        Assert.Contains(VkLShift, vks);
        Assert.Contains(VkRShift, vks);
        Assert.Contains(VkLControl, vks);
        Assert.Contains(VkRControl, vks);
        Assert.Contains(VkLMenu, vks);
        Assert.Contains(VkRMenu, vks);
        Assert.Contains(VkLWin, vks);
        Assert.Contains(VkRWin, vks);

        // And the generic VKs an app calling GetKeyState(VK_CONTROL) actually reads.
        Assert.Contains(VkShift, vks);
        Assert.Contains(VkControl, vks);
        Assert.Contains(VkMenu, vks);
    }

    [Fact]
    public void The_f13_press_comes_before_the_win_key_ups()
    {
        var inputs = KeyReleaser.BuildReleaseInputs(KeyReleaser.AlwaysReleased);

        int spacerDown = IndexOf(inputs, VkF13, up: false);
        int spacerUp = IndexOf(inputs, VkF13, up: true);
        int leftWin = IndexOf(inputs, VkLWin, up: true);
        int rightWin = IndexOf(inputs, VkRWin, up: true);

        // The whole point: the shell sees Win+F13 rather than a bare Win tap, so releasing a stuck
        // Win key does not leave the Start menu open.
        Assert.True(spacerDown >= 0 && spacerUp > spacerDown);
        Assert.True(spacerUp < leftWin);
        Assert.True(spacerUp < rightWin);
    }

    [Fact]
    public void No_win_key_in_the_batch_means_no_spacer()
    {
        var inputs = KeyReleaser.BuildReleaseInputs([VkLControl, VkControl]);

        Assert.Equal(2, inputs.Length);
        Assert.DoesNotContain(inputs, i => i.Union.Keyboard.Vk == VkF13);
        Assert.All(inputs, i => Assert.True(IsUp(i)));
    }

    [Fact]
    public void Toggle_keys_are_never_touched()
    {
        var vks = KeyReleaser.BuildReleaseInputs(KeyReleaser.AlwaysReleased)
            .Select(i => i.Union.Keyboard.Vk)
            .ToArray();

        // Blind-releasing a toggle does nothing, and blind-tapping it flips a state the user wanted.
        Assert.DoesNotContain(VkCapsLock, vks);
        Assert.DoesNotContain(VkNumLock, vks);
        Assert.DoesNotContain(VkScrollLock, vks);

        Assert.DoesNotContain(VkCapsLock, KeyReleaser.AlwaysReleased);
        Assert.DoesNotContain(VkNumLock, KeyReleaser.AlwaysReleased);
        Assert.DoesNotContain(VkScrollLock, KeyReleaser.AlwaysReleased);
    }

    [Fact]
    public void Right_hand_modifiers_carry_the_extended_flag()
    {
        var inputs = KeyReleaser.BuildReleaseInputs([VkRControl, VkLControl]);

        // Without it, a Right Ctrl key-up releases Left Ctrl and the stuck one stays down.
        Assert.True(HasFlag(inputs[0], NativeMethods.KEYEVENTF_EXTENDEDKEY));
        Assert.False(HasFlag(inputs[1], NativeMethods.KEYEVENTF_EXTENDEDKEY));
    }

    [Fact]
    public void Nothing_stuck_produces_nothing()
    {
        Assert.Empty(KeyReleaser.BuildReleaseInputs([]));
        Assert.Empty(KeyReleaser.BuildReleaseInputs(null!));
        Assert.Empty(KeyReleaser.BuildMouseReleaseInputs([]));
    }

    [Fact]
    public void Only_the_mouse_buttons_found_down_are_released()
    {
        // A stranded drag is the same failure as a stuck Ctrl, so the scan covers the buttons too.
        var inputs = KeyReleaser.BuildMouseReleaseInputs([0x01, VkLControl]);

        var only = Assert.Single(inputs);
        Assert.Equal(NativeMethods.INPUT_MOUSE, only.Type);
        Assert.Equal(NativeMethods.MOUSEEVENTF_LEFTUP, only.Union.Mouse.Flags);
    }

    [Fact]
    public void Both_sides_of_a_modifier_read_as_one_name()
    {
        // "Released Ctrl", not "Released LCONTROL, RCONTROL, CONTROL".
        Assert.Equal("Ctrl", KeyReleaser.NameOf(VkLControl));
        Assert.Equal("Ctrl", KeyReleaser.NameOf(VkRControl));
        Assert.Equal("Ctrl", KeyReleaser.NameOf(VkControl));
        Assert.Equal("Win", KeyReleaser.NameOf(VkRWin));
        Assert.Equal("A", KeyReleaser.NameOf(0x41));
        Assert.Equal("F13", KeyReleaser.NameOf(VkF13));
        Assert.Equal("Left button", KeyReleaser.NameOf(0x01));
    }

    private static bool IsUp(NativeMethods.INPUT input) => HasFlag(input, NativeMethods.KEYEVENTF_KEYUP);

    private static bool HasFlag(NativeMethods.INPUT input, uint flag) =>
        (input.Union.Keyboard.Flags & flag) == flag;

    private static int IndexOf(NativeMethods.INPUT[] inputs, ushort vk, bool up)
    {
        for (int i = 0; i < inputs.Length; i++)
        {
            if (inputs[i].Union.Keyboard.Vk == vk && IsUp(inputs[i]) == up) return i;
        }
        return -1;
    }
}
