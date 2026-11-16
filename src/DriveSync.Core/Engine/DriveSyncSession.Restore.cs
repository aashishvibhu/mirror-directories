using DriveSync.Core.IO;
using DriveSync.Core.Model;
using DriveSync.Core.RecycleBin;

namespace DriveSync.Core.Engine;

public sealed partial class DriveSyncSession
{
    enum RestoreAction { Restore, AlreadyThere, InTheWay, MissingOnBackup }

    /// <summary>One file to restore: where its contents are now, and where they should go.</summary>
    sealed record RestoreItem(string Key, string From, string Target, long Size, FileRecord? Record);

    List<RestoreItem> RestoreItems(RestoreRequest request)
    {
        var items = new Dictionary<string, RestoreItem>(KeyPath.Comparer);
        foreach (string prefix in request.Paths.Select(ResolveKey).Distinct(KeyPath.Comparer))
        {
            switch (request.Source)
            {
                case RestoreSource.CurrentBackup:
                    foreach (var record in Catalog.Query(underKey: prefix).Where(r => r.State != FileState.Deleted))
                        items.TryAdd(record.Key, new RestoreItem(record.Key, Location.PathFor(record.Key), TargetFor(request, record.Key), record.Size, record));
                    break;
                case RestoreSource.OldVersion:
                    if (string.IsNullOrEmpty(request.VersionStamp))
                        throw new DriveSyncException("Pick which version to restore.");
                    foreach (var version in ListVersions(prefix).Where(v => v.Stamp == request.VersionStamp))
                        items.TryAdd(version.Key, new RestoreItem(version.Key, version.Path, TargetFor(request, version.Key), version.Size, null));
                    break;
                case RestoreSource.Deleted:
                    foreach (var deleted in ListDeleted(prefix))
                        items.TryAdd(deleted.Key, new RestoreItem(deleted.Key, deleted.DataPath, TargetFor(request, deleted.Key), deleted.Size, null));
                    break;
            }
        }
        return [.. items.Values];
    }

    string TargetFor(RestoreRequest request, string key) =>
        request.TargetFolder is null ? OriginalPlaceFor(key) : Path.Combine(request.TargetFolder, key);

    /// <summary>Where a key lived on the PC, including folders that were later taken out of this backup.</summary>
    string OriginalPlaceFor(string key)
    {
        if (_sources.ContainsKey(KeyPath.SourceName(key)))
            return SourcePathFor(key);
        if (RemovedFolderFor(key) is { Path.Length: > 0 } removed)
        {
            string relative = KeyPath.Relative(key);
            return relative.Length == 0 ? removed.Path : Path.Combine(removed.Path, relative);
        }
        throw new DriveSyncException($"DriveSync doesn't know where {KeyPath.SourceName(key)} was on this PC. Pick a folder to restore it to.");
    }

    static RestoreAction Classify(RestoreItem item)
    {
        var backup = new FileInfo(item.From);
        if (!backup.Exists)
            return RestoreAction.MissingOnBackup;
        var existing = new FileInfo(item.Target);
        if (!existing.Exists)
            return RestoreAction.Restore;
        // Backup drives formatted as exFAT or FAT32 round times (to 10 ms or 2 s), so allow for that.
        return existing.Length == backup.Length && (existing.LastWriteTimeUtc - backup.LastWriteTimeUtc).Duration() <= TimeSpan.FromSeconds(2)
            ? RestoreAction.AlreadyThere
            : RestoreAction.InTheWay;
    }

    /// <summary>Works out what a restore would do. Changes nothing.</summary>
    public RestorePlan PlanRestore(RestoreRequest request)
    {
        var plan = new RestorePlan();
        foreach (var item in RestoreItems(request))
        {
            switch (Classify(item))
            {
                case RestoreAction.MissingOnBackup:
                    plan.MissingOnBackup++;
                    break;
                case RestoreAction.AlreadyThere:
                    plan.AlreadyThere++;
                    break;
                case RestoreAction.InTheWay:
                    plan.InTheWay++;
                    plan.ToRestore++;
                    plan.Bytes += item.Size;
                    break;
                default:
                    plan.ToRestore++;
                    plan.Bytes += item.Size;
                    break;
            }
        }
        return plan;
    }

