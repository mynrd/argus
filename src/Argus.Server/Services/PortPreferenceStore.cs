using System.Text.Json;
using System.Text.Json.Serialization;

namespace Argus.Server.Services;

/// <summary>
/// The two per-port choices, and the shape of the file they are saved in.
///
/// Mutually exclusive by construction in <see cref="PortPreferenceStore"/>: a port that is pinned
/// to the top of the page and also struck off it is a state neither button could explain.
/// </summary>
public sealed record PortPreferences(
    [property: JsonPropertyName("favorites")] int[] Favourites,
    [property: JsonPropertyName("hidden")] int[] Hidden)
{
    public static PortPreferences Empty { get; } = new([], []);
}

/// <summary>
/// The ports pinned to the top of the Ports page and the ones kept off it, saved on the host
/// rather than in the browser.
///
/// On the host on purpose: the point of this app is reaching one machine from whatever device is
/// to hand, and a choice kept in localStorage would be missing from the phone you pick up next.
/// Keyed by port number alone - a favourite is "whatever is on 3000", and it has to survive the
/// service behind it being restarted under a new pid.
/// </summary>
public sealed class PortPreferenceStore
{
    private readonly string _path;
    private readonly ILogger<PortPreferenceStore> _log;
    private readonly Lock _gate = new();
    private readonly HashSet<int> _favourites;
    private readonly HashSet<int> _hidden;

    public PortPreferenceStore(ILogger<PortPreferenceStore> log, IConfiguration config)
    {
        _log = log;
        _path = config["Argus:PortPreferencesPath"] ?? DefaultPath();
        (_favourites, _hidden) = Load();
    }

    private static string DefaultPath() => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Argus",
        "ports.json");

    /// <summary>Both lists, read together so they cannot be seen mid-change disagreeing.</summary>
    public PortPreferences Current
    {
        get { lock (_gate) return Snapshot(); }
    }

    /// <summary>Pins a port or unpins it. Pinning it un-hides it. Returns both lists afterwards.</summary>
    public PortPreferences SetFavourite(int port, bool favourite)
    {
        if (!IsPort(port)) return Current;

        lock (_gate)
        {
            bool changed = favourite
                ? _favourites.Add(port) | _hidden.Remove(port)
                : _favourites.Remove(port);

            if (changed) Save();
            return Snapshot();
        }
    }

    /// <summary>Hides a port or brings it back. Hiding it unpins it. Returns both lists afterwards.</summary>
    public PortPreferences SetHidden(int port, bool hidden)
    {
        if (!IsPort(port)) return Current;

        lock (_gate)
        {
            bool changed = hidden
                ? _hidden.Add(port) | _favourites.Remove(port)
                : _hidden.Remove(port);

            if (changed) Save();
            return Snapshot();
        }
    }

    private static bool IsPort(int port) => port is > 0 and <= 65535;

    private PortPreferences Snapshot() => new([.. _favourites.Order()], [.. _hidden.Order()]);

    /// <summary>
    /// Reads the file, or starts empty if it says anything this version cannot read - including a
    /// file written by the version that saved a bare array of favourites. Losing the list is a
    /// nuisance; refusing to start the page over it is worse, and the next tap rewrites the file.
    /// </summary>
    private (HashSet<int> Favourites, HashSet<int> Hidden) Load()
    {
        try
        {
            if (!File.Exists(_path)) return ([], []);

            var saved = JsonSerializer.Deserialize<PortPreferences>(File.ReadAllText(_path));
            if (saved is null) return ([], []);

            var favourites = Ports(saved.Favourites);
            var hidden = Ports(saved.Hidden);

            // A hand-edited file could list a port in both. Pinned wins - it is the one that puts
            // something on screen, so the result is visible rather than silently absent.
            hidden.ExceptWith(favourites);

            return (favourites, hidden);
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            _log.LogWarning(ex, "Could not read the port preferences at {Path}; starting empty", _path);
            return ([], []);
        }
    }

    private static HashSet<int> Ports(int[]? saved) => [.. (saved ?? []).Where(IsPort)];

    private void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            File.WriteAllText(_path, JsonSerializer.Serialize(
                Snapshot(),
                new JsonSerializerOptions { WriteIndented = true }));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _log.LogWarning(ex, "Could not persist the port preferences to {Path}", _path);
        }
    }
}
