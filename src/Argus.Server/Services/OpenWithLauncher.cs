using System.ComponentModel;
using System.Diagnostics;

namespace Argus.Server.Services;

/// <summary>One app the Explorer page can hand a path to.</summary>
public sealed record OpenWithApp(string Key, string Label, bool HandlesFolders, bool HandlesFiles);

/// <summary>
/// Opens a file or folder on the host in a chosen app.
///
/// Deliberately not Windows' own "Open with" dialog (rundll32 shell32.dll,OpenAs_RunDLL): that
/// draws on the host desktop, so from a tablet you would be picking an app in a dialog you cannot
/// see. The list here is fixed and rendered in the browser instead.
/// </summary>
public sealed class OpenWithLauncher
{
    private const int ERROR_FILE_NOT_FOUND = 2;
    private const int ERROR_CANCELLED = 1223;

    public const string DefaultApp = "default";

    /// <summary>
    /// What the UI offers. A terminal opens *at* a folder, so for a file it opens at the file's
    /// parent - which is what you want when the next thing you type is a command about that file.
    /// The exception is a terminal handed a script it can run: PowerShell on a .ps1, Command
    /// Prompt on a .bat or .cmd. Those run it, because that is what picking them meant.
    /// </summary>
    public static readonly IReadOnlyList<OpenWithApp> Apps =
    [
        new("default", "Default app", false, true),
        new("vscode", "VS Code", true, true),
        new("powershell", "PowerShell", true, true),
        new("cmd", "Command Prompt", true, true),
        new("claude", "Claude", true, true),
        new("explorer", "File Explorer", true, true),
    ];

    private readonly ILogger<OpenWithLauncher> _log;

    public OpenWithLauncher(ILogger<OpenWithLauncher> log) => _log = log;

    public LaunchResult Open(string? path, string? app)
    {
        string target = path?.Trim() ?? string.Empty;
        if (target.Length == 0) return new LaunchResult(false, "No path given.");

        bool isDirectory = Directory.Exists(target);
        bool isFile = File.Exists(target);
        if (!isDirectory && !isFile) return new LaunchResult(false, $"'{target}' no longer exists.");

        var startInfo = Plan(target, isDirectory, app ?? DefaultApp);

        try
        {
            using var process = Process.Start(startInfo);
            _log.LogInformation("Opened '{Path}' with {App}", target, app ?? DefaultApp);
            return new LaunchResult(true);
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == ERROR_FILE_NOT_FOUND)
        {
            _log.LogWarning("Could not open '{Path}' with {App}: not found", target, app);
            return new LaunchResult(false, $"Could not find the app for '{app}' on this machine.");
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == ERROR_CANCELLED)
        {
            return new LaunchResult(false, "The prompt on the desktop was declined.");
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Could not open '{Path}' with {App}", target, app);
            return new LaunchResult(false, ex.Message);
        }
    }

    /// <summary>
    /// What to start, for one app and one target. Separated from starting it because the whole
    /// difficulty here is the argument string - a terminal handed the wrong one silently opens a
    /// prompt in the right folder and does nothing, which is what selecting PowerShell on run.ps1
    /// used to do. Takes isDirectory rather than checking the disk, so it is testable on a path
    /// that does not exist.
    /// </summary>
    internal static ProcessStartInfo Plan(string target, bool isDirectory, string app)
    {
        // A terminal cannot cd into a file, so it gets the containing folder instead.
        string folder = isDirectory ? target : (Path.GetDirectoryName(target) ?? target);

        return app switch
        {
            "vscode" => new ProcessStartInfo
            {
                FileName = "code",
                Arguments = Quote(target),
                UseShellExecute = true,
            },

            // A script gets run, anything else gets a shell sitting next to it. Picking PowerShell
            // on run.ps1 and getting a bare prompt in the right folder is not what the tap meant.
            //
            // -NoExit so the window survives the script: without it a script that fails prints its
            // error and closes, which from a phone is indistinguishable from nothing happening.
            // WorkingDirectory rather than a Set-Location argument: no quoting to get wrong, and
            // the shell starts there even if the path contains characters it would otherwise eat.
            "powershell" => new ProcessStartInfo
            {
                FileName = "pwsh.exe",
                Arguments = IsScript(target, isDirectory, PowerShellScripts)
                    ? $"-NoExit -File {Quote(target)}"
                    : string.Empty,
                UseShellExecute = true,
                WorkingDirectory = folder,
            },

            // The same deal for .bat and .cmd, and /K keeps the window up either way.
            //
            // The doubled quotes and /S are cmd's documented escape hatch: without /S it keeps the
            // quotes only if the path holds no &<>()@^| character, and a folder called "R&D" would
            // otherwise have its quotes stripped and the command cut in half. /S makes cmd strip
            // exactly the outer pair and take the rest literally.
            "cmd" => new ProcessStartInfo
            {
                FileName = "cmd.exe",
                Arguments = IsScript(target, isDirectory, BatchScripts)
                    ? $"/S /K {Quote(Quote(target))}"
                    : "/K",
                UseShellExecute = true,
                WorkingDirectory = folder,
            },

            // claude is a CLI, so it needs a terminal to live in. -NoExit keeps the window up when
            // it exits, so an error message is still readable instead of flashing past. It works on
            // a folder rather than a file, so a file only decides which folder.
            "claude" => new ProcessStartInfo
            {
                FileName = "pwsh.exe",
                Arguments = "-NoExit -Command claude",
                UseShellExecute = true,
                WorkingDirectory = folder,
            },

            // /select, highlights the file inside its folder rather than trying to "run" it.
            "explorer" => new ProcessStartInfo
            {
                FileName = "explorer.exe",
                Arguments = isDirectory ? Quote(target) : $"/select,{Quote(target)}",
                UseShellExecute = true,
            },

            _ => new ProcessStartInfo
            {
                FileName = target,
                UseShellExecute = true,
                WorkingDirectory = folder,
            },
        };
    }

    /// <summary>
    /// Scripts pwsh will take with -File. Only .ps1 - .psm1 is a module and .psd1 is a manifest,
    /// and -File on either fails in a way that is worse than opening a prompt.
    /// </summary>
    private static readonly string[] PowerShellScripts = [".ps1"];

    private static readonly string[] BatchScripts = [".bat", ".cmd"];

    private static bool IsScript(string target, bool isDirectory, string[] extensions) =>
        !isDirectory
        && extensions.Contains(Path.GetExtension(target), StringComparer.OrdinalIgnoreCase);

    private static string Quote(string value) => $"\"{value}\"";
}
