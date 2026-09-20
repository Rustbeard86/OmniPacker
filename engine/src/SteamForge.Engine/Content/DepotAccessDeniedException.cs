namespace SteamForge.Engine.Content;

/// <summary>
/// Thrown when Steam denies a depot decryption key with AccessDenied: the active
/// account holds no download-capable license for the app. This is distinct from a
/// transient/network failure, so higher layers (see <see cref="Steam.AccountRouter"/>)
/// can rotate to another owning account instead of failing the job. The catalog's
/// ownership can include false positives - a free-weekend / promo / family-share
/// license grants the app id but not depot-key entitlement - and this exception is the
/// only reliable signal that a listed "owner" cannot actually download the content.
/// </summary>
public sealed class DepotAccessDeniedException : InvalidOperationException
{
    public DepotAccessDeniedException(uint appId, string message) : base(message)
    {
        AppId = appId;
    }

    /// <summary>The app whose depot key was denied.</summary>
    public uint AppId { get; }
}
