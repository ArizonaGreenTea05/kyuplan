using Kyuplan.Client.LinuxApp.Kit;
using Kyuplan.Client.LinuxApp.Pages.Parts;
using Kyuplan.Client.Resources;
using Kyuplan.Client.ViewModels;
using Kyuplan.Contracts;

namespace Kyuplan.Client.LinuxApp.Pages;

/// <summary>Belt exams: the list of exams, and for the selected one the plan (who is suggested), the results (who got which belt) and the payments.</summary>
internal static class ExamsPage
{
    public static Gtk.Widget Build(UiContext context, ExamsViewModel page)
    {
        var detail = page.Detail;
        detail.ExportReady += async (_, file) => await context.SaveAsync(file, Strings.Export_Title);

        var exams = Ui.BoxedList(page, () => page.Exams, exam =>
        {
            var row = Ui.Row(exam.Label, $"{exam.WhenText} · {exam.PeopleText}", Ui.Chip(exam.StatusText, exam.IsUpcoming ? "chip-ok" : ""));
            row.Activatable = true;
            row.OnActivated += (_, _) => page.SelectedExam = exam;
            return row;
        }, [nameof(ExamsViewModel.Exams)]);

        var body = Ui.VBox(14,
            Widgets.Header(page.Title, Ui.Muted(Strings.Exams_Lede),
                Ui.HBox(8, Ui.Primary(Strings.Exams_Add, page.BeginAddCommand).VisibleWhen(page, nameof(ExamsViewModel.CanEdit)))),
            Widgets.ErrorBanner(page, page.RetryCommand), Widgets.Notice(page),
            AddForm(page),
            Widgets.ConfirmBar(page, nameof(ExamsViewModel.IsConfirmingDelete), nameof(ExamsViewModel.DeleteQuestion), page.ConfirmDeleteCommand, Strings.Saved_ConfirmYes, page.CancelDeleteCommand, Strings.Saved_Cancel),
            Ui.Label(Strings.Exams_None, "dim-label").VisibleWhen(page, nameof(ExamsViewModel.IsEmpty)),
            exams,
            Detail(page, detail));
        return Widgets.PageFrame(page, body, 1200);
    }

    private static Gtk.Widget AddForm(ExamsViewModel page)
    {
        var events = EventPicker.Create(page, () => page.EventOptions, () => page.NewEventId, id => page.NewEventId = id,
            [nameof(ExamsViewModel.EventOptions), nameof(ExamsViewModel.NewEventId)], Strings.Exams_AddEvent);
        var subgroups = Ui.Field(Strings.Exams_AddSubGroups,
            SubGroupPicker.Create(page, () => page.SubGroupOptions, () => page.SubGroupSummary, [nameof(ExamsViewModel.SubGroupOptions), nameof(ExamsViewModel.SubGroupSummary)], page.ClearSubGroupsCommand, Strings.Exams_AddSubGroups),
            Strings.Exams_AddSubGroupsHint).VisibleWhen(page, nameof(ExamsViewModel.HasSubGroups));
        return Ui.VBox(10,
            Ui.Label(Strings.Exams_AddTitle, "heading").Start(),
            Ui.Muted(Strings.Exams_AddHint),
            Ui.Field(Strings.Exams_AddEvent, events), subgroups,
            Ui.HBox(8, Ui.Primary(Strings.Exams_AddSave, page.AddCommand), Ui.Button(Strings.Saved_Cancel, page.CancelAddCommand)))
            .Css("card").Padded(14).VisibleWhen(page, nameof(ExamsViewModel.IsAdding));
    }

