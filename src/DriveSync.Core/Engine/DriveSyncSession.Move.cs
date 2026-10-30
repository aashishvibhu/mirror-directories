using DriveSync.Core.IO;
using DriveSync.Core.Model;
using DriveSync.Core.Storage;

namespace DriveSync.Core.Engine;

public sealed partial class DriveSyncSession
{
    const string MovingMarker = "moving";

    /// <summary>
    /// Copies this whole backup (files, old versions, trash and catalog) to a folder on <paramref name="target"/>,
    /// checking every backed-up file against its checksum as it's copied. Only if everything matches does the copy
    /// get its own identity, ready to take over. The old backup is never changed or deleted.
    /// The caller then points the backup at <see cref="MoveResult.NewRoot"/> and <see cref="MoveResult.NewDriveId"/>.
    /// </summary>
    public async Task<MoveResult> MoveToAsync(string target, IReadOnlyList<BackupProfile> allBackups, IProgress<SyncProgress>? progress = null, CancellationToken ct = default)
    {
        string targetPath = Path.TrimEndingDirectorySeparator(Path.GetFullPath(target));
        if (!Directory.Exists(targetPath))
            throw new DriveSyncException($"{targetPath} doesn't exist. Check that the drive is plugged in.");
        if (BackupSetup.IsSameOrInside(targetPath, Location.Root) || BackupSetup.IsSameOrInside(Location.Root, targetPath))
            throw new DriveSyncException("Pick a different drive or folder from the one this backup is on now.");

        string newRoot = Path.Combine(targetPath, Profile.BackupFolderName);
        foreach (var source in Profile.Sources)
        {
            if (BackupSetup.IsSameOrInside(newRoot, source.Path) || BackupSetup.IsSameOrInside(source.Path, newRoot))
                throw new DriveSyncException($"That's inside {source.Path}, which this backup copies. Pick a drive or folder outside your backed-up folders.");
        }
        newRoot = ChooseMoveRoot(targetPath);
        BackupSetup.CheckDestination(newRoot, allBackups, except: Profile);

        var files = ListFilesToMove();
        long bytesTotal = files.Sum(f => f.Length);
        string? drive = Path.GetPathRoot(newRoot);
        if (!string.IsNullOrEmpty(drive) && !drive.StartsWith(@"\\", StringComparison.Ordinal))
        {
            long free = new DriveInfo(drive).AvailableFreeSpace;
            if (bytesTotal > free)
                throw new DriveSyncException($"This backup takes {Formatting.Size(bytesTotal)}, but {drive} has only {Formatting.Size(free)} free.");
        }

        string newMeta = Path.Combine(newRoot, BackupLocation.MetaFolderName);
        Directory.CreateDirectory(newMeta);
        File.WriteAllText(Path.Combine(newMeta, MovingMarker), $"A copy of the DriveSync backup \"{Profile.Name}\" that hasn't finished. Safe to delete.");
        Catalog.Log("moving", null, "to " + newRoot);

        var report = new ProgressThrottle(progress);
        var records = Catalog.LoadAll();
        var problems = new List<Problem>();
        long bytesDone = 0;
        int copied = 0;
        foreach (var file in files)
        {
            ct.ThrowIfCancellationRequested();
            string relative = Path.GetRelativePath(Location.Root, file.FullName);
            string destination = Path.Combine(newRoot, relative);
            string temp = destination + FsUtil.TempSuffix;
            report.Report(new SyncProgress("Copying to the new drive", copied, files.Count, bytesDone, bytesTotal, relative));
            try
            {
                FsUtil.EnsureParent(destination);
                string hash = await FileHasher.CopyWithHashAsync(file.FullName, temp, n =>
                {
                    bytesDone += n;
                    report.Report(new SyncProgress("Copying to the new drive", copied, files.Count, bytesDone, bytesTotal, relative));
                }, ct);
                if (records.TryGetValue(relative, out var record) && record.State != FileState.Deleted && record.Hash.Length > 0
                    && !record.Hash.Equals(hash, StringComparison.OrdinalIgnoreCase))
                {
                    FsUtil.TryRemoveOwnTemp(temp);
                    problems.Add(new Problem(relative, "This file on the current drive doesn't match its checksum, so it can't be copied safely. Run a full health check first."));
                    continue;
                }
                File.Move(temp, destination, overwrite: true);
                copied++;
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or OperationCanceledException)
            {
                FsUtil.TryRemoveOwnTemp(temp);
                if (e is OperationCanceledException)
                    throw;
                problems.Add(new Problem(relative, $"Couldn't copy this file: {e.Message}"));
            }
        }

        if (problems.Count > 0)
            return new MoveResult(false, newRoot, "", copied, bytesDone, problems);

        Catalog.Log("moved", null, "to " + newRoot);
        Catalog.CopyTo(Path.Combine(newMeta, "catalog.db"));
        string newId = Guid.NewGuid().ToString("D");
        File.WriteAllText(Path.Combine(newMeta, "drive.id"), newId);
        File.Delete(Path.Combine(newMeta, MovingMarker)); // DriveSync's own marker, written above
        report.Report(new SyncProgress("Done", copied, files.Count, bytesDone, bytesTotal, null), force: true);
        return new MoveResult(true, newRoot, newId, copied, bytesDone, problems);
    }

    /// <summary>Everything in the backup folder except the parts that belong to this drive: its id, lock, catalog file and unfinished copies.</summary>
    List<FileInfo> ListFilesToMove()
    {
        string meta = Location.MetaDir;
        var skip = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            Location.LockPath, Location.DriveIdPath, Location.CatalogPath, Location.CatalogPath + "-journal",
            Path.Combine(meta, MovingMarker),
        };
        return [.. new DirectoryInfo(Location.Root)
            .EnumerateFiles("*", new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = false, AttributesToSkip = 0 })
            .Where(f => !skip.Contains(f.FullName) && !f.Name.EndsWith(FsUtil.TempSuffix, StringComparison.OrdinalIgnoreCase))];
    }

    /// <summary>The backup's folder name on the new drive, or a numbered one if taken. An unfinished copy of an earlier move is reused.</summary>
    string ChooseMoveRoot(string targetPath)
    {
        string name = Profile.BackupFolderName;
        for (int i = 1; ; i++)
        {
            string candidate = Path.Combine(targetPath, i == 1 ? name : $"{name}-{i}");
            string meta = Path.Combine(candidate, BackupLocation.MetaFolderName);
            if (!Directory.Exists(candidate))
                return candidate;
            if (File.Exists(Path.Combine(meta, MovingMarker)) && !File.Exists(Path.Combine(meta, "drive.id")))
                return candidate;
        }
    }
}
