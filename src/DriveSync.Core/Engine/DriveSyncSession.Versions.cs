using System.Globalization;
using DriveSync.Core.IO;
using DriveSync.Core.Model;
using DriveSync.Core.Storage;

namespace DriveSync.Core.Engine;

/// <summary>A suggestion for which old versions could go. The user confirms; nothing is ever removed on its own.</summary>
public enum VersionCleanup
{
    /// <summary>Versions replaced more than N days ago.</summary>
    OlderThanDays,

    /// <summary>All but the newest N old versions of each file.</summary>
    KeepNewest,

    /// <summary>Keep everything for 30 days, then one a week for a year, then one a month.</summary>
    ThinOut,
}

public sealed partial class DriveSyncSession
{
    /// <summary>
    /// Rebuilds the catalog's list of old versions from the <c>versions</c> folder. Run once for backups made before
    /// the list existed, and by the full health check, in case files there were removed outside DriveSync.
    /// </summary>
    public void ReindexVersions()
    {
        try
        {
            var hashes = Catalog.Versions().Where(v => v.Hash.Length > 0).ToDictionary(v => v.Path, v => v.Hash, StringComparer.OrdinalIgnoreCase);
            using (Catalog.Batch())
            {
                Catalog.ClearVersions();
                if (Directory.Exists(Location.VersionsDir))
                {
                    foreach (string stampDir in Directory.EnumerateDirectories(Location.VersionsDir))
                    {
                        string stamp = Path.GetFileName(stampDir);
                        foreach (var file in new DirectoryInfo(stampDir).EnumerateFiles("*", SearchOption.AllDirectories))
                        {
                            string path = Path.GetRelativePath(Location.VersionsDir, file.FullName);
                            Catalog.AddVersion(new VersionRow(path, Path.GetRelativePath(stampDir, file.FullName), stamp, file.Length,
                                file.LastWriteTimeUtc.Ticks, hashes.GetValueOrDefault(path, "")));
                        }
                    }
                }
                Catalog.SetMeta("versions_indexed", "1");
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // The folder couldn't be read this time. The list is rebuilt the next time the backup is opened.
        }
    }

    /// <summary>Older versions of the files at or under <paramref name="path"/>, newest first.</summary>
    public IReadOnlyList<VersionEntry> ListVersions(string path) => [.. Catalog.Versions(ResolveKey(path)).Select(ToEntry)];

    /// <summary>Every old version on this backup, newest first.</summary>
    public IReadOnlyList<VersionEntry> ListAllVersions() => [.. Catalog.Versions().Select(ToEntry)];

    VersionEntry ToEntry(VersionRow row) =>
        new(row.Stamp, row.Key, row.Size, new DateTime(row.ModifiedUtcTicks, DateTimeKind.Utc), Path.Combine(Location.VersionsDir, row.Path));

    /// <summary>When a version was replaced, read from its stamp (local time).</summary>
    public static DateTime? StampTime(string stamp) =>
        DateTime.TryParseExact(stamp, "yyyyMMdd-HHmmss", CultureInfo.InvariantCulture, DateTimeStyles.AssumeLocal, out var t) ? t : null;

    public VersionUsage GetVersionUsage()
    {
        var (count, bytes) = Catalog.VersionTotals();
        return new VersionUsage(count, bytes, [.. Catalog.VersionHogs().Select(h => new VersionHog(h.Key, h.Count, h.Bytes))]);
    }

    /// <summary>
    /// Old versions a clean-up rule would remove. Only a suggestion: pass the ones the user confirms to <see cref="RemoveVersions"/>.
    /// </summary>
    public IReadOnlyList<VersionEntry> SuggestVersions(VersionCleanup rule, int value = 0, DateTime? now = null)
    {
        var today = now ?? DateTime.Now;
        var all = ListAllVersions();
        switch (rule)
        {
            case VersionCleanup.OlderThanDays:
                return [.. all.Where(v => StampTime(v.Stamp) is { } t && t < today.AddDays(-value))];
            case VersionCleanup.KeepNewest:
                return [.. all.GroupBy(v => v.Key, KeyPath.Comparer).SelectMany(g => g.Skip(Math.Max(0, value)))];
            default:
                var remove = new List<VersionEntry>();
                foreach (var file in all.GroupBy(v => v.Key, KeyPath.Comparer))
                {
                    var kept = new HashSet<string>();
                    foreach (var version in file) // newest first
                    {
                        if (StampTime(version.Stamp) is not { } t || t >= today.AddDays(-30))
                            continue;
                        string slot = t >= today.AddDays(-365)
                            ? $"w{ISOWeek.GetYear(t)}-{ISOWeek.GetWeekOfYear(t)}"
                            : $"m{t:yyyy-MM}";
                        if (!kept.Add(slot))
                            remove.Add(version);
                    }
                }
                return remove;
        }
    }

    /// <summary>Sends old versions to the Recycle Bin or the backup's trash folder. Current backup copies aren't touched.</summary>
    public DecisionResult RemoveVersions(IEnumerable<VersionEntry> versions)
    {
        var result = new DecisionResult();
        var items = versions
            .Select(v => new Removal(Path.GetRelativePath(Location.VersionsDir, v.Path), v.Path, Path.Combine("versions", Path.GetRelativePath(Location.VersionsDir, v.Path))))
            .DistinctBy(r => r.Id, StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (items.Count == 0)
            return result;

        var outcome = SendAway(items, NewStamp(), out int fellBack);
        result.SentToTrashFolder = fellBack;
        using (Catalog.Batch())
        {
            foreach (var item in items)
            {
                if (outcome[item.Id] is { } problem)
                {
                    result.Problems.Add(new Problem(item.Id, problem));
                    continue;
                }
                Catalog.RemoveVersion(item.Id);
                FsUtil.RemoveEmptyFoldersUpTo(Path.GetDirectoryName(item.Path), Location.VersionsDir);
                result.Applied++;
            }
            if (result.Applied > 0)
                Catalog.Log("versions-removed", null, $"{result.Applied:N0} old versions");
        }
        return result;
    }

    /// <summary>What takes the space on the backup drive.</summary>
    public StorageUsage GetStorageUsage()
    {
        var summary = GetSummary();
        long trash = 0;
        try
        {
            if (Directory.Exists(Location.TrashDir))
                trash = new DirectoryInfo(Location.TrashDir).EnumerateFiles("*", SearchOption.AllDirectories).Sum(f => f.Length);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
        }
        return new StorageUsage(
            summary.Bytes(FileState.Active),
            summary.Bytes(FileState.Archived) + summary.Bytes(FileState.Pending),
            Catalog.VersionTotals().Bytes,
            trash,
            summary.DriveFreeBytes,
            summary.DriveTotalBytes);
    }
}