    /// <summary>
    /// Copies files from the backup back to the PC (or to <see cref="RestoreRequest.TargetFolder"/>).
    /// A different file already in the way is renamed, never replaced.
    /// </summary>
    public async Task<RestoreResult> RestoreAsync(RestoreRequest request, IProgress<SyncProgress>? progress = null, CancellationToken ct = default, SyncControl? control = null)
    {
        var result = new RestoreResult();
        var report = new ProgressThrottle(progress);
        var items = RestoreItems(request);
        if (items.Count == 0)
            result.Problems.Add(new Problem(string.Join(", ", request.Paths), "There's nothing to restore here."));

        var work = new List<RestoreItem>();
        foreach (var item in items)
        {
            switch (Classify(item))
            {
                case RestoreAction.MissingOnBackup:
                    result.Problems.Add(new Problem(item.Key, "The backup copy of this file is missing."));
                    break;
                case RestoreAction.AlreadyThere:
                    result.AlreadyThere++;
                    if (IsOriginalPlace(request))
                        MarkBackOnPc(item.Key);
                    break;
                default:
                    work.Add(item);
                    break;
            }
        }
        EnsureRoomOnPc(work);

        long bytesTotal = work.Sum(i => i.Size), bytesDone = 0;
        for (int i = 0; i < work.Count; i++)
        {
            if (control is { IsPaused: true })
            {
                report.Report(new SyncProgress("Paused", i, work.Count, bytesDone, bytesTotal, null), force: true);
                await control.WaitIfPausedAsync(ct);
            }
            ct.ThrowIfCancellationRequested();
            var item = work[i];
            int filesDone = i;
            report.Report(new SyncProgress("Restoring", filesDone, work.Count, bytesDone, bytesTotal, item.Key));
            try
            {
                if (File.Exists(item.Target))
                {
                    File.Move(item.Target, FsUtil.UniquePath(AsidePath(item.Target)));
                    result.KeptBoth++;
                }

                FsUtil.EnsureParent(item.Target);
                string temp = item.Target + FsUtil.TempSuffix;
                try
                {
                    string hash = await FileHasher.CopyWithHashAsync(item.From, temp, n =>
                    {
                        bytesDone += n;
                        report.Report(new SyncProgress("Restoring", filesDone, work.Count, bytesDone, bytesTotal, item.Key));
                    }, ct);
                    if (item.Record is { Hash.Length: > 0 } known)
                    {
                        if (!known.Hash.Equals(hash, StringComparison.OrdinalIgnoreCase))
                        {
                            result.Problems.Add(new Problem(item.Key,
                                "The backup copy no longer matches its checksum, so it may be damaged. It was restored anyway; check the file, and run a full health check."));
                        }
                        else if (IsOriginalPlace(request))
                        {
                            // Give the file the exact time on record, so the next sync sees it as unchanged
                            // even when the backup drive rounds times.
                            File.SetLastWriteTimeUtc(temp, new DateTime(known.ModifiedUtcTicks, DateTimeKind.Utc));
                        }
                    }
                    File.Move(temp, item.Target);
                }
                catch
                {
                    FsUtil.TryRemoveOwnTemp(temp);
                    throw;
                }
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                result.Problems.Add(new Problem(item.Key, $"Couldn't restore this file: {e.Message}"));
                continue;
            }

            result.Restored++;
            result.BytesRestored += item.Size;
            result.RestoredPaths.Add(item.Target);
            Catalog.Log("restored", item.Key, request.Source.ToString());
            if (IsOriginalPlace(request))
                MarkBackOnPc(item.Key);
        }
        report.Report(new SyncProgress("Done", work.Count, work.Count, bytesDone, bytesTotal, null), force: true);
        return result;
    }

    static bool IsOriginalPlace(RestoreRequest request) => request.TargetFolder is null && request.Source == RestoreSource.CurrentBackup;

    /// <summary>Stops before copying anything if a drive on the PC doesn't have room for what's coming back.</summary>
    static void EnsureRoomOnPc(List<RestoreItem> work)
    {
        foreach (var group in work.GroupBy(i => Path.GetPathRoot(Path.GetFullPath(i.Target)) ?? "", StringComparer.OrdinalIgnoreCase))
        {
            if (group.Key.Length == 0 || group.Key.StartsWith(@"\\", StringComparison.Ordinal))
                continue;
            long needed = group.Sum(i => i.Size), free;
            try
            {
                free = new DriveInfo(group.Key).AvailableFreeSpace;
            }
            catch (Exception e) when (e is IOException or ArgumentException)
            {
                continue;
            }
            if (needed > free)
            {
                throw new DriveSyncException(
                    $"Restoring needs {Formatting.Size(needed)} on {group.Key}, but only {Formatting.Size(free)} is free. Free up space or restore fewer files.");
            }
        }
    }

    /// <summary>Files deleted on purpose at or under <paramref name="path"/> (everywhere when null), and where their backup copies are now.</summary>
    public IReadOnlyList<DeletedEntry> ListDeleted(string? path = null)
    {
        string? prefix = path is null ? null : ResolveKey(path);
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
        if (!_sources.ContainsKey(KeyPath.SourceName(key)))
            return;
        var record = Catalog.Get(key);
        if (record is { State: FileState.Archived or FileState.Pending })
            SetState(record, FileState.Active, "back-on-pc", "restored");
    }

    static string AsidePath(string path) =>
        Path.Combine(Path.GetDirectoryName(path)!,
            $"{Path.GetFileNameWithoutExtension(path)} (before restore {DateTime.Now:yyyy-MM-dd HHmm}){Path.GetExtension(path)}");
}
