using System.IO.Pipes;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Argus.TerminalHost;

/// <summary>
/// The daemon's protocol server: newline-delimited JSON over a Windows named pipe.
///
/// One connection per operation, except <c>attach</c>, which stays open for the life of the
/// terminal. The client's first line is always <c>{ token, op, ... }</c>; the answer is one JSON
/// line for a one-shot op, or a header line followed by a live stream of events for
/// <c>attach</c> - and on an attach connection the client may keep sending <c>write</c> and
/// <c>resize</c> lines afterwards, so keystrokes ride the connection that is already open rather
/// than costing a fresh pipe each. That also gets ordering for free: in a shell, "ls" arriving as
/// "sl" matters, and one socket cannot reorder itself.
///
/// Security: this pipe drives shells running as the user, which is arbitrary code execution by
/// design (see <see cref="TerminalRegistry"/>). The token is what stands between any local process
/// and that - 256 random bits minted once per daemon run and written only to a file under the
/// user's own profile, which gets the OS's normal per-user permissions. A wrong or missing token
/// gets one JSON line and the pipe closed, before any op runs. Every other field in a request is
/// used purely as data: a dictionary key, pty input bytes, a CreateProcess working directory, a
/// display string. The program and argument list stay in <see cref="TerminalRegistry"/>, out of
/// reach of anything that arrives here.
/// </summary>
internal sealed class HostServer(TerminalRegistry registry, string pipeName, string token, Action onShutdown)
{
    /// <summary>Cap on one request line. A paste is well under it; anything over is not a request.</summary>
    private const int MaxRequestBytes = 256 * 1024;

    /// <summary>
    /// Accepts connections until cancelled. Each one is handled on its own task, so a terminal
    /// sitting attached for a week never blocks the next `list`.
    /// </summary>
    public async Task RunAsync(CancellationToken cancel)
    {
        while (!cancel.IsCancellationRequested)
        {
            var pipe = new NamedPipeServerStream(
                pipeName,
                PipeDirection.InOut,
                NamedPipeServerStream.MaxAllowedServerInstances,
                PipeTransmissionMode.Byte,
                PipeOptions.Asynchronous);

            try
            {
                await pipe.WaitForConnectionAsync(cancel);
            }
            catch (Exception ex) when (ex is OperationCanceledException or IOException or ObjectDisposedException)
            {
                await pipe.DisposeAsync();
                if (cancel.IsCancellationRequested) return;
                continue;
            }

            _ = Task.Run(() => HandleAsync(pipe, cancel), CancellationToken.None);
        }
    }

