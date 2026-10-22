using DriveSync.App.ViewModels;
using Microsoft.Win32;
using Wpf.Ui.Appearance;
using Wpf.Ui.Controls;

namespace DriveSync.App.Views;

public partial class SetupWindow : FluentWindow
{
    public SetupWindow(SetupWizardViewModel viewModel)
    {
        DataContext = viewModel;
        viewModel.PickFolder = PickFolder;
        InitializeComponent();
        Loaded += (_, _) =>
        {
            if (WindowBackdropType != WindowBackdropType.None)
                SystemThemeWatcher.Watch(this, WindowBackdropType, updateAccents: true);
        };
    }

    public SetupWizardViewModel ViewModel => (SetupWizardViewModel)DataContext;

    string? PickFolder()
    {
        var dialog = new OpenFolderDialog { Title = "Choose a folder" };
        return dialog.ShowDialog(this) == true ? dialog.FolderName : null;
    }
}
