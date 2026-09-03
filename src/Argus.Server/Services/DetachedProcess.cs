using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;

namespace Argus.Server.Services;

/// <summary>
/// Starts a process that is genuinely on its own - no console, no window, and no tie back to this
/// one.
///
/// <see cref="System.Diagnostics.Process.Start(string)"/> is not enough for the terminal host. The
/// server is normally started from run.ps1 in a console, and Ctrl+C there sends CTRL_C_EVENT to
/// every process in that console's group - which would take the daemon, and every terminal it
/// owns, down with the server. That is precisely what the daemon exists to avoid, so it gets its
/// own process group and no console at all.
///
/// CREATE_BREAKAWAY_FROM_JOB covers the other half: a server launched from something that puts its
/// children in a job (a CI agent, some terminals) would otherwise hand the daemon the job's
/// kill-on-close, and the daemon would die with the shell that started the server. A job that
/// refuses breakaway makes CreateProcess fail, so that flag is dropped and the call retried rather
/// than treated as fatal - a daemon inside a job is still far better than no daemon.
/// </summary>
internal static class DetachedProcess
{
    private const string Kernel32 = "kernel32.dll";

    private const uint DETACHED_PROCESS = 0x00000008;
    private const uint CREATE_NEW_PROCESS_GROUP = 0x00000200;
    private const uint CREATE_BREAKAWAY_FROM_JOB = 0x01000000;
    private const uint CREATE_UNICODE_ENVIRONMENT = 0x00000400;

    /// <summary>
    /// Launches <paramref name="executablePath"/> and returns its pid. The handles Windows hands
    /// back are closed immediately: this process never waits on the child and must not keep it in
    /// a zombie state after it exits.
    /// </summary>
    /// <exception cref="Win32Exception">The process could not be started.</exception>
    public static int Start(string executablePath, string? workingDirectory = null)
    {
        uint flags = DETACHED_PROCESS | CREATE_NEW_PROCESS_GROUP | CREATE_UNICODE_ENVIRONMENT;

        if (TryStart(executablePath, workingDirectory, flags | CREATE_BREAKAWAY_FROM_JOB, out int pid)) return pid;
        if (TryStart(executablePath, workingDirectory, flags, out pid)) return pid;

        throw new Win32Exception(Marshal.GetLastWin32Error(), $"Could not start {executablePath}.");
    }

    private static bool TryStart(string executablePath, string? workingDirectory, uint flags, out int processId)
    {
        var startup = new STARTUPINFO { cb = Marshal.SizeOf<STARTUPINFO>() };
        var commandLine = new StringBuilder($"\"{executablePath}\"");

        bool started = CreateProcess(
            null,
            commandLine,
            0,
            0,
            false,
            flags,
            0,
            workingDirectory ?? Path.GetDirectoryName(executablePath),
            ref startup,
            out var info);

        if (!started)
        {
            processId = 0;
            return false;
        }

        CloseHandle(info.hThread);
        CloseHandle(info.hProcess);
        processId = info.dwProcessId;
        return true;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct STARTUPINFO
    {
        public int cb;
        public nint lpReserved;
        public nint lpDesktop;
        public nint lpTitle;
        public int dwX;
        public int dwY;
        public int dwXSize;
        public int dwYSize;
        public int dwXCountChars;
        public int dwYCountChars;
        public int dwFillAttribute;
        public int dwFlags;
        public short wShowWindow;
        public short cbReserved2;
        public nint lpReserved2;
        public nint hStdInput;
        public nint hStdOutput;
        public nint hStdError;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PROCESS_INFORMATION
    {
        public nint hProcess;
        public nint hThread;
        public int dwProcessId;
        public int dwThreadId;
    }

    [DllImport(Kernel32, EntryPoint = "CreateProcessW", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreateProcess(
        string? applicationName,
        StringBuilder commandLine,
        nint processAttributes,
        nint threadAttributes,
        [MarshalAs(UnmanagedType.Bool)] bool inheritHandles,
        uint creationFlags,
        nint environment,
        string? currentDirectory,
        ref STARTUPINFO startupInfo,
        out PROCESS_INFORMATION processInformation);

    [DllImport(Kernel32, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(nint handle);
}
