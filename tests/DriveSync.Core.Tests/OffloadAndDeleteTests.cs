using DriveSync.Core.Model;

namespace DriveSync.Core.Tests;

public class OffloadAndDeleteTests
{
    [Fact]
    public async Task Offload_sends_the_pc_copy_to_the_recycle_bin_and_keeps_the_backup()
    {
        using var env = new TestEnv();
        env.Write(@"Desktop\Videos\wedding.mp4", "lots of video");
        await env.SyncAsync();

        using var session = env.Open();
        var result = await session.OffloadAsync([env.PcPath(@"Desktop\Videos\wedding.mp4")]);

        Assert.Equal(1, result.Offloaded);
        Assert.Equal("lots of video".Length, result.BytesMovedToRecycleBin);
        Assert.False(File.Exists(env.PcPath(@"Desktop\Videos\wedding.mp4")));
        Assert.True(env.Bin.Holds(env.PcPath(@"Desktop\Videos\wedding.mp4")));
        Assert.Equal("lots of video", File.ReadAllText(env.BackupPath(@"Desktop\Videos\wedding.mp4")));
        Assert.Equal(FileState.Archived, session.Catalog.Get(@"Desktop\Videos\wedding.mp4")!.State);
    }

    [Fact]
    public async Task Offloaded_files_are_not_asked_about_on_the_next_sync()
    {
        using var env = new TestEnv();
        env.Write(@"Desktop\a.bin", "a");
        await env.SyncAsync();
        using (var session = env.Open())
            await session.OffloadAsync([@"Desktop\a.bin"]);

        var result = await env.SyncAsync();

        Assert.Equal(0, result.WaitingForDecision);
        Assert.Equal(FileState.Archived, env.Record(@"Desktop\a.bin")!.State);
    }

    [Fact]
    public async Task Offloading_a_folder_also_recycles_the_folder_once_it_is_empty()
    {
        using var env = new TestEnv();
        env.Write(@"Desktop\Old\one.txt", "1");
        env.Write(@"Desktop\Old\Sub\two.txt", "2");
        await env.SyncAsync();

        using var session = env.Open();
        var result = await session.OffloadAsync([env.PcPath(@"Desktop\Old")]);

        Assert.Equal(2, result.Offloaded);
        Assert.False(Directory.Exists(env.PcPath(@"Desktop\Old")));
        Assert.True(env.Bin.Holds(env.PcPath(@"Desktop\Old")));
        Assert.True(Directory.Exists(env.PcPath("Desktop")));
    }

    [Fact]
    public async Task Offload_backs_up_new_or_changed_files_first()
    {
        using var env = new TestEnv();
        env.Write(@"Desktop\new.txt", "never synced");

        using var session = env.Open();
        var result = await session.OffloadAsync([env.PcPath(@"Desktop\new.txt")]);

        Assert.Equal(1, result.BackedUpFirst);
        Assert.Equal(1, result.Offloaded);
        Assert.Equal("never synced", File.ReadAllText(env.BackupPath(@"Desktop\new.txt")));
    }

    [Fact]
    public async Task Offload_refuses_when_the_backup_copy_is_damaged()
    {
        using var env = new TestEnv();
        env.Write(@"Desktop\precious.txt", "precious");
        await env.SyncAsync();
        File.WriteAllText(env.BackupPath(@"Desktop\precious.txt"), "PRECIOUS"); // same size, different contents

        using var session = env.Open();
        var result = await session.OffloadAsync([env.PcPath(@"Desktop\precious.txt")]);

        Assert.Equal(0, result.Offloaded);
        Assert.Contains("doesn't match", Assert.Single(result.Problems).Message);
        Assert.True(File.Exists(env.PcPath(@"Desktop\precious.txt")));
        Assert.Empty(env.Bin.Items);
    }

