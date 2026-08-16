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
    private readonly ILogger<ForegroundInjector> _log;

    public ForegroundInjector(ILogger<ForegroundInjector> log) => _log = log;

    public string Name => "sendinput";

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
