using System.IO.Pipes;
using System.Text;
using Argus.TerminalHost;

namespace Argus.Server.Tests;

/// <summary>
/// The terminal registry, driven against a fake pty rather than a real pseudo console.
///
/// ConPTY itself is not what these check - that needs a real shell and is verified by running the
/// thing. What they check is everything around it: that a terminal is remembered, that its output
/// is buffered and replayed to whoever attaches next, that exactly one exit event reaches every
/// watcher, and that a dead terminal refuses input rather than pretending.
/// </summary>
public class TerminalRegistryTests
{
    private static TerminalRegistry RegistryWith(FakePty pty, string id = "0123456789abcdef0123456789abcdef") =>
        new(spawn: (_, _, _, _, _) => pty, newId: () => id);

    [Fact]
    public void Opening_a_terminal_lists_it_as_running()
    {
        using var pty = new FakePty();
        using var registry = RegistryWith(pty);

        var opened = registry.Open(cwd: null, cols: 100, rows: 30);

        Assert.True(opened.Ok);
        Assert.True(opened.Value!.Running);
        Assert.Equal(100, opened.Value.Cols);
        Assert.Single(registry.List());
    }

    [Fact]
    public void A_cwd_that_is_not_a_directory_falls_back_to_the_profile()
    {
        // Rather than failing the open: the page can pass anything, and a terminal in the wrong
        // folder is recoverable in a way "could not start" is not.
        using var pty = new FakePty();
        using var registry = RegistryWith(pty);

        var opened = registry.Open(cwd: @"Z:\nothing\here", cols: null, rows: null);

        Assert.True(opened.Ok);
        Assert.Equal(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), opened.Value!.Cwd);
    }

    [Fact]
    public void The_marker_carries_the_terminal_id_into_the_shell()
    {
        // What the straggler scan matches on. Without it, a shell that outlives the daemon is
        // indistinguishable from any other pwsh on the machine.
        using var pty = new FakePty();
        using var registry = RegistryWith(pty, id: "abcdef0123456789abcdef0123456789");

        var opened = registry.Open(cwd: null, cols: null, rows: null);

        Assert.Contains("abcdef0123456789abcdef0123456789", opened.Value!.Command);
    }

    [Fact]
    public async Task Output_is_buffered_and_replayed_to_whoever_attaches_next()
    {
        // The whole reason a browser can close and come back to a terminal mid-build.
        using var pty = new FakePty();
        using var registry = RegistryWith(pty);
        registry.Open(cwd: null, cols: null, rows: null);

        pty.Emit("first line\r\n");
        await pty.Flushed();

        var session = registry.Find("0123456789abcdef0123456789abcdef")!;
        var (replay, running, _, subscription) = session.Attach();
        using (subscription)
        {
            Assert.True(running);
            Assert.Contains("first line", replay);
        }
    }

    [Fact]
    public async Task Live_output_reaches_everyone_attached()
    {
        using var pty = new FakePty();
        using var registry = RegistryWith(pty);
        registry.Open(cwd: null, cols: null, rows: null);
        var session = registry.Find("0123456789abcdef0123456789abcdef")!;

        var (_, _, first, firstSubscription) = session.Attach();
        var (_, _, second, secondSubscription) = session.Attach();

        using (firstSubscription)
        using (secondSubscription)
        {
            pty.Emit("shared");

            Assert.Equal("shared", (await Read(first)).Data);
            Assert.Equal("shared", (await Read(second)).Data);
        }
    }

    [Fact]
    public async Task A_shell_that_exits_delivers_exactly_one_exit_event()
    {
        // Everyone attached ends their socket on it, so a second would close an ended response.
        using var pty = new FakePty();
        using var registry = RegistryWith(pty);
        registry.Open(cwd: null, cols: null, rows: null);
        var session = registry.Find("0123456789abcdef0123456789abcdef")!;

        var (_, _, events, subscription) = session.Attach();
        using (subscription)
        {
            pty.Exit(3);

            var exit = await Read(events);
            Assert.Equal("exit", exit.Type);
            Assert.Equal(3, exit.ExitCode);

            // The channel completes on exit, so nothing else can ever arrive.
            Assert.False(await events.WaitToReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(2)));
        }
    }

    [Fact]
    public async Task Attaching_to_a_terminal_that_already_exited_still_gets_its_last_screen()
    {
        using var pty = new FakePty();
        using var registry = RegistryWith(pty);
        registry.Open(cwd: null, cols: null, rows: null);
        var session = registry.Find("0123456789abcdef0123456789abcdef")!;

        pty.Emit("build failed");
        await pty.Flushed();
        pty.Exit(1);
        await WaitFor(() => !session.Running);

        var (replay, running, _, subscription) = session.Attach();
        using (subscription)
        {
            Assert.False(running);
            Assert.Contains("build failed", replay);
        }
    }

    [Fact]
    public async Task Writing_to_a_terminal_that_has_exited_is_refused_rather_than_swallowed()
    {
        using var pty = new FakePty();
        using var registry = RegistryWith(pty);
        registry.Open(cwd: null, cols: null, rows: null);

        pty.Exit(0);
        await WaitFor(() => !registry.Find("0123456789abcdef0123456789abcdef")!.Running);

        var written = registry.Write("0123456789abcdef0123456789abcdef", "ls\r");

        Assert.False(written.Ok);
        Assert.Equal(409, written.Status);
    }

    [Fact]
    public void Keystrokes_reach_the_shell_unchanged()
    {
        using var pty = new FakePty();
        using var registry = RegistryWith(pty);
        registry.Open(cwd: null, cols: null, rows: null);

        registry.Write("0123456789abcdef0123456789abcdef", "ls -la\r");

        Assert.Equal("ls -la\r", pty.Written);
    }

    [Fact]
    public void An_empty_write_is_a_bad_request_not_a_silent_no_op()
    {
        using var pty = new FakePty();
        using var registry = RegistryWith(pty);
        registry.Open(cwd: null, cols: null, rows: null);

        Assert.Equal(400, registry.Write("0123456789abcdef0123456789abcdef", "").Status);
    }

    [Theory]
    [InlineData(0, 24)]
    [InlineData(80, 9999)]
    [InlineData(-1, -1)]
    public void A_resize_outside_the_sane_range_is_refused(int cols, int rows)
    {
        using var pty = new FakePty();
        using var registry = RegistryWith(pty);
        registry.Open(cwd: null, cols: null, rows: null);

        Assert.Equal(400, registry.Resize("0123456789abcdef0123456789abcdef", cols, rows).Status);
    }

    [Fact]
    public void A_resize_is_remembered_and_passed_on()
    {
        using var pty = new FakePty();
        using var registry = RegistryWith(pty);
        registry.Open(cwd: null, cols: null, rows: null);

        registry.Resize("0123456789abcdef0123456789abcdef", 120, 40);

        Assert.Equal((120, 40), pty.LastResize);
        Assert.Equal(120, registry.List()[0].Cols);
    }

    [Fact]
    public void Killing_a_terminal_removes_it_from_the_list()
    {
        using var pty = new FakePty();
        using var registry = RegistryWith(pty);
        registry.Open(cwd: null, cols: null, rows: null);

        Assert.True(registry.Kill("0123456789abcdef0123456789abcdef").Ok);
        Assert.Empty(registry.List());
        Assert.True(pty.Killed);
    }

    [Fact]
    public void Killing_a_terminal_that_is_not_there_is_a_404_not_a_crash()
    {
        using var pty = new FakePty();
        using var registry = RegistryWith(pty);

        Assert.Equal(404, registry.Kill("ffffffffffffffffffffffffffffffff").Status);
    }

    [Fact]
    public async Task Renaming_works_on_a_terminal_whose_shell_has_exited()
    {
        // The tab is still on screen until it is closed, so refusing to rename it would be a
        // rule with nothing behind it.
        using var pty = new FakePty();
        using var registry = RegistryWith(pty);
        registry.Open(cwd: null, cols: null, rows: null);

        pty.Exit(0);
        await WaitFor(() => !registry.Find("0123456789abcdef0123456789abcdef")!.Running);

        Assert.True(registry.Rename("0123456789abcdef0123456789abcdef", "  Build  ").Ok);
        Assert.Equal("Build", registry.List()[0].Name);
    }

    [Fact]
    public void An_empty_name_clears_the_label_rather_than_setting_a_blank_one()
    {
        using var pty = new FakePty();
        using var registry = RegistryWith(pty);
        registry.Open(cwd: null, cols: null, rows: null);

        registry.Rename("0123456789abcdef0123456789abcdef", "Build");
        registry.Rename("0123456789abcdef0123456789abcdef", "   ");

        Assert.Null(registry.List()[0].Name);
    }

    [Fact]
    public void Opening_past_the_ceiling_is_refused_rather_than_spawning_without_bound()
    {
        var ptys = new List<FakePty>();
        using var registry = new TerminalRegistry(
            spawn: (_, _, _, _, _) =>
            {
                var pty = new FakePty();
                ptys.Add(pty);
                return pty;
            },
            newId: () => Guid.NewGuid().ToString("N"));

        try
        {
            for (int i = 0; i < 40; i++) Assert.True(registry.Open(null, null, null).Ok);

            var refused = registry.Open(null, null, null);
            Assert.False(refused.Ok);
            Assert.Equal(429, refused.Status);
        }
        finally
        {
            registry.KillAll();
            foreach (var pty in ptys) pty.Dispose();
        }
    }

    private static async Task<TerminalEvent> Read(System.Threading.Channels.ChannelReader<TerminalEvent> events) =>
        await events.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));

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

