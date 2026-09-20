using System.Buffers;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net.Http;
using System.Threading.Channels;
using Microsoft.Extensions.Options;
using Microsoft.Win32.SafeHandles;
using SteamForge.Abstractions.Logging;
using SteamForge.Engine.Jobs;
using SteamForge.Engine.Steam;
using SteamKit2;
using SteamKit2.Internal;
using CdnClient = SteamKit2.CDN.Client;
using CdnServer = SteamKit2.CDN.Server;

namespace SteamForge.Engine.Content;

/// <summary>
/// SteamPipe content client built directly on SteamKit2: resolves Workshop
/// items, fetches depot keys and manifests, and downloads/decrypts/decompresses
/// depot chunks. Exposes the chunk-level primitive so higher layers (workshop
/// and game download plugins, and the future delta patch-maker) share one path.
/// </summary>
public sealed class SteamContentClient
{
    private const string LogSource = "content";
    private const int ChunkRetries = 8;

    // Shared client for legacy web-hosted Workshop files (steamusercontent.com UGC).
    // These predate SteamPipe and are a plain HTTP download, no depot key/manifest. A
    // single static client avoids socket exhaustion; per-call timeouts ride the token.
    private static readonly HttpClient LegacyHttp = new() { Timeout = Timeout.InfiniteTimeSpan };

    // Steam calls complete via a callback; if the session drops mid-call the callback
    // may never fire, so bound every await. Without this a scan interrupted by an
    // account switch/disconnect hangs the ownership scanner indefinitely.
    private static readonly TimeSpan SteamCallTimeout = TimeSpan.FromSeconds(30);

    private readonly SteamSessionManager _session;
    private readonly ILogBroadcaster _log;
    private readonly SteamForge.Engine.Settings.RuntimeSettingsStore _settings;

    private readonly ConcurrentDictionary<uint, byte[]> _depotKeys = new();

    // Cached PICS appinfo per app, with the time it was fetched. Build ids and depot
    // manifest GIDs (what a game download dedupes on) live here, so a stale entry makes
    // a newly-shipped build look unchanged and wrongly dedupe to the old archive. The
    // PICS change watcher invalidates entries as Steam reports changes; ResolveGameAsync
    // forces a fresh fetch at download time so the dedupe key is always current.
    private readonly ConcurrentDictionary<uint, (KeyValue Kv, DateTimeOffset At)> _appInfo = new();
    // Value is nullable on purpose: a null entry is a cached NEGATIVE ("this depot/host
    // needs no token, or Steam declined"), so we attempt the lookup at most once per
    // (depot, host) instead of re-issuing - and timing out - a Steam job on every chunk.
    private readonly ConcurrentDictionary<(uint DepotId, string Host), string?> _cdnTokens = new();
    private readonly SemaphoreSlim _cdnGate = new(1, 1);

    private CdnClient? _cdn;
    private IReadOnlyList<CdnServer>? _servers;
    private int _serverCursor;

    public SteamContentClient(
        SteamSessionManager session,
        ILogBroadcaster log,
        SteamForge.Engine.Settings.RuntimeSettingsStore settings)
    {
        _session = session;
        _log = log;
        _settings = settings;
    }

    private SteamClient Client =>
        _session.IsLoggedOn
            ? _session.Client
            : throw new InvalidOperationException("Steam session is not logged on");

    private T Handler<T>() where T : ClientMsgHandler =>
        Client.GetHandler<T>() ?? throw new InvalidOperationException($"{typeof(T).Name} handler unavailable");

    /// <summary>Resolve a Workshop published-file id to its app/depot/manifest.</summary>
    public async Task<WorkshopItemInfo> ResolveWorkshopItemAsync(ulong publishedFileId, CancellationToken ct = default)
    {
        var service = Handler<SteamUnifiedMessages>().CreateService<PublishedFile>();
        var request = new CPublishedFile_GetDetails_Request();
        request.publishedfileids.Add(publishedFileId);

        var response = await service.GetDetails(request).ToTask().WaitAsync(SteamCallTimeout, ct);
        var details = response.Body.publishedfiledetails.FirstOrDefault()
            ?? throw new InvalidOperationException($"Workshop item {publishedFileId} not found");

        if (details.result != 1) // 1 == EResult.OK
        {
            throw new InvalidOperationException(
                $"Steam returned result {details.result} for Workshop item {publishedFileId}");
        }

        var info = new WorkshopItemInfo(
            PublishedFileId: publishedFileId,
            ConsumerAppId: details.consumer_appid,
            ManifestId: details.hcontent_file,
            Title: string.IsNullOrWhiteSpace(details.title) ? publishedFileId.ToString() : details.title,
            FileName: details.filename,
            FileSize: details.file_size,
            LegacyFileUrl: string.IsNullOrEmpty(details.file_url) ? null : details.file_url);

        _log.Info(LogSource,
            $"Resolved Workshop item {publishedFileId}: app {info.ConsumerAppId}, manifest {info.ManifestId}, \"{info.Title}\"");
        return info;
    }

    /// <summary>
    /// Expand any Workshop collection ids in <paramref name="input"/> into their member
    /// item ids (recursively, depth-capped); plain items pass through. Uses the logged-on
    /// session's PublishedFile.GetDetails, whose file_type is the only reliable way to tell
    /// a collection from an item - both carry a preview file and can list children, so the
    /// public web API cannot distinguish them. Returns distinct ids in first-seen order and
    /// the number of collections expanded (0 = input was all items).
    /// </summary>
    public async Task<(IReadOnlyList<ulong> Ids, int Collections)> ExpandCollectionsAsync(
        IEnumerable<ulong> input, CancellationToken ct = default)
    {
        var service = Handler<SteamUnifiedMessages>().CreateService<PublishedFile>();
        var final = new List<ulong>();
        var finalSeen = new HashSet<ulong>();
        var visited = new HashSet<ulong>();
        var current = input.Distinct().ToList();
        var collections = 0;

        for (var depth = 0; depth < 4 && current.Count > 0; depth++)
        {
            var batch = current.Where(id => visited.Add(id)).ToList();
            if (batch.Count == 0)
            {
                break;
            }

            var request = new CPublishedFile_GetDetails_Request { includechildren = true };
            foreach (var id in batch)
            {
                request.publishedfileids.Add(id);
            }

            var response = await service.GetDetails(request).ToTask().WaitAsync(SteamCallTimeout, ct);
            var byId = response.Body.publishedfiledetails
                .Where(d => d.result == 1)
                .GroupBy(d => d.publishedfileid)
                .ToDictionary(g => g.Key, g => g.First());

            var next = new List<ulong>();
            foreach (var id in batch)
            {
                if (byId.TryGetValue(id, out var d) &&
                    d.file_type == (uint)EWorkshopFileType.Collection && d.children.Count > 0)
                {
                    collections++;
                    foreach (var child in d.children)
                    {
                        next.Add(child.publishedfileid);
                    }
                }
                else if (finalSeen.Add(id))
                {
                    final.Add(id);
                }
            }

            current = next;
        }

        // Depth cap reached: queue whatever is left as items.
        foreach (var id in current)
        {
            if (finalSeen.Add(id))
            {
                final.Add(id);
            }
        }

        return (final, collections);
    }

    /// <summary>
    /// Get (and cache) a CDN auth token for a (depot, host). App depots often
    /// require one on the manifest/chunk request; returns null if Steam declines
    /// (some content, e.g. workshop, needs none and downloads fine without it).
    /// </summary>
    private async Task<string?> GetCdnAuthTokenAsync(uint appId, uint depotId, string? host, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(host))
        {
            return null;
        }

        var key = (depotId, host);
        if (_cdnTokens.TryGetValue(key, out var cached))
        {
            return cached;
        }

        string? token = null;
        try
        {
            var result = await Handler<SteamContent>().GetCDNAuthToken(appId, depotId, host).WaitAsync(ct);
            if (result.Result == EResult.OK && !string.IsNullOrEmpty(result.Token))
            {
                token = result.Token;
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // The job itself was cancelled (shutdown/account switch) - propagate; do not
            // cache a negative that would outlive the cancellation.
            throw;
        }
        catch (Exception ex)
        {
            // SteamPipe content on steamcontent.com generally needs no token; Steam then
            // ignores the request and the AsyncJob times out ("A task was canceled").
            // Downloading with a null token still works, so this is expected - log once
            // at Debug and cache the negative below so we never re-pay the timeout.
            _log.Debug(LogSource, $"No CDN auth token for depot {depotId} on {host} ({ex.Message}); continuing without one");
        }

        // Cache success AND negative: this lookup now runs at most once per (depot, host).
        _cdnTokens[key] = token;
        return token;
    }

