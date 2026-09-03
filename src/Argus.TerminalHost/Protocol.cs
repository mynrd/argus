using System.Text.Json;
using System.Text.Json.Serialization;

namespace Argus.TerminalHost;

/// <summary>
/// The wire format between the daemon and Argus.Server: newline-delimited JSON over a named pipe.
///
/// Bumped whenever the shape below changes incompatibly. Both sides compare it on `ping`, and a
/// mismatch makes the client retire the old daemon and start a fresh one rather than talk a
/// protocol it does not understand - the daemon outlives the server, so the two really can be
/// different builds.
/// </summary>
public static class TerminalProtocol
{
    public const int Version = 1;

    /// <summary>The env var planted in every terminal, and what the straggler scan matches on.</summary>
    public const string MarkerEnv = "ARGUS_TERMINAL";

    public static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };
}

/// <summary>
/// One thing that happened to a terminal, relayed to everyone attached.
///
/// <c>type</c> is <c>replay</c> (the buffer as it stood at attach), <c>data</c> (live output) or
/// <c>exit</c>. Listeners see exactly one exit, ever.
/// </summary>
public sealed record TerminalEvent(
    [property: JsonPropertyName("type")] string Type,
    [property: JsonPropertyName("data")] string? Data = null,
    [property: JsonPropertyName("exitCode")] int? ExitCode = null);

/// <summary>What a terminal looks like to the page. No handles, no buffer - just the row.</summary>
public sealed record TerminalView(
    string TerminalId,
    int? Pid,
    bool Running,
    string Command,
    string Cwd,
    int Cols,
    int Rows,
    long StartedAt,
    int? ExitCode,
    string? Name);
