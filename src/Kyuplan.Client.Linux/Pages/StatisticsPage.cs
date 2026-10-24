using Kyuplan.Client.LinuxApp.Kit;
using Kyuplan.Client.LinuxApp.Pages.Parts;
using Kyuplan.Client.Resources;
using Kyuplan.Client.ViewModels;
using Kyuplan.Contracts;

namespace Kyuplan.Client.LinuxApp.Pages;

/// <summary>The attendance statistics: the filter, the saved filters, and the result as insights, members and events.</summary>
internal static class StatisticsPage
{
    public static Gtk.Widget Build(UiContext context, AttendanceStatisticsViewModel page)
    {
        var body = Ui.VBox(14,
            Widgets.Header(page.Title),
            Widgets.ErrorBanner(page),
            Filters(page),
            SavedFilters(page.SavedFilters),
            Ui.Label(Strings.Stats_NoResults, "dim-label").Margin(20, 0).VisibleWhen(page, nameof(AttendanceStatisticsViewModel.HasNoResults)),
            Result(page).VisibleWhen(page, nameof(AttendanceStatisticsViewModel.HasResults)));
        return Widgets.PageFrame(page, body, 1300);
    }

    // ---- filter ------------------------------------------------------------------------------------------------------------

    private static Gtk.Widget Filters(AttendanceStatisticsViewModel page)
    {
        Gtk.Widget Range(string text, string preset) => Ui.Button(text, () => page.SetRangeCommand.Execute(preset));
        var ranges = Ui.HBox(6,
            Ui.Caption(Strings.Stats_QuickRanges, "dim-label"),
            Range(Strings.Range_3Months, "3m"), Range(Strings.Range_6Months, "6m"), Range(Strings.Range_12Months, "12m"), Range(Strings.Range_ThisYear, "year"));

        var byDates = Ui.HBox(12,
            Ui.Field(Strings.Stats_From, DateField.Create(page, nameof(AttendanceStatisticsViewModel.From), Strings.Stats_From, Strings.Stats_From)).Expand(),
            Ui.Field(Strings.Stats_To, DateField.Create(page, nameof(AttendanceStatisticsViewModel.To), Strings.Stats_To, Strings.Stats_To)).Expand())
            .HiddenWhen(page, nameof(AttendanceStatisticsViewModel.SpanByEvents));
        var byEvents = Ui.HBox(12,
            Ui.Field(Strings.Stats_FromEvent, EventPicker.Create(page, () => page.EventOptions, () => page.FromEventId, id => page.FromEventId = id,
                [nameof(AttendanceStatisticsViewModel.EventOptions), nameof(AttendanceStatisticsViewModel.FromEventId)], Strings.Stats_FromEvent)).Expand(),
            Ui.Field(Strings.Stats_ToEvent, EventPicker.Create(page, () => page.EventOptions, () => page.ToEventId, id => page.ToEventId = id,
                [nameof(AttendanceStatisticsViewModel.EventOptions), nameof(AttendanceStatisticsViewModel.ToEventId)], Strings.Stats_ToEvent)).Expand())
            .VisibleWhen(page, nameof(AttendanceStatisticsViewModel.SpanByEvents));
        var spanSwitch = Ui.HBox(8, Ui.Switch(page, nameof(AttendanceStatisticsViewModel.SpanByEvents)), Ui.Label(page, nameof(AttendanceStatisticsViewModel.SpanHint), "dim-label", "caption").Expand());

        var thresholds = Ui.Flow(
            Ui.Field(Strings.Stats_MinAttendance, Ui.Spin(page, nameof(AttendanceStatisticsViewModel.MinAttendancePercent), 0, 100, 1, 0, Strings.Stats_MinAttendance), Strings.Stats_MinAttendanceHint),
            Ui.Field(Strings.Stats_MinInvited, Ui.Spin(page, nameof(AttendanceStatisticsViewModel.MinInvitedEvents), 0, 1000, 1, 0, Strings.Stats_MinInvited), Strings.Stats_MinInvitedHint),
            Ui.Field(Strings.Stats_MinStars, Ui.Spin(page, nameof(AttendanceStatisticsViewModel.MinAverageStars), 0, 5, 0.1, 1, Strings.Stats_MinStars), Strings.Stats_MinStarsHint),
            Ui.Field(Strings.Stats_Members, SubGroupPicker.Create(page, () => page.SubGroupOptions, () => page.SubGroupSummary,
                [nameof(AttendanceStatisticsViewModel.SubGroupOptions), nameof(AttendanceStatisticsViewModel.SubGroupSummary)], page.ClearSubGroupsCommand, Strings.Stats_Members)));

        var advanced = Ui.VBox(8,
            Ui.Flow(
                Ui.Field(Strings.Stats_NameContains, Ui.Entry(page, nameof(AttendanceStatisticsViewModel.EventNameContains), Strings.Stats_NameContainsPlaceholder, Strings.Stats_NameContains), Strings.Stats_NameContainsHint),
                Ui.Field(Strings.Stats_NameExcludes, Ui.Entry(page, nameof(AttendanceStatisticsViewModel.EventNameExcludes), Strings.Stats_NameExcludesPlaceholder, Strings.Stats_NameExcludes), Strings.Stats_NameExcludesHint)))
            .VisibleWhen(page, nameof(AttendanceStatisticsViewModel.ShowAdvanced));

        return Ui.VBox(10,
            Ui.Label(Strings.Stats_Filters, "heading").Start(),
            ranges, spanSwitch, byDates, byEvents, thresholds,
            Ui.Check(page, nameof(AttendanceStatisticsViewModel.ShowAdvanced), Strings.Stats_Advanced), advanced,
            Ui.Label(page, nameof(AttendanceStatisticsViewModel.FilterError), "error").VisibleWhen(page, () => page.FilterError is not null, nameof(AttendanceStatisticsViewModel.FilterError)),
            Ui.Button(Strings.Stats_Reset, page.ResetFiltersCommand).Start())
            .Css("card").Padded(14);
    }

