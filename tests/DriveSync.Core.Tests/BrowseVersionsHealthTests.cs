using DriveSync.Core.Engine;
using DriveSync.Core.Model;
using DriveSync.Core.Storage;
using Microsoft.Data.Sqlite;

namespace DriveSync.Core.Tests;

public class BrowseAndRestoreTests
{
    [Fact]
    public async Task Browsing_lists_folders_with_totals_then_files_with_their_state()
    {
        using var env = new TestEnv();
        env.Write(@"Desktop\Taxes\2025.pdf", "aaaa");
        env.Write(@"Desktop\Taxes\2026.pdf", "bb");
        env.Write(@"Desktop\note.txt", "c");
        env.Write(@"Documents\cv.docx", "dd");
        await env.SyncAsync();
        using (var s = env.Open())
            await s.OffloadAsync([@"Desktop\Taxes\2025.pdf"]);

        using var session = env.Open();
        var top = session.Catalog.ListFolder("");
        Assert.Equal(["Desktop", "Documents"], top.Select(e => e.Name));
        Assert.All(top, e => Assert.True(e.IsFolder));
        Assert.Equal(3, top[0].Files);
        Assert.Equal(7, top[0].Bytes);
        Assert.Equal(1, top[0].Offloaded);

        var desktop = session.Catalog.ListFolder("Desktop");
        Assert.Equal(["Taxes", "note.txt"], desktop.Select(e => e.Name));
        Assert.Equal(@"Desktop\Taxes", desktop[0].Key);
        Assert.Equal(FileState.Active, desktop[1].State);

        var taxes = session.Catalog.ListFolder(@"Desktop\Taxes");
        Assert.Equal(FileState.Archived, taxes.Single(e => e.Name == "2025.pdf").State);
        Assert.Equal("2025.pdf", Assert.Single(session.Catalog.ListFolder(@"Desktop\Taxes", FileState.Archived)).Name);
        Assert.Equal(@"Desktop\Taxes\2026.pdf", Assert.Single(session.Catalog.Search("2026")).Key);
    }

    [Fact]
    public async Task A_restore_can_be_previewed_and_reports_bytes_as_it_goes()
    {
        using var env = new TestEnv();
        env.Write(@"Desktop\a.txt", "aaaa");
        env.Write(@"Desktop\b.txt", "bbbbbb");
        env.Write(@"Desktop\c.txt", "cc");
        await env.SyncAsync();
        File.Delete(env.PcPath(@"Desktop\a.txt"));               // to restore
        env.Write(@"Desktop\b.txt", "different");                 // in the way
        using var session = env.Open();

        var plan = session.PlanRestore(new RestoreRequest(["Desktop"]));
        Assert.Equal(2, plan.ToRestore);
        Assert.Equal(1, plan.InTheWay);
        Assert.Equal(1, plan.AlreadyThere);
        Assert.Equal(10, plan.Bytes);
        Assert.False(File.Exists(env.PcPath(@"Desktop\a.txt")));

        var seen = new List<SyncProgress>();
        var result = await session.RestoreAsync(new RestoreRequest(["Desktop"]), new InlineProgress(seen.Add));
        Assert.Equal(2, result.Restored);
        Assert.Equal(10, result.BytesRestored);
        Assert.Equal(1, result.KeptBoth);
        Assert.Contains(seen, p => p.Phase == "Done" && p.BytesDone == 10 && p.BytesTotal == 10);
        Assert.Equal("bbbbbb", File.ReadAllText(env.PcPath(@"Desktop\b.txt")));
        Assert.Single(Directory.GetFiles(env.PcPath("Desktop"), "b (before restore*"));
    }

    [Fact]
    public async Task Files_of_a_folder_no_longer_backed_up_can_still_be_restored()
    {
        using var env = new TestEnv();
        env.Write(@"Documents\cv.docx", "cv");
        await env.SyncAsync();
        BackupEditor.RemoveFolder(env.Profile, "Documents");
        File.Delete(env.PcPath(@"Documents\cv.docx"));

        using var session = env.Open();
        var result = await session.RestoreAsync(new RestoreRequest(["Documents"]));

        Assert.Equal(1, result.Restored);
        Assert.Equal("cv", File.ReadAllText(env.PcPath(@"Documents\cv.docx")));
    }

    sealed class InlineProgress(Action<SyncProgress> report) : IProgress<SyncProgress>
    {
        public void Report(SyncProgress value) => report(value);
    }
}

