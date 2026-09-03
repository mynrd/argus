using System.Text.RegularExpressions;
using Argus.Server.Services;

namespace Argus.Server.Api;

public sealed class OpenTerminalRequest
{
    /// <summary>Where the shell starts. Anything that is not an existing directory falls back to the profile.</summary>
    public string? Cwd { get; set; }

    public int? Cols { get; set; }

    public int? Rows { get; set; }
}

public sealed class RenameTerminalRequest
{
    /// <summary>Empty clears the label, and the tab falls back to "Terminal N".</summary>
    public string? Name { get; set; }
}

/// <summary>
/// The terminals running on the host: what is open, opening another, renaming a tab, and killing
/// one.
///
/// Plain endpoints rather than hub methods, for the reason <see cref="PortEndpoints"/> spells out -
/// SignalR runs one invocation per connection at a time, and a terminal list that waits on a WMI
/// scan has no business blocking a keystroke. The live half of a terminal is not here at all: that
/// is <see cref="Streaming.TerminalSocketEndpoint"/>, one WebSocket per attached terminal.
///
/// Everything under /api is behind the session gate, which is what stands between the network and
/// a shell running as the user. Note what that means with no password configured: Argus is open by
/// design in that state, and a terminal is the most direct thing in it.
/// </summary>
public static partial class TerminalEndpoints
{
    public static void MapTerminalEndpoints(this WebApplication app)
    {
        // Both lists in one answer: the daemon's own terminals, which get tabs, and any marked
        // shell the OS still shows that the daemon has lost track of, which gets a row and a Kill.
        app.MapGet("/api/terminals", async (
            TerminalHostClient host,
            TerminalProcessScanner scanner,
            CancellationToken cancel) =>
        {
            var terminals = await host.ListAsync(cancel);
            var known = terminals.Select(t => t.TerminalId).ToHashSet(StringComparer.Ordinal);
            var pids = terminals.Select(t => t.Pid).OfType<int>().ToHashSet();

            var strays = (await scanner.ScanAsync(cancel))
                // Matched by id first, which survives pid reuse, and by pid as the fallback for a
                // shell whose marker could not be read off the command line.
                .Where(stray => (stray.TerminalId is null || !known.Contains(stray.TerminalId)) && !pids.Contains(stray.Pid))
                .ToList();

            return Results.Ok(new { terminals, strays });
        });

        app.MapPost("/api/terminals", async (
            OpenTerminalRequest body,
            TerminalHostClient host,
            CancellationToken cancel) =>
        {
            var (outcome, terminal) = await host.OpenAsync(body.Cwd, body.Cols, body.Rows, cancel);
            return outcome.Ok ? Results.Ok(terminal) : Problem(outcome);
        });

        app.MapDelete("/api/terminals/{terminalId}", async (
            string terminalId,
            TerminalHostClient host,
            CancellationToken cancel) =>
        {
            if (!IsTerminalId(terminalId)) return Results.BadRequest(new { error = "Malformed terminal id" });

            var outcome = await host.KillAsync(terminalId, cancel);
            return outcome.Ok ? Results.NoContent() : Problem(outcome);
        });

        // No id: closes every terminal the daemon owns. Checked ahead of the route above because
        // "/api/terminals" would otherwise never match it.
        app.MapDelete("/api/terminals", async (TerminalHostClient host, CancellationToken cancel) =>
        {
            var outcome = await host.KillAllAsync(cancel);
            return outcome.Ok ? Results.NoContent() : Problem(outcome);
        });

        app.MapPost("/api/terminals/{terminalId}/name", async (
            string terminalId,
            RenameTerminalRequest body,
            TerminalHostClient host,
            CancellationToken cancel) =>
        {
            if (!IsTerminalId(terminalId)) return Results.BadRequest(new { error = "Malformed terminal id" });

            var outcome = await host.RenameAsync(terminalId, body.Name, cancel);
            return outcome.Ok ? Results.NoContent() : Problem(outcome);
        });

        // A stray has no pty to close, so it goes by pid and taskkill. The scanner re-checks that
        // the pid is still a marked Argus shell before it kills anything.
        app.MapDelete("/api/terminals/strays/{pid:int}", async (
            int pid,
            TerminalProcessScanner scanner,
            CancellationToken cancel) =>
        {
            var outcome = await scanner.KillAsync(pid, cancel);
            return outcome.Ok ? Results.NoContent() : Problem(outcome);
        });
    }

    private static IResult Problem(TerminalOutcome outcome) =>
        Results.Json(new { error = outcome.Error }, statusCode: outcome.Status);

    /// <summary>
    /// The daemon mints ids as bare Guid hex, so anything else never named a terminal and is
    /// rejected before it reaches the pipe.
    /// </summary>
    internal static bool IsTerminalId(string? value) => value is not null && TerminalIdPattern().IsMatch(value);

    [GeneratedRegex("^[0-9a-f]{32}$")]
    private static partial Regex TerminalIdPattern();
}
