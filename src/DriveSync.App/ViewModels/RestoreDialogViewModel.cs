using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DriveSync.App.Services;
using DriveSync.Core;
using DriveSync.Core.Engine;
using Microsoft.Data.Sqlite;

namespace DriveSync.App.ViewModels;

/// <summary>A tab on a backup's page, such as Home or Files.</summary>
public sealed partial class PageTabViewModel(string title, IRelayCommand command) : ObservableObject
{
    public string Title { get; } = title;

    public IRelayCommand Command { get; } = command;

    [ObservableProperty]
    public partial bool IsSelected { get; set; }

    /// <summary>A count shown next to the title when it isn't zero.</summary>
    [ObservableProperty]
    public partial int Badge { get; set; }
}

/// <summary>
/// The restore dialog, shared by the Files and Storage tabs: where to restore to, what would happen,
/// then progress and the result. A different file already in the way is renamed, never replaced.
/// </summary>
public sealed partial class RestoreDialogViewModel(BackupPageViewModel page) : ObservableObject
{
    RestoreRequest? _request;
    CancellationTokenSource? _cancel;
    SyncControl? _control;
    int _planVersion;

    /// <summary>Opens a folder picker. Set by the view so this class stays free of UI code.</summary>
    public Func<string?>? PickFolder { get; set; }

    /// <summary>Raised after a restore has run, so the screen behind can reload.</summary>
    public event Action? Finished;

    public ObservableCollection<string> Problems { get; } = [];

    [ObservableProperty]
    public partial bool IsOpen { get; set; }

    [ObservableProperty]
    public partial string Title { get; set; } = "";

