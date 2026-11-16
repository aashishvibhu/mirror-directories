using System.Globalization;
using DriveSync.Core.IO;
using DriveSync.Core.Model;
using DriveSync.Core.RecycleBin;
using DriveSync.Core.Scanning;
using DriveSync.Core.Storage;

namespace DriveSync.Core.Engine;

/// <summary>
/// An open connection to one backup drive. Holds the drive's lock while open, so only one task works on it at a time.
/// DriveSync never deletes a user's file: everything it removes goes through <see cref="IRecycleBin"/>,
/// or into the drive's trash folder when the drive has no Recycle Bin.
/// </summary>
public sealed partial class DriveSyncSession : IDisposable
{
    readonly IRecycleBin _bin;
    readonly RunLock _lock;
    readonly string? _mirrorDirectory;
    readonly ExclusionMatcher _exclusions;
    readonly Dictionary<string, SourceFolder> _sources;
    readonly OffloadList? _offloadList;

    public BackupProfile Profile { get; }

    public BackupLocation Location { get; }

    public Catalog Catalog { get; }

    DriveSyncSession(BackupProfile profile, BackupLocation location, Catalog catalog, IRecycleBin bin, RunLock runLock, string? mirrorDirectory, string? offloadListPath)
    {
        Profile = profile;
        Location = location;
        Catalog = catalog;
        _bin = bin;
        _lock = runLock;
        _mirrorDirectory = mirrorDirectory;
        _offloadList = offloadListPath is null ? null : new OffloadList(offloadListPath);
        _exclusions = new ExclusionMatcher(profile.Exclusions);
        _sources = profile.Sources.ToDictionary(s => s.Name, KeyPath.Comparer);
    }

    /// <summary>
    /// Finds the profile's backup drive and opens it. <paramref name="mirrorDirectory"/> receives a copy of the
    /// catalog when the session closes, so the record survives even if the drive fails.
    /// <paramref name="offloadListPath"/> is the list of offloaded files shared by every backup on this PC.
    /// </summary>
    public static DriveSyncSession Open(BackupProfile profile, IRecycleBin recycleBin, string? mirrorDirectory = null, string? offloadListPath = null)
    {
        var location = BackupLocation.Find(profile)
            ?? throw new DriveSyncException("The backup drive isn't connected. Plug in the drive you set up for DriveSync, then try again.");
        profile.LastKnownRoot = location.Root;
        var runLock = RunLock.Acquire(location.LockPath);
        try
        {
            var session = new DriveSyncSession(profile, location, Catalog.Open(location.CatalogPath), recycleBin, runLock, mirrorDirectory, offloadListPath);
            try
            {
                // A backup made before old versions were listed in the catalog gets its list built once.
                if (session.Catalog.GetMeta("versions_indexed") is null)
                    session.ReindexVersions();
                return session;
            }
            catch
            {
                session.Catalog.Dispose();
                throw;
            }
        }
        catch
        {
            runLock.Dispose();
            throw;
        }
    }

    public void Dispose()
    {
        // The drive carries its own settings, so it can be used from another PC.
        BackupManifest.TryWrite(Location.Root, Profile);
        if (_mirrorDirectory is not null)
        {
            try
            {
                Directory.CreateDirectory(_mirrorDirectory);
                Catalog.CopyTo(Path.Combine(_mirrorDirectory, $"catalog-{Profile.DriveId}.db"));
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or Microsoft.Data.Sqlite.SqliteException)
            {
                // The copy on the PC is a convenience; the drive's catalog is the real one.
            }
        }
        Catalog.Dispose();
        _lock.Dispose();
    }

    /// <summary>
    /// Turns a path the user picked into a catalog key. Accepts a path on the PC, a path inside the backup,
    /// or a key such as <c>Desktop\Taxes</c>.
    /// </summary>
    public string ResolveKey(string path)
    {
        if (Path.IsPathRooted(path))
        {
            string full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
            foreach (var source in Profile.Sources)
            {
                if (TryRelative(full, source.Path, out string relative))
                    return KeyPath.Combine(source.Name, relative);
            }
            if (TryRelative(full, Location.Root, out string inBackup) && inBackup.Length > 0 && _sources.ContainsKey(KeyPath.SourceName(inBackup)))
                return inBackup;
            throw new DriveSyncException($"{path} isn't in a folder DriveSync backs up.");
        }

        string key = path.Trim('\\');
        if (_sources.ContainsKey(KeyPath.SourceName(key)) || RemovedFolderFor(key) is not null)
            return key;
        throw new DriveSyncException($"{path} isn't in a folder DriveSync backs up.");
    }

    /// <summary>The folder that was taken out of this backup and still has <paramref name="key"/> on the drive, if any.</summary>
    RemovedFolder? RemovedFolderFor(string key) =>
        Profile.RemovedFolders.FirstOrDefault(r => r.Name.Equals(KeyPath.SourceName(key), StringComparison.OrdinalIgnoreCase));

