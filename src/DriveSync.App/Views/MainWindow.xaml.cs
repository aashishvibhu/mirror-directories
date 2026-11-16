using System.ComponentModel;
using System.Windows.Interop;
using System.Windows.Threading;
using DriveSync.App.ViewModels;
using Wpf.Ui.Appearance;
using Wpf.Ui.Controls;

namespace DriveSync.App.Views;

public partial class MainWindow : FluentWindow
{
    const int WM_DEVICECHANGE = 0x0219;

    // Windows sends several device messages for one drive; wait for them to settle before looking.
    readonly DispatcherTimer _driveCheck = new() { Interval = TimeSpan.FromSeconds(1.5) };

    public MainWindow(MainViewModel viewModel)
    {
        DataContext = viewModel;
        InitializeComponent();
        Loaded += (_, _) =>
        {
            if (WindowBackdropType != WindowBackdropType.None)
                SystemThemeWatcher.Watch(this, WindowBackdropType, updateAccents: true);
        };

        _driveCheck.Tick += (_, _) =>
        {
            _driveCheck.Stop();
            _ = ViewModel.CheckDrivesAsync();
        };
        SourceInitialized += (_, _) => (System.Windows.PresentationSource.FromVisual(this) as HwndSource)?.AddHook(OnWindowMessage);
        Activated += (_, _) => ScheduleDriveCheck();
        Closed += (_, _) => _driveCheck.Stop();
    }

    public MainViewModel ViewModel => (MainViewModel)DataContext;

    /// <summary>A backup drive plugged in or pulled out shows up without the user having to click anything.</summary>
    IntPtr OnWindowMessage(IntPtr hwnd, int message, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (message == WM_DEVICECHANGE)
            ScheduleDriveCheck();
        return IntPtr.Zero;
    }

    void ScheduleDriveCheck()
    {
        _driveCheck.Stop();
        _driveCheck.Start();
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        if (ViewModel.IsAnySyncing)
        {
            var answer = System.Windows.MessageBox.Show(this,
                "A backup is still copying files. If you close DriveSync now, the sync stops.\n\n"
                + "Everything copied so far stays on the backup, and the next sync picks up where this one left off.\n\nClose anyway?",
                "DriveSync", System.Windows.MessageBoxButton.YesNo, System.Windows.MessageBoxImage.Warning, System.Windows.MessageBoxResult.No);
            if (answer != System.Windows.MessageBoxResult.Yes)
                e.Cancel = true;
        }
        base.OnClosing(e);
    }
}
