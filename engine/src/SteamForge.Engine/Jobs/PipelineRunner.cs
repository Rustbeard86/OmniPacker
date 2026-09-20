using System.Text.RegularExpressions;
using Microsoft.Extensions.Options;
using SteamForge.Abstractions.Logging;
using SteamForge.Abstractions.Plugins;
using SteamForge.Engine.Settings;

namespace SteamForge.Engine.Jobs;

/// <summary>
/// Runs one job through the plugin pipeline: resolve -> dedupe-check ->
/// download -> archive -> upload -> record history -> clean up local files.
/// Content sources, the archiver and the uploader are all plugins resolved from
/// DI, so the same runner drives Workshop items, games, and future sources.
/// </summary>
public sealed partial class PipelineRunner
{
    private const string LogSource = "queue";

    private readonly IReadOnlyList<IContentSource> _sources;
    private readonly IArchiver _archiver;
    private readonly IUploader _uploader;
    private readonly JobStore _store;
    private readonly JobNotifier _notifier;
    private readonly ILogBroadcaster _log;
    private readonly StorageOptions _storage;
    private readonly StoragePaths _paths;
    private readonly RuntimeSettingsStore _settings;

    public PipelineRunner(
        IEnumerable<IContentSource> sources,
        IEnumerable<IArchiver> archivers,
        IEnumerable<IUploader> uploaders,
        JobStore store,
        JobNotifier notifier,
        ILogBroadcaster log,
        IOptions<StorageOptions> storage,
        StoragePaths paths,
        RuntimeSettingsStore settings)
    {
        _sources = sources.ToList();
        _archiver = archivers.FirstOrDefault()
            ?? throw new InvalidOperationException("No archiver plugin registered");
        _uploader = uploaders.FirstOrDefault()
            ?? throw new InvalidOperationException("No uploader plugin registered");
        _store = store;
        _notifier = notifier;
        _log = log;
        _storage = storage.Value;
        _paths = paths;
        _settings = settings;
    }

