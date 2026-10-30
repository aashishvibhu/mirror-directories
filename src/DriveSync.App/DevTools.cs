using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using DriveSync.App.Services;
using DriveSync.App.ViewModels;
using DriveSync.App.Views;
using DriveSync.Core;
using Wpf.Ui.Appearance;
using Wpf.Ui.Controls;

namespace DriveSync.App;

/// <summary>
/// Developer commands for trying the app without touching real files:
/// <c>--dev-demo &lt;folder&gt;</c> builds sample folders, three backups and some history inside that folder;
/// <c>--dev-screenshots &lt;folder&gt;</c> renders every screen, in light and dark, to PNG files, then drives the
/// main flows (stop backing up a folder, move a backup, sync all, remove a backup) on the demo data.
/// Both refuse to run unless DRIVESYNC_DATA_DIR and DRIVESYNC_RECYCLE_BIN_DIR are set, so the
/// real settings and the real Recycle Bin are never used.
/// </summary>
internal static class DevTools
{
    public enum Command { Demo, Screenshots }

    const string DemoMarker = ".drivesync-demo";

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

    static async Task<int> BuildDemoAsync(AppServices services, string root)
    {
        if (Directory.Exists(root) && Directory.EnumerateFileSystemEntries(root).Any() && !File.Exists(Path.Combine(root, DemoMarker)))
            throw new InvalidOperationException($"{root} isn't empty and isn't a DriveSync demo folder, so it was left alone.");
        foreach (string sub in new[] { "pc", "drive", "drive-e", "drive-f", "drive-f-unplugged", "drive-g" })
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
            Write($@"Pictures\2024\IMG_{i:D4}.jpg", 900 + i * 37);
        for (int i = 1; i <= 3; i++)
            Write($@"Videos\Clip {i}.mp4", 3000 + i * 400);

        string driveE = Path.Combine(root, "drive-e"), driveF = Path.Combine(root, "drive-f");
        Directory.CreateDirectory(driveE);
        Directory.CreateDirectory(driveF);
        Directory.CreateDirectory(Path.Combine(root, "drive-g"));
        services.Backups.Clear();
        services.Save();

        var personal = BackupSetup.CreateProfile(driveE, [Path.Combine(pc, "Desktop"), Path.Combine(pc, "Documents")], "Personal files", services.Backups);
        services.Add(personal);
        var copy = BackupSetup.CreateProfile(driveF, [Path.Combine(pc, "Desktop"), Path.Combine(pc, "Documents")], "Personal files (copy 2)", services.Backups);
        services.Add(copy);
        var photos = BackupSetup.CreateProfile(driveE, [Path.Combine(pc, "Pictures"), Path.Combine(pc, "Videos")], "Photos archive", services.Backups);
        services.Add(photos);
        foreach (var backup in services.Backups)
        {
            using var session = services.OpenSession(backup);
            await session.SyncAsync();
        }
        using (var session = services.OpenSession(photos))
            await session.OffloadAsync([Path.Combine(pc, "Videos")]);

        // Some files leave the PC by hand: a folder deleted in Explorer, scans moved off somewhere.
        services.RecycleBin.Recycle([Path.Combine(pc, @"Desktop\Old Projects")]);
        foreach (string scan in Directory.GetFiles(Path.Combine(pc, @"Documents\Scans")))
            File.Delete(scan);
        using (var session = services.OpenSession(personal))
            await session.SyncAsync();

        // Ordinary work since the last sync, for the review screen.
        Write(@"Desktop\Projects\Budget.xlsx", 72);
        for (int i = 1; i <= 5; i++)
            Write($@"Desktop\Holiday\Beach {i:D2}.jpg", 1500 + i * 120);
        Directory.CreateDirectory(Path.Combine(pc, @"Documents\Thesis\Old drafts"));
        File.Move(Path.Combine(pc, @"Documents\Thesis\Draft 2.docx"), Path.Combine(pc, @"Documents\Thesis\Old drafts\Draft 2.docx"));
        File.Delete(Path.Combine(pc, @"Desktop\Notes.txt"));

        // The second copy's drive is "unplugged".
        Directory.Move(driveF, Path.Combine(root, "drive-f-unplugged"));
        return 0;
    }

    // Screenshots

