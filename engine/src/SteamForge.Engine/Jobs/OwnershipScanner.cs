using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using SteamForge.Abstractions.Logging;
using SteamForge.Engine.Content;
using SteamForge.Engine.Steam;

namespace SteamForge.Engine.Jobs;

/// <summary>
/// Scans the logged-on account's owned library and persists it to the ownership
/// catalog, so downloads can later be routed to an owning account and users can search
/// by game name. Re-scans when a different account logs on, and again on a periodic
/// interval so build ids and appinfo stay current even if a PICS change poll was missed
/// (<see cref="PicsChangeWatcher"/> handles the fine-grained, near-real-time updates).
/// </summary>
internal sealed class OwnershipScanner : BackgroundService
{
    private const string LogSource = "library";

    private readonly SteamSessionManager _session;
    private readonly SteamContentClient _content;
    private readonly JobStore _store;
    private readonly ILogBroadcaster _log;
    private readonly StorageOptions _options;
    private readonly LibraryRescanService _rescan;

    private string? _scannedAccount;
    private DateTimeOffset _lastScan;

    public OwnershipScanner(
        SteamSessionManager session, SteamContentClient content, JobStore store, ILogBroadcaster log,
        IOptions<StorageOptions> options, LibraryRescanService rescan)
    {
        _session = session;
        _content = content;
        _store = store;
        _log = log;
        _options = options.Value;
        _rescan = rescan;
    }

    /// <summary>Scan when the account changed, or the periodic refresh interval has elapsed.</summary>
    private bool DueForScan(string account)
    {
        if (!string.Equals(account, _scannedAccount, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        var interval = TimeSpan.FromMinutes(Math.Max(1, _options.LibraryRescanIntervalMinutes));
        return DateTimeOffset.UtcNow - _lastScan >= interval;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                // Stand down while an on-demand rescan-all rotation owns the session:
                // it walks every account itself, so scanning here would double the work
                // and race its switches.
                if (!_rescan.IsRunning &&
                    _session.IsLoggedOn && _session.AccountName is { Length: > 0 } account && DueForScan(account))
                {
                    // Scan under the login lock so a concurrent interactive login/switch
                    // cannot swap the session out from under the scan (which failed the
                    // scan and left the catalog empty in production). Re-check the session
                    // once we hold the lock, since it may have changed while we waited.
                    await _session.WithLoginLockAsync(async ct =>
                    {
                        if (!_session.IsLoggedOn ||
                            !string.Equals(_session.AccountName, account, StringComparison.OrdinalIgnoreCase))
                        {
                            return;
                        }

                        _log.Info(LogSource, $"Scanning owned library for '{account}'...");
                        var owned = await _content.EnumerateOwnedAppsAsync(ct);
                        _store.ReplaceOwnership(account, owned);
                        _scannedAccount = account;
                        _lastScan = DateTimeOffset.UtcNow;
                        _log.Info(LogSource, $"Library scan complete: {owned.Count} apps for '{account}'");
                    }, stoppingToken);
                }
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                _log.Warn(LogSource, $"Library scan failed: {ex.Message}");
            }

            try
            {
                await Task.Delay(TimeSpan.FromSeconds(10), stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }
}
