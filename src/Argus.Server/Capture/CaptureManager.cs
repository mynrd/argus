using System.Collections.Concurrent;
using Argus.Server.Streaming;
using Argus.Server.Windows;

namespace Argus.Server.Capture;

/// <summary>
/// Owns the set of attached windows and their capture sessions, and routes frames to the frame
/// sockets. Sessions live as long as a window is attached, independently of whether anyone is
/// currently watching, so opening a tile is instant rather than paying capture start-up latency.
/// </summary>
public sealed class CaptureManager : IDisposable
{
    private readonly ConcurrentDictionary<long, CaptureSession> _sessions = new();
    private readonly ClientRegistry _clients;
    private readonly ILogger<CaptureManager> _log;
    private readonly ILoggerFactory _loggerFactory;

    /// <summary>Raised whenever a window's status changes, so the hub can push it to the dashboard.</summary>
    public event Action<WindowStatusUpdate>? StatusChanged;

    public CaptureManager(ClientRegistry clients, ILogger<CaptureManager> log, ILoggerFactory loggerFactory)
    {
        _clients = clients;
        _log = log;
        _loggerFactory = loggerFactory;
    }

    public IReadOnlyCollection<CaptureSession> Sessions => _sessions.Values.ToArray();

    public CaptureSession? Find(long handle) => _sessions.TryGetValue(handle, out var s) ? s : null;

    public CaptureSession? FindByMatchKey(string matchKey) =>
        _sessions.Values.FirstOrDefault(s =>
            string.Equals(s.MatchKey, matchKey, StringComparison.OrdinalIgnoreCase));

    public CaptureSession Attach(WindowInfo info)
    {
        return _sessions.GetOrAdd(info.Handle, _ =>
        {
            _log.LogInformation("Attaching {Process} '{Title}' (HWND {Hwnd})",
                info.ProcessName, info.Title, info.Handle);

            return new CaptureSession(
                info,
                _loggerFactory.CreateLogger($"capture:{info.ProcessName}"),
                SendFrame,
                session => StatusChanged?.Invoke(session.Snapshot()));
        });
    }

    public void Detach(long handle)
    {
        if (!_sessions.TryRemove(handle, out var session)) return;

        _log.LogInformation("Detaching HWND {Hwnd}", handle);
        var snapshot = session.Snapshot() with { Status = WindowStatus.Closed, Note = "Detached." };
        session.Dispose();
        StatusChanged?.Invoke(snapshot);
    }

    public bool Subscribe(string clientId, long handle, QualityLevel quality)
    {
        var session = Find(handle);
        if (session is null) return false;

        session.Subscribe(clientId, quality);
        return true;
    }

    public void Unsubscribe(string clientId, long handle) => Find(handle)?.Unsubscribe(clientId);

    /// <summary>Called when a viewer disconnects - drops that client from every session at once.</summary>
    public void UnsubscribeAll(string clientId)
    {
        foreach (var session in _sessions.Values) session.Unsubscribe(clientId);
    }

    /// <summary>
    /// Replaces the handle a session captures from, keyed by the old handle. Used by the watchdog
    /// when an app is closed and reopened with a new HWND.
    /// </summary>
    public void Reattach(long oldHandle, WindowInfo replacement)
    {
        if (!_sessions.TryRemove(oldHandle, out var session)) return;

        session.Reattach(replacement);
        _sessions[replacement.Handle] = session;
        StatusChanged?.Invoke(session.Snapshot());
    }

    public void RaiseStatus(CaptureSession session) => StatusChanged?.Invoke(session.Snapshot());

    private void SendFrame(long handle, byte[] payload, string clientId)
    {
        // A dropped frame is expected under load and is not worth logging per occurrence -
        // ClientConnection counts them and the dashboard surfaces the count.
        _clients.TrySend(clientId, payload);
    }

    public void Dispose()
    {
        foreach (var handle in _sessions.Keys.ToArray())
        {
            if (_sessions.TryRemove(handle, out var session)) session.Dispose();
        }
    }
}