public class VersionTests
{
    [Fact]
    public async Task Old_versions_are_listed_measured_and_removed_only_when_asked()
    {
        using var env = new TestEnv();
        env.Write(@"Desktop\essay.txt", "draft one");
        await env.SyncAsync();
        env.Write(@"Desktop\essay.txt", "draft two!");
        await env.SyncAsync();
        using var session = env.Open();

        var usage = session.GetVersionUsage();
        Assert.Equal(1, usage.Count);
        Assert.Equal(9, usage.Bytes);
        Assert.Equal(@"Desktop\essay.txt", Assert.Single(usage.Biggest).Key);
        Assert.Equal(9, session.GetStorageUsage().OldVersions);
        Assert.Empty(session.SuggestVersions(VersionCleanup.KeepNewest, 1));

        var suggested = session.SuggestVersions(VersionCleanup.KeepNewest, 0);
        var result = session.RemoveVersions(suggested);

        Assert.Equal(1, result.Applied);
        Assert.Empty(session.ListVersions(@"Desktop\essay.txt"));
        Assert.Equal(0, session.GetVersionUsage().Count);
        Assert.Single(env.Bin.Items);                                               // not erased
        Assert.Equal("draft two!", File.ReadAllText(env.BackupPath(@"Desktop\essay.txt"))); // current copy untouched
    }

    [Fact]
    public void Thinning_out_keeps_recent_versions_then_one_a_week_then_one_a_month()
    {
        using var env = new TestEnv();
        using var session = env.Open();
        var now = new DateTime(2026, 11, 16, 12, 0, 0);
        void Add(string key, DateTime when) => session.Catalog.AddVersion(
            new VersionRow($@"{when:yyyyMMdd-HHmmss}\{key}", key, when.ToString("yyyyMMdd-HHmmss"), 10, 0, ""));
        Add(@"Desktop\a.txt", now.AddDays(-1));
        Add(@"Desktop\a.txt", now.AddDays(-2));
        Add(@"Desktop\a.txt", new DateTime(2026, 9, 1, 9, 0, 0));   // Tuesday
        Add(@"Desktop\a.txt", new DateTime(2026, 9, 2, 9, 0, 0));   // same week: the newer one is kept
        Add(@"Desktop\a.txt", new DateTime(2024, 3, 5, 9, 0, 0));
        Add(@"Desktop\a.txt", new DateTime(2024, 3, 20, 9, 0, 0));  // same month: the newer one is kept
        Add(@"Desktop\b.txt", new DateTime(2026, 9, 1, 9, 0, 0));   // another file is judged on its own

        var remove = session.SuggestVersions(VersionCleanup.ThinOut, now: now);

        Assert.Equal(["20240305-090000", "20260901-090000"], remove.Where(v => v.Key == @"Desktop\a.txt").Select(v => v.Stamp).Order());
        Assert.DoesNotContain(remove, v => v.Key == @"Desktop\b.txt");
        Assert.Equal(5, session.SuggestVersions(VersionCleanup.OlderThanDays, 30, now).Count);
    }

    [Fact]
    public async Task A_catalog_from_before_version_2_is_upgraded_and_its_old_versions_listed()
    {
        using var env = new TestEnv();
        env.Write(@"Desktop\essay.txt", "draft one");
        await env.SyncAsync();
        env.Write(@"Desktop\essay.txt", "draft two!");
        await env.SyncAsync();
        string catalog = Path.Combine(env.BackupRoot, ".drivesync", "catalog.db");
        using (var db = new SqliteConnection($"Data Source={catalog};Pooling=False"))
        {
            db.Open();
            using var cmd = db.CreateCommand();
            cmd.CommandText = "DROP TABLE versions; ALTER TABLE files DROP COLUMN verified; DELETE FROM meta WHERE name = 'versions_indexed'; PRAGMA user_version = 1;";
            cmd.ExecuteNonQuery();
        }

        using var session = env.Open();

        Assert.True(session.Catalog.HasVersion2);
        var version = Assert.Single(session.ListVersions(@"Desktop\essay.txt"));
        Assert.Equal("draft one", File.ReadAllText(version.Path));
        Assert.Equal(FileState.Active, session.Catalog.Get(@"Desktop\essay.txt")!.State);
    }
}

public class HealthAndSizeTests
{
    [Fact]
    public async Task A_file_that_changes_between_the_review_and_the_copy_isnt_a_health_problem_and_is_copied_again()
    {
        using var env = new TestEnv();
        env.Write(@"Desktop\log.txt", "short");
        using (var session = env.Open())
        {
            var plan = await session.PlanSyncAsync();
            File.AppendAllText(env.PcPath(@"Desktop\log.txt"), " and then it grew"); // changed after the review
            await session.ApplyAsync(plan);

            Assert.Equal(new FileInfo(env.BackupPath(@"Desktop\log.txt")).Length, session.Catalog.Get(@"Desktop\log.txt")!.Size);
            Assert.True((await session.CheckHealthAsync(full: true)).IsHealthy);
        }
        var next = await env.Open().UsingAsync(s => s.PlanSyncAsync());
        Assert.Single(next.ChangedFiles); // the newer time is still noticed
    }

