using System.Runtime.InteropServices;
using Argus.Server.Interop;

namespace Argus.Server.Input;

/// <summary>What one Release keys pass found down and lifted. Names are for the banner.</summary>
public sealed record ReleaseReport(IReadOnlyList<string> Released, string? Reason = null);

/// <summary>
/// Lifts every modifier and every stuck key off the host keyboard.
///
/// The failure this exists for: a viewer locks Ctrl on the key pad, then the tab closes, the
/// connection drops, or the window it was aimed at dies. Nothing sends the matching key-up, so Ctrl
/// stays physically down on the machine and every subsequent keystroke over there - typed by Argus
/// or by whoever is sitting at it - becomes a shortcut.
///
/// Unlike every other injection path, this deliberately does not focus anything. A key-up updates
/// the global async key state whatever is in front, so there is no target to pick, and demanding
/// one would make the button useless in exactly the case you want it: nothing attached, or the
/// window that stranded the key already gone.
/// </summary>
public sealed class KeyReleaser
{
    /// <summary>VK_F13. Held-Win mitigation - see <see cref="BuildReleaseInputs"/>.</summary>
    internal const ushort VkF13 = 0x7C;

    internal const ushort VkLWin = 0x5B;
    internal const ushort VkRWin = 0x5C;

    /// <summary>
    /// Released every time, whether or not they read as down.
    ///
    /// Both sides of each modifier, because a stuck Right Shift is invisible if you only release
    /// Left. The three generic VKs because apps calling GetKeyState(VK_CONTROL) read those rather
    /// than the sided ones. Win last, so the F13 spacer and every other key-up are already through
    /// by the time the shell has a bare Win tap to react to.
    /// </summary>
    internal static readonly ushort[] AlwaysReleased =
    [
        0xA0, 0xA1,   // LSHIFT, RSHIFT
        0xA2, 0xA3,   // LCONTROL, RCONTROL
        0xA4, 0xA5,   // LMENU, RMENU
        0x10,         // SHIFT
        0x11,         // CONTROL
        0x12,         // MENU
        VkLWin, VkRWin,
    ];

    /// <summary>
    /// Toggles, not latches. Blind-releasing them does nothing at all, and blind-tapping them
    /// flips a state the user may well have wanted on - so the sweep steps over them.
    /// </summary>
    private static readonly ushort[] Toggles = [0x14, 0x90, 0x91];   // CAPITAL, NUMLOCK, SCROLL

    /// <summary>The buttons a stranded drag leaves down. Same failure mode, different device.</summary>
    private static readonly (ushort Vk, uint UpFlag)[] MouseButtons =
    [
        (0x01, NativeMethods.MOUSEEVENTF_LEFTUP),
        (0x02, NativeMethods.MOUSEEVENTF_RIGHTUP),
        (0x04, NativeMethods.MOUSEEVENTF_MIDDLEUP),
    ];

    private readonly ILogger<KeyReleaser> _log;

    public KeyReleaser(ILogger<KeyReleaser> log) => _log = log;

