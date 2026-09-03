using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using Argus.Server.Api;
using Argus.Server.Services;

namespace Argus.Server.Streaming;

/// <summary>
/// GET /ws/terminal/{terminalId} upgraded to a WebSocket carrying one terminal both ways.
///
/// One duplex socket rather than a download stream plus a POST per keystroke: the ordering falls
/// out for free (in a shell, "ls" arriving as "sl" matters), and it is the same shape the frame
/// socket next door already uses. It rides the session cookie through the upgrade, so the gate in
/// front of /ws applies here with no client plumbing.
///
/// The frames are small JSON objects, short-keyed because output frames are frequent:
///   down  {"t":"b","d":…} the replay buffer   {"t":"d","d":…} live output
///         {"t":"x","c":…} the shell exited    {"t":"e","m":…} this socket could not be set up
///   up    {"t":"i","d":…} keystrokes          {"t":"r","c":…,"r":…} the terminal was resized
///
/// The replay is its own type so the page can reset xterm before writing it - reattaching after a
/// reconnect would otherwise paint the buffer on top of what is already on screen.
/// </summary>
public static class TerminalSocketEndpoint
{
    /// <summary>A frame from the page. Keystrokes are small; a paste is the only thing near this.</summary>
    private const int MaxIncomingFrameBytes = 64 * 1024;

    private static readonly JsonSerializerOptions Json = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    public static void MapTerminalSocket(this WebApplication app)
    {
        app.Map("/ws/terminal/{terminalId}", async (HttpContext context, string terminalId) =>
        {
            if (!context.WebSockets.IsWebSocketRequest)
            {
                context.Response.StatusCode = StatusCodes.Status400BadRequest;
                await context.Response.WriteAsync("Expected a WebSocket upgrade.");
                return;
            }

            if (!TerminalEndpoints.IsTerminalId(terminalId))
            {
                context.Response.StatusCode = StatusCodes.Status400BadRequest;
                await context.Response.WriteAsync("Malformed terminal id.");
                return;
            }

            var host = context.RequestServices.GetRequiredService<TerminalHostClient>();
            var log = context.RequestServices.GetRequiredService<ILoggerFactory>().CreateLogger("terminal");

            using var socket = await context.WebSockets.AcceptWebSocketAsync();

            var (outcome, attachment) = await host.AttachAsync(terminalId, context.RequestAborted);
            if (attachment is null)
            {
                // The upgrade already happened, so the reason has to travel as a frame rather than
                // as a status code - the page shows it on the terminal's status line.
                await SendAsync(socket, new { t = "e", m = outcome.Error ?? "Could not attach." }, CancellationToken.None);
                await CloseAsync(socket);
                return;
            }

            await using (attachment)
            {
                using var stop = CancellationTokenSource.CreateLinkedTokenSource(context.RequestAborted);
                var fromPage = Task.Run(() => PumpInputAsync(socket, attachment, stop), CancellationToken.None);

                try
                {
                    await foreach (var evt in attachment.ReadAsync(stop.Token))
                    {
                        object frame = evt.Type switch
                        {
                            "replay" => new { t = "b", d = evt.Data ?? string.Empty },
                            "data" => new { t = "d", d = evt.Data ?? string.Empty },
                            _ => new { t = "x", c = evt.ExitCode },
                        };

                        await SendAsync(socket, frame, stop.Token);
                    }
                }
                catch (Exception ex) when (ex is OperationCanceledException or WebSocketException)
                {
                    // The viewer went away - normal on a phone locking its screen. The terminal
                    // itself carries on running in the daemon, which is the whole point.
                }
                catch (Exception ex)
                {
                    log.LogWarning(ex, "Terminal socket for {TerminalId} failed", terminalId);
                }
                finally
                {
                    await stop.CancelAsync();
                    await fromPage.ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
                    await CloseAsync(socket);
                }
            }
        });
    }

    /// <summary>
    /// Keystrokes and resizes, on to the daemon. Ends when the page closes the socket, which is
    /// also what tells the output pump above to stop.
    /// </summary>
    private static async Task PumpInputAsync(WebSocket socket, TerminalAttachment attachment, CancellationTokenSource stop)
    {
        var buffer = new byte[8192];
        var message = new MemoryStream();

        try
        {
            while (socket.State == WebSocketState.Open && !stop.IsCancellationRequested)
            {
                var result = await socket.ReceiveAsync(buffer, stop.Token);
                if (result.MessageType == WebSocketMessageType.Close) return;

                message.Write(buffer, 0, result.Count);
                if (message.Length > MaxIncomingFrameBytes) return; // nothing legitimate is this big

                if (!result.EndOfMessage) continue;

                string text = Encoding.UTF8.GetString(message.GetBuffer(), 0, (int)message.Length);
                message.SetLength(0);
                await HandleAsync(text, attachment, stop.Token);
            }
        }
        catch (Exception ex) when (ex is OperationCanceledException or WebSocketException or IOException)
        {
            // The page hung up mid-frame. Nothing to report to.
        }
        finally
        {
            await stop.CancelAsync();
        }
    }

    private static async Task HandleAsync(string text, TerminalAttachment attachment, CancellationToken cancel)
    {
        try
        {
            using var document = JsonDocument.Parse(text);
            var root = document.RootElement;
            if (!root.TryGetProperty("t", out var type)) return;

            switch (type.GetString())
            {
                case "i" when root.TryGetProperty("d", out var data) && data.GetString() is { Length: > 0 } keystrokes:
                    await attachment.SendInputAsync(keystrokes, cancel);
                    return;

                case "r" when root.TryGetProperty("c", out var cols) && root.TryGetProperty("r", out var rows):
                    if (cols.TryGetInt32(out int c) && rows.TryGetInt32(out int r))
                    {
                        await attachment.SendResizeAsync(c, r, cancel);
                    }
                    return;
            }
        }
        catch (JsonException)
        {
            // A frame this build cannot read is dropped rather than ending the terminal.
        }
    }

    private static Task SendAsync(WebSocket socket, object frame, CancellationToken cancel) =>
        socket.State != WebSocketState.Open
            ? Task.CompletedTask
            : socket.SendAsync(
                JsonSerializer.SerializeToUtf8Bytes(frame, Json),
                WebSocketMessageType.Text,
                endOfMessage: true,
                cancel);

    private static async Task CloseAsync(WebSocket socket)
    {
        if (socket.State is not (WebSocketState.Open or WebSocketState.CloseReceived)) return;

        try
        {
            await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "Terminal ended", CancellationToken.None);
        }
        catch (Exception ex) when (ex is WebSocketException or OperationCanceledException)
        {
            // Already gone.
        }
    }
}
