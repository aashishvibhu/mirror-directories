using System.Globalization;

namespace DriveSync.Core.RecycleBin;

/// <summary>
/// A stand-in Recycle Bin that keeps items in an ordinary folder. Used by tests and by the app's demo mode,
/// so trying DriveSync out never touches the real Recycle Bin. It follows the real bin's rules:
/// items it has no room for are refused, never discarded. Its index is saved in the folder,
/// so it remembers its items between runs.
/// </summary>
public sealed class FolderRecycleBin : IRecycleBin
{
    readonly object _gate = new();
    readonly string _indexPath;

    public FolderRecycleBin(string folder)
    {
        Folder = Path.GetFullPath(folder);
        _indexPath = Path.Combine(Folder, "index.tsv");
        if (!File.Exists(_indexPath))
            return;
        foreach (string line in File.ReadAllLines(_indexPath))
        {
            string[] parts = line.Split('\t');
            if (parts.Length == 4
                && long.TryParse(parts[1], CultureInfo.InvariantCulture, out long ticks)
                && long.TryParse(parts[2], CultureInfo.InvariantCulture, out long size))
            {
                Items.Add(new RecycledItem(parts[0], new DateTime(ticks, DateTimeKind.Utc), size, _indexPath, parts[3]));
            }
        }
    }

    public string Folder { get; }

    public bool Supported { get; set; } = true;

    public long MaxBytes { get; set; } = long.MaxValue;

    public List<RecycledItem> Items { get; } = [];

    public bool IsSupported(string path) => Supported;

    public IReadOnlyDictionary<string, RecycleOutcome> Recycle(IReadOnlyList<string> paths)
    {
        lock (_gate)
        {
            var outcomes = new Dictionary<string, RecycleOutcome>(StringComparer.OrdinalIgnoreCase);
            if (!Supported)
            {
                foreach (string p in paths)
                    outcomes[p] = RecycleOutcome.NotSupported;
                return outcomes;
            }

            long used = Items.Where(i => File.Exists(i.DataPath) || Directory.Exists(i.DataPath)).Sum(i => i.Size);
            var accepted = new List<string>();
            foreach (string p in paths)
            {
                if (SizeOf(p) > MaxBytes)
                    outcomes[p] = RecycleOutcome.TooLarge;
                else
                    accepted.Add(p);
            }
            if (used + accepted.Sum(SizeOf) > MaxBytes)
            {
                foreach (string p in accepted)
                    outcomes[p] = RecycleOutcome.BinFull;
                return outcomes;
            }

            Directory.CreateDirectory(Folder);
            using var index = File.AppendText(_indexPath);
            foreach (string p in accepted)
            {
                long size = SizeOf(p);
                string data = Path.Combine(Folder, $"$R{Guid.NewGuid():N}{Path.GetExtension(p)}");
                if (File.Exists(p))
                    File.Move(p, data);
                else if (Directory.Exists(p))
                    Directory.Move(p, data);
                else
                {
                    outcomes[p] = RecycleOutcome.Failed;
                    continue;
                }
                var item = new RecycledItem(Path.GetFullPath(p), DateTime.UtcNow, size, _indexPath, data);
                Items.Add(item);
                index.WriteLine(string.Join('\t', item.OriginalPath, item.DeletedUtc.Ticks.ToString(CultureInfo.InvariantCulture),
                    size.ToString(CultureInfo.InvariantCulture), data));
                outcomes[p] = RecycleOutcome.Recycled;
            }
            return outcomes;
        }
    }

    public IReadOnlyList<RecycledItem> List(string driveRoot)
    {
        lock (_gate)
            return [.. Items.Where(i => i.OriginalPath.StartsWith(driveRoot, StringComparison.OrdinalIgnoreCase))];
    }

    public bool Holds(string originalPath)
    {
        lock (_gate)
            return Items.Any(i => i.OriginalPath.Equals(Path.GetFullPath(originalPath), StringComparison.OrdinalIgnoreCase));
    }

    static long SizeOf(string path) =>
        File.Exists(path) ? new FileInfo(path).Length
        : Directory.Exists(path) ? Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories).Sum(f => new FileInfo(f).Length)
        : 0;
}
