using System.Globalization;
using DriveSync.Core.IO;
using DriveSync.Core.Model;
using DriveSync.Core.Scanning;

namespace DriveSync.Core.Engine;

public sealed partial class DriveSyncSession
{
    enum CopyOutcome { Copied, Updated, Touched, Failed }

    /// <summary>Works out what a sync would do, then does it.</summary>
    public async Task<SyncResult> SyncAsync(IProgress<SyncProgress>? progress = null, CancellationToken ct = default) =>
        await ApplyAsync(await PlanSyncAsync(progress, ct), progress, ct);

    /// <summary>
    /// Compares the PC with the catalog. Reads only names, sizes and dates, except for new files whose size
    /// matches a missing one: those are checksummed to spot moves and renames. Changes nothing.
    /// </summary>
    public Task<SyncPlan> PlanSyncAsync(IProgress<SyncProgress>? progress = null, CancellationToken ct = default) =>
        Task.Run(() => PlanSync(new ProgressThrottle(progress), ct), ct);

    SyncPlan PlanSync(ProgressThrottle report, CancellationToken ct)
    {
        var scanner = new SourceScanner(_exclusions, Profile.SkipCloudOnlyFiles);
        var scan = scanner.Scan(Profile.Sources, n => report.Report(new SyncProgress("Scanning", n, 0, 0, 0, null)), ct);
        var records = Catalog.LoadAll();
        var plan = new SyncPlan(scan);

        foreach (var file in scan.Files.Values)
        {
            if (!records.TryGetValue(file.Key, out var record) || record.State == FileState.Deleted)
                plan.NewFiles.Add(file);
            else if (record.Size == file.Size && record.ModifiedUtcTicks == file.ModifiedUtcTicks)
            {
                if (record.State == FileState.Active)
                    plan.UnchangedCount++;
                else
                    plan.BackOnPc.Add(record);
            }
            else
                plan.ChangedFiles.Add(file);
        }

        int active = 0;
        var missing = new List<FileRecord>();
        foreach (var record in records.Values)
        {
            if (record.State == FileState.Active)
                active++;
            if (record.State is not (FileState.Active or FileState.Pending) || scan.Files.ContainsKey(record.Key))
                continue;
            if (!_sources.ContainsKey(KeyPath.SourceName(record.Key)) || scan.IsBlind(record.Key) || _exclusions.IsExcludedKey(record.Key))
                continue;
            missing.Add(record);
        }

        DetectMoves(plan, missing, report, ct);

        var recycleBin = missing.Count > 0 ? PcRecycleBin() : null;
        foreach (var record in missing)
            plan.Missing.Add(new MissingFile(record, recycleBin?.Find(SourcePathFor(record.Key)) is not null));

        int newlyMissing = plan.Missing.Count(m => m.Record.State == FileState.Active);
        plan.MassDisappearance = newlyMissing >= Profile.MassDisappearanceMinFiles
            && active > 0
            && newlyMissing * 100L >= (long)Profile.MassDisappearancePercent * active;
        plan.BytesToCopy = plan.NewFiles.Sum(f => f.Size) + plan.ChangedFiles.Sum(f => f.Size);
        return plan;
    }

    void DetectMoves(SyncPlan plan, List<FileRecord> missing, ProgressThrottle report, CancellationToken ct)
    {
        var bySize = missing
            .Where(r => r.Size > 0 && r.Hash.Length > 0)
            .GroupBy(r => r.Size)
            .ToDictionary(g => g.Key, g => g.ToList());
        if (bySize.Count == 0)
            return;

        var movedTo = new HashSet<string>(KeyPath.Comparer);
        var movedFrom = new HashSet<string>(KeyPath.Comparer);
        foreach (var file in plan.NewFiles)
        {
            if (!bySize.TryGetValue(file.Size, out var candidates) || candidates.Count == 0)
                continue;
            report.Report(new SyncProgress("Checking moved files", 0, 0, 0, 0, file.Key));
            string hash;
            try
            {
                hash = FileHasher.HashFile(file.FullPath, ct);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                continue;
            }
            int match = candidates.FindIndex(c => c.Hash.Equals(hash, StringComparison.OrdinalIgnoreCase));
            if (match < 0)
                continue;
            var from = candidates[match];
            candidates.RemoveAt(match);
            plan.Moves.Add(new PlannedMove(from, file, hash));
            movedTo.Add(file.Key);
            movedFrom.Add(from.Key);
        }
        plan.NewFiles.RemoveAll(f => movedTo.Contains(f.Key));
        missing.RemoveAll(r => movedFrom.Contains(r.Key));
    }

