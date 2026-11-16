using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DriveSync.App.Services;
using DriveSync.Core;
using DriveSync.Core.Engine;
using DriveSync.Core.Model;
using DriveSync.Core.Storage;
using Microsoft.Data.Sqlite;

namespace DriveSync.App.ViewModels;

public enum FileFilter
{
    All,
    Offloaded,
    Missing,
}

public sealed record Crumb(string Title, string Key);

/// <summary>
/// "Files": everything on the backup, browsed like folders. From here files are restored, offloaded to free space,
/// or deleted everywhere, and a file's older versions are brought back.
/// </summary>
public sealed partial class FilesViewModel(BackupPageViewModel page) : ObservableObject
{
    Func<Task>? _confirmAction;
    int _loadVersion;

    /// <summary>Shows a file or folder in File Explorer. Set by the view.</summary>
    public Action<string>? ShowInExplorer { get; set; }

    public ObservableCollection<FileRowViewModel> Rows { get; } = [];

    public ObservableCollection<Crumb> Crumbs { get; } = [];

    public ObservableCollection<VersionRowViewModel> Versions { get; } = [];

    public ObservableCollection<string> ResultProblems { get; } = [];

    [ObservableProperty]
    public partial string FolderKey { get; set; } = "";

    [ObservableProperty]
    public partial FileFilter Filter { get; set; }

    [ObservableProperty]
    public partial string SearchText { get; set; } = "";

    [ObservableProperty]
    public partial bool IsLoading { get; set; }

    [ObservableProperty]
    public partial bool IsBusy { get; set; }

    [ObservableProperty]
    public partial string BusyText { get; set; } = "";

    [ObservableProperty]
    public partial string? LoadError { get; set; }

    /// <summary>False while browsing the PC's copy of the records because the drive is unplugged.</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(RestoreSelectedCommand), nameof(OffloadSelectedCommand), nameof(DeleteSelectedCommand))]
    public partial bool IsDriveConnected { get; set; } = true;

