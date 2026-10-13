using Kyuplan.Client.Resources;
using Kyuplan.Contracts;

namespace Kyuplan.Client.ViewModels;

/// <summary>Turns statistics into the bars both clients draw.</summary>
internal static class ChartData
{
    public static IReadOnlyList<BarViewModel> MonthlyTrend(IReadOnlyList<MonthlyTrendPointDto> months) =>
        months.Select(m => new BarViewModel(
                Display.Month(m.Month),
                Display.Percent(m.AttendanceRate),
                m.AttendanceRate / 100.0,
                string.Format(Strings.Chart_MonthTip,
                    Display.Month(m.Month), m.Events, Display.Number(m.AverageAttendees), Display.Percent(m.AttendanceRate))))
            .ToList();

    /// <summary>Bars at or above the attendance target are highlighted, so the chart shows how many members would qualify.</summary>
    public static IReadOnlyList<BarViewModel> Distribution(IReadOnlyList<DistributionBucketDto> buckets, int thresholdPercent)
    {
        var max = Math.Max(1, buckets.Count == 0 ? 0 : buckets.Max(b => b.Members));
        return buckets.Select(b =>
            {
                var upper = b.UpperPercent == 100 ? 100 : b.UpperPercent - 1;
                return new BarViewModel(
                    $"{b.LowerPercent}–{upper}",
                    b.Members.ToString(),
                    b.Members / (double)max,
                    string.Format(b.Members == 1 ? Strings.Chart_BandTipOne : Strings.Chart_BandTipMany, b.Members, b.LowerPercent, upper),
                    IsHighlighted: b.LowerPercent >= thresholdPercent);
            })
            .ToList();
    }
}
