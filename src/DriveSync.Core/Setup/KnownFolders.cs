using System.Runtime.InteropServices;

namespace DriveSync.Core;

public sealed record FolderSuggestion(string Name, string Path);

/// <summary>Common folders the setup wizard lists as suggestions. None are selected for the user.</summary>
public static class KnownFolders
{
    static readonly Guid DownloadsId = new("374DE290-123F-4565-9164-39C4925E467B");

    public static IReadOnlyList<FolderSuggestion> Suggestions()
    {
        var list = new List<FolderSuggestion>();
        Add(list, "Desktop", Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory));
        Add(list, "Documents", Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments));
        Add(list, "Pictures", Environment.GetFolderPath(Environment.SpecialFolder.MyPictures));
        Add(list, "Music", Environment.GetFolderPath(Environment.SpecialFolder.MyMusic));
        Add(list, "Videos", Environment.GetFolderPath(Environment.SpecialFolder.MyVideos));
        Add(list, "Downloads", DownloadsPath());
        return list;
    }

    static void Add(List<FolderSuggestion> list, string name, string? path)
    {
        if (!string.IsNullOrEmpty(path) && Directory.Exists(path))
            list.Add(new FolderSuggestion(name, path));
    }

    static string? DownloadsPath()
    {
        if (SHGetKnownFolderPath(DownloadsId, 0, IntPtr.Zero, out IntPtr raw) != 0)
            return null;
        try
        {
            return Marshal.PtrToStringUni(raw);
        }
        finally
        {
            Marshal.FreeCoTaskMem(raw);
        }
    }

    [DllImport("shell32.dll")]
    static extern int SHGetKnownFolderPath([MarshalAs(UnmanagedType.LPStruct)] Guid id, uint flags, IntPtr token, out IntPtr path);
}
