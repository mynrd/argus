using System.Runtime.InteropServices;
using System.Text;

namespace Argus.Server.Interop;

/// <summary>
/// Raw Win32 entry points used for window discovery, capture and input injection.
/// Kept deliberately thin - no policy here, only P/Invoke.
/// </summary>
internal static partial class NativeMethods
{
    private const string User32 = "user32.dll";
    private const string Kernel32 = "kernel32.dll";
    private const string Gdi32 = "gdi32.dll";
    private const string Dwmapi = "dwmapi.dll";
    private const string IpHlpApi = "iphlpapi.dll";

    // ---------------------------------------------------------------- windows

    internal delegate bool EnumWindowsProc(nint hWnd, nint lParam);

    [LibraryImport(User32, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool EnumWindows(EnumWindowsProc callback, nint lParam);

    [LibraryImport(User32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool IsWindow(nint hWnd);

    [LibraryImport(User32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool IsWindowVisible(nint hWnd);

    [LibraryImport(User32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool IsIconic(nint hWnd);

    // char[]/char are not blittable, and LibraryImport refuses them unless runtime marshalling is
    // disabled assembly-wide. Raw char* keeps the source-generated path without that side effect.
    [LibraryImport(User32, EntryPoint = "GetWindowTextW", SetLastError = true)]
    internal static unsafe partial int GetWindowText(nint hWnd, char* text, int maxCount);

    [LibraryImport(User32, EntryPoint = "GetWindowTextLengthW", SetLastError = true)]
    internal static partial int GetWindowTextLength(nint hWnd);

    [LibraryImport(User32, EntryPoint = "GetClassNameW", SetLastError = true)]
    internal static unsafe partial int GetClassName(nint hWnd, char* name, int maxCount);

    [LibraryImport(User32, SetLastError = true)]
    internal static partial uint GetWindowThreadProcessId(nint hWnd, out uint processId);

    [LibraryImport(User32, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool GetWindowRect(nint hWnd, out RECT rect);

    [LibraryImport(User32, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool GetClientRect(nint hWnd, out RECT rect);

    [LibraryImport(User32, EntryPoint = "GetWindowLongPtrW", SetLastError = true)]
    internal static partial nint GetWindowLongPtr(nint hWnd, int index);

    [LibraryImport(User32)]
    internal static partial nint GetAncestor(nint hWnd, uint flags);

    [LibraryImport(User32)]
    internal static partial nint GetShellWindow();

    [LibraryImport(User32)]
    internal static partial nint GetForegroundWindow();

    [LibraryImport(User32, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool SetForegroundWindow(nint hWnd);

    [LibraryImport(User32, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool BringWindowToTop(nint hWnd);

    [LibraryImport(User32, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool SetWindowPos(
        nint hWnd, nint insertAfter, int x, int y, int cx, int cy, uint flags);

    [LibraryImport(User32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool IsZoomed(nint hWnd);

    [LibraryImport(User32, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool ShowWindow(nint hWnd, int cmdShow);

    [LibraryImport(User32, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool AttachThreadInput(uint idAttach, uint idAttachTo, [MarshalAs(UnmanagedType.Bool)] bool attach);

    [LibraryImport(Kernel32)]
    internal static partial uint GetCurrentThreadId();

    /// <summary>
    /// Only meaningful for the calling thread's input queue - hence the AttachThreadInput dance in
    /// <see cref="Input.WindowMessageInjector"/>.
    /// </summary>
    [LibraryImport(User32)]
    internal static partial nint GetFocus();

    [LibraryImport(User32)]
    internal static partial nint RealChildWindowFromPoint(nint parent, POINT point);

    [LibraryImport(User32, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool ScreenToClient(nint hWnd, ref POINT point);

    [StructLayout(LayoutKind.Sequential)]
    internal struct POINT
    {
        public int X;
        public int Y;
    }

    /// <summary>
    /// Gives the HWND that actually owns keyboard focus inside the target's thread. Posting to the
    /// top-level window usually does nothing, because real apps put focus on a child control.
    /// </summary>
    [LibraryImport(User32, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool GetGUIThreadInfo(uint threadId, ref GUITHREADINFO info);

    [StructLayout(LayoutKind.Sequential)]
    internal struct GUITHREADINFO
    {
        public int cbSize;
        public uint flags;
        public nint hwndActive;
        public nint hwndFocus;
        public nint hwndCapture;
        public nint hwndMenuOwner;
        public nint hwndMoveSize;
        public nint hwndCaret;
        public RECT rcCaret;
    }

    internal const int GWL_STYLE = -16;
    internal const int GWL_EXSTYLE = -20;
    internal const long WS_VISIBLE = 0x10000000L;
    internal const long WS_CHILD = 0x40000000L;
    internal const long WS_EX_TOOLWINDOW = 0x00000080L;
    internal const long WS_EX_NOREDIRECTIONBITMAP = 0x00200000L;
    internal const uint GA_ROOT = 2;
    internal const uint GA_ROOTOWNER = 3;
    internal const int SW_RESTORE = 9;

    internal const uint SWP_NOMOVE = 0x0002;
    internal const uint SWP_NOZORDER = 0x0004;
    internal const uint SWP_NOACTIVATE = 0x0010;

    // ------------------------------------------------------------------- dwm

    [LibraryImport(Dwmapi)]
    internal static partial int DwmGetWindowAttribute(nint hWnd, int attribute, out int value, int size);

    [LibraryImport(Dwmapi)]
    internal static partial int DwmGetWindowAttribute(nint hWnd, int attribute, out RECT value, int size);

    internal const int DWMWA_EXTENDED_FRAME_BOUNDS = 9;
    internal const int DWMWA_CLOAKED = 14;

    // --------------------------------------------------------------- capture

    [LibraryImport(User32, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool PrintWindow(nint hWnd, nint hdcBlt, uint flags);

    internal const uint PW_CLIENTONLY = 0x00000001;
    internal const uint PW_RENDERFULLCONTENT = 0x00000002;

    [LibraryImport(User32, SetLastError = true)]
    internal static partial nint GetWindowDC(nint hWnd);

    [LibraryImport(User32)]
    internal static partial int ReleaseDC(nint hWnd, nint hdc);

    [LibraryImport(Gdi32, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool BitBlt(nint hdcDest, int xDest, int yDest, int w, int h,
                                        nint hdcSrc, int xSrc, int ySrc, uint rop);

    internal const uint SRCCOPY = 0x00CC0020;
    internal const uint CAPTUREBLT = 0x40000000;

    // ------------------------------------------------------------------- dpi

    [LibraryImport(User32, SetLastError = true)]
    internal static partial nint SetThreadDpiAwarenessContext(nint context);

    [LibraryImport(User32, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool SetProcessDpiAwarenessContext(nint context);

    internal static readonly nint DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2 = -4;

    // ----------------------------------------------------------------- input

    [LibraryImport(User32, EntryPoint = "PostMessageW", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool PostMessage(nint hWnd, uint msg, nint wParam, nint lParam);

    [DllImport(User32, EntryPoint = "SendMessageTimeoutW", SetLastError = true)]
    internal static extern nint SendMessageTimeout(nint hWnd, uint msg, nint wParam, nint lParam,
                                                   uint flags, uint timeoutMs, out nint result);

    [DllImport(User32, SetLastError = true)]
    internal static extern uint SendInput(uint count, INPUT[] inputs, int size);

    [LibraryImport(User32, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool SetCursorPos(int x, int y);

    [LibraryImport(User32, EntryPoint = "MapVirtualKeyW")]
    internal static partial uint MapVirtualKey(uint code, uint mapType);

    [LibraryImport(User32, EntryPoint = "VkKeyScanW")]
    internal static partial short VkKeyScan(ushort ch);

    /// <summary>
    /// Physical key state, independent of any window's message queue. The high bit (0x8000) means
    /// the key is down right now - which is exactly what a stuck modifier looks like.
    /// </summary>
    [LibraryImport(User32)]
    internal static partial short GetAsyncKeyState(int vk);

    /// <summary>
    /// Key state as the *calling thread's* queue last saw it. Not a substitute for
    /// <see cref="GetAsyncKeyState"/> here - this process pumps no input - but it costs nothing as
    /// a second opinion when deciding whether a modifier was really stuck.
    /// </summary>
    [LibraryImport(User32)]
    internal static partial short GetKeyState(int vk);

    internal const uint MAPVK_VK_TO_VSC = 0;
    internal const uint MAPVK_VK_TO_VSC_EX = 4;

    internal const uint WM_CLOSE = 0x0010;
    internal const uint WM_KEYDOWN = 0x0100;
    internal const uint WM_KEYUP = 0x0101;
    internal const uint WM_CHAR = 0x0102;
    internal const uint WM_SYSKEYDOWN = 0x0104;
    internal const uint WM_SYSKEYUP = 0x0105;
    internal const uint WM_SYSCHAR = 0x0106;
    internal const uint WM_MOUSEMOVE = 0x0200;
    internal const uint WM_LBUTTONDOWN = 0x0201;
    internal const uint WM_LBUTTONUP = 0x0202;
    internal const uint WM_RBUTTONDOWN = 0x0204;
    internal const uint WM_RBUTTONUP = 0x0205;
    internal const uint WM_MOUSEWHEEL = 0x020A;

    internal const uint SMTO_ABORTIFHUNG = 0x0002;

    [StructLayout(LayoutKind.Sequential)]
    internal struct RECT
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;

        public readonly int Width => Right - Left;
        public readonly int Height => Bottom - Top;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct INPUT
    {
        public uint Type;
        public InputUnion Union;
    }

    [StructLayout(LayoutKind.Explicit)]
    internal struct InputUnion
    {
        [FieldOffset(0)] public MOUSEINPUT Mouse;
        [FieldOffset(0)] public KEYBDINPUT Keyboard;
        [FieldOffset(0)] public HARDWAREINPUT Hardware;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct MOUSEINPUT
    {
        public int Dx;
        public int Dy;
        public uint MouseData;
        public uint Flags;
        public uint Time;
        public nint ExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct KEYBDINPUT
    {
        public ushort Vk;
        public ushort Scan;
        public uint Flags;
        public uint Time;
        public nint ExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct HARDWAREINPUT
    {
        public uint Msg;
        public ushort ParamL;
        public ushort ParamH;
    }

    internal const uint INPUT_MOUSE = 0;
    internal const uint INPUT_KEYBOARD = 1;
    internal const uint KEYEVENTF_EXTENDEDKEY = 0x0001;
    internal const uint KEYEVENTF_KEYUP = 0x0002;
    internal const uint KEYEVENTF_UNICODE = 0x0004;
    internal const uint KEYEVENTF_SCANCODE = 0x0008;

    internal const uint MOUSEEVENTF_LEFTDOWN = 0x0002;
    internal const uint MOUSEEVENTF_LEFTUP = 0x0004;
    internal const uint MOUSEEVENTF_RIGHTDOWN = 0x0008;
    internal const uint MOUSEEVENTF_RIGHTUP = 0x0010;
    internal const uint MOUSEEVENTF_MIDDLEDOWN = 0x0020;
    internal const uint MOUSEEVENTF_MIDDLEUP = 0x0040;
    internal const uint MOUSEEVENTF_WHEEL = 0x0800;

    // ------------------------------------------------------------------ tcp

    /// <summary>
    /// Listening TCP sockets plus the pid that owns each one. Needs no elevation for
    /// TCP_TABLE_OWNER_PID_LISTENER; the connection tables would.
    /// </summary>
    [LibraryImport(IpHlpApi)]
    internal static partial uint GetExtendedTcpTable(
        nint table,
        ref int size,
        [MarshalAs(UnmanagedType.Bool)] bool sorted,
        int addressFamily,
        int tableClass,
        uint reserved);

    internal const int AF_INET = 2;
    internal const int AF_INET6 = 23;
    internal const int TCP_TABLE_OWNER_PID_LISTENER = 3;
    internal const uint ERROR_INSUFFICIENT_BUFFER = 122;

    [StructLayout(LayoutKind.Sequential)]
    internal struct MIB_TCPROW_OWNER_PID
    {
        public uint State;
        public uint LocalAddr;
        /// <summary>Network byte order in the low two bytes; the high two are undefined.</summary>
        public uint LocalPort;
        public uint RemoteAddr;
        public uint RemotePort;
        public uint OwningPid;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal unsafe struct MIB_TCP6ROW_OWNER_PID
    {
        public fixed byte LocalAddr[16];
        public uint LocalScopeId;
        public uint LocalPort;
        public fixed byte RemoteAddr[16];
        public uint RemoteScopeId;
        public uint RemotePort;
        public uint State;
        public uint OwningPid;
    }

    /// <summary>Undoes the network byte order the TCP table stores ports in.</summary>
    internal static int HostPort(uint tablePort) =>
        (int)(((tablePort & 0xFF) << 8) | ((tablePort >> 8) & 0xFF));

    internal static unsafe string GetWindowTitle(nint hWnd)
    {
        int len = GetWindowTextLength(hWnd);
        if (len <= 0) return string.Empty;

        // Titles change between the length query and the read, so cap and re-check.
        int capacity = Math.Min(len + 1, 1024);
        Span<char> buf = stackalloc char[capacity];
        fixed (char* p = buf)
        {
            int written = GetWindowText(hWnd, p, capacity);
            return written <= 0 ? string.Empty : new string(buf[..written]);
        }
    }

    internal static unsafe string GetWindowClass(nint hWnd)
    {
        Span<char> buf = stackalloc char[256];
        fixed (char* p = buf)
        {
            int written = GetClassName(hWnd, p, buf.Length);
            return written <= 0 ? string.Empty : new string(buf[..written]);
        }
    }
}