    /// <summary>True when the key's folder is one this backup still copies.</summary>
    public bool IsBackedUp(string key) => _sources.ContainsKey(KeyPath.SourceName(key));

    /// <summary>Where a key lives on the PC.</summary>
    public string SourcePathFor(string key)
    {
        if (!_sources.TryGetValue(KeyPath.SourceName(key), out var source))
            throw new DriveSyncException($"{KeyPath.SourceName(key)} is no longer one of the folders DriveSync backs up.");
        string relative = KeyPath.Relative(key);
        return relative.Length == 0 ? source.Path : Path.Combine(source.Path, relative);
    }

    public BackupSummary GetSummary() => ReadSummary(Catalog, Location.Root);

    /// <summary>
    /// Reads a summary from any catalog. The app uses this on the PC's copy of the catalog
    /// to show counts while the backup drive is unplugged.
    /// </summary>
    public static BackupSummary ReadSummary(Catalog catalog, string backupRoot)
    {
        DateTime? lastSync = DateTime.TryParse(catalog.GetMeta("last_sync_utc"), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var t) ? t : null;
        int? lastProblems = int.TryParse(catalog.GetMeta("last_sync_problems"), CultureInfo.InvariantCulture, out int p) ? p : null;
        long? free = null, total = null;
        string? root = Path.GetPathRoot(backupRoot);
        if (!string.IsNullOrEmpty(root) && !root.StartsWith(@"\\", StringComparison.Ordinal))
        {
            try
            {
                var drive = new DriveInfo(root);
                if (drive.IsReady)
                {
                    free = drive.AvailableFreeSpace;
                    total = drive.TotalSize;
                }
            }
            catch (IOException)
            {
            }
        }
        int health = int.TryParse(catalog.GetMeta("quick_check_issues"), CultureInfo.InvariantCulture, out int q) ? q : 0;
        if (catalog.HasVersion2)
            health += catalog.VerifiedStats(DateTime.UtcNow).Failed;
        return new BackupSummary(catalog.Totals(), lastSync, lastProblems, free, total, backupRoot) { HealthIssues = health };
    }

    static bool TryRelative(string full, string folder, out string relative)
    {
        folder = Path.TrimEndingDirectorySeparator(Path.GetFullPath(folder));
        relative = "";
        if (full.Equals(folder, StringComparison.OrdinalIgnoreCase))
            return true;
        string prefix = folder.EndsWith('\\') ? folder : folder + "\\";
        if (!full.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            return false;
        relative = full[prefix.Length..];
        return true;
    }

    static string NewStamp() => DateTime.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);

    void SetState(FileRecord record, FileState state, string action, string? detail = null)
    {
        record.State = state;
        record.StateChangedUtc = DateTime.UtcNow;
        Catalog.Save(record);
        Catalog.Log(action, record.Key, detail);
    }

    /// <summary>Moves a backup copy into <c>versions\&lt;stamp&gt;\&lt;key&gt;</c>, where it's kept until the user removes it.</summary>
    string MoveToVersions(string backupPath, string key, string stamp, string hash = "")
    {
        string destination = FsUtil.UniquePath(Path.Combine(Location.VersionsDir, stamp, key));
        var info = new FileInfo(backupPath);
        long size = info.Length, modified = info.LastWriteTimeUtc.Ticks;
        FsUtil.MoveFile(backupPath, destination);
        Catalog.AddVersion(new VersionRow(Path.GetRelativePath(Location.VersionsDir, destination), key, stamp, size, modified, hash));
        return destination;
    }

    /// <summary>Something to take out of the backup: a file or folder, and where it would sit inside the trash folder.</summary>
    sealed record Removal(string Id, string Path, string TrashRelative);

