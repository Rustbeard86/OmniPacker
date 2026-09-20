namespace SteamForge.Engine.Content;

/// <summary>
/// Resolved metadata for a Steam Workshop published file, enough to drive a
/// SteamPipe depot download and to key the dedupe history.
/// </summary>
/// <param name="PublishedFileId">The Workshop item id.</param>
/// <param name="ConsumerAppId">Owning app; also the depot id for SteamPipe workshop content.</param>
/// <param name="ManifestId">hcontent_file - the depot manifest GID to download.</param>
/// <param name="Title">Human title, for naming and logs.</param>
/// <param name="FileName">Server-side filename, if any.</param>
/// <param name="FileSize">Declared size in bytes, if known.</param>
/// <param name="LegacyFileUrl">
/// Non-empty when the item is a legacy web-hosted file rather than SteamPipe
/// content (download directly over HTTP instead of via depot chunks).
/// </param>
public sealed record WorkshopItemInfo(
    ulong PublishedFileId,
    uint ConsumerAppId,
    ulong ManifestId,
    string Title,
    string? FileName,
    ulong FileSize,
    string? LegacyFileUrl)
{
    /// <summary>True when this item is downloadable via SteamPipe depot chunks.</summary>
    public bool IsSteamPipe => ManifestId != 0 && string.IsNullOrEmpty(LegacyFileUrl);
}

/// <summary>Outcome of a completed content download.</summary>
/// <param name="AppId">App the content belongs to.</param>
/// <param name="DepotId">Depot the manifest came from.</param>
/// <param name="ManifestId">Manifest GID that was downloaded.</param>
/// <param name="Directory">Local directory the files were written to.</param>
/// <param name="FileCount">Number of files written.</param>
/// <param name="TotalBytes">Total uncompressed bytes written.</param>
public sealed record ContentDownloadResult(
    uint AppId,
    uint DepotId,
    ulong ManifestId,
    string Directory,
    int FileCount,
    long TotalBytes);