    [ObservableProperty]
    public partial string EmptyText { get; set; } = "";

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(RestoreSelectedCommand), nameof(OffloadSelectedCommand), nameof(DeleteSelectedCommand))]
    public partial int SelectedCount { get; set; }

    [ObservableProperty]
    public partial string? ResultMessage { get; set; }

    [ObservableProperty]
    public partial bool ResultIsWarning { get; set; }

    // Confirmation

    [ObservableProperty]
    public partial bool IsConfirmOpen { get; set; }

    [ObservableProperty]
    public partial string ConfirmTitle { get; set; } = "";

    [ObservableProperty]
    public partial string ConfirmMessage { get; set; } = "";

    [ObservableProperty]
    public partial string ConfirmButton { get; set; } = "";

    [ObservableProperty]
    public partial bool ConfirmIsDanger { get; set; }

    // Version history of one file

    [ObservableProperty]
    public partial bool IsHistoryOpen { get; set; }

    [ObservableProperty]
    public partial string HistoryTitle { get; set; } = "";

    [ObservableProperty]
    public partial string HistoryNote { get; set; } = "";

    public bool IsAll => Filter == FileFilter.All;

    public bool IsOffloadedOnly => Filter == FileFilter.Offloaded;

    public bool IsMissingOnly => Filter == FileFilter.Missing;

    partial void OnFilterChanged(FileFilter value)
    {
        OnPropertyChanged(nameof(IsAll));
        OnPropertyChanged(nameof(IsOffloadedOnly));
        OnPropertyChanged(nameof(IsMissingOnly));
        _ = LoadAsync();
    }

    partial void OnSearchTextChanged(string value) => _ = LoadAsync();

    [RelayCommand]
    void ShowAll() => Filter = FileFilter.All;

    [RelayCommand]
    void ShowOffloaded() => Filter = FileFilter.Offloaded;

    [RelayCommand]
    void ShowMissing() => Filter = FileFilter.Missing;

    [RelayCommand]
    public Task OpenFolder(string key)
    {
        FolderKey = key;
        if (SearchText.Length > 0)
        {
            SearchText = ""; // reloads
            return Task.CompletedTask;
        }
        return LoadAsync();
    }

    /// <summary>
    /// Lists the current folder. Reads the drive's records when it's plugged in, else the copy kept on this PC,
    /// so the backup can be looked through (but not restored from) without the drive.
    /// </summary>
    public async Task LoadAsync()
    {
        int version = ++_loadVersion;
        IsLoading = true;
        LoadError = null;
        string folder = FolderKey, search = SearchText.Trim();
        FileState? state = Filter switch
        {
            FileFilter.Offloaded => FileState.Archived,
            FileFilter.Missing => FileState.Pending,
            _ => null,
        };
        try
        {
            var (connected, rows) = await Task.Run(() =>
            {
                var location = BackupLocation.Find(page.Profile);
                string path = location?.CatalogPath ?? page.MirrorCatalogPath;
                if (!File.Exists(path))
                    return (location is not null, new List<FileRowViewModel>());
                using var catalog = Catalog.OpenReadOnly(path);
                var list = search.Length >= 2
                    ? catalog.Search(search, state).Select(r => new FileRowViewModel(this, r)).ToList()
                    : catalog.ListFolder(folder, state).Select(e => new FileRowViewModel(this, e)).ToList();
                return (location is not null, list);
            });
            if (version != _loadVersion)
                return;
            IsDriveConnected = connected;
            Rows.Clear();
            foreach (var row in rows)
                Rows.Add(row);
            Crumbs.Clear();
            Crumbs.Add(new Crumb(page.Name, ""));
            string walked = "";
            foreach (string part in folder.Split('\\', StringSplitOptions.RemoveEmptyEntries))
            {
                walked = walked.Length == 0 ? part : walked + "\\" + part;
                Crumbs.Add(new Crumb(part, walked));
            }
            EmptyText = search.Length >= 2 ? $"Nothing on this backup matches \"{search}\"."
                : Filter == FileFilter.Offloaded ? "Nothing here is offloaded."
                : Filter == FileFilter.Missing ? "Nothing here is missing from this PC."
                : "Nothing is backed up here yet. Run a sync first.";
            UpdateSelection();
        }
        catch (Exception e) when (e is SqliteException or IOException or UnauthorizedAccessException)
        {
            if (version == _loadVersion)
            {
                Rows.Clear();
                LoadError = $"Couldn't read the backup's records: {e.Message}";
            }
        }
        finally
        {
            if (version == _loadVersion)
                IsLoading = false;
        }
    }

    internal void UpdateSelection() => SelectedCount = Rows.Count(r => r.IsSelected);

    IReadOnlyList<FileRowViewModel> Selected() => [.. Rows.Where(r => r.IsSelected)];

    bool CanAct() => SelectedCount > 0 && IsDriveConnected;

    [RelayCommand]
    void ClearSelection()
    {
        foreach (var row in Rows)
            row.IsSelected = false;
    }

    [RelayCommand]
    void DismissResult() => ResultMessage = null;

    // Restore

    [RelayCommand(CanExecute = nameof(CanAct))]
    void RestoreSelected() => Restore(Selected());

    internal void Restore(IReadOnlyList<FileRowViewModel> rows)
    {
        if (rows.Count == 0)
            return;
        string title = rows.Count == 1 ? $"Restore {rows[0].Name}" : $"Restore {rows.Count:N0} items";
        page.Restore.Open(new RestoreRequest([.. rows.Select(r => r.Key)]), title, canUseOriginal: rows.All(r => page.CanRestoreInPlace(r.Key)));
    }

    // Offload and delete

    [RelayCommand(CanExecute = nameof(CanAct))]
    void OffloadSelected()
    {
        var rows = Selected();
        Confirm(rows.Count == 1 ? $"Free up space used by {rows[0].Name}?" : $"Free up space used by {rows.Count:N0} items?",
            "DriveSync checks that every file is safely on the backup, comparing checksums, then sends the copies on this PC to this PC's Recycle Bin. "
            + "The files stay on the backup, marked as offloaded. The space comes back when you empty the Recycle Bin.",
            "Free up space", danger: false,
            () => RunAsync("Checking backup copies…", async session =>
            {
                var result = await session.OffloadAsync([.. rows.Select(r => r.Key)]);
                string done = $"Offloaded {Formatting.Files(result.Offloaded)}: {Formatting.Size(result.BytesMovedToRecycleBin)} moved to this PC's Recycle Bin. Empty it to get the space back.";
                if (result.Offloaded == 0)
                    done = "Nothing was offloaded. Files that are already only on the backup have nothing to free on this PC.";
                return (done, result.Problems);
            }));
    }

    [RelayCommand(CanExecute = nameof(CanAct))]
    void DeleteSelected()
    {
        var rows = Selected();
        Confirm(rows.Count == 1 ? $"Delete {rows[0].Name} everywhere?" : $"Delete {rows.Count:N0} items everywhere?",
            $"The copies on this PC go to this PC's Recycle Bin, and the backup copies go to {page.RemovedPlace}. "
            + "Nothing is erased: both can be brought back until you empty them.",
            "Delete everywhere", danger: true,
            () => RunAsync("Removing…", session =>
            {
                var result = session.DeleteEverywhere([.. rows.Select(r => r.Key)]);
                string done = $"Deleted {Formatting.Files(result.Deleted)} from this PC and the backup.";
                if (result.SentToTrashFolder > 0)
                    done += $" The Recycle Bin had no room, so {Formatting.Files(result.SentToTrashFolder)} went to the backup's trash folder instead.";
                return Task.FromResult((done, result.Problems));
            }));
    }

    async Task RunAsync(string busy, Func<DriveSyncSession, Task<(string Message, List<Problem> Problems)>> work)
    {
        IsBusy = true;
        BusyText = busy;
        ResultProblems.Clear();
        try
        {
            string message;
            List<Problem> problems;
            using (var session = await page.OpenSessionAsync())
                (message, problems) = await Task.Run(() => work(session));
            ResultIsWarning = problems.Count > 0;
            ResultMessage = problems.Count == 0 ? message : $"{message} {Formatting.Files(problems.Count)} had a problem:";
            foreach (var problem in problems.Take(20))
                ResultProblems.Add($"{problem.Key}: {problem.Message}");
        }
        catch (Exception e) when (e is DriveSyncException or IOException or UnauthorizedAccessException or SqliteException)
        {
            ResultIsWarning = true;
            ResultMessage = e is DriveSyncException ? e.Message : $"Nothing was changed: {e.Message}";
        }
        finally
        {
            IsBusy = false;
        }
        await LoadAsync();
        _ = page.Dashboard.RefreshAsync();
    }

    void Confirm(string title, string message, string button, bool danger, Func<Task> action)
    {
        ConfirmTitle = title;
        ConfirmMessage = message;
        ConfirmButton = button;
        ConfirmIsDanger = danger;
        _confirmAction = action;
        IsConfirmOpen = true;
    }

    [RelayCommand]
    async Task ConfirmYes()
    {
        IsConfirmOpen = false;
        var action = _confirmAction;
        _confirmAction = null;
        if (action is not null)
            await action();
    }

    [RelayCommand]
    void ConfirmNo()
    {
        IsConfirmOpen = false;
        _confirmAction = null;
    }

    // Version history

    internal async Task ShowHistoryAsync(FileRowViewModel row)
    {
        Versions.Clear();
        HistoryTitle = row.Name;
        HistoryNote = "Looking for older versions…";
        IsHistoryOpen = true;
        try
        {
            IReadOnlyList<VersionEntry> versions;
            using (var session = await page.OpenSessionAsync())
                versions = await Task.Run(() => session.ListVersions(row.Key));
            foreach (var version in versions)
                Versions.Add(new VersionRowViewModel(this, version));
            HistoryNote = versions.Count == 0
                ? "This file has no older versions. One is kept each time the file changes between syncs."
                : $"{versions.Count:N0} older {(versions.Count == 1 ? "version" : "versions")}, kept from before the file changed.";
        }
        catch (Exception e) when (e is DriveSyncException or IOException or SqliteException)
        {
            HistoryNote = e is DriveSyncException ? e.Message : $"Couldn't read the versions: {e.Message}";
        }
    }

    internal void RestoreVersion(VersionRowViewModel version, bool asCopy)
    {
        IsHistoryOpen = false;
        var request = new RestoreRequest([version.Key]) { Source = RestoreSource.OldVersion, VersionStamp = version.Stamp };
        page.Restore.Open(request, $"Restore {Path.GetFileName(version.Key)} from {version.When}", canUseOriginal: !asCopy && page.CanRestoreInPlace(version.Key));
    }

    [RelayCommand]
    void CloseHistory() => IsHistoryOpen = false;

    internal void Show(FileRowViewModel row)
    {
        if (page.Profile.LastKnownRoot is { } root)
            ShowInExplorer?.Invoke(Path.Combine(root, row.Key));
    }
}

