using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DriveSync.App.Services;
using DriveSync.Core;
using DriveSync.Core.Model;
using DriveSync.Core.Storage;

namespace DriveSync.App.ViewModels;

public sealed record WizardStep(int Number, string Title);

/// <summary>
/// First-run setup in four steps: folders, drive, what to do with missing files, start.
/// Nothing is written anywhere until the last step's button is pressed.
/// </summary>
public sealed partial class SetupWizardViewModel : ObservableObject, IDisposable
{
    /// <summary>Typical sustained write speed of a USB hard drive, used for the time estimate.</summary>
    const double TypicalBytesPerSecond = 130.0 * 1024 * 1024;

    readonly AppServices _services;
    readonly CancellationTokenSource _sizing = new();

    public SetupWizardViewModel(AppServices services, IEnumerable<FolderSuggestion> suggestions)
    {
        _services = services;
        _suggestions = [.. suggestions];
        foreach (var choice in AdoptChoices())
            AdoptModes.Add(choice);
        AdoptModes[0].IsSelected = true;
        IsFirstBackup = services.Backups.Count == 0;
        Name = IsFirstBackup ? "My backup" : NextFreeName(services.Backups);
        foreach (var suggestion in _suggestions)
            AddFolderChoice(suggestion.Name, suggestion.Path, selected: false);
        foreach (var choice in PolicyChoices())
            Policies.Add(choice);
        Policies[0].IsSelected = true;
        RefreshDrives();
    }

    /// <summary>The four rules for files that disappear from the PC, shared with Edit backup.</summary>
    internal static IEnumerable<PolicyChoiceViewModel> PolicyChoices() =>
    [
        new(MissingFilePolicy.Ask, "Ask me (recommended)",
            "DriveSync lists missing files and you decide: deleted on purpose, or removed to free space. Until you answer, they stay on the backup."),
        new(MissingFilePolicy.RecycleBinMeansDeleted, "Treat files in the Recycle Bin as deleted",
            "If a missing file is in this PC's Recycle Bin, its backup copy is removed from the backup too (and can still be restored). DriveSync asks about the rest."),
        new(MissingFilePolicy.AlwaysKeep, "Always keep them on the backup",
            "Missing files are treated as removed to free space. The backup never loses anything."),
        new(MissingFilePolicy.AlwaysRemove, "Always remove them from the backup",
            "The backup follows your PC. Copies of missing files are removed from the backup, and can be restored until you erase them yourself."),
    ];

    readonly List<FolderSuggestion> _suggestions;

    // Using a backup that's already on the drive (made on another PC, or removed from the app earlier)

    static IEnumerable<AdoptChoiceViewModel> AdoptChoices() =>
    [
        new(FirstRun.RestoreEverything, "Copy everything back to this PC",
            "For a new or reset PC. Every file on the backup is restored into the folders above, then backing up carries on from here."),
        new(FirstRun.KeepOnDrive, "Leave the files on the drive",
            "Files that aren't on this PC stay on the backup, marked as offloaded. Nothing is asked about and nothing is removed. Restore any of them later from Files."),
        new(FirstRun.Review, "Show me the differences first",
            "DriveSync compares this PC with the backup and you review what it found before anything changes."),
    ];

    FoundBackup? _found;

    /// <summary>What the first sync does once the wizard closes. Only differs from a plain backup after adopting one.</summary>
    public FirstRun FirstRun { get; private set; } = FirstRun.Backup;

    /// <summary>Opens Windows' BitLocker page. Set by the window.</summary>
    public Action? OpenBitLocker { get; set; }

    public ObservableCollection<AdoptFolderViewModel> AdoptFolders { get; } = [];

    public ObservableCollection<AdoptChoiceViewModel> AdoptModes { get; } = [];

