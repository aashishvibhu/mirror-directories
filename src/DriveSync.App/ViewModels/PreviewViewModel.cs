using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DriveSync.Core;
using DriveSync.Core.Engine;
using DriveSync.Core.Model;
using Microsoft.Data.Sqlite;
using Wpf.Ui.Controls;

namespace DriveSync.App.ViewModels;

public sealed record PreviewRow(string Path, string Detail);

/// <summary>One tab on the review screen, e.g. "New" or "Missing from this PC".</summary>
public sealed class PreviewCategory(string title, SymbolRegular icon, string note, IReadOnlyList<PreviewRow> rows, long bytes)
{
    public string Title { get; } = title;

    public SymbolRegular Icon { get; } = icon;

    public string Note { get; } = note;

    public IReadOnlyList<PreviewRow> Rows { get; } = rows;

    public string CountText { get; } = rows.Count.ToString("N0");

    public string SizeText { get; } = bytes > 0 ? Formatting.Size(bytes) : "";
}

/// <summary>"Review changes": what a sync would do, shown before anything changes.</summary>
public sealed partial class PreviewViewModel(BackupPageViewModel page) : ObservableObject, IDisposable
{
    readonly CancellationTokenSource _cancel = new();
    DriveSyncSession? _session;
    SyncPlan? _plan;
    bool _loading, _closed;

    [ObservableProperty]
    public partial bool IsScanning { get; set; } = true;

    [ObservableProperty]
    public partial string ScanStatus { get; set; } = "Looking through your folders…";

    [ObservableProperty]
    public partial string? Error { get; set; }

