using DriveSync.Core.Model;
using DriveSync.Core.Storage;

namespace DriveSync.Core;

/// <summary>Creates a new backup profile from the folders and drive the user picked.</summary>
public static class BackupSetup
{
    public const string DefaultFolderName = "DriveSyncBackup";

    /// <summary>
    /// Prepares <c>&lt;target&gt;\&lt;folderName&gt;</c> as the backup for <paramref name="sourceFolders"/>.
    /// If the drive already holds a DriveSync backup in that folder, the profile reconnects to it.
    /// </summary>
    public static BackupProfile CreateProfile(string target, IEnumerable<string> sourceFolders, string folderName = DefaultFolderName)
    {
        string targetPath = Path.GetFullPath(target);
        if (!Directory.Exists(targetPath))
            throw new DriveSyncException($"{targetPath} doesn't exist. Check that the drive is plugged in.");
        string root = Path.Combine(targetPath, folderName);

        var sources = new List<SourceFolder>();
        var names = new HashSet<string>(KeyPath.Comparer);
        foreach (string folder in sourceFolders)
        {
            string path = Path.TrimEndingDirectorySeparator(Path.GetFullPath(folder));
            if (!Directory.Exists(path))
                throw new DriveSyncException($"{path} doesn't exist.");
            if (IsSameOrInside(root, path) || IsSameOrInside(path, root))
                throw new DriveSyncException("The backup can't be inside a folder you're backing up, or the other way round.");
            if (sources.Any(s => s.Path.Equals(path, StringComparison.OrdinalIgnoreCase)))
                continue;

            string name = Path.GetFileName(path);
            if (string.IsNullOrEmpty(name))
                name = path[..1] + "-drive";
            string unique = name;
            for (int i = 2; !names.Add(unique); i++)
                unique = $"{name} {i}";
            sources.Add(new SourceFolder(unique, path));
        }
        if (sources.Count == 0)
            throw new DriveSyncException("Pick at least one folder to back up.");

        string driveId = BackupLocation.ReadDriveId(root) ?? Guid.NewGuid().ToString("D");
        var location = BackupLocation.Create(root, driveId);
        return new BackupProfile
        {
            DriveId = driveId,
            BackupFolderName = folderName,
            LastKnownRoot = location.Root,
            Sources = sources,
        };
    }

    /// <summary>True when both paths are on the same drive, in which case the "backup" doesn't protect against a disk failure.</summary>
    public static bool IsOnSameDrive(string a, string b) =>
        string.Equals(Path.GetPathRoot(Path.GetFullPath(a)), Path.GetPathRoot(Path.GetFullPath(b)), StringComparison.OrdinalIgnoreCase);

    static bool IsSameOrInside(string path, string folder)
    {
        path = Path.TrimEndingDirectorySeparator(path);
        folder = Path.TrimEndingDirectorySeparator(folder);
        string prefix = folder.EndsWith('\\') ? folder : folder + "\\";
        return path.Equals(folder, StringComparison.OrdinalIgnoreCase) || path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase);
    }
}
