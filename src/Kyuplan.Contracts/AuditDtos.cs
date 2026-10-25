namespace Kyuplan.Contracts;

/// <summary>The kinds of change the audit log records (<see cref="AuditEntryDto.Area"/>).</summary>
public static class AuditAreas
{
    public const string Exams = "exams";
    public const string Payments = "payments";
    public const string Belts = "belts";
    public const string Members = "members";
    public const string Users = "users";
    public const string Roles = "roles";
    public const string Settings = "settings";
    public const string Backup = "backup";
    public const string CalendarFeeds = "calendarfeeds";
    public const string Manual = "manual";
    public const string Tags = "tags";
    public const string Comments = "comments";
    public const string Branding = "branding";
    public const string Themes = "themes";

    public static IReadOnlyList<string> All { get; } = [Exams, Payments, Belts, Members, Users, Roles, Settings, Backup, CalendarFeeds, Manual, Tags, Comments, Branding, Themes];

    public static bool IsKnown(string? area) => area is not null && All.Contains(area);

    /// <summary>The name of the text of an area in the language files: <c>AuditArea_exams</c>.</summary>
    public static string ResourceKey(string area) => "AuditArea_" + area;
}

/// <summary>What was done, as the audit log names it (<see cref="AuditEntryDto.Action"/>).</summary>
public static class AuditActions
{
    public const string Create = "create";
    public const string Update = "update";
    public const string Delete = "delete";
    public const string Setup = "setup";
    public const string Download = "download";
    public const string Restore = "restore";
    public const string Run = "run";
    public const string Regenerate = "regenerate";

    public static IReadOnlyList<string> All { get; } = [Create, Update, Delete, Setup, Download, Restore, Run, Regenerate];

    /// <summary>The name of the text of an action in the language files: <c>AuditAction_create</c>.</summary>
    public static string ResourceKey(string action) => "AuditAction_" + action;
}

/// <summary>One change somebody made, as it stands in the audit log.</summary>
/// <param name="Id">Rises with time; the log is read newest first.</param>
/// <param name="At">When the change was made.</param>
/// <param name="UserId">The user who made it (an id of the accounts database); null when it is not known.</param>
/// <param name="UserName">The user's name when the change was made; stays readable after the user is deleted.</param>
/// <param name="Area">One of <see cref="AuditAreas"/>.</param>
/// <param name="Action">One of <see cref="AuditActions"/> (a change that sets a value, such as a payment or a result, is an update).</param>
/// <param name="Target">What was changed, by the ids of the request, e.g. <c>examId=3, memberId=12</c>; null when the change has no target (a new record).</param>
/// <param name="Details">What was sent, as JSON, without passwords, tokens, scripts, connection strings and other secrets; null when nothing was sent. The settings list the names of what changed instead.</param>
public sealed record AuditEntryDto(
    long Id,
    DateTimeOffset At,
    int? UserId,
    string UserName,
    string Area,
    string Action,
    string? Target,
    string? Details);

/// <summary>A page of the audit log, newest first.</summary>
/// <param name="Entries">At most the requested number of entries.</param>
/// <param name="NextBefore">Pass it as <c>before</c> to read the next (older) page; null when this was the last.</param>
public sealed record AuditPageDto(IReadOnlyList<AuditEntryDto> Entries, long? NextBefore);
