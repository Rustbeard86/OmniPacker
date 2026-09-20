using System.Diagnostics;
using SteamForge.Abstractions.Logging;
using SteamForge.Abstractions.Plugins;

namespace SteamForge.Engine.Jobs;

/// <summary>Per-job <see cref="IPipelineContext"/> handed to each plugin step.</summary>
internal sealed class JobPipelineContext : IPipelineContext
{
    public JobPipelineContext(string jobId, string workingDirectory, IJobReporter reporter, CancellationToken ct)
    {
        JobId = jobId;
        WorkingDirectory = workingDirectory;
        Reporter = reporter;
        CancellationToken = ct;
    }

    public string JobId { get; }
    public string WorkingDirectory { get; }

    /// <summary>Set by the runner after resolve, before the download step runs.</summary>
    public string StagingDirectory { get; set; } = "";
    public IJobReporter Reporter { get; }
    public CancellationToken CancellationToken { get; }
    public IDictionary<string, object?> Items { get; } = new Dictionary<string, object?>();
}

/// <summary>
/// Writes job progress/status back to the store, notifies the UI, and mirrors
/// log lines to the console stream. DB writes for high-frequency progress are
/// throttled; in-memory notifications are always sent so the UI stays live.
/// </summary>
internal sealed class JobReporter : IJobReporter
{
    private static readonly TimeSpan PersistInterval = TimeSpan.FromMilliseconds(750);

    private readonly Job _job;
    private readonly JobStore _store;
    private readonly JobNotifier _notifier;
    private readonly ILogBroadcaster _log;
    private readonly Stopwatch _sinceLastPersist = Stopwatch.StartNew();

    public JobReporter(Job job, JobStore store, JobNotifier notifier, ILogBroadcaster log)
    {
        _job = job;
        _store = store;
        _notifier = notifier;
        _log = log;
    }

    public void Progress(int percent, string message)
    {
        _job.Progress = Math.Clamp(percent, 0, 100);
        _job.Message = message;
        _notifier.Notify(_job.ToSnapshot());

        if (_sinceLastPersist.Elapsed >= PersistInterval)
        {
            _store.Update(_job);
            _sinceLastPersist.Restart();
        }
    }

    public void Log(LogLevel level, string source, string message) =>
        _log.Publish(LogEntry.Now(level, source, $"[{Short(_job.Id)}] {message}"));

    /// <summary>Set the job's status/message and persist immediately.</summary>
    public void SetStatus(JobStatus status, string message)
    {
        _job.Status = status;
        _job.Message = message;
        _store.Update(_job);
        _notifier.Notify(_job.ToSnapshot());
        _log.Info("queue", $"[{Short(_job.Id)}] {status}: {message}");
    }

    /// <summary>Persist the current job state and notify the UI.</summary>
    public void Flush()
    {
        _store.Update(_job);
        _notifier.Notify(_job.ToSnapshot());
    }

    private static string Short(string id) => id.Length <= 8 ? id : id[..8];
}
