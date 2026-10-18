namespace Kyuplan.Contracts;

/// <summary>
/// One user's evaluation of one member at one event: 0 to 5 stars. A user evaluates a member at most once per event; an evaluation of 0
/// stars is an evaluation (a poor one), which is not the same as none.
/// </summary>
/// <param name="MemberId">The planner's id of the member.</param>
/// <param name="EventId">The Spond event the evaluation is for.</param>
/// <param name="UserId">The user who evaluated.</param>
/// <param name="UserName">The user's display name now, or as it was when they evaluated if the user is gone.</param>
/// <param name="Stars">0 to 5.</param>
/// <param name="UpdatedAt">When it was given or last changed, in the club time zone.</param>
public sealed record RatingDto(int MemberId, string EventId, int UserId, string UserName, int Stars, DateTimeOffset UpdatedAt);

/// <summary>What a client sends to evaluate a member at an event as the signed-in user: replaces the user's earlier evaluation, if any.</summary>
public sealed record RatingInputDto(int Stars);

/// <summary>What an evaluation has to look like, and the standard values of the settings that use them.</summary>
public static class RatingRules
{
    public const int MinStars = 0;
    public const int MaxStars = 5;

    /// <summary>What an event counts for a member who attended it and was evaluated by nobody (setting <c>Statistics:DefaultEventStars</c>).</summary>
    public const double StandardDefaultEventStars = 2.5;

    /// <summary>The average a member needs to be suggested for an exam (setting <c>Statistics:ExamMinAverageStars</c>).</summary>
    public const double StandardExamMinAverageStars = 3;

    public static bool IsStars(int stars) => stars is >= MinStars and <= MaxStars;

    /// <summary>A value of a setting that is a number of stars: between 0 and 5 (halves and decimals are fine).</summary>
    public static bool IsAverage(double value) => double.IsFinite(value) && value is >= MinStars and <= MaxStars;
}
