using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DriveSync.App.Services;
using DriveSync.Core;
using DriveSync.Core.Engine;
using DriveSync.Core.Model;

namespace DriveSync.App.ViewModels;

/// <summary>
/// "Missing files": backed-up files that left the PC without going through DriveSync.
/// The user says why each one is gone: deleted on purpose, removed to free space, or decide later.
/// </summary>
public sealed partial class MissingFilesViewModel(MainViewModel main) : ObservableObject
{
    readonly List<MissingGroupViewModel> _all = [];
    List<MissingFileViewModel>? _awaitingConfirmation;

    public ObservableCollection<MissingGroupViewModel> Groups { get; } = [];

    public ObservableCollection<string> ResultProblems { get; } = [];

    [ObservableProperty]
    public partial bool IsLoading { get; set; }

    [ObservableProperty]
    public partial bool IsBusy { get; set; }

    [ObservableProperty]
    public partial string? LoadError { get; set; }

    [ObservableProperty]
    public partial string Headline { get; set; } = "";

    [ObservableProperty]
    public partial int TotalCount { get; set; }

    [ObservableProperty]
    public partial string SearchText { get; set; } = "";

    [ObservableProperty]
    public partial int SelectedCount { get; set; }

    [ObservableProperty]
    public partial string? ResultMessage { get; set; }

    [ObservableProperty]
    public partial bool ResultIsWarning { get; set; }

    [ObservableProperty]
    public partial bool IsConfirmOpen { get; set; }

    [ObservableProperty]
    public partial string ConfirmTitle { get; set; } = "";

    [ObservableProperty]
    public partial string ConfirmMessage { get; set; } = "";

    public bool ShowEmptyState => !IsLoading && LoadError is null && TotalCount == 0;

    partial void OnIsLoadingChanged(bool value) => OnPropertyChanged(nameof(ShowEmptyState));

    partial void OnLoadErrorChanged(string? value) => OnPropertyChanged(nameof(ShowEmptyState));

    partial void OnTotalCountChanged(int value)
    {
        OnPropertyChanged(nameof(ShowEmptyState));
        Headline = value switch
        {
            0 => "Nothing is waiting for a decision.",
            1 => "1 backed-up file is no longer on this PC. It's still on the backup.",
            _ => $"{value:N0} backed-up files are no longer on this PC. They're still on the backup.",
        };
    }

    partial void OnSearchTextChanged(string value) => ApplyFilter();

    public async Task LoadAsync()
    {
        IsLoading = true;
        LoadError = null;
        ResultMessage = null;
        ResultProblems.Clear();
        try
        {
            IReadOnlyList<MissingFile> pending;
            using (var session = await main.Services.OpenSessionAsync())
                pending = await Task.Run(session.GetPendingFiles);
            _all.Clear();
            foreach (var group in pending.GroupBy(m => GroupKey(m.Record.Key), KeyPath.Comparer).OrderBy(g => g.Key, StringComparer.OrdinalIgnoreCase))
                _all.Add(new MissingGroupViewModel(this, group.Key, group.OrderBy(m => m.Record.Key, StringComparer.OrdinalIgnoreCase)));
            UpdateTotals();
            ApplyFilter();
        }
        catch (DriveSyncException e)
        {
            LoadError = e.Message;
            _all.Clear();
            Groups.Clear();
        }
        finally
        {
            IsLoading = false;
        }
    }

    /// <summary>Groups files by their first two folders, e.g. <c>Desktop\Old Projects</c>.</summary>
    internal static string GroupKey(string key)
    {
        string[] parts = key.Split('\\');
        int depth = Math.Clamp(parts.Length - 1, 1, 2);
        return string.Join('\\', parts.Take(depth));
    }

    void ApplyFilter()
    {
        string text = SearchText.Trim();
        Groups.Clear();
        foreach (var group in _all)
        {
            if (group.ApplyFilter(text))
                Groups.Add(group);
        }
    }

    void UpdateTotals()
    {
        TotalCount = _all.Sum(g => g.Files.Count);
        UpdateSelection();
    }

    internal void UpdateSelection() => SelectedCount = _all.Sum(g => g.Files.Count(f => f.IsSelected));

