using DriveSync.Core.Model;

namespace DriveSync.Core.Tests;

/// <summary>Files that disappear from the PC without going through DriveSync.</summary>
public class MissingFileTests
{
    [Fact]
    public async Task Ask_policy_keeps_the_file_on_the_backup_as_pending()
    {
        using var env = new TestEnv();
        env.Write(@"Desktop\old.txt", "old");
        await env.SyncAsync();

        File.Delete(env.PcPath(@"Desktop\old.txt"));
        var result = await env.SyncAsync();

        Assert.Equal(1, result.WaitingForDecision);
        Assert.Equal(1, result.PendingTotal);
        Assert.Equal(FileState.Pending, env.Record(@"Desktop\old.txt")!.State);
        Assert.True(File.Exists(env.BackupPath(@"Desktop\old.txt")));
        Assert.Empty(env.Bin.Items);
    }

    [Fact]
    public async Task Deleted_on_purpose_sends_the_backup_copy_to_the_recycle_bin()
    {
        using var env = new TestEnv();
        env.Write(@"Desktop\old.txt", "old");
        await env.SyncAsync();
        File.Delete(env.PcPath(@"Desktop\old.txt"));
        await env.SyncAsync();

        using (var session = env.Open())
        {
            var result = session.ResolveMissing([@"Desktop\old.txt"], MissingDecision.DeletedOnPurpose);
            Assert.Equal(1, result.Applied);
        }

        Assert.Equal(FileState.Deleted, env.Record(@"Desktop\old.txt")!.State);
        Assert.False(File.Exists(env.BackupPath(@"Desktop\old.txt")));
        Assert.True(env.Bin.Holds(env.BackupPath(@"Desktop\old.txt")));
    }

    [Fact]
    public async Task Keep_on_backup_archives_the_file_and_later_syncs_leave_it_alone()
    {
        using var env = new TestEnv();
        env.Write(@"Desktop\big.iso", "big");
        await env.SyncAsync();
        File.Delete(env.PcPath(@"Desktop\big.iso"));
        await env.SyncAsync();

        using (var session = env.Open())
            session.ResolveMissing([@"Desktop\big.iso"], MissingDecision.KeepOnBackup);
        var next = await env.SyncAsync();

        Assert.Equal(FileState.Archived, env.Record(@"Desktop\big.iso")!.State);
        Assert.Equal(0, next.WaitingForDecision);
        Assert.True(File.Exists(env.BackupPath(@"Desktop\big.iso")));
    }

    [Fact]
    public async Task A_file_cannot_be_resolved_while_it_is_still_on_the_pc()
    {
        using var env = new TestEnv();
        env.Write(@"Desktop\here.txt", "here");
        await env.SyncAsync();

        using var session = env.Open();
        var result = session.ResolveMissing([@"Desktop\here.txt"], MissingDecision.DeletedOnPurpose);

        Assert.Equal(0, result.Applied);
        Assert.Single(result.Problems);
        Assert.True(File.Exists(env.BackupPath(@"Desktop\here.txt")));
    }

    [Fact]
    public async Task Recycle_bin_policy_removes_only_files_found_in_the_pc_recycle_bin()
    {
        using var env = new TestEnv();
        env.Profile.MissingFilePolicy = MissingFilePolicy.RecycleBinMeansDeleted;
        env.Write(@"Desktop\binned.txt", "binned");
        env.Write(@"Desktop\vanished.txt", "vanished");
        await env.SyncAsync();

        env.Bin.Recycle([env.PcPath(@"Desktop\binned.txt")]); // the user deleted it in Explorer
        File.Delete(env.PcPath(@"Desktop\vanished.txt"));      // gone some other way
        var result = await env.SyncAsync();

        Assert.Equal(1, result.RemovedFromBackup);
        Assert.Equal(FileState.Deleted, env.Record(@"Desktop\binned.txt")!.State);
        Assert.Equal(FileState.Pending, env.Record(@"Desktop\vanished.txt")!.State);
    }

    [Fact]
    public async Task Missing_files_are_flagged_when_they_are_in_the_recycle_bin()
    {
        using var env = new TestEnv();
        env.Write(@"Desktop\Old Projects\a.txt", "a");
        await env.SyncAsync();

        env.Bin.Recycle([env.PcPath(@"Desktop\Old Projects")]); // a whole folder deleted in Explorer
        using var session = env.Open();
        var plan = await session.PlanSyncAsync();

        Assert.True(Assert.Single(plan.Missing).InRecycleBin);
    }

