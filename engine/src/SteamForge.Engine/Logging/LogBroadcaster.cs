using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using SteamForge.Abstractions.Logging;
using AppLogLevel = SteamForge.Abstractions.Logging.LogLevel;

namespace SteamForge.Engine.Logging;

/// <summary>
/// Thread-safe <see cref="ILogBroadcaster"/> with a bounded recent-lines buffer.
/// Every published line is also mirrored to the standard <see cref="ILogger"/>
/// pipeline so container stdout captures the same stream the admin console shows.
/// </summary>
public sealed class LogBroadcaster : ILogBroadcaster
{
    private const int MaxBuffered = 2000;

    private readonly ConcurrentQueue<LogEntry> _buffer = new();
    private readonly ILogger<LogBroadcaster> _logger;
    private int _count;

    public LogBroadcaster(ILogger<LogBroadcaster> logger) => _logger = logger;

    public event Action<LogEntry>? Published;

    public void Publish(LogEntry entry)
    {
        _buffer.Enqueue(entry);
        if (Interlocked.Increment(ref _count) > MaxBuffered && _buffer.TryDequeue(out _))
        {
            Interlocked.Decrement(ref _count);
        }

        _logger.Log(ToMsLevel(entry.Level), "[{Source}] {Message}", entry.Source, entry.Message);

        foreach (var handler in Published?.GetInvocationList() ?? [])
        {
            try
            {
                ((Action<LogEntry>)handler)(entry);
            }
            catch
            {
                // A misbehaving subscriber must never break the log fan-out.
            }
        }
    }

    public IReadOnlyList<LogEntry> Snapshot() => _buffer.ToArray();

    private static Microsoft.Extensions.Logging.LogLevel ToMsLevel(AppLogLevel level) => level switch
    {
        AppLogLevel.Debug => Microsoft.Extensions.Logging.LogLevel.Debug,
        AppLogLevel.Info => Microsoft.Extensions.Logging.LogLevel.Information,
        AppLogLevel.Warning => Microsoft.Extensions.Logging.LogLevel.Warning,
        AppLogLevel.Error => Microsoft.Extensions.Logging.LogLevel.Error,
        _ => Microsoft.Extensions.Logging.LogLevel.Information,
    };
}