    IReadOnlyList<MissingFileViewModel> Selected() => [.. _all.SelectMany(g => g.Files).Where(f => f.IsSelected)];

    /// <summary>Applies a decision to some files. "Deleted on purpose" asks for confirmation first.</summary>
    internal void Decide(IReadOnlyList<MissingFileViewModel> files, MissingDecision decision)
    {
        if (files.Count == 0 || IsBusy)
            return;
        switch (decision)
        {
            case MissingDecision.DeletedOnPurpose:
                _awaitingConfirmation = [.. files];
                long bytes = files.Sum(f => f.Size);
                ConfirmTitle = files.Count == 1 ? $"Remove {files[0].Name} from the backup?" : $"Remove {files.Count:N0} files from the backup?";
                ConfirmMessage = $"The backup {(files.Count == 1 ? "copy goes" : "copies go")} to the Recycle Bin on the backup drive ({Formatting.Size(bytes)}). "
                    + "Nothing is erased: you can restore from there until you empty that Recycle Bin.";
                IsConfirmOpen = true;
                break;
            case MissingDecision.KeepOnBackup:
                _ = RunDecisionAsync(files, decision);
                break;
            default:
                Hide(files);
                ResultIsWarning = false;
                ResultMessage = files.Count == 1
                    ? $"Left {files[0].Name} for later. It stays on the backup and will be here next time."
                    : $"Left {files.Count:N0} files for later. They stay on the backup and will be here next time.";
                ResultProblems.Clear();
                break;
        }
    }

    [RelayCommand]
    Task ConfirmDelete()
    {
        IsConfirmOpen = false;
        var files = _awaitingConfirmation ?? [];
        _awaitingConfirmation = null;
        return RunDecisionAsync(files, MissingDecision.DeletedOnPurpose);
    }

    [RelayCommand]
    void CancelConfirm()
    {
        IsConfirmOpen = false;
        _awaitingConfirmation = null;
    }

    [RelayCommand]
    void DeleteSelected() => Decide(Selected(), MissingDecision.DeletedOnPurpose);

    [RelayCommand]
    void KeepSelected() => Decide(Selected(), MissingDecision.KeepOnBackup);

    [RelayCommand]
    void LaterSelected() => Decide(Selected(), MissingDecision.DecideLater);

    [RelayCommand]
    void ClearSelection()
    {
        foreach (var file in _all.SelectMany(g => g.Files))
            file.IsSelected = false;
    }

    [RelayCommand]
    void DismissResult() => ResultMessage = null;

    async Task RunDecisionAsync(IReadOnlyList<MissingFileViewModel> files, MissingDecision decision)
    {
        IsBusy = true;
        ResultProblems.Clear();
        var keys = files.Select(f => f.Key).ToList();
        try
        {
            DecisionResult result;
            using (var session = await main.Services.OpenSessionAsync())
                result = await Task.Run(() => session.ResolveMissing(keys, decision));
            var failed = result.Problems.Select(p => p.Key).ToHashSet(KeyPath.Comparer);
            Hide(files.Where(f => !failed.Contains(f.Key)).ToList());
            main.PendingCount = Math.Max(0, main.PendingCount - result.Applied);

            ResultIsWarning = result.Problems.Count > 0;
            string done = decision == MissingDecision.DeletedOnPurpose
                ? $"Sent {Formatting.Files(result.Applied)} to the backup drive's Recycle Bin."
                : $"Kept {Formatting.Files(result.Applied)} on the backup. {(result.Applied == 1 ? "It's" : "They're")} now marked as offloaded.";
            ResultMessage = result.Problems.Count == 0 ? done : $"{done} {Formatting.Files(result.Problems.Count)} couldn't be changed:";
            foreach (var problem in result.Problems.Take(20))
                ResultProblems.Add($"{problem.Key}: {problem.Message}");
        }
        catch (DriveSyncException e)
        {
            ResultIsWarning = true;
            ResultMessage = e.Message;
        }
        finally
        {
            IsBusy = false;
        }
    }

