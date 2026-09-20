using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using SteamForge.Abstractions.Logging;
using SteamForge.Engine.Settings;
using SteamForge.Engine.Steam;

namespace SteamForge.Engine.Jobs;

/// <summary>
/// Single background worker that drains the queue one job at a time (matching
/// the reference tool's serial model - SteamPipe downloads and archiving are
/// heavy, so serial keeps resource use predictable).
/// </summary>
internal sealed class JobQueueWorker : BackgroundService
{
    private readonly PipelineRunner _runner;
    private readonly JobStore _store;
    private readonly StoragePaths _paths;
    private readonly ILogBroadcaster _log;
    private readonly StorageOptions _options;
    private readonly SteamSessionManager _session;
    private readonly RuntimeSettingsStore _settings;
    private readonly LibraryRescanService _rescan;

    public JobQueueWorker(
        PipelineRunner runner,
        JobStore store,
        StoragePaths paths,
        ILogBroadcaster log,
        IOptions<StorageOptions> options,
        SteamSessionManager session,
        RuntimeSettingsStore settings,
        LibraryRescanService rescan)
    {
        _runner = runner;
        _store = store;
        _paths = paths;
        _log = log;
        _options = options.Value;
        _session = session;
        _settings = settings;
        _rescan = rescan;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Put jobs that were mid-run when the process last stopped back on the queue so
        // a redeploy resumes them instead of dropping them (see JobStore for the guard).
        var (requeued, abandoned) = _store.RecoverInterrupted(_options.MaxResumeAttempts);
        if (requeued > 0)
        {
            _log.Info("queue", $"Re-queued {requeued} job(s) interrupted by the last restart");
        }

        if (abandoned > 0)
        {
            _log.Warn("queue", $"Abandoned {abandoned} job(s) after {_options.MaxResumeAttempts} restart attempts");
        }

        // Sweep the ephemeral scratch root (per-job archives): a hard stop kills the
        // pipeline before its own cleanup, so anything here is residue. This does NOT
        // touch the staging root, whose partial downloads are deliberately kept to resume.
        CleanWorkRoot();

        // GC the staging root: keep partials owned by a job that will still run
        // (queued/mid-pipeline) AND recently-failed jobs whose work is retained so a Retry
        // can re-upload the cached archive / resume the download instead of starting over;
        // delete the rest as orphaned (completed, expired, long-failed, or a stale build
        // supplanted by a newer one). The archive cache is keyed the same way and shares
        // this fate, so sweep it against the same retained set.
        var retentionHours = _settings.Current.FailedRetentionHours;
        var failedRetainedSince = retentionHours > 0
            ? DateTimeOffset.UtcNow.AddHours(-retentionHours)
            : DateTimeOffset.MaxValue; // 0 = retain no failed work (reclaim on restart)
        var retained = _store.RetainedStagingKeys(failedRetainedSince);
        CleanOrphanKeyed(_paths.StagingRoot, retained, "staging");
        CleanOrphanKeyed(_paths.ArchiveRoot, retained, "archive");
        _log.Info("queue", "Job queue worker started");

        var poll = TimeSpan.FromSeconds(Math.Max(0.2, _options.PollSeconds));

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                // Stand down while a rescan-all rotation owns the session: it switches
                // accounts across every stored login, and starting a job now would drive
                // the same single Steam session concurrently -> LogonSessionReplaced, which
                // aborts BOTH the rescan and the job (the job then fails spuriously, e.g.
                // "No accessible depots"). Rescan-all only starts when the queue is idle, so
                // holding here makes the two fully mutually exclusive. Jobs stay Queued.
                await WaitForRescanAsync(stoppingToken);

                // Re-check-and-claim atomically against rescan start (shared gate): closes
                // the window where a rescan begins between the wait above and the claim.
                // Returns null if a rotation is (or just started) running - loop and wait.
                var job = _rescan.ClaimWhileIdle(_store.ClaimNextQueued);
                if (job is not null)
                {
                    // Every job's first step is a Steam call (resolve). Steam routinely
                    // cycles connection managers, and SteamKit cancels in-flight requests
                    // the instant the socket drops - so a resolve started during a
                    // reconnect window fails spuriously. The session self-heals (see
                    // SteamSessionManager auto-reconnect); wait for it to be back before
                    // starting, so a transient blip pauses the queue instead of burning
                    // jobs. The job stays Queued (visible) until then.
                    await WaitForSessionAsync(stoppingToken);
                    await _runner.RunAsync(job, _paths.WorkRoot, stoppingToken);
                    continue; // immediately check for the next job
                }
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                _log.Error("queue", $"Queue worker error: {ex.Message}");
            }

