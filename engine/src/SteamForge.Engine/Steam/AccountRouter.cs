using SteamForge.Abstractions.Logging;
using SteamForge.Engine.Content;
using SteamForge.Engine.Jobs;

namespace SteamForge.Engine.Steam;

/// <summary>
/// Routes a job to a Steam account that owns the app, swapping the single shared
/// session when needed. Ownership comes from the scanned catalog; the queue is
/// FIFO so only one account is ever active at once.
/// </summary>
public sealed class AccountRouter
{
    private const string LogSource = "account";

    private readonly SteamSessionManager _session;
    private readonly JobStore _store;
    private readonly ILogBroadcaster _log;

    public AccountRouter(SteamSessionManager session, JobStore store, ILogBroadcaster log)
    {
        _session = session;
        _store = store;
        _log = log;
    }

    /// <summary>
    /// Ensure the active session is an account that owns <paramref name="appId"/>.
    /// No-op if the current account already owns it, or if ownership is unknown
    /// (then we just use whatever is logged in).
    /// </summary>
    public async Task EnsureOwnerAsync(uint appId, CancellationToken ct = default)
    {
        var owners = _store.OwningAccounts(appId);
        if (owners.Count == 0)
        {
            return;
        }

        var current = _session.AccountName;
        if (!string.IsNullOrEmpty(current) &&
            owners.Any(o => o.Equals(current, StringComparison.OrdinalIgnoreCase)))
        {
            return;
        }

        var target = owners[0];
        _log.Info(LogSource, $"App {appId} is owned by '{target}'; switching session");
        if (!await _session.SwitchToAccountAsync(target, ct))
        {
            throw new InvalidOperationException(
                $"Could not switch to owner account '{target}' for app {appId}.");
        }
    }

    /// <summary>
    /// Run <paramref name="action"/> against an account that can actually download
    /// <paramref name="appId"/>, rotating owners on AccessDenied. A plain membership
    /// check (see <see cref="EnsureOwnerAsync"/>) is not enough: the scanned catalog
    /// records an app as "owned" whenever a license grants its app id, but a
    /// free-weekend / promo / family-share grant confers no depot-key entitlement, so
    /// the listed account is denied at download time. The only ground truth is trying
    /// the depot key, so we try each owning account in turn - the active one first to
    /// avoid a needless switch - and rotate to the next when the action reports the app
    /// is not truly owned (<see cref="DepotAccessDeniedException"/>). When ownership is
    /// unknown we just run against whatever is logged in (prior behaviour).
    /// </summary>
    public async Task<T> RunWithOwnerAsync<T>(
        uint appId, Func<CancellationToken, Task<T>> action, CancellationToken ct = default)
    {
        var owners = _store.OwningAccounts(appId);
        if (owners.Count == 0)
        {
            // No listed owner for this app. Two cases, distinguished by whether the
            // ownership catalog has been populated at all:
            //  - Catalog is empty (nothing scanned yet): we cannot prove ownership either
            //    way, so run against whatever is logged in - prior behaviour, and the scan
            //    may still be pending (a newly-owned app appears only after a rescan).
            //  - Catalog is populated but this appid has no owner: no connected account
            //    holds any license granting the appid, so a depot key cannot succeed.
            //    Fail fast with an actionable message instead of firing a doomed depot-key
            //    request at Steam (which otherwise denies once per queued item).
            if (!_store.HasAnyOwnership())
            {
                return await action(ct);
            }

            // Operator guidance goes to the admin console/log only - never into the
            // exception message, which becomes the job's public failure text.
            _log.Warn(LogSource,
                $"App {appId} has no owning account in the catalog - sign in an account that " +
                "owns it, then run rescan-all.");
            throw new DepotAccessDeniedException(appId,
                $"No Steam account here owns this app ({appId}), so its files can't be downloaded.");
        }

        // Active account first when it is a listed owner, then the rest in catalog order
        // (OrderByDescending is stable, so equal-key entries keep their original order).
        var current = _session.AccountName;
        var order = owners
            .OrderByDescending(o => !string.IsNullOrEmpty(current) &&
                o.Equals(current, StringComparison.OrdinalIgnoreCase))
            .ToList();

        var tried = new List<string>();
        foreach (var account in order)
        {
            if (!string.Equals(_session.AccountName, account, StringComparison.OrdinalIgnoreCase))
            {
                _log.Info(LogSource, $"App {appId}: switching session to owner '{account}'");
                if (!await _session.SwitchToAccountAsync(account, ct))
                {
                    _log.Warn(LogSource, $"Could not switch to '{account}' for app {appId}; trying next owner");
                    continue;
                }
            }

            tried.Add(account);
            try
            {
                return await action(ct);
            }
            catch (DepotAccessDeniedException)
            {
                // Listed as an owner but Steam denies the depot key: a false-positive
                // license. Rotate to the next owner and retry (nothing was downloaded -
                // the denial is raised before any chunk fetch, so staging stays clean).
                _log.Warn(LogSource,
                    $"Account '{account}' cannot download app {appId} (AccessDenied); trying another owner");
            }
        }

        // Every owning account was denied (or could not be switched to). The operator
        // detail (which accounts, the free-weekend/promo/family caveat) goes to the log;
        // the exception message stays plain because it becomes the job's public failure text.
        var accounts = tried.Count > 0 ? string.Join(", ", tried) : string.Join(", ", owners);
        _log.Warn(LogSource,
            $"App {appId}: every owning account ({accounts}) was denied (AccessDenied) or unavailable - " +
            "likely a free-weekend/promo/family license that grants the app id but not download rights. " +
            "Add or sign in an account that truly owns it.");
        throw new DepotAccessDeniedException(appId,
            $"No Steam account here owns this app ({appId}) with download rights, so its files can't be downloaded.");
    }
}
