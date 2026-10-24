using Kyuplan.Api.Domain;

namespace Kyuplan.Api.Persistence;

/// <summary>
/// An exam, e.g. the belt exam of a given day: a Spond event, nothing else. The filters saved for it hang off it.
/// </summary>
public sealed class Exam
{
    public int Id { get; set; }

    /// <summary>Spond group of <see cref="EventId"/>; Spond lists events per group.</summary>
    public string GroupId { get; set; } = "";

    /// <summary>The Spond event that is the exam. Unique: one event is at most one exam.</summary>
    public string EventId { get; set; } = "";

    /// <summary>The event's name as last seen on Spond; <see cref="ExamService"/> keeps it current.</summary>
    public string Name { get; set; } = "";

    /// <summary>The day the event starts on (club time zone) as last seen on Spond; kept current like <see cref="Name"/>.</summary>
    public DateOnly Date { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    /// <summary>The subgroups the default filter of the exam looks at (see <see cref="ExamPlanningService"/>); empty = everyone in the group. Stored as a JSON array in one column.</summary>
    public List<string> SubGroupIds { get; set; } = [];

    /// <summary>Deleted along with the exam.</summary>
    public List<SavedFilter> Filters { get; set; } = [];

    /// <summary>Everybody who was invited to the event, with their answer; <see cref="SpondSync"/> keeps it current. Deleted along with the exam.</summary>
    public List<ExamParticipant> Participants { get; set; } = [];

    /// <summary>The members a user saved for the exam, to be invited on Spond later. The planner's own list: the sync never touches it. Deleted along with the exam.</summary>
    public List<ExamCandidate> Candidates { get; set; } = [];

    /// <summary>The belts handed out at the exam. Kept apart from <see cref="Participants"/>, which follow Spond. Deleted along with the exam.</summary>
    public List<ExamResult> Results { get; set; } = [];

    /// <summary>What members paid for the exam. Deleted along with the exam.</summary>
    public List<ExamPayment> Payments { get; set; } = [];
}

/// <summary>
/// One line of the exam price list: the general fee (<see cref="BeltId"/> null, at most one row) or the extra costs of one belt (at most
/// one row per belt). What a member owes for an exam is the general fee plus the stamp fee and the belt price of the belt they receive
/// (the belt price not when they got the belt elsewhere); no row = 0.
/// </summary>
public sealed class ExamPrice
{
    public int Id { get; set; }

    /// <summary>The belt the costs are for; null = the general fee.</summary>
    public int? BeltId { get; set; }

    public Belt? Belt { get; set; }

    /// <summary>The general fee on the general row; the fee for the belt's exam stamp (Prüfungsmarke) on a belt row.</summary>
    public decimal Amount { get; set; }

    /// <summary>The price of the belt itself (belt rows only; 0 on the general row).</summary>
    public decimal BeltPrice { get; set; }
}

/// <summary>
/// What one member paid for one exam. A row exists once an administrator recorded something; a member without one has paid nothing. It
/// keeps the amount due as it was worked out when it was last saved, so that a later change of the prices does not turn a settled exam
/// into an open one.
/// </summary>
public sealed class ExamPayment
{
    public int ExamId { get; set; }

    public Exam Exam { get; set; } = null!;

    public int MemberId { get; set; }

    public Member Member { get; set; } = null!;

    /// <summary>What was due when this was saved: the general fee, <see cref="StampFee"/> and <see cref="BeltPrice"/> together.</summary>
    public decimal AmountDue { get; set; }

    /// <summary>The part of <see cref="AmountDue"/> that is the fee for the exam stamp.</summary>
    public decimal StampFee { get; set; }

    /// <summary>The part of <see cref="AmountDue"/> that is the price of the belt; 0 when <see cref="OwnBelt"/>.</summary>
    public decimal BeltPrice { get; set; }

