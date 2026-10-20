using Microsoft.Extensions.Options;
using Kyuplan.Api.Configuration;
using Kyuplan.Api.Domain;
using Kyuplan.Api.Infrastructure;

namespace Kyuplan.Api.Spond;

/// <summary>
/// Generated sample data for a fictional karate club, so the whole stack can be tried without Spond
/// credentials. Deterministic per day: the same day always yields the same members and RSVPs. The main group is named
/// after the instance's <see cref="BrandingOptions.OrganizationName"/>.
/// </summary>
public sealed class DemoDataSource(ClubClock clock, IOptions<BrandingOptions> branding) : IDataSource
{
    private const int HistoryDays = 540;

    /// <summary>Reaches past the six-month event search window, so the next exam (every March and September) is always in sight.</summary>
    private const int FutureDays = 200;

    /// <summary>The further ahead an upcoming event is, the fewer members have answered yet; the share bottoms out at this many days ahead.</summary>
    private const int AnswerFadeDays = 21;

    private static readonly string[] LastNames =
    [
        "Becker", "Schmitz", "Weber", "Fischer", "Wagner", "Hoffmann", "Koch", "Richter", "Klein", "Wolf",
        "Neumann", "Schwarz", "Zimmermann", "Braun", "Krüger", "Hartmann", "Lange", "Schmitt", "Werner", "Krause",
        "Meier", "Lehmann", "Köhler", "Herrmann", "König", "Walter", "Mayer", "Huber", "Kaiser", "Fuchs",
        "Peters", "Lang", "Scholz", "Möller", "Weiß", "Jung",
    ];

    private sealed record Snapshot(
        DateOnly Day,
        IReadOnlyList<GroupInfo> Groups,
        IReadOnlyDictionary<string, IReadOnlyList<EventInfo>> Events);

    /// <summary>One training slot of the weekly schedule. <c>SubGroupId</c> null means the whole group is invited.</summary>
    private sealed record Slot(DayOfWeek Day, TimeOnly Start, TimeOnly End, string Name, string? SubGroupId);

    /// <summary>How a fictional member behaves: how often they come, how reliably they answer, whether it fades.</summary>
    private sealed record Profile(
        MemberInfo Info,
        double BaseProbability,
        double Diligence,
        DateOnly Joined,
        double Drift,
        DateOnly? StoppedComing);

    private volatile Snapshot? _snapshot;

    public bool IsDemo => true;

    public Task<IReadOnlyList<GroupInfo>> GetGroupsAsync(CancellationToken cancellationToken) =>
        Task.FromResult(Current().Groups);

    public Task<IReadOnlyList<EventInfo>> GetEventsAsync(
        string groupId, DateTimeOffset from, DateTimeOffset to, CancellationToken cancellationToken)
    {
        IReadOnlyList<EventInfo> events = Current().Events.GetValueOrDefault(groupId) is { } all
            ? all.Where(e => e.End >= from && e.End <= to).ToList()
            : [];
        return Task.FromResult(events);
    }

    public void InvalidateCache() => _snapshot = null;

    private Snapshot Current()
    {
        var today = clock.Today;
        if (_snapshot is { } snapshot && snapshot.Day == today) return snapshot;
        return _snapshot = Build(today);
    }

    private string DemoClubName() => branding.Value.OrganizationName.Trim() is { Length: > 0 } name ? name : "Demo Dojo";

