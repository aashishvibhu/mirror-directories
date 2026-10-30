using System.Text;
using DriveSync.Core.Model;
using DriveSync.Core.Storage;

namespace DriveSync.Core;

/// <summary>Creates new backups from the folders and drive the user picked, and checks folder changes against every backup.</summary>
public static class BackupSetup
{
    public const string DefaultFolderName = "DriveSyncBackup";

    /// <summary>Single-backup form, kept for tests and the first backup on a PC.</summary>
    public static BackupProfile CreateProfile(string target, IEnumerable<string> sourceFolders, string folderName = DefaultFolderName) =>
        CreateProfile(target, sourceFolders, "My backup", [], folderName);

    /// <summary>
    /// Prepares a backup named <paramref name="name"/> of <paramref name="sourceFolders"/> on <paramref name="target"/>.
    /// The first backup on a drive goes in <c>DriveSyncBackup</c>; further ones get a folder of their own, such as
    /// <c>DriveSyncBackup-photos-archive</c>. A folder holding a DriveSync backup the app doesn't know yet is reconnected.
    /// </summary>
    public static BackupProfile CreateProfile(
        string target, IEnumerable<string> sourceFolders, string name, IReadOnlyList<BackupProfile> existing, string? folderName = null)
    {
        string targetPath = Path.GetFullPath(target);
        if (!Directory.Exists(targetPath))
            throw new DriveSyncException($"{targetPath} doesn't exist. Check that the drive is plugged in.");
        name = string.IsNullOrWhiteSpace(name) ? "My backup" : name.Trim();
        if (existing.Any(b => b.Name.Equals(name, StringComparison.OrdinalIgnoreCase)))
            throw new DriveSyncException($"You already have a backup called \"{name}\". Pick another name.");

        folderName ??= ChooseFolderName(targetPath, name, existing);
        string root = Path.Combine(targetPath, folderName);
        var sources = new List<SourceFolder>();
        foreach (string folder in sourceFolders)
        {
            string path = Path.TrimEndingDirectorySeparator(Path.GetFullPath(folder));
            if (sources.Any(s => s.Path.Equals(path, StringComparison.OrdinalIgnoreCase)))
                continue;
            CheckFolder(path, root, existing);
            sources.Add(new SourceFolder(UniqueName(path, sources.Select(s => s.Name)), path));
        }
        if (sources.Count == 0)
            throw new DriveSyncException("Pick at least one folder to back up.");
        CheckDestination(root, existing, except: null);

        string driveId = BackupLocation.ReadDriveId(root) ?? Guid.NewGuid().ToString("D");
        if (existing.FirstOrDefault(b => b.DriveId.Equals(driveId, StringComparison.OrdinalIgnoreCase)) is { } same)
            throw new DriveSyncException($"That backup is already in DriveSync as \"{same.Name}\".");
        var location = BackupLocation.Create(root, driveId);
        return new BackupProfile
        {
            Name = name,
            DriveId = driveId,
            BackupFolderName = folderName,
            LastKnownRoot = location.Root,
            Sources = sources,
        };
    }

    /// <summary>
    /// Checks a folder before it's added to a backup whose files go to <paramref name="backupRoot"/>.
    /// A folder may be in several backups (two drives, two copies), but no folder may contain a backup.
    /// </summary>
    public static void CheckFolder(string folder, string backupRoot, IReadOnlyList<BackupProfile> allBackups)
    {
        string path = Path.TrimEndingDirectorySeparator(Path.GetFullPath(folder));
        if (!Directory.Exists(path))
            throw new DriveSyncException($"{path} doesn't exist.");
        if (IsSameOrInside(backupRoot, path) || IsSameOrInside(path, backupRoot))
            throw new DriveSyncException("The backup can't be inside a folder you're backing up, or the other way round.");
        foreach (var other in allBackups)
        {
            if (other.LastKnownRoot is { } otherRoot && (IsSameOrInside(otherRoot, path) || IsSameOrInside(path, otherRoot)))
                throw new DriveSyncException($"{path} holds the backup \"{other.Name}\", or is inside it. Backing it up would copy a backup into a backup.");
        }
    }