    // ---- saved filters -----------------------------------------------------------------------------------------------------

    private static Gtk.Widget SavedFilters(SavedFiltersViewModel saved)
    {
        var pick = Ui.Choice(saved, () => saved.FilterChoices, f => $"{f.Name} · {f.ExamText}", () => saved.SelectedFilter, f => saved.SelectedFilter = f,
            [nameof(SavedFiltersViewModel.FilterChoices), nameof(SavedFiltersViewModel.SelectedFilter)], Strings.Saved_Pick);

        var existing = Ui.VBox(8,
            Ui.Muted(Strings.Saved_PickEmpty).HiddenWhen(saved, nameof(SavedFiltersViewModel.HasFilters)),
            Ui.HBox(8, pick,
                Ui.Button(Strings.Saved_Reload, saved.ReloadSelectedCommand).Tip(Strings.Saved_ReloadTip),
                Ui.Button(Strings.Saved_Update, saved.AskUpdateCommand).Tip(Strings.Saved_UpdateTip).VisibleWhen(saved, nameof(SavedFiltersViewModel.CanChangeFilters)),
                Ui.Button(Strings.Saved_Delete, saved.AskDeleteFilterCommand).VisibleWhen(saved, nameof(SavedFiltersViewModel.CanChangeFilters)))
                .VisibleWhen(saved, nameof(SavedFiltersViewModel.HasFilters)));

        var exam = Ui.Choice(saved, () => saved.ExamChoices, e => e.Label, () => saved.SelectedExam, e => saved.SelectedExam = e,
            [nameof(SavedFiltersViewModel.ExamChoices), nameof(SavedFiltersViewModel.SelectedExam)], Strings.Saved_Exam);
        var save = Ui.VBox(8,
            Ui.Field(Strings.Saved_Exam, exam),
            Ui.Field(Strings.Saved_ExamEvent, EventPicker.Create(saved, () => saved.EventOptions, () => saved.NewExamEventId, id => saved.NewExamEventId = id,
                [nameof(SavedFiltersViewModel.EventOptions), nameof(SavedFiltersViewModel.NewExamEventId)], Strings.Saved_ExamEvent)).VisibleWhen(saved, nameof(SavedFiltersViewModel.IsNewExam)),
            Ui.Field(Strings.Saved_FilterName, Ui.Entry(saved, nameof(SavedFiltersViewModel.NewFilterName), name: Strings.Saved_FilterName)),
            Ui.HBox(8, Ui.Primary(Strings.Saved_Save, saved.SaveCommand), Ui.Button(Strings.Saved_Cancel, saved.CancelSaveCommand),
                Ui.Button(Strings.Saved_DeleteExam, saved.AskDeleteExamCommand).VisibleWhen(saved, () => !saved.IsNewExam, nameof(SavedFiltersViewModel.IsNewExam))))
            .VisibleWhen(saved, nameof(SavedFiltersViewModel.IsSaving));

        var confirm = Widgets.ConfirmBar(saved, nameof(SavedFiltersViewModel.IsConfirming), nameof(SavedFiltersViewModel.ConfirmText), saved.ConfirmPendingCommand, Strings.Saved_ConfirmYes, saved.CancelPendingCommand, Strings.Saved_Cancel);
        var yes = (Gtk.Button)confirm.GetFirstChild()!.GetNextSibling()!;
        Bind.Watch(yes, saved, () => yes.SetLabel(saved.ConfirmYesText), nameof(SavedFiltersViewModel.PendingAction));

        return Ui.VBox(8,
            Ui.HBox(8, Ui.Label(Strings.Saved_Title, "heading").Expand(), Ui.Button(Strings.Saved_SaveAs, saved.BeginSaveCommand).VisibleWhen(saved, nameof(SavedFiltersViewModel.CanSaveFilter))),
            Ui.Label(saved, nameof(ViewModelBase.ErrorMessage), "error").VisibleWhen(saved, nameof(ViewModelBase.HasError)),
            existing, save, confirm)
            .Css("card").Padded(14).VisibleWhen(saved, nameof(SavedFiltersViewModel.CanUse));
    }

