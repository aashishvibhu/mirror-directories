using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DriveSync.Core;
using DriveSync.Core.Engine;
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
public sealed partial class PreviewViewModel(MainViewModel main) : ObservableObject, IDisposable
{
    DriveSyncSession? _session;
    SyncPlan? _plan;

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
        try
        {
            _session = await main.Services.OpenSessionAsync();
            var progress = new Progress<SyncProgress>(p => ScanStatus = p.Phase == "Scanning"
                ? $"Looking through your folders… {p.FilesDone:N0} files so far"
                : "Checking for files that were moved or renamed…");
            _plan = await _session.PlanSyncAsync(progress);
            Build(_plan);
        }
        catch (DriveSyncException e)
        {
            Error = e.Message;
            Dispose();
        }
        finally
        {
            IsScanning = false;
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
        Add("Missing from this PC", SymbolRegular.DocumentQuestionMark24,
            "These stay on the backup. After the sync, DriveSync asks whether you deleted them on purpose or removed them to free space.",
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

    [RelayCommand(CanExecute = nameof(HasChanges))]
    void Apply()
    {
        if (_session is null || _plan is null)
            return;
        var session = _session;
        _session = null; // the dashboard owns it now
        main.ApplyPreview(session, _plan);
    }

    [RelayCommand]
    void Back() => main.ShowHome();

    public void Dispose()
    {
        _session?.Dispose();
        _session = null;
    }
}
