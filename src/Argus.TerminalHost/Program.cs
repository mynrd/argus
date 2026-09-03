using System.Security.Cryptography;
using System.Text.Json;

namespace Argus.TerminalHost;

/// <summary>
/// The terminal host daemon: a small, long-lived process whose only job is to own the pseudo
/// consoles that would otherwise live inside Argus.Server.
///
/// That split is the entire feature. A terminal opened from the browser has to survive the server
/// being restarted, rebuilt, or crashing - so the thing holding the shell cannot be the server. It
/// is spawned detached with no console and no window (see Argus.Server's TerminalHostClient), and
/// it outlives whoever started it. A terminal ends when it is killed, when its shell exits, when
/// this process is told to shut down, or when the machine restarts.
///
/// Nothing here ever writes to stdout - there is no console attached to read it - so everything
/// worth knowing goes to stderr, and the exit code says whether starting failed.
///
/// Discovery: a client finds this daemon through the info file next to Argus's other state,
/// <c>%LOCALAPPDATA%\Argus\terminal-host.json</c>, written only after the pipe is listening so a
/// client never reads a pipe name nobody is answering on yet.
/// </summary>
public static class Program
{
    /// <summary>
    /// One daemon per session. A random pipe name means two would never collide - they would both
    /// listen, both write the info file, and the loser would sit there holding terminals nothing
    /// could ever find again.
    /// </summary>
    private const string SingleInstanceMutex = @"Local\Argus.TerminalHost";

    public static async Task<int> Main()
    {
        using var only = new Mutex(initiallyOwned: false, SingleInstanceMutex, out _);
        bool mine;
        try
        {
            mine = only.WaitOne(TimeSpan.Zero);
        }
        catch (AbandonedMutexException)
        {
            // A previous daemon died without releasing it. The name is ours now.
            mine = true;
        }

        if (!mine) return 0; // another daemon is already up; it is the one clients will find

        try
        {
            string directory = StateDirectory();
            Directory.CreateDirectory(directory);

            string token = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
            string pipeName = $"Argus.Terminals.{Convert.ToHexString(RandomNumberGenerator.GetBytes(16))}";

            using var registry = new TerminalRegistry();
            using var stopping = new CancellationTokenSource();

            var server = new HostServer(registry, pipeName, token, onShutdown: () => stopping.Cancel());
            var listening = server.RunAsync(stopping.Token);

            File.WriteAllText(InfoPath(), JsonSerializer.Serialize(new
            {
                version = TerminalProtocol.Version,
                pipe = pipeName,
                token,
                pid = Environment.ProcessId,
                startedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            }, new JsonSerializerOptions { WriteIndented = true }));

            try
            {
                await listening;
            }
            catch (OperationCanceledException)
            {
                // `shutdown` asked for this.
            }

            // The registry's Dispose takes every terminal with it, which is the promise the
            // shutdown op makes to whoever called it.
            TryDeleteInfoFile();
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"terminal-host: {ex}");
            return 1;
        }
        finally
        {
            only.ReleaseMutex();
        }
    }

    /// <summary>
    /// Where Argus keeps state it writes outside a project - the same folder as ports.json, so
    /// everything this app remembers lives in one place.
    ///
    /// Duplicated by Argus.Server's TerminalHostClient on purpose: that project references this
    /// one for the .exe only (ReferenceOutputAssembly=false), so there is no shared assembly for
    /// a helper to live in.
    /// </summary>
    internal static string StateDirectory() => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Argus");

    internal static string InfoPath() => Path.Combine(StateDirectory(), "terminal-host.json");

    private static void TryDeleteInfoFile()
    {
        try
        {
            File.Delete(InfoPath());
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Already gone, or never written. A stale file is handled by the client pinging it.
        }
    }
}
