namespace Kyuplan.Contracts;

/// <summary>What a source of a calendar feed puts into the calendar.</summary>
public static class CalendarSourceKinds
{
    /// <summary>The events of a Spond group (trainings, exams, ...), narrowed by who is invited and by name.</summary>
    public const string Events = "events";

    /// <summary>Every member's birthday, as an all-day entry that names the age.</summary>
    public const string Birthdays = "birthdays";

    /// <summary>The day each member started in the club, as an all-day entry that names the years.</summary>
    public const string ClubAnniversaries = "clubAnniversaries";

    /// <summary>The day each member got the belt they hold, as an all-day entry that names the belt and the years.</summary>
    public const string BeltAnniversaries = "beltAnniversaries";

    public static IReadOnlyList<string> All { get; } = [Events, Birthdays, ClubAnniversaries, BeltAnniversaries];
}

/// <summary>How the people a source selects must be invited for an event to be in the feed.</summary>
public static class EventInvitations
{
    /// <summary>At least one of the selected is invited (default).</summary>
    public const string Any = "any";

    /// <summary>All of the selected are invited.</summary>
    public const string All = "all";

    /// <summary>Nobody but the selected is invited: the event is only for them.</summary>
    public const string Only = "only";

    public static bool IsValid(string? value) => value is null or Any or All or Only;
}

/// <summary>
/// One source of a feed. The people it is about are the members of the <paramref name="SubGroupIds"/> plus the <paramref name="MemberIds"/>;
/// when both are empty, everybody of the group.
/// </summary>
/// <param name="Kind">One of <see cref="CalendarSourceKinds"/>.</param>
/// <param name="GroupId">The Spond group the source looks at.</param>
/// <param name="SubGroupIds">Spond subgroups of the group.</param>
/// <param name="MemberIds">Members (ids of the members list) of the group.</param>
/// <param name="Invitation">Events only: one of <see cref="EventInvitations"/>; <c>null</c> = any. Ignored while nobody is selected.</param>
/// <param name="NameContains">Events only: words separated by comma; the name must contain one of them. Empty = every name.</param>
/// <param name="NameExcludes">Events only: words separated by comma; a name that contains one is left out.</param>
/// <param name="OnlyExams">Events only: only the events that are exams of the planner.</param>
/// <param name="IncludeCancelled">Events only: cancelled events stay in, marked as cancelled (calendar apps show them struck through).</param>
/// <param name="Label">A word put in front of the title of every entry of the source, e.g. "Kids"; <c>null</c> = none.</param>
/// <param name="SubGroupLabels">Subgroup id → word put in front of the title of every entry that concerns somebody of that subgroup (after <paramref name="Label"/>, e.g. "Kids / Teens: Training"); only the selected subgroups count.</param>
public sealed record CalendarSourceDto(
    string Kind,
    string GroupId,
    IReadOnlyList<string>? SubGroupIds = null,
    IReadOnlyList<int>? MemberIds = null,
    string? Invitation = null,
    string? NameContains = null,
    string? NameExcludes = null,
    bool OnlyExams = false,
    bool IncludeCancelled = false,
    string? Label = null,
    IReadOnlyDictionary<string, string>? SubGroupLabels = null);

/// <param name="Name">Shown by the calendar app as the name of the calendar.</param>
public sealed record CalendarFeedInputDto(string Name, IReadOnlyList<CalendarSourceDto> Sources);

/// <param name="Url">
/// The address to subscribe to in a calendar app (ICS). Whoever knows it sees the calendar without logging in, so the server keeps only a hash
/// of its secret part: the address is in the answer of creating a feed and of giving it a new address (regenerate) and <c>null</c> everywhere
/// else, i.e. it can be shown once. A new address makes the old one stop working.
/// </param>
/// <param name="BrokenSources">Positions in <paramref name="Sources"/> of the sources whose group does not exist any more; the feed leaves them out until somebody fixes or removes them. <c>null</c>/empty = all fine (or not checked).</param>
public sealed record CalendarFeedDto(
    int Id,
    string Name,
    string? Url,
    IReadOnlyList<CalendarSourceDto> Sources,
    DateTimeOffset CreatedAt,
    IReadOnlyList<int>? BrokenSources = null);

public static class CalendarFeedRules
{
    public const int MaxNameLength = 100;
    public const int MaxSources = 20;
    public const int MaxLabelLength = 30;
    public const int MaxTermsLength = 500;

    /// <summary>Everything that is wrong with the input, by field.</summary>
    public static Dictionary<string, string[]> Check(CalendarFeedInputDto? input)
    {
        var errors = new Dictionary<string, string[]>();
        if (input is null)
        {
            errors["input"] = ["A request body is required."];
            return errors;
        }

        var name = input.Name?.Trim();
        if (string.IsNullOrEmpty(name)) errors[nameof(input.Name)] = ["name is required."];
        else if (name.Length > MaxNameLength) errors[nameof(input.Name)] = [$"name must be at most {MaxNameLength} characters long."];

        if (input.Sources is null || input.Sources.Count == 0) errors[nameof(input.Sources)] = ["A calendar needs at least one source."];
        else if (input.Sources.Count > MaxSources) errors[nameof(input.Sources)] = [$"A calendar has at most {MaxSources} sources."];
        else
        {
            for (var i = 0; i < input.Sources.Count; i++)
            {
                var problem = CheckSource(input.Sources[i]);
                if (problem is not null) errors[$"{nameof(input.Sources)}[{i}]"] = [problem];
            }
        }

        return errors;
    }

    private static string? CheckSource(CalendarSourceDto? source)
    {
        if (source is null) return "A source must not be empty.";
        if (!CalendarSourceKinds.All.Contains(source.Kind)) return $"kind must be one of {string.Join(", ", CalendarSourceKinds.All)}.";
        if (string.IsNullOrWhiteSpace(source.GroupId)) return "groupId is required.";
        if (!EventInvitations.IsValid(source.Invitation)) return "invitation must be any, all or only.";
        if (source.Label is { } label && label.Length > MaxLabelLength) return $"label must be at most {MaxLabelLength} characters long.";
        if (source.SubGroupLabels?.Values.Any(l => l?.Length > MaxLabelLength) == true) return $"subGroupLabels must be at most {MaxLabelLength} characters long.";
        if (source.NameContains?.Length > MaxTermsLength || source.NameExcludes?.Length > MaxTermsLength)
            return $"nameContains and nameExcludes must be at most {MaxTermsLength} characters long.";
        return null;
    }
}
