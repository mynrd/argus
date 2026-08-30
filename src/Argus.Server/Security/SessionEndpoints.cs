namespace Argus.Server.Security;

public sealed class UnlockRequest
{
    public string Password { get; set; } = string.Empty;
}

public static class SessionEndpoints
{
    /// <summary>
    /// Everything that can see or touch the host. Static files are not on the list on purpose: the
    /// Angular shell has to load in order to render the lock screen and ask for the password.
    /// </summary>
    private static readonly string[] Guarded = ["/api", "/hubs", "/ws"];

    /// <summary>The lock's own endpoints, which necessarily answer while locked.</summary>
    private static readonly string[] Unguarded = ["/api/session", "/api/unlock", "/api/lock"];

    /// <summary>
    /// Rejects anything that reaches the host without a live session. Must be registered before the
    /// hub and the frame socket are mapped, or those get their chance first.
    /// </summary>
    public static void UseSessionGate(this WebApplication app)
    {
        app.Use(async (context, next) =>
        {
            var guard = context.RequestServices.GetRequiredService<SessionGuard>();
            if (!guard.Required || !IsGuarded(context.Request.Path))
            {
                await next();
                return;
            }

            if (guard.IsOpen(context.Request.Cookies[SessionGuard.CookieName]))
            {
                await next();
                return;
            }

            context.Response.StatusCode = StatusCodes.Status401Unauthorized;
            await context.Response.WriteAsJsonAsync(new { error = "Argus is locked" });
        });
    }

    public static void MapSessionEndpoints(this WebApplication app)
    {
        // Lets the app decide on load whether to show the lock screen, without guessing from a 401.
        // The machine name rides along so the header tag and the tab title name the host the
        // moment the page loads, before the hub is up and while the lock screen is still on.
        app.MapGet("/api/session", (SessionGuard guard, HttpContext context) => Results.Ok(new
        {
            required = guard.Required,
            unlocked = guard.IsOpen(context.Request.Cookies[SessionGuard.CookieName]),
            host = Environment.MachineName,
        }));

        app.MapPost("/api/unlock", (UnlockRequest body, SessionGuard guard, HttpContext context) =>
        {
            string client = ClientOf(context);

            if (guard.Cooldown(client) is { } wait)
            {
                return Results.Json(
                    new { unlocked = false, reason = $"Too many attempts - wait {Math.Ceiling(wait.TotalSeconds)}s" },
                    statusCode: StatusCodes.Status429TooManyRequests);
            }

            if (!guard.PasswordMatches(body.Password))
            {
                guard.RecordFailure(client);
                return Results.Json(
                    new { unlocked = false, reason = "Wrong password" },
                    statusCode: StatusCodes.Status401Unauthorized);
            }

            guard.ClearFailures(client);
            context.Response.Cookies.Append(SessionGuard.CookieName, guard.Open(), CookieOptions(context));
            return Results.Ok(new { unlocked = true });
        });

        app.MapPost("/api/lock", (SessionGuard guard, HttpContext context) =>
        {
            guard.Close(context.Request.Cookies[SessionGuard.CookieName]);
            context.Response.Cookies.Delete(SessionGuard.CookieName, CookieOptions(context));
            return Results.Ok(new { unlocked = false });
        });
    }

    internal static bool IsGuarded(PathString path) =>
        Guarded.Any(prefix => path.StartsWithSegments(prefix))
        && !Unguarded.Any(exact => path.Equals(exact, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// HttpOnly so the token is out of reach of anything running in the page, and no expiry so the
    /// session dies with the browser. It rides the SignalR negotiate and the WebSocket upgrade for
    /// free, which is what makes the hub and the frame socket enforceable without client plumbing.
    /// </summary>
    private static CookieOptions CookieOptions(HttpContext context) => new()
    {
        HttpOnly = true,
        IsEssential = true,
        SameSite = SameSiteMode.Strict,
        Secure = context.Request.IsHttps,
        Path = "/",
    };

    private static string ClientOf(HttpContext context) =>
        context.Connection.RemoteIpAddress?.ToString() ?? "unknown";
}
