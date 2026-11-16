using System.Globalization;
using DriveSync.Core.Model;
using Microsoft.Data.Sqlite;

namespace DriveSync.Core.Storage;

public sealed record ActivityEntry(DateTime TimeUtc, string Action, string? Key, string? Detail);

public sealed record StateTotal(FileState State, int Files, long Bytes);

/// <summary>A row in the backup browser. For a folder, the numbers cover everything inside it and <see cref="State"/> is null.</summary>
public sealed record BrowseEntry(string Name, string Key, bool IsFolder, int Files, long Bytes, int Offloaded, int Missing, long ModifiedUtcTicks, FileState? State);

/// <summary>An older version of a file. <see cref="Path"/> is its place inside the <c>versions</c> folder.</summary>
public sealed record VersionRow(string Path, string Key, string Stamp, long Size, long ModifiedUtcTicks, string Hash);

/// <summary>
/// The record of every file DriveSync has backed up, with its checksum and state.
/// Lives in <c>.drivesync\catalog.db</c> on the backup drive; a copy is kept on the PC.
/// </summary>
public sealed class Catalog : IDisposable
{
    const string Schema = """
        CREATE TABLE IF NOT EXISTS files (
            key           TEXT    NOT NULL PRIMARY KEY COLLATE NOCASE,
            size          INTEGER NOT NULL,
            mtime         INTEGER NOT NULL,
            hash          BLOB,
            state         INTEGER NOT NULL,
            state_changed INTEGER NOT NULL);
        CREATE INDEX IF NOT EXISTS ix_files_state ON files(state);
        CREATE TABLE IF NOT EXISTS activity (
            id     INTEGER PRIMARY KEY,
            time   INTEGER NOT NULL,
            action TEXT    NOT NULL,
            key    TEXT,
            detail TEXT);
        CREATE TABLE IF NOT EXISTS meta (name TEXT PRIMARY KEY, value TEXT);
        """;

    /// <summary>
    /// Version 2 adds the list of old versions and, for every file, when its backup copy last passed a checksum check
    /// (0 = never, -1 = it failed the last check).
    /// </summary>
    const string SchemaV2 = """
        ALTER TABLE files ADD COLUMN verified INTEGER NOT NULL DEFAULT 0;
        CREATE TABLE IF NOT EXISTS versions (
            path  TEXT    NOT NULL PRIMARY KEY COLLATE NOCASE,
            key   TEXT    NOT NULL COLLATE NOCASE,
            stamp TEXT    NOT NULL,
            size  INTEGER NOT NULL,
            mtime INTEGER NOT NULL,
            hash  BLOB);
        CREATE INDEX IF NOT EXISTS ix_versions_key ON versions(key);
        PRAGMA user_version = 2;
        """;

    public const int CurrentVersion = 2;

    const string Columns = "key, size, mtime, hash, state, state_changed";

    readonly SqliteConnection _db;
    SqliteTransaction? _tx;

    public string FilePath { get; }

