using Argus.Server.Interop;

namespace Argus.Server.Input;

/// <summary>Resolved Win32 form of a browser key event.</summary>
public readonly record struct MappedKey(ushort VirtualKey, ushort ScanCode, bool IsExtended)
{
    public bool IsValid => VirtualKey != 0;
}

/// <summary>
/// Maps a browser <c>KeyboardEvent.code</c> (physical key position) onto a Win32 virtual-key code.
/// Using <c>code</c> rather than <c>key</c> keeps the mapping layout-independent and testable:
/// "KeyA" is always VK_A regardless of Shift or the viewer's keyboard layout.
/// </summary>
public static class KeyMapper
{
    private static readonly Dictionary<string, ushort> CodeToVk = BuildTable();

    /// <summary>
    /// Keys that live on the "extended" part of the keyboard. Without the extended flag the target
    /// reads numpad equivalents - arrows become numpad digits, Delete becomes '.', and so on.
    /// </summary>
    private static readonly HashSet<string> ExtendedCodes = new(StringComparer.Ordinal)
    {
        "Insert", "Delete", "Home", "End", "PageUp", "PageDown",
        "ArrowUp", "ArrowDown", "ArrowLeft", "ArrowRight",
        "NumLock", "NumpadDivide", "NumpadEnter",
        "ControlRight", "AltRight", "PrintScreen",
    };

    public static MappedKey Map(string code)
    {
        if (string.IsNullOrEmpty(code) || !CodeToVk.TryGetValue(code, out ushort vk))
        {
            return default;
        }

        bool extended = ExtendedCodes.Contains(code);
        ushort scan = (ushort)NativeMethods.MapVirtualKey(vk, NativeMethods.MAPVK_VK_TO_VSC);
        return new MappedKey(vk, scan, extended);
    }

    /// <summary>lParam layout for WM_KEYDOWN / WM_KEYUP (see the WM_KEYDOWN docs).</summary>
    public static nint BuildLParam(MappedKey key, bool isKeyUp)
    {
        nint lParam = 1;                                   // bits 0-15: repeat count
        lParam |= (nint)key.ScanCode << 16;                // bits 16-23: scan code
        if (key.IsExtended) lParam |= 1 << 24;             // bit 24: extended key
        if (isKeyUp) lParam |= (nint)1 << 30 | (nint)1 << 31;  // bits 30/31: previous + transition
        return lParam;
    }

    private static Dictionary<string, ushort> BuildTable()
    {
        var map = new Dictionary<string, ushort>(StringComparer.Ordinal)
        {
            ["Backspace"] = 0x08,
            ["Tab"] = 0x09,
            ["Enter"] = 0x0D,
            ["NumpadEnter"] = 0x0D,
            ["ShiftLeft"] = 0xA0,
            ["ShiftRight"] = 0xA1,
            ["ControlLeft"] = 0xA2,
            ["ControlRight"] = 0xA3,
            ["AltLeft"] = 0xA4,
            ["AltRight"] = 0xA5,
            ["Pause"] = 0x13,
            ["CapsLock"] = 0x14,
            ["Escape"] = 0x1B,
            ["Space"] = 0x20,
            ["PageUp"] = 0x21,
            ["PageDown"] = 0x22,
            ["End"] = 0x23,
            ["Home"] = 0x24,
            ["ArrowLeft"] = 0x25,
            ["ArrowUp"] = 0x26,
            ["ArrowRight"] = 0x27,
            ["ArrowDown"] = 0x28,
            ["PrintScreen"] = 0x2C,
            ["Insert"] = 0x2D,
            ["Delete"] = 0x2E,
            ["MetaLeft"] = 0x5B,
            ["MetaRight"] = 0x5C,
            ["ContextMenu"] = 0x5D,
            ["NumpadMultiply"] = 0x6A,
            ["NumpadAdd"] = 0x6B,
            ["NumpadSubtract"] = 0x6D,
            ["NumpadDecimal"] = 0x6E,
            ["NumpadDivide"] = 0x6F,
            ["NumLock"] = 0x90,
            ["ScrollLock"] = 0x91,
            ["Semicolon"] = 0xBA,
            ["Equal"] = 0xBB,
            ["Comma"] = 0xBC,
            ["Minus"] = 0xBD,
            ["Period"] = 0xBE,
            ["Slash"] = 0xBF,
            ["Backquote"] = 0xC0,
            ["BracketLeft"] = 0xDB,
            ["Backslash"] = 0xDC,
            ["BracketRight"] = 0xDD,
            ["Quote"] = 0xDE,
            ["IntlBackslash"] = 0xE2,
        };

        for (char c = 'A'; c <= 'Z'; c++) map[$"Key{c}"] = c;              // VK_A..VK_Z == 'A'..'Z'
        for (int d = 0; d <= 9; d++) map[$"Digit{d}"] = (ushort)('0' + d); // VK_0..VK_9 == '0'..'9'
        for (int d = 0; d <= 9; d++) map[$"Numpad{d}"] = (ushort)(0x60 + d);
        for (int f = 1; f <= 24; f++) map[$"F{f}"] = (ushort)(0x6F + f);   // VK_F1 == 0x70

        return map;
    }
}
