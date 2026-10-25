using System.IO;
using DriveSync.App.Services;
using DriveSync.App.ViewModels;
using DriveSync.Core;
using DriveSync.Core.RecycleBin;
using Microsoft.Data.Sqlite;

namespace DriveSync.App.Tests;

/// <summary>A PC, a backup drive and app settings in a folder next to the test binaries. Nothing outside it is touched.</summary>
public sealed class AppEnv : IDisposable
{
    static readonly string Root = Path.Combine(AppContext.BaseDirectory, "testdata");

    public string Dir { get; } = Path.Combine(Root, "app-" + Guid.NewGuid().ToString("N")[..10]);

    public AppEnv()
    {
        Directory.CreateDirectory(Path.Combine(Dir, "pc", "Desktop"));
        Directory.CreateDirectory(Path.Combine(Dir, "drive"));
        Services = AppServices.Create(Path.Combine(Dir, "appdata"), new FolderRecycleBin(Path.Combine(Dir, "recycle-bin")));
    }

    public AppServices Services { get; }

    public string Desktop => Path.Combine(Dir, "pc", "Desktop");

    public string Drive => Path.Combine(Dir, "drive");

    public void SetUpBackup() => Services.SaveProfile(BackupSetup.CreateProfile(Drive, [Desktop]));

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

        await main.Dashboard.RunFirstBackupAsync();

        Assert.False(main.Dashboard.IsSyncing);
        Assert.True(main.Dashboard.SyncNowCommand.CanExecute(null), "Sync now should be enabled after the first backup.");
        Assert.Equal("1", main.Dashboard.ProtectedCount);

        // A second backup in the same window: Sync now -> review -> Back up now.
        File.WriteAllText(Path.Combine(env.Desktop, "b.txt"), "second");
        await main.Dashboard.SyncNowCommand.ExecuteAsync(null);
        var review = Assert.IsType<PreviewViewModel>(main.CurrentPage);
        Assert.True(review.ApplyCommand.CanExecute(null));
        review.ApplyCommand.Execute(null);
        await AppEnv.WaitUntil(() => main.CurrentPage is DashboardViewModel && !main.Dashboard.IsSyncing && main.Dashboard.ProtectedCount == "2");

        Assert.True(main.Dashboard.SyncNowCommand.CanExecute(null), "Sync now should be enabled after a sync.");
        Assert.True(File.Exists(Path.Combine(env.Drive, BackupSetup.DefaultFolderName, "Desktop", "b.txt")));
    }

    [Fact]
    public async Task Leaving_the_review_screen_frees_the_drive_for_the_next_sync()
    {
        using var env = new AppEnv();
        File.WriteAllText(Path.Combine(env.Desktop, "a.txt"), "a");
        env.SetUpBackup();
        var main = new MainViewModel(env.Services);
        await main.Dashboard.RunFirstBackupAsync();

        await main.Dashboard.SyncNowCommand.ExecuteAsync(null);
        main.ShowHome();
        await main.Dashboard.SyncNowCommand.ExecuteAsync(null);

        var review = Assert.IsType<PreviewViewModel>(main.CurrentPage);
        Assert.Null(review.Error);
    }
}