    Catalog(string path, bool readOnly = false)
    {
        FilePath = path;
        _db = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Mode = readOnly ? SqliteOpenMode.ReadOnly : SqliteOpenMode.ReadWriteCreate,
            Pooling = false, // release the file as soon as we're done, so the drive can be ejected
        }.ToString());
        _db.Open();
        if (readOnly)
            return;
        // A rollback journal (not WAL) is safest on a USB drive that may be unplugged.
        Execute("PRAGMA journal_mode = DELETE; PRAGMA synchronous = FULL;");
        Execute(Schema);
        if (SchemaVersion() < 2)
            Execute(SchemaV2);
    }

    int SchemaVersion()
    {
        using var cmd = Command("PRAGMA user_version");
        return Convert.ToInt32(cmd.ExecuteScalar(), CultureInfo.InvariantCulture);
    }

    /// <summary>False for a copy written by an older DriveSync that hasn't been upgraded yet (only possible read-only).</summary>
    public bool HasVersion2 => SchemaVersion() >= 2;

    public static Catalog Open(string path) => new(path);

    /// <summary>Opens an existing catalog without changing it, e.g. the copy kept on the PC.</summary>
    public static Catalog OpenReadOnly(string path) => new(path, readOnly: true);

    /// <summary>Groups writes into one transaction until disposed. Nested calls join the open one.</summary>
    public IDisposable Batch()
    {
        if (_tx is not null)
            return new Scope(null);
        _tx = _db.BeginTransaction();
        return new Scope(this);
    }

    /// <summary>Commits what the open batch has so far, so an interruption loses little.</summary>
    public void Checkpoint()
    {
        if (_tx is null)
            return;
        _tx.Commit();
        _tx.Dispose();
        _tx = _db.BeginTransaction();
    }

    void EndBatch()
    {
        if (_tx is null)
            return;
        _tx.Commit();
        _tx.Dispose();
        _tx = null;
    }

    sealed class Scope(Catalog? owner) : IDisposable
    {
        public void Dispose() => owner?.EndBatch();
    }

    public Dictionary<string, FileRecord> LoadAll()
    {
        var all = new Dictionary<string, FileRecord>(KeyPath.Comparer);
        using var cmd = Command($"SELECT {Columns} FROM files");
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            var record = ReadRecord(reader);
            all[record.Key] = record;
        }
        return all;
    }

    public FileRecord? Get(string key)
    {
        using var cmd = Command($"SELECT {Columns} FROM files WHERE key = $key");
        cmd.Parameters.AddWithValue("$key", key);
        using var reader = cmd.ExecuteReader();
        return reader.Read() ? ReadRecord(reader) : null;
    }

    /// <summary>Records in a state and/or at or under a key, e.g. everything under <c>Desktop\Projects</c>.</summary>
    public List<FileRecord> Query(FileState? state = null, string? underKey = null)
    {
        var where = new List<string>();
        using var cmd = Command("");
        if (state is not null)
        {
            where.Add("state = $state");
            cmd.Parameters.AddWithValue("$state", (int)state.Value);
        }
        if (!string.IsNullOrEmpty(underKey))
        {
            where.Add(@"(key = $key OR key LIKE $like ESCAPE '|')");
            cmd.Parameters.AddWithValue("$key", underKey);
            cmd.Parameters.AddWithValue("$like", EscapeLike(underKey) + @"\%");
        }
        cmd.CommandText = $"SELECT {Columns} FROM files" + (where.Count > 0 ? " WHERE " + string.Join(" AND ", where) : "") + " ORDER BY key";
        using var reader = cmd.ExecuteReader();
        var list = new List<FileRecord>();
        while (reader.Read())
            list.Add(ReadRecord(reader));
        return list;
    }

    public void Save(FileRecord record)
    {
        using var cmd = Command($"""
            INSERT INTO files ({Columns}) VALUES ($key, $size, $mtime, $hash, $state, $changed)
            ON CONFLICT(key) DO UPDATE SET key = excluded.key, size = excluded.size, mtime = excluded.mtime,
                verified = CASE WHEN excluded.hash IS files.hash THEN files.verified ELSE 0 END,
                hash = excluded.hash, state = excluded.state, state_changed = excluded.state_changed
            """);
        cmd.Parameters.AddWithValue("$key", record.Key);
        cmd.Parameters.AddWithValue("$size", record.Size);
        cmd.Parameters.AddWithValue("$mtime", record.ModifiedUtcTicks);
        cmd.Parameters.AddWithValue("$hash", record.Hash.Length == 0 ? DBNull.Value : Convert.FromHexString(record.Hash));
        cmd.Parameters.AddWithValue("$state", (int)record.State);
        cmd.Parameters.AddWithValue("$changed", record.StateChangedUtc.Ticks);
        cmd.ExecuteNonQuery();
    }

    public void Remove(string key)
    {
        using var cmd = Command("DELETE FROM files WHERE key = $key");
        cmd.Parameters.AddWithValue("$key", key);
        cmd.ExecuteNonQuery();
    }

    public int Count(FileState state)
    {
        using var cmd = Command("SELECT COUNT(*) FROM files WHERE state = $state");
        cmd.Parameters.AddWithValue("$state", (int)state);
        return Convert.ToInt32(cmd.ExecuteScalar(), CultureInfo.InvariantCulture);
    }

    public List<StateTotal> Totals()
    {
        using var cmd = Command("SELECT state, COUNT(*), COALESCE(SUM(size), 0) FROM files GROUP BY state");
        using var reader = cmd.ExecuteReader();
        var totals = new List<StateTotal>();
        while (reader.Read())
            totals.Add(new StateTotal((FileState)reader.GetInt32(0), reader.GetInt32(1), reader.GetInt64(2)));
        return totals;
    }

    /// <summary>Files and bytes on the backup per top-level folder (each folder is one backed-up PC folder), Deleted excluded.</summary>
    public Dictionary<string, (int Files, long Bytes)> TotalsByFolder()
    {
        using var cmd = Command("""
            SELECT CASE WHEN instr(key, '\') > 0 THEN substr(key, 1, instr(key, '\') - 1) ELSE key END AS folder,
                   COUNT(*), COALESCE(SUM(size), 0)
            FROM files WHERE state <> $deleted GROUP BY folder
            """);
        cmd.Parameters.AddWithValue("$deleted", (int)FileState.Deleted);
        using var reader = cmd.ExecuteReader();
        var totals = new Dictionary<string, (int, long)>(KeyPath.Comparer);
        while (reader.Read())
            totals[reader.GetString(0)] = (reader.GetInt32(1), reader.GetInt64(2));
        return totals;
    }

    // Browsing

    /// <summary>
    /// The folders and files directly inside <paramref name="folderKey"/> (empty for the top of the backup),
    /// leaving out files deleted on purpose. Folders carry the totals of everything beneath them.
    /// </summary>
    public List<BrowseEntry> ListFolder(string folderKey, FileState? state = null)
    {
        using var cmd = Command("");
        string where = "state <> 3";
        if (folderKey.Length > 0)
        {
            where += " AND key LIKE $like ESCAPE '|'";
            cmd.Parameters.AddWithValue("$like", EscapeLike(folderKey) + @"\%");
        }
        if (state is not null)
        {
            where += " AND state = $state";
            cmd.Parameters.AddWithValue("$state", (int)state.Value);
        }
        cmd.Parameters.AddWithValue("$skip", folderKey.Length == 0 ? 1 : folderKey.Length + 2);
        cmd.CommandText = $"""
            SELECT CASE WHEN instr(rest, '\') > 0 THEN substr(rest, 1, instr(rest, '\') - 1) ELSE rest END AS name,
                   instr(rest, '\') > 0 AS is_folder,
                   COUNT(*), COALESCE(SUM(size), 0), SUM(state = 1), SUM(state = 2), MAX(mtime), MIN(state)
            FROM (SELECT substr(key, $skip) AS rest, size, state, mtime FROM files WHERE {where})
            GROUP BY name COLLATE NOCASE, is_folder
            ORDER BY is_folder DESC, name COLLATE NOCASE
            """;
        using var reader = cmd.ExecuteReader();
        var list = new List<BrowseEntry>();
        while (reader.Read())
        {
            string name = reader.GetString(0);
            bool isFolder = reader.GetInt64(1) != 0;
            list.Add(new BrowseEntry(name, folderKey.Length == 0 ? name : folderKey + "\\" + name, isFolder,
                reader.GetInt32(2), reader.GetInt64(3), reader.GetInt32(4), reader.GetInt32(5), reader.GetInt64(6),
                isFolder ? null : (FileState)reader.GetInt32(7)));
        }
        return list;
    }

    /// <summary>Files whose path contains <paramref name="text"/>, leaving out files deleted on purpose.</summary>
    public List<FileRecord> Search(string text, FileState? state = null, int max = 500)
    {
        using var cmd = Command("");
        string where = "state <> 3 AND key LIKE $like ESCAPE '|'";
        cmd.Parameters.AddWithValue("$like", "%" + EscapeLike(text) + "%");
        if (state is not null)
        {
            where += " AND state = $state";
            cmd.Parameters.AddWithValue("$state", (int)state.Value);
        }
        cmd.Parameters.AddWithValue("$max", max);
        cmd.CommandText = $"SELECT {Columns} FROM files WHERE {where} ORDER BY key LIMIT $max";
        using var reader = cmd.ExecuteReader();
        var list = new List<FileRecord>();
        while (reader.Read())
            list.Add(ReadRecord(reader));
        return list;
    }

    // Old versions

    public void AddVersion(VersionRow version)
    {
        using var cmd = Command("""
            INSERT INTO versions (path, key, stamp, size, mtime, hash) VALUES ($path, $key, $stamp, $size, $mtime, $hash)
            ON CONFLICT(path) DO UPDATE SET key = excluded.key, stamp = excluded.stamp, size = excluded.size,
                mtime = excluded.mtime, hash = COALESCE(excluded.hash, versions.hash)
            """);
        cmd.Parameters.AddWithValue("$path", version.Path);
        cmd.Parameters.AddWithValue("$key", version.Key);
        cmd.Parameters.AddWithValue("$stamp", version.Stamp);
        cmd.Parameters.AddWithValue("$size", version.Size);
        cmd.Parameters.AddWithValue("$mtime", version.ModifiedUtcTicks);
        cmd.Parameters.AddWithValue("$hash", version.Hash.Length == 0 ? DBNull.Value : Convert.FromHexString(version.Hash));
        cmd.ExecuteNonQuery();
    }

    public void RemoveVersion(string path)
    {
        using var cmd = Command("DELETE FROM versions WHERE path = $path");
        cmd.Parameters.AddWithValue("$path", path);
        cmd.ExecuteNonQuery();
    }

    public void ClearVersions() => Execute("DELETE FROM versions");

    /// <summary>Old versions, newest first; all of them, or those at or under a key.</summary>
    public List<VersionRow> Versions(string? underKey = null)
    {
        using var cmd = Command("");
        string where = "";
        if (!string.IsNullOrEmpty(underKey))
        {
            where = @" WHERE (key = $key OR key LIKE $like ESCAPE '|')";
            cmd.Parameters.AddWithValue("$key", underKey);
            cmd.Parameters.AddWithValue("$like", EscapeLike(underKey) + @"\%");
        }
        cmd.CommandText = "SELECT path, key, stamp, size, mtime, hash FROM versions" + where + " ORDER BY stamp DESC, key";
        using var reader = cmd.ExecuteReader();
        var list = new List<VersionRow>();
        while (reader.Read())
        {
            list.Add(new VersionRow(reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetInt64(3), reader.GetInt64(4),
                reader.IsDBNull(5) ? "" : Convert.ToHexString((byte[])reader[5])));
        }
        return list;
    }

    public (int Count, long Bytes) VersionTotals()
    {
        using var cmd = Command("SELECT COUNT(*), COALESCE(SUM(size), 0) FROM versions");
        using var reader = cmd.ExecuteReader();
        reader.Read();
        return (reader.GetInt32(0), reader.GetInt64(1));
    }

    /// <summary>The files whose old versions take the most space.</summary>
    public List<(string Key, int Count, long Bytes)> VersionHogs(int max = 50)
    {
        using var cmd = Command("SELECT key, COUNT(*), SUM(size) FROM versions GROUP BY key ORDER BY SUM(size) DESC LIMIT $max");
        cmd.Parameters.AddWithValue("$max", max);
        using var reader = cmd.ExecuteReader();
        var list = new List<(string, int, long)>();
        while (reader.Read())
            list.Add((reader.GetString(0), reader.GetInt32(1), reader.GetInt64(2)));
        return list;
    }

    // Checksum checks

    /// <summary>Records the outcome of a checksum check: the time it passed, or <see cref="FailedCheck"/>.</summary>
    public void SetVerified(string key, long value)
    {
        using var cmd = Command("UPDATE files SET verified = $v WHERE key = $key");
        cmd.Parameters.AddWithValue("$v", value);
        cmd.Parameters.AddWithValue("$key", key);
        cmd.ExecuteNonQuery();
    }

    public const long FailedCheck = -1;

    /// <summary>Backup copies to check next: never checked first, then the ones checked longest ago.</summary>
    public List<FileRecord> LeastRecentlyVerified(int max, long checkedBeforeTicks)
    {
        using var cmd = Command($"SELECT {Columns} FROM files WHERE state <> 3 AND verified >= 0 AND verified < $before ORDER BY verified, key LIMIT $max");
        cmd.Parameters.AddWithValue("$max", max);
        cmd.Parameters.AddWithValue("$before", checkedBeforeTicks);
        using var reader = cmd.ExecuteReader();
        var list = new List<FileRecord>();
        while (reader.Read())
            list.Add(ReadRecord(reader));
        return list;
    }

    /// <summary>Files whose backup copy failed its last checksum check.</summary>
    public List<FileRecord> FailedChecks()
    {
        using var cmd = Command($"SELECT {Columns} FROM files WHERE state <> 3 AND verified = -1 ORDER BY key");
        using var reader = cmd.ExecuteReader();
        var list = new List<FileRecord>();
        while (reader.Read())
            list.Add(ReadRecord(reader));
        return list;
    }

    /// <summary>How many backup copies there are, and how many passed a checksum check since <paramref name="sinceUtc"/>.</summary>
    public (int Files, int Verified, int Failed) VerifiedStats(DateTime sinceUtc)
    {
        using var cmd = Command("SELECT COUNT(*), COALESCE(SUM(verified >= $since AND verified > 0), 0), COALESCE(SUM(verified = -1), 0) FROM files WHERE state <> 3");
        cmd.Parameters.AddWithValue("$since", sinceUtc.Ticks);
        using var reader = cmd.ExecuteReader();
        reader.Read();
        return (reader.GetInt32(0), reader.GetInt32(1), reader.GetInt32(2));
    }

    public void Log(string action, string? key, string? detail = null)
    {
        using var cmd = Command("INSERT INTO activity (time, action, key, detail) VALUES ($time, $action, $key, $detail)");
        cmd.Parameters.AddWithValue("$time", DateTime.UtcNow.Ticks);
        cmd.Parameters.AddWithValue("$action", action);
        cmd.Parameters.AddWithValue("$key", (object?)key ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$detail", (object?)detail ?? DBNull.Value);
        cmd.ExecuteNonQuery();
    }

    public List<ActivityEntry> RecentActivity(int max = 200)
    {
        using var cmd = Command("SELECT time, action, key, detail FROM activity ORDER BY id DESC LIMIT $max");
        cmd.Parameters.AddWithValue("$max", max);
        using var reader = cmd.ExecuteReader();
        var list = new List<ActivityEntry>();
        while (reader.Read())
        {
            list.Add(new ActivityEntry(
                new DateTime(reader.GetInt64(0), DateTimeKind.Utc),
                reader.GetString(1),
                reader.IsDBNull(2) ? null : reader.GetString(2),
                reader.IsDBNull(3) ? null : reader.GetString(3)));
        }
        return list;
    }

    public string? GetMeta(string name)
    {
        using var cmd = Command("SELECT value FROM meta WHERE name = $name");
        cmd.Parameters.AddWithValue("$name", name);
        return cmd.ExecuteScalar() as string;
    }

    public void SetMeta(string name, string value)
    {
        using var cmd = Command("INSERT INTO meta (name, value) VALUES ($name, $value) ON CONFLICT(name) DO UPDATE SET value = excluded.value");
        cmd.Parameters.AddWithValue("$name", name);
        cmd.Parameters.AddWithValue("$value", value);
        cmd.ExecuteNonQuery();
    }

    /// <summary>Writes a consistent copy of the whole catalog to another file.</summary>
    public void CopyTo(string destinationPath)
    {
        EndBatch();
        using var destination = new SqliteConnection($"Data Source={destinationPath};Pooling=False");
        destination.Open();
        _db.BackupDatabase(destination);
    }

    public void Dispose()
    {
        EndBatch();
        _db.Dispose();
    }

    SqliteCommand Command(string sql)
    {
        var cmd = _db.CreateCommand();
        cmd.CommandText = sql;
        cmd.Transaction = _tx;
        return cmd;
    }

    void Execute(string sql)
    {
        using var cmd = Command(sql);
        cmd.ExecuteNonQuery();
    }

    static FileRecord ReadRecord(SqliteDataReader r) => new()
    {
        Key = r.GetString(0),
        Size = r.GetInt64(1),
        ModifiedUtcTicks = r.GetInt64(2),
        Hash = r.IsDBNull(3) ? "" : Convert.ToHexString((byte[])r[3]),
        State = (FileState)r.GetInt32(4),
        StateChangedUtc = new DateTime(r.GetInt64(5), DateTimeKind.Utc),
    };

    static string EscapeLike(string text) => text.Replace("|", "||").Replace("%", "|%").Replace("_", "|_");
}