    // ---- result ------------------------------------------------------------------------------------------------------------

    private static Gtk.Widget Result(AttendanceStatisticsViewModel page)
    {
        var stack = Gtk.Stack.New();
        stack.AddTitled(Insights(page.Result), "insights", Strings.Tab_Insights);
        stack.AddTitled(Members(page), "members", Strings.Tab_Members);
        stack.AddTitled(Events(page.Result), "events", Strings.Tab_Events);
        var switcher = Gtk.StackSwitcher.New();
        switcher.SetStack(stack);
        switcher.Halign = Gtk.Align.Start;
        return Ui.VBox(12, Ui.Label(page.Result, nameof(StatisticsResultViewModel.ResultTitle), "title-3").Start(), switcher, stack);
    }

    private static Gtk.Widget Insights(StatisticsResultViewModel result) => Ui.VBox(14,
        Ui.Repeat(result, () => new[] { result.Kpis }, k => Widgets.KpiGrid(k, true), [nameof(StatisticsResultViewModel.Kpis)]),
        Widgets.Section(Strings.Chart_DistributionTitle,
            Ui.Repeat(result, () => new[] { result.Distribution }, bars => Widgets.ColumnChart(bars, 120), [nameof(StatisticsResultViewModel.Distribution)]),
            Ui.Muted(Strings.Chart_DistributionNote).Css("caption")),
        Widgets.Section(Strings.Chart_TrendTitle,
            Ui.Repeat(result, () => new[] { result.Trend }, bars => Widgets.ColumnChart(bars, 120), [nameof(StatisticsResultViewModel.Trend)]),
            Ui.Muted(Strings.Chart_TrendNote).Css("caption")));

