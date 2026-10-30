using System.Windows;
using System.Windows.Controls;
using DriveSync.App.ViewModels;
using Microsoft.Win32;

namespace DriveSync.App.Views;

public partial class EditBackupView : UserControl
{
    public EditBackupView()
    {
        InitializeComponent();
        DataContextChanged += (_, e) =>
        {
            if (e.NewValue is EditBackupViewModel model)
                model.PickFolder = PickFolder;
        };
    }

    string? PickFolder()
    {
        var dialog = new OpenFolderDialog { Title = "Choose a folder" };
        return dialog.ShowDialog(Window.GetWindow(this)) == true ? dialog.FolderName : null;
    }
}