    public async Task RunAsync(Job job, string workRoot, CancellationToken ct)
    {
        var workDir = Path.Combine(workRoot, job.Id);
        var reporter = new JobReporter(job, _store, _notifier, _log);
        var context = new JobPipelineContext(job.Id, workDir, reporter, ct);

        // Persistent download staging, set once resolved. Kept on interruption/failure
        // so a re-run resumes; removed only after a successful upload.
        string? stagingDir = null;

        // Persistent, content-keyed archive cache. Set once the archive step runs; kept
        // on a failed upload so a retry re-uploads the SAME archive rather than
        // recompressing, and removed (like staging) only after a successful upload.
        string? archiveDir = null;

        try
        {
            Directory.CreateDirectory(workDir);
            var request = new DownloadRequest(job.Kind, job.AppId, job.WorkshopId, job.DisplayName, job.Branch, job.TargetOs);

            var source = _sources.FirstOrDefault(s => s.CanHandle(request))
                ?? throw new InvalidOperationException($"No content source handles {job.Kind} for app {job.AppId}");

            // 1. Resolve identity (cheap) so we can dedupe before downloading.
            reporter.SetStatus(JobStatus.Resolving, "Resolving content");
            var identity = await source.ResolveAsync(request, context);
            job.AppId = identity.AppId;
            job.DepotId = identity.DepotId;
            job.ManifestId = identity.ManifestId;
            if (!string.IsNullOrWhiteSpace(identity.DisplayName))
            {
                job.DisplayName = identity.DisplayName;
            }

            reporter.Flush();

            // 2. Dedupe: identical manifest already uploaded and still live?
            var history = _store.FindHistory(identity.DepotId, identity.ManifestId);
            if (history is not null && (history.ExpiresAt is null || history.ExpiresAt > DateTimeOffset.UtcNow)
                && await StillExistsAsync(history, identity, ct))
            {
                job.Reused = true;
                job.ResultUrl = history.Url;
                job.ResultPassword = history.Password;
                job.ExpiresAt = history.ExpiresAt;
                job.ProviderRef = history.ProviderRef;
                job.Progress = 100;
                job.CompletedAt = DateTimeOffset.UtcNow;

                // Label the (possibly older, pre-workshop-columns) history row with this
                // Workshop item's id/title on a dedupe hit, so re-queuing an item that was
                // archived before the Workshop page existed makes it show there - cheaply,
                // without a re-download.
                if (job.Kind == ContentKind.WorkshopItem && (history.WorkshopId == 0 || string.IsNullOrEmpty(history.Title)))
                {
                    _store.UpdateHistoryWorkshopInfo(identity.DepotId, identity.ManifestId, job.WorkshopId, job.DisplayName);
                }

                reporter.SetStatus(JobStatus.Ready, "Reused existing upload (identical manifest)");
                return;
            }

            // 3. Content-keyed staging dir so an interrupted job resumes (only missing
            //    chunks re-fetched) rather than restarting. Persist the key BEFORE any
            //    bytes so a crash mid-download still leaves a trail for resume and for
            //    startup orphan GC. Computed before the budget check so a retry of
            //    already-charged content is recognised as free (re-upload, not re-download).
            var stagingKey = StagingKeyFor(job, identity);
            stagingDir = Path.Combine(_paths.StagingRoot, stagingKey);
            job.StagingKey = stagingKey;
            context.StagingDirectory = stagingDir;

            // 3b. Per-IP resource budget (public requests only): a reused upload above
            //     costs nothing, but a fresh download does. If this submitter's rolling
            //     24h byte total is already at/over the budget, refuse before spending any
            //     bandwidth/CPU. Re-checked here (not just at submit) because a single
            //     worker drains the queue serially, so an earlier job in the same batch may
            //     have pushed the IP over since this one was queued. A retry of content this
            //     IP was ALREADY charged for is allowed through - it re-uploads the cached
            //     archive and adds no new consumption, so it must not be trapped behind the
            //     very budget its first (successful) download filled.
            if (IsOverBudget(job.SubmitterIp, stagingKey))
            {
                job.Error = "Daily download budget reached for your address. Please try again later.";
                job.CompletedAt = DateTimeOffset.UtcNow;
                reporter.SetStatus(JobStatus.Failed, "Failed: daily download budget reached");
                _log.Warn(LogSource, $"Job {job.Id[..8]} refused: IP {job.SubmitterIp} over daily download budget");
                return;
            }

            reporter.Flush();

            reporter.SetStatus(JobStatus.Downloading, "Downloading from Steam");
            var content = await source.DownloadAsync(request, context);

            // Charge the actual downloaded bytes to the submitter's rolling budget. Done
            // right after the download (the bandwidth/disk was spent here regardless of
            // whether the later upload succeeds), and only for public (IP-tagged) jobs.
            // Operator IPs are exempt, and a given content (staging key) is charged at most
            // once per rolling window, so a Retry or shutdown-resume of the same job does
            // not double-bill the budget (a resumed download re-fetches few bytes anyway).
            if (!string.IsNullOrWhiteSpace(job.SubmitterIp) && content.TotalBytes > 0
                && !_settings.Current.AllowedIps().Contains(job.SubmitterIp)
                && !_store.AlreadyChargedKey(job.SubmitterIp, job.StagingKey, DateTimeOffset.UtcNow.AddHours(-24)))
            {
                _store.ChargeIp(job.SubmitterIp, content.TotalBytes, DateTimeOffset.UtcNow, job.StagingKey);
            }

            // 4. Archive with the native archiver, into a persistent content-keyed cache
            //    so a failed upload can be retried by re-uploading the SAME archive
            //    instead of recompressing many GB from scratch. A complete archive left
            //    by a prior attempt (same staging key) is reused as-is; otherwise we
            //    compress into ephemeral scratch and atomically move the finished file
            //    into the cache, so the cache only ever holds complete archives (a
            //    crash mid-compress leaves the partial in scratch, which is wiped).
            var archiveName = $"{FileBaseName(job)}.{_archiver.Extension}";
            archiveDir = Path.Combine(_paths.ArchiveRoot, stagingKey);

            ArchiveResult archive;
            var cached = FindCompleteArchive(archiveDir, _archiver.Extension);
            if (cached is not null)
            {
                reporter.SetStatus(JobStatus.Archiving, "Reusing archive from a previous attempt");
                _log.Info(LogSource,
                    $"[{job.Id[..8]}] Reusing existing archive {Path.GetFileName(cached)} (skipping re-compression)");
                archive = new ArchiveResult(cached, new FileInfo(cached).Length);
            }
            else
            {
                reporter.SetStatus(JobStatus.Archiving, "Compressing archive");
                var scratchPath = Path.Combine(workDir, archiveName);
                var built = await _archiver.ArchiveAsync(content.OutputDirectory, scratchPath, context);

                // Publish the finished archive into the cache with an atomic same-volume
                // move (data dir holds both scratch and cache), so a reader only ever
                // sees a complete file.
                Directory.CreateDirectory(archiveDir);
                var cachedPath = Path.Combine(archiveDir, archiveName);
                if (File.Exists(cachedPath))
                {
                    File.Delete(cachedPath);
                }

                File.Move(built.ArchivePath, cachedPath);
                archive = new ArchiveResult(cachedPath, built.SizeBytes);
            }

            // 5. Upload and get the share link. Expiry is chosen per job and
            //    anchored to upload time so "purge after N hours" is honoured. A large
            //    archive is split into fixed-size volumes first (Gofile has no resumable
            //    upload, so a single multi-tens-of-GB POST that drops restarts from zero);
            //    each part is a smaller, independently-retried POST into the one folder. The
            //    split parts live in the ephemeral work dir (cleaned in finally); the cached
            //    archive itself is untouched, so a retry re-splits and re-uploads it.
            reporter.SetStatus(JobStatus.Uploading, "Uploading to host");
            var expiresAt = job.ExpiryHours is > 0
                ? DateTimeOffset.UtcNow.AddHours(job.ExpiryHours.Value)
                : (DateTimeOffset?)null;
            var parts = await PrepareUploadPartsAsync(archive, archiveName, workDir, reporter, context.CancellationToken);
            var upload = await _uploader.UploadAsync(
                parts,
                new UploadOptions(job.Password, DisplayName: null, FolderLabel(job), expiresAt),
                context);

            job.ResultUrl = upload.Url;
            job.ResultPassword = upload.Password;
            job.ExpiresAt = upload.ExpiresAt;
            job.ProviderRef = upload.ProviderRef;

            // 6. Record history so an identical future request reuses this link.
            //    Branch is stored so the browse page only compares public archives
            //    against the current public build for staleness.
            _store.SaveHistory(new HistoryEntry(
                identity.AppId, identity.DepotId, identity.ManifestId,
                upload.Provider, upload.Url, upload.Password,
                DateTimeOffset.UtcNow, upload.ExpiresAt, upload.ProviderRef,
                Branch: BranchTag(job),
                // Carry Workshop provenance so the public Workshop page can list each mod
                // under its owning app card (0/no-title for a game build).
                WorkshopId: job.Kind == ContentKind.WorkshopItem ? job.WorkshopId : 0,
                Title: job.Kind == ContentKind.WorkshopItem ? job.DisplayName : null));

            // Uploaded and recorded - reclaim the staged content and the cached archive
            // now (nothing is kept on the VPS after a job) and drop the resume key so it
            // is not GC-scanned.
            TryDeleteDirectory(stagingDir);
            TryDeleteDirectory(archiveDir);
            job.StagingKey = null;

            job.Progress = 100;
            job.CompletedAt = DateTimeOffset.UtcNow;
            reporter.SetStatus(JobStatus.Ready, "Ready to share");
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // The token is only cancelled by host shutdown (there is no per-job user
            // cancel), so this is a redeploy/restart, not a terminal cancel. Put the job
            // back on the queue so the next start resumes it. The staged content is kept
            // (only the ephemeral work dir is cleared in finally), so the resumed run
            // re-fetches just the missing chunks. A hard kill that beats this write is
            // recovered by JobStore.RecoverInterrupted on the next boot.
            reporter.SetStatus(JobStatus.Queued, "Interrupted by shutdown - will resume on restart");
        }
        catch (OperationCanceledException)
        {
            // NOT our shutdown token (handled above): SteamKit cancelled an in-flight
            // request because the Steam connection dropped mid-pipeline (its async jobs
            // cancel on disconnect). Auto-reconnect restores the session, so this is a
            // transient, retriable condition - surface it as a Failure with an actionable
            // reason rather than a bare "Canceled" that looks like a deliberate abort.
            _log.Warn(LogSource, $"Job {job.Id} interrupted by a Steam disconnect (status {job.Status}); marking failed - re-queue to retry");
            job.Error = "Lost the Steam connection while preparing this item - please re-queue.";
            job.CompletedAt = DateTimeOffset.UtcNow;
            reporter.SetStatus(JobStatus.Failed, "Failed: Steam connection dropped");
        }
        catch (Exception ex)
        {
            job.Error = Truncate(ex.Message, 2000);
            job.CompletedAt = DateTimeOffset.UtcNow;
            reporter.SetStatus(JobStatus.Failed, "Failed");
            _log.Error(LogSource, $"Job {job.Id} failed: {ex.Message}");
        }
        finally
        {
            // Always clean local files - nothing is kept on the VPS after a job.
            TryDeleteDirectory(workDir);
        }
    }