    /// <summary>"This drive already holds the backup …", shown on the drive step with a button to use it.</summary>
    [ObservableProperty]
    public partial string? FoundNote { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsStep1), nameof(IsStep2), nameof(IsStep3), nameof(IsStep4), nameof(ShowNext), nameof(ShowFinish))]
    [NotifyCanExecuteChangedFor(nameof(BackCommand))]
    public partial bool IsAdopting { get; set; }

    [ObservableProperty]
    public partial string AdoptName { get; set; } = "";

    [ObservableProperty]
    public partial string AdoptSummary { get; set; } = "";

    /// <summary>True when the selected drive isn't encrypted, so the wizard can suggest BitLocker once.</summary>
    [ObservableProperty]
    public partial bool ShowBitLockerHint { get; set; }

    public bool ShowNext => !IsLastStep && !IsAdopting;

    public bool ShowFinish => IsLastStep && !IsAdopting;

    void LookForBackupOn(DriveChoiceViewModel? drive)
    {
        _found = drive is null ? null : BackupSetup.FindBackups(drive.Root, _services.Backups).FirstOrDefault();
        if (_found is null)
        {
            FoundNote = null;
            return;
        }
        string when = _found.LastSyncUtc is { } t ? $", last backed up {Format.Ago(t)}" : "";
        string from = _found.FromAnotherPc ? $" from the PC {_found.Manifest!.PcName}" : "";
        FoundNote = $"This place already holds the backup \"{_found.Name}\"{from}: {Formatting.Files(_found.Files)}, {Formatting.Size(_found.Bytes)}{when}. "
            + "You can carry on using it here instead of starting a new one.";
    }

    [RelayCommand]
    void OpenBitLockerPage() => OpenBitLocker?.Invoke();

    [RelayCommand]
    void UseFoundBackup()
    {
        if (_found is null)
            return;
        Error = null;
        AdoptName = _services.Backups.Any(b => b.Name.Equals(_found.Name, StringComparison.OrdinalIgnoreCase)) ? NextFreeName(_services.Backups) : _found.Name;
        AdoptFolders.Clear();
        foreach (var folder in _found.Folders)
        {
            string path = folder.OriginalPath.Length > 0 && Directory.Exists(folder.OriginalPath) ? folder.OriginalPath
                : _suggestions.FirstOrDefault(s => s.Name.Equals(folder.Name, StringComparison.OrdinalIgnoreCase))?.Path ?? "";
            AdoptFolders.Add(new AdoptFolderViewModel(this, folder) { Path = path });
        }
        AdoptSummary = $"{Formatting.Files(_found.Files)}, {Formatting.Size(_found.Bytes)} in {_found.Root}. Copying everything back needs that much room on this PC.";
        IsAdopting = true;
    }

    [RelayCommand(CanExecute = nameof(CanFinish))]
    async Task FinishAdopt()
    {
        if (_found is null)
            return;
        IsCreating = true;
        Error = null;
        var found = _found;
        string name = AdoptName;
        var paths = AdoptFolders.Where(f => f.Path.Length > 0).ToDictionary(f => f.Name, f => f.Path, StringComparer.OrdinalIgnoreCase);
        var existing = _services.Backups.ToList();
        try
        {
            var profile = await Task.Run(() => BackupSetup.Adopt(found, name, paths, existing));
            FirstRun = AdoptModes.First(m => m.IsSelected).Mode;
            _services.Add(profile);
            _sizing.Cancel();
            Completed?.Invoke(profile);
        }
        catch (Exception e) when (e is DriveSyncException or IOException or UnauthorizedAccessException)
        {
            Error = e is DriveSyncException ? e.Message : $"Couldn't set up the folders: {e.Message}";
        }
        finally
        {
            IsCreating = false;
        }
    }

    static string NextFreeName(IReadOnlyList<BackupProfile> backups)
    {
        for (int i = backups.Count + 1; ; i++)
        {
            string name = $"Backup {i}";
            if (!backups.Any(b => b.Name.Equals(name, StringComparison.OrdinalIgnoreCase)))
                return name;
        }
    }

    /// <summary>True when this is the PC's first backup (the welcome wizard), false when adding another.</summary>
    public bool IsFirstBackup { get; }

    public string WelcomeTitle => IsFirstBackup ? "Welcome" : "New backup";

    public string WelcomeText => IsFirstBackup ? "Four quick steps, then your first backup." : "Your other backups keep running as they are.";

    internal IReadOnlyList<BackupProfile> ExistingBackups => _services.Backups;

    public IReadOnlyList<WizardStep> Steps { get; } =
    [
        new(1, "Name and folders"),
        new(2, "Backup drive"),
        new(3, "Missing files"),
        new(4, "Start"),
    ];

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(NextCommand))]
    public partial string Name { get; set; }

    /// <summary>Why the name can't be used, if it can't.</summary>
    public string? NameProblem => Name.Trim().Length == 0 ? "Give this backup a name."
        : _services.Backups.Any(b => b.Name.Equals(Name.Trim(), StringComparison.OrdinalIgnoreCase)) ? $"You already have a backup called \"{Name.Trim()}\"."
        : null;

    partial void OnNameChanged(string value) => OnPropertyChanged(nameof(NameProblem));

    public ObservableCollection<FolderChoiceViewModel> Folders { get; } = [];

    public ObservableCollection<DriveChoiceViewModel> Drives { get; } = [];

    public ObservableCollection<PolicyChoiceViewModel> Policies { get; } = [];

    /// <summary>Opens a folder picker. Set by the window so this class stays free of UI code.</summary>
    public Func<string?>? PickFolder { get; set; }

    /// <summary>Raised with the new backup once it's created and saved.</summary>
    public event Action<BackupProfile>? Completed;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsStep1), nameof(IsStep2), nameof(IsStep3), nameof(IsStep4), nameof(IsLastStep), nameof(CurrentStep), nameof(ShowNext), nameof(ShowFinish))]
    [NotifyCanExecuteChangedFor(nameof(NextCommand), nameof(BackCommand))]
    public partial int Step { get; set; } = 1;

    public bool IsStep1 => Step == 1 && !IsAdopting;

    public bool IsStep2 => Step == 2 && !IsAdopting;

    public bool IsStep3 => Step == 3 && !IsAdopting;

    public bool IsStep4 => Step == 4 && !IsAdopting;

    public bool IsLastStep => Step == 4;

    public WizardStep CurrentStep => Steps[Step - 1];

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(NextCommand))]
    public partial DriveChoiceViewModel? SelectedDrive { get; set; }

    [ObservableProperty]
    public partial string SelectionSummary { get; set; } = "No folders selected yet.";

    [ObservableProperty]
    public partial string? DriveWarning { get; set; }

    [ObservableProperty]
    public partial string? Error { get; set; }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(FinishCommand), nameof(BackCommand), nameof(FinishAdoptCommand))]
    public partial bool IsCreating { get; set; }

    // Step 4 summary

    [ObservableProperty]
    public partial string SummaryName { get; set; } = "";

    [ObservableProperty]
    public partial string SummaryFolders { get; set; } = "";

    [ObservableProperty]
    public partial string SummaryDrive { get; set; } = "";

    [ObservableProperty]
    public partial string SummaryPolicy { get; set; } = "";

    [ObservableProperty]
    public partial string SummarySize { get; set; } = "";

    [ObservableProperty]
    public partial string SummaryTime { get; set; } = "";

    IEnumerable<FolderChoiceViewModel> SelectedFolders => Folders.Where(f => f.IsSelected);

    bool CanGoNext() => Step switch
    {
        1 => SelectedFolders.Any() && NameProblem is null,
        2 => SelectedDrive is not null,
        3 => true,
        _ => false,
    };

    [RelayCommand(CanExecute = nameof(CanGoNext))]
    void Next()
    {
        Error = null;
        if (Step == 1)
            UpdateDriveWarning();
        if (Step == 3)
            UpdateSummary();
        Step++;
    }

    bool CanGoBack() => (Step > 1 || IsAdopting) && !IsCreating;

    [RelayCommand(CanExecute = nameof(CanGoBack))]
    void Back()
    {
        Error = null;
        if (IsAdopting)
            IsAdopting = false; // back to the drive step
        else
            Step--;
    }

    [RelayCommand]
    void AddFolder()
    {
        string? path = PickFolder?.Invoke();
        if (string.IsNullOrEmpty(path))
            return;
        var existing = Folders.FirstOrDefault(f => f.Path.Equals(Path.TrimEndingDirectorySeparator(path), StringComparison.OrdinalIgnoreCase));
        if (existing is not null)
        {
            existing.IsSelected = true;
            return;
        }
        string name = Path.GetFileName(Path.TrimEndingDirectorySeparator(path));
        AddFolderChoice(string.IsNullOrEmpty(name) ? path : name, path, selected: true);
    }

    void AddFolderChoice(string name, string path, bool selected)
    {
        string folder = Path.TrimEndingDirectorySeparator(path);
        var holders = _services.Backups.Where(b => b.Sources.Any(s => s.Path.Equals(folder, StringComparison.OrdinalIgnoreCase))).Select(b => b.Name).ToList();
        var choice = new FolderChoiceViewModel(this, name, folder)
        {
            IsSelected = selected,
            AlsoIn = holders.Count == 0 ? "" : "In " + string.Join(", ", holders),
        };
        Folders.Add(choice);
        _ = choice.MeasureAsync(_sizing.Token);
    }

    [RelayCommand]
    void RefreshDrives()
    {
        string? keep = SelectedDrive?.Root;
        Drives.Clear();
        string systemRoot = Path.GetPathRoot(Environment.SystemDirectory) ?? "C:\\";
        foreach (var drive in DriveInfo.GetDrives())
        {
            try
            {
                if (!drive.IsReady || drive.DriveType is DriveType.CDRom or DriveType.NoRootDirectory or DriveType.Ram)
                    continue;
                Drives.Add(DriveChoiceViewModel.FromDrive(this, drive, systemRoot));
            }
            catch (IOException)
            {
            }
        }
        var again = Drives.FirstOrDefault(d => d.Root.Equals(keep, StringComparison.OrdinalIgnoreCase));
        if (again is not null)
            again.IsSelected = true;
        else
            SelectedDrive = null;
    }

    [RelayCommand]
    void ChooseDriveFolder()
    {
        string? path = PickFolder?.Invoke();
        if (string.IsNullOrEmpty(path))
            return;
        string folder = Path.TrimEndingDirectorySeparator(path);
        var choice = Drives.FirstOrDefault(d => d.Root.Equals(folder, StringComparison.OrdinalIgnoreCase));
        if (choice is null)
        {
            choice = DriveChoiceViewModel.FromFolder(this, folder);
            Drives.Add(choice);
        }
        choice.IsSelected = true;
    }

    bool CanFinish() => !IsCreating;

    [RelayCommand(CanExecute = nameof(CanFinish))]
    async Task Finish()
    {
        if (SelectedDrive is null)
            return;
        IsCreating = true;
        Error = null;
        string target = SelectedDrive.Root;
        var folders = SelectedFolders.Select(f => f.Path).ToList();
        var policy = Policies.First(p => p.IsSelected).Policy;
        try
        {
            string name = Name.Trim();
            var existing = _services.Backups.ToList();
            var profile = await Task.Run(() =>
            {
                var created = BackupSetup.CreateProfile(target, folders, name, existing);
                // A brand-new backup keeps what it removes in its own trash folder: no size limit, and the same on every PC.
                // One that was already on the drive keeps the setting it had.
                if (!BackupLocation.HasCatalog(created.LastKnownRoot!))
                    created.RemovedFilesGoTo = RemovedFilesGoTo.TrashFolder;
                else if (BackupManifest.TryRead(created.LastKnownRoot!) is { } manifest)
                    created.RemovedFilesGoTo = manifest.RemovedFilesGoTo;
                return created;
            });
            profile.MissingFilePolicy = policy;
            _services.Add(profile);
            _sizing.Cancel();
            Completed?.Invoke(profile);
        }
        catch (Exception e) when (e is DriveSyncException or IOException or UnauthorizedAccessException)
        {
            Error = e is DriveSyncException ? e.Message : $"Couldn't prepare the backup drive: {e.Message}";
        }
        finally
        {
            IsCreating = false;
        }
    }

    internal void OnFolderSelectionChanged()
    {
        NextCommand.NotifyCanExecuteChanged();
        var selected = SelectedFolders.ToList();
        if (selected.Count == 0)
        {
            SelectionSummary = "No folders selected yet.";
            return;
        }
        bool measuring = selected.Any(f => f.Bytes is null);
        long bytes = selected.Sum(f => f.Bytes ?? 0);
        SelectionSummary = selected.Count == 1 ? "1 folder selected" : $"{selected.Count} folders selected";
        SelectionSummary += measuring ? " · measuring size…" : $" · {Formatting.Size(bytes)}";
        if (Step == 4)
            UpdateSummary();
    }

    internal void OnDriveChecked(DriveChoiceViewModel drive)
    {
        if (ReferenceEquals(drive, SelectedDrive))
            UpdateDriveWarning();
    }

    internal void OnDriveSelected(DriveChoiceViewModel drive)
    {
        // Radio buttons only clear each other when clicked. A choice selected from code
        // (a folder just added, or the drive kept after Refresh) must clear the rest here.
        foreach (var other in Drives)
        {
            if (!ReferenceEquals(other, drive))
                other.IsSelected = false;
        }
        SelectedDrive = drive;
        UpdateDriveWarning();
    }

    void UpdateDriveWarning()
    {
        var drive = SelectedDrive;
        LookForBackupOn(drive);
        ShowBitLockerHint = drive is { Encryption: Core.IO.EncryptionState.NotEncrypted, IsSystemDrive: false };
        if (drive is null)
        {
            DriveWarning = null;
            return;
        }
        var warnings = new List<string>();
        if (!drive.IsCustomFolder && SelectedFolders.Any(f => BackupSetup.IsOnSameDrive(f.Path, drive.Root)))
            warnings.Add("This is the same drive as some of your folders. If it fails, you'd lose the files and the backup together. Pick an external drive if you can.");

        long needed = SelectedFolders.Sum(f => f.Bytes ?? 0);
        if (drive.FreeBytes is { } free && needed > free)
            warnings.Add($"Your folders take {Formatting.Size(needed)}, but this drive has only {Formatting.Size(free)} free.");
        DriveWarning = warnings.Count == 0 ? null : string.Join("\n\n", warnings);
    }

    void UpdateSummary()
    {
        var selected = SelectedFolders.ToList();
        SummaryFolders = string.Join("\n", selected.Select(f => $"{f.Name}  ({f.Path})"));
        SummaryName = Name.Trim();
        SummaryDrive = SelectedDrive is null ? "" : $"{BackupSetup.PlannedFolder(SelectedDrive.Root, Name.Trim(), _services.Backups)}  ·  {SelectedDrive.Title}";
        SummaryPolicy = Policies.First(p => p.IsSelected).Title.Replace(" (recommended)", "");
        if (selected.Any(f => f.Bytes is null))
        {
            SummarySize = "Still measuring…";
            SummaryTime = "";
            return;
        }
        long bytes = selected.Sum(f => f.Bytes ?? 0);
        int files = selected.Sum(f => f.FileCount);
        SummarySize = $"{Formatting.Size(bytes)} in {Formatting.Files(files)}";
        SummaryTime = Format.Duration(TimeSpan.FromSeconds(bytes / TypicalBytesPerSecond)) + " on a typical USB hard drive";
    }

    public void Dispose()
    {
        _sizing.Cancel();
        _sizing.Dispose();
    }
}

