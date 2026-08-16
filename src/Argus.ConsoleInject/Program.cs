using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;

namespace Argus.ConsoleInject;

/// <summary>
/// Injects keystrokes into a classic conhost window (powershell.exe / cmd.exe started directly,
/// class "ConsoleWindowClass"). Those windows ignore posted WM_KEYDOWN messages entirely - they
/// read from the console input buffer - so the only way in is WriteConsoleInput.
///
/// This lives in its own process on purpose: AttachConsole/FreeConsole are process-wide, so doing
/// this inside Argus.Server would tear down the server's own console and its logging with it.
///
/// Usage:  Argus.ConsoleInject.exe &lt;target-console-hwnd&gt;
/// Then one command per stdin line, kept alive for the life of the attached window:
///   C &lt;utf16-code-unit&gt;                        - type one character
///   K &lt;down 0|1&gt; &lt;vk&gt; &lt;scan&gt; &lt;ext 0|1&gt; &lt;ctrlState&gt;  - raw key event
/// Writes "OK"/"ERR &lt;reason&gt;" per line to stdout so the caller can tell delivery apart from silence.
/// </summary>
internal static class Program
{
    private static int Main(string[] args)
    {
        if (args.Length < 1 || !long.TryParse(args[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out long rawHwnd))
        {
            Console.Error.WriteLine("usage: Argus.ConsoleInject.exe <target-console-hwnd>");
            return 2;
        }

        var targetHwnd = (nint)rawHwnd;

        if (!AttachToConsoleOf(targetHwnd, out int clientPid))
        {
            Console.Error.WriteLine($"ERR could not attach to the console owning HWND {targetHwnd}");
            return 3;
        }

        // CONIN$ resolves against the console we just attached to, not our original one.
        using var conin = Native.CreateFile(
            "CONIN$",
            Native.GENERIC_READ | Native.GENERIC_WRITE,
            Native.FILE_SHARE_READ | Native.FILE_SHARE_WRITE,
            nint.Zero,
            Native.OPEN_EXISTING,
            0,
            nint.Zero);

        if (conin.IsInvalid)
        {
            Console.Error.WriteLine($"ERR CONIN$ open failed: {Marshal.GetLastWin32Error()}");
            return 4;
        }

        Console.Error.WriteLine($"attached pid={clientPid} hwnd={targetHwnd}");

        // stdout is our own (inherited) pipe back to the server - unaffected by the attach.
        var writer = new StreamWriter(Console.OpenStandardOutput()) { AutoFlush = true };

        string? line;
        while ((line = Console.ReadLine()) is not null)
        {
            if (line.Length == 0) continue;
            try
            {
                var record = Parse(line);
                if (record is null)
                {
                    writer.WriteLine("ERR parse");
                    continue;
                }

                var buffer = new[] { record.Value };
                bool ok = Native.WriteConsoleInput(conin, buffer, 1, out uint written) && written == 1;
                writer.WriteLine(ok ? "OK" : $"ERR write {Marshal.GetLastWin32Error()}");
            }
            catch (Exception ex)
            {
                writer.WriteLine($"ERR {ex.GetType().Name}");
            }
        }

        return 0;
    }

    private static Native.INPUT_RECORD? Parse(string line)
    {
        var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0) return null;

        switch (parts[0])
        {
            case "C" when parts.Length == 2 && ushort.TryParse(parts[1], out ushort unit):
                // A pure character event: VK 0 with a Unicode payload is what a paste looks like to
                // the console, and it is the most layout-proof way to deliver text.
                return NewKeyRecord(down: true, vk: 0, scan: 0, unicode: unit, controlState: 0);

            case "K" when parts.Length == 6
                       && byte.TryParse(parts[1], out byte down)
                       && ushort.TryParse(parts[2], out ushort vk)
                       && ushort.TryParse(parts[3], out ushort scan)
                       && byte.TryParse(parts[4], out byte ext)
                       && uint.TryParse(parts[5], out uint ctrlState):
            {
                if (ext != 0) ctrlState |= Native.ENHANCED_KEY;
                return NewKeyRecord(down != 0, vk, scan, 0, ctrlState);
            }

            default:
                return null;
        }
    }

    private static Native.INPUT_RECORD NewKeyRecord(bool down, ushort vk, ushort scan, ushort unicode, uint controlState) =>
        new()
        {
            EventType = Native.KEY_EVENT,
            KeyEvent = new Native.KEY_EVENT_RECORD
            {
                bKeyDown = down ? 1 : 0,
                wRepeatCount = 1,
                wVirtualKeyCode = vk,
                wVirtualScanCode = scan,
                UnicodeChar = unicode,
                dwControlKeyState = controlState,
            },
        };

    /// <summary>
    /// GetWindowThreadProcessId on a console window returns conhost.exe, and AttachConsole refuses
    /// conhost's own pid. The reliable identification is the other way round: attach to a candidate
    /// process's console and ask whether GetConsoleWindow now reports the HWND we are after.
    /// </summary>
    private static bool AttachToConsoleOf(nint targetHwnd, out int clientPid)
    {
        clientPid = 0;

        foreach (int pid in CandidatePids(targetHwnd))
        {
            Native.FreeConsole();
            if (!Native.AttachConsole((uint)pid)) continue;

            if (Native.GetConsoleWindow() == targetHwnd)
            {
                clientPid = pid;
                return true;
            }
        }

        Native.FreeConsole();
        return false;
    }

    private static IEnumerable<int> CandidatePids(nint targetHwnd)
    {
        Process[] all;
        try
        {
            all = Process.GetProcesses();
        }
        catch (InvalidOperationException)
        {
            yield break;
        }

        // The console client normally reports the console window as its MainWindowHandle, so try
        // those first and avoid attach-churn across every process on the machine.
        var ordered = all
            .OrderByDescending(p => SafeMainWindowHandle(p) == targetHwnd)
            .ToList();

        foreach (var p in ordered)
        {
            int pid;
            try
            {
                pid = p.Id;
            }
            catch (InvalidOperationException)
            {
                continue;
            }
            finally
            {
                p.Dispose();
            }

            if (pid > 4) yield return pid;   // skip Idle/System
        }
    }

    private static nint SafeMainWindowHandle(Process p)
    {
        try
        {
            return p.MainWindowHandle;
        }
        catch (Exception ex) when (ex is InvalidOperationException or NotSupportedException)
        {
            return nint.Zero;
        }
    }
}
