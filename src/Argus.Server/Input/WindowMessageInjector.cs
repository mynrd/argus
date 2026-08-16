using Argus.Server.Interop;
using Argus.Server.Windows;

namespace Argus.Server.Input;

/// <summary>
/// Delivers keys with PostMessage, without taking desktop focus.
///
/// What this backend can and cannot do - worth knowing before trusting it:
///   - Plain typing and navigation keys are reliable for normal Win32/WinForms/WPF apps.
///   - Modifier combos (Ctrl+C, Alt+F4) are NOT reliable. A posted WM_KEYDOWN does not update the
///     target thread's async key state, so an app that asks GetKeyState(VK_CONTROL) sees the key
///     as up and treats Ctrl+C as a plain 'c'. Use <see cref="InjectionMode.Focus"/> for those.
///   - conhost windows ignore posted key messages entirely; <see cref="ConsoleInputInjector"/>
///     handles those.
/// </summary>
public sealed class WindowMessageInjector : IInputInjector
{
    private readonly ILogger<WindowMessageInjector> _log;

    public WindowMessageInjector(ILogger<WindowMessageInjector> log) => _log = log;

    public string Name => "postmessage";

    public bool CanHandle(WindowInfo target) => !target.IsConsole;

    public bool TrySend(WindowInfo target, KeyEventDto keyEvent)
    {
        var hwnd = ResolveFocusTarget((nint)target.Handle);
        if (hwnd == nint.Zero) return false;

        bool isUp = keyEvent.Type == KeyEventType.Up;

        // Printable keys are delivered as WM_CHAR alone. Posting WM_KEYDOWN as well double-types:
        // controls that run their own key handling (Scintilla, and every rich edit) synthesise the
        // character from the keydown, then receive our WM_CHAR on top of it.
        if (keyEvent.IsPrintable)
        {
            if (isUp) return true;   // the character was already delivered on the down event

            var printable = KeyMapper.Map(keyEvent.Code);
            nint charLParam = printable.IsValid ? KeyMapper.BuildLParam(printable, isKeyUp: false) : 1;
            return NativeMethods.PostMessage(hwnd, NativeMethods.WM_CHAR, keyEvent.Key[0], charLParam);
        }

        var mapped = KeyMapper.Map(keyEvent.Code);
        if (!mapped.IsValid)
        {
            _log.LogDebug("No VK mapping for code {Code}", keyEvent.Code);
            return false;
        }

        nint lParam = KeyMapper.BuildLParam(mapped, isUp);

        // Alt-held keys travel as WM_SYSKEYDOWN/UP, and carry the context-code bit.
        bool sys = keyEvent.Alt;
        if (sys) lParam |= (nint)1 << 29;

        uint downMsg = sys ? NativeMethods.WM_SYSKEYDOWN : NativeMethods.WM_KEYDOWN;
        uint upMsg = sys ? NativeMethods.WM_SYSKEYUP : NativeMethods.WM_KEYUP;

        return NativeMethods.PostMessage(hwnd, isUp ? upMsg : downMsg, mapped.VirtualKey, lParam);
    }

    /// <summary>
    /// Posting to the top-level HWND is usually a no-op, because real apps keep keyboard focus on a
    /// child control (Notepad++ types into a Scintilla child, not into its frame).
    ///
    /// Three strategies, in order:
    ///   1. GetGUIThreadInfo - correct and side-effect free, but hwndFocus is NULL for any thread
    ///      that does not currently own the foreground, which is the normal case for us.
    ///   2. AttachThreadInput + GetFocus - merges our input queue with the target's just long
    ///      enough to read its focus. This is what actually works for background windows.
    ///   3. RealChildWindowFromPoint at the client centre - for apps that report no focus at all.
    /// </summary>
    internal static nint ResolveFocusTarget(nint topLevel)
    {
        if (!NativeMethods.IsWindow(topLevel)) return nint.Zero;

        uint threadId = NativeMethods.GetWindowThreadProcessId(topLevel, out _);
        if (threadId == 0) return topLevel;

        var info = new NativeMethods.GUITHREADINFO
        {
            cbSize = System.Runtime.InteropServices.Marshal.SizeOf<NativeMethods.GUITHREADINFO>(),
        };
        if (NativeMethods.GetGUIThreadInfo(threadId, ref info) && info.hwndFocus != nint.Zero)
        {
            return info.hwndFocus;
        }

        uint currentThread = NativeMethods.GetCurrentThreadId();
        if (threadId != currentThread && NativeMethods.AttachThreadInput(currentThread, threadId, true))
        {
            try
            {
                var focus = NativeMethods.GetFocus();
                if (focus != nint.Zero) return focus;
            }
            finally
            {
                NativeMethods.AttachThreadInput(currentThread, threadId, false);
            }
        }

        return ChildAtClientCentre(topLevel);
    }

    private static nint ChildAtClientCentre(nint topLevel)
    {
        if (!NativeMethods.GetClientRect(topLevel, out var client)) return topLevel;
        if (client.Width <= 0 || client.Height <= 0) return topLevel;

        var centre = new NativeMethods.POINT { X = client.Width / 2, Y = client.Height / 2 };
        var child = NativeMethods.RealChildWindowFromPoint(topLevel, centre);
        return child == nint.Zero ? topLevel : child;
    }
}
