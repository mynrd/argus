using System.Collections.Concurrent;
using System.Net;
using System.Net.Http;
using System.Text.RegularExpressions;

namespace Argus.Server.Services;

/// <summary>What a port answered with, if it answered at all.</summary>
/// <param name="Scheme">http or https, whichever replied. The links are built from it.</param>
public sealed record PortIdentity(bool Responded, string Scheme, string? Title);

/// <summary>
/// Asks a listening port what it is, by fetching its home page and reading the &lt;title&gt;.
///
/// Server side rather than in the page for two reasons: the browser may be a phone on the tailnet
/// that cannot reach 127.0.0.1 at all, and a page served over https cannot fetch a plain http
/// address without the browser blocking it. Only runs when a port is expanded - nothing here
/// probes the whole machine on load.
/// </summary>
public sealed partial class PortProbe : IDisposable
{
    /// <summary>Long enough for a local web app to answer, short enough not to stall the row.</summary>
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(2);

    /// <summary>How long an answer is reused. Re-expanding a row should not re-probe.</summary>
    private static readonly TimeSpan CacheFor = TimeSpan.FromSeconds(30);

    private readonly HttpClient _http;
    private readonly ConcurrentDictionary<int, (DateTimeOffset At, PortIdentity Identity)> _cache = new();

    public PortProbe()
    {
        // Local dev servers run on self-signed certificates as a matter of course, and refusing to
        // read a title over one would mean https ports never get identified.
        var handler = new SocketsHttpHandler
        {
            AllowAutoRedirect = false,
            SslOptions = { RemoteCertificateValidationCallback = (_, _, _, _) => true },
            ConnectTimeout = Timeout,
        };

        _http = new HttpClient(handler) { Timeout = Timeout };
    }

    public async Task<PortIdentity> IdentifyAsync(int port, CancellationToken cancellation = default)
    {
        if (port is < 1 or > 65535) return new PortIdentity(false, "http", null);

        if (_cache.TryGetValue(port, out var hit) && DateTimeOffset.UtcNow - hit.At < CacheFor)
        {
            return hit.Identity;
        }

        var identity = await ProbeAsync(port, cancellation);
        _cache[port] = (DateTimeOffset.UtcNow, identity);
        return identity;
    }

    private async Task<PortIdentity> ProbeAsync(int port, CancellationToken cancellation)
    {
        foreach (string scheme in new[] { "http", "https" })
        {
            try
            {
                using var response = await _http.GetAsync(
                    $"{scheme}://127.0.0.1:{port}/",
                    HttpCompletionOption.ResponseHeadersRead,
                    cancellation);

                // A redirect or a 404 still proves something HTTP is there and which scheme it
                // speaks, which is what the link needs even when there is no title to read.
                return new PortIdentity(true, scheme, await TitleOf(response, cancellation));
            }
            catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
            {
                throw;
            }
            catch
            {
                // Wrong scheme, or not HTTP at all. Try the other one, then give up quietly.
            }
        }

        return new PortIdentity(false, "http", null);
    }

    private static async Task<string?> TitleOf(HttpResponseMessage response, CancellationToken cancellation)
    {
        // An error page's title names the error, not the service - "Not Found" next to a port is
        // worse than no label at all. The response still counted as an answer, so the link and the
        // scheme it resolved stay.
        if (!response.IsSuccessStatusCode) return null;

        var type = response.Content.Headers.ContentType?.MediaType;
        if (type is not null && !type.Contains("html", StringComparison.OrdinalIgnoreCase)) return null;

        // The title is in the head; reading the whole body of an unknown endpoint is not worth it.
        var buffer = new char[16 * 1024];
        using var stream = await response.Content.ReadAsStreamAsync(cancellation);
        using var reader = new StreamReader(stream);
        int read = await reader.ReadBlockAsync(buffer, cancellation);
        if (read <= 0) return null;

        var match = TitleTag().Match(new string(buffer, 0, read));
        if (!match.Success) return null;

        string title = WebUtility.HtmlDecode(match.Groups[1].Value).Trim();
        title = Whitespace().Replace(title, " ");

        return title.Length == 0 ? null : title[..Math.Min(title.Length, 120)];
    }

    [GeneratedRegex("<title[^>]*>(.*?)</title>", RegexOptions.IgnoreCase | RegexOptions.Singleline)]
    private static partial Regex TitleTag();

    [GeneratedRegex(@"\s+")]
    private static partial Regex Whitespace();

    public void Dispose() => _http.Dispose();
}