    /// <summary>The member got the belt elsewhere (e.g. handed down by a sibling): the belt itself is not charged.</summary>
    public bool OwnBelt { get; set; }

    public decimal AmountPaid { get; set; }

    /// <summary>The day of the last payment; null when nothing is paid.</summary>
    public DateOnly? PaidOn { get; set; }

    public string? Note { get; set; }
}

/// <summary>
/// A member a user saved for an exam, so that they are on record before anybody is invited. Nothing is sent to Spond by it; whether the
/// member was invited since shows in <see cref="ExamParticipant"/>.
/// </summary>
public sealed class ExamCandidate
{
    public int ExamId { get; set; }

    public Exam Exam { get; set; } = null!;

    public int MemberId { get; set; }

    public Member Member { get; set; } = null!;

    public DateTimeOffset CreatedAt { get; set; }
}

/// <summary>
/// The belt a member received at an exam: one row per member who received one, none for those who did not. Spond knows nothing about
/// it, and the row does not depend on the member still being invited (the sync drops invitations that are gone from Spond).
/// </summary>
public sealed class ExamResult
{
    public int ExamId { get; set; }

    public Exam Exam { get; set; } = null!;

    public int MemberId { get; set; }

    public Member Member { get; set; } = null!;

    public int BeltId { get; set; }

    /// <summary>A belt that was handed out at an exam cannot be deleted.</summary>
    public Belt Belt { get; set; } = null!;
}

/// <summary>One member invited to an exam, and what they answered. A member who is no longer in Spond is gone from here too.</summary>
public sealed class ExamParticipant
{
    public int ExamId { get; set; }

    public Exam Exam { get; set; } = null!;

    public int MemberId { get; set; }

    public Member Member { get; set; } = null!;

    /// <summary>Stored by name (<c>Accepted</c>, <c>Declined</c>, …) so that the table reads without this code.</summary>
    public Rsvp Rsvp { get; set; }

    /// <summary>"Attended" means "accepted the invitation": Spond has RSVPs, not check-ins (see docs/attendance-statistics.md).</summary>
    public bool Attended => Rsvp == Rsvp.Accepted;
}

/// <summary>A belt (grade) that can be held and taken an exam for, e.g. "Orange belt".</summary>
public sealed class Belt
{
    public int Id { get; set; }

    /// <summary>Unique regardless of case.</summary>
    public string Name { get; set; } = "";

    /// <summary>The belt's colour as <c>#RRGGBB</c>.</summary>
    public string PrimaryColor { get; set; } = "";

    /// <summary>The colour of the stripe, if the belt has one; <c>#RRGGBB</c>.</summary>
    public string? SecondaryColor { get; set; }

    /// <summary>
    /// What it takes to be allowed to take this belt: a member must hold <b>one of</b> the belts listed here (the previous belt), and
    /// what else is required (minimum age, waiting time) depends on which of them it is. None = nothing is required (the first belt;
    /// nothing demands "at least one", so a club may have several such belts). <see cref="BeltService"/> keeps the belts from
    /// requiring each other in a circle.
    /// </summary>
    public List<BeltPrerequisite> Prerequisites { get; set; } = [];
}

/// <summary>
/// One way to take <see cref="Belt"/>: from <see cref="RequiredBelt"/>, with what is required of a member who comes from that belt.
/// The requirements belong here and not to the belt because they differ with the previous belt: from blue to brown takes another
/// age and waiting time than from blue-brown to brown.
/// </summary>
public sealed class BeltPrerequisite
{
    public int BeltId { get; set; }

    public Belt Belt { get; set; } = null!;

    public int RequiredBeltId { get; set; }

    public Belt RequiredBelt { get; set; } = null!;

    /// <summary>How old (in full years) a member must be to take <see cref="Belt"/> from <see cref="RequiredBelt"/>. 0 = no minimum.</summary>
    public int MinAgeYears { get; set; }

