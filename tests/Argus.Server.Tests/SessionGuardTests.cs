using Argus.Server.Security;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;

namespace Argus.Server.Tests;

public class SessionGuardTests
{
    [Fact]
    public void A_configured_password_makes_the_lock_required()
    {
        Assert.True(Guard("hunter2").Required);
        Assert.False(Guard(null).Required);
        Assert.False(Guard("").Required);
    }

    [Fact]
    public void Only_the_configured_password_matches()
    {
        var guard = Guard("hunter2");

        Assert.True(guard.PasswordMatches("hunter2"));
        Assert.False(guard.PasswordMatches("hunter3"));
        Assert.False(guard.PasswordMatches("hunter"));
        Assert.False(guard.PasswordMatches("hunter2 "));
        Assert.False(guard.PasswordMatches(""));
        Assert.False(guard.PasswordMatches(null));
    }

    [Fact]
    public void An_unconfigured_agent_stays_open()
    {
        var guard = Guard(null);

        // No password means the lock was never asked for, so nothing may be turned away - a typo in
        // appsettings.json must not lock the machine's owner out of their own agent.
        Assert.True(guard.PasswordMatches("anything"));
        Assert.True(guard.IsOpen(null));
        Assert.True(guard.IsOpen("not-a-real-token"));
    }

    [Fact]
    public void Only_a_minted_token_opens_the_gate()
    {
        var guard = Guard("hunter2");
        string token = guard.Open();

        Assert.True(guard.IsOpen(token));
        Assert.False(guard.IsOpen(null));
        Assert.False(guard.IsOpen(""));
        Assert.False(guard.IsOpen("forged"));
        Assert.False(guard.IsOpen(token + "x"));
    }

    [Fact]
    public void Tokens_are_unique_per_unlock()
    {
        var guard = Guard("hunter2");

        var tokens = Enumerable.Range(0, 50).Select(_ => guard.Open()).ToList();

        Assert.Equal(tokens.Count, tokens.Distinct(StringComparer.Ordinal).Count());
    }

    [Fact]
    public void Closing_a_session_revokes_that_token_only()
    {
        var guard = Guard("hunter2");
        string phone = guard.Open();
        string desktop = guard.Open();

        guard.Close(phone);

        // Locking the phone must not sign the desktop out - and the phone's cookie is worthless
        // even if it is replayed, which is the whole point of tracking sessions server side.
        Assert.False(guard.IsOpen(phone));
        Assert.True(guard.IsOpen(desktop));
    }

    [Fact]
    public void Repeated_wrong_passwords_start_a_cooldown()
    {
        var guard = Guard("hunter2");

        Assert.Null(guard.Cooldown("10.0.0.5"));

        for (int i = 0; i < 5; i++) guard.RecordFailure("10.0.0.5");

        Assert.NotNull(guard.Cooldown("10.0.0.5"));
        // The penalty is per client, or one guesser would lock everyone else out.
        Assert.Null(guard.Cooldown("10.0.0.6"));
    }

    [Fact]
    public void A_successful_unlock_clears_the_cooldown()
    {
        var guard = Guard("hunter2");
        for (int i = 0; i < 5; i++) guard.RecordFailure("10.0.0.5");

        guard.ClearFailures("10.0.0.5");

        Assert.Null(guard.Cooldown("10.0.0.5"));
    }

    [Theory]
    // Everything that can see or touch the host.
    [InlineData("/api/windows", true)]
    [InlineData("/api/health", true)]
    [InlineData("/api/windows/123/frame", true)]
    [InlineData("/hubs/argus", true)]
    [InlineData("/hubs/argus/negotiate", true)]
    [InlineData("/ws/frames", true)]
    // The lock's own endpoints, which necessarily answer while locked.
    [InlineData("/api/session", false)]
    [InlineData("/api/unlock", false)]
    [InlineData("/api/lock", false)]
    // The shell has to load in order to render the lock screen at all.
    [InlineData("/", false)]
    [InlineData("/index.html", false)]
    [InlineData("/main.js", false)]
    [InlineData("/dashboard", false)]
    public void The_gate_covers_the_host_and_leaves_the_shell_alone(string path, bool guarded)
    {
        Assert.Equal(guarded, SessionEndpoints.IsGuarded(new PathString(path)));
    }

    private static SessionGuard Guard(string? password)
    {
        // The env var wins over configuration, so it has to be cleared or a developer's own
        // ARGUS_PASSWORD would decide what these tests are asserting against.
        Environment.SetEnvironmentVariable("ARGUS_PASSWORD", null);

        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["Argus:Password"] = password })
            .Build();

        return new SessionGuard(config);
    }
}
