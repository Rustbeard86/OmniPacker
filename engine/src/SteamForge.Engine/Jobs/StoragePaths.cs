namespace SteamForge.Engine.Jobs;

/// <summary>Resolved absolute paths for job storage, computed once at startup.</summary>
/// <param name="WorkRoot">Ephemeral per-job scratch (compression happens here); wiped on startup.</param>
/// <param name="StagingRoot">Persistent, content-keyed download staging that survives
/// an interrupted job so downloads can resume; orphans are GC'd at startup.</param>
/// <param name="ArchiveRoot">Persistent, content-keyed cache of finished archives that
/// survives a failed upload, so a retry re-uploads the same archive instead of
/// recompressing many GB from scratch; orphans are GC'd at startup, same as staging.</param>
public sealed record StoragePaths(
    string DataDir, string DatabasePath, string WorkRoot, string StagingRoot, string ArchiveRoot);
