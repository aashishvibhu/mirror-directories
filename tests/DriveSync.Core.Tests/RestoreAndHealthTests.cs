using DriveSync.Core.Engine;
using DriveSync.Core.Model;

namespace DriveSync.Core.Tests;

public class RestoreAndHealthTests
{
    [Fact]
    public async Task Restoring_an_offloaded_file_puts_it_back_and_makes_it_active()
    {
        using var env = new TestEnv();
        env.Write(@"Desktop\thesis.docx", "final final v3");
        await env.SyncAsync();
        using (var session = env.Open())
            await session.OffloadAsync([@"Desktop\thesis.docx"]);

        using (var session = env.Open())
        {
            var result = await session.RestoreAsync(new RestoreRequest([@"Desktop\thesis.docx"]));
            Assert.Equal(1, result.Restored);
        }

        Assert.Equal("final final v3", File.ReadAllText(env.PcPath(@"Desktop\thesis.docx")));
        Assert.Equal(FileState.Active, env.Record(@"Desktop\thesis.docx")!.State);
        Assert.False((await env.Open().UsingAsync(s => s.PlanSyncAsync())).HasChanges);
    }

    [Fact]
    public async Task Restore_never_replaces_a_different_file_in_the_way()
    {
        using var env = new TestEnv();
        env.Write(@"Desktop\notes.txt", "backed up");
        await env.SyncAsync();
        using (var session = env.Open())
            await session.OffloadAsync([@"Desktop\notes.txt"]);
        env.Write(@"Desktop\notes.txt", "a newer, different file");

        using var s = env.Open();
        var result = await s.RestoreAsync(new RestoreRequest([@"Desktop\notes.txt"]));

        Assert.Equal(1, result.KeptBoth);
        Assert.Equal("backed up", File.ReadAllText(env.PcPath(@"Desktop\notes.txt")));
        string aside = Assert.Single(Directory.GetFiles(env.PcPath("Desktop"), "notes (before restore *).txt"));
        Assert.Equal("a newer, different file", File.ReadAllText(aside));
    }

    [Fact]
    public async Task Restore_to_another_folder_keeps_the_folder_structure()
    {
        using var env = new TestEnv();
        env.Write(@"Desktop\Taxes\2026.pdf", "tax");
        await env.SyncAsync();
        string target = Path.Combine(env.Dir, "restored");

        using var session = env.Open();
        await session.RestoreAsync(new RestoreRequest([@"Desktop\Taxes"]) { TargetFolder = target });

        Assert.Equal("tax", File.ReadAllText(Path.Combine(target, @"Desktop\Taxes\2026.pdf")));
    }

    [Fact]
    public async Task An_old_version_can_be_restored()
    {
        using var env = new TestEnv();
        env.Write(@"Desktop\essay.txt", "first draft");
        await env.SyncAsync();
        env.Write(@"Desktop\essay.txt", "second draft");
        await env.SyncAsync();
        string target = Path.Combine(env.Dir, "restored");

        using var session = env.Open();
        var version = Assert.Single(session.ListVersions(@"Desktop\essay.txt"));
        await session.RestoreAsync(new RestoreRequest([@"Desktop\essay.txt"])
        {
            Source = RestoreSource.OldVersion,
            VersionStamp = version.Stamp,
            TargetFolder = target,
        });

        Assert.Equal("first draft", File.ReadAllText(Path.Combine(target, @"Desktop\essay.txt")));
    }

    [Fact]
    public async Task A_deleted_file_can_be_restored_from_the_drives_recycle_bin()
    {
        using var env = new TestEnv();
        env.Write(@"Desktop\oops.txt", "needed after all");
        await env.SyncAsync();
        using (var session = env.Open())
            session.DeleteEverywhere([@"Desktop\oops.txt"]);

        using (var session = env.Open())
        {
            var deleted = Assert.Single(session.ListDeleted("Desktop"));
            Assert.True(deleted.InRecycleBin);
            var result = await session.RestoreAsync(new RestoreRequest([@"Desktop\oops.txt"]) { Source = RestoreSource.Deleted });
            Assert.Equal(1, result.Restored);
        }

        Assert.Equal("needed after all", File.ReadAllText(env.PcPath(@"Desktop\oops.txt")));
        Assert.Equal(1, (await env.SyncAsync()).Copied); // backed up again on the next sync
    }

    [Fact]
    public async Task Health_check_finds_missing_and_damaged_copies_and_repairs_what_it_can()
    {
        using var env = new TestEnv();
        env.Write(@"Desktop\fine.txt", "fine");
        env.Write(@"Desktop\damaged.txt", "damaged");
        env.Write(@"Desktop\gone.txt", "gone");
        env.Write(@"Desktop\offloaded.txt", "offloaded");
        await env.SyncAsync();
        using (var session = env.Open())
            await session.OffloadAsync([@"Desktop\offloaded.txt"]);

        File.WriteAllText(env.BackupPath(@"Desktop\damaged.txt"), "DAMAGED");
        File.Delete(env.BackupPath(@"Desktop\gone.txt"));
        File.WriteAllText(env.BackupPath(@"Desktop\offloaded.txt"), "OFFLOADED");
        File.WriteAllText(env.BackupPath(@"Desktop\stray.txt"), "put here by hand");

        using (var session = env.Open())
        {
            var quick = await session.CheckHealthAsync(full: false);
            Assert.Equal(HealthProblem.Missing, Assert.Single(quick.Issues).Problem);

            var full = await session.CheckHealthAsync(full: true, repair: true);
            Assert.Equal(3, full.Issues.Count);
            Assert.Equal(2, full.Repaired);
            var cannot = Assert.Single(full.Issues, i => !i.Repaired);
            Assert.Equal(@"Desktop\offloaded.txt", cannot.Record.Key);
            Assert.Contains("only one", cannot.Note);
            Assert.Equal(@"Desktop\stray.txt", Assert.Single(full.Untracked));
        }

        Assert.Equal("damaged", File.ReadAllText(env.BackupPath(@"Desktop\damaged.txt")));
        Assert.Equal("gone", File.ReadAllText(env.BackupPath(@"Desktop\gone.txt")));
        Assert.True(File.Exists(env.BackupPath(@"Desktop\stray.txt")));
        using var after = env.Open();
        Assert.Contains(after.ListVersions(@"Desktop\damaged.txt"), v => File.ReadAllText(v.Path) == "DAMAGED");
    }

    [Fact]
    public async Task Summary_counts_files_by_state()
    {
        using var env = new TestEnv();
        env.Write(@"Desktop\a.txt", "a");
        env.Write(@"Desktop\b.txt", "bb");
        await env.SyncAsync();
        using (var session = env.Open())
            await session.OffloadAsync([@"Desktop\b.txt"]);

        using var s = env.Open();
        var summary = s.GetSummary();

        Assert.Equal(1, summary.Count(FileState.Active));
        Assert.Equal(1, summary.Count(FileState.Archived));
        Assert.Equal(2, summary.Bytes(FileState.Archived));
        Assert.NotNull(summary.LastSyncUtc);
    }
}

static class SessionExtensions
{
    public static async Task<T> UsingAsync<T>(this DriveSyncSession session, Func<DriveSyncSession, Task<T>> action)
    {
        using (session)
            return await action(session);
    }
}
