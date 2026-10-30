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

        _services.Load();
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
            _ = page.Dashboard.SyncWithoutReviewAsync();
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
