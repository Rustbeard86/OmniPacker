namespace SteamForge.Abstractions.Plugins;

/// <summary>What kind of Steam content a request targets.</summary>
public enum ContentKind
{
    WorkshopItem,
    Game,
}

/// <summary>A request to fetch content into the job's working directory.</summary>
/// <param name="Kind">Workshop item or full game.</param>
/// <param name="AppId">Steam app id.</param>
/// <param name="WorkshopId">Workshop published-file id (for <see cref="ContentKind.WorkshopItem"/>).</param>
/// <param name="DisplayName">Human label for naming and logs, if known.</param>
public sealed record DownloadRequest(
    ContentKind Kind,
    uint AppId,
    ulong WorkshopId = 0,
    string? DisplayName = null,
    string? Branch = null,
    string? TargetOs = null);

/// <summary>Result of a content-source download.</summary>
/// <param name="OutputDirectory">Directory the content was written to.</param>
/// <param name="AppId">App the content belongs to.</param>
/// <param name="DepotId">Depot the content came from.</param>
/// <param name="ManifestId">
/// Manifest GID identifying the exact bytes - the key for dedupe history.
/// </param>
/// <param name="FileCount">Number of files written.</param>
/// <param name="TotalBytes">Total uncompressed bytes written.</param>
public sealed record ContentResult(
    string OutputDirectory,
    uint AppId,
    uint DepotId,
    ulong ManifestId,
    int FileCount,
    long TotalBytes);

/// <summary>
/// Lightweight identity of the content behind a request, resolved without
/// downloading. The manifest id lets the queue dedupe against prior uploads.
/// </summary>
public sealed record ContentIdentity(
    uint AppId,
    uint DepotId,
    ulong ManifestId,
    string DisplayName);

/// <summary>
/// A plugin that fetches Steam content (Workshop item, game, ...) using the
/// engine's SteamKit2 content client.
/// </summary>
public interface IContentSource : IPlugin
{
    /// <summary>True if this source can service the given request.</summary>
    bool CanHandle(DownloadRequest request);

    /// <summary>Resolve app/depot/manifest for a request without downloading.</summary>
    Task<ContentIdentity> ResolveAsync(DownloadRequest request, IPipelineContext context);

    /// <summary>Download the requested content into the job working directory.</summary>
    Task<ContentResult> DownloadAsync(DownloadRequest request, IPipelineContext context);
}
