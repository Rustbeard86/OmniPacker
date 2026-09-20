using SteamForge.Abstractions.Logging;

namespace SteamForge.Abstractions.Plugins;

/// <summary>
/// Per-job context handed to each plugin step. Carries the working directory,
/// a job-scoped reporter for progress/log, cancellation, and a property bag so
/// one step can pass artifacts (paths, manifest ids) to the next.
/// </summary>
public interface IPipelineContext
{
    /// <summary>Opaque job id, unique per queued request.</summary>
    string JobId { get; }

    /// <summary>Private working directory for this job; cleaned up after completion.</summary>
    string WorkingDirectory { get; }

    /// <summary>
    /// Persistent, content-keyed directory that downloaded content is staged into.
    /// Unlike <see cref="WorkingDirectory"/> it survives an interrupted job so the
    /// download can resume (only missing chunks are re-fetched); it is removed once
    /// the job uploads successfully. Set by the runner after content is resolved.
    /// </summary>
    string StagingDirectory { get; }

    /// <summary>Job-scoped progress/log sink.</summary>
    IJobReporter Reporter { get; }

    /// <summary>Cancelled when the job is cancelled or the host shuts down.</summary>
    CancellationToken CancellationToken { get; }

    /// <summary>Cross-step scratch state (e.g. "archivePath", "manifestId").</summary>
    IDictionary<string, object?> Items { get; }
}

/// <summary>Job-scoped progress and logging, fanned out to the admin UI.</summary>
public interface IJobReporter
{
    /// <summary>Report coarse progress (0-100) with a short status message.</summary>
    void Progress(int percent, string message);

    /// <summary>Emit a log line tagged with the job and the plugin source.</summary>
    void Log(LogLevel level, string source, string message);
}

public static class JobReporterExtensions
{
    public static void Info(this IJobReporter reporter, string source, string message) =>
        reporter.Log(LogLevel.Info, source, message);

    public static void Warn(this IJobReporter reporter, string source, string message) =>
        reporter.Log(LogLevel.Warning, source, message);

    public static void Error(this IJobReporter reporter, string source, string message) =>
        reporter.Log(LogLevel.Error, source, message);
}
