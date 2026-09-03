using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using Argus.TerminalHost;

namespace Argus.Server.Tests;

/// <summary>
/// The daemon's pipe protocol, over a real named pipe.
///
/// It is worth testing over a real pipe rather than by calling the registry directly, because the
/// interesting parts are exactly the ones a direct call would skip: that the token is checked
/// before any operation runs, that an attach connection keeps taking input after its header, and
/// that a client sending nonsense gets an answer instead of taking the daemon down. The server
/// this exercises outlives Argus.Server by design, so it is the one place a protocol mistake would
/// survive a fix and a restart.
/// </summary>
public class TerminalHostProtocolTests : IAsyncLifetime
{
    private const string Token = "a-token-only-this-test-knows";
    private const string TerminalId = "0123456789abcdef0123456789abcdef";

    private readonly string _pipeName = $"Argus.Terminals.Test.{Guid.NewGuid():N}";
    private readonly CancellationTokenSource _stopping = new();
    private readonly FakePty _pty = new();
    private TerminalRegistry _registry = null!;
    private Task _serving = null!;

    public Task InitializeAsync()
    {
        _registry = new TerminalRegistry(spawn: (_, _, _, _, _) => _pty, newId: () => TerminalId);
        var server = new HostServer(_registry, _pipeName, Token, onShutdown: () => _stopping.Cancel());
        _serving = server.RunAsync(_stopping.Token);
        return Task.CompletedTask;
    }

    public async Task DisposeAsync()
    {
        await _stopping.CancelAsync();
        await _serving.ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
        _registry.Dispose();
        _pty.Dispose();
        _stopping.Dispose();
    }

    [Fact]
    public async Task Ping_answers_with_the_protocol_version()
    {
        // What the client compares to decide whether to talk to this daemon or retire it - the
        // daemon really can be an older build than the server, since it survives a rebuild.
        var reply = await CallAsync(new { token = Token, op = "ping" });

        Assert.True(reply.GetProperty("ok").GetBoolean());
        Assert.Equal(TerminalProtocol.Version, reply.GetProperty("version").GetInt32());
    }

    [Fact]
    public async Task A_wrong_token_is_refused_before_the_operation_runs()
    {
        // The token is the only thing between any local process and a shell running as the user,
        // so "refused" has to mean the open never happened, not that it happened and was hidden.
        var reply = await CallAsync(new { token = "wrong", op = "open" });

        Assert.False(reply.GetProperty("ok").GetBoolean());
        Assert.Equal(403, reply.GetProperty("status").GetInt32());
        Assert.Empty(_registry.List());
    }

    [Fact]
    public async Task A_missing_token_is_refused_the_same_way()
    {
        var reply = await CallAsync(new { op = "list" });

        Assert.Equal(403, reply.GetProperty("status").GetInt32());
    }

    [Fact]
    public async Task An_unknown_op_is_a_bad_request_not_a_dead_daemon()
    {
        var reply = await CallAsync(new { token = Token, op = "rm-rf" });
        Assert.Equal(400, reply.GetProperty("status").GetInt32());

        // Still answering afterwards, which is the half that matters.
        Assert.True((await CallAsync(new { token = Token, op = "ping" })).GetProperty("ok").GetBoolean());
    }

    [Fact]
    public async Task A_malformed_request_line_is_answered_rather_than_dropped()
    {
        await using var pipe = await ConnectAsync();
        var writer = new StreamWriter(pipe, new UTF8Encoding(false), 4096, leaveOpen: true) { AutoFlush = true };
        var reader = new StreamReader(pipe, new UTF8Encoding(false), false, 4096, leaveOpen: true);

        await writer.WriteLineAsync("{not json at all");

        using var document = JsonDocument.Parse((await reader.ReadLineAsync())!);
        Assert.Equal(400, document.RootElement.GetProperty("status").GetInt32());
    }

    [Fact]
    public async Task Open_then_list_reports_the_terminal()
    {
        var opened = await CallAsync(new { token = Token, op = "open", cols = 100, rows = 30 });
        Assert.True(opened.GetProperty("ok").GetBoolean());
        Assert.Equal(TerminalId, opened.GetProperty("terminal").GetProperty("terminalId").GetString());

        var listed = await CallAsync(new { token = Token, op = "list" });
        Assert.Single(listed.GetProperty("terminals").EnumerateArray());
    }

