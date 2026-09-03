using System.Diagnostics;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Argus.Server.Services;

/// <summary>A marked shell as the OS still sees it, whether or not the daemon remembers it.</summary>
public sealed record StrayTerminal(int Pid, string? TerminalId, long? StartedAt);

/// <summary>
/// Shells the operating system knows about that the terminal host does not.
///
/// The daemon's own list is the truth almost always, but it is not the only truth: a daemon that
/// was killed outright, or a shell that somehow outlived its pseudo console, leaves a pwsh running
/// with nothing pointing at it. Those are what this finds - every pwsh whose command line carries
/// the ARGUS_TERMINAL marker - so the Terminals page can offer to kill them instead of leaving a
/// build server running that nothing on screen accounts for.
///
/// Killing one goes through taskkill by pid rather than through a pty, because there is no pty
/// left to go through. The scan is repeated inside the kill rather than trusted from the caller,
/// so this route can only ever end a marked Argus shell - never an arbitrary process someone names
/// in a crafted request.
///
/// Everything on the command lines below is a fixed literal; the only variable that reaches one is
/// a pid, checked as a positive integer first.
/// </summary>
public sealed partial class TerminalProcessScanner(ILogger<TerminalProcessScanner> log)
{
    /// <summary>Kept in step with Argus.TerminalHost's TerminalProtocol.MarkerEnv.</summary>
    private const string MarkerEnv = "ARGUS_TERMINAL";

    private static readonly TimeSpan QueryTimeout = TimeSpan.FromSeconds(10);

    /// <summary>
    /// Every marked shell the OS reports. Never throws - a failed query answers an empty list and
    /// logs, because this only ever augments the daemon's own listing.
    /// </summary>
    public async Task<IReadOnlyList<StrayTerminal>> ScanAsync(CancellationToken cancel)
    {
        // $PID excludes this scan itself. It has to: the pattern it matches on is part of its own
        // command line, and both shells it looks for are shells it could be running as, so without
        // this every scan would report itself as a stray terminal.
        string script =
            "Get-CimInstance Win32_Process -Filter \"Name='pwsh.exe' OR Name='powershell.exe'\" | "
            + $"Where-Object {{ $_.ProcessId -ne $PID -and $_.CommandLine -like '*{MarkerEnv}=*' }} | "
            + "ForEach-Object { [pscustomobject]@{ pid = $_.ProcessId; cmd = $_.CommandLine; "
            + "started = $_.CreationDate.ToUniversalTime().ToString('o') } } | ConvertTo-Json -Compress";

        string output;
        try
        {
            output = await RunAsync("powershell.exe", ["-NoProfile", "-NonInteractive", "-Command", script], cancel);
        }
        catch (Exception ex)
        {
            log.LogWarning(ex, "The terminal process scan failed");
            return [];
        }

        if (output.Trim().Length == 0) return [];

        try
        {
            using var document = JsonDocument.Parse(output);
            var rows = document.RootElement.ValueKind == JsonValueKind.Array
                ? document.RootElement.EnumerateArray().ToList()
                : [document.RootElement];

            return [.. rows.Select(Describe).OfType<StrayTerminal>()];
        }
        catch (JsonException ex)
        {
            log.LogWarning(ex, "The terminal process scan returned something that is not JSON");
            return [];
        }
    }

    /// <summary>
    /// Force-kills a stray by pid, whole tree - taskkill /T takes an `npm run dev` under the shell
    /// with it.
    /// </summary>
    public async Task<TerminalOutcome> KillAsync(int pid, CancellationToken cancel)
    {
        if (pid <= 0) return TerminalOutcome.Fail(400, "pid must be a positive integer.");

        var marked = await ScanAsync(cancel);
        if (!marked.Any(stray => stray.Pid == pid))
        {
            return TerminalOutcome.Fail(404, $"pid {pid} is not an Argus terminal (it may have already exited).");
        }

        try
        {
            await RunAsync("taskkill.exe", ["/PID", pid.ToString(), "/T", "/F"], cancel);
            return TerminalOutcome.Pass;
        }
        catch (Exception ex)
        {
            // taskkill exits non-zero when the pid is already gone, which is the outcome asked for.
            if (ex.Message.Contains("not found", StringComparison.OrdinalIgnoreCase)) return TerminalOutcome.Pass;

            log.LogWarning(ex, "taskkill failed for pid {Pid}", pid);
            return TerminalOutcome.Fail(500, $"Could not kill pid {pid}: {ex.Message}");
        }
    }

    private static StrayTerminal? Describe(JsonElement row)
    {
        if (!row.TryGetProperty("pid", out var pidValue) || !pidValue.TryGetInt32(out int pid)) return null;

        string? terminalId = row.TryGetProperty("cmd", out var command)
            ? MarkerPattern().Match(command.GetString() ?? string.Empty) is { Success: true } match
                ? match.Groups[1].Value
                : null
            : null;

        long? startedAt = row.TryGetProperty("started", out var started)
            && DateTimeOffset.TryParse(started.GetString(), out var parsed)
                ? parsed.ToUnixTimeMilliseconds()
                : null;

        return new StrayTerminal(pid, terminalId, startedAt);
    }

    private static async Task<string> RunAsync(string file, string[] arguments, CancellationToken cancel)
    {
        var startInfo = new ProcessStartInfo(file)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var argument in arguments) startInfo.ArgumentList.Add(argument);

        using var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException($"Could not start {file}.");

        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancel);
        deadline.CancelAfter(QueryTimeout);

        var stdout = process.StandardOutput.ReadToEndAsync(deadline.Token);
        var stderr = process.StandardError.ReadToEndAsync(deadline.Token);

        try
        {
            await process.WaitForExitAsync(deadline.Token);
        }
        catch (OperationCanceledException)
        {
            try { process.Kill(entireProcessTree: true); } catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception) { }
            throw;
        }

        string output = await stdout;
        if (process.ExitCode != 0) throw new InvalidOperationException((await stderr).Trim() is { Length: > 0 } message ? message : $"{file} exited {process.ExitCode}.");

        return output;
    }

    [GeneratedRegex($@"{MarkerEnv}='([^']*)'")]
    private static partial Regex MarkerPattern();
}
