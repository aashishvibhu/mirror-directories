using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DriveSync.App.Services;
using DriveSync.Core.Engine;
using DriveSync.Core.Model;

namespace DriveSync.App.ViewModels;

/// <summary>What happens straight after a backup is added to the app.</summary>
public enum FirstRun
{
    /// <summary>A new backup copies everything; one that already has records opens the review first.</summary>
    Backup,

    /// <summary>A backup from another PC: copy all its files back to this PC, then sync.</summary>
    RestoreEverything,

    /// <summary>A backup from another PC: leave its files on the drive, marked as offloaded, then sync.</summary>
    KeepOnDrive,

    /// <summary>Show what a sync would do and let the user decide.</summary>
    Review,
}

/// <summary>A non-selectable label in the left menu, such as "Backups".</summary>
public sealed record NavHeader(string Title);

/// <summary>The main window: the left menu (overview, one entry per backup) and the page beside it.</summary>
public sealed partial class MainViewModel : ObservableObject
{
    public MainViewModel(AppServices services)
    {
        Services = services;
        Overview = new OverviewViewModel(this);
        NavItems.Add(Overview);
        NavItems.Add(new NavHeader("Backups"));
        foreach (var profile in services.Backups)
            AddPage(profile);
        CurrentPage = Overview;
        SelectedNavItem = Overview;
    }

    public AppServices Services { get; }

    public OverviewViewModel Overview { get; }

    public ObservableCollection<BackupPageViewModel> Backups { get; } = [];

    /// <summary>The left menu: the overview, a "Backups" label, then each backup.</summary>
    public ObservableCollection<object> NavItems { get; } = [];

    [ObservableProperty]
    public partial object? SelectedNavItem { get; set; }

    [ObservableProperty]
    public partial object CurrentPage { get; set; }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SyncAllCommand))]
    public partial bool IsSyncingAll { get; set; }

    public bool UsesStandInRecycleBin => Services.UsesStandInRecycleBin;

    /// <summary>Raised when the user asks to add a backup. The window opens the setup wizard.</summary>
    public event Action? AddBackupRequested;

    partial void OnSelectedNavItemChanged(object? value)
    {
        switch (value)
        {
            case OverviewViewModel:
                ShowOverview();
                break;
            case BackupPageViewModel page when !ReferenceEquals(CurrentPage, page):
                CurrentPage = page;
                page.ShowHome();
                break;
        }
    }

    [RelayCommand]
    public void ShowOverview()
    {
        // A review screen left open keeps its backup's drive to itself; close it so the overview can read and sync it.
        foreach (var page in Backups)
        {
            if (page.Content is PreviewViewModel)
                page.Content = page.Dashboard;
        }
        CurrentPage = Overview;
        SelectedNavItem = Overview;
        _ = Overview.RefreshAsync();
    }

    /// <summary>Shows a backup's page, highlighting it in the menu.</summary>
    public void Select(BackupPageViewModel page)
    {
        CurrentPage = page;
        if (!ReferenceEquals(SelectedNavItem, page))
            SelectedNavItem = page;
    }

    [RelayCommand]
    void AddBackup() => AddBackupRequested?.Invoke();

    /// <summary>Adds a newly set-up backup, opens it and runs its first backup.</summary>
    public BackupPageViewModel OnBackupAdded(BackupProfile profile, bool runFirstBackup, FirstRun mode = FirstRun.Backup)
    {
        var page = AddPage(profile);
        Select(page);
        if (runFirstBackup)
            _ = RunFirstBackupAsync(page, mode);
        return page;
    }

    /// <summary>
    /// Starts a new backup's first sync. A brand-new backup just copies everything. A backup that was reconnected
    /// (its drive already holds files and records) goes through the review screen instead, so nothing recorded
    /// there is touched before the user has seen what would happen.
    /// </summary>
    public async Task RunFirstBackupAsync(BackupPageViewModel page, FirstRun mode = FirstRun.Backup)
    {
        switch (mode)
        {
            case FirstRun.RestoreEverything:
                // Bring the files back first; the sync that follows then finds them all in place.
                await page.Restore.RunNowAsync(new RestoreRequest([.. page.Profile.Sources.Select(s => s.Name)]), "Restoring this backup to this PC");
                await page.Dashboard.SyncWithoutReviewAsync();
                break;
            case FirstRun.KeepOnDrive:
                try
                {
                    using var session = await page.OpenSessionAsync();
                    await session.KeepMissingOnBackupAsync();
                }
                catch (Exception e) when (e is Core.DriveSyncException or IOException or Microsoft.Data.Sqlite.SqliteException)
                {
                    // Couldn't mark them: fall back to the review, where nothing happens unseen.
                    await page.SyncNowAsync();
                    break;
                }
                await page.Dashboard.SyncWithoutReviewAsync();
                break;
            case FirstRun.Review:
                await page.SyncNowAsync();
                break;
            default:
                bool reconnected = page.Profile.LastKnownRoot is { } root && Core.Storage.BackupLocation.HasCatalog(root);
                await (reconnected ? page.SyncNowAsync() : page.Dashboard.SyncWithoutReviewAsync());
                break;
        }
    }

    /// <summary>Forgets a backup. Its files and history on the drive are left exactly as they are.</summary>
    public void RemoveBackup(BackupPageViewModel page)
    {
        Services.Remove(page.Profile);
        Backups.Remove(page);
        NavItems.Remove(page);
        ShowOverview();
        OnBackupStatusChanged();
    }

    bool CanSyncAll() => !IsSyncingAll;

    /// <summary>Syncs every backup whose drive is plugged in, one after another, without the review step.</summary>
    [RelayCommand(CanExecute = nameof(CanSyncAll))]
    async Task SyncAllAsync()
    {
        IsSyncingAll = true;
        try
        {
            foreach (var page in Backups.ToList())
            {
                if (!Backups.Contains(page) || page.Dashboard.IsSyncing)
                    continue;
                if (!await Task.Run(() => AppServices.IsConnected(page.Profile)))
                    continue;
                await page.Dashboard.SyncWithoutReviewAsync();
            }
        }
        finally
        {
            IsSyncingAll = false;
        }
    }

    bool _checkingDrives;

    /// <summary>
    /// Notices backup drives being plugged in or unplugged, and refreshes the backups whose drive came or went.
    /// The window calls this when Windows reports a device change and when it becomes the active window.
    /// </summary>
    public async Task CheckDrivesAsync()
    {
        if (_checkingDrives)
            return;
        _checkingDrives = true;
        try
        {
            foreach (var page in Backups.ToList())
            {
                if (page.Dashboard.IsSyncing || page.Content is PreviewViewModel)
                    continue;
                bool connected = await Task.Run(() => AppServices.IsConnected(page.Profile));
                if (connected != page.Dashboard.IsDriveConnected && Backups.Contains(page))
                    await page.Dashboard.RefreshAsync();
            }
        }
        finally
        {
            _checkingDrives = false;
        }
    }

    /// <summary>True while any backup is copying files, so the window can ask before closing.</summary>
    public bool IsAnySyncing => Backups.Any(p => p.Dashboard.IsSyncing);

    /// <summary>Called when any backup's status, badge or sync state changes, to update the overview.</summary>
    internal void OnBackupStatusChanged() => Overview.UpdateHeadline();

    BackupPageViewModel AddPage(BackupProfile profile)
    {
        var page = new BackupPageViewModel(this, profile);
        Backups.Add(page);
        NavItems.Add(page);
        return page;
    }
}
