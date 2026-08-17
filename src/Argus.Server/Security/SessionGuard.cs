using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;

namespace Argus.Server.Security;

/// <summary>
/// The single gate in front of everything Argus can do to the host machine.
///
/// It has to be enforced on the server rather than in the Angular app, because the interesting
/// surface is not the page: /hubs/argus injects real keystrokes and mouse clicks into the desktop
/// and /ws/frames streams it back. A lock screen that only hid the UI would leave both of those
/// answering anyone who can reach the port.
///
/// Sessions live in memory only, so restarting the agent locks every viewer out again. That is the
/// desired behaviour here - the agent restarting means the machine's state changed under them.
/// </summary>
public sealed class SessionGuard
{
    public const string CookieName = "argus.session";

    /// <summary>How long an unlocked session survives without being used.</summary>
    private static readonly TimeSpan Lifetime = TimeSpan.FromHours(12);

    /// <summary>Wrong passwords from one address before it is made to wait.</summary>
    private const int MaxAttempts = 5;

    private static readonly TimeSpan Penalty = TimeSpan.FromSeconds(30);

    private readonly ConcurrentDictionary<string, DateTimeOffset> _sessions = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, Attempts> _failures = new(StringComparer.Ordinal);
    private readonly byte[] _password;

    public SessionGuard(IConfiguration config)
    {
        // The env var wins so the password can be kept out of appsettings.json, which is tracked
        // by git - anything written to that file is in the history for good.
        string configured = Environment.GetEnvironmentVariable("ARGUS_PASSWORD")
            ?? config["Argus:Password"]
            ?? string.Empty;

        _password = Encoding.UTF8.GetBytes(configured);
    }

    /// <summary>
    /// False when no password is configured, which leaves the server open exactly as it was before
    /// there was a lock at all. Deliberate: a blank password must not be a password nobody can
    /// guess, or a typo in appsettings.json would lock the machine's owner out of their own agent.
    /// </summary>
    public bool Required => _password.Length > 0;

    public bool PasswordMatches(string? candidate)
    {
        if (!Required) return true;
        if (string.IsNullOrEmpty(candidate)) return false;

        // Fixed-time compare: a plain == would return faster the earlier the first wrong character
        // is, which is enough to recover the password one character at a time over a LAN.
        return CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(candidate), _password);
    }

    /// <summary>Mints a session token. The caller puts it in the cookie.</summary>
    public string Open()
    {
        var now = DateTimeOffset.UtcNow;
        foreach (var (token, expiry) in _sessions)
        {
            if (expiry <= now) _sessions.TryRemove(token, out _);
        }

        string fresh = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
        _sessions[fresh] = now + Lifetime;
        return fresh;
    }

    /// <summary>True if this token is a live session. Touching it slides the expiry forward.</summary>
    public bool IsOpen(string? token)
    {
        if (!Required) return true;
        if (string.IsNullOrEmpty(token)) return false;
        if (!_sessions.TryGetValue(token, out var expiry)) return false;

        if (expiry <= DateTimeOffset.UtcNow)
        {
            _sessions.TryRemove(token, out _);
            return false;
        }

        _sessions[token] = DateTimeOffset.UtcNow + Lifetime;
        return true;
    }

    public void Close(string? token)
    {
        if (!string.IsNullOrEmpty(token)) _sessions.TryRemove(token, out _);
    }

    /// <summary>How long this client must wait before guessing again, or null if it may try now.</summary>
    public TimeSpan? Cooldown(string client)
    {
        if (!_failures.TryGetValue(client, out var attempts)) return null;

        var remaining = attempts.Until - DateTimeOffset.UtcNow;
        return remaining > TimeSpan.Zero ? remaining : null;
    }

    /// <summary>
    /// Counts a wrong password and starts a cooldown every <see cref="MaxAttempts"/> of them. A
    /// short password is only worth anything if guesses cost wall-clock time.
    /// </summary>
    public void RecordFailure(string client) => _failures.AddOrUpdate(
        client,
        _ => new Attempts(1, DateTimeOffset.MinValue),
        (_, current) =>
        {
            int count = current.Count + 1;
            return count >= MaxAttempts
                ? new Attempts(0, DateTimeOffset.UtcNow + Penalty)
                : new Attempts(count, current.Until);
        });

    public void ClearFailures(string client) => _failures.TryRemove(client, out _);

    private sealed record Attempts(int Count, DateTimeOffset Until);
}
