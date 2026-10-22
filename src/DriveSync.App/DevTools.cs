using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using DriveSync.App.Services;
using DriveSync.App.ViewModels;
using DriveSync.App.Views;
using DriveSync.Core;
using DriveSync.Core.Engine;
using Wpf.Ui.Appearance;
using Wpf.Ui.Controls;

namespace DriveSync.App;

/// <summary>
/// Developer commands for trying the app without touching real files:
/// <c>--dev-demo &lt;folder&gt;</c> builds sample folders, a backup and some history inside that folder;
/// <c>--dev-screenshots &lt;folder&gt;</c> renders every screen, in light and dark, to PNG files.
/// Both refuse to run unless DRIVESYNC_DATA_DIR and DRIVESYNC_RECYCLE_BIN_DIR are set, so the
/// real settings and the real Recycle Bin are never used.
/// </summary>
internal static class DevTools
{
    public enum Command { Demo, Screenshots }

    public static bool TryGetCommand(string[] args, out Command command, out string argument)
    {
        command = default;
        argument = "";
        if (args.Length != 2)
            return false;
        switch (args[0])
        {
            case "--dev-demo": command = Command.Demo; break;
            case "--dev-screenshots": command = Command.Screenshots; break;
            default: return false;
        }
        argument = Path.GetFullPath(args[1]);
        return true;
    }

    public static async Task<int> RunAsync(AppServices services, Command command, string folder)
    {
        if (!services.UsesStandInRecycleBin || string.IsNullOrEmpty(Environment.GetEnvironmentVariable("DRIVESYNC_DATA_DIR")))
        {
            System.Windows.MessageBox.Show("Developer commands need DRIVESYNC_DATA_DIR and DRIVESYNC_RECYCLE_BIN_DIR to be set.", "DriveSync");
            return 2;
        }
        return command == Command.Demo ? await BuildDemoAsync(services, folder) : await ScreenshotsAsync(services, folder);
    }

    // Demo data

    const string DemoMarker = ".drivesync-demo";

