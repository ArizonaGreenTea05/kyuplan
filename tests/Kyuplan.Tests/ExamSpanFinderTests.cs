using Kyuplan.Api.Domain;
using Kyuplan.Api.Statistics;
using static Kyuplan.Tests.TestData;

namespace Kyuplan.Tests;

/// <summary>The default time span of the statistics page: from the last exam to the next one.</summary>
public class ExamSpanFinderTests
{
    private static readonly DateTimeOffset Today = Now; // 30 June 2026, noon

    private static EventInfo Event(string id, string name, DateTimeOffset start, bool cancelled = false) =>
        new(id, name, start, start.AddHours(2), cancelled, new Dictionary<string, Rsvp>());

    private static (string From, string To)? Find(string? keyword, params EventInfo[] events) =>
        ExamSpanFinder.Find(events, keyword, Today) is { } span ? (span.From.Id, span.To.Id) : null;

    [Fact]
    public void The_span_runs_from_the_last_exam_to_the_next_one()
    {
        var span = Find("Prüfung",
            Event("old", "Gürtelprüfung", At(1, 10)),
            Event("last", "Gürtelprüfung", At(3, 28)),
            Event("training", "Training", At(6, 20)),
            Event("next", "Gürtelprüfung", At(9, 26)),
            Event("later", "Gürtelprüfung", At(12, 5)));

        Assert.Equal(("last", "next"), span);
    }

    [Fact]
    public void The_input_order_does_not_matter()
    {
        var span = Find("Prüfung",
            Event("later", "Gürtelprüfung", At(12, 5)),
            Event("last", "Gürtelprüfung", At(3, 28)),
            Event("next", "Gürtelprüfung", At(9, 26)),
            Event("old", "Gürtelprüfung", At(1, 10)));

        Assert.Equal(("last", "next"), span);
    }

    [Fact]
    public void The_name_only_has_to_contain_the_keyword_in_any_case()
    {
        var span = Find("prüfung",
            Event("a", "PRÜFUNG Kinder", At(3, 28)),
            Event("b", "Dan-Prüfung (Schwarzgurt)", At(9, 26)));

        Assert.Equal(("a", "b"), span);
    }

    [Fact]
    public void A_cancelled_exam_is_neither_the_next_nor_the_last()
    {
        var span = Find("Prüfung",
            Event("last", "Gürtelprüfung", At(3, 28)),
            Event("cancelled-last", "Gürtelprüfung", At(6, 1), cancelled: true),
            Event("cancelled-next", "Gürtelprüfung", At(8, 1), cancelled: true),
            Event("next", "Gürtelprüfung", At(9, 26)));

        Assert.Equal(("last", "next"), span);
    }

    [Fact]
    public void An_exam_that_has_already_started_today_counts_as_past()
    {
        var span = Find("Prüfung",
            Event("last", "Gürtelprüfung", At(3, 28)),
            Event("today", "Gürtelprüfung", At(6, 30, 10)), // started at 10:00, it is 12:00
            Event("next", "Gürtelprüfung", At(9, 26)));

        Assert.Equal(("today", "next"), span);
    }

    [Fact]
    public void An_exam_later_today_is_the_next_one()
    {
        var span = Find("Prüfung",
            Event("last", "Gürtelprüfung", At(3, 28)),
            Event("today", "Gürtelprüfung", At(6, 30, 18)));

        Assert.Equal(("last", "today"), span);
    }

    [Fact]
    public void Several_upcoming_exams_use_the_nearest_one_and_the_last_before_it()
    {
        var span = Find("Prüfung",
            Event("past", "Gürtelprüfung", At(3, 28)),
            Event("near", "Gürtelprüfung", At(7, 11)),
            Event("far", "Gürtelprüfung", At(12, 5)));

        Assert.Equal(("past", "near"), span);
    }

    [Fact]
    public void Without_an_upcoming_exam_there_is_nothing_to_suggest()
    {
        Assert.Null(Find("Prüfung", Event("a", "Gürtelprüfung", At(3, 28)), Event("b", "Gürtelprüfung", At(5, 1))));
    }

    [Fact]
    public void Without_an_earlier_exam_there_is_nothing_to_suggest()
    {
        Assert.Null(Find("Prüfung", Event("a", "Gürtelprüfung", At(9, 26)), Event("t", "Training", At(3, 1))));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Without_a_keyword_there_is_nothing_to_suggest(string? keyword)
    {
        Assert.Null(Find(keyword, Event("a", "Gürtelprüfung", At(3, 28)), Event("b", "Gürtelprüfung", At(9, 26))));
    }

    private static (string From, string To)? FindRecent(params EventInfo[] events) =>
        ExamSpanFinder.FindRecent(events, Today) is { } span ? (span.From.Id, span.To.Id) : null;

    [Fact]
    public void Without_exams_the_recent_span_runs_from_the_first_event_of_six_months_to_the_latest_one_so_far()
    {
        var span = FindRecent(
            Event("too-old", "Training", new DateTimeOffset(2025, 12, 29, 18, 0, 0, Today.Offset)), // before 1 January 2026
            Event("first", "Training", At(1, 1)),                                                    // 30 June minus six months, plus a day
            Event("middle", "Training", At(4, 14)),
            Event("latest", "Training", At(6, 29)),
            Event("tomorrow", "Training", At(7, 1)));

        Assert.Equal(("first", "latest"), span);
    }

    [Fact]
    public void An_event_later_today_belongs_to_the_recent_span_because_its_day_is_today()
    {
        var span = FindRecent(Event("a", "Training", At(6, 2)), Event("today", "Training", At(6, 30, 19)));

        Assert.Equal(("a", "today"), span);
    }

    [Fact]
    public void Cancelled_events_are_not_part_of_the_recent_span()
    {
        var span = FindRecent(
            Event("cancelled-first", "Training", At(2, 1), cancelled: true),
            Event("first", "Training", At(2, 3)),
            Event("last", "Training", At(6, 20)),
            Event("cancelled-last", "Training", At(6, 29), cancelled: true));

        Assert.Equal(("first", "last"), span);
    }

    [Fact]
    public void A_single_recent_event_is_both_ends_of_the_span()
    {
        Assert.Equal(("only", "only"), FindRecent(Event("only", "Training", At(5, 5))));
    }

    [Fact]
    public void Without_a_recent_event_there_is_no_recent_span()
    {
        Assert.Null(FindRecent(Event("future", "Training", At(7, 15))));
        Assert.Null(FindRecent());
    }

    [Fact]
    public void Exams_starting_at_the_same_time_pick_the_next_one_by_id_and_never_pair_with_each_other()
    {
        var span = Find("Prüfung",
            Event("last", "Gürtelprüfung", At(3, 28)),
            Event("next-b", "Gürtelprüfung Jugend", At(9, 26, 10)),
            Event("next-a", "Gürtelprüfung Kinder", At(9, 26, 10)));

        Assert.Equal(("last", "next-a"), span);
    }
}
