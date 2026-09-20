using Microsoft.Extensions.Options;
using SteamForge.Abstractions.Logging;

namespace SteamForge.Engine.Jobs;

/// <summary>Thrown when the queue is at capacity.</summary>
public sealed class QueueFullException(int max)
    : Exception($"The queue is full ({max} jobs). Try again once some finish.");

/// <summary>
/// Public queue facade for the admin UI: enqueue requests and read job state.
/// A single background worker drains it one job at a time.
/// </summary>
public sealed class JobQueue
{
    private readonly JobStore _store;
    private readonly JobNotifier _notifier;
    private readonly ILogBroadcaster _log;
    private readonly StorageOptions _options;
    private readonly StoragePaths _paths;

    public JobQueue(
        JobStore store, JobNotifier notifier, ILogBroadcaster log,
        IOptions<StorageOptions> options, StoragePaths paths)
    {
        _store = store;
        _notifier = notifier;
        _log = log;
        _options = options.Value;
        _paths = paths;
    }

    public string Enqueue(JobRequest request)
    {
        if (_store.CountQueued() >= _options.MaxQueueSize)
        {
            throw new QueueFullException(_options.MaxQueueSize);
        }

        var job = new Job
        {
            Id = Guid.NewGuid().ToString("n"),
            Kind = request.Kind,
            AppId = request.AppId,
            WorkshopId = request.WorkshopId,
            DisplayName = request.DisplayName ?? (request.WorkshopId != 0
                ? $"Workshop {request.WorkshopId}"
                : $"App {request.AppId}"),
            Password = request.Password,
            ExpiryHours = request.ExpiryHours,
            Branch = request.Branch,
            TargetOs = request.TargetOs,
            SubmitterIp = request.SubmitterIp,
            Status = JobStatus.Queued,
            Message = "Queued",
        };

        _store.Insert(job);
        _notifier.Notify(job.ToSnapshot());
        // The owning app id is only known after resolve, so for workshop jobs log
        // the workshop id (what the user actually supplied) rather than "app 0". Include
        // the submitter IP (public requests only) so the logs attribute who queued what.
        var target = job.WorkshopId != 0 ? $"workshop {job.WorkshopId}" : $"app {job.AppId}";
        var from = string.IsNullOrWhiteSpace(job.SubmitterIp) ? "" : $" from {job.SubmitterIp}";
        _log.Info("queue", $"Queued {job.Kind} job {job.Id[..8]} for {target}{from}");
        return job.Id;
    }

    public IReadOnlyList<JobSnapshot> List(int limit = 100) => _store.List(limit);

    public JobSnapshot? Get(string id) => _store.Get(id)?.ToSnapshot();

    /// <summary>Re-run a job by queueing a fresh copy of its request.</summary>
    public string Retry(string id)
    {
        var job = _store.Get(id) ?? throw new InvalidOperationException($"Job {id} not found");
        return Enqueue(new JobRequest(
            job.Kind, job.AppId, job.WorkshopId, job.DisplayName, job.Password, job.ExpiryHours, job.Branch, job.TargetOs, job.SubmitterIp));
    }

    /// <summary>
    /// Remove a job record from the queue (leaves any completed upload on the host in
    /// place). Also reclaims the job's on-disk staging + cached-archive directories now,
    /// rather than leaving them to leak until the next startup orphan sweep: deleting a
    /// failed/interrupted job is the common way multi-GB residue is abandoned.
    /// </summary>
    public void DeleteJob(string id)
    {
        var job = _store.Get(id);
        _store.DeleteJob(id);

        if (job?.StagingKey is { Length: > 0 } key)
        {
            DeleteKeyedDir(_paths.StagingRoot, key, "staging");
            DeleteKeyedDir(_paths.ArchiveRoot, key, "archive");
        }

        _notifier.NotifyRemoved(id);
        _log.Info("queue", $"Deleted job {id[..Math.Min(8, id.Length)]}");
    }

    /// <summary>Best-effort removal of one content-keyed directory (staging or archive
    /// cache) belonging to a deleted job. A key with no directory (never ran, or already
    /// cleaned) is a no-op.</summary>
    private void DeleteKeyedDir(string root, string key, string label)
    {
        try
        {
            var dir = Path.Combine(root, key);
            if (Directory.Exists(dir))
            {
                Directory.Delete(dir, recursive: true);
                _log.Info("queue", $"Reclaimed {label} for deleted job ({key})");
            }
        }
        catch (Exception ex)
        {
            _log.Warn("queue", $"Could not remove {label} {key} for deleted job: {ex.Message}");
        }
    }

    /// <summary>
    /// Clear the dedupe-history entry behind a job so an identical future request
    /// downloads fresh instead of reusing the recorded link. Does not delete the
    /// upload itself (that lives in the Gofile dashboard).
    /// </summary>
    public void ClearCache(string id)
    {
        var job = _store.Get(id);
        if (job is null || job.ManifestId == 0)
        {
            return;
        }

        _store.DeleteHistory(job.DepotId, job.ManifestId);
        _log.Info("queue", $"Cleared dedupe cache for '{job.DisplayName}'");
    }
}
