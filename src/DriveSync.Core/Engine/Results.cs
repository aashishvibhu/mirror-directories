using DriveSync.Core.Model;
using DriveSync.Core.Scanning;

namespace DriveSync.Core.Engine;

/// <summary>Something that went wrong with one file, explained for the user.</summary>
public sealed record Problem(string Key, string Message);

/// <summary>Progress of a long task. Totals are 0 when not known yet.</summary>
public sealed record SyncProgress(string Phase, int FilesDone, int FilesTotal, long BytesDone, long BytesTotal, string? CurrentItem);

/// <summary>A file that moved or was renamed on the PC, recognised by its checksum.</summary>
public sealed record PlannedMove(FileRecord From, ScannedFile To, string Hash);

/// <summary>A backed-up file that is no longer on the PC.</summary>
public sealed record MissingFile(FileRecord Record, bool InRecycleBin);

/// <summary>What a sync is about to do. Nothing changes until it's applied.</summary>
public sealed class SyncPlan(ScanResult scan)
{
    public ScanResult Scan { get; } = scan;

    public List<ScannedFile> NewFiles { get; } = [];

    public List<ScannedFile> ChangedFiles { get; } = [];

    public List<PlannedMove> Moves { get; } = [];

    /// <summary>Offloaded or pending files that are back on the PC unchanged.</summary>
    public List<FileRecord> BackOnPc { get; } = [];

    public List<MissingFile> Missing { get; } = [];

    public int UnchangedCount { get; set; }

    public long BytesToCopy { get; set; }

    /// <summary>
    /// True when so many files vanished at once that it looks like a disconnected folder, a cloud-sync problem or ransomware.
    /// Nothing is removed from the backup in that case; the missing files wait for a decision.
    /// </summary>
    public bool MassDisappearance { get; set; }

    public bool HasChanges => NewFiles.Count + ChangedFiles.Count + Moves.Count + BackOnPc.Count + Missing.Count > 0;
}

public sealed class SyncResult
{
    public int Copied { get; set; }

    public int Updated { get; set; }

    /// <summary>Files whose date changed but whose contents didn't.</summary>
    public int Touched { get; set; }

    public int Moved { get; set; }

    public int BackOnPc { get; set; }

    public long BytesCopied { get; set; }

    public int RemovedFromBackup { get; set; }

    public int KeptOnBackup { get; set; }

    public int WaitingForDecision { get; set; }

    /// <summary>All files waiting for a decision after this sync, including older ones.</summary>
    public int PendingTotal { get; set; }

    public bool MassDisappearance { get; set; }

    public List<Problem> Problems { get; } = [];

    public List<string> Warnings { get; } = [];

    public TimeSpan Duration { get; set; }
}

public sealed class DecisionResult
{
    public int Applied { get; set; }

    public List<Problem> Problems { get; } = [];
}

public sealed class OffloadResult
{
    public int Offloaded { get; set; }

    /// <summary>Files that weren't backed up yet (or had changed) and were backed up before offloading.</summary>
    public int BackedUpFirst { get; set; }

    /// <summary>Space that comes back once the user empties the PC's Recycle Bin.</summary>
    public long BytesMovedToRecycleBin { get; set; }

    public int CloudOnlySkipped { get; set; }

    public List<Problem> Problems { get; } = [];
}

public sealed class DeleteResult
{
    public int Deleted { get; set; }

    public List<Problem> Problems { get; } = [];
}

public enum RestoreSource
{
    CurrentBackup,
    OldVersion,
    Deleted,
}

/// <summary>What to restore. <see cref="Paths"/> can be PC paths, backup paths or catalog keys.</summary>
public sealed record RestoreRequest(IReadOnlyList<string> Paths)
{
    public RestoreSource Source { get; init; } = RestoreSource.CurrentBackup;

    /// <summary>For <see cref="RestoreSource.OldVersion"/>: the version's stamp, as listed by <c>ListVersions</c>.</summary>
    public string? VersionStamp { get; init; }

    /// <summary>Restore into this folder instead of the original location.</summary>
    public string? TargetFolder { get; init; }
}

public sealed class RestoreResult
{
    public int Restored { get; set; }

    public int AlreadyThere { get; set; }

    /// <summary>Files where a different file was already in the way; it was renamed, not replaced.</summary>
    public int KeptBoth { get; set; }

    public List<string> RestoredPaths { get; } = [];

    public List<Problem> Problems { get; } = [];
}

public sealed record VersionEntry(string Stamp, string Key, long Size, DateTime ModifiedUtc, string Path);

public sealed record DeletedEntry(string Key, long Size, DateTime DeletedUtc, bool InRecycleBin, string DataPath);

public enum HealthProblem
{
    Missing,
    WrongSize,
    Damaged,
}

public sealed class HealthIssue(FileRecord record, HealthProblem problem)
{
    public FileRecord Record { get; } = record;

    public HealthProblem Problem { get; } = problem;

    public bool Repaired { get; set; }

    /// <summary>Why it couldn't be repaired, when it couldn't.</summary>
    public string? Note { get; set; }
}

public sealed class HealthReport
{
    public bool FullCheck { get; init; }

    public int Checked { get; set; }

    public List<HealthIssue> Issues { get; } = [];

    /// <summary>Files in the backup that the catalog doesn't know about. Reported, never touched.</summary>
    public List<string> Untracked { get; } = [];

    public List<string> LeftoverTempFiles { get; } = [];

    public int Repaired { get; set; }

    public bool IsHealthy => Issues.Count == 0;
}

public sealed record BackupSummary(
    IReadOnlyList<Storage.StateTotal> Totals,
    DateTime? LastSyncUtc,
    long? DriveFreeBytes,
    long? DriveTotalBytes,
    string BackupRoot)
{
    public int Count(FileState state) => Totals.FirstOrDefault(t => t.State == state)?.Files ?? 0;

    public long Bytes(FileState state) => Totals.FirstOrDefault(t => t.State == state)?.Bytes ?? 0;
}
