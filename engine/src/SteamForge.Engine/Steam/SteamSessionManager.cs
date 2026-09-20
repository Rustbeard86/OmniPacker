using System.Collections.Concurrent;
using Microsoft.Extensions.Options;
using SteamForge.Abstractions.Logging;
using SteamKit2;
using SteamKit2.Authentication;

namespace SteamForge.Engine.Steam;

/// <summary>
/// Owns the single shared SteamKit session: connection, authentication
/// (stored-token, QR, or credentials + Steam Guard), and the callback pump the
/// rest of the engine rides on. Login progress is broadcast as
/// <see cref="SteamAuthStatus"/> so the admin UI can render the QR and Guard
/// prompts live.
/// </summary>
public sealed class SteamSessionManager : IAsyncDisposable
{
    private const string LogSource = "steam";

    private readonly SteamOptions _options;
    private readonly SteamTokenStore _tokenStore;
    private readonly ILogBroadcaster _log;

    private readonly SemaphoreSlim _loginGate = new(1, 1);
    private readonly SteamClient _client;
    private readonly CallbackManager _callbacks;
    private readonly SteamUser _user;

    private CancellationTokenSource? _pumpCts;
    private Task? _pumpTask;

    private TaskCompletionSource? _connectedTcs;
    private TaskCompletionSource<EResult>? _loggedOnTcs;
    private WebAuthenticator? _authenticator;

    // Cancels the in-flight interactive auth-session poll (QR / credentials /
    // add-account) if the shared CM connection drops. PollingWaitForResultAsync
    // does not observe a disconnect on its own, so without this a mid-flow
    // TryAnotherCM/drop parks the poll forever holding _loginGate and wedges every
    // later login with "a login is already in progress" until the process restarts.
    private volatile CancellationTokenSource? _authFlowCts;

    private SteamAuthStatus _status = SteamAuthStatus.Disconnected;
    private bool _disposed;

    // Steam routinely cycles CMs, dropping the socket without any user action. When
    // that happens we must reconnect and re-logon from the stored token on our own -
    // otherwise the session stays down and every queued job fails at resolve with a
    // cryptic AsyncJobFailedException until the container is restarted.
    //   _lastLoggedOnAccount: who to log back in as after an unexpected drop.
    //   _intentionalDisconnect: set by LogoutAsync so a deliberate teardown is NOT
    //     treated as a drop to recover from (a switch uses UserInitiated instead).
    //   _reconnecting: guards against launching more than one recovery loop.
    private volatile string? _lastLoggedOnAccount;
    private volatile bool _intentionalDisconnect;
    private volatile bool _reconnecting;

    private IReadOnlyList<SteamApps.LicenseListCallback.License> _licenses = [];
    private readonly ConcurrentDictionary<uint, ulong> _packageTokens = new();
    private volatile TaskCompletionSource _licensesReady =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    public SteamSessionManager(
        IOptions<SteamOptions> options,
        SteamTokenStore tokenStore,
        ILogBroadcaster log)
    {
        _options = options.Value;
        _tokenStore = tokenStore;
        _log = log;

        _client = new SteamClient();
        _callbacks = new CallbackManager(_client);
        _user = _client.GetHandler<SteamUser>()
            ?? throw new InvalidOperationException("SteamUser handler unavailable");

        _callbacks.Subscribe<SteamClient.ConnectedCallback>(OnConnected);
        _callbacks.Subscribe<SteamClient.DisconnectedCallback>(OnDisconnected);
        _callbacks.Subscribe<SteamUser.LoggedOnCallback>(OnLoggedOn);
        _callbacks.Subscribe<SteamUser.LoggedOffCallback>(OnLoggedOff);
        _callbacks.Subscribe<SteamApps.LicenseListCallback>(OnLicenseList);
    }

    /// <summary>Raised whenever the login status changes. Handlers must not throw.</summary>
    public event Action<SteamAuthStatus>? StatusChanged;

    public SteamAuthStatus Status => _status;

    public bool IsLoggedOn => _status.State == SteamAuthState.LoggedOn;

