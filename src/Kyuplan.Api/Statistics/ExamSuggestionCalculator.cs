using Kyuplan.Api.Domain;
using Kyuplan.Api.Persistence;
using Kyuplan.Contracts;

namespace Kyuplan.Api.Statistics;

/// <summary>
/// Judges who is fit for an exam. Pure and deterministic like <see cref="AttendanceCalculator"/>: everything it needs, including the
/// attendance the chosen filter produced, is passed in. The rules are documented in docs/exam-planning.md.
/// <para>
/// A member is fit when they meet, <b>on the day of the exam</b>, everything the belt they would take asks for coming from the belt they hold
/// (minimum age, minimum waiting time since they got that belt) and attended enough of the events the filter looks at.
/// </para>
/// </summary>
internal static class ExamSuggestionCalculator
{
    /// <param name="examDate">The day of the exam: age and waiting time are counted until then.</param>
    /// <param name="beltsInOrder">Every belt with its prerequisites, in the order of the progression (the first belt first).</param>
    /// <param name="people">The members to judge: the database row (belt, days) with what Spond says (subgroups).</param>
    /// <param name="attendance">What the attendance statistics say, by Spond id. A member without an entry was not invited to any event the filter counts.</param>
    /// <param name="subGroupNames">Subgroup names by id.</param>
    /// <param name="lastExams">The day of the latest exam before this one that each member (by id) took part in.</param>
    /// <param name="invitations">What each member (by id) answered to the invitation to this exam; members who are not in here are not invited.</param>
    /// <param name="preSaved">The members (by id) saved for this exam.</param>
    /// <param name="minAverageStars">The average evaluation (0–5) a member needs over the filter's time span; 0 = not asked.</param>
    public static List<ExamSuggestionDto> Calculate(
        DateOnly examDate,
        IReadOnlyList<Belt> beltsInOrder,
        IEnumerable<(Member Member, MemberInfo Info)> people,
        IReadOnlyDictionary<string, MemberAttendanceDto> attendance,
        IReadOnlyDictionary<string, string> subGroupNames,
        IReadOnlyDictionary<int, DateOnly> lastExams,
        IReadOnlyDictionary<int, AttendanceMark> invitations,
        IReadOnlySet<int> preSaved,
        double minAverageStars = 0)
    {
        return people
            .Select(p => Judge(p.Member, p.Info))
            .OrderBy(s => Rank(s.Verdict))
            .ThenByDescending(s => s.AttendanceRate)
            .ThenByDescending(s => s.Invited)
            .ThenBy(s => s.Name, StringComparer.CurrentCultureIgnoreCase)
            .ThenBy(s => s.MemberId)
            .ToList();

        ExamSuggestionDto Judge(Member member, MemberInfo info)
        {
            attendance.TryGetValue(info.Id, out var stats);
            var status = stats?.Status ?? QualificationStatus.InsufficientData;

            // No event to average over is no evaluation to speak of, like too few events for the attendance.
            var averageStars = stats?.AverageStars;
            var ratingCheck = minAverageStars <= 0 || (averageStars is { } average && average >= minAverageStars) ? CheckState.Met : CheckState.NotMet;
            var lastExam = lastExams.TryGetValue(member.Id, out var last) ? last : (DateOnly?)null;
            var age = member.BirthDate is { } birth ? AgeOn(birth, examDate) : (int?)null;

            // The belt held decides what comes next, and so what is asked; a member without a belt has neither.
            var holds = member.BeltId;
            var beltSince = holds is null ? null : member.BeltSince;
            var waitingSince = holds is null ? null : beltSince ?? lastExam;

            Assessment? chosen = null;
            if (holds is { } heldId)
            {
                // Belts that ask for the one held, in the order of the progression. The highest one the member is fit for wins (somebody
                // who qualifies for blue-brown and for the 1st brown is put forward for brown), else the nearest one.
                var candidates = beltsInOrder
                    .Select(b => (Belt: b, Link: b.Prerequisites.FirstOrDefault(p => p.RequiredBeltId == heldId)))
                    .Where(c => c.Link is not null)
                    .Select(c => Assess(c.Belt, c.Link!, age, waitingSince, status, ratingCheck))
                    .ToList();
                chosen = candidates.LastOrDefault(c => c.Verdict == SuggestionVerdict.Fit) ?? candidates.FirstOrDefault();
            }

            var verdict = holds is null ? SuggestionVerdict.NeedsData : chosen?.Verdict ?? SuggestionVerdict.NoNextBelt;
            var waited = waitingSince is { } since ? MonthsBetween(since, examDate) : (int?)null;

            return new ExamSuggestionDto(
                member.Id, member.Name,
                info.SubGroupIds.Select(id => subGroupNames.GetValueOrDefault(id)).OfType<string>().ToList(),
                holds, chosen?.Belt.Id,
                chosen?.MinAge ?? 0, age, chosen?.AgeCheck ?? CheckState.Met,
                chosen?.MinWait ?? 0, waited, chosen?.WaitCheck ?? CheckState.Met,
                beltSince, lastExam, waitingSince,
                stats?.Invited ?? 0, stats?.Attended ?? 0, stats?.AttendanceRate ?? 0, status, stats?.EngagementScore ?? 0,
                verdict,
                invitations.TryGetValue(member.Id, out var answer) ? answer : null,
                preSaved.Contains(member.Id),
                averageStars, minAverageStars, ratingCheck);
        }

        Assessment Assess(Belt belt, BeltPrerequisite link, int? age, DateOnly? waitingSince, QualificationStatus status, CheckState ratingCheck)
        {
            var ageCheck = Check(link.MinAgeYears, age);
            var waitCheck = Check(link.MinWaitMonths, waitingSince is { } since ? MonthsBetween(since, examDate) : null);

            // What is missed wins over what cannot be told: someone too young is "not yet" whether or not the waiting time is known.
            // Too few events for a verdict is no attendance to speak of, so it counts as missed too; so does an average below the one asked.
            var attendanceMet = status == QualificationStatus.Qualified;
            var verdict = !attendanceMet || ratingCheck == CheckState.NotMet || ageCheck == CheckState.NotMet || waitCheck == CheckState.NotMet ? SuggestionVerdict.NotYet
                : ageCheck == CheckState.Unknown || waitCheck == CheckState.Unknown ? SuggestionVerdict.NeedsData
                : SuggestionVerdict.Fit;

            return new Assessment(belt, link.MinAgeYears, ageCheck, link.MinWaitMonths, waitCheck, verdict);
        }
    }

