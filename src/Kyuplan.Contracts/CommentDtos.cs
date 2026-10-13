namespace Kyuplan.Contracts;

/// <summary>
/// The Spond event a tag is connected to (a training, an exam, ...). The name and the start are the event's as the API last saw them, so
/// that a tag still reads when Spond cannot be reached.
/// </summary>
/// <param name="GroupId">The Spond group the event belongs to; Spond lists events per group.</param>
/// <param name="Start">Start in the club time zone.</param>
public sealed record TagEventDto(string GroupId, string EventId, string Name, DateTimeOffset Start);

/// <summary>A topic a comment can be about, e.g. "Belt exam September". It is connected to one event or to none.</summary>
/// <param name="Name">Unique regardless of case.</param>
/// <param name="Event">The event the tag is about; <c>null</c> when it is connected to none.</param>
/// <param name="CommentCount">How many comments carry the tag.</param>
public sealed record TagDto(int Id, string Name, TagEventDto? Event, int CommentCount);

/// <summary>What a client sends to create a tag or to replace everything about one. <paramref name="GroupId"/> and <paramref name="EventId"/> are both given or both left out.</summary>
public sealed record TagInputDto(string Name, string? GroupId = null, string? EventId = null);

/// <summary>A tag as it hangs on a comment.</summary>
public sealed record CommentTagDto(int Id, string Name, TagEventDto? Event);

/// <summary>
/// One edit of a comment: who made it, when, and how. The comment keeps what it said before (<paramref name="PreviousBody"/>), so that
/// every version can be read again.
/// </summary>
/// <param name="EditorName">The editor's user name now, or as it was when the edit was made if the user is gone.</param>
/// <param name="TextChanged">The words changed.</param>
/// <param name="FormattingChanged">The words are the same, only how they are styled changed. Never set together with <paramref name="TextChanged"/>.</param>
/// <param name="TagsAdded">Names of the tags the edit put on the comment, as they were then.</param>
/// <param name="TagsRemoved">Names of the tags the edit took off, as they were then.</param>
/// <param name="PreviousBody">The comment's body before this edit, in the markup of <see cref="CommentFormat"/>.</param>
public sealed record CommentEditDto(
    int Id,
    int EditorId,
    string EditorName,
    DateTimeOffset EditedAt,
    bool TextChanged,
    bool FormattingChanged,
    IReadOnlyList<string> TagsAdded,
    IReadOnlyList<string> TagsRemoved,
    string PreviousBody);

/// <summary>A comment on a member, or a reply to another comment of the same member.</summary>
/// <param name="ParentId">The comment this one replies to; <c>null</c> for a comment that starts a thread.</param>
/// <param name="Body">The text with its styling, written in the small markup of <see cref="CommentFormat"/>.</param>
/// <param name="AuthorId">The user who wrote it.</param>
/// <param name="AuthorName">The author's user name now, or as it was when the comment was written if the user is gone.</param>
/// <param name="Tags">By name.</param>
/// <param name="Edits">Oldest first; empty for a comment that was never changed.</param>
public sealed record CommentDto(
    int Id,
    int MemberId,
    int? ParentId,
    string Body,
    int AuthorId,
    string AuthorName,
    DateTimeOffset CreatedAt,
    IReadOnlyList<CommentTagDto> Tags,
    IReadOnlyList<CommentEditDto> Edits);

/// <summary>What a client sends to write a comment. Tags are given by id; a reply names the comment it replies to.</summary>
public sealed record CommentInputDto(string Body, IReadOnlyList<int> TagIds, int? ParentId = null);

/// <summary>What a client sends to change a comment: the body and the tags are replaced. A comment cannot be moved to another thread.</summary>
public sealed record CommentUpdateDto(string Body, IReadOnlyList<int> TagIds);
