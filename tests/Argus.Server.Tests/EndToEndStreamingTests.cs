using System.Buffers.Binary;
using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Threading.Channels;
using Argus.Server.Capture;
using Argus.Server.Windows;
using Microsoft.AspNetCore.SignalR.Client;

namespace Argus.Server.Tests;

/// <summary>
/// Runs the real server executable and drives it exactly as the browser does: SignalR for control,
/// a raw WebSocket for frames. This is the test that answers "do frames actually arrive", which no
/// amount of unit testing of the pieces can.
/// </summary>
[Trait("Category", "EndToEnd")]
public sealed class EndToEndStreamingTests : IAsyncLifetime
{
    /// <summary>
    /// The server under test is locked, like a real one. Every connection here therefore carries a
    /// session cookie, which is also what proves the gate lets the browser's own traffic through.
    /// </summary>
    private const string Password = "end-to-end-password";

    private Process? _server;
    private string _baseUrl = "";
    private readonly CookieContainer _cookies = new();
    private readonly HttpClient _http;

    public EndToEndStreamingTests()
    {
        _http = new HttpClient(new HttpClientHandler { CookieContainer = _cookies })
        {
            Timeout = TimeSpan.FromSeconds(10),
        };
    }

    public async Task InitializeAsync()
    {
        int port = FreeTcpPort();
        _baseUrl = $"http://127.0.0.1:{port}";

        var serverDll = LocateServerAssembly();
        var startInfo = new ProcessStartInfo("dotnet")
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            WorkingDirectory = Path.GetDirectoryName(serverDll)!,
        };
        startInfo.ArgumentList.Add(serverDll);
        startInfo.Environment["ARGUS_URLS"] = _baseUrl;
        startInfo.Environment["ASPNETCORE_ENVIRONMENT"] = "Development";
        // Overrides whatever appsettings.json ships, so the test does not depend on that value.
        startInfo.Environment["ARGUS_PASSWORD"] = Password;
        // Keep the test run from stomping on the developer's real saved selection.
        startInfo.Environment["Argus__SelectionPath"] =
            Path.Combine(Path.GetTempPath(), $"argus-test-selection-{Guid.NewGuid():N}.json");

        _server = Process.Start(startInfo);
        Assert.NotNull(_server);

        _ = _server!.StandardOutput.ReadToEndAsync();
        _ = _server.StandardError.ReadToEndAsync();