    /// <summary>Checks that a backup destination isn't inside a folder that any backup copies.</summary>
    public static void CheckDestination(string backupRoot, IReadOnlyList<BackupProfile> allBackups, BackupProfile? except)
    {
        foreach (var other in allBackups.Where(b => except is null || b.Id != except.Id))
        {
            foreach (var source in other.Sources)
            {
                if (IsSameOrInside(backupRoot, source.Path))
                    throw new DriveSyncException($"That place is inside {source.Path}, which \"{other.Name}\" backs up. Pick somewhere outside your backed-up folders.");
            }
            if (other.LastKnownRoot is { } otherRoot && Path.TrimEndingDirectorySeparator(otherRoot).Equals(Path.TrimEndingDirectorySeparator(backupRoot), StringComparison.OrdinalIgnoreCase))
                throw new DriveSyncException($"\"{other.Name}\" already uses that folder.");
        }
    }

    /// <summary>A folder name on the drive for this backup's files, unique among the backup's sources and removed folders.</summary>
    public static string UniqueName(string path, IEnumerable<string> taken)
    {
        var names = new HashSet<string>(taken, KeyPath.Comparer);
        string name = Path.GetFileName(path);
        if (string.IsNullOrEmpty(name))
            name = path[..1] + "-drive";
        string unique = name;
        for (int i = 2; names.Contains(unique); i++)
            unique = $"{name} {i}";
        return unique;
    }

    /// <summary>True when both paths are on the same drive, in which case the "backup" doesn't protect against a disk failure.</summary>
    public static bool IsOnSameDrive(string a, string b) =>
        string.Equals(Path.GetPathRoot(Path.GetFullPath(a)), Path.GetPathRoot(Path.GetFullPath(b)), StringComparison.OrdinalIgnoreCase);

    /// <summary>The folder a new backup called <paramref name="name"/> would get on <paramref name="target"/>.</summary>
    public static string PlannedFolder(string target, string name, IReadOnlyList<BackupProfile> existing) =>
        Path.Combine(target, ChooseFolderName(Path.GetFullPath(target), name, existing));

    static string ChooseFolderName(string targetPath, string name, IReadOnlyList<BackupProfile> existing)
    {
        bool Taken(string folder)
        {
            string root = Path.Combine(targetPath, folder);
            string? id = BackupLocation.ReadDriveId(root);
            return existing.Any(b => (id is not null && b.DriveId.Equals(id, StringComparison.OrdinalIgnoreCase))
                || (b.LastKnownRoot is { } r && Path.TrimEndingDirectorySeparator(r).Equals(root, StringComparison.OrdinalIgnoreCase)));
        }

        if (!Taken(DefaultFolderName))
            return DefaultFolderName;
        string candidate = $"{DefaultFolderName}-{Slug(name)}";
        for (int i = 2; Taken(candidate); i++)
            candidate = $"{DefaultFolderName}-{Slug(name)}-{i}";
        return candidate;
    }

    static string Slug(string name)
    {
        var text = new StringBuilder();
        foreach (char c in name.ToLowerInvariant())
        {
            if (char.IsAsciiLetterOrDigit(c))
                text.Append(c);
            else if (text.Length > 0 && text[^1] != '-')
                text.Append('-');
        }
        string slug = text.ToString().Trim('-');
        if (slug.Length > 30)
            slug = slug[..30].TrimEnd('-');
        return slug.Length == 0 ? "backup" : slug;
    }

    internal static bool IsSameOrInside(string path, string folder)
    {
        path = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        folder = Path.TrimEndingDirectorySeparator(Path.GetFullPath(folder));
        string prefix = folder.EndsWith('\\') ? folder : folder + "\\";
        return path.Equals(folder, StringComparison.OrdinalIgnoreCase) || path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase);
    }
}
