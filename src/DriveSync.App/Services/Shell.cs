using System.Diagnostics;

namespace DriveSync.App.Services;

/// <summary>Hands things over to File Explorer and Windows' own settings pages.</summary>
public static class Shell
{
    /// <summary>Opens a folder (or a shell place such as <c>shell:RecycleBinFolder</c>) in File Explorer.</summary>
    public static void OpenFolder(string path) => Start("explorer.exe", $"\"{path}\"");

    /// <summary>Opens the folder holding <paramref name="path"/> with it selected; a folder is opened itself.</summary>
    public static void Reveal(string path)
    {
        if (Directory.Exists(path))
            OpenFolder(path);
        else if (File.Exists(path))
            Start("explorer.exe", $"/select,\"{path}\"");
        else if (Path.GetDirectoryName(path) is { } parent && Directory.Exists(parent))
            OpenFolder(parent);
    }

    /// <summary>Opens Windows' BitLocker page, where a drive's encryption is turned on.</summary>
    public static void OpenBitLocker() => Start("control.exe", "/name Microsoft.BitLockerDriveEncryption");

    static void Start(string file, string arguments)
    {
        try
        {
            Process.Start(new ProcessStartInfo(file, arguments) { UseShellExecute = true })?.Dispose();
        }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException or IOException)
        {
            // Explorer couldn't be started; there's nothing useful to do about it here.
        }
    }
}