    private sealed record Assessment(Belt Belt, int MinAge, CheckState AgeCheck, int MinWait, CheckState WaitCheck, SuggestionVerdict Verdict);

    /// <summary>Nothing asked is met; something asked about a day that is not known cannot be told.</summary>
    private static CheckState Check(int required, int? actual) =>
        required == 0 ? CheckState.Met
        : actual is null ? CheckState.Unknown
        : actual >= required ? CheckState.Met
        : CheckState.NotMet;

    private static int Rank(SuggestionVerdict verdict) => verdict switch
    {
        SuggestionVerdict.Fit => 0,
        SuggestionVerdict.NeedsData => 1,
        SuggestionVerdict.NotYet => 2,
        _ => 3,
    };

    /// <summary>
    /// The age in full years on <paramref name="day"/>; somebody who is not born yet is 0. Somebody born on 29 February has their
    /// birthday on 28 February in the years that have no 29th, like a month that ends on its last day (see <see cref="MonthsBetween"/>).
    /// </summary>
    internal static int AgeOn(DateOnly birth, DateOnly day)
    {
        if (day <= birth) return 0;

        var years = day.Year - birth.Year;
        return birth.AddYears(years) > day ? years - 1 : years;
    }

    /// <summary>
    /// The full months from <paramref name="from"/> to <paramref name="to"/>, i.e. how often <paramref name="from"/> plus a month still
    /// is not after <paramref name="to"/>. A month that is shorter than the day of <paramref name="from"/> ends on its last day (from 31 Jan,
    /// a month has passed on 28 Feb). 0 when <paramref name="to"/> is not after <paramref name="from"/>.
    /// </summary>
    internal static int MonthsBetween(DateOnly from, DateOnly to)
    {
        if (to <= from) return 0;

        var months = (to.Year - from.Year) * 12 + to.Month - from.Month;
        return from.AddMonths(months) > to ? months - 1 : months;
    }
}
