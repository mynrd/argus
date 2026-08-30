using System.Text.Json;

namespace Argus.Server.Services;

/// <summary>
/// The ports pinned to the top of the Ports page, saved on the host rather than in the browser.
///
/// On the host on purpose: the point of this app is reaching one machine from whatever device is
/// to hand, and a favourite kept in localStorage would be missing from the phone you pick up next.
/// Keyed by port number alone - a favourite is "whatever is on 3000", and it has to survive the
/// service behind it being restarted under a new pid.
/// </summary>
public sealed class FavouritePortStore
{
    private readonly string _path;
    private readonly ILogger<FavouritePortStore> _log;
    private readonly Lock _gate = new();
    private HashSet<int> _ports;

    public FavouritePortStore(ILogger<FavouritePortStore> log, IConfiguration config)
    {
        _log = log;
        _path = config["Argus:FavouritePortsPath"] ?? DefaultPath();
        _ports = Load();
    }

    private static string DefaultPath() => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Argus",
        "favourite-ports.json");

    public IReadOnlyCollection<int> Ports
    {
        get { lock (_gate) return _ports.ToArray(); }
    }

    /// <summary>Adds or removes one port. Returns what the list is afterwards.</summary>
    public IReadOnlyCollection<int> Set(int port, bool favourite)
    {
        if (port is < 1 or > 65535) return Ports;

        lock (_gate)
        {
            bool changed = favourite ? _ports.Add(port) : _ports.Remove(port);
            if (changed) Save();
            return _ports.ToArray();
        }
    }

    private HashSet<int> Load()
    {
        try
        {
            if (!File.Exists(_path)) return [];

            var ports = JsonSerializer.Deserialize<int[]>(File.ReadAllText(_path)) ?? [];
            return [.. ports.Where(p => p is > 0 and <= 65535)];
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            _log.LogWarning(ex, "Could not read the favourite ports at {Path}; starting empty", _path);
            return [];
        }
    }

    private void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            File.WriteAllText(_path, JsonSerializer.Serialize(_ports.Order().ToArray()));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _log.LogWarning(ex, "Could not persist the favourite ports to {Path}", _path);
        }
    }
}
