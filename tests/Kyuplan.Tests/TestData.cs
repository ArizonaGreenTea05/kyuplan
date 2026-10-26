using Kyuplan.Contracts;

namespace Kyuplan.Tests;

/// <summary>Small builders so that tests can describe a scenario in a few readable lines.</summary>
internal static class TestData
{
    public static readonly TimeSpan Cest = TimeSpan.FromHours(2);

    /// <summary>The "current" moment of the engine tests: 30 June 2026, noon, club time.</summary>
    public static readonly DateTimeOffset Now = new(2026, 6, 30, 12, 0, 0, Cest);

    public static DateTimeOffset At(int month, int day, int hour = 18) => new(2026, month, day, hour, 0, 0, Cest);

    public static MemberInfo Member(string id, params string[] subGroupIds) => new(id, id.ToUpperInvariant(), subGroupIds);

    public static GroupInfo Group(IEnumerable<MemberInfo> members, params SubGroupInfo[] subGroups) =>
        new("g1", "Test group", subGroups, members.ToList());

    public static AttendanceFilter Filter(
        int fromMonth = 1, int fromDay = 1, int toMonth = 6, int toDay = 30,
        int minPercent = 75, int minInvited = 3,
        string[]? subGroupIds = null, string? contains = null, string? excludes = null) =>
        new("g1", new DateOnly(2026, fromMonth, fromDay), new DateOnly(2026, toMonth, toDay),
            subGroupIds, minPercent, minInvited, contains, excludes);

    /// <summary>
    /// One event per pattern position, one day apart starting at <paramref name="first"/>.
    /// Each member's pattern says how they answered: A accepted, D declined, U unanswered, W waiting list, '-' not invited.
    /// </summary>
    public static List<EventInfo> Events(DateTimeOffset first, IReadOnlyDictionary<string, string> patterns, string name = "Training")
    {
        var count = patterns.Values.Max(p => p.Length);
        var events = new List<EventInfo>();
        for (var i = 0; i < count; i++)
        {
            var responses = new Dictionary<string, Rsvp>();
            foreach (var (memberId, pattern) in patterns)
            {
                if (i >= pattern.Length || pattern[i] == '-') continue;
                responses[memberId] = pattern[i] switch
                {
                    'A' => Rsvp.Accepted,
                    'D' => Rsvp.Declined,
                    'U' => Rsvp.Unanswered,
                    'W' => Rsvp.Other,
                    var c => throw new ArgumentException($"Unknown pattern character '{c}'."),
                };
            }

            var start = first.AddDays(i);
            events.Add(new EventInfo($"e{start:MMdd}-{name}", name, start, start.AddMinutes(90), false, responses));
        }

        return events;
    }

    public static Dictionary<string, string> Patterns(params (string Member, string Pattern)[] entries) =>
        entries.ToDictionary(e => e.Member, e => e.Pattern);

    public static MemberAttendanceDto MemberStats(
        string name, int invited = 10, int attended = 8, double? rate = null,
        QualificationStatus status = QualificationStatus.Qualified,
        EngagementLevel level = EngagementLevel.Good, int score = 70, string[]? subGroups = null) =>
        new(name.ToLowerInvariant(), name, subGroups ?? [], invited, attended, invited - attended, 0,
            rate ?? Math.Round(100.0 * attended / invited, 1), 100,
            CurrentStreak: 2, LongestStreak: 4,
            FirstInvited: new DateOnly(2026, 1, 1), LastAttended: new DateOnly(2026, 6, 20), DaysSinceLastAttended: 10,
            SessionsPerWeek: 1.5, RecentRate: 75, TrendDelta: 5, Trend: AttendanceTrend.Stable,
            EngagementScore: score, Level: level, Status: status, RecentMarks: [AttendanceMark.Attended]);
}
