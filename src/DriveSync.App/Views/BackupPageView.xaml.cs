using System.Windows;
using System.Windows.Controls;
using DriveSync.App.Services;
using DriveSync.App.ViewModels;
using Microsoft.Win32;

namespace DriveSync.App.Views;

public partial class BackupPageView : UserControl
{
    public BackupPageView()
    {
        InitializeComponent();
        DataContextChanged += (_, e) =>
        {
            if (e.NewValue is not BackupPageViewModel page)
                return;
            // The view models stay free of UI code: pickers and Explorer are handed in here.
            page.Restore.PickFolder = PickFolder;
            page.Files.ShowInExplorer = Shell.Reveal;
            page.Storage.OpenInExplorer = Shell.OpenFolder;
        };
    }

    string? PickFolder()
    {
        var dialog = new OpenFolderDialog { Title = "Choose a folder to restore to" };
        return dialog.ShowDialog(Window.GetWindow(this)) == true ? dialog.FolderName : null;
    }
}
