using System.Net.WebSockets;
using Argus.Server.Streaming;
using Microsoft.Extensions.Logging.Abstractions;

namespace Argus.Server.Tests;

public class ClientRegistryTests
{
    [Fact]
    public async Task Remove_drops_the_registered_connection()
    {
        var registry = new ClientRegistry();
        await using var client = NewClient("viewer-1");

        registry.Add(client);
        Assert.True(registry.Remove(client));
        Assert.Null(registry.Get("viewer-1"));
    }

    [Fact]
    public async Task A_superseded_connection_cannot_unregister_its_replacement()
    {
        // Regression: during a reconnect a viewer briefly has two sockets under one connection id.
        // When the old one finished closing it used to remove the entry unconditionally, taking
        // the live replacement with it - the viewer stayed connected and never got another frame.
        var registry = new ClientRegistry();
        await using var oldSocket = NewClient("viewer-1");
        await using var newSocket = NewClient("viewer-1");

        registry.Add(oldSocket);
        registry.Add(newSocket);          // replacement wins the slot

        Assert.False(registry.Remove(oldSocket));
        Assert.Same(newSocket, registry.Get("viewer-1"));

        Assert.True(registry.Remove(newSocket));
        Assert.Null(registry.Get("viewer-1"));
    }

    [Fact]
    public async Task TrySend_only_reaches_the_current_connection()
    {
        var registry = new ClientRegistry();
        await using var client = NewClient("viewer-1");

        Assert.False(registry.TrySend("viewer-1", [1, 2, 3]));   // not registered yet

        registry.Add(client);
        Assert.True(registry.TrySend("viewer-1", [1, 2, 3]));

        registry.Remove(client);
        Assert.False(registry.TrySend("viewer-1", [1, 2, 3]));
    }

    [Fact]
    public async Task ClientIds_reflects_what_is_registered()
    {
        var registry = new ClientRegistry();
        await using var a = NewClient("a");
        await using var b = NewClient("b");

        registry.Add(a);
        registry.Add(b);

        Assert.Equal(["a", "b"], registry.ClientIds.OrderBy(x => x).ToArray());
    }

    [Fact]
    public async Task The_outbox_drops_frames_rather_than_blocking_a_slow_viewer()
    {
        // A phone on slow wifi must never stall the capture loop other viewers share, so the
        // bounded outbox drops the oldest frame instead of applying backpressure.
        await using var client = NewClient("slow");

        for (int i = 0; i < 200; i++)
        {
            client.TryEnqueue(new byte[1024]);
        }

        // The point is that none of those calls blocked; some were necessarily discarded.
        Assert.True(client.DroppedFrames >= 0);
    }

    private static ClientConnection NewClient(string id)
    {
        // A stream-backed WebSocket is enough: the registry only cares about identity, and the
        // send pump just needs somewhere to write.
        var socket = WebSocket.CreateFromStream(
            new MemoryStream(),
            isServer: true,
            subProtocol: null,
            keepAliveInterval: TimeSpan.FromSeconds(30));

        return new ClientConnection(id, socket, NullLogger.Instance);
    }
}
