using System.Text;
using System.Threading.Channels;

namespace Argus.TerminalHost;

/// <summary>
/// One terminal: a shell under a pseudo console, everything it has printed recently, and whoever
/// is currently watching it.
///
/// The replay buffer is the reason a browser can close, the server can restart, and the screen
/// still comes back exactly as it was - a fresh attach gets the buffer before it gets live output,
/// so the terminal repaints rather than starting blank halfway through a build.
///
/// Output arrives as bytes and leaves as text, decoded through one stateful <see cref="Decoder"/>
/// per session: a multi-byte character split across two pipe reads has to survive the split, and a
/// fresh decoder per read would turn it into two replacement characters.
/// </summary>
internal sealed class PtySession : IDisposable
{
    /// <summary>Replay cap per terminal. Enough to repaint a busy screen, bounded so 40 of them are not a leak.</summary>
    private const int MaxBufferChars = 256 * 1024;

    /// <summary>
    /// How far one watcher may fall behind before it is dropped. A browser that cannot keep up is
    /// better off reattaching and replaying than holding output nobody is reading - and dropping
    /// events mid-stream would leave its terminal drawing garbage from a half-applied escape.
    /// </summary>
    private const int SubscriberQueueDepth = 4096;

    /// <summary>
    /// Grace between the shell exiting and the pseudo console closing, so conhost's last bytes
    /// make it into the buffer. Without it the final line of output loses a race with Dispose.
    /// </summary>
    private static readonly TimeSpan DrainGrace = TimeSpan.FromMilliseconds(200);

    private readonly Lock _gate = new();
    private readonly StringBuilder _buffer = new();
    private readonly List<Channel<TerminalEvent>> _subscribers = [];
    private readonly Decoder _decoder = Encoding.UTF8.GetDecoder();
    private readonly IPty _pty;
    private RegisteredWaitHandle? _exitWatch;
    private bool _ended;
    private bool _disposed;

    public string TerminalId { get; }
    public string Command { get; }
    public string Cwd { get; }
    public long StartedAt { get; }
    public int Cols { get; private set; }
    public int Rows { get; private set; }

    /// <summary>The tab label, null until renamed. Cosmetic only - it never reaches the command line.</summary>
    public string? Name { get; set; }

    public bool Running { get { lock (_gate) return !_ended; } }

    public int? ExitCode { get; private set; }

    public PtySession(string terminalId, IPty pty, string command, string cwd, int cols, int rows)
    {
        TerminalId = terminalId;
        _pty = pty;
        Command = command;
        Cwd = cwd;
        Cols = cols;
        Rows = rows;
        StartedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

        // A dedicated thread rather than the pool: anonymous pipes from CreatePipe are synchronous,
        // so this read blocks for as long as the shell is quiet - which is most of the time, and
        // is exactly what a pool thread must not do.
        var reader = new Thread(ReadLoop) { IsBackground = true, Name = $"pty-{terminalId[..8]}" };
        reader.Start();

        _exitWatch = ThreadPool.RegisterWaitForSingleObject(
            _pty.Exited, (_, _) => OnProcessExited(), null, Timeout.Infinite, executeOnlyOnce: true);
    }

    public TerminalView View() => new(
        TerminalId,
        _pty.ProcessId == 0 ? null : _pty.ProcessId,
        Running,
        Command,
        Cwd,
        Cols,
        Rows,
        StartedAt,
        ExitCode,
        Name);

    /// <summary>
    /// Starts watching this terminal. The replay is taken under the same lock that registers the
    /// reader, so no output can slip between the two and be seen twice or not at all.
    /// </summary>
    /// <returns>The buffer as it stands, and the channel every later event arrives on.</returns>
    public (string Replay, bool Running, ChannelReader<TerminalEvent> Events, IDisposable Subscription) Attach()
    {
        var channel = Channel.CreateBounded<TerminalEvent>(new BoundedChannelOptions(SubscriberQueueDepth)
        {
            SingleReader = true,
            SingleWriter = false,
            FullMode = BoundedChannelFullMode.DropWrite,
        });

        string replay;
        bool running;
        lock (_gate)
        {
            replay = _buffer.ToString();
            running = !_ended;

            // A terminal that already ended has no future event to relay, so its one exit event is
            // synthesized here rather than waited for. Everything else gets the live feed.
            if (_ended) channel.Writer.TryComplete();
            else _subscribers.Add(channel);
        }

        return (replay, running, channel.Reader, new Subscription(this, channel));
    }