    [Fact]
    public async Task Offload_leaves_the_file_when_the_recycle_bin_is_full()
    {
        using var env = new TestEnv();
        env.Write(@"Desktop\x.txt", "0123456789");
        await env.SyncAsync();
        env.Bin.MaxBytes = 15;
        env.Bin.Items.Add(new RecycleBin.RecycledItem(@"C:\older.txt", DateTime.UtcNow, 8, "", env.PcPath(@"Desktop\x.txt")));

        using var session = env.Open();
        var result = await session.OffloadAsync([env.PcPath(@"Desktop\x.txt")]);

        Assert.Equal(0, result.Offloaded);
        Assert.Contains("doesn't have room", Assert.Single(result.Problems).Message);
        Assert.True(File.Exists(env.PcPath(@"Desktop\x.txt")));
        Assert.Equal(FileState.Active, session.Catalog.Get(@"Desktop\x.txt")!.State);
    }

    [Fact]
    public async Task Offload_never_deletes_when_the_pc_drive_has_no_recycle_bin()
    {
        using var env = new TestEnv();
        env.Write(@"Desktop\x.txt", "x");
        await env.SyncAsync();
        env.Bin.Supported = false;

        using var session = env.Open();
        var result = await session.OffloadAsync([env.PcPath(@"Desktop\x.txt")]);

        Assert.Equal(0, result.Offloaded);
        Assert.Contains("no Recycle Bin", Assert.Single(result.Problems).Message);
        Assert.True(File.Exists(env.PcPath(@"Desktop\x.txt")));
    }

    [Fact]
    public async Task Delete_everywhere_sends_both_copies_to_recycle_bins()
    {
        using var env = new TestEnv();
        env.Write(@"Documents\Drafts\v1.docx", "v1");
        env.Write(@"Documents\Drafts\v2.docx", "v2");
        await env.SyncAsync();

        using (var session = env.Open())
        {
            var result = session.DeleteEverywhere([env.PcPath(@"Documents\Drafts")]);
            Assert.Equal(2, result.Deleted);
            Assert.Empty(result.Problems);
        }

        Assert.False(File.Exists(env.PcPath(@"Documents\Drafts\v1.docx")));
        Assert.False(File.Exists(env.BackupPath(@"Documents\Drafts\v1.docx")));
        Assert.True(env.Bin.Holds(env.PcPath(@"Documents\Drafts\v1.docx")));
        Assert.True(env.Bin.Holds(env.BackupPath(@"Documents\Drafts\v1.docx")));
        Assert.Equal(FileState.Deleted, env.Record(@"Documents\Drafts\v2.docx")!.State);
        Assert.Equal(0, (await env.SyncAsync()).WaitingForDecision);
    }

    [Fact]
    public async Task Delete_everywhere_works_on_files_that_were_offloaded_earlier()
    {
        using var env = new TestEnv();
        env.Write(@"Desktop\Archive\old.pdf", "old");
        await env.SyncAsync();
        using (var session = env.Open())
            await session.OffloadAsync([env.PcPath(@"Desktop\Archive")]);

        using (var session = env.Open())
        {
            // The folder is gone from the PC, so the user picks it on the backup drive.
            var result = session.DeleteEverywhere([env.BackupPath(@"Desktop\Archive")]);
            Assert.Equal(1, result.Deleted);
        }

        Assert.Equal(FileState.Deleted, env.Record(@"Desktop\Archive\old.pdf")!.State);
        Assert.True(env.Bin.Holds(env.BackupPath(@"Desktop\Archive\old.pdf")));
    }

    [Fact]
    public async Task Delete_everywhere_keeps_the_backup_if_the_pc_copy_could_not_be_recycled()
    {
        using var env = new TestEnv();
        env.Write(@"Desktop\x.txt", "x");
        await env.SyncAsync();
        env.Bin.Supported = false;

        using var session = env.Open();
        var result = session.DeleteEverywhere([env.PcPath(@"Desktop\x.txt")]);

        Assert.Equal(0, result.Deleted);
        Assert.True(File.Exists(env.PcPath(@"Desktop\x.txt")));
        Assert.True(File.Exists(env.BackupPath(@"Desktop\x.txt")));
        Assert.Equal(FileState.Active, session.Catalog.Get(@"Desktop\x.txt")!.State);
    }

    [Fact]
    public async Task Paths_outside_the_backed_up_folders_are_refused()
    {
        using var env = new TestEnv();
        await env.SyncAsync();

        using var session = env.Open();
        await Assert.ThrowsAsync<DriveSyncException>(() => session.OffloadAsync([Path.Combine(env.Dir, "somewhere-else.txt")]));
    }
}
