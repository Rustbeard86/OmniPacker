using System.Text;

namespace SteamForge.Engine.Content;

/// <summary>One depot recorded in a generated appmanifest's InstalledDepots block.</summary>
/// <param name="DepotId">Depot id.</param>
/// <param name="ManifestId">Manifest GID installed for that depot.</param>
/// <param name="Size">Installed (uncompressed) size of the depot, in bytes.</param>
public sealed record InstalledDepot(uint DepotId, ulong ManifestId, long Size);

/// <summary>
/// Builds a Steam <c>appmanifest_&lt;appid&gt;.acf</c> (Valve VDF text) for a
/// downloaded game so the archive is a drop-in install that Steam recognizes.
///
/// PRIVACY: we synthesize this file ourselves, so it must never carry anything that
/// fingerprints the account or client that produced the download. Every identity and
/// timing marker a real Steam-written manifest contains is zeroed here by
/// construction - most importantly <c>LastOwner</c> (the installing account's
/// SteamID64), and all timestamps (<c>LastUpdated</c>, <c>LastPlayed</c>) and
/// progress/byte counters. No account language/preference blocks are emitted at all
/// (Steam recreates <c>UserConfig</c>/<c>MountedConfig</c> on first run), so the
/// manifest is neutral. If you add a field, keep it non-identifying.
/// </summary>
public static class SteamAppManifest
{
    /// <summary>The conventional file name for an app's manifest.</summary>
    public static string FileNameFor(uint appId) => $"appmanifest_{appId}.acf";

    /// <summary>
    /// Write a sanitized manifest into <paramref name="installRoot"/> (the archive
    /// root), returning the file path written.
    /// </summary>
    public static async Task<string> WriteAsync(
        string installRoot,
        uint appId,
        string name,
        string? installDir,
        uint buildId,
        long sizeOnDisk,
        IReadOnlyList<InstalledDepot> depots,
        CancellationToken ct = default)
    {
        var text = Build(appId, name, installDir, buildId, sizeOnDisk, depots);
        var path = Path.Combine(installRoot, FileNameFor(appId));
        // UTF-8 without BOM, matching how the Steam client writes .acf files.
        await File.WriteAllTextAsync(path, text, new UTF8Encoding(false), ct);
        return path;
    }

    /// <summary>Build the sanitized VDF text (exposed for testing).</summary>
    public static string Build(
        uint appId, string name, string? installDir, uint buildId, long sizeOnDisk,
        IReadOnlyList<InstalledDepot> depots)
    {
        var sb = new StringBuilder();
        sb.Append("\"AppState\"\n{\n");

        void Kv(string key, string value) =>
            sb.Append('\t').Append('"').Append(key).Append("\"\t\t\"").Append(Escape(value)).Append("\"\n");

        Kv("appid", appId.ToString());
        Kv("Universe", "1");
        Kv("name", name);
        Kv("StateFlags", "4"); // StateFullyInstalled
        if (!string.IsNullOrWhiteSpace(installDir))
        {
            Kv("installdir", installDir);
        }

        // Timing markers - zeroed. A real manifest stamps wall-clock times here.
        Kv("LastUpdated", "0");
        Kv("LastPlayed", "0");

        Kv("SizeOnDisk", sizeOnDisk.ToString());
        Kv("StagingSize", "0");
        Kv("buildid", buildId.ToString());

        // Account marker - zeroed. A real manifest stores the installing account's
        // SteamID64 here; that is exactly what we must not redistribute.
        Kv("LastOwner", "0");

        // Update/progress bookkeeping - all zeroed (nothing pending; not identifying).
        Kv("UpdateResult", "0");
        Kv("BytesToDownload", "0");
        Kv("BytesDownloaded", "0");
        Kv("BytesToStage", "0");
        Kv("BytesStaged", "0");
        Kv("TargetBuildID", buildId.ToString()); // a build number, not a marker - keep it real
        Kv("AutoUpdateBehavior", "0");
        Kv("AllowOtherDownloadsWhileRunning", "0");
        Kv("ScheduledAutoUpdate", "0");

        sb.Append("\t\"InstalledDepots\"\n\t{\n");
        foreach (var d in depots.OrderBy(d => d.DepotId))
        {
            sb.Append("\t\t\"").Append(d.DepotId).Append("\"\n\t\t{\n");
            sb.Append("\t\t\t\"manifest\"\t\t\"").Append(d.ManifestId).Append("\"\n");
            sb.Append("\t\t\t\"size\"\t\t\"").Append(d.Size).Append("\"\n");
            sb.Append("\t\t}\n");
        }

        sb.Append("\t}\n");
        sb.Append("}\n");
        return sb.ToString();
    }

    // VDF quotes strings and escapes backslash and double-quote with a backslash.
    private static string Escape(string value) =>
        (value ?? "").Replace("\\", "\\\\").Replace("\"", "\\\"");
}
