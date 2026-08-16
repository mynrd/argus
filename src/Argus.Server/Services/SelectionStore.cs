using System.Text.Json;

namespace Argus.Server.Services;

/// <summary>
/// Remembers which apps were picked in Settings, keyed by <see cref="Windows.WindowInfo.MatchKey"/>
/// rather than HWND so a selection survives both an Argus restart and the target app being closed
/// and reopened.
/// </summary>
public sealed class SelectionStore
{
    private readonly string _path;
    private readonly ILogger<SelectionStore> _log;
    private readonly Lock _gate = new();
    private HashSet<string> _selected;

    public SelectionStore(ILogger<SelectionStore> log, IConfiguration config)
    {
        _log = log;
        _path = config["Argus:SelectionPath"] ?? DefaultPath();
        _selected = Load();
    }

    private static string DefaultPath() => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Argus",
        "selection.json");

    public IReadOnlyCollection<string> Selected
    {
        get { lock (_gate) return _selected.ToArray(); }
    }

    public bool IsSelected(string matchKey)
    {
        lock (_gate) return _selected.Contains(matchKey);
    }

    public void Set(IEnumerable<string> matchKeys)
    {
        lock (_gate)
        {
            _selected = new HashSet<string>(matchKeys, StringComparer.OrdinalIgnoreCase);
            Save();
        }
    }

    public void Add(string matchKey)
    {
        lock (_gate)
        {
            if (_selected.Add(matchKey)) Save();
        }
    }

    public void Remove(string matchKey)
    {
        lock (_gate)
        {
            if (_selected.Remove(matchKey)) Save();
        }
    }

    private HashSet<string> Load()
    {
        try
        {
            if (!File.Exists(_path)) return new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            var json = File.ReadAllText(_path);
            var keys = JsonSerializer.Deserialize<string[]>(json) ?? [];
            return new HashSet<string>(keys, StringComparer.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            _log.LogWarning(ex, "Could not read the saved selection at {Path}; starting empty", _path);
            return new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        }
    }

    private void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            File.WriteAllText(_path, JsonSerializer.Serialize(_selected.ToArray()));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _log.LogWarning(ex, "Could not persist the selection to {Path}", _path);
        }
    }
}
