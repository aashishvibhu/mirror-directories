using DriveSync.Core.Engine;
using DriveSync.Core.Model;
using DriveSync.Core.RecycleBin;
using Microsoft.Data.Sqlite;

namespace DriveSync.Core.Tests;

/// <summary>
/// A Recycle Bin that stores items in a test folder, so tests never touch the real one.
/// Follows the same rules as the real bin: it refuses items it has no room for instead of discarding older ones.
/// </summary>
public sealed class FakeRecycleBin(string folder) : IRecycleBin
{
    int _next;

    public bool Supported { get; set; } = true;

    public long MaxBytes { get; set; } = long.MaxValue;

    public List<RecycledItem> Items { get; } = [];

    public bool IsSupported(string path) => Supported;

    public IReadOnlyDictionary<string, RecycleOutcome> Recycle(IReadOnlyList<string> paths)
    {
        var outcomes = new Dictionary<string, RecycleOutcome>(StringComparer.OrdinalIgnoreCase);
        if (!Supported)
        {
            foreach (string p in paths)
                outcomes[p] = RecycleOutcome.NotSupported;
            return outcomes;
        }

        long used = Items.Where(i => File.Exists(i.DataPath) || Directory.Exists(i.DataPath)).Sum(i => i.Size);
        var accepted = new List<string>();
        foreach (string p in paths)
        {
            if (SizeOf(p) > MaxBytes)
                outcomes[p] = RecycleOutcome.TooLarge;
            else
                accepted.Add(p);
        }
        if (used + accepted.Sum(SizeOf) > MaxBytes)
        {
            foreach (string p in accepted)
                outcomes[p] = RecycleOutcome.BinFull;
            return outcomes;
        }

        Directory.CreateDirectory(folder);
        foreach (string p in accepted)
        {
            long size = SizeOf(p);
            string data = Path.Combine(folder, $"$R{++_next:D4}{Path.GetExtension(p)}");
            if (File.Exists(p))
                File.Move(p, data);
            else if (Directory.Exists(p))
                Directory.Move(p, data);
            else
            {
                outcomes[p] = RecycleOutcome.Failed;
                continue;
            }
            Items.Add(new RecycledItem(Path.GetFullPath(p), DateTime.UtcNow, size, "", data));
            outcomes[p] = RecycleOutcome.Recycled;
        }
        return outcomes;
    }

    public IReadOnlyList<RecycledItem> List(string driveRoot) =>
        [.. Items.Where(i => i.OriginalPath.StartsWith(driveRoot, StringComparison.OrdinalIgnoreCase))];

    public bool Holds(string originalPath) =>
        Items.Any(i => i.OriginalPath.Equals(Path.GetFullPath(originalPath), StringComparison.OrdinalIgnoreCase));

    static long SizeOf(string path) =>
        File.Exists(path) ? new FileInfo(path).Length
        : Directory.Exists(path) ? Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories).Sum(f => new FileInfo(f).Length)
        : 0;
}

/// <summary>A throwaway PC (Desktop and Documents folders), backup drive and Recycle Bin in a temp folder.</summary>
public sealed class TestEnv : IDisposable
{
    static readonly DateTime BaseTime = new(2026, 1, 1, 9, 0, 0, DateTimeKind.Utc);
    int _clock;

    public string Dir { get; } = Path.Combine(Path.GetTempPath(), "DriveSyncTests", Guid.NewGuid().ToString("N")[..10]);

    public string Pc => Path.Combine(Dir, "pc");

    public string Drive => Path.Combine(Dir, "drive");

    public FakeRecycleBin Bin { get; }

    public BackupProfile Profile { get; }

    public TestEnv()
    {
        Directory.CreateDirectory(Path.Combine(Pc, "Desktop"));
        Directory.CreateDirectory(Path.Combine(Pc, "Documents"));
        Directory.CreateDirectory(Drive);
        Bin = new FakeRecycleBin(Path.Combine(Dir, "recycle-bin"));
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

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try
        {
            Directory.Delete(Dir, recursive: true);
        }
        catch (IOException)
        {
        }
    }
}
