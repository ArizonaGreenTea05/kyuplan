using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using Microsoft.Win32;
using Kyuplan.Client.Api;
using Kyuplan.Client.Resources;
using Kyuplan.Client.ViewModels;
using Kyuplan.Contracts;

namespace Kyuplan.Client.WpfApp.Views;

public partial class BackupView : UserControl
{
    public BackupView() => InitializeComponent();

    private BackupViewModel? ViewModel => DataContext as BackupViewModel;

    // The page is created anew every time it is shown, but its view model lives on.
    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (ViewModel is not { } viewModel) return;

        viewModel.DownloadReady += OnDownloadReady;
        viewModel.PropertyChanged += OnViewModelChanged;
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        if (ViewModel is not { } viewModel) return;

        viewModel.DownloadReady -= OnDownloadReady;
        viewModel.PropertyChanged -= OnViewModelChanged;
    }

    // Opening the form puts the cursor in its first field (once the form is visible: it only becomes so after this change).
    private void OnViewModelChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(BackupViewModel.IsFormOpen) && ViewModel is { IsFormOpen: true })
            Dispatcher.BeginInvoke(DispatcherPriority.Input, () => NameBox.Focus());
    }

    private void OnDownloadReady(object? sender, BackupFile file)
    {
        var dialog = new SaveFileDialog
        {
            Title = Strings.Backup_Download,
            FileName = file.FileName,
            Filter = $"{Strings.Backup_FileTitle}|*{BackupRules.Extension}",
            DefaultExt = BackupRules.Extension,
        };
        if (dialog.ShowDialog(Window.GetWindow(this)) != true) return;

        try
        {
            File.WriteAllBytes(dialog.FileName, file.Content);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            MessageBox.Show(Window.GetWindow(this), ex.Message, Strings.Backup_Download, MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void OnChooseFile(object sender, RoutedEventArgs e)
    {
        if (ViewModel is not { } viewModel) return;

        var dialog = new OpenFileDialog
        {
            Title = Strings.Backup_ChooseFile,
            Filter = $"{Strings.Backup_FileTitle}|*{BackupRules.Extension};*.zip",
            CheckFileExists = true,
        };
        if (dialog.ShowDialog(Window.GetWindow(this)) != true) return;

        var path = dialog.FileName;
        viewModel.StageRestore(Path.GetFileName(path), () => File.OpenRead(path));
    }
}
