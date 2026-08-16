using System.Net;
using Argus.Server.Services;

namespace Argus.Server.Tests;

public class NetworkBindingTests
{
    [Theory]
    [InlineData("100.64.0.0")]
    [InlineData("100.97.229.12")]
    [InlineData("100.127.255.255")]
    public void Tailscale_addresses_are_recognised(string address)
    {
        Assert.True(NetworkBinding.IsInCgnatRange(IPAddress.Parse(address)));
    }

    [Theory]
    [InlineData("100.63.255.255")]   // just below the range
    [InlineData("100.128.0.0")]      // just above the range
    [InlineData("192.168.1.10")]
    [InlineData("10.0.0.1")]
    [InlineData("127.0.0.1")]
    [InlineData("8.8.8.8")]
    public void Other_addresses_are_not_mistaken_for_Tailscale(string address)
    {
        Assert.False(NetworkBinding.IsInCgnatRange(IPAddress.Parse(address)));
    }

    [Fact]
    public void IPv6_is_not_matched()
    {
        Assert.False(NetworkBinding.IsInCgnatRange(IPAddress.Parse("::1")));
    }

    [Fact]
    public void Explicit_urls_win_over_detection()
    {
        var (urls, note) = NetworkBinding.Resolve(5227, "http://0.0.0.0:9000;http://127.0.0.1:9001");

        Assert.Equal(["http://0.0.0.0:9000", "http://127.0.0.1:9001"], urls);
        Assert.Contains("ARGUS_URLS", note);
    }

    [Fact]
    public void Resolve_always_includes_loopback_and_never_binds_all_interfaces_by_default()
    {
        // Argus can type into the desktop and has no auth, so a default 0.0.0.0 bind would expose
        // desktop control to every network this machine joins.
        var (urls, _) = NetworkBinding.Resolve(5227, explicitUrls: null);

        Assert.Contains("http://127.0.0.1:5227", urls);
        Assert.DoesNotContain(urls, u => u.Contains("0.0.0.0"));
        Assert.DoesNotContain(urls, u => u.Contains("[::]"));
    }

    [Fact]
    public void Resolve_honours_the_requested_port()
    {
        var (urls, _) = NetworkBinding.Resolve(8080, explicitUrls: null);
        Assert.All(urls, u => Assert.EndsWith(":8080", u));
    }

    [Fact]
    public void Blank_explicit_urls_fall_back_to_detection()
    {
        var (urls, _) = NetworkBinding.Resolve(5227, "   ");
        Assert.Contains("http://127.0.0.1:5227", urls);
    }
}