    private static Gtk.Widget Members(AttendanceStatisticsViewModel page)
    {
        var table = page.Table;
        Gtk.Widget Segment(string text, MemberStatusFilter filter)
        {
            var button = Gtk.ToggleButton.NewWithLabel(text);
            Bind.Watch(button, table, () => button.Active = table.StatusFilter == filter, nameof(MemberTableViewModel.StatusFilter));
            button.OnToggled += (_, _) =>
            {
                if (button.Active) table.StatusFilter = filter;
            };
            return button;
        }

        var status = Ui.HBox(0, Segment(Strings.Stats_Everyone, MemberStatusFilter.All), Segment(Strings.Status_Qualified, MemberStatusFilter.Qualified),
            Segment(Strings.Filter_Below, MemberStatusFilter.BelowThreshold), Segment(Strings.Status_TooFew, MemberStatusFilter.InsufficientData)).Css("linked");

        Column<MemberRowViewModel> Sorted(string heading, MemberSortKey key, Func<MemberRowViewModel, Gtk.Widget> cell, bool expand = false, string? tip = null) =>
            new(heading, cell, expand, tip, () => table.SortByCommand.Execute(key));

        var grid = Table.Create(table, () => table.VisibleMembers, [nameof(MemberTableViewModel.VisibleMembers)],
            Sorted(Strings.Col_Member, MemberSortKey.Name, m => Ui.VBox(0, Table.Text(m.Name, "heading"), Table.Text(m.SubGroupsText, "dim-label", "caption")), true),
            Sorted(Strings.Col_Attendance, MemberSortKey.AttendanceRate, m => Ui.VBox(2,
                Ui.HBox(6, Widgets.RateBar(m.AttendanceFraction, m.IsQualified ? null : "warn", m.AttendanceAriaText), Ui.Label(m.AttendanceText, "numeric")),
                Ui.Caption(m.AttendedEventsText, "dim-label", "caption")).Tap(w => w.WidthRequest = 190)),
            Sorted(Strings.Col_Answered, MemberSortKey.ResponseRate, m => Ui.Caption(m.ResponseRateText, "numeric")),
            Sorted(Strings.Col_Declined, MemberSortKey.Invited, m => Ui.Caption(m.Declined.ToString(), "numeric")),
            Sorted(Strings.Col_NoAnswer, MemberSortKey.Invited, m => Ui.Caption(m.Unanswered.ToString(), "numeric")),
            Sorted(Strings.Col_Streak, MemberSortKey.CurrentStreak, m => Ui.Caption(m.StreakText, "numeric")),
            Sorted(Strings.Col_LastAttended, MemberSortKey.DaysSinceLastAttended, m => Ui.Caption(m.LastAttendedText)),
            Sorted(Strings.Col_PerWeek, MemberSortKey.SessionsPerWeek, m => Ui.Caption(m.SessionsPerWeekText, "numeric")),
            Sorted(Strings.Rating_StarsHeader, MemberSortKey.Stars, m => Ui.Caption(m.StarsText, "numeric").Tip(m.StarsTip)),
            Sorted(Strings.Col_Form, MemberSortKey.AttendanceRate, m => Ui.Caption(m.TrendGlyph).Tip(m.TrendText), false, Strings.Col_FormTip),
            Sorted(Strings.Col_Engagement, MemberSortKey.EngagementScore, m => Widgets.Pill($"{m.EngagementScore}  {m.LevelText}", DashboardPage.LevelTone(m.Level)).Tip(Strings.Col_EngagementTip), false, Strings.Col_EngagementTip),
            Sorted(Strings.Col_Status, MemberSortKey.AttendanceRate, m => Widgets.Pill(m.StatusText, m.IsQualified ? "chip-ok" : m.Status == QualificationStatus.BelowThreshold ? "chip-warn" : null).Tip(m.StatusTip)));

        return Ui.VBox(10,
            Ui.HBox(12, status, Ui.Search(table, nameof(MemberTableViewModel.SearchText), Strings.Stats_FindMember).Expand(),
                Ui.Label(table, nameof(MemberTableViewModel.MembersCaption), "dim-label")),
            grid);
    }

    private static Gtk.Widget Events(StatisticsResultViewModel result) =>
        Table.Create(result, () => result.Events, [nameof(StatisticsResultViewModel.Events)],
            new Column<EventRowViewModel>(Strings.Col_Event, e => Table.Text(e.Name, "heading"), true),
            new Column<EventRowViewModel>(Strings.Col_Date, e => Ui.Caption(e.StartText)),
            new Column<EventRowViewModel>(Strings.Col_Attendance, e => Ui.HBox(6, Widgets.RateBar(e.AttendanceFraction), Ui.Label(e.AttendanceText, "numeric")).Tap(w => w.WidthRequest = 170)),
            new Column<EventRowViewModel>(Strings.Legend_Attended, e => Ui.Caption(e.InvitedCameText, "numeric")),
            new Column<EventRowViewModel>(Strings.Col_Declined, e => Ui.Caption(e.DeclinedText, "numeric")),
            new Column<EventRowViewModel>(Strings.Col_NoAnswer, e => Ui.Caption(e.UnansweredText, "numeric")),
            new Column<EventRowViewModel>(Strings.Rating_StarsHeader, e => Ui.Caption(e.StarsText, "numeric")));

}
