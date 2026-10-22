using DriveSync.Core.Engine;
using DriveSync.Core.IO;
using DriveSync.Core.Model;

namespace DriveSync.Core.Tests;

public class SyncTests
{
    [Fact]
    public async Task First_backup_copies_every_file_with_matching_checksums_and_dates()
    {
        using var env = new TestEnv();
        env.Write(@"Desktop\notes.txt", "hello");
        env.Write(@"Desktop\Projects\plan.docx", "plan v1");
        env.Write(@"Documents\tax.pdf", "tax return");

        var result = await env.SyncAsync();

        Assert.Equal(3, result.Copied);
        Assert.Empty(result.Problems);
        foreach (string key in new[] { @"Desktop\notes.txt", @"Desktop\Projects\plan.docx", @"Documents\tax.pdf" })
        {
            var record = env.Record(key)!;
            Assert.Equal(FileState.Active, record.State);
            Assert.Equal(FileHasher.HashFile(env.PcPath(key)), record.Hash);
            Assert.Equal(FileHasher.HashFile(env.BackupPath(key)), record.Hash);
            Assert.Equal(File.GetLastWriteTimeUtc(env.PcPath(key)), File.GetLastWriteTimeUtc(env.BackupPath(key)));
        }
    }

    [Fact]
    public async Task Unchanged_files_are_not_copied_again()
    {
        using var env = new TestEnv();
        env.Write(@"Desktop\a.txt", "a");
        await env.SyncAsync();

        using var session = env.Open();
        var plan = await session.PlanSyncAsync();

        Assert.False(plan.HasChanges);
        Assert.Equal(1, plan.UnchangedCount);
    }

    [Fact]
    public async Task Changed_file_updates_the_backup_and_keeps_the_old_version()
    {
        using var env = new TestEnv();
        env.Write(@"Desktop\report.docx", "draft 1");
        await env.SyncAsync();

        env.Write(@"Desktop\report.docx", "draft 2 is longer");
        var result = await env.SyncAsync();

        Assert.Equal(1, result.Updated);
        Assert.Equal("draft 2 is longer", File.ReadAllText(env.BackupPath(@"Desktop\report.docx")));
        using var session = env.Open();
        var version = Assert.Single(session.ListVersions(@"Desktop\report.docx"));
        Assert.Equal("draft 1", File.ReadAllText(version.Path));
    }

    [Fact]
    public async Task File_saved_without_changes_is_not_versioned()
    {
        using var env = new TestEnv();
        env.Write(@"Desktop\same.txt", "same");
        await env.SyncAsync();

        env.Write(@"Desktop\same.txt", "same");
        var result = await env.SyncAsync();

        Assert.Equal(1, result.Touched);
        using var session = env.Open();
        Assert.Empty(session.ListVersions(@"Desktop\same.txt"));
    }

    [Fact]
    public async Task Renamed_file_is_moved_on_the_drive_instead_of_copied()
    {
        using var env = new TestEnv();
        env.Write(@"Desktop\Photos\IMG_001.jpg", "pixels pixels pixels");
        await env.SyncAsync();

        Directory.CreateDirectory(env.PcPath(@"Desktop\Holiday"));
        File.Move(env.PcPath(@"Desktop\Photos\IMG_001.jpg"), env.PcPath(@"Desktop\Holiday\beach.jpg"));
        var result = await env.SyncAsync();

        Assert.Equal(1, result.Moved);
        Assert.Equal(0, result.Copied);
        Assert.Equal(0, result.WaitingForDecision);
        Assert.True(File.Exists(env.BackupPath(@"Desktop\Holiday\beach.jpg")));
        Assert.False(File.Exists(env.BackupPath(@"Desktop\Photos\IMG_001.jpg")));
        Assert.Null(env.Record(@"Desktop\Photos\IMG_001.jpg"));
        Assert.Equal(FileState.Active, env.Record(@"Desktop\Holiday\beach.jpg")!.State);
    }

    [Fact]
    public async Task Long_paths_are_backed_up()
    {
        using var env = new TestEnv();
        string deep = string.Join('\\', Enumerable.Repeat("a-rather-long-folder-name", 12));
        string key = $@"Desktop\{deep}\file.txt";
        Assert.True(env.PcPath(key).Length > 300);
        env.Write(key, "deep");

        var result = await env.SyncAsync();

        Assert.Equal(1, result.Copied);
        Assert.Equal("deep", File.ReadAllText(env.BackupPath(key)));
    }