    [ObservableProperty]
    public partial string Summary { get; set; } = "";

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(StartCommand))]
    public partial bool IsPlanning { get; set; }

    /// <summary>False when the original place isn't known (a version or removed file can still go to a folder).</summary>
    [ObservableProperty]
    public partial bool CanUseOriginal { get; set; } = true;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(StartCommand))]
    public partial bool ToOriginal { get; set; } = true;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(StartCommand))]
    public partial string? TargetFolder { get; set; }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(StartCommand))]
    public partial bool IsRunning { get; set; }

    [ObservableProperty]
    public partial bool IsPaused { get; set; }

    [ObservableProperty]
    public partial bool IsDone { get; set; }

    [ObservableProperty]
    public partial string Phase { get; set; } = "";

    [ObservableProperty]
    public partial double Percent { get; set; }

    [ObservableProperty]
    public partial string Detail { get; set; } = "";

    [ObservableProperty]
    public partial string? ResultMessage { get; set; }

    [ObservableProperty]
    public partial bool ResultIsWarning { get; set; }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(StartCommand))]
    public partial int ToRestore { get; set; }

    public bool ShowsChoices => !IsRunning && !IsDone;

    partial void OnIsRunningChanged(bool value) => OnPropertyChanged(nameof(ShowsChoices));

    partial void OnIsDoneChanged(bool value) => OnPropertyChanged(nameof(ShowsChoices));

    partial void OnToOriginalChanged(bool value) => _ = PlanAsync();

    partial void OnTargetFolderChanged(string? value) => _ = PlanAsync();

    /// <summary>Shows the dialog for <paramref name="request"/>. Nothing is copied until the user starts it.</summary>
    public void Open(RestoreRequest request, string title, bool canUseOriginal = true)
    {
        _request = request;
        Title = title;
        CanUseOriginal = canUseOriginal;
        IsDone = false;
        ResultMessage = null;
        Problems.Clear();
        Summary = "";
        ToRestore = 0;
        TargetFolder = null;
        ToOriginal = canUseOriginal;
        IsOpen = true;
        _ = PlanAsync();
    }

    RestoreRequest? CurrentRequest() =>
        _request is null ? null
        : ToOriginal ? _request with { TargetFolder = null }
        : string.IsNullOrEmpty(TargetFolder) ? null
        : _request with { TargetFolder = TargetFolder };

    async Task PlanAsync()
    {
        if (!IsOpen || IsRunning || IsDone)
            return;
        int version = ++_planVersion;
        if (CurrentRequest() is not { } request)
        {
            ToRestore = 0;
            Summary = "Choose a folder to restore to.";
            return;
        }
        IsPlanning = true;
        try
        {
            RestorePlan plan;
            using (var session = await page.OpenSessionAsync())
                plan = await Task.Run(() => session.PlanRestore(request));
            if (version != _planVersion)
                return;
            ToRestore = plan.ToRestore;
            Summary = Describe(plan);
        }
        catch (Exception e) when (e is DriveSyncException or IOException or UnauthorizedAccessException or SqliteException)
        {
            if (version != _planVersion)
                return;
            ToRestore = 0;
            Summary = e.Message;
        }
        finally
        {
            if (version == _planVersion)
                IsPlanning = false;
        }
    }

    internal static string Describe(RestorePlan plan)
    {
        var parts = new List<string>();
        parts.Add(plan.ToRestore == 0 ? "Nothing needs restoring." : $"{Formatting.Files(plan.ToRestore)} to restore ({Formatting.Size(plan.Bytes)}).");
        if (plan.AlreadyThere > 0)
            parts.Add($"{Formatting.Files(plan.AlreadyThere)} already there, identical to the backup.");
        if (plan.InTheWay > 0)
        {
            parts.Add(plan.InTheWay == 1
                ? "1 different file is in the way. It will be kept and renamed, never replaced."
                : $"{plan.InTheWay:N0} different files are in the way. They will be kept and renamed, never replaced.");
        }
        if (plan.MissingOnBackup > 0)
            parts.Add($"{Formatting.Files(plan.MissingOnBackup)} can't be restored: the backup copy is missing.");
        return string.Join(" ", parts);
    }

    [RelayCommand]
    void ChooseFolder()
    {
        if (PickFolder?.Invoke() is { Length: > 0 } folder)
        {
            TargetFolder = folder;
            ToOriginal = false;
        }
    }

    bool CanStart() => !IsRunning && !IsPlanning && ToRestore > 0 && (ToOriginal || !string.IsNullOrEmpty(TargetFolder));

    [RelayCommand(CanExecute = nameof(CanStart))]
    Task Start() => CurrentRequest() is { } request ? RunAsync(request) : Task.CompletedTask;

    /// <summary>Restores straight away, showing progress in the dialog. Used when a backup is first connected to a new PC.</summary>
    public Task RunNowAsync(RestoreRequest request, string title)
    {
        _request = request;
        Title = title;
        CanUseOriginal = true;
        ToOriginal = request.TargetFolder is null;
        IsDone = false;
        ResultMessage = null;
        Problems.Clear();
        Summary = "";
        IsOpen = true;
        return RunAsync(request);
    }

    async Task RunAsync(RestoreRequest request)
    {
        IsRunning = true;
        IsPaused = false;
        Phase = "Getting ready";
        Percent = 0;
        Detail = "";
        Problems.Clear();
        _cancel = new CancellationTokenSource();
        _control = new SyncControl();
        var started = DateTime.UtcNow;
        var progress = new Progress<SyncProgress>(p =>
        {
            if (p.Phase == "Paused")
            {
                Phase = "Paused";
                return;
            }
            if (p.BytesTotal <= 0 || p.Phase == "Done")
                return;
            if (!IsPaused)
                Phase = $"Restoring {Math.Min(p.FilesDone + 1, p.FilesTotal):N0} of {p.FilesTotal:N0}";
            Percent = 100.0 * p.BytesDone / p.BytesTotal;
            string detail = $"{Formatting.Size(p.BytesDone)} of {Formatting.Size(p.BytesTotal)}";
            double seconds = (DateTime.UtcNow - started).TotalSeconds;
            if (seconds > 2 && p.BytesDone > 0)
                detail += $" · {Format.Duration(TimeSpan.FromSeconds((p.BytesTotal - p.BytesDone) / (p.BytesDone / seconds)))} left";
            Detail = detail;
        });
        var token = _cancel.Token;
        var control = _control;
        try
        {
            RestoreResult result;
            using (var session = await page.OpenSessionAsync())
                result = await Task.Run(() => session.RestoreAsync(request, progress, token, control));

            var parts = new List<string> { $"Restored {Formatting.Files(result.Restored)} ({Formatting.Size(result.BytesRestored)})." };
            if (result.AlreadyThere > 0)
                parts.Add($"{Formatting.Files(result.AlreadyThere)} were already there.");
            if (result.KeptBoth > 0)
                parts.Add($"{Formatting.Files(result.KeptBoth)} in the way were kept, renamed \"(before restore …)\".");
            if (result.Problems.Count > 0)
                parts.Add($"{Formatting.Files(result.Problems.Count)} had a problem:");
            ResultMessage = string.Join(" ", parts);
            ResultIsWarning = result.Problems.Count > 0;
            foreach (var problem in result.Problems.Take(30))
                Problems.Add($"{problem.Key}: {problem.Message}");
        }
        catch (OperationCanceledException)
        {
            ResultMessage = "Restore stopped. Files already restored stay where they are; nothing on the backup changed.";
            ResultIsWarning = false;
        }
        catch (Exception e) when (e is DriveSyncException or IOException or UnauthorizedAccessException or SqliteException)
        {
            ResultMessage = e is DriveSyncException ? e.Message : $"The restore stopped: {e.Message}";
            ResultIsWarning = true;
        }
        finally
        {
            _cancel.Dispose();
            _cancel = null;
            _control = null;
            IsRunning = false;
            IsPaused = false;
            IsDone = true;
        }
        Finished?.Invoke();
    }

    [RelayCommand]
    void PauseOrResume()
    {
        if (_control is null)
            return;
        if (_control.IsPaused)
        {
            _control.Resume();
            IsPaused = false;
        }
        else
        {
            _control.Pause();
            IsPaused = true;
            Phase = "Pausing after this file…";
        }
    }

    /// <summary>Cancel while restoring; otherwise close the dialog.</summary>
    [RelayCommand]
    void Close()
    {
        if (IsRunning)
        {
            Phase = "Stopping…";
            _control?.Resume();
            _cancel?.Cancel();
            return;
        }
        _planVersion++;
        IsOpen = false;
    }
}
