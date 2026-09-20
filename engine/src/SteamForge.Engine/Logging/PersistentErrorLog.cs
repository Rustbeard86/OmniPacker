using Microsoft.Extensions.Hosting;
using SteamForge.Abstractions.Logging;
using SteamForge.Engine.Jobs;

namespace SteamForge.Engine.Logging;

/// <summary>
/// Durable, on-disk sink for error-level log lines. The live console buffer is
/// in-memory (reset on every start) and container stdout is discarded when a redeploy
/// recreates the container, so neither survives a rebuild/restart. This appends every
/// Error line to a file on the /data bind mount so failures can be reviewed after the
/// fact. Size-rotated to a few files so it never grows without bound. Best-effort:
/// logging must never throw into the app, so all IO here swallows its own errors.
/// </summary>
public sealed class PersistentErrorLog : IHostedService
{
    // Errors only, by design (that is what we want to keep across restarts). Lower this
    // to LogLevel.Warning to also retain warnings.
    private const LogLevel MinLevel = LogLevel.Error;

    private const long MaxBytes = 5 * 1024 * 1024; // roll at ~5 MiB
    private const int KeepFiles = 3;               // errors.log + errors.log.1..3

    private readonly ILogBroadcaster _broadcaster;
    private readonly string _dir;
    private readonly string _path;
    private readonly object _gate = new();

    public PersistentErrorLog(ILogBroadcaster broadcaster, StoragePaths paths)
    {
        _broadcaster = broadcaster;
        _dir = Path.Combine(paths.DataDir, "logs");
        _path = Path.Combine(_dir, "errors.log");
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        try
        {
            Directory.CreateDirectory(_dir);
        }
        catch
        {
            // If the directory cannot be made we still subscribe; each write retries it.
        }

        _broadcaster.Published += OnPublished;
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        _broadcaster.Published -= OnPublished;
        return Task.CompletedTask;
    }

    private void OnPublished(LogEntry entry)
    {
        if (entry.Level < MinLevel)
        {
            return;
        }

        // ISO-8601 UTC timestamp, then level/source/message - greppable and stable.
        var line = $"{entry.Timestamp.UtcDateTime:yyyy-MM-ddTHH:mm:ss.fffZ} [{entry.Level}] [{entry.Source}] {entry.Message}";

        lock (_gate)
        {
            try
            {
                Directory.CreateDirectory(_dir);
                RollIfNeeded();
                File.AppendAllText(_path, line + Environment.NewLine);
            }
            catch
            {
                // Never let a logging IO failure surface into the app.
            }
        }
    }

    /// <summary>Size-based rotation: once the active file passes the cap, shift
    /// errors.log -> errors.1.log -> ... and drop the oldest, then start fresh.</summary>
    private void RollIfNeeded()
    {
        try
        {
            var info = new FileInfo(_path);
            if (!info.Exists || info.Length < MaxBytes)
            {
                return;
            }

            var oldest = $"{_path}.{KeepFiles}";
            if (File.Exists(oldest))
            {
                File.Delete(oldest);
            }

            for (var i = KeepFiles - 1; i >= 1; i--)
            {
                var src = $"{_path}.{i}";
                if (File.Exists(src))
                {
                    File.Move(src, $"{_path}.{i + 1}", overwrite: true);
                }
            }

            File.Move(_path, $"{_path}.1", overwrite: true);
        }
        catch
        {
            // A rotation hiccup must not stop the line being written; ignore.
        }
    }
}