    [Fact]
    public async Task Every_sync_ends_with_a_quick_check_of_the_backup_copies()
    {
        using var env = new TestEnv();
        env.Write(@"Desktop\a.txt", "a");
        env.Write(@"Desktop\b.txt", "b");
        Assert.Equal(0, (await env.SyncAsync()).HealthIssues);
        File.Delete(env.BackupPath(@"Desktop\b.txt")); // lost on the drive

        var result = await env.SyncAsync();

        Assert.Equal(1, result.HealthIssues);
        using var session = env.Open();
        Assert.Equal(1, session.GetSummary().HealthIssues);
        Assert.Equal(1, session.GetHealthStatus().MissingOrWrongSize);
    }

    [Fact]
    public async Task The_rolling_check_finds_a_damaged_copy_and_doesnt_recheck_what_it_just_checked()
    {
        using var env = new TestEnv();
        env.Write(@"Desktop\good.txt", "good");
        env.Write(@"Desktop\photo.jpg", "pixels");
        await env.SyncAsync();
        File.WriteAllText(env.BackupPath(@"Desktop\photo.jpg"), "pix#ls"); // same size, different contents
        using var session = env.Open();

        var first = await session.VerifySliceAsync(TimeSpan.FromMinutes(1), long.MaxValue);
        Assert.Equal(2, first.Checked);
        Assert.Equal(1, first.Failed);
        var status = session.GetHealthStatus();
        Assert.Equal(@"Desktop\photo.jpg", Assert.Single(status.FailedChecksum).Key);
        Assert.Equal(1, status.VerifiedLast30Days);
        Assert.Equal(1, session.GetSummary().HealthIssues);

        var second = await session.VerifySliceAsync(TimeSpan.FromMinutes(1), long.MaxValue);
        Assert.Equal(0, second.Checked);

        // Repairing from the PC's good copy clears it.
        var report = await session.CheckHealthAsync(full: true, repair: true);
        Assert.Equal(1, report.Repaired);
        Assert.Empty(session.GetHealthStatus().FailedChecksum);
        Assert.Equal("pixels", File.ReadAllText(env.BackupPath(@"Desktop\photo.jpg")));
    }

    [Fact]
    public async Task The_rolling_check_stops_at_its_size_budget()
    {
        using var env = new TestEnv();
        for (int i = 0; i < 5; i++)
            env.Write($@"Desktop\f{i}.txt", "0123456789");
        await env.SyncAsync();
        using var session = env.Open();

        var slice = await session.VerifySliceAsync(TimeSpan.FromMinutes(1), maxBytes: 20);

        Assert.Equal(2, slice.Checked);
        Assert.Equal(3, (await session.VerifySliceAsync(TimeSpan.FromMinutes(1), long.MaxValue)).Checked);
    }
}

public class NewPcTests
{
    [Fact]
    public async Task A_backup_found_on_a_drive_can_be_used_on_a_pc_that_doesnt_have_its_files()
    {
        using var env = new TestEnv();
        env.Write(@"Desktop\a.txt", "a");
        env.Write(@"Documents\cv.docx", "cv");
        env.Profile.Name = "Personal files";
        env.Profile.MissingFilePolicy = MissingFilePolicy.AlwaysKeep;
        await env.SyncAsync();

        // The drive now carries its own settings.
        var found = Assert.Single(BackupSetup.FindBackups(env.Drive, []));
        Assert.Equal("Personal files", found.Name);
        Assert.Equal(2, found.Files);
        Assert.Equal(env.PcPath("Desktop"), found.Folders.Single(f => f.Name == "Desktop").OriginalPath);
        Assert.Empty(BackupSetup.FindBackups(env.Drive, [env.Profile])); // already known: not offered

        // On the "new PC", Desktop lives somewhere else and Documents isn't wanted.
        string newDesktop = Path.Combine(env.Dir, "new-pc", "Desktop");
        var adopted = BackupSetup.Adopt(found, "Personal files", new Dictionary<string, string> { ["Desktop"] = newDesktop }, []);
        Assert.Equal(env.Profile.DriveId, adopted.DriveId);
        Assert.Equal("Desktop", Assert.Single(adopted.Sources).Name);
        Assert.Equal("Documents", Assert.Single(adopted.RemovedFolders).Name);
        Assert.Equal(MissingFilePolicy.AlwaysKeep, adopted.MissingFilePolicy);

        using (var session = DriveSyncSession.Open(adopted, env.Bin))
        {
            Assert.Equal(1, await session.KeepMissingOnBackupAsync());
            Assert.Equal(FileState.Archived, session.Catalog.Get(@"Desktop\a.txt")!.State);
            Assert.False((await session.PlanSyncAsync()).HasChanges); // nothing is asked about

            var restored = await session.RestoreAsync(new RestoreRequest(["Desktop"]));
            Assert.Equal(1, restored.Restored);
            Assert.Equal(FileState.Active, session.Catalog.Get(@"Desktop\a.txt")!.State);
        }
        Assert.Equal("a", File.ReadAllText(Path.Combine(newDesktop, "a.txt")));
        Assert.True(File.Exists(env.BackupPath(@"Documents\cv.docx"))); // the unmapped folder stays on the drive
    }
}