/// <summary>A folder or file in the backup browser.</summary>
public sealed partial class FileRowViewModel : ObservableObject
{
    readonly FilesViewModel _owner;

    public FileRowViewModel(FilesViewModel owner, BrowseEntry entry)
    {
        _owner = owner;
        Name = entry.Name;
        Key = entry.Key;
        IsFolder = entry.IsFolder;
        SizeText = Formatting.Size(entry.Bytes);
        Modified = entry.IsFolder ? "" : Format.Date(new DateTime(entry.ModifiedUtcTicks, DateTimeKind.Utc));
        if (entry.IsFolder)
        {
            var parts = new List<string> { Formatting.Files(entry.Files) };
            if (entry.Offloaded > 0) parts.Add($"{entry.Offloaded:N0} offloaded");
            if (entry.Missing > 0) parts.Add($"{entry.Missing:N0} missing from this PC");
            Detail = string.Join(" · ", parts);
            State = entry.Missing > 0 ? FileState.Pending : entry.Offloaded == entry.Files ? FileState.Archived : FileState.Active;
            StatusText = entry.Missing > 0 ? "Has missing files" : entry.Offloaded == entry.Files ? "Offloaded" : entry.Offloaded > 0 ? "Partly offloaded" : "On this PC";
        }
        else
        {
            State = entry.State ?? FileState.Active;
            StatusText = StatusFor(State);
        }
    }

