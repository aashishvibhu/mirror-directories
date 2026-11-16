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

/// <summary>Where copies removed from the backup go. Neither choice erases anything.</summary>
public enum RemovedFilesGoTo
{
    /// <summary>The backup drive's Recycle Bin; the backup's own trash folder when the bin has no room or doesn't exist.</summary>
    RecycleBinThenTrashFolder,

    /// <summary>Always the backup's own trash folder (<c>.drivesync\trash</c>): no size limit, and the same on every PC.</summary>
    TrashFolder,
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

/// <summary>
/// A folder that used to be in a backup. Its files stay on the backup drive under <see cref="Name"/>
/// unless the user sends them to the Recycle Bin.
/// </summary>
public sealed record RemovedFolder(string Name, string Path, DateTime RemovedUtc);

/// <summary>Everything DriveSync needs to know about one backup: a set of folders going to one destination.</summary>
public sealed class BackupProfile
{
    public static readonly string[] DefaultExclusions =
    [
        "desktop.ini", "Thumbs.db", "ehthumbs.db", ".DS_Store", "~$*", "*.tmp", "$RECYCLE.BIN", "System Volume Information",
    ];

    /// <summary>Identifies the backup in the app. Unlike <see cref="DriveId"/>, it stays the same when the backup moves to another drive.</summary>
    public string Id { get; set; } = Guid.NewGuid().ToString("N");

    /// <summary>The name people see, such as "Personal files".</summary>
    public string Name { get; set; } = "My backup";

    /// <summary>Identifies the backup folder on the drive (its <c>drive.id</c> file).</summary>
    public string DriveId { get; set; } = "";

    public string BackupFolderName { get; set; } = BackupSetup.DefaultFolderName;

    /// <summary>Where the backup was last found, e.g. <c>E:\DriveSyncBackup</c>. The drive letter can change.</summary>
    public string? LastKnownRoot { get; set; }

    public List<SourceFolder> Sources { get; set; } = [];

    /// <summary>Folders taken out of this backup whose files are still on the drive.</summary>
    public List<RemovedFolder> RemovedFolders { get; set; } = [];

    public List<string> Exclusions { get; set; } = [.. DefaultExclusions];

    public MissingFilePolicy MissingFilePolicy { get; set; } = MissingFilePolicy.Ask;

    /// <summary>If at least this share of backed-up files vanish in one go, nothing is removed from the backup.</summary>
    public int MassDisappearancePercent { get; set; } = 30;

    /// <summary>The guard only applies once at least this many files vanish together.</summary>
    public int MassDisappearanceMinFiles { get; set; } = 50;

    /// <summary>Skip OneDrive files that exist only in the cloud, so reading them doesn't download them.</summary>
    public bool SkipCloudOnlyFiles { get; set; } = true;

    public RemovedFilesGoTo RemovedFilesGoTo { get; set; } = RemovedFilesGoTo.RecycleBinThenTrashFolder;

    /// <summary>After each sync, checksum the backup copies that were checked longest ago, for a few minutes.</summary>
    public bool RollingCheck { get; set; } = true;

    public int RollingCheckMinutes { get; set; } = 5;
}
