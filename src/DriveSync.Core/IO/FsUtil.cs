namespace DriveSync.Core.IO;

internal static class FsUtil
{
    /// <summary>Suffix of DriveSync's own unfinished copies. Nothing else ever uses it.</summary>
    public const string TempSuffix = ".dstmp";

    public static void EnsureParent(string path)
    {
        string? dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);
    }

    /// <summary>Returns <paramref name="path"/>, or "name (2).ext", "name (3).ext"... if it's taken.</summary>
    public static string UniquePath(string path)
    {
        if (!Exists(path))
            return path;
        string dir = Path.GetDirectoryName(path)!, name = Path.GetFileNameWithoutExtension(path), ext = Path.GetExtension(path);
        for (int i = 2; ; i++)
        {
            string candidate = Path.Combine(dir, $"{name} ({i}){ext}");
            if (!Exists(candidate))
                return candidate;
        }
    }

    public static bool Exists(string path) => File.Exists(path) || Directory.Exists(path);

    /// <summary>Moves a file, creating the destination folder. Never overwrites.</summary>
    public static void MoveFile(string from, string to)
    {
        EnsureParent(to);
        ClearReadOnly(from);
        File.Move(from, to, overwrite: false);
    }

    public static void ClearReadOnly(string path)
    {
        var attributes = File.GetAttributes(path);
        if (attributes.HasFlag(FileAttributes.ReadOnly))
            File.SetAttributes(path, attributes & ~FileAttributes.ReadOnly);
    }

    /// <summary>Removes one of DriveSync's own unfinished copies. Refuses any other file.</summary>
    public static void TryRemoveOwnTemp(string tempPath)
    {
        if (!tempPath.EndsWith(TempSuffix, StringComparison.OrdinalIgnoreCase))
            return;
        try
        {
            File.Delete(tempPath);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
        }
    }

    /// <summary>
    /// Removes empty folders from <paramref name="dir"/> upwards, stopping below <paramref name="stopAt"/>.
    /// Used only inside the backup folder. A non-recursive delete refuses folders that hold anything, so no file can be lost.
    /// </summary>
    public static void RemoveEmptyFoldersUpTo(string? dir, string stopAt)
    {
        string limit = Path.TrimEndingDirectorySeparator(stopAt);
        while (dir is not null && dir.Length > limit.Length && dir.StartsWith(limit, StringComparison.OrdinalIgnoreCase))
        {
            try
            {
                if (Directory.EnumerateFileSystemEntries(dir).Any())
                    return;
                Directory.Delete(dir, recursive: false);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                return;
            }
            dir = Path.GetDirectoryName(dir);
        }
    }

    public static string FormatSize(long bytes) => Formatting.Size(bytes);
}
