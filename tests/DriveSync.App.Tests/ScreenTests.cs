using System.IO;
using DriveSync.App.Services;
using DriveSync.App.ViewModels;
using DriveSync.Core;
using DriveSync.Core.Model;
using DriveSync.Core.RecycleBin;
using Microsoft.Data.Sqlite;

namespace DriveSync.App.Tests;

/// <summary>A PC, backup drives and app settings in a folder next to the test binaries. Nothing outside it is touched.</summary>
public sealed class AppEnv : IDisposable
{
    static readonly string Root = Path.Combine(AppContext.BaseDirectory, "testdata");

    public string Dir { get; } = Path.Combine(Root, "app-" + Guid.NewGuid().ToString("N")[..10]);

    public AppEnv()
    {
        Directory.CreateDirectory(Path.Combine(Dir, "pc", "Desktop"));
        Directory.CreateDirectory(Path.Combine(Dir, "pc", "Pictures"));
        Directory.CreateDirectory(Path.Combine(Dir, "drive"));
        Services = AppServices.Create(Path.Combine(Dir, "appdata"), new FolderRecycleBin(Path.Combine(Dir, "recycle-bin")));
    }

    public AppServices Services { get; }

    public string Desktop => Path.Combine(Dir, "pc", "Desktop");

    public string Pictures => Path.Combine(Dir, "pc", "Pictures");

    public string Drive => Path.Combine(Dir, "drive");

    public string NewDrive(string name)
    {
        string path = Path.Combine(Dir, name);
        Directory.CreateDirectory(path);
        return path;
    }