            try
            {
                await Task.Delay(poll, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }

        _log.Info("queue", "Job queue worker stopped");
    }

    /// <summary>
    /// Block until the shared Steam session is logged on (or shutdown is requested), so
    /// a job never starts its resolve against a dropped/reconnecting session. Polls
    /// cheaply; logs once when it actually has to wait so a stall is visible.
    /// </summary>
    private async Task WaitForSessionAsync(CancellationToken ct)
    {
        if (_session.IsLoggedOn)
        {
            return;
        }

        _log.Info("queue", "Steam session is not ready; holding the next job until it reconnects...");
        while (!ct.IsCancellationRequested && !_session.IsLoggedOn)
        {
            await Task.Delay(TimeSpan.FromSeconds(1), ct);
        }

        if (_session.IsLoggedOn)
        {
            _log.Info("queue", "Steam session ready; resuming the queue");
        }
    }

    /// <summary>
    /// Block while a rescan-all rotation is in flight (or until shutdown), so the worker
    /// never starts a job that would race the rotation's account switching on the shared
    /// Steam session. Logs once when it actually has to wait so a hold is visible.
    /// </summary>
    private async Task WaitForRescanAsync(CancellationToken ct)
    {
        if (!_rescan.IsRunning)
        {
            return;
        }

        _log.Info("queue", "Library rescan-all in progress; holding the next job until it finishes...");
        while (!ct.IsCancellationRequested && _rescan.IsRunning)
        {
            await Task.Delay(TimeSpan.FromSeconds(1), ct);
        }

        if (!_rescan.IsRunning)
        {
            _log.Info("queue", "Rescan-all finished; resuming the queue");
        }
    }

    /// <summary>
    /// Remove every per-job scratch entry under the work root. Work dirs are transient
    /// (download -> archive -> upload -> delete); the only reason one survives a start
    /// is that a crash or redeploy killed the pipeline before its <c>finally</c> ran.
    /// </summary>
    private void CleanWorkRoot()
    {
        Directory.CreateDirectory(_paths.WorkRoot);

        var removed = 0;
        foreach (var entry in Directory.EnumerateFileSystemEntries(_paths.WorkRoot))
        {
            try
            {
                if (Directory.Exists(entry))
                {
                    Directory.Delete(entry, recursive: true);
                }
                else
                {
                    File.Delete(entry);
                }

                removed++;
            }
            catch (Exception ex)
            {
                _log.Warn("queue", $"Could not remove leftover work item {entry}: {ex.Message}");
            }
        }

        if (removed > 0)
        {
            _log.Info("queue", $"Cleared {removed} leftover work item(s) from a previous run");
        }
    }

    /// <summary>
    /// Delete content-keyed directories under <paramref name="root"/> that no live job
    /// owns. A directory is kept only if its name matches the staging key of a
    /// queued/mid-pipeline job (so its resume, or its reusable archive, survives);
    /// everything else - a finished job's leftovers, a failed job, or a stale build
    /// replaced by a newer one - is reclaimed. Used for both the staging root (partial
    /// downloads) and the archive cache (finished archives kept for upload retry).
    /// </summary>
    private void CleanOrphanKeyed(string root, IReadOnlySet<string> live, string label)
    {
        if (!Directory.Exists(root))
        {
            return;
        }

        var removed = 0;
        foreach (var dir in Directory.EnumerateDirectories(root))
        {
            if (live.Contains(Path.GetFileName(dir)))
            {
                continue;
            }

            try
            {
                Directory.Delete(dir, recursive: true);
                removed++;
            }
            catch (Exception ex)
            {
                _log.Warn("queue", $"Could not remove orphaned {label} {dir}: {ex.Message}");
            }
        }

        if (removed > 0)
        {
            _log.Info("queue", $"Reclaimed {removed} orphaned {label} director(ies) from a previous run");
        }
    }
}