    /// <summary>
    /// True when a public submitter's rolling 24h download total has reached the
    /// configured budget. Never over for: admin-queued jobs (no submitter IP), a disabled
    /// budget (0), an operator (allow-listed) IP, or a retry of content this IP was already
    /// charged for in the window (it re-uploads the cached archive, adding no consumption).
    /// </summary>
    private bool IsOverBudget(string? submitterIp, string? stagingKey)
    {
        if (string.IsNullOrWhiteSpace(submitterIp))
        {
            return false;
        }

        var current = _settings.Current;

        // Operator IPs (the admin allow-list) are never budget-limited - they are the
        // trusted operator queuing from the public page, not a public consumer to cap.
        if (current.AllowedIps().Contains(submitterIp))
        {
            return false;
        }

        var budgetGb = current.PublicDailyBudgetGb;
        if (budgetGb <= 0)
        {
            return false;
        }

        var since = DateTimeOffset.UtcNow.AddHours(-24);

        // A retry of content this IP already paid for adds no new consumption (it re-uploads
        // the cached archive), so let it through even if the IP is otherwise over budget -
        // otherwise a big download that filled the budget could never be re-uploaded.
        if (_store.AlreadyChargedKey(submitterIp, stagingKey, since))
        {
            return false;
        }

        var budgetBytes = (long)(budgetGb * 1024 * 1024 * 1024);
        var used = _store.BytesInWindow(submitterIp, since);
        return used >= budgetBytes;
    }