public sealed partial class FolderChoiceViewModel(SetupWizardViewModel owner, string name, string path) : ObservableObject
{
    public string Name { get; } = name;

    public string Path { get; } = path;

    /// <summary>"In Personal files" when another backup already copies this folder.</summary>
    public string AlsoIn { get; init; } = "";

    [ObservableProperty]
    public partial bool IsSelected { get; set; }

    [ObservableProperty]
    public partial string SizeText { get; set; } = "Measuring…";

    public long? Bytes { get; private set; }

    public int FileCount { get; private set; }

    partial void OnIsSelectedChanged(bool value) => owner.OnFolderSelectionChanged();

    /// <summary>Adds up the folder's size in the background. Reads names and sizes only.</summary>
    internal async Task MeasureAsync(CancellationToken ct)
    {
        string path = Path;
        try
        {
            var (bytes, files) = await Task.Run(() =>
            {
                long total = 0;
                int count = 0;
                var options = new EnumerationOptions
                {
                    RecurseSubdirectories = true,
                    IgnoreInaccessible = true,
                    AttributesToSkip = FileAttributes.ReparsePoint,
                };
                foreach (var file in new DirectoryInfo(path).EnumerateFiles("*", options))
                {
                    ct.ThrowIfCancellationRequested();
                    total += file.Length;
                    count++;
                }
                return (total, count);
            }, ct);
            Bytes = bytes;
            FileCount = files;
            SizeText = $"{Formatting.Size(bytes)} · {Formatting.Files(files)}";
        }
        catch (OperationCanceledException)
        {
            return;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            Bytes = 0;
            SizeText = "Couldn't measure this folder";
        }
        owner.OnFolderSelectionChanged();
    }
}

