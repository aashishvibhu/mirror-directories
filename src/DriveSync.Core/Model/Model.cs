namespace DriveSync.Core.Model;

/// <summary>Where a backed-up file stands.</summary>
public enum FileState
{
    /// <summary>On the PC and backed up.</summary>
    Active = 0,

    /// <summary>Removed from the PC to free space; kept on the backup on purpose.</summary>
    Archived = 1,

    /// <summary>Missing from the PC, waiting for the user to say why. Still on the backup.</summary>
    Pending = 2,

    /// <summary>Deleted on purpose; the backup copy went to a Recycle Bin (or the drive's trash folder).</summary>
    Deleted = 3,
}

/// <summary>What a sync does with files that disappeared from the PC without going through DriveSync.</summary>
public enum MissingFilePolicy
{
    Ask,
    RecycleBinMeansDeleted,
    AlwaysKeep,
    AlwaysRemove,
}

/// <summary>The user's answer for a file that is missing from the PC.</summary>
public enum MissingDecision
{
    DeletedOnPurpose,
    KeepOnBackup,
    DecideLater,
}

/// <summary>One file DriveSync knows about, as stored in the catalog on the backup drive.</summary>
public sealed class FileRecord
{
    /// <summary>Source name plus the path inside it, e.g. <c>Desktop\Taxes\2026.pdf</c>.</summary>
    public required string Key { get; init; }

    public long Size { get; set; }

    public long ModifiedUtcTicks { get; set; }

    /// <summary>SHA-256 of the contents as upper-case hex, or empty when unknown.</summary>
    public string Hash { get; set; } = "";

    public FileState State { get; set; }

    public DateTime StateChangedUtc { get; set; }
}

/// <summary>A folder on the PC that DriveSync backs up. <see cref="Name"/> is its folder name on the backup drive.</summary>
public sealed record SourceFolder(string Name, string Path);

/// <summary>Everything DriveSync needs to know about one PC-to-drive backup.</summary>
public sealed class BackupProfile
{
    public static readonly string[] DefaultExclusions =
    [
        "desktop.ini", "Thumbs.db", "ehthumbs.db", ".DS_Store", "~$*", "*.tmp", "$RECYCLE.BIN", "System Volume Information",
    ];

    public string DriveId { get; set; } = "";

    public string BackupFolderName { get; set; } = BackupSetup.DefaultFolderName;

    /// <summary>Where the backup was last found, e.g. <c>E:\DriveSyncBackup</c>. The drive letter can change.</summary>
    public string? LastKnownRoot { get; set; }

    public List<SourceFolder> Sources { get; set; } = [];

    public List<string> Exclusions { get; set; } = [.. DefaultExclusions];

    public MissingFilePolicy MissingFilePolicy { get; set; } = MissingFilePolicy.Ask;

    /// <summary>If at least this share of backed-up files vanish in one go, nothing is removed from the backup.</summary>
    public int MassDisappearancePercent { get; set; } = 30;

    /// <summary>The guard only applies once at least this many files vanish together.</summary>
    public int MassDisappearanceMinFiles { get; set; } = 50;

    /// <summary>Skip OneDrive files that exist only in the cloud, so reading them doesn't download them.</summary>
    public bool SkipCloudOnlyFiles { get; set; } = true;
}
