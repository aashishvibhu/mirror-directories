using Microsoft.Data.Sqlite;

namespace DriveSync.Core.Storage;

/// <summary>
/// Files offloaded through DriveSync, kept on the PC and shared by every backup. When a file is in two backups and is
/// offloaded through one, the other finds it here and keeps its copy instead of asking whether it was deleted.
/// </summary>
public sealed class OffloadList(string path)
{
    const string Schema = """
        CREATE TABLE IF NOT EXISTS offloaded (
            path  TEXT    NOT NULL PRIMARY KEY COLLATE NOCASE,
            hash  BLOB    NOT NULL,
            size  INTEGER NOT NULL,
            time  INTEGER NOT NULL);
        """;

    public string FilePath { get; } = path;

    public void Add(IEnumerable<(string Path, string Hash, long Size)> files)
    {
        using var db = Open();
        using var tx = db.BeginTransaction();
        foreach (var (file, hash, size) in files)
        {
            using var cmd = db.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = """
                INSERT INTO offloaded (path, hash, size, time) VALUES ($path, $hash, $size, $time)
                ON CONFLICT(path) DO UPDATE SET hash = excluded.hash, size = excluded.size, time = excluded.time
                """;
            cmd.Parameters.AddWithValue("$path", file);
            cmd.Parameters.AddWithValue("$hash", Convert.FromHexString(hash));
            cmd.Parameters.AddWithValue("$size", size);
            cmd.Parameters.AddWithValue("$time", DateTime.UtcNow.Ticks);
            cmd.ExecuteNonQuery();
        }
        tx.Commit();
    }

    /// <summary>The paths among <paramref name="candidates"/> that were offloaded with exactly that content.</summary>
    public HashSet<string> Matching(IEnumerable<(string Path, string Hash)> candidates)
    {
        var found = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (!File.Exists(FilePath))
            return found;
        using var db = Open();
        foreach (var (file, hash) in candidates)
        {
            if (hash.Length == 0)
                continue;
            using var cmd = db.CreateCommand();
            cmd.CommandText = "SELECT hash FROM offloaded WHERE path = $path";
            cmd.Parameters.AddWithValue("$path", file);
            if (cmd.ExecuteScalar() is byte[] stored && Convert.ToHexString(stored).Equals(hash, StringComparison.OrdinalIgnoreCase))
                found.Add(file);
        }
        return found;
    }

    SqliteConnection Open()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
        var db = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = FilePath, Pooling = false }.ToString());
        db.Open();
        using var cmd = db.CreateCommand();
        cmd.CommandText = Schema;
        cmd.ExecuteNonQuery();
        return db;
    }
}
