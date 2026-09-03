using System.Collections.Concurrent;

namespace Argus.TerminalHost;

/// <summary>
/// Every terminal this daemon owns, keyed by a generated id.
///
/// This is the file the whole feature exists for. Argus.Server holds none of these - it is a
/// client of this process over a pipe - so restarting or crashing the server leaves every shell
/// running, and reattaching replays each one's buffer. A terminal ends when someone kills it, when
/// its shell exits, when this daemon is told to shut down, or when the machine restarts. Nothing
/// else touches it.
///
/// Security: this is arbitrary command execution by design, exactly as
/// <see cref="Argus.Server.Input"/> injection already is - the browser's keystrokes become a
/// shell's stdin, running as the user. What keeps it to the user is the token on the pipe (see
/// <see cref="HostServer"/>) and the session gate in front of the HTTP routes. Nothing here builds
/// a command line out of request data: the program and its arguments are fixed, the one templated
/// value is the marker (an id this process generated), and `cwd` is validated as an existing
/// directory before it is used. Input only ever travels as pty bytes, never as an argument.
/// </summary>
internal sealed class TerminalRegistry : IDisposable
{
    /// <summary>A ceiling so a stuck "new terminal" button cannot spawn shells without bound.</summary>
    private const int MaxTerminals = 40;

    /// <summary>A tab name is a label, not a path - the cap only keeps the strip readable.</summary>
    private const int MaxNameLength = 60;

    private const int MinDimension = 2;
    private const int MaxDimension = 500;

    private readonly ConcurrentDictionary<string, PtySession> _sessions = new(StringComparer.Ordinal);
    private readonly Func<string, string, IReadOnlyDictionary<string, string>, short, short, IPty> _spawn;
    private readonly Func<string> _newId;

    /// <summary>
    /// <paramref name="spawn"/> and <paramref name="newId"/> are injectable so the tests can drive
    /// the registry and the protocol without a real pseudo console or a random id.
    /// </summary>
    public TerminalRegistry(
        Func<string, string, IReadOnlyDictionary<string, string>, short, short, IPty>? spawn = null,
        Func<string>? newId = null)
    {
        _spawn = spawn ?? ConPtyProcess.Start;
        _newId = newId ?? (() => Guid.NewGuid().ToString("N"));
    }

    public int Count => _sessions.Count;

    /// <summary>Starts a shell. Always a new one - the caller decides how many are wanted.</summary>
    public HostResult<TerminalView> Open(string? cwd, int? cols, int? rows)
    {
        if (_sessions.Count >= MaxTerminals)
        {
            return HostResult<TerminalView>.Fail(429, $"Too many terminals are open ({MaxTerminals}). Close some before opening more.");
        }

        string id = _newId();
        string directory = ResolveWorkingDirectory(cwd);
        short columns = (short)Clamp(cols, 80);
        short lines = (short)Clamp(rows, 24);

        var (file, arguments) = ShellCommand(id);
        string commandLine = $"\"{file}\" {arguments}";

        var environment = CurrentEnvironment();
        environment[TerminalProtocol.MarkerEnv] = id;

        IPty pty;
        try
        {
            pty = _spawn(commandLine, directory, environment, columns, lines);
        }
        catch (Exception ex)
        {
            return HostResult<TerminalView>.Fail(500, $"Could not start {Path.GetFileName(file)}: {ex.Message}");
        }

        var session = new PtySession(id, pty, commandLine, directory, columns, lines);
        _sessions[id] = session;
        return HostResult<TerminalView>.Pass(session.View());
    }

    public PtySession? Find(string? terminalId) =>
        terminalId is not null && _sessions.TryGetValue(terminalId, out var session) ? session : null;

    public HostResult<bool> Write(string? terminalId, string? data)
    {
        if (Find(terminalId) is not { } session) return HostResult<bool>.Fail(404, "No such terminal. It may have been closed.");
        if (string.IsNullOrEmpty(data)) return HostResult<bool>.Fail(400, "`data` must be a non-empty string.");
        if (!session.Running) return HostResult<bool>.Fail(409, "That terminal is not running.");

        session.Write(data);
        return HostResult<bool>.Pass(true);
    }

    public HostResult<bool> Resize(string? terminalId, int? cols, int? rows)
    {
        if (Find(terminalId) is not { } session) return HostResult<bool>.Fail(404, "No such terminal. It may have been closed.");
        if (!session.Running) return HostResult<bool>.Fail(409, "That terminal is not running.");
        if (!IsDimension(cols) || !IsDimension(rows))
        {
            return HostResult<bool>.Fail(400, $"cols and rows must be integers between {MinDimension} and {MaxDimension}.");
        }

        session.Resize(cols!.Value, rows!.Value);
        return HostResult<bool>.Pass(true);
    }

