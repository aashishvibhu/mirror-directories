using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DriveSync.Core;
using DriveSync.Core.Engine;
using DriveSync.Core.Model;

namespace DriveSync.App.ViewModels;

/// <summary>One backup's page: its Home and Missing files tabs, the review screen and its settings.</summary>
public sealed partial class BackupPageViewModel : ObservableObject
{
    public BackupPageViewModel(MainViewModel main, BackupProfile profile)
    {
        Main = main;
        Profile = profile;
        Dashboard = new DashboardViewModel(this);
        Missing = new MissingFilesViewModel(this);
        Files = new FilesViewModel(this);
        Storage = new StorageViewModel(this);
        Health = new HealthViewModel(this);
        Restore = new RestoreDialogViewModel(this);
        Restore.Finished += OnRestoreFinished;
        _homeTab = new PageTabViewModel("Home", ShowHomeCommand);
        _filesTab = new PageTabViewModel("Files", ShowFilesCommand);
        _missingTab = new PageTabViewModel("Missing files", ShowMissingCommand);
        _storageTab = new PageTabViewModel("Storage", ShowStorageCommand);
        _healthTab = new PageTabViewModel("Health", ShowHealthCommand);
        Tabs = [_homeTab, _filesTab, _missingTab, _storageTab, _healthTab];
        Content = Dashboard;
        _homeTab.IsSelected = true;
        RefreshDescription();
    }

    readonly PageTabViewModel _homeTab, _filesTab, _missingTab, _storageTab, _healthTab;

    public IReadOnlyList<PageTabViewModel> Tabs { get; }

    public FilesViewModel Files { get; }

    public StorageViewModel Storage { get; }

    public HealthViewModel Health { get; }

    /// <summary>The restore dialog, drawn over whichever tab asked for it.</summary>
    public RestoreDialogViewModel Restore { get; }

    /// <summary>Backup copies known to be missing or damaged, shown as a badge on the Health tab.</summary>
    [ObservableProperty]
    public partial int HealthIssues { get; set; }

    partial void OnHealthIssuesChanged(int value)
    {
        _healthTab.Badge = value;
        Main.OnBackupStatusChanged();
    }

    /// <summary>Where copies removed from this backup go, in words for a message.</summary>
    public string RemovedPlace => Profile.RemovedFilesGoTo == RemovedFilesGoTo.TrashFolder
        ? "the backup's trash folder"
        : "the backup drive's Recycle Bin";

    /// <summary>True when DriveSync knows where this file lived on the PC, so it can go back there.</summary>
    internal bool CanRestoreInPlace(string key)
    {
        string folder = KeyPath.SourceName(key);
        return Profile.Sources.Any(s => s.Name.Equals(folder, StringComparison.OrdinalIgnoreCase))
            || Profile.RemovedFolders.Any(r => r.Name.Equals(folder, StringComparison.OrdinalIgnoreCase) && r.Path.Length > 0);
    }

    void OnRestoreFinished()
    {
        if (Content is FilesViewModel)
            _ = Files.LoadAsync();
        else if (Content is StorageViewModel)
            _ = Storage.LoadAsync();
        if (!Dashboard.IsSyncing)
            _ = Dashboard.RefreshAsync();
    }

    [RelayCommand]
    public async Task ShowFilesAsync()
    {
        Content = Files;
        await Files.LoadAsync();
    }

    [RelayCommand]
    public async Task ShowStorageAsync()
    {
        Content = Storage;
        await Storage.LoadAsync();
    }

    [RelayCommand]
    public async Task ShowHealthAsync()
    {
        Content = Health;
        await Health.LoadAsync();
    }

    public MainViewModel Main { get; }

    public BackupProfile Profile { get; }

    public DashboardViewModel Dashboard { get; }

    public MissingFilesViewModel Missing { get; }

    /// <summary>What the page shows: the dashboard, the review screen, missing files, or the settings.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowsTabs), nameof(IsHomeTab), nameof(IsMissingTab))]
    public partial object Content { get; set; }

    [ObservableProperty]
    public partial string Name { get; set; } = "";

    /// <summary>"Desktop, Documents → E:\ Seagate Expansion".</summary>
    [ObservableProperty]
    public partial string Route { get; set; } = "";

