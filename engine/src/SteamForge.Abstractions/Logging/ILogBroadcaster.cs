namespace SteamForge.Abstractions.Logging;

/// <summary>
/// Central fan-out for console log lines. The engine and plugins publish here;
/// the Blazor admin console subscribes to <see cref="Published"/> for the live
/// stream and reads <see cref="Snapshot"/> for backfill on connect.
/// </summary>
public interface ILogBroadcaster
{
    /// <summary>Raised for every published line. Handlers must not throw.</summary>
    event Action<LogEntry>? Published;

    /// <summary>Publish a line to all subscribers and the recent-lines buffer.</summary>
    void Publish(LogEntry entry);

    /// <summary>Recent lines (bounded ring buffer), oldest first, for UI backfill.</summary>
    IReadOnlyList<LogEntry> Snapshot();
}

/// <summary>Convenience helpers over <see cref="ILogBroadcaster"/>.</summary>
public static class LogBroadcasterExtensions
{
    public static void Info(this ILogBroadcaster log, string source, string message) =>
        log.Publish(LogEntry.Now(LogLevel.Info, source, message));

    public static void Warn(this ILogBroadcaster log, string source, string message) =>
        log.Publish(LogEntry.Now(LogLevel.Warning, source, message));

    public static void Error(this ILogBroadcaster log, string source, string message) =>
        log.Publish(LogEntry.Now(LogLevel.Error, source, message));

    public static void Debug(this ILogBroadcaster log, string source, string message) =>
        log.Publish(LogEntry.Now(LogLevel.Debug, source, message));
}
