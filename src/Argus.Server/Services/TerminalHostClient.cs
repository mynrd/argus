using System.IO.Pipes;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;

namespace Argus.Server.Services;

/// <summary>One terminal, as the page sees it. Mirrors Argus.TerminalHost's TerminalView.</summary>
public sealed record TerminalInfo(
    string TerminalId,
    int? Pid,
    bool Running,
    string Command,
    string Cwd,
    int Cols,
    int Rows,
    long StartedAt,
    int? ExitCode,
    string? Name);

/// <summary>One thing that happened to a terminal: <c>replay</c>, <c>data</c> or <c>exit</c>.</summary>
public sealed record TerminalHostEvent(string Type, string? Data, int? ExitCode);

/// <summary>
/// What an operation answered. The status is HTTP-shaped all the way from the daemon, so a write
/// to a shell that has exited reads as 409 in the browser rather than as a bare failure.
/// </summary>
public readonly record struct TerminalOutcome(bool Ok, int Status, string? Error)
{
    public static TerminalOutcome Pass { get; } = new(true, 200, null);

    public static TerminalOutcome Fail(int status, string error) => new(false, status, error);
}

/// <summary>
/// The server's view of the terminal host daemon (Argus.TerminalHost): everything a terminal
/// needs, relayed over a named pipe to a process that outlives this one.
///
/// The one thing that makes this more than an RPC wrapper is <see cref="EnsureAsync"/>: before any
/// operation it makes sure a daemon is actually there - reading the info file, pinging it, and
/// starting a fresh one (after retiring one speaking a protocol version this build does not know)
/// when it is not. Every method below goes through it, so nothing else has to think about whether
/// the daemon exists yet, and a server that has just been rebuilt reattaches to terminals that
/// were opened by the previous build.
///
/// Nothing here puts request data on a command line. The only thing this file ever starts is the
/// daemon itself - a fixed path, no arguments - and the terminalId/data/cwd/name a caller passes
/// travel only inside the JSON line written to the pipe.
/// </summary>
public sealed class TerminalHostClient : IDisposable
{
    /// <summary>Kept in step with Argus.TerminalHost's TerminalProtocol.Version.</summary>
    private const int ProtocolVersion = 1;

    private static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan OperationTimeout = TimeSpan.FromSeconds(5);

    /// <summary>Budget for starting a daemon and waiting for it to answer its first ping.</summary>
    private static readonly TimeSpan StartTimeout = TimeSpan.FromSeconds(10);

