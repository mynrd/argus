using Argus.Server.Input;
using Argus.Server.Interop;

namespace Argus.Server.Tests;

/// <summary>
/// The keystrokes the Send Text panel turns a block of text into, before any of it reaches
/// SendInput - see ForegroundInjector.BuildTextInputs.
/// </summary>
public class TextInjectionTests
{
    private const ushort VkReturn = 0x0D;
    private const ushort VkTab = 0x09;

    [Fact]
    public void Each_character_becomes_a_unicode_down_and_up()
    {
        var inputs = ForegroundInjector.BuildTextInputs("hi", submit: false);

        Assert.Equal(4, inputs.Length);
        Assert.All(inputs, i => Assert.Equal(NativeMethods.INPUT_KEYBOARD, i.Type));
        // Vk 0 with the unicode flag is what makes the character arrive whatever layout the host
        // keyboard is set to.
        Assert.All(inputs, i => Assert.Equal(0, i.Union.Keyboard.Vk));
        Assert.All(inputs, i => Assert.True(HasFlag(i, NativeMethods.KEYEVENTF_UNICODE)));

        Assert.Equal('h', (char)inputs[0].Union.Keyboard.Scan);
        Assert.False(HasFlag(inputs[0], NativeMethods.KEYEVENTF_KEYUP));
        Assert.Equal('h', (char)inputs[1].Union.Keyboard.Scan);
        Assert.True(HasFlag(inputs[1], NativeMethods.KEYEVENTF_KEYUP));
        Assert.Equal('i', (char)inputs[2].Union.Keyboard.Scan);
        Assert.Equal('i', (char)inputs[3].Union.Keyboard.Scan);
    }

    [Fact]
    public void Hit_enter_presses_return_after_the_text()
    {
        var inputs = ForegroundInjector.BuildTextInputs("hi", submit: true);

        Assert.Equal(6, inputs.Length);
        Assert.Equal(VkReturn, inputs[4].Union.Keyboard.Vk);
        Assert.False(HasFlag(inputs[4], NativeMethods.KEYEVENTF_KEYUP));
        Assert.Equal(VkReturn, inputs[5].Union.Keyboard.Vk);
        Assert.True(HasFlag(inputs[5], NativeMethods.KEYEVENTF_KEYUP));
    }

    [Fact]
    public void Hit_enter_on_its_own_is_just_the_return_key()
    {
        var inputs = ForegroundInjector.BuildTextInputs(string.Empty, submit: true);

        Assert.Equal(2, inputs.Length);
        Assert.All(inputs, i => Assert.Equal(VkReturn, i.Union.Keyboard.Vk));
    }

    [Fact]
    public void Nothing_to_type_produces_nothing()
    {
        Assert.Empty(ForegroundInjector.BuildTextInputs(string.Empty, submit: false));
        Assert.Empty(ForegroundInjector.BuildTextInputs(null, submit: false));
    }

    [Fact]
    public void Line_breaks_become_the_return_key_rather_than_a_literal_character()
    {
        // A newline sent as a unicode character shows up as a box in a text field and does nothing
        // at all in a shell; only the real key submits a line.
        var inputs = ForegroundInjector.BuildTextInputs("a\nb", submit: false);

        Assert.Equal(6, inputs.Length);
        Assert.Equal(VkReturn, inputs[2].Union.Keyboard.Vk);
        Assert.Equal(VkReturn, inputs[3].Union.Keyboard.Vk);
        Assert.Equal(0, inputs[4].Union.Keyboard.Vk);          // back to unicode for 'b'
    }

    [Fact]
    public void Crlf_is_one_return_not_two()
    {
        var windows = ForegroundInjector.BuildTextInputs("a\r\nb", submit: false);
        var unix = ForegroundInjector.BuildTextInputs("a\nb", submit: false);

        Assert.Equal(unix.Length, windows.Length);
    }

    [Fact]
    public void Text_that_already_ends_in_a_newline_is_not_submitted_twice()
    {
        var inputs = ForegroundInjector.BuildTextInputs("dir\n", submit: true);

        // Three characters plus exactly one Enter - a second one would run an empty command.
        Assert.Equal(8, inputs.Length);
        Assert.Equal(VkReturn, inputs[6].Union.Keyboard.Vk);
        Assert.Equal(VkReturn, inputs[7].Union.Keyboard.Vk);
    }

    [Fact]
    public void Tabs_become_the_tab_key()
    {
        var inputs = ForegroundInjector.BuildTextInputs("\t", submit: false);

        Assert.Equal(2, inputs.Length);
        Assert.All(inputs, i => Assert.Equal(VkTab, i.Union.Keyboard.Vk));
    }

    [Fact]
    public void Other_control_characters_are_dropped()
    {
        // A NUL or a bell in a pasted block is noise; typing it would put a box in the app.
        var inputs = ForegroundInjector.BuildTextInputs("a\0b", submit: false);

        Assert.Equal(4, inputs.Length);
        Assert.Equal('a', (char)inputs[0].Union.Keyboard.Scan);
        Assert.Equal('b', (char)inputs[2].Union.Keyboard.Scan);
    }

    [Fact]
    public void Non_ascii_characters_travel_as_themselves()
    {
        // Escaped rather than written literally so the test says the same thing whatever the file
        // encoding does. Unicode injection is the whole reason a layout on the host cannot mangle it.
        var inputs = ForegroundInjector.BuildTextInputs("\u00e9", submit: false);

        Assert.Equal(2, inputs.Length);
        Assert.Equal('\u00e9', (char)inputs[0].Union.Keyboard.Scan);
    }

    private static bool HasFlag(NativeMethods.INPUT input, uint flag) =>
        (input.Union.Keyboard.Flags & flag) == flag;
}
