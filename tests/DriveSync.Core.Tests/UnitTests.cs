using System.Text;
using DriveSync.Core.IO;
using DriveSync.Core.Model;
using DriveSync.Core.RecycleBin;
using DriveSync.Core.Scanning;
using DriveSync.Core.Storage;

namespace DriveSync.Core.Tests;

public class ExclusionMatcherTests
{
    static readonly ExclusionMatcher Matcher = new([.. BackupProfile.DefaultExclusions, "node_modules", @"Projects\*\bin"]);

    [Theory]
    [InlineData("desktop.ini", @"desktop.ini", true)]
    [InlineData("~$report.docx", @"Work\~$report.docx", true)]
    [InlineData("cache.TMP", @"cache.TMP", true)]
    [InlineData("half.docx.dstmp", @"half.docx.dstmp", true)]
    [InlineData("bin", @"Projects\app\bin", true)]
    [InlineData("bin", @"Other\bin", false)]
    [InlineData("report.docx", @"Work\report.docx", false)]
    public void Matches_names_and_paths(string name, string relative, bool expected) =>
        Assert.Equal(expected, Matcher.IsExcluded(name, relative));

    [Fact]
    public void Keys_are_excluded_when_any_folder_on_their_path_is() =>
        Assert.True(Matcher.IsExcludedKey(@"Desktop\web\node_modules\react\index.js"));
}

public class RecycleBinIndexTests
{
    [Fact]
    public void Reads_a_windows_10_index_file()
    {
        string dir = Directory.CreateTempSubdirectory("ds-bin-").FullName;
        try
        {
            const string original = @"C:\Users\me\Desktop\Old Projects";
            var deleted = new DateTime(2026, 10, 1, 12, 30, 0, DateTimeKind.Utc);
            var bytes = new List<byte>();
            bytes.AddRange(BitConverter.GetBytes(2L));
            bytes.AddRange(BitConverter.GetBytes(4096L));
            bytes.AddRange(BitConverter.GetBytes(deleted.ToFileTimeUtc()));
            bytes.AddRange(BitConverter.GetBytes(original.Length + 1));
            bytes.AddRange(Encoding.Unicode.GetBytes(original + "\0"));
            File.WriteAllBytes(Path.Combine(dir, "$IAB12CD"), [.. bytes]);

            var item = Assert.Single(RecycleBinIndex.ReadFolder(dir));

            Assert.Equal(original, item.OriginalPath);
            Assert.Equal(4096, item.Size);
            Assert.Equal(deleted, item.DeletedUtc);
            Assert.Equal(Path.Combine(dir, "$RAB12CD"), item.DataPath);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void Finds_files_inside_a_recycled_folder()
    {
        var lookup = new RecycledLookup([new RecycledItem(@"C:\Users\me\Desktop\Old", DateTime.UtcNow, 0, "", @"C:\$Recycle.Bin\S-1\$R123")]);

        var hit = lookup.Find(@"C:\Users\me\Desktop\Old\sub\a.txt");

        Assert.NotNull(hit);
        Assert.Equal(@"C:\$Recycle.Bin\S-1\$R123\sub\a.txt", hit.Value.DataPath);
        Assert.Null(lookup.Find(@"C:\Users\me\Desktop\Older\a.txt"));
    }
}

public class CatalogTests
{
    [Fact]
    public void Records_survive_reopening_and_can_be_queried_by_folder()
    {
        string dir = Directory.CreateTempSubdirectory("ds-cat-").FullName;
        string path = Path.Combine(dir, "catalog.db");
        try
        {
            using (var catalog = Catalog.Open(path))
            using (catalog.Batch())
            {
                foreach (string key in new[] { @"Desktop\a.txt", @"Desktop\Sub\b.txt", @"Desktop_old\c.txt", @"Docs\d_1.txt" })
                    catalog.Save(new FileRecord { Key = key, Size = 1, ModifiedUtcTicks = 2, Hash = "AB", State = FileState.Active, StateChangedUtc = DateTime.UtcNow });
            }

            using var reopened = Catalog.Open(path);
            Assert.Equal(4, reopened.LoadAll().Count);
            Assert.Equal("AB", reopened.Get(@"desktop\A.TXT")!.Hash);
            Assert.Equal([@"Desktop\a.txt", @"Desktop\Sub\b.txt"], reopened.Query(underKey: "Desktop").Select(r => r.Key));
            Assert.Single(reopened.Query(underKey: @"Docs\d_1.txt"));
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            Directory.Delete(dir, recursive: true);
        }
    }
}

public class FileHasherTests
{
    [Fact]
    public async Task Copy_and_checksum_in_one_pass_match_a_separate_checksum()
    {
        string dir = Directory.CreateTempSubdirectory("ds-hash-").FullName;
        try
        {
            string source = Path.Combine(dir, "big.bin");
            var data = new byte[3 * FileHasher.BufferSize + 12345];
            new Random(7).NextBytes(data);
            File.WriteAllBytes(source, data);

            string hash = await FileHasher.CopyWithHashAsync(source, Path.Combine(dir, "copy.bin"));

            Assert.Equal(FileHasher.HashFile(source), hash);
            Assert.Equal(data, File.ReadAllBytes(Path.Combine(dir, "copy.bin")));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }
}

/// <summary>Checks the real Windows Recycle Bin code without putting anything in it.</summary>
public class WindowsRecycleBinTests
{
    [Fact]
    public void The_system_drive_has_a_recycle_bin_and_it_can_be_listed()
    {
        var bin = new WindowsRecycleBin();
        string temp = Path.GetTempPath();

        Assert.True(bin.IsSupported(temp));
        _ = bin.List(Path.GetPathRoot(temp)!); // reads the real index without throwing
    }

    [Fact]
    public void Network_paths_have_no_recycle_bin() =>
        Assert.False(new WindowsRecycleBin().IsSupported(@"\\server\share\file.txt"));

    [Fact]
    public void Paths_too_long_for_the_recycle_bin_are_refused_not_deleted()
    {
        string dir = Directory.CreateTempSubdirectory("ds-long-").FullName;
        try
        {
            string path = Path.Combine(dir, new string('x', 120), new string('y', 120), "file.txt");
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, "keep me");

            var outcome = new WindowsRecycleBin().Recycle([path]);

            Assert.Equal(RecycleOutcome.NotSupported, outcome[path]);
            Assert.True(File.Exists(path));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    /// <summary>
    /// Actually sends a small file to the real Recycle Bin. Off by default so test runs leave the bin alone;
    /// set DRIVESYNC_REAL_RECYCLE_TEST=1 to run it.
    /// </summary>
    [Fact]
    public void Sends_a_file_to_the_real_recycle_bin_when_enabled()
    {
        if (Environment.GetEnvironmentVariable("DRIVESYNC_REAL_RECYCLE_TEST") != "1")
            return;
        string dir = Directory.CreateTempSubdirectory("ds-real-").FullName;
        string path = Path.Combine(dir, $"DriveSync self-test {DateTime.Now:yyyyMMdd-HHmmss}.txt");
        File.WriteAllText(path, "DriveSync Recycle Bin self-test. Safe to delete.");
        var bin = new WindowsRecycleBin();

        var outcome = bin.Recycle([path]);

        Assert.Equal(RecycleOutcome.Recycled, outcome[path]);
        Assert.False(File.Exists(path));
        var item = Assert.Single(bin.List(Path.GetPathRoot(path)!), i => i.OriginalPath.Equals(path, StringComparison.OrdinalIgnoreCase));
        Assert.True(File.Exists(item.DataPath));
        Directory.Delete(dir);
    }
}
