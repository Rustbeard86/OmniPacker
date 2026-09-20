using Microsoft.Data.Sqlite;
using SteamForge.Abstractions.Plugins;
using SteamForge.Engine.Content;

namespace SteamForge.Engine.Jobs;

/// <summary>
/// SQLite persistence for the job queue and the dedupe history. Deliberately
/// lean (no ORM) to keep the container light. A single connection guarded by a
/// lock is fine: the queue processes one job at a time.
/// </summary>
public sealed class JobStore : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly object _gate = new();

    public JobStore(string databasePath)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(databasePath)!);
        _connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Mode = SqliteOpenMode.ReadWriteCreate,
        }.ToString());
        _connection.Open();
        Initialize();
    }

    private void Initialize()
    {
        lock (_gate)
        {
            using var cmd = _connection.CreateCommand();
            cmd.CommandText = """
                PRAGMA journal_mode=WAL;
                CREATE TABLE IF NOT EXISTS jobs (
                    id TEXT PRIMARY KEY,
                    kind INTEGER NOT NULL,
                    app_id INTEGER NOT NULL,
                    workshop_id TEXT NOT NULL,
                    display_name TEXT NOT NULL,
                    status INTEGER NOT NULL,
                    progress INTEGER NOT NULL,
                    message TEXT NOT NULL,
                    error TEXT,
                    result_url TEXT,
                    result_password TEXT,
                    manifest_id TEXT NOT NULL,
                    depot_id INTEGER NOT NULL,
                    reused INTEGER NOT NULL,
                    password TEXT,
                    created_at INTEGER NOT NULL,
                    completed_at INTEGER,
                    expires_at INTEGER,
                    provider_ref TEXT,
                    expiry_hours INTEGER,
                    branch TEXT
                );
                CREATE TABLE IF NOT EXISTS history (
                    depot_id INTEGER NOT NULL,
                    manifest_id TEXT NOT NULL,
                    app_id INTEGER NOT NULL,
                    provider TEXT NOT NULL,
                    url TEXT NOT NULL,
                    password TEXT,
                    uploaded_at INTEGER NOT NULL,
                    expires_at INTEGER,
                    provider_ref TEXT,
                    verified_at INTEGER,
                    PRIMARY KEY (depot_id, manifest_id)
                );
                CREATE TABLE IF NOT EXISTS ownership (
                    account TEXT NOT NULL,
                    app_id INTEGER NOT NULL,
                    name TEXT NOT NULL,
                    type TEXT NOT NULL,
                    scanned_at INTEGER NOT NULL,
                    PRIMARY KEY (account, app_id)
                );
                CREATE TABLE IF NOT EXISTS hidden_apps (
                    app_id INTEGER PRIMARY KEY,
                    hidden_at INTEGER NOT NULL
                );
                CREATE TABLE IF NOT EXISTS ip_charges (
                    ip TEXT NOT NULL,
                    bytes INTEGER NOT NULL,
                    charged_at INTEGER NOT NULL
                );
                CREATE INDEX IF NOT EXISTS ix_ip_charges ON ip_charges (ip, charged_at);
                """;
            cmd.ExecuteNonQuery();

            // Additive migrations for databases created before these columns.
            EnsureColumn("jobs", "provider_ref", "TEXT");
            EnsureColumn("jobs", "expiry_hours", "INTEGER");
            EnsureColumn("jobs", "branch", "TEXT");
            EnsureColumn("jobs", "target_os", "TEXT");
            EnsureColumn("jobs", "resume_attempts", "INTEGER NOT NULL DEFAULT 0");
            EnsureColumn("jobs", "staging_key", "TEXT");
            EnsureColumn("jobs", "submitter_ip", "TEXT");
            EnsureColumn("history", "provider_ref", "TEXT");
            EnsureColumn("history", "verified_at", "INTEGER");
            EnsureColumn("history", "branch", "TEXT NOT NULL DEFAULT 'public'");
            EnsureColumn("history", "workshop_id", "TEXT NOT NULL DEFAULT '0'");
            EnsureColumn("history", "title", "TEXT");
            BackfillWorkshopHistory();
            EnsureColumn("ownership", "is_free", "INTEGER NOT NULL DEFAULT 0");
            EnsureColumn("ownership", "downloadable", "INTEGER NOT NULL DEFAULT 1");
            EnsureColumn("ownership", "supported_os", "TEXT NOT NULL DEFAULT 'windows'");
            EnsureColumn("ownership", "latest_build_id", "INTEGER NOT NULL DEFAULT 0");
            // Tag each budget charge with the content it paid for, so a retry/resume of the
            // same staging key is not double-charged (see AlreadyChargedKey).
            EnsureColumn("ip_charges", "staging_key", "TEXT");
        }
    }

    /// <summary>
    /// Backfill Workshop provenance (workshop_id + title) onto history rows written
    /// before those columns existed, by matching each row's (depot_id, manifest_id) to a
    /// retained Workshop job (jobs.workshop_id != '0'). Idempotent: only touches rows
    /// still flagged as a game (workshop_id '0'/NULL) that have a matching workshop job,
    /// so once a row is labelled it is left alone and game rows are never mislabelled.
    /// </summary>
    private void BackfillWorkshopHistory()
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = """
            UPDATE history
            SET workshop_id = (
                    SELECT j.workshop_id FROM jobs j
                    WHERE j.depot_id = history.depot_id AND j.manifest_id = history.manifest_id
                      AND j.workshop_id <> '0' LIMIT 1),
                title = (
                    SELECT j.display_name FROM jobs j
                    WHERE j.depot_id = history.depot_id AND j.manifest_id = history.manifest_id
                      AND j.workshop_id <> '0' LIMIT 1)
            WHERE (workshop_id = '0' OR workshop_id IS NULL)
              AND EXISTS (
                    SELECT 1 FROM jobs j
                    WHERE j.depot_id = history.depot_id AND j.manifest_id = history.manifest_id
                      AND j.workshop_id <> '0');
            """;
        cmd.ExecuteNonQuery();
    }

    /// <summary>Add a column if the table does not already have it (idempotent).</summary>
    private void EnsureColumn(string table, string column, string type)
    {
        using var info = _connection.CreateCommand();
        info.CommandText = $"PRAGMA table_info({table});";
        using (var reader = info.ExecuteReader())
        {
            var nameOrdinal = reader.GetOrdinal("name");
            while (reader.Read())
            {
                if (string.Equals(reader.GetString(nameOrdinal), column, StringComparison.OrdinalIgnoreCase))
                {
                    return;
                }
            }
        }

        using var alter = _connection.CreateCommand();
        alter.CommandText = $"ALTER TABLE {table} ADD COLUMN {column} {type};";
        alter.ExecuteNonQuery();
    }

    public void Insert(Job job)
    {
        lock (_gate)
        {
            using var cmd = _connection.CreateCommand();
            cmd.CommandText = """
                INSERT INTO jobs (id, kind, app_id, workshop_id, display_name, status,
                    progress, message, error, result_url, result_password, manifest_id,
                    depot_id, reused, password, created_at, completed_at, expires_at, provider_ref, expiry_hours, branch, target_os, staging_key, submitter_ip)
                VALUES ($id, $kind, $app, $wsid, $name, $status, $progress, $message,
                    $error, $url, $pw, $manifest, $depot, $reused, $reqpw, $created,
                    $completed, $expires, $pref, $exph, $branch, $target_os, $stagingkey, $sip);
                """;
            Bind(cmd, job);
            cmd.ExecuteNonQuery();
        }
    }

    public void Update(Job job)
    {
        lock (_gate)
        {
            using var cmd = _connection.CreateCommand();
            cmd.CommandText = """
                UPDATE jobs SET kind=$kind, app_id=$app, workshop_id=$wsid, display_name=$name,
                    status=$status, progress=$progress, message=$message, error=$error,
                    result_url=$url, result_password=$pw, manifest_id=$manifest, depot_id=$depot,
                    reused=$reused, password=$reqpw, created_at=$created, completed_at=$completed,
                    expires_at=$expires, provider_ref=$pref, expiry_hours=$exph, branch=$branch,
                    target_os=$target_os, staging_key=$stagingkey, submitter_ip=$sip
                WHERE id=$id;
                """;
            Bind(cmd, job);
            cmd.ExecuteNonQuery();
        }
    }

    public Job? Get(string id)
    {
        lock (_gate)
        {
            using var cmd = _connection.CreateCommand();
            cmd.CommandText = "SELECT * FROM jobs WHERE id=$id;";
            cmd.Parameters.AddWithValue("$id", id);
            using var reader = cmd.ExecuteReader();
            return reader.Read() ? ReadJob(reader) : null;
        }
    }

    public IReadOnlyList<JobSnapshot> List(int limit = 100)
    {
        lock (_gate)
        {
            using var cmd = _connection.CreateCommand();
            cmd.CommandText = "SELECT * FROM jobs ORDER BY created_at DESC LIMIT $limit;";
            cmd.Parameters.AddWithValue("$limit", limit);
            using var reader = cmd.ExecuteReader();
            var list = new List<JobSnapshot>();
            while (reader.Read())
            {
                list.Add(ReadJob(reader).ToSnapshot());
            }

            return list;
        }
    }

    /// <summary>Remove a job record from the queue (does not touch the upload).</summary>
    public void DeleteJob(string id)
    {
        lock (_gate)
        {
            using var cmd = _connection.CreateCommand();
            cmd.CommandText = "DELETE FROM jobs WHERE id=$id;";
            cmd.Parameters.AddWithValue("$id", id);
            cmd.ExecuteNonQuery();
        }
    }

    /// <summary>Ready jobs whose expiry has passed - candidates for auto-purge.</summary>
    public IReadOnlyList<Job> ListExpiredReady(DateTimeOffset now)
    {
        lock (_gate)
        {
            using var cmd = _connection.CreateCommand();
            cmd.CommandText = "SELECT * FROM jobs WHERE status=$ready AND expires_at IS NOT NULL AND expires_at < $now;";
            cmd.Parameters.AddWithValue("$ready", (int)JobStatus.Ready);
            cmd.Parameters.AddWithValue("$now", now.ToUnixTimeSeconds());
            using var reader = cmd.ExecuteReader();
            var list = new List<Job>();
            while (reader.Read())
            {
                list.Add(ReadJob(reader));
            }

            return list;
        }
    }

    /// <summary>Oldest queued job, or null if the queue is empty.</summary>
    public Job? ClaimNextQueued()
    {
        lock (_gate)
        {
            using var cmd = _connection.CreateCommand();
            cmd.CommandText = "SELECT * FROM jobs WHERE status=$q ORDER BY created_at ASC LIMIT 1;";
            cmd.Parameters.AddWithValue("$q", (int)JobStatus.Queued);
            using var reader = cmd.ExecuteReader();
            return reader.Read() ? ReadJob(reader) : null;
        }
    }

    public int CountQueued()
    {
        lock (_gate)
        {
            using var cmd = _connection.CreateCommand();
            cmd.CommandText = "SELECT COUNT(*) FROM jobs WHERE status=$q;";
            cmd.Parameters.AddWithValue("$q", (int)JobStatus.Queued);
            return Convert.ToInt32(cmd.ExecuteScalar());
        }
    }

    /// <summary>Find a non-expired prior upload of the exact same manifest.</summary>
    public HistoryEntry? FindHistory(uint depotId, ulong manifestId)
    {
        lock (_gate)
        {
            using var cmd = _connection.CreateCommand();
            cmd.CommandText = "SELECT * FROM history WHERE depot_id=$depot AND manifest_id=$manifest;";
            cmd.Parameters.AddWithValue("$depot", depotId);
            cmd.Parameters.AddWithValue("$manifest", manifestId.ToString());
            using var reader = cmd.ExecuteReader();
            if (!reader.Read())
            {
                return null;
            }

            return new HistoryEntry(
                (uint)reader.GetInt64(reader.GetOrdinal("app_id")),
                (uint)reader.GetInt64(reader.GetOrdinal("depot_id")),
                ulong.Parse(reader.GetString(reader.GetOrdinal("manifest_id"))),
                reader.GetString(reader.GetOrdinal("provider")),
                reader.GetString(reader.GetOrdinal("url")),
                reader.IsDBNull(reader.GetOrdinal("password")) ? null : reader.GetString(reader.GetOrdinal("password")),
                DateTimeOffset.FromUnixTimeSeconds(reader.GetInt64(reader.GetOrdinal("uploaded_at"))),
                ReadNullableTime(reader, "expires_at"),
                reader.IsDBNull(reader.GetOrdinal("provider_ref")) ? null : reader.GetString(reader.GetOrdinal("provider_ref")),
                ReadNullableTime(reader, "verified_at"),
                ReadBranch(reader),
                ReadWorkshopId(reader),
                ReadTitle(reader));
        }
    }

    /// <summary>Branch column, defaulting to "public" for rows predating the column.</summary>
    private static string ReadBranch(SqliteDataReader reader)
    {
        var ordinal = reader.GetOrdinal("branch");
        return reader.IsDBNull(ordinal) || reader.GetString(ordinal) is not { Length: > 0 } b ? "public" : b;
    }

    /// <summary>Workshop id column (stored as text), 0 for game rows or rows predating it.</summary>
    private static ulong ReadWorkshopId(SqliteDataReader reader)
    {
        var ordinal = reader.GetOrdinal("workshop_id");
        return !reader.IsDBNull(ordinal) && ulong.TryParse(reader.GetString(ordinal), out var id) ? id : 0;
    }

    /// <summary>Workshop-item title column, null for game rows or rows predating it.</summary>
    private static string? ReadTitle(SqliteDataReader reader)
    {
        var ordinal = reader.GetOrdinal("title");
        return reader.IsDBNull(ordinal) ? null : reader.GetString(ordinal);
    }

    /// <summary>Update the last-verified timestamp for a history entry.</summary>
    public void TouchHistoryVerified(uint depotId, ulong manifestId, DateTimeOffset when)
    {
        lock (_gate)
        {
            using var cmd = _connection.CreateCommand();
            cmd.CommandText = "UPDATE history SET verified_at=$v WHERE depot_id=$depot AND manifest_id=$manifest;";
            cmd.Parameters.AddWithValue("$v", when.ToUnixTimeSeconds());
            cmd.Parameters.AddWithValue("$depot", depotId);
            cmd.Parameters.AddWithValue("$manifest", manifestId.ToString());
            cmd.ExecuteNonQuery();
        }
    }

    /// <summary>Update a history entry's expiry (kept in step with a provider change).</summary>
    public void UpdateHistoryExpiry(uint depotId, ulong manifestId, DateTimeOffset? expiresAt)
    {
        lock (_gate)
        {
            using var cmd = _connection.CreateCommand();
            cmd.CommandText = "UPDATE history SET expires_at=$e WHERE depot_id=$depot AND manifest_id=$manifest;";
            cmd.Parameters.AddWithValue("$e", (object?)expiresAt?.ToUnixTimeSeconds() ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$depot", depotId);
            cmd.Parameters.AddWithValue("$manifest", manifestId.ToString());
            cmd.ExecuteNonQuery();
        }
    }

    /// <summary>Label a history entry with Workshop provenance (id + title), e.g. when an
    /// older row is reused by a re-queued Workshop item so it surfaces on the Workshop page.</summary>
    public void UpdateHistoryWorkshopInfo(uint depotId, ulong manifestId, ulong workshopId, string? title)
    {
        lock (_gate)
        {
            using var cmd = _connection.CreateCommand();
            cmd.CommandText = "UPDATE history SET workshop_id=$wsid, title=$title WHERE depot_id=$depot AND manifest_id=$manifest;";
            cmd.Parameters.AddWithValue("$wsid", workshopId.ToString());
            cmd.Parameters.AddWithValue("$title", (object?)title ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$depot", depotId);
            cmd.Parameters.AddWithValue("$manifest", manifestId.ToString());
            cmd.ExecuteNonQuery();
        }
    }

    /// <summary>Update a history entry's password (kept in step with a provider change).</summary>
    public void UpdateHistoryPassword(uint depotId, ulong manifestId, string? password)
    {
        lock (_gate)
        {
            using var cmd = _connection.CreateCommand();
            cmd.CommandText = "UPDATE history SET password=$pw WHERE depot_id=$depot AND manifest_id=$manifest;";
            cmd.Parameters.AddWithValue("$pw", (object?)password ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$depot", depotId);
            cmd.Parameters.AddWithValue("$manifest", manifestId.ToString());
            cmd.ExecuteNonQuery();
        }
    }

    /// <summary>Remove a history entry (e.g. its upload no longer exists).</summary>
    public void DeleteHistory(uint depotId, ulong manifestId)
    {
        lock (_gate)
        {
            using var cmd = _connection.CreateCommand();
            cmd.CommandText = "DELETE FROM history WHERE depot_id=$depot AND manifest_id=$manifest;";
            cmd.Parameters.AddWithValue("$depot", depotId);
            cmd.Parameters.AddWithValue("$manifest", manifestId.ToString());
            cmd.ExecuteNonQuery();
        }
    }

    /// <summary>All dedupe-history entries, newest upload first.</summary>
    public IReadOnlyList<HistoryEntry> ListHistory()
    {
        lock (_gate)
        {
            using var cmd = _connection.CreateCommand();
            cmd.CommandText = "SELECT * FROM history ORDER BY uploaded_at DESC;";
            using var reader = cmd.ExecuteReader();
            var list = new List<HistoryEntry>();
            while (reader.Read())
            {
                list.Add(new HistoryEntry(
                    (uint)reader.GetInt64(reader.GetOrdinal("app_id")),
                    (uint)reader.GetInt64(reader.GetOrdinal("depot_id")),
                    ulong.Parse(reader.GetString(reader.GetOrdinal("manifest_id"))),
                    reader.GetString(reader.GetOrdinal("provider")),
                    reader.GetString(reader.GetOrdinal("url")),
                    reader.IsDBNull(reader.GetOrdinal("password")) ? null : reader.GetString(reader.GetOrdinal("password")),
                    DateTimeOffset.FromUnixTimeSeconds(reader.GetInt64(reader.GetOrdinal("uploaded_at"))),
                    ReadNullableTime(reader, "expires_at"),
                    reader.IsDBNull(reader.GetOrdinal("provider_ref")) ? null : reader.GetString(reader.GetOrdinal("provider_ref")),
                    ReadNullableTime(reader, "verified_at"),
                    ReadBranch(reader),
                    ReadWorkshopId(reader),
                    ReadTitle(reader)));
            }

            return list;
        }
    }

    public void SaveHistory(HistoryEntry entry)
    {
        lock (_gate)
        {
            using var cmd = _connection.CreateCommand();
            cmd.CommandText = """
                INSERT INTO history (depot_id, manifest_id, app_id, provider, url, password, uploaded_at, expires_at, provider_ref, verified_at, branch, workshop_id, title)
                VALUES ($depot, $manifest, $app, $provider, $url, $pw, $uploaded, $expires, $pref, $verified, $branch, $wsid, $title)
                ON CONFLICT(depot_id, manifest_id) DO UPDATE SET
                    provider=$provider, url=$url, password=$pw, uploaded_at=$uploaded, expires_at=$expires, provider_ref=$pref, verified_at=$verified, branch=$branch, workshop_id=$wsid, title=$title;
                """;
            cmd.Parameters.AddWithValue("$depot", entry.DepotId);
            cmd.Parameters.AddWithValue("$manifest", entry.ManifestId.ToString());
            cmd.Parameters.AddWithValue("$app", entry.AppId);
            cmd.Parameters.AddWithValue("$provider", entry.Provider);
            cmd.Parameters.AddWithValue("$url", entry.Url);
            cmd.Parameters.AddWithValue("$pw", (object?)entry.Password ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$uploaded", entry.UploadedAt.ToUnixTimeSeconds());
            cmd.Parameters.AddWithValue("$expires", (object?)entry.ExpiresAt?.ToUnixTimeSeconds() ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$pref", (object?)entry.ProviderRef ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$verified", (entry.VerifiedAt ?? entry.UploadedAt).ToUnixTimeSeconds());
            cmd.Parameters.AddWithValue("$branch", string.IsNullOrWhiteSpace(entry.Branch) ? "public" : entry.Branch);
            cmd.Parameters.AddWithValue("$wsid", entry.WorkshopId.ToString());
            cmd.Parameters.AddWithValue("$title", (object?)entry.Title ?? DBNull.Value);
            cmd.ExecuteNonQuery();
        }
    }

    /// <summary>Replace an account's ownership rows with a fresh scan (atomic).</summary>
    public void ReplaceOwnership(string account, IReadOnlyList<OwnedApp> apps)
    {
        lock (_gate)
        {
            using var tx = _connection.BeginTransaction();
            using (var del = _connection.CreateCommand())
            {
                del.Transaction = tx;
                del.CommandText = "DELETE FROM ownership WHERE account=$account;";
                del.Parameters.AddWithValue("$account", account);
                del.ExecuteNonQuery();
            }

            var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            foreach (var app in apps)
            {
                using var ins = _connection.CreateCommand();
                ins.Transaction = tx;
                ins.CommandText = """
                    INSERT INTO ownership (account, app_id, name, type, scanned_at, is_free, downloadable, supported_os, latest_build_id)
                    VALUES ($account, $app, $name, $type, $now, $free, $dl, $os, $build);
                    """;
                ins.Parameters.AddWithValue("$account", account);
                ins.Parameters.AddWithValue("$app", app.AppId);
                ins.Parameters.AddWithValue("$name", app.Name);
                ins.Parameters.AddWithValue("$type", app.Type);
                ins.Parameters.AddWithValue("$now", now);
                ins.Parameters.AddWithValue("$free", app.IsFree ? 1 : 0);
                ins.Parameters.AddWithValue("$dl", app.Downloadable ? 1 : 0);
                ins.Parameters.AddWithValue("$os", app.SupportedOs);
                ins.Parameters.AddWithValue("$build", app.LatestBuildId);
                ins.ExecuteNonQuery();
            }

            tx.Commit();
        }
    }

    /// <summary>All ownership rows (account, app, name, type), name order.</summary>
    public IReadOnlyList<OwnedAppRow> ListOwnership()
    {
        lock (_gate)
        {
            using var cmd = _connection.CreateCommand();
            cmd.CommandText =
                "SELECT account, app_id, name, type, is_free, downloadable, supported_os, latest_build_id FROM ownership ORDER BY name COLLATE NOCASE;";
            using var reader = cmd.ExecuteReader();
            var list = new List<OwnedAppRow>();
            while (reader.Read())
            {
                list.Add(new OwnedAppRow(
                    reader.GetString(0), (uint)reader.GetInt64(1), reader.GetString(2), reader.GetString(3),
                    reader.GetInt64(4) != 0, reader.GetInt64(5) != 0, reader.GetString(6), (uint)reader.GetInt64(7)));
            }

            return list;
        }
    }

    /// <summary>Distinct app ids present in the ownership catalog (across all accounts).</summary>
    public IReadOnlySet<uint> ListOwnedAppIds()
    {
        lock (_gate)
        {
            using var cmd = _connection.CreateCommand();
            cmd.CommandText = "SELECT DISTINCT app_id FROM ownership;";
            using var reader = cmd.ExecuteReader();
            var set = new HashSet<uint>();
            while (reader.Read())
            {
                set.Add((uint)reader.GetInt64(0));
            }

            return set;
        }
    }

    /// <summary>
    /// Update the cached latest public-branch build id for an app (all owning accounts'
    /// rows). Driven by PICS change notifications so the browse "update available" signal
    /// tracks new builds without a full rescan.
    /// </summary>
    public void UpdateLatestBuildId(uint appId, uint buildId)
    {
        lock (_gate)
        {
            using var cmd = _connection.CreateCommand();
            cmd.CommandText = "UPDATE ownership SET latest_build_id=$build WHERE app_id=$app;";
            cmd.Parameters.AddWithValue("$build", buildId);
            cmd.Parameters.AddWithValue("$app", appId);
            cmd.ExecuteNonQuery();
        }
    }

    /// <summary>App ids the admin has hidden from the public (family) browse page.</summary>
    public IReadOnlySet<uint> ListHiddenApps()
    {
        lock (_gate)
        {
            using var cmd = _connection.CreateCommand();
            cmd.CommandText = "SELECT app_id FROM hidden_apps;";
            using var reader = cmd.ExecuteReader();
            var set = new HashSet<uint>();
            while (reader.Read())
            {
                set.Add((uint)reader.GetInt64(0));
            }

            return set;
        }
    }

    /// <summary>Hide or unhide an app from the public browse page (admin curation).</summary>
    public void SetAppHidden(uint appId, bool hidden)
    {
        lock (_gate)
        {
            using var cmd = _connection.CreateCommand();
            if (hidden)
            {
                cmd.CommandText = """
                    INSERT INTO hidden_apps (app_id, hidden_at) VALUES ($app, $now)
                    ON CONFLICT(app_id) DO NOTHING;
                    """;
                cmd.Parameters.AddWithValue("$app", appId);
                cmd.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToUnixTimeSeconds());
            }
            else
            {
                cmd.CommandText = "DELETE FROM hidden_apps WHERE app_id=$app;";
                cmd.Parameters.AddWithValue("$app", appId);
            }

            cmd.ExecuteNonQuery();
        }
    }

    /// <summary>Accounts that own a given app.</summary>
    public IReadOnlyList<string> OwningAccounts(uint appId)
    {
        lock (_gate)
        {
            using var cmd = _connection.CreateCommand();
            cmd.CommandText = "SELECT account FROM ownership WHERE app_id=$app;";
            cmd.Parameters.AddWithValue("$app", appId);
            using var reader = cmd.ExecuteReader();
            var list = new List<string>();
            while (reader.Read())
            {
                list.Add(reader.GetString(0));
            }

            return list;
        }
    }

    /// <summary>
    /// True when the ownership catalog holds at least one row, i.e. some account has
    /// been scanned. Distinguishes "the catalog is populated and this app simply has no
    /// owner" from "nothing has been scanned yet" - only the former is safe to fail fast
    /// on (an empty catalog would false-negative a legitimately-owned app whose scan lagged).
    /// </summary>
    public bool HasAnyOwnership()
    {
        lock (_gate)
        {
            using var cmd = _connection.CreateCommand();
            cmd.CommandText = "SELECT EXISTS(SELECT 1 FROM ownership);";
            return Convert.ToInt64(cmd.ExecuteScalar()) != 0;
        }
    }

    /// <summary>
    /// Recover jobs left mid-run by a crash or redeploy. A job caught mid-pipeline
    /// (Resolving/Downloading/Archiving/Uploading) is put back to Queued so the worker
    /// re-runs it from the start - downloads are not checkpointed, and dedupe/history
    /// make the re-run cheap if it had in fact finished. A bounded attempt counter
    /// guards against a poison job that kills the process every time: once it has
    /// burned <paramref name="maxAttempts"/> restarts it is marked Failed instead, so
    /// the rest of the queue can make progress. Returns (requeued, abandoned) counts.
    /// </summary>
    public (int Requeued, int Abandoned) RecoverInterrupted(int maxAttempts)
    {
        // Statuses that mean "this job was actively running when the process died".
        void BindActive(SqliteCommand cmd)
        {
            cmd.Parameters.AddWithValue("$resolving", (int)JobStatus.Resolving);
            cmd.Parameters.AddWithValue("$downloading", (int)JobStatus.Downloading);
            cmd.Parameters.AddWithValue("$archiving", (int)JobStatus.Archiving);
            cmd.Parameters.AddWithValue("$uploading", (int)JobStatus.Uploading);
        }

        lock (_gate)
        {
            using var tx = _connection.BeginTransaction();

            // Abandon jobs that have already used up their restart budget FIRST, so the
            // re-queue pass below no longer sees them (they are Failed, not active).
            int abandoned;
            using (var fail = _connection.CreateCommand())
            {
                fail.Transaction = tx;
                fail.CommandText = """
                    UPDATE jobs SET status=$failed, message='Interrupted',
                        error='Abandoned after repeated restart interruptions', completed_at=$now
                    WHERE status IN ($resolving, $downloading, $archiving, $uploading)
                        AND resume_attempts >= $max;
                    """;
                fail.Parameters.AddWithValue("$failed", (int)JobStatus.Failed);
                fail.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToUnixTimeSeconds());
                fail.Parameters.AddWithValue("$max", Math.Max(1, maxAttempts));
                BindActive(fail);
                abandoned = fail.ExecuteNonQuery();
            }

            int requeued;
            using (var requeue = _connection.CreateCommand())
            {
                requeue.Transaction = tx;
                requeue.CommandText = """
                    UPDATE jobs SET status=$queued, resume_attempts=resume_attempts+1,
                        progress=0, message='Re-queued after restart', error=NULL, completed_at=NULL
                    WHERE status IN ($resolving, $downloading, $archiving, $uploading);
                    """;
                requeue.Parameters.AddWithValue("$queued", (int)JobStatus.Queued);
                BindActive(requeue);
                requeued = requeue.ExecuteNonQuery();
            }

            tx.Commit();
            return (requeued, abandoned);
        }
    }

    /// <summary>
    /// Staging keys whose on-disk staging/archive must be kept: those owned by a job that
    /// will still run (queued or mid-pipeline), plus recently-failed jobs whose work is
    /// retained so a Retry re-uploads the cached archive (or resumes the download) instead
    /// of starting over. A failed job counts as retained while its completion time is at or
    /// after <paramref name="failedRetainedSince"/>; pass DateTimeOffset.MaxValue to retain
    /// none (old behavior: a failed job's work is reclaimed on the next restart).
    /// </summary>
    public IReadOnlySet<string> RetainedStagingKeys(DateTimeOffset failedRetainedSince)
    {
        lock (_gate)
        {
            using var cmd = _connection.CreateCommand();
            cmd.CommandText = """
                SELECT staging_key FROM jobs
                WHERE staging_key IS NOT NULL
                    AND (status IN ($queued, $resolving, $downloading, $archiving, $uploading)
                         OR (status = $failed AND completed_at IS NOT NULL AND completed_at >= $failedsince));
                """;
            cmd.Parameters.AddWithValue("$queued", (int)JobStatus.Queued);
            cmd.Parameters.AddWithValue("$resolving", (int)JobStatus.Resolving);
            cmd.Parameters.AddWithValue("$downloading", (int)JobStatus.Downloading);
            cmd.Parameters.AddWithValue("$archiving", (int)JobStatus.Archiving);
            cmd.Parameters.AddWithValue("$uploading", (int)JobStatus.Uploading);
            cmd.Parameters.AddWithValue("$failed", (int)JobStatus.Failed);
            cmd.Parameters.AddWithValue("$failedsince", failedRetainedSince.ToUnixTimeSeconds());
            using var reader = cmd.ExecuteReader();
            var set = new HashSet<string>(StringComparer.Ordinal);
            while (reader.Read())
            {
                set.Add(reader.GetString(0));
            }

            return set;
        }
    }

    /// <summary>
    /// Total download bytes charged to a client IP since <paramref name="since"/> (the
    /// rolling per-IP resource budget). 0 for an IP with no charges in the window.
    /// </summary>
    public long BytesInWindow(string ip, DateTimeOffset since)
    {
        lock (_gate)
        {
            using var cmd = _connection.CreateCommand();
            cmd.CommandText = "SELECT COALESCE(SUM(bytes), 0) FROM ip_charges WHERE ip=$ip AND charged_at >= $since;";
            cmd.Parameters.AddWithValue("$ip", ip);
            cmd.Parameters.AddWithValue("$since", since.ToUnixTimeSeconds());
            return Convert.ToInt64(cmd.ExecuteScalar());
        }
    }

    /// <summary>
    /// True if this IP has already been charged for the given staging key within the
    /// window, so a retry or shutdown-resume of the SAME content is not billed twice
    /// against the rolling budget. A null/empty key never matches (always chargeable).
    /// </summary>
    public bool AlreadyChargedKey(string ip, string? stagingKey, DateTimeOffset since)
    {
        if (string.IsNullOrWhiteSpace(stagingKey))
        {
            return false;
        }

        lock (_gate)
        {
            using var cmd = _connection.CreateCommand();
            cmd.CommandText =
                "SELECT 1 FROM ip_charges WHERE ip=$ip AND staging_key=$key AND charged_at >= $since LIMIT 1;";
            cmd.Parameters.AddWithValue("$ip", ip);
            cmd.Parameters.AddWithValue("$key", stagingKey);
            cmd.Parameters.AddWithValue("$since", since.ToUnixTimeSeconds());
            return cmd.ExecuteScalar() is not null;
        }
    }

    /// <summary>
    /// Record <paramref name="bytes"/> of downloaded content against a client IP for the
    /// rolling budget, tagged with the <paramref name="stagingKey"/> it paid for, and
    /// opportunistically prune charges older than 48h so the ledger stays small (the widest
    /// window we query is 24h).
    /// </summary>
    public void ChargeIp(string ip, long bytes, DateTimeOffset at, string? stagingKey = null)
    {
        if (bytes <= 0 || string.IsNullOrWhiteSpace(ip))
        {
            return;
        }

        lock (_gate)
        {
            using var ins = _connection.CreateCommand();
            ins.CommandText =
                "INSERT INTO ip_charges (ip, bytes, charged_at, staging_key) VALUES ($ip, $bytes, $at, $key);";
            ins.Parameters.AddWithValue("$ip", ip);
            ins.Parameters.AddWithValue("$bytes", bytes);
            ins.Parameters.AddWithValue("$at", at.ToUnixTimeSeconds());
            ins.Parameters.AddWithValue("$key", (object?)stagingKey ?? DBNull.Value);
            ins.ExecuteNonQuery();

            using var prune = _connection.CreateCommand();
            prune.CommandText = "DELETE FROM ip_charges WHERE charged_at < $cutoff;";
            prune.Parameters.AddWithValue("$cutoff", at.AddHours(-48).ToUnixTimeSeconds());
            prune.ExecuteNonQuery();
        }
    }

    private static void Bind(SqliteCommand cmd, Job job)
    {
        cmd.Parameters.AddWithValue("$id", job.Id);
        cmd.Parameters.AddWithValue("$kind", (int)job.Kind);
        cmd.Parameters.AddWithValue("$app", job.AppId);
        cmd.Parameters.AddWithValue("$wsid", job.WorkshopId.ToString());
        cmd.Parameters.AddWithValue("$name", job.DisplayName);
        cmd.Parameters.AddWithValue("$status", (int)job.Status);
        cmd.Parameters.AddWithValue("$progress", job.Progress);
        cmd.Parameters.AddWithValue("$message", job.Message);
        cmd.Parameters.AddWithValue("$error", (object?)job.Error ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$url", (object?)job.ResultUrl ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$pw", (object?)job.ResultPassword ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$manifest", job.ManifestId.ToString());
        cmd.Parameters.AddWithValue("$depot", job.DepotId);
        cmd.Parameters.AddWithValue("$reused", job.Reused ? 1 : 0);
        cmd.Parameters.AddWithValue("$reqpw", (object?)job.Password ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$created", job.CreatedAt.ToUnixTimeSeconds());
        cmd.Parameters.AddWithValue("$completed", (object?)job.CompletedAt?.ToUnixTimeSeconds() ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$expires", (object?)job.ExpiresAt?.ToUnixTimeSeconds() ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$pref", (object?)job.ProviderRef ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$exph", (object?)job.ExpiryHours ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$branch", (object?)job.Branch ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$target_os", (object?)job.TargetOs ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$stagingkey", (object?)job.StagingKey ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$sip", (object?)job.SubmitterIp ?? DBNull.Value);
    }

    private static Job ReadJob(SqliteDataReader r) => new()
    {
        Id = r.GetString(r.GetOrdinal("id")),
        Kind = (ContentKind)r.GetInt32(r.GetOrdinal("kind")),
        AppId = (uint)r.GetInt64(r.GetOrdinal("app_id")),
        WorkshopId = ulong.Parse(r.GetString(r.GetOrdinal("workshop_id"))),
        DisplayName = r.GetString(r.GetOrdinal("display_name")),
        Status = (JobStatus)r.GetInt32(r.GetOrdinal("status")),
        Progress = r.GetInt32(r.GetOrdinal("progress")),
        Message = r.GetString(r.GetOrdinal("message")),
        Error = r.IsDBNull(r.GetOrdinal("error")) ? null : r.GetString(r.GetOrdinal("error")),
        ResultUrl = r.IsDBNull(r.GetOrdinal("result_url")) ? null : r.GetString(r.GetOrdinal("result_url")),
        ResultPassword = r.IsDBNull(r.GetOrdinal("result_password")) ? null : r.GetString(r.GetOrdinal("result_password")),
        ManifestId = ulong.Parse(r.GetString(r.GetOrdinal("manifest_id"))),
        DepotId = (uint)r.GetInt64(r.GetOrdinal("depot_id")),
        Reused = r.GetInt32(r.GetOrdinal("reused")) != 0,
        Password = r.IsDBNull(r.GetOrdinal("password")) ? null : r.GetString(r.GetOrdinal("password")),
        CreatedAt = DateTimeOffset.FromUnixTimeSeconds(r.GetInt64(r.GetOrdinal("created_at"))),
        CompletedAt = ReadNullableTime(r, "completed_at"),
        ExpiresAt = ReadNullableTime(r, "expires_at"),
        ProviderRef = r.IsDBNull(r.GetOrdinal("provider_ref")) ? null : r.GetString(r.GetOrdinal("provider_ref")),
        ExpiryHours = r.IsDBNull(r.GetOrdinal("expiry_hours")) ? null : r.GetInt32(r.GetOrdinal("expiry_hours")),
        Branch = r.IsDBNull(r.GetOrdinal("branch")) ? null : r.GetString(r.GetOrdinal("branch")),
        TargetOs = r.IsDBNull(r.GetOrdinal("target_os")) ? null : r.GetString(r.GetOrdinal("target_os")),
        StagingKey = r.IsDBNull(r.GetOrdinal("staging_key")) ? null : r.GetString(r.GetOrdinal("staging_key")),
        SubmitterIp = r.IsDBNull(r.GetOrdinal("submitter_ip")) ? null : r.GetString(r.GetOrdinal("submitter_ip")),
    };

    private static DateTimeOffset? ReadNullableTime(SqliteDataReader r, string column)
    {
        var ordinal = r.GetOrdinal(column);
        return r.IsDBNull(ordinal) ? null : DateTimeOffset.FromUnixTimeSeconds(r.GetInt64(ordinal));
    }

    public void Dispose() => _connection.Dispose();
}
