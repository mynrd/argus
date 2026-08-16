namespace Argus.Server.Services;

public sealed record BrowseEntry(string Name, string Path, bool IsDirectory);

public sealed record BrowseListing(
    string Path,
    string Label,
    string? Parent,
    IReadOnlyList<BrowseEntry> Entries,
    string? Error = null);

/// <summary>
/// Directory listings of the host machine, backing the Browse dialog next to Run Application.
///
/// This exists because a browser file picker is the wrong tool here: it lists files on the device
/// holding the browser (your phone) and, by design, never reveals a filesystem path. Running a
/// program on the host needs a path on the host, so the listing is served from here.
///
/// Only directories and things that can actually be launched are listed - a full file manager
/// would bury the one .exe you came for.
/// </summary>
public static class HostFileBrowser
{
    private static readonly HashSet<string> RunnableExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".exe", ".lnk", ".bat", ".cmd", ".com", ".msi", ".ps1",
    };

    /// <param name="runnableOnly">
    /// True for the Run dialog's Browse, which only wants things it can launch. False for the
    /// Explorer page, which is a file manager and has to show every file.
    /// </param>
    public static BrowseListing List(string? path, bool runnableOnly = true)
    {
        if (string.IsNullOrWhiteSpace(path)) return Roots();

        string full;
        try
        {
            full = Path.GetFullPath(path.Trim());
        }
        catch (Exception ex)
        {
            return new BrowseListing(string.Empty, "This PC", null, [], ex.Message);
        }

        if (!Directory.Exists(full))
        {
            return new BrowseListing(string.Empty, "This PC", null, Roots().Entries, $"'{full}' is not a folder.");
        }

        var entries = new List<BrowseEntry>();
        string? error = null;

        try
        {
            var directory = new DirectoryInfo(full);

            foreach (var child in directory.EnumerateDirectories().OrderBy(d => d.Name, StringComparer.OrdinalIgnoreCase))
            {
                if (IsNoise(child.Attributes)) continue;
                entries.Add(new BrowseEntry(child.Name, child.FullName, true));
            }

            foreach (var file in directory.EnumerateFiles().OrderBy(f => f.Name, StringComparer.OrdinalIgnoreCase))
            {
                if (IsNoise(file.Attributes)) continue;
                if (runnableOnly && !RunnableExtensions.Contains(file.Extension)) continue;
                entries.Add(new BrowseEntry(file.Name, file.FullName, false));
            }
        }
        catch (UnauthorizedAccessException)
        {
            error = "No permission to read that folder.";
        }
        catch (Exception ex)
        {
            error = ex.Message;
        }

        // Directory.GetParent returns null at a drive root, which is the signal the UI needs to
        // offer "This PC" instead of a parent folder.
        string? parent = Directory.GetParent(full)?.FullName;

        return new BrowseListing(full, full, parent, entries, error);
    }

    private static BrowseListing Roots()
    {
        var entries = new List<BrowseEntry>();

        foreach (var folder in new[]
                 {
                     Environment.SpecialFolder.DesktopDirectory,
                     Environment.SpecialFolder.UserProfile,
                     Environment.SpecialFolder.ProgramFiles,
                     Environment.SpecialFolder.ProgramFilesX86,
                     Environment.SpecialFolder.CommonStartMenu,
                     Environment.SpecialFolder.Windows,
                 })
        {
            string path = Environment.GetFolderPath(folder);
            if (path.Length > 0 && Directory.Exists(path))
            {
                entries.Add(new BrowseEntry(Label(folder), path, true));
            }
        }

        foreach (var drive in DriveInfo.GetDrives())
        {
            if (!drive.IsReady) continue;
            entries.Add(new BrowseEntry(drive.Name, drive.RootDirectory.FullName, true));
        }

        return new BrowseListing(string.Empty, "This PC", null, entries);
    }

    private static string Label(Environment.SpecialFolder folder) => folder switch
    {
        Environment.SpecialFolder.DesktopDirectory => "Desktop",
        Environment.SpecialFolder.UserProfile => "Home",
        Environment.SpecialFolder.ProgramFiles => "Program Files",
        Environment.SpecialFolder.ProgramFilesX86 => "Program Files (x86)",
        Environment.SpecialFolder.CommonStartMenu => "Start Menu",
        Environment.SpecialFolder.Windows => "Windows",
        _ => folder.ToString(),
    };

    private static bool IsNoise(FileAttributes attributes) =>
        attributes.HasFlag(FileAttributes.Hidden) || attributes.HasFlag(FileAttributes.System);
}