    /// <summary>Ends a terminal and forgets it. Its tab disappears rather than greying out.</summary>
    public HostResult<bool> Kill(string? terminalId)
    {
        if (terminalId is null || !_sessions.TryRemove(terminalId, out var session))
        {
            return HostResult<bool>.Fail(404, "No such terminal.");
        }

        session.Dispose();
        return HostResult<bool>.Pass(true);
    }

    /// <summary>
    /// Sets or clears a tab label. Allowed on a terminal whose shell has exited: the tab is still
    /// on screen until it is closed, and there is no reason to refuse renaming it in the meantime.
    /// </summary>
    public HostResult<bool> Rename(string? terminalId, string? name)
    {
        if (Find(terminalId) is not { } session) return HostResult<bool>.Fail(404, "No such terminal.");

        string trimmed = name?.Trim() ?? string.Empty;
        session.Name = trimmed.Length == 0
            ? null
            : trimmed[..Math.Min(trimmed.Length, MaxNameLength)];

        return HostResult<bool>.Pass(true);
    }

    /// <summary>Every terminal, oldest first, so the tab strip keeps a stable order across reloads.</summary>
    public IReadOnlyList<TerminalView> List() =>
        [.. _sessions.Values.Select(s => s.View()).OrderBy(v => v.StartedAt)];

    public void KillAll()
    {
        foreach (var id in _sessions.Keys.ToList()) Kill(id);
    }

    public void Dispose() => KillAll();

    // ------------------------------------------------------------------ helpers

    /// <summary>
    /// The fixed program and arguments for one terminal, marker included. The marker is an id this
    /// process generated - hex from <see cref="Guid"/> - never anything that arrived in a request,
    /// which is what makes putting it on a command line safe.
    ///
    /// -NoExit keeps the shell interactive after the marker assignment has run, so the assignment
    /// is a line of setup rather than the whole session.
    /// </summary>
    private static (string File, string Arguments) ShellCommand(string terminalId)
    {
        string marker = $"$env:{TerminalProtocol.MarkerEnv}='{terminalId}'";
        return (ResolveShell(), $"-NoLogo -NoExit -Command {marker}");
    }

    /// <summary>
    /// pwsh if it is installed, else the Windows PowerShell that always is. ARGUS_TERMINAL_SHELL
    /// overrides both, for anyone who would rather have cmd or a Git bash.
    /// </summary>
    private static string ResolveShell()
    {
        if (Environment.GetEnvironmentVariable("ARGUS_TERMINAL_SHELL") is { Length: > 0 } configured
            && File.Exists(configured))
        {
            return configured;
        }

        foreach (var directory in (Environment.GetEnvironmentVariable("PATH") ?? string.Empty).Split(Path.PathSeparator))
        {
            if (directory.Length == 0) continue;

            string candidate;
            try
            {
                candidate = Path.Combine(directory.Trim('"'), "pwsh.exe");
            }
            catch (ArgumentException)
            {
                continue; // a PATH entry with illegal characters, which is common enough
            }

            if (File.Exists(candidate)) return candidate;
        }

        return Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.System),
            "WindowsPowerShell", "v1.0", "powershell.exe");
    }

    /// <summary>
    /// Where the shell starts. A path that is not an existing directory falls back to the profile
    /// rather than failing the open - and it is only ever handed to CreateProcess as a working
    /// directory, never parsed into a command.
    /// </summary>
    private static string ResolveWorkingDirectory(string? cwd)
    {
        try
        {
            if (!string.IsNullOrWhiteSpace(cwd) && Directory.Exists(cwd)) return Path.GetFullPath(cwd);
        }
        catch (Exception ex) when (ex is ArgumentException or PathTooLongException or NotSupportedException)
        {
            // Not a usable path; the profile below is.
        }

        return Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
    }

    private static Dictionary<string, string> CurrentEnvironment()
    {
        var environment = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (System.Collections.DictionaryEntry entry in Environment.GetEnvironmentVariables())
        {
            if (entry.Key is string key && entry.Value is string value) environment[key] = value;
        }
        return environment;
    }

    private static bool IsDimension(int? value) => value is >= MinDimension and <= MaxDimension;

    private static int Clamp(int? value, int fallback) => IsDimension(value) ? value!.Value : fallback;
}

/// <summary>
/// What an operation answers: a value, or an HTTP-shaped status and message. The status travels
/// all the way to the browser, so a dead terminal reads as 409 there rather than as a generic 500.
/// </summary>
internal readonly record struct HostResult<T>(bool Ok, T? Value, int Status, string? Error)
{
    public static HostResult<T> Pass(T value) => new(true, value, 200, null);

    public static HostResult<T> Fail(int status, string error) => new(false, default, status, error);
}
