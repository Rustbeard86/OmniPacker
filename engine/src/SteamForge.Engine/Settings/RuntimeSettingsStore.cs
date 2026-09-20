using System.Text.Json;
using Microsoft.Extensions.Configuration;
using SteamForge.Engine.Jobs;

namespace SteamForge.Engine.Settings;

/// <summary>
/// Operator-editable settings that must take effect at runtime (from the admin
/// Settings page) without a redeploy. Seeded from configuration on first run, then
/// persisted to settings.json on the data volume and read live by their consumers.
/// </summary>
public sealed record RuntimeSettings
{
    /// <summary>Comma/space-separated client IPs allowed to reach the admin surface;
    /// empty means the allow-list is off. Matched on the real client IP.</summary>
    public string AdminAllowedIpsCsv { get; init; } = "";

    /// <summary>Headroom in GiB that must stay free after a download (disk guard).</summary>
    public double MinFreeSpaceMarginGb { get; init; } = 10;

    /// <summary>Default Gofile link lifetime in hours for new jobs; 0 = no expiry.</summary>
    public int GofileDefaultExpiryHours { get; init; } = 72;

    /// <summary>
    /// Ceiling for concurrent depot-chunk fetches per download. The engine adaptively
    /// ramps up to this then holds ~10% below the saturating level. Runtime-editable so
    /// the VPS link can be tuned without a redeploy; clamped to a sane range live.
    /// </summary>
    public int MaxParallelDownloads { get; init; } = 32;

    /// <summary>7-Zip compression method: "zstd" (fast, multi-threaded; needs a zstd
    /// build - the VPS image has one) or "lzma2" (universal, higher ratio, much slower).</summary>
    public string SevenZipMethod { get; init; } = "zstd";

    /// <summary>7-Zip compression level (0-9 for lzma2, 0-22 for zstd).</summary>
    public int SevenZipLevel { get; init; } = 19;

    /// <summary>
    /// Public-page anti-flood: max queue submissions a single client IP may make per
    /// minute. One user action (a bulk/collection submit that fans out to many items)
    /// counts once. 0 disables the per-minute limit. Admins (IP-gated) are never limited.
    /// </summary>
    public int PublicRatePerMinute { get; init; } = 15;

    /// <summary>Public-page anti-flood: max queue submissions per client IP per hour.
    /// 0 disables the per-hour limit.</summary>
    public int PublicRatePerHour { get; init; } = 100;

    /// <summary>
    /// Public-page resource budget: total downloaded bytes a single client IP may spend,
    /// expressed in GiB, over a rolling 24h window. Charged by each job's actual download
    /// size (a reused/deduped upload costs nothing); once the window total reaches this,
    /// further requests from that IP are refused until it drains. 0 disables the budget.
    /// </summary>
    public double PublicDailyBudgetGb { get; init; } = 100;

    /// <summary>
    /// How long, in hours, a failed job's downloaded staging and cached archive are kept
    /// so a Retry re-uploads the same archive (or resumes the download) instead of
    /// re-fetching and recompressing many GB from scratch. Startup GC still reclaims them
    /// once this window passes, and an explicit job delete frees them immediately. 0 keeps
    /// the old behavior (a failed job's on-disk work is reclaimed on the next restart).
    /// </summary>
    public int FailedRetentionHours { get; init; } = 48;

    /// <summary>
    /// Archives larger than this many GiB are split into fixed-size volumes for upload, so
    /// a dropped connection only loses the current part instead of restarting a multi-tens-
    /// of-GB single POST (Gofile has no resumable upload). 0 disables splitting.
    /// </summary>
    public double UploadVolumeThresholdGb { get; init; } = 20;

    /// <summary>Size, in GiB, of each upload volume when an archive is split (see
    /// <see cref="UploadVolumeThresholdGb"/>). Must be smaller than the threshold to
    /// actually produce multiple parts.</summary>
    public double UploadVolumeSizeGb { get; init; } = 10;

    /// <summary>Parsed <see cref="AdminAllowedIpsCsv"/>; empty means allow-list off.</summary>
    public IReadOnlySet<string> AllowedIps() =>
        AdminAllowedIpsCsv
            .Split([',', ' ', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
}

/// <summary>
/// Thread-safe store for <see cref="RuntimeSettings"/>. Loads settings.json if present,
/// otherwise seeds from the app's configuration (env/appsettings) so existing deploys
/// keep their values. Every update is persisted atomically.
/// </summary>
public sealed class RuntimeSettingsStore
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    private readonly string _path;
    private readonly object _gate = new();
    private RuntimeSettings _current;

    public RuntimeSettingsStore(StoragePaths paths, IConfiguration config)
    {
        Directory.CreateDirectory(paths.DataDir);
        _path = Path.Combine(paths.DataDir, "settings.json");
        _current = Load() ?? SeedFromConfig(config);
        Persist(); // materialize the file on first run
    }

    public RuntimeSettings Current
    {
        get { lock (_gate) { return _current; } }
    }

    public void Update(RuntimeSettings settings)
    {
        lock (_gate)
        {
            _current = settings;
            Persist();
        }
    }

    private static RuntimeSettings SeedFromConfig(IConfiguration config) => new()
    {
        AdminAllowedIpsCsv = config["Admin:AllowedIpsCsv"] ?? "",
        MinFreeSpaceMarginGb = config.GetValue("Storage:MinFreeSpaceMarginGb", 10d),
        GofileDefaultExpiryHours = config.GetValue("Gofile:DefaultExpiryHours", 72),
        MaxParallelDownloads = config.GetValue("Storage:MaxParallelDownloads", 32),
        SevenZipMethod = config["SevenZip:Method"] is { Length: > 0 } m ? m : "zstd",
        SevenZipLevel = config.GetValue("SevenZip:CompressionLevel", 19),
        PublicRatePerMinute = config.GetValue("Public:RatePerMinute", 15),
        PublicRatePerHour = config.GetValue("Public:RatePerHour", 100),
        PublicDailyBudgetGb = config.GetValue("Public:DailyBudgetGb", 100d),
        FailedRetentionHours = config.GetValue("Storage:FailedRetentionHours", 48),
        UploadVolumeThresholdGb = config.GetValue("Upload:VolumeThresholdGb", 20d),
        UploadVolumeSizeGb = config.GetValue("Upload:VolumeSizeGb", 10d),
    };

    private RuntimeSettings? Load()
    {
        if (!File.Exists(_path))
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize<RuntimeSettings>(File.ReadAllText(_path));
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private void Persist()
    {
        var tmp = _path + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(_current, JsonOptions));
        File.Move(tmp, _path, overwrite: true);
    }
}
