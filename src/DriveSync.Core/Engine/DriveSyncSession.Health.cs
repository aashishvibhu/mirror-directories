using System.Globalization;
using DriveSync.Core.IO;
using DriveSync.Core.Model;
using DriveSync.Core.Scanning;

namespace DriveSync.Core.Engine;

public sealed partial class DriveSyncSession
{
    /// <summary>A checksum check passed this recently isn't repeated by the rolling check.</summary>
    static readonly TimeSpan RecheckAfter = TimeSpan.FromDays(7);

    /// <summary>
    /// Run at the end of every sync: confirms each backup copy exists and has the right size, reading names and sizes only.
    /// Returns how many don't, or the last known count if it was interrupted.
    /// </summary>
    int QuickCheck(CancellationToken ct)
    {
        int issues = 0;
        try
        {
            foreach (var record in Catalog.LoadAll().Values)
            {
                if (ct.IsCancellationRequested)
                    return StoredInt("quick_check_issues");
                if (record.State == FileState.Deleted)
                    continue;
                var info = new FileInfo(Location.PathFor(record.Key));
                if (!info.Exists || info.Length != record.Size)
                    issues++;
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return StoredInt("quick_check_issues");
        }
        Catalog.SetMeta("quick_check_issues", issues.ToString(CultureInfo.InvariantCulture));
        Catalog.SetMeta("last_quick_check_utc", DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture));
        return issues;
    }

    int StoredInt(string name) => int.TryParse(Catalog.GetMeta(name), CultureInfo.InvariantCulture, out int n) ? n : 0;

    /// <summary>
    /// The rolling check: checksums the backup copies that were checked longest ago, until the time or size budget runs out.
    /// Over a few weeks of syncs this covers the whole drive without one long run. Stopping it loses nothing.
    /// </summary>
    public Task<VerifyResult> VerifySliceAsync(TimeSpan budget, long maxBytes, IProgress<SyncProgress>? progress = null, CancellationToken ct = default) =>
        Task.Run(() =>
        {
            var throttle = new ProgressThrottle(progress);
            var started = DateTime.UtcNow;
            int done = 0, failed = 0;
            long bytes = 0;
            var due = Catalog.LeastRecentlyVerified(20_000, (DateTime.UtcNow - RecheckAfter).Ticks);
            long bytesDue = due.Sum(r => r.Size);
            using (Catalog.Batch())
            {
                foreach (var record in due)
                {
                    if (ct.IsCancellationRequested || DateTime.UtcNow - started > budget || bytes >= maxBytes)
                        break;
                    throttle.Report(new SyncProgress("Checking older backup copies", done, due.Count, bytes, Math.Min(bytesDue, maxBytes), record.Key));
                    string path = Location.PathFor(record.Key);
                    if (record.Hash.Length == 0 || !File.Exists(path))
                        continue; // the quick check reports missing copies
                    try
                    {
                        bool good = FileHasher.HashFile(path, ct).Equals(record.Hash, StringComparison.OrdinalIgnoreCase);
                        Catalog.SetVerified(record.Key, good ? DateTime.UtcNow.Ticks : Storage.Catalog.FailedCheck);
                        if (!good)
                        {
                            failed++;
                            Catalog.Log("damaged", record.Key, "the backup copy no longer matches its checksum");
                        }
                    }
                    catch (OperationCanceledException)
                    {
                        break;
                    }
                    catch (Exception e) when (e is IOException or UnauthorizedAccessException)
                    {
                        continue;
                    }
                    done++;
                    bytes += record.Size;
                    if (done % 100 == 0)
                        Catalog.Checkpoint();
                }
                Catalog.SetMeta("last_rolling_check_utc", DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture));
            }
            return new VerifyResult(done, bytes, failed);
        }, CancellationToken.None);

    /// <summary>What the Health screen shows before anything is run.</summary>
    public HealthStatus GetHealthStatus()
    {
        var (files, verified, _) = Catalog.VerifiedStats(DateTime.UtcNow.AddDays(-30));
        return new HealthStatus(MetaTime("last_quick_check_utc"), MetaTime("last_full_check_utc"), files, verified,
            StoredInt("quick_check_issues"), Catalog.FailedChecks());
    }

    DateTime? MetaTime(string name) =>
        DateTime.TryParse(Catalog.GetMeta(name), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var t) ? t : null;

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
            using (Catalog.Batch())
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
                    else if (full)
                    {
                        bool good = FileHasher.HashFile(info.FullName, ct).Equals(record.Hash, StringComparison.OrdinalIgnoreCase);
                        if (!good)
                            report.Issues.Add(new HealthIssue(record, HealthProblem.Damaged));
                        Catalog.SetVerified(record.Key, good ? DateTime.UtcNow.Ticks : Storage.Catalog.FailedCheck);
                    }
                    report.Checked++;
                    bytesDone += full ? record.Size : 0;
                    if (report.Checked % 200 == 0)
                        Catalog.Checkpoint();
                }
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
            if (full)
                ReindexVersions();
        }, ct);

        if (repair)
        {
            string stamp = NewStamp();
            using (Catalog.Batch())
            {
                foreach (var issue in report.Issues)
                {
                    var record = issue.Record;
                    if (record.State != FileState.Active || !_sources.ContainsKey(KeyPath.SourceName(record.Key)))
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
                        Catalog.SetVerified(record.Key, 0);
                    }
                    else
                    {
                        issue.Note = problems.FirstOrDefault()?.Message;
                    }
                }
            }
            foreach (string temp in report.LeftoverTempFiles)
                FsUtil.TryRemoveOwnTemp(temp);
        }

        int sizeIssues = report.Issues.Count(i => !i.Repaired && i.Problem != HealthProblem.Damaged);
        Catalog.SetMeta("quick_check_issues", sizeIssues.ToString(CultureInfo.InvariantCulture));
        Catalog.SetMeta("last_quick_check_utc", DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture));
        if (full)
            Catalog.SetMeta("last_full_check_utc", DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture));
        return report;
    }
}