    static async Task<int> ScreenshotsAsync(AppServices services, string outDir)
    {
        services.Load();
        if (services.Backups.Count == 0)
            throw new InvalidOperationException("Run --dev-demo first.");
        Directory.CreateDirectory(outDir);
        string pc = Path.GetDirectoryName(services.Backups[0].Sources[0].Path)!;
        string demoRoot = Path.GetDirectoryName(pc)!;
        var suggestions = new[] { "Desktop", "Documents", "Pictures", "Videos" }.Select(n => new FolderSuggestion(n, Path.Combine(pc, n))).ToList();

        foreach (var theme in new[] { ApplicationTheme.Light, ApplicationTheme.Dark })
        {
            ApplicationThemeManager.Apply(theme, WindowBackdropType.None, true);
            string suffix = theme == ApplicationTheme.Light ? "light" : "dark";

            using (var wizardModel = new SetupWizardViewModel(services, suggestions))
            {
                var wizard = Offscreen(new SetupWindow(wizardModel));
                wizard.Show();
                wizardModel.Name = "Work projects";
                wizardModel.Folders[1].IsSelected = true;
                wizardModel.Folders[2].IsSelected = true;
                await Settle(1200);
                await Save(wizard, outDir, $"01-add-backup-{suffix}");
                wizardModel.NextCommand.Execute(null);
                var drive = wizardModel.Drives.FirstOrDefault(d => !d.Kind.StartsWith("This PC", StringComparison.Ordinal)) ?? wizardModel.Drives.FirstOrDefault();
                if (drive is not null)
                    drive.IsSelected = true;
                await Save(wizard, outDir, $"02-add-backup-drive-{suffix}");
                wizard.Close();
            }

            var main = new MainViewModel(services);
            var window = Offscreen(new MainWindow(main));
            window.Show();
            await main.Overview.RefreshAsync();
            await Save(window, outDir, $"03-overview-{suffix}");

            var personal = main.Backups.First(p => p.Name == "Personal files");
            var photos = main.Backups.First(p => p.Name == "Photos archive");
            photos.Dashboard.ShowSampleProgress();
            await Save(window, outDir, $"04-overview-syncing-{suffix}");
            photos.Dashboard.ClearSampleProgress();

            main.Select(personal);
            personal.ShowHome();
            await Settle(600);
            await Save(window, outDir, $"05-backup-page-{suffix}");

            await personal.SyncNowAsync();
            await Save(window, outDir, $"06-review-{suffix}");
            personal.ShowHome();

            await personal.ShowMissingAsync();
            await Save(window, outDir, $"07-missing-{suffix}");

            await personal.EditCommand.ExecuteAsync(null);
            await Save(window, outDir, $"08-edit-backup-{suffix}");
            var edit = (EditBackupViewModel)personal.Content;
            edit.Folders[1].RemoveCommand.Execute(null);
            await Save(window, outDir, $"09-stop-backing-up-{suffix}");
            edit.CancelRemoveFolderCommand.Execute(null);
            edit.OpenMoveCommand.Execute(null);
            await Save(window, outDir, $"10-move-{suffix}");
            edit.CancelMoveCommand.Execute(null);
            edit.AskRemoveBackupCommand.Execute(null);
            await Save(window, outDir, $"11-remove-backup-{suffix}");
            edit.ConfirmNoCommand.Execute(null);
            personal.ShowHome();
            window.Close();
        }

        // Run the real flows on the demo data, through the same commands the buttons use.
        ApplicationThemeManager.Apply(ApplicationTheme.Light, WindowBackdropType.None, true);
        var app = new MainViewModel(services);
        var appWindow = Offscreen(new MainWindow(app));
        appWindow.Show();
        await app.Overview.RefreshAsync();

        var work = app.Backups.First(p => p.Name == "Personal files");
        app.Select(work);
        await work.EditCommand.ExecuteAsync(null);
        var editor = (EditBackupViewModel)work.Content;
        editor.Folders.First(f => f.Name == "Documents").RemoveCommand.Execute(null);
        await editor.ConfirmRemoveFolderCommand.ExecuteAsync(null);
        await Save(appWindow, outDir, "12-after-stop-backing-up-light");
        editor.RemovedFolders.First().AddBackCommand.Execute(null);

        var archive = app.Backups.First(p => p.Name == "Photos archive");
        app.Select(archive);
        await archive.EditCommand.ExecuteAsync(null);
        var mover = (EditBackupViewModel)archive.Content;
        mover.PickFolder = () => Path.Combine(demoRoot, "drive-g");
        mover.OpenMoveCommand.Execute(null);
        mover.ChooseMoveFolderCommand.Execute(null);
        await mover.StartMoveCommand.ExecuteAsync(null);
        await Save(appWindow, outDir, "13-after-move-light");

        app.ShowOverview();
        await app.SyncAllCommand.ExecuteAsync(null);
        await app.Overview.RefreshAsync();
        await Save(appWindow, outDir, "14-overview-after-sync-all-light");

        var copy = app.Backups.First(p => p.Name == "Personal files (copy 2)");
        app.Select(copy);
        await copy.EditCommand.ExecuteAsync(null);
        var remover = (EditBackupViewModel)copy.Content;
        remover.AskRemoveBackupCommand.Execute(null);
        await remover.ConfirmYesCommand.ExecuteAsync(null);
        await app.Overview.RefreshAsync();
        await Save(appWindow, outDir, "15-overview-after-remove-light");
        appWindow.Close();
        return 0;
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