    /// <summary>How many months must have passed since the member got <see cref="RequiredBelt"/>. 0 = no waiting time.</summary>
    public int MinWaitMonths { get; set; }
}

/// <summary>
/// A member of a Spond group, as last seen there. <see cref="SpondSync"/> adds the members Spond has, keeps their name current and
/// removes those Spond no longer has. What Spond does not know (<see cref="BeltId"/>, and <see cref="StartedAt"/> when it cannot be
/// worked out) is kept across syncs.
/// </summary>
public sealed class Member
{
    public int Id { get; set; }

    /// <summary>Spond group of <see cref="SpondId"/>. A person in two groups is two members: Spond has an id per group.</summary>
    public string GroupId { get; set; } = "";

    public string SpondId { get; set; } = "";

    /// <summary>As last seen on Spond.</summary>
    public string Name { get; set; } = "";

    /// <summary>The belt the member holds; null until somebody assigns one, Spond knows nothing about belts.</summary>
    public int? BeltId { get; set; }

    public Belt? Belt { get; set; }

    /// <summary>
    /// The day the member started in the club. Null until known: <see cref="SpondSync"/> fills it with the day of the first event
    /// the member attended, and never changes a value that is there.
    /// </summary>
    public DateOnly? StartedAt { get; set; }

    /// <summary>
    /// The day the member got the belt they hold; the waiting time for the next belt counts from it. Null when there is no belt or the day
    /// is not known. Set by hand, and when a belt is recorded at an exam (the day of the exam).
    /// </summary>
    public DateOnly? BeltSince { get; set; }

    /// <summary>
    /// The day the member was born; the age a belt asks for is worked out from it. Null until known: <see cref="SpondSync"/> takes it over
    /// from Spond when Spond has it, and never changes a value that is there.
    /// </summary>
    public DateOnly? BirthDate { get; set; }

    /// <summary>
    /// Whether the member is registered in the club. False while they are in the Spond subgroup of the not registered yet
    /// (<c>Spond:UnregisteredSubGroup</c>); <see cref="SpondSync"/> keeps it in line with Spond.
    /// </summary>
    public bool Registered { get; set; } = true;

    public DateTimeOffset CreatedAt { get; set; }

    /// <summary>
    /// When <see cref="SpondSync"/> found the member gone from the data source; null for a member that is there. A removed member is hidden
    /// everywhere (a query filter, see <c>PlannerDbContext</c>) but keeps their belts, payments, comments and results for
    /// <c>Sync:RemovedMemberRetention</c>, so a wrong answer of the data source can be undone: when it lists them again they are back.
    /// </summary>
    public DateTimeOffset? RemovedAt { get; set; }

    /// <summary>
    /// Hidden by a user (<c>members.edit</c>): the member is ignored by every statistic and shown nowhere, like a removed one (the same
    /// query filter, see <c>PlannerDbContext</c>), but stays in the data source and keeps everything the planner knows about them. Only the
    /// list of hidden members on the members page (<c>members.hidden</c>) shows them again and makes them visible. <see cref="SpondSync"/>
    /// keeps them up to date and never changes this.
    /// </summary>
    public bool Hidden { get; set; }

    /// <summary>
    /// The user (accounts database id, so no foreign key) who is this member; an administrator links them. A user may be several members (one per
    /// Spond group), a member is at most one user. What is written about a member is hidden from their user unless they have the "own" permission.
    /// </summary>
    public int? UserId { get; set; }

    /// <summary>The exams the member was invited to; removed along with the member.</summary>
    public List<ExamParticipant> Participations { get; set; } = [];

    /// <summary>What the users wrote about the member (replies included); removed along with the member, like everything the planner keeps about one.</summary>
    public List<Comment> Comments { get; set; } = [];

