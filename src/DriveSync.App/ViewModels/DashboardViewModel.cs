using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DriveSync.App.Services;
using DriveSync.Core;
using DriveSync.Core.Engine;
using DriveSync.Core.Model;
using DriveSync.Core.Storage;
using Microsoft.Data.Sqlite;
using Wpf.Ui.Controls;

namespace DriveSync.App.ViewModels;

public enum BackupStatus
{
    UpToDate,
    NeedsAttention,
    NeverBackedUp,
    DriveMissing,
}

public sealed record ActivityRow(string What, string Item, string When);

/// <summary>Home: backup status, the sync button, live progress, totals and recent activity.</summary>
public sealed partial class DashboardViewModel(MainViewModel main) : ObservableObject
{
    CancellationTokenSource? _cancel;
    SyncControl? _control;
    DateTime _copyStarted;
    long _copyStartBytes;

    [ObservableProperty]
    public partial BackupStatus Status { get; set; } = BackupStatus.NeverBackedUp;

    [ObservableProperty]
    public partial SymbolRegular StatusIcon { get; set; } = SymbolRegular.HardDrive20;

    [ObservableProperty]
    public partial string StatusTitle { get; set; } = "Checking your backup…";

    [ObservableProperty]
    public partial string StatusDetail { get; set; } = "";

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SyncNowCommand))]
    public partial bool IsDriveConnected { get; set; }

    [ObservableProperty]
    public partial string ProtectedCount { get; set; } = "–";

    [ObservableProperty]
    public partial string ProtectedSize { get; set; } = "";

    [ObservableProperty]
    public partial string OffloadedCount { get; set; } = "–";

    [ObservableProperty]
    public partial string OffloadedSize { get; set; } = "";

    [ObservableProperty]
    public partial int PendingCount { get; set; }

    [ObservableProperty]
    public partial string PendingHeadline { get; set; } = "";

    [ObservableProperty]
    public partial string PendingDetail { get; set; } = "";

    [ObservableProperty]
    public partial string ProblemsCount { get; set; } = "–";

    [ObservableProperty]
    public partial string ProblemsNote { get; set; } = "in the last sync";

    // Live sync

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SyncNowCommand))]
    public partial bool IsSyncing { get; set; }

    [ObservableProperty]
    public partial bool IsPaused { get; set; }

    [ObservableProperty]
    public partial string SyncPhase { get; set; } = "";

    [ObservableProperty]
    public partial string? SyncItem { get; set; }

    [ObservableProperty]
    public partial double SyncPercent { get; set; }

    [ObservableProperty]
    public partial bool IsProgressIndeterminate { get; set; } = true;

    [ObservableProperty]
    public partial string SyncDetail { get; set; } = "";

    // Result of the last sync run in this window

    [ObservableProperty]
    public partial string? LastRunMessage { get; set; }

    [ObservableProperty]
    public partial bool LastRunNeedsAttention { get; set; }

    public ObservableCollection<string> LastRunProblems { get; } = [];

    public ObservableCollection<ActivityRow> Activity { get; } = [];

    bool CanSync() => IsDriveConnected && !IsSyncing;

    [RelayCommand(CanExecute = nameof(CanSync))]
    Task SyncNow() => main.ShowPreviewAsync();

    [RelayCommand]
    void PauseOrResume()
    {
        if (_control is null)
            return;
        if (_control.IsPaused)
        {
            _control.Resume();
            IsPaused = false;
            SyncPhase = "Copying";
        }
        else
        {
            _control.Pause();
            IsPaused = true;
            SyncPhase = "Pausing after this file…";
        }
    }

    [RelayCommand]
    void CancelSync()
    {
        SyncPhase = "Stopping…";
        _control?.Resume();
        _cancel?.Cancel();
    }

    [RelayCommand]
    Task ReviewMissing() => main.ShowMissingAsync();

    /// <summary>Reads totals and recent activity from the drive, or from the PC's copy when the drive is unplugged.</summary>
    public async Task RefreshAsync()
    {
        if (main.Services.Profile is null || IsSyncing)
            return;
        try
        {
            using var session = await main.Services.OpenSessionAsync();
            var (summary, activity) = await Task.Run(() => (session.GetSummary(), session.Catalog.RecentActivity(40)));
            Show(summary, activity, connected: true);
        }
        catch (DriveBusyException e)
        {
            StatusDetail = e.Message;
        }
        catch (DriveSyncException)
        {
            var offline = await Task.Run(ReadOfflineCopy);
            if (offline is { } copy)
                Show(copy.Summary, copy.Activity, connected: false);
            else
                ShowNoData();
        }
    }

    (BackupSummary Summary, List<ActivityEntry> Activity)? ReadOfflineCopy()
    {
        string? path = main.Services.MirrorCatalogPath;
        if (path is null || !File.Exists(path))
            return null;
        try
        {
            using var catalog = Catalog.OpenReadOnly(path);
            return (DriveSyncSession.ReadSummary(catalog, main.Services.Profile?.LastKnownRoot ?? ""), catalog.RecentActivity(40));
        }
        catch (SqliteException)
        {
            return null;
        }
    }

    void ShowNoData()
    {
        IsDriveConnected = false;
        Status = BackupStatus.DriveMissing;
        StatusIcon = SymbolRegular.HardDrive20;
        StatusTitle = "Backup drive not connected";
        StatusDetail = "Plug in the drive you set up for DriveSync to back up.";
    }

    void Show(BackupSummary summary, List<ActivityEntry> activity, bool connected)
    {
        IsDriveConnected = connected;
        int active = summary.Count(FileState.Active), archived = summary.Count(FileState.Archived);
        PendingCount = summary.Count(FileState.Pending);
        main.PendingCount = PendingCount;
        ProtectedCount = active.ToString("N0");
        ProtectedSize = Formatting.Size(summary.Bytes(FileState.Active));
        OffloadedCount = archived.ToString("N0");
        OffloadedSize = archived == 0 ? "nothing offloaded yet" : Formatting.Size(summary.Bytes(FileState.Archived)) + " only on the drive";
        ProblemsCount = summary.LastSyncProblems?.ToString("N0") ?? "–";
        PendingHeadline = PendingCount == 1
            ? "1 file is missing from this PC"
            : $"{PendingCount:N0} files are missing from this PC";
        PendingDetail = PendingCount == 1
            ? "It's still on the backup. Tell DriveSync whether you deleted it on purpose or removed it to free space."
            : "They're still on the backup. Tell DriveSync whether you deleted them on purpose or removed them to free space.";

        string last = summary.LastSyncUtc is { } t ? "Backed up " + Format.Ago(t) : "Not backed up yet";
        if (!connected)
        {
            Status = BackupStatus.DriveMissing;
            StatusIcon = SymbolRegular.HardDrive20;
            StatusTitle = "Backup drive not connected";
            StatusDetail = summary.LastSyncUtc is { } when
                ? $"Last backup {Format.Ago(when)}. Plug in the drive to back up again."
                : "Plug in the drive you set up for DriveSync to back up.";
        }
        else
        {
            Status = summary.LastSyncUtc is null ? BackupStatus.NeverBackedUp
                : PendingCount > 0 || summary.LastSyncProblems > 0 ? BackupStatus.NeedsAttention
                : BackupStatus.UpToDate;
            StatusIcon = Status switch
            {
                BackupStatus.UpToDate => SymbolRegular.ShieldCheckmark24,
                BackupStatus.NeedsAttention => SymbolRegular.Warning24,
                _ => SymbolRegular.HardDrive20,
            };
            StatusTitle = last;
            string free = summary.DriveFreeBytes is { } f ? $" · {Formatting.Size(f)} free" : "";
            StatusDetail = $"Backup drive connected at {summary.BackupRoot}{free}";
        }

        Activity.Clear();
        foreach (var entry in activity.Where(a => a.Action != "touched").Take(10))
            Activity.Add(new ActivityRow(Describe(entry.Action), entry.Key ?? entry.Detail ?? "", Format.Ago(entry.TimeUtc)));
    }

    static string Describe(string action) => action switch
    {
        "copied" => "Backed up",
        "updated" => "Updated",
        "moved" => "Moved",
        "offloaded" => "Offloaded",
        "kept-on-backup" => "Kept on backup",
        "removed-from-backup" => "To Recycle Bin",
        "deleted-everywhere" => "Deleted",
        "missing-from-pc" => "Missing from PC",
        "back-on-pc" => "Back on PC",
        "restored" => "Restored",
        "sync" => "Sync finished",
        _ => action,
    };

    /// <summary>First backup straight after setup: scans and copies everything, showing progress here.</summary>
    public async Task RunFirstBackupAsync()
    {
        DriveSyncSession session;
        try
        {
            session = await main.Services.OpenSessionAsync();
        }
        catch (DriveSyncException e)
        {
            LastRunMessage = e.Message;
            LastRunNeedsAttention = true;
            return;
        }
        await RunAsync(session, plan: null);
    }

    /// <summary>Runs a sync on <paramref name="session"/> (planning first if no plan is given) and closes the session afterwards.</summary>
    public async Task RunAsync(DriveSyncSession session, SyncPlan? plan)
    {
        IsSyncing = true;
        IsPaused = false;
        LastRunMessage = null;
        LastRunProblems.Clear();
        _cancel = new CancellationTokenSource();
        _control = new SyncControl();
        _copyStarted = default;
        SyncPhase = "Getting ready";
        SyncItem = null;
        SyncDetail = "";
        IsProgressIndeterminate = true;
        var progress = new Progress<SyncProgress>(OnProgress);
        var token = _cancel.Token;
        var control = _control;

        try
        {
            var result = await Task.Run(async () =>
            {
                var toApply = plan ?? await session.PlanSyncAsync(progress, token);
                return await session.ApplyAsync(toApply, progress, token, control);
            });
            ShowResult(result);
        }
        catch (OperationCanceledException)
        {
            LastRunMessage = "Sync stopped. Everything copied so far is safely on the backup.";
            LastRunNeedsAttention = false;
        }
        catch (DriveSyncException e)
        {
            LastRunMessage = e.Message;
            LastRunNeedsAttention = true;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or SqliteException)
        {
            LastRunMessage = $"The sync stopped: {e.Message} Check that the backup drive is still connected, then sync again. Nothing already copied is lost.";
            LastRunNeedsAttention = true;
        }
        finally
        {
            try
            {
                session.Dispose();
            }
            catch (Exception e) when (e is IOException or SqliteException)
            {
            }
            _cancel.Dispose();
            _cancel = null;
            _control = null;
            IsSyncing = false;
            IsPaused = false;
        }
        await RefreshAsync();
    }

    void OnProgress(SyncProgress p)
    {
        if (p.Phase == "Done")
            return;
        if (p.Phase == "Paused")
        {
            SyncPhase = "Paused";
            return;
        }
        SyncItem = p.CurrentItem;
        if (p.Phase != "Copying" || p.BytesTotal <= 0)
        {
            IsProgressIndeterminate = true;
            SyncPhase = p.Phase == "Scanning" ? "Looking through your folders" : p.Phase;
            SyncDetail = p.Phase == "Scanning" ? $"{p.FilesDone:N0} files found so far" : "";
            return;
        }

        if (_copyStarted == default)
        {
            _copyStarted = DateTime.UtcNow;
            _copyStartBytes = p.BytesDone;
        }
        if (!IsPaused)
            SyncPhase = $"Copying {p.FilesDone + 1:N0} of {p.FilesTotal:N0}";
        IsProgressIndeterminate = false;
        SyncPercent = 100.0 * p.BytesDone / p.BytesTotal;

        double seconds = (DateTime.UtcNow - _copyStarted).TotalSeconds;
        long copied = p.BytesDone - _copyStartBytes;
        string detail = $"{Formatting.Size(p.BytesDone)} of {Formatting.Size(p.BytesTotal)}";
        if (seconds > 2 && copied > 0)
        {
            double speed = copied / seconds;
            var left = TimeSpan.FromSeconds((p.BytesTotal - p.BytesDone) / speed);
            detail += $" · {Formatting.Size((long)speed)}/s · {Format.Duration(left)} left";
        }
        SyncDetail = detail;
    }

    void ShowResult(SyncResult r)
    {
        var parts = new List<string>();
        if (r.Copied > 0) parts.Add($"{Formatting.Files(r.Copied)} backed up");
        if (r.Updated > 0) parts.Add($"{r.Updated:N0} updated");
        if (r.Moved > 0) parts.Add($"{r.Moved:N0} moved");
        if (r.BackOnPc > 0) parts.Add($"{r.BackOnPc:N0} back on this PC");
        if (r.KeptOnBackup > 0) parts.Add($"{r.KeptOnBackup:N0} kept on the backup");
        if (r.RemovedFromBackup > 0) parts.Add($"{r.RemovedFromBackup:N0} sent to the backup drive's Recycle Bin");

        string message = parts.Count == 0
            ? "Everything was already backed up."
            : $"Sync finished in {Format.Duration(r.Duration).Replace("about ", "")}: {string.Join(", ", parts)}.";
        if (r.MassDisappearance)
        {
            message += " So many files vanished at once that DriveSync didn't remove anything from the backup."
                + " Check that your folders are where they should be, then review the missing files.";
        }
        if (r.Problems.Count > 0)
            message += $" {Formatting.Files(r.Problems.Count)} couldn't be copied:";
        LastRunMessage = message;
        LastRunNeedsAttention = r.Problems.Count > 0 || r.MassDisappearance;
        foreach (var problem in r.Problems.Take(50))
            LastRunProblems.Add($"{problem.Key}: {problem.Message}");
        if (r.Problems.Count > 50)
            LastRunProblems.Add($"…and {r.Problems.Count - 50:N0} more.");
    }

    /// <summary>Puts the progress card into a realistic state, for the screenshot tool.</summary>
    internal void ShowSampleProgress()
    {
        IsSyncing = true;
        IsProgressIndeterminate = false;
        SyncPhase = "Copying 1,204 of 3,876";
        SyncItem = @"Desktop\Projects\Garden plan.docx";
        SyncPercent = 38;
        SyncDetail = "1.7 GB of 4.5 GB · 118 MB/s · about 24 minutes left";
    }

    internal void ClearSampleProgress() => IsSyncing = false;
}