    private async Task HandleAsync(NamedPipeServerStream pipe, CancellationToken cancel)
    {
        // A client that vanishes mid-request - closes its end, or its process dies - is routine,
        // not a bug, and must never surface as an exception that takes the daemon down with it.
        try
        {
            using var reader = new StreamReader(pipe, new UTF8Encoding(false), false, 4096, leaveOpen: true);
            await using var writer = new StreamWriter(pipe, new UTF8Encoding(false), 4096, leaveOpen: true) { AutoFlush = true };

            string? line = await ReadLineAsync(reader, cancel);
            if (line is null) return;

            HostRequest? request;
            try
            {
                request = JsonSerializer.Deserialize<HostRequest>(line, TerminalProtocol.Json);
            }
            catch (JsonException ex)
            {
                await Reply(writer, new { ok = false, status = 400, error = $"Malformed request: {ex.Message}" });
                return;
            }

            if (request?.Op is null)
            {
                await Reply(writer, new { ok = false, status = 400, error = "Request must be a JSON object with a string `op`." });
                return;
            }

            if (!TokenMatches(request.Token))
            {
                await Reply(writer, new { ok = false, status = 403, error = "Bad or missing token." });
                return;
            }

            await DispatchAsync(request, reader, writer, cancel);
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or OperationCanceledException)
        {
            // The client went away. Nothing to report to.
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"terminal-host: connection failed: {ex}");
        }
        finally
        {
            try { pipe.Disconnect(); } catch (Exception ex) when (ex is IOException or InvalidOperationException or ObjectDisposedException) { }
            await pipe.DisposeAsync();
        }
    }

    private async Task DispatchAsync(HostRequest request, StreamReader reader, StreamWriter writer, CancellationToken cancel)
    {
        switch (request.Op)
        {
            case "ping":
                await Reply(writer, new { ok = true, version = TerminalProtocol.Version, pid = Environment.ProcessId });
                return;

            case "open":
            {
                var opened = registry.Open(request.Cwd, request.Cols, request.Rows);
                await Reply(writer, opened.Ok
                    ? new { ok = true, terminal = opened.Value }
                    : Problem(opened.Status, opened.Error));
                return;
            }

            case "attach":
                await AttachAsync(request.TerminalId, reader, writer, cancel);
                return;

            case "write":
                await ReplyTo(writer, registry.Write(request.TerminalId, request.Data));
                return;

            case "resize":
                await ReplyTo(writer, registry.Resize(request.TerminalId, request.Cols, request.Rows));
                return;

            case "kill":
                await ReplyTo(writer, registry.Kill(request.TerminalId));
                return;

            case "rename":
                await ReplyTo(writer, registry.Rename(request.TerminalId, request.Name));
                return;

            case "list":
                await Reply(writer, new { ok = true, terminals = registry.List() });
                return;

            case "killAll":
                registry.KillAll();
                await Reply(writer, new { ok = true });
                return;

            case "shutdown":
                registry.KillAll();
                await Reply(writer, new { ok = true });
                onShutdown();
                return;

            default:
                await Reply(writer, new { ok = false, status = 400, error = $"Unknown op: {request.Op}" });
                return;
        }
    }

    /// <summary>
    /// One terminal's live output, relayed until it exits or the client goes away, while the same
    /// connection keeps accepting input.
    /// </summary>
    private async Task AttachAsync(string? terminalId, StreamReader reader, StreamWriter writer, CancellationToken cancel)
    {
        if (registry.Find(terminalId) is not { } session)
        {
            await Reply(writer, new { ok = false, status = 404, error = "No such terminal. It may have been closed." });
            return;
        }

        var (replay, running, events, subscription) = session.Attach();
        using (subscription)
        using (var stop = CancellationTokenSource.CreateLinkedTokenSource(cancel))
        {
            await Reply(writer, new { ok = true, running, cols = session.Cols, rows = session.Rows });
            if (replay.Length > 0) await Reply(writer, new TerminalEvent("replay", replay));

            if (!running)
            {
                // Already dead by the time we attached: there is no future exit event to relay, so
                // this is the one place one is made up rather than forwarded.
                await Reply(writer, new TerminalEvent("exit", ExitCode: session.ExitCode));
                return;
            }

            // Follow-up input on the same connection. It ends when the client closes its end,
            // which is also what tells the pump below to stop.
            var input = Task.Run(async () =>
            {
                try
                {
                    while (await ReadLineAsync(reader, stop.Token) is { } follow)
                    {
                        var next = JsonSerializer.Deserialize<HostRequest>(follow, TerminalProtocol.Json);
                        if (next?.Op == "write") session.Write(next.Data ?? string.Empty);
                        else if (next?.Op == "resize" && next.Cols is { } c && next.Rows is { } r) registry.Resize(terminalId, c, r);
                    }
                }
                catch (Exception ex) when (ex is IOException or ObjectDisposedException or OperationCanceledException or JsonException)
                {
                    // The client hung up, or sent something unreadable. Either way this attach is over.
                }
                finally
                {
                    await stop.CancelAsync();
                }
            }, CancellationToken.None);

            try
            {
                await foreach (var evt in events.ReadAllAsync(stop.Token))
                {
                    await Reply(writer, evt);
                    if (evt.Type == "exit") break;
                }
            }
            catch (OperationCanceledException)
            {
                // The client closed its end while the terminal was still alive - normal.
            }
            finally
            {
                await stop.CancelAsync();
                await input.ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
            }
        }
    }

    /// <summary>
    /// A line, with a hard cap so a client that never sends a newline cannot grow this process
    /// without bound. Returns null at end of stream.
    /// </summary>
    private static async Task<string?> ReadLineAsync(StreamReader reader, CancellationToken cancel)
    {
        var line = await reader.ReadLineAsync(cancel);
        if (line is not null && line.Length > MaxRequestBytes) throw new IOException("Request line is too long.");
        return line;
    }

    private static object Problem(int status, string? error) => new { ok = false, status, error };

    private static Task ReplyTo(StreamWriter writer, HostResult<bool> result) =>
        Reply(writer, result.Ok ? new { ok = true } : Problem(result.Status, result.Error));

    private static Task Reply(StreamWriter writer, object payload) =>
        writer.WriteLineAsync(JsonSerializer.Serialize(payload, TerminalProtocol.Json));

    /// <summary>
    /// Fixed-time compare. A plain == would return sooner the earlier the first wrong character
    /// is, which over enough tries recovers the token one character at a time.
    /// </summary>
    private bool TokenMatches(string? candidate) =>
        candidate is not null
        && CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(candidate), Encoding.UTF8.GetBytes(token));

    /// <summary>
    /// Every field any op takes. One flat shape rather than a union: the daemon reads exactly the
    /// fields the op it dispatched needs, and an op that ignores a field is not an error.
    /// </summary>
    private sealed record HostRequest(
        string? Token,
        string? Op,
        string? TerminalId,
        string? Data,
        int? Cols,
        int? Rows,
        string? Cwd,
        string? Name);
}
