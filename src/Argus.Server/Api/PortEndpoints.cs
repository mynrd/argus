using Argus.Server.Services;

namespace Argus.Server.Api;

/// <summary>
/// The listening ports of the host, which are pinned or hidden, and what each one turns out to be.
///
/// This is the page that proved the rule. Identifying a port means an HTTP request that can sit
/// for two seconds against something that is not a web server, and while these lived on the hub -
/// one invocation per connection at a time - thirty of them queued ahead of everything else and
/// stalled the whole app. As plain endpoints they run in parallel and share nothing.
/// </summary>
public static class PortEndpoints
{
    public static void MapPortEndpoints(this WebApplication app)
    {
        app.MapGet("/api/ports", (PortPreferenceStore saved) => Results.Ok(Listing(saved.Current)));

        // Every one of these answers with the whole listing rather than an ack: pinning a port
        // that is not listening adds a row, and either choice clears the other one, so there is
        // no local edit that would produce the same result.
        app.MapPut("/api/ports/{port:int}/favourite", (int port, PortPreferenceStore saved) =>
            Results.Ok(Listing(saved.SetFavourite(port, favourite: true))));

        app.MapDelete("/api/ports/{port:int}/favourite", (int port, PortPreferenceStore saved) =>
            Results.Ok(Listing(saved.SetFavourite(port, favourite: false))));

        app.MapPut("/api/ports/{port:int}/hidden", (int port, PortPreferenceStore saved) =>
            Results.Ok(Listing(saved.SetHidden(port, hidden: true))));

        app.MapDelete("/api/ports/{port:int}/hidden", (int port, PortPreferenceStore saved) =>
            Results.Ok(Listing(saved.SetHidden(port, hidden: false))));

        // One port at a time so each answer lands as soon as it is known, rather than the page
        // waiting on the slowest thing that will never reply.
        app.MapGet("/api/ports/{port:int}/identity", async (int port, PortProbe probe, CancellationToken cancel) =>
        {
            var identity = await probe.IdentifyAsync(port, cancel);
            return Results.Ok(new
            {
                port,
                responded = identity.Responded,
                scheme = identity.Scheme,
                title = identity.Title,
            });
        });
    }

    private static IEnumerable<object> Listing(PortPreferences preferences) =>
        PortScanner.Compose(PortScanner.Enumerate(), preferences).Select(p => new
        {
            port = p.Port,
            process = p.Process,
            pid = p.Pid,
            isSystem = p.IsSystem,
            isFavourite = p.IsFavourite,
            isHidden = p.IsHidden,
            isListening = p.IsListening,
            addresses = p.Addresses.Select(a => new
            {
                kind = a.Kind,
                label = a.Label,
                ip = a.Address.ToString(),
            }),
        });
}
