using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using SwiftGet.Core.Models;

namespace SwiftGet.Core.Storage;

public sealed class SqliteDatabase : ISwiftGetDatabase
{
    private readonly string _connectionString;
    private readonly ILogger<SqliteDatabase> _logger;
    private SqliteConnection? _connection;
    private readonly SemaphoreSlim _lock = new(1, 1);

    public SqliteDatabase(string dbPath, ILogger<SqliteDatabase>? logger = null)
    {
        var builder = new SqliteConnectionStringBuilder
        {
            DataSource = dbPath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Shared,
            DefaultTimeout = 5
        };
        _connectionString = builder.ToString();
        _logger = logger ?? NullLogger<SqliteDatabase>.Instance;
    }

    public async Task InitializeAsync(CancellationToken ct = default)
    {
        await _lock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_connection == null)
            {
                _connection = new SqliteConnection(_connectionString);
                await _connection.OpenAsync(ct).ConfigureAwait(false);

                // Configure SQLite WAL mode & performance pragmas
                using var pragmaCmd = _connection.CreateCommand();
                pragmaCmd.CommandText = @"
                    PRAGMA journal_mode = WAL;
                    PRAGMA synchronous = NORMAL;
                    PRAGMA foreign_keys = ON;
                    PRAGMA busy_timeout = 5000;";
                await pragmaCmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);

