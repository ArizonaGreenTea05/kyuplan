using Android.Widget;
using Kyuplan.Client.AndroidApp.Kit;
using Kyuplan.Client.Resources;
using Kyuplan.Client.ViewModels;

namespace Kyuplan.Client.AndroidApp.Screens;

/// <summary>The dashboard of the selected group: key figures, the next events, and who is doing well or needs a nudge.</summary>
public sealed class DashboardScreen : ScrollScreen
{
    private readonly DashboardViewModel _vm;

    public DashboardScreen(Ui ui, DashboardViewModel vm) : base(ui)
    {
        _vm = vm;
        Observe(vm);
        Observe(vm.Shell, nameof(ShellViewModel.SelectedGroupId), nameof(ShellViewModel.Groups));
        Observe(vm.Shell.Language);
    }

    protected override void Build(LinearLayout content)
    {
        if (_vm.IsBusy) content.AddView(Ui.Busy());
        if (_vm.HasError) Ui.Add(content, Ui.Notice(_vm.ErrorMessage!, Tone.Danger), top: 8);

        if (_vm.Shell.SelectedGroup is null)
        {
            Ui.Add(content, Ui.Text(Strings.Calendar_NoGroup, muted: true), top: 8);
            return;
        }

        var periods = _vm.PeriodOptions.Select(option => (Android.Views.View)Ui.Chip(option.Label, option.Days == _vm.SelectedDays, () => _vm.SelectedDays = option.Days)).ToArray();
        Ui.Add(content, Ui.ChipRow(periods), top: 4);
        if (_vm.PeriodText.Length > 0) Ui.Add(content, Ui.Text($"{_vm.GroupName} · {_vm.PeriodText}", TextStyle.Caption), bottom: 12);

        if (_vm.HasNoData)
        {
            Ui.Add(content, Ui.Notice(Strings.Dashboard_Empty));
            return;
        }

        // The key figures, two to a row.
        for (var i = 0; i < _vm.Kpis.Count; i += 2)
        {
            var row = Ui.Row();
            row.SetGravity(Android.Views.GravityFlags.Top);
            row.AddView(Tile(_vm.Kpis[i]), Ui.Params(weight: 1, right: 6, matchHeight: true));
            if (i + 1 < _vm.Kpis.Count) row.AddView(Tile(_vm.Kpis[i + 1]), Ui.Params(weight: 1, left: 6, matchHeight: true));
            else row.AddView(new Android.Views.View(Ui.Context), Ui.Params(weight: 1, left: 6));
            Ui.Add(content, row);
        }

        Section(content, Strings.Dashboard_Upcoming);
        if (_vm.Upcoming.Count == 0) Ui.Add(content, Ui.Text(Strings.Dashboard_NothingScheduled, muted: true));
        foreach (var upcoming in _vm.Upcoming)
        {
            var card = Ui.Card(
                Ui.Text(upcoming.Name, TextStyle.Heading),
                Ui.Text(upcoming.StartText, TextStyle.Caption),
                Ui.Space(8),
                Ui.Bar(upcoming.AcceptedFraction, Ui.P.Success),
                Ui.Space(4),
                Ui.Text(upcoming.AcceptedText, TextStyle.Caption));
            Ui.Add(content, card, bottom: 8);
        }

        Section(content, Strings.Dashboard_NeedsAttention);
        Members(content, _vm.NeedsAttention, Strings.Dashboard_NeedsAttentionEmpty);
        Section(content, Strings.Dashboard_TopMembers);
        Members(content, _vm.TopMembers, Strings.Dashboard_TopMembersEmpty);
    }

    private void Section(LinearLayout content, string title) => Ui.Add(content, Ui.Text(title, TextStyle.Heading), bottom: 8, top: 12);

    private Android.Views.View Tile(KpiViewModel kpi)
    {
        var tone = kpi.Tone switch
        {
            KpiTone.Success => Tone.Success,
            KpiTone.Warning => Tone.Warning,
            _ => Tone.Neutral,
        };
        var card = Ui.Card(Ui.Text(kpi.Label, TextStyle.Caption), Ui.Text(kpi.Value, TextStyle.Title, tone));
        if (!string.IsNullOrEmpty(kpi.Hint)) card.AddView(Ui.Text(kpi.Hint, TextStyle.Caption));
        return card;
    }

    private void Members(LinearLayout content, IReadOnlyList<MemberRowViewModel> members, string empty)
    {
        if (members.Count == 0)
        {
            Ui.Add(content, Ui.Text(empty, muted: true));
            return;
        }

        foreach (var member in members)
        {
            var text = Ui.Column(Ui.Text(member.Name, TextStyle.Body), Ui.Text(member.AttendedEventsText, TextStyle.Caption));
            var row = Ui.Row();
            row.AddView(text, Ui.Params(weight: 1));
            row.AddView(Ui.Text(member.AttendanceText, TextStyle.Heading), Ui.Params(wrap: true));
            var card = Ui.Card(row);
            Ui.Add(content, card, bottom: 8);
        }
    }
}
