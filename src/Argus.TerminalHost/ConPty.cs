using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace Argus.TerminalHost;

/// <summary>
/// What a terminal needs from the thing running its shell.
///
/// It exists so <see cref="PtySession"/> and <see cref="TerminalRegistry"/> can be tested without
/// a real pseudo console: the only implementation in production is <see cref="ConPtyProcess"/>,
/// and the only other one lives in the test project.
/// </summary>
internal interface IPty : IDisposable
{
    /// <summary>Everything the shell prints. Read until EOF, which arrives when the PTY closes.</summary>
    Stream Output { get; }

    int ProcessId { get; }

    /// <summary>Signalled when the shell exits.</summary>
    WaitHandle Exited { get; }

    /// <summary>The shell's exit code, or null while it is still running.</summary>
    int? ExitCode { get; }

    void Write(string data);

    void Resize(short cols, short rows);

    /// <summary>Ends the shell and everything under it.</summary>
    void Kill();
}

/// <summary>
/// One shell running under a Windows pseudo console.
///
/// ConPTY is the OS object that makes a child process believe it is talking to a real console, so
/// interactive programs behave here exactly as they do in a window - which is the whole point: a
/// terminal that cannot run `claude` or a REPL is a command runner, not a terminal.
///
/// The dance, in the order Windows requires it:
///   1. two anonymous pipes, one each way
///   2. CreatePseudoConsole over the PTY ends, then close our copies of those two ends - conhost
///      dup'd them, and holding them open means the read side never sees EOF
///   3. a process/thread attribute list carrying the HPCON, so CreateProcess attaches the child
///   4. CreateProcess suspended, assign to a kill-on-close job, then resume
///
/// The job object is what makes Kill actually kill: closing the pseudo console takes the shell,
/// but an `npm run dev` the shell started is a grandchild, and without the job it would be left
/// running with nothing pointing at it. Suspended-then-assign closes the window where the shell
/// could spawn something before the job existed.
///
/// Everything here is P/Invoke and nothing here is policy - <see cref="PtySession"/> owns that.
/// </summary>
internal sealed class ConPtyProcess : IPty
{
    private const string Kernel32 = "kernel32.dll";

    private nint _pseudoConsole;
    private nint _process;
    private nint _thread;
    private nint _job;
    private readonly FileStream _input;
    private readonly Lock _writeGate = new();
    private bool _disposed;

    /// <inheritdoc />
    public Stream Output { get; }

    /// <inheritdoc />
    public int ProcessId { get; }

    /// <summary>Signalled when the shell exits. A real process handle, so pid reuse cannot fool it.</summary>
    public WaitHandle Exited { get; }

    private ConPtyProcess(nint pseudoConsole, nint process, nint thread, nint job, int processId, FileStream input, FileStream output)
    {
        _pseudoConsole = pseudoConsole;
        _process = process;
        _thread = thread;
        _job = job;
        _input = input;
        ProcessId = processId;
        Output = output;

        // A process handle is waitable, so it can stand in for an event rather than costing a
        // thread parked in WaitForSingleObject per open terminal.
        Exited = new ManualResetEvent(false) { SafeWaitHandle = new SafeWaitHandle(process, ownsHandle: false) };
    }

