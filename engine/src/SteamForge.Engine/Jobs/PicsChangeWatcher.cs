using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using SteamForge.Abstractions.Logging;
using SteamForge.Engine.Content;
using SteamForge.Engine.Steam;

namespace SteamForge.Engine.Jobs;

/// <summary>
/// Polls Steam PICS for changed apps and keeps our view of "latest build" fresh so a
/// newly-shipped build is picked up without a restart. For each owned app Steam reports
/// as changed, it drops the cached appinfo (so the next download resolve fetches the new
/// build id and depot manifests) and updates the ownership catalog's latest build id (so
/// the browse "update available" badge is accurate). The poll is a cheap delta - only the
/// change number and changed ids - not a full library scan; <see cref="OwnershipScanner"/>
/// does the periodic full refresh and is the fallback when a full re-sync is required.
/// </summary>
internal sealed class PicsChangeWatcher : BackgroundService
{
    private const string LogSource = "library";

    private readonly SteamSessionManager _session;
    private readonly SteamContentClient _content;
    private readonly JobStore _store;
    private readonly ILogBroadcaster _log;
    private readonly StorageOptions _options;

    // Steam's global change-number cursor; 0 until we take a baseline on first poll.
    private uint _lastChangeNumber;

    public PicsChangeWatcher(
        SteamSessionManager session,
        SteamContentClient content,
        JobStore store,
        ILogBroadcaster log,
        IOptions<StorageOptions> options)
    {
        _session = session;
        _content = content;
        _store = store;
        _log = log;
        _options = options.Value;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var period = TimeSpan.FromSeconds(Math.Max(30, _options.PicsChangePollSeconds));
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                if (_session.IsLoggedOn)
                {
                    await PollAsync(stoppingToken);
                }
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                // A session swap or transient Steam error just fails this tick; retry next.
                _log.Warn(LogSource, $"PICS change poll failed: {ex.Message}");
            }

            try
            {
                await Task.Delay(period, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    private async Task PollAsync(CancellationToken ct)
    {
        var changes = await _content.GetPicsChangesAsync(_lastChangeNumber, ct);

        // First poll only takes a baseline cursor - we do not treat the whole world as
        // "changed"; the initial ownership scan already loaded current build ids.
        if (_lastChangeNumber == 0)
        {
            _lastChangeNumber = changes.CurrentChangeNumber;
            return;
        }

        if (changes.RequiresFullUpdate)
        {
            // Our cursor was too old for a delta; drop all cached appinfo and let the
            // periodic ownership rescan rebuild the catalog's build ids.
            _content.InvalidateAllAppInfo();
            _lastChangeNumber = changes.CurrentChangeNumber;
            _log.Info(LogSource, "PICS reported a full-update requirement; cleared appinfo cache");
            return;
        }

        // Only refresh apps we actually own - Steam's changelist covers all of Steam.
        var owned = _store.ListOwnedAppIds();
        var refreshed = 0;
        foreach (var appId in changes.ChangedAppIds)
        {
            ct.ThrowIfCancellationRequested();
            if (!owned.Contains(appId))
            {
                continue;
            }

            _content.InvalidateAppInfo(appId);
            try
            {
                var kv = await _content.GetAppInfoAsync(appId, ct, forceRefresh: true);
                _store.UpdateLatestBuildId(appId, SteamContentClient.GetPublicBuildId(kv));
                refreshed++;
            }
            catch (Exception ex)
            {
                // Leave the entry invalidated; the next resolve or rescan will retry.
                _log.Warn(LogSource, $"Refreshing changed app {appId} failed: {ex.Message}");
            }
        }

        _lastChangeNumber = changes.CurrentChangeNumber;
        if (refreshed > 0)
        {
            _log.Info(LogSource, $"PICS change poll refreshed {refreshed} owned app(s)");
        }
    }
}
