using Android.Views;
using Android.Widget;
using Kyuplan.Client.AndroidApp.Kit;
using Kyuplan.Client.Resources;
using Kyuplan.Client.ViewModels;
using Kyuplan.Contracts;

namespace Kyuplan.Client.AndroidApp.Screens;

/// <summary>The exams of the selected group, and one exam in detail: who is a candidate (tick them to plan), and for a past exam who came and what it cost.</summary>
public sealed class ExamsScreen : ScrollScreen
{
    private readonly ExamsViewModel _vm;

    public ExamsScreen(Ui ui, ExamsViewModel vm) : base(ui)
    {
        _vm = vm;
        Observe(vm);
        Observe(vm.Detail);
        Observe(vm.Shell, nameof(ShellViewModel.SelectedGroupId), nameof(ShellViewModel.Groups));
        Observe(vm.Shell.Language);
    }

    public override bool HandleBack()
    {
        if (_vm.SelectedExam is null) return false;

        ScrollToTop();
        _vm.SelectedExam = null;
        return true;
    }

    protected override void Build(LinearLayout content)
    {
        if (_vm.IsBusy || _vm.Detail.IsBusy) content.AddView(Ui.Busy());
        if (_vm.HasError) Ui.Add(content, Ui.Notice(_vm.ErrorMessage!, Tone.Danger), top: 8);

        if (_vm.SelectedExam is { } exam) BuildDetail(content, exam);
        else BuildList(content);
    }

    // --- The exams -------------------------------------------------------------------------------------------

    private void BuildList(LinearLayout content)
    {
        if (string.IsNullOrEmpty(_vm.Shell.SelectedGroupId))
        {
            Ui.Add(content, Ui.Text(Strings.Calendar_NoGroup, muted: true), top: 8);
            return;
        }

        if (_vm.HasNotice) Ui.Add(content, Ui.Notice(_vm.NoticeText, Tone.Success), top: 8);
        if (_vm.IsEmpty)
        {
            Ui.Add(content, Ui.Text(Strings.Exams_None, muted: true), top: 8);
            return;
        }

        foreach (var exam in _vm.Exams)
        {
            var date = Ui.Column(Ui.Text(exam.MonthText, TextStyle.Caption), Ui.Text(exam.DayNumber, TextStyle.Title));
            date.SetGravity(GravityFlags.Center);
            var text = Ui.Column(Ui.Text(exam.Name, TextStyle.Heading), Ui.Text(exam.WhenText, TextStyle.Body), Ui.Text(exam.PeopleText, TextStyle.Caption));
            var row = Ui.Row();
            row.AddView(date, Ui.Params(wrap: true, right: 16));
            row.AddView(text, Ui.Params(weight: 1));
            var card = Ui.Card(row);
            card.Clickable = true;
            card.Foreground = Ui.Ripple(null);
            card.Click += (_, _) =>
            {
                ScrollToTop();
                _vm.SelectedExam = exam;
            };
            Ui.Add(content, card, bottom: 8, top: exam == _vm.Exams[0] ? 8 : 0);
        }
    }

    // --- One exam --------------------------------------------------------------------------------------------

    private void BuildDetail(LinearLayout content, ExamRow exam)
    {
        var detail = _vm.Detail;
        Ui.Add(content, Ui.Row(Ui.IconButton(Resource.Drawable.ic_back, Strings.Nav_Exams, () => HandleBack()), Ui.Text(Strings.Nav_Exams, TextStyle.Caption)));

        var header = Ui.Card(Ui.Text(exam.Name, TextStyle.Title), Ui.Text($"{exam.DateText} · {exam.WhenText}", TextStyle.Body), Ui.Text(exam.PeopleText, TextStyle.Caption));
        Ui.Add(content, header, bottom: 12);

        if (detail.HasError) Ui.Add(content, Ui.Notice(detail.ErrorMessage!, Tone.Danger));
        if (detail.HasNotice) Ui.Add(content, Ui.Notice(detail.NoticeText, Tone.Success));

        if (detail.ShowsPlan) Plan(content, detail);
        if (detail.ShowsResults) Results(content, detail);
        if (detail.CanViewPayments && detail.HasPayments) Payments(content, detail);
    }

