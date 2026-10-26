using Kyuplan.Client.LinuxApp.Kit;
using Kyuplan.Client.Resources;
using Kyuplan.Client.ViewModels;

namespace Kyuplan.Client.LinuxApp.Pages;

/// <summary>A page a plugin added: the plugin describes it in JSON, <see cref="PluginView"/> draws it.</summary>
internal static class PluginPage
{
    public static Gtk.Widget Build(UiContext context, PluginPageViewModel page)
    {
        var confirm = Ui.HBox(12,
            Ui.Label(page, nameof(PluginPageViewModel.Confirmation)).Expand(),
            Ui.Button(Strings.Plugin_Confirm, page.ConfirmCommand, null, "destructive-action"),
            Ui.Button(Strings.Plugin_Cancel, page.CancelConfirmationCommand))
            .Css("banner-problem").VisibleWhen(page, nameof(PluginPageViewModel.IsConfirming));

        var body = Ui.VBox(14,
            Widgets.Header(page.Title),
            Widgets.ErrorBanner(page, page.RetryCommand),
            Ui.Label(page, nameof(PluginPageViewModel.Notice), "accent").VisibleWhen(page, nameof(PluginPageViewModel.HasNotice)),
            confirm,
            PluginView.Build(page.Items).EnabledWhen(page, () => !page.IsBusy, nameof(PluginPageViewModel.IsBusy)));
        return Widgets.PageFrame(page, body, 900);
    }
}
