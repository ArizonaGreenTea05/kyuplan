namespace Kyuplan.Api.Statistics;

/// <summary>
/// Finds the time span the attendance page starts with: from the last exam to the next one, or, when there is no such
/// pair, the last few months. Pure and deterministic, like <see cref="AttendanceCalculator"/>: the events, the keyword
/// and "now" are passed in.
/// </summary>
internal static class ExamSpanFinder
{
    /// <summary>How far back <see cref="FindRecent"/> looks; the same six months the page falls back to when it has no events to offer.</summary>
    public const int RecentMonths = 6;

    /// <summary>
    /// The next exam is the first non-cancelled event after <paramref name="now"/> whose name contains
    /// <paramref name="keyword"/> (case-insensitive); the previous exam is the latest one that starts before it.
    /// <c>null</c> when either is missing: a half pair says nothing about the span the user wants.
    /// </summary>
    public static (EventInfo From, EventInfo To)? Find(IEnumerable<EventInfo> events, string? keyword, DateTimeOffset now)
    {
        if (string.IsNullOrWhiteSpace(keyword)) return null;

        var term = keyword.Trim();
        var exams = events
            .Where(e => !e.Cancelled && e.Name.Contains(term, StringComparison.OrdinalIgnoreCase))
            .OrderBy(e => e.Start)
            .ThenBy(e => e.Id, StringComparer.Ordinal)
            .ToList();

        var next = exams.FirstOrDefault(e => e.Start > now);
        if (next is null) return null;

        // Everything before `next` in the list is over: had it started after now, it would have been the next exam.
        var previous = exams.LastOrDefault(e => e.Start < next.Start);
        return previous is null ? null : (previous, next);
    }

    /// <summary>
    /// The span without exams to go by: the first non-cancelled event of the last <see cref="RecentMonths"/> months to
    /// the latest one that takes place today or earlier. <c>null</c> when none took place in that time.
    /// </summary>
    public static (EventInfo From, EventInfo To)? FindRecent(IEnumerable<EventInfo> events, DateTimeOffset now)
    {
        // Event times and "now" are both in the club time zone, so their dates are the club's days.
        var today = now.Date;
        var firstDay = today.AddMonths(-RecentMonths).AddDays(1);

        var recent = events
            .Where(e => !e.Cancelled && e.Start.Date >= firstDay && e.Start.Date <= today)
            .OrderBy(e => e.Start)
            .ThenBy(e => e.Id, StringComparer.Ordinal)
            .ToList();

        return recent.Count == 0 ? null : (recent[0], recent[^1]);
    }
}
