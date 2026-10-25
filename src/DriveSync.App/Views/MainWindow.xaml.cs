using DriveSync.App.ViewModels;
using Wpf.Ui.Appearance;
using Wpf.Ui.Controls;

namespace DriveSync.App.Views;

public partial class MainWindow : FluentWindow
{
    public MainWindow(MainViewModel viewModel)
    {
        DataContext = viewModel;
        InitializeComponent();
        Loaded += (_, _) =>
        {
            if (WindowBackdropType != WindowBackdropType.None)
                SystemThemeWatcher.Watch(this, WindowBackdropType, updateAccents: true);
        };
    }

    public MainViewModel ViewModel => (MainViewModel)DataContext;
}
