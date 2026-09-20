namespace SteamForge.Engine.Content;

/// <summary>The delta from a PICS "changes since" poll.</summary>
/// <param name="CurrentChangeNumber">Steam's latest change number - the new cursor to poll from next.</param>
/// <param name="RequiresFullUpdate">True when our cursor was too old for a delta, so appinfo must be re-synced wholesale.</param>
/// <param name="ChangedAppIds">App ids Steam reported as changed since the previous cursor.</param>
public sealed record PicsChanges(
    uint CurrentChangeNumber, bool RequiresFullUpdate, IReadOnlyCollection<uint> ChangedAppIds);

/// <summary>An available branch of an app.</summary>
/// <param name="Name">Branch id (e.g. "public", "open-beta").</param>
/// <param name="BuildId">Current build id on this branch.</param>
/// <param name="PasswordRequired">True if the branch needs a beta password.</param>
public sealed record GameBranch(string Name, uint BuildId, bool PasswordRequired);

/// <summary>An app's name, available branches, and supported OS platforms (ordered
/// windows,macos,linux) for the queue branch + OS pickers.</summary>
public sealed record GameBranches(
    uint AppId, string Name, IReadOnlyList<GameBranch> Branches, IReadOnlyList<string> SupportedOs);

/// <summary>An app owned by an account (games/apps/tools/demos), for the library catalog.</summary>
/// <param name="IsFree">
/// True when the app is only granted through free packages (FreeOnDemand/NoCost) - i.e.
/// free-to-play, never paid for - so the library can filter it out.
/// </param>
/// <param name="Downloadable">
/// True when the app has downloadable content for at least one OS (see <paramref
/// name="SupportedOs"/> non-empty). False for apps with no usable depots at all -
/// delisted-and-stripped titles - so the library can hide undownloadable entries.
/// </param>
/// <param name="SupportedOs">
/// CSV of OS platforms the app can be downloaded for, ordered windows,macos,linux
/// (e.g. "windows,linux"); empty when nothing is downloadable.
/// </param>
/// <param name="LatestBuildId">
/// Current public-branch build id from appinfo (0 if unknown). Lets the browse
/// page flag an archived link as stale when a newer build has shipped since.
/// </param>
public sealed record OwnedApp(
    uint AppId, string Name, string Type, bool IsFree, bool Downloadable, string SupportedOs,
    uint LatestBuildId = 0);

/// <summary>A persisted ownership row: which account owns which app.</summary>
public sealed record OwnedAppRow(
    string Account, uint AppId, string Name, string Type, bool IsFree, bool Downloadable, string SupportedOs,
    uint LatestBuildId = 0);

/// <summary>One depot selected for a game download.</summary>
/// <param name="DepotId">Depot id.</param>
/// <param name="ManifestId">Manifest GID for the requested branch.</param>
/// <param name="Name">Human-readable depot/app name, if known.</param>
/// <param name="IsDlc">
/// True when the depot belongs to a DLC (its appinfo carries a "dlcappid"). A denied
/// DLC depot is expected (unowned extra content) and skipped; a denied NON-DLC (base
/// content) depot means the base game itself is not entitled, so the download is
/// incomplete and must not be packaged as if it were the full game.
/// </param>
public sealed record GameDepot(uint DepotId, ulong ManifestId, string? Name, bool IsDlc = false);

/// <summary>
/// Resolved download plan for a game app: the depots (and their manifests) that
/// match the target OS on the requested branch, plus the branch build id which
/// identifies the exact content for dedupe.
/// </summary>
/// <param name="AppId">Steam app id.</param>
/// <param name="Name">Store/common name of the app.</param>
/// <param name="InstallDir">Steam install-dir name (steamapps/common/&lt;dir&gt;), if any.</param>
/// <param name="BuildId">Branch build id - stable identity of the content.</param>
/// <param name="Depots">Depots to download for this app on this branch/OS.</param>
public sealed record GameInfo(
    uint AppId,
    string Name,
    string? InstallDir,
    uint BuildId,
    IReadOnlyList<GameDepot> Depots);
