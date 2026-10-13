using System.Globalization;
using Kyuplan.Client.ViewModels;
using Kyuplan.Contracts;
using static Kyuplan.Tests.TestData;

namespace Kyuplan.Tests;

/// <summary>What the event pickers show of an event, and how they find it.</summary>
public class EventOptionTests
{
    private static readonly EventOption Exam = new(new("exam", "Gürtelprüfung", At(3, 28, 10)));    // Saturday
    private static readonly EventOption Training = new(new("train", "Kindertraining", At(6, 20, 17)));

    public EventOptionTests()
    {
        CultureInfo.CurrentCulture = CultureInfo.CurrentUICulture = new CultureInfo("en-GB");
    }

    [Fact]
    public void An_event_is_shown_by_its_date_first()
    {
        Assert.Equal("Sat", Exam.Weekday);
        Assert.Equal("28", Exam.DayNumber);
        Assert.Equal("Mar", Exam.MonthText);
        Assert.Equal("Sat, 28 Mar 2026", Exam.DateText); // with the year, unlike the label
        Assert.Equal("10:00", Exam.TimeText);
        Assert.Equal("Gürtelprüfung", Exam.EventName);
    }

    [Fact]
    public void The_empty_entry_has_no_date()
    {
        var none = EventOption.None;

        Assert.Equal("", none.Weekday + none.DayNumber + none.MonthText + none.DateText + none.TimeText);
        Assert.Null(none.Day);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void An_empty_search_fits_every_event(string? search)
    {
        Assert.True(Exam.Matches(search));
        Assert.True(Training.Matches(search));
    }

    [Theory]
    [InlineData("prüfung")]
    [InlineData("PRÜFUNG")]
    [InlineData("pruefung", false)]  // "ue" is not "ü"
    [InlineData("prufung")]          // accents do not matter
    [InlineData("gürtel")]
    [InlineData("training", false)]
    public void The_name_is_found_anywhere_in_it(string search, bool expected = true) =>
        Assert.Equal(expected, Exam.Matches(search));

    [Theory]
    [InlineData("28.3.")]
    [InlineData("28.03.2026")]
    [InlineData("2026-03-28")]
    [InlineData("2026-03")]
    [InlineData("sat")]
    [InlineData("saturday")]
    [InlineData("mar")]
    [InlineData("march")]
    [InlineData("28 mar")]
    [InlineData("10:00")]
    [InlineData("2026")]
    [InlineData("29.3.", false)]
    [InlineData("sun", false)]
    [InlineData("apr", false)]
    [InlineData("11:00", false)]
    public void The_date_is_found_however_it_is_written(string search, bool expected = true) =>
        Assert.Equal(expected, Exam.Matches(search));

    [Fact]
    public void A_day_number_finds_the_day_and_not_every_day_of_the_year()
    {
        Assert.True(Training.Matches("20"));   // 20 June 2026
        Assert.False(Training.Matches("26"));  // inside "2026", but not the day
        Assert.False(Training.Matches("026"));
        Assert.True(Training.Matches("2026"));
    }

    [Fact]
    public void Every_word_of_a_search_has_to_fit()
    {
        Assert.True(Exam.Matches("prüfung mar 2026"));
        Assert.True(Exam.Matches("  mar   prüfung "));
        Assert.False(Exam.Matches("prüfung jun"));
        Assert.False(Exam.Matches("prüfung mar zzz"));
        Assert.True(Training.Matches("kinder jun sat")); // 20 June 2026 is a Saturday
    }

    [Fact]
    public void The_empty_entry_fits_no_search()
    {
        Assert.False(EventOption.None.Matches("a"));
        Assert.True(EventOption.None.Matches(""));
    }

    [Fact]
    public void The_search_follows_the_language()
    {
        var exam = new EventOption(new("exam", "Gürtelprüfung", At(3, 28, 10)));
        Assert.False(exam.Matches("samstag"));

        CultureInfo.CurrentCulture = CultureInfo.CurrentUICulture = new CultureInfo("de-DE");
        exam.Refresh();

        Assert.True(exam.Matches("samstag"));
        Assert.True(exam.Matches("märz"));
        Assert.True(exam.Matches("28.3."));
        Assert.False(exam.Matches("saturday"));
        Assert.Contains("28. Mär", exam.DateText);
    }

    [Fact]
    public void Refreshing_raises_a_change_of_every_text()
    {
        var changed = new List<string?>();
        Exam.PropertyChanged += (_, e) => changed.Add(e.PropertyName);

        Exam.Refresh();

        Assert.Contains(string.Empty, changed); // "all properties": the label, the date texts, ...
    }
}
