using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using Kyuplan.Client.Resources;
using Kyuplan.Contracts;

namespace Kyuplan.Client.ViewModels;

public enum KpiTone
{
    Neutral,
    Success,
    Warning,
}

/// <summary>A headline figure.</summary>
public sealed record KpiViewModel(string Label, string Value, string? Hint = null, KpiTone Tone = KpiTone.Neutral);

/// <summary>One bar of a simple bar chart. <see cref="Fraction"/> is 0–1 relative to the chart's tallest bar.</summary>
public sealed record BarViewModel(string Label, string Value, double Fraction, string Tooltip, bool IsHighlighted = false);

/// <summary>An entry of the member filter: one subgroup, ticked or not. Nothing ticked means everyone.</summary>
public sealed partial class SubGroupOption(string id, string name) : ObservableObject
{
    public string Id { get; } = id;

    /// <summary>The name of the subgroup as it comes from Spond.</summary>
    public string Name { get; } = name;

    [ObservableProperty]
    private bool _isSelected;
}

/// <summary>
/// An entry of the event lists that define the time span. The first entry of a list is the empty one
/// (<see cref="None"/>). The list is never replaced when the language changes (a replaced list loses its selection in
/// WPF): the entries read their texts again after <see cref="Refresh"/>. The pickers show an event by its date first (<see cref="Weekday"/>, <see cref="DayNumber"/>, <see cref="MonthText"/>,
/// <see cref="DateText"/>) and find it by name or date (<see cref="Matches"/>).
/// </summary>
public sealed class EventOption(EventChoiceDto? choice = null) : ObservableObject
{
    /// <summary>Id of the empty entry.</summary>
    public const string NoneId = "";

    private string[]? _dateParts;

    public string Id => choice?.Id ?? NoneId;

    /// <summary>The event's name as it comes from Spond; empty for the empty entry.</summary>
    public string EventName => choice?.Name ?? "";

    /// <summary>The day the event starts on, or <c>null</c> for the empty entry.</summary>
    public DateOnly? Day => choice is null ? null : DateOnly.FromDateTime(choice.Start.Date);

    /// <summary>"Gürtelprüfung · Sat, 26 Sep · 10:00", or the prompt of the empty entry.</summary>
    public string Label => choice is null ? Strings.Stats_ChooseEvent : $"{choice.Name} · {Display.EventStart(choice.Start)}";

    /// <summary>"Sat", the weekday of the event; empty for the empty entry.</summary>
    public string Weekday => Format("ddd");

    /// <summary>"26", the day of the month; empty for the empty entry.</summary>
    public string DayNumber => Format("%d");

    /// <summary>"Sep", the month; empty for the empty entry.</summary>
    public string MonthText => Format("MMM");

    /// <summary>"Sat, 26 Sep 2026": the full date; empty for the empty entry.</summary>
    public string DateText => choice is null ? "" : Display.EventDay(choice.Start);

    /// <summary>"10:00"; empty for the empty entry.</summary>
    public string TimeText => choice is null ? "" : Display.Time(choice.Start);

    public static EventOption None => new();

    /// <summary>
    /// Whether this event fits a search: every word of <paramref name="search"/> (separated by blanks) must occur in
    /// the name, or start one of the parts of the date as people write it — "prüfung sep", "26.9.", "2026-09",
    /// "samstag". A date word must start a part so that "26" finds the 26th and not every day of 2026. Case and
    /// accents do not matter. An empty search fits every event.
    /// </summary>
    public bool Matches(string? search)
    {
        if (string.IsNullOrWhiteSpace(search)) return true;
        if (choice is null) return false;

        var parts = _dateParts ??= DateParts(choice.Start);
        var compare = CultureInfo.CurrentCulture.CompareInfo;
        const CompareOptions Loose = CompareOptions.IgnoreCase | CompareOptions.IgnoreNonSpace;

        return search.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).All(word =>
            compare.IndexOf(choice.Name, word, Loose) >= 0 || parts.Any(part => compare.IsPrefix(part, word, Loose)));
    }

    public void Refresh()
    {
        _dateParts = null;
        OnPropertyChanged(string.Empty); // every text depends on the language
    }

    private string Format(string pattern) => choice?.Start.ToString(pattern, CultureInfo.CurrentCulture) ?? "";

    /// <summary>The parts of the date in the ways people write it: "Sat", "26", "Sep", "September", "2026", "26.09.2026", "2026-09-26", "10:00".</summary>
    private static string[] DateParts(DateTimeOffset start)
    {
        var text = string.Join(' ', Display.EventDay(start), start.ToString("dddd MMMM", CultureInfo.CurrentCulture),
            start.ToString("dd.MM.yyyy d.M.yyyy yyyy-MM-dd", CultureInfo.InvariantCulture), Display.Time(start));
        return text.Split([' ', ','], StringSplitOptions.RemoveEmptyEntries);
    }
}

/// <summary>An entry of the dashboard's period list; like <see cref="EventOption"/> it re-reads its label after <see cref="Refresh"/>.</summary>
public sealed class PeriodOption(int days) : ObservableObject
{
    public int Days { get; } = days;

    public string Label => Days switch
    {
        30 => Strings.Period_30Days,
        90 => Strings.Period_90Days,
        180 => Strings.Period_6Months,
        _ => Strings.Period_12Months,
    };

    public void Refresh() => OnPropertyChanged(nameof(Label));
}

/// <summary>Display model of one member: the DTO plus ready-to-show texts.</summary>
public sealed class MemberRowViewModel(MemberAttendanceDto dto)
{
    public MemberAttendanceDto Dto { get; } = dto;

    public string Name => Dto.Name;
    public string SubGroupsText => string.Join(", ", Dto.SubGroups);