    static async Task<int> BuildDemoAsync(AppServices services, string root)
    {
        if (Directory.Exists(root) && Directory.EnumerateFileSystemEntries(root).Any() && !File.Exists(Path.Combine(root, DemoMarker)))
            throw new InvalidOperationException($"{root} isn't empty and isn't a DriveSync demo folder, so it was left alone.");
        foreach (string sub in new[] { "pc", "drive" })
        {
            string path = Path.Combine(root, sub);
            if (Directory.Exists(path))
                Directory.Delete(path, recursive: true); // only ever a previous demo, checked above
        }
        Directory.CreateDirectory(root);
        File.WriteAllText(Path.Combine(root, DemoMarker), "Sample data made by DriveSync --dev-demo. Safe to delete.");

        var random = new Random(2026);
        string pc = Path.Combine(root, "pc");
        void Write(string relative, int kilobytes)
        {
            string path = Path.Combine(pc, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var bytes = new byte[kilobytes * 1024];
            random.NextBytes(bytes);
            File.WriteAllBytes(path, bytes);
        }

        Write(@"Desktop\Taxes\2026 tax return.pdf", 420);
        Write(@"Desktop\Taxes\Receipts.xlsx", 96);
        Write(@"Desktop\Projects\Garden plan.docx", 210);
        Write(@"Desktop\Projects\Budget.xlsx", 64);
        Write(@"Desktop\Old Projects\Website v1\index.html", 12);
        Write(@"Desktop\Old Projects\Website v1\style.css", 8);
        Write(@"Desktop\Old Projects\Website v1\logo.png", 180);
        Write(@"Desktop\Old Projects\Poster draft.psd", 2400);
        Write(@"Desktop\Notes.txt", 2);
        Write(@"Documents\Thesis\Final.docx", 880);
        Write(@"Documents\Thesis\Draft 2.docx", 760);
        Write(@"Documents\Scans\Passport.pdf", 1300);
        Write(@"Documents\Scans\Lease 2025.pdf", 640);
        Write(@"Documents\Scans\Car insurance.pdf", 320);
        Write(@"Documents\Recipes\Lentil soup.md", 3);
        for (int i = 1; i <= 12; i++)
            Write($@"Documents\Photos 2024\IMG_{i:D4}.jpg", 900 + i * 37);

        Directory.CreateDirectory(Path.Combine(root, "drive"));
        var profile = BackupSetup.CreateProfile(Path.Combine(root, "drive"), [Path.Combine(pc, "Desktop"), Path.Combine(pc, "Documents")]);
        services.SaveProfile(profile);
        using (var session = services.OpenSession())
            await session.SyncAsync();

        // Some files leave the PC by hand: a folder deleted in Explorer, scans moved off somewhere.
        services.RecycleBin.Recycle([Path.Combine(pc, @"Desktop\Old Projects")]);
        foreach (string scan in Directory.GetFiles(Path.Combine(pc, @"Documents\Scans")))
            File.Delete(scan);
        using (var session = services.OpenSession())
            await session.SyncAsync();

        // Ordinary work since the last sync, for the review screen.
        Write(@"Desktop\Projects\Budget.xlsx", 72);
        for (int i = 1; i <= 5; i++)
            Write($@"Desktop\Holiday\Beach {i:D2}.jpg", 1500 + i * 120);
        Directory.CreateDirectory(Path.Combine(pc, @"Documents\Thesis\Old drafts"));
        File.Move(Path.Combine(pc, @"Documents\Thesis\Draft 2.docx"), Path.Combine(pc, @"Documents\Thesis\Old drafts\Draft 2.docx"));
        File.Delete(Path.Combine(pc, @"Desktop\Notes.txt"));
        return 0;
    }

    // Screenshots

    static async Task<int> ScreenshotsAsync(AppServices services, string outDir)
    {
        if (services.LoadProfile() is null)
            throw new InvalidOperationException("Run --dev-demo first.");
        Directory.CreateDirectory(outDir);
        string pc = Path.GetDirectoryName(services.Profile!.Sources[0].Path)!;
        var suggestions = services.Profile.Sources.Select(s => new FolderSuggestion(s.Name, s.Path))
            .Append(new FolderSuggestion("Pictures", Path.Combine(pc, "Pictures")))
            .ToList();
        Directory.CreateDirectory(Path.Combine(pc, "Pictures"));

        foreach (var theme in new[] { ApplicationTheme.Light, ApplicationTheme.Dark })
        {
            ApplicationThemeManager.Apply(theme, WindowBackdropType.None, true);
            string suffix = theme == ApplicationTheme.Light ? "light" : "dark";

            using (var wizardModel = new SetupWizardViewModel(services, suggestions))
            {
                var wizard = Offscreen(new SetupWindow(wizardModel));
                wizard.Show();
                wizardModel.Folders[0].IsSelected = true;
                wizardModel.Folders[1].IsSelected = true;
                await Settle(1200);
                await Save(wizard, outDir, $"1-setup-folders-{suffix}");
                wizardModel.NextCommand.Execute(null);
                var drive = wizardModel.Drives.FirstOrDefault(d => !d.Kind.StartsWith("This PC", StringComparison.Ordinal)) ?? wizardModel.Drives.FirstOrDefault();
                if (drive is not null)
                    drive.IsSelected = true;
                await Save(wizard, outDir, $"2-setup-drive-{suffix}");
                wizardModel.NextCommand.Execute(null);
                await Save(wizard, outDir, $"3-setup-policy-{suffix}");
                wizardModel.NextCommand.Execute(null);
                await Save(wizard, outDir, $"4-setup-start-{suffix}");
                wizard.Close();
            }

            var main = new MainViewModel(services);
            var window = Offscreen(new MainWindow(main));
            window.Show();
            await main.Dashboard.RefreshAsync();
            await Save(window, outDir, $"5-home-{suffix}");

            main.Dashboard.ShowSampleProgress();
            await Save(window, outDir, $"6-home-syncing-{suffix}");
            main.Dashboard.ClearSampleProgress();

            await main.ShowPreviewAsync();
            await Save(window, outDir, $"7-review-{suffix}");
            if (main.CurrentPage is PreviewViewModel preview && preview.Categories.Count > 0)
            {
                preview.SelectedCategory = preview.Categories[^1];
                await Save(window, outDir, $"8-review-missing-{suffix}");
            }
            main.ShowHome();

            await main.ShowMissingAsync();
            if (main.Missing.Groups.Count > 0)
            {
                main.Missing.Groups[0].IsExpanded = true;
                main.Missing.Groups[0].Files[0].IsSelected = true;
            }
            await Save(window, outDir, $"9-missing-{suffix}");
            if (main.Missing.Groups.Count > 0)
            {
                main.Missing.Groups[0].DeleteAllCommand.Execute(null);
                await Save(window, outDir, $"10-missing-confirm-{suffix}");
                main.Missing.CancelConfirmCommand.Execute(null);
            }
            window.Close();
        }

        // Act on the demo through the same commands the buttons use, then show the outcome.
        ApplicationThemeManager.Apply(ApplicationTheme.Light, WindowBackdropType.None, true);
        var app = new MainViewModel(services);
        var appWindow = Offscreen(new MainWindow(app));
        appWindow.Show();
        await app.ShowMissingAsync();
        var scans = app.Missing.Groups.First(g => g.Folder.EndsWith("Scans", StringComparison.Ordinal));
        scans.KeepAllCommand.Execute(null);
        await WaitUntil(() => !app.Missing.IsBusy);
        var oldProjects = app.Missing.Groups.First(g => g.Folder.EndsWith("Old Projects", StringComparison.Ordinal));
        oldProjects.DeleteAllCommand.Execute(null);
        await app.Missing.ConfirmDeleteCommand.ExecuteAsync(null);
        await Save(appWindow, outDir, "11-missing-after-decisions-light");

        await app.ShowPreviewAsync();
        ((PreviewViewModel)app.CurrentPage).ApplyCommand.Execute(null);
        await WaitUntil(() => !app.Dashboard.IsSyncing, seconds: 60);
        await Save(appWindow, outDir, "12-home-after-sync-light");
        appWindow.Close();
        return 0;
    }

    static async Task WaitUntil(Func<bool> done, int seconds = 15)
    {
        var giveUp = DateTime.UtcNow.AddSeconds(seconds);
        await Task.Delay(200);
        while (!done() && DateTime.UtcNow < giveUp)
            await Task.Delay(100);
        if (!done())
            throw new TimeoutException("The demo action didn't finish in time.");
    }

    static T Offscreen<T>(T window) where T : FluentWindow
    {
        window.WindowBackdropType = WindowBackdropType.None;
        window.WindowStartupLocation = WindowStartupLocation.Manual;
        window.Left = -32000;
        window.Top = 0;
        window.ShowActivated = false;
        window.ShowInTaskbar = false;
        return window;
    }

    static async Task Settle(int milliseconds = 400)
    {
        await Task.Delay(milliseconds);
        await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
    }

    static async Task Save(Window window, string folder, string name)
    {
        await Settle();
        var root = (FrameworkElement)window.Content;
        root.UpdateLayout();
        int width = (int)Math.Ceiling(root.ActualWidth), height = (int)Math.Ceiling(root.ActualHeight);
        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            var background = (Brush)Application.Current.FindResource("ApplicationBackgroundBrush");
            dc.DrawRectangle(background, null, new Rect(0, 0, width, height));
            dc.DrawRectangle(new VisualBrush(root), null, new Rect(0, 0, width, height));
        }
        var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        await using var file = File.Create(Path.Combine(folder, name + ".png"));
        encoder.Save(file);
    }
}