    /// <summary>
    /// Dedupe safety fallback: confirm a prior upload still exists before reusing
    /// its link, but at most once per the configured interval per item (a recently
    /// verified entry is trusted without a call). A confirmed deletion drops the
    /// stale history so the job re-downloads; a transient error trusts the cached
    /// link rather than risk a needless re-download.
    /// </summary>
    private async Task<bool> StillExistsAsync(HistoryEntry history, ContentIdentity identity, CancellationToken ct)
    {
        var interval = TimeSpan.FromMinutes(Math.Max(0, _storage.DedupeVerifyIntervalMinutes));
        if (history.VerifiedAt is { } v && DateTimeOffset.UtcNow - v < interval)
        {
            return true;
        }

        var reference = UploadReference(history);
        if (reference is null)
        {
            return true;
        }

        try
        {
            if (await _uploader.ExistsAsync(reference, ct))
            {
                _store.TouchHistoryVerified(identity.DepotId, identity.ManifestId, DateTimeOffset.UtcNow);
                return true;
            }

            _log.Info(LogSource, "Prior upload no longer exists on the host; re-downloading");
            _store.DeleteHistory(identity.DepotId, identity.ManifestId);
            return false;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _log.Warn(LogSource, $"Dedupe existence check failed ({ex.Message}); trusting cached link");
            return true;
        }
    }

    /// <summary>Best identifier for an existence check: the provider ref, or the share code from the URL.</summary>
    private static string? UploadReference(HistoryEntry history)
    {
        if (!string.IsNullOrEmpty(history.ProviderRef))
        {
            return history.ProviderRef;
        }

        var url = history.Url;
        var slash = url.LastIndexOf('/');
        return slash >= 0 && slash < url.Length - 1 ? url[(slash + 1)..] : null;
    }

