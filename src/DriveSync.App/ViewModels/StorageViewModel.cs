using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DriveSync.App.Services;
using DriveSync.Core;
using DriveSync.Core.Engine;
using Microsoft.Data.Sqlite;

namespace DriveSync.App.ViewModels;

/// <summary>What takes the space on a backup drive, as shares for a stacked bar.</summary>
public sealed class StorageBar
{
    public StorageBar(StorageUsage usage)
    {
        Usage = usage;
        long used = usage.Current + usage.Offloaded + usage.OldVersions + usage.TrashFolder;
        long other = usage.Total is { } total && usage.Free is { } free ? Math.Max(0, total - free - used) : 0;
        Other = other;
        Free = usage.Free ?? 0;
        if (used + other + Free == 0)
            Free = 1; // an empty bar still needs a width
        // On a big drive a small backup would be a sliver too thin to see, so anything that exists gets a visible minimum.
        double least = (used + other + Free) * 0.012;
        double Share(long bytes) => bytes == 0 ? 0 : Math.Max(bytes, least);
        Current = Share(usage.Current);
        Offloaded = Share(usage.Offloaded);
        OldVersions = Share(usage.OldVersions);
        Trash = Share(usage.TrashFolder);
        CurrentText = Formatting.Size(usage.Current);
        OffloadedText = Formatting.Size(usage.Offloaded);
        OldVersionsText = Formatting.Size(usage.OldVersions);
        TrashText = Formatting.Size(usage.TrashFolder);
        OtherText = Formatting.Size(other);
        FreeText = usage.Free is { } f ? Formatting.Size(f) : "unknown";
    }

    public StorageUsage Usage { get; }

    public double Current { get; }

    public double Offloaded { get; }

    public double OldVersions { get; }

    public double Trash { get; }

    public double Other { get; }

    public double Free { get; }

    public string CurrentText { get; }

    public string OffloadedText { get; }

    public string OldVersionsText { get; }

    public string TrashText { get; }

    public string OtherText { get; }

    public string FreeText { get; }

    /// <summary>A nudge when old versions or a nearly full drive deserve a look, else null.</summary>
    public string? Warning
    {
        get
        {
            long files = Usage.Current + Usage.Offloaded;
            if (Usage.Total is { } total && Usage.Free is { } free && total > 0 && free < total / 10)
                return $"The backup drive is nearly full: {Formatting.Size(free)} free. Old versions and removed files can make room.";
            if (files > 0 && Usage.OldVersions > files / 5)
                return $"Old versions take {Formatting.Size(Usage.OldVersions)}, more than a fifth of this backup.";
            return null;
        }
    }
}

public sealed record VersionHogRow(string Key, string Detail);

/// <summary>
/// "Storage": what takes the space on the backup drive, old versions (with clean-ups the user confirms),
/// and files removed from the backup, which can be restored until the user erases them in Explorer.
/// DriveSync itself never erases anything here.
/// </summary>
public sealed partial class StorageViewModel(BackupPageViewModel page) : ObservableObject
{
    IReadOnlyList<VersionEntry> _suggested = [];

    /// <summary>Opens a folder in File Explorer. Set by the view.</summary>
    public Action<string>? OpenInExplorer { get; set; }

    public ObservableCollection<VersionHogRow> Hogs { get; } = [];

    public ObservableCollection<RemovedRowViewModel> Removed { get; } = [];

    public ObservableCollection<string> ResultProblems { get; } = [];

    [ObservableProperty]
    public partial bool IsLoading { get; set; }

    [ObservableProperty]
    public partial bool IsBusy { get; set; }

    [ObservableProperty]
    public partial string? LoadError { get; set; }

    [ObservableProperty]
    public partial StorageBar? Bar { get; set; }

