using Kyuplan.Client.LinuxApp.Kit;
using Kyuplan.Client.Resources;
using Kyuplan.Client.ViewModels;

namespace Kyuplan.Client.LinuxApp.Pages;

/// <summary>The plugins: the installed ones, and installing one from a public GitHub repository. A change restarts the API, which the page waits for.</summary>
internal static class PluginsPage
{
    public static Gtk.Widget Build(UiContext context, PluginsViewModel page)
    {
        var install = Widgets.Section(Strings.Plugins_InstallTitle,
            Ui.Label(Strings.Plugins_Warning, "warn-text").Start(),
            Ui.Field(Strings.Plugins_Url, Ui.Entry(page, nameof(PluginsViewModel.Url), name: Strings.Plugins_Url), Strings.Plugins_UrlHint),
            Ui.Label(page, () => page.UrlProblem, [nameof(PluginsViewModel.UrlProblem)], "error", "caption").VisibleWhen(page, () => page.UrlProblem is not null, nameof(PluginsViewModel.UrlProblem)),
            Ui.Primary(Strings.Plugins_Install, page.InstallCommand).Start())
            .VisibleWhen(page, () => page.CanEdit, nameof(PluginsViewModel.CanEdit));

        var installed = Widgets.Section(Strings.Plugins_InstalledTitle,
            Ui.Muted(Strings.Plugins_None).VisibleWhen(page, () => !page.HasPlugins, nameof(PluginsViewModel.HasPlugins)),
            Ui.Repeat(page, () => page.Plugins, row => Row(page, row), [nameof(PluginsViewModel.Plugins)], 8));

        var body = Ui.VBox(14,
            Widgets.Header(page.Title, Ui.Muted(Strings.Plugins_Lede)),
            Widgets.ErrorBanner(page, page.RetryCommand),
            Ui.Label(page, nameof(PluginsViewModel.NoticeText), "accent").VisibleWhen(page, nameof(PluginsViewModel.HasNotice)),
            Widgets.ConfirmBar(page, nameof(PluginsViewModel.IsConfirmingRemoval), nameof(PluginsViewModel.RemovalQuestion), page.RemoveCommand, Strings.Plugins_RemoveYes,
                page.CancelRemovalCommand, Strings.Plugin_Cancel),
            install,
            installed);
        return Widgets.PageFrame(page, body, 900);
    }

    private static Gtk.Widget Row(PluginsViewModel page, PluginRow row)
    {
        var actions = Ui.HBox(8);
        if (row.CanUpdate) actions.Append(Ui.Button(Strings.Plugins_Update, page.UpdateCommand, row));
        actions.Append(Ui.Button(Strings.Plugins_Remove, page.AskRemovalCommand, row));

        return Ui.VBox(4,
            Ui.Label(row.Name, "heading").Start(),
            Ui.Muted(row.Details).Css("caption"),
            Ui.Label(row.Description).Start().VisibleWhen(page, () => !string.IsNullOrEmpty(row.Description), nameof(PluginsViewModel.Plugins)),
            Ui.Muted(row.Offers).Css("caption").VisibleWhen(page, () => row.HasOffers, nameof(PluginsViewModel.Plugins)),
            Ui.Label(row.StatusText, row.HasProblem ? "error" : "dim-label").Start(),
            actions.Start().VisibleWhen(page, () => page.CanEdit, nameof(PluginsViewModel.CanEdit)))
            .Css("card").Padded(10);
    }
}