    /// <summary>
    /// Starts <paramref name="commandLine"/> attached to a new pseudo console.
    /// </summary>
    /// <param name="environment">The full environment for the child, marker included.</param>
    public static ConPtyProcess Start(
        string commandLine,
        string workingDirectory,
        IReadOnlyDictionary<string, string> environment,
        short cols,
        short rows)
    {
        nint inputRead = 0, inputWrite = 0, outputRead = 0, outputWrite = 0;
        nint pseudoConsole = 0;
        nint attributeList = 0;
        nint job = 0;
        var info = new PROCESS_INFORMATION();

        try
        {
            if (!CreatePipe(out inputRead, out inputWrite, 0, 0)) throw Fail("CreatePipe (input)");
            if (!CreatePipe(out outputRead, out outputWrite, 0, 0)) throw Fail("CreatePipe (output)");

            int hr = CreatePseudoConsole(new COORD { X = cols, Y = rows }, inputRead, outputWrite, 0, out pseudoConsole);
            if (hr != 0) throw new Win32Exception(hr, $"CreatePseudoConsole failed (0x{hr:X8}).");

            // conhost holds its own duplicates now. Ours must go, or the output read below never
            // ends and a closed terminal looks like a hung one.
            CloseHandle(inputRead);
            inputRead = 0;
            CloseHandle(outputWrite);
            outputWrite = 0;

            attributeList = BuildAttributeList(pseudoConsole);

            var startup = new STARTUPINFOEX
            {
                StartupInfo = new STARTUPINFO { cb = Marshal.SizeOf<STARTUPINFOEX>() },
                lpAttributeList = attributeList,
            };

            // CreateProcessW writes into its command line argument, so it cannot be a literal.
            var mutableCommandLine = new StringBuilder(commandLine);

            unsafe
            {
                fixed (char* block = EnvironmentBlock(environment))
                {
                    bool started = CreateProcess(
                        null,
                        mutableCommandLine,
                        0,
                        0,
                        false,
                        EXTENDED_STARTUPINFO_PRESENT | CREATE_UNICODE_ENVIRONMENT | CREATE_SUSPENDED,
                        (nint)block,
                        workingDirectory,
                        ref startup,
                        out info);

                    if (!started) throw Fail("CreateProcess");
                }
            }

            job = CreateKillOnCloseJob();
            // A job is best effort: nested job limits can refuse the assignment on some machines,
            // and a terminal that kills only its shell is far better than no terminal at all.
            if (job != 0 && !AssignProcessToJobObject(job, info.hProcess))
            {
                CloseHandle(job);
                job = 0;
            }

            if (ResumeThread(info.hThread) == -1) throw Fail("ResumeThread");

            var output = new FileStream(new SafeFileHandle(outputRead, ownsHandle: true), FileAccess.Read, 4096, isAsync: false);
            outputRead = 0;
            var input = new FileStream(new SafeFileHandle(inputWrite, ownsHandle: true), FileAccess.Write, 4096, isAsync: false);
            inputWrite = 0;

            var started2 = new ConPtyProcess(pseudoConsole, info.hProcess, info.hThread, job, info.dwProcessId, input, output);
            pseudoConsole = 0;
            job = 0;
            info = default;
            return started2;
        }
        catch
        {
            if (info.hThread != 0) CloseHandle(info.hThread);
            if (info.hProcess != 0)
            {
                TerminateProcess(info.hProcess, 1);
                CloseHandle(info.hProcess);
            }
            if (job != 0) CloseHandle(job);
            if (pseudoConsole != 0) ClosePseudoConsole(pseudoConsole);
            foreach (var handle in new[] { inputRead, inputWrite, outputRead, outputWrite })
            {
                if (handle != 0) CloseHandle(handle);
            }
            throw;
        }
        finally
        {
            if (attributeList != 0)
            {
                DeleteProcThreadAttributeList(attributeList);
                Marshal.FreeHGlobal(attributeList);
            }
        }
    }

    /// <summary>Keystrokes, straight to the shell's stdin. Serialised so two writers cannot interleave.</summary>
    public void Write(string data)
    {
        var bytes = Encoding.UTF8.GetBytes(data);
        lock (_writeGate)
        {
            if (_disposed) return;
            _input.Write(bytes, 0, bytes.Length);
            _input.Flush();
        }
    }

    /// <summary>Tells the shell the window changed size, so full-screen programs redraw correctly.</summary>
    public void Resize(short cols, short rows)
    {
        if (_disposed || _pseudoConsole == 0) return;
        ResizePseudoConsole(_pseudoConsole, new COORD { X = cols, Y = rows });
    }

    /// <inheritdoc />
    public int? ExitCode
    {
        get
        {
            if (_process == 0) return null;
            return GetExitCodeProcess(_process, out uint code) && code != STILL_ACTIVE ? (int)code : null;
        }
    }

