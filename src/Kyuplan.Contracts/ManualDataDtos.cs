namespace Kyuplan.Contracts;

/// <summary>
/// Where the groups, members and events come from (<c>DataSource</c> in the settings). The three that are meant for real use are
/// <see cref="Spond"/>, <see cref="Custom"/> (a script of the administrator talks to any service) and <see cref="Manual"/> (users with
/// the permission <see cref="Permission.ManualDataEdit"/> keep everything by hand); <see cref="Demo"/> serves generated sample data.
/// </summary>
public static class DataSourceKinds
{
    public const string Spond = "spond";
    public const string Demo = "demo";
    public const string Custom = "custom";
    public const string Manual = "manual";

    public static IReadOnlyList<string> All { get; } = [Spond, Custom, Manual, Demo];

    public static bool IsKnown(string? kind) => kind is not null && All.Contains(kind);

    /// <summary>The kind in the form it is stored in (<c>null</c> or unknown = <see cref="Spond"/>).</summary>
    public static string Normalize(string? kind) => kind?.Trim().ToLowerInvariant() is { } k && All.Contains(k) ? k : Spond;
}

/// <summary>What a member answered to an event invitation, as the manual data keeps it.</summary>
public static class ManualRsvp
{
    public const string Accepted = "accepted";
    public const string Declined = "declined";
    public const string Unanswered = "unanswered";

    /// <summary>Waiting list or unconfirmed.</summary>
    public const string Other = "other";

    public static IReadOnlyList<string> All { get; } = [Accepted, Declined, Unanswered, Other];

    public static bool IsKnown(string? rsvp) => rsvp is not null && All.Contains(rsvp);
}

/// <summary>A group the users keep by hand (<see cref="DataSourceKinds.Manual"/>).</summary>
public sealed record ManualGroupDto(string Id, string Name, IReadOnlyList<SubGroupDto> SubGroups, int MemberCount);

/// <summary>The name of a group or subgroup to create or rename.</summary>
public sealed record ManualNameInputDto(string Name);

/// <param name="SubGroupIds">The subgroups of the group the member belongs to.</param>
public sealed record ManualMemberDto(string Id, string GroupId, string Name, DateOnly? BirthDate, IReadOnlyList<string> SubGroupIds);

/// <summary>What is saved of a member. Everything is replaced; <c>null</c> subgroups = none.</summary>
public sealed record ManualMemberInputDto(string Name, DateOnly? BirthDate, IReadOnlyList<string>? SubGroupIds);

/// <param name="Rsvp">One of <see cref="ManualRsvp"/>.</param>
public sealed record ManualInvitationDto(string MemberId, string Rsvp);

/// <param name="Invitations">The members invited to the event and what each answered; members not in the list were not invited.</param>
public sealed record ManualEventDto(
    string Id,
    string GroupId,
    string Name,
    DateTimeOffset Start,
    DateTimeOffset End,
    bool Cancelled,
    IReadOnlyList<ManualInvitationDto> Invitations);

/// <summary>What is saved of an event. Everything is replaced, the invitations too.</summary>
public sealed record ManualEventInputDto(
    string Name,
    DateTimeOffset Start,
    DateTimeOffset End,
    bool Cancelled,
    IReadOnlyList<ManualInvitationDto>? Invitations);

/// <summary>What the manual data have to look like. The API enforces it; the clients check the same rules.</summary>
public static class ManualDataRules
{
    public const int MaxNameLength = 100;

    public static bool IsName(string? name) => !string.IsNullOrWhiteSpace(name) && name.Trim().Length <= MaxNameLength && !name.Any(char.IsControl);

    /// <summary>The problems with a name, by field (camelCase), or empty.</summary>
    public static Dictionary<string, string[]> Check(ManualNameInputDto input) =>
        IsName(input.Name) ? [] : new() { ["name"] = [$"name is required (at most {MaxNameLength} characters)."] };

    public static Dictionary<string, string[]> Check(ManualMemberInputDto input) =>
        IsName(input.Name) ? [] : new() { ["name"] = [$"name is required (at most {MaxNameLength} characters)."] };

    public static Dictionary<string, string[]> Check(ManualEventInputDto input)
    {
        var errors = new Dictionary<string, string[]>();
        if (!IsName(input.Name)) errors["name"] = [$"name is required (at most {MaxNameLength} characters)."];
        if (input.End < input.Start) errors["end"] = ["end must not be before start."];
        if (input.Invitations?.Any(i => !ManualRsvp.IsKnown(i.Rsvp)) == true)
            errors["invitations"] = [$"rsvp must be one of: {string.Join(", ", ManualRsvp.All)}."];
        return errors;
    }
}