public sealed partial class DriveChoiceViewModel : ObservableObject
{
    readonly SetupWizardViewModel _owner;

    DriveChoiceViewModel(SetupWizardViewModel owner, string root, string title, string kind)
    {
        _owner = owner;
        Root = root;
        Title = title;
        Kind = kind;
    }

    public string Root { get; }

    public string Title { get; }

    public string Kind { get; }

    public long? FreeBytes { get; private init; }

    public double UsedPercent { get; private init; }

    public string SpaceText { get; private init; } = "";

    public bool IsRemovable { get; private init; }

    public bool IsSystemDrive { get; private init; }

    /// <summary>Whether BitLocker protects the drive; worked out in the background after the list is shown.</summary>
    public Core.IO.EncryptionState Encryption { get; private set; }

    async Task CheckEncryptionAsync()
    {
        Encryption = await Task.Run(() => Core.IO.DriveEncryption.Check(Root));
        _owner.OnDriveChecked(this);
    }

    public bool IsCustomFolder { get; private init; }

    /// <summary>"Has Personal files" for backups the app knows, or "Has a DriveSync backup" for one it would reconnect to.</summary>
    public string ExistingNote { get; private init; } = "";

    [ObservableProperty]
    public partial bool IsSelected { get; set; }

    partial void OnIsSelectedChanged(bool value)
    {
        if (value)
            _owner.OnDriveSelected(this);
    }

