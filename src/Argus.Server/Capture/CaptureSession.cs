using System.Collections.Concurrent;
using System.Diagnostics;
using System.Drawing;
using Argus.Server.Interop;
using Argus.Server.Windows;

namespace Argus.Server.Capture;

/// <summary>
/// One attached window's capture loop. Runs on its own dedicated thread because GDI capture is a
/// blocking call and a stalled app must not consume a thread-pool slot shared with request handling.
///
/// Frames are captured once per tick at the highest rate any subscriber asked for, then encoded
/// once per distinct quality level in use, then fanned out. A dashboard tile and a full-screen view
/// of the same window therefore cost one capture and two encodes, not two of each.
/// </summary>
public sealed class CaptureSession : IDisposable
{
    private sealed class Subscriber
    {
        public required string ClientId { get; init; }
        public QualityLevel Quality { get; set; }
        public long LastSentTimestamp { get; set; }
    }

    /// <summary>Consecutive failed captures before the window is treated as stalled.</summary>
    private const int StallThreshold = 5;

    /// <summary>How long the loop idles when nothing is subscribed.</summary>
    private static readonly TimeSpan IdlePoll = TimeSpan.FromMilliseconds(250);

    /// <summary>Averaging window for the reported frame rate.</summary>
    private static readonly TimeSpan FpsWindow = TimeSpan.FromSeconds(5);

    private readonly ConcurrentDictionary<string, Subscriber> _subscribers = new(StringComparer.Ordinal);
    private readonly CancellationTokenSource _cts = new();
    private readonly Thread _thread;
    private readonly ILogger _log;
    private readonly Action<long, byte[], string> _send;
    private readonly Action<CaptureSession> _onStatusChanged;

    private IWindowCaptureSource _source;
    private WindowInfo _info;
    private int _sequence;
    private int _consecutiveFailures;
    private WindowStatus _status = WindowStatus.Starting;
    private string? _note;

    // Rolling frame-rate measurement, for the "is it actually meeting the requirement" check.
    private readonly Queue<long> _recentFrameTicks = new();
    private readonly Lock _fpsLock = new();

    public CaptureSession(
        WindowInfo info,
        ILogger log,
        Action<long, byte[], string> send,
        Action<CaptureSession> onStatusChanged)
    {
        _info = info;
        _log = log;
        _send = send;
        _onStatusChanged = onStatusChanged;
        _source = new GdiWindowCaptureSource(info.Handle, log);

        _thread = new Thread(Loop)
        {
            IsBackground = true,
            Name = $"argus-capture-{info.Handle}",
        };
        _thread.Start();
    }

    public long Handle => _info.Handle;
    public WindowInfo Info => _info;
    public WindowStatus Status => _status;
    public int SubscriberCount => _subscribers.Count;
    public string MatchKey => _info.MatchKey;

    /// <summary>
    /// Frames per second over the last <see cref="FpsWindow"/> only.
    ///
    /// The window matters: a session with no subscribers stops capturing, so an average taken over
    /// "the last 30 frames" would stretch across that idle gap and report a fraction of a frame per
    /// second the moment someone opened a tile again.
    /// </summary>
    public double ActualFps
    {
        get
        {
            lock (_fpsLock)
            {
                long cutoff = Stopwatch.GetTimestamp() - (long)(FpsWindow.TotalSeconds * Stopwatch.Frequency);
                long first = 0, last = 0;
                int count = 0;

                foreach (long tick in _recentFrameTicks)
                {
                    if (tick < cutoff) continue;
                    if (count == 0) first = tick;
                    last = tick;
                    count++;
                }

                if (count < 2) return 0;

                long span = last - first;
                if (span <= 0) return 0;
                return (count - 1) / ((double)span / Stopwatch.Frequency);
            }
        }
    }

    public WindowStatusUpdate Snapshot() => new()
    {
        WindowId = _info.Id,
        Handle = _info.Handle,
        Title = _info.Title,
        ProcessName = _info.ProcessName,
        Status = _status,
        Backend = _source.BackendName,
        ActualFps = Math.Round(ActualFps, 1),
        Subscribers = _subscribers.Count,
        SupportsBackgroundInput = _info.SupportsBackgroundInput,
        Note = _note,
    };

    public void Subscribe(string clientId, QualityLevel quality)
    {
        _subscribers.AddOrUpdate(
            clientId,
            _ => new Subscriber { ClientId = clientId, Quality = quality },
            (_, existing) =>
            {
                existing.Quality = quality;
                return existing;
            });
        _onStatusChanged(this);
    }

    public bool Unsubscribe(string clientId)
    {
        bool removed = _subscribers.TryRemove(clientId, out _);
        if (removed) _onStatusChanged(this);
        return removed;
    }

    /// <summary>
    /// Points the session at a new HWND after the target app was closed and reopened. The session,
    /// its subscribers and its window id all survive, so tiles do not flicker out of the dashboard.
    /// </summary>
    public void Reattach(WindowInfo info)
    {
        _log.LogInformation("Re-attaching {Title} from HWND {Old} to {New}", info.Title, _info.Handle, info.Handle);

        var replacement = new GdiWindowCaptureSource(info.Handle, _log);
        var old = Interlocked.Exchange(ref _source, replacement);
        old.Dispose();

        _info = info;
        _consecutiveFailures = 0;
        SetStatus(WindowStatus.Starting, note: null);
    }

