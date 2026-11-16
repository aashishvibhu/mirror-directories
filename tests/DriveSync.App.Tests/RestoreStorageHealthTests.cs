using System.IO;
using DriveSync.App.ViewModels;
using DriveSync.Core;
using DriveSync.Core.Model;

namespace DriveSync.App.Tests;

public class FilesTabTests
{
    static async Task<(MainViewModel Main, BackupPageViewModel Page)> SyncedAsync(AppEnv env)
    {
        env.SetUpBackup();
        var main = new MainViewModel(env.Services);
        var page = main.Backups[0];
        await page.Dashboard.SyncWithoutReviewAsync();
        return (main, page);
    }

    [Fact]
    public async Task The_files_tab_browses_the_backup_and_restores_a_file_that_left_the_pc()
    {
        using var env = new AppEnv();
        Directory.CreateDirectory(Path.Combine(env.Desktop, "Taxes"));
        File.WriteAllText(Path.Combine(env.Desktop, "Taxes", "2026.pdf"), "tax return");
        File.WriteAllText(Path.Combine(env.Desktop, "note.txt"), "note");
        var (_, page) = await SyncedAsync(env);
        File.Delete(Path.Combine(env.Desktop, "Taxes", "2026.pdf"));

        await page.ShowFilesAsync();
        Assert.True(page.Tabs.Single(t => t.Title == "Files").IsSelected);
        Assert.Equal("Desktop", Assert.Single(page.Files.Rows).Name);
        await page.Files.Rows[0].OpenCommand.ExecuteAsync(null);
        Assert.Equal(["Taxes", "note.txt"], page.Files.Rows.Select(r => r.Name));
        Assert.Equal(2, page.Files.Crumbs.Count);
        await page.Files.Rows[0].OpenCommand.ExecuteAsync(null);
        var row = Assert.Single(page.Files.Rows);

        row.IsSelected = true;
        Assert.True(page.Files.RestoreSelectedCommand.CanExecute(null));
        page.Files.RestoreSelectedCommand.Execute(null);
        Assert.True(page.Restore.IsOpen);
        await AppEnv.WaitUntil(() => page.Restore.ToRestore == 1 && !page.Restore.IsPlanning);
        Assert.Contains("1 file to restore", page.Restore.Summary);
        await page.Restore.StartCommand.ExecuteAsync(null);

        Assert.True(page.Restore.IsDone);
        Assert.False(page.Restore.ResultIsWarning, page.Restore.ResultMessage);
        Assert.Equal("tax return", File.ReadAllText(Path.Combine(env.Desktop, "Taxes", "2026.pdf")));
    }

    [Fact]
    public async Task Search_and_the_offloaded_filter_narrow_the_list()
    {
        using var env = new AppEnv();
        File.WriteAllText(Path.Combine(env.Desktop, "holiday.mp4"), "video video video");
        File.WriteAllText(Path.Combine(env.Desktop, "note.txt"), "note");
        var (_, page) = await SyncedAsync(env);
        await page.ShowFilesAsync();
        await page.Files.OpenFolderCommand.ExecuteAsync("Desktop");

        // Free up space: asks first, then the PC copy goes to the Recycle Bin and the file is marked offloaded.
        page.Files.Rows.Single(r => r.Name == "holiday.mp4").IsSelected = true;
        page.Files.OffloadSelectedCommand.Execute(null);
        Assert.True(page.Files.IsConfirmOpen);
        Assert.True(File.Exists(Path.Combine(env.Desktop, "holiday.mp4")));
        await page.Files.ConfirmYesCommand.ExecuteAsync(null);
        Assert.False(File.Exists(Path.Combine(env.Desktop, "holiday.mp4")));
        Assert.Equal("Offloaded", page.Files.Rows.Single(r => r.Name == "holiday.mp4").StatusText);

        page.Files.Filter = FileFilter.Offloaded;
        await AppEnv.WaitUntil(() => !page.Files.IsLoading && page.Files.Rows.Count == 1);
        Assert.Equal("holiday.mp4", page.Files.Rows[0].Name);

        page.Files.Filter = FileFilter.All;
        page.Files.SearchText = "note";
        await AppEnv.WaitUntil(() => !page.Files.IsLoading && page.Files.Rows.Count == 1 && page.Files.Rows[0].Name == "note.txt");
        Assert.Equal("Desktop", page.Files.Rows[0].Detail);
    }

