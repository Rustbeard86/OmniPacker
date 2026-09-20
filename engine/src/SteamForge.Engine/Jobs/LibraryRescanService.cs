using Microsoft.Extensions.Options;
using SteamForge.Abstractions.Logging;
using SteamForge.Engine.Content;
using SteamForge.Engine.Steam;

namespace SteamForge.Engine.Jobs;

/// <summary>
/// On-demand "scan every account's library" rotation. The ownership catalog only
/// ever reflects the currently logged-on account (see <see cref="OwnershipScanner"/>),
/// so a freshly added account contributes nothing until the session has actually been
/// switched to it once. This walks every stored account in turn - switch, wait for the
/// license list, enumerate + persist ownership - so a single click refreshes the whole
/// catalog. Switches are paced so a burst of logons never trips Steam's rate limiting,
/// and it refuses to run while the queue is busy (a mid-download account switch would
/// corrupt the active job). The background <see cref="OwnershipScanner"/> is suspended
/// for the duration so the two never double-scan the same account.
/// </summary>
public sealed class LibraryRescanService
{
    private const string LogSource = "library";

    private readonly SteamSessionManager _session;
    private readonly SteamContentClient _content;
    private readonly JobStore _store;
    private readonly ILogBroadcaster _log;
    private readonly StorageOptions _options;

    // Only one rotation at a time; also read by OwnershipScanner to stand down while
    // a rotation owns the session.
    private volatile bool _running;
    private string? _progress;

    // Guards the start decision (idle-check + set-running) against the queue worker's
    // job-claim decision, so the two can never both conclude "the session is free" in the
    // same instant (see ClaimWhileIdle and TryStart).
    private readonly object _startGate = new();

    public LibraryRescanService(
        SteamSessionManager session, SteamContentClient content, JobStore store, ILogBroadcaster log,
        IOptions<StorageOptions> options)
    {
        _session = session;
        _content = content;
        _store = store;
        _log = log;
        _options = options.Value;
    }

    /// <summary>Raised when <see cref="IsRunning"/> or <see cref="Progress"/> changes.</summary>
    public event Action? Changed;

    /// <summary>True while a rotation is in flight.</summary>
    public bool IsRunning => _running;

    /// <summary>Human-readable progress for the UI (e.g. "Scanning 2/5: p4rogue"), or null.</summary>
    public string? Progress => _progress;

    /// <summary>
    /// Kick off a background rotation over every stored account. Returns immediately;
    /// watch <see cref="Changed"/> for progress. No-op (returns false) if one is already
    /// running, the queue is not idle, or there are no stored accounts.
    /// </summary>
    public bool TryStart()
    {
        if (_session.StoredAccounts.Count == 0)
        {
            return false;
        }

        // Decide idle -> running atomically against the worker's claim (ClaimWhileIdle takes
        // the same gate), so a job cannot be claimed in the window between our idle-check and
        // flipping _running. The queue worker holds this gate only for the instant it takes
        // to peek/claim a job, so this never blocks meaningfully.
        lock (_startGate)
        {
            if (_running)
            {
                return false;
            }

            if (!QueueIsIdle())
            {
                _log.Warn(LogSource, "Rescan-all skipped: a download is in progress (would break it to switch accounts).");
                return false;
            }

            _running = true;
        }

        SetProgress("Starting library rescan...");
        _ = Task.Run(RunAsync);
        return true;
    }

    /// <summary>
    /// Claim the next job via <paramref name="claim"/> ONLY if no rescan rotation is running
    /// (or starting) - taken under the same gate as <see cref="TryStart"/> so the two can
    /// never both proceed. Returns null when a rotation owns the session; the caller should
    /// wait (see the queue worker's WaitForRescanAsync) and retry.
    /// </summary>
    public T? ClaimWhileIdle<T>(Func<T?> claim) where T : class
    {
        lock (_startGate)
        {
            return _running ? null : claim();
        }
    }

    private async Task RunAsync()
    {
        // Return to whatever account was active when we started, so the rotation is
        // transparent to the rest of the engine.
        var original = _session.AccountName;
        var accounts = _session.StoredAccounts.Select(t => t.AccountName).ToList();
        var pace = TimeSpan.FromSeconds(Math.Max(0, _options.LibraryRescanPaceSeconds));
        var done = 0;

        try
        {
            _log.Info(LogSource, $"Rescan-all started for {accounts.Count} account(s).");
            for (var i = 0; i < accounts.Count; i++)
            {
                var account = accounts[i];
                SetProgress($"Scanning {i + 1}/{accounts.Count}: {account}");
                try
                {
                    if (!await _session.SwitchToAccountAsync(account))
                    {
                        _log.Warn(LogSource, $"Rescan-all: could not switch to '{account}'; skipping.");
                        continue;
                    }

                    // EnumerateOwnedAppsAsync waits on the license list itself.
                    var owned = await _content.EnumerateOwnedAppsAsync();
                    _store.ReplaceOwnership(account, owned);
                    done++;
                    _log.Info(LogSource, $"Rescan-all: {owned.Count} apps for '{account}' ({i + 1}/{accounts.Count}).");
                }
                catch (Exception ex)
                {
                    _log.Warn(LogSource, $"Rescan-all: '{account}' failed: {ex.Message}");
                }

                // Pace between account logons so a burst never trips Steam rate limiting.
                if (i < accounts.Count - 1 && pace > TimeSpan.Zero)
                {
                    await Task.Delay(pace);
                }
            }

            // Restore the pre-rotation account so downstream state is unchanged.
            if (!string.IsNullOrEmpty(original) &&
                !string.Equals(original, _session.AccountName, StringComparison.OrdinalIgnoreCase))
            {
                if (pace > TimeSpan.Zero)
                {
                    await Task.Delay(pace);
                }

                await _session.SwitchToAccountAsync(original);
            }

            _log.Info(LogSource, $"Rescan-all complete: {done}/{accounts.Count} account(s) scanned.");
        }
        catch (Exception ex)
        {
            _log.Error(LogSource, $"Rescan-all aborted: {ex.Message}");
        }
        finally
        {
            lock (_startGate)
            {
                _running = false;
            }

            SetProgress(null);
        }
    }

    // A rotation switches accounts, which would corrupt any job the worker is actively
    // running. Only proceed when nothing is queued or in flight.
    private bool QueueIsIdle() =>
        !_store.List().Any(j => j.Status
            is JobStatus.Queued or JobStatus.Resolving or JobStatus.Downloading
            or JobStatus.Archiving or JobStatus.Uploading);

    private void SetProgress(string? text)
    {
        _progress = text;
        Changed?.Invoke();
    }
}