    public int Invited => Dto.Invited;
    public int Attended => Dto.Attended;
    public int Declined => Dto.Declined;
    public int Unanswered => Dto.Unanswered;
    public string AttendedOfInvitedText => $"{Dto.Attended} / {Dto.Invited}";
    public string AttendedEventsText => string.Format(Strings.Row_AttendedOfInvited, Dto.Attended, Dto.Invited);

    public double AttendanceRate => Dto.AttendanceRate;
    public double AttendanceFraction => Dto.AttendanceRate / 100.0;
    public string AttendanceText => Display.Percent(Dto.AttendanceRate);

    /// <summary>What a screen reader says for the attendance bar.</summary>
    public string AttendanceAriaText => string.Format(Strings.Rate_Aria, AttendanceText, AttendedOfInvitedText);

    public double ResponseRate => Dto.ResponseRate;
    public string ResponseRateText => Display.Percent(Dto.ResponseRate);

    public int CurrentStreak => Dto.CurrentStreak;
    public int LongestStreak => Dto.LongestStreak;
    public string StreakText => string.Format(Strings.Row_Streak, Dto.CurrentStreak, Dto.LongestStreak);

    /// <summary>Sort key: members who never attended count as the longest absent.</summary>
    public int DaysSinceLastAttended => Dto.DaysSinceLastAttended ?? int.MaxValue;

    public string LastAttendedDateText => Dto.LastAttended is { } date ? Display.Date(date) : Strings.Row_NotInPeriod;

    public string LastAttendedAgoText => Dto.DaysSinceLastAttended switch
    {
        null => "",
        0 => Strings.Row_Today,
        1 => Strings.Row_Yesterday,
        var days => string.Format(Strings.Row_DaysAgo, days),
    };

    public string LastAttendedText => Dto.LastAttended is null ? LastAttendedDateText : $"{LastAttendedDateText} · {LastAttendedAgoText}";

    public double SessionsPerWeek => Dto.SessionsPerWeek;
    public string SessionsPerWeekText => string.Format(Strings.Row_PerWeek, Display.Number(Dto.SessionsPerWeek));

    /// <summary>Sort key: members without an evaluated or attended event come last.</summary>
    public double Stars => Dto.AverageStars ?? -1;

    /// <summary>"3.4", or "–" without an event to average over.</summary>
    public string StarsText => Dto.AverageStars is { } stars ? Display.Stars(stars) : Display.None;

    /// <summary>"5 of 8 events evaluated": how many of the events behind the average somebody evaluated; the rest count the default.</summary>
    public string StarsTip => string.Format(Strings.Rating_StarsEvents, Dto.EvaluatedEvents, Dto.StarEvents);

    public AttendanceTrend Trend => Dto.Trend;
    public string TrendGlyph => Display.TrendGlyph(Dto.Trend);
    public string TrendText => Display.TrendText(Dto.Trend, Dto.TrendDelta);

    public int EngagementScore => Dto.EngagementScore;
    public EngagementLevel Level => Dto.Level;
    public string LevelText => Display.Level(Dto.Level);

    public QualificationStatus Status => Dto.Status;
    public bool IsQualified => Dto.Status == QualificationStatus.Qualified;
    public string StatusText => Display.Status(Dto.Status);
    public string StatusTip => Display.StatusTip(Dto.Status);

    public IReadOnlyList<AttendanceMark> RecentMarks => Dto.RecentMarks;
}

public sealed class EventRowViewModel(EventAttendanceDto dto)
{
    public string Name => dto.Name;
    public DateTimeOffset Start => dto.Start;
    public string StartText => Display.EventStart(dto.Start);
    public int Invited => dto.Invited;
    public int Attended => dto.Attended;
    public string AttendedOfInvitedText => $"{dto.Attended} / {dto.Invited}";
    public string InvitedCameText => string.Format(Strings.Row_InvitedCame, dto.Attended, dto.Invited);
    public string DeclinedText => dto.Declined.ToString();
    public string UnansweredText => dto.Unanswered.ToString();
    public double AttendanceRate => dto.AttendanceRate;
    public double AttendanceFraction => dto.AttendanceRate / 100.0;
    public string AttendanceText => Display.Percent(dto.AttendanceRate);

    /// <summary>"3.4", or "–" when nobody attended and nobody was evaluated.</summary>
    public string StarsText => dto.AverageStars is { } stars ? Display.Stars(stars) : Display.None;
}

public sealed class UpcomingEventRowViewModel(UpcomingEventDto dto)
{
    public string Name => dto.Name;
    public string StartText => Display.EventStart(dto.Start);
    public int Invited => dto.Invited;
    public int Accepted => dto.Accepted;
    public int Declined => dto.Declined;
    public int Unanswered => dto.Unanswered;

    /// <summary>Shares of the invited people (0–1) for the three segments of the RSVP bar.</summary>
    public double AcceptedFraction => Share(dto.Accepted);
    public double DeclinedFraction => Share(dto.Declined);
    public double UnansweredFraction => Share(dto.Unanswered);

    public string AcceptedText => string.Format(Strings.Upcoming_Coming, dto.Accepted, dto.Invited);
    public string OpenText => dto.Unanswered == 0 ? Strings.Upcoming_EveryoneAnswered : string.Format(Strings.Upcoming_Open, dto.Unanswered);
    public string SummaryText => $"{AcceptedText} · {OpenText}";

    /// <summary>What a screen reader says for the RSVP bar.</summary>
    public string AriaText => string.Format(Strings.Upcoming_Aria, dto.Accepted, dto.Declined, dto.Unanswered);

    private double Share(int part) => dto.Invited == 0 ? 0 : part / (double)dto.Invited;
}