    private void Plan(LinearLayout content, ExamDetailViewModel detail)
    {
        Ui.Add(content, Ui.Text(Strings.Plan_Title, TextStyle.Heading), bottom: 8);
        Ui.Add(content, Ui.ChipRow([.. detail.FilterOptions.Select(o => (View)Ui.Chip(o.Label, o == detail.SelectedFilter, () => detail.SelectedFilter = o))]));

        if (detail.NoneMatch || !detail.HasSuggestions) Ui.Add(content, Ui.Text(detail.EmptyText, muted: true));
        foreach (var row in detail.VisibleSuggestions) Ui.Add(content, Candidate(detail, row), bottom: 8);

        if (!detail.CanPlan) return;

        Ui.Add(content, Ui.Text(detail.SelectionText, TextStyle.Caption), top: 4);
        var buttons = Ui.Row(
            Ui.Button(Strings.Plan_SelectFit, () => Run(detail.SelectFitCommand), filled: false),
            Ui.Button(Strings.Plan_ClearSelection, () => Run(detail.ClearSelectionCommand), filled: false));
        buttons.GetChildAt(0)!.LayoutParameters = Ui.Params(weight: 1, right: 4);
        buttons.GetChildAt(1)!.LayoutParameters = Ui.Params(weight: 1, left: 4);
        Ui.Add(content, buttons, bottom: 8);
        Ui.Add(content, Ui.Button(Strings.Plan_Save, () => Run(detail.SaveSelectionCommand), enabled: detail.CanSave), bottom: 8);
        if (detail.HasChanges) Ui.Add(content, Ui.Button(Strings.Plan_Discard, () => Run(detail.DiscardChangesCommand), filled: false));
    }

    private View Candidate(ExamDetailViewModel detail, SuggestionRow row)
    {
        var tone = row.Verdict switch
        {
            SuggestionVerdict.Fit => Tone.Success,
            SuggestionVerdict.NotYet => Tone.Warning,
            _ => Tone.Neutral,
        };

        var belts = row.HasNextBelt ? $"{row.Belt.Label} → {row.NextBelt!.Label}" : row.Belt.Label;
        var text = Ui.Column(Ui.Text(row.Name, TextStyle.Body), Ui.Text(belts, TextStyle.Caption));
        text.AddView(Ui.Badge(row.VerdictText, tone), Ui.Params(wrap: true, top: 4));
        text.AddView(Ui.Text(row.ReasonText, TextStyle.Caption), Ui.Params(top: 4));
        if (row.IsInvited) text.AddView(Ui.Text(row.InvitationText, TextStyle.Caption));

        var line = Ui.Row();
        if (detail.CanPlan) line.AddView(Ui.Check(row.IsSelected, row.Name, ticked => row.IsSelected = ticked), Ui.Params(wrap: true, right: 8));
        line.AddView(text, Ui.Params(weight: 1));
        return Ui.Card(line);
    }

    private void Results(LinearLayout content, ExamDetailViewModel detail)
    {
        Ui.Add(content, Ui.Text(Strings.Nav_Members, TextStyle.Heading), top: 12, bottom: 8);
        if (detail.NobodyAttended)
        {
            Ui.Add(content, Ui.Text(Strings.Exams_None, muted: true));
            return;
        }

        foreach (var attendee in detail.Attendees)
        {
            var row = Ui.Row();
            row.AddView(Ui.Text(attendee.Name, TextStyle.Body), Ui.Params(weight: 1));
            if (attendee.SelectedBelt is { IsNone: false } belt)
            {
                row.AddView(Ui.BeltSwatch(belt.Color, belt.SecondaryColor, right: 8));
                row.AddView(Ui.Text(belt.Label, TextStyle.Caption), Ui.Params(wrap: true));
            }

            Ui.Add(content, Ui.Card(row), bottom: 8);
        }
    }

    private void Payments(LinearLayout content, ExamDetailViewModel detail)
    {
        Ui.Add(content, Ui.Text(detail.PaymentTotalsText, TextStyle.Caption), top: 12, bottom: 8);
        foreach (var payment in detail.PaymentRows)
        {
            var row = Ui.Row();
            row.AddView(Ui.Column(Ui.Text(payment.Name, TextStyle.Body), Ui.Text(payment.StatusText, TextStyle.Caption)), Ui.Params(weight: 1));
            row.AddView(Ui.Text(payment.DueText, TextStyle.Heading), Ui.Params(wrap: true));
            Ui.Add(content, Ui.Card(row), bottom: 8);
        }
    }
}