    /// <summary>
    /// Ends the shell and everything under it.
    ///
    /// Order matters. Terminating the job first means ClosePseudoConsole has nothing left to wait
    /// for - closing it while the shell is alive blocks until conhost has drained, and that has
    /// deadlocked plenty of ConPTY wrappers.
    /// </summary>
    public void Kill()
    {
        if (_job != 0) TerminateJobObject(_job, 1);
        else if (_process != 0) TerminateProcess(_process, 1);
    }

    public void Dispose()
    {
        lock (_writeGate)
        {
            if (_disposed) return;
            _disposed = true;
        }

        Kill();

        try { _input.Dispose(); } catch (IOException) { /* the pipe went with the shell */ }
        try { Output.Dispose(); } catch (IOException) { /* same */ }

        if (_pseudoConsole != 0) { ClosePseudoConsole(_pseudoConsole); _pseudoConsole = 0; }
        if (_thread != 0) { CloseHandle(_thread); _thread = 0; }
        if (_job != 0) { CloseHandle(_job); _job = 0; }

        // Last: the wait handle above borrows this handle without owning it.
        Exited.Dispose();
        if (_process != 0) { CloseHandle(_process); _process = 0; }
    }

    // ------------------------------------------------------------------ helpers

    private static Win32Exception Fail(string what) => new(Marshal.GetLastWin32Error(), $"{what} failed.");

    /// <summary>
    /// A one-entry attribute list carrying the HPCON. Sized by the documented two-call pattern:
    /// the first call is expected to fail with ERROR_INSUFFICIENT_BUFFER and fill in the size.
    /// </summary>
    private static nint BuildAttributeList(nint pseudoConsole)
    {
        nint size = 0;
        InitializeProcThreadAttributeList(0, 1, 0, ref size);

        nint list = Marshal.AllocHGlobal(size);
        try
        {
            if (!InitializeProcThreadAttributeList(list, 1, 0, ref size)) throw Fail("InitializeProcThreadAttributeList");

            if (!UpdateProcThreadAttribute(list, 0, PROC_THREAD_ATTRIBUTE_PSEUDOCONSOLE_HANDLE, pseudoConsole, nint.Size, 0, 0))
            {
                DeleteProcThreadAttributeList(list);
                throw Fail("UpdateProcThreadAttribute");
            }

            return list;
        }
        catch
        {
            Marshal.FreeHGlobal(list);
            throw;
        }
    }

