using System.Windows;
using System.Windows.Threading;
using DriveSync.App.Services;
using DriveSync.App.ViewModels;
using DriveSync.App.Views;
using DriveSync.Core;
using DriveSync.Core.Model;
using Microsoft.Win32;
using Wpf.Ui.Appearance;
using Wpf.Ui.Controls;

namespace DriveSync.App;

public partial class App : Application
{
    AppServices _services = null!;
    Mutex? _singleInstance;

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        DispatcherUnhandledException += OnUnhandledException;
        _services = AppServices.FromEnvironment();
        ApplicationThemeManager.Apply(SystemUsesLightTheme() ? ApplicationTheme.Light : ApplicationTheme.Dark, WindowBackdropType.Mica, true);

        if (DevTools.TryGetCommand(e.Args, out var command, out string argument))
        {
            int code = await DevTools.RunAsync(_services, command, argument);
            Shutdown(code);
            return;
        }

        // Two copies of the app would overwrite each other's list of backups.
        _singleInstance = new Mutex(initiallyOwned: true, "DriveSync.App." + Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
            System.Text.Encoding.UTF8.GetBytes(Path.GetFullPath(_services.Store.Folder).ToUpperInvariant())))[..16], out bool first);
        if (!first)
        {
            System.Windows.MessageBox.Show("DriveSync is already running. Look for it on the taskbar.", "DriveSync",
                System.Windows.MessageBoxButton.OK, MessageBoxImage.Information);
            Shutdown();
            return;
        }

        try
        {
            _services.Load();
        }
        catch (Exception ex) when (ex is System.Text.Json.JsonException or IOException or UnauthorizedAccessException)
        {
            // Starting with an empty list would overwrite the file on the next change, so stop instead.
            System.Windows.MessageBox.Show(
                $"DriveSync couldn't read its list of backups:\n{_services.Store.FilePath}\n\n{ex.Message}\n\n"
                + "Your backup drives aren't affected. Fix or rename that file, then start DriveSync again; "
                + "Add backup reconnects a drive that already holds a backup.",
                "DriveSync", System.Windows.MessageBoxButton.OK, MessageBoxImage.Warning);
            Shutdown(1);
            return;
        }
        if (_services.Backups.Count == 0)
            ShowFirstSetup();
        else
            ShowMain(firstBackup: null);
    }

    /// <summary>The welcome wizard on a PC with no backups yet.</summary>
    void ShowFirstSetup()
    {
        var viewModel = new SetupWizardViewModel(_services, KnownFolders.Suggestions());
        var window = new SetupWindow(viewModel);
        BackupProfile? created = null;
        viewModel.Completed += profile =>
        {
            created = profile;
            window.Close();
        };
        window.Closed += (_, _) =>
        {
            viewModel.Dispose();
            if (created is null)
                Shutdown();
            else
                ShowMain(firstBackup: created);
        };
        window.Show();
    }

    void ShowMain(BackupProfile? firstBackup)
    {
        var viewModel = new MainViewModel(_services);
        var window = new MainWindow(viewModel);
        MainWindow = window;
        window.Closed += (_, _) => Shutdown();
        viewModel.AddBackupRequested += () => AddBackup(window, viewModel);
        window.Show();

        var page = viewModel.Backups.FirstOrDefault(p => ReferenceEquals(p.Profile, firstBackup));
        if (page is not null)
        {
            viewModel.Select(page);
            _ = viewModel.RunFirstBackupAsync(page);
        }
        else
        {
            _ = viewModel.Overview.RefreshAsync();
        }
    }

    /// <summary>"Add backup": the same wizard, over the main window. Other backups keep running.</summary>
    void AddBackup(Window owner, MainViewModel main)
    {
        var viewModel = new SetupWizardViewModel(_services, KnownFolders.Suggestions());
        var window = new SetupWindow(viewModel) { Owner = owner, WindowStartupLocation = WindowStartupLocation.CenterOwner };
        BackupProfile? created = null;
        viewModel.Completed += profile =>
        {
            created = profile;
            window.Close();
        };
        window.Closed += (_, _) =>
        {
            viewModel.Dispose();
            if (created is not null)
                main.OnBackupAdded(created, runFirstBackup: true);
        };
        window.Show();
    }

    static bool SystemUsesLightTheme()
    {
        using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
        return key?.GetValue("AppsUseLightTheme") is not int value || value != 0;
    }

    void OnUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        e.Handled = true;
        System.Windows.MessageBox.Show(
            $"Something went wrong: {e.Exception.Message}\n\nDriveSync never deletes your files, so nothing is lost. Close this message to keep going.",
            "DriveSync", System.Windows.MessageBoxButton.OK, MessageBoxImage.Warning);
    }
}
