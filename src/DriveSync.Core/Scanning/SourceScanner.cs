using DriveSync.Core.Model;

namespace DriveSync.Core.Scanning;

public sealed record ScannedFile(string Key, string FullPath, long Size, long ModifiedUtcTicks);

/// <summary>What a scan of the PC found, and the places it couldn't see into.</summary>
public sealed class ScanResult
{
    public Dictionary<string, ScannedFile> Files { get; } = new(KeyPath.Comparer);

    /// <summary>
    /// Keys of files and folders the scan couldn't see: unreadable folders, missing sources, links, cloud-only files.
    /// Backed-up files under these are never treated as missing.
    /// </summary>
    public HashSet<string> BlindSpots { get; } = new(KeyPath.Comparer);

    public List<string> Warnings { get; } = [];

    public int CloudOnlySkipped { get; set; }

    public int LinksSkipped { get; set; }

    public bool IsBlind(string key)
    {
        string? current = key;
        while (current is not null)
        {
            if (BlindSpots.Contains(current))
                return true;
            int i = current.LastIndexOf('\\');
            current = i < 0 ? null : current[..i];
        }
        return false;
    }
}

/// <summary>Lists the files in the source folders on the PC, reading only names, sizes and dates.</summary>
public sealed class SourceScanner(ExclusionMatcher exclusions, bool skipCloudOnly)
{
    // Attributes OneDrive and other cloud providers set on files that aren't stored on the PC.
    const FileAttributes RecallOnOpen = (FileAttributes)0x40000;
    const FileAttributes RecallOnDataAccess = (FileAttributes)0x400000;
    const FileAttributes CloudOnly = FileAttributes.Offline | RecallOnOpen | RecallOnDataAccess;

    static readonly EnumerationOptions OneLevel = new()
    {
        RecurseSubdirectories = false,
        IgnoreInaccessible = false,
        AttributesToSkip = 0,
        ReturnSpecialDirectories = false,
    };

    public ScanResult Scan(IEnumerable<SourceFolder> sources, Action<int>? onFilesFound = null, CancellationToken ct = default)
    {
        var result = new ScanResult();
        foreach (var source in sources)
            ScanSubtree(source, "", result, onFilesFound, ct);
        return result;
    }

    /// <summary>Scans one file or folder inside a source, adding to <paramref name="into"/>.</summary>
    public void ScanSubtree(SourceFolder source, string relative, ScanResult into, Action<int>? onFilesFound = null, CancellationToken ct = default)
    {
        string root = Path.TrimEndingDirectorySeparator(source.Path);
        string startPath = relative.Length == 0 ? source.Path : Path.Combine(root, relative);
        string startKey = KeyPath.Combine(source.Name, relative);

        if (relative.Length > 0 && File.Exists(startPath))
        {
            AddFile(new FileInfo(startPath), startKey, into);
            return;
        }
        if (!Directory.Exists(startPath))
        {
            into.BlindSpots.Add(startKey);
            into.Warnings.Add($"Couldn't find {startPath}. Nothing in it is treated as deleted.");
            return;
        }

        var folders = new Stack<(string Path, string Key)>();
        folders.Push((startPath, startKey));
        while (folders.Count > 0)
        {
            ct.ThrowIfCancellationRequested();
            var (folder, folderKey) = folders.Pop();
            List<FileSystemInfo> items;
            try
            {
                items = [.. new DirectoryInfo(folder).EnumerateFileSystemInfos("*", OneLevel)];
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Security.SecurityException)
            {
                into.BlindSpots.Add(folderKey);
                into.Warnings.Add($"Couldn't read {folder}: {e.Message} Nothing in it is treated as deleted.");
                continue;
            }

            foreach (var item in items)
            {
                string key = folderKey + "\\" + item.Name;
                if (exclusions.IsExcluded(item.Name, KeyPath.Relative(key)))
                    continue;
                if (item is DirectoryInfo dir)
                {
                    if (dir.Attributes.HasFlag(FileAttributes.ReparsePoint))
                    {
                        // Junctions and folder links can point back up the tree; follow neither.
                        into.LinksSkipped++;
                        into.BlindSpots.Add(key);
                        continue;
                    }
                    folders.Push((dir.FullName, key));
                }
                else if (item is FileInfo file)
                {
                    AddFile(file, key, into);
                    if (into.Files.Count % 500 == 0)
                        onFilesFound?.Invoke(into.Files.Count);
                }
            }
        }
        onFilesFound?.Invoke(into.Files.Count);
    }

    void AddFile(FileInfo file, string key, ScanResult into)
    {
        if (skipCloudOnly && (file.Attributes & CloudOnly) != 0)
        {
            into.CloudOnlySkipped++;
            into.BlindSpots.Add(key);
            return;
        }
        into.Files[key] = new ScannedFile(key, file.FullName, file.Length, file.LastWriteTimeUtc.Ticks);
    }
}