    [Fact]
    public async Task A_few_missing_files_below_the_guard_threshold_are_handled_normally()
    {
        using var env = new TestEnv();
        env.Profile.MissingFilePolicy = MissingFilePolicy.AlwaysRemove;
        for (int i = 0; i < 100; i++)
            env.Write($@"Desktop\file{i:D2}.txt", $"content {i}");
        await env.SyncAsync();

        for (int i = 0; i < 40; i++)
            File.Delete(env.PcPath($@"Desktop\file{i:D2}.txt"));
        var result = await env.SyncAsync();

        Assert.False(result.MassDisappearance);
        Assert.Equal(40, result.RemovedFromBackup);
    }

    [Theory]
    [InlineData(MissingFilePolicy.AlwaysKeep, FileState.Archived)]
    [InlineData(MissingFilePolicy.AlwaysRemove, FileState.Deleted)]
    public async Task Automatic_policies_apply_without_asking(MissingFilePolicy policy, FileState expected)
    {
        using var env = new TestEnv();
        env.Profile.MissingFilePolicy = policy;
        env.Write(@"Desktop\x.txt", "x");
        await env.SyncAsync();

        File.Delete(env.PcPath(@"Desktop\x.txt"));
        await env.SyncAsync();

        Assert.Equal(expected, env.Record(@"Desktop\x.txt")!.State);
    }

    [Fact]
    public async Task Mass_disappearance_removes_nothing_even_with_always_remove()
    {
        using var env = new TestEnv();
        env.Profile.MissingFilePolicy = MissingFilePolicy.AlwaysRemove;
        for (int i = 0; i < 100; i++)
            env.Write($@"Desktop\file{i:D2}.txt", $"content {i}");
        await env.SyncAsync();

        for (int i = 0; i < 60; i++)
            File.Delete(env.PcPath($@"Desktop\file{i:D2}.txt"));
        var result = await env.SyncAsync();

        Assert.True(result.MassDisappearance);
        Assert.Equal(0, result.RemovedFromBackup);
        Assert.Equal(60, result.WaitingForDecision);
        Assert.Empty(env.Bin.Items);
        Assert.True(File.Exists(env.BackupPath(@"Desktop\file00.txt")));
    }

    [Fact]
    public async Task Full_recycle_bin_leaves_the_backup_copy_in_place()
    {
        using var env = new TestEnv();
        env.Write(@"Desktop\x.txt", "some content");
        await env.SyncAsync();
        File.Delete(env.PcPath(@"Desktop\x.txt"));
        await env.SyncAsync();

        env.Bin.MaxBytes = 3;
        using var session = env.Open();
        var result = session.ResolveMissing([@"Desktop\x.txt"], MissingDecision.DeletedOnPurpose);

        Assert.Equal(0, result.Applied);
        Assert.Contains("bigger than the Recycle Bin", Assert.Single(result.Problems).Message);
        Assert.True(File.Exists(env.BackupPath(@"Desktop\x.txt")));
        Assert.Equal(FileState.Pending, session.Catalog.Get(@"Desktop\x.txt")!.State);
    }

    [Fact]
    public async Task Drive_without_a_recycle_bin_uses_the_trash_folder_which_is_never_emptied()
    {
        using var env = new TestEnv();
        env.Write(@"Desktop\x.txt", "x");
        await env.SyncAsync();
        File.Delete(env.PcPath(@"Desktop\x.txt"));
        await env.SyncAsync();

        env.Bin.Supported = false;
        using var session = env.Open();
        session.ResolveMissing([@"Desktop\x.txt"], MissingDecision.DeletedOnPurpose);

        string trash = Path.Combine(env.BackupRoot, ".drivesync", "trash");
        Assert.Single(Directory.GetFiles(trash, "x.txt", SearchOption.AllDirectories));
        Assert.Equal(FileState.Deleted, session.Catalog.Get(@"Desktop\x.txt")!.State);
    }

    [Fact]
    public async Task Archived_file_that_comes_back_unchanged_becomes_active_again()
    {
        using var env = new TestEnv();
        string path = env.Write(@"Desktop\back.txt", "back");
        await env.SyncAsync();
        string copy = Path.Combine(env.Dir, "elsewhere.txt");
        File.Copy(path, copy);
        File.SetLastWriteTimeUtc(copy, File.GetLastWriteTimeUtc(path));
        File.Delete(path);
        await env.SyncAsync();
        using (var session = env.Open())
            session.ResolveMissing([@"Desktop\back.txt"], MissingDecision.KeepOnBackup);

        File.Move(copy, path);
        var result = await env.SyncAsync();

        Assert.Equal(1, result.BackOnPc);
        Assert.Equal(FileState.Active, env.Record(@"Desktop\back.txt")!.State);
    }
}