    /// <summary>The default account name (first configured), for token resume.</summary>
    public string? DefaultAccountName =>
        _options.Accounts.Count > 0 ? _options.Accounts[0].Username : null;

    /// <summary>The shared SteamKit client, valid once <see cref="IsLoggedOn"/>.</summary>
    public SteamClient Client => _client;

    /// <summary>Account name currently logged on, if any.</summary>
    public string? AccountName => _status.AccountName;

    /// <summary>Owned licenses for the logged-on account (populated after logon).</summary>
    public IReadOnlyList<SteamApps.LicenseListCallback.License> Licenses => _licenses;

    /// <summary>Per-package access tokens from the license list, for package PICS lookups.</summary>
    public IReadOnlyDictionary<uint, ulong> PackageTokens => _packageTokens;

    /// <summary>Completes when the license list has arrived (shortly after logon).</summary>
    public Task LicensesReady => _licensesReady.Task;

    /// <summary>Start the callback pump. Call once at host startup.</summary>
    public void StartPump()
    {
        if (_pumpTask is not null)
        {
            return;
        }

        _pumpCts = new CancellationTokenSource();
        var ct = _pumpCts.Token;
        _pumpTask = Task.Run(() =>
        {
            while (!ct.IsCancellationRequested)
            {
                _callbacks.RunWaitCallbacks(TimeSpan.FromSeconds(1));
            }
        }, ct);
    }

    /// <summary>
    /// Try a silent login using a previously stored refresh token for the
    /// default account. Returns false (and leaves status Disconnected) if there
    /// is no token or it is no longer valid, so the caller can offer QR login.
    /// </summary>
    public async Task<bool> TryResumeAsync(CancellationToken ct = default)
    {
        // Prefer the configured account's token; otherwise resume the most recent
        // stored one. QR login (the primary path) configures no username, so the
        // token is keyed by the Steam-returned account name, not by app config -
        // looking it up by the configured username alone would never find it.
        var account = DefaultAccountName;
        var token = (!string.IsNullOrWhiteSpace(account) ? _tokenStore.Get(account) : null)
            ?? _tokenStore.GetMostRecent();
        if (token is null)
        {
            return false;
        }

        await _loginGate.WaitAsync(ct);
        try
        {
            _log.Info(LogSource, $"Resuming Steam session for '{token.AccountName}' from stored token");

            const int maxAttempts = 4;
            for (var attempt = 1; attempt <= maxAttempts; attempt++)
            {
                EResult result;
                try
                {
                    await ConnectAsync(ct);
                    result = await LogOnWithTokenAsync(token.AccountName, token.RefreshToken, ct);
                }
                catch (IOException) when (attempt < maxAttempts)
                {
                    // Dropped mid-logon (Steam cycling CMs); reconnect and retry.
                    await Task.Delay(TimeSpan.FromSeconds(1), ct);
                    continue;
                }

                if (result == EResult.OK)
                {
                    return true;
                }

                // TryAnotherCM / ServiceUnavailable are Steam load-balancing, not a
                // bad token - reconnect (SteamKit picks another CM) and retry rather
                // than discarding perfectly good credentials.
                if (IsTransient(result) && attempt < maxAttempts)
                {
                    _log.Info(LogSource, $"Steam redirected ({result}); retrying on another CM");
                    await Task.Delay(TimeSpan.FromSeconds(1), ct);
                    continue;
                }

                if (IsTransient(result))
                {
                    _log.Warn(LogSource, $"Steam still redirecting ({result}); will retry on next start");
                    break;
                }

                // A genuine rejection means the refresh token is no longer usable.
                _log.Warn(LogSource, $"Stored token rejected ({result}); QR login required");
                _tokenStore.Remove(token.AccountName);
                break;
            }

            SetStatus(SteamAuthStatus.Disconnected);
            return false;
        }
        catch (Exception ex)
        {
            _log.Warn(LogSource, $"Token resume failed: {ex.Message}");
            SetStatus(SteamAuthStatus.Disconnected);
            return false;
        }
        finally
        {
            _loginGate.Release();
        }
    }

