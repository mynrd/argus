using Argus.Server.Services;

namespace Argus.Server.Api;

public sealed record OpenWithRequest(string Path, string? App);

/// <summary>
/// The host's filesystem, and handing one of its files to an app over there.
///
/// Plain HTTP for the same reason as <see cref="WindowEndpoints"/>: nothing here needs a live
/// connection, and a directory listing on a slow drive has no business sharing a queue with
/// keystrokes.
/// </summary>
public static class ExplorerEndpoints
{
    public static void MapExplorerEndpoints(this WebApplication app)
    {
        // One directory of the host filesystem, for the Browse dialog. A file picker in the browser
        // would list the phone's files and hand back no path at all, so the listing has to come
        // from the machine being controlled.
        app.MapGet("/api/browse", (string? path) =>
            Results.Ok(Listing(HostFileBrowser.List(path, runnableOnly: true))));

        // The Explorer page's listing - every file, not just the launchable ones.
        app.MapGet("/api/explore", (string? path) =>
            Results.Ok(Listing(HostFileBrowser.List(path, runnableOnly: false))));

        // The apps the Explorer page offers, so the list lives in one place.
        app.MapGet("/api/open-with/apps", () => Results.Ok(OpenWithLauncher.Apps.Select(a => new
        {
            key = a.Key,
            label = a.Label,
            handlesFolders = a.HandlesFolders,
            handlesFiles = a.HandlesFiles,
        })));

        // Opens a file or folder on the host, in a named app or its default one.
        app.MapPost("/api/open-with", (OpenWithRequest body, OpenWithLauncher launcher) =>
        {
            var result = launcher.Open(body.Path, body.App);
            return Results.Ok(new { started = result.Started, reason = result.Reason });
        });
    }

    private static object Listing(BrowseListing listing) => new
    {
        path = listing.Path,
        label = listing.Label,
        parent = listing.Parent,
        error = listing.Error,
        entries = listing.Entries.Select(e => new
        {
            name = e.Name,
            path = e.Path,
            isDirectory = e.IsDirectory,
        }),
    };
}
