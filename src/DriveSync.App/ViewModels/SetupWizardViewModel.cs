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
        foreach (var suggestion in suggestions)
            AddFolderChoice(suggestion.Name, suggestion.Path, selected: false);
        Policies.Add(new PolicyChoiceViewModel(MissingFilePolicy.Ask, "Ask me (recommended)",
            "DriveSync lists missing files and you decide: deleted on purpose, or removed to free space. Until you answer, they stay on the backup."));
        Policies.Add(new PolicyChoiceViewModel(MissingFilePolicy.RecycleBinMeansDeleted, "Treat files in the Recycle Bin as deleted",
            "If a missing file is in this PC's Recycle Bin, its backup copy goes to the backup drive's Recycle Bin too. DriveSync asks about the rest."));
        Policies.Add(new PolicyChoiceViewModel(MissingFilePolicy.AlwaysKeep, "Always keep them on the backup",
            "Missing files are treated as removed to free space. The backup never loses anything."));
        Policies.Add(new PolicyChoiceViewModel(MissingFilePolicy.AlwaysRemove, "Always remove them from the backup",
            "The backup follows your PC. Copies of missing files go to the backup drive's Recycle Bin."));
        Policies[0].IsSelected = true;
        RefreshDrives();
    }

    public IReadOnlyList<WizardStep> Steps { get; } =
    [
        new(1, "Folders"),
        new(2, "Backup drive"),
        new(3, "Missing files"),
        new(4, "Start"),
    ];

    public ObservableCollection<FolderChoiceViewModel> Folders { get; } = [];

    public ObservableCollection<DriveChoiceViewModel> Drives { get; } = [];

    public ObservableCollection<PolicyChoiceViewModel> Policies { get; } = [];

    /// <summary>Opens a folder picker. Set by the window so this class stays free of UI code.</summary>
    public Func<string?>? PickFolder { get; set; }

    public event Action? Completed;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsStep1), nameof(IsStep2), nameof(IsStep3), nameof(IsStep4), nameof(IsLastStep), nameof(CurrentStep))]
    [NotifyCanExecuteChangedFor(nameof(NextCommand), nameof(BackCommand))]
    public partial int Step { get; set; } = 1;

    public bool IsStep1 => Step == 1;

    public bool IsStep2 => Step == 2;

    public bool IsStep3 => Step == 3;

    public bool IsStep4 => Step == 4;

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
    [NotifyCanExecuteChangedFor(nameof(FinishCommand), nameof(BackCommand))]
    public partial bool IsCreating { get; set; }

    // Step 4 summary

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
        1 => SelectedFolders.Any(),
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

    bool CanGoBack() => Step > 1 && !IsCreating;

    [RelayCommand(CanExecute = nameof(CanGoBack))]
    void Back()
    {
        Error = null;
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
        var choice = new FolderChoiceViewModel(this, name, Path.TrimEndingDirectorySeparator(path)) { IsSelected = selected };
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
            var profile = await Task.Run(() => BackupSetup.CreateProfile(target, folders));
            profile.MissingFilePolicy = policy;
            _services.SaveProfile(profile);
            _sizing.Cancel();
            Completed?.Invoke();
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
        if (drive is null)
        {
            DriveWarning = null;
            return;
        }
        var warnings = new List<string>();
        if (!drive.IsCustomFolder && SelectedFolders.Any(f => BackupSetup.IsOnSameDrive(f.Path, drive.Root)))
            warnings.Add("This is the same drive as some of your folders. If it fails, you'd lose the files and the backup together. Pick an external drive if you can.");
        if (drive.IsRemovable)
            warnings.Add("Windows has no Recycle Bin on removable drives, so anything DriveSync removes from this backup goes to a trash folder on the drive instead. DriveSync never empties it.");
        long needed = SelectedFolders.Sum(f => f.Bytes ?? 0);
        if (drive.FreeBytes is { } free && needed > free)
            warnings.Add($"Your folders take {Formatting.Size(needed)}, but this drive has only {Formatting.Size(free)} free.");
        DriveWarning = warnings.Count == 0 ? null : string.Join("\n\n", warnings);
    }

    void UpdateSummary()
    {
        var selected = SelectedFolders.ToList();
        SummaryFolders = string.Join("\n", selected.Select(f => $"{f.Name}  ({f.Path})"));
        SummaryDrive = SelectedDrive is null ? "" : $"{Path.Combine(SelectedDrive.Root, BackupSetup.DefaultFolderName)}  ·  {SelectedDrive.Title}";
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

    public bool IsCustomFolder { get; private init; }

    public bool HasExistingBackup { get; private init; }

    [ObservableProperty]
    public partial bool IsSelected { get; set; }

    partial void OnIsSelectedChanged(bool value)
    {
        if (value)
            _owner.OnDriveSelected(this);
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
        return new DriveChoiceViewModel(owner, root, $"{root.TrimEnd('\\')}  {label}", kind)
        {
            FreeBytes = free,
            UsedPercent = total > 0 ? 100.0 * (total - free) / total : 0,
            SpaceText = $"{Formatting.Size(free)} free of {Formatting.Size(total)}",
            IsRemovable = drive.DriveType == DriveType.Removable,
            HasExistingBackup = BackupLocation.ReadDriveId(Path.Combine(root, BackupSetup.DefaultFolderName)) is not null,
        };
    }

    internal static DriveChoiceViewModel FromFolder(SetupWizardViewModel owner, string folder)
    {
        string path = Path.TrimEndingDirectorySeparator(folder);
        return new DriveChoiceViewModel(owner, path, path, "A folder you picked")
        {
            IsCustomFolder = true,
            SpaceText = "",
            HasExistingBackup = BackupLocation.ReadDriveId(Path.Combine(path, BackupSetup.DefaultFolderName)) is not null,
        };
    }
}

public sealed partial class PolicyChoiceViewModel(MissingFilePolicy policy, string title, string description) : ObservableObject
{
    public MissingFilePolicy Policy { get; } = policy;

    public string Title { get; } = title;

    public string Description { get; } = description;

    [ObservableProperty]
    public partial bool IsSelected { get; set; }

}