    /// <summary>The evaluations users gave the member at events; removed along with the member.</summary>
    public List<MemberRating> Ratings { get; set; } = [];
}

/// <summary>
/// One user's evaluation of a member at one event, 0 to 5 stars (<see cref="Kyuplan.Contracts.RatingRules"/>). A user has at most one per
/// member and event; 0 stars is an evaluation, no row is none.
/// </summary>
public sealed class MemberRating
{
    public int Id { get; set; }

    public int MemberId { get; set; }

    public Member Member { get; set; } = null!;

    /// <summary>The Spond event, as events are not kept in the database.</summary>
    public string EventId { get; set; } = "";

    /// <summary>The user who evaluated: an id of the accounts database, which is a database of its own, so no foreign key.</summary>
    public int UserId { get; set; }

    /// <summary>The user's name when they evaluated; shown only when the user is gone.</summary>
    public string UserName { get; set; } = "";

    public int Stars { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }
}

/// <summary>
/// A note a user wrote about a member, or a reply to another note of the same member. Written in the markup of
/// <see cref="Kyuplan.Contracts.CommentFormat"/>. Every change leaves a <see cref="CommentEdit"/> behind.
/// </summary>
public sealed class Comment
{
    public int Id { get; set; }

    public int MemberId { get; set; }

    public Member Member { get; set; } = null!;

    /// <summary>The comment this one replies to; null for one that starts a thread. A reply is deleted with the comment it replies to.</summary>
    public int? ParentId { get; set; }

    public Comment? Parent { get; set; }

    public List<Comment> Replies { get; set; } = [];

    public string Body { get; set; } = "";

    /// <summary>The user who wrote it: an id of the accounts database, which is a database of its own, so no foreign key.</summary>
    public int AuthorId { get; set; }

    /// <summary>The author's user name when the comment was written; shown only when the user is gone.</summary>
    public string AuthorName { get; set; } = "";

    public DateTimeOffset CreatedAt { get; set; }

    public List<CommentTag> Tags { get; set; } = [];

    /// <summary>Oldest first. Deleted along with the comment.</summary>
    public List<CommentEdit> Edits { get; set; } = [];
}

/// <summary>
/// One change of a comment: who made it, when, how, and what the comment said before. Names of the tags are kept as they were, so that
/// the history still reads after a tag was renamed or deleted.
/// </summary>
public sealed class CommentEdit
{
    public int Id { get; set; }

    public int CommentId { get; set; }

    public Comment Comment { get; set; } = null!;

    /// <summary>An id of the accounts database, like <see cref="Comment.AuthorId"/>.</summary>
    public int EditorId { get; set; }

    public string EditorName { get; set; } = "";

    public DateTimeOffset EditedAt { get; set; }

    /// <summary>The words changed.</summary>
    public bool TextChanged { get; set; }

    /// <summary>The words are the same, only the styling changed.</summary>
    public bool FormattingChanged { get; set; }

    /// <summary>Stored as a JSON array in one column.</summary>
    public List<string> TagsAdded { get; set; } = [];

    /// <summary>Stored as a JSON array in one column.</summary>
    public List<string> TagsRemoved { get; set; } = [];

    /// <summary>The body before this change.</summary>
    public string PreviousBody { get; set; } = "";
}

/// <summary>
/// A calendar the API offers as an ICS address (<c>/calendar-feeds/{token}.ics</c>) that calendar apps subscribe to. What it contains is
/// defined by its sources (<see cref="Kyuplan.Contracts.CalendarSourceDto"/>, see <see cref="CalendarFeedService"/>).
/// </summary>
public sealed class CalendarFeed
{
    public int Id { get; set; }

    /// <summary>Unique regardless of case.</summary>
    public string Name { get; set; } = "";

    /// <summary>The secret in the address; whoever knows it may read the calendar without logging in. Unique.</summary>
    public string Token { get; set; } = "";

    /// <summary>The sources as a JSON array, in one column: they are only ever read and written together with the feed.</summary>
    public string SourcesJson { get; set; } = "[]";