    public BackupProfile SetUpBackup(string name = "My backup", string? drive = null, params string[] folders)
    {
        var profile = BackupSetup.CreateProfile(drive ?? Drive, folders.Length > 0 ? folders : [Desktop], name, Services.Backups);
        Services.Add(profile);
        return profile;
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try
        {
            Directory.Delete(Dir, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    public static async Task WaitUntil(Func<bool> done, int seconds = 20)
    {
        var giveUp = DateTime.UtcNow.AddSeconds(seconds);
        while (!done() && DateTime.UtcNow < giveUp)
            await Task.Delay(50);
        Assert.True(done(), "Timed out waiting.");
    }
}

public class SetupWizardTests
{
    [Fact]
    public void Adding_a_folder_as_the_backup_location_leaves_only_that_one_selected()
    {
        using var env = new AppEnv();
        using var wizard = new SetupWizardViewModel(env.Services, []);
        Assert.NotEmpty(wizard.Drives);
        wizard.Drives[0].IsSelected = true;

        wizard.PickFolder = () => env.Drive;
        wizard.ChooseDriveFolderCommand.Execute(null);

        var selected = Assert.Single(wizard.Drives, d => d.IsSelected);
        Assert.Equal(env.Drive, selected.Root);
        Assert.Same(selected, wizard.SelectedDrive);
    }

    [Fact]
    public void Adding_the_same_folder_twice_doesnt_list_it_twice()
    {
        using var env = new AppEnv();
        using var wizard = new SetupWizardViewModel(env.Services, []);
        wizard.PickFolder = () => env.Drive;

        wizard.ChooseDriveFolderCommand.Execute(null);
        wizard.ChooseDriveFolderCommand.Execute(null);

        Assert.Single(wizard.Drives, d => d.Root == env.Drive);
    }

    [Fact]
    public void Refresh_keeps_one_drive_selected()
    {
        using var env = new AppEnv();
        using var wizard = new SetupWizardViewModel(env.Services, []);
        wizard.Drives[0].IsSelected = true;

        wizard.RefreshDrivesCommand.Execute(null);

        Assert.Single(wizard.Drives, d => d.IsSelected);
    }

    [Fact]
    public void Adding_a_backup_suggests_a_free_name_tags_shared_folders_and_rejects_duplicate_names()
    {
        using var env = new AppEnv();
        env.SetUpBackup("Personal files");

        using var wizard = new SetupWizardViewModel(env.Services, [new FolderSuggestion("Desktop", env.Desktop), new FolderSuggestion("Pictures", env.Pictures)]);

        Assert.False(wizard.IsFirstBackup);
        Assert.Equal("Backup 2", wizard.Name);
        Assert.Equal("In Personal files", wizard.Folders[0].AlsoIn);
        Assert.Equal("", wizard.Folders[1].AlsoIn);
        wizard.Folders[1].IsSelected = true;
        wizard.Name = "personal FILES";
        Assert.NotNull(wizard.NameProblem);
        Assert.False(wizard.NextCommand.CanExecute(null));
        wizard.Name = "Photos";
        Assert.True(wizard.NextCommand.CanExecute(null));
    }

    [Fact]
    public async Task Finishing_the_wizard_adds_a_backup_alongside_the_existing_one()
    {
        using var env = new AppEnv();
        env.SetUpBackup("Personal files");
        using var wizard = new SetupWizardViewModel(env.Services, [new FolderSuggestion("Pictures", env.Pictures)]);
        BackupProfile? created = null;
        wizard.Completed += p => created = p;
        wizard.Name = "Photos";
        wizard.Folders[0].IsSelected = true;
        wizard.PickFolder = () => env.Drive;
        wizard.ChooseDriveFolderCommand.Execute(null);

        await wizard.FinishCommand.ExecuteAsync(null);

        Assert.NotNull(created);
        Assert.Equal(2, env.Services.Backups.Count);
        Assert.Equal("DriveSyncBackup-photos", created.BackupFolderName);
        Assert.Equal(2, new BackupStore(Path.Combine(env.Dir, "appdata")).Load().Count);
    }
}

public class HomeTests
{
    [Fact]
    public async Task Sync_now_is_available_again_after_the_first_backup_and_after_each_sync()
    {
        using var env = new AppEnv();
        File.WriteAllText(Path.Combine(env.Desktop, "a.txt"), "first");
        env.SetUpBackup();
        var main = new MainViewModel(env.Services);
        var page = Assert.Single(main.Backups);

        await page.Dashboard.SyncWithoutReviewAsync();

        Assert.False(page.Dashboard.IsSyncing);
        Assert.True(page.Dashboard.SyncNowCommand.CanExecute(null), "Sync now should be enabled after the first backup.");
        Assert.Equal("1", page.Dashboard.ProtectedCount);

        // A second backup in the same window: Sync now -> review -> Back up now.
        File.WriteAllText(Path.Combine(env.Desktop, "b.txt"), "second");
        await page.Dashboard.SyncNowCommand.ExecuteAsync(null);
        var review = Assert.IsType<PreviewViewModel>(page.Content);
        Assert.True(review.ApplyCommand.CanExecute(null));
        review.ApplyCommand.Execute(null);
        await AppEnv.WaitUntil(() => page.Content is DashboardViewModel && !page.Dashboard.IsSyncing && page.Dashboard.ProtectedCount == "2");

        Assert.True(page.Dashboard.SyncNowCommand.CanExecute(null), "Sync now should be enabled after a sync.");
        Assert.True(File.Exists(Path.Combine(env.Drive, BackupSetup.DefaultFolderName, "Desktop", "b.txt")));
    }

    [Fact]
    public async Task Leaving_the_review_screen_frees_the_drive_for_the_next_sync()
    {
        using var env = new AppEnv();
        File.WriteAllText(Path.Combine(env.Desktop, "a.txt"), "a");
        env.SetUpBackup();
        var main = new MainViewModel(env.Services);
        var page = main.Backups[0];
        await page.Dashboard.SyncWithoutReviewAsync();

        await page.SyncNowCommand.ExecuteAsync(null);
        page.ShowHome();
        await page.SyncNowCommand.ExecuteAsync(null);

        var review = Assert.IsType<PreviewViewModel>(page.Content);
        Assert.Null(review.Error);
    }
}

public class ReviewAndDriveTests
{
    [Fact]
    public async Task Leaving_the_review_screen_while_it_is_still_scanning_is_safe()
    {
        using var env = new AppEnv();
        for (int i = 0; i < 300; i++)
            File.WriteAllText(Path.Combine(env.Desktop, $"file{i}.txt"), "x" + i);
        env.SetUpBackup();
        var main = new MainViewModel(env.Services);
        var page = main.Backups[0];

        var scanning = page.SyncNowAsync();
        page.ShowHome();
        await scanning; // must not throw

        await page.SyncNowAsync();
        var review = Assert.IsType<PreviewViewModel>(page.Content);
        Assert.Null(review.Error);
        Assert.True(review.HasChanges);
    }

    [Fact]
    public async Task Going_to_the_overview_closes_an_open_review_so_sync_all_can_use_the_drive()
    {
        using var env = new AppEnv();
        File.WriteAllText(Path.Combine(env.Desktop, "a.txt"), "a");
        env.SetUpBackup();
        var main = new MainViewModel(env.Services);
        var page = main.Backups[0];
        await page.SyncNowAsync();
        Assert.IsType<PreviewViewModel>(page.Content);

        main.ShowOverview();
        Assert.IsType<DashboardViewModel>(page.Content);
        await main.SyncAllCommand.ExecuteAsync(null);

        Assert.False(page.Dashboard.LastRunNeedsAttention, page.Dashboard.LastRunMessage);
        Assert.Equal("1", page.Dashboard.ProtectedCount);
    }

    [Fact]
    public async Task The_review_says_when_this_backups_rule_will_remove_missing_files()
    {
        using var env = new AppEnv();
        File.WriteAllText(Path.Combine(env.Desktop, "a.txt"), "a");
        File.WriteAllText(Path.Combine(env.Desktop, "b.txt"), "b");
        var profile = env.SetUpBackup();
        var main = new MainViewModel(env.Services);
        var page = main.Backups[0];
        await page.Dashboard.SyncWithoutReviewAsync();
        profile.MissingFilePolicy = MissingFilePolicy.AlwaysRemove;
        File.Delete(Path.Combine(env.Desktop, "b.txt"));

        await page.SyncNowAsync();

        var review = Assert.IsType<PreviewViewModel>(page.Content);
        var missing = Assert.Single(review.Categories, c => c.Title == "Missing from this PC");
        Assert.Contains("Recycle Bin", missing.Note);
        Assert.DoesNotContain("These stay on the backup", missing.Note);
    }

    [Fact]
    public async Task A_sync_without_review_reports_a_folder_it_couldnt_find()
    {
        using var env = new AppEnv();
        File.WriteAllText(Path.Combine(env.Desktop, "a.txt"), "a");
        File.WriteAllText(Path.Combine(env.Pictures, "cat.jpg"), "cat");
        env.SetUpBackup(folders: [env.Desktop, env.Pictures]);
        var main = new MainViewModel(env.Services);
        var page = main.Backups[0];
        await page.Dashboard.SyncWithoutReviewAsync();
        Directory.Move(env.Pictures, env.Pictures + "-gone");

        await page.Dashboard.SyncWithoutReviewAsync();

        Assert.True(page.Dashboard.LastRunNeedsAttention);
        Assert.Contains(page.Dashboard.LastRunProblems, p => p.Contains("Pictures"));
        Assert.Equal("2", page.Dashboard.ProtectedCount); // nothing was treated as deleted
    }

    [Fact]
    public async Task A_reconnected_backup_opens_the_review_instead_of_syncing_straight_away()
    {
        using var env = new AppEnv();
        File.WriteAllText(Path.Combine(env.Desktop, "a.txt"), "a");
        var first = env.SetUpBackup();
        var main = new MainViewModel(env.Services);
        await main.Backups[0].Dashboard.SyncWithoutReviewAsync();
        main.RemoveBackup(main.Backups[0]);
        File.Delete(Path.Combine(env.Desktop, "a.txt"));

        var again = env.SetUpBackup();
        Assert.Equal(first.DriveId, again.DriveId);
        var page = main.OnBackupAdded(again, runFirstBackup: false);
        await main.RunFirstBackupAsync(page);

        Assert.IsType<PreviewViewModel>(page.Content);
        Assert.True(File.Exists(Path.Combine(env.Drive, BackupSetup.DefaultFolderName, "Desktop", "a.txt")));
    }

    [Fact]
    public async Task Plugging_a_drive_in_or_out_is_noticed_without_opening_the_backup()
    {
        using var env = new AppEnv();
        File.WriteAllText(Path.Combine(env.Desktop, "a.txt"), "a");
        string drive = env.NewDrive("drive-f");
        env.SetUpBackup(drive: drive);
        var main = new MainViewModel(env.Services);
        var page = main.Backups[0];
        await page.Dashboard.SyncWithoutReviewAsync();
        Assert.True(page.Dashboard.IsDriveConnected);

        Directory.Move(drive, drive + "-unplugged");
        await main.CheckDrivesAsync();
        Assert.False(page.Dashboard.IsDriveConnected);
        Assert.False(page.SyncNowCommand.CanExecute(null));

        Directory.Move(drive + "-unplugged", drive);
        await main.CheckDrivesAsync();
        Assert.True(page.Dashboard.IsDriveConnected);
        Assert.True(page.SyncNowCommand.CanExecute(null));
    }
}

public class MultipleBackupScreenTests
{
    [Fact]
    public async Task Each_backup_gets_a_menu_entry_and_sync_all_runs_every_connected_one()
    {
        using var env = new AppEnv();
        File.WriteAllText(Path.Combine(env.Desktop, "a.txt"), "a");
        File.WriteAllText(Path.Combine(env.Pictures, "cat.jpg"), "cat");
        env.SetUpBackup("Personal files");
        env.SetUpBackup("Photos", folders: env.Pictures);
        var offline = env.SetUpBackup("Copy on F", env.NewDrive("drive-f"));
        Directory.Move(Path.Combine(env.Dir, "drive-f"), Path.Combine(env.Dir, "drive-f-unplugged"));
        var main = new MainViewModel(env.Services);

        Assert.Equal(3, main.Backups.Count);
        Assert.Equal(5, main.NavItems.Count); // overview, "Backups" label, three backups

        await main.SyncAllCommand.ExecuteAsync(null);
        await main.Overview.RefreshAsync();

        Assert.Equal("1", main.Backups[0].Dashboard.ProtectedCount);
        Assert.Equal("1", main.Backups[1].Dashboard.ProtectedCount);
        Assert.False(main.Backups[2].Dashboard.IsDriveConnected);
        Assert.Contains("2 connected", main.Overview.Headline);
    }

    [Fact]
    public async Task Edit_backup_stops_backing_up_a_folder_but_keeps_its_files_and_can_add_it_back()
    {
        using var env = new AppEnv();
        File.WriteAllText(Path.Combine(env.Desktop, "a.txt"), "a");
        File.WriteAllText(Path.Combine(env.Pictures, "cat.jpg"), "cat");
        env.SetUpBackup("Everything", folders: [env.Desktop, env.Pictures]);
        var main = new MainViewModel(env.Services);
        var page = main.Backups[0];
        await page.Dashboard.SyncWithoutReviewAsync();

        await page.EditCommand.ExecuteAsync(null);
        var edit = Assert.IsType<EditBackupViewModel>(page.Content);
        edit.Folders.First(f => f.Name == "Pictures").RemoveCommand.Execute(null);
        Assert.True(edit.IsRemoveFolderOpen);
        Assert.True(edit.KeepRemovedFiles);
        await edit.ConfirmRemoveFolderCommand.ExecuteAsync(null);

        Assert.Single(edit.Folders);
        Assert.Single(edit.RemovedFolders);
        Assert.True(File.Exists(Path.Combine(env.Drive, BackupSetup.DefaultFolderName, "Pictures", "cat.jpg")));
        Assert.Equal("Desktop → " + page.Profile.LastKnownRoot, page.Route);

        edit.RemovedFolders[0].AddBackCommand.Execute(null);
        Assert.Equal(2, page.Profile.Sources.Count);
        Assert.Empty(page.Profile.RemovedFolders);
    }

    [Fact]
    public async Task Edit_backup_can_send_a_removed_folders_files_to_the_recycle_bin()
    {
        using var env = new AppEnv();
        File.WriteAllText(Path.Combine(env.Desktop, "a.txt"), "a");
        File.WriteAllText(Path.Combine(env.Pictures, "cat.jpg"), "cat");
        env.SetUpBackup("Everything", folders: [env.Desktop, env.Pictures]);
        var main = new MainViewModel(env.Services);
        var page = main.Backups[0];
        await page.Dashboard.SyncWithoutReviewAsync();
        await page.EditCommand.ExecuteAsync(null);
        var edit = (EditBackupViewModel)page.Content;

        edit.Folders.First(f => f.Name == "Pictures").RemoveCommand.Execute(null);
        edit.KeepRemovedFiles = false;
        await edit.ConfirmRemoveFolderCommand.ExecuteAsync(null);

        Assert.Empty(edit.RemovedFolders);
        Assert.False(Directory.Exists(Path.Combine(env.Drive, BackupSetup.DefaultFolderName, "Pictures")));
        Assert.True(File.Exists(Path.Combine(env.Pictures, "cat.jpg")));
        Assert.Contains("Recycle Bin", edit.Message);
    }

    [Fact]
    public async Task Moving_a_backup_switches_it_to_the_new_drive_and_leaves_the_old_one()
    {
        using var env = new AppEnv();
        File.WriteAllText(Path.Combine(env.Desktop, "a.txt"), "a");
        env.SetUpBackup("Personal files");
        var main = new MainViewModel(env.Services);
        var page = main.Backups[0];
        await page.Dashboard.SyncWithoutReviewAsync();
        string oldRoot = page.Profile.LastKnownRoot!;
        string newDrive = env.NewDrive("drive-g");

        await page.EditCommand.ExecuteAsync(null);
        var edit = (EditBackupViewModel)page.Content;
        edit.PickFolder = () => newDrive;
        edit.OpenMoveCommand.Execute(null);
        edit.ChooseMoveFolderCommand.Execute(null);
        Assert.True(edit.StartMoveCommand.CanExecute(null));
        await edit.StartMoveCommand.ExecuteAsync(null);

        Assert.Null(edit.MoveError);
        Assert.False(edit.IsMoveOpen);
        Assert.Equal(Path.Combine(newDrive, BackupSetup.DefaultFolderName), page.Profile.LastKnownRoot);
        Assert.True(File.Exists(Path.Combine(oldRoot, "Desktop", "a.txt")));
        Assert.Equal(page.Profile.LastKnownRoot, new BackupStore(Path.Combine(env.Dir, "appdata")).Load()[0].LastKnownRoot);
    }

    [Fact]
    public async Task Removing_a_backup_forgets_it_and_leaves_its_files()
    {
        using var env = new AppEnv();
        File.WriteAllText(Path.Combine(env.Desktop, "a.txt"), "a");
        env.SetUpBackup("Personal files");
        env.SetUpBackup("Photos", folders: env.Pictures);
        var main = new MainViewModel(env.Services);
        var page = main.Backups[0];
        await page.Dashboard.SyncWithoutReviewAsync();

        await page.EditCommand.ExecuteAsync(null);
        var edit = (EditBackupViewModel)page.Content;
        edit.AskRemoveBackupCommand.Execute(null);
        Assert.True(edit.IsConfirmOpen);
        await edit.ConfirmYesCommand.ExecuteAsync(null);

        Assert.Single(main.Backups);
        Assert.Same(main.Overview, main.CurrentPage);
        Assert.Single(new BackupStore(Path.Combine(env.Dir, "appdata")).Load());
        Assert.True(File.Exists(Path.Combine(env.Drive, BackupSetup.DefaultFolderName, "Desktop", "a.txt")));
    }

    [Fact]
    public async Task Edit_backup_saves_a_new_name_and_rule_but_not_a_duplicate_name()
    {
        using var env = new AppEnv();
        env.SetUpBackup("Personal files");
        env.SetUpBackup("Photos", folders: env.Pictures);
        var main = new MainViewModel(env.Services);
        var page = main.Backups[0];
        await page.EditCommand.ExecuteAsync(null);
        var edit = (EditBackupViewModel)page.Content;

        edit.Name = "photos";
        edit.SaveCommand.Execute(null);
        Assert.NotNull(edit.Error);

        edit.Name = "Home PC";
        edit.Policies.First(p => p.Policy == MissingFilePolicy.AlwaysKeep).IsSelected = true;
        edit.Policies.First(p => p.Policy == MissingFilePolicy.Ask).IsSelected = false;
        edit.SaveCommand.Execute(null);

        Assert.Equal("Home PC", page.Name);
        Assert.Equal(MissingFilePolicy.AlwaysKeep, page.Profile.MissingFilePolicy);
        Assert.IsType<DashboardViewModel>(page.Content);
    }
}
