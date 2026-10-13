namespace Kyuplan.Contracts;

/// <summary>An event a user can pick as the first or the last day of a time span.</summary>
/// <param name="Start">Start in the club time zone.</param>
public sealed record EventChoiceDto(string Id, string Name, DateTimeOffset Start);

/// <summary>
/// The events around today (the search window, by default a year back and six months ahead; cancelled ones left out) and the pair
/// the statistics page preselects as time span: the next upcoming event whose name contains the configured exam
/// keyword, and the latest such event before it. Without such a pair, the first and the latest event of the last
/// six months.
/// </summary>
/// <param name="Events">Chronological.</param>
/// <param name="SuggestedFromEventId">First event of the suggested span; <c>null</c> when no event took place in the last six months.</param>
/// <param name="SuggestedToEventId">Last event of the suggested span; <c>null</c> when no event took place in the last six months.</param>
public sealed record EventChoicesDto(
    IReadOnlyList<EventChoiceDto> Events,
    string? SuggestedFromEventId,
    string? SuggestedToEventId);
