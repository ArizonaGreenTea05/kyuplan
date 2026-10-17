using Kyuplan.Contracts;

namespace Kyuplan.Api.Statistics;

/// <summary>
/// The star evaluations (0–5) of the members of a group at events, read for the statistics and the exam suggestions. Members are
/// identified by their Spond id, as the statistics do. An event counts <b>once</b> for a member, however many users evaluated them: as the
/// average of those evaluations, or, when nobody did, as <see cref="DefaultStars"/>. The rules are in docs/attendance-statistics.md.
/// </summary>
public sealed class RatingBook(IReadOnlyDictionary<(string MemberId, string EventId), double> averages, double defaultStars)
{
    /// <summary>No evaluations at all: every event counts the default.</summary>
    public static RatingBook None(double defaultStars = RatingRules.StandardDefaultEventStars) =>
        new(new Dictionary<(string, string), double>(), defaultStars);

    /// <summary>What an event counts for a member who attended it and was evaluated by nobody.</summary>
    public double DefaultStars => defaultStars;

    /// <summary>Somebody evaluated the member at the event.</summary>
    public bool IsEvaluated(string memberId, string eventId) => averages.ContainsKey((memberId, eventId));

    /// <summary>
    /// What the event counts for the member, or <c>null</c> when it does not count: the member neither attended it nor was evaluated at it
    /// (a member who stayed away is not judged by the event).
    /// </summary>
    public double? StarsOf(string memberId, string eventId, bool attended) =>
        averages.TryGetValue((memberId, eventId), out var average) ? average : attended ? defaultStars : null;
}
