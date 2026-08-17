using Argus.Server.Capture;
using Argus.Server.Hubs;
using Argus.Server.Input;
using Argus.Server.Interop;
using Argus.Server.Security;
using Argus.Server.Services;
using Argus.Server.Streaming;
using Argus.Server.Windows;
using Microsoft.AspNetCore.SignalR;

// Physical-pixel capture and correct window rects require per-monitor DPI awareness, and it must be
// set before any window is queried.
NativeMethods.SetProcessDpiAwarenessContext(NativeMethods.DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2);

var builder = WebApplication.CreateBuilder(args);

int port = builder.Configuration.GetValue("Argus:Port", 5227);
var (urls, bindingNote) = NetworkBinding.Resolve(
    port,
    builder.Configuration["Argus:Urls"] ?? Environment.GetEnvironmentVariable("ARGUS_URLS"));
builder.WebHost.UseUrls(urls);

builder.Services.AddSignalR(options =>
{
    options.EnableDetailedErrors = builder.Environment.IsDevelopment();
});

builder.Services.AddSingleton<SessionGuard>();
builder.Services.AddSingleton<ClientRegistry>();
builder.Services.AddSingleton<CaptureManager>();
builder.Services.AddSingleton<SelectionStore>();
builder.Services.AddSingleton<ApplicationLauncher>();
builder.Services.AddSingleton<OpenWithLauncher>();

builder.Services.AddSingleton<WindowMessageInjector>();
builder.Services.AddSingleton<ForegroundInjector>();
builder.Services.AddSingleton<ConsoleInputInjector>();
builder.Services.AddSingleton<MouseInjector>();
builder.Services.AddSingleton<InputRouter>();

builder.Services.AddHostedService<WatchdogService>();

// The Angular dev server runs on a different origin during development; in production the built
// app is served from wwwroot by this same host and no CORS is involved.
builder.Services.AddCors(options => options.AddPolicy("dev", policy => policy
    .WithOrigins("http://localhost:4200", "http://127.0.0.1:4200")
    .AllowAnyHeader()
    .AllowAnyMethod()
    .AllowCredentials()));

var app = builder.Build();

if (app.Environment.IsDevelopment()) app.UseCors("dev");

app.UseWebSockets(new WebSocketOptions { KeepAliveInterval = TimeSpan.FromSeconds(20) });

// Before the hub and the frame socket are mapped, or a locked viewer still reaches them.
app.UseSessionGate();

app.UseDefaultFiles();
app.UseStaticFiles();

app.MapSessionEndpoints();
app.MapHub<ArgusHub>("/hubs/argus");
app.MapFrameSocket();

app.MapGet("/api/health", (CaptureManager capture) => Results.Ok(new
{
    ok = true,
    utc = DateTimeOffset.UtcNow,
    attached = capture.Sessions.Count,
    sessions = capture.Sessions.Select(s => s.Snapshot()),
}));

app.MapGet("/api/windows", () => Results.Ok(WindowEnumerator.Enumerate()));

// Single-frame grab, handy for smoke tests and for debugging a tile that looks wrong.
app.MapGet("/api/windows/{handle:long}/frame", (long handle, string? quality, ILoggerFactory loggers) =>
{
    var info = WindowEnumerator.Describe(handle);
    if (info is null) return Results.NotFound(new { error = "No such window" });

    var level = Enum.TryParse<QualityLevel>(quality, ignoreCase: true, out var parsed)
        ? parsed
        : QualityLevel.Medium;

    using var source = new GdiWindowCaptureSource(handle, loggers.CreateLogger("frame"));
    using var bitmap = source.TryCapture();
    if (bitmap is null) return Results.NotFound(new { error = "Nothing to capture (minimised or gone)" });

    var frame = JpegEncoder.Encode(bitmap, QualityPreset.For(level));
    return Results.File(frame.Jpeg, "image/jpeg");
});

// SPA fallback: any unmatched non-API path serves the Angular shell so deep links work.
app.MapFallbackToFile("index.html");

// Push every capture status change to the dashboard.
var capture = app.Services.GetRequiredService<CaptureManager>();
var hub = app.Services.GetRequiredService<IHubContext<ArgusHub>>();
capture.StatusChanged += update => _ = hub.Clients.All.SendAsync("WindowStatus", update);

var startupLog = app.Services.GetRequiredService<ILoggerFactory>().CreateLogger("startup");
startupLog.LogInformation("Argus listening on {Urls} ({Note})", string.Join(", ", urls), bindingNote);

if (!app.Services.GetRequiredService<SessionGuard>().Required)
{
    startupLog.LogWarning(
        "No password set - anyone who can reach this port can drive this desktop. "
        + "Set Argus:Password in appsettings.json or the ARGUS_PASSWORD environment variable.");
}

app.Run();
