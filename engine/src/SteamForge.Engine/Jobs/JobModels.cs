using SteamForge.Abstractions.Plugins;

namespace SteamForge.Engine.Jobs;

/// <summary>Lifecycle of a queued job.</summary>
public enum JobStatus
{
    Queued,
    Resolving,
    Downloading,
    Archiving,
    Uploading,
    Ready,
    Failed,
    Expired,
    Canceled,
}

/// <summary>A user request to fetch, package and share Steam content.</summary>
/// <param name="Kind">Workshop item or full game.</param>
/// <param name="AppId">Steam app id.</param>
/// <param name="WorkshopId">Workshop published-file id (workshop jobs).</param>
/// <param name="DisplayName">Optional label, filled from Steam metadata when resolved.</param>
/// <param name="Password">Optional password for the resulting share link.</param>
public sealed record JobRequest(
    ContentKind Kind,
    uint AppId,
    ulong WorkshopId = 0,
    string? DisplayName = null,
    string? Password = null,
    int? ExpiryHours = null,
    string? Branch = null,
    string? TargetOs = null,
    string? SubmitterIp = null);

/// <summary>Immutable view of a job for the admin UI.</summary>
public sealed record JobSnapshot(
    string Id,
    ContentKind Kind,
    uint AppId,
    ulong WorkshopId,
    string DisplayName,
    JobStatus Status,
    int Progress,
    string Message,
    string? Error,
    string? ResultUrl,
    string? ResultPassword,
    ulong ManifestId,
    bool Reused,
    DateTimeOffset CreatedAt,
    DateTimeOffset? CompletedAt,
    DateTimeOffset? ExpiresAt,
    string? ProviderRef = null,
    // App branch this job targets ("public", "open-beta", ...); null for workshop items,
    // which have no branch. Lets "Your requests" distinguish two branch requests for the
    // same title instead of rendering identical cards.
    string? Branch = null);

/// <summary>A dedupe-history record: an exact manifest already uploaded somewhere.</summary>
/// <param name="Branch">
/// App branch this archive was built from ("public" default). Lets the browse page
/// compare only public archives against the current public build for staleness, so a
/// deliberately-archived beta is never flagged as out of date.
/// </param>
public sealed record HistoryEntry(
    uint AppId,
    uint DepotId,
    ulong ManifestId,
    string Provider,
    string Url,
    string? Password,
    DateTimeOffset UploadedAt,
    DateTimeOffset? ExpiresAt,
    string? ProviderRef = null,
    DateTimeOffset? VerifiedAt = null,
    string Branch = "public",
    // Workshop provenance: non-zero for a Workshop-item archive (0 for a game), with the
    // item's title. Lets the public Workshop page list each mod under its owning app card
    // instead of colliding on the consumer-app id like a game build would.
    ulong WorkshopId = 0,
    string? Title = null);

/// <summary>Mutable running job, persisted via <see cref="JobStore"/>.</summary>
public sealed class Job
{
    public required string Id { get; init; }
    public required ContentKind Kind { get; init; }
    public required uint AppId { get; set; }
    public ulong WorkshopId { get; init; }
    public string DisplayName { get; set; } = "";
    public JobStatus Status { get; set; } = JobStatus.Queued;
    public int Progress { get; set; }
    public string Message { get; set; } = "Queued";
    public string? Error { get; set; }
    public string? ResultUrl { get; set; }
    public string? ResultPassword { get; set; }
    public ulong ManifestId { get; set; }
    public uint DepotId { get; set; }
    public bool Reused { get; set; }
    public string? Password { get; init; }

    /// <summary>Requested link lifetime in hours (null/0 = no expiry).</summary>
    public int? ExpiryHours { get; init; }

    /// <summary>App branch to download (games); null = default "public".</summary>
    public string? Branch { get; init; }

    /// <summary>Target OS for game depot selection; null = default "windows".</summary>
    public string? TargetOs { get; init; }

    /// <summary>
    /// Client IP that submitted this job (public browse page only; null for admin-queued
    /// jobs, which are never rate-limited). Persisted so the download's actual byte cost
    /// can be charged to the submitter's rolling budget when the job runs.
    /// </summary>
    public string? SubmitterIp { get; init; }

    /// <summary>Provider-side handle (e.g. gofile folder UUID) for later management.</summary>
    public string? ProviderRef { get; set; }

    /// <summary>
    /// Name of this job's persistent staging directory (under the staging root), set
    /// once the download begins. Lets a redeploy resume the same partial content and
    /// lets startup GC tell a live partial from an orphaned one. Null before download.
    /// </summary>
    public string? StagingKey { get; set; }

    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? CompletedAt { get; set; }
    public DateTimeOffset? ExpiresAt { get; set; }

    public JobSnapshot ToSnapshot() => new(
        Id, Kind, AppId, WorkshopId, DisplayName, Status, Progress, Message, Error,
        ResultUrl, ResultPassword, ManifestId, Reused, CreatedAt, CompletedAt, ExpiresAt, ProviderRef,
        Kind == ContentKind.Game
            ? (string.IsNullOrWhiteSpace(Branch) ? "public" : Branch.Trim())
            : null);
}
