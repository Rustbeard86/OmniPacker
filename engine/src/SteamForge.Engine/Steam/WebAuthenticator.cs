using SteamKit2.Authentication;

namespace SteamForge.Engine.Steam;

/// <summary>
/// Bridges SteamKit's <see cref="IAuthenticator"/> to the web admin flow. When
/// Steam asks for a Guard code, this raises <see cref="CodeRequested"/> (which
/// puts the UI into AwaitingGuardCode) and awaits the operator's submission via
/// <see cref="SubmitCode"/>.
/// </summary>
internal sealed class WebAuthenticator : IAuthenticator
{
    private readonly object _gate = new();
    private TaskCompletionSource<string>? _pending;

    /// <summary>Raised when a code is needed. Arg is a short human prompt.</summary>
    public event Action<string>? CodeRequested;

    /// <summary>
    /// Raised when Steam is waiting for the login to be approved in the mobile app
    /// (the device-confirmation path, where no typed code is requested). Lets the UI
    /// tell the operator to check their phone instead of showing a silent spinner.
    /// </summary>
    public event Action? DeviceConfirmationRequested;

    public Task<string> GetDeviceCodeAsync(bool previousCodeWasIncorrect)
    {
        var prompt = previousCodeWasIncorrect
            ? "That Steam Guard code was rejected. Enter the current code from your authenticator app."
            : "Enter the current Steam Guard code from your authenticator app.";
        return WaitForCode(prompt);
    }

    public Task<string> GetEmailCodeAsync(string email, bool previousCodeWasIncorrect)
    {
        var prompt = previousCodeWasIncorrect
            ? $"That code was rejected. Enter the Steam Guard code emailed to {email}."
            : $"Enter the Steam Guard code emailed to {email}.";
        return WaitForCode(prompt);
    }

    /// <summary>
    /// Returning true lets SteamKit poll for a mobile-app approval instead of
    /// forcing a typed code, which is the smoother path when the operator has
    /// the Steam mobile app.
    /// </summary>
    public Task<bool> AcceptDeviceConfirmationAsync()
    {
        DeviceConfirmationRequested?.Invoke();
        return Task.FromResult(true);
    }

    /// <summary>Fulfill a pending Guard-code request from the admin UI.</summary>
    public bool SubmitCode(string code)
    {
        lock (_gate)
        {
            if (_pending is null)
            {
                return false;
            }

            var completed = _pending.TrySetResult(code.Trim());
            _pending = null;
            return completed;
        }
    }

    /// <summary>Cancel any in-flight code wait (e.g. on logout/reset).</summary>
    public void Cancel()
    {
        lock (_gate)
        {
            _pending?.TrySetCanceled();
            _pending = null;
        }
    }

    private Task<string> WaitForCode(string prompt)
    {
        TaskCompletionSource<string> tcs;
        lock (_gate)
        {
            _pending?.TrySetCanceled();
            tcs = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
            _pending = tcs;
        }

        CodeRequested?.Invoke(prompt);
        return tcs.Task;
    }
}