    static string NoteFor(string place, IReadOnlyList<BackupProfile> known)
    {
        var here = known.Where(b => b.LastKnownRoot is { } r && string.Equals(Path.GetDirectoryName(Path.TrimEndingDirectorySeparator(r)),
            Path.TrimEndingDirectorySeparator(place), StringComparison.OrdinalIgnoreCase)).Select(b => b.Name).ToList();
        if (here.Count > 0)
            return "Has " + string.Join(", ", here);
        string? id = BackupLocation.ReadDriveId(Path.Combine(place, BackupSetup.DefaultFolderName));
        return id is not null && !known.Any(b => b.DriveId.Equals(id, StringComparison.OrdinalIgnoreCase)) ? "Has a DriveSync backup" : "";
    }

    internal static DriveChoiceViewModel FromDrive(SetupWizardViewModel owner, DriveInfo drive, string systemRoot)
    {
        string root = drive.RootDirectory.FullName;
        string label = string.IsNullOrWhiteSpace(drive.VolumeLabel) ? "Local disk" : drive.VolumeLabel;
        string kind = drive.DriveType switch
        {
            DriveType.Removable => "Removable drive (USB stick or memory card)",
            DriveType.Network => "Network drive",
            _ when root.Equals(systemRoot, StringComparison.OrdinalIgnoreCase) => "This PC's main drive",
            _ => "Local or external drive",
        };
        long total = drive.TotalSize, free = drive.AvailableFreeSpace;
        var choice = new DriveChoiceViewModel(owner, root, $"{root.TrimEnd('\\')}  {label}", kind)
        {
            FreeBytes = free,
            UsedPercent = total > 0 ? 100.0 * (total - free) / total : 0,
            SpaceText = $"{Formatting.Size(free)} free of {Formatting.Size(total)}",
            IsRemovable = drive.DriveType == DriveType.Removable,
            IsSystemDrive = root.Equals(systemRoot, StringComparison.OrdinalIgnoreCase),
            ExistingNote = NoteFor(root, owner.ExistingBackups),
        };
        if (drive.DriveType is DriveType.Fixed or DriveType.Removable)
            _ = choice.CheckEncryptionAsync();
        return choice;
    }

