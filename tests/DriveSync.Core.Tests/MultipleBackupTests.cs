using System.Text.Json;
using DriveSync.Core.Engine;
using DriveSync.Core.Model;

namespace DriveSync.Core.Tests;

public class MultipleBackupTests
{
    static string Offloads(TestEnv env) => Path.Combine(env.Dir, "pc-mirror", "offloaded.db");

    static DriveSyncSession Open(TestEnv env, BackupProfile backup) =>
        DriveSyncSession.Open(backup, env.Bin, Path.Combine(env.Dir, "pc-mirror"), Offloads(env));

    [Fact]
    public void Store_turns_an_old_single_profile_into_the_first_backup_and_keeps_the_old_file()
    {
        using var env = new TestEnv();
        string folder = Path.Combine(env.Dir, "appdata");
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, "profile.json"), JsonSerializer.Serialize(env.Profile));

        var backups = new BackupStore(folder).Load();

        var only = Assert.Single(backups);
        Assert.Equal("My backup", only.Name);
        Assert.Equal(env.Profile.DriveId, only.DriveId);
        Assert.True(File.Exists(Path.Combine(folder, "backups.json")));
        Assert.True(File.Exists(Path.Combine(folder, "profile.json")));
        Assert.Equal(only.Id, new BackupStore(folder).Load()[0].Id);
    }

    [Fact]
    public async Task A_second_backup_on_the_same_drive_gets_its_own_folder_and_records()
    {
        using var env = new TestEnv();
        env.Write(@"Desktop\a.txt", "a");
        Directory.CreateDirectory(env.PcPath("Pictures"));
        File.WriteAllText(env.PcPath(@"Pictures\cat.jpg"), "cat");
        await env.SyncAsync();

        var photos = BackupSetup.CreateProfile(env.Drive, [env.PcPath("Pictures")], "Photos archive", [env.Profile]);
        using (var session = Open(env, photos))
            await session.SyncAsync();

        Assert.Equal("DriveSyncBackup-photos-archive", photos.BackupFolderName);
        Assert.NotEqual(env.Profile.DriveId, photos.DriveId);
        Assert.True(File.Exists(Path.Combine(env.Drive, "DriveSyncBackup-photos-archive", "Pictures", "cat.jpg")));
        Assert.False(Directory.Exists(Path.Combine(env.BackupRoot, "Pictures")));
        using var first = env.Open();
        Assert.Single(first.Catalog.LoadAll());
    }

    [Fact]
    public void Backup_names_must_be_different()
    {
        using var env = new TestEnv();
        var error = Assert.Throws<DriveSyncException>(() =>
            BackupSetup.CreateProfile(env.Drive, [env.PcPath("Desktop")], "my BACKUP", [env.Profile]));
        Assert.Contains("already have a backup", error.Message);
    }

    [Fact]
    public void A_backup_can_never_go_inside_a_folder_another_backup_copies()
    {
        using var env = new TestEnv();
        string inside = env.PcPath(@"Documents\Backups");
        Directory.CreateDirectory(inside);

        var error = Assert.Throws<DriveSyncException>(() =>
            BackupSetup.CreateProfile(inside, [env.PcPath("Desktop")], "Nested", [env.Profile]));
        Assert.Contains("which \"My backup\" backs up", error.Message);
    }

    [Fact]
    public void A_folder_holding_another_backup_can_never_be_backed_up()
    {
        using var env = new TestEnv();
        string other = Path.Combine(env.Dir, "elsewhere");
        Directory.CreateDirectory(other);

        var error = Assert.Throws<DriveSyncException>(() =>
            BackupSetup.CreateProfile(other, [env.Drive], "Backup of a backup", [env.Profile]));
        Assert.Contains("copy a backup into a backup", error.Message);
    }

    [Fact]
    public void The_same_folder_can_be_in_two_backups()
    {
        using var env = new TestEnv();
        string secondDrive = Path.Combine(env.Dir, "drive-2");
        Directory.CreateDirectory(secondDrive);

        var copy = BackupSetup.CreateProfile(secondDrive, [env.PcPath("Desktop"), env.PcPath("Documents")], "Copy 2", [env.Profile]);

        Assert.Equal(2, copy.Sources.Count);
    }

    [Fact]
    public async Task Offloading_through_one_backup_keeps_the_file_in_the_other_without_asking()
    {
        using var env = new TestEnv();
        env.Write(@"Desktop\movie.mp4", "frames");
        string secondDrive = Path.Combine(env.Dir, "drive-2");
        Directory.CreateDirectory(secondDrive);
        var copy = BackupSetup.CreateProfile(secondDrive, [env.PcPath("Desktop"), env.PcPath("Documents")], "Copy 2", [env.Profile]);
        using (var session = Open(env, env.Profile))
            await session.SyncAsync();
        using (var session = Open(env, copy))
            await session.SyncAsync();

        using (var session = Open(env, env.Profile))
            Assert.Equal(1, (await session.OffloadAsync([@"Desktop\movie.mp4"])).Offloaded);
        SyncResult second;
        using (var session = Open(env, copy))
            second = await session.SyncAsync();

        Assert.Equal(0, second.WaitingForDecision);
        Assert.Equal(1, second.KeptOnBackup);
        using var check = Open(env, copy);
        Assert.Equal(FileState.Archived, check.Catalog.Get(@"Desktop\movie.mp4")!.State);
    }

    [Fact]
    public async Task A_file_deleted_by_hand_is_still_asked_about_even_with_an_offload_list()
    {
        using var env = new TestEnv();
        env.Write(@"Desktop\gone.txt", "gone");
        using (var session = Open(env, env.Profile))
            await session.SyncAsync();
        File.Delete(env.PcPath(@"Desktop\gone.txt"));

        using var again = Open(env, env.Profile);
        var result = await again.SyncAsync();

        Assert.Equal(1, result.WaitingForDecision);
    }

    [Fact]
    public async Task Removing_a_folder_keeps_its_files_and_never_counts_them_as_missing()
    {
        using var env = new TestEnv();
        env.Write(@"Desktop\a.txt", "a");
        env.Write(@"Documents\report.docx", "report");
        await env.SyncAsync();

        BackupEditor.RemoveFolder(env.Profile, "Documents");
        File.Delete(env.PcPath(@"Documents\report.docx"));
        var result = await env.SyncAsync();

        Assert.Equal(0, result.WaitingForDecision);
        Assert.True(File.Exists(env.BackupPath(@"Documents\report.docx")));
        Assert.Equal("Documents", Assert.Single(env.Profile.RemovedFolders).Name);
        using var session = env.Open();
        Assert.Equal(1, session.FolderTotals()["Documents"].Files);
    }

    [Fact]
    public async Task A_removed_folder_can_be_sent_to_the_backup_drives_recycle_bin_as_one_item()
    {
        using var env = new TestEnv();
        env.Write(@"Desktop\a.txt", "a");
        env.Write(@"Documents\x.txt", "x");
        env.Write(@"Documents\Sub\y.txt", "y");
        await env.SyncAsync();
        BackupEditor.RemoveFolder(env.Profile, "Documents");

        using var session = env.Open();
        var result = session.RecycleRemovedFolder("Documents");

        Assert.Equal(2, result.Applied);
        Assert.False(Directory.Exists(env.BackupPath("Documents")));
        Assert.True(env.Bin.Holds(env.BackupPath("Documents")));
        Assert.Equal(FileState.Deleted, session.Catalog.Get(@"Documents\Sub\y.txt")!.State);
        Assert.True(File.Exists(env.PcPath(@"Documents\x.txt")));
    }

    [Fact]
    public void A_folder_still_backed_up_cant_be_sent_to_the_recycle_bin()
    {
        using var env = new TestEnv();
        using var session = env.Open();
        Assert.Throws<DriveSyncException>(() => session.RecycleRemovedFolder("Documents"));
    }

    [Fact]
    public async Task Adding_a_removed_folder_back_reuses_what_is_already_on_the_backup()
    {
        using var env = new TestEnv();
        env.Write(@"Documents\big.iso", "lots of bytes");
        await env.SyncAsync();
        BackupEditor.RemoveFolder(env.Profile, "Documents");
        await env.SyncAsync();

        var source = BackupEditor.AddFolder(env.Profile, env.PcPath("Documents"), [env.Profile]);
        var result = await env.SyncAsync();

        Assert.Equal("Documents", source.Name);
        Assert.Empty(env.Profile.RemovedFolders);
        Assert.Equal(0, result.Copied);
    }

    [Fact]
    public void The_last_folder_of_a_backup_cant_be_removed()
    {
        using var env = new TestEnv();
        BackupEditor.RemoveFolder(env.Profile, "Documents");
        Assert.Throws<DriveSyncException>(() => BackupEditor.RemoveFolder(env.Profile, "Desktop"));
    }

    [Fact]
    public async Task Moving_a_backup_copies_and_checks_everything_and_leaves_the_old_drive_alone()
    {
        using var env = new TestEnv();
        env.Write(@"Desktop\a.txt", "first");
        env.Write(@"Documents\b.txt", "b");
        await env.SyncAsync();
        env.Write(@"Desktop\a.txt", "second version");
        await env.SyncAsync();
        using (var session = env.Open())
            await session.OffloadAsync([@"Documents\b.txt"]);
        string oldRoot = env.BackupRoot, oldId = env.Profile.DriveId;
        string newDrive = Path.Combine(env.Dir, "new-drive");
        Directory.CreateDirectory(newDrive);

        MoveResult moved;
        using (var session = env.Open())
            moved = await session.MoveToAsync(newDrive, [env.Profile]);

        Assert.True(moved.Switched);
        Assert.Empty(moved.Problems);
        Assert.Equal(Path.Combine(newDrive, "DriveSyncBackup"), moved.NewRoot);
        Assert.Equal("b", File.ReadAllText(Path.Combine(moved.NewRoot, @"Documents\b.txt")));
        Assert.Single(Directory.GetFiles(Path.Combine(moved.NewRoot, ".drivesync", "versions"), "a.txt", SearchOption.AllDirectories));
        Assert.Equal(moved.NewDriveId, BackupLocation_ReadId(moved.NewRoot));
        Assert.Equal(oldId, BackupLocation_ReadId(oldRoot));
        Assert.True(File.Exists(Path.Combine(oldRoot, @"Documents\b.txt")));

        env.Profile.DriveId = moved.NewDriveId;
        env.Profile.LastKnownRoot = moved.NewRoot;
        var after = await env.SyncAsync();
        Assert.Equal(0, after.Copied);
        Assert.False(after.HasProblems());
        using var check = env.Open();
        Assert.Equal(moved.NewRoot, check.Location.Root);
        Assert.Equal(FileState.Archived, check.Catalog.Get(@"Documents\b.txt")!.State);
    }

    [Fact]
    public async Task A_move_with_a_damaged_file_doesnt_switch()
    {
        using var env = new TestEnv();
        env.Write(@"Desktop\a.txt", "good");
        await env.SyncAsync();
        File.WriteAllText(env.BackupPath(@"Desktop\a.txt"), "BAAD");
        string newDrive = Path.Combine(env.Dir, "new-drive");
        Directory.CreateDirectory(newDrive);

        using var session = env.Open();
        var moved = await session.MoveToAsync(newDrive, [env.Profile]);

        Assert.False(moved.Switched);
        Assert.Contains("doesn't match its checksum", Assert.Single(moved.Problems).Message);
        Assert.Null(BackupLocation_ReadId(moved.NewRoot));
    }

    [Fact]
    public async Task A_backup_cant_move_into_one_of_its_own_folders()
    {
        using var env = new TestEnv();
        using var session = env.Open();
        await Assert.ThrowsAsync<DriveSyncException>(() => session.MoveToAsync(env.PcPath("Desktop"), [env.Profile]));
    }

    static string? BackupLocation_ReadId(string root) => Storage.BackupLocation.ReadDriveId(root);
}

static class SyncResultExtensions
{
    public static bool HasProblems(this SyncResult result) => result.Problems.Count > 0;
}
