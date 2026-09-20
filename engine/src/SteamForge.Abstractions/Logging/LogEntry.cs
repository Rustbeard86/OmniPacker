namespace SteamForge.Abstractions.Logging;

/// <summary>Severity of a broadcast log line, mirrored to the admin console.</summary>
public enum LogLevel
{
    Debug,
    Info,
    Warning,
    Error,
}

/// <summary>
/// A single line in the live console stream. Immutable so it can be safely
/// fanned out to any number of admin UI subscribers.
/// </summary>
/// <param name="Timestamp">UTC time the line was produced.</param>
/// <param name="Level">Severity.</param>
/// <param name="Source">Short origin tag, e.g. "steam", "queue", "gofile".</param>
/// <param name="Message">Already-redacted, human-readable text.</param>
public sealed record LogEntry(
    DateTimeOffset Timestamp,
    LogLevel Level,
    string Source,
    string Message)
{
    public static LogEntry Now(LogLevel level, string source, string message) =>
        new(DateTimeOffset.UtcNow, level, source, message);
}