    /// <summary>Fetch (and cache) the depot decryption key for a depot.</summary>
    public async Task<byte[]> GetDepotKeyAsync(uint depotId, uint appId, CancellationToken ct = default)
    {
        if (_depotKeys.TryGetValue(depotId, out var cached))
        {
            return cached;
        }

        var result = await Handler<SteamApps>().GetDepotDecryptionKey(depotId, appId).ToTask().WaitAsync(SteamCallTimeout, ct);
        if (result.Result != EResult.OK)
        {
            // AccessDenied here almost always means entitlement, not a transient error:
            // no signed-in Steam account holds a license for this app, so Steam refuses
            // the decryption key. Say so plainly - the raw "AccessDenied" repeatedly read
            // as "the whole downloader is broken" when it was a single unowned title.
            if (result.Result == EResult.AccessDenied)
            {
                throw new DepotAccessDeniedException(appId,
                    $"Steam denied the depot key for app {appId} (AccessDenied): the active Steam account holds no " +
                    "download-capable license for this app, so its content cannot be downloaded. Add or sign in an " +
                    "account that owns it (for a workshop item, that means owning the game the item is for).");
            }

            throw new InvalidOperationException(
                $"Could not get depot key for depot {depotId} (app {appId}): {result.Result}");
        }

        _depotKeys[depotId] = result.DepotKey;
        return result.DepotKey;
    }

    /// <summary>Download and decrypt a depot manifest for a branch.</summary>
    public async Task<DepotManifest> GetManifestAsync(
        uint depotId, uint appId, ulong manifestId, byte[] depotKey,
        string branch = "public", CancellationToken ct = default)
    {
        // The request code is branch-scoped; a mismatched branch yields CDN 401s.
        var requestCode = await Handler<SteamContent>()
            .GetManifestRequestCode(depotId, appId, manifestId, branch, null);

        var cdn = await GetCdnAsync(ct);
        DepotManifest? manifest = null;
        Exception? last = null;

        for (var attempt = 0; attempt < ChunkRetries && manifest is null; attempt++)
        {
            var server = await NextServerAsync(ct);
            try
            {
                var cdnToken = await GetCdnAuthTokenAsync(appId, depotId, server.Host, ct);
                manifest = await cdn.DownloadManifestAsync(
                    depotId, manifestId, requestCode, server, depotKey, null, cdnToken);
            }
            catch (Exception ex)
            {
                last = ex;
                _log.Warn(LogSource, $"Manifest fetch attempt {attempt + 1} failed on {server.Host}: {ex.Message}");
            }
        }

        if (manifest is null)
        {
            throw new InvalidOperationException($"Failed to download manifest {manifestId}", last);
        }

        if (manifest.FilenamesEncrypted)
        {
            manifest.DecryptFilenames(depotKey);
        }

        return manifest;
    }

    /// <summary>
    /// Download one depot chunk into <paramref name="destination"/> (decrypted and
    /// decompressed), returning the number of bytes written. This is the shared
    /// primitive for whole-file downloads and delta patching.
    /// </summary>
    public async Task<int> DownloadChunkAsync(
        uint appId,
        uint depotId,
        DepotManifest.ChunkData chunk,
        byte[] destination,
        byte[] depotKey,
        CancellationToken ct = default)
    {
        var cdn = await GetCdnAsync(ct);
        Exception? last = null;

        for (var attempt = 0; attempt < ChunkRetries; attempt++)
        {
            // Back off between attempts so a transient CDN 503 burst (every cache
            // briefly returning 503, seen under Steam load) is ridden out rather than
            // burning all attempts in milliseconds and failing the whole download.
            if (attempt > 0)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(Math.Min(4000, 250 * (1 << (attempt - 1)))), ct);
            }

            var server = await NextServerAsync(ct);
            try
            {
                var cdnToken = await GetCdnAuthTokenAsync(appId, depotId, server.Host, ct);
                return await cdn.DownloadDepotChunkAsync(depotId, chunk, server, destination, depotKey, null, cdnToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
            {
                last = ex;
                _log.Warn(LogSource, $"Chunk fetch attempt {attempt + 1} failed on {server.Host}: {ex.Message}");
            }
        }

        throw new InvalidOperationException("Failed to download depot chunk", last);
    }

    /// <summary>Shared byte counter so multi-depot downloads report one aggregate progress.</summary>
    private sealed class ByteTracker
    {
        public long Done;
        public long Total;
    }

    /// <summary>
    /// Download every file in a manifest into <paramref name="destinationDir"/>,
    /// reporting coarse progress as (percent, message).
    /// </summary>
    public async Task<ContentDownloadResult> DownloadManifestFilesAsync(
        DepotManifest manifest,
        uint appId,
        byte[] depotKey,
        string destinationDir,
        Action<int, string>? progress = null,
        CancellationToken ct = default)
    {
        Directory.CreateDirectory(destinationDir);
        var destRoot = Path.GetFullPath(destinationDir);

        var tracker = new ByteTracker
        {
            Total = (manifest.Files ?? [])
                .Where(f => !f.Flags.HasFlag(EDepotFileFlag.Directory))
                .Sum(f => (long)f.TotalSize),
        };

        // Free-space is checked inside DownloadChunksAsync, after it discounts any
        // bytes a prior (resumed) run already wrote.
        var fileCount = await DownloadChunksAsync(
            [(manifest, appId, depotKey)], destRoot, tracker, progress, ct);

        _log.Info(LogSource, $"Downloaded {fileCount} files ({tracker.Done / (1024 * 1024)} MB) to {destinationDir}");
        return new ContentDownloadResult(appId, manifest.DepotID, manifest.ManifestGID, destinationDir, fileCount, tracker.Done);
    }

    private sealed class FileWork(SafeFileHandle handle, int chunks, string keyPrefix)
    {
        public readonly SafeFileHandle Handle = handle;

        /// <summary>Per-chunk resume-key prefix ("{depotId}\t{relpath}"); the offset is appended.</summary>
        public readonly string KeyPrefix = keyPrefix;
        public int Remaining = chunks;
    }

    private readonly record struct ChunkWork(
        FileWork File, uint AppId, uint DepotId, byte[] DepotKey, DepotManifest.ChunkData Chunk);

    /// <summary>
    /// Append-only record of the chunks already written under a staging dir, so an
    /// interrupted download resumes by fetching only what is missing (SteamCMD-style).
    /// Each completed chunk is one line ("{depotId}\t{relpath}\t{offset}"); a torn final
    /// line from a hard kill is simply ignored on reload and that chunk is re-fetched.
    /// Bytes already on disk survive a process kill (the kernel flushes the page cache),
    /// so trusting the log is safe for the redeploy/crash case it targets.
    /// </summary>
    private sealed class ResumeLog : IDisposable
    {
        public const string FileName = ".forge-resume";

        private readonly HashSet<string> _done;
        private readonly StreamWriter _writer;
        private readonly object _gate = new();
        private int _sinceFlush;

        private ResumeLog(HashSet<string> done, StreamWriter writer)
        {
            _done = done;
            _writer = writer;
        }

        public int CompletedCount => _done.Count;

        public static ResumeLog Open(string stagingDir)
        {
            Directory.CreateDirectory(stagingDir);
            var path = Path.Combine(stagingDir, FileName);

            var done = new HashSet<string>(StringComparer.Ordinal);
            if (File.Exists(path))
            {
                foreach (var line in File.ReadLines(path))
                {
                    if (line.Length > 0)
                    {
                        done.Add(line);
                    }
                }
            }

            var writer = new StreamWriter(
                new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.Read));
            return new ResumeLog(done, writer);
        }

        /// <summary>True if this chunk was already written in a previous run.</summary>
        public bool WasDone(string key) => _done.Contains(key);

