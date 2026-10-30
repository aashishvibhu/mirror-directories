using DriveSync.Core.Model;

namespace DriveSync.Core;

/// <summary>Changes to an existing backup's folders. The caller saves the backup list afterwards.</summary>
public static class BackupEditor
{
    /// <summary>
    /// Adds a folder to <paramref name="backup"/>. A folder removed earlier comes back under its old name,
    /// so its files already on the drive are reused rather than copied again.
    /// </summary>
    public static SourceFolder AddFolder(BackupProfile backup, string folder, IReadOnlyList<BackupProfile> allBackups)
    {
        string path = Path.TrimEndingDirectorySeparator(Path.GetFullPath(folder));
        if (backup.Sources.Any(s => s.Path.Equals(path, StringComparison.OrdinalIgnoreCase)))
            throw new DriveSyncException($"{path} is already in this backup.");
        BackupSetup.CheckFolder(path, backup.LastKnownRoot ?? Path.Combine(Path.GetPathRoot(path) ?? "", backup.BackupFolderName), allBackups);

        var removed = backup.RemovedFolders.FirstOrDefault(r => r.Path.Equals(path, StringComparison.OrdinalIgnoreCase));
        SourceFolder source;
        if (removed is not null)
        {
            backup.RemovedFolders.Remove(removed);
            source = new SourceFolder(removed.Name, path);
        }
        else
        {
            var taken = backup.Sources.Select(s => s.Name).Concat(backup.RemovedFolders.Select(r => r.Name));
            source = new SourceFolder(BackupSetup.UniqueName(path, taken), path);
        }
        backup.Sources.Add(source);
        return source;
    }

    /// <summary>
    /// Takes a folder out of <paramref name="backup"/>. Its files stay on the drive and it's listed under removed folders.
    /// Nothing is deleted anywhere.
    /// </summary>
    public static RemovedFolder RemoveFolder(BackupProfile backup, string sourceName)
    {
        var source = backup.Sources.FirstOrDefault(s => s.Name.Equals(sourceName, StringComparison.OrdinalIgnoreCase))
            ?? throw new DriveSyncException($"{sourceName} isn't in this backup.");
        if (backup.Sources.Count == 1)
            throw new DriveSyncException("A backup needs at least one folder. To stop it completely, remove the backup instead.");
        backup.Sources.Remove(source);
        var removed = new RemovedFolder(source.Name, source.Path, DateTime.UtcNow);
        backup.RemovedFolders.Add(removed);
        return removed;
    }
}