    private Snapshot Build(DateOnly today)
    {
        var rng = new Random(20240601);
        var groups = new List<GroupInfo>();
        var events = new Dictionary<string, IReadOnlyList<EventInfo>>();

        // Dojo: three age classes with trainings on fixed weekdays, plus club-wide events.
        var dojoSubGroups = new[]
        {
            new SubGroupInfo("sg-kids", "Kinder"),
            new SubGroupInfo("sg-youth", "Jugend"),
            new SubGroupInfo("sg-adults", "Erwachsene"),
        };

        var dojoProfiles = new List<Profile>();
        dojoProfiles.AddRange(CreateProfiles(rng, today, "dojo-k", "sg-kids", 0.55, 0.95,
            ["Emma", "Noah", "Mia", "Ben", "Lina", "Finn", "Ella", "Paul", "Leni"], lastNameOffset: 0, youngestAge: 7, ageSpread: 6));
        dojoProfiles.AddRange(CreateProfiles(rng, today, "dojo-j", "sg-youth", 0.35, 0.92,
            ["Jonas", "Hannah", "Leon", "Sophie", "Elias", "Clara", "Felix", "Marie", "Luca"], lastNameOffset: 9, youngestAge: 12, ageSpread: 6));
        dojoProfiles.AddRange(CreateProfiles(rng, today, "dojo-a", "sg-adults", 0.30, 0.95,
            ["Thomas", "Sabine", "Michael", "Julia", "Stefan", "Anna", "Markus", "Katrin", "Daniel", "Nina", "Frank", "Petra"],
            lastNameOffset: 18, youngestAge: 20, ageSpread: 30));

        var dojoSlots = new Slot[]
        {
            new(DayOfWeek.Monday, new(17, 0), new(18, 0), "Kindertraining", "sg-kids"),
            new(DayOfWeek.Thursday, new(17, 0), new(18, 0), "Kindertraining", "sg-kids"),
            new(DayOfWeek.Tuesday, new(18, 0), new(19, 30), "Jugendtraining", "sg-youth"),
            new(DayOfWeek.Friday, new(17, 30), new(19, 0), "Jugendtraining", "sg-youth"),
            new(DayOfWeek.Monday, new(19, 0), new(20, 30), "Training Erwachsene", "sg-adults"),
            new(DayOfWeek.Wednesday, new(19, 30), new(21, 0), "Training Erwachsene", "sg-adults"),
        };

        groups.Add(new GroupInfo("demo-dojo", $"{DemoClubName()} (Demo)", dojoSubGroups, dojoProfiles.Select(p => p.Info).ToList()));
        events["demo-dojo"] = BuildEvents(rng, today, "demo-dojo", dojoProfiles, dojoSlots, withSpecialEvents: true);

        // Competition team: a small group without subgroups.
        var teamProfiles = CreateProfiles(rng, today, "team", null, 0.6, 0.97,
            ["Tim", "Laura", "Jan", "Lea", "Max", "Sarah", "Nico", "Amelie"], lastNameOffset: 30, youngestAge: 15, ageSpread: 14);
        var teamSlots = new Slot[]
        {
            new(DayOfWeek.Tuesday, new(20, 0), new(21, 30), "Wettkampftraining", null),
            new(DayOfWeek.Saturday, new(9, 0), new(11, 0), "Kumite-Training", null),
        };

        groups.Add(new GroupInfo("demo-team", "Wettkampfteam (Demo)", [], teamProfiles.Select(p => p.Info).ToList()));
        events["demo-team"] = BuildEvents(rng, today, "demo-team", teamProfiles, teamSlots, withSpecialEvents: false);

        return new Snapshot(today, groups, events);
    }

    private static List<Profile> CreateProfiles(
        Random rng, DateOnly today, string idPrefix, string? subGroupId,
        double minProbability, double maxProbability, string[] firstNames, int lastNameOffset, int youngestAge, int ageSpread)
    {
        var profiles = new List<Profile>();
        for (var i = 0; i < firstNames.Length; i++)
        {
            var info = new MemberInfo(
                $"{idPrefix}-{i + 1:00}",
                $"{firstNames[i]} {LastNames[(lastNameOffset + i) % LastNames.Length]}",
                subGroupId is null ? [] : [subGroupId],
                BirthDate(today, youngestAge + i * 5 % ageSpread, i));

            // Every fifth member joined recently, every eighth one has drifted away lately.
            var joined = i % 5 == 4 ? today.AddDays(-rng.Next(30, 100)) : today.AddDays(-HistoryDays - 30);
            DateOnly? stopped = i % 8 == 7 ? today.AddDays(-rng.Next(35, 80)) : null;

            // Skewed towards the high end: most members of a healthy club come regularly, a few rarely.
            var probability = minProbability + Math.Pow(rng.NextDouble(), 0.5) * (maxProbability - minProbability);

            profiles.Add(new Profile(
                info,
                probability,
                0.55 + rng.NextDouble() * 0.45,
                joined,
                -0.25 + rng.NextDouble() * 0.45,
                stopped));
        }

        return profiles;
    }

    /// <summary>
    /// A birth date that gives <paramref name="age"/> today, and a birthday that is spread over the year. Worked out from the index and
    /// not drawn from the random generator, so that adding it left every other generated value as it was.
    /// </summary>
    private static DateOnly BirthDate(DateOnly today, int age, int index) => today.AddYears(-age).AddDays(-(index * 53 % 330 + 1));

