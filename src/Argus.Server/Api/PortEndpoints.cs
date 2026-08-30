using Argus.Server.Services;

namespace Argus.Server.Api;

/// <summary>
/// The listening ports of the host, the pinned ones, and what each port turns out to be.
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
        app.MapGet("/api/ports", (FavouritePortStore favourites) => Results.Ok(Listing(favourites.Ports)));

        app.MapPut("/api/ports/{port:int}/favourite", (int port, FavouritePortStore favourites) =>
            Results.Ok(Listing(favourites.Set(port, favourite: true))));

        app.MapDelete("/api/ports/{port:int}/favourite", (int port, FavouritePortStore favourites) =>
            Results.Ok(Listing(favourites.Set(port, favourite: false))));

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

    private static IEnumerable<object> Listing(IReadOnlyCollection<int> favourites) =>
        PortScanner.Compose(PortScanner.Enumerate(), favourites).Select(p => new
        {
            port = p.Port,
            process = p.Process,
            pid = p.Pid,
            isSystem = p.IsSystem,
            isFavourite = p.IsFavourite,
            isListening = p.IsListening,
            addresses = p.Addresses.Select(a => new
            {
                kind = a.Kind,
                label = a.Label,
                ip = a.Address.ToString(),
            }),
        });
}
