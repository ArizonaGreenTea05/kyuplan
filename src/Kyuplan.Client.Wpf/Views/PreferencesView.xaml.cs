using System.Windows;
using System.Windows.Controls;
using Kyuplan.Client.Localization;
using Kyuplan.Client.Resources;
using Kyuplan.Client.ViewModels;

namespace Kyuplan.Client.WpfApp.Views;

public partial class PreferencesView : UserControl
{
    public PreferencesView() => InitializeComponent();

    private PreferencesViewModel? ViewModel => DataContext as PreferencesViewModel;

    private MainWindow? Main => Window.GetWindow(this) as MainWindow;

    private void OnToggleTheme(object sender, RoutedEventArgs e) => Main?.Theme.Toggle();

    // The choice is the user's own and kept with the account. A theme of the design family in use is drawn at once; a theme of another
    // family is loaded at start: the choice is remembered and the program starts again (after asking, as the pages' unsaved input is lost).
    private async void OnThemeChanged(object sender, SelectionChangedEventArgs e)
    {
        if (Main is not { } main || ViewModel is not { } viewModel) return;
        var themes = viewModel.Themes;
        if (e.AddedItems.Count != 1 || e.AddedItems[0] is not ThemeChoice picked || picked.Id == themes.SelectedId) return;

        await themes.ChooseAsync(picked.Id);
        if (themes.ErrorMessage is { } problem)
        {
            MessageBox.Show(main, problem, Strings.Shell_Design, MessageBoxButton.OK, MessageBoxImage.Warning);
            ThemeBox.SelectedItem = themes.Selected;
            return;
        }

        if (!main.Theme.Adopt(themes.Effective, themes.EffectivePack)) return;

        if (MessageBox.Show(main, Strings.Shell_DesignRestart, Strings.Shell_Design, MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes)
            ((App)Application.Current).RestartNow();
    }

    // Saved with the account; the shell then switches the language and every {loc:Loc ...} reads its text again.
    private async void OnLanguageChanged(object sender, SelectionChangedEventArgs e)
    {
        if (ViewModel is not { } viewModel || e.AddedItems.Count != 1 || e.AddedItems[0] is not LanguageOption picked
            || picked.Code == viewModel.SelectedLanguage.Code) return;

        await viewModel.ChooseLanguageAsync(picked);
        if (viewModel.HasError) LanguageBox.SelectedItem = viewModel.SelectedLanguage;
    }
}