    private static Gtk.Widget Detail(ExamsViewModel page, ExamDetailViewModel detail)
    {
        var heading = Ui.HBox(10,
            Ui.Label(detail, nameof(ExamDetailViewModel.Heading), "title-2").Expand(),
            Ui.Button(Strings.Exams_Delete, page.AskDeleteCommand, null, "destructive-action").VisibleWhen(page, nameof(ExamsViewModel.CanEdit)));
        return Ui.VBox(14,
            heading,
            Widgets.Notice(detail),
            Ui.Label(detail, nameof(ViewModelBase.ErrorMessage), "error").VisibleWhen(detail, nameof(ViewModelBase.HasError)),
            Plan(page, detail).VisibleWhen(detail, nameof(ExamDetailViewModel.ShowsPlan)),
            Results(detail).VisibleWhen(detail, nameof(ExamDetailViewModel.ShowsResults)),
            Payments(detail).VisibleWhen(detail, nameof(ExamDetailViewModel.CanViewPayments)))
            .VisibleWhen(detail, nameof(ExamDetailViewModel.HasExam));
    }

    // ---- plan --------------------------------------------------------------------------------------------------------------

    private static Gtk.Widget Plan(ExamsViewModel page, ExamDetailViewModel detail)
    {
        var basis = Ui.Choice(detail, () => detail.BasisOptions, b => b.Label, () => detail.SelectedBasis, b => detail.SelectedBasis = b,
            [nameof(ExamDetailViewModel.BasisOptions), nameof(ExamDetailViewModel.SelectedBasis)], Strings.Plan_Basis);
        var filter = Ui.Choice(detail, () => detail.FilterOptions, f => f.Label, () => detail.SelectedFilter, f => detail.SelectedFilter = f,
            [nameof(ExamDetailViewModel.SelectedFilter), nameof(ExamDetailViewModel.Suggestions)], Strings.Plan_Show);

        var table = Table.Create(detail, () => detail.VisibleSuggestions, [nameof(ExamDetailViewModel.VisibleSuggestions)],
            new Column<SuggestionRow>(Strings.Plan_ColSave, r => PlanCheck(detail, r)),
            new Column<SuggestionRow>(Strings.Col_Member, r => Ui.VBox(0, Table.Text(r.Name, "heading"), Table.Text(r.SubGroupsText, "dim-label", "caption")), true),
            new Column<SuggestionRow>(Strings.Belts_ColBelt, r => r.HasBelt ? ColorClass.Belt(r.Belt.Color, r.Belt.SecondaryColor, 36, 12) : Ui.Caption(r.Belt.Label, "dim-label")),
            new Column<SuggestionRow>(Strings.Plan_ColNext, r => r.HasNextBelt ? ColorClass.Belt(r.NextBelt!.Color, r.NextBelt.SecondaryColor, 36, 12) : Ui.Caption(Display.None)),
            new Column<SuggestionRow>(Strings.Plan_ColAge, r => Check(r.AgeText, r.AgeState)),
            new Column<SuggestionRow>(Strings.Plan_ColWait, r => Check(r.WaitText, r.WaitState).Tip(r.WaitTip)),
            new Column<SuggestionRow>(Strings.Plan_ColStars, r => Check(r.StarsText, r.StarsState).Tip(Strings.Rating_StarsTip)),
            new Column<SuggestionRow>(Strings.Plan_ColAttendance, r => Ui.HBox(6, Widgets.RateBar(r.AttendanceFraction, r.AttendanceMet ? null : "warn", r.AttendanceAriaText), Ui.Label(r.AttendanceText, "numeric")).Tap(w => w.WidthRequest = 150)),
            new Column<SuggestionRow>(Strings.Plan_ColLastExam, r => Ui.Caption(r.LastExamText)),
            new Column<SuggestionRow>(Strings.Plan_ColVerdict, r => Ui.Chip(r.VerdictText, r.IsFit ? "chip-ok" : r.Verdict == SuggestionVerdict.NotYet ? "chip-warn" : "").Tip(r.VerdictTip)),
            new Column<SuggestionRow>(Strings.Plan_ColSpond, r => Ui.Caption(r.InvitationText)));

        var actions = Ui.HBox(8,
            Ui.Label(detail, nameof(ExamDetailViewModel.SelectionText), "heading").Expand(),
            Ui.Label(detail, nameof(ExamDetailViewModel.SavedNotInvitedText), "warn-text").VisibleWhen(detail, nameof(ExamDetailViewModel.HasSavedNotInvited)),
            Ui.Button(Strings.Plan_SelectFit, detail.SelectFitCommand),
            Ui.Button(Strings.Plan_ClearSelection, detail.ClearSelectionCommand),
            Ui.Button(Strings.Plan_Discard, detail.DiscardChangesCommand),
            Ui.Primary(Strings.Plan_Save, detail.SaveSelectionCommand).Tip(Strings.Plan_SaveHint)).VisibleWhen(detail, nameof(ExamDetailViewModel.CanPlan));

        return Widgets.Section(Strings.Plan_Title,
            Ui.Muted(Strings.Plan_Lede),
            Ui.Muted(Strings.Plan_NoBelts).VisibleWhen(detail, nameof(ExamDetailViewModel.HasNoBelts)),
            Ui.HBox(12,
                Ui.Field(Strings.Plan_Basis, basis, Strings.Plan_BasisHint).Expand(),
                Ui.Button(Strings.Plan_BasisDefine, page.OpenStatisticsCommand).VisibleWhen(page, nameof(ExamsViewModel.CanOpenStatistics)).Tap(w => w.Valign = Gtk.Align.Center)),
            Ui.HBox(12,
                Ui.Field(Strings.Plan_Show, filter).Expand(),
                Ui.Field(Strings.Stats_FindMember, Ui.Search(detail, nameof(ExamDetailViewModel.SearchText), Strings.Stats_FindMember)).Expand()),
            Ui.Muted(Strings.Res_Nobody).VisibleWhen(detail, () => detail.NoneMatch, nameof(ExamDetailViewModel.NoneMatch)),
            table, actions);
    }

