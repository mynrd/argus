using Argus.Server.Input;

namespace Argus.Server.Tests;

public class KeyMapperTests
{
    [Theory]
    [InlineData("KeyA", 0x41)]
    [InlineData("KeyZ", 0x5A)]
    [InlineData("Digit0", 0x30)]
    [InlineData("Digit9", 0x39)]
    [InlineData("Enter", 0x0D)]
    [InlineData("Escape", 0x1B)]
    [InlineData("Space", 0x20)]
    [InlineData("Backspace", 0x08)]
    [InlineData("Tab", 0x09)]
    [InlineData("ArrowLeft", 0x25)]
    [InlineData("ArrowUp", 0x26)]
    [InlineData("F1", 0x70)]
    [InlineData("F12", 0x7B)]
    [InlineData("F24", 0x87)]
    [InlineData("Numpad0", 0x60)]
    [InlineData("Semicolon", 0xBA)]
    [InlineData("Slash", 0xBF)]
    [InlineData("ControlLeft", 0xA2)]
    [InlineData("ShiftRight", 0xA1)]
    public void Map_resolves_known_codes_to_virtual_keys(string code, int expectedVk)
    {
        var mapped = KeyMapper.Map(code);

        Assert.True(mapped.IsValid);
        Assert.Equal(expectedVk, mapped.VirtualKey);
    }

    [Theory]
    [InlineData("")]
    [InlineData("NotAKey")]
    [InlineData("Unidentified")]
    public void Map_returns_invalid_for_unknown_codes(string code)
    {
        Assert.False(KeyMapper.Map(code).IsValid);
    }

    [Theory]
    [InlineData("ArrowLeft")]
    [InlineData("ArrowRight")]
    [InlineData("Delete")]
    [InlineData("Home")]
    [InlineData("End")]
    [InlineData("PageUp")]
    [InlineData("NumpadDivide")]
    [InlineData("ControlRight")]
    public void Extended_keys_are_flagged(string code)
    {
        // Without the extended flag the target reads these as their numpad twins - arrows become
        // digits and Delete becomes '.'.
        Assert.True(KeyMapper.Map(code).IsExtended);
    }

    [Theory]
    [InlineData("KeyA")]
    [InlineData("Digit1")]
    [InlineData("ControlLeft")]
    [InlineData("F5")]
    public void Non_extended_keys_are_not_flagged(string code)
    {
        Assert.False(KeyMapper.Map(code).IsExtended);
    }

    [Fact]
    public void BuildLParam_sets_repeat_count_and_scan_code_on_key_down()
    {
        var key = KeyMapper.Map("KeyA");
        nint lParam = KeyMapper.BuildLParam(key, isKeyUp: false);

        Assert.Equal(1, lParam & 0xFFFF);                          // repeat count
        Assert.Equal(key.ScanCode, (lParam >> 16) & 0xFF);         // scan code
        Assert.Equal(0, (lParam >> 30) & 1);                       // previous state
        Assert.Equal(0, (lParam >> 31) & 1);                       // transition state
    }

    [Fact]
    public void BuildLParam_sets_transition_bits_on_key_up()
    {
        var key = KeyMapper.Map("KeyA");
        nint lParam = KeyMapper.BuildLParam(key, isKeyUp: true);

        Assert.Equal(1, (lParam >> 30) & 1);
        Assert.Equal(1, (lParam >> 31) & 1);
    }

    [Fact]
    public void BuildLParam_sets_the_extended_bit_for_extended_keys()
    {
        var arrow = KeyMapper.Map("ArrowLeft");
        var letter = KeyMapper.Map("KeyA");

        Assert.Equal(1, (KeyMapper.BuildLParam(arrow, false) >> 24) & 1);
        Assert.Equal(0, (KeyMapper.BuildLParam(letter, false) >> 24) & 1);
    }

    [Fact]
    public void Every_letter_and_digit_position_maps()
    {
        for (char c = 'A'; c <= 'Z'; c++) Assert.True(KeyMapper.Map($"Key{c}").IsValid);
        for (int d = 0; d <= 9; d++) Assert.True(KeyMapper.Map($"Digit{d}").IsValid);
        for (int f = 1; f <= 24; f++) Assert.True(KeyMapper.Map($"F{f}").IsValid);
    }
}
