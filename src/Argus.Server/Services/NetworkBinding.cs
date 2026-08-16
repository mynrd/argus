using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace Argus.Server.Services;

/// <summary>
/// Chooses which addresses Kestrel listens on.
///
/// Argus injects keystrokes into the desktop and has no authentication, so binding 0.0.0.0 would
/// expose desktop control to every network the machine ever joins. Instead it binds loopback plus
/// the Tailscale address only, which is the reachability the product actually needs.
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
        foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (nic.OperationalStatus != OperationalStatus.Up) continue;

            foreach (var ua in nic.GetIPProperties().UnicastAddresses)
            {
                if (IsInCgnatRange(ua.Address)) return ua.Address;
            }
        }

        return null;
    }

    internal static bool IsInCgnatRange(IPAddress address)
    {
        if (address.AddressFamily != AddressFamily.InterNetwork) return false;

        Span<byte> candidate = stackalloc byte[4];
        Span<byte> baseline = stackalloc byte[4];
        if (!address.TryWriteBytes(candidate, out _)) return false;
        if (!CgnatBase.TryWriteBytes(baseline, out _)) return false;

        uint value = (uint)(candidate[0] << 24 | candidate[1] << 16 | candidate[2] << 8 | candidate[3]);
        uint start = (uint)(baseline[0] << 24 | baseline[1] << 16 | baseline[2] << 8 | baseline[3]);
        uint mask = uint.MaxValue << (32 - CgnatPrefixLength);

        return (value & mask) == (start & mask);
    }

    /// <summary>
    /// Loopback plus Tailscale, unless ARGUS_URLS / --urls says otherwise. Returns the urls and a
    /// human-readable note about what is reachable.
    /// </summary>
    public static (string[] Urls, string Note) Resolve(int port, string? explicitUrls)
    {
        if (!string.IsNullOrWhiteSpace(explicitUrls))
        {
            return (explicitUrls.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries),
                    "using the configured ARGUS_URLS");
        }

        var tailscale = FindTailscaleAddress();
        if (tailscale is null)
        {
            return ([$"http://127.0.0.1:{port}"],
                    "no Tailscale interface found - listening on localhost only");
        }

        return ([$"http://127.0.0.1:{port}", $"http://{tailscale}:{port}"],
                $"reachable on your tailnet at http://{tailscale}:{port}");
    }
}
