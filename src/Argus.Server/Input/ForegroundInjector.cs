using Argus.Server.Interop;
using Argus.Server.Windows;

namespace Argus.Server.Input;

/// <summary>
/// Brings the target window to the foreground and replays keys with SendInput.
///
/// This is the only backend that is fully faithful - SendInput updates real key state, so
/// Ctrl+C, Alt+Tab-style combos and anything reading GetKeyState behave exactly as if typed at the
/// machine. The cost is that it owns the physical desktop while in use: whatever you type from the
/// browser lands in the foregrounded window, and the target is raised in front of other windows.
///
/// It also drives Windows Terminal, which neither PostMessage nor WriteConsoleInput can reach
/// (its tabs are ConPTY-hosted and have no per-tab HWND of their own).
/// </summary>
public sealed class ForegroundInjector : IInputInjector
{
    /// <summary>How many key events one SendInput call carries when typing a block of text.</summary>
    private const int TextBatchSize = 200;

    private readonly ILogger<ForegroundInjector> _log;

    public ForegroundInjector(ILogger<ForegroundInjector> log) => _log = log;

    /// <summary>
    /// Named, because the hub has to recognise this backend by its result: only SendInput updates
    /// the global key state, so only keys that went this way are worth tracking as held.
    /// </summary>
    public const string BackendName = "sendinput";

    public string Name => BackendName;

    public bool CanHandle(WindowInfo target) => true;

    public bool TrySend(WindowInfo target, KeyEventDto keyEvent)
    {
        var hwnd = (nint)target.Handle;
        if (!NativeMethods.IsWindow(hwnd)) return false;

        if (!Focus(hwnd)) return false;

        var inputs = BuildInputs(keyEvent);
        if (inputs.Length == 0) return false;

        uint sent = NativeMethods.SendInput(
            (uint)inputs.Length, inputs, System.Runtime.InteropServices.Marshal.SizeOf<NativeMethods.INPUT>());

        if (sent != inputs.Length)
        {
            _log.LogWarning("SendInput delivered {Sent}/{Total} events for {Code}",
                sent, inputs.Length, keyEvent.Code);
        }
        return sent > 0;
    }

    /// <summary>
    /// Types a whole string into the target in one go, optionally followed by Enter.
    ///
    /// A block of text sent as individual key events would be one hub round trip and one
    /// SetForegroundWindow per character - a hundred-character command line becomes a hundred
    /// chances for a stray click on the host to steal the focus mid-word. Building every keystroke
    /// up front and handing them to SendInput in batches keeps the whole paste atomic from the
    /// app's point of view.
    /// </summary>
    public InjectionResult TrySendText(WindowInfo target, string text, bool submit)
    {
        var hwnd = (nint)target.Handle;
        if (!NativeMethods.IsWindow(hwnd)) return new InjectionResult(false, Name, "That window is gone");
        if (!Focus(hwnd)) return new InjectionResult(false, Name, "Could not bring that window to the front");

        var inputs = BuildTextInputs(text, submit);
        if (inputs.Length == 0) return new InjectionResult(false, Name, "Nothing to type");

        int size = System.Runtime.InteropServices.Marshal.SizeOf<NativeMethods.INPUT>();

        // In batches, because SendInput is atomic per call: one enormous array is more likely to be
        // rejected outright, and a batch that fails tells us how far the text actually got.
        for (int offset = 0; offset < inputs.Length; offset += TextBatchSize)
        {
            var batch = inputs[offset..Math.Min(offset + TextBatchSize, inputs.Length)];
            uint sent = NativeMethods.SendInput((uint)batch.Length, batch, size);
            if (sent == batch.Length) continue;

            _log.LogWarning("SendInput delivered {Sent}/{Total} text events to {Title}",
                offset + sent, inputs.Length, target.Title);

            return new InjectionResult(
                false,
                Name,
                offset + sent == 0 ? "not delivered" : "Only part of the text was typed");
        }

        return new InjectionResult(true, Name);
    }

    /// <summary>
    /// Every keystroke a block of text is worth, in order.
    ///
    /// Printable characters travel as Unicode so the host's keyboard layout cannot change what
    /// arrives. Tab and the line breaks cannot: KEYEVENTF_UNICODE would hand the app a literal
    /// control character, which a text box shows as a box and a shell ignores, so they go through
    /// as the real keys instead. CRLF is one Enter, not two.
    /// </summary>
    internal static NativeMethods.INPUT[] BuildTextInputs(string? text, bool submit)
    {
        string body = text ?? string.Empty;
        var inputs = new List<NativeMethods.INPUT>(body.Length * 2 + 2);
        bool endedWithNewLine = false;

        for (int i = 0; i < body.Length; i++)
        {
            char character = body[i];
            endedWithNewLine = false;

            if (character is '\r' or '\n')
            {
                // CRLF is one Enter, not two.
                if (character == '\r' && i + 1 < body.Length && body[i + 1] == '\n') i++;
                AddNamedKey(inputs, "Enter");
                endedWithNewLine = true;
                continue;
            }

            if (character == '\t')
            {
                AddNamedKey(inputs, "Tab");
                continue;
            }

            // Anything else a keyboard could not produce is dropped rather than typed as a box:
            // a stray NUL or bell in a paste is noise, not input.
            if (char.IsControl(character)) continue;

            inputs.Add(UnicodeInput(character, up: false));
            inputs.Add(UnicodeInput(character, up: true));
        }

        // The Hit Enter box, skipped when the text already ended in a line break - the box means
        // "finish with Enter", and a second one would submit an empty line after the real one.
        if (submit && !endedWithNewLine) AddNamedKey(inputs, "Enter");

        return [.. inputs];
    }