    /// <summary>
    /// The SendInput call, as a seam. Swapped in tests so running the suite does not fire real
    /// key-ups and button-ups at the machine running it - a stray LBUTTONUP would land in whatever
    /// the developer had in front.
    /// </summary>
    internal Func<NativeMethods.INPUT[], uint> Sender { get; set; } = static inputs =>
        NativeMethods.SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<NativeMethods.INPUT>());

    /// <summary>
    /// Scans the physical keyboard, lifts everything found down plus the modifiers unconditionally,
    /// and reports what was actually stuck.
    ///
    /// The report names only keys that read as down. Telling you nothing was stuck is as useful as
    /// telling you what was fixed - it means the problem is somewhere else.
    /// </summary>
    public ReleaseReport ReleaseAll()
    {
        var stuck = Scan();
        var names = stuck.Select(NameOf).Distinct(StringComparer.Ordinal).ToArray();

        // Union, not just what was found: the modifiers go up whether or not they read as down.
        var toRelease = AlwaysReleased
            .Concat(stuck.Where(vk => !AlwaysReleased.Contains(vk) && !IsMouseButton(vk)))
            .ToArray();

        string? reason = Lift([.. toRelease, .. stuck.Where(IsMouseButton)]);

        if (names.Length == 0) _log.LogInformation("Release keys: nothing was down");
        else _log.LogInformation("Release keys: lifted {Keys}", string.Join(", ", names));

        return new ReleaseReport(names, reason);
    }

    /// <summary>
    /// Lifts exactly these virtual keys and mouse buttons, with no scan of the keyboard first.
    ///
    /// This is the per-connection cleanup path: the hub knows precisely what a viewer pressed and
    /// never released, so it says so rather than sweeping the whole keyboard. Sweeping on every
    /// disconnect would lift keys the person at the machine is genuinely holding down.
    /// </summary>
    public ReleaseReport Release(IReadOnlyList<ushort> vks)
    {
        if (vks is null || vks.Count == 0) return new ReleaseReport([]);

        var names = vks.Select(NameOf).Distinct(StringComparer.Ordinal).ToArray();
        return new ReleaseReport(names, Lift(vks));
    }

    /// <summary>
    /// Buttons first, then the keyboard. Returns the first failure, or null.
    ///
    /// The order matters for a drag that was stranded with a modifier held: releasing Ctrl first
    /// turns a Ctrl-drag into a plain drop, which is not the gesture the viewer was making when it
    /// vanished. Dropping first and clearing the modifier after finishes what was actually started.
    /// </summary>
    private string? Lift(IReadOnlyList<ushort> vks)
    {
        string? reason = null;

        var mouseInputs = BuildMouseReleaseInputs(vks);
        if (mouseInputs.Length > 0 && !Send(mouseInputs)) reason = "Windows rejected the button release";

        var keyInputs = BuildReleaseInputs([.. vks.Where(vk => !IsMouseButton(vk))]);
        if (keyInputs.Length > 0 && !Send(keyInputs)) reason ??= "Windows rejected some of the key-ups";

        return reason;
    }

    /// <summary>
    /// Every virtual key that reads as physically down right now, in VK order.
    ///
    /// The sweep is what covers the stuck-letter case - a key repeating into whatever has focus -
    /// rather than only the modifiers. Mouse buttons are scanned so a stranded drag comes back in
    /// the same pass; the toggle keys are not.
    /// </summary>
    private static ushort[] Scan()
    {
        var down = new List<ushort>();

        foreach (var (vk, _) in MouseButtons)
        {
            if (IsDown(vk)) down.Add(vk);
        }

        for (ushort vk = 0x08; vk <= 0xFE; vk++)
        {
            if (Toggles.Contains(vk)) continue;
            if (IsDown(vk)) down.Add(vk);
        }

        return [.. down];
    }

    /// <summary>
    /// GetAsyncKeyState is the real answer - it reads physical state, whatever window is in front.
    /// GetKeyState is a second opinion only: this process pumps no input, so its queue state is
    /// almost always "up", which means it can add a true positive but never invent one.
    /// </summary>
    private static bool IsDown(ushort vk) =>
        (NativeMethods.GetAsyncKeyState(vk) & 0x8000) != 0
        || (NativeMethods.GetKeyState(vk) & 0x8000) != 0;

    private static bool IsMouseButton(ushort vk) => MouseButtons.Any(b => b.Vk == vk);

    /// <summary>
    /// The key-up events one release pass is worth, in order. Pure, so the ordering rules below are
    /// testable without touching the host keyboard.
    ///
    /// The Win gotcha: injecting a lone LWIN key-up when Windows saw the key-down opens the Start
    /// menu - you would fix the stuck key and get Start in your face. So if either Win key is in the
    /// batch, a full F13 press goes in first, while Win is still held, and the shell sees a combo
    /// rather than a bare tap. F13 is not a Win shortcut and does nothing on its own in essentially
    /// any app. See the README for the manual test and the Escape fallback if it ever does not
    /// suppress it.
    /// </summary>
    internal static NativeMethods.INPUT[] BuildReleaseInputs(IReadOnlyList<ushort> stuck)
    {
        if (stuck is null || stuck.Count == 0) return [];

        var inputs = new List<NativeMethods.INPUT>(stuck.Count + 2);

        if (stuck.Contains(VkLWin) || stuck.Contains(VkRWin))
        {
            inputs.Add(KeyInput(VkF13, up: false));
            inputs.Add(KeyInput(VkF13, up: true));
        }

        foreach (var vk in stuck) inputs.Add(KeyInput(vk, up: true));

        return [.. inputs];
    }

    /// <summary>Button-up events for whichever mouse buttons the scan found down. Usually none.</summary>
    internal static NativeMethods.INPUT[] BuildMouseReleaseInputs(IReadOnlyList<ushort> stuck)
    {
        if (stuck is null || stuck.Count == 0) return [];

        return [.. MouseButtons
            .Where(b => stuck.Contains(b.Vk))
            .Select(b => new NativeMethods.INPUT
            {
                Type = NativeMethods.INPUT_MOUSE,
                Union = new NativeMethods.InputUnion
                {
                    Mouse = new NativeMethods.MOUSEINPUT { Flags = b.UpFlag },
                },
            })];
    }

    /// <summary>
    /// Scan code and the extended flag come from MAPVK_VK_TO_VSC_EX, which returns 0xE0xx for the
    /// extended keys. Without the flag, a Right Ctrl key-up releases Left Ctrl instead and the
    /// stuck one stays down.
    /// </summary>
    private static NativeMethods.INPUT KeyInput(ushort vk, bool up)
    {
        uint mapped = NativeMethods.MapVirtualKey(vk, NativeMethods.MAPVK_VK_TO_VSC_EX);
        ushort scan = (ushort)(mapped & 0xFF);

        uint flags = up ? NativeMethods.KEYEVENTF_KEYUP : 0;
        if ((mapped & 0xE000) == 0xE000) flags |= NativeMethods.KEYEVENTF_EXTENDEDKEY;

        return new NativeMethods.INPUT
        {
            Type = NativeMethods.INPUT_KEYBOARD,
            Union = new NativeMethods.InputUnion
            {
                Keyboard = new NativeMethods.KEYBDINPUT { Vk = vk, Scan = scan, Flags = flags },
            },
        };
    }

    private bool Send(NativeMethods.INPUT[] inputs)
    {
        uint sent = Sender(inputs);
        if (sent == inputs.Length) return true;

        _log.LogWarning("SendInput delivered {Sent}/{Total} release events", sent, inputs.Length);
        return false;
    }

    /// <summary>
    /// What to call a virtual key in the banner. Both sides collapse onto one name, because
    /// "Released Ctrl" is what you want to read, not "Released LCONTROL, RCONTROL, CONTROL".
    /// </summary>
    internal static string NameOf(ushort vk) => vk switch
    {
        0x01 => "Left button",
        0x02 => "Right button",
        0x04 => "Middle button",
        0x10 or 0xA0 or 0xA1 => "Shift",
        0x11 or 0xA2 or 0xA3 => "Ctrl",
        0x12 or 0xA4 or 0xA5 => "Alt",
        VkLWin or VkRWin => "Win",
        0x08 => "Backspace",
        0x09 => "Tab",
        0x0D => "Enter",
        0x1B => "Esc",
        0x20 => "Space",
        0x21 => "PageUp",
        0x22 => "PageDown",
        0x23 => "End",
        0x24 => "Home",
        0x25 => "Left",
        0x26 => "Up",
        0x27 => "Right",
        0x28 => "Down",
        0x2D => "Insert",
        0x2E => "Delete",
        >= 0x30 and <= 0x39 => ((char)vk).ToString(),
        >= 0x41 and <= 0x5A => ((char)vk).ToString(),
        >= 0x60 and <= 0x69 => $"Numpad {(char)('0' + vk - 0x60)}",
        >= 0x70 and <= 0x87 => $"F{vk - 0x6F}",
        _ => $"VK 0x{vk:X2}",
    };
}
