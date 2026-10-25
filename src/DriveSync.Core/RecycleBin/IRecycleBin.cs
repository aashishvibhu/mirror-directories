namespace DriveSync.Core.RecycleBin;

public enum RecycleOutcome
{
    Recycled,

    /// <summary>The drive has no Recycle Bin (USB stick, network folder, or the bin is turned off), or the path is too long for it.</summary>
    NotSupported,

    /// <summary>The bin has no room without Windows erasing older items, so nothing was moved.</summary>
    BinFull,

    /// <summary>The item is bigger than the bin can hold; Windows would delete it permanently, so nothing was moved.</summary>
    TooLarge,

    /// <summary>Windows didn't move it, usually because it's open in another program.</summary>
    Failed,
}

/// <summary>An item in a Recycle Bin. <see cref="DataPath"/> is where its contents are stored now.</summary>
public sealed record RecycledItem(string OriginalPath, DateTime DeletedUtc, long Size, string InfoPath, string DataPath);

/// <summary>
/// The only way DriveSync removes anything. Implementations must never delete permanently:
/// if an item can't go to the Recycle Bin, it stays where it is.
/// </summary>
public interface IRecycleBin
{
    /// <summary>True when the drive holding <paramref name="path"/> has a working Recycle Bin.</summary>
    bool IsSupported(string path);

    /// <summary>Sends files or folders to the Recycle Bin. Returns an outcome for every path given.</summary>
    IReadOnlyDictionary<string, RecycleOutcome> Recycle(IReadOnlyList<string> paths);

    /// <summary>The current user's items in the Recycle Bin of the drive at <paramref name="driveRoot"/>.</summary>
    IReadOnlyList<RecycledItem> List(string driveRoot);
}

/// <summary>Finds the Recycle Bin item holding a path, including files inside a recycled folder.</summary>
public sealed class RecycledLookup
{
    readonly Dictionary<string, RecycledItem> _byOriginalPath = new(StringComparer.OrdinalIgnoreCase);

    public RecycledLookup(IEnumerable<RecycledItem> items)
    {
        foreach (var item in items)
        {
            string path = Path.TrimEndingDirectorySeparator(item.OriginalPath);
            if (!_byOriginalPath.TryGetValue(path, out var existing) || existing.DeletedUtc < item.DeletedUtc)
                _byOriginalPath[path] = item;
        }
    }

    public int Count => _byOriginalPath.Count;

    /// <summary>The newest bin item at or above <paramref name="path"/>, and where that path's contents are inside it.</summary>
    public (RecycledItem Item, string DataPath)? Find(string path)
    {
        string full = Path.TrimEndingDirectorySeparator(path);
        string? current = full;
        while (!string.IsNullOrEmpty(current))
        {
            if (_byOriginalPath.TryGetValue(current, out var item))
                return (item, current.Length == full.Length ? item.DataPath : item.DataPath + full[current.Length..]);
            current = Path.GetDirectoryName(current);
        }
        return null;
    }
}