    private static void AddNamedKey(List<NativeMethods.INPUT> inputs, string code)
    {
        var mapped = KeyMapper.Map(code);
        if (!mapped.IsValid) return;

        uint flags = mapped.IsExtended ? NativeMethods.KEYEVENTF_EXTENDEDKEY : 0;
        inputs.Add(KeyInput(mapped.VirtualKey, mapped.ScanCode, flags));
        inputs.Add(KeyInput(mapped.VirtualKey, mapped.ScanCode, flags | NativeMethods.KEYEVENTF_KEYUP));
    }

    private static NativeMethods.INPUT UnicodeInput(char character, bool up) =>
        KeyInput(0, character, NativeMethods.KEYEVENTF_UNICODE | (up ? NativeMethods.KEYEVENTF_KEYUP : 0));

    private static NativeMethods.INPUT KeyInput(ushort vk, ushort scan, uint flags) => new()
    {
        Type = NativeMethods.INPUT_KEYBOARD,
        Union = new NativeMethods.InputUnion
        {
            Keyboard = new NativeMethods.KEYBDINPUT { Vk = vk, Scan = scan, Flags = flags },
        },
    };

    internal static NativeMethods.INPUT[] BuildInputs(KeyEventDto keyEvent)
    {
        bool isUp = keyEvent.Type == KeyEventType.Up;

        // Printable characters go through as Unicode so the exact character the viewer typed
        // appears, regardless of the target machine's keyboard layout.
        if (keyEvent.IsPrintable)
        {
            uint flags = NativeMethods.KEYEVENTF_UNICODE | (isUp ? NativeMethods.KEYEVENTF_KEYUP : 0);
            return
            [
                new NativeMethods.INPUT
                {
                    Type = NativeMethods.INPUT_KEYBOARD,
                    Union = new NativeMethods.InputUnion
                    {
                        Keyboard = new NativeMethods.KEYBDINPUT
                        {
                            Vk = 0,
                            Scan = keyEvent.Key[0],
                            Flags = flags,
                        },
                    },
                },
            ];
        }

        var mapped = KeyMapper.Map(keyEvent.Code);
        if (!mapped.IsValid) return [];

        uint keyFlags = 0;
        if (mapped.IsExtended) keyFlags |= NativeMethods.KEYEVENTF_EXTENDEDKEY;
        if (isUp) keyFlags |= NativeMethods.KEYEVENTF_KEYUP;

        return
        [
            new NativeMethods.INPUT
            {
                Type = NativeMethods.INPUT_KEYBOARD,
                Union = new NativeMethods.InputUnion
                {
                    Keyboard = new NativeMethods.KEYBDINPUT
                    {
                        Vk = mapped.VirtualKey,
                        Scan = mapped.ScanCode,
                        Flags = keyFlags,
                    },
                },
            },
        ];
    }

    /// <summary>
    /// SetForegroundWindow refuses calls from a process that does not already own the foreground.
    ///
    /// Attaching to the *foreground* thread alone is not enough, which is easy to miss because it
    /// works for plenty of apps. Measured against VS Code with Chrome in front: attaching only to
    /// Chrome's thread, SetForegroundWindow returned false and the foreground never changed;
    /// attaching to VS Code's thread as well, it returned true and won immediately. Windows wants
    /// the calling thread sharing an input queue with the window being raised, so the target's
    /// thread has to be in the attachment too.
    ///
    /// BringWindowToTop comes first so the window is already at the top of the Z-order by the time
    /// the foreground change lands, rather than being raised behind whatever was in front.
    /// </summary>
    public bool Focus(nint hwnd)
    {
        if (!NativeMethods.IsWindow(hwnd)) return false;
        if (NativeMethods.GetForegroundWindow() == hwnd) return true;

        if (NativeMethods.IsIconic(hwnd))
        {
            NativeMethods.ShowWindow(hwnd, NativeMethods.SW_RESTORE);
        }

        var foreground = NativeMethods.GetForegroundWindow();
        uint currentThread = NativeMethods.GetCurrentThreadId();
        uint foregroundThread = foreground == nint.Zero
            ? 0
            : NativeMethods.GetWindowThreadProcessId(foreground, out _);
        uint targetThread = NativeMethods.GetWindowThreadProcessId(hwnd, out _);

        bool attachedForeground = foregroundThread != 0
            && foregroundThread != currentThread
            && NativeMethods.AttachThreadInput(currentThread, foregroundThread, true);

        bool attachedTarget = targetThread != 0
            && targetThread != currentThread
            && targetThread != foregroundThread
            && NativeMethods.AttachThreadInput(currentThread, targetThread, true);

        try
        {
            NativeMethods.BringWindowToTop(hwnd);
            NativeMethods.SetForegroundWindow(hwnd);
        }
        finally
        {
            // Detach in the reverse order of attaching.
            if (attachedTarget) NativeMethods.AttachThreadInput(currentThread, targetThread, false);
            if (attachedForeground) NativeMethods.AttachThreadInput(currentThread, foregroundThread, false);
        }

        bool ok = NativeMethods.GetForegroundWindow() == hwnd;
        if (!ok)
        {
            _log.LogWarning("Could not foreground HWND {Hwnd}; keys would land in the wrong window", hwnd);
        }
        return ok;
    }
}
