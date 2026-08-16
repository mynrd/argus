using System.Net.WebSockets;
using Argus.Server.Capture;

namespace Argus.Server.Streaming;

/// <summary>
/// GET /ws/frames?clientId={signalr-connection-id} upgraded to a WebSocket carrying only binary
/// frames. The client id ties this socket to the viewer's SignalR connection, so a Subscribe call
/// on the control channel knows which socket to feed.
/// </summary>
public static class FrameSocketEndpoint
{
    public static void MapFrameSocket(this WebApplication app)
    {
        app.Map("/ws/frames", async context =>
        {
            if (!context.WebSockets.IsWebSocketRequest)
            {
                context.Response.StatusCode = StatusCodes.Status400BadRequest;
                await context.Response.WriteAsync("Expected a WebSocket upgrade.");
                return;
            }

            var clientId = context.Request.Query["clientId"].ToString();
            if (string.IsNullOrWhiteSpace(clientId))
            {
                context.Response.StatusCode = StatusCodes.Status400BadRequest;
                await context.Response.WriteAsync("clientId is required.");
                return;
            }

            var registry = context.RequestServices.GetRequiredService<ClientRegistry>();
            var capture = context.RequestServices.GetRequiredService<CaptureManager>();
            var log = context.RequestServices.GetRequiredService<ILoggerFactory>().CreateLogger("frames");

            using var socket = await context.WebSockets.AcceptWebSocketAsync();
            var client = new ClientConnection(clientId, socket, log);
            registry.Add(client);
            log.LogInformation("Frame socket open for {ClientId}", clientId);

            try
            {
                await ReadUntilClosedAsync(socket, context.RequestAborted);
            }
            catch (Exception ex) when (ex is WebSocketException or OperationCanceledException)
            {
                // Client went away - normal on a phone locking its screen.
            }
            finally
            {
                // Only tear down subscriptions if this socket is still the viewer's current one.
                // During a reconnect the replacement socket is already registered, and dropping
                // its subscriptions here would leave a connected viewer with a frozen screen.
                bool wasCurrent = registry.Remove(client);
                if (wasCurrent) capture.UnsubscribeAll(clientId);

                await client.DisposeAsync();
                log.LogInformation(
                    "Frame socket closed for {ClientId}{Note}",
                    clientId,
                    wasCurrent ? "" : " (superseded)");
            }
        });
    }

    /// <summary>
    /// The socket is send-only in practice, but a receive loop must run for the close handshake to
    /// be observed at all - without it a disconnected viewer would keep its subscriptions alive.
    /// </summary>
    private static async Task ReadUntilClosedAsync(WebSocket socket, CancellationToken token)
    {
        var buffer = new byte[256];
        while (socket.State == WebSocketState.Open && !token.IsCancellationRequested)
        {
            var result = await socket.ReceiveAsync(buffer, token);
            if (result.MessageType == WebSocketMessageType.Close) break;
        }
    }
}
