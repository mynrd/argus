using Argus.Server.Services;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

namespace Argus.Server.Tests;

public class FavouritePortStoreTests : IDisposable
{
    private readonly string _path = Path.Combine(
        Path.GetTempPath(), $"argus-favourites-{Guid.NewGuid():N}.json");

    private FavouritePortStore Store() => new(
        NullLogger<FavouritePortStore>.Instance,
        new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["Argus:FavouritePortsPath"] = _path })
            .Build());

    [Fact]
    public void A_favourite_survives_a_restart()
    {
        // The reason this is a file on the host at all: the phone you pick up next has never seen
        // this machine's localStorage.
        Store().Set(3000, favourite: true);

        Assert.Equal([3000], Store().Ports);
    }

    [Fact]
    public void Unfavouriting_removes_it_from_the_file()
    {
        var store = Store();
        store.Set(3000, favourite: true);
        store.Set(3000, favourite: false);

        Assert.Empty(Store().Ports);
    }

    [Fact]
    public void Favouriting_twice_leaves_one_entry()
    {
        var store = Store();
        store.Set(3000, favourite: true);

        Assert.Equal([3000], store.Set(3000, favourite: true));
    }

    [Fact]
    public void Set_returns_the_list_as_it_stands_afterwards()
    {
        var store = Store();
        store.Set(3000, favourite: true);

        Assert.Equal([3000, 5227], store.Set(5227, favourite: true).Order());
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(65536)]
    public void Impossible_port_numbers_are_ignored(int port)
    {
        Assert.Empty(Store().Set(port, favourite: true));
    }

    [Fact]
    public void A_missing_file_is_not_an_error()
    {
        Assert.Empty(Store().Ports);
    }

    [Fact]
    public void A_corrupt_file_starts_empty_rather_than_throwing()
    {
        // A half-written file should cost you your favourites, not the whole page.
        File.WriteAllText(_path, "{ not json");

        Assert.Empty(Store().Ports);
    }

    public void Dispose() => File.Delete(_path);
}
