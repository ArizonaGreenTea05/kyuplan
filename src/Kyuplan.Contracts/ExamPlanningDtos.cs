using System.Text.Json.Serialization;

namespace Kyuplan.Contracts;

/// <summary>
/// One member in the context of one exam: what the exam knows about them. A member is listed when they were invited to the exam's
/// event on Spond, were saved for the exam in the planner (<see cref="IsPreSaved"/>) or received a belt at it.
/// </summary>
/// <param name="MemberId">The member, as in <see cref="MemberDto.Id"/>.</param>
/// <param name="BeltId">The belt the member holds now.</param>
/// <param name="Invitation">What the member answered to the invitation on Spond; <see cref="AttendanceMark.Attended"/> = accepted. <c>null</c> = not invited, as far as the last sync knows.</param>
/// <param name="IsPreSaved">The member was saved for the exam, to be invited on Spond later.</param>
/// <param name="ReceivedBeltId">The belt the member received at the exam; <c>null</c> = none recorded.</param>
public sealed record ExamMemberDto(
    int MemberId,
    string Name,
    int? BeltId,
    AttendanceMark? Invitation,
    bool IsPreSaved,
    int? ReceivedBeltId);

/// <summary>
/// A change to the members saved for an exam, e.g. the ones the suggestions found fit, so that they can be invited on Spond later, all
/// at once. This is the planner's own list, nobody is invited by saving it. What is not named stays as it is, so that a client that sees
/// only part of the list (the members of some subgroups) cannot take back what it does not show, nor undo what somebody else saved meanwhile.
/// </summary>
/// <param name="Add">Members of the exam's group to save. Those who are saved already stay saved.</param>
/// <param name="Remove">Members to take off the list. A member who is not on it is no problem; one named in both lists is saved.</param>
public sealed record ExamCandidatesInputDto(IReadOnlyList<int> Add, IReadOnlyList<int> Remove);

/// <summary>The belt a member received at an exam. Giving it also makes it the belt the member holds, unless a later exam says otherwise.</summary>
/// <param name="BeltId">The belt received; <c>null</c> takes the result back (the belt the member holds is left as it is).</param>
public sealed record ExamResultInputDto(int? BeltId);

/// <summary>Whether a member can be put forward for an exam.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<SuggestionVerdict>))]
public enum SuggestionVerdict
{
    /// <summary>Every requirement is met on the day of the exam: the member is fit for it.</summary>
    Fit,

    /// <summary>Nothing is missed so far, but something cannot be checked yet: no belt, no birth date, or no idea when the belt was received.</summary>
    NeedsData,

    /// <summary>At least one requirement is not met: attendance, waiting time or age.</summary>
    NotYet,

    /// <summary>The member holds a belt no other belt asks for: there is nothing to take an exam for.</summary>
    NoNextBelt,
}

/// <summary>Whether one requirement of the next belt is met.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<CheckState>))]
public enum CheckState
{
    Met,
    NotMet,

    /// <summary>It cannot be told because a day it depends on is not known.</summary>
    Unknown,
}

/// <summary>
/// How fit one member is for an exam: the belt they would take, and whether they meet its requirements <b>on the day of the exam</b>
/// (age and waiting time are counted until then) and the attendance of the filter the suggestion was made with.
/// </summary>
/// <param name="SubGroups">The subgroups of the member, by name.</param>
/// <param name="BeltId">The belt the member holds now; <c>null</c> = none assigned yet.</param>
/// <param name="NextBeltId">The belt they would take: the one that asks for the belt they hold. When several do, the first (in the order of the belts) whose requirements they meet, else the first. <c>null</c> when there is none (<see cref="SuggestionVerdict.NoNextBelt"/>) or no belt is known.</param>
/// <param name="MinAgeYears">The age in full years the next belt asks for, coming from the belt they hold. 0 = none.</param>
/// <param name="AgeYears">The age in full years on the exam day; <c>null</c> without a birth date.</param>
/// <param name="MinWaitMonths">The months the next belt asks for since the belt they hold was received. 0 = none.</param>
/// <param name="WaitedMonths">The full months from <see cref="WaitingSince"/> to the exam day; <c>null</c> when that day is not known.</param>
/// <param name="BeltSince">The day the member got the belt they hold, as recorded; <c>null</c> = not known.</param>
/// <param name="LastExam">The latest exam before this one that the member took part in (accepted, or received a belt at it); <c>null</c> = none known.</param>
/// <param name="WaitingSince">The day the waiting time counts from: <see cref="BeltSince"/>, and, while it is not known, <see cref="LastExam"/> (the belt cannot have been received later than that, so the waiting time is never overstated). <c>null</c> = neither is known.</param>
/// <param name="Invited">Events of the filter's time span that the member was invited to (see the attendance statistics).</param>
/// <param name="Attended">Of them, those they accepted.</param>
/// <param name="AttendanceRate">Attended / Invited, 0–100.</param>
/// <param name="AttendanceStatus">The verdict the attendance statistics give with the filter; a member who was not invited to any event in the span has <see cref="QualificationStatus.InsufficientData"/>.</param>
/// <param name="Invitation">What the member answered to the invitation to this exam; <c>null</c> = not invited yet.</param>
/// <param name="IsPreSaved">The member is saved for this exam.</param>
public sealed record ExamSuggestionDto(
    int MemberId,
    string Name,
    IReadOnlyList<string> SubGroups,
    int? BeltId,
    int? NextBeltId,
    int MinAgeYears,
    int? AgeYears,
    CheckState AgeCheck,
    int MinWaitMonths,
    int? WaitedMonths,
    CheckState WaitCheck,
    DateOnly? BeltSince,
    DateOnly? LastExam,
    DateOnly? WaitingSince,
    int Invited,
    int Attended,
    double AttendanceRate,
    QualificationStatus AttendanceStatus,
    int EngagementScore,
    SuggestionVerdict Verdict,
    AttendanceMark? Invitation,
    bool IsPreSaved);

/// <summary>The members of an exam's group, judged for the exam with one filter.</summary>
/// <param name="FilterId">The saved filter the attendance was measured with; <c>null</c> = the default one (see <see cref="Filter"/>).</param>
/// <param name="FilterName">Name of that saved filter.</param>
/// <param name="Filter">What the attendance was measured with: the time span as days, the thresholds, the subgroups and the event names.</param>
/// <param name="Members">The fit ones first (best attendance first), then those that need data, those that are not fit yet and those with nothing to take; within a kind by attendance.</param>
public sealed record ExamSuggestionsDto(
    int ExamId,
    string ExamName,
    DateOnly ExamDate,
    int? FilterId,
    string? FilterName,
    AttendanceFilter Filter,
    IReadOnlyList<ExamSuggestionDto> Members);