    [Fact]
    public async Task Excluded_files_are_ignored_and_never_treated_as_missing()
    {
        using var env = new TestEnv();
        env.Write(@"Desktop\keep.txt", "keep");
        env.Write(@"Desktop\desktop.ini", "[.ShellClassInfo]");
        env.Write(@"Desktop\~$report.docx", "word lock file");
        env.Write(@"Desktop\node_modules\lib.js", "js");
        await env.SyncAsync();
        Assert.False(File.Exists(env.BackupPath(@"Desktop\desktop.ini")));
        Assert.False(File.Exists(env.BackupPath(@"Desktop\~$report.docx")));

        // The user excludes a folder that was already backed up: its files stay on the backup, untouched.
        env.Profile.Exclusions.Add("node_modules");
        var result = await env.SyncAsync();

        Assert.Equal(0, result.WaitingForDecision);
        Assert.Equal(FileState.Active, env.Record(@"Desktop\node_modules\lib.js")!.State);
    }

    [Fact]
    public async Task Missing_source_folder_never_counts_as_deleted()
    {
        using var env = new TestEnv();
        env.Write(@"Documents\a.txt", "a");
        await env.SyncAsync();

        Directory.Move(env.PcPath("Documents"), env.PcPath("Documents-renamed"));
        var result = await env.SyncAsync();

        Assert.Equal(0, result.WaitingForDecision);
        Assert.Contains(result.Warnings, w => w.Contains("Documents"));
        Assert.Equal(FileState.Active, env.Record(@"Documents\a.txt")!.State);
    }

    [Fact]
    public async Task Cloud_only_files_are_skipped_and_never_treated_as_missing()
    {
        using var env = new TestEnv();
        string path = env.Write(@"Desktop\cloud.docx", "in the cloud");
        await env.SyncAsync();

        File.SetAttributes(path, File.GetAttributes(path) | FileAttributes.Offline);
        var result = await env.SyncAsync();

        Assert.Equal(0, result.WaitingForDecision);
        Assert.Equal(FileState.Active, env.Record(@"Desktop\cloud.docx")!.State);
    }

    [Fact]
    public async Task Catalog_is_kept_on_the_drive_and_copied_to_the_pc()
    {
        using var env = new TestEnv();
        env.Write(@"Desktop\a.txt", "a");
        await env.SyncAsync();

        Assert.True(File.Exists(Path.Combine(env.BackupRoot, ".drivesync", "catalog.db")));
        Assert.True(File.Exists(Path.Combine(env.Dir, "pc-mirror", $"catalog-{env.Profile.DriveId}.db")));
        Assert.True(File.Exists(Path.Combine(env.BackupRoot, "README.txt")));
    }

    [Fact]
    public async Task A_different_drive_is_never_used()
    {
        using var env = new TestEnv();
        File.WriteAllText(Path.Combine(env.BackupRoot, ".drivesync", "drive.id"), Guid.NewGuid().ToString());
        env.Profile.BackupFolderName = "not-on-any-real-drive-" + Guid.NewGuid().ToString("N");

        var error = await Assert.ThrowsAsync<DriveSyncException>(env.SyncAsync);
        Assert.Contains("isn't connected", error.Message);
    }

    [Fact]
    public void Only_one_task_can_use_the_drive_at_a_time()
    {
        using var env = new TestEnv();
        using var first = env.Open();

        var error = Assert.Throws<DriveSyncException>(() => env.Open());
        Assert.Contains("already using", error.Message);
    }

    [Fact]
    public async Task Backup_reconnects_after_a_new_setup_on_the_same_drive()
    {
        using var env = new TestEnv();
        env.Write(@"Desktop\a.txt", "a");
        await env.SyncAsync();

        var again = BackupSetup.CreateProfile(env.Drive, [env.PcPath("Desktop"), env.PcPath("Documents")]);

        Assert.Equal(env.Profile.DriveId, again.DriveId);
        using var session = DriveSyncSession.Open(again, env.Bin);
        Assert.False((await session.PlanSyncAsync()).HasChanges);
    }
}
