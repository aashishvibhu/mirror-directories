using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DriveSync.App.Services;
using DriveSync.Core.Engine;

namespace DriveSync.App.ViewModels;

/// <summary>The main window: left navigation and the page shown beside it.</summary>
public sealed partial class MainViewModel : ObservableObject
{
    public const int HomeIndex = 0;
    public const int MissingIndex = 1;

    public MainViewModel(AppServices services)
    {
        Services = services;
        Dashboard = new DashboardViewModel(this);
        Missing = new MissingFilesViewModel(this);
        CurrentPage = Dashboard;
    }

    public AppServices Services { get; }

    public DashboardViewModel Dashboard { get; }

    public MissingFilesViewModel Missing { get; }

    [ObservableProperty]
    public partial object CurrentPage { get; set; }

    /// <summary>Which navigation item is highlighted. The review screen belongs to Home.</summary>
    [ObservableProperty]
    public partial int NavIndex { get; set; }

    /// <summary>Files waiting for a decision, shown as a badge on "Missing files".</summary>
    [ObservableProperty]
    public partial int PendingCount { get; set; }

    public bool UsesStandInRecycleBin => Services.UsesStandInRecycleBin;

    partial void OnCurrentPageChanged(object oldValue, object newValue)
    {
        if (oldValue is PreviewViewModel preview && !ReferenceEquals(oldValue, newValue))
            preview.Dispose();
        NavIndex = newValue is MissingFilesViewModel ? MissingIndex : HomeIndex;
    }

    partial void OnNavIndexChanged(int value)
    {
        if (value == MissingIndex && CurrentPage is not MissingFilesViewModel)
            _ = ShowMissingAsync();
        else if (value == HomeIndex && CurrentPage is MissingFilesViewModel)
            ShowHome();
    }

    [RelayCommand]
    public void ShowHome()
    {
        CurrentPage = Dashboard;
        if (!Dashboard.IsSyncing)
            _ = Dashboard.RefreshAsync();
    }

    [RelayCommand]
    public async Task ShowMissingAsync()
    {
        CurrentPage = Missing;
        await Missing.LoadAsync();
    }

    /// <summary>Scans the PC and shows what a sync would do, before anything changes.</summary>
    public async Task ShowPreviewAsync()
    {
        var preview = new PreviewViewModel(this);
        CurrentPage = preview;
        await preview.LoadAsync();
    }

    /// <summary>Called by the review screen: runs the reviewed plan with progress on the dashboard.</summary>
    public void ApplyPreview(DriveSyncSession session, SyncPlan plan)
    {
        CurrentPage = Dashboard;
        _ = Dashboard.RunAsync(session, plan);
    }
}