    /// <summary>Carries out a plan from <see cref="PlanSyncAsync"/>.</summary>
    public async Task<SyncResult> ApplyAsync(SyncPlan plan, IProgress<SyncProgress>? progress = null, CancellationToken ct = default, SyncControl? control = null)
    {
        var started = DateTime.UtcNow;
        var report = new ProgressThrottle(progress);
        var result = new SyncResult { MassDisappearance = plan.MassDisappearance };
        result.Warnings.AddRange(plan.Scan.Warnings);
        string stamp = NewStamp();
        EnsureFreeSpace(plan.BytesToCopy);

        using (Catalog.Batch())
        {
            foreach (var record in plan.BackOnPc)
            {
                SetState(record, FileState.Active, "back-on-pc");
                result.BackOnPc++;
            }

            var copies = new List<ScannedFile>(plan.NewFiles.Concat(plan.ChangedFiles));
            foreach (var move in plan.Moves)
            {
                if (TryApplyMove(move, stamp))
                    result.Moved++;
                else
                    copies.Add(move.To);
            }

            long bytesTotal = copies.Sum(f => f.Size), bytesDone = 0;
            for (int i = 0; i < copies.Count; i++)
            {
                if (control is { IsPaused: true })
                {
                    report.Report(new SyncProgress("Paused", i, copies.Count, bytesDone, bytesTotal, null), force: true);
                    await control.WaitIfPausedAsync(ct);
                }
                ct.ThrowIfCancellationRequested();
                var file = copies[i];
                int filesDone = i;
                report.Report(new SyncProgress("Copying", filesDone, copies.Count, bytesDone, bytesTotal, file.Key));
                var (outcome, _) = await BackUpFileAsync(file, stamp, result.Problems, n =>
                {
                    bytesDone += n;
                    report.Report(new SyncProgress("Copying", filesDone, copies.Count, bytesDone, bytesTotal, file.Key));
                }, ct);
                switch (outcome)
                {
                    case CopyOutcome.Copied: result.Copied++; result.BytesCopied += file.Size; break;
                    case CopyOutcome.Updated: result.Updated++; result.BytesCopied += file.Size; break;
                    case CopyOutcome.Touched: result.Touched++; break;
                }
                if (i % 200 == 199)
                    Catalog.Checkpoint();
            }

            ApplyMissingPolicy(plan, result, stamp);
            Catalog.SetMeta("last_sync_utc", DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture));
            Catalog.SetMeta("last_sync_problems", result.Problems.Count.ToString(CultureInfo.InvariantCulture));
            Catalog.Log("sync", null, Describe(result));
        }

