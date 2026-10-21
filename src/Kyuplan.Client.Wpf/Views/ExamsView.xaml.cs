using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Microsoft.Win32;
using Kyuplan.Client.Api;
using Kyuplan.Client.Resources;
using Kyuplan.Client.ViewModels;

namespace Kyuplan.Client.WpfApp.Views;

public partial class ExamsView : UserControl
{
    public ExamsView() => InitializeComponent();

    private ExamsViewModel? ViewModel => DataContext as ExamsViewModel;

    // The page is created anew every time it is shown, but its view model lives on.
    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (ViewModel is { } viewModel) viewModel.Detail.ExportReady += OnExportReady;
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        if (ViewModel is { } viewModel) viewModel.Detail.ExportReady -= OnExportReady;
    }

    private void OnExportReady(object? sender, BackupFile file)
    {
        var excel = file.FileName.EndsWith(".xlsx", StringComparison.OrdinalIgnoreCase);
        var dialog = new SaveFileDialog
        {
            Title = Strings.Export_Title,
            FileName = file.FileName,
            Filter = excel ? $"{Strings.Export_FileExcel}|*.xlsx" : $"{Strings.Export_FileCsv}|*.csv",
            DefaultExt = excel ? ".xlsx" : ".csv",
        };
        if (dialog.ShowDialog(Window.GetWindow(this)) != true) return;

        try
        {
            File.WriteAllBytes(dialog.FileName, file.Content);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            MessageBox.Show(Window.GetWindow(this), ex.Message, Strings.Export_Title, MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    /// <summary>
    /// The tables and the lists that sit in this page have scroll viewers of their own (they scroll sideways when the window is narrow,
    /// and a list box has one by nature). A scroll viewer takes every turn of the mouse wheel for itself even when it has nothing to
    /// scroll up or down, so the wheel would stop working while the pointer is over them. They hand it on to the page instead.
    /// </summary>
    private void OnInnerWheel(object sender, MouseWheelEventArgs e)
    {
        if (e.Handled) return;

        e.Handled = true;
        PageScroller.RaiseEvent(new MouseWheelEventArgs(e.MouseDevice, e.Timestamp, e.Delta)
        {
            RoutedEvent = MouseWheelEvent,
            Source = sender,
        });
    }
}