    /// <summary>
    /// The only way anything leaves the backup. Items go to the drive's Recycle Bin, or to <c>.drivesync\trash</c>
    /// when this backup always uses its trash folder, the drive has no bin, or the bin has no room. Nothing is erased.
    /// Returns, for each id, null when it's gone from the backup or the reason it couldn't be removed.
    /// <paramref name="fellBack"/> counts items meant for the Recycle Bin that went to the trash folder instead.
    /// </summary>
    Dictionary<string, string?> SendAway(IReadOnlyList<Removal> items, string stamp, out int fellBack)
    {
        fellBack = 0;
        var outcome = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        var present = new List<Removal>();
        foreach (var item in items)
        {
            if (FsUtil.Exists(item.Path))
                present.Add(item);
            else
                outcome[item.Id] = null;
        }
        if (present.Count == 0)
            return outcome;

        var toTrashFolder = new List<Removal>();
        bool useBin = Profile.RemovedFilesGoTo == RemovedFilesGoTo.RecycleBinThenTrashFolder && _bin.IsSupported(Location.Root);
        if (useBin)
        {
            var recycled = _bin.Recycle([.. present.Select(r => r.Path)]);
            foreach (var item in present)
            {
                switch (recycled[item.Path])
                {
                    case RecycleOutcome.Recycled:
                        outcome[item.Id] = null;
                        break;
                    case RecycleOutcome.NotSupported or RecycleOutcome.BinFull or RecycleOutcome.TooLarge:
                        // No room, too big, or a path too long for the bin: the trash folder takes it instead.
                        toTrashFolder.Add(item);
                        break;
                    case var other:
                        outcome[item.Id] = Explain(other, onBackupDrive: true);
                        break;
                }
            }
        }
        else
        {
            toTrashFolder.AddRange(present);
        }

        foreach (var item in toTrashFolder)
        {
            try
            {
                string destination = FsUtil.UniquePath(Path.Combine(Location.TrashDir, stamp, item.TrashRelative));
                if (Directory.Exists(item.Path))
                {
                    FsUtil.EnsureParent(destination);
                    Directory.Move(item.Path, destination);
                }
                else
                {
                    FsUtil.MoveFile(item.Path, destination);
                }
                outcome[item.Id] = null;
                if (useBin)
                    fellBack++;
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                outcome[item.Id] = $"Couldn't move this to the backup's trash folder: {e.Message}";
            }
        }
        return outcome;
    }

    /// <summary>Removes backup copies of files. See <see cref="SendAway"/>.</summary>
    Dictionary<string, string?> RemoveFromBackup(IReadOnlyList<FileRecord> records, string stamp, out int fellBack)
    {
        var outcome = SendAway([.. records.Select(r => new Removal(r.Key, Location.PathFor(r.Key), r.Key))], stamp, out fellBack);
        foreach (var record in records)
        {
            if (outcome[record.Key] is null)
                FsUtil.RemoveEmptyFoldersUpTo(Path.GetDirectoryName(Location.PathFor(record.Key)), Location.Root);
        }
        return outcome;
    }

    /// <summary>Where this backup's removed copies go, in words for a message.</summary>
    public string RemovedPlace => Profile.RemovedFilesGoTo == RemovedFilesGoTo.TrashFolder || !_bin.IsSupported(Location.Root)
        ? "the backup's trash folder"
        : "the backup drive's Recycle Bin";

    /// <summary>Sends PC folders that no longer hold any files to the Recycle Bin, after an offload or delete emptied them.</summary>
    void RecycleEmptyFolders(string key)
    {
        string path;
        try
        {
            path = SourcePathFor(key);
        }
        catch (DriveSyncException)
        {
            return;
        }
        if (!Directory.Exists(path))
            return;

        var empty = new List<string>();
        try
        {
            CollectEmptyTrees(path, canTakeSelf: KeyPath.Relative(key).Length > 0, empty);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return;
        }
        if (empty.Count > 0)
            _bin.Recycle(empty);
    }

    static void CollectEmptyTrees(string folder, bool canTakeSelf, List<string> into)
    {
        if (canTakeSelf && !Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories).Any())
        {
            into.Add(folder);
            return;
        }
        foreach (var sub in new DirectoryInfo(folder).EnumerateDirectories())
        {
            if (!sub.Attributes.HasFlag(FileAttributes.ReparsePoint))
                CollectEmptyTrees(sub.FullName, canTakeSelf: true, into);
        }
    }

    RecycledLookup PcRecycleBin()
    {
        var roots = Profile.Sources
            .Select(s => Path.GetPathRoot(s.Path))
            .Where(r => !string.IsNullOrEmpty(r) && !r.StartsWith(@"\\", StringComparison.Ordinal))
            .Distinct(StringComparer.OrdinalIgnoreCase);
        return new RecycledLookup(roots.SelectMany(r => _bin.List(r!)));
    }

    internal static string Explain(RecycleOutcome outcome, bool onBackupDrive) => outcome switch
    {
        RecycleOutcome.BinFull => "The Recycle Bin doesn't have room for this without Windows erasing older items. Empty the Recycle Bin or raise its size limit (Recycle Bin > Properties), then try again.",
        RecycleOutcome.TooLarge => "This is bigger than the Recycle Bin can hold. DriveSync never deletes files permanently, so it was left where it is.",
        RecycleOutcome.NotSupported when !onBackupDrive => "This drive has no Recycle Bin, so DriveSync left the file where it is. Remove it yourself if you're sure.",
        RecycleOutcome.NotSupported => "This drive has no Recycle Bin.",
        _ => "Windows couldn't move this to the Recycle Bin. It may be open in another program.",
    };

    /// <summary>Limits progress reports to about ten a second.</summary>
    sealed class ProgressThrottle(IProgress<SyncProgress>? target)
    {
        long _last;

        public void Report(SyncProgress progress, bool force = false)
        {
            if (target is null)
                return;
            long now = Environment.TickCount64;
            if (!force && now - _last < 100)
                return;
            _last = now;
            target.Report(progress);
        }
    }
}
