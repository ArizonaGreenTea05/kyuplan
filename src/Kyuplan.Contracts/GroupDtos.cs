namespace Kyuplan.Contracts;

/// <summary>Information about the running API, used by clients to configure themselves.</summary>
/// <param name="IsDemoData">True when the API serves generated sample data instead of real Spond data.</param>
/// <param name="DefaultMinAttendancePercent">Server-side default for the attendance threshold.</param>
/// <param name="DefaultMinInvitedEvents">Server-side default for the minimum number of invitations.</param>
/// <param name="DefaultExamMinAverageStars">Server-side default for the average evaluation an exam asks for.</param>
/// <param name="TimeZone">IANA/Windows id of the time zone all dates are evaluated in.</param>
/// <param name="HasDocumentation">Whether this server carries the documentation (<see cref="Documentation"/>); a build from source has none.</param>
/// <param name="RepositoryUrl">Web address of the source repository this build was published from (set at release time); <c>null</c> for a build from source.</param>
/// <param name="ClientVersions">Version of the desktop client's installer this server offers for each platform (<see cref="ClientPlatform"/>; see <see cref="ClientDownload"/>); <c>null</c> or without an entry when it has none.</param>
public sealed record ServerInfoDto(
    string Name,
    string Version,
    bool IsDemoData,
    int DefaultMinAttendancePercent,
    int DefaultMinInvitedEvents,
    string TimeZone,
    IReadOnlyDictionary<string, string>? ClientVersions = null,
    bool HasDocumentation = false,
    string? RepositoryUrl = null,
    double DefaultExamMinAverageStars = RatingRules.StandardExamMinAverageStars)
{
    /// <summary>The version of the installer for this platform, <c>null</c> when the server has none.</summary>
    public string? ClientVersion(string platform) => ClientVersions is not null && ClientVersions.TryGetValue(platform, out var v) ? v : null;
}

public sealed record SubGroupDto(string Id, string Name);

public sealed record GroupDto(
    string Id,
    string Name,
    int MemberCount,
    IReadOnlyList<SubGroupDto> SubGroups);
