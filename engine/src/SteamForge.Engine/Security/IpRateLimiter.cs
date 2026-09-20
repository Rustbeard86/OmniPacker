using System.Collections.Concurrent;

namespace SteamForge.Engine.Security;

/// <summary>
/// In-memory per-IP sliding-window limiter for public queue actions (anti-flood).
/// Counts one hit per user action - a bulk/collection submit that fans out to many
/// jobs still counts once - so the resource budget (bytes), not this, is what caps
/// heavy but legitimate use. Limits are passed in per call so they stay live-editable
/// from the Settings page. Ephemeral by design: a restart clears the windows, which is
/// harmless for a short-horizon anti-flood control.
/// </summary>
public sealed class IpRateLimiter
{
    // Recent action timestamps per IP, newest last. Pruned to the 1h horizon on access.
    private readonly ConcurrentDictionary<string, Window> _windows = new(StringComparer.OrdinalIgnoreCase);

    private sealed class Window
    {
        public readonly Queue<DateTimeOffset> Hits = new();
    }

    /// <summary>
    /// Record an action for <paramref name="ip"/> if it is within both limits, and
    /// return true. If it would exceed a limit, record nothing and return false with a
    /// short human reason. A non-positive limit disables that dimension.
    /// </summary>
    public bool TryAcquire(string ip, int perMinute, int perHour, out string reason)
    {
        reason = "";
        if (string.IsNullOrWhiteSpace(ip))
        {
            ip = "unknown";
        }

        var now = DateTimeOffset.UtcNow;
        var window = _windows.GetOrAdd(ip, _ => new Window());

        lock (window)
        {
            // Drop anything older than the longest horizon we care about (1 hour).
            while (window.Hits.Count > 0 && now - window.Hits.Peek() > TimeSpan.FromHours(1))
            {
                window.Hits.Dequeue();
            }

            if (perMinute > 0)
            {
                var minuteAgo = now - TimeSpan.FromMinutes(1);
                var inMinute = window.Hits.Count(t => t >= minuteAgo);
                if (inMinute >= perMinute)
                {
                    reason = $"Too many requests - up to {perMinute} per minute. Please wait a moment and try again.";
                    return false;
                }
            }

            if (perHour > 0 && window.Hits.Count >= perHour)
            {
                reason = $"Hourly request limit reached ({perHour} per hour). Please try again later.";
                return false;
            }

            window.Hits.Enqueue(now);

            // Opportunistic cap on dictionary growth: if we are tracking a lot of IPs,
            // shed those whose most recent hit has aged out of the 1h horizon.
            if (_windows.Count > 4096)
            {
                PruneIdle(now);
            }

            return true;
        }
    }

    private void PruneIdle(DateTimeOffset now)
    {
        foreach (var (key, w) in _windows)
        {
            lock (w)
            {
                if (w.Hits.Count == 0 || now - w.Hits.Last() > TimeSpan.FromHours(1))
                {
                    _windows.TryRemove(key, out _);
                }
            }
        }
    }
}