    private static readonly TimeSpan StartPoll = TimeSpan.FromMilliseconds(150);

    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
    };

    private readonly ILogger<TerminalHostClient> _log;
    private readonly SemaphoreSlim _ensureGate = new(1, 1);
    private readonly string _infoPath;

    /// <summary>The last endpoint known to answer. Cleared whenever an operation cannot reach it.</summary>
    private HostEndpoint? _endpoint;

    public TerminalHostClient(ILogger<TerminalHostClient> log, IConfiguration config)
    {
        _log = log;
        _infoPath = config["Argus:TerminalHostInfoPath"] ?? DefaultInfoPath();
    }

    /// <summary>
    /// Where the daemon publishes its pipe name and token: the same folder as ports.json, so
    /// everything Argus writes lives in one place. Duplicated in Argus.TerminalHost's Program,
    /// which this project references for its .exe only and so shares no assembly with.
    /// </summary>
    private static string DefaultInfoPath() => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Argus",
        "terminal-host.json");

    // ------------------------------------------------------------------ operations

    /// <summary>Starts a shell. Always a new one - the page decides how many it wants.</summary>
    public async Task<(TerminalOutcome Outcome, TerminalInfo? Terminal)> OpenAsync(
        string? cwd, int? cols, int? rows, CancellationToken cancel)
    {
        using var reply = await CallAsync(new { op = "open", cwd, cols, rows }, cancel);
        if (!reply.Outcome.Ok) return (reply.Outcome, null);

        var terminal = reply.Root.TryGetProperty("terminal", out var element)
            ? element.Deserialize<TerminalInfo>(Json)
            : null;

        return terminal is null
            ? (TerminalOutcome.Fail(502, "The terminal host answered without a terminal."), null)
            : (TerminalOutcome.Pass, terminal);
    }

    /// <summary>
    /// Every terminal the daemon owns. A bare list rather than a failure when it cannot be
    /// reached: no daemon means no terminals, which is a true and useful answer for a page that
    /// only wants to draw rows.
    /// </summary>
    public async Task<IReadOnlyList<TerminalInfo>> ListAsync(CancellationToken cancel)
    {
        using var reply = await CallAsync(new { op = "list" }, cancel);
        if (!reply.Outcome.Ok) return [];

        return reply.Root.TryGetProperty("terminals", out var element)
            ? element.Deserialize<List<TerminalInfo>>(Json) ?? []
            : [];
    }

    public async Task<TerminalOutcome> KillAsync(string terminalId, CancellationToken cancel)
    {
        using var reply = await CallAsync(new { op = "kill", terminalId }, cancel);
        return reply.Outcome;
    }

    public async Task<TerminalOutcome> RenameAsync(string terminalId, string? name, CancellationToken cancel)
    {
        using var reply = await CallAsync(new { op = "rename", terminalId, name }, cancel);
        return reply.Outcome;
    }

    public async Task<TerminalOutcome> KillAllAsync(CancellationToken cancel)
    {
        using var reply = await CallAsync(new { op = "killAll" }, cancel);
        return reply.Outcome;
    }

    /// <summary>
    /// Opens a live connection to one terminal: replay first, then output as it happens, with
    /// input and resizes going back up the same connection. The caller owns the result and must
    /// dispose it.
    /// </summary>
    public async Task<(TerminalOutcome Outcome, TerminalAttachment? Attachment)> AttachAsync(
        string terminalId, CancellationToken cancel)
    {
        var endpoint = await EnsureAsync(cancel);
        if (endpoint is null) return (Unreachable, null);

        NamedPipeClientStream? pipe = null;
        try
        {
            pipe = await ConnectAsync(endpoint, cancel);
            if (pipe is null)
            {
                Invalidate(endpoint);
                return (Unreachable, null);
            }

            var reader = new StreamReader(pipe, new UTF8Encoding(false), false, 4096, leaveOpen: true);
            var writer = new StreamWriter(pipe, new UTF8Encoding(false), 4096, leaveOpen: true) { AutoFlush = true };

            await writer.WriteLineAsync(JsonSerializer.Serialize(
                new { token = endpoint.Token, op = "attach", terminalId }, Json).AsMemory(), cancel);

            string? header = await reader.ReadLineAsync(cancel);
            if (header is null) return (Unreachable, null);

            using var parsed = JsonDocument.Parse(header);
            if (!IsOk(parsed.RootElement)) return (OutcomeOf(parsed.RootElement), null);

            var attachment = new TerminalAttachment(
                pipe,
                reader,
                writer,
                running: Read(parsed.RootElement, "running").GetBoolean());

            pipe = null; // owned by the attachment now
            return (TerminalOutcome.Pass, attachment);
        }
        catch (Exception ex) when (ex is IOException or JsonException or TimeoutException or UnauthorizedAccessException)
        {
            Invalidate(endpoint);
            _log.LogWarning(ex, "Attaching to terminal {TerminalId} failed", terminalId);
            return (Unreachable, null);
        }
        finally
        {
            if (pipe is not null) await pipe.DisposeAsync();
        }
    }

    // ------------------------------------------------------------------ plumbing

    private static TerminalOutcome Unreachable =>
        TerminalOutcome.Fail(503, "The terminal host is not answering.");

    /// <summary>
    /// One request line in, one response line out. Retried once against a freshly ensured daemon:
    /// the cached endpoint is the daemon this server last spoke to, and it may have been shut down
    /// or replaced since - which is normal, given the daemon and the server restart independently.
    /// </summary>
    private async Task<Reply> CallAsync(object payload, CancellationToken cancel)
    {
        for (int attempt = 0; attempt < 2; attempt++)
        {
            var endpoint = await EnsureAsync(cancel);
            if (endpoint is null) break;

            var reply = await SendAsync(endpoint, payload, cancel);
            if (reply.Reached) return reply;

            reply.Dispose();
            Invalidate(endpoint);
        }

        return Reply.Unreached;
    }

    private async Task<Reply> SendAsync(HostEndpoint endpoint, object payload, CancellationToken cancel)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancel);
        deadline.CancelAfter(OperationTimeout);

        NamedPipeClientStream? pipe = null;
        try
        {
            pipe = await ConnectAsync(endpoint, deadline.Token);
            if (pipe is null) return Reply.Unreached;

            var writer = new StreamWriter(pipe, new UTF8Encoding(false), 4096, leaveOpen: true) { AutoFlush = true };
            var reader = new StreamReader(pipe, new UTF8Encoding(false), false, 4096, leaveOpen: true);

            string line = JsonSerializer.Serialize(Merge(endpoint.Token, payload), Json);
            await writer.WriteLineAsync(line.AsMemory(), deadline.Token);

            string? answer = await reader.ReadLineAsync(deadline.Token);
            if (answer is null) return Reply.Unreached;

            var document = JsonDocument.Parse(answer);
            return new Reply { Reached = true, Document = document, Outcome = OutcomeOf(document.RootElement) };
        }
        catch (OperationCanceledException) when (!cancel.IsCancellationRequested)
        {
            _log.LogWarning("The terminal host did not answer within {Timeout}", OperationTimeout);
            return Reply.Unreached;
        }
        catch (Exception ex) when (ex is IOException or JsonException or TimeoutException or UnauthorizedAccessException)
        {
            return Reply.Unreached;
        }
        finally
        {
            if (pipe is not null) await pipe.DisposeAsync();
        }
    }

    /// <summary>The token has to ride every request, and the payloads are anonymous types.</summary>
    private static Dictionary<string, object?> Merge(string token, object payload)
    {
        var merged = new Dictionary<string, object?> { ["token"] = token };
        foreach (var property in payload.GetType().GetProperties())
        {
            merged[JsonNamingPolicy.CamelCase.ConvertName(property.Name)] = property.GetValue(payload);
        }
        return merged;
    }

    private static async Task<NamedPipeClientStream?> ConnectAsync(HostEndpoint endpoint, CancellationToken cancel)
    {
        var pipe = new NamedPipeClientStream(".", endpoint.Pipe, PipeDirection.InOut, PipeOptions.Asynchronous);
        try
        {
            await pipe.ConnectAsync((int)ConnectTimeout.TotalMilliseconds, cancel);
            return pipe;
        }
        catch (Exception ex) when (ex is TimeoutException or IOException or UnauthorizedAccessException or OperationCanceledException)
        {
            await pipe.DisposeAsync();
            return null;
        }
    }

    /// <summary>
    /// A daemon that answers, starting one if there is not. Concurrent callers share a single
    /// attempt, so a burst of requests before the daemon exists starts it once rather than once
    /// per request.
    /// </summary>
    private async Task<HostEndpoint?> EnsureAsync(CancellationToken cancel)
    {
        if (_endpoint is { } cached) return cached;

        await _ensureGate.WaitAsync(cancel);
        try
        {
            if (_endpoint is { } raced) return raced;

            var found = await FindOrStartAsync(cancel);
            _endpoint = found;
            return found;
        }
        finally
        {
            _ensureGate.Release();
        }
    }

    private async Task<HostEndpoint?> FindOrStartAsync(CancellationToken cancel)
    {
        if (ReadInfoFile() is { } existing)
        {
            var version = await PingAsync(existing, cancel);
            if (version == ProtocolVersion) return existing;

            if (version is not null)
            {
                // A live daemon speaking a protocol this build does not know - the two really can
                // be different builds, since the daemon survives the server being rebuilt. Retire
                // it and wait for it to actually go before starting a replacement.
                _log.LogInformation(
                    "Retiring a terminal host speaking protocol {Found} (this build speaks {Expected})",
                    version, ProtocolVersion);

                using var reply = await SendAsync(existing, new { op = "shutdown" }, cancel);
                await WaitUntilGoneAsync(existing, cancel);
            }
        }

        return await StartAsync(cancel);
    }

    private async Task<HostEndpoint?> StartAsync(CancellationToken cancel)
    {
        string executable = ResolveHostPath();
        if (executable.Length == 0) return null;

        try
        {
            int pid = DetachedProcess.Start(executable);
            _log.LogInformation("Started the terminal host (pid {Pid})", pid);
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Could not start the terminal host at {Path}", executable);
            return null;
        }

        var deadline = DateTimeOffset.UtcNow + StartTimeout;
        while (DateTimeOffset.UtcNow < deadline && !cancel.IsCancellationRequested)
        {
            if (ReadInfoFile() is { } fresh && await PingAsync(fresh, cancel) == ProtocolVersion) return fresh;
            await Task.Delay(StartPoll, cancel);
        }

        _log.LogError("Timed out waiting for the terminal host to start");
        return null;
    }

    private async Task WaitUntilGoneAsync(HostEndpoint endpoint, CancellationToken cancel)
    {
        var deadline = DateTimeOffset.UtcNow + StartTimeout;
        while (DateTimeOffset.UtcNow < deadline && !cancel.IsCancellationRequested)
        {
            if (await PingAsync(endpoint, cancel) is null) return;
            await Task.Delay(StartPoll, cancel);
        }
    }

    /// <summary>The daemon's protocol version, or null if nothing answered.</summary>
    private async Task<int?> PingAsync(HostEndpoint endpoint, CancellationToken cancel)
    {
        using var reply = await SendAsync(endpoint, new { op = "ping" }, cancel);
        if (!reply.Outcome.Ok) return null;

        return reply.Root.TryGetProperty("version", out var version) && version.TryGetInt32(out int value)
            ? value
            : null;
    }

    private HostEndpoint? ReadInfoFile()
    {
        try
        {
            if (!File.Exists(_infoPath)) return null;

            using var document = JsonDocument.Parse(File.ReadAllText(_infoPath));
            var root = document.RootElement;

            return root.TryGetProperty("pipe", out var pipe)
                && root.TryGetProperty("token", out var token)
                && pipe.GetString() is { Length: > 0 } pipeName
                && token.GetString() is { Length: > 0 } tokenValue
                ? new HostEndpoint(pipeName, tokenValue)
                : null;
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            return null; // a half-written or stale file is the same as no file
        }
    }

    /// <summary>Forgets an endpoint that stopped answering, so the next call re-reads and restarts.</summary>
    private void Invalidate(HostEndpoint endpoint)
    {
        if (ReferenceEquals(_endpoint, endpoint)) _endpoint = null;
    }

    private static string ResolveHostPath()
    {
        var baseDirectory = AppContext.BaseDirectory;
        string[] candidates =
        [
            Path.Combine(baseDirectory, "Argus.TerminalHost.exe"),
            Path.Combine(baseDirectory, "TerminalHost", "Argus.TerminalHost.exe"),
        ];

        return candidates.FirstOrDefault(File.Exists) ?? string.Empty;
    }

    private static bool IsOk(JsonElement root) =>
        root.TryGetProperty("ok", out var ok) && ok.ValueKind == JsonValueKind.True;

    private static JsonElement Read(JsonElement root, string name) =>
        root.TryGetProperty(name, out var value) ? value : default;

    private static TerminalOutcome OutcomeOf(JsonElement root)
    {
        if (IsOk(root)) return TerminalOutcome.Pass;

        int status = root.TryGetProperty("status", out var value) && value.TryGetInt32(out int parsed) ? parsed : 500;
        string error = root.TryGetProperty("error", out var message) ? message.GetString() ?? "Unknown error" : "Unknown error";
        return TerminalOutcome.Fail(status, error);
    }

    public void Dispose() => _ensureGate.Dispose();

    private sealed record HostEndpoint(string Pipe, string Token);

    /// <summary>
    /// One answer. <c>Reached</c> separates "the daemon said no" from "nothing answered" - only
    /// the second is worth invalidating the endpoint and retrying over.
    /// </summary>
    private sealed class Reply : IDisposable
    {
        public static Reply Unreached { get; } = new() { Outcome = TerminalHostClient.Unreachable };

        public bool Reached { get; init; }

        public JsonDocument? Document { get; init; }

        public TerminalOutcome Outcome { get; init; }

        public JsonElement Root => Document?.RootElement ?? default;

        public void Dispose() => Document?.Dispose();
    }
}