    [Fact]
    public async Task Attach_streams_the_replay_then_live_output()
    {
        await CallAsync(new { token = Token, op = "open" });
        _pty.Emit("already on screen\r\n");
        await _pty.Flushed();

        await using var pipe = await ConnectAsync();
        var writer = new StreamWriter(pipe, new UTF8Encoding(false), 4096, leaveOpen: true) { AutoFlush = true };
        var reader = new StreamReader(pipe, new UTF8Encoding(false), false, 4096, leaveOpen: true);

        await writer.WriteLineAsync(JsonSerializer.Serialize(new { token = Token, op = "attach", terminalId = TerminalId }));

        var header = await ReadLineAsync(reader);
        Assert.True(header.GetProperty("ok").GetBoolean());
        Assert.True(header.GetProperty("running").GetBoolean());

        var replay = await ReadLineAsync(reader);
        Assert.Equal("replay", replay.GetProperty("type").GetString());
        Assert.Contains("already on screen", replay.GetProperty("data").GetString());

        _pty.Emit("live output");
        var live = await ReadLineAsync(reader);
        Assert.Equal("data", live.GetProperty("type").GetString());
        Assert.Equal("live output", live.GetProperty("data").GetString());
    }

    [Fact]
    public async Task An_attach_connection_keeps_taking_input_after_its_header()
    {
        // The reason keystrokes cannot arrive out of order: they ride the connection that is
        // already open rather than racing each other over fresh ones.
        await CallAsync(new { token = Token, op = "open" });

        await using var pipe = await ConnectAsync();
        var writer = new StreamWriter(pipe, new UTF8Encoding(false), 4096, leaveOpen: true) { AutoFlush = true };
        var reader = new StreamReader(pipe, new UTF8Encoding(false), false, 4096, leaveOpen: true);

        await writer.WriteLineAsync(JsonSerializer.Serialize(new { token = Token, op = "attach", terminalId = TerminalId }));
        await ReadLineAsync(reader);

        await writer.WriteLineAsync(JsonSerializer.Serialize(new { op = "write", data = "ls\r" }));
        await writer.WriteLineAsync(JsonSerializer.Serialize(new { op = "resize", cols = 120, rows = 40 }));

        await WaitFor(() => _pty.Written == "ls\r" && _pty.LastResize == (120, 40));
    }

    [Fact]
    public async Task Attaching_to_a_terminal_that_is_not_there_says_so_and_closes()
    {
        await using var pipe = await ConnectAsync();
        var writer = new StreamWriter(pipe, new UTF8Encoding(false), 4096, leaveOpen: true) { AutoFlush = true };
        var reader = new StreamReader(pipe, new UTF8Encoding(false), false, 4096, leaveOpen: true);

        await writer.WriteLineAsync(JsonSerializer.Serialize(
            new { token = Token, op = "attach", terminalId = "ffffffffffffffffffffffffffffffff" }));

        var reply = await ReadLineAsync(reader);
        Assert.False(reply.GetProperty("ok").GetBoolean());
        Assert.Equal(404, reply.GetProperty("status").GetInt32());
    }

    [Fact]
    public async Task Killing_a_terminal_over_the_pipe_removes_it()
    {
        await CallAsync(new { token = Token, op = "open" });

        var killed = await CallAsync(new { token = Token, op = "kill", terminalId = TerminalId });

        Assert.True(killed.GetProperty("ok").GetBoolean());
        Assert.Empty(_registry.List());
    }

    // ---------------------------------------------------------------- helpers

    private async Task<NamedPipeClientStream> ConnectAsync()
    {
        var pipe = new NamedPipeClientStream(".", _pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
        await pipe.ConnectAsync(5000);
        return pipe;
    }

    /// <summary>One request line in, one response line out - the shape of every op but attach.</summary>
    private async Task<JsonElement> CallAsync(object payload)
    {
        await using var pipe = await ConnectAsync();
        var writer = new StreamWriter(pipe, new UTF8Encoding(false), 4096, leaveOpen: true) { AutoFlush = true };
        var reader = new StreamReader(pipe, new UTF8Encoding(false), false, 4096, leaveOpen: true);

        await writer.WriteLineAsync(JsonSerializer.Serialize(payload));
        return await ReadLineAsync(reader);
    }

    private static async Task<JsonElement> ReadLineAsync(StreamReader reader)
    {
        string? line = await reader.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(10));
        Assert.NotNull(line);

        // Cloned, because the document is disposed the moment this returns.
        using var document = JsonDocument.Parse(line);
        return document.RootElement.Clone();
    }

    private static async Task WaitFor(Func<bool> condition)
    {
        var deadline = DateTimeOffset.UtcNow.AddSeconds(5);
        while (DateTimeOffset.UtcNow < deadline)
        {
            if (condition()) return;
            await Task.Delay(25);
        }

        Assert.Fail("The condition never became true.");
    }
}
