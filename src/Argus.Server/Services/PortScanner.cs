using System.Diagnostics;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using Argus.Server.Interop;

namespace Argus.Server.Services;

/// <summary>One way to reach the host: which network it belongs to, and the address on it.</summary>
/// <param name="Kind">localhost | tailscale | nordvpn | lan.</param>
/// <param name="Label">What to print. The kind, except on the LAN where the adapter name is more useful.</param>
public sealed record HostAddress(string Kind, string Label, IPAddress Address);

/// <summary>A listening TCP port and everywhere it can be reached from.</summary>
public sealed record ListeningPort(
    int Port,
    string Process,
    int Pid,
    bool IsSystem,
    IReadOnlyList<HostAddress> Addresses);

/// <summary>
/// A row on the Ports page: a listening port, or a favourite that is not listening right now.
/// </summary>
/// <param name="IsListening">
/// False for a favourite nothing is serving at the moment - the row stays so the link you pinned
/// does not vanish every time you restart the thing behind it.
/// </param>
/// <param name="IsHidden">
/// Struck off the list by hand. Still sent to the browser rather than dropped here: the page has
/// a "show hidden" toggle, and a row you cannot get back is a trap.
/// </param>
public sealed record PortEntry(
    int Port,
    string Process,
    int Pid,
    bool IsSystem,
    bool IsFavourite,
    bool IsHidden,
    bool IsListening,
    IReadOnlyList<HostAddress> Addresses);

/// <summary>
/// Every TCP port something on this machine is listening on, and which of the host's addresses
/// actually reach each one.
///
/// The second half is the part that is easy to get wrong: a dev server bound to 127.0.0.1 is not
/// reachable at the Tailscale address no matter how many links you print, so the address list is
/// computed per port from what the socket bound to rather than assumed to be the same for all.
/// </summary>
public static class PortScanner
{
    /// <summary>
    /// Processes whose ports are Windows plumbing rather than anything you would open. Matched on
    /// the process name, which catches the whole svchost block without hardcoding port numbers.
    /// </summary>
    private static readonly HashSet<string> SystemProcesses = new(StringComparer.OrdinalIgnoreCase)
    {
        "System", "Idle", "svchost", "lsass", "services", "wininit", "smss", "csrss",
        "spoolsv", "dasHost", "SearchIndexer", "wslservice",
    };

    /// <summary>
    /// Adapters whose addresses only mean something to a virtual machine on this box. They are
    /// private and up, so nothing else here would reject them, but a link to 192.168.224.1 is
    /// dead from the phone or laptop actually holding the browser.
    /// </summary>
    private static readonly string[] VirtualAdapters =
        ["vEthernet", "Hyper-V", "WSL", "VirtualBox", "VMware", "Loopback"];

    public static IReadOnlyList<ListeningPort> Enumerate()
    {
        var catalog = Catalog();
        var names = new Dictionary<int, string>();

        // Both tables: a socket bound to :: (the Node and Vite default) shows up only in the IPv6
        // one, and dropping it would hide the dev server you most likely came here for.
        var sockets = Listeners(NativeMethods.AF_INET).Concat(Listeners(NativeMethods.AF_INET6));

        // One row per port. Two processes cannot hold the same TCP port, so the first pid wins and
        // the extra rows are only extra bind addresses for it.
        var byPort = new Dictionary<int, (int Pid, List<IPAddress> Bindings)>();

        foreach (var (port, pid, address) in sockets)
        {
            if (!byPort.TryGetValue(port, out var entry))
            {
                entry = (pid, []);
                byPort[port] = entry;
            }

            if (!entry.Bindings.Contains(address)) entry.Bindings.Add(address);
        }

        var result = new List<ListeningPort>();

        foreach (var (port, entry) in byPort)
        {
            string name = ProcessName(entry.Pid, names);
            result.Add(new ListeningPort(
                port,
                name,
                entry.Pid,
                IsSystem(name),
                Reachable(entry.Bindings, catalog)));
        }

        return result.OrderBy(p => p.Port).ToList();
    }

    /// <summary>
    /// The scan with the saved choices folded in: every listening port flagged pinned or hidden,
    /// plus a row for each favourite that is not listening. Pure, so the offline-favourite case
    /// is testable.
    ///
    /// A hidden port that is not listening gets no row. Pinning one is a request to keep watching
    /// it; hiding one is the opposite, so there is nothing to draw and nothing to hide.
    /// </summary>
    public static IReadOnlyList<PortEntry> Compose(
        IReadOnlyList<ListeningPort> scanned,
        PortPreferences preferences)
    {
        var pinned = preferences.Favourites.ToHashSet();
        var struck = preferences.Hidden.ToHashSet();

        var entries = scanned
            .Select(p => new PortEntry(
                p.Port, p.Process, p.Pid, p.IsSystem,
                pinned.Contains(p.Port), struck.Contains(p.Port), true, p.Addresses))
            .ToList();

        var listening = scanned.Select(p => p.Port).ToHashSet();

        foreach (int port in pinned.Where(p => !listening.Contains(p)))
        {
            entries.Add(new PortEntry(port, string.Empty, 0, false, true, false, false, []));
        }

        return entries.OrderBy(e => e.Port).ToList();
    }

