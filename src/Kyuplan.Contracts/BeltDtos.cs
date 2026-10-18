namespace Kyuplan.Contracts;

/// <summary>
/// One way to take a belt: from the belt <see cref="BeltId"/>, which a member must hold, and what else a member who comes from that
/// belt must meet. What is required depends on the previous belt: from blue to brown takes another age and waiting time than from
/// blue-brown to brown.
/// </summary>
/// <param name="BeltId">The previous belt.</param>
/// <param name="MinAgeYears">The age in full years a member must have. 0 = no minimum.</param>
/// <param name="MinWaitMonths">The months that must have passed since the member got the previous belt. 0 = no waiting time.</param>
public sealed record BeltPrerequisiteDto(int BeltId, int MinAgeYears, int MinWaitMonths);

/// <summary>A belt (grade) that can be held and taken an exam for, with what is required to take it.</summary>
/// <param name="Name">Unique regardless of case.</param>
/// <param name="PrimaryColor">The belt's colour, <c>#RRGGBB</c>.</param>
/// <param name="SecondaryColor">The colour of the stripe, <c>#RRGGBB</c>; <c>null</c> when the belt has none.</param>
/// <param name="Prerequisites">A member must hold <b>one of</b> these belts to take this one, and meet what is listed for the one they hold; by <see cref="BeltPrerequisiteDto.BeltId"/>. Empty = nothing is required (the first belt).</param>
/// <param name="MemberCount">How many members hold this belt now. A belt that somebody holds cannot be deleted.</param>
public sealed record BeltDto(
    int Id,
    string Name,
    string PrimaryColor,
    string? SecondaryColor,
    IReadOnlyList<BeltPrerequisiteDto> Prerequisites,
    int MemberCount);

/// <summary>What a client sends to create a belt or to replace everything about one. Every previous belt may be listed once.</summary>
public sealed record BeltInputDto(
    string Name,
    string PrimaryColor,
    string? SecondaryColor,
    IReadOnlyList<BeltPrerequisiteDto> Prerequisites);

/// <summary>
/// A member of a Spond group as the API last saw them. The name and the group are Spond's; the belt, the day the member got it and
/// the start day are what the users keep here, and so is the birth date when Spond does not have it.
/// </summary>
/// <param name="GroupId">Spond group the member belongs to.</param>
/// <param name="BeltId">The belt the member holds; <c>null</c> until somebody assigns one.</param>
/// <param name="StartedAt">The day the member started in the club; <c>null</c> until it is known (the API fills it in from the events the member attended, and a day set by hand is kept).</param>
/// <param name="BirthDate">The day the member was born; <c>null</c> until it is known (the API takes it over from Spond when Spond has it, and a day set by hand is kept). The age a belt requires is worked out from it.</param>
/// <param name="BeltSince">The day the member got the belt they hold now; the waiting time for the next belt counts from it. <c>null</c> when there is no belt or the day is not known (recording a belt at an exam sets it).</param>
/// <param name="CommentCount">How many comments the users wrote about the member, replies included.</param>
/// <param name="Registered"><c>false</c> while the member is in the Spond subgroup of the people not registered in the club yet.</param>
public sealed record MemberDto(
    int Id,
    string GroupId,
    string Name,
    int? BeltId,
    DateOnly? StartedAt,
    DateOnly? BirthDate = null,
    DateOnly? BeltSince = null,
    int CommentCount = 0,
    bool Registered = true);

/// <summary>What a client sends to change a member: what Spond does not know. Everything is replaced; <c>null</c> clears it. Without a belt there is no <paramref name="BeltSince"/>.</summary>
public sealed record MemberInputDto(int? BeltId, DateOnly? StartedAt, DateOnly? BirthDate = null, DateOnly? BeltSince = null);
