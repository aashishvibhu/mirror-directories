using CommunityToolkit.Mvvm.ComponentModel;

namespace DriveSync.App.ViewModels;

/// <summary>The overview: one card per backup, with "Sync all connected" and "Add backup".</summary>
public sealed partial class OverviewViewModel(MainViewModel main) : ObservableObject
{
    public MainViewModel Main { get; } = main;

    [ObservableProperty]
    public partial string Headline { get; set; } = "";

    /// <summary>Reads every backup's status. Backups on different drives are read at the same time.</summary>
    public async Task RefreshAsync()
    {
        await Task.WhenAll(Main.Backups.Where(p => !p.Dashboard.IsSyncing).Select(p => p.Dashboard.RefreshAsync()));
        UpdateHeadline();
    }

    internal void UpdateHeadline()
    {
        var pages = Main.Backups;
        if (pages.Count == 0)
        {
            Headline = "No backups yet. Add one to start protecting your files.";
            return;
        }
        int connected = pages.Count(p => p.Dashboard.IsDriveConnected);
        int attention = pages.Count(p => p.PendingCount > 0 || p.Dashboard.Status == BackupStatus.NeedsAttention);
        var parts = new List<string>
        {
            pages.Count == 1 ? "1 backup" : $"{pages.Count} backups",
            connected == 1 ? "1 connected" : $"{connected} connected",
        };
        if (attention > 0)
            parts.Add(attention == 1 ? "1 needs your attention" : $"{attention} need your attention");
        Headline = string.Join(" · ", parts);
    }
}
