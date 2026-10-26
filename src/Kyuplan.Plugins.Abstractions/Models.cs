namespace Kyuplan.Plugins;

// Spond-agnostic model. Everything the statistics need, nothing more — so the calculator can be
// tested without Spond and the demo data source can feed it just like the real one.

public sealed record SubGroupInfo(string Id, string Name);

/// <param name="BirthDate">The member's date of birth when Spond has one; the age a belt asks for is worked out from it.</param>
public sealed record MemberInfo(string Id, string Name, IReadOnlyList<string> SubGroupIds, DateOnly? BirthDate = null);

public sealed record GroupInfo(
    string Id,
    string Name,
    IReadOnlyList<SubGroupInfo> SubGroups,
    IReadOnlyList<MemberInfo> Members);

/// <summary>A member's answer to an invitation.</summary>
public enum Rsvp
{
    Accepted,
    Declined,
    Unanswered,

    /// <summary>Waiting list or unconfirmed.</summary>
    Other,
}

/// <param name="Start">Start in the club time zone.</param>
/// <param name="End">End in the club time zone.</param>
/// <param name="Responses">Every invited member id with their answer. Members not in here were not invited.</param>
/// <param name="Trainers">The ids of the members who host (train) the event. They run it instead of taking part: it counts for no statistic of theirs, whatever <paramref name="Responses"/> says about them.</param>
public sealed record EventInfo(
    string Id,
    string Name,
    DateTimeOffset Start,
    DateTimeOffset End,
    bool Cancelled,
    IReadOnlyDictionary<string, Rsvp> Responses,
    IReadOnlyCollection<string>? Trainers = null)
{
    /// <summary>The invitations of the members who take part: <see cref="Responses"/> without the <see cref="Trainers"/>. This is what every statistic reads.</summary>
    public IEnumerable<KeyValuePair<string, Rsvp>> Participants =>
        Trainers is { Count: > 0 } trainers ? Responses.Where(r => !trainers.Contains(r.Key)) : Responses;

    /// <summary>Whether the member hosts the event.</summary>
    public bool IsTrainer(string memberId) => Trainers?.Contains(memberId) == true;
}
