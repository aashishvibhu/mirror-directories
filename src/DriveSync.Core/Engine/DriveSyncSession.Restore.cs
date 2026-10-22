using DriveSync.Core.IO;
using DriveSync.Core.Model;
using DriveSync.Core.RecycleBin;
using DriveSync.Core.Scanning;

namespace DriveSync.Core.Engine;

public sealed partial class DriveSyncSession
{
    /// <summary>
    /// Copies files from the backup back to the PC (or to <see cref="RestoreRequest.TargetFolder"/>).
    /// A different file already in the way is renamed, never replaced.
    /// </summary>
    public async Task<RestoreResult> RestoreAsync(RestoreRequest request, IProgress<SyncProgress>? progress = null, CancellationToken ct = default)
    {
        var result = new RestoreResult();
        var report = new ProgressThrottle(progress);
        var items = new Dictionary<string, string>(KeyPath.Comparer); // key -> where its contents are now
        foreach (string prefix in request.Paths.Select(ResolveKey).Distinct(KeyPath.Comparer))
        {
            switch (request.Source)
            {
                case RestoreSource.CurrentBackup:
                    foreach (var record in Catalog.Query(underKey: prefix).Where(r => r.State != FileState.Deleted))
                        items.TryAdd(record.Key, Location.PathFor(record.Key));
                    break;
                case RestoreSource.OldVersion:
                    if (string.IsNullOrEmpty(request.VersionStamp))
                        throw new DriveSyncException("Pick which version to restore.");
                    foreach (var version in ListVersions(prefix).Where(v => v.Stamp == request.VersionStamp))
                        items.TryAdd(version.Key, version.Path);
                    break;
                case RestoreSource.Deleted:
                    foreach (var deleted in ListDeleted(prefix))
                        items.TryAdd(deleted.Key, deleted.DataPath);
                    break;
            }
        }
        if (items.Count == 0)
            result.Problems.Add(new Problem(string.Join(", ", request.Paths), "There's nothing to restore here."));

        int done = 0;
        foreach (var (key, from) in items)
        {
            ct.ThrowIfCancellationRequested();
            report.Report(new SyncProgress("Restoring", done++, items.Count, 0, 0, key));
            if (!File.Exists(from))
            {
                result.Problems.Add(new Problem(key, "The backup copy of this file is missing."));
                continue;
            }

            string target = request.TargetFolder is null ? SourcePathFor(key) : Path.Combine(request.TargetFolder, key);
            bool toOriginalPlace = request.TargetFolder is null && request.Source == RestoreSource.CurrentBackup;
            try
            {
                if (File.Exists(target))
                {
                    var existing = new FileInfo(target);
                    var backup = new FileInfo(from);
                    if (existing.Length == backup.Length && existing.LastWriteTimeUtc == backup.LastWriteTimeUtc)
                    {
                        result.AlreadyThere++;
                        if (toOriginalPlace)
                            MarkBackOnPc(key);
                        continue;
                    }
                    File.Move(target, FsUtil.UniquePath(AsidePath(target)));
                    result.KeptBoth++;
                }

                FsUtil.EnsureParent(target);
                string temp = target + FsUtil.TempSuffix;
                try
                {
                    await FileHasher.CopyWithHashAsync(from, temp, null, ct);
                    File.Move(temp, target);
                }
                catch
                {
                    FsUtil.TryRemoveOwnTemp(temp);
                    throw;
                }
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                result.Problems.Add(new Problem(key, $"Couldn't restore this file: {e.Message}"));
                continue;
            }

            result.Restored++;
            result.RestoredPaths.Add(target);
            Catalog.Log("restored", key, request.Source.ToString());
            if (toOriginalPlace)
                MarkBackOnPc(key);
        }
        return result;
    }

    /// <summary>Older versions of the files at or under <paramref name="path"/>, newest first.</summary>
    public IReadOnlyList<VersionEntry> ListVersions(string path)
    {
        string prefix = ResolveKey(path);
        var versions = new List<VersionEntry>();
        if (!Directory.Exists(Location.VersionsDir))
            return versions;
        foreach (string stampDir in Directory.EnumerateDirectories(Location.VersionsDir).OrderByDescending(d => d, StringComparer.Ordinal))
        {
            string stamp = Path.GetFileName(stampDir);
            string candidate = Path.Combine(stampDir, prefix);
            if (File.Exists(candidate))
                versions.Add(Version(stamp, prefix, candidate));
            else if (Directory.Exists(candidate))
            {
                foreach (string file in Directory.EnumerateFiles(candidate, "*", SearchOption.AllDirectories))
                    versions.Add(Version(stamp, KeyPath.Combine(prefix, Path.GetRelativePath(candidate, file)), file));
            }
        }
        return versions;

        static VersionEntry Version(string stamp, string key, string file)
        {
            var info = new FileInfo(file);
            return new VersionEntry(stamp, key, info.Length, info.LastWriteTimeUtc, file);
        }
    }

