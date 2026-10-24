using Kyuplan.Client.LinuxApp.Kit;
using Kyuplan.Client.Resources;
using Kyuplan.Client.ViewModels;

namespace Kyuplan.Client.LinuxApp.Pages;

/// <summary>How the synchronisation with the data source is doing: when it last ran, what failed, and the recent runs.</summary>
internal static class SyncStatusPage
{
    public static Gtk.Widget Build(UiContext context, SyncStatusViewModel page)
    {
        var confirm = Ui.VBox(8,
            Ui.Label(Strings.SyncStatus_ConfirmQuestion).Start(),
            Ui.HBox(8, Ui.Button(Strings.SyncStatus_ConfirmYes, page.ConfirmRemovalCommand, null, "destructive-action"), Ui.Button(Strings.SyncStatus_ConfirmCancel, page.CancelRemovalCommand)))
            .Css("banner-problem").VisibleWhen(page, nameof(SyncStatusViewModel.IsConfirmingRemoval));

        var refused = Ui.VBox(8,
            Ui.Label(Strings.SyncStatus_Refused, "warn-text").Start(),
            Ui.Button(Strings.SyncStatus_Confirm, page.AskRemovalCommand).Start().HiddenWhen(page, nameof(SyncStatusViewModel.IsConfirmingRemoval)),
            confirm).VisibleWhen(page, nameof(SyncStatusViewModel.CanConfirmRemoval));

        var runs = Ui.Repeat(page, () => page.Runs, run => Ui.Label(run.Text).Tap(l => { if (!run.Succeeded) l.AddCssClass("bad-text"); }),
            [nameof(SyncStatusViewModel.Status)], 6);

        var summary = Widgets.Section(Strings.Nav_SyncStatus,
            Ui.Label(page, nameof(SyncStatusViewModel.Headline), "heading").Start(),
            Ui.Label(page, nameof(SyncStatusViewModel.IntervalText), "dim-label"),
            Ui.Label(page, nameof(SyncStatusViewModel.LastSuccessText)),
            Ui.Label(page, nameof(SyncStatusViewModel.LastFailureText), "bad-text").VisibleWhen(page, nameof(SyncStatusViewModel.IsFailing)));

        var body = Ui.VBox(14,
            Widgets.Header(page.Title, Ui.Muted(Strings.SyncStatus_Lede), Ui.Button(Strings.SyncStatus_Refresh, page.RetryCommand)),
            Widgets.ErrorBanner(page, page.RetryCommand),
            Ui.Label(page, nameof(SyncStatusViewModel.NoticeText), "accent").VisibleWhen(page, nameof(SyncStatusViewModel.HasNotice)),
            refused,
            summary.VisibleWhen(page, nameof(SyncStatusViewModel.HasLoaded)),
            Widgets.Section(Strings.SyncStatus_RecentTitle,
                Ui.Muted(Strings.SyncStatus_RecentEmpty).HiddenWhen(page, nameof(SyncStatusViewModel.HasRuns)), runs).VisibleWhen(page, nameof(SyncStatusViewModel.HasLoaded)));
        return Widgets.PageFrame(page, body, 900);
    }
}
