using System.Net;
using System.Net.Sockets;
using Argus.Server.Services;

namespace Argus.Server.Tests;

public class PortScannerTests
{
    private static readonly HostAddress Localhost = new("localhost", "localhost", IPAddress.Loopback);
    private static readonly HostAddress Tailnet = new("tailscale", "tailscale", IPAddress.Parse("100.97.229.12"));
    private static readonly HostAddress Nord = new("nordvpn", "nordvpn", IPAddress.Parse("100.83.4.9"));
    private static readonly HostAddress Lan = new("lan", "Wi-Fi", IPAddress.Parse("192.168.68.56"));

    private static readonly HostAddress[] Catalog = [Localhost, Tailnet, Nord, Lan];

    [Fact]
    public void A_wildcard_socket_is_reachable_everywhere()
    {
        Assert.Equal(Catalog, PortScanner.Reachable([IPAddress.Any], Catalog));
    }

    [Fact]
    public void An_IPv6_wildcard_also_covers_the_IPv4_addresses()
    {
        // Windows opens :: sockets dual-stack unless the app sets IPV6_V6ONLY, and Node and Vite
        // both bind :: - treating that as IPv6-only would show no links at all for a dev server.
        Assert.Equal(Catalog, PortScanner.Reachable([IPAddress.IPv6Any], Catalog));
    }

    [Fact]
    public void A_loopback_socket_is_reachable_only_on_localhost()
    {
        Assert.Equal([Localhost], PortScanner.Reachable([IPAddress.Loopback], Catalog));
        Assert.Equal([Localhost], PortScanner.Reachable([IPAddress.IPv6Loopback], Catalog));
    }

    [Fact]
    public void Any_loopback_address_counts_as_localhost()
    {
        // The whole 127.0.0.0/8 block is loopback, and 127.0.0.2 is a real choice some tools make.
        Assert.Equal([Localhost], PortScanner.Reachable([IPAddress.Parse("127.0.0.2")], Catalog));
    }

    [Fact]
    public void A_socket_bound_to_one_interface_lists_only_that_one()
    {
        Assert.Equal([Tailnet], PortScanner.Reachable([Tailnet.Address], Catalog));
    }

    [Fact]
    public void Several_bindings_are_merged_in_catalog_order()
    {
        // Kestrel binds each address separately, which is how Argus itself shows up.
        var reachable = PortScanner.Reachable([Lan.Address, IPAddress.Loopback, Tailnet.Address], Catalog);

        Assert.Equal([Localhost, Tailnet, Lan], reachable);
    }

    [Fact]
    public void An_address_this_machine_does_not_have_reaches_nothing()
    {
        Assert.Empty(PortScanner.Reachable([IPAddress.Parse("10.9.9.9")], Catalog));
    }

    // ------------------------------------------------------------- favourites

    private static ListeningPort Listening(int port, string process = "node", bool system = false) =>
        new(port, process, 42, system, [Localhost]);

    [Fact]
    public void Composing_flags_the_favourites_among_the_listening_ports()
    {
        var composed = PortScanner.Compose([Listening(3000), Listening(5227)], [5227]);

        Assert.Equal([false, true], composed.Select(e => e.IsFavourite));
        Assert.All(composed, e => Assert.True(e.IsListening));
    }

    [Fact]
    public void A_favourite_that_is_not_listening_still_gets_a_row()
    {
        // The whole point of pinning 3000 is that it stays on the page while you restart the dev
        // server behind it.
        var composed = PortScanner.Compose([Listening(5227)], [3000]);

        var offline = Assert.Single(composed, e => e.Port == 3000);
        Assert.True(offline.IsFavourite);
        Assert.False(offline.IsListening);
        Assert.Empty(offline.Addresses);
    }

    [Fact]
    public void A_favourite_that_is_listening_is_not_duplicated()
    {
        var composed = PortScanner.Compose([Listening(5227)], [5227]);

        var entry = Assert.Single(composed);
        Assert.True(entry.IsListening);
        Assert.Equal("node", entry.Process);
    }

    [Fact]
    public void Composing_keeps_the_rows_in_port_order()
    {
        var composed = PortScanner.Compose([Listening(8080), Listening(80)], [3000, 65000]);

        Assert.Equal([80, 3000, 8080, 65000], composed.Select(e => e.Port));
    }

    [Fact]
    public void Composing_preserves_what_the_scan_said_about_each_port()
    {
        var composed = PortScanner.Compose([Listening(445, "System", system: true)], []);

        var entry = Assert.Single(composed);
        Assert.True(entry.IsSystem);
        Assert.Equal(42, entry.Pid);
        Assert.Equal([Localhost], entry.Addresses);
    }

    [Fact]
    public void The_catalog_always_starts_with_localhost()
    {
        var catalog = PortScanner.Catalog();

        Assert.NotEmpty(catalog);
        Assert.Equal("localhost", catalog[0].Kind);
        Assert.Equal(IPAddress.Loopback, catalog[0].Address);
    }

    [Fact]
    public void The_catalog_holds_no_duplicate_addresses()
    {
        var catalog = PortScanner.Catalog();

        Assert.Equal(catalog.Count, catalog.Select(a => a.Address.ToString()).Distinct().Count());
    }

    [Fact]
    public void A_port_opened_here_shows_up_in_the_scan()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();

        try
        {
            int port = ((IPEndPoint)listener.LocalEndpoint).Port;
            var found = PortScanner.Enumerate().SingleOrDefault(p => p.Port == port);

            Assert.NotNull(found);
            Assert.Equal(Environment.ProcessId, found.Pid);
            Assert.Equal(["localhost"], found.Addresses.Select(a => a.Kind));
        }
        finally
        {
            listener.Stop();
        }
    }

    [Fact]
    public void Ports_come_back_sorted_and_listed_once_each()
    {
        var ports = PortScanner.Enumerate().Select(p => p.Port).ToList();

        Assert.Equal(ports.OrderBy(p => p), ports);
        Assert.Equal(ports.Count, ports.Distinct().Count());
    }
}
