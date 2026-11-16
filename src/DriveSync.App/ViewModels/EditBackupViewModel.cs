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

/// <summary>
/// "Edit backup": its name, folders (and folders removed earlier), destination and missing-files rule.
/// Folder changes, moves and removing the backup happen through confirmations and apply straight away;
/// the name and rule apply on Save. Nothing here ever deletes a file.
/// </summary>
public sealed partial class EditBackupViewModel : ObservableObject
{
    readonly BackupPageViewModel _page;
    Dictionary<string, (int Files, long Bytes)>? _totals;
    FolderRowViewModel? _removing;
    Func<Task>? _confirmAction;
    CancellationTokenSource? _moveCancel;

    public EditBackupViewModel(BackupPageViewModel page)
    {
        _page = page;
        Name = page.Profile.Name;
        UseTrashFolder = page.Profile.RemovedFilesGoTo == RemovedFilesGoTo.TrashFolder;
        foreach (var choice in SetupWizardViewModel.PolicyChoices())
        {
            choice.IsSelected = choice.Policy == page.Profile.MissingFilePolicy;
            Policies.Add(choice);
        }
        Rebuild();
    }

    BackupProfile Profile => _page.Profile;

    /// <summary>The backup's saved name, for the link back to its page.</summary>
    public string PageName => _page.Name;

    IReadOnlyList<BackupProfile> AllBackups => _page.Main.Services.Backups;

    AppServices Services => _page.Main.Services;

    /// <summary>Opens a folder picker. Set by the view so this class stays free of UI code.</summary>
    public Func<string?>? PickFolder { get; set; }

    public ObservableCollection<FolderRowViewModel> Folders { get; } = [];

    public ObservableCollection<FolderRowViewModel> RemovedFolders { get; } = [];

    public ObservableCollection<PolicyChoiceViewModel> Policies { get; } = [];

    public ObservableCollection<MoveTargetViewModel> MoveTargets { get; } = [];

    [ObservableProperty]
    public partial string Name { get; set; }

    /// <summary>Removed copies go to the backup's trash folder (true) or the drive's Recycle Bin (false). Applied on Save.</summary>
    [ObservableProperty]
    public partial bool UseTrashFolder { get; set; }

    [ObservableProperty]
    public partial string DestinationPath { get; set; } = "";

    [ObservableProperty]
    public partial string DestinationDetail { get; set; } = "";

    [ObservableProperty]
    public partial string? Error { get; set; }

    [ObservableProperty]
    public partial string? Message { get; set; }

    [ObservableProperty]
    public partial bool MessageIsWarning { get; set; }

    [ObservableProperty]
    public partial bool IsBusy { get; set; }

    // "Stop backing up a folder"

    [ObservableProperty]
    public partial bool IsRemoveFolderOpen { get; set; }

    [ObservableProperty]
    public partial string RemoveFolderTitle { get; set; } = "";

    [ObservableProperty]
    public partial string RemoveFolderMessage { get; set; } = "";

    [ObservableProperty]
    public partial string RecycleOptionDetail { get; set; } = "";

    [ObservableProperty]
    public partial string RemoveFolderNote { get; set; } = "";

    [ObservableProperty]
    public partial bool KeepRemovedFiles { get; set; } = true;

    // A plain yes/no confirmation

    [ObservableProperty]
    public partial bool IsConfirmOpen { get; set; }

    [ObservableProperty]
    public partial string ConfirmTitle { get; set; } = "";

    [ObservableProperty]
    public partial string ConfirmMessage { get; set; } = "";

    [ObservableProperty]
    public partial string ConfirmButton { get; set; } = "";

    // "Move to another drive"

