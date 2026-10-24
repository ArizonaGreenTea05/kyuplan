using Kyuplan.Client.LinuxApp.Kit;
using Kyuplan.Client.Resources;
using Kyuplan.Client.ViewModels;

namespace Kyuplan.Client.LinuxApp.Pages;

internal static class DashboardPage
{
    public static Gtk.Widget Build(UiContext context, DashboardViewModel page)
    {
        var period = Ui.Choice(page, () => page.PeriodOptions, o => o.Label, () => page.PeriodOptions.FirstOrDefault(o => o.Days == page.SelectedDays),
            o => page.SelectedDays = o.Days, [nameof(DashboardViewModel.SelectedDays)], Strings.Dashboard_Period);
        period.Hexpand = false;

        var lede = Ui.VBox(0,
            Ui.Label(page, () => $"{page.GroupName} · {page.PeriodText}", [nameof(DashboardViewModel.GroupName), nameof(DashboardViewModel.PeriodText)], "dim-label")
                .VisibleWhen(page, nameof(DashboardViewModel.HasData)),
            Ui.Muted(page.Lede).HiddenWhen(page, nameof(DashboardViewModel.HasData)));

        var data = Ui.VBox(14,
            Ui.Repeat(page, () => new[] { page.Kpis }, kpis => Widgets.KpiGrid(kpis), [nameof(DashboardViewModel.Kpis)]),
            TwoColumns(Upcoming(page), Trend(page)),
            TwoColumns(Members(page, Strings.Dashboard_TopMembers, Strings.Dashboard_TopMembersEmpty, () => page.TopMembers, nameof(DashboardViewModel.TopMembers)),
                Members(page, Strings.Dashboard_NeedsAttention, Strings.Dashboard_NeedsAttentionEmpty, () => page.NeedsAttention, nameof(DashboardViewModel.NeedsAttention))))
            .VisibleWhen(page, nameof(DashboardViewModel.HasData));

        var body = Ui.VBox(14,
            Widgets.Header(page.Title, lede, Ui.HBox(10, Ui.Caption(Strings.Dashboard_Period, "dim-label"), period)),
            Widgets.ErrorBanner(page, page.RefreshCommand),
            data,
            Ui.Label(Strings.Dashboard_Empty, "dim-label").Margin(40, 0).VisibleWhen(page, nameof(DashboardViewModel.HasNoData)));
        return Widgets.PageFrame(page, body);
    }

    /// <summary>Two cards side by side on a wide window, one under the other on a narrow one.</summary>
    private static Gtk.Widget TwoColumns(Gtk.Widget left, Gtk.Widget right)
    {
        var flow = Gtk.FlowBox.New();
        flow.SelectionMode = Gtk.SelectionMode.None;
        flow.Homogeneous = true;
        flow.MaxChildrenPerLine = 2;
        flow.ColumnSpacing = flow.RowSpacing = 14;
        flow.Append(left);
        flow.Append(right);
        return flow;
    }

    private static Gtk.Widget Upcoming(DashboardViewModel page)
    {
        var list = Ui.Repeat(page, () => page.Upcoming, e => Ui.HBox(12,
            Ui.VBox(0, Ui.Label(e.Name, "heading").Start(), Ui.Muted(e.StartText)).Expand(),
            Ui.VBox(4, Widgets.StackedBar(e.AcceptedFraction, e.DeclinedFraction, e.UnansweredFraction, e.AriaText), Ui.Muted(e.SummaryText).Css("caption")).Expand()).Homogeneous(),
            [nameof(DashboardViewModel.Upcoming)], spacing: 12);
        var legend = Ui.HBox(14,
            Legend("bar-yes-swatch", Strings.Legend_Coming), Legend("bar-no-swatch", Strings.Legend_Declined), Legend("bar-open-swatch", Strings.Legend_NoAnswerYet));
        return Widgets.Section(Strings.Dashboard_Upcoming,
            Ui.Muted(Strings.Dashboard_NothingScheduled).VisibleWhen(page, () => page.Upcoming.Count == 0, nameof(DashboardViewModel.Upcoming)),
            list, legend);
    }

    private static Gtk.Widget Legend(string swatch, string text)
    {
        var square = Gtk.Box.New(Gtk.Orientation.Horizontal, 0);
        square.SetSizeRequest(12, 12);
        square.Valign = Gtk.Align.Center;
        square.Css("legend-swatch", swatch);
        return Ui.HBox(6, square, Ui.Muted(text).Css("caption"));
    }

    private static Gtk.Widget Trend(DashboardViewModel page) => Widgets.Section(Strings.Chart_TrendTitle,
        Ui.Repeat(page, () => new[] { page.Trend }, bars => Widgets.ColumnChart(bars), [nameof(DashboardViewModel.Trend)]),
        Ui.Muted(Strings.Chart_TrendNoteDashboard).Css("caption"));

    private static Gtk.Widget Members(DashboardViewModel page, string title, string empty, Func<IReadOnlyList<MemberRowViewModel>> rows, string property) =>
        Widgets.Section(title,
            Ui.Muted(empty).VisibleWhen(page, () => rows().Count == 0, property),
            Ui.Repeat(page, rows, MemberRow, [property], spacing: 10));

    /// <summary>One row of "Most engaged" / "Needs attention": name, last attendance, bar, trend and level.</summary>
    public static Gtk.Widget MemberRow(MemberRowViewModel member)
    {
        var rate = Ui.HBox(8, Widgets.RateBar(member.AttendanceFraction, null, member.AttendanceAriaText),
            Ui.Label(member.AttendanceText, "heading", "numeric"));
        rate.WidthRequest = 180;
        var level = Widgets.Pill($"{member.EngagementScore}  {member.LevelText}", LevelTone(member.Level)).Tip(Strings.Col_EngagementTip);
        var name = Ui.Label(member.Name, "heading").Start();
        name.Wrap = false;
        name.Ellipsize = Pango.EllipsizeMode.End;
        return Ui.HBox(12,
            Ui.VBox(0, name, Ui.Muted(member.LastAttendedText).Css("caption")).Expand(),
            rate,
            Ui.Label(member.TrendGlyph, "title-3").Tip(member.TrendText),
            level);
    }

    /// <summary>The colour of an engagement level's pill.</summary>
    public static string? LevelTone(Kyuplan.Contracts.EngagementLevel level) => level switch
    {
        Kyuplan.Contracts.EngagementLevel.AtRisk => "chip-bad",
        Kyuplan.Contracts.EngagementLevel.Irregular => "chip-warn",
        _ => "chip-ok",
    };
}