/// <summary>
/// A pty the test drives by hand. A real OS pipe carries the output, because the session's read
/// loop blocks on a synchronous read and a MemoryStream would just report end of file.
/// </summary>
internal sealed class FakePty : IPty
{
    private readonly AnonymousPipeServerStream _writeEnd;
    private readonly ManualResetEvent _exited = new(false);
    private readonly StringBuilder _written = new();
    private bool _disposed;

    public FakePty()
    {
        _writeEnd = new AnonymousPipeServerStream(PipeDirection.Out);
        Output = new AnonymousPipeClientStream(PipeDirection.In, _writeEnd.ClientSafePipeHandle);
    }

    public Stream Output { get; }

    public int ProcessId => 4242;

    public WaitHandle Exited => _exited;

    public int? ExitCode { get; private set; }

    public bool Killed { get; private set; }

    public (int Cols, int Rows) LastResize { get; private set; }

    public string Written { get { lock (_written) return _written.ToString(); } }

    /// <summary>Whatever the shell would have printed.</summary>
    public void Emit(string text)
    {
        var bytes = Encoding.UTF8.GetBytes(text);
        _writeEnd.Write(bytes, 0, bytes.Length);
        _writeEnd.Flush();
    }

    /// <summary>Gives the session's reader a moment to pick up what was just emitted.</summary>
    public Task Flushed() => Task.Delay(150);

    public void Exit(int code)
    {
        ExitCode = code;
        _exited.Set();
    }

    public void Write(string data)
    {
        lock (_written) _written.Append(data);
    }

    public void Resize(short cols, short rows) => LastResize = (cols, rows);

    public void Kill()
    {
        Killed = true;
        ExitCode ??= 1;
        _exited.Set();
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        _exited.Set();
        Output.Dispose();
        _writeEnd.Dispose();
        _exited.Dispose();
    }
}