    [Fact]
    public async Task An_older_version_can_be_brought_back_without_losing_the_current_file()
    {
        using var env = new AppEnv();
        string essay = Path.Combine(env.Desktop, "essay.txt");
        File.WriteAllText(essay, "first draft");
        var (_, page) = await SyncedAsync(env);
        File.WriteAllText(essay, "second draft, longer");
        File.SetLastWriteTimeUtc(essay, DateTime.UtcNow.AddMinutes(5));
        await page.Dashboard.SyncWithoutReviewAsync();

        await page.ShowFilesAsync();
        await page.Files.OpenFolderCommand.ExecuteAsync("Desktop");
        await page.Files.Rows.Single().OpenCommand.ExecuteAsync(null);
        Assert.True(page.Files.IsHistoryOpen);
        var version = Assert.Single(page.Files.Versions);

        version.RestoreCommand.Execute(null);
        await AppEnv.WaitUntil(() => page.Restore.ToRestore == 1 && !page.Restore.IsPlanning);
        Assert.Contains("in the way", page.Restore.Summary);
        await page.Restore.StartCommand.ExecuteAsync(null);

        Assert.Equal("first draft", File.ReadAllText(essay));
        string kept = Assert.Single(Directory.GetFiles(env.Desktop, "essay (before restore*"));
        Assert.Equal("second draft, longer", File.ReadAllText(kept));
    }

    [Fact]
    public async Task Without_the_drive_the_backup_can_still_be_looked_through_but_not_restored()
    {
        using var env = new AppEnv();
        File.WriteAllText(Path.Combine(env.Desktop, "a.txt"), "a");
        string drive = env.NewDrive("drive-f");
        env.SetUpBackup(drive: drive);
        var main = new MainViewModel(env.Services);
        var page = main.Backups[0];
        await page.Dashboard.SyncWithoutReviewAsync();
        Directory.Move(drive, drive + "-unplugged");

        await page.ShowFilesAsync();
        await page.Files.OpenFolderCommand.ExecuteAsync("Desktop");

        Assert.False(page.Files.IsDriveConnected);
        var row = Assert.Single(page.Files.Rows);
        row.IsSelected = true;
        Assert.False(page.Files.RestoreSelectedCommand.CanExecute(null));
    }
}

public class StorageAndHealthTabTests
{
    [Fact]
    public async Task Storage_shows_what_takes_the_space_and_restores_a_file_removed_from_the_backup()
    {
        using var env = new AppEnv();
        string a = Path.Combine(env.Desktop, "a.txt"), b = Path.Combine(env.Desktop, "b.txt");
        File.WriteAllText(a, "aaaa");
        File.WriteAllText(b, "bbbbbb");
        env.SetUpBackup();
        var main = new MainViewModel(env.Services);
        var page = main.Backups[0];
        await page.Dashboard.SyncWithoutReviewAsync();
        Assert.NotNull(page.Dashboard.Storage); // the bar on Home
        File.WriteAllText(a, "aaaa changed");
        File.SetLastWriteTimeUtc(a, DateTime.UtcNow.AddMinutes(5));
        File.Delete(b);
        await page.Dashboard.SyncWithoutReviewAsync();
        await page.ShowMissingAsync();
        page.Missing.Groups.Single().DeleteAllCommand.Execute(null);
        await page.Missing.ConfirmDeleteCommand.ExecuteAsync(null);

        await page.ShowStorageAsync();

        Assert.True(page.Storage.HasVersions);
        Assert.StartsWith("1 old version", page.Storage.VersionsSummary);
        Assert.Equal(4, page.Storage.Bar!.Usage.OldVersions);
        var removed = Assert.Single(page.Storage.Removed);
        Assert.Equal("b.txt", removed.Name);

        // A clean-up that matches nothing offers nothing to remove.
        await page.Storage.SuggestOlderThanYearCommand.ExecuteAsync(null);
        Assert.True(page.Storage.IsConfirmOpen);
        Assert.False(page.Storage.ConfirmHasAction);
        page.Storage.ConfirmNoCommand.Execute(null);

        removed.RestoreCommand.Execute(null);
        await AppEnv.WaitUntil(() => page.Restore.ToRestore == 1 && !page.Restore.IsPlanning);
        await page.Restore.StartCommand.ExecuteAsync(null);
        Assert.Equal("bbbbbb", File.ReadAllText(b));
    }

