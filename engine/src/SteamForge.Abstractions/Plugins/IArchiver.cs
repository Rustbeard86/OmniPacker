namespace SteamForge.Abstractions.Plugins;

/// <summary>Result of an archive step.</summary>
/// <param name="ArchivePath">Path to the produced archive file.</param>
/// <param name="SizeBytes">Archive size on disk.</param>
public sealed record ArchiveResult(string ArchivePath, long SizeBytes);

/// <summary>
/// A plugin that packs a directory into a single distributable archive
/// (e.g. native 7-Zip with high compression).
/// </summary>
public interface IArchiver : IPlugin
{
    /// <summary>File extension this archiver produces, without a dot (e.g. "7z", "zip").</summary>
    string Extension { get; }

    /// <summary>Pack <paramref name="sourceDirectory"/> into an archive at <paramref name="outputPath"/>.</summary>
    Task<ArchiveResult> ArchiveAsync(string sourceDirectory, string outputPath, IPipelineContext context);
}
