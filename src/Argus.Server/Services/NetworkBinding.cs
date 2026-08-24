using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace Argus.Server.Services;

/// <summary>
/// Chooses which addresses Kestrel listens on.
///
/// Argus injects keystrokes into the desktop, so it binds named private interfaces only - loopback,
/// Tailscale, and the machine's private LAN addresses. It never binds 0.0.0.0 by default: that
/// would also expose desktop control on any public-range interface, and would keep listening on a
/// hotspot or hotel network the moment the machine joins one. Set ARGUS_URLS to override.
/// </summary>
public static class NetworkBinding
{
    /// <summary>Tailscale hands out addresses from the 100.64.0.0/10 CGNAT range.</summary>
    private static readonly IPAddress CgnatBase = IPAddress.Parse("100.64.0.0");
    private const int CgnatPrefixLength = 10;

    public static IPAddress? FindTailscaleAddress()
    {
        // The CGNAT range is the reliable signal - the adapter name differs across Tailscale
        // versions, and nothing else on a normal machine hands out 100.64/10.
        foreach (var address in OperationalUnicastAddresses())
        {
            if (IsInCgnatRange(address)) return address;
        }

        return null;
    }

    /// <summary>
    /// Private LAN addresses (RFC 1918) on live interfaces, so the app is reachable from another
    /// machine on the same home or office network without any extra flags.
    /// </summary>
    public static IReadOnlyList<IPAddress> FindLanAddresses()
    {
        List<IPAddress> found = [];

        foreach (var address in OperationalUnicastAddresses())
        {
            // Tailscale is handled separately, and listing it twice would bind the same socket.
            if (IsInCgnatRange(address)) continue;
            if (!IsPrivateLan(address)) continue;
            if (found.Contains(address)) continue;

            found.Add(address);
        }

        return found;
    }

    private static IEnumerable<IPAddress> OperationalUnicastAddresses()
    {
        foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (nic.OperationalStatus != OperationalStatus.Up) continue;
            if (nic.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;

            foreach (var ua in nic.GetIPProperties().UnicastAddresses)
            {
                yield return ua.Address;
            }
        }
    }

    internal static bool IsInCgnatRange(IPAddress address) =>
        IsInRange(address, CgnatBase, CgnatPrefixLength);

    /// <summary>RFC 1918 ranges only - the addresses a router hands out on a home or office LAN.</summary>
    internal static bool IsPrivateLan(IPAddress address)
    {
        if (address.AddressFamily != AddressFamily.InterNetwork) return false;
        if (IPAddress.IsLoopback(address)) return false;

        return IsInRange(address, IPAddress.Parse("10.0.0.0"), 8)
            || IsInRange(address, IPAddress.Parse("172.16.0.0"), 12)
            || IsInRange(address, IPAddress.Parse("192.168.0.0"), 16);
    }

    private static bool IsInRange(IPAddress address, IPAddress rangeBase, int prefixLength)
    {
        if (address.AddressFamily != AddressFamily.InterNetwork) return false;

        Span<byte> candidate = stackalloc byte[4];
        Span<byte> baseline = stackalloc byte[4];
        if (!address.TryWriteBytes(candidate, out _)) return false;
        if (!rangeBase.TryWriteBytes(baseline, out _)) return false;

        uint value = (uint)(candidate[0] << 24 | candidate[1] << 16 | candidate[2] << 8 | candidate[3]);
        uint start = (uint)(baseline[0] << 24 | baseline[1] << 16 | baseline[2] << 8 | baseline[3]);
        uint mask = uint.MaxValue << (32 - prefixLength);

        return (value & mask) == (start & mask);
    }

    /// <summary>
    /// Loopback, Tailscale and the private LAN, unless ARGUS_URLS / --urls says otherwise. Returns
    /// the urls and a human-readable note about what is reachable.
    /// </summary>
    public static (string[] Urls, string Note) Resolve(int port, string? explicitUrls)
    {
        if (!string.IsNullOrWhiteSpace(explicitUrls))
        {
            return (explicitUrls.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries),
                    "using the configured ARGUS_URLS");
        }

        List<string> urls = [$"http://127.0.0.1:{port}"];
        List<string> reachable = [];

        var tailscale = FindTailscaleAddress();
        if (tailscale is not null)
        {
            urls.Add($"http://{tailscale}:{port}");
            reachable.Add($"tailnet http://{tailscale}:{port}");
        }

        foreach (var lan in FindLanAddresses())
        {
            urls.Add($"http://{lan}:{port}");
            reachable.Add($"lan http://{lan}:{port}");
        }

        var note = reachable.Count == 0
            ? "no Tailscale or private LAN interface found - listening on localhost only"
            : $"reachable at {string.Join(", ", reachable)}";

        return (urls.ToArray(), note);
    }
}