    [Fact]
    public async Task Old_versions_are_only_removed_after_the_suggestion_is_confirmed()
    {
        using var env = new AppEnv();
        string a = Path.Combine(env.Desktop, "a.txt");
        File.WriteAllText(a, "one");
        var profile = env.SetUpBackup();
        var main = new MainViewModel(env.Services);
        var page = main.Backups[0];
        await page.Dashboard.SyncWithoutReviewAsync();
        File.WriteAllText(a, "two!");
        File.SetLastWriteTimeUtc(a, DateTime.UtcNow.AddMinutes(5));
        await page.Dashboard.SyncWithoutReviewAsync();
        string versions = Path.Combine(profile.LastKnownRoot!, ".drivesync", "versions");
        // Make the version look two years old, as its folder name is what dates it.
        string stamp = Directory.GetDirectories(versions).Single();
        Directory.Move(stamp, Path.Combine(versions, DateTime.Now.AddYears(-2).ToString("yyyyMMdd-HHmmss")));
        using (var session = env.Services.OpenSession(profile))
            session.ReindexVersions();

        await page.ShowStorageAsync();
        await page.Storage.SuggestOlderThanYearCommand.ExecuteAsync(null);
        Assert.True(page.Storage.ConfirmHasAction);
        Assert.Single(Directory.GetFiles(versions, "a.txt", SearchOption.AllDirectories)); // nothing has gone yet

        await page.Storage.ConfirmYesCommand.ExecuteAsync(null);

        Assert.Empty(Directory.GetFiles(versions, "a.txt", SearchOption.AllDirectories));
        Assert.False(page.Storage.HasVersions);
        Assert.Equal("two!", File.ReadAllText(Path.Combine(profile.LastKnownRoot!, "Desktop", "a.txt")));
    }

    [Fact]
    public async Task Health_finds_a_damaged_copy_flags_the_backup_and_repairs_it_from_the_pc()
    {
        using var env = new AppEnv();
        File.WriteAllText(Path.Combine(env.Desktop, "photo.jpg"), "pixels");
        var profile = env.SetUpBackup();
        var main = new MainViewModel(env.Services);
        var page = main.Backups[0];
        await page.Dashboard.SyncWithoutReviewAsync();
        await page.ShowHealthAsync();
        Assert.True(page.Health.IsHealthy);
        File.WriteAllText(Path.Combine(profile.LastKnownRoot!, "Desktop", "photo.jpg"), "pix#ls");

        await page.Health.CheckNowCommand.ExecuteAsync(null);

        Assert.False(page.Health.IsHealthy);
        Assert.Equal("Damaged", Assert.Single(page.Health.Issues).Problem);
        Assert.Equal(1, page.HealthIssues);
        Assert.Equal(1, page.Tabs.Single(t => t.Title == "Health").Badge);
        await AppEnv.WaitUntil(() => page.Dashboard.Status == BackupStatus.NeedsAttention);

        Assert.True(page.Health.RepairCommand.CanExecute(null));
        await page.Health.RepairCommand.ExecuteAsync(null);

        Assert.True(page.Health.IsHealthy);
        Assert.Empty(page.Health.Issues);
        Assert.Equal("pixels", File.ReadAllText(Path.Combine(profile.LastKnownRoot!, "Desktop", "photo.jpg")));
    }

    [Fact]
    public async Task The_check_after_a_sync_reports_a_damaged_older_copy()
    {
        using var env = new AppEnv();
        File.WriteAllText(Path.Combine(env.Desktop, "photo.jpg"), "pixels");
        var profile = env.SetUpBackup();
        profile.RollingCheck = false;
        var main = new MainViewModel(env.Services);
        var page = main.Backups[0];
        await page.Dashboard.SyncWithoutReviewAsync();
        File.WriteAllText(Path.Combine(profile.LastKnownRoot!, "Desktop", "photo.jpg"), "pix#ls");

        profile.RollingCheck = true;
        await page.Dashboard.SyncWithoutReviewAsync();

        Assert.True(page.Dashboard.LastRunNeedsAttention);
        Assert.Contains("damaged", page.Dashboard.LastRunMessage);
        Assert.Equal(1, page.HealthIssues);
    }
}