        /// <summary>Record a freshly-written chunk; flushed periodically to bound rework.</summary>
        public void MarkDone(string key)
        {
            lock (_gate)
            {
                _writer.WriteLine(key);
                if (++_sinceFlush >= 256)
                {
                    _writer.Flush();
                    _sinceFlush = 0;
                }
            }
        }

        public void Dispose()
        {
            lock (_gate)
            {
                _writer.Flush();
                _writer.Dispose();
            }
        }
    }

    /// <summary>
    /// A SemaphoreSlim whose permit count can be raised and lowered at runtime, so the
    /// adaptive controller can grow or shrink the number of in-flight chunk fetches.
    /// Only the controller calls <see cref="SetLimitAsync"/> (single writer).
    /// </summary>
    private sealed class ResizableGate
    {
        private readonly SemaphoreSlim _sem;
        private readonly int _max;
        private int _limit;

        public ResizableGate(int initial, int max)
        {
            _max = Math.Max(1, max);
            initial = Math.Clamp(initial, 1, _max);
            _sem = new SemaphoreSlim(initial, _max);
            _limit = initial;
        }

        public int Limit => _limit;
        public int Max => _max;

        public Task WaitAsync(CancellationToken ct) => _sem.WaitAsync(ct);
        public void Release() => _sem.Release();

        public async Task SetLimitAsync(int target, CancellationToken ct)
        {
            target = Math.Clamp(target, 1, _max);
            while (_limit < target)
            {
                _sem.Release();
                _limit++;
            }

            // Shrinking parks permits by acquiring them; a worker frees one as its
            // current chunk completes, so this only briefly awaits.
            while (_limit > target)
            {
                await _sem.WaitAsync(ct);
                _limit--;
            }
        }
    }

    /// <summary>
    /// Download every file across <paramref name="sources"/> (one or many depot
    /// manifests) into <paramref name="destRoot"/> through a single shared, adaptively
    /// sized pool of concurrent chunk fetches, advancing the shared
    /// <paramref name="tracker"/>. Chunks - not files - are the unit of parallelism, so
    /// a single huge file (a common Workshop shape) still saturates the link, and a
    /// multi-depot game download overlaps all depots at once. Returns files written.
    /// </summary>
    private async Task<int> DownloadChunksAsync(
        IReadOnlyList<(DepotManifest Manifest, uint AppId, byte[] Key)> sources,
        string destRoot,
        ByteTracker tracker,
        Action<int, string>? progress,
        CancellationToken ct)
    {
        // Create directories first so file writes never race a missing parent.
        foreach (var (manifest, _, _) in sources)
        {
            foreach (var dir in (manifest.Files ?? []).Where(f => f.Flags.HasFlag(EDepotFileFlag.Directory)))
            {
                Directory.CreateDirectory(ResolveSafePath(destRoot, dir.FileName));
            }
        }

        // A single relative path can appear in more than one depot (Steam shares
        // content across depots). Opening the same output path from two depots at once
        // collides on FileShare.None and fails the whole download with "being used by
        // another process", so flatten every source to one entry per resolved path -
        // the first depot to carry it wins (identical shared content, so the choice is
        // immaterial). This also stops those bytes being double-counted in the total.
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var plan = new List<(uint AppId, uint DepotId, byte[] Key, DepotManifest.FileData File, string Path)>();
        foreach (var (manifest, appId, key) in sources)
        {
            foreach (var file in (manifest.Files ?? [])
                .Where(f => !f.Flags.HasFlag(EDepotFileFlag.Directory) && string.IsNullOrEmpty(f.LinkTarget)))
            {
                var path = ResolveSafePath(destRoot, file.FileName);
                if (!seen.Add(path))
                {
                    continue;
                }

                plan.Add((appId, manifest.DepotID, key, file, path));
            }
        }

        // Total reflects the deduped set so progress can actually reach 100%.
        tracker.Total = plan.Sum(p => (long)p.File.TotalSize);

        // Resume: the log records which chunks are already on disk from a prior run.
        // Pre-count those bytes so progress starts where it left off and the free-space
        // check only accounts for the remaining download, not what is already present.
        using var resume = ResumeLog.Open(destRoot);
        long resumedBytes = 0;
        foreach (var item in plan)
        {
            var prefix = $"{item.DepotId}\t{item.File.FileName}";
            foreach (var chunk in item.File.Chunks ?? [])
            {
                if (resume.WasDone($"{prefix}\t{chunk.Offset}"))
                {
                    resumedBytes += chunk.UncompressedLength;
                }
            }
        }

        tracker.Done = resumedBytes;
        EnsureFreeSpace(destRoot, Math.Max(0, tracker.Total - resumedBytes));
        if (resumedBytes > 0)
        {
            _log.Info(LogSource,
                $"Resuming download: {resumedBytes / (1024 * 1024)} of {tracker.Total / (1024 * 1024)} MB already present, fetching the rest");
        }

        // Wall clock for this run's download speed/ETA (resumed bytes are excluded).
        var swDownload = Stopwatch.StartNew();

        var completedFiles = 0;
        var openFiles = new ConcurrentDictionary<FileWork, byte>();

        // Concurrency ceiling read live from settings so the Settings page can tune it
        // without a redeploy; clamped to a sane range.
        var maxParallel = Math.Clamp(_settings.Current.MaxParallelDownloads, 1, 128);

        // Bounded so the producer cannot open thousands of file handles ahead of the
        // workers; it blocks once this many chunks are buffered.
        var channel = Channel.CreateBounded<ChunkWork>(new BoundedChannelOptions(Math.Max(maxParallel * 4, 32))
        {
            SingleWriter = true,
            SingleReader = false,
            FullMode = BoundedChannelFullMode.Wait,
        });

        var gate = new ResizableGate(Math.Min(8, maxParallel), maxParallel);
        using var doneCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var controller = RunConcurrencyControllerAsync(gate, tracker, doneCts.Token);

        // First fatal fault from the producer or any worker. When a chunk exhausts its
        // retries a worker records it here and cancels doneCts, which unblocks the
        // producer (otherwise stuck on a full bounded channel with no live readers) and
        // winds the rest down - so a CDN failure fails the job fast instead of leaving
        // it wedged forever in "downloading". Staging + the resume log let a retry pick
        // up where this left off.
        Exception? fault = null;

        var producer = Task.Run(async () =>
        {
            try
            {
                // plan is already deduped by resolved path and symlink/dir-free, so each
                // output file is opened exactly once - no two depots race the same handle.
                foreach (var item in plan)
                {
                    doneCts.Token.ThrowIfCancellationRequested();
                    var path = item.Path;
                    Directory.CreateDirectory(Path.GetDirectoryName(path)!);

                    // Preserve bytes from a prior run so a resume keeps them. Preallocation
                    // (posix_fallocate on Linux, to avoid fragmentation as chunks land out
                    // of order) is only permitted when creating a brand-new file, so hint it
                    // on first creation and just open the existing partial on resume.
                    var handle = File.Exists(path)
                        ? File.OpenHandle(path, FileMode.Open, FileAccess.Write, FileShare.None, FileOptions.Asynchronous)
                        : File.OpenHandle(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, FileOptions.Asynchronous, (long)item.File.TotalSize);

                    var chunks = item.File.Chunks;
                    if (chunks is null || chunks.Count == 0)
                    {
                        handle.Dispose();
                        Interlocked.Increment(ref completedFiles);
                        continue;
                    }

                    var fileWork = new FileWork(handle, chunks.Count, $"{item.DepotId}\t{item.File.FileName}");
                    openFiles[fileWork] = 0;
                    foreach (var chunk in chunks)
                    {
                        await channel.Writer.WriteAsync(new ChunkWork(fileWork, item.AppId, item.DepotId, item.Key, chunk), doneCts.Token);
                    }
                }
            }
            catch (OperationCanceledException) when (doneCts.IsCancellationRequested)
            {
                // Job cancelled (shutdown/resume) or a worker faulted - stop feeding.
            }
            catch (Exception ex)
            {
                Interlocked.CompareExchange(ref fault, ex, null);
                doneCts.Cancel();
            }
            finally
            {
                // Always signal completion so workers draining the tail wake and exit
                // rather than blocking on WaitToReadAsync of a never-completed channel.
                channel.Writer.Complete();
            }
        }, ct);

        void CompleteChunk(FileWork file)
        {
            if (Interlocked.Decrement(ref file.Remaining) == 0)
            {
                file.Handle.Dispose();
                openFiles.TryRemove(file, out _);
                Interlocked.Increment(ref completedFiles);
            }
        }

        void ReportProgress()
        {
            if (tracker.Total <= 0)
            {
                return;
            }

            var done = Interlocked.Read(ref tracker.Done);
            var pct = (int)Math.Clamp(done * 100 / tracker.Total, 0, 100);
            var msg = $"Downloading {pct}% ({done / (1024 * 1024)} / {tracker.Total / (1024 * 1024)} MB)";

            // Speed + ETA from this run's freshly-downloaded bytes (excludes resumed).
            var secs = swDownload.Elapsed.TotalSeconds;
            var fetched = done - resumedBytes;
            if (secs >= 3 && fetched > 0)
            {
                var bps = fetched / secs;
                msg += $", {bps / (1024 * 1024):0.#} MB/s";
                var remaining = tracker.Total - done;
                if (remaining > 0)
                {
                    var eta = TimeSpan.FromSeconds(remaining / bps);
                    msg += eta.TotalHours >= 1 ? $", ETA {eta:h\\:mm\\:ss}" : $", ETA {eta:m\\:ss}";
                }
            }

            progress?.Invoke(pct, msg);
        }

        async Task WorkerAsync()
        {
            try
            {
                while (await channel.Reader.WaitToReadAsync(doneCts.Token))
                {
                    while (channel.Reader.TryRead(out var work))
                    {
                        var key = $"{work.File.KeyPrefix}\t{work.Chunk.Offset}";

                        // Already on disk from a prior run - skip the fetch (its bytes were
                        // pre-counted). No gate: no network, no contention.
                        if (resume.WasDone(key))
                        {
                            CompleteChunk(work.File);
                            continue;
                        }

                        await gate.WaitAsync(doneCts.Token);
                        try
                        {
                            var buffer = ArrayPool<byte>.Shared.Rent((int)work.Chunk.UncompressedLength);
                            try
                            {
                                var written = await DownloadChunkAsync(
                                    work.AppId, work.DepotId, work.Chunk, buffer, work.DepotKey, doneCts.Token);

                                // RandomAccess writes at an explicit offset, so concurrent
                                // chunks of the same file never fight over a shared position.
                                await RandomAccess.WriteAsync(
                                    work.File.Handle, buffer.AsMemory(0, written), (long)work.Chunk.Offset, doneCts.Token);

                                // Record the chunk as done AFTER its bytes are written, so a
                                // crash between can only cause a harmless re-fetch, never a gap.
                                resume.MarkDone(key);
                                Interlocked.Add(ref tracker.Done, written);
                                ReportProgress();
                            }
                            finally
                            {
                                ArrayPool<byte>.Shared.Return(buffer);
                            }
                        }
                        finally
                        {
                            gate.Release();
                        }

                        CompleteChunk(work.File);
                    }
                }
            }
            catch (OperationCanceledException) when (doneCts.IsCancellationRequested)
            {
                // Job cancelled (shutdown/resume) or a sibling worker faulted - wind down.
            }
            catch (Exception ex)
            {
                // A chunk exhausted its retries (e.g. a persistent CDN 503 storm). Record
                // the first such fault and cancel so the producer and other workers stop,
                // rather than the producer dead-locking on a full channel no one drains.
                Interlocked.CompareExchange(ref fault, ex, null);
                doneCts.Cancel();
            }
        }

        var workers = new Task[maxParallel];
        for (var i = 0; i < workers.Length; i++)
        {
            workers[i] = WorkerAsync();
        }

        try
        {
            // The producer and workers each catch their own faults (recording the first
            // into `fault` and cancelling), so awaiting them together never wedges: a
            // worker death cancels the producer's blocked write and vice-versa.
            await Task.WhenAll(workers.Append(producer));
        }
        finally
        {
            doneCts.Cancel();
            try
            {
                await controller;
            }
            catch (OperationCanceledException)
            {
                // expected on completion/cancel
            }

            // Close any handles still open (a failure or cancellation mid-flight).
            foreach (var fileWork in openFiles.Keys)
            {
                fileWork.Handle.Dispose();
            }
        }

        // Surface a captured fault (chunk retries exhausted, disk full, ...) so the job
        // fails and can be retried; a plain job cancellation (shutdown/resume) has no
        // fault and rethrows here as OperationCanceledException.
        if (fault is not null)
        {
            throw new InvalidOperationException("Download failed", fault);
        }

        ct.ThrowIfCancellationRequested();

        return completedFiles;
    }

    /// <summary>
    /// Adaptive concurrency governor: measures aggregate throughput on a fixed cadence
    /// and grows the in-flight chunk count while throughput keeps rising. When it
    /// plateaus (or hits the configured cap) it settles ~10% below the peak-achieving
    /// level to leave headroom, and re-probes if throughput later collapses.
    /// </summary>
    private async Task RunConcurrencyControllerAsync(ResizableGate gate, ByteTracker tracker, CancellationToken ct)
    {
        const int intervalMs = 1500;
        const double improveFactor = 1.07; // <7% gain from more workers == plateau
        const double headroom = 0.90;      // hold ~10% below the saturating level
        const double reprobeFraction = 0.60;
        var step = Math.Max(4, gate.Max / 8);

        var lastDone = Interlocked.Read(ref tracker.Done);
        var sw = Stopwatch.StartNew();
        double lastBps = 0, peakBps = 0;
        var prevLimit = gate.Limit;
        var ramping = true;

        try
        {
            while (!ct.IsCancellationRequested)
            {
                await Task.Delay(intervalMs, ct);

                var now = Interlocked.Read(ref tracker.Done);
                var secs = sw.Elapsed.TotalSeconds;
                sw.Restart();
                var bps = secs > 0 ? (now - lastDone) / secs : 0;
                lastDone = now;
                peakBps = Math.Max(peakBps, bps);

                if (ramping)
                {
                    if (bps > lastBps * improveFactor && gate.Limit < gate.Max)
                    {
                        prevLimit = gate.Limit;
                        await gate.SetLimitAsync(gate.Limit + step, ct);
                    }
                    else
                    {
                        var saturating = gate.Limit >= gate.Max ? gate.Max : prevLimit;
                        await gate.SetLimitAsync((int)Math.Round(saturating * headroom), ct);
                        ramping = false;
                        _log.Debug(LogSource,
                            $"Download concurrency held at {gate.Limit} (~{peakBps / (1024 * 1024):0.#} MB/s peak)");
                    }
                }
                else if (bps < peakBps * reprobeFraction)
                {
                    // Throughput fell well below peak - the link or CDN may have freed
                    // up; probe upward again from the current level.
                    ramping = true;
                    lastBps = 0;
                    continue;
                }

                lastBps = bps;
            }
        }
        catch (OperationCanceledException)
        {
            // download finished or was cancelled
        }
    }

    /// <summary>
    /// Refuse a download that would not leave the configured free-space margin on
    /// the work drive, before any bytes are fetched. If the check itself cannot run
    /// (e.g. drive not found), the download is allowed rather than blocked.
    /// </summary>
    private void EnsureFreeSpace(string destRoot, long requiredBytes)
    {
        long available;
        try
        {
            available = new DriveInfo(Path.GetPathRoot(Path.GetFullPath(destRoot))!).AvailableFreeSpace;
        }
        catch (Exception ex)
        {
            _log.Warn(LogSource, $"Could not check free disk space: {ex.Message}");
            return;
        }

        // Read the headroom live so the Settings page can change it without a redeploy.
        var marginBytes = (long)(Math.Max(0, _settings.Current.MinFreeSpaceMarginGb) * 1024 * 1024 * 1024);
        if (available - marginBytes < requiredBytes)
        {
            throw new InvalidOperationException(
                $"Insufficient disk space: need {FormatBytes(requiredBytes)}, only {FormatBytes(available)} free " +
                $"(keeping {FormatBytes(marginBytes)} headroom).");
        }
    }

    private static string FormatBytes(long bytes)
    {
        string[] units = ["B", "KiB", "MiB", "GiB", "TiB"];
        double n = bytes;
        var u = 0;
        while (n >= 1024 && u < units.Length - 1)
        {
            n /= 1024;
            u++;
        }

        return $"{n:0.#} {units[u]}";
    }

    /// <summary>
    /// High-level: resolve a Workshop item and download its SteamPipe content.
    /// Throws for legacy web-hosted items (caller can fetch those over HTTP).
    /// </summary>
    public async Task<ContentDownloadResult> DownloadWorkshopItemAsync(
        ulong publishedFileId,
        string destinationDir,
        Action<int, string>? progress = null,
        CancellationToken ct = default)
    {
        var item = await ResolveWorkshopItemAsync(publishedFileId, ct);
        if (!item.IsSteamPipe)
        {
            // Legacy web-hosted UGC (steamusercontent.com): not a SteamPipe depot, just a
            // plain HTTP file. Fetch it directly so the pipeline can still archive+share it.
            if (!string.IsNullOrEmpty(item.LegacyFileUrl))
            {
                return await DownloadLegacyWorkshopFileAsync(item, destinationDir, progress, ct);
            }

            throw new NotSupportedException(
                $"Workshop item {publishedFileId} has no downloadable content (no manifest and no file URL).");
        }

        var depotId = item.ConsumerAppId; // Workshop content lives in the app's own depot.
        progress?.Invoke(2, "Fetching depot key");
        var depotKey = await GetDepotKeyAsync(depotId, item.ConsumerAppId, ct);

        progress?.Invoke(4, "Fetching manifest");
        var manifest = await GetManifestAsync(depotId, item.ConsumerAppId, item.ManifestId, depotKey, "public", ct);

        return await DownloadManifestFilesAsync(manifest, item.ConsumerAppId, depotKey, destinationDir, progress, ct);
    }

    /// <summary>
    /// Download a legacy web-hosted Workshop file (pre-SteamPipe UGC served from
    /// steamusercontent.com) directly over HTTP into the staging dir, so the normal
    /// archive+upload pipeline can share it. No depot key, manifest, or Steam license is
    /// involved - the file URL is public. The dedupe/manifest slot uses the item's
    /// hcontent_file when present, else the published-file id (unique per item), so two
    /// different legacy items never collide in history.
    /// </summary>
    private async Task<ContentDownloadResult> DownloadLegacyWorkshopFileAsync(
        WorkshopItemInfo item, string destinationDir, Action<int, string>? progress, CancellationToken ct)
    {
        Directory.CreateDirectory(destinationDir);
        var dedupeId = item.ManifestId != 0 ? item.ManifestId : item.PublishedFileId;

        progress?.Invoke(1, "Fetching web-hosted file");
        _log.Info(LogSource, $"Workshop item {item.PublishedFileId} is legacy web-hosted; downloading over HTTP");

        // Bound the whole transfer; the static client has no timeout of its own.
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(TimeSpan.FromMinutes(30));
        var linkedCt = timeoutCts.Token;

        using var response = await LegacyHttp.GetAsync(
            item.LegacyFileUrl!, HttpCompletionOption.ResponseHeadersRead, linkedCt);
        response.EnsureSuccessStatusCode();

        var fileName = SafeLegacyFileName(item, response.Content.Headers.ContentType?.MediaType);
        var destPath = ResolveSafePath(destinationDir, fileName);
        Directory.CreateDirectory(Path.GetDirectoryName(destPath)!);

        var total = response.Content.Headers.ContentLength
            ?? (item.FileSize > 0 ? (long)item.FileSize : 0);

        await using (var src = await response.Content.ReadAsStreamAsync(linkedCt))
        await using (var dst = new FileStream(destPath, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            var buffer = ArrayPool<byte>.Shared.Rent(1 << 16);
            try
            {
                long written = 0;
                var lastPct = -1;
                int read;
                while ((read = await src.ReadAsync(buffer, linkedCt)) > 0)
                {
                    await dst.WriteAsync(buffer.AsMemory(0, read), linkedCt);
                    written += read;
                    if (total > 0)
                    {
                        // Map bytes to 1..99% (compression/upload own the rest).
                        var pct = (int)Math.Clamp(1 + written * 98 / total, 1, 99);
                        if (pct != lastPct)
                        {
                            lastPct = pct;
                            progress?.Invoke(pct, $"Downloading web-hosted file {pct}%");
                        }
                    }
                }
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(buffer);
            }
        }

        var size = new FileInfo(destPath).Length;
        progress?.Invoke(100, "Downloaded");
        _log.Info(LogSource,
            $"Downloaded legacy Workshop file {item.PublishedFileId} \"{item.Title}\" ({size / 1024} KB) -> {fileName}");

        return new ContentDownloadResult(
            item.ConsumerAppId, item.ConsumerAppId, dedupeId, destinationDir, 1, size);
    }

    /// <summary>
    /// A safe, single-segment filename for a legacy UGC download: the server-declared
    /// name (basename only) if any, else the item title, else the id - stripped of path
    /// and filesystem-unsafe characters, with an extension inferred from the response
    /// content type when the name lacks one.
    /// </summary>
    private static string SafeLegacyFileName(WorkshopItemInfo item, string? mediaType)
    {
        var raw = !string.IsNullOrWhiteSpace(item.FileName)
            ? Path.GetFileName(item.FileName!.Replace('\\', '/'))
            : null;
        if (string.IsNullOrWhiteSpace(raw))
        {
            raw = string.IsNullOrWhiteSpace(item.Title) ? item.PublishedFileId.ToString() : item.Title;
        }

        var cleaned = new string(raw.Select(c =>
            char.IsControl(c) || "<>:\"/\\|?*".Contains(c) ? '_' : c).ToArray()).Trim().Trim('.');
        if (cleaned.Length == 0)
        {
            cleaned = item.PublishedFileId.ToString();
        }

        if (!Path.HasExtension(cleaned) && ExtensionForMediaType(mediaType) is { } ext)
        {
            cleaned += ext;
        }

        return cleaned;
    }

    private static string? ExtensionForMediaType(string? mediaType) => mediaType?.ToLowerInvariant() switch
    {
        "image/jpeg" => ".jpg",
        "image/png" => ".png",
        "image/gif" => ".gif",
        "image/webp" => ".webp",
        "video/mp4" => ".mp4",
        "video/webm" => ".webm",
        "application/zip" => ".zip",
        "application/x-7z-compressed" => ".7z",
        _ => null,
    };

    /// <summary>
    /// Fetch (and cache) an app's PICS product-info KeyValues. A cached entry is
    /// reused only while younger than <paramref name="maxAge"/> (null = any age);
    /// <paramref name="forceRefresh"/> always re-fetches. Callers that must see the
    /// current build (a game download resolve, the change watcher) force a refresh;
    /// UI lookups accept the cache the watcher keeps fresh.
    /// </summary>
    public async Task<KeyValue> GetAppInfoAsync(
        uint appId, CancellationToken ct = default, bool forceRefresh = false, TimeSpan? maxAge = null)
    {
        if (!forceRefresh && _appInfo.TryGetValue(appId, out var cached) &&
            (maxAge is null || DateTimeOffset.UtcNow - cached.At < maxAge))
        {
            return cached.Kv;
        }

        var apps = Handler<SteamApps>();
        var tokens = await apps.PICSGetAccessTokens([appId], []).ToTask().WaitAsync(SteamCallTimeout, ct);

        var request = new SteamApps.PICSRequest(appId);
        if (tokens.AppTokens.TryGetValue(appId, out var token))
        {
            request.AccessToken = token;
        }

        var productInfo = await apps.PICSGetProductInfo([request], []).ToTask().WaitAsync(SteamCallTimeout, ct);
        foreach (var result in productInfo.Results ?? [])
        {
            if (result.Apps.TryGetValue(appId, out var info))
            {
                _appInfo[appId] = (info.KeyValues, DateTimeOffset.UtcNow);
                return info.KeyValues;
            }
        }

        throw new InvalidOperationException($"No app info for {appId} (unknown app or no access).");
    }

    /// <summary>Drop an app's cached appinfo so its next lookup re-fetches from PICS.</summary>
    public void InvalidateAppInfo(uint appId) => _appInfo.TryRemove(appId, out _);

    /// <summary>Drop all cached appinfo (e.g. a PICS full-update signal).</summary>
    public void InvalidateAllAppInfo() => _appInfo.Clear();

    /// <summary>
    /// Ask Steam for the apps/packages that changed since <paramref name="lastChangeNumber"/>.
    /// Returns the new change number, whether a full re-sync is required (our cursor was
    /// too old), and the changed app ids. Cheap: a delta, not a full library batch.
    /// </summary>
    public async Task<PicsChanges> GetPicsChangesAsync(uint lastChangeNumber, CancellationToken ct = default)
    {
        var callback = await Handler<SteamApps>()
            .PICSGetChangesSince(lastChangeNumber, sendAppChangelist: true, sendPackageChangelist: false)
            .ToTask().WaitAsync(SteamCallTimeout, ct);

        return new PicsChanges(
            callback.CurrentChangeNumber,
            callback.RequiresFullUpdate || callback.RequiresFullAppUpdate,
            callback.AppChanges.Keys.ToArray());
    }

    /// <summary>
    /// Enumerate the apps the logged-on account owns (games/applications/tools/
    /// demos): licenses -> packages -> app ids, then batched PICS to resolve name
    /// and type and filter out DLC/music/videos. Mirrors DepotDownloader's owned-app
    /// enumeration.
    /// </summary>
    public async Task<IReadOnlyList<OwnedApp>> EnumerateOwnedAppsAsync(CancellationToken ct = default)
    {
        await _session.LicensesReady.WaitAsync(TimeSpan.FromSeconds(60), ct);
        var apps = Handler<SteamApps>();

        // Drop revoked licenses before mapping packages -> apps. A lapsed free-weekend/promo
        // license lingers on the account flagged Expired (or Cancelled): Steam still lists it,
        // but it no longer entitles the paid content depots. Counting it would put an
        // undownloadable title in the catalog that only ships a stub when someone tries it.
        // A package kept alive by any non-revoked license still counts.
        const ELicenseFlags revoked =
            ELicenseFlags.Expired | ELicenseFlags.CancelledByUser | ELicenseFlags.CancelledByAdmin;
        var activePackages = _session.Licenses
            .Where(l => (l.LicenseFlags & revoked) == 0)
            .Select(l => l.PackageID)
            .ToHashSet();
        // Packages left with no surviving active license (fully revoked) are dropped.
        var droppedPackages = _session.Licenses
            .Select(l => l.PackageID)
            .Distinct()
            .Count(id => !activePackages.Contains(id));
        if (droppedPackages > 0)
        {
            _log.Info(LogSource,
                $"Skipping {droppedPackages} expired/cancelled package(s) during ownership scan.");
        }

        // Packages -> candidate app ids.
        var packageRequests = _session.Licenses
            .Select(l => l.PackageID)
            .Distinct()
            .Where(activePackages.Contains)
            .Select(pkgId =>
            {
                var req = new SteamApps.PICSRequest(pkgId);
                if (_session.PackageTokens.TryGetValue(pkgId, out var token))
                {
                    req.AccessToken = token;
                }

                return req;
            })
            .ToList();

        var candidates = new HashSet<uint>();
        // Track how each app was granted so we can flag free-to-play (only free
        // packages grant it) vs paid (at least one purchased package grants it).
        var grantedByFree = new HashSet<uint>();
        var grantedByPaid = new HashSet<uint>();
        if (packageRequests.Count > 0)
        {
            var pkgInfo = await apps.PICSGetProductInfo([], packageRequests).ToTask().WaitAsync(SteamCallTimeout, ct);
            foreach (var result in pkgInfo.Results ?? [])
            {
                foreach (var package in result.Packages.Values)
                {
                    // FreeOnDemand is how F2P titles are licensed; NoCost is the other
                    // genuinely-free grant. Anything else means the app was paid for.
                    var billingType = (EBillingType)package.KeyValues["billingtype"].AsInteger();
                    var isFreePkg = billingType is EBillingType.FreeOnDemand or EBillingType.NoCost;

                    foreach (var child in package.KeyValues["appids"].Children)
                    {
                        var appId = child.AsUnsignedInteger();
                        if (appId != 0)
                        {
                            candidates.Add(appId);
                            (isFreePkg ? grantedByFree : grantedByPaid).Add(appId);
                        }
                    }
                }
            }
        }

        // Batched PICS to resolve name + type; keep only playable app types.
        var owned = new List<OwnedApp>();
        var list = candidates.ToList();
        const int batchSize = 200;
        for (var i = 0; i < list.Count; i += batchSize)
        {
            ct.ThrowIfCancellationRequested();
            var batch = list.GetRange(i, Math.Min(batchSize, list.Count - i));
            foreach (var (appId, kv) in await GetAppInfoBatchAsync(batch, ct))
            {
                var common = kv["common"];
                var type = common["type"].Value?.ToLowerInvariant() ?? "";
                if (type is not ("game" or "application" or "tool" or "demo"))
                {
                    continue;
                }

                var name = common["name"].Value is { Length: > 0 } n ? n : appId.ToString();
                var isFree = grantedByFree.Contains(appId) && !grantedByPaid.Contains(appId);
                // OS platforms with downloadable content (windows,macos,linux). Empty =>
                // nothing to download at all. Metadata-only, no extra network calls.
                var supportedOs = GetSupportedOses(kv);
                var latestBuild = GetPublicBuildId(kv);
                owned.Add(new OwnedApp(appId, name, type, isFree, supportedOs.Length > 0, supportedOs, latestBuild));
            }
        }

        _log.Info(LogSource, $"Owned library: {owned.Count} apps across {packageRequests.Count} packages");
        return owned;
    }

    /// <summary>
    /// Conservative "can this be downloaded" check from an app's appinfo: true if any
    /// numeric depot has content for <paramref name="targetOs"/> on the public branch,
    /// or is a shared depot (depotfromapp). Returns false only when there is clearly no
    /// usable depot (e.g. delisted-and-stripped apps), so real games/tools/servers are
    /// never hidden by mistake. Metadata-only; makes no network calls.
    /// </summary>
    public static bool HasDownloadableDepots(KeyValue appInfo, string targetOs)
    {
        var depots = appInfo["depots"];
        if (depots == KeyValue.Invalid)
        {
            return false;
        }

        foreach (var depot in depots.Children)
        {
            // Only numeric children are depots (skip branches, baselanguages, etc.).
            if (!uint.TryParse(depot.Name, out _) || depot.Children.Count == 0)
            {
                continue;
            }

            var oslist = depot["config"]["oslist"];
            if (oslist != KeyValue.Invalid && !string.IsNullOrWhiteSpace(oslist.Value) &&
                !oslist.Value.Split(',').Contains(targetOs, StringComparer.OrdinalIgnoreCase))
            {
                continue;
            }

            // A public-branch manifest means downloadable content; a shared depot
            // (depotfromapp) resolves its manifest from another owned app.
            if (depot["manifests"]["public"] != KeyValue.Invalid ||
                depot["depotfromapp"] != KeyValue.Invalid)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>The public-branch build id from an app's appinfo
    /// (depots.branches.public.buildid), or 0 if absent. This is the same value the
    /// game download dedupes on, so comparing it to an archived link's build id tells
    /// the browse page whether a newer build has shipped. Metadata-only; no network.</summary>
    public static uint GetPublicBuildId(KeyValue appInfo)
    {
        var buildId = appInfo["depots"]["branches"]["public"]["buildid"];
        return buildId != KeyValue.Invalid && uint.TryParse(buildId.Value, out var id) ? id : 0;
    }

    /// <summary>The OS platforms this app can be downloaded for, ordered
    /// windows,macos,linux, as a CSV. Derived from the appinfo depots' oslist (a
    /// downloadable depot with no oslist is OS-agnostic shared content). Empty means no
    /// downloadable depots at all. Metadata-only; makes no network calls.</summary>
    public static string GetSupportedOses(KeyValue appInfo)
    {
        var depots = appInfo["depots"];
        if (depots == KeyValue.Invalid)
        {
            return "";
        }

        var oses = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var hasBareDepot = false;
        foreach (var depot in depots.Children)
        {
            if (!uint.TryParse(depot.Name, out _) || depot.Children.Count == 0)
            {
                continue;
            }

            // Only depots that actually have downloadable content count.
            if (depot["manifests"]["public"] == KeyValue.Invalid && depot["depotfromapp"] == KeyValue.Invalid)
            {
                continue;
            }

            var oslist = depot["config"]["oslist"];
            if (oslist == KeyValue.Invalid || string.IsNullOrWhiteSpace(oslist.Value))
            {
                hasBareDepot = true; // no oslist = shared content, pulled for any OS build
                continue;
            }

            foreach (var os in oslist.Value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                if (os is "windows" or "macos" or "linux")
                {
                    oses.Add(os.ToLowerInvariant());
                }
            }
        }

        // Apps whose only depots are OS-agnostic (many tools) default to Windows.
        if (oses.Count == 0 && hasBareDepot)
        {
            oses.Add("windows");
        }

        return string.Join(",", new[] { "windows", "macos", "linux" }.Where(oses.Contains));
    }

    /// <summary>Batched PICS product-info fetch (access tokens + info) for many apps.</summary>
    private async Task<IReadOnlyList<(uint AppId, KeyValue KeyValues)>> GetAppInfoBatchAsync(
        IReadOnlyList<uint> appIds, CancellationToken ct)
    {
        var apps = Handler<SteamApps>();
        var tokens = await apps.PICSGetAccessTokens(appIds, []).ToTask().WaitAsync(SteamCallTimeout, ct);

        var requests = appIds.Select(id =>
        {
            var req = new SteamApps.PICSRequest(id);
            if (tokens.AppTokens.TryGetValue(id, out var token))
            {
                req.AccessToken = token;
            }

            return req;
        }).ToList();

        var productInfo = await apps.PICSGetProductInfo(requests, []).ToTask().WaitAsync(SteamCallTimeout, ct);
        var result = new List<(uint, KeyValue)>();
        foreach (var callback in productInfo.Results ?? [])
        {
            foreach (var app in callback.Apps)
            {
                _appInfo[app.Key] = (app.Value.KeyValues, DateTimeOffset.UtcNow);
                result.Add((app.Key, app.Value.KeyValues));
            }
        }

        return result;
    }

    /// <summary>List an app's available branches (name, build id, password flag).</summary>
    public async Task<GameBranches> ListBranchesAsync(uint appId, CancellationToken ct = default)
    {
        var app = await GetAppInfoAsync(appId, ct);
        var name = app["common"]["name"].Value is { Length: > 0 } n ? n : appId.ToString();

        var branches = new List<GameBranch>();
        foreach (var b in app["depots"]["branches"].Children)
        {
            if (string.IsNullOrEmpty(b.Name))
            {
                continue;
            }

            uint.TryParse(b["buildid"].Value, out var buildId);
            var pwd = b["pwdrequired"] != KeyValue.Invalid && b["pwdrequired"].AsBoolean();
            branches.Add(new GameBranch(b.Name, buildId, pwd));
        }

        // Public first, then the rest alphabetically.
        branches = branches
            .OrderBy(b => b.Name == "public" ? 0 : 1)
            .ThenBy(b => b.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var supportedOs = GetSupportedOses(app)
            .Split(',', StringSplitOptions.RemoveEmptyEntries)
            .ToList();
        if (supportedOs.Count == 0)
        {
            supportedOs.Add("windows"); // always offer at least Windows in the picker
        }

        return new GameBranches(appId, name, branches, supportedOs);
    }

    /// <summary>
    /// Resolve a game's download plan for a branch and target OS: the depots whose
    /// OS matches (depots with no oslist are always included), each with its
    /// branch manifest, plus the branch build id for dedupe.
    /// </summary>
    public async Task<GameInfo> ResolveGameAsync(
        uint appId, string branch = "public", string targetOs = "windows", CancellationToken ct = default)
    {
        // Force-fresh appinfo: this build id + depot GIDs are the dedupe key, so a stale
        // cache here would reuse an old archive when a newer build has actually shipped.
        var app = await GetAppInfoAsync(appId, ct, forceRefresh: true);
        var common = app["common"];
        var name = common["name"].Value is { Length: > 0 } n ? n : appId.ToString();
        var installDir = app["config"]["installdir"].Value;

        var depotsKv = app["depots"];
        if (depotsKv == KeyValue.Invalid)
        {
            throw new InvalidOperationException($"App {appId} has no depots section.");
        }

        uint buildId = 0;
        var branchNode = depotsKv["branches"][branch]["buildid"];
        if (branchNode != KeyValue.Invalid)
        {
            uint.TryParse(branchNode.Value, out buildId);
        }

        var depots = new List<GameDepot>();
        foreach (var depotChild in depotsKv.Children)
        {
            ct.ThrowIfCancellationRequested();
            if (!uint.TryParse(depotChild.Name, out var depotId) || depotChild.Children.Count == 0)
            {
                continue;
            }

            var config = depotChild["config"];
            if (config != KeyValue.Invalid)
            {
                var oslist = config["oslist"];
                if (oslist != KeyValue.Invalid && !string.IsNullOrWhiteSpace(oslist.Value) &&
                    !oslist.Value.Split(',').Contains(targetOs, StringComparer.OrdinalIgnoreCase))
                {
                    continue;
                }

                if (config["lowviolence"] != KeyValue.Invalid && config["lowviolence"].AsBoolean())
                {
                    continue;
                }
            }

            var manifestId = await ResolveDepotManifestAsync(depotChild, depotId, appId, branch, ct);
            if (manifestId == 0)
            {
                continue;
            }

            var depotName = depotChild["name"].Value is { Length: > 0 } dn ? dn : name;
            // A "dlcappid" marks this depot as belonging to a DLC (unowned DLC is expected
            // to deny). Base-game content depots have no dlcappid; a denial there means the
            // account is not entitled to the game itself (e.g. a lapsed free-weekend license).
            var isDlc = depotChild["dlcappid"] != KeyValue.Invalid &&
                        depotChild["dlcappid"].AsUnsignedInteger() != 0;
            depots.Add(new GameDepot(depotId, manifestId, depotName, isDlc));
        }

        if (depots.Count == 0)
        {
            throw new InvalidOperationException(
                $"App {appId} has no downloadable depots for OS '{targetOs}' on branch '{branch}'.");
        }

        _log.Info(LogSource,
            $"Resolved game {appId} \"{name}\": build {buildId}, {depots.Count} depots (OS {targetOs}, branch {branch})");
        return new GameInfo(appId, name, installDir, buildId, depots);
    }

    /// <summary>Manifest GID for a depot on a branch, following shared depotfromapp links.</summary>
    private async Task<ulong> ResolveDepotManifestAsync(
        KeyValue depotChild, uint depotId, uint appId, string branch, CancellationToken ct)
    {
        var manifests = depotChild["manifests"];
        if (manifests == KeyValue.Invalid || manifests.Children.Count == 0)
        {
            // Shared depot hosted by another app; resolve there instead.
            var from = depotChild["depotfromapp"];
            if (from != KeyValue.Invalid)
            {
                var otherAppId = from.AsUnsignedInteger();
                if (otherAppId != 0 && otherAppId != appId)
                {
                    var other = await GetAppInfoAsync(otherAppId, ct);
                    var otherDepot = other["depots"][depotId.ToString()];
                    if (otherDepot != KeyValue.Invalid)
                    {
                        return await ResolveDepotManifestAsync(otherDepot, depotId, otherAppId, branch, ct);
                    }
                }
            }

            return 0;
        }

        var gid = manifests[branch]["gid"];
        return gid != KeyValue.Invalid && ulong.TryParse(gid.Value, out var manifestId) ? manifestId : 0;
    }

    /// <summary>
    /// High-level: resolve a game and download every accessible matching depot into
    /// <paramref name="destinationDir"/> as one merged install. Depots the account
    /// cannot access (e.g. unowned DLC) are skipped with a warning.
    /// </summary>
    public async Task<ContentDownloadResult> DownloadGameAsync(
        uint appId,
        string destinationDir,
        string branch = "public",
        string targetOs = "windows",
        Action<int, string>? progress = null,
        CancellationToken ct = default)
    {
        var info = await ResolveGameAsync(appId, branch, targetOs, ct);
        Directory.CreateDirectory(destinationDir);
        var destRoot = Path.GetFullPath(destinationDir);

        progress?.Invoke(1, $"Fetching {info.Depots.Count} depot manifests");
        var ready = new List<(DepotManifest Manifest, byte[] Key)>();
        var deniedDepots = 0;
        var deniedBaseDepots = 0;
        var succeededBaseDepots = 0;
        foreach (var depot in info.Depots)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                var key = await GetDepotKeyAsync(depot.DepotId, appId, ct);
                var manifest = await GetManifestAsync(depot.DepotId, appId, depot.ManifestId, key, branch, ct);
                ready.Add((manifest, key));
                if (!depot.IsDlc)
                {
                    succeededBaseDepots++;
                }
            }
            catch (DepotAccessDeniedException)
            {
                // An unowned depot: either unowned DLC inside an otherwise-owned game
                // (expected - some depots still succeed, so we download those), or a base
                // content depot the account is not entitled to. A denied base depot is not by
                // itself proof the game is unowned: some titles ship base-tier (no dlcappid)
                // depots that a rightful owner still cannot key - developer/Debug depots, and
                // region-split depots (e.g. FromSoftware's mutually-exclusive JP vs ROW), where
                // an owner only ever holds one region and is denied the other. Track denied
                // base depots, but decide entitlement below on whether ANY base depot succeeded
                // rather than treating a single base denial as fatal.
                deniedDepots++;
                if (!depot.IsDlc)
                {
                    deniedBaseDepots++;
                }

                _log.Debug(LogSource,
                    $"Skipping depot {depot.DepotId} ({depot.Name}): not owned by the active account " +
                    $"(AccessDenied, {(depot.IsDlc ? "DLC" : "base content")})");
            }
            catch (Exception ex) when (ex is OperationCanceledException || !_session.IsLoggedOn)
            {
                // The Steam session dropped/was replaced mid-fetch (SteamKit cancels
                // in-flight requests on disconnect; a "not logged on" error means the same).
                // Do NOT swallow this per depot: every remaining depot would then "skip" and
                // the job would fail with a misleading terminal "No accessible depots".
                // Abort the whole download and surface it as a cancellation so the pipeline
                // classifies it as a retriable Steam disconnect (or a shutdown resume when
                // our own token is cancelled), not a permanent no-content failure.
                if (ex is OperationCanceledException)
                {
                    throw;
                }

                throw new OperationCanceledException(
                    "Steam session dropped while fetching depot manifests", ex);
            }
            catch (Exception ex)
            {
                _log.Warn(LogSource, $"Skipping depot {depot.DepotId} ({depot.Name}): {ex.Message}");
            }
        }

        // Entitlement test: the account owns the game only if it could key at least one
        // BASE-content depot. When EVERY base depot denied (succeededBaseDepots == 0) the app
        // id is a false positive - a lapsed free-weekend/promo/family license that grants the
        // id but no paid content - so downloading the accessible scraps would ship a stub as
        // the whole game and cache a junk link. Refuse and surface AccessDenied so the router
        // rotates to another owning account; if none can, the job fails with a plain "not
        // owned" message instead of a bad upload. But when some base content DID authorize,
        // the game is genuinely owned and the remaining base denials are expected variants
        // (Debug/dev depots, the non-matching region depot) - skip them and download the rest.
        if (deniedBaseDepots > 0 && succeededBaseDepots == 0)
        {
            throw new DepotAccessDeniedException(appId,
                $"The active account is not entitled to app {appId}: all base content " +
                "depot(s) were denied (AccessDenied) - likely a free-weekend/promo license that grants " +
                "the app but not the game.");
        }

        if (ready.Count == 0)
        {
            // Every depot denied on entitlement -> this account cannot download the app.
            // Surface it as AccessDenied so AccountRouter rotates to the next owning
            // account rather than failing the job outright.
            if (deniedDepots > 0)
            {
                throw new DepotAccessDeniedException(appId,
                    $"The active account cannot download app {appId}: every depot key was denied (AccessDenied).");
            }

            throw new InvalidOperationException($"No accessible depots for app {appId}.");
        }

        // Partial ownership is normal and not an error - the owned base content still
        // downloads. Skipped depots are unowned DLC, plus any base-tier depots a rightful
        // owner cannot key (Debug/dev depots, the non-matching region depot). Emit one
        // visible summary rather than a per-depot AccessDenied warning per skipped depot (the
        // old behaviour read as a total failure); the per-depot detail stays at Debug above.
        if (deniedDepots > 0)
        {
            var baseNote = deniedBaseDepots > 0
                ? $" ({deniedBaseDepots} of them base-tier - Debug/dev or non-matching region depots)"
                : "";
            _log.Info(LogSource,
                $"App {appId}: downloading {ready.Count} owned depot(s); skipped {deniedDepots} depot(s) " +
                $"not owned by the active account{baseNote}.");
        }

        var tracker = new ByteTracker
        {
            Total = ready.Sum(r => (r.Manifest.Files ?? [])
                .Where(f => !f.Flags.HasFlag(EDepotFileFlag.Directory))
                .Sum(f => (long)f.TotalSize)),
        };

        // Free-space is checked inside DownloadChunksAsync, after it discounts any bytes
        // a prior (resumed) run already wrote. Feed every depot's chunks into one shared
        // adaptive pool so the whole game download saturates the link, rather than
        // draining one depot at a time.
        var totalFiles = await DownloadChunksAsync(
            ready.Select(r => (r.Manifest, appId, r.Key)).ToList(), destRoot, tracker, progress, ct);

        _log.Info(LogSource,
            $"Downloaded {totalFiles} files ({tracker.Done / (1024 * 1024)} MB) across {ready.Count} depots to {destinationDir}");

        // Emit a sanitized appmanifest so the archive is a drop-in Steam install without
        // fingerprinting our account: SteamAppManifest zeroes LastOwner (SteamID64) and
        // every client-activity timestamp. Best-effort - the download already succeeded,
        // so a manifest hiccup should not fail the job (and never leaks PII either way).
        try
        {
            var installed = ready
                .Select(r => new InstalledDepot(
                    r.Manifest.DepotID,
                    r.Manifest.ManifestGID,
                    (r.Manifest.Files ?? [])
                        .Where(f => !f.Flags.HasFlag(EDepotFileFlag.Directory))
                        .Sum(f => (long)f.TotalSize)))
                .ToList();

            await SteamAppManifest.WriteAsync(
                destRoot, appId, info.Name, info.InstallDir, info.BuildId, tracker.Done, installed, ct);
            _log.Info(LogSource, $"Wrote sanitized {SteamAppManifest.FileNameFor(appId)} (SteamID + activity times zeroed)");
        }
        catch (Exception ex)
        {
            _log.Warn(LogSource, $"Could not write appmanifest for app {appId}: {ex.Message}");
        }

        // Dedupe on (app, build): identical build == identical content.
        return new ContentDownloadResult(appId, appId, info.BuildId, destinationDir, totalFiles, tracker.Done);
    }

    private async Task<CdnClient> GetCdnAsync(CancellationToken ct)
    {
        if (_cdn is not null && _servers is { Count: > 0 })
        {
            return _cdn;
        }

        await _cdnGate.WaitAsync(ct);
        try
        {
            _cdn ??= new CdnClient(Client);
            if (_servers is not { Count: > 0 })
            {
                var servers = await Handler<SteamContent>().GetServersForSteamPipe(null, null);
                _servers = servers.Where(s => !string.IsNullOrEmpty(s.Host)).ToList();
                if (_servers.Count == 0)
                {
                    throw new InvalidOperationException("Steam returned no SteamPipe CDN servers");
                }

                _log.Info(LogSource, $"Using {_servers.Count} SteamPipe CDN servers");
            }

            return _cdn;
        }
        finally
        {
            _cdnGate.Release();
        }
    }

    private async Task<CdnServer> NextServerAsync(CancellationToken ct)
    {
        await GetCdnAsync(ct);
        var servers = _servers!;
        var index = Interlocked.Increment(ref _serverCursor);
        return servers[(index & int.MaxValue) % servers.Count];
    }

    private static string ResolveSafePath(string root, string relative)
    {
        var normalized = relative.Replace('\\', Path.DirectorySeparatorChar).TrimStart(Path.DirectorySeparatorChar);
        var full = Path.GetFullPath(Path.Combine(root, normalized));
        if (!full.StartsWith(root, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException($"Manifest path escapes destination: {relative}");
        }

        return full;
    }
}
