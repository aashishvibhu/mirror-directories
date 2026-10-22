using DriveSync.Core.IO;
using DriveSync.Core.Model;
using DriveSync.Core.RecycleBin;
using DriveSync.Core.Scanning;

namespace DriveSync.Core.Engine;

public sealed partial class DriveSyncSession
{
    /// <summary>
    /// Frees space on the PC while keeping files on the backup. Each file is backed up first if needed, then both
    /// copies are checksummed; only when they match is the PC copy sent to the Recycle Bin and the file marked Archived.
    /// </summary>
    public async Task<OffloadResult> OffloadAsync(IEnumerable<string> paths, IProgress<SyncProgress>? progress = null, CancellationToken ct = default)
    {
        var result = new OffloadResult();
        var report = new ProgressThrottle(progress);
        string stamp = NewStamp();
        var targets = paths.Select(ResolveKey).Distinct(KeyPath.Comparer).ToList();
        var scan = ScanTargets(targets, ct);
        result.CloudOnlySkipped = scan.CloudOnlySkipped;

        using (Catalog.Batch())
        {
            var files = scan.Files.Values.ToList();
            var verified = new List<(ScannedFile File, FileRecord Record)>();
            for (int i = 0; i < files.Count; i++)
            {
                ct.ThrowIfCancellationRequested();
                var file = files[i];
                report.Report(new SyncProgress("Checking backup copies", i, files.Count, 0, 0, file.Key));
                var record = Catalog.Get(file.Key);
                if (record is null || record.State != FileState.Active || record.Size != file.Size || record.ModifiedUtcTicks != file.ModifiedUtcTicks)
                {
                    (_, record) = await BackUpFileAsync(file, stamp, result.Problems, null, ct);
                    if (record is null)
                        continue;
                    result.BackedUpFirst++;
                }
                if (!await Task.Run(() => BothCopiesMatch(file, record), ct))
                {
                    result.Problems.Add(new Problem(file.Key,
                        "The backup copy doesn't match the file on this PC, so the file was left where it is. Run a sync, then try again."));
                    continue;
                }
                verified.Add((file, record));
            }

            if (verified.Count > 0)
            {
                report.Report(new SyncProgress("Moving to the Recycle Bin", 0, verified.Count, 0, 0, null), force: true);
                var outcomes = _bin.Recycle([.. verified.Select(v => v.File.FullPath)]);
                foreach (var (file, record) in verified)
                {
                    var outcome = outcomes[file.FullPath];
                    if (outcome == RecycleOutcome.Recycled)
                    {
                        SetState(record, FileState.Archived, "offloaded");
                        result.Offloaded++;
                        result.BytesMovedToRecycleBin += file.Size;
                    }
                    else
                    {
                        result.Problems.Add(new Problem(file.Key, Explain(outcome, onBackupDrive: false)));
                    }
                }
            }
        }

        foreach (string key in targets)
            RecycleEmptyFolders(key);
        return result;
    }

    /// <summary>
    /// Removes files on purpose from both the PC and the backup. The PC copy goes to the PC's Recycle Bin first;
    /// only if that worked does the backup copy go to the drive's Recycle Bin.
    /// </summary>
    public DeleteResult DeleteEverywhere(IEnumerable<string> paths)
    {
        var result = new DeleteResult();
        var targets = paths.Select(ResolveKey).Distinct(KeyPath.Comparer).ToList();
        var scan = ScanTargets(targets, CancellationToken.None);
        var records = new Dictionary<string, FileRecord>(KeyPath.Comparer);
        foreach (string target in targets)
        {
            foreach (var record in Catalog.Query(underKey: target))
            {
                if (record.State != FileState.Deleted)
                    records.TryAdd(record.Key, record);
            }
        }

        using (Catalog.Batch())
        {
            var stillOnPc = new HashSet<string>(KeyPath.Comparer);
            var pcFiles = scan.Files.Values.ToList();
            if (pcFiles.Count > 0)
            {
                var outcomes = _bin.Recycle([.. pcFiles.Select(f => f.FullPath)]);
                foreach (var file in pcFiles)
                {
                    var outcome = outcomes[file.FullPath];
                    if (outcome == RecycleOutcome.Recycled)
                    {
                        if (!records.ContainsKey(file.Key))
                            result.Deleted++; // never backed up, so it's only in the PC's Recycle Bin
                        continue;
                    }
                    stillOnPc.Add(file.Key);
                    result.Problems.Add(new Problem(file.Key, Explain(outcome, onBackupDrive: false)));
                }
            }

            // Only remove backup copies whose PC copy is now gone: recycled just now, or offloaded earlier.
            // Anything still on the PC (couldn't be recycled, cloud-only, unreadable) keeps its backup.
            var fromBackup = records.Values
                .Where(r => !stillOnPc.Contains(r.Key) && !File.Exists(SourcePathFor(r.Key)))
                .ToList();
            foreach (var (key, problem) in RemoveFromBackup(fromBackup, NewStamp()))
            {
                var record = records[key];
                if (problem is null)
                {
                    SetState(record, FileState.Deleted, "deleted-everywhere");
                    result.Deleted++;
                }
                else
                {
                    SetState(record, FileState.Pending, "missing-from-pc", "the backup copy couldn't be removed");
                    result.Problems.Add(new Problem(key, "Moved to the Recycle Bin on this PC, but the backup copy couldn't be removed. " + problem));
                }
            }
        }

        foreach (string key in targets)
            RecycleEmptyFolders(key);
        return result;
    }

    ScanResult ScanTargets(IEnumerable<string> keys, CancellationToken ct)
    {
        var scan = new ScanResult();
        var scanner = new SourceScanner(_exclusions, Profile.SkipCloudOnlyFiles);
        foreach (string key in keys)
            scanner.ScanSubtree(_sources[KeyPath.SourceName(key)], KeyPath.Relative(key), scan, null, ct);
        return scan;
    }

    bool BothCopiesMatch(ScannedFile file, FileRecord record)
    {
        try
        {
            string backup = Location.PathFor(file.Key);
            return File.Exists(backup)
                && FileHasher.HashFile(backup).Equals(record.Hash, StringComparison.OrdinalIgnoreCase)
                && FileHasher.HashFile(file.FullPath).Equals(record.Hash, StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }
}
