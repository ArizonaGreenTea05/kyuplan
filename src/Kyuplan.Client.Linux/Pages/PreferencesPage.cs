using Kyuplan.Client.LinuxApp.Kit;
using Kyuplan.Client.Localization;
using Kyuplan.Client.Resources;
using Kyuplan.Client.ViewModels;

namespace Kyuplan.Client.LinuxApp.Pages;

internal static class PreferencesPage
{
    public static Gtk.Widget Build(UiContext context, PreferencesViewModel page)
    {
        var themes = page.Themes;

        // The user's own theme, kept with the account; the first entry follows the administrators' default. Light or dark follows the desktop.
        var theme = Ui.ComboRow(themes, Strings.Shell_Design, () => themes.Choices, c => c.Name, () => themes.Selected,
            c => _ = themes.ChooseAsync(c.Id), nameof(ThemeViewModel.Choices), nameof(ThemeViewModel.Selected));
        theme.Subtitle = GLib.Functions.MarkupEscapeText(Strings.Shell_DesignHint, -1);

        // The user's own language, kept with the account; the open window switches at once.
        var language = Ui.ComboRow(page, Strings.Preferences_LanguageTitle, () => page.Languages, l => l.Name, () => page.SelectedLanguage,
            l => _ = page.ChooseLanguageAsync(l), nameof(PreferencesViewModel.SelectedLanguage));
        language.Subtitle = GLib.Functions.MarkupEscapeText(Strings.Shell_LanguageHint, -1);

        var body = Ui.VBox(14,
            Widgets.Header(page.Title, Ui.Muted(Strings.Preferences_Lede)),
            Widgets.ErrorBanner(page),
            Ui.Label(themes, nameof(ThemeViewModel.ErrorMessage), "error").VisibleWhen(themes, () => !string.IsNullOrEmpty(themes.ErrorMessage), nameof(ThemeViewModel.ErrorMessage)),
            Ui.Group(null, theme, language));
        return Widgets.PageFrame(page, body, 760);
    }
}
