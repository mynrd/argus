using System.Collections.Concurrent;

namespace Argus.Server.Streaming;

/// <summary>
/// Tracks live frame sockets by client id. The id is the browser's SignalR connection id, which is
/// how a control-channel subscription and a binary frame socket are tied to the same viewer.
/// </summary>
public sealed class ClientRegistry
{
    private readonly ConcurrentDictionary<string, ClientConnection> _clients = new(StringComparer.Ordinal);

    public void Add(ClientConnection client) => _clients[client.Id] = client;

    /// <summary>
    /// Removes a connection only if it is still the registered one.
    ///
    /// A viewer can briefly have two sockets - a reconnect opens the new one before the old one
    /// finishes closing. An unconditional remove would let the dying socket's cleanup unregister
    /// its live replacement, and the viewer would stay connected but never receive another frame.
    /// </summary>
    public bool Remove(ClientConnection client) =>
        ((System.Collections.Generic.ICollection<KeyValuePair<string, ClientConnection>>)_clients)
            .Remove(new KeyValuePair<string, ClientConnection>(client.Id, client));

    public ClientConnection? Get(string clientId) =>
        _clients.TryGetValue(clientId, out var client) ? client : null;

    public IReadOnlyCollection<string> ClientIds => _clients.Keys.ToArray();

    public bool TrySend(string clientId, byte[] payload) =>
        _clients.TryGetValue(clientId, out var client) && client.TryEnqueue(payload);
}