        await WaitForServerAsync();
        await UnlockAsync();
    }

    public Task DisposeAsync()
    {
        _http.Dispose();
        try
        {
            if (_server is { HasExited: false }) _server.Kill(entireProcessTree: true);
        }
        catch (InvalidOperationException)
        {
            // Already gone.
        }
        _server?.Dispose();
        return Task.CompletedTask;
    }

    [Fact]
    public async Task Health_endpoint_reports_the_server_is_up()
    {
        var health = await _http.GetFromJsonAsync<HealthResponse>($"{_baseUrl}/api/health");

        Assert.NotNull(health);
        Assert.True(health!.Ok);
    }

    [Fact]
    public async Task A_client_without_a_session_is_refused_the_host()
    {
        // A fresh client, so it carries none of the cookies InitializeAsync collected.
        using var stranger = new HttpClient();

        var api = await stranger.GetAsync($"{_baseUrl}/api/windows");
        Assert.Equal(HttpStatusCode.Unauthorized, api.StatusCode);

        var negotiate = await stranger.PostAsync($"{_baseUrl}/hubs/argus/negotiate?negotiateVersion=1", null);
        Assert.Equal(HttpStatusCode.Unauthorized, negotiate.StatusCode);

        using var socket = new ClientWebSocket();
        await Assert.ThrowsAsync<WebSocketException>(() => socket.ConnectAsync(
            new Uri($"{_baseUrl.Replace("http://", "ws://")}/ws/frames?clientId=stranger"),
            CancellationToken.None));

        // The lock's own endpoint has to keep answering, or there would be no way to unlock.
        var session = await stranger.GetAsync($"{_baseUrl}/api/session");
        Assert.Equal(HttpStatusCode.OK, session.StatusCode);
    }

    [Fact]
    public async Task Attaching_a_window_streams_frames_to_a_subscribed_client()
    {
        var target = PickTargetWindow();
        if (target is null) return;   // headless machine - nothing to stream

        await using var hub = BuildHub();

        await hub.StartAsync();
        Assert.NotNull(hub.ConnectionId);

        using var socket = await ConnectFrameSocketAsync(hub.ConnectionId);

        await using var reader = new FrameReader(socket);

        var attached = await hub.InvokeAsync<WindowStatusUpdate?>("Attach", target.Handle);
        Assert.NotNull(attached);

        // High quality so the capture loop runs fast enough for the test to finish quickly.
        Assert.True(await hub.InvokeAsync<bool>("Subscribe", target.Handle, QualityLevel.High));

        var frames = await reader.TakeAsync(3, TimeSpan.FromSeconds(15));

        Assert.True(frames.Count >= 3, $"Only {frames.Count} frames arrived");
        Assert.All(frames, frame =>
        {
            Assert.Equal(target.Handle, frame.Handle);
            Assert.True(frame.Width > 0);
            Assert.True(frame.Height > 0);
            Assert.True(frame.JpegLength > 0);
            Assert.Equal(0xFF, frame.FirstByte);      // JPEG SOI
            Assert.Equal(0xD8, frame.SecondByte);
        });

        // Sequence numbers must advance, otherwise the same frame is being resent.
        Assert.True(frames[^1].Sequence > frames[0].Sequence);

        await hub.InvokeAsync("Detach", target.Handle);
    }

    [Fact]
    public async Task Changing_quality_changes_the_frame_size()
    {
        var target = PickTargetWindow();
        if (target is null) return;

        await using var hub = BuildHub();
        await hub.StartAsync();

        using var socket = await ConnectFrameSocketAsync(hub.ConnectionId);

        await using var reader = new FrameReader(socket);
        await hub.InvokeAsync<WindowStatusUpdate?>("Attach", target.Handle);

        await hub.InvokeAsync<bool>("Subscribe", target.Handle, QualityLevel.High);
        var highFrames = await reader.TakeAsync(2, TimeSpan.FromSeconds(15));
        Assert.NotEmpty(highFrames);

        await hub.InvokeAsync<bool>("Subscribe", target.Handle, QualityLevel.Preview);
        // Frames already in flight are still High; drain them before judging the new size.
        await reader.DrainAsync(TimeSpan.FromMilliseconds(600));
        var previewFrames = await reader.TakeAsync(2, TimeSpan.FromSeconds(15));
        Assert.NotEmpty(previewFrames);

        Assert.True(previewFrames[^1].Width <= QualityPreset.Preview.MaxWidth,
            $"Preview frame was {previewFrames[^1].Width}px wide");
        Assert.True(previewFrames[^1].Width < highFrames[^1].Width,
            "Switching to Preview did not shrink the stream");

        await hub.InvokeAsync("Detach", target.Handle);
    }

    [Fact]
    public async Task Detaching_stops_the_stream()
    {
        var target = PickTargetWindow();
        if (target is null) return;

        await using var hub = BuildHub();
        await hub.StartAsync();

        using var socket = await ConnectFrameSocketAsync(hub.ConnectionId);

        await using var reader = new FrameReader(socket);
        await hub.InvokeAsync<WindowStatusUpdate?>("Attach", target.Handle);
        await hub.InvokeAsync<bool>("Subscribe", target.Handle, QualityLevel.High);

        var before = await reader.TakeAsync(1, TimeSpan.FromSeconds(15));
        Assert.NotEmpty(before);

        await hub.InvokeAsync("Detach", target.Handle);

        // Drain whatever was already queued, then confirm the stream has gone quiet.
        await reader.DrainAsync(TimeSpan.FromMilliseconds(700));
        var afterDetach = await reader.NextAsync(TimeSpan.FromSeconds(2));

        Assert.Null(afterDetach);
    }

    [Fact]
    public async Task Sending_a_key_to_an_unattached_window_is_reported_as_undelivered()
    {
        await using var hub = BuildHub();
        await hub.StartAsync();

        var result = await hub.InvokeAsync<SendKeyResponse>(
            "SendKey",
            new
            {
                windowId = "999999999",
                type = 0,
                code = "KeyA",
                key = "a",
                ctrl = false,
                shift = false,
                alt = false,
                meta = false,
            },
            0);

        Assert.False(result.Delivered);
        Assert.False(string.IsNullOrWhiteSpace(result.Reason));
    }

    [Fact]
    public async Task Closing_an_attached_app_is_reported_to_the_dashboard()
    {
        // Character Map, deliberately: it holds no user data, always starts its own instance, and
        // has nothing to lose when killed. Notepad would be the obvious choice and is the wrong
        // one - Windows 11 tabs new Notepad launches into an existing instance, so killing "our"
        // process could take a developer's unsaved document with it.
        var charmapPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.System), "charmap.exe");
        if (!File.Exists(charmapPath)) return;

        using var app = Process.Start(new ProcessStartInfo(charmapPath) { UseShellExecute = true });
        Assert.NotNull(app);

        try
        {
            var target = await WaitForWindowAsync(app!.Id, TimeSpan.FromSeconds(20));
            if (target is null) return;   // never showed a window on this machine

            await using var hub = BuildHub();

            var closed = new TaskCompletionSource<WindowStatusUpdate>(
                TaskCreationOptions.RunContinuationsAsynchronously);

            hub.On<WindowStatusUpdate>("WindowStatus", update =>
            {
                if (update.Handle == target.Handle && update.Status == WindowStatus.Closed)
                {
                    closed.TrySetResult(update);
                }
            });

            await hub.StartAsync();

            var attached = await hub.InvokeAsync<WindowStatusUpdate?>("Attach", target.Handle);
            Assert.NotNull(attached);
            Assert.NotEqual(WindowStatus.Closed, attached!.Status);

            app.Kill(entireProcessTree: true);

            var completed = await Task.WhenAny(closed.Task, Task.Delay(TimeSpan.FromSeconds(20)));
            Assert.True(ReferenceEquals(completed, closed.Task),
                "The watchdog never reported the closed window to connected viewers.");

            var update = await closed.Task;
            Assert.Equal(WindowStatus.Closed, update.Status);
            Assert.False(string.IsNullOrWhiteSpace(update.Note));

            await hub.InvokeAsync("Detach", target.Handle);
        }
        finally
        {
            try
            {
                if (!app!.HasExited) app.Kill(entireProcessTree: true);
            }
            catch (InvalidOperationException) { }
        }
    }

    private static async Task<WindowInfo?> WaitForWindowAsync(int processId, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            var window = WindowEnumerator.Enumerate().FirstOrDefault(w => w.ProcessId == processId);
            if (window is not null) return window;
            await Task.Delay(300);
        }
        return null;
    }

    [Fact]
    public async Task Window_list_is_returned_over_the_hub()
    {
        await using var hub = BuildHub();
        await hub.StartAsync();

        var windows = await hub.InvokeAsync<List<HubWindow>>("ListWindows");

        Assert.NotNull(windows);
        foreach (var window in windows)
        {
            Assert.False(string.IsNullOrWhiteSpace(window.Title));
            Assert.False(string.IsNullOrWhiteSpace(window.MatchKey));
        }
    }

    // ------------------------------------------------------------------ helpers

    private static WindowInfo? PickTargetWindow() =>
        WindowEnumerator.Enumerate().FirstOrDefault(w => w.Width > 300 && w.Height > 300);

    /// <summary>
    /// Readiness is checked on /api/session rather than /api/health, because health is behind the
    /// lock and answers 401 until this test has a session - which it cannot get before the server
    /// is listening.
    /// </summary>
    private async Task WaitForServerAsync()
    {
        var deadline = DateTime.UtcNow.AddSeconds(45);
        while (DateTime.UtcNow < deadline)
        {
            if (_server is { HasExited: true })
            {
                throw new InvalidOperationException($"Server exited early with code {_server.ExitCode}");
            }

            try
            {
                var response = await _http.GetAsync($"{_baseUrl}/api/session");
                if (response.StatusCode == HttpStatusCode.OK) return;
            }
            catch (HttpRequestException)
            {
                // Not listening yet.
            }
            catch (TaskCanceledException)
            {
                // Slow start.
            }

            await Task.Delay(300);
        }

        throw new TimeoutException($"Server did not start listening at {_baseUrl}");
    }

    private async Task UnlockAsync()
    {
        var response = await _http.PostAsJsonAsync($"{_baseUrl}/api/unlock", new { password = Password });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.NotEmpty(_cookies.GetCookies(new Uri(_baseUrl)).Cast<Cookie>());
    }

    /// <summary>A hub connection carrying the session cookie, exactly as the browser's does.</summary>
    private HubConnection BuildHub() => new HubConnectionBuilder()
        .WithUrl($"{_baseUrl}/hubs/argus", options => options.Cookies = _cookies)
        .Build();

    private async Task<ClientWebSocket> ConnectFrameSocketAsync(string? clientId)
    {
        var socket = new ClientWebSocket();
        socket.Options.Cookies = _cookies;

        await socket.ConnectAsync(
            new Uri($"{_baseUrl.Replace("http://", "ws://")}/ws/frames?clientId={clientId}"),
            CancellationToken.None);

        return socket;
    }

    /// <summary>
    /// Reads frames off the socket continuously into a queue.
    ///
    /// The receive loop is never cancelled mid-read: cancelling ClientWebSocket.ReceiveAsync
    /// puts the socket into Aborted, so a "wait a bit for a frame, then keep using the socket"
    /// pattern built on cancellation destroys the connection it is testing. Timeouts belong on
    /// the queue read instead.
    /// </summary>
    private sealed class FrameReader : IAsyncDisposable
    {
        private readonly WebSocket _socket;
        private readonly Channel<ReceivedFrame> _frames = Channel.CreateUnbounded<ReceivedFrame>();
        private readonly CancellationTokenSource _cts = new();
        private readonly Task _loop;

        public FrameReader(WebSocket socket)
        {
            _socket = socket;
            _loop = Task.Run(ReceiveLoopAsync);
        }

        private async Task ReceiveLoopAsync()
        {
            var buffer = new byte[512 * 1024];
            try
            {
                while (_socket.State == WebSocketState.Open && !_cts.IsCancellationRequested)
                {
                    using var payload = new MemoryStream();
                    WebSocketReceiveResult result;
                    do
                    {
                        result = await _socket.ReceiveAsync(buffer, _cts.Token);
                        if (result.MessageType == WebSocketMessageType.Close)
                        {
                            _frames.Writer.TryComplete();
                            return;
                        }
                        payload.Write(buffer, 0, result.Count);
                    }
                    while (!result.EndOfMessage);

                    var bytes = payload.ToArray();
                    if (bytes.Length < 18) continue;

                    _frames.Writer.TryWrite(new ReceivedFrame(
                        Handle: BinaryPrimitives.ReadInt64LittleEndian(bytes.AsSpan(0, 8)),
                        Sequence: BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(8, 4)),
                        Width: BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(12, 2)),
                        Height: BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(14, 2)),
                        JpegLength: bytes.Length - 16,
                        FirstByte: bytes[16],
                        SecondByte: bytes[17]));
                }
            }
            catch (Exception ex) when (ex is OperationCanceledException or WebSocketException)
            {
                // Socket closed during teardown.
            }
            finally
            {
                _frames.Writer.TryComplete();
            }
        }

        public async Task<ReceivedFrame?> NextAsync(TimeSpan timeout)
        {
            using var cts = new CancellationTokenSource(timeout);
            try
            {
                return await _frames.Reader.ReadAsync(cts.Token);
            }
            catch (Exception ex) when (ex is OperationCanceledException or ChannelClosedException)
            {
                return null;
            }
        }

        public async Task<List<ReceivedFrame>> TakeAsync(int count, TimeSpan timeout)
        {
            var frames = new List<ReceivedFrame>();
            var deadline = DateTime.UtcNow + timeout;

            while (frames.Count < count && DateTime.UtcNow < deadline)
            {
                var frame = await NextAsync(deadline - DateTime.UtcNow);
                if (frame is null) break;
                frames.Add(frame);
            }

            return frames;
        }

        /// <summary>Consumes whatever is queued until nothing arrives for <paramref name="quiet"/>.</summary>
        public async Task DrainAsync(TimeSpan quiet)
        {
            while (await NextAsync(quiet) is not null) { }
        }

        public async ValueTask DisposeAsync()
        {
            await _cts.CancelAsync();
            try { await _loop; } catch (Exception ex) when (ex is OperationCanceledException or WebSocketException) { }
            _cts.Dispose();
        }
    }

    private static int FreeTcpPort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    private static string LocateServerAssembly()
    {
        // The test output directory sits alongside the server's, so walk up to the repo root.
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !Directory.Exists(Path.Combine(dir.FullName, "src", "Argus.Server")))
        {
            dir = dir.Parent;
        }

        if (dir is null) throw new InvalidOperationException("Could not locate the repository root.");

        var serverBin = Path.Combine(dir.FullName, "src", "Argus.Server", "bin");
        var candidate = Directory
            .EnumerateFiles(serverBin, "Argus.Server.dll", SearchOption.AllDirectories)
            .OrderByDescending(File.GetLastWriteTimeUtc)
            .FirstOrDefault();

        return candidate ?? throw new InvalidOperationException(
            $"Argus.Server.dll not found under {serverBin}. Build the server first.");
    }

    private sealed record ReceivedFrame(
        long Handle, int Sequence, int Width, int Height, int JpegLength, byte FirstByte, byte SecondByte);

    private sealed record HealthResponse(bool Ok, int Attached);

    private sealed record SendKeyResponse(bool Delivered, string? Backend, string? Reason);

    private sealed record HubWindow(string WindowId, long Handle, string Title, string MatchKey);
}