    [ObservableProperty]
    public partial string Summary { get; set; } = "";

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ApplyCommand))]
    public partial bool HasChanges { get; set; }

    [ObservableProperty]
    public partial bool IsUpToDate { get; set; }

    [ObservableProperty]
    public partial string? MassWarning { get; set; }

    [ObservableProperty]
    public partial PreviewCategory? SelectedCategory { get; set; }

    public ObservableCollection<PreviewCategory> Categories { get; } = [];

    public async Task LoadAsync()
    {
        _loading = true;
        try
        {
            _session = await page.OpenSessionAsync();
            var progress = new Progress<SyncProgress>(p => ScanStatus = p.Phase == "Scanning"
                ? $"Looking through your folders… {p.FilesDone:N0} files so far"
                : "Checking for files that were moved or renamed…");
            _plan = await _session.PlanSyncAsync(progress, _cancel.Token);
            if (!_closed)
                Build(_plan);
        }
        catch (OperationCanceledException)
        {
            // The user left this screen while it was still looking through the folders.
        }
        catch (DriveSyncException e)
        {
            Error = e.Message;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or SqliteException)
        {
            Error = $"Couldn't read the backup: {e.Message} Check that the backup drive is still connected, then try again.";
        }
        finally
        {
            _loading = false;
            IsScanning = false;
            // Leaving the screen mid-scan, or any error, lets go of the drive here, once nothing is using it.
            if (_closed || Error is not null)
                CloseSession();
        }
    }

    void Build(SyncPlan plan)
    {
        void Add(string title, SymbolRegular icon, string note, IEnumerable<PreviewRow> rows, long bytes)
        {
            var list = rows.ToList();
            if (list.Count > 0)
                Categories.Add(new PreviewCategory(title, icon, note, list, bytes));
        }

        Add("New", SymbolRegular.DocumentAdd24, "Files that aren't on the backup yet. They'll be copied.",
            plan.NewFiles.OrderBy(f => f.Key).Select(f => new PreviewRow(f.Key, Formatting.Size(f.Size))),
            plan.NewFiles.Sum(f => f.Size));
        Add("Changed", SymbolRegular.DocumentEdit24, "Files you edited. The backup gets the new version; the old one is kept in version history.",
            plan.ChangedFiles.OrderBy(f => f.Key).Select(f => new PreviewRow(f.Key, Formatting.Size(f.Size))),
            plan.ChangedFiles.Sum(f => f.Size));
        Add("Moved or renamed", SymbolRegular.ArrowMove24, "Recognised by their contents, so they're moved on the backup instead of copied again.",
            plan.Moves.OrderBy(m => m.To.Key).Select(m => new PreviewRow(m.To.Key, "was " + m.From.Key)), 0);
        Add("Back on this PC", SymbolRegular.ArrowUndo24, "Files that were offloaded or missing and are back, unchanged. Nothing needs copying.",
            plan.BackOnPc.OrderBy(r => r.Key).Select(r => new PreviewRow(r.Key, Formatting.Size(r.Size))), 0);
        Add("Offloaded elsewhere", SymbolRegular.Archive24, "You offloaded these through another backup. They're kept on this backup too, marked as offloaded.",
            plan.OffloadedElsewhere.OrderBy(r => r.Key).Select(r => new PreviewRow(r.Key, Formatting.Size(r.Size))), 0);
        Add("Missing from this PC", SymbolRegular.DocumentQuestionMark24, MissingNote(plan, page.Profile.MissingFilePolicy),
            plan.Missing.OrderBy(m => m.Record.Key).Select(m => new PreviewRow(m.Record.Key, m.InRecycleBin ? "In the Recycle Bin" : "")),
            plan.Missing.Sum(m => m.Record.Size));

        SelectedCategory = Categories.FirstOrDefault();
        HasChanges = plan.HasChanges;
        IsUpToDate = !plan.HasChanges;

        int toCopy = plan.NewFiles.Count + plan.ChangedFiles.Count;
        var parts = new List<string>();
        if (toCopy > 0) parts.Add($"{Formatting.Files(toCopy)} to copy ({Formatting.Size(plan.BytesToCopy)})");
        if (plan.Moves.Count > 0) parts.Add($"{plan.Moves.Count:N0} moved");
        if (plan.BackOnPc.Count > 0) parts.Add($"{plan.BackOnPc.Count:N0} back on this PC");
        if (plan.Missing.Count > 0) parts.Add($"{plan.Missing.Count:N0} missing");
        Summary = parts.Count > 0
            ? string.Join(" · ", parts) + $". {plan.UnchangedCount:N0} files are unchanged."
            : $"All {plan.UnchangedCount:N0} files match the backup.";

        if (plan.MassDisappearance)
        {
            int vanished = plan.Missing.Count(m => m.Record.State == Core.Model.FileState.Active);
            MassWarning = $"{vanished:N0} files vanished from this PC at once. That usually means a folder was moved or disconnected, "
                + "a cloud-sync problem, or ransomware. Nothing will be removed from the backup; these files will wait for your decision.";
        }
        foreach (string warning in plan.Scan.Warnings.Take(3))
            MassWarning = MassWarning is null ? warning : MassWarning + "\n" + warning;
    }

    /// <summary>What the sync will do with the missing files, which depends on this backup's rule.</summary>
    internal static string MissingNote(SyncPlan plan, MissingFilePolicy policy)
    {
        if (plan.MassDisappearance)
            return "These stay on the backup. So many vanished at once that nothing is removed; they wait for your decision.";
        return policy switch
        {
            MissingFilePolicy.AlwaysRemove =>
                "This backup's rule is to remove missing files: when you sync, these copies leave the backup (kept in its trash folder or Recycle Bin, so they can be restored). Change the rule in Edit backup if that isn't what you want.",
            MissingFilePolicy.AlwaysKeep =>
                "These stay on the backup. This backup's rule marks missing files as offloaded.",
            MissingFilePolicy.RecycleBinMeansDeleted when plan.Missing.Any(m => m.InRecycleBin) =>
                "This backup's rule treats files in this PC's Recycle Bin as deleted: when you sync, those copies leave the backup (kept in its trash folder or Recycle Bin, so they can be restored). DriveSync asks about the rest.",
            _ => "These stay on the backup. After the sync, DriveSync asks whether you deleted them on purpose or removed them to free space.",
        };
    }

    [RelayCommand(CanExecute = nameof(HasChanges))]
    void Apply()
    {
        if (_session is null || _plan is null || _closed)
            return;
        var session = _session;
        _session = null; // the dashboard owns it now
        page.ApplyPreview(session, _plan);
    }

    [RelayCommand]
    void Back() => page.ShowHome();

    /// <summary>
    /// Called when the page moves away from this screen. A scan still running is cancelled and the drive is
    /// let go once it has stopped; closing the records under a running scan would make it fail.
    /// </summary>
    public void Dispose()
    {
        if (_closed)
            return;
        _closed = true;
        _cancel.Cancel();
        if (!_loading)
            CloseSession();
    }

    void CloseSession()
    {
        var session = _session;
        _session = null;
        try
        {
            session?.Dispose();
        }
        catch (Exception e) when (e is IOException or SqliteException)
        {
            // The drive went away; there's nothing left to close.
        }
    }
}
