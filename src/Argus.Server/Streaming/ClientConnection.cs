using System.Net.WebSockets;
using System.Threading.Channels;

namespace Argus.Server.Streaming;

/// <summary>
/// One browser's binary frame socket.
///
/// The outbox is a small bounded channel that drops the oldest frame when full. That is the whole
/// backpressure story and it is deliberate: a phone on slow wifi must never be able to stall a
/// capture loop that other viewers share. A late frame has no value in a live view - dropping it
/// and sending the newest one is always the better trade.
/// </summary>
public sealed class ClientConnection : IAsyncDisposable
{
    private const int OutboxCapacity = 3;

    private readonly WebSocket _socket;
    private readonly Channel<byte[]> _outbox;
    private readonly CancellationTokenSource _cts = new();
    private readonly ILogger _log;
    private readonly Task _pump;

    private long _dropped;
    private long _sent;

    public ClientConnection(string id, WebSocket socket, ILogger log)
    {
        Id = id;
        _socket = socket;
        _log = log;
        _outbox = Channel.CreateBounded<byte[]>(new BoundedChannelOptions(OutboxCapacity)
        {
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleReader = true,
            SingleWriter = false,
        });
        _pump = Task.Run(PumpAsync);
    }

    public string Id { get; }
    public long DroppedFrames => Interlocked.Read(ref _dropped);
    public long SentFrames => Interlocked.Read(ref _sent);
    public Task Completion => _pump;

    /// <summary>Never blocks. Returns false once the socket is finished.</summary>
    public bool TryEnqueue(byte[] payload)
    {
        if (_cts.IsCancellationRequested) return false;

        if (!_outbox.Writer.TryWrite(payload))
        {
            Interlocked.Increment(ref _dropped);
            return false;
        }
        return true;
    }

    private async Task PumpAsync()
    {
        try
        {
            await foreach (var payload in _outbox.Reader.ReadAllAsync(_cts.Token).ConfigureAwait(false))
            {
                if (_socket.State != WebSocketState.Open) break;

                await _socket.SendAsync(payload, WebSocketMessageType.Binary, endOfMessage: true, _cts.Token)
                             .ConfigureAwait(false);
                Interlocked.Increment(ref _sent);
            }
        }
        catch (OperationCanceledException)
        {
            // Normal shutdown.
        }
        catch (WebSocketException ex)
        {
            _log.LogDebug(ex, "Frame socket {Id} closed by peer", Id);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Frame socket {Id} pump failed", Id);
        }
        finally
        {
            _outbox.Writer.TryComplete();
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _cts.CancelAsync().ConfigureAwait(false);
        _outbox.Writer.TryComplete();

        try
        {
            await _pump.ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is OperationCanceledException or WebSocketException)
        {
            // Expected while tearing down.
        }

        if (_socket.State is WebSocketState.Open or WebSocketState.CloseReceived)
        {
            try
            {
                await _socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "bye", CancellationToken.None)
                             .ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is WebSocketException or OperationCanceledException or ObjectDisposedException)
            {
                // Peer already gone.
            }
        }

        _cts.Dispose();
        _socket.Dispose();
    }
}