    internal static DriveChoiceViewModel FromFolder(SetupWizardViewModel owner, string folder)
    {
        string path = Path.TrimEndingDirectorySeparator(folder);
        return new DriveChoiceViewModel(owner, path, path, "A folder you picked")
        {
            IsCustomFolder = true,
            SpaceText = "",
            ExistingNote = NoteFor(path, owner.ExistingBackups),
        };
    }
}

/// <summary>One of the backup's folders, and where it should live on this PC.</summary>
public sealed partial class AdoptFolderViewModel(SetupWizardViewModel owner, FoundFolder folder) : ObservableObject
{
    public string Name { get; } = folder.Name;

    public string Info { get; } = $"{Formatting.Files(folder.Files)} · {Formatting.Size(folder.Bytes)}"
        + (folder.OriginalPath.Length > 0 ? $" · was {folder.OriginalPath}" : "");

    /// <summary>The folder on this PC, or empty to leave this one on the drive without backing anything up into it.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PathText), nameof(IsUsed))]
    public partial string Path { get; set; } = "";

    public bool IsUsed => Path.Length > 0;

    public string PathText => Path.Length > 0 ? Path : "Not used on this PC. Its files stay on the drive.";

    [RelayCommand]
    void Choose()
    {
        if (owner.PickFolder?.Invoke() is { Length: > 0 } picked)
            Path = System.IO.Path.TrimEndingDirectorySeparator(picked);
    }

    [RelayCommand]
    void Skip() => Path = "";
}

public sealed partial class AdoptChoiceViewModel(FirstRun mode, string title, string description) : ObservableObject
{
    public FirstRun Mode { get; } = mode;

    public string Title { get; } = title;

    public string Description { get; } = description;

    [ObservableProperty]
    public partial bool IsSelected { get; set; }
}

public sealed partial class PolicyChoiceViewModel(MissingFilePolicy policy, string title, string description) : ObservableObject
{
    public MissingFilePolicy Policy { get; } = policy;

    public string Title { get; } = title;

    public string Description { get; } = description;

    [ObservableProperty]
    public partial bool IsSelected { get; set; }

}