    void Hide(IReadOnlyList<MissingFileViewModel> files)
    {
        var gone = files.ToHashSet();
        foreach (var group in _all.ToList())
        {
            group.Remove(gone);
            if (group.Files.Count == 0)
            {
                _all.Remove(group);
                Groups.Remove(group);
            }
        }
        UpdateTotals();
    }
}

public sealed partial class MissingGroupViewModel : ObservableObject
{
    readonly MissingFilesViewModel _owner;

    public MissingGroupViewModel(MissingFilesViewModel owner, string folder, IEnumerable<MissingFile> files)
    {
        _owner = owner;
        Folder = folder;
        foreach (var file in files)
            Files.Add(new MissingFileViewModel(owner, file));
        Refresh();
    }

    public string Folder { get; }

    public ObservableCollection<MissingFileViewModel> Files { get; } = [];

    public ObservableCollection<MissingFileViewModel> VisibleFiles { get; } = [];

    [ObservableProperty]
    public partial string Summary { get; set; } = "";

    [ObservableProperty]
    public partial int InRecycleBinCount { get; set; }

    [ObservableProperty]
    public partial string RecycleText { get; set; } = "";

    [ObservableProperty]
    public partial bool IsExpanded { get; set; }

    [RelayCommand]
    void DeleteAll() => _owner.Decide([.. VisibleFiles], MissingDecision.DeletedOnPurpose);

    [RelayCommand]
    void KeepAll() => _owner.Decide([.. VisibleFiles], MissingDecision.KeepOnBackup);

    [RelayCommand]
    void LaterAll() => _owner.Decide([.. VisibleFiles], MissingDecision.DecideLater);

    [RelayCommand]
    void ToggleExpanded() => IsExpanded = !IsExpanded;

    /// <summary>Shows only matching files; returns false when nothing in the group matches.</summary>
    internal bool ApplyFilter(string text)
    {
        VisibleFiles.Clear();
        bool folderMatches = text.Length == 0 || Folder.Contains(text, StringComparison.OrdinalIgnoreCase);
        foreach (var file in Files)
        {
            if (folderMatches || file.Key.Contains(text, StringComparison.OrdinalIgnoreCase))
                VisibleFiles.Add(file);
        }
        if (text.Length > 0 && !folderMatches && VisibleFiles.Count > 0)
            IsExpanded = true;
        Refresh();
        return VisibleFiles.Count > 0;
    }

    internal void Remove(HashSet<MissingFileViewModel> gone)
    {
        foreach (var file in Files.Where(gone.Contains).ToList())
        {
            Files.Remove(file);
            VisibleFiles.Remove(file);
        }
        Refresh();
    }

    void Refresh()
    {
        var shown = VisibleFiles.Count > 0 ? VisibleFiles : Files;
        Summary = $"{Formatting.Files(shown.Count)} · {Formatting.Size(shown.Sum(f => f.Size))}";
        InRecycleBinCount = shown.Count(f => f.InRecycleBin);
        RecycleText = InRecycleBinCount == shown.Count && shown.Count > 1 ? "All in the Recycle Bin" : $"{InRecycleBinCount:N0} in the Recycle Bin";
    }
}

public sealed partial class MissingFileViewModel(MissingFilesViewModel owner, MissingFile file) : ObservableObject
{
    public string Key { get; } = file.Record.Key;

    public string Name { get; } = Path.GetFileName(file.Record.Key);

    public string Location { get; } = Path.GetDirectoryName(file.Record.Key) ?? "";

    public long Size { get; } = file.Record.Size;

    public string SizeText { get; } = Formatting.Size(file.Record.Size);

    public bool InRecycleBin { get; } = file.InRecycleBin;

    public string MissingSince { get; } = "Missing since " + Format.Date(file.Record.StateChangedUtc);

    [ObservableProperty]
    public partial bool IsSelected { get; set; }

    partial void OnIsSelectedChanged(bool value) => owner.UpdateSelection();

    [RelayCommand]
    void Delete() => owner.Decide([this], MissingDecision.DeletedOnPurpose);

    [RelayCommand]
    void Keep() => owner.Decide([this], MissingDecision.KeepOnBackup);

    [RelayCommand]
    void Later() => owner.Decide([this], MissingDecision.DecideLater);
}