                await ApplyMigrationsAsync(_connection, ct).ConfigureAwait(false);
            }
        }
        finally
        {
            _lock.Release();
        }
    }

    private static async Task ApplyMigrationsAsync(SqliteConnection connection, CancellationToken ct)
    {
        using var createMigrationTableCmd = connection.CreateCommand();
        createMigrationTableCmd.CommandText = @"
            CREATE TABLE IF NOT EXISTS schema_migrations (
                version INTEGER PRIMARY KEY,
                applied_at_utc TEXT NOT NULL,
                description TEXT NOT NULL
            );";
        await createMigrationTableCmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);

        using var checkCmd = connection.CreateCommand();
        checkCmd.CommandText = "SELECT MAX(version) FROM schema_migrations;";
        var maxVersionObj = await checkCmd.ExecuteScalarAsync(ct).ConfigureAwait(false);
        var currentVersion = maxVersionObj is long v ? (int)v : 0;

        using (var preTx = connection.BeginTransaction())
        {
            await EnsureDownloadColumnsAsync(connection, preTx, ct).ConfigureAwait(false);
            await preTx.CommitAsync(ct).ConfigureAwait(false);
        }

        if (currentVersion < 1)
        {
            using var tx = connection.BeginTransaction();
            using var v1Cmd = connection.CreateCommand();
            v1Cmd.Transaction = tx;
            v1Cmd.CommandText = @"
                CREATE TABLE IF NOT EXISTS categories (
                    id TEXT PRIMARY KEY,
                    name TEXT NOT NULL UNIQUE,
                    target_directory TEXT NOT NULL,
                    file_extensions TEXT NOT NULL,
                    icon_key TEXT NOT NULL,
                    is_default INTEGER NOT NULL DEFAULT 0
                );

                CREATE TABLE IF NOT EXISTS queues (
                    id TEXT PRIMARY KEY,
                    name TEXT NOT NULL UNIQUE,
                    max_concurrent_downloads INTEGER NOT NULL DEFAULT 4,
                    speed_limit_bytes_per_sec INTEGER NOT NULL DEFAULT 0,
                    is_active INTEGER NOT NULL DEFAULT 1,
                    schedule_days_mask INTEGER NOT NULL DEFAULT 127,
                    start_time_utc TEXT,
                    stop_time_utc TEXT,
                    after_completion_action TEXT NOT NULL DEFAULT 'None',
                    custom_action_command TEXT
                );

                CREATE TABLE IF NOT EXISTS downloads (
                    id TEXT PRIMARY KEY,
                    url TEXT NOT NULL,
                    final_url TEXT NOT NULL,
                    destination_path TEXT NOT NULL,
                    file_name TEXT NOT NULL,
                    total_bytes INTEGER NOT NULL DEFAULT -1,
                    downloaded_bytes INTEGER NOT NULL DEFAULT 0,
                    status TEXT NOT NULL,
                    etag TEXT,
                    last_modified TEXT,
                    content_type TEXT,
                    supports_ranges INTEGER NOT NULL DEFAULT 1,
                    checksum_expected TEXT,
                    checksum_actual TEXT,
                    checksum_type TEXT,
                    created_at_utc TEXT NOT NULL,
                    completed_at_utc TEXT,
                    error_message TEXT,
                    retry_count INTEGER NOT NULL DEFAULT 0,
                    speed_limit_bytes_per_sec INTEGER NOT NULL DEFAULT 0,
                    queue_id TEXT NOT NULL,
                    category_id TEXT NOT NULL,
                    priority INTEGER NOT NULL DEFAULT 0,
                    referrer TEXT,
                    user_agent TEXT,
                    custom_headers_json TEXT,
                    cookies_json TEXT,
                    is_user_locked INTEGER NOT NULL DEFAULT 0
                );

                CREATE TABLE IF NOT EXISTS segments (
                    id TEXT PRIMARY KEY,
                    download_id TEXT NOT NULL REFERENCES downloads(id) ON DELETE CASCADE,
                    segment_index INTEGER NOT NULL,
                    start_offset INTEGER NOT NULL,
                    end_offset INTEGER NOT NULL,
                    current_offset INTEGER NOT NULL,
                    status TEXT NOT NULL,
                    connection_id INTEGER,
                    updated_at_utc TEXT NOT NULL,
                    checksum TEXT
                );

                CREATE INDEX IF NOT EXISTS idx_downloads_status ON downloads(status);
                CREATE INDEX IF NOT EXISTS idx_downloads_queue_id ON downloads(queue_id);
                CREATE INDEX IF NOT EXISTS idx_segments_download_id ON segments(download_id);
                CREATE INDEX IF NOT EXISTS idx_segments_download_offset ON segments(download_id, start_offset);

                INSERT INTO schema_migrations (version, applied_at_utc, description)
                VALUES (1, datetime('now'), 'Initial schema setup');";
            await v1Cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
            await tx.CommitAsync(ct).ConfigureAwait(false);
        }

        if (currentVersion < 2)
        {
            using var tx = connection.BeginTransaction();
            using var v2Cmd = connection.CreateCommand();
            v2Cmd.Transaction = tx;
            v2Cmd.CommandText = @"
                INSERT OR IGNORE INTO categories (id, name, target_directory, file_extensions, icon_key, is_default)
                VALUES 
                    ('compressed', 'Compressed', '%USERPROFILE%\\Downloads\\Compressed', 'zip,rar,7z,tar,gz,iso,bz2,xz,tgz', 'Archive', 0),
                    ('documents', 'Documents', '%USERPROFILE%\\Downloads\\Documents', 'doc,docx,pdf,txt,rtf,xls,xlsx,ppt,pptx,csv,epub', 'Document', 0),
                    ('music', 'Music', '%USERPROFILE%\\Downloads\\Music', 'mp3,wav,wma,flac,m4a,aac,ogg,alac', 'Audio', 0),
                    ('programs', 'Programs', '%USERPROFILE%\\Downloads\\Programs', 'exe,msi,bat,cmd,ps1,apk', 'Application', 0),
                    ('video', 'Video', '%USERPROFILE%\\Downloads\\Video', 'mp4,mkv,avi,mov,wmv,flv,webm,m4v,ts', 'Video', 0),
                    ('general', 'General', '%USERPROFILE%\\Downloads', '*', 'Folder', 1);

                INSERT OR IGNORE INTO queues (id, name, max_concurrent_downloads, speed_limit_bytes_per_sec, is_active, schedule_days_mask, after_completion_action)
                VALUES 
                    ('default', 'Main Download Queue', 4, 0, 1, 127, 'None'),
                    ('sync', 'Synchronization Queue', 1, 0, 1, 127, 'None');

                INSERT INTO schema_migrations (version, applied_at_utc, description)
                VALUES (2, datetime('now'), 'Seed default categories and queues');";
            await v2Cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
            await tx.CommitAsync(ct).ConfigureAwait(false);
        }

        if (currentVersion < 3)
        {
            using var tx = connection.BeginTransaction();

            using var checkColsCmd = connection.CreateCommand();
            checkColsCmd.Transaction = tx;
            checkColsCmd.CommandText = "PRAGMA table_info(downloads);";
            var dlCols = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            using (var r = await checkColsCmd.ExecuteReaderAsync(ct).ConfigureAwait(false))
            {
                while (await r.ReadAsync(ct).ConfigureAwait(false))
                {
                    dlCols.Add(r.GetString(1));
                }
            }

            if (!dlCols.Contains("job_kind"))
            {
                using var alter = connection.CreateCommand();
                alter.Transaction = tx;
                alter.CommandText = "ALTER TABLE downloads ADD COLUMN job_kind TEXT NOT NULL DEFAULT 'Http';";
                await alter.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
            }

            if (!dlCols.Contains("info_hash"))
            {
                using var alter = connection.CreateCommand();
                alter.Transaction = tx;
                alter.CommandText = "ALTER TABLE downloads ADD COLUMN info_hash TEXT;";
                await alter.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
            }

            using var v3Cmd = connection.CreateCommand();
            v3Cmd.Transaction = tx;
            v3Cmd.CommandText = @"
                -- 2. Torrents Master Table
                CREATE TABLE IF NOT EXISTS torrents (
                    id TEXT PRIMARY KEY REFERENCES downloads(id) ON DELETE CASCADE,
                    info_hash TEXT NOT NULL UNIQUE,
                    magnet_uri TEXT,
                    name TEXT NOT NULL,
                    total_bytes INTEGER NOT NULL DEFAULT 0,
                    piece_length INTEGER NOT NULL DEFAULT 0,
                    piece_count INTEGER NOT NULL DEFAULT 0,
                    is_private INTEGER NOT NULL DEFAULT 0,
                    seed_ratio_limit REAL NOT NULL DEFAULT 1.0,
                    seed_time_limit_mins INTEGER NOT NULL DEFAULT 0,
                    uploaded_bytes INTEGER NOT NULL DEFAULT 0,
                    downloaded_bytes INTEGER NOT NULL DEFAULT 0,
                    fast_resume_blob BLOB,
                    comment TEXT,
                    created_by TEXT,
                    created_at_utc TEXT NOT NULL,
                    completed_at_utc TEXT
                );

                -- 3. Torrent Files Table (Multi-file selective downloading)
                CREATE TABLE IF NOT EXISTS torrent_files (
                    id TEXT PRIMARY KEY,
                    torrent_id TEXT NOT NULL REFERENCES torrents(id) ON DELETE CASCADE,
                    file_index INTEGER NOT NULL,
                    relative_path TEXT NOT NULL,
                    file_size_bytes INTEGER NOT NULL,
                    downloaded_bytes INTEGER NOT NULL DEFAULT 0,
                    priority INTEGER NOT NULL DEFAULT 3,
                    is_selected INTEGER NOT NULL DEFAULT 1
                );

                -- 4. Torrent Trackers Table
                CREATE TABLE IF NOT EXISTS torrent_trackers (
                    id TEXT PRIMARY KEY,
                    torrent_id TEXT NOT NULL REFERENCES torrents(id) ON DELETE CASCADE,
                    tier INTEGER NOT NULL DEFAULT 0,
                    announce_url TEXT NOT NULL,
                    status TEXT NOT NULL DEFAULT 'NotContacted',
                    seeders INTEGER NOT NULL DEFAULT 0,
                    leechers INTEGER NOT NULL DEFAULT 0,
                    last_announce_utc TEXT,
                    error_message TEXT
                );

                CREATE INDEX IF NOT EXISTS idx_torrents_info_hash ON torrents(info_hash);
                CREATE INDEX IF NOT EXISTS idx_torrent_files_torrent ON torrent_files(torrent_id);
                CREATE INDEX IF NOT EXISTS idx_torrent_trackers_torrent ON torrent_trackers(torrent_id);

                INSERT INTO schema_migrations (version, applied_at_utc, description)
                VALUES (3, datetime('now'), 'Add BitTorrent storage tables, indices, and downloads job_kind column');";
            await v3Cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
            await tx.CommitAsync(ct).ConfigureAwait(false);
        }

        using (var tx = connection.BeginTransaction())
        {
            await EnsureDownloadColumnsAsync(connection, tx, ct).ConfigureAwait(false);
            await tx.CommitAsync(ct).ConfigureAwait(false);
        }
    }

    private static async Task EnsureDownloadColumnsAsync(SqliteConnection connection, SqliteTransaction tx, CancellationToken ct)
    {
        // 1. Reconcile legacy prototype queues table
        var queueColumns = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        using (var qCmd = connection.CreateCommand())
        {
            qCmd.Transaction = tx;
            qCmd.CommandText = "PRAGMA table_info(queues);";
            using var reader = await qCmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
            while (await reader.ReadAsync(ct).ConfigureAwait(false))
            {
                queueColumns.Add(reader.GetString(1));
            }
        }
        if (queueColumns.Count > 0 && !queueColumns.Contains("id"))
        {
            using var dropCmd = connection.CreateCommand();
            dropCmd.Transaction = tx;
            dropCmd.CommandText = "DROP TABLE queues;";
            await dropCmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }

        // 2. Reconcile legacy prototype categories table
        var catColumns = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        using (var cCmd = connection.CreateCommand())
        {
            cCmd.Transaction = tx;
            cCmd.CommandText = "PRAGMA table_info(categories);";
            using var reader = await cCmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
            while (await reader.ReadAsync(ct).ConfigureAwait(false))
            {
                catColumns.Add(reader.GetString(1));
            }
        }
        if (catColumns.Count > 0 && !catColumns.Contains("id"))
        {
            using var dropCmd = connection.CreateCommand();
            dropCmd.Transaction = tx;
            dropCmd.CommandText = "DROP TABLE categories;";
            await dropCmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }

        // Recreate modern schema for queues and categories if missing
        using (var createCoreCmd = connection.CreateCommand())
        {
            createCoreCmd.Transaction = tx;
            createCoreCmd.CommandText = @"
                CREATE TABLE IF NOT EXISTS queues (
                    id TEXT PRIMARY KEY,
                    name TEXT NOT NULL UNIQUE,
                    max_concurrent_downloads INTEGER NOT NULL DEFAULT 4,
                    speed_limit_bytes_per_sec INTEGER NOT NULL DEFAULT 0,
                    is_active INTEGER NOT NULL DEFAULT 1,
                    schedule_days_mask INTEGER NOT NULL DEFAULT 127,
                    start_time_utc TEXT,
                    stop_time_utc TEXT,
                    after_completion_action TEXT NOT NULL DEFAULT 'None',
                    custom_action_command TEXT
                );

                CREATE TABLE IF NOT EXISTS categories (
                    id TEXT PRIMARY KEY,
                    name TEXT NOT NULL UNIQUE,
                    target_directory TEXT NOT NULL,
                    file_extensions TEXT NOT NULL,
                    icon_key TEXT NOT NULL,
                    is_default INTEGER NOT NULL DEFAULT 0
                );";
            await createCoreCmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }

        // 3. Reconcile downloads table columns
        var existingColumns = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        using var pragmaCmd = connection.CreateCommand();
        pragmaCmd.Transaction = tx;
        pragmaCmd.CommandText = "PRAGMA table_info(downloads);";
        using (var reader = await pragmaCmd.ExecuteReaderAsync(ct).ConfigureAwait(false))
        {
            while (await reader.ReadAsync(ct).ConfigureAwait(false))
            {
                existingColumns.Add(reader.GetString(1));
            }
        }

        if (existingColumns.Count == 0) return;

        // Cleanly reconcile legacy prototype table with conflicting NOT NULL constraints (e.g. speed_bps, supports_resume)
        if (existingColumns.Contains("speed_bps") || existingColumns.Contains("supports_resume"))
        {
            var catCol = existingColumns.Contains("category_id") ? "category_id" : (existingColumns.Contains("category") ? "category" : "'all'");
            var queueCol = existingColumns.Contains("queue_id") ? "queue_id" : (existingColumns.Contains("queue_name") ? "queue_name" : "'default'");
            var fileCol = existingColumns.Contains("file_name") ? "file_name" : (existingColumns.Contains("filename") ? "filename" : "''");
            var dateCol = existingColumns.Contains("created_at_utc") ? "created_at_utc" : (existingColumns.Contains("created_at") ? "created_at" : "datetime('now')");

            using var migCmd = connection.CreateCommand();
            migCmd.Transaction = tx;
            migCmd.CommandText = $@"
                CREATE TABLE downloads_reconciled (
                    id TEXT PRIMARY KEY,
                    url TEXT NOT NULL,
                    final_url TEXT NOT NULL DEFAULT '',
                    destination_path TEXT NOT NULL,
                    file_name TEXT NOT NULL,
                    total_bytes INTEGER NOT NULL DEFAULT -1,
                    downloaded_bytes INTEGER NOT NULL DEFAULT 0,
                    status TEXT NOT NULL,
                    job_kind TEXT NOT NULL DEFAULT 'Http',
                    info_hash TEXT,
                    etag TEXT,
                    last_modified TEXT,
                    content_type TEXT,
                    supports_ranges INTEGER NOT NULL DEFAULT 1,
                    checksum_expected TEXT,
                    checksum_actual TEXT,
                    checksum_type TEXT,
                    created_at_utc TEXT NOT NULL,
                    completed_at_utc TEXT,
                    error_message TEXT,
                    retry_count INTEGER NOT NULL DEFAULT 0,
                    speed_limit_bytes_per_sec INTEGER NOT NULL DEFAULT 0,
                    queue_id TEXT NOT NULL DEFAULT 'default',
                    category_id TEXT NOT NULL DEFAULT 'all',
                    priority INTEGER NOT NULL DEFAULT 0,
                    referrer TEXT,
                    user_agent TEXT,
                    custom_headers_json TEXT,
                    cookies_json TEXT,
                    is_user_locked INTEGER NOT NULL DEFAULT 0
                );

                INSERT INTO downloads_reconciled (id, url, destination_path, file_name, total_bytes, downloaded_bytes, status, created_at_utc, category_id, queue_id)
                SELECT id, url, destination_path, {fileCol}, total_bytes, downloaded_bytes, status, {dateCol}, {catCol}, {queueCol} FROM downloads;

                DROP TABLE downloads;
                ALTER TABLE downloads_reconciled RENAME TO downloads;
                CREATE INDEX IF NOT EXISTS idx_downloads_status ON downloads(status);
                CREATE INDEX IF NOT EXISTS idx_downloads_queue_id ON downloads(queue_id);";
            await migCmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
            return;
        }

        // Rename legacy prototype columns if present
        if (existingColumns.Contains("created_at") && !existingColumns.Contains("created_at_utc"))
        {
            using var alter = connection.CreateCommand();
            alter.Transaction = tx;
            alter.CommandText = "ALTER TABLE downloads RENAME COLUMN created_at TO created_at_utc;";
            await alter.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
            existingColumns.Add("created_at_utc");
        }

        if (existingColumns.Contains("filename") && !existingColumns.Contains("file_name"))
        {
            using var alter = connection.CreateCommand();
            alter.Transaction = tx;
            alter.CommandText = "ALTER TABLE downloads RENAME COLUMN filename TO file_name;";
            await alter.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
            existingColumns.Add("file_name");
        }

        if (existingColumns.Contains("category") && !existingColumns.Contains("category_id"))
        {
            using var alter = connection.CreateCommand();
            alter.Transaction = tx;
            alter.CommandText = "ALTER TABLE downloads RENAME COLUMN category TO category_id;";
            await alter.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
            existingColumns.Add("category_id");
        }

        if (existingColumns.Contains("queue_name") && !existingColumns.Contains("queue_id"))
        {
            using var alter = connection.CreateCommand();
            alter.Transaction = tx;
            alter.CommandText = "ALTER TABLE downloads RENAME COLUMN queue_name TO queue_id;";
            await alter.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
            existingColumns.Add("queue_id");
        }

        // Ensure all required schema columns exist
        var requiredColumns = new Dictionary<string, string>
        {
            ["final_url"] = "TEXT NOT NULL DEFAULT ''",
            ["file_name"] = "TEXT NOT NULL DEFAULT ''",
            ["created_at_utc"] = "TEXT NOT NULL DEFAULT (datetime('now'))",
            ["completed_at_utc"] = "TEXT",
            ["etag"] = "TEXT",
            ["last_modified"] = "TEXT",
            ["content_type"] = "TEXT",
            ["supports_ranges"] = "INTEGER NOT NULL DEFAULT 1",
            ["checksum_expected"] = "TEXT",
            ["checksum_actual"] = "TEXT",
            ["checksum_type"] = "TEXT",
            ["error_message"] = "TEXT",
            ["retry_count"] = "INTEGER NOT NULL DEFAULT 0",
            ["speed_limit_bytes_per_sec"] = "INTEGER NOT NULL DEFAULT 0",
            ["queue_id"] = "TEXT NOT NULL DEFAULT 'default'",
            ["category_id"] = "TEXT NOT NULL DEFAULT 'all'",
            ["priority"] = "INTEGER NOT NULL DEFAULT 0",
            ["referrer"] = "TEXT",
            ["user_agent"] = "TEXT",
            ["custom_headers_json"] = "TEXT",
            ["cookies_json"] = "TEXT",
            ["job_kind"] = "TEXT NOT NULL DEFAULT 'Http'",
            ["info_hash"] = "TEXT",
            ["is_user_locked"] = "INTEGER NOT NULL DEFAULT 0"
        };

        foreach (var (col, colDef) in requiredColumns)
        {
            if (!existingColumns.Contains(col))
            {
                using var alter = connection.CreateCommand();
                alter.Transaction = tx;
                alter.CommandText = $"ALTER TABLE downloads ADD COLUMN {col} {colDef};";
                await alter.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
                existingColumns.Add(col);
            }
        }
    }

    private SqliteConnection GetOpenConnection()
    {
        if (_connection == null || _connection.State != System.Data.ConnectionState.Open)
        {
            throw new InvalidOperationException("SqliteDatabase has not been initialized. Call InitializeAsync first.");
        }
        return _connection;
    }

    public async Task SaveDownloadAsync(DownloadItem item, CancellationToken ct = default)
    {
        await _lock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var conn = GetOpenConnection();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = @"
                INSERT INTO downloads (
                    id, url, final_url, destination_path, file_name, total_bytes, downloaded_bytes,
                    status, job_kind, info_hash, etag, last_modified, content_type, supports_ranges, checksum_expected,
                    checksum_actual, checksum_type, created_at_utc, completed_at_utc, error_message,
                    retry_count, speed_limit_bytes_per_sec, queue_id, category_id, priority, referrer,
                    user_agent, custom_headers_json, cookies_json, is_user_locked
                ) VALUES (
                    $id, $url, $final_url, $destination_path, $file_name, $total_bytes, $downloaded_bytes,
                    $status, $job_kind, $info_hash, $etag, $last_modified, $content_type, $supports_ranges, $checksum_expected,
                    $checksum_actual, $checksum_type, $created_at_utc, $completed_at_utc, $error_message,
                    $retry_count, $speed_limit_bytes_per_sec, $queue_id, $category_id, $priority, $referrer,
                    $user_agent, $custom_headers_json, $cookies_json, $is_user_locked
                ) ON CONFLICT(id) DO UPDATE SET
                    url = excluded.url,
                    final_url = excluded.final_url,
                    destination_path = excluded.destination_path,
                    file_name = excluded.file_name,
                    total_bytes = excluded.total_bytes,
                    downloaded_bytes = excluded.downloaded_bytes,
                    status = excluded.status,
                    job_kind = excluded.job_kind,
                    info_hash = excluded.info_hash,
                    etag = excluded.etag,
                    last_modified = excluded.last_modified,
                    content_type = excluded.content_type,
                    supports_ranges = excluded.supports_ranges,
                    checksum_expected = excluded.checksum_expected,
                    checksum_actual = excluded.checksum_actual,
                    checksum_type = excluded.checksum_type,
                    completed_at_utc = excluded.completed_at_utc,
                    error_message = excluded.error_message,
                    retry_count = excluded.retry_count,
                    speed_limit_bytes_per_sec = excluded.speed_limit_bytes_per_sec,
                    queue_id = excluded.queue_id,
                    category_id = excluded.category_id,
                    priority = excluded.priority,
                    referrer = excluded.referrer,
                    user_agent = excluded.user_agent,
                    custom_headers_json = excluded.custom_headers_json,
                    cookies_json = excluded.cookies_json,
                    is_user_locked = excluded.is_user_locked;";

            cmd.Parameters.AddWithValue("$id", item.Id);
            cmd.Parameters.AddWithValue("$url", item.Url);
            cmd.Parameters.AddWithValue("$final_url", item.FinalUrl);
            cmd.Parameters.AddWithValue("$destination_path", item.DestinationPath);
            cmd.Parameters.AddWithValue("$file_name", item.FileName);
            cmd.Parameters.AddWithValue("$total_bytes", item.TotalBytes);
            cmd.Parameters.AddWithValue("$downloaded_bytes", item.DownloadedBytes);
            cmd.Parameters.AddWithValue("$status", item.Status.ToString());
            cmd.Parameters.AddWithValue("$job_kind", item.JobKind.ToString());
            cmd.Parameters.AddWithValue("$info_hash", (object?)item.InfoHash ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$etag", (object?)item.ETag ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$last_modified", (object?)item.LastModified ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$content_type", (object?)item.ContentType ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$supports_ranges", item.SupportsRanges ? 1 : 0);
            cmd.Parameters.AddWithValue("$checksum_expected", (object?)item.ChecksumExpected ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$checksum_actual", (object?)item.ChecksumActual ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$checksum_type", item.ChecksumType.ToString());
            cmd.Parameters.AddWithValue("$created_at_utc", item.CreatedAtUtc.ToString("o"));
            cmd.Parameters.AddWithValue("$completed_at_utc", (object?)item.CompletedAtUtc?.ToString("o") ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$error_message", (object?)item.ErrorMessage ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$retry_count", item.RetryCount);
            cmd.Parameters.AddWithValue("$speed_limit_bytes_per_sec", item.SpeedLimitBytesPerSec);
            var effectiveQueueId = string.Equals(item.QueueId, "Main", StringComparison.OrdinalIgnoreCase)
                ? "default"
                : (string.IsNullOrWhiteSpace(item.QueueId) ? "default" : item.QueueId);
            cmd.Parameters.AddWithValue("$queue_id", effectiveQueueId);
            cmd.Parameters.AddWithValue("$category_id", item.CategoryId);
            cmd.Parameters.AddWithValue("$priority", item.Priority);
            cmd.Parameters.AddWithValue("$referrer", (object?)item.Referrer ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$user_agent", (object?)item.UserAgent ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$custom_headers_json", (object?)item.CustomHeadersJson ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$cookies_json", (object?)item.CookiesJson ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$is_user_locked", item.IsFileNameUserLocked ? 1 : 0);

            await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task<DownloadItem?> GetDownloadAsync(string id, CancellationToken ct = default)
    {
        await _lock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var conn = GetOpenConnection();
            using var cmd = conn.CreateCommand();
            var hyphenId = Guid.TryParse(id, out var g) ? g.ToString("D") : id;
            var noHyphenId = Guid.TryParse(id, out var g2) ? g2.ToString("N") : id;
            cmd.CommandText = "SELECT * FROM downloads WHERE id = $id OR id = $hyphenId OR id = $noHyphenId LIMIT 1;";
            cmd.Parameters.AddWithValue("$id", id);
            cmd.Parameters.AddWithValue("$hyphenId", hyphenId);
            cmd.Parameters.AddWithValue("$noHyphenId", noHyphenId);

            using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
            if (!await reader.ReadAsync(ct).ConfigureAwait(false))
            {
                return null;
            }

            var item = ReadDownloadRow(reader);
            item.Segments = (await GetSegmentsInternalAsync(conn, item.Id, ct).ConfigureAwait(false)).ToList();
            return item;
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task<IReadOnlyList<DownloadItem>> GetAllDownloadsAsync(CancellationToken ct = default)
    {
        await _lock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var conn = GetOpenConnection();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT * FROM downloads ORDER BY created_at_utc DESC;";

            var list = new List<DownloadItem>();
            using (var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false))
            {
                while (await reader.ReadAsync(ct).ConfigureAwait(false))
                {
                    list.Add(ReadDownloadRow(reader));
                }
            }

            foreach (var item in list)
            {
                item.Segments = (await GetSegmentsInternalAsync(conn, item.Id, ct).ConfigureAwait(false)).ToList();
            }

            return list;
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task UpdateDownloadStatusAsync(string id, DownloadStatus status, string? errorMessage = null, CancellationToken ct = default)
    {
        await _lock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var conn = GetOpenConnection();
            using var cmd = conn.CreateCommand();
            var hyphenId = Guid.TryParse(id, out var g) ? g.ToString("D") : id;
            var noHyphenId = Guid.TryParse(id, out var g2) ? g2.ToString("N") : id;
            cmd.CommandText = @"
                UPDATE downloads
                SET status = $status,
                    error_message = $error_message,
                    completed_at_utc = CASE WHEN $status = 'Completed' THEN datetime('now') ELSE completed_at_utc END
                WHERE id = $id OR id = $hyphenId OR id = $noHyphenId;";
            cmd.Parameters.AddWithValue("$id", id);
            cmd.Parameters.AddWithValue("$hyphenId", hyphenId);
            cmd.Parameters.AddWithValue("$noHyphenId", noHyphenId);
            cmd.Parameters.AddWithValue("$status", status.ToString());
            cmd.Parameters.AddWithValue("$error_message", (object?)errorMessage ?? DBNull.Value);

            await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task SaveSegmentsAsync(string downloadId, IReadOnlyList<DownloadSegment> segments, CancellationToken ct = default)
    {
        await _lock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var conn = GetOpenConnection();
            using var tx = conn.BeginTransaction();

            using var delCmd = conn.CreateCommand();
            delCmd.Transaction = tx;
            delCmd.CommandText = "DELETE FROM segments WHERE download_id = $download_id;";
            delCmd.Parameters.AddWithValue("$download_id", downloadId);
            await delCmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);

            foreach (var seg in segments)
            {
                using var insCmd = conn.CreateCommand();
                insCmd.Transaction = tx;
                insCmd.CommandText = @"
                    INSERT INTO segments (
                        id, download_id, segment_index, start_offset, end_offset, current_offset,
                        status, connection_id, updated_at_utc, checksum
                    ) VALUES (
                        $id, $download_id, $segment_index, $start_offset, $end_offset, $current_offset,
                        $status, $connection_id, $updated_at_utc, $checksum
                    );";
                insCmd.Parameters.AddWithValue("$id", seg.Id);
                insCmd.Parameters.AddWithValue("$download_id", downloadId);
                insCmd.Parameters.AddWithValue("$segment_index", seg.SegmentIndex);
                insCmd.Parameters.AddWithValue("$start_offset", seg.StartOffset);
                insCmd.Parameters.AddWithValue("$end_offset", seg.EndOffset);
                insCmd.Parameters.AddWithValue("$current_offset", seg.CurrentOffset);
                insCmd.Parameters.AddWithValue("$status", seg.Status.ToString());
                insCmd.Parameters.AddWithValue("$connection_id", (object?)seg.ConnectionId ?? DBNull.Value);
                insCmd.Parameters.AddWithValue("$updated_at_utc", seg.UpdatedAtUtc.ToString("o"));
                insCmd.Parameters.AddWithValue("$checksum", (object?)seg.Checksum ?? DBNull.Value);
                await insCmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
            }

            await tx.CommitAsync(ct).ConfigureAwait(false);
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task<IReadOnlyList<DownloadSegment>> GetSegmentsAsync(string downloadId, CancellationToken ct = default)
    {
        await _lock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var conn = GetOpenConnection();
            return await GetSegmentsInternalAsync(conn, downloadId, ct).ConfigureAwait(false);
        }
        finally
        {
            _lock.Release();
        }
    }

    private static async Task<IReadOnlyList<DownloadSegment>> GetSegmentsInternalAsync(SqliteConnection conn, string downloadId, CancellationToken ct)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT * FROM segments WHERE download_id = $download_id ORDER BY segment_index ASC;";
        cmd.Parameters.AddWithValue("$download_id", downloadId);

        var list = new List<DownloadSegment>();
        using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            list.Add(new DownloadSegment
            {
                Id = reader.GetString(reader.GetOrdinal("id")),
                DownloadId = reader.GetString(reader.GetOrdinal("download_id")),
                SegmentIndex = reader.GetInt32(reader.GetOrdinal("segment_index")),
                StartOffset = reader.GetInt64(reader.GetOrdinal("start_offset")),
                EndOffset = reader.GetInt64(reader.GetOrdinal("end_offset")),
                CurrentOffset = reader.GetInt64(reader.GetOrdinal("current_offset")),
                Status = Enum.Parse<SegmentStatus>(reader.GetString(reader.GetOrdinal("status"))),
                ConnectionId = reader.IsDBNull(reader.GetOrdinal("connection_id")) ? null : reader.GetInt32(reader.GetOrdinal("connection_id")),
                UpdatedAtUtc = DateTime.Parse(reader.GetString(reader.GetOrdinal("updated_at_utc")), System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.RoundtripKind),
                Checksum = reader.IsDBNull(reader.GetOrdinal("checksum")) ? null : reader.GetString(reader.GetOrdinal("checksum"))
            });
        }
        return list;
    }

    public async Task CheckpointProgressAsync(string downloadId, long downloadedBytes, IReadOnlyList<DownloadSegment> segments, CancellationToken ct = default)
    {
        await _lock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var conn = GetOpenConnection();
            using var tx = conn.BeginTransaction();

            using var updateDownloadCmd = conn.CreateCommand();
            updateDownloadCmd.Transaction = tx;
            updateDownloadCmd.CommandText = "UPDATE downloads SET downloaded_bytes = $downloaded_bytes WHERE id = $id;";
            updateDownloadCmd.Parameters.AddWithValue("$downloaded_bytes", downloadedBytes);
            updateDownloadCmd.Parameters.AddWithValue("$id", downloadId);
            await updateDownloadCmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);

            foreach (var seg in segments)
            {
                using var updateSegCmd = conn.CreateCommand();
                updateSegCmd.Transaction = tx;
                updateSegCmd.CommandText = @"
                    UPDATE segments
                    SET current_offset = $current_offset,
                        status = $status,
                        updated_at_utc = $updated_at_utc
                    WHERE id = $id;";
                updateSegCmd.Parameters.AddWithValue("$current_offset", seg.CurrentOffset);
                updateSegCmd.Parameters.AddWithValue("$status", seg.Status.ToString());
                updateSegCmd.Parameters.AddWithValue("$updated_at_utc", DateTime.UtcNow.ToString("o"));
                updateSegCmd.Parameters.AddWithValue("$id", seg.Id);
                await updateSegCmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
            }

            await tx.CommitAsync(ct).ConfigureAwait(false);
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task DeleteDownloadAsync(string id, CancellationToken ct = default)
    {
        await _lock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var conn = GetOpenConnection();
            using var cmd = conn.CreateCommand();
            var hyphenId = Guid.TryParse(id, out var g) ? g.ToString("D") : id;
            var noHyphenId = Guid.TryParse(id, out var g2) ? g2.ToString("N") : id;
            cmd.CommandText = "DELETE FROM downloads WHERE id = $id OR id = $hyphenId OR id = $noHyphenId;";
            cmd.Parameters.AddWithValue("$id", id);
            cmd.Parameters.AddWithValue("$hyphenId", hyphenId);
            cmd.Parameters.AddWithValue("$noHyphenId", noHyphenId);
            await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }
        finally
        {
            _lock.Release();
        }
    }

    private static int GetOrdinalSafe(SqliteDataReader reader, string columnName)
    {
        for (int i = 0; i < reader.FieldCount; i++)
        {
            if (string.Equals(reader.GetName(i), columnName, StringComparison.OrdinalIgnoreCase))
            {
                return i;
            }
        }
        return -1;
    }

    private static DownloadItem ReadDownloadRow(SqliteDataReader reader)
    {
        var jobKindOrd = GetOrdinalSafe(reader, "job_kind");
        var infoHashOrd = GetOrdinalSafe(reader, "info_hash");
        var isLockedOrd = GetOrdinalSafe(reader, "is_user_locked");

        return new DownloadItem
        {
            Id = reader.GetString(reader.GetOrdinal("id")),
            Url = reader.GetString(reader.GetOrdinal("url")),
            FinalUrl = reader.GetString(reader.GetOrdinal("final_url")),
            DestinationPath = reader.GetString(reader.GetOrdinal("destination_path")),
            FileName = reader.GetString(reader.GetOrdinal("file_name")),
            IsFileNameUserLocked = isLockedOrd >= 0 && !reader.IsDBNull(isLockedOrd) && reader.GetInt32(isLockedOrd) == 1,
            TotalBytes = reader.GetInt64(reader.GetOrdinal("total_bytes")),
            DownloadedBytes = reader.GetInt64(reader.GetOrdinal("downloaded_bytes")),
            Status = Enum.Parse<DownloadStatus>(reader.GetString(reader.GetOrdinal("status"))),
            JobKind = jobKindOrd >= 0 && !reader.IsDBNull(jobKindOrd) ? Enum.Parse<JobKind>(reader.GetString(jobKindOrd)) : JobKind.Http,
            InfoHash = infoHashOrd >= 0 && !reader.IsDBNull(infoHashOrd) ? reader.GetString(infoHashOrd) : null,
            ETag = reader.IsDBNull(reader.GetOrdinal("etag")) ? null : reader.GetString(reader.GetOrdinal("etag")),
            LastModified = reader.IsDBNull(reader.GetOrdinal("last_modified")) ? null : reader.GetString(reader.GetOrdinal("last_modified")),
            ContentType = reader.IsDBNull(reader.GetOrdinal("content_type")) ? null : reader.GetString(reader.GetOrdinal("content_type")),
            SupportsRanges = reader.GetInt32(reader.GetOrdinal("supports_ranges")) == 1,
            ChecksumExpected = reader.IsDBNull(reader.GetOrdinal("checksum_expected")) ? null : reader.GetString(reader.GetOrdinal("checksum_expected")),
            ChecksumActual = reader.IsDBNull(reader.GetOrdinal("checksum_actual")) ? null : reader.GetString(reader.GetOrdinal("checksum_actual")),
            ChecksumType = Enum.Parse<ChecksumType>(reader.GetString(reader.GetOrdinal("checksum_type"))),
            CreatedAtUtc = DateTime.Parse(reader.GetString(reader.GetOrdinal("created_at_utc")), System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.RoundtripKind),
            CompletedAtUtc = reader.IsDBNull(reader.GetOrdinal("completed_at_utc")) ? null : DateTime.Parse(reader.GetString(reader.GetOrdinal("completed_at_utc")), System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.RoundtripKind),
            ErrorMessage = reader.IsDBNull(reader.GetOrdinal("error_message")) ? null : reader.GetString(reader.GetOrdinal("error_message")),
            RetryCount = reader.GetInt32(reader.GetOrdinal("retry_count")),
            SpeedLimitBytesPerSec = reader.GetInt64(reader.GetOrdinal("speed_limit_bytes_per_sec")),
            QueueId = reader.GetString(reader.GetOrdinal("queue_id")),
            CategoryId = reader.GetString(reader.GetOrdinal("category_id")),
            Priority = reader.GetInt32(reader.GetOrdinal("priority")),
            Referrer = reader.IsDBNull(reader.GetOrdinal("referrer")) ? null : reader.GetString(reader.GetOrdinal("referrer")),
            UserAgent = reader.IsDBNull(reader.GetOrdinal("user_agent")) ? null : reader.GetString(reader.GetOrdinal("user_agent")),
            CustomHeadersJson = reader.IsDBNull(reader.GetOrdinal("custom_headers_json")) ? null : reader.GetString(reader.GetOrdinal("custom_headers_json")),
            CookiesJson = reader.IsDBNull(reader.GetOrdinal("cookies_json")) ? null : reader.GetString(reader.GetOrdinal("cookies_json"))
        };
    }

    public async Task<IReadOnlyList<DownloadItem>> GetDownloadsByQueueAsync(string queueId, CancellationToken ct = default)
    {
        await _lock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var conn = GetOpenConnection();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT * FROM downloads WHERE queue_id = $queue_id ORDER BY priority ASC, created_at_utc ASC;";
            cmd.Parameters.AddWithValue("$queue_id", queueId);

            var list = new List<DownloadItem>();
            using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
            while (await reader.ReadAsync(ct).ConfigureAwait(false))
            {
                list.Add(ReadDownloadRow(reader));
            }
            return list;
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task UpdateDownloadPriorityAsync(string id, int priority, CancellationToken ct = default)
    {
        await _lock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var conn = GetOpenConnection();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "UPDATE downloads SET priority = $priority WHERE id = $id;";
            cmd.Parameters.AddWithValue("$priority", priority);
            cmd.Parameters.AddWithValue("$id", id);
            await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task SaveQueueAsync(DownloadQueue queue, CancellationToken ct = default)
    {
        await _lock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var conn = GetOpenConnection();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = @"
                INSERT INTO queues (
                    id, name, max_concurrent_downloads, speed_limit_bytes_per_sec,
                    is_active, schedule_days_mask, start_time_utc, stop_time_utc,
                    after_completion_action, custom_action_command
                ) VALUES (
                    $id, $name, $max_concurrent, $speed_limit,
                    $is_active, $schedule_mask, $start_time, $stop_time,
                    $after_action, $custom_cmd
                ) ON CONFLICT(id) DO UPDATE SET
                    name = excluded.name,
                    max_concurrent_downloads = excluded.max_concurrent_downloads,
                    speed_limit_bytes_per_sec = excluded.speed_limit_bytes_per_sec,
                    is_active = excluded.is_active,
                    schedule_days_mask = excluded.schedule_days_mask,
                    start_time_utc = excluded.start_time_utc,
                    stop_time_utc = excluded.stop_time_utc,
                    after_completion_action = excluded.after_completion_action,
                    custom_action_command = excluded.custom_action_command;";

            cmd.Parameters.AddWithValue("$id", queue.Id);
            cmd.Parameters.AddWithValue("$name", queue.Name);
            cmd.Parameters.AddWithValue("$max_concurrent", queue.MaxConcurrentDownloads);
            cmd.Parameters.AddWithValue("$speed_limit", queue.SpeedLimitBytesPerSec);
            cmd.Parameters.AddWithValue("$is_active", queue.IsActive ? 1 : 0);
            cmd.Parameters.AddWithValue("$schedule_mask", queue.ScheduleDaysMask);
            cmd.Parameters.AddWithValue("$start_time", (object?)queue.StartTimeUtc?.ToString("c") ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$stop_time", (object?)queue.StopTimeUtc?.ToString("c") ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$after_action", queue.AfterCompletionAction.ToString());
            cmd.Parameters.AddWithValue("$custom_cmd", (object?)queue.CustomActionCommand ?? DBNull.Value);

            await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task<DownloadQueue?> GetQueueAsync(string id, CancellationToken ct = default)
    {
        await _lock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var conn = GetOpenConnection();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT * FROM queues WHERE id = $id LIMIT 1;";
            cmd.Parameters.AddWithValue("$id", id);

            using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
            if (await reader.ReadAsync(ct).ConfigureAwait(false))
            {
                return ReadQueueRow(reader);
            }
            return null;
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task<IReadOnlyList<DownloadQueue>> GetAllQueuesAsync(CancellationToken ct = default)
    {
        await _lock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var conn = GetOpenConnection();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT * FROM queues ORDER BY name ASC;";

            var list = new List<DownloadQueue>();
            using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
            while (await reader.ReadAsync(ct).ConfigureAwait(false))
            {
                list.Add(ReadQueueRow(reader));
            }
            return list;
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task DeleteQueueAsync(string id, CancellationToken ct = default)
    {
        await _lock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var conn = GetOpenConnection();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "DELETE FROM queues WHERE id = $id;";
            cmd.Parameters.AddWithValue("$id", id);
            await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task SaveCategoryAsync(CategoryRule category, CancellationToken ct = default)
    {
        await _lock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var conn = GetOpenConnection();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = @"
                INSERT INTO categories (
                    id, name, target_directory, file_extensions, icon_key, is_default
                ) VALUES (
                    $id, $name, $target_dir, $file_exts, $icon_key, $is_default
                ) ON CONFLICT(id) DO UPDATE SET
                    name = excluded.name,
                    target_directory = excluded.target_directory,
                    file_extensions = excluded.file_extensions,
                    icon_key = excluded.icon_key,
                    is_default = excluded.is_default;";

            cmd.Parameters.AddWithValue("$id", category.Id);
            cmd.Parameters.AddWithValue("$name", category.Name);
            cmd.Parameters.AddWithValue("$target_dir", category.TargetDirectory);
            cmd.Parameters.AddWithValue("$file_exts", category.FileExtensions);
            cmd.Parameters.AddWithValue("$icon_key", category.IconKey);
            cmd.Parameters.AddWithValue("$is_default", category.IsDefault ? 1 : 0);

            await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task<CategoryRule?> GetCategoryAsync(string id, CancellationToken ct = default)
    {
        await _lock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var conn = GetOpenConnection();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT * FROM categories WHERE id = $id LIMIT 1;";
            cmd.Parameters.AddWithValue("$id", id);

            using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
            if (await reader.ReadAsync(ct).ConfigureAwait(false))
            {
                return ReadCategoryRow(reader);
            }
            return null;
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task<IReadOnlyList<CategoryRule>> GetAllCategoriesAsync(CancellationToken ct = default)
    {
        await _lock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var conn = GetOpenConnection();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT * FROM categories ORDER BY is_default DESC, name ASC;";

            var list = new List<CategoryRule>();
            using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
            while (await reader.ReadAsync(ct).ConfigureAwait(false))
            {
                list.Add(ReadCategoryRow(reader));
            }
            return list;
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task DeleteCategoryAsync(string id, CancellationToken ct = default)
    {
        await _lock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var conn = GetOpenConnection();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "DELETE FROM categories WHERE id = $id;";
            cmd.Parameters.AddWithValue("$id", id);
            await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }
        finally
        {
            _lock.Release();
        }
    }

    private static DownloadQueue ReadQueueRow(SqliteDataReader reader)
    {
        var startTimeStr = reader.IsDBNull(reader.GetOrdinal("start_time_utc")) ? null : reader.GetString(reader.GetOrdinal("start_time_utc"));
        var stopTimeStr = reader.IsDBNull(reader.GetOrdinal("stop_time_utc")) ? null : reader.GetString(reader.GetOrdinal("stop_time_utc"));

        return new DownloadQueue
        {
            Id = reader.GetString(reader.GetOrdinal("id")),
            Name = reader.GetString(reader.GetOrdinal("name")),
            MaxConcurrentDownloads = reader.GetInt32(reader.GetOrdinal("max_concurrent_downloads")),
            SpeedLimitBytesPerSec = reader.GetInt64(reader.GetOrdinal("speed_limit_bytes_per_sec")),
            IsActive = reader.GetInt32(reader.GetOrdinal("is_active")) == 1,
            ScheduleDaysMask = reader.GetInt32(reader.GetOrdinal("schedule_days_mask")),
            StartTimeUtc = startTimeStr != null ? TimeSpan.Parse(startTimeStr, System.Globalization.CultureInfo.InvariantCulture) : null,
            StopTimeUtc = stopTimeStr != null ? TimeSpan.Parse(stopTimeStr, System.Globalization.CultureInfo.InvariantCulture) : null,
            AfterCompletionAction = Enum.Parse<AfterCompletionAction>(reader.GetString(reader.GetOrdinal("after_completion_action"))),
            CustomActionCommand = reader.IsDBNull(reader.GetOrdinal("custom_action_command")) ? null : reader.GetString(reader.GetOrdinal("custom_action_command"))
        };
    }

    private static CategoryRule ReadCategoryRow(SqliteDataReader reader)
    {
        return new CategoryRule
        {
            Id = reader.GetString(reader.GetOrdinal("id")),
            Name = reader.GetString(reader.GetOrdinal("name")),
            TargetDirectory = reader.GetString(reader.GetOrdinal("target_directory")),
            FileExtensions = reader.GetString(reader.GetOrdinal("file_extensions")),
            IconKey = reader.GetString(reader.GetOrdinal("icon_key")),
            IsDefault = reader.GetInt32(reader.GetOrdinal("is_default")) == 1
        };
    }

    // Torrent Management
    public async Task SaveTorrentAsync(TorrentItem torrent, CancellationToken ct = default)
    {
        await _lock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var conn = GetOpenConnection();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = @"
                DELETE FROM torrents WHERE info_hash = $info_hash AND id != $id;
                INSERT INTO torrents (
                    id, info_hash, magnet_uri, name, total_bytes, piece_length, piece_count,
                    is_private, seed_ratio_limit, seed_time_limit_mins, uploaded_bytes,
                    downloaded_bytes, fast_resume_blob, comment, created_by, created_at_utc, completed_at_utc
                ) VALUES (
                    $id, $info_hash, $magnet_uri, $name, $total_bytes, $piece_length, $piece_count,
                    $is_private, $seed_ratio_limit, $seed_time_limit_mins, $uploaded_bytes,
                    $downloaded_bytes, $fast_resume_blob, $comment, $created_by, $created_at_utc, $completed_at_utc
                ) ON CONFLICT(id) DO UPDATE SET
                    info_hash = excluded.info_hash,
                    magnet_uri = excluded.magnet_uri,
                    name = excluded.name,
                    total_bytes = excluded.total_bytes,
                    piece_length = excluded.piece_length,
                    piece_count = excluded.piece_count,
                    is_private = excluded.is_private,
                    seed_ratio_limit = excluded.seed_ratio_limit,
                    seed_time_limit_mins = excluded.seed_time_limit_mins,
                    uploaded_bytes = excluded.uploaded_bytes,
                    downloaded_bytes = excluded.downloaded_bytes,
                    fast_resume_blob = excluded.fast_resume_blob,
                    comment = excluded.comment,
                    created_by = excluded.created_by,
                    completed_at_utc = excluded.completed_at_utc;";

            cmd.Parameters.AddWithValue("$id", torrent.Id);
            cmd.Parameters.AddWithValue("$info_hash", torrent.InfoHash);
            cmd.Parameters.AddWithValue("$magnet_uri", (object?)torrent.MagnetUri ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$name", torrent.Name);
            cmd.Parameters.AddWithValue("$total_bytes", torrent.TotalBytes);
            cmd.Parameters.AddWithValue("$piece_length", torrent.PieceLength);
            cmd.Parameters.AddWithValue("$piece_count", torrent.PieceCount);
            cmd.Parameters.AddWithValue("$is_private", torrent.IsPrivate ? 1 : 0);
            cmd.Parameters.AddWithValue("$seed_ratio_limit", torrent.SeedRatioLimit);
            cmd.Parameters.AddWithValue("$seed_time_limit_mins", torrent.SeedTimeLimitMins);
            cmd.Parameters.AddWithValue("$uploaded_bytes", torrent.UploadedBytes);
            cmd.Parameters.AddWithValue("$downloaded_bytes", torrent.DownloadedBytes);
            cmd.Parameters.AddWithValue("$fast_resume_blob", (object?)torrent.FastResumeBlob ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$comment", (object?)torrent.Comment ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$created_by", (object?)torrent.CreatedBy ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$created_at_utc", torrent.CreatedAtUtc.ToString("o"));
            cmd.Parameters.AddWithValue("$completed_at_utc", (object?)torrent.CompletedAtUtc?.ToString("o") ?? DBNull.Value);

            await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task<TorrentItem?> GetTorrentAsync(string id, CancellationToken ct = default)
    {
        await _lock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var conn = GetOpenConnection();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT * FROM torrents WHERE id = $id;";
            cmd.Parameters.AddWithValue("$id", id);
            using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
            if (!await reader.ReadAsync(ct).ConfigureAwait(false)) return null;
            return ReadTorrentRow(reader);
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task<TorrentItem?> GetTorrentByInfoHashAsync(string infoHash, CancellationToken ct = default)
    {
        await _lock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var conn = GetOpenConnection();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT * FROM torrents WHERE info_hash = $info_hash COLLATE NOCASE;";
            cmd.Parameters.AddWithValue("$info_hash", infoHash);
            using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
            if (!await reader.ReadAsync(ct).ConfigureAwait(false)) return null;
            return ReadTorrentRow(reader);
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task<IReadOnlyList<TorrentItem>> GetAllTorrentsAsync(CancellationToken ct = default)
    {
        await _lock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var conn = GetOpenConnection();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT * FROM torrents ORDER BY created_at_utc DESC;";
            var list = new List<TorrentItem>();
            using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
            while (await reader.ReadAsync(ct).ConfigureAwait(false))
            {
                list.Add(ReadTorrentRow(reader));
            }
            return list;
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task SaveTorrentFilesAsync(string torrentId, IReadOnlyList<TorrentFileItem> files, CancellationToken ct = default)
    {
        await _lock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var conn = GetOpenConnection();
            using var tx = conn.BeginTransaction();
            using var delCmd = conn.CreateCommand();
            delCmd.Transaction = tx;
            delCmd.CommandText = "DELETE FROM torrent_files WHERE torrent_id = $torrent_id;";
            delCmd.Parameters.AddWithValue("$torrent_id", torrentId);
            await delCmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);

            foreach (var file in files)
            {
                using var insCmd = conn.CreateCommand();
                insCmd.Transaction = tx;
                insCmd.CommandText = @"
                    INSERT INTO torrent_files (
                        id, torrent_id, file_index, relative_path, file_size_bytes, downloaded_bytes, priority, is_selected
                    ) VALUES (
                        $id, $torrent_id, $file_index, $relative_path, $file_size_bytes, $downloaded_bytes, $priority, $is_selected
                    );";
                insCmd.Parameters.AddWithValue("$id", file.Id);
                insCmd.Parameters.AddWithValue("$torrent_id", torrentId);
                insCmd.Parameters.AddWithValue("$file_index", file.FileIndex);
                insCmd.Parameters.AddWithValue("$relative_path", file.RelativePath);
                insCmd.Parameters.AddWithValue("$file_size_bytes", file.FileSizeBytes);
                insCmd.Parameters.AddWithValue("$downloaded_bytes", file.DownloadedBytes);
                insCmd.Parameters.AddWithValue("$priority", (int)file.Priority);
                insCmd.Parameters.AddWithValue("$is_selected", file.IsSelected ? 1 : 0);
                await insCmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
            }
            await tx.CommitAsync(ct).ConfigureAwait(false);
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task<IReadOnlyList<TorrentFileItem>> GetTorrentFilesAsync(string torrentId, CancellationToken ct = default)
    {
        await _lock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var conn = GetOpenConnection();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT * FROM torrent_files WHERE torrent_id = $torrent_id ORDER BY file_index ASC;";
            cmd.Parameters.AddWithValue("$torrent_id", torrentId);
            var list = new List<TorrentFileItem>();
            using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
            while (await reader.ReadAsync(ct).ConfigureAwait(false))
            {
                list.Add(ReadTorrentFileRow(reader));
            }
            return list;
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task UpdateTorrentFilePriorityAsync(string fileId, TorrentFilePriority priority, bool isSelected, CancellationToken ct = default)
    {
        await _lock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var conn = GetOpenConnection();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "UPDATE torrent_files SET priority = $priority, is_selected = $is_selected WHERE id = $id;";
            cmd.Parameters.AddWithValue("$priority", (int)priority);
            cmd.Parameters.AddWithValue("$is_selected", isSelected ? 1 : 0);
            cmd.Parameters.AddWithValue("$id", fileId);
            await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task SaveTorrentTrackersAsync(string torrentId, IReadOnlyList<TorrentTrackerItem> trackers, CancellationToken ct = default)
    {
        await _lock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var conn = GetOpenConnection();
            using var tx = conn.BeginTransaction();
            using var delCmd = conn.CreateCommand();
            delCmd.Transaction = tx;
            delCmd.CommandText = "DELETE FROM torrent_trackers WHERE torrent_id = $torrent_id;";
            delCmd.Parameters.AddWithValue("$torrent_id", torrentId);
            await delCmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);

            foreach (var tr in trackers)
            {
                using var insCmd = conn.CreateCommand();
                insCmd.Transaction = tx;
                insCmd.CommandText = @"
                    INSERT INTO torrent_trackers (
                        id, torrent_id, tier, announce_url, status, seeders, leechers, last_announce_utc, error_message
                    ) VALUES (
                        $id, $torrent_id, $tier, $announce_url, $status, $seeders, $leechers, $last_announce_utc, $error_message
                    );";
                insCmd.Parameters.AddWithValue("$id", tr.Id);
                insCmd.Parameters.AddWithValue("$torrent_id", torrentId);
                insCmd.Parameters.AddWithValue("$tier", tr.Tier);
                insCmd.Parameters.AddWithValue("$announce_url", tr.AnnounceUrl);
                insCmd.Parameters.AddWithValue("$status", tr.Status);
                insCmd.Parameters.AddWithValue("$seeders", tr.Seeders);
                insCmd.Parameters.AddWithValue("$leechers", tr.Leechers);
                insCmd.Parameters.AddWithValue("$last_announce_utc", (object?)tr.LastAnnounceUtc?.ToString("o") ?? DBNull.Value);
                insCmd.Parameters.AddWithValue("$error_message", (object?)tr.ErrorMessage ?? DBNull.Value);
                await insCmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
            }
            await tx.CommitAsync(ct).ConfigureAwait(false);
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task<IReadOnlyList<TorrentTrackerItem>> GetTorrentTrackersAsync(string torrentId, CancellationToken ct = default)
    {
        await _lock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var conn = GetOpenConnection();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT * FROM torrent_trackers WHERE torrent_id = $torrent_id ORDER BY tier ASC, announce_url ASC;";
            cmd.Parameters.AddWithValue("$torrent_id", torrentId);
            var list = new List<TorrentTrackerItem>();
            using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
            while (await reader.ReadAsync(ct).ConfigureAwait(false))
            {
                list.Add(ReadTorrentTrackerRow(reader));
            }
            return list;
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task UpdateTorrentFastResumeAsync(string torrentId, byte[] fastResumeBlob, CancellationToken ct = default)
    {
        await _lock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var conn = GetOpenConnection();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "UPDATE torrents SET fast_resume_blob = $blob WHERE id = $id;";
            cmd.Parameters.AddWithValue("$blob", (object?)fastResumeBlob ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$id", torrentId);
            await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task UpdateTorrentStatsAsync(string torrentId, long downloadedBytes, long uploadedBytes, CancellationToken ct = default)
    {
        await _lock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var conn = GetOpenConnection();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = @"
                UPDATE torrents
                SET downloaded_bytes = $downloaded_bytes,
                    uploaded_bytes = $uploaded_bytes
                WHERE id = $id;";
            cmd.Parameters.AddWithValue("$downloaded_bytes", downloadedBytes);
            cmd.Parameters.AddWithValue("$uploaded_bytes", uploadedBytes);
            cmd.Parameters.AddWithValue("$id", torrentId);
            await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task DeleteTorrentAsync(string id, CancellationToken ct = default)
    {
        await _lock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var conn = GetOpenConnection();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "DELETE FROM torrents WHERE id = $id;";
            cmd.Parameters.AddWithValue("$id", id);
            await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }
        finally
        {
            _lock.Release();
        }
    }

    private static TorrentItem ReadTorrentRow(SqliteDataReader reader)
    {
        var resumeBlob = reader.IsDBNull(reader.GetOrdinal("fast_resume_blob")) ? null : (byte[])reader["fast_resume_blob"];
        return new TorrentItem
        {
            Id = reader.GetString(reader.GetOrdinal("id")),
            InfoHash = reader.GetString(reader.GetOrdinal("info_hash")),
            MagnetUri = reader.IsDBNull(reader.GetOrdinal("magnet_uri")) ? null : reader.GetString(reader.GetOrdinal("magnet_uri")),
            Name = reader.GetString(reader.GetOrdinal("name")),
            TotalBytes = reader.GetInt64(reader.GetOrdinal("total_bytes")),
            PieceLength = reader.GetInt32(reader.GetOrdinal("piece_length")),
            PieceCount = reader.GetInt32(reader.GetOrdinal("piece_count")),
            IsPrivate = reader.GetInt32(reader.GetOrdinal("is_private")) == 1,
            SeedRatioLimit = reader.GetDouble(reader.GetOrdinal("seed_ratio_limit")),
            SeedTimeLimitMins = reader.GetInt32(reader.GetOrdinal("seed_time_limit_mins")),
            UploadedBytes = reader.GetInt64(reader.GetOrdinal("uploaded_bytes")),
            DownloadedBytes = reader.GetInt64(reader.GetOrdinal("downloaded_bytes")),
            FastResumeBlob = resumeBlob,
            Comment = reader.IsDBNull(reader.GetOrdinal("comment")) ? null : reader.GetString(reader.GetOrdinal("comment")),
            CreatedBy = reader.IsDBNull(reader.GetOrdinal("created_by")) ? null : reader.GetString(reader.GetOrdinal("created_by")),
            CreatedAtUtc = DateTime.Parse(reader.GetString(reader.GetOrdinal("created_at_utc")), System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.RoundtripKind),
            CompletedAtUtc = reader.IsDBNull(reader.GetOrdinal("completed_at_utc")) ? null : DateTime.Parse(reader.GetString(reader.GetOrdinal("completed_at_utc")), System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.RoundtripKind)
        };
    }

    private static TorrentFileItem ReadTorrentFileRow(SqliteDataReader reader)
    {
        return new TorrentFileItem
        {
            Id = reader.GetString(reader.GetOrdinal("id")),
            TorrentId = reader.GetString(reader.GetOrdinal("torrent_id")),
            FileIndex = reader.GetInt32(reader.GetOrdinal("file_index")),
            RelativePath = reader.GetString(reader.GetOrdinal("relative_path")),
            FileSizeBytes = reader.GetInt64(reader.GetOrdinal("file_size_bytes")),
            DownloadedBytes = reader.GetInt64(reader.GetOrdinal("downloaded_bytes")),
            Priority = (TorrentFilePriority)reader.GetInt32(reader.GetOrdinal("priority")),
            IsSelected = reader.GetInt32(reader.GetOrdinal("is_selected")) == 1
        };
    }

    private static TorrentTrackerItem ReadTorrentTrackerRow(SqliteDataReader reader)
    {
        return new TorrentTrackerItem
        {
            Id = reader.GetString(reader.GetOrdinal("id")),
            TorrentId = reader.GetString(reader.GetOrdinal("torrent_id")),
            Tier = reader.GetInt32(reader.GetOrdinal("tier")),
            AnnounceUrl = reader.GetString(reader.GetOrdinal("announce_url")),
            Status = reader.GetString(reader.GetOrdinal("status")),
            Seeders = reader.GetInt32(reader.GetOrdinal("seeders")),
            Leechers = reader.GetInt32(reader.GetOrdinal("leechers")),
            LastAnnounceUtc = reader.IsDBNull(reader.GetOrdinal("last_announce_utc")) ? null : DateTime.Parse(reader.GetString(reader.GetOrdinal("last_announce_utc")), System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.RoundtripKind),
            ErrorMessage = reader.IsDBNull(reader.GetOrdinal("error_message")) ? null : reader.GetString(reader.GetOrdinal("error_message"))
        };
    }

    public async ValueTask DisposeAsync()
    {
        if (_connection != null)
        {
            await _connection.CloseAsync().ConfigureAwait(false);
            await _connection.DisposeAsync().ConfigureAwait(false);
            _connection = null;
        }
        _lock.Dispose();
    }
}
