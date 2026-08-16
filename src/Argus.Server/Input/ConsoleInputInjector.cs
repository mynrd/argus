using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using Argus.Server.Windows;

namespace Argus.Server.Input;

/// <summary>
/// Drives classic conhost windows (class "ConsoleWindowClass") through the out-of-process
/// Argus.ConsoleInject helper, which owns the AttachConsole/WriteConsoleInput dance.
///
/// One helper is kept alive per target window and fed over stdin, because process startup per
/// keystroke would add tens of milliseconds to every character typed.
///
/// This does NOT cover Windows Terminal. Its tabs are ConPTY-hosted and have no HWND of their own,
/// so neither posted messages nor WriteConsoleInput reach them - those need
/// <see cref="InjectionMode.Focus"/>.
/// </summary>
public sealed class ConsoleInputInjector : IInputInjector, IDisposable
{
    private sealed record Helper(Process Process, StreamWriter Input);

    private readonly ConcurrentDictionary<long, Helper> _helpers = new();
    private readonly ILogger<ConsoleInputInjector> _log;
    private readonly string _helperPath;
    private readonly Lock _spawnLock = new();

    public ConsoleInputInjector(ILogger<ConsoleInputInjector> log)
    {
        _log = log;
        _helperPath = ResolveHelperPath();
    }

    public string Name => "writeconsoleinput";

    public bool CanHandle(WindowInfo target) => target.IsConsole && _helperPath.Length > 0;

    public bool TrySend(WindowInfo target, KeyEventDto keyEvent)
    {
        // The console input buffer has no concept of key-up for typed text; replaying both halves
        // would double every character.
        if (keyEvent.Type == KeyEventType.Up) return true;

        var helper = GetOrSpawn(target.Handle);
        if (helper is null) return false;

        string command = BuildCommand(keyEvent);
        if (command.Length == 0) return false;

        try
        {
            helper.Input.WriteLine(command);
            return true;
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or InvalidOperationException)
        {
            _log.LogWarning(ex, "Console helper for HWND {Hwnd} died; it will be respawned", target.Handle);
            Kill(target.Handle);
            return false;
        }
    }

    internal static string BuildCommand(KeyEventDto keyEvent)
    {
        if (keyEvent.IsPrintable)
        {
            return string.Create(CultureInfo.InvariantCulture, $"C {(ushort)keyEvent.Key[0]}");
        }

        var mapped = KeyMapper.Map(keyEvent.Code);
        if (!mapped.IsValid) return string.Empty;

        uint controlState = 0;
        if (keyEvent.Shift) controlState |= 0x0010;   // SHIFT_PRESSED
        if (keyEvent.Ctrl) controlState |= 0x0008;    // LEFT_CTRL_PRESSED
        if (keyEvent.Alt) controlState |= 0x0002;     // LEFT_ALT_PRESSED

        return string.Create(CultureInfo.InvariantCulture,
            $"K 1 {mapped.VirtualKey} {mapped.ScanCode} {(mapped.IsExtended ? 1 : 0)} {controlState}");
    }

    private Helper? GetOrSpawn(long handle)
    {
        if (_helpers.TryGetValue(handle, out var existing))
        {
            if (!existing.Process.HasExited) return existing;
            Kill(handle);
        }

        if (_helperPath.Length == 0) return null;

        lock (_spawnLock)
        {
            if (_helpers.TryGetValue(handle, out var raced) && !raced.Process.HasExited) return raced;

            try
            {
                var psi = new ProcessStartInfo(_helperPath)
                {
                    UseShellExecute = false,
                    RedirectStandardInput = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true,
                };
                psi.ArgumentList.Add(handle.ToString(CultureInfo.InvariantCulture));

                var process = Process.Start(psi);
                if (process is null) return null;

                // Drain both pipes: a full stdout buffer would deadlock the helper mid-write.
                _ = DrainAsync(process.StandardOutput, handle, isError: false);
                _ = DrainAsync(process.StandardError, handle, isError: true);

                var helper = new Helper(process, process.StandardInput);
                helper.Input.AutoFlush = true;
                _helpers[handle] = helper;

                _log.LogInformation("Started console-inject helper pid {Pid} for HWND {Hwnd}", process.Id, handle);
                return helper;
            }
            catch (Exception ex)
            {
                _log.LogError(ex, "Could not start console-inject helper for HWND {Hwnd}", handle);
                return null;
            }
        }
    }

    private async Task DrainAsync(StreamReader reader, long handle, bool isError)
    {
        try
        {
            string? line;
            while ((line = await reader.ReadLineAsync().ConfigureAwait(false)) is not null)
            {
                if (isError || line.StartsWith("ERR", StringComparison.Ordinal))
                {
                    _log.LogDebug("console-inject[{Hwnd}] {Line}", handle, line);
                }
            }
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException)
        {
            // Helper exited; GetOrSpawn will notice and restart it.
        }
    }

    private void Kill(long handle)
    {
        if (!_helpers.TryRemove(handle, out var helper)) return;
        try
        {
            if (!helper.Process.HasExited) helper.Process.Kill(entireProcessTree: true);
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            // Already gone.
        }
        finally
        {
            helper.Process.Dispose();
        }
    }

    /// <summary>Releases the helper for a window that is no longer attached.</summary>
    public void Release(long handle) => Kill(handle);

    private string ResolveHelperPath()
    {
        var baseDir = AppContext.BaseDirectory;
        var candidates = new[]
        {
            Path.Combine(baseDir, "Argus.ConsoleInject.exe"),
            Path.Combine(baseDir, "ConsoleInject", "Argus.ConsoleInject.exe"),
        };

        foreach (var candidate in candidates)
        {
            if (File.Exists(candidate)) return candidate;
        }

        _log.LogWarning(
            "Argus.ConsoleInject.exe not found next to the server; classic conhost windows will fall back to Focus mode.");
        return string.Empty;
    }

    public void Dispose()
    {
        foreach (var handle in _helpers.Keys.ToList()) Kill(handle);
    }
}
