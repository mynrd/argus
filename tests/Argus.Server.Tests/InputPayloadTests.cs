using Argus.Server.Input;

namespace Argus.Server.Tests;

public class InputPayloadTests
{
    [Theory]
    [InlineData("KeyA", "a")]
    [InlineData("KeyA", "A")]
    [InlineData("Digit1", "1")]
    [InlineData("Space", " ")]
    [InlineData("Slash", "/")]
    public void Plain_characters_are_printable(string code, string key)
    {
        Assert.True(Key(code, key).IsPrintable);
    }

    [Theory]
    [InlineData("Enter", "Enter")]
    [InlineData("ArrowLeft", "ArrowLeft")]
    [InlineData("F5", "F5")]
    [InlineData("ShiftLeft", "Shift")]
    [InlineData("Backspace", "Backspace")]
    public void Named_keys_are_not_printable(string code, string key)
    {
        Assert.False(Key(code, key).IsPrintable);
    }

    [Fact]
    public void A_character_held_with_ctrl_or_alt_is_not_printable()
    {
        // Ctrl+C must travel as a key event, not as the literal character 'c'.
        Assert.False((Key("KeyC", "c") with { Ctrl = true }).IsPrintable);
        Assert.False((Key("KeyC", "c") with { Alt = true }).IsPrintable);
        Assert.False((Key("KeyC", "c") with { Meta = true }).IsPrintable);
    }

    [Fact]
    public void Shift_alone_still_produces_a_printable_character()
    {
        Assert.True((Key("KeyA", "A") with { Shift = true }).IsPrintable);
    }

    [Fact]
    public void Control_characters_are_not_printable()
    {
        Assert.False(Key("Enter", "\r").IsPrintable);
        Assert.False(Key("Tab", "\t").IsPrintable);
    }

    [Fact]
    public void Console_command_for_a_character_carries_its_utf16_code_unit()
    {
        Assert.Equal("C 97", ConsoleInputInjector.BuildCommand(Key("KeyA", "a")));
        Assert.Equal("C 65", ConsoleInputInjector.BuildCommand(Key("KeyA", "A")));
    }

    [Fact]
    public void Console_command_for_a_named_key_carries_vk_scan_and_extended_flag()
    {
        var command = ConsoleInputInjector.BuildCommand(Key("ArrowLeft", "ArrowLeft"));
        var parts = command.Split(' ');

        Assert.Equal("K", parts[0]);
        Assert.Equal("1", parts[1]);                       // key down
        Assert.Equal("37", parts[2]);                      // VK_LEFT
        Assert.Equal("1", parts[4]);                       // extended
    }

    [Fact]
    public void Console_command_encodes_modifier_state()
    {
        var command = ConsoleInputInjector.BuildCommand(Key("KeyC", "c") with { Ctrl = true });
        var controlState = uint.Parse(command.Split(' ')[5]);

        Assert.Equal(0x0008u, controlState & 0x0008u);      // LEFT_CTRL_PRESSED
    }

    [Fact]
    public void Console_command_is_empty_for_an_unmappable_key()
    {
        Assert.Equal(string.Empty, ConsoleInputInjector.BuildCommand(Key("Unidentified", "Unidentified")));
    }

    [Fact]
    public void Printable_key_up_events_are_dropped_for_consoles()
    {
        // The console input buffer has no key-up concept for text; replaying both halves would
        // double every character typed.
        var up = Key("KeyA", "a") with { Type = KeyEventType.Up };
        Assert.Equal("C 97", ConsoleInputInjector.BuildCommand(up));
    }

    private static KeyEventDto Key(string code, string key) => new()
    {
        WindowId = "1",
        Type = KeyEventType.Down,
        Code = code,
        Key = key,
    };
}
