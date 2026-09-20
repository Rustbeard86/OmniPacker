namespace SteamForge.Abstractions.Plugins;

/// <summary>Options controlling how an upload is exposed.</summary>
/// <param name="Password">Optional password to protect the download.</param>
/// <param name="DisplayName">Optional friendly name for the uploaded file.</param>
/// <param name="FolderName">
/// Optional friendly name for the container the file is placed in (providers that
/// support folders, e.g. gofile, use this instead of a random share code).
/// </param>
/// <param name="ExpiresAt">
/// Absolute time the upload should expire, if the user asked for one. Providers
/// that support expiry set it; the engine also purges past this time.
/// </param>
public sealed record UploadOptions(
    string? Password = null,
    string? DisplayName = null,
    string? FolderName = null,
    DateTimeOffset? ExpiresAt = null);

/// <summary>Result of an upload step.</summary>
/// <param name="Url">Public download URL.</param>
/// <param name="Password">Password required to download, if any.</param>
/// <param name="Provider">Provider id, e.g. "gofile".</param>
/// <param name="ExpiresAt">When the link is expected to expire, if known.</param>
/// <param name="ProviderRef">
/// Provider-side handle for later management (e.g. the gofile folder UUID), so
/// the admin dashboards can list, reconfigure or delete this upload.
/// </param>
public sealed record UploadResult(
    string Url,
    string? Password,
    string Provider,
    DateTimeOffset? ExpiresAt = null,
    string? ProviderRef = null);

/// <summary>
/// A plugin that uploads a finished archive to an external host (e.g. gofile)
/// and returns a shareable link, so nothing is served from the VPS itself.
/// </summary>
public interface IUploader : IPlugin
{
    /// <summary>Upload a file and return its public link.</summary>
    Task<UploadResult> UploadAsync(string filePath, UploadOptions options, IPipelineContext context);

    /// <summary>
    /// Upload one or more files into a single shared container (e.g. a gofile folder) and
    /// return the link to that container. Used for large archives split into volume parts,
    /// so a dropped transfer only re-sends the affected part rather than the whole archive.
    /// A single-element list is equivalent to <see cref="UploadAsync(string, UploadOptions, IPipelineContext)"/>.
    /// </summary>
    Task<UploadResult> UploadAsync(
        IReadOnlyList<string> filePaths, UploadOptions options, IPipelineContext context);

    /// <summary>
    /// Confirm a prior upload still exists at the provider, identified by the
    /// <paramref name="reference"/> from <see cref="UploadResult.ProviderRef"/>
    /// (or a provider share code). Returns false only when the content is
    /// definitively gone; throws on transient errors so callers can distinguish a
    /// real deletion from a blip. Used by the queue's dedupe safety fallback.
    /// </summary>
    Task<bool> ExistsAsync(string reference, CancellationToken cancellationToken = default);

    /// <summary>
    /// Delete a prior upload at the provider, identified by the
    /// <paramref name="reference"/> from <see cref="UploadResult.ProviderRef"/>
    /// (or a provider share code). Used by expiry purge and dashboard deletes.
    /// </summary>
    Task DeleteAsync(string reference, CancellationToken cancellationToken = default);
}