    /// <summary>
    /// Which of the host's addresses reach a socket, given what it bound to.
    ///
    /// Pure so the wildcard and loopback rules can be tested without a socket in sight.
    /// </summary>
    internal static IReadOnlyList<HostAddress> Reachable(
        IReadOnlyList<IPAddress> bindings,
        IReadOnlyList<HostAddress> catalog)
    {
        // 0.0.0.0 and :: both mean "every interface" - Windows opens :: sockets dual-stack unless
        // the app sets IPV6_V6ONLY, so an IPv6 wildcard answers on the IPv4 addresses too.
        if (bindings.Any(b => b.Equals(IPAddress.Any) || b.Equals(IPAddress.IPv6Any))) return catalog;

        var reachable = new List<HostAddress>();

        foreach (var entry in catalog)
        {
            bool hit = bindings.Any(b => IPAddress.IsLoopback(b)
                ? entry.Kind == "localhost"
                : b.Equals(entry.Address));

            if (hit && !reachable.Contains(entry)) reachable.Add(entry);
        }

        return reachable;
    }

    /// <summary>
    /// Every address this machine answers on, in the order worth reading: the loopback you are
    /// most likely on, then the overlay networks, then the LAN.
    /// </summary>
    internal static IReadOnlyList<HostAddress> Catalog()
    {
        List<HostAddress> local = [new HostAddress("localhost", "localhost", IPAddress.Loopback)];
        List<HostAddress> tailscale = [];
        List<HostAddress> nord = [];
        List<HostAddress> lan = [];

        foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (nic.OperationalStatus != OperationalStatus.Up) continue;
            if (nic.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;

            string kind = Classify(nic);
            if (kind == "virtual") continue;

            foreach (var ua in nic.GetIPProperties().UnicastAddresses)
            {
                var address = ua.Address;
                if (address.AddressFamily != AddressFamily.InterNetwork) continue;
                if (IPAddress.IsLoopback(address)) continue;

                switch (kind)
                {
                    case "tailscale":
                        tailscale.Add(new HostAddress(kind, "tailscale", address));
                        break;
                    case "nordvpn":
                        nord.Add(new HostAddress(kind, "nordvpn", address));
                        break;
                    default:
                        // Public-range addresses on a normal adapter are not something to hand out
                        // a click-to-open link for.
                        if (!NetworkBinding.IsPrivateLan(address)) continue;
                        lan.Add(new HostAddress("lan", nic.Name, address));
                        break;
                }
            }
        }

        return [.. local, .. tailscale, .. nord, .. lan];
    }

    /// <summary>
    /// Which overlay network an adapter belongs to.
    ///
    /// By adapter name, not by address range: NordVPN's Meshnet hands out 100.64.0.0/10 addresses
    /// exactly like Tailscale does, so the range that identifies a tailnet elsewhere in this app
    /// cannot tell the two apart here.
    /// </summary>
    private static string Classify(NetworkInterface nic)
    {
        string text = $"{nic.Name} {nic.Description}";

        if (text.Contains("Tailscale", StringComparison.OrdinalIgnoreCase)) return "tailscale";
        if (text.Contains("NordLynx", StringComparison.OrdinalIgnoreCase)
            || text.Contains("Nord", StringComparison.OrdinalIgnoreCase)) return "nordvpn";

        if (VirtualAdapters.Any(v => text.Contains(v, StringComparison.OrdinalIgnoreCase))) return "virtual";

        return "lan";
    }

    /// <summary>
    /// Windows plumbing rather than something you would open. Keyed on the process alone: a rule
    /// about low port numbers would also hide a web server on 80 and an FTP server on 21, which
    /// are exactly the ports this page exists to show.
    /// </summary>
    private static bool IsSystem(string process) => SystemProcesses.Contains(process);

    private static string ProcessName(int pid, Dictionary<int, string> cache)
    {
        if (cache.TryGetValue(pid, out var cached)) return cached;

        string name;
        try
        {
            // Protected processes refuse to be opened at all; the pid is still worth showing.
            using var process = Process.GetProcessById(pid);
            name = process.ProcessName;
        }
        catch
        {
            name = string.Empty;
        }

        cache[pid] = name;
        return name;
    }

    /// <summary>Reads one address family's listener table out of iphlpapi.</summary>
    private static unsafe List<(int Port, int Pid, IPAddress Address)> Listeners(int family)
    {
        List<(int, int, IPAddress)> rows = [];

        int size = 0;
        uint status = NativeMethods.GetExtendedTcpTable(
            nint.Zero, ref size, false, family, NativeMethods.TCP_TABLE_OWNER_PID_LISTENER, 0);

        if (status != NativeMethods.ERROR_INSUFFICIENT_BUFFER || size <= 0) return rows;

        nint buffer = Marshal.AllocHGlobal(size);
        try
        {
            status = NativeMethods.GetExtendedTcpTable(
                buffer, ref size, false, family, NativeMethods.TCP_TABLE_OWNER_PID_LISTENER, 0);

            if (status != 0) return rows;

            // Both tables are a uint count followed by a packed array of rows.
            int count = *(int*)buffer;
            nint first = buffer + sizeof(int);

            for (int i = 0; i < count; i++)
            {
                if (family == NativeMethods.AF_INET)
                {
                    var row = ((NativeMethods.MIB_TCPROW_OWNER_PID*)first)[i];
                    rows.Add((
                        NativeMethods.HostPort(row.LocalPort),
                        (int)row.OwningPid,
                        new IPAddress(row.LocalAddr)));
                }
                else
                {
                    var row = ((NativeMethods.MIB_TCP6ROW_OWNER_PID*)first)[i];
                    var bytes = new ReadOnlySpan<byte>(row.LocalAddr, 16);
                    rows.Add((
                        NativeMethods.HostPort(row.LocalPort),
                        (int)row.OwningPid,
                        new IPAddress(bytes)));
                }
            }
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }

        return rows;
    }
}
