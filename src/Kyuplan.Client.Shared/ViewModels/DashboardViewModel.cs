using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;
using Kyuplan.Client.Api;
using Kyuplan.Client.Resources;
using Kyuplan.Contracts;

namespace Kyuplan.Client.ViewModels;

public sealed partial class DashboardViewModel(ShellViewModel shell, IPlannerApi api) : PageViewModel(shell)
{
    private DashboardDto? _last;

    public override string Title => Strings.Nav_Dashboard;

    protected override string? ViewPermission => Permission.DashboardView;

    public IReadOnlyList<PeriodOption> PeriodOptions { get; } = [new(30), new(90), new(180), new(365)];

    /// <summary>The sentence under the title while nothing is loaded yet.</summary>
    public string Lede => Strings.Dashboard_Lede;

    [ObservableProperty]
    private int _selectedDays = 90;

    [ObservableProperty]
    private string _groupName = "";

    [ObservableProperty]
    private string _periodText = "";

    [ObservableProperty]
    private IReadOnlyList<KpiViewModel> _kpis = [];

    [ObservableProperty]
    private IReadOnlyList<UpcomingEventRowViewModel> _upcoming = [];

    [ObservableProperty]
    private IReadOnlyList<MemberRowViewModel> _topMembers = [];

    [ObservableProperty]
    private IReadOnlyList<MemberRowViewModel> _needsAttention = [];

    [ObservableProperty]
    private IReadOnlyList<BarViewModel> _trend = [];

    [ObservableProperty]
    private bool _hasData;

    /// <summary>True when a load finished without any held event in the period: the page says so instead of staying blank.</summary>
    public bool HasNoData => !HasData && !IsBusy && !HasError && Shell.SelectedGroup is not null;

    protected override void OnPropertyChanged(PropertyChangedEventArgs e)
    {
        base.OnPropertyChanged(e);

        if (e.PropertyName == nameof(SelectedDays)) _ = InvalidateAsync();
        if (e.PropertyName is nameof(HasData) or nameof(IsBusy) or nameof(ErrorMessage)) OnPropertyChanged(nameof(HasNoData));
    }

    protected override Task ReloadAsync() => LoadAsync(async ct =>
    {
        if (Shell.SelectedGroup is not { } group)
        {
            HasData = false;
            return;
        }

        var dashboard = await api.GetDashboardAsync(group.Id, days: SelectedDays, cancellationToken: ct);
        ct.ThrowIfCancellationRequested(); // a newer load took over while this response was arriving
        Apply(dashboard);
    });

    protected override void OnLanguageChanged()
    {
        base.OnLanguageChanged();
        foreach (var option in PeriodOptions) option.Refresh();
        OnPropertyChanged(nameof(Lede));
        if (_last is { } d) Apply(d);
    }

    private void Apply(DashboardDto d)
    {
        _last = d;
        var s = d.Summary;
        GroupName = d.GroupName;
        PeriodText = Display.Period(d.From, d.To);

        Kpis =
        [
            new(Strings.Kpi_EventsHeld, s.EventCount.ToString(), Strings.Kpi_EventsHeldHint),
            new(Strings.Kpi_Members, s.MemberCount.ToString(), Strings.Kpi_MembersHint),
            new(Strings.Kpi_Attendance, Display.Percent(s.OverallAttendanceRate), Strings.Kpi_AttendanceHint),
            new(Strings.Kpi_AverageAttendeesPerEvent, Display.Number(s.AverageAttendeesPerEvent), Strings.Kpi_AverageAttendeesPerEventHint),
            new(string.Format(Strings.Kpi_Meeting, d.MinAttendancePercent),
                string.Format(Strings.Kpi_OfAssessed, s.QualifiedCount, s.MemberCount - s.InsufficientDataCount),
                Strings.Kpi_MeetingDashboardHint, KpiTone.Success),
            new(Strings.Kpi_AtRisk, s.AtRiskCount.ToString(), Strings.Kpi_AtRiskHint, s.AtRiskCount > 0 ? KpiTone.Warning : KpiTone.Neutral),
        ];

        Upcoming = d.Upcoming.Select(u => new UpcomingEventRowViewModel(u)).ToList();
        TopMembers = d.TopMembers.Select(m => new MemberRowViewModel(m)).ToList();
        NeedsAttention = d.NeedsAttention.Select(m => new MemberRowViewModel(m)).ToList();
        Trend = ChartData.MonthlyTrend(d.MonthlyTrend);
        HasData = s.EventCount > 0;
    }
}