    // Steam load-balancing / availability results that a stored token should be
    // retried through, never deleted for.
    private static bool IsTransient(EResult result) =>
        result is EResult.TryAnotherCM or EResult.ServiceUnavailable or EResult.Busy;

    /// <summary>Stored accounts we can silently swap the session between.</summary>
    public IReadOnlyList<SteamToken> StoredAccounts => _tokenStore.ListTokens();

    /// <summary>
    /// Swap the single session to a stored account (log off, reconnect, log on with
    /// its saved token). No-op if already logged on as that account. The queue is
    /// FIFO/single-worker, so only one account is ever active at a time.
    /// </summary>
    public async Task<bool> SwitchToAccountAsync(string account, CancellationToken ct = default)
    {
        if (IsLoggedOn && string.Equals(AccountName, account, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        var token = _tokenStore.Get(account);
        if (token is null)
        {
            _log.Warn(LogSource, $"No stored token for '{account}' - cannot switch");
            return false;
        }

        await _loginGate.WaitAsync(ct);
        try
        {
            _log.Info(LogSource, $"Switching Steam session to '{account}'");

            // NB: the license-readiness gate is reset in OnLoggedOn (scoped to the new
            // session), not here. Resetting it before the old session is torn down let a
            // still-queued LicenseListCallback from the PREVIOUS account satisfy this
            // switch's gate, so the rescan enumerated one account's library and persisted
            // it under another account's name (phantom ownership rows).
            if (_client.IsConnected)
            {
                _client.Disconnect();
                await Task.Delay(TimeSpan.FromMilliseconds(500), ct);
            }

            await ConnectAsync(ct);
            var result = await LogOnWithTokenAsync(token.AccountName, token.RefreshToken, ct);
            if (result == EResult.OK)
            {
                // Don't report the switch complete until THIS account's own license list
                // has arrived: callers (library rescan) immediately enumerate ownership,
                // and enumerating against a stale/empty list mis-attributes the catalog.
                try
                {
                    await _licensesReady.Task.WaitAsync(TimeSpan.FromSeconds(30), ct);
                }
                catch (TimeoutException)
                {
                    _log.Warn(LogSource, $"Switch to '{account}': license list not received within 30s; ownership scan may be incomplete");
                }

                return true;
            }

            _log.Warn(LogSource, $"Switch to '{account}' rejected: {result}");
            SetStatus(SteamAuthStatus.Disconnected);
            return false;
        }
        catch (Exception ex)
        {
            _log.Warn(LogSource, $"Switch to '{account}' failed: {ex.Message}");
            SetStatus(SteamAuthStatus.Disconnected);
            return false;
        }
        finally
        {
            _loginGate.Release();
        }
    }

    /// <summary>
    /// Run an interactive auth flow (QR / credentials / add-account) under a
    /// cancellation token that <see cref="OnDisconnected"/> trips if the shared CM
    /// connection drops mid-flow. The flow's poll then unwinds (throwing
    /// <see cref="OperationCanceledException"/>) instead of parking forever, so the
    /// caller's finally releases <see cref="_loginGate"/>. Only one interactive flow
    /// runs at a time (the gate is held by the caller), so a single field suffices.
    /// </summary>
    private async Task<T> RunAuthFlowAsync<T>(Func<CancellationToken, Task<T>> flow, CancellationToken ct)
    {
        var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        _authFlowCts = cts;
        try
        {
            return await flow(cts.Token);
        }
        finally
        {
            // Clear the field before disposing so a concurrent OnDisconnected sees
            // null (or catches ObjectDisposedException) rather than touching a live-
            // then-disposed source.
            _authFlowCts = null;
            cts.Dispose();
        }
    }

    /// <summary>Begin an interactive QR login for the given (or default) account.</summary>
    public async Task StartQrLoginAsync(CancellationToken ct = default)
    {
        if (!await _loginGate.WaitAsync(0, ct))
        {
            _log.Warn(LogSource, "A login is already in progress");
            return;
        }

        try
        {
            await RunAuthFlowAsync(async flowCt =>
            {
                SetStatus(new SteamAuthStatus(SteamAuthState.Connecting));
                await ConnectAsync(flowCt);

                var details = BuildAuthDetails(username: null, password: null, account: DefaultAccountName);
                var session = await _client.Authentication.BeginAuthSessionViaQRAsync(details);

                void PublishChallenge() =>
                    SetStatus(new SteamAuthStatus(SteamAuthState.AwaitingQrScan, QrChallengeUrl: session.ChallengeURL));

                session.ChallengeURLChanged = PublishChallenge;
                PublishChallenge();
                _log.Info(LogSource, "QR challenge issued; scan with the Steam mobile app");

                var poll = await session.PollingWaitForResultAsync(flowCt);
                await CompleteLoginFromPollAsync(poll, flowCt);
                return true;
            }, ct);
        }
        catch (OperationCanceledException)
        {
            SetStatus(SteamAuthStatus.Disconnected);
        }
        catch (Exception ex)
        {
            Fail($"QR login failed: {ex.Message}");
        }
        finally
        {
            _loginGate.Release();
        }
    }

    /// <summary>Begin a username/password login, prompting for Guard codes via the UI.</summary>
    public async Task StartCredentialLoginAsync(string username, string password, CancellationToken ct = default)
    {
        if (!await _loginGate.WaitAsync(0, ct))
        {
            _log.Warn(LogSource, "A login is already in progress");
            return;
        }

        try
        {
            await RunAuthFlowAsync(async flowCt =>
            {
                SetStatus(new SteamAuthStatus(SteamAuthState.Connecting));
                await ConnectAsync(flowCt);

                _authenticator = new WebAuthenticator();
                _authenticator.CodeRequested += prompt =>
                    SetStatus(new SteamAuthStatus(SteamAuthState.AwaitingGuardCode, AccountName: username, Prompt: prompt));

                var details = BuildAuthDetails(username, password, account: username);
                details.Authenticator = _authenticator;

                var session = await _client.Authentication.BeginAuthSessionViaCredentialsAsync(details);
                var poll = await session.PollingWaitForResultAsync(flowCt);
                await CompleteLoginFromPollAsync(poll, flowCt);
                return true;
            }, ct);
        }
        catch (OperationCanceledException)
        {
            SetStatus(SteamAuthStatus.Disconnected);
        }
        catch (Exception ex)
        {
            Fail($"Credential login failed: {ex.Message}");
        }
        finally
        {
            _authenticator = null;
            _loginGate.Release();
        }
    }

    /// <summary>
    /// Authenticate a NEW account by username/password (+ Steam Guard) and store its
    /// refresh token WITHOUT disturbing the currently logged-on session. Unlike
    /// <see cref="StartCredentialLoginAsync"/> this never calls LogOn, so the active
    /// account stays online; the new account simply joins <see cref="StoredAccounts"/>
    /// and can be switched to (or swapped to for a download) later. The Guard prompt is
    /// surfaced via <paramref name="onGuardPrompt"/> and answered with
    /// <see cref="SubmitGuardCode"/>, the same submit path the login flow uses. Returns
    /// the added account name on success, or null if it failed / was cancelled.
    /// </summary>
    public async Task<string?> AddAccountViaCredentialsAsync(
        string username,
        string password,
        Action<string>? onGuardPrompt = null,
        Action? onDeviceConfirmation = null,
        CancellationToken ct = default)
    {
        if (!await _loginGate.WaitAsync(0, ct))
        {
            _log.Warn(LogSource, "A login is already in progress");
            return null;
        }

        try
        {
            return await RunAuthFlowAsync<string?>(async flowCt =>
            {
                // Authentication rides the shared CM connection but is independent of the
                // logged-on session, so we can enrol a second account while the first
                // stays online. We deliberately do NOT touch _status or call LogOn here.
                await ConnectAsync(flowCt);

                _authenticator = new WebAuthenticator();
                if (onGuardPrompt is not null)
                {
                    _authenticator.CodeRequested += onGuardPrompt;
                }

                if (onDeviceConfirmation is not null)
                {
                    _authenticator.DeviceConfirmationRequested += onDeviceConfirmation;
                }

                _authenticator.DeviceConfirmationRequested += () =>
                    _log.Info(LogSource, $"Waiting for '{username}' to approve the login in the Steam mobile app");

                var details = BuildAuthDetails(username, password, account: username);
                details.Authenticator = _authenticator;

                _log.Info(LogSource, $"Authenticating new account '{username}' (active session stays online)");
                var session = await _client.Authentication.BeginAuthSessionViaCredentialsAsync(details);
                var poll = await session.PollingWaitForResultAsync(flowCt);

                _tokenStore.Save(new SteamToken(
                    poll.AccountName,
                    poll.RefreshToken,
                    poll.NewGuardData,
                    DateTimeOffset.UtcNow));
                _log.Info(LogSource, $"Added account '{poll.AccountName}'; refresh token stored (active session unchanged)");
                return poll.AccountName;
            }, ct);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            // The operator did not cancel; the shared CM connection dropped mid-add
            // (e.g. a TryAnotherCM redirect) and unwound the poll. Surface it as a
            // transient, retryable condition instead of a silent failure.
            _log.Warn(LogSource, $"Add account '{username}' interrupted: Steam dropped the connection (try again).");
            return null;
        }
        catch (OperationCanceledException)
        {
            return null;
        }
        catch (Exception ex)
        {
            _log.Error(LogSource, $"Add account failed: {ex.Message}");
            return null;
        }
        finally
        {
            _authenticator = null;
            _loginGate.Release();
        }
    }

    /// <summary>Submit a Steam Guard code the UI collected for an in-flight login.</summary>
    public bool SubmitGuardCode(string code) => _authenticator?.SubmitCode(code) ?? false;

    /// <summary>
    /// Run <paramref name="action"/> while holding the login gate, so an interactive
    /// login (QR/credentials) or account switch cannot change the session underneath
    /// it. The ownership scanner uses this: without it, a second QR login started while
    /// a scan is in flight tears the session down mid-scan (failed scan + a spurious
    /// InvalidName logon). Interactive logins use a non-blocking gate acquire, so while
    /// a scan holds it they are cleanly refused ("a login is already in progress")
    /// rather than racing.
    /// </summary>
    public async Task WithLoginLockAsync(Func<CancellationToken, Task> action, CancellationToken ct = default)
    {
        await _loginGate.WaitAsync(ct);
        try
        {
            await action(ct);
        }
        finally
        {
            _loginGate.Release();
        }
    }

    /// <summary>Log off and disconnect.</summary>
    public Task LogoutAsync()
    {
        // Mark this teardown deliberate so OnDisconnected does not try to recover it.
        _intentionalDisconnect = true;
        _lastLoggedOnAccount = null;
        _authenticator?.Cancel();
        if (_client.IsConnected)
        {
            _user.LogOff();
        }

        SetStatus(SteamAuthStatus.Disconnected);
        _log.Info(LogSource, "Logged out");
        return Task.CompletedTask;
    }

    private AuthSessionDetails BuildAuthDetails(string? username, string? password, string? account)
    {
        var guardData = account is null ? null : _tokenStore.Get(account)?.GuardData;
        return new AuthSessionDetails
        {
            Username = username,
            Password = password,
            DeviceFriendlyName = _options.DeviceFriendlyName,
            IsPersistentSession = true,
            GuardData = guardData,
        };
    }

    private async Task CompleteLoginFromPollAsync(AuthPollResult poll, CancellationToken ct)
    {
        _tokenStore.Save(new SteamToken(
            poll.AccountName,
            poll.RefreshToken,
            poll.NewGuardData,
            DateTimeOffset.UtcNow));
        _log.Info(LogSource, $"Authenticated as '{poll.AccountName}'; refresh token stored");

        var result = await LogOnWithTokenAsync(poll.AccountName, poll.RefreshToken, ct);
        if (result != EResult.OK)
        {
            Fail($"Steam logon rejected: {result}");
        }
    }

    private async Task<EResult> LogOnWithTokenAsync(string accountName, string refreshToken, CancellationToken ct)
    {
        SetStatus(new SteamAuthStatus(SteamAuthState.LoggingIn, AccountName: accountName));
        _loggedOnTcs = new TaskCompletionSource<EResult>(TaskCreationOptions.RunContinuationsAsynchronously);

        _user.LogOn(new SteamUser.LogOnDetails
        {
            Username = accountName,
            AccessToken = refreshToken,
            ShouldRememberPassword = true,
        });

        var result = await _loggedOnTcs.Task.WaitAsync(ct);
        if (result == EResult.OK)
        {
            _lastLoggedOnAccount = accountName;
            SetStatus(new SteamAuthStatus(SteamAuthState.LoggedOn, AccountName: accountName));
            _log.Info(LogSource, $"Logged on to Steam as '{accountName}'");
        }

        return result;
    }

    private async Task ConnectAsync(CancellationToken ct)
    {
        // Any deliberate connect attempt supersedes a prior LogoutAsync, so a later
        // unexpected drop is again eligible for automatic recovery.
        _intentionalDisconnect = false;

        if (_client.IsConnected)
        {
            return;
        }

        _connectedTcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _client.Connect();
        await _connectedTcs.Task.WaitAsync(TimeSpan.FromSeconds(30), ct);
    }

    private void OnConnected(SteamClient.ConnectedCallback _)
    {
        _log.Info(LogSource, "Connected to Steam");
        _connectedTcs?.TrySetResult();
    }

    private void OnDisconnected(SteamClient.DisconnectedCallback callback)
    {
        _log.Info(LogSource, callback.UserInitiated
            ? "Disconnected from Steam"
            : "Lost connection to Steam");
        _connectedTcs?.TrySetException(new IOException("Disconnected before connect completed"));
        _loggedOnTcs?.TrySetException(new IOException("Disconnected during logon"));

        // Unwedge an in-flight interactive auth poll: PollingWaitForResultAsync keeps
        // polling a dead connection and would otherwise hold _loginGate forever (a
        // mid-add TryAnotherCM was observed to wedge every later login). Guarded
        // because the flow's finally may have disposed the source concurrently.
        try
        {
            _authFlowCts?.Cancel();
        }
        catch (ObjectDisposedException)
        {
        }

        // Unexpected drop (Steam cycling CMs, a network blip) while we held a live
        // session - not a deliberate logout (_intentionalDisconnect) or an account
        // switch (UserInitiated). Downgrade the status so IsLoggedOn stops reporting a
        // live session on a dead socket (the queue would otherwise keep issuing Steam
        // calls that fail with AsyncJobFailedException), then recover in the background
        // from the stored token. OnDisconnected runs on the single callback-pump thread,
        // so this check-and-set of _reconnecting is not racing another OnDisconnected.
        if (!callback.UserInitiated && !_intentionalDisconnect && !_disposed
            && _lastLoggedOnAccount is { } account && !_reconnecting)
        {
            _reconnecting = true;
            if (_status.State == SteamAuthState.LoggedOn)
            {
                SetStatus(new SteamAuthStatus(SteamAuthState.Connecting, AccountName: account));
            }

            _ = Task.Run(ReconnectLoopAsync);
        }
    }

    /// <summary>
    /// Rebuild a lost session from the stored token, with capped exponential backoff,
    /// until it is logged back on, the token is rejected, or the app is shutting down /
    /// was logged out deliberately. Serialized against interactive logins and account
    /// switches via <see cref="_loginGate"/> so it never tears down a login in progress,
    /// and re-logs-in as <see cref="_lastLoggedOnAccount"/> (which a switch updates), so
    /// it restores whichever account was last active rather than fighting the router.
    /// </summary>
    private async Task ReconnectLoopAsync()
    {
        var delay = TimeSpan.FromSeconds(2);
        try
        {
            while (!_disposed && !_intentionalDisconnect)
            {
                var account = _lastLoggedOnAccount;
                if (account is null)
                {
                    return; // logged out while we were backing off
                }

                var token = _tokenStore.Get(account) ?? _tokenStore.GetMostRecent();
                if (token is null)
                {
                    _log.Warn(LogSource, "Steam session lost and no stored token to restore it; QR login required");
                    SetStatus(SteamAuthStatus.Disconnected);
                    return;
                }

                await _loginGate.WaitAsync();
                try
                {
                    if (_disposed || _intentionalDisconnect)
                    {
                        return;
                    }

                    if (IsLoggedOn && _client.IsConnected)
                    {
                        return; // an interactive login or switch already restored it
                    }

                    _log.Info(LogSource, $"Reconnecting to Steam as '{token.AccountName}'...");
                    await ConnectAsync(CancellationToken.None);
                    var result = await LogOnWithTokenAsync(token.AccountName, token.RefreshToken, CancellationToken.None);
                    if (result == EResult.OK)
                    {
                        _log.Info(LogSource, "Steam session restored");
                        return;
                    }

                    if (!IsTransient(result))
                    {
                        _log.Warn(LogSource, $"Stored token rejected on reconnect ({result}); QR login required");
                        _tokenStore.Remove(token.AccountName);
                        SetStatus(SteamAuthStatus.Disconnected);
                        return;
                    }

                    _log.Info(LogSource, $"Steam still unavailable ({result}); retrying in {delay.TotalSeconds:0}s");
                }
                catch (Exception ex)
                {
                    // A drop mid-reconnect throws (IOException from the TCS); just back off.
                    _log.Warn(LogSource, $"Reconnect attempt failed: {ex.Message}; retrying in {delay.TotalSeconds:0}s");
                }
                finally
                {
                    _loginGate.Release();
                }

                try
                {
                    await Task.Delay(delay);
                }
                catch
                {
                    // shutting down
                }

                delay = TimeSpan.FromSeconds(Math.Min(delay.TotalSeconds * 2, 30));
            }
        }
        finally
        {
            _reconnecting = false;
        }
    }

    private void OnLoggedOn(SteamUser.LoggedOnCallback callback)
    {
        if (callback.Result != EResult.OK)
        {
            _log.Warn(LogSource, $"Logon result: {callback.Result} ({callback.ExtendedResult})");
        }
        else
        {
            // A new session is established: scope a fresh license-readiness gate to it.
            // OnLoggedOn and OnLicenseList are both dispatched on the single callback-pump
            // thread, so any LicenseListCallback still queued from the PREVIOUS account was
            // already delivered before this point and cannot satisfy the new gate - only
            // this session's own license list (delivered next) will. This is what prevents
            // one account's library from being attributed to another during a switch.
            _licenses = [];
            _packageTokens.Clear();
            _licensesReady = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        }

        _loggedOnTcs?.TrySetResult(callback.Result);
    }

    private void OnLicenseList(SteamApps.LicenseListCallback callback)
    {
        if (callback.Result != EResult.OK)
        {
            _log.Warn(LogSource, $"License list result: {callback.Result}");
            return;
        }

        _licenses = callback.LicenseList;
        foreach (var license in callback.LicenseList)
        {
            _packageTokens[license.PackageID] = license.AccessToken;
        }

        _log.Info(LogSource, $"Received {callback.LicenseList.Count} licenses");
        _licensesReady.TrySetResult();
    }

    private void OnLoggedOff(SteamUser.LoggedOffCallback callback)
    {
        _log.Info(LogSource, $"Logged off from Steam: {callback.Result}");
        if (_status.State == SteamAuthState.LoggedOn)
        {
            SetStatus(SteamAuthStatus.Disconnected);
        }
    }

    private void Fail(string message)
    {
        _log.Error(LogSource, message);
        SetStatus(new SteamAuthStatus(SteamAuthState.Failed, Error: message));
    }

    private void SetStatus(SteamAuthStatus status)
    {
        _status = status;
        foreach (var handler in StatusChanged?.GetInvocationList() ?? [])
        {
            try
            {
                ((Action<SteamAuthStatus>)handler)(status);
            }
            catch
            {
                // A subscriber must never break status fan-out.
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _authenticator?.Cancel();
        if (_pumpCts is not null)
        {
            await _pumpCts.CancelAsync();
        }

        if (_pumpTask is not null)
        {
            try
            {
                await _pumpTask;
            }
            catch (OperationCanceledException)
            {
            }
        }

        if (_client.IsConnected)
        {
            _client.Disconnect();
        }

        _pumpCts?.Dispose();
        _loginGate.Dispose();
    }
}
