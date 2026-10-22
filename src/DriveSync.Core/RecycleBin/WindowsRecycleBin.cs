using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32;

namespace DriveSync.Core.RecycleBin;

/// <summary>
/// Sends items to the Windows Recycle Bin. Before each move it checks that the bin can take the items
/// without Windows erasing older ones; if not, nothing is moved.
/// </summary>
public sealed class WindowsRecycleBin : IRecycleBin
{
    const int MaxShellPath = 260;
    const int ChunkSize = 200;

    sealed record VolumeBin(string Root, long MaxBytes);

    public bool IsSupported(string path) => GetVolumeBin(path) is not null;

    public IReadOnlyDictionary<string, RecycleOutcome> Recycle(IReadOnlyList<string> paths)
    {
        var outcomes = new Dictionary<string, RecycleOutcome>(StringComparer.OrdinalIgnoreCase);
        foreach (var group in paths.GroupBy(p => Path.GetPathRoot(Path.GetFullPath(p)) ?? "", StringComparer.OrdinalIgnoreCase))
        {
            var bin = GetVolumeBin(group.Key);
            var accepted = new List<(string Path, long Size)>();
            foreach (string path in group)
            {
                if (bin is null || Path.GetFullPath(path).Length >= MaxShellPath)
                {
                    outcomes[path] = RecycleOutcome.NotSupported;
                    continue;
                }
                long size = SizeOf(path);
                if (size > bin.MaxBytes)
                    outcomes[path] = RecycleOutcome.TooLarge;
                else
                    accepted.Add((path, size));
            }
            if (bin is null || accepted.Count == 0)
                continue;

            long used = CurrentSize(bin.Root) ?? 0;
            if (used + accepted.Sum(a => a.Size) > bin.MaxBytes)
            {
                foreach (var (path, _) in accepted)
                    outcomes[path] = RecycleOutcome.BinFull;
                continue;
            }

            foreach (var chunk in accepted.Chunk(ChunkSize))
                SendToBin(chunk.Select(c => Path.GetFullPath(c.Path)));
            foreach (var (path, _) in accepted)
                outcomes[path] = File.Exists(path) || Directory.Exists(path) ? RecycleOutcome.Failed : RecycleOutcome.Recycled;
        }
        return outcomes;
    }

    public IReadOnlyList<RecycledItem> List(string driveRoot) => RecycleBinIndex.Read(driveRoot);

    /// <summary>The bin for the volume holding <paramref name="path"/>, or null if Windows won't recycle there.</summary>
    static VolumeBin? GetVolumeBin(string path)
    {
        string? root = Path.GetPathRoot(Path.GetFullPath(path));
        if (string.IsNullOrEmpty(root) || root.StartsWith(@"\\", StringComparison.Ordinal))
            return null; // network folders have no Recycle Bin

        DriveInfo drive;
        try
        {
            drive = new DriveInfo(root);
            // Windows gives USB sticks (removable drives) no Recycle Bin. External hard drives report as fixed and do have one.
            if (!drive.IsReady || drive.DriveType != DriveType.Fixed)
                return null;
        }
        catch (Exception e) when (e is IOException or ArgumentException or UnauthorizedAccessException)
        {
            return null;
        }

        // Without a configured limit, assume 5% of the drive. That's at or below Windows' default,
        // so the room check errs towards asking the user rather than letting old items be erased.
        long maxBytes = drive.TotalSize / 20;
        var volumeName = new StringBuilder(64);
        if (GetVolumeNameForVolumeMountPoint(root, volumeName, volumeName.Capacity))
        {
            string name = volumeName.ToString();
            int open = name.IndexOf('{'), close = name.IndexOf('}');
            if (open >= 0 && close > open)
            {
                using var key = Registry.CurrentUser.OpenSubKey(
                    @"Software\Microsoft\Windows\CurrentVersion\Explorer\BitBucket\Volume\" + name[open..(close + 1)]);
                if (key?.GetValue("NukeOnDelete") is int nuke && nuke != 0)
                    return null; // "Don't move files to the Recycle Bin" is turned on for this drive
                if (key?.GetValue("MaxCapacity") is int megabytes && megabytes > 0)
                    maxBytes = megabytes * 1024L * 1024L;
            }
        }
        return new VolumeBin(root, maxBytes);
    }

    static long? CurrentSize(string root)
    {
        var info = new SHQUERYRBINFO { cbSize = Marshal.SizeOf<SHQUERYRBINFO>() };
        return SHQueryRecycleBin(root, ref info) == 0 ? info.i64Size : null;
    }

    static long SizeOf(string path)
    {
        if (File.Exists(path))
            return new FileInfo(path).Length;
        if (Directory.Exists(path))
            return new DirectoryInfo(path).EnumerateFiles("*", SearchOption.AllDirectories).Sum(f => f.Length);
        return 0;
    }

    static void SendToBin(IEnumerable<string> paths)
    {
        var op = new SHFILEOPSTRUCT
        {
            wFunc = FO_DELETE,
            pFrom = string.Join('\0', paths) + "\0\0",
            // ALLOWUNDO sends to the bin. WANTNUKEWARNING makes Windows ask, rather than silently delete,
            // if anything were still about to be destroyed instead of recycled.
            fFlags = FOF_ALLOWUNDO | FOF_NOCONFIRMATION | FOF_SILENT | FOF_NOERRORUI | FOF_WANTNUKEWARNING,
        };
        SHFileOperation(ref op);
    }

    const uint FO_DELETE = 3;
    const ushort FOF_SILENT = 0x0004;
    const ushort FOF_NOCONFIRMATION = 0x0010;
    const ushort FOF_ALLOWUNDO = 0x0040;
    const ushort FOF_NOERRORUI = 0x0400;
    const ushort FOF_WANTNUKEWARNING = 0x4000;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct SHFILEOPSTRUCT
    {
        public IntPtr hwnd;
        public uint wFunc;
        public string pFrom;
        public string? pTo;
        public ushort fFlags;
        [MarshalAs(UnmanagedType.Bool)] public bool fAnyOperationsAborted;
        public IntPtr hNameMappings;
        public string? lpszProgressTitle;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct SHQUERYRBINFO
    {
        public int cbSize;
        public long i64Size;
        public long i64NumItems;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    static extern int SHFileOperation(ref SHFILEOPSTRUCT op);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    static extern int SHQueryRecycleBin(string rootPath, ref SHQUERYRBINFO info);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    static extern bool GetVolumeNameForVolumeMountPoint(string mountPoint, StringBuilder volumeName, int length);
}