        result.PendingTotal = Catalog.Count(FileState.Pending);
        result.Duration = DateTime.UtcNow - started;
        report.Report(new SyncProgress("Done", 0, 0, 0, 0, null), force: true);
        return result;
    }

    static string Describe(SyncResult r)
    {
        var parts = new List<string>();
        void Add(int n, string what)
        {
            if (n > 0)
                parts.Add($"{n:N0} {what}");
        }
        Add(r.Copied, "new");
        Add(r.Updated, "updated");
        Add(r.Moved, "moved");
        Add(r.BackOnPc, "back on PC");
        Add(r.KeptOnBackup, "kept on backup");
        Add(r.RemovedFromBackup, "sent to Recycle Bin");
        Add(r.WaitingForDecision, "waiting for a decision");
        Add(r.Problems.Count, "problems");
        return parts.Count == 0 ? "No changes" : string.Join(", ", parts);
    }

    bool TryApplyMove(PlannedMove move, string stamp)
    {
        string from = Location.PathFor(move.From.Key), to = Location.PathFor(move.To.Key);
        if (!File.Exists(from))
            return false;
        try
        {
            if (File.Exists(to))
                MoveToVersions(to, move.To.Key, stamp);
            FsUtil.MoveFile(from, to);
            File.SetLastWriteTimeUtc(to, new DateTime(move.To.ModifiedUtcTicks, DateTimeKind.Utc));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return false;
        }
        FsUtil.RemoveEmptyFoldersUpTo(Path.GetDirectoryName(from), Location.Root);

        Catalog.Remove(move.From.Key);
        Catalog.Save(new FileRecord
        {
            Key = move.To.Key,
            Size = move.To.Size,
            ModifiedUtcTicks = move.To.ModifiedUtcTicks,
            Hash = move.Hash,
            State = FileState.Active,
            StateChangedUtc = DateTime.UtcNow,
        });
        Catalog.Log("moved", move.To.Key, "from " + move.From.Key);
        return true;
    }

    /// <summary>
    /// Copies one file to the backup through a temporary file, so the backup never holds a half-written copy.
    /// A different older copy is moved to <c>versions</c> first.
    /// </summary>
    async Task<(CopyOutcome Outcome, FileRecord? Record)> BackUpFileAsync(ScannedFile file, string stamp, List<Problem> problems, Action<long>? onBytes, CancellationToken ct)
    {
        string destination = Location.PathFor(file.Key);
        string temp = destination + FsUtil.TempSuffix;
        var record = Catalog.Get(file.Key);
        string hash;
        try
        {
            FsUtil.EnsureParent(destination);
            hash = await FileHasher.CopyWithHashAsync(file.FullPath, temp, onBytes, ct);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or OperationCanceledException)
        {
            FsUtil.TryRemoveOwnTemp(temp);
            if (e is OperationCanceledException)
                throw;
            problems.Add(new Problem(file.Key, $"Couldn't copy this file: {e.Message}"));
            return (CopyOutcome.Failed, null);
        }

        var outcome = record is null || record.State == FileState.Deleted ? CopyOutcome.Copied : CopyOutcome.Updated;
        try
        {
            if (File.Exists(destination))
            {
                string existingHash = record is { State: not FileState.Deleted, Hash.Length: > 0 } ? record.Hash : FileHasher.HashFile(destination, ct);
                if (existingHash.Equals(hash, StringComparison.OrdinalIgnoreCase))
                {
                    if (outcome == CopyOutcome.Updated)
                        outcome = CopyOutcome.Touched;
                }
                else
                {
                    MoveToVersions(destination, file.Key, stamp);
                }
            }
            File.Move(temp, destination, overwrite: true);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            FsUtil.TryRemoveOwnTemp(temp);
            problems.Add(new Problem(file.Key, $"Couldn't update the backup copy: {e.Message}"));
            return (CopyOutcome.Failed, null);
        }

        record ??= new FileRecord { Key = file.Key };
        record.Size = file.Size;
        record.ModifiedUtcTicks = file.ModifiedUtcTicks;
        record.Hash = hash;
        SetState(record, FileState.Active, outcome.ToString().ToLowerInvariant());
        return (outcome, record);
    }

    void ApplyMissingPolicy(SyncPlan plan, SyncResult result, string stamp)
    {
        var toRemove = new List<FileRecord>();
        foreach (var missing in plan.Missing)
        {
            var decision = plan.MassDisappearance ? MissingDecision.DecideLater : Profile.MissingFilePolicy switch
            {
                MissingFilePolicy.AlwaysKeep => MissingDecision.KeepOnBackup,
                MissingFilePolicy.AlwaysRemove => MissingDecision.DeletedOnPurpose,
                MissingFilePolicy.RecycleBinMeansDeleted when missing.InRecycleBin => MissingDecision.DeletedOnPurpose,
                _ => MissingDecision.DecideLater,
            };
            switch (decision)
            {
                case MissingDecision.DeletedOnPurpose:
                    toRemove.Add(missing.Record);
                    break;
                case MissingDecision.KeepOnBackup:
                    SetState(missing.Record, FileState.Archived, "kept-on-backup", "by policy");
                    result.KeptOnBackup++;
                    break;
                default:
                    if (missing.Record.State != FileState.Pending)
                        SetState(missing.Record, FileState.Pending, "missing-from-pc");
                    result.WaitingForDecision++;
                    break;
            }
        }

        var byKey = toRemove.ToDictionary(r => r.Key, KeyPath.Comparer);
        foreach (var (key, problem) in RemoveFromBackup(toRemove, stamp))
        {
            var record = byKey[key];
            if (problem is null)
            {
                SetState(record, FileState.Deleted, "removed-from-backup", "by policy");
                result.RemovedFromBackup++;
            }
            else
            {
                if (record.State != FileState.Pending)
                    SetState(record, FileState.Pending, "missing-from-pc");
                result.WaitingForDecision++;
                result.Problems.Add(new Problem(key, problem));
            }
        }
    }

    /// <summary>Files waiting for a decision, with a hint when the PC's Recycle Bin holds them.</summary>
    public IReadOnlyList<MissingFile> GetPendingFiles()
    {
        var pending = Catalog.Query(FileState.Pending);
        if (pending.Count == 0)
            return [];
        var recycleBin = PcRecycleBin();
        return [.. pending.Select(r => new MissingFile(r,
            _sources.ContainsKey(KeyPath.SourceName(r.Key)) && recycleBin.Find(SourcePathFor(r.Key)) is not null))];
    }

    /// <summary>Applies the user's answer for files that are missing from the PC.</summary>
    public DecisionResult ResolveMissing(IEnumerable<string> keys, MissingDecision decision)
    {
        var result = new DecisionResult();
        var records = new List<FileRecord>();
        foreach (string key in keys.Distinct(KeyPath.Comparer))
        {
            var record = Catalog.Get(key);
            if (record is not { State: FileState.Pending or FileState.Active })
            {
                result.Problems.Add(new Problem(key, "This file isn't waiting for a decision."));
                continue;
            }
            if (_sources.ContainsKey(KeyPath.SourceName(key)) && File.Exists(SourcePathFor(key)))
            {
                result.Problems.Add(new Problem(key, "This file is back on the PC, so there's nothing to decide."));
                continue;
            }
            records.Add(record);
        }

        using (Catalog.Batch())
        {
            switch (decision)
            {
                case MissingDecision.KeepOnBackup:
                    foreach (var record in records)
                        SetState(record, FileState.Archived, "kept-on-backup");
                    result.Applied = records.Count;
                    break;
                case MissingDecision.DecideLater:
                    foreach (var record in records.Where(r => r.State != FileState.Pending))
                        SetState(record, FileState.Pending, "missing-from-pc");
                    result.Applied = records.Count;
                    break;
                case MissingDecision.DeletedOnPurpose:
                    var byKey = records.ToDictionary(r => r.Key, KeyPath.Comparer);
                    foreach (var (key, problem) in RemoveFromBackup(records, NewStamp()))
                    {
                        var record = byKey[key];
                        if (problem is null)
                        {
                            SetState(record, FileState.Deleted, "removed-from-backup");
                            result.Applied++;
                        }
                        else
                        {
                            if (record.State != FileState.Pending)
                                SetState(record, FileState.Pending, "missing-from-pc");
                            result.Problems.Add(new Problem(key, problem));
                        }
                    }
                    break;
            }
        }
        return result;
    }

    void EnsureFreeSpace(long bytesNeeded)
    {
        if (bytesNeeded <= 0)
            return;
        string? root = Path.GetPathRoot(Location.Root);
        if (string.IsNullOrEmpty(root) || root.StartsWith(@"\\", StringComparison.Ordinal))
            return;
        long free = new DriveInfo(root).AvailableFreeSpace;
        if (bytesNeeded > free)
        {
            throw new DriveSyncException(
                $"This sync needs {FsUtil.FormatSize(bytesNeeded)} on the backup drive, but only {FsUtil.FormatSize(free)} is free. Free up space on the drive, then try again.");
        }
    }
}