    [ObservableProperty]
    public partial bool IsMoveOpen { get; set; }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(StartMoveCommand))]
    public partial bool IsMoving { get; set; }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(StartMoveCommand))]
    public partial MoveTargetViewModel? SelectedMoveTarget { get; set; }

    [ObservableProperty]
    public partial string MoveSummary { get; set; } = "";

    [ObservableProperty]
    public partial string MovePhase { get; set; } = "";

    [ObservableProperty]
    public partial double MovePercent { get; set; }

    [ObservableProperty]
    public partial string MoveDetail { get; set; } = "";

    [ObservableProperty]
    public partial string? MoveError { get; set; }

    long BackupBytes => _totals?.Values.Sum(t => t.Bytes) ?? 0;

    /// <summary>Reads how much of each folder is on the backup. Without the drive, sizes are left out.</summary>
    public async Task LoadAsync()
    {
        try
        {
            using var session = await _page.OpenSessionAsync();
            _totals = await Task.Run(session.FolderTotals);
        }
        catch (Exception e) when (e is DriveSyncException or SqliteException or IOException)
        {
            _totals = null;
        }
        Rebuild();
    }

    void Rebuild()
    {
        Folders.Clear();
        foreach (var source in Profile.Sources)
        {
            var also = AllBackups.Where(b => b.Id != Profile.Id && b.Sources.Any(s => s.Path.Equals(source.Path, StringComparison.OrdinalIgnoreCase))).Select(b => b.Name).ToList();
            string detail = source.Path + SizeOnBackup(source.Name) + (also.Count > 0 ? " · also in " + string.Join(", ", also) : "");
            Folders.Add(new FolderRowViewModel(this, source.Name, source.Path, detail, isRemoved: false));
        }
        RemovedFolders.Clear();
        foreach (var removed in Profile.RemovedFolders)
        {
            string detail = $"No longer backed up. Its files{SizeOnBackup(removed.Name, prefix: " (", suffix: ")")} are still on the backup and can be restored.";
            RemovedFolders.Add(new FolderRowViewModel(this, removed.Name, removed.Path, detail, isRemoved: true) { RemovedOn = "removed " + Format.Date(removed.RemovedUtc) });
        }

        DestinationPath = Profile.LastKnownRoot ?? Profile.BackupFolderName;
        string label = BackupPageViewModel.DestinationLabel(Profile);
        DestinationDetail = _totals is null ? "Drive not connected" : $"{label} · {Formatting.Size(BackupBytes)} backed up";
    }

    string SizeOnBackup(string folder, string prefix = " · ", string suffix = "") =>
        _totals is not null && _totals.TryGetValue(folder, out var t) ? $"{prefix}{Formatting.Size(t.Bytes)}, {Formatting.Files(t.Files)}{suffix}" : "";

    bool EnsureNotSyncing()
    {
        if (!_page.Dashboard.IsSyncing)
            return true;
        Error = "This backup is syncing. Wait for it to finish, then try again.";
        return false;
    }

    void Saved(string message, bool warning = false)
    {
        Services.Save();
        _page.RefreshDescription();
        Rebuild();
        Error = null;
        Message = message;
        MessageIsWarning = warning;
    }

    [RelayCommand]
    void Back() => _page.ShowHome();

    [RelayCommand]
    void Save()
    {
        string name = Name.Trim();
        if (name.Length == 0)
        {
            Error = "Give this backup a name.";
            return;
        }
        if (AllBackups.Any(b => b.Id != Profile.Id && b.Name.Equals(name, StringComparison.OrdinalIgnoreCase)))
        {
            Error = $"You already have a backup called \"{name}\".";
            return;
        }
        Profile.Name = name;
        Profile.MissingFilePolicy = Policies.First(p => p.IsSelected).Policy;
        Profile.RemovedFilesGoTo = UseTrashFolder ? RemovedFilesGoTo.TrashFolder : RemovedFilesGoTo.RecycleBinThenTrashFolder;
        Services.Save();
        _page.RefreshDescription();
        _page.ShowHome();
    }

    [RelayCommand]
    void AddFolder()
    {
        if (!EnsureNotSyncing() || PickFolder?.Invoke() is not { Length: > 0 } path)
            return;
        try
        {
            var added = BackupEditor.AddFolder(Profile, path, AllBackups);
            Saved($"{added.Name} added. It's backed up on the next sync.");
        }
        catch (DriveSyncException e)
        {
            Error = e.Message;
        }
    }

    internal void AddBack(FolderRowViewModel row)
    {
        if (!EnsureNotSyncing())
            return;
        try
        {
            BackupEditor.AddFolder(Profile, row.Path, AllBackups);
            Saved($"{row.Name} is backed up again. Files already on the backup are reused; only changes are copied.");
        }
        catch (DriveSyncException e)
        {
            Error = e.Message;
        }
    }

    internal void AskRemoveFolder(FolderRowViewModel row)
    {
        if (!EnsureNotSyncing())
            return;
        if (Profile.Sources.Count == 1)
        {
            Error = "A backup needs at least one folder. To stop it completely, remove the backup instead.";
            return;
        }
        _removing = row;
        KeepRemovedFiles = true;
        RemoveFolderTitle = $"Stop backing up {row.Name} in {Profile.Name}?";
        string onBackup = _totals is not null && _totals.TryGetValue(row.Name, out var t)
            ? $"Its {Formatting.Files(t.Files)} already on the backup ({Formatting.Size(t.Bytes)})"
            : "Its files already on the backup";
        RemoveFolderMessage = $"New changes in {row.Name} won't be copied to this backup any more. {onBackup} can stay or go:";
        RecycleOptionDetail = $"They go to {_page.RemovedPlace}. Nothing is erased: you can restore them from the Storage tab until you delete them there yourself.";
        var others = AllBackups.Where(b => b.Id != Profile.Id && b.Sources.Any(s => s.Path.Equals(row.Path, StringComparison.OrdinalIgnoreCase))).Select(b => b.Name).ToList();
        RemoveFolderNote = $"Your {row.Name} folder on this PC isn't touched."
            + (others.Count > 0 ? $" {string.Join(", ", others)} keeps backing it up." : "");
        IsRemoveFolderOpen = true;
    }

    [RelayCommand]
    void CancelRemoveFolder()
    {
        IsRemoveFolderOpen = false;
        _removing = null;
    }

    [RelayCommand]
    async Task ConfirmRemoveFolderAsync()
    {
        IsRemoveFolderOpen = false;
        if (_removing is null)
            return;
        var row = _removing;
        _removing = null;
        try
        {
            BackupEditor.RemoveFolder(Profile, row.Name);
        }
        catch (DriveSyncException e)
        {
            Error = e.Message;
            return;
        }
        Saved($"{row.Name} is no longer backed up. Its files stay on the backup under Removed folders.");
        if (!KeepRemovedFiles)
            await RecycleRemovedAsync(row.Name);
    }

    internal void AskRecycleRemoved(FolderRowViewModel row)
    {
        string size = _totals is not null && _totals.TryGetValue(row.Name, out var t) ? $" ({Formatting.Size(t.Bytes)})" : "";
        Confirm($"Remove {row.Name}'s files from the backup?",
            $"The copies of {row.Name} on the backup{size} go to {_page.RemovedPlace}. You can restore them from there until you delete them yourself. "
            + "Your folder on this PC isn't touched.",
            "Remove from backup",
            () => RecycleRemovedAsync(row.Name));
    }

    async Task RecycleRemovedAsync(string folder)
    {
        IsBusy = true;
        try
        {
            DecisionResult result;
            using (var session = await _page.OpenSessionAsync())
                result = await Task.Run(() => session.RecycleRemovedFolder(folder));
            if (result.Problems.Count > 0)
            {
                Saved($"{folder}'s files were kept on the backup: {result.Problems[0].Message}", warning: true);
                return;
            }
            Profile.RemovedFolders.RemoveAll(r => r.Name.Equals(folder, StringComparison.OrdinalIgnoreCase));
            _totals?.Remove(folder);
            Saved(result.SentToTrashFolder > 0
                ? $"The Recycle Bin had no room, so {folder}'s {Formatting.Files(result.Applied)} went to the backup's trash folder instead."
                : $"Sent {folder}'s {Formatting.Files(result.Applied)} to {_page.RemovedPlace}.");
        }
        catch (DriveSyncException e)
        {
            Error = e.Message;
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    void AskRemoveBackup()
    {
        if (!EnsureNotSyncing())
            return;
        string where = Profile.LastKnownRoot ?? "its drive";
        string offloaded = _page.Dashboard.OffloadedCount is "0" or "–" ? ""
            : $" Offloaded files ({_page.Dashboard.OffloadedSize}) exist only on that drive, so keep it safe.";
        Confirm($"Remove {Profile.Name} from DriveSync?",
            $"DriveSync stops syncing this backup and forgets it on this PC. Your folders on this PC aren't touched.\n\n"
            + $"Everything in {where} stays exactly where it is, including its history.{offloaded}\n\n"
            + "To use it again later, choose Add backup and pick that drive.",
            "Remove from DriveSync",
            () =>
            {
                _page.Main.RemoveBackup(_page);
                return Task.CompletedTask;
            });
    }

    void Confirm(string title, string message, string button, Func<Task> action)
    {
        ConfirmTitle = title;
        ConfirmMessage = message;
        ConfirmButton = button;
        _confirmAction = action;
        IsConfirmOpen = true;
    }

    [RelayCommand]
    async Task ConfirmYesAsync()
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

    // Moving

    [RelayCommand]
    void OpenMove()
    {
        if (!EnsureNotSyncing())
            return;
        if (_totals is null)
        {
            Error = "Plug in this backup's drive first. DriveSync copies from it to the new drive.";
            return;
        }
        MoveError = null;
        MoveTargets.Clear();
        SelectedMoveTarget = null;
        string? current = Path.GetPathRoot(Profile.LastKnownRoot ?? "");
        foreach (var drive in DriveInfo.GetDrives())
        {
            try
            {
                if (!drive.IsReady || drive.DriveType is DriveType.CDRom or DriveType.NoRootDirectory or DriveType.Ram)
                    continue;
                MoveTargets.Add(MoveTargetViewModel.ForDrive(this, drive, current, BackupBytes, Profile, AllBackups));
            }
            catch (IOException)
            {
            }
        }
        MoveSummary = $"DriveSync copies everything in {Profile.LastKnownRoot}: {Formatting.Size(BackupBytes)} of backed-up files, "
            + "plus old versions and its records. It checks every file against its checksum, then switches this backup to the new drive.";
        IsMoveOpen = true;
    }

    [RelayCommand]
    void ChooseMoveFolder()
    {
        if (PickFolder?.Invoke() is not { Length: > 0 } path)
            return;
        var target = MoveTargetViewModel.ForFolder(this, path);
        MoveTargets.Add(target);
        target.IsSelected = true;
    }

    internal void OnMoveTargetSelected(MoveTargetViewModel target)
    {
        foreach (var other in MoveTargets)
        {
            if (!ReferenceEquals(other, target))
                other.IsSelected = false;
        }
        SelectedMoveTarget = target;
    }

    bool CanStartMove() => SelectedMoveTarget is { IsAvailable: true } && !IsMoving;

    [RelayCommand(CanExecute = nameof(CanStartMove))]
    async Task StartMoveAsync()
    {
        if (SelectedMoveTarget is null || !EnsureNotSyncing())
            return;
        string target = SelectedMoveTarget.Root, oldRoot = Profile.LastKnownRoot ?? "";
        IsMoving = true;
        MoveError = null;
        MovePhase = "Getting ready";
        _moveCancel = new CancellationTokenSource();
        var started = DateTime.UtcNow;
        var progress = new Progress<SyncProgress>(p =>
        {
            if (p.BytesTotal <= 0)
                return;
            MovePhase = $"Copying {p.FilesDone + 1:N0} of {p.FilesTotal:N0}";
            MovePercent = 100.0 * p.BytesDone / p.BytesTotal;
            double seconds = (DateTime.UtcNow - started).TotalSeconds;
            string detail = $"{Formatting.Size(p.BytesDone)} of {Formatting.Size(p.BytesTotal)}";
            if (seconds > 2 && p.BytesDone > 0)
                detail += $" · {Format.Duration(TimeSpan.FromSeconds((p.BytesTotal - p.BytesDone) / (p.BytesDone / seconds)))} left";
            MoveDetail = detail;
        });
        var token = _moveCancel.Token;
        try
        {
            MoveResult result;
            using (var session = await _page.OpenSessionAsync())
                result = await Task.Run(() => session.MoveToAsync(target, AllBackups, progress, token));
            if (!result.Switched)
            {
                MoveError = $"Nothing changed: this backup still uses {oldRoot}. {Formatting.Files(result.Problems.Count)} couldn't be copied safely, "
                    + $"for example {result.Problems[0].Key}: {result.Problems[0].Message}";
                return;
            }
            Profile.DriveId = result.NewDriveId;
            Profile.LastKnownRoot = result.NewRoot;
            Profile.BackupFolderName = Path.GetFileName(result.NewRoot);
            IsMoveOpen = false;
            Saved($"{Profile.Name} now backs up to {result.NewRoot}. The old copy in {oldRoot} is untouched; clear it yourself once you're happy with the new drive.");
            _ = _page.Dashboard.RefreshAsync();
        }
        catch (OperationCanceledException)
        {
            MoveError = $"Move stopped. Nothing changed: this backup still uses {oldRoot}. The partial copy on the new drive is reused if you try again.";
        }
        catch (Exception e) when (e is DriveSyncException or IOException or UnauthorizedAccessException or SqliteException)
        {
            MoveError = e.Message;
        }
        finally
        {
            IsMoving = false;
            _moveCancel.Dispose();
            _moveCancel = null;
        }
    }

    [RelayCommand]
    void CancelMove()
    {
        if (IsMoving)
            _moveCancel?.Cancel();
        else
            IsMoveOpen = false;
    }
}

/// <summary>A row in the folder lists of Edit backup.</summary>
public sealed partial class FolderRowViewModel(EditBackupViewModel owner, string name, string path, string detail, bool isRemoved) : ObservableObject
{
    public string Name { get; } = name;

    public string Path { get; } = path;

    public string Detail { get; } = detail;

    public bool IsRemoved { get; } = isRemoved;

    public string RemovedOn { get; init; } = "";

    [RelayCommand]
    void Remove() => owner.AskRemoveFolder(this);

    [RelayCommand]
    void AddBack() => owner.AddBack(this);

    [RelayCommand]
    void Recycle() => owner.AskRecycleRemoved(this);
}

/// <summary>A drive or folder the backup could move to.</summary>
public sealed partial class MoveTargetViewModel : ObservableObject
{
    readonly EditBackupViewModel _owner;

    MoveTargetViewModel(EditBackupViewModel owner, string root, string title)
    {
        _owner = owner;
        Root = root;
        Title = title;
    }

    public string Root { get; }

    public string Title { get; }

    public string Detail { get; private init; } = "";

    /// <summary>Why this choice isn't ideal, or can't be used.</summary>
    public string? Note { get; private init; }

    public bool IsAvailable { get; private init; } = true;

    [ObservableProperty]
    public partial bool IsSelected { get; set; }

    partial void OnIsSelectedChanged(bool value)
    {
        if (value)
            _owner.OnMoveTargetSelected(this);
    }

    internal static MoveTargetViewModel ForDrive(EditBackupViewModel owner, DriveInfo drive, string? currentRoot, long needed, BackupProfile backup, IReadOnlyList<BackupProfile> all)
    {
        string root = drive.RootDirectory.FullName;
        string label = string.IsNullOrWhiteSpace(drive.VolumeLabel) ? "Local disk" : drive.VolumeLabel;
        long free = drive.AvailableFreeSpace;
        string? note = null;
        bool available = true;
        if (string.Equals(root, currentRoot, StringComparison.OrdinalIgnoreCase))
        {
            note = "This backup is on this drive now.";
            available = false;
        }
        else if (free < needed)
        {
            note = $"Not enough room: needs about {Formatting.Size(needed)}, has {Formatting.Size(free)} free.";
            available = false;
        }
        else if (all.FirstOrDefault(b => b.Id != backup.Id && SameDrive(b.LastKnownRoot, root)
                     && b.Sources.Select(s => s.Path).Intersect(backup.Sources.Select(s => s.Path), StringComparer.OrdinalIgnoreCase).Any()) is { } twin)
        {
            note = $"Already holds {twin.Name}, with some of the same folders. Pick another drive to keep two separate copies.";
        }
        else if (drive.DriveType == DriveType.Removable)
        {
            note = "Removable drives have no Recycle Bin, so files DriveSync removes from this backup always go to its trash folder on the drive.";
        }
        return new MoveTargetViewModel(owner, root, $"{root.TrimEnd('\\')}  {label}")
        {
            Detail = $"{Formatting.Size(free)} free of {Formatting.Size(drive.TotalSize)}",
            Note = note,
            IsAvailable = available,
        };
    }

    internal static MoveTargetViewModel ForFolder(EditBackupViewModel owner, string folder) =>
        new(owner, System.IO.Path.TrimEndingDirectorySeparator(folder), System.IO.Path.TrimEndingDirectorySeparator(folder)) { Detail = "A folder you picked" };

    static bool SameDrive(string? path, string driveRoot) =>
        path is not null && string.Equals(System.IO.Path.GetPathRoot(path), driveRoot, StringComparison.OrdinalIgnoreCase);
}