public class NewPcWizardTests
{
    [Fact]
    public async Task A_new_backup_keeps_removed_copies_in_its_own_trash_folder()
    {
        using var env = new AppEnv();
        using var wizard = new SetupWizardViewModel(env.Services, [new FolderSuggestion("Desktop", env.Desktop)]);
        BackupProfile? created = null;
        wizard.Completed += p => created = p;
        wizard.Folders[0].IsSelected = true;
        wizard.PickFolder = () => env.Drive;
        wizard.ChooseDriveFolderCommand.Execute(null);

        await wizard.FinishCommand.ExecuteAsync(null);

        Assert.NotNull(created);
        Assert.Equal(RemovedFilesGoTo.TrashFolder, created.RemovedFilesGoTo);
        Assert.Equal(FirstRun.Backup, wizard.FirstRun);
    }

    [Fact]
    public async Task The_wizard_offers_a_backup_already_on_the_drive_and_keeps_its_files_there()
    {
        using var env = new AppEnv();
        File.WriteAllText(Path.Combine(env.Desktop, "a.txt"), "a");
        File.WriteAllText(Path.Combine(env.Desktop, "b.txt"), "b");
        var original = env.SetUpBackup("Personal files");
        var main = new MainViewModel(env.Services);
        await main.Backups[0].Dashboard.SyncWithoutReviewAsync();
        main.RemoveBackup(main.Backups[0]);
        File.Delete(Path.Combine(env.Desktop, "b.txt")); // this PC no longer has it

        using var wizard = new SetupWizardViewModel(env.Services, []);
        BackupProfile? adopted = null;
        wizard.Completed += p => adopted = p;
        wizard.PickFolder = () => env.Drive;
        wizard.ChooseDriveFolderCommand.Execute(null);
        Assert.Contains("Personal files", wizard.FoundNote);

        wizard.UseFoundBackupCommand.Execute(null);
        Assert.True(wizard.IsAdopting);
        Assert.Equal("Personal files", wizard.AdoptName);
        var folder = Assert.Single(wizard.AdoptFolders);
        Assert.Equal(env.Desktop, folder.Path); // found again where it was
        wizard.AdoptModes.Single(m => m.Mode == FirstRun.KeepOnDrive).IsSelected = true;
        wizard.AdoptModes.Single(m => m.Mode == FirstRun.RestoreEverything).IsSelected = false;
        await wizard.FinishAdoptCommand.ExecuteAsync(null);

        Assert.NotNull(adopted);
        Assert.Equal(original.DriveId, adopted.DriveId);
        Assert.Equal(FirstRun.KeepOnDrive, wizard.FirstRun);

        var page = main.OnBackupAdded(adopted, runFirstBackup: false);
        await main.RunFirstBackupAsync(page, wizard.FirstRun);

        Assert.Equal(0, page.PendingCount);                 // nothing is asked about
        Assert.Equal("1", page.Dashboard.OffloadedCount);   // b.txt stays on the drive
        Assert.Equal("1", page.Dashboard.ProtectedCount);
        Assert.True(File.Exists(Path.Combine(adopted.LastKnownRoot!, "Desktop", "b.txt")));
    }

    [Fact]
    public async Task Restoring_everything_brings_the_files_to_a_pc_that_has_none_of_them()
    {
        using var env = new AppEnv();
        File.WriteAllText(Path.Combine(env.Desktop, "a.txt"), "a");
        env.SetUpBackup("Personal files");
        var main = new MainViewModel(env.Services);
        await main.Backups[0].Dashboard.SyncWithoutReviewAsync();
        main.RemoveBackup(main.Backups[0]);
        string newDesktop = Path.Combine(env.Dir, "new-pc", "Desktop");

        using var wizard = new SetupWizardViewModel(env.Services, []);
        BackupProfile? adopted = null;
        wizard.Completed += p => adopted = p;
        wizard.PickFolder = () => env.Drive;
        wizard.ChooseDriveFolderCommand.Execute(null);
        wizard.UseFoundBackupCommand.Execute(null);
        wizard.PickFolder = () => newDesktop;
        wizard.AdoptFolders[0].ChooseCommand.Execute(null);
        await wizard.FinishAdoptCommand.ExecuteAsync(null);
        Assert.Equal(FirstRun.RestoreEverything, wizard.FirstRun);

        var page = main.OnBackupAdded(adopted!, runFirstBackup: false);
        await main.RunFirstBackupAsync(page, wizard.FirstRun);

        Assert.Equal("a", File.ReadAllText(Path.Combine(newDesktop, "a.txt")));
        Assert.True(page.Restore.IsDone);
        Assert.Equal("1", page.Dashboard.ProtectedCount);
        Assert.Equal(0, page.PendingCount);
    }
}