    /// <summary>Files waiting for a decision, shown as badges in the menu and on the tab.</summary>
    [ObservableProperty]
    public partial int PendingCount { get; set; }

    public bool ShowsTabs => Content is DashboardViewModel or MissingFilesViewModel or FilesViewModel or StorageViewModel or HealthViewModel;

    public bool IsHomeTab => Content is DashboardViewModel;

    public bool IsMissingTab => Content is MissingFilesViewModel;

    partial void OnContentChanged(object oldValue, object newValue)
    {
        if (oldValue is PreviewViewModel preview && !ReferenceEquals(oldValue, newValue))
            preview.Dispose();
        if (_homeTab is null)
            return; // still being constructed
        _homeTab.IsSelected = newValue is DashboardViewModel;
        _filesTab.IsSelected = newValue is FilesViewModel;
        _missingTab.IsSelected = newValue is MissingFilesViewModel;
        _storageTab.IsSelected = newValue is StorageViewModel;
        _healthTab.IsSelected = newValue is HealthViewModel;
    }

    partial void OnPendingCountChanged(int value)
    {
        _missingTab.Badge = value;
        Main.OnBackupStatusChanged();
    }

    public Task<DriveSyncSession> OpenSessionAsync() => Main.Services.OpenSessionAsync(Profile);

    public string MirrorCatalogPath => Main.Services.MirrorCatalogPath(Profile);

    /// <summary>Opens this backup's page in the main window.</summary>
    [RelayCommand]
    void Open() => Main.Select(this);

    [RelayCommand]
    public void ShowHome()
    {
        Content = Dashboard;
        if (!Dashboard.IsSyncing)
            _ = Dashboard.RefreshAsync();
    }

    [RelayCommand]
    public async Task ShowMissingAsync()
    {
        Content = Missing;
        await Missing.LoadAsync();
    }

    /// <summary>From the overview: opens this backup on its Missing files tab.</summary>
    [RelayCommand]
    async Task OpenMissingAsync()
    {
        Main.Select(this);
        await ShowMissingAsync();
    }

    [RelayCommand]
    async Task EditAsync()
    {
        var edit = new EditBackupViewModel(this);
        Content = edit;
        await edit.LoadAsync();
    }

    /// <summary>Opens this backup and shows what a sync would do, before anything changes.</summary>
    [RelayCommand(CanExecute = nameof(CanSync))]
    public async Task SyncNowAsync()
    {
        Main.Select(this);
        var preview = new PreviewViewModel(this);
        Content = preview;
        await preview.LoadAsync();
    }

    bool CanSync() => Dashboard.IsDriveConnected && !Dashboard.IsSyncing;

    internal void OnSyncStateChanged()
    {
        SyncNowCommand.NotifyCanExecuteChanged();
        Main.OnBackupStatusChanged();
    }

    /// <summary>Called by the review screen: runs the reviewed plan with progress on the dashboard.</summary>
    public void ApplyPreview(DriveSyncSession session, SyncPlan plan)
    {
        Content = Dashboard;
        _ = Dashboard.RunAsync(session, plan);
    }

    /// <summary>Updates the name and route after the backup's settings change.</summary>
    public void RefreshDescription()
    {
        Name = Profile.Name;
        string folders = string.Join(", ", Profile.Sources.Select(s => s.Name));
        Route = $"{folders} → {DestinationLabel(Profile)}";
    }

    /// <summary>"E:\ Seagate Expansion" when the drive is plugged in, else the folder last used.</summary>
    internal static string DestinationLabel(BackupProfile profile)
    {
        string root = profile.LastKnownRoot ?? profile.BackupFolderName;
        string? drive = Path.GetPathRoot(root);
        // Only a backup at the top of a drive is described by the drive; one in a folder shows the folder.
        if (string.IsNullOrEmpty(drive) || drive.StartsWith(@"\\", StringComparison.Ordinal)
            || !string.Equals(Path.GetDirectoryName(root), drive, StringComparison.OrdinalIgnoreCase))
            return root;
        try
        {
            var info = new DriveInfo(drive);
            if (info.IsReady && !string.IsNullOrWhiteSpace(info.VolumeLabel))
                return $"{drive} {info.VolumeLabel}";
        }
        catch (IOException)
        {
        }
        return root;
    }
}