    /// <summary>
    /// A complete archive left in the content-keyed cache by a prior attempt, or null.
    /// Only complete archives are ever moved into the cache dir, so any non-empty file
    /// with the archiver's extension is safe to reuse; the name is not matched (the
    /// display name can drift between runs), just the extension.
    /// </summary>
    private static string? FindCompleteArchive(string dir, string extension)
    {
        if (!Directory.Exists(dir))
        {
            return null;
        }

        foreach (var file in Directory.EnumerateFiles(dir, $"*.{extension}"))
        {
            try
            {
                if (new FileInfo(file).Length > 0)
                {
                    return file;
                }
            }
            catch
            {
                // File vanished between enumerate and stat; skip it.
            }
        }

        return null;
    }

    /// <summary>
    /// The files to upload for a finished archive: the single cached archive when it is at
    /// or below the volume threshold, or a set of fixed-size volume parts split into the
    /// ephemeral work dir when it is larger. Splitting keeps the cached archive intact so a
    /// retry re-splits from it rather than re-downloading and recompressing.
    /// </summary>
    private async Task<IReadOnlyList<string>> PrepareUploadPartsAsync(
        ArchiveResult archive, string archiveName, string workDir, IJobReporter reporter, CancellationToken ct)
    {
        var thresholdBytes = (long)(_settings.Current.UploadVolumeThresholdGb * 1024 * 1024 * 1024);
        var volumeBytes = (long)(_settings.Current.UploadVolumeSizeGb * 1024 * 1024 * 1024);
        if (thresholdBytes <= 0 || volumeBytes <= 0 || archive.SizeBytes <= thresholdBytes)
        {
            return [archive.ArchivePath];
        }

        var partCount = (int)((archive.SizeBytes + volumeBytes - 1) / volumeBytes);
        reporter.Progress(0,
            $"Splitting archive into {partCount} x {_settings.Current.UploadVolumeSizeGb:0.#} GB volumes for upload...");
        _log.Info(LogSource,
            $"Archive {archive.SizeBytes / (1024 * 1024)} MB exceeds the {_settings.Current.UploadVolumeThresholdGb:0.#} GB volume threshold; splitting into {partCount} parts");

        Directory.CreateDirectory(workDir);
        var parts = await SplitFileAsync(archive.ArchivePath, workDir, archiveName, volumeBytes, ct);
        return parts;
    }

    /// <summary>
    /// Split <paramref name="sourcePath"/> into fixed-size volumes named
    /// "<paramref name="baseName"/>.001", ".002", ... in <paramref name="destDir"/>. This
    /// is the byte layout of a 7-Zip multi-volume set, so the parts open directly as
    /// "name.7z.001" (or rejoin with a concatenation) - no special reassembly tool needed.
    /// </summary>
    private static async Task<IReadOnlyList<string>> SplitFileAsync(
        string sourcePath, string destDir, string baseName, long volumeBytes, CancellationToken ct)
    {
        var parts = new List<string>();
        var buffer = new byte[1024 * 1024];
        await using var source = File.OpenRead(sourcePath);

        var index = 0;
        while (source.Position < source.Length)
        {
            index++;
            var partPath = Path.Combine(destDir, $"{baseName}.{index:D3}");
            await using (var part = File.Create(partPath))
            {
                long written = 0;
                while (written < volumeBytes)
                {
                    var want = (int)Math.Min(buffer.Length, volumeBytes - written);
                    var read = await source.ReadAsync(buffer.AsMemory(0, want), ct);
                    if (read == 0)
                    {
                        break; // end of source
                    }

                    await part.WriteAsync(buffer.AsMemory(0, read), ct);
                    written += read;
                }
            }

            parts.Add(partPath);
        }

        return parts;
    }

    private void TryDeleteDirectory(string? path)
    {
        try
        {
            if (!string.IsNullOrEmpty(path) && Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
            }
        }
        catch (Exception ex)
        {
            _log.Warn(LogSource, $"Could not remove working directory {path}: {ex.Message}");
        }
    }

