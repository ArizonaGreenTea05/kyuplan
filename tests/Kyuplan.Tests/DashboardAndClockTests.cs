using Microsoft.Extensions.Time.Testing;
using Kyuplan.Api.Configuration;
using Kyuplan.Api.Domain;
using Kyuplan.Api.Infrastructure;
using Kyuplan.Api.Statistics;
using Kyuplan.Contracts;
using static Kyuplan.Tests.TestData;

namespace Kyuplan.Tests;

public class DashboardBuilderTests
{
    private static readonly StatisticsOptions Options = new() { DefaultMinAttendancePercent = 75, DefaultMinInvitedEvents = 3, UpcomingDays = 14 };

    private static EventInfo Upcoming(string id, DateTimeOffset start, params (string, Rsvp)[] responses) =>
        new(id, "Training", start, start.AddMinutes(90), false, responses.ToDictionary(r => r.Item1, r => r.Item2));

    [Fact]
    public void Upcoming_lists_future_events_inside_the_horizon_with_rsvp_progress()
    {
        var group = Group([Member("a"), Member("b"), Member("c")]);
        var events = new List<EventInfo>
        {
            Upcoming("soon", At(7, 2), ("a", Rsvp.Accepted), ("b", Rsvp.Declined), ("c", Rsvp.Unanswered)),
            Upcoming("far", At(8, 15), ("a", Rsvp.Accepted)),                // beyond 14 days
            Upcoming("cancelled", At(7, 1), ("a", Rsvp.Accepted)) with { Cancelled = true },
        };
        events.AddRange(Events(At(6, 20), Patterns(("a", "AAA"))));          // past → not upcoming

        var dashboard = DashboardBuilder.Build(group, null, events, 90, Now, Options);

        var soon = Assert.Single(dashboard.Upcoming);
        Assert.Equal("soon", soon.Id);
        Assert.Equal((3, 1, 1, 1), (soon.Invited, soon.Accepted, soon.Declined, soon.Unanswered));
    }

    [Fact]
    public void Look_back_window_ends_today_and_spans_the_requested_days()
    {
        var dashboard = DashboardBuilder.Build(Group([Member("a")]), null, [], 30, Now, Options);

        Assert.Equal(new DateOnly(2026, 6, 30), dashboard.To);
        Assert.Equal(new DateOnly(2026, 6, 1), dashboard.From); // 30 days including today
        Assert.Equal(75, dashboard.MinAttendancePercent);
    }

    [Fact]
    public void Top_members_rank_by_score_and_needs_attention_lists_the_weakest_with_a_verdict()
    {
        var events = Events(At(6, 1), Patterns(
            ("star", "AAAAAAAAAA"), ("ok", "AAAAADDAAA"), ("gone", "DDDDDDDDDD"), ("lapsed", "UUUUUUUUUU"), ("newcomer", "UU--------")));
        var group = Group(new[] { "star", "ok", "gone", "lapsed", "newcomer" }.Select(id => Member(id)));

        var dashboard = DashboardBuilder.Build(group, null, events, 90, Now, Options);

        Assert.Equal("STAR", dashboard.TopMembers[0].Name);
        Assert.DoesNotContain(dashboard.TopMembers, m => m.MemberId == "newcomer");  // too few events for a verdict
        Assert.Equal(["lapsed", "gone"], dashboard.NeedsAttention.Select(m => m.MemberId));
    }
}

public class ClubClockTests
{
    private static readonly TimeZoneInfo Berlin = TimeZoneInfo.FindSystemTimeZoneById("Europe/Berlin");

    [Fact]
    public void Today_is_the_club_date_not_the_utc_date()
    {
        // 22:30 UTC on 30 June is already 00:30 on 1 July in Germany (CEST).
        var clock = new ClubClock(new FakeTimeProvider(new DateTimeOffset(2026, 6, 30, 22, 30, 0, TimeSpan.Zero)), Berlin);

        Assert.Equal(new DateOnly(2026, 7, 1), clock.Today);
    }

    [Fact]
    public void Start_of_day_uses_the_offset_valid_on_that_day_across_a_dst_change()
    {
        var clock = new ClubClock(TimeProvider.System, Berlin);

        Assert.Equal(TimeSpan.FromHours(1), clock.StartOfDay(new DateOnly(2026, 3, 28)).Offset);
        Assert.Equal(TimeSpan.FromHours(1), clock.StartOfDay(new DateOnly(2026, 3, 29)).Offset); // clocks jump at 02:00
        Assert.Equal(TimeSpan.FromHours(2), clock.StartOfDay(new DateOnly(2026, 3, 30)).Offset);
    }

    [Fact]
    public void At_builds_a_local_timestamp()
    {
        var clock = new ClubClock(TimeProvider.System, Berlin);

        var winter = clock.At(new DateOnly(2026, 1, 15), new TimeOnly(18, 30));
        var summer = clock.At(new DateOnly(2026, 7, 15), new TimeOnly(18, 30));

        Assert.Equal(new DateTimeOffset(2026, 1, 15, 17, 30, 0, TimeSpan.Zero), winter);
        Assert.Equal(new DateTimeOffset(2026, 7, 15, 16, 30, 0, TimeSpan.Zero), summer);
    }
}
