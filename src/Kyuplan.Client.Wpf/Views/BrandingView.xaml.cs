using System.IO;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;
using Kyuplan.Client.Resources;
using Kyuplan.Client.ViewModels;
using Kyuplan.Contracts;

namespace Kyuplan.Client.WpfApp.Views;

public partial class BrandingView : UserControl
{
    public BrandingView() => InitializeComponent();

    /// <summary>The file extensions of the content types a slot takes: the browser's <c>accept</c> list, as a file dialog wants it.</summary>
    private static readonly Dictionary<string, string[]> Extensions = new()
    {
        [BrandingSlots.Png] = ["*.png"],
        [BrandingSlots.Jpeg] = ["*.jpg", "*.jpeg"],
        [BrandingSlots.Gif] = ["*.gif"],
        [BrandingSlots.Bmp] = ["*.bmp"],
        [BrandingSlots.Svg] = ["*.svg"],
        [BrandingSlots.Ico] = ["*.ico"],
    };

    /// <summary>Lets the user pick a file for the slot of the clicked row, and hands it to the view model (which refuses one that is too large without reading it, and keeps the others until they are saved).</summary>
    private async void OnUploadClick(object sender, RoutedEventArgs e)
    {
        if (DataContext is not BrandingAdminViewModel viewModel || (sender as FrameworkElement)?.DataContext is not BrandingImageRow row) return;

        var patterns = row.Accept.Split(',', StringSplitOptions.RemoveEmptyEntries)
            .SelectMany(type => Extensions.GetValueOrDefault(type, []))
            .ToArray();
        var dialog = new OpenFileDialog
        {
            Title = row.Title,
            Filter = $"{Strings.Branding_ImagesTitle}|{string.Join(';', patterns)}",
            CheckFileExists = true,
        };
        if (dialog.ShowDialog(Window.GetWindow(this)) != true) return;

        var path = dialog.FileName;
        long size;
        try
        {
            size = new FileInfo(path).Length;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            viewModel.ErrorMessage = string.Format(Strings.Error_Unexpected, ex.Message);
            return;
        }

        await viewModel.PickImageAsync(row.Slot, size, () => File.OpenRead(path));
    }
}
