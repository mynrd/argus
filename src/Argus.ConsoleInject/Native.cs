using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace Argus.ConsoleInject;

internal static class Native
{
    private const string Kernel32 = "kernel32.dll";

    [DllImport(Kernel32, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool AttachConsole(uint processId);

    [DllImport(Kernel32, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool FreeConsole();

    [DllImport(Kernel32)]
    internal static extern nint GetConsoleWindow();

    [DllImport(Kernel32, EntryPoint = "CreateFileW", SetLastError = true, CharSet = CharSet.Unicode)]
    internal static extern SafeFileHandle CreateFile(
        string fileName, uint access, uint shareMode, nint security,
        uint creationDisposition, uint flags, nint template);

    [DllImport(Kernel32, EntryPoint = "WriteConsoleInputW", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool WriteConsoleInput(
        SafeFileHandle consoleInput, INPUT_RECORD[] buffer, uint length, out uint written);

    internal const uint GENERIC_READ = 0x80000000;
    internal const uint GENERIC_WRITE = 0x40000000;
    internal const uint FILE_SHARE_READ = 0x00000001;
    internal const uint FILE_SHARE_WRITE = 0x00000002;
    internal const uint OPEN_EXISTING = 3;

    internal const ushort KEY_EVENT = 0x0001;

    internal const uint RIGHT_ALT_PRESSED = 0x0001;
    internal const uint LEFT_ALT_PRESSED = 0x0002;
    internal const uint RIGHT_CTRL_PRESSED = 0x0004;
    internal const uint LEFT_CTRL_PRESSED = 0x0008;
    internal const uint SHIFT_PRESSED = 0x0010;
    internal const uint ENHANCED_KEY = 0x0100;

    /// <summary>
    /// EventType is a WORD followed by a union whose first member starts with a 4-byte BOOL, so the
    /// struct aligns to 4 and KeyEvent sits at offset 4. Only KEY_EVENT is ever written here.
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    internal struct INPUT_RECORD
    {
        public ushort EventType;
        private readonly ushort _padding;
        public KEY_EVENT_RECORD KeyEvent;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct KEY_EVENT_RECORD
    {
        public int bKeyDown;
        public ushort wRepeatCount;
        public ushort wVirtualKeyCode;
        public ushort wVirtualScanCode;
        public ushort UnicodeChar;
        public uint dwControlKeyState;
    }
}
