namespace SteamForge.Engine.Jobs;

/// <summary>Storage/queue configuration, bound from the "Storage" section.</summary>
public sealed class StorageOptions
{
    public const string SectionName = "Storage";

    /// <summary>Parent directory for the database and working files.</summary>
    public string DataDirectory { get; set; } = "data";

    /// <summary>SQLite database file name (under DataDirectory).</summary>
    public string DatabaseFileName { get; set; } = "jobs.sqlite3";

    /// <summary>Per-job scratch directory name (under DataDirectory).</summary>
    public string WorkDirectoryName { get; set; } = "work";

    /// <summary>Maximum jobs allowed to wait in the queue.</summary>
    public int MaxQueueSize { get; set; } = 100;

    /// <summary>
    /// How many times a job interrupted by a restart/crash is re-queued and re-run
    /// before it is abandoned as Failed. Guards against a poison job that kills the
    /// process on every attempt from wedging the queue forever.
    /// </summary>
    public int MaxResumeAttempts { get; set; } = 10;

    /// <summary>How often the worker polls for the next job, in seconds.</summary>
    public double PollSeconds { get; set; } = 1;

    /// <summary>
    /// Safety throttle for the dedupe existence check: before reusing a prior
    /// upload, confirm it still exists on the provider - but at most once per this
    /// many minutes per item, so a burst of identical requests does not hammer the
    /// provider API. A recently-verified entry is trusted without a call.
    /// </summary>
    public double DedupeVerifyIntervalMinutes { get; set; } = 60;

    /// <summary>
    /// How often the expiry sweeper runs, in minutes. It deletes uploads whose
    /// expiry has passed from the provider, clears their dedupe history, and marks
    /// the job expired - so expired links are purged automatically.
    /// </summary>
    public double ExpirySweepMinutes { get; set; } = 15;

    /// <summary>
    /// Ceiling for concurrent depot-chunk fetches per job. SteamPipe chunks are
    /// latency-bound, so the downloader adaptively ramps concurrency up to this cap
    /// until throughput plateaus, then holds ~10% below the peak to leave headroom.
    /// This is the upper bound on that ramp, not a fixed worker count. Env-overridable
    /// as Storage__MaxParallelDownloads so the VPS can be tuned without a rebuild.
    /// </summary>
    public int MaxParallelDownloads { get; set; } = 32;

    /// <summary>
    /// How often to poll Steam PICS for changed apps, in seconds. A cheap delta query
    /// that keeps cached appinfo (build ids, depot manifests) and the browse "update
    /// available" signal fresh, so a newly-shipped build is picked up without a restart.
    /// </summary>
    public double PicsChangePollSeconds { get; set; } = 300;

    /// <summary>
    /// How often to re-scan the whole owned library, in minutes, as a fallback that
    /// refreshes appinfo and latest build ids even if a PICS change poll was missed.
    /// The first scan after an account logs on always runs regardless.
    /// </summary>
    public double LibraryRescanIntervalMinutes { get; set; } = 360;

    /// <summary>
    /// Delay in seconds between account logons during an on-demand "rescan all
    /// libraries" rotation (<see cref="LibraryRescanService"/>). Spacing the switches
    /// keeps a burst of logons from tripping Steam's rate limiting. Applies to the
    /// next rotation; env-overridable as Storage__LibraryRescanPaceSeconds.
    /// </summary>
    public double LibraryRescanPaceSeconds { get; set; } = 8;

    /// <summary>
    /// Headroom in GiB that must remain free on the work drive AFTER a download.
    /// A job whose content would not fit within this margin is refused before any
    /// bytes are fetched, so the VPS disk is never filled. Also covers the archive
    /// that briefly coexists with the raw content.
    /// </summary>
    public double MinFreeSpaceMarginGb { get; set; } = 10;
}
