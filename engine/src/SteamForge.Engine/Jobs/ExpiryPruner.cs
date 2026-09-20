using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using SteamForge.Abstractions.Logging;
using SteamForge.Abstractions.Plugins;

namespace SteamForge.Engine.Jobs;

/// <summary>
/// Periodically purges uploads whose expiry has passed: deletes them from the
/// provider, clears the matching dedupe history, and marks the job expired. This
/// keeps expired links from lingering and reclaims provider storage automatically.
/// </summary>
internal sealed class ExpiryPruner : BackgroundService
{
    private const string LogSource = "expiry";

    private readonly JobStore _store;
    private readonly JobNotifier _notifier;
    private readonly IReadOnlyList<IUploader> _uploaders;
    private readonly ILogBroadcaster _log;
    private readonly StorageOptions _options;

    public ExpiryPruner(
        JobStore store,
        JobNotifier notifier,
        IEnumerable<IUploader> uploaders,
        ILogBroadcaster log,
        IOptions<StorageOptions> options)
    {
        _store = store;
        _notifier = notifier;
        _uploaders = uploaders.ToList();
        _log = log;
        _options = options.Value;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var interval = TimeSpan.FromMinutes(Math.Max(1, _options.ExpirySweepMinutes));

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await SweepAsync(stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                _log.Error(LogSource, $"Expiry sweep error: {ex.Message}");
            }

            try
            {
                await Task.Delay(interval, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    private async Task SweepAsync(CancellationToken ct)
    {
        var now = DateTimeOffset.UtcNow;
        foreach (var job in _store.ListExpiredReady(now))
        {
            ct.ThrowIfCancellationRequested();

            // Best-effort provider delete; proceed to expire the job regardless so
            // a provider hiccup does not leave a dead link marked "ready" forever.
            if (!string.IsNullOrEmpty(job.ProviderRef) && _uploaders.Count > 0)
            {
                try
                {
                    await _uploaders[0].DeleteAsync(job.ProviderRef, ct);
                    _log.Info(LogSource, $"Deleted expired upload for '{job.DisplayName}'");
                }
                catch (Exception ex)
                {
                    _log.Warn(LogSource, $"Could not delete expired upload for '{job.DisplayName}': {ex.Message}");
                }
            }

            if (job.ManifestId != 0)
            {
                _store.DeleteHistory(job.DepotId, job.ManifestId);
            }

            job.Status = JobStatus.Expired;
            job.Message = "Expired";
            job.CompletedAt = now;
            _store.Update(job);
            _notifier.Notify(job.ToSnapshot());
        }
    }
}
