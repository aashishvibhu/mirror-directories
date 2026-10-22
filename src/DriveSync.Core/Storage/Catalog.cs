using System.Globalization;
using DriveSync.Core.Model;
using Microsoft.Data.Sqlite;

namespace DriveSync.Core.Storage;

public sealed record ActivityEntry(DateTime TimeUtc, string Action, string? Key, string? Detail);

public sealed record StateTotal(FileState State, int Files, long Bytes);

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
        PRAGMA user_version = 1;
        """;

    const string Columns = "key, size, mtime, hash, state, state_changed";

    readonly SqliteConnection _db;
    SqliteTransaction? _tx;

    public string FilePath { get; }

    Catalog(string path)
    {
        FilePath = path;
        _db = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Pooling = false, // release the file as soon as we're done, so the drive can be ejected
        }.ToString());
        _db.Open();
        // A rollback journal (not WAL) is safest on a USB drive that may be unplugged.
        Execute("PRAGMA journal_mode = DELETE; PRAGMA synchronous = FULL;");
        Execute(Schema);
    }

    public static Catalog Open(string path) => new(path);

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
