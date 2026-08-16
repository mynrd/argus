using System.ComponentModel;
using System.Diagnostics;

namespace Argus.Server.Services;

public sealed record LaunchResult(bool Started, string? Reason = null);

/// <summary>
/// Runs a command the way the Windows Run dialog does.
///
/// The important part is UseShellExecute: it is what makes bare names like "chrome" resolve at all.
/// CreateProcess only searches PATH, and Chrome is not on it - ShellExecute additionally consults
/// the App Paths registry key, which is exactly how the Run dialog finds it. The same path opens
/// documents, folders and URLs with whatever is registered for them.
///
/// The launched process inherits this one's session, so it lands on the interactive desktop and
/// becomes capturable like any other window.
/// </summary>
public sealed class ApplicationLauncher
{
    private const int ERROR_FILE_NOT_FOUND = 2;
    private const int ERROR_CANCELLED = 1223;

    private readonly ILogger<ApplicationLauncher> _log;

    public ApplicationLauncher(ILogger<ApplicationLauncher> log) => _log = log;

    public LaunchResult Run(string? command)
    {
        string text = command?.Trim() ?? string.Empty;
        if (text.Length == 0) return new LaunchResult(false, "Type the name of a program to run.");

        var (file, arguments) = Split(text);

        try
        {
            using var process = Process.Start(new ProcessStartInfo
            {
                FileName = file,
                Arguments = arguments,
                UseShellExecute = true,
                WorkingDirectory = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            });

            _log.LogInformation("Ran '{Command}'", text);
            return new LaunchResult(true);
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == ERROR_FILE_NOT_FOUND)
        {
            // Same wording as the Run dialog, because it is the same mistake: a name that does not
            // resolve to anything ShellExecute knows about.
            _log.LogWarning("Could not find '{File}'", file);
            return new LaunchResult(false, $"Windows cannot find '{file}'. Check the name and try again.");
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == ERROR_CANCELLED)
        {
            // The UAC prompt appears on the host desktop, not in the browser, so say where it went.
            _log.LogWarning("Elevation for '{File}' was declined", file);
            return new LaunchResult(false, $"'{file}' needs elevation and the prompt on the desktop was declined.");
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Could not run '{Command}'", text);
            return new LaunchResult(false, ex.Message);
        }
    }

    /// <summary>
    /// Splits "chrome https://example.com" into program and arguments. A quoted first token keeps
    /// paths containing spaces intact, which is the only case bare splitting gets wrong.
    /// </summary>
    internal static (string File, string Arguments) Split(string text)
    {
        if (text.StartsWith('"'))
        {
            int close = text.IndexOf('"', 1);
            if (close > 0) return (text[1..close], text[(close + 1)..].Trim());
        }

        int space = text.IndexOf(' ');
        return space < 0 ? (text, string.Empty) : (text[..space], text[(space + 1)..].Trim());
    }
}