/// <summary>
/// A live connection to one terminal. Output comes down <see cref="ReadAsync"/>; input and
/// resizes go back up the same connection, which is what keeps keystrokes in order - two
/// connections could deliver "ls" as "sl", and in a shell that matters.
/// </summary>
public sealed class TerminalAttachment(
    NamedPipeClientStream pipe,
    StreamReader reader,
    StreamWriter writer,
    bool running) : IAsyncDisposable
{
    private readonly SemaphoreSlim _writeGate = new(1, 1);

    /// <summary>False when the shell had already exited before this attach - its replay is all there is.</summary>
    public bool Running { get; } = running;

    /// <summary>
    /// Every event until the terminal exits or the connection drops. The first is the replay, when
    /// there is anything to replay.
    /// </summary>
    public async IAsyncEnumerable<TerminalHostEvent> ReadAsync([EnumeratorCancellation] CancellationToken cancel)
    {
        while (!cancel.IsCancellationRequested)
        {
            string? line;
            try
            {
                line = await reader.ReadLineAsync(cancel);
            }
            catch (Exception ex) when (ex is IOException or ObjectDisposedException or OperationCanceledException)
            {
                yield break;
            }

            if (line is null) yield break;

            TerminalHostEvent? evt;
            try
            {
                evt = JsonSerializer.Deserialize<TerminalHostEvent>(line, TerminalAttachmentJson.Options);
            }
            catch (JsonException)
            {
                continue; // a line this build cannot read is not a reason to drop the terminal
            }

            if (evt is null) continue;

            yield return evt;
            if (evt.Type == "exit") yield break;
        }
    }

    public Task SendInputAsync(string data, CancellationToken cancel) =>
        SendAsync(new { op = "write", data }, cancel);

    public Task SendResizeAsync(int cols, int rows, CancellationToken cancel) =>
        SendAsync(new { op = "resize", cols, rows }, cancel);

    private async Task SendAsync(object payload, CancellationToken cancel)
    {
        await _writeGate.WaitAsync(cancel);
        try
        {
            await writer.WriteLineAsync(
                JsonSerializer.Serialize(payload, TerminalAttachmentJson.Options).AsMemory(), cancel);
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException)
        {
            // The daemon went away mid-keystroke. The read side is about to end this attach.
        }
        finally
        {
            _writeGate.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        _writeGate.Dispose();
        reader.Dispose();
        try { await writer.DisposeAsync(); } catch (IOException) { /* the pipe is already gone */ }
        await pipe.DisposeAsync();
    }
}

internal static class TerminalAttachmentJson
{
    public static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
    };
}
