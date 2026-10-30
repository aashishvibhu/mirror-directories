using DriveSync.Core.IO;
using DriveSync.Core.Model;
using DriveSync.Core.RecycleBin;

namespace DriveSync.Core.Engine;

public sealed partial class DriveSyncSession
{
    /// <summary>Files and bytes on the backup for each backed-up or removed folder.</summary>
    public Dictionary<string, (int Files, long Bytes)> FolderTotals() => Catalog.TotalsByFolder();

    /// <summary>
    /// Sends the files of a folder that was removed from this backup to the backup drive's Recycle Bin, as one item.
    /// On a drive without a Recycle Bin they go to the drive's trash folder instead. Nothing is deleted.
    /// </summary>
    public DecisionResult RecycleRemovedFolder(string folderName)
    {
        if (Profile.Sources.Any(s => s.Name.Equals(folderName, StringComparison.OrdinalIgnoreCase)))
            throw new DriveSyncException($"{folderName} is still backed up. Remove it from the backup first.");
        var result = new DecisionResult();
        string path = Location.PathFor(folderName);
        if (Directory.Exists(path))
        {
            var outcome = _bin.IsSupported(Location.Root) ? _bin.Recycle([path])[path] : RecycleOutcome.NotSupported;
            if (outcome == RecycleOutcome.NotSupported)
            {
                try
                {
                    string destination = FsUtil.UniquePath(Path.Combine(Location.TrashDir, NewStamp(), folderName));
                    FsUtil.EnsureParent(destination);
                    Directory.Move(path, destination);
                }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException)
                {
                    result.Problems.Add(new Problem(folderName, $"Couldn't move the folder to the drive's trash folder: {e.Message}"));
                    return result;
                }
            }
            else if (outcome != RecycleOutcome.Recycled)
            {
                result.Problems.Add(new Problem(folderName, Explain(outcome, onBackupDrive: true)));
                return result;
            }
        }

        using (Catalog.Batch())
        {
            foreach (var record in Catalog.Query(underKey: folderName).Where(r => r.State != FileState.Deleted))
            {
                SetState(record, FileState.Deleted, "removed-from-backup", "folder no longer backed up");
                result.Applied++;
            }
            Catalog.Log("folder-recycled", folderName);
        }
        return result;
    }
}
