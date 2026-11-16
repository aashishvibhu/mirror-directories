using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DriveSync.App.Services;
using DriveSync.Core;
using DriveSync.Core.Engine;
using Microsoft.Data.Sqlite;

namespace DriveSync.App.ViewModels;

public sealed record HealthIssueRow(string Key, string Problem, string Note);

/// <summary>
/// "Health": whether the copies on the backup drive are still good. Shows what the checks after each sync have found,
/// runs a full checksum check on request, and repairs copies the PC still has a good original of.
/// </summary>
public sealed partial class HealthViewModel(BackupPageViewModel page) : ObservableObject
{
    CancellationTokenSource? _cancel;

    public ObservableCollection<HealthIssueRow> Issues { get; } = [];

    [ObservableProperty]
    public partial bool IsLoading { get; set; }

    [ObservableProperty]
    public partial string? LoadError { get; set; }

    [ObservableProperty]
    public partial bool IsHealthy { get; set; } = true;

    [ObservableProperty]
    public partial string Headline { get; set; } = "";

    [ObservableProperty]
    public partial string QuickLine { get; set; } = "";

    [ObservableProperty]
    public partial string VerifiedLine { get; set; } = "";

    [ObservableProperty]
    public partial double VerifiedPercent { get; set; }

    [ObservableProperty]
    public partial string FullLine { get; set; } = "";