    /// <summary>Files deleted on purpose at or under <paramref name="path"/>, and where their backup copies are now.</summary>
    public IReadOnlyList<DeletedEntry> ListDeleted(string path)
    {
        string prefix = ResolveKey(path);
        var deleted = new List<DeletedEntry>();
        var records = Catalog.Query(FileState.Deleted, prefix);
        if (records.Count == 0)
            return deleted;

        string? driveRoot = Path.GetPathRoot(Location.Root);
        var bin = new RecycledLookup(string.IsNullOrEmpty(driveRoot) ? [] : _bin.List(driveRoot));
        var trashStamps = Directory.Exists(Location.TrashDir)
            ? Directory.GetDirectories(Location.TrashDir).OrderByDescending(d => d, StringComparer.Ordinal).ToList()
            : [];
        foreach (var record in records)
        {
            if (bin.Find(Location.PathFor(record.Key)) is { } hit && File.Exists(hit.DataPath))
            {
                deleted.Add(new DeletedEntry(record.Key, record.Size, hit.Item.DeletedUtc, InRecycleBin: true, hit.DataPath));
                continue;
            }
            foreach (string stampDir in trashStamps)
            {
                string candidate = Path.Combine(stampDir, record.Key);
                if (File.Exists(candidate))
                {
                    deleted.Add(new DeletedEntry(record.Key, record.Size, record.StateChangedUtc, InRecycleBin: false, candidate));
                    break;
                }
            }
        }
        return deleted;
    }

    void MarkBackOnPc(string key)
    {
        var record = Catalog.Get(key);
        if (record is { State: FileState.Archived or FileState.Pending })
            SetState(record, FileState.Active, "back-on-pc", "restored");
    }

    static string AsidePath(string path) =>
        Path.Combine(Path.GetDirectoryName(path)!,
            $"{Path.GetFileNameWithoutExtension(path)} (before restore {DateTime.Now:yyyy-MM-dd HHmm}){Path.GetExtension(path)}");

    /// <summary>
    /// Checks every backup copy against the catalog: quick (exists, right size) or full (checksum).
    /// With <paramref name="repair"/>, recopies damaged files the PC still has; the damaged copy is moved to versions, not removed.
    /// </summary>
    public async Task<HealthReport> CheckHealthAsync(bool full, bool repair = false, IProgress<SyncProgress>? progress = null, CancellationToken ct = default)
    {
        var report = new HealthReport { FullCheck = full };
        var throttle = new ProgressThrottle(progress);
        var records = Catalog.LoadAll();
        var live = records.Values.Where(r => r.State != FileState.Deleted).ToList();
        long bytesTotal = full ? live.Sum(r => r.Size) : 0, bytesDone = 0;

        await Task.Run(() =>
        {
            foreach (var record in live)
            {
                ct.ThrowIfCancellationRequested();
                throttle.Report(new SyncProgress("Checking backup copies", report.Checked, live.Count, bytesDone, bytesTotal, record.Key));
                var info = new FileInfo(Location.PathFor(record.Key));
                if (!info.Exists)
                    report.Issues.Add(new HealthIssue(record, HealthProblem.Missing));
                else if (info.Length != record.Size)
                    report.Issues.Add(new HealthIssue(record, HealthProblem.WrongSize));
                else if (full && !FileHasher.HashFile(info.FullName, ct).Equals(record.Hash, StringComparison.OrdinalIgnoreCase))
                    report.Issues.Add(new HealthIssue(record, HealthProblem.Damaged));
                report.Checked++;
                bytesDone += full ? record.Size : 0;
            }

            foreach (var source in Profile.Sources)
            {
                string folder = Path.Combine(Location.Root, source.Name);
                if (!Directory.Exists(folder))
                    continue;
                foreach (string file in Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories))
                {
                    if (file.EndsWith(FsUtil.TempSuffix, StringComparison.OrdinalIgnoreCase))
                    {
                        report.LeftoverTempFiles.Add(file);
                        continue;
                    }
                    string key = Path.GetRelativePath(Location.Root, file);
                    if (!records.TryGetValue(key, out var record) || record.State == FileState.Deleted)
                        report.Untracked.Add(key);
                }
            }
        }, ct);

        if (!repair)
            return report;

        string stamp = NewStamp();
        using (Catalog.Batch())
        {
            foreach (var issue in report.Issues)
            {
                var record = issue.Record;
                if (record.State != FileState.Active)
                {
                    issue.Note = "This file is no longer on the PC, so the backup copy was the only one. It can't be repaired.";
                    continue;
                }
                string source = SourcePathFor(record.Key);
                var info = new FileInfo(source);
                if (!info.Exists || info.Length != record.Size || info.LastWriteTimeUtc.Ticks != record.ModifiedUtcTicks)
                {
                    issue.Note = "The file on the PC has changed or is gone. Run a sync to back up the current version.";
                    continue;
                }
                string backup = Location.PathFor(record.Key);
                if (File.Exists(backup))
                    MoveToVersions(backup, record.Key, stamp);
                var problems = new List<Problem>();
                var (_, repaired) = await BackUpFileAsync(new ScannedFile(record.Key, source, info.Length, record.ModifiedUtcTicks), stamp, problems, null, ct);
                if (repaired is not null)
                {
                    issue.Repaired = true;
                    report.Repaired++;
                }
                else
                {
                    issue.Note = problems.FirstOrDefault()?.Message;
                }
            }
        }
        foreach (string temp in report.LeftoverTempFiles)
            FsUtil.TryRemoveOwnTemp(temp);
        return report;
    }
}