    /// <summary>Refreshes title/size without touching the capture source.</summary>
    public void UpdateInfo(WindowInfo info)
    {
        if (info.Handle != _info.Handle) return;
        if (info.Title == _info.Title && info.Width == _info.Width && info.Height == _info.Height) return;

        _info = info;
        _onStatusChanged(this);
    }

    private void Loop()
    {
        var token = _cts.Token;
        var stopwatch = new Stopwatch();

        while (!token.IsCancellationRequested)
        {
            stopwatch.Restart();

            var subscribers = _subscribers.Values.ToArray();
            if (subscribers.Length == 0)
            {
                Sleep(IdlePoll, token);
                continue;
            }

            int targetFps = subscribers.Max(s => QualityPreset.For(s.Quality).Fps);
            var tickInterval = TimeSpan.FromMilliseconds(1000.0 / targetFps);

            try
            {
                Tick(subscribers);
            }
            catch (Exception ex)
            {
                _log.LogError(ex, "Capture tick failed for {Title}", _info.Title);
                RecordFailure();
            }

            var elapsed = stopwatch.Elapsed;
            if (elapsed < tickInterval) Sleep(tickInterval - elapsed, token);
        }
    }

    private void Tick(Subscriber[] subscribers)
    {
        using var bitmap = _source.TryCapture();
        if (bitmap is null)
        {
            RecordFailure();
            return;
        }

        _consecutiveFailures = 0;
        RecordFrameTime();
        if (_status != WindowStatus.Streaming)
        {
            SetStatus(WindowStatus.Streaming, NoteForSource());
        }

        long now = Stopwatch.GetTimestamp();
        int sequence = Interlocked.Increment(ref _sequence);

        // One encode per distinct quality actually due this tick, shared by every subscriber on it.
        Dictionary<QualityLevel, byte[]>? encoded = null;

        foreach (var subscriber in subscribers)
        {
            var preset = QualityPreset.For(subscriber.Quality);
            long dueAfter = subscriber.LastSentTimestamp + (long)(preset.FrameInterval.TotalSeconds * Stopwatch.Frequency);
            if (subscriber.LastSentTimestamp != 0 && now < dueAfter) continue;

            encoded ??= [];
            if (!encoded.TryGetValue(subscriber.Quality, out var payload))
            {
                var frame = JpegEncoder.Encode(bitmap, preset);
                payload = Streaming.FrameProtocol.Encode(_info.Handle, sequence, frame);
                encoded[subscriber.Quality] = payload;
            }

            _send(_info.Handle, payload, subscriber.ClientId);
            subscriber.LastSentTimestamp = now;
        }
    }

    private string? NoteForSource() =>
        _source is GdiWindowCaptureSource { ProducesBlankFrames: true }
            ? "Capture returns a blank image for this window."
            : null;

    private void RecordFailure()
    {
        _consecutiveFailures++;

        if (!NativeMethods.IsWindow((nint)_info.Handle))
        {
            SetStatus(WindowStatus.Closed, "The app is no longer running.");
            return;
        }

        if (NativeMethods.IsIconic((nint)_info.Handle))
        {
            SetStatus(WindowStatus.Minimised, "Minimised - restore the window to resume the stream.");
            return;
        }

        if (_consecutiveFailures >= StallThreshold && _status != WindowStatus.Stalled)
        {
            SetStatus(WindowStatus.Stalled, "Capture is not producing frames.");
        }
    }

    private void SetStatus(WindowStatus status, string? note)
    {
        if (_status == status && _note == note) return;
        _status = status;
        _note = note;
        _onStatusChanged(this);
    }

    private void RecordFrameTime()
    {
        lock (_fpsLock)
        {
            _recentFrameTicks.Enqueue(Stopwatch.GetTimestamp());
            while (_recentFrameTicks.Count > 30) _recentFrameTicks.Dequeue();
        }
    }

    /// <summary>True when the loop has produced nothing for longer than the given window.</summary>
    public bool IsSilentFor(TimeSpan threshold)
    {
        lock (_fpsLock)
        {
            if (_recentFrameTicks.Count == 0) return false;
            long since = Stopwatch.GetTimestamp() - _recentFrameTicks.Last();
            return (double)since / Stopwatch.Frequency > threshold.TotalSeconds;
        }
    }

    /// <summary>
    /// Declares the window gone.
    ///
    /// The capture loop can only notice this while it is actually capturing, and it stops entirely
    /// when nothing is subscribed - so an app closed while nobody was watching it would otherwise
    /// sit on its last status forever. The watchdog polls window liveness independently and calls
    /// this, which is why detection does not depend on anyone having a tile open.
    /// </summary>
    public void MarkClosed(string note) => SetStatus(WindowStatus.Closed, note);

    /// <summary>Drops and rebuilds the capture source after a stall.</summary>
    public void RestartSource()
    {
        _log.LogWarning("Restarting capture source for {Title}", _info.Title);
        var replacement = new GdiWindowCaptureSource(_info.Handle, _log);
        var old = Interlocked.Exchange(ref _source, replacement);
        old.Dispose();
        _consecutiveFailures = 0;
    }

    private static void Sleep(TimeSpan duration, CancellationToken token)
    {
        if (duration <= TimeSpan.Zero) return;
        token.WaitHandle.WaitOne(duration);
    }

    public void Dispose()
    {
        _cts.Cancel();
        _thread.Join(TimeSpan.FromSeconds(2));
        _source.Dispose();
        _cts.Dispose();
    }
}
