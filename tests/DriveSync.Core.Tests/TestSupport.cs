using DriveSync.Core.Engine;
using DriveSync.Core.Model;
using DriveSync.Core.RecycleBin;
using Microsoft.Data.Sqlite;

namespace DriveSync.Core.Tests;

/// <summary>
/// Where tests put their files: a folder next to the test binaries, inside the repo.
/// Tests never write anywhere else, and never to the real Recycle Bin.
/// </summary>
public static class TestPaths
{
    public static string Root { get; } = Path.Combine(AppContext.BaseDirectory, "testdata");

    public static string NewFolder(string prefix)
    {
        string path = Path.Combine(Root, prefix + Guid.NewGuid().ToString("N")[..10]);
        Directory.CreateDirectory(path);
        return path;
    }

    public static void Remove(string path)
    {
        SqliteConnection.ClearAllPools();
        if (!Path.GetFullPath(path).StartsWith(Root, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException($"Refusing to remove {path}: it's outside the test folder.");
        try
        {
            Directory.Delete(path, recursive: true);
        }
        catch (IOException)
        {
        }
    }
}

/// <summary>A throwaway PC (Desktop and Documents folders), backup drive and Recycle Bin inside the test folder.</summary>
public sealed class TestEnv : IDisposable
{
    static readonly DateTime BaseTime = new(2026, 1, 1, 9, 0, 0, DateTimeKind.Utc);
    int _clock;

    public string Dir { get; } = TestPaths.NewFolder("env-");

    public string Pc => Path.Combine(Dir, "pc");

    public string Drive => Path.Combine(Dir, "drive");

    public FolderRecycleBin Bin { get; }

    public BackupProfile Profile { get; }

    public TestEnv()
    {
        Directory.CreateDirectory(Path.Combine(Pc, "Desktop"));
        Directory.CreateDirectory(Path.Combine(Pc, "Documents"));
        Directory.CreateDirectory(Drive);
        Bin = new FolderRecycleBin(Path.Combine(Dir, "recycle-bin"));
        Profile = BackupSetup.CreateProfile(Drive, [Path.Combine(Pc, "Desktop"), Path.Combine(Pc, "Documents")]);
    }

    public string BackupRoot => Profile.LastKnownRoot!;

    public DriveSyncSession Open() => DriveSyncSession.Open(Profile, Bin, Path.Combine(Dir, "pc-mirror"));

    /// <summary>Path on the PC for a key such as <c>Desktop\a.txt</c>.</summary>
    public string PcPath(string key) => Path.Combine(Pc, key);

    public string BackupPath(string key) => Path.Combine(BackupRoot, key);

    /// <summary>Writes a file on the PC. Each write gets a later modified time, like real edits.</summary>
    public string Write(string key, string content)
    {
        string path = PcPath(key);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
        File.SetLastWriteTimeUtc(path, BaseTime.AddMinutes(++_clock));
        return path;
    }

    public async Task<SyncResult> SyncAsync()
    {
        using var session = Open();
        return await session.SyncAsync();
    }

    public FileRecord? Record(string key)
    {
        using var session = Open();
        return session.Catalog.Get(key);
    }

    public void Dispose() => TestPaths.Remove(Dir);
}