    /// <summary>
    /// Stable directory name for a job's persistent download staging, keyed to the
    /// exact content so a re-run lands on the same partial and resumes. For a game the
    /// identity carries the build id (a new build yields a new key, so a stale partial
    /// is not reused); for workshop it carries the manifest GID.
    /// </summary>
    private static string StagingKeyFor(Job job, ContentIdentity identity)
    {
        if (job.Kind == ContentKind.Game)
        {
            var branch = Slug(BranchTag(job) is { Length: > 0 } b ? b : "public");
            var os = Slug(string.IsNullOrWhiteSpace(job.TargetOs) ? "windows" : job.TargetOs);
            return $"game-{identity.AppId}-{branch}-{os}-b{identity.ManifestId}";
        }

        return $"ws-{identity.AppId}-m{identity.ManifestId}";
    }

    private static string Slug(string value)
    {
        var slug = SlugRegex().Replace(value.ToLowerInvariant(), "-").Trim('-');
        return slug.Length > 0 ? slug : "x";
    }

    // Content-kind tag: WS = Workshop item, CSF = Clean Steam Files (game).
    private static string KindPrefix(ContentKind kind) => kind switch
    {
        ContentKind.WorkshopItem => "WS",
        ContentKind.Game => "CSF",
        _ => "SF",
    };

    /// <summary>Branch tag for games (e.g. "public", "open-beta"); empty for workshop.</summary>
    private static string BranchTag(Job job) =>
        job.Kind == ContentKind.Game
            ? (string.IsNullOrWhiteSpace(job.Branch) ? "public" : job.Branch.Trim())
            : "";

    /// <summary>OS tag for games when not the default Windows (keeps existing Windows
    /// archive names stable while distinguishing macOS/Linux downloads).</summary>
    private static string OsTag(Job job) =>
        job.Kind == ContentKind.Game && !string.IsNullOrWhiteSpace(job.TargetOs)
            && !job.TargetOs.Trim().Equals("windows", StringComparison.OrdinalIgnoreCase)
            ? job.TargetOs.Trim().ToLowerInvariant()
            : "";

    /// <summary>Archive base name, e.g. "CSF_vanguard-galaxy_3471800_open-beta".</summary>
    private static string FileBaseName(Job job)
    {
        var id = job.WorkshopId != 0 ? job.WorkshopId.ToString() : job.AppId.ToString();
        var basis = string.IsNullOrWhiteSpace(job.DisplayName)
            ? (job.WorkshopId != 0 ? job.WorkshopId.ToString() : $"app-{job.AppId}")
            : job.DisplayName;
        var slug = SlugRegex().Replace(basis.ToLowerInvariant(), "-").Trim('-');
        if (slug.Length > 60)
        {
            slug = slug[..60].Trim('-');
        }

        var core = string.IsNullOrEmpty(slug) ? id : $"{slug}_{id}";
        var name = $"{KindPrefix(job.Kind)}_{core}";

        var tag = BranchTag(job);
        if (tag.Length > 0)
        {
            name += "_" + SlugRegex().Replace(tag.ToLowerInvariant(), "-").Trim('-');
        }

        var os = OsTag(job);
        if (os.Length > 0)
        {
            name += "_" + os;
        }

        return name;
    }

    /// <summary>Human folder label, e.g. "CSF Vanguard Galaxy (open-beta)".</summary>
    private static string FolderLabel(Job job)
    {
        var id = job.WorkshopId != 0 ? job.WorkshopId.ToString() : job.AppId.ToString();
        var title = string.IsNullOrWhiteSpace(job.DisplayName) ? id : job.DisplayName.Trim();
        var label = $"{KindPrefix(job.Kind)} {title}";

        var tag = BranchTag(job);
        var os = OsTag(job);
        var suffix = string.Join(", ", new[] { tag, os }.Where(s => s.Length > 0));
        return suffix.Length > 0 ? $"{label} ({suffix})" : label;
    }

    private static string Truncate(string value, int max) =>
        value.Length <= max ? value : value[^max..];

    [GeneratedRegex("[^a-z0-9]+")]
    private static partial Regex SlugRegex();
}