    [ObservableProperty]
    public partial string VersionsSummary { get; set; } = "";

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SuggestOlderThanYearCommand), nameof(SuggestKeepNewestCommand), nameof(SuggestThinOutCommand))]
    public partial bool HasVersions { get; set; }

    [ObservableProperty]
    public partial string RemovedSummary { get; set; } = "";

    [ObservableProperty]
    public partial string RemovedPlaceNote { get; set; } = "";

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(RestoreSelectedCommand))]
    public partial int SelectedCount { get; set; }

    [ObservableProperty]
    public partial string? ResultMessage { get; set; }

    [ObservableProperty]
    public partial bool ResultIsWarning { get; set; }

    // Clean-up confirmation

    [ObservableProperty]
    public partial bool IsConfirmOpen { get; set; }

    [ObservableProperty]
    public partial string ConfirmTitle { get; set; } = "";

    [ObservableProperty]
    public partial string ConfirmMessage { get; set; } = "";

    [ObservableProperty]
    public partial bool ConfirmHasAction { get; set; }

    public async Task LoadAsync()
    {
        IsLoading = true;
        LoadError = null;
        try
        {
            StorageUsage usage;
            VersionUsage versions;
            IReadOnlyList<DeletedEntry> removed;
            string place;
            using (var session = await page.OpenSessionAsync())
            {
                (usage, versions, removed) = await Task.Run(() => (session.GetStorageUsage(), session.GetVersionUsage(), session.ListDeleted()));
                place = session.RemovedPlace;
            }
            Bar = new StorageBar(usage);
            HasVersions = versions.Count > 0;
            VersionsSummary = versions.Count == 0
                ? "No old versions yet. One is kept each time a file changes between syncs."
                : $"{versions.Count:N0} old {(versions.Count == 1 ? "version" : "versions")} · {Formatting.Size(versions.Bytes)}. They stay until you remove them.";
            Hogs.Clear();
            foreach (var hog in versions.Biggest.Take(8))
                Hogs.Add(new VersionHogRow(hog.Key, $"{hog.Versions:N0} {(hog.Versions == 1 ? "version" : "versions")} · {Formatting.Size(hog.Bytes)}"));

            Removed.Clear();
            foreach (var entry in removed.OrderByDescending(d => d.DeletedUtc).ThenBy(d => d.Key, StringComparer.OrdinalIgnoreCase))
                Removed.Add(new RemovedRowViewModel(this, entry));
            RemovedSummary = removed.Count == 0
                ? "Nothing removed from this backup can be restored right now."
                : $"{Formatting.Files(removed.Count)} removed from this backup can still be restored · {Formatting.Size(removed.Sum(d => d.Size))}";
            RemovedPlaceNote = $"Files removed from this backup go to {place}. DriveSync never erases them. "
                + "To get the space back, open that place in File Explorer and delete what you no longer want.";
            UpdateSelection();
        }
        catch (Exception e) when (e is DriveSyncException or IOException or UnauthorizedAccessException or SqliteException)
        {
            LoadError = e is DriveSyncException ? e.Message : $"Couldn't read the backup: {e.Message}";
        }
        finally
        {
            IsLoading = false;
        }
    }

    // Old versions

    [RelayCommand(CanExecute = nameof(HasVersions))]
    Task SuggestOlderThanYear() => SuggestAsync(VersionCleanup.OlderThanDays, 365, "Remove versions older than a year?", "every version replaced in the last year is kept");

    [RelayCommand(CanExecute = nameof(HasVersions))]
    Task SuggestKeepNewest() => SuggestAsync(VersionCleanup.KeepNewest, 5, "Keep only the newest 5 versions of each file?", "every file keeps its newest 5 old versions");

    [RelayCommand(CanExecute = nameof(HasVersions))]
    Task SuggestThinOut() => SuggestAsync(VersionCleanup.ThinOut, 0, "Thin out old versions?",
        "everything from the last 30 days is kept, then one version a week for a year, then one a month");

    async Task SuggestAsync(VersionCleanup rule, int value, string title, string keeps)
    {
        IsBusy = true;
        try
        {
            string place;
            using (var session = await page.OpenSessionAsync())
            {
                _suggested = await Task.Run(() => session.SuggestVersions(rule, value));
                place = session.RemovedPlace;
            }
            ConfirmTitle = title;
            ConfirmHasAction = _suggested.Count > 0;
            ConfirmMessage = _suggested.Count == 0
                ? "Nothing matches: no old versions would be removed by this."
                : $"{_suggested.Count:N0} old {(_suggested.Count == 1 ? "version goes" : "versions go")} to {place}, {Formatting.Size(_suggested.Sum(v => v.Size))} in all. "
                  + $"Afterwards {keeps}. The current copy of every file is untouched, and nothing is erased.";
            IsConfirmOpen = true;
        }
        catch (Exception e) when (e is DriveSyncException or IOException or SqliteException)
        {
            ResultIsWarning = true;
            ResultMessage = e.Message;
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    async Task ConfirmYes()
    {
        IsConfirmOpen = false;
        var versions = _suggested;
        _suggested = [];
        if (versions.Count == 0)
            return;
        IsBusy = true;
        ResultProblems.Clear();
        try
        {
            DecisionResult result;
            string place;
            using (var session = await page.OpenSessionAsync())
            {
                result = await Task.Run(() => session.RemoveVersions(versions));
                place = session.RemovedPlace;
            }
            string done = $"Sent {result.Applied:N0} old {(result.Applied == 1 ? "version" : "versions")} to {place}.";
            if (result.SentToTrashFolder > 0)
                done = $"The Recycle Bin had no room, so {result.SentToTrashFolder:N0} of {result.Applied:N0} old versions went to the backup's trash folder instead.";
            ResultIsWarning = result.Problems.Count > 0;
            ResultMessage = result.Problems.Count == 0 ? done : $"{done} {result.Problems.Count:N0} couldn't be removed:";
            foreach (var problem in result.Problems.Take(20))
                ResultProblems.Add($"{problem.Key}: {problem.Message}");
        }
        catch (Exception e) when (e is DriveSyncException or IOException or SqliteException)
        {
            ResultIsWarning = true;
            ResultMessage = e.Message;
        }
        finally
        {
            IsBusy = false;
        }
        await LoadAsync();
    }

    [RelayCommand]
    void ConfirmNo()
    {
        IsConfirmOpen = false;
        _suggested = [];
    }

    [RelayCommand]
    void DismissResult() => ResultMessage = null;

    // Removed files

    internal void UpdateSelection() => SelectedCount = Removed.Count(r => r.IsSelected);

    bool CanRestore() => SelectedCount > 0;

    [RelayCommand(CanExecute = nameof(CanRestore))]
    void RestoreSelected() => Restore([.. Removed.Where(r => r.IsSelected)]);

    internal void Restore(IReadOnlyList<RemovedRowViewModel> rows)
    {
        if (rows.Count == 0)
            return;
        string title = rows.Count == 1 ? $"Restore {rows[0].Name}" : $"Restore {rows.Count:N0} removed files";
        page.Restore.Open(new RestoreRequest([.. rows.Select(r => r.Key)]) { Source = RestoreSource.Deleted }, title,
            canUseOriginal: rows.All(r => page.CanRestoreInPlace(r.Key)));
    }

    [RelayCommand]
    void OpenTrashFolder()
    {
        if (page.Profile.LastKnownRoot is { } root)
        {
            string trash = Path.Combine(root, ".drivesync", "trash");
            OpenInExplorer?.Invoke(Directory.Exists(trash) ? trash : Path.Combine(root, ".drivesync"));
        }
    }

    [RelayCommand]
    void OpenRecycleBin() => OpenInExplorer?.Invoke("shell:RecycleBinFolder");

    [RelayCommand]
    void OpenVersionsFolder()
    {
        if (page.Profile.LastKnownRoot is { } root)
            OpenInExplorer?.Invoke(Path.Combine(root, ".drivesync", "versions"));
    }
}

public sealed partial class RemovedRowViewModel(StorageViewModel owner, DeletedEntry entry) : ObservableObject
{
    public string Key { get; } = entry.Key;

    public string Name { get; } = Path.GetFileName(entry.Key);

    public string Detail { get; } = $"{Path.GetDirectoryName(entry.Key)} · removed {Format.Date(entry.DeletedUtc)}";

    public string SizeText { get; } = Formatting.Size(entry.Size);

    public string Where { get; } = entry.InRecycleBin ? "Recycle Bin" : "Trash folder";

    [ObservableProperty]
    public partial bool IsSelected { get; set; }

    partial void OnIsSelectedChanged(bool value) => owner.UpdateSelection();

    [RelayCommand]
    void Restore() => owner.Restore([this]);
}