    /// <summary>
    /// A job whose processes die when the last handle to it closes - which is what makes closing
    /// this daemon take every terminal with it, rather than orphaning a tree of build servers.
    /// </summary>
    private static nint CreateKillOnCloseJob()
    {
        nint job = CreateJobObject(0, null);
        if (job == 0) return 0;

        var limits = new JOBOBJECT_EXTENDED_LIMIT_INFORMATION();
        limits.BasicLimitInformation.LimitFlags = JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE;

        int size = Marshal.SizeOf<JOBOBJECT_EXTENDED_LIMIT_INFORMATION>();
        nint buffer = Marshal.AllocHGlobal(size);
        try
        {
            Marshal.StructureToPtr(limits, buffer, fDeleteOld: false);
            if (!SetInformationJobObject(job, JobObjectExtendedLimitInformation, buffer, (uint)size))
            {
                CloseHandle(job);
                return 0;
            }
            return job;
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    /// <summary>
    /// The child's environment as CreateProcess wants it: KEY=VALUE runs separated by NUL and
    /// terminated by an empty one. Sorted because the documented block is, and a few programs
    /// read it directly rather than through the CRT.
    /// </summary>
    private static char[] EnvironmentBlock(IReadOnlyDictionary<string, string> environment)
    {
        var builder = new StringBuilder();
        foreach (var (key, value) in environment.OrderBy(e => e.Key, StringComparer.OrdinalIgnoreCase))
        {
            // A leading '=' is how Windows stores per-drive current directories (=C:=C:\work).
            // They are legitimate; a name that is empty or carries its own '=' is not.
            if (key.Length == 0 || key.IndexOf('=', 1) >= 0) continue;
            builder.Append(key).Append('=').Append(value).Append('\0');
        }
        builder.Append('\0');
        return builder.ToString().ToCharArray();
    }

    // ------------------------------------------------------------------ interop

    private const uint EXTENDED_STARTUPINFO_PRESENT = 0x00080000;
    private const uint CREATE_UNICODE_ENVIRONMENT = 0x00000400;
    private const uint CREATE_SUSPENDED = 0x00000004;
    private const nint PROC_THREAD_ATTRIBUTE_PSEUDOCONSOLE_HANDLE = 0x00020016;
    private const uint JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE = 0x00002000;
    private const int JobObjectExtendedLimitInformation = 9;
    private const uint STILL_ACTIVE = 259;

    [StructLayout(LayoutKind.Sequential)]
    private struct COORD
    {
        public short X;
        public short Y;
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
    private struct STARTUPINFOEX
    {
        public STARTUPINFO StartupInfo;
        public nint lpAttributeList;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PROCESS_INFORMATION
    {
        public nint hProcess;
        public nint hThread;
        public int dwProcessId;
        public int dwThreadId;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct IO_COUNTERS
    {
        public ulong ReadOperationCount;
        public ulong WriteOperationCount;
        public ulong OtherOperationCount;
        public ulong ReadTransferCount;
        public ulong WriteTransferCount;
        public ulong OtherTransferCount;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct JOBOBJECT_BASIC_LIMIT_INFORMATION
    {
        public long PerProcessUserTimeLimit;
        public long PerJobUserTimeLimit;
        public uint LimitFlags;
        public nuint MinimumWorkingSetSize;
        public nuint MaximumWorkingSetSize;
        public uint ActiveProcessLimit;
        public nuint Affinity;
        public uint PriorityClass;
        public uint SchedulingClass;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct JOBOBJECT_EXTENDED_LIMIT_INFORMATION
    {
        public JOBOBJECT_BASIC_LIMIT_INFORMATION BasicLimitInformation;
        public IO_COUNTERS IoInfo;
        public nuint ProcessMemoryLimit;
        public nuint JobMemoryLimit;
        public nuint PeakProcessMemoryUsed;
        public nuint PeakJobMemoryUsed;
    }

    [DllImport(Kernel32, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreatePipe(out nint readPipe, out nint writePipe, nint attributes, int size);

    [DllImport(Kernel32, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(nint handle);

    [DllImport(Kernel32, SetLastError = true)]
    private static extern int CreatePseudoConsole(COORD size, nint input, nint output, uint flags, out nint pseudoConsole);

    [DllImport(Kernel32, SetLastError = true)]
    private static extern int ResizePseudoConsole(nint pseudoConsole, COORD size);

    [DllImport(Kernel32, SetLastError = true)]
    private static extern void ClosePseudoConsole(nint pseudoConsole);

    [DllImport(Kernel32, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool InitializeProcThreadAttributeList(nint attributeList, int attributeCount, int flags, ref nint size);

    [DllImport(Kernel32, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UpdateProcThreadAttribute(
        nint attributeList, uint flags, nint attribute, nint value, nint size, nint previousValue, nint returnSize);

    [DllImport(Kernel32)]
    private static extern void DeleteProcThreadAttributeList(nint attributeList);

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
        ref STARTUPINFOEX startupInfo,
        out PROCESS_INFORMATION processInformation);

    [DllImport(Kernel32, SetLastError = true)]
    private static extern int ResumeThread(nint thread);

    [DllImport(Kernel32, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool TerminateProcess(nint process, uint exitCode);

    [DllImport(Kernel32, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetExitCodeProcess(nint process, out uint exitCode);

    [DllImport(Kernel32, EntryPoint = "CreateJobObjectW", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern nint CreateJobObject(nint attributes, string? name);

    [DllImport(Kernel32, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetInformationJobObject(nint job, int infoClass, nint info, uint infoLength);

    [DllImport(Kernel32, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AssignProcessToJobObject(nint job, nint process);

    [DllImport(Kernel32, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool TerminateJobObject(nint job, uint exitCode);
}
