using Microsoft.Extensions.Options;

namespace SteamForge.Engine.Jobs;

/// <summary>Point-in-time disk usage for the work drive plus our scratch dir.</summary>
/// <param name="TotalBytes">Total capacity of the work drive.</param>
/// <param name="FreeBytes">Free space on the work drive.</param>
/// <param name="UsedBytes">Used space on the work drive.</param>
/// <param name="WorkDirBytes">Bytes under the ephemeral scratch/work directory (archives).</param>
/// <param name="StagingDirBytes">Bytes under the persistent staging root plus the archive
/// cache - partial/complete downloads kept so an interrupted job can resume, and finished
/// archives kept so a failed upload can be retried without recompressing; legitimate, not residue.</param>
/// <param name="MarginBytes">Configured free-space headroom that gates downloads.</param>
public sealed record StorageStatus(
    long TotalBytes, long FreeBytes, long UsedBytes, long WorkDirBytes, long StagingDirBytes, long MarginBytes)
{
    /// <summary>Free space minus the reserved margin - the effective budget for a download.</summary>
    public long AvailableForDownload => Math.Max(0, FreeBytes - MarginBytes);

    /// <summary>True if the scratch dir is holding files - a possible leak if no job is running
    /// (staging is excluded: partial downloads there are kept on purpose to resume).</summary>
    public bool WorkDirHasResidue => WorkDirBytes > 0;
}

/// <summary>
/// Reports disk usage on the work drive and the size of our scratch directory, so
/// the admin UI can monitor storage and spot residue left behind by a failed run
/// (a potential leak - the pipeline is supposed to clean up after every job).
/// </summary>
public sealed class StorageMonitor
{
    private readonly StoragePaths _paths;
    private readonly StorageOptions _options;

    public StorageMonitor(StoragePaths paths, IOptions<StorageOptions> options)
    {
        _paths = paths;
        _options = options.Value;
    }

    public StorageStatus Read()
    {
        long total = 0, free = 0;
        try
        {
            var drive = new DriveInfo(Path.GetPathRoot(Path.GetFullPath(_paths.DataDir))!);
            total = drive.TotalSize;
            free = drive.AvailableFreeSpace;
        }
        catch
        {
            // Drive info unavailable; report zeros rather than throw.
        }

        var margin = (long)(Math.Max(0, _options.MinFreeSpaceMarginGb) * 1024 * 1024 * 1024);
        return new StorageStatus(
            total, free, total - free,
            DirectorySize(_paths.WorkRoot),
            DirectorySize(_paths.StagingRoot) + DirectorySize(_paths.ArchiveRoot),
            margin);
    }

    private static long DirectorySize(string path)
    {
        try
        {
            if (!Directory.Exists(path))
            {
                return 0;
            }

            long total = 0;
            foreach (var file in Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories))
            {
                try
                {
                    total += new FileInfo(file).Length;
                }
                catch
                {
                    // File vanished mid-scan (active download); ignore.
                }
            }

            return total;
        }
        catch
        {
            return 0;
        }
    }
}