    public FileRowViewModel(FilesViewModel owner, FileRecord record)
    {
        _owner = owner;
        Name = Path.GetFileName(record.Key);
        Key = record.Key;
        Detail = Path.GetDirectoryName(record.Key) ?? "";
        SizeText = Formatting.Size(record.Size);
        Modified = Format.Date(new DateTime(record.ModifiedUtcTicks, DateTimeKind.Utc));
        State = record.State;
        StatusText = StatusFor(record.State);
    }

    static string StatusFor(FileState state) => state switch
    {
        FileState.Archived => "Offloaded",
        FileState.Pending => "Missing from this PC",
        _ => "On this PC",
    };

    public string Name { get; }

    public string Key { get; }

    public bool IsFolder { get; }

    public bool IsFile => !IsFolder;

    /// <summary>For a folder, what's inside; for a search result, where the file is.</summary>
    public string Detail { get; } = "";

    public string SizeText { get; }

    public string Modified { get; }

    public FileState State { get; }

    public string StatusText { get; }

    [ObservableProperty]
    public partial bool IsSelected { get; set; }

    partial void OnIsSelectedChanged(bool value) => _owner.UpdateSelection();

    /// <summary>Opens a folder, or a file's version history.</summary>
    [RelayCommand]
    Task Open() => IsFolder ? _owner.OpenFolder(Key) : _owner.ShowHistoryAsync(this);

    [RelayCommand]
    void Restore() => _owner.Restore([this]);

    [RelayCommand]
    void Show() => _owner.Show(this);
}

public sealed partial class VersionRowViewModel(FilesViewModel owner, VersionEntry version)
{
    public string Key { get; } = version.Key;

    public string Stamp { get; } = version.Stamp;

    /// <summary>When this version was replaced by a newer one.</summary>
    public string When { get; } = DriveSyncSession.StampTime(version.Stamp) is { } t ? t.ToString("d MMM yyyy, HH:mm") : version.Stamp;

    public string Detail { get; } = $"{Formatting.Size(version.Size)} · last changed {Format.Date(version.ModifiedUtc)}";

    [RelayCommand]
    void Restore() => owner.RestoreVersion(this, asCopy: false);

    [RelayCommand]
    void RestoreAsCopy() => owner.RestoreVersion(this, asCopy: true);
}
