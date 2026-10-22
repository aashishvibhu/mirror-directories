using System.Windows;
using System.Windows.Threading;
using DriveSync.App.Services;
using DriveSync.App.ViewModels;
using DriveSync.App.Views;
using DriveSync.Core;
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

        if (_services.LoadProfile() is null)
            ShowSetup();
        else
            ShowMain(firstBackup: false);
    }

    void ShowSetup()
    {
        var viewModel = new SetupWizardViewModel(_services, KnownFolders.Suggestions());
        var window = new SetupWindow(viewModel);
        bool finished = false;
        viewModel.Completed += () =>
        {
            finished = true;
            ShowMain(firstBackup: true);
            window.Close();
        };
        window.Closed += (_, _) =>
        {
            viewModel.Dispose();
            if (!finished)
                Shutdown();
        };
        window.Show();
    }

    void ShowMain(bool firstBackup)
    {
        var viewModel = new MainViewModel(_services);
        var window = new MainWindow(viewModel);
        MainWindow = window;
        window.Closed += (_, _) => Shutdown();
        window.Show();
        if (firstBackup)
            _ = viewModel.Dashboard.RunFirstBackupAsync();
        else
            _ = viewModel.Dashboard.RefreshAsync();
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
