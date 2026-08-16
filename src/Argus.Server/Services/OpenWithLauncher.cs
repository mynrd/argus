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

        // A terminal cannot cd into a file, so it gets the containing folder instead.
        string folder = isDirectory ? target : (Path.GetDirectoryName(target) ?? target);

        var startInfo = (app ?? DefaultApp) switch
        {
            "vscode" => new ProcessStartInfo
            {
                FileName = "code",
                Arguments = Quote(target),
                UseShellExecute = true,
            },

            // WorkingDirectory rather than a Set-Location argument: no quoting to get wrong, and
            // the shell starts there even if the path contains characters it would otherwise eat.
            "powershell" => new ProcessStartInfo
            {
                FileName = "pwsh.exe",
                UseShellExecute = true,
                WorkingDirectory = folder,
            },

            "cmd" => new ProcessStartInfo
            {
                FileName = "cmd.exe",
                Arguments = "/K",
                UseShellExecute = true,
                WorkingDirectory = folder,
            },

            // claude is a CLI, so it needs a terminal to live in. -NoExit keeps the window up when
            // it exits, so an error message is still readable instead of flashing past.
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

    private static string Quote(string value) => $"\"{value}\"";
}
