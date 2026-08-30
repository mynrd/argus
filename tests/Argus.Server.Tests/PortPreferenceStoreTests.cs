using System.Text.Json;
using Argus.Server.Services;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

namespace Argus.Server.Tests;

public class PortPreferenceStoreTests : IDisposable
{
    private readonly string _path = Path.Combine(
        Path.GetTempPath(), $"argus-ports-{Guid.NewGuid():N}.json");

    private PortPreferenceStore Store() => new(
        NullLogger<PortPreferenceStore>.Instance,
        new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["Argus:PortPreferencesPath"] = _path })
            .Build());

    [Fact]
    public void A_favourite_survives_a_restart()
    {
        // The reason this is a file on the host at all: the phone you pick up next has never seen
        // this machine's localStorage.
        Store().SetFavourite(3000, favourite: true);

        Assert.Equal([3000], Store().Current.Favourites);
    }

    [Fact]
    public void A_hidden_port_survives_a_restart()
    {
        Store().SetHidden(445, hidden: true);

        Assert.Equal([445], Store().Current.Hidden);
    }

    [Fact]
    public void Unfavouriting_removes_it_from_the_file()
    {
        var store = Store();
        store.SetFavourite(3000, favourite: true);
        store.SetFavourite(3000, favourite: false);

        Assert.Empty(Store().Current.Favourites);
    }

    [Fact]
    public void Unhiding_removes_it_from_the_file()
    {
        var store = Store();
        store.SetHidden(445, hidden: true);
        store.SetHidden(445, hidden: false);

        Assert.Empty(Store().Current.Hidden);
    }

    [Fact]
    public void Favouriting_twice_leaves_one_entry()
    {
        var store = Store();
        store.SetFavourite(3000, favourite: true);

        Assert.Equal([3000], store.SetFavourite(3000, favourite: true).Favourites);
    }

    [Fact]
    public void Set_returns_the_list_as_it_stands_afterwards()
    {
        var store = Store();
        store.SetFavourite(3000, favourite: true);

        Assert.Equal([3000, 5227], store.SetFavourite(5227, favourite: true).Favourites);
    }

    [Fact]
    public void Hiding_a_favourite_unpins_it()
    {
        // Pinned to the top and struck off the page at once is a state neither button explains.
        var store = Store();
        store.SetFavourite(3000, favourite: true);

        var after = store.SetHidden(3000, hidden: true);

        Assert.Empty(after.Favourites);
        Assert.Equal([3000], after.Hidden);
    }

    [Fact]
    public void Favouriting_a_hidden_port_brings_it_back()
    {
        var store = Store();
        store.SetHidden(3000, hidden: true);

        var after = store.SetFavourite(3000, favourite: true);

        Assert.Equal([3000], after.Favourites);
        Assert.Empty(after.Hidden);
    }

    [Fact]
    public void Moving_a_port_between_the_lists_is_persisted()
    {
        // The clearing half of the swap has to reach the file, not just the in-memory set.
        var store = Store();
        store.SetFavourite(3000, favourite: true);
        store.SetHidden(3000, hidden: true);

        var reloaded = Store().Current;

        Assert.Empty(reloaded.Favourites);
        Assert.Equal([3000], reloaded.Hidden);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(65536)]
    public void Impossible_port_numbers_are_ignored(int port)
    {
        var store = Store();

        Assert.Empty(store.SetFavourite(port, favourite: true).Favourites);
        Assert.Empty(store.SetHidden(port, hidden: true).Hidden);
    }

    [Fact]
    public void A_missing_file_is_not_an_error()
    {
        Assert.Empty(Store().Current.Favourites);
        Assert.Empty(Store().Current.Hidden);
    }

    [Fact]
    public void A_corrupt_file_starts_empty_rather_than_throwing()
    {
        // A half-written file should cost you your choices, not the whole page.
        File.WriteAllText(_path, "{ not json");

        Assert.Empty(Store().Current.Favourites);
    }

    [Fact]
    public void A_file_from_the_version_that_saved_a_bare_array_starts_empty()
    {
        // What the favourites-only build wrote. Unreadable as the new shape, so it is dropped and
        // the next tap replaces it - which is the agreed trade rather than a migration.
        File.WriteAllText(_path, "[3000,5227]");

        var store = Store();
        Assert.Empty(store.Current.Favourites);

        store.SetFavourite(8080, favourite: true);
        Assert.Equal([8080], Store().Current.Favourites);
    }

    [Fact]
    public void A_hand_edited_file_listing_a_port_in_both_keeps_it_pinned()
    {
        File.WriteAllText(_path, """{"favorites":[3000],"hidden":[3000]}""");

        var loaded = Store().Current;

        Assert.Equal([3000], loaded.Favourites);
        Assert.Empty(loaded.Hidden);
    }

    [Fact]
    public void The_file_holds_both_lists_under_the_agreed_names()
    {
        var store = Store();
        store.SetFavourite(3000, favourite: true);
        store.SetHidden(445, hidden: true);

        using var document = JsonDocument.Parse(File.ReadAllText(_path));

        Assert.Equal([3000], document.RootElement.GetProperty("favorites").EnumerateArray().Select(e => e.GetInt32()));
        Assert.Equal([445], document.RootElement.GetProperty("hidden").EnumerateArray().Select(e => e.GetInt32()));
    }

    public void Dispose() => File.Delete(_path);
}
