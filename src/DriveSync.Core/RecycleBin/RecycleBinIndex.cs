using System.Security.Principal;
using System.Text;

namespace DriveSync.Core.RecycleBin;

/// <summary>
/// Reads the Recycle Bin's own index files. For every recycled item Windows keeps a <c>$I...</c> file
/// (original path, size, deletion time) next to a <c>$R...</c> file or folder holding the contents.
/// </summary>
public static class RecycleBinIndex
{
    /// <summary>The current user's bin folder on a drive, e.g. <c>C:\$Recycle.Bin\S-1-5-21-...</c>.</summary>
    public static string UserBinFolder(string driveRoot) =>
        Path.Combine(driveRoot, "$Recycle.Bin", WindowsIdentity.GetCurrent().User?.Value ?? "");

    public static IReadOnlyList<RecycledItem> Read(string driveRoot) => ReadFolder(UserBinFolder(driveRoot));

    public static IReadOnlyList<RecycledItem> ReadFolder(string binFolder)
    {
        var items = new List<RecycledItem>();
        string[] infoFiles;
        try
        {
            infoFiles = Directory.GetFiles(binFolder, "$I*");
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return items;
        }
        foreach (string info in infoFiles)
        {
            if (Parse(info) is { } item)
                items.Add(item);
        }
        return items;
    }

    /// <summary>Parses one <c>$I</c> file. Version 1 is Windows Vista to 8.1; version 2 is Windows 10 and later.</summary>
    public static RecycledItem? Parse(string infoPath)
    {
        byte[] bytes;
        try
        {
            bytes = File.ReadAllBytes(infoPath);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return null;
        }
        if (bytes.Length < 24)
            return null;

        long version = BitConverter.ToInt64(bytes, 0);
        long size = BitConverter.ToInt64(bytes, 8);
        long fileTime = BitConverter.ToInt64(bytes, 16);
        string? original = version switch
        {
            2 when bytes.Length >= 28 => Decode(bytes, 28, Math.Max(0, BitConverter.ToInt32(bytes, 24)) * 2),
            1 => Decode(bytes, 24, 520),
            _ => null,
        };
        if (string.IsNullOrEmpty(original))
            return null;

        string name = Path.GetFileName(infoPath);
        string dataPath = Path.Combine(Path.GetDirectoryName(infoPath)!, "$R" + name[2..]);
        DateTime deleted = fileTime > 0 ? DateTime.FromFileTimeUtc(fileTime) : DateTime.MinValue;
        return new RecycledItem(original, deleted, size, infoPath, dataPath);
    }

    static string Decode(byte[] bytes, int offset, int maxBytes)
    {
        int length = Math.Min(maxBytes, bytes.Length - offset) & ~1;
        string text = Encoding.Unicode.GetString(bytes, offset, length);
        int end = text.IndexOf('\0');
        return end < 0 ? text : text[..end];
    }
}