    public DateTimeOffset CreatedAt { get; set; }
}

/// <summary>
/// A topic comments can be about. It may be connected to one Spond event (a training, an exam, ...); one event can have many tags.
/// The event's name and start are kept as last seen, so that a tag reads without Spond; <see cref="TagService"/> keeps them current.
/// </summary>
public sealed class Tag
{
    public int Id { get; set; }

    /// <summary>Unique regardless of case.</summary>
    public string Name { get; set; } = "";

    /// <summary>The Spond group of <see cref="EventId"/>. The group, the event, its name and its start are all set or all null.</summary>
    public string? EventGroupId { get; set; }

    public string? EventId { get; set; }

    public string? EventName { get; set; }

    public DateTimeOffset? EventStart { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    /// <summary>The comments with this tag; deleting the tag takes it off them, it does not delete them.</summary>
    public List<CommentTag> Comments { get; set; } = [];
}

/// <summary>A tag on a comment.</summary>
public sealed class CommentTag
{
    public int CommentId { get; set; }

    public Comment Comment { get; set; } = null!;

    public int TagId { get; set; }

    public Tag Tag { get; set; } = null!;
}

/// <summary>The settings of the statistics page's filter card, stored under a name for one exam.</summary>
public sealed class SavedFilter
{
    public int Id { get; set; }

    public int ExamId { get; set; }

    public Exam Exam { get; set; } = null!;

    public string Name { get; set; } = "";

    public string GroupId { get; set; } = "";

    public DateOnly From { get; set; }

    public DateOnly To { get; set; }

    /// <summary>With <see cref="ToEventId"/>: the events that define the time span. Both are set or both are null.</summary>
    public string? FromEventId { get; set; }

    public string? ToEventId { get; set; }

    /// <summary>Stored as a JSON array in one column.</summary>
    public List<string> SubGroupIds { get; set; } = [];

    public int MinAttendancePercent { get; set; }

    public int MinInvitedEvents { get; set; }

    public string? EventNameContains { get; set; }

    public string? EventNameExcludes { get; set; }

    /// <summary>Average evaluation an exam asks for with this filter; null = the administrators' setting.</summary>
    public double? MinAverageStars { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }
}

/// <summary>
/// The texts and the colour of the branding as administrators saved them (see <see cref="BrandingService"/>): at most one row, saved
/// together. No row = the API's <c>Branding</c> settings apply.
/// </summary>
public sealed class CustomBranding
{
    /// <summary>Always 1: there is only one.</summary>
    public int Id { get; set; } = 1;

    public string ProductName { get; set; } = "";

    public string OrganizationName { get; set; } = "";

    /// <summary><c>#RGB</c> or <c>#RRGGBB</c>.</summary>
    public string AccentColor { get; set; } = "";

    /// <summary>A theme of the web client (<see cref="ThemeRules"/>): what users who chose none see.</summary>
    public string DefaultWebTheme { get; set; } = Kyuplan.Contracts.ThemeIds.Default;

    /// <summary>The same for the Windows client.</summary>
    public string DefaultWindowsTheme { get; set; } = Kyuplan.Contracts.ThemeIds.Default;

    /// <summary>Unit of money, shown after every amount.</summary>
    public string Currency { get; set; } = Kyuplan.Contracts.BrandingRules.DefaultCurrency;

    public DateTimeOffset UpdatedAt { get; set; }
}

/// <summary>
/// What an administrator did with one image slot (<see cref="Kyuplan.Contracts.BrandingSlots"/>): uploaded an image, or took the
/// slot's image away (<see cref="Content"/> is <c>null</c>). A slot without a row is what the API's <c>Branding</c> settings say.
/// </summary>
public sealed class CustomBrandingImage
{
    public string Slot { get; set; } = "";

    /// <summary>What the bytes are (found out by <see cref="Kyuplan.Contracts.BrandingImageFormats"/>, not claimed by the uploader); <c>null</c> with <see cref="Content"/>.</summary>
    public string? ContentType { get; set; }

