using DriveSync.Core.Model;

namespace DriveSync.Core.Storage;

/// <summary>
/// The backup folder on the drive, e.g. <c>E:\DriveSyncBackup</c>. Files are stored as plain copies under
/// <c>&lt;source name&gt;\...</c>; DriveSync's own data lives in <c>.drivesync</c>.
/// </summary>
public sealed class BackupLocation
{
    public const string MetaFolderName = ".drivesync";

    public string Root { get; }

    public string MetaDir => Path.Combine(Root, MetaFolderName);

    public string CatalogPath => Path.Combine(MetaDir, "catalog.db");

    /// <summary>Older versions of changed files, kept forever.</summary>
    public string VersionsDir => Path.Combine(MetaDir, "versions");

    /// <summary>Removed files, used only when the drive has no Recycle Bin. Never emptied by DriveSync.</summary>
    public string TrashDir => Path.Combine(MetaDir, "trash");

    public string LockPath => Path.Combine(MetaDir, "lock");

    public string DriveIdPath => Path.Combine(MetaDir, "drive.id");

    BackupLocation(string root) => Root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));

    public string PathFor(string key) => Path.Combine(Root, key);

    /// <summary>Creates the backup folder (or reuses an existing one) and stamps it with <paramref name="driveId"/>.</summary>
    public static BackupLocation Create(string root, string driveId)
    {
        var location = new BackupLocation(root);
        Directory.CreateDirectory(location.MetaDir);
        Directory.CreateDirectory(location.VersionsDir);
        if (!File.Exists(location.DriveIdPath))
            File.WriteAllText(location.DriveIdPath, driveId);
        string readme = Path.Combine(location.Root, "README.txt");
        if (!File.Exists(readme))
            File.WriteAllText(readme, Readme);
        return location;
    }

    public static string? ReadDriveId(string root)
    {
        try
        {
            return File.ReadAllText(Path.Combine(root, MetaFolderName, "drive.id")).Trim();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>Looks for this profile's backup where it was last seen, then on every connected drive.</summary>
    public static BackupLocation? Find(BackupProfile profile)
    {
        var candidates = new List<string>();
        if (!string.IsNullOrEmpty(profile.LastKnownRoot))
            candidates.Add(profile.LastKnownRoot);
        foreach (var drive in DriveInfo.GetDrives())
        {
            try
            {
                if (drive.DriveType is DriveType.CDRom or DriveType.NoRootDirectory || !drive.IsReady)
                    continue;
            }
            catch (IOException)
            {
                continue;
            }
            candidates.Add(Path.Combine(drive.RootDirectory.FullName, profile.BackupFolderName));
        }

        foreach (string root in candidates)
        {
            if (string.Equals(ReadDriveId(root), profile.DriveId, StringComparison.OrdinalIgnoreCase))
                return new BackupLocation(root);
        }
        return null;
    }

    const string Readme = """
        This folder is a DriveSync backup.

        Your files are stored here as plain copies, one folder per backed-up PC folder,
        so you can open them in File Explorer without DriveSync.

        .drivesync\catalog.db   the state and SHA-256 checksum of every file
        .drivesync\versions     older versions of files that changed, kept forever
        .drivesync\trash        removed files, used only if this drive has no Recycle Bin

        DriveSync never deletes your files. Anything it removes goes to a Recycle Bin.
        """;
}