    public void Write(string data)
    {
        lock (_gate)
        {
            if (_ended) return;
        }
        _pty.Write(data);
    }

    public void Resize(int cols, int rows)
    {
        lock (_gate)
        {
            if (_ended) return;
            Cols = cols;
            Rows = rows;
        }
        _pty.Resize((short)cols, (short)rows);
    }

    /// <summary>Ends the shell and everything it started. Safe to call on one that already exited.</summary>
    public void Kill() => _pty.Kill();

    private void ReadLoop()
    {
        var bytes = new byte[8192];
        var chars = new char[8192 * 2];

        while (true)
        {
            int read;
            try
            {
                read = _pty.Output.Read(bytes, 0, bytes.Length);
            }
            catch (Exception ex) when (ex is IOException or ObjectDisposedException)
            {
                break; // the pseudo console closed under us - the exit path below has it covered
            }

            if (read <= 0) break;

            int decoded = _decoder.GetChars(bytes, 0, read, chars, 0);
            if (decoded == 0) continue;

            Publish(new string(chars, 0, decoded));
        }
    }

    private void Publish(string text)
    {
        lock (_gate)
        {
            _buffer.Append(text);
            if (_buffer.Length > MaxBufferChars) _buffer.Remove(0, _buffer.Length - MaxBufferChars);

            var evt = new TerminalEvent("data", text);
            foreach (var subscriber in _subscribers) subscriber.Writer.TryWrite(evt);
        }
    }

    /// <summary>
    /// The one place a terminal dies. Runs at most once: everyone attached must see exactly one
    /// exit event, because that is what ends their socket.
    /// </summary>
    private void OnProcessExited()
    {
        // Let the reader drain conhost's last bytes into the buffer before anything closes. Also
        // settles the exit code, which TerminateJobObject has not necessarily published yet when
        // this runs from Dispose rather than from the shell ending on its own.
        Thread.Sleep(DrainGrace);
        int? code = _pty.ExitCode;

        List<Channel<TerminalEvent>> watchers;
        lock (_gate)
        {
            if (_ended) return;
            _ended = true;
            ExitCode = code;
            watchers = [.. _subscribers];
            _subscribers.Clear();
        }

        var evt = new TerminalEvent("exit", ExitCode: code);
        foreach (var watcher in watchers)
        {
            watcher.Writer.TryWrite(evt);
            watcher.Writer.TryComplete();
        }

        // The shell is gone, so the pseudo console has nothing left to carry. The session itself
        // stays in the registry with its buffer, so the page can still attach and read the last
        // screen until someone closes the tab.
        _pty.Dispose();
    }

    private void Detach(Channel<TerminalEvent> channel)
    {
        lock (_gate) _subscribers.Remove(channel);
        channel.Writer.TryComplete();
    }

    public void Dispose()
    {
        bool stillRunning;
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            stillRunning = !_ended;
        }

        _exitWatch?.Unregister(null);
        _exitWatch = null;

        // Only a terminal that is still alive gets killed here. Dispose is how a running terminal
        // is closed, so marking it exited while its shell carried on would leave a tree running
        // that nothing lists - but one whose shell already ended has been through this path once,
        // and killing it again would be a second Kill on a pty that is already gone.
        if (stillRunning)
        {
            _pty.Kill();
            OnProcessExited();
        }

        _pty.Dispose();
    }

    private sealed class Subscription(PtySession session, Channel<TerminalEvent> channel) : IDisposable
    {
        public void Dispose() => session.Detach(channel);
    }
}
