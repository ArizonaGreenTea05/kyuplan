using CommunityToolkit.Mvvm.ComponentModel;
using Kyuplan.Client.Resources;
using Kyuplan.Contracts;

namespace Kyuplan.Client.ViewModels;

/// <summary>The panels of the statistics page that show a loaded result besides the member table: the key figures, the charts and the event list.</summary>
public sealed partial class StatisticsResultViewModel : ObservableObject
{
    [ObservableProperty]
    private IReadOnlyList<KpiViewModel> _kpis = [];

    [ObservableProperty]
    private IReadOnlyList<BarViewModel> _distribution = [];

    [ObservableProperty]
    private IReadOnlyList<BarViewModel> _trend = [];

    [ObservableProperty]
    private IReadOnlyList<EventRowViewModel> _events = [];

    [ObservableProperty]
    private string _resultTitle = "";

    /// <summary>Shows <paramref name="result"/>. Texts are built in the current language, so this is called again when it changes.</summary>
    public void Show(AttendanceStatisticsDto result)
    {
        var filter = result.Filter;
        var s = result.Summary;
        ResultTitle = $"{result.GroupName} · {Display.Period(filter.From, filter.To)}";

        var assessed = s.MemberCount - s.InsufficientDataCount;
        Kpis =
        [
            new(string.Format(Strings.Kpi_Meeting, filter.MinAttendancePercent),
                string.Format(Strings.Kpi_OfAssessed, s.QualifiedCount, assessed),
                string.Format(Strings.Kpi_MeetingStatsHint, filter.MinAttendancePercent),
                KpiTone.Success),
            new(Strings.Kpi_BelowTarget, s.BelowThresholdCount.ToString(), Strings.Kpi_BelowTargetHint),
            new(Strings.Kpi_TooFew, s.InsufficientDataCount.ToString(), string.Format(Strings.Kpi_TooFewHint, filter.MinInvitedEvents)),
            new(Strings.Kpi_EventsAnalysed, s.EventCount.ToString(), Strings.Kpi_EventsAnalysedHint),
            new(Strings.Kpi_Attendance, Display.Percent(s.OverallAttendanceRate), Strings.Kpi_AttendanceOverallHint),
            new(Strings.Kpi_ResponseRate, Display.Percent(s.AverageResponseRate), Strings.Kpi_ResponseRateHint),
            new(Strings.Kpi_AverageAttendees, Display.Number(s.AverageAttendeesPerEvent), Strings.Kpi_AverageAttendeesHint),
            new(Strings.Kpi_Stars, s.AverageStars is { } stars ? Display.Stars(stars) : Display.None, Strings.Kpi_StarsHint),
            new(Strings.Kpi_AtRisk, s.AtRiskCount.ToString(), Strings.Kpi_AtRiskHint, s.AtRiskCount > 0 ? KpiTone.Warning : KpiTone.Neutral),
        ];

        Events = result.Events.Select(e => new EventRowViewModel(e)).ToList();
        Distribution = ChartData.Distribution(result.Distribution, filter.MinAttendancePercent);
        Trend = ChartData.MonthlyTrend(result.MonthlyTrend);
    }
}