    private List<EventInfo> BuildEvents(
        Random rng, DateOnly today, string groupId, List<Profile> profiles, Slot[] slots, bool withSpecialEvents)
    {
        var now = clock.Now;
        var historyStart = today.AddDays(-HistoryDays);
        var events = new List<EventInfo>();
        var eventsToday = 0;

        for (var day = historyStart; day <= today.AddDays(FutureDays); day = day.AddDays(1))
        {
            eventsToday = 0;
            var onBreak = IsBreak(day);

            foreach (var slot in slots.Where(s => s.Day == day.DayOfWeek))
                Add(day, slot.Name, slot.Start, slot.End, slot.SubGroupId, onBreak || rng.NextDouble() < 0.03);

            if (!withSpecialEvents) continue;

            if (day.DayOfWeek == DayOfWeek.Saturday && day.DayNumber / 7 % 4 == 0)
                Add(day, "Offenes Training (alle)", new(10, 0), new(12, 0), null, onBreak);
            if (day.Month is 3 or 9 && day.DayOfWeek == DayOfWeek.Saturday && day.Day is >= 22 and <= 28)
                Add(day, "Gürtelprüfung", new(10, 0), new(13, 0), null, cancelled: false);
            if (day is { Month: 7, DayOfWeek: DayOfWeek.Saturday, Day: >= 8 and <= 14 })
                Add(day, "Sommerfest", new(15, 0), new(20, 0), null, cancelled: false);
            if (day.Month is 5 or 11 && day.DayOfWeek == DayOfWeek.Sunday && day.Day is >= 8 and <= 14)
                Add(day, "Turnier", new(9, 0), new(16, 0), "sg-youth", cancelled: false);
        }

        return events;

        void Add(DateOnly day, string name, TimeOnly start, TimeOnly end, string? subGroupId, bool cancelled)
        {
            var startAt = clock.At(day, start);
            var endAt = clock.At(day, end);

            var responses = new Dictionary<string, Rsvp>();
            foreach (var profile in profiles)
            {
                if (subGroupId is not null && !profile.Info.SubGroupIds.Contains(subGroupId)) continue;
                if (day < profile.Joined) continue;
                responses[profile.Info.Id] = Respond(rng, profile, day, historyStart, endAt > now, cancelled);
            }

            if (responses.Count == 0) return;

            // Named by day, not by position: the history window moves with "today", and exams and saved filters keep
            // the ids of the events they stand for.
            events.Add(new EventInfo($"{groupId}-{day:yyyyMMdd}-{++eventsToday}", name, startAt, endAt, cancelled, responses));
        }
    }

    private static Rsvp Respond(Random rng, Profile profile, DateOnly day, DateOnly historyStart, bool upcoming, bool cancelled)
    {
        if (cancelled) return Rsvp.Unanswered;

        var progress = Math.Min(1.0, (day.DayNumber - historyStart.DayNumber) / (double)HistoryDays);
        var probability = profile.BaseProbability + profile.Drift * (progress - 0.5);
        if (day.Month is 7 or 8) probability -= 0.10;
        if (profile.StoppedComing is { } stopped && day >= stopped) probability = 0.03;
        probability = Math.Clamp(probability, 0.02, 0.98);

        if (upcoming)
        {
            // Only part of the invitees has answered so far, fewer the further away the event is.
            var daysAhead = Math.Clamp(day.DayNumber - historyStart.DayNumber - HistoryDays, 0, AnswerFadeDays);
            var answered = profile.Diligence * (1 - daysAhead / (double)AnswerFadeDays * 0.7);
            if (rng.NextDouble() > answered) return Rsvp.Unanswered;
            return rng.NextDouble() < probability ? Rsvp.Accepted : Rsvp.Declined;
        }

        if (rng.NextDouble() < probability) return Rsvp.Accepted;
        return rng.NextDouble() < profile.Diligence ? Rsvp.Declined : Rsvp.Unanswered;
    }

    /// <summary>Christmas and summer holidays: trainings are cancelled.</summary>
    private static bool IsBreak(DateOnly day) =>
        (day.Month == 12 && day.Day >= 23) || (day.Month == 1 && day.Day <= 6) ||
        (day.Month == 7 && day.Day >= 15) || (day.Month == 8 && day.Day <= 5);
}
