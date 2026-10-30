using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DriveSync.App.Services;
using DriveSync.Core.Model;

namespace DriveSync.App.ViewModels;

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
    public BackupPageViewModel OnBackupAdded(BackupProfile profile, bool runFirstBackup)
    {
        var page = AddPage(profile);
        Select(page);
        if (runFirstBackup)
            _ = page.Dashboard.SyncWithoutReviewAsync();
        return page;
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
