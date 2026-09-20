namespace SteamForge.Engine.Steam;

/// <summary>Where the Steam login currently is, for the admin UI to render.</summary>
public enum SteamAuthState
{
    /// <summary>No session; nothing in progress.</summary>
    Disconnected,

    /// <summary>Opening the connection to Steam.</summary>
    Connecting,

    /// <summary>A QR challenge URL is available; waiting for a mobile scan + approval.</summary>
    AwaitingQrScan,

    /// <summary>Steam Guard needs a code the operator must enter.</summary>
    AwaitingGuardCode,

    /// <summary>Credentials/token accepted; completing the logon handshake.</summary>
    LoggingIn,

    /// <summary>Fully logged on and ready to download.</summary>
    LoggedOn,

    /// <summary>Login failed; <see cref="SteamAuthStatus.Error"/> explains why.</summary>
    Failed,
}

/// <summary>Immutable snapshot of the Steam login, broadcast to the admin UI.</summary>
public sealed record SteamAuthStatus(
    SteamAuthState State,
    string? AccountName = null,
    string? QrChallengeUrl = null,
    string? Prompt = null,
    string? Error = null)
{
    public static readonly SteamAuthStatus Disconnected = new(SteamAuthState.Disconnected);

    public bool IsReady => State == SteamAuthState.LoggedOn;
}