    [ObservableProperty]
    public partial string? ExtraLine { get; set; }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(CheckNowCommand), nameof(RepairCommand))]
    public partial bool IsChecking { get; set; }

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
    [NotifyCanExecuteChangedFor(nameof(RepairCommand))]
    public partial bool CanRepairSomething { get; set; }

    public bool RollingCheck
    {
        get => page.Profile.RollingCheck;
        set
        {
            if (page.Profile.RollingCheck == value)
                return;
            page.Profile.RollingCheck = value;
            page.Main.Services.Save();
            OnPropertyChanged();
        }
    }

    public string RollingCheckNote =>
        $"After each sync, DriveSync spends up to {page.Profile.RollingCheckMinutes} minutes checksumming the copies it checked longest ago. "
        + "Over a few weeks that covers the whole drive. You can stop it at any time with Cancel.";

    public async Task LoadAsync()
    {
        if (IsChecking)
            return;
        IsLoading = true;
        LoadError = null;
        try
        {
            HealthStatus status;
            using (var session = await page.OpenSessionAsync())
                status = await Task.Run(session.GetHealthStatus);
            Show(status);
            Issues.Clear();
            foreach (var record in status.FailedChecksum)
                Issues.Add(new HealthIssueRow(record.Key, "Damaged", "The backup copy no longer matches its checksum."));
            CanRepairSomething = status.Issues > 0;
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

    void Show(HealthStatus status)
    {
        page.HealthIssues = status.Issues;
        IsHealthy = status.Issues == 0;
        Headline = status.Issues == 0
            ? "No problems found with the copies on this backup"
            : status.Issues == 1 ? "1 backup copy has a problem" : $"{status.Issues:N0} backup copies have a problem";
        QuickLine = status.LastQuickCheckUtc is { } quick
            ? $"Checked {Format.Ago(quick)} that every copy is there and the right size: "
              + (status.MissingOrWrongSize == 0 ? "all present." : $"{Formatting.Files(status.MissingOrWrongSize)} missing or the wrong size. Run a check to see which.")
            : "Not checked yet. The first check runs at the end of the next sync.";
        VerifiedPercent = status.Files == 0 ? 0 : 100.0 * status.VerifiedLast30Days / status.Files;
        VerifiedLine = status.Files == 0
            ? "Nothing is backed up yet."
            : $"{status.VerifiedLast30Days:N0} of {status.Files:N0} copies passed a checksum check in the last 30 days ({VerifiedPercent:0}%).";
        FullLine = status.LastFullCheckUtc is { } full ? $"Last check of everything: {Format.Ago(full)}." : "Everything has never been checked in one go.";
    }

    bool CanCheck() => !IsChecking;

    /// <summary>Checksums every copy on the drive. Reads the whole backup, so it can take a while.</summary>
    [RelayCommand(CanExecute = nameof(CanCheck))]
    Task CheckNow() => RunAsync(repair: false);

    bool CanRepair() => !IsChecking && CanRepairSomething;

    /// <summary>Checks again and recopies damaged or missing copies from the PC where the original is unchanged.</summary>
    [RelayCommand(CanExecute = nameof(CanRepair))]
    Task Repair() => RunAsync(repair: true);

    async Task RunAsync(bool repair)
    {
        if (page.Dashboard.IsSyncing)
        {
            ResultIsWarning = true;
            ResultMessage = "This backup is syncing. Wait for it to finish, then check.";
            return;
        }
        IsChecking = true;
        ResultMessage = null;
        Phase = "Getting ready";
        Percent = 0;
        Detail = "";
        _cancel = new CancellationTokenSource();
        var progress = new Progress<SyncProgress>(p =>
        {
            Phase = $"Checking {Math.Min(p.FilesDone + 1, Math.Max(1, p.FilesTotal)):N0} of {p.FilesTotal:N0}";
            if (p.BytesTotal > 0)
            {
                Percent = 100.0 * p.BytesDone / p.BytesTotal;
                Detail = $"{Formatting.Size(p.BytesDone)} of {Formatting.Size(p.BytesTotal)}";
            }
        });
        var token = _cancel.Token;
        try
        {
            HealthReport report;
            HealthStatus status;
            using (var session = await page.OpenSessionAsync())
            {
                report = await Task.Run(() => session.CheckHealthAsync(full: true, repair, progress, token));
                status = await Task.Run(session.GetHealthStatus);
            }
            Show(status);
            Issues.Clear();
            foreach (var issue in report.Issues.Where(i => !i.Repaired).Take(200))
            {
                Issues.Add(new HealthIssueRow(issue.Record.Key, issue.Problem switch
                {
                    HealthProblem.Missing => "Missing",
                    HealthProblem.WrongSize => "Wrong size",
                    _ => "Damaged",
                }, issue.Note ?? (issue.Record.State == Core.Model.FileState.Active
                    ? "The file is still on this PC. Repair recopies it."
                    : "This file isn't on this PC, so the backup held the only copy.")));
            }
            int left = report.Issues.Count(i => !i.Repaired);
            CanRepairSomething = report.Issues.Any(i => !i.Repaired && i.Record.State == Core.Model.FileState.Active && i.Note is null);
            var parts = new List<string> { $"Checked {Formatting.Files(report.Checked)}." };
            if (report.Repaired > 0)
                parts.Add($"Repaired {Formatting.Files(report.Repaired)} from this PC; the damaged copies were kept as old versions.");
            parts.Add(left == 0 ? "Every copy matches its checksum." : $"{Formatting.Files(left)} {(left == 1 ? "has" : "have")} a problem, listed below.");
            ResultMessage = string.Join(" ", parts);
            ResultIsWarning = left > 0;
            var extra = new List<string>();
            if (report.Untracked.Count > 0)
                extra.Add($"{Formatting.Files(report.Untracked.Count)} on the drive aren't in DriveSync's records (added by hand?). They're left alone.");
            if (report.LeftoverTempFiles.Count > 0)
                extra.Add(repair ? $"Cleared {report.LeftoverTempFiles.Count:N0} unfinished copies left by an interrupted sync."
                    : $"{report.LeftoverTempFiles.Count:N0} unfinished copies were left by an interrupted sync. Repair clears them.");
            ExtraLine = extra.Count == 0 ? null : string.Join(" ", extra);
            if (!repair && report.LeftoverTempFiles.Count > 0)
                CanRepairSomething = true;
        }
        catch (OperationCanceledException)
        {
            ResultMessage = "Check stopped. What was checked so far is remembered.";
            ResultIsWarning = false;
        }
        catch (Exception e) when (e is DriveSyncException or IOException or UnauthorizedAccessException or SqliteException)
        {
            ResultMessage = e is DriveSyncException ? e.Message : $"The check stopped: {e.Message}";
            ResultIsWarning = true;
        }
        finally
        {
            _cancel.Dispose();
            _cancel = null;
            IsChecking = false;
        }
        _ = page.Dashboard.RefreshAsync();
    }

    [RelayCommand]
    void Cancel()
    {
        Phase = "Stopping…";
        _cancel?.Cancel();
    }

    [RelayCommand]
    void DismissResult() => ResultMessage = null;
}