    /// <summary>The image; <c>null</c> = the slot has no image on purpose, even if the settings name a file.</summary>
    public byte[]? Content { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }
}

/// <summary>
/// A group the users keep by hand when the data source is <see cref="Kyuplan.Contracts.DataSourceKinds.Manual"/>: what Spond would
/// deliver, entered in the clients instead (see <see cref="ManualDataService"/>, read by <see cref="Sources.ManualDataSource"/>).
/// </summary>
public sealed class ManualGroup
{
    /// <summary>A GUID in the format "N"; it is the group's id for everything else (members, exams, filters).</summary>
    public string Id { get; set; } = "";

    public string Name { get; set; } = "";

    public List<ManualSubGroup> SubGroups { get; set; } = [];

    public List<ManualMember> Members { get; set; } = [];

    public List<ManualEvent> Events { get; set; } = [];
}

public sealed class ManualSubGroup
{
    public string Id { get; set; } = "";

    public string GroupId { get; set; } = "";

    public ManualGroup Group { get; set; } = null!;

    public string Name { get; set; } = "";
}

public sealed class ManualMember
{
    public string Id { get; set; } = "";

    public string GroupId { get; set; } = "";

    public ManualGroup Group { get; set; } = null!;

    public string Name { get; set; } = "";

    public DateOnly? BirthDate { get; set; }

    public List<ManualMemberSubGroup> SubGroups { get; set; } = [];
}

public sealed class ManualMemberSubGroup
{
    public string MemberId { get; set; } = "";

    public ManualMember Member { get; set; } = null!;

    public string SubGroupId { get; set; } = "";

    public ManualSubGroup SubGroup { get; set; } = null!;
}

public sealed class ManualEvent
{
    public string Id { get; set; } = "";

    public string GroupId { get; set; } = "";

    public ManualGroup Group { get; set; } = null!;

    public string Name { get; set; } = "";

    public DateTimeOffset Start { get; set; }

    public DateTimeOffset End { get; set; }

    public bool Cancelled { get; set; }

    public List<ManualInvitation> Invitations { get; set; } = [];

    /// <summary>The members who host (train) the event; they take part in no statistic of it.</summary>
    public List<ManualEventTrainer> Trainers { get; set; } = [];
}

/// <summary>One member who hosts a manual event.</summary>
public sealed class ManualEventTrainer
{
    public string EventId { get; set; } = "";

    public ManualEvent Event { get; set; } = null!;

    public string MemberId { get; set; } = "";

    public ManualMember Member { get; set; } = null!;
}

/// <summary>One member invited to a manual event and what they answered.</summary>
public sealed class ManualInvitation
{
    public string EventId { get; set; } = "";

    public ManualEvent Event { get; set; } = null!;

    public string MemberId { get; set; } = "";

    public ManualMember Member { get; set; } = null!;

    /// <summary>Stored by name (<c>Accepted</c>, <c>Declined</c>, …) like <see cref="ExamParticipant.Rsvp"/>.</summary>
    public Rsvp Rsvp { get; set; }
}

/// <summary>
/// One change somebody made to exams, payments, belts, members, users or roles. Written by <c>AuditFilter</c> after the change succeeded;
/// never changed afterwards. The user is an id of the accounts database, which is a database of its own, so no foreign key.
/// </summary>
public sealed class AuditEntry
{
    public long Id { get; set; }

    public DateTimeOffset At { get; set; }

    public int? UserId { get; set; }

    /// <summary>The user's name when the change was made, so that the log still reads after the user is gone.</summary>
    public string UserName { get; set; } = "";

    public string Area { get; set; } = "";

    public string Action { get; set; } = "";

    public string? Target { get; set; }

    /// <summary>What was sent, as JSON, without passwords.</summary>
    public string? Details { get; set; }
}