    /// <summary>The box that puts a member on the exam (and shows that it is saved).</summary>
    private static Gtk.Widget PlanCheck(ExamDetailViewModel detail, SuggestionRow row)
    {
        var check = Ui.Check(row, nameof(SuggestionRow.IsSelected), "");
        check.Sensitive = detail.CanPlan;
        check.Tip(row.IsSaved ? Strings.Plan_SavedMark : row.Name);
        return check;
    }

    private static Gtk.Widget Check(string text, CheckState state)
    {
        var label = Ui.Caption(text, "numeric");
        if (state == CheckState.NotMet) label.AddCssClass("warn-text");
        else if (state == CheckState.Unknown) label.AddCssClass("dim-label");
        return label;
    }

    // ---- results -----------------------------------------------------------------------------------------------------------

    private static Gtk.Widget Results(ExamDetailViewModel detail)
    {
        var table = Table.Create(detail, () => detail.Attendees, [nameof(ExamDetailViewModel.Attendees)],
            new Column<AttendeeRow>(Strings.Col_Member, a => Table.Text(a.Name, "heading"), true),
            new Column<AttendeeRow>(Strings.Res_ColAnswer, a => Ui.Caption(a.AnswerText)),
            new Column<AttendeeRow>(Strings.Res_ColBelt, a => Widgets.BeltChoice(a, () => detail.BeltOptions, () => a.SelectedBelt, b => a.SelectedBelt = b,
                [nameof(AttendeeRow.SelectedBelt)]).Tap(w =>
                {
                    w.Hexpand = false;
                    w.Sensitive = detail.CanRecordResults;
                })));
        var exports = Ui.HBox(8, Ui.Button(Strings.Export_ResultsCsv, detail.ExportResultsCsvCommand), Ui.Button(Strings.Export_ResultsExcel, detail.ExportResultsExcelCommand));
        return Widgets.Section(Strings.Res_Title,
            Ui.Muted(Strings.Res_Lede),
            Ui.Repeat(detail, () => new[] { detail.ResultKpis }, k => Widgets.KpiGrid(k, true), [nameof(ExamDetailViewModel.ResultKpis)]),
            Ui.Muted(Strings.Res_Nobody).VisibleWhen(detail, nameof(ExamDetailViewModel.NobodyAttended)),
            table, exports);
    }

