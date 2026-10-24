using Kyuplan.Client.LinuxApp.Kit;
using Kyuplan.Client.LinuxApp.Pages.Parts;
using Kyuplan.Client.Resources;
using Kyuplan.Client.ViewModels;

namespace Kyuplan.Client.LinuxApp.Pages;

/// <summary>The settings of the API (databases, data source, statistics defaults, sync, security). Saving restarts the API, which the page waits for.</summary>
internal static class SettingsPage
{
    public static Gtk.Widget Build(UiContext context, SettingsViewModel page)
    {
        var form = SettingsFormView.Build(page.Form).EnabledWhen(page, () => page.CanEdit && !page.IsRestarting, nameof(SettingsViewModel.CanEdit), nameof(SettingsViewModel.IsRestarting))
            .VisibleWhen(page, nameof(SettingsViewModel.IsLoaded));

        var body = Ui.VBox(14,
            Widgets.Header(page.Title, Ui.Muted(Strings.Settings_Lede)),
            Widgets.ErrorBanner(page, page.RetryCommand),
            Ui.Label(page, nameof(SettingsViewModel.NoticeText), "accent").VisibleWhen(page, nameof(SettingsViewModel.HasNotice)),
            Ui.Label(Strings.Setup_Restarting, "accent").VisibleWhen(page, nameof(SettingsViewModel.IsRestarting)),
            form,
            Ui.Primary(Strings.Settings_Save, page.SaveCommand).Start().VisibleWhen(page, nameof(SettingsViewModel.CanEdit)));
        return Widgets.PageFrame(page, body, 900);
    }
}