    // ---- payments ----------------------------------------------------------------------------------------------------------

    private static Gtk.Widget Payments(ExamDetailViewModel detail)
    {
        var table = Table.Create(detail, () => detail.PaymentRows, [nameof(ExamDetailViewModel.PaymentRows)],
            new Column<PaymentRow>(Strings.Pay_Member, p => Table.Text(p.Name, "heading"), true),
            new Column<PaymentRow>(Strings.Pay_Belt, p => p.Belt.IsNone ? Ui.Caption(Display.None) : ColorClass.Belt(p.Belt.Color, p.Belt.SecondaryColor, 36, 12)),
            new Column<PaymentRow>(Strings.Pay_General, p => Ui.Caption(p.GeneralFeeText, "numeric")),
            new Column<PaymentRow>(Strings.Pay_Stamp, p => Ui.Caption(p.StampFeeText, "numeric")),
            new Column<PaymentRow>(Strings.Pay_BeltPrice, p => Ui.Caption(p.BeltPriceText, "numeric")),
            new Column<PaymentRow>(Strings.Pay_OwnBelt, p => Ui.Check(p, nameof(PaymentRow.OwnBelt), "").Tap(w =>
            {
                w.Sensitive = p.CanEdit;
                w.Tip(Strings.Pay_OwnBeltHint);
            })),
            new Column<PaymentRow>(Strings.Pay_Due, p => Ui.Caption(p.DueText, "numeric", "heading")),
            new Column<PaymentRow>(Strings.Pay_Paid, p => Ui.Entry(p.Paid, nameof(MoneyField.Text), name: Strings.Pay_Paid).Tap(w =>
            {
                w.WidthChars = 8;
                w.Hexpand = false;
                w.Sensitive = p.CanEdit;
            })),
            new Column<PaymentRow>(Strings.Pay_Note, p => Ui.Entry(p, nameof(PaymentRow.Note), name: Strings.Pay_Note).Tap(w =>
            {
                w.WidthChars = 16;
                w.Hexpand = false;
                w.Sensitive = p.CanEdit;
            })),
            new Column<PaymentRow>(Strings.Col_Status, p => Ui.Chip(p.StatusText, p.Status == PaymentStatus.Paid ? "chip-ok" : p.Status == PaymentStatus.Partial ? "chip-warn" : "").Tip(p.PaidOnText)),
            new Column<PaymentRow>("", p => Ui.HBox(4,
                Ui.Button(Strings.Pay_MarkPaid, p.MarkPaidCommand),
                Ui.Primary(Strings.Pay_Save, p.SaveCommand)).VisibleWhen(detail, nameof(ExamDetailViewModel.CanEditPayments))));

        return Widgets.Section(Strings.Pay_Title,
            Ui.Muted(Strings.Pay_Intro),
            Ui.Label(detail, nameof(ExamDetailViewModel.PaymentTotalsText), "heading").VisibleWhen(detail, nameof(ExamDetailViewModel.HasPayments)),
            Ui.Muted(Strings.Pay_Empty).VisibleWhen(detail, nameof(ExamDetailViewModel.NobodyPays)),
            Ui.Label(detail, nameof(ExamDetailViewModel.PaymentCountsText), "dim-label").VisibleWhen(detail, nameof(ExamDetailViewModel.HasPayments)),
            table,
            Ui.HBox(8, Ui.Button(Strings.Export_PaymentsCsv, detail.ExportPaymentsCsvCommand), Ui.Button(Strings.Export_PaymentsExcel, detail.ExportPaymentsExcelCommand)));
    }

}
