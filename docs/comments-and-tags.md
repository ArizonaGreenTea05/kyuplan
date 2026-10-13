# Comments and tags: definitions

This page defines what users can write about a member, so that what is on screen can always be traced back to a rule. The rules are in
[`CommentService`](../src/Kyuplan.Api/Persistence/CommentService.cs), [`TagService`](../src/Kyuplan.Api/Persistence/TagService.cs)
and [`CommentFormat`](../src/Kyuplan.Contracts/CommentFormat.cs); the tests in `tests/Kyuplan.Tests/CommentApiTests.cs`,
`CommentFormatTests.cs`, `MemberCommentsViewModelTests.cs` and `TagsViewModelTests.cs` pin each rule below.

## Where

- **Comments** are on the **Members** page: every row has a **Comments (n)** button. It opens the comments on that member in place of the
  list (**Back to the members** returns to it).
- **Tags** have a page of their own (**Tags**), and can also be made and changed while a comment is written.

## Comments

A member has any number of comments. Each is written by a signed-in user and shows who and when (in the club's time zone).

- **Replies.** A comment can be a reply to another comment **of the same member**, and a reply can be replied to. The page lists the
  threads newest first; within a thread a comment is followed by its replies, oldest first, each moved in a step (not further than four
  steps).
- **Formatting.** The words can be **bold**, *italic*, <u>underlined</u> and ~~crossed out~~, partly or as a whole, and the styles combine.
  The comment is stored as text in a small markup (below); the editor has a button for each style (it puts the markers around what is
  selected, or takes them off again when the text has the style already) and shows a live preview. Nothing is ever rendered as HTML: the
  clients turn the markup into styled text themselves, so nothing a user types can inject anything.
- **Tags.** A comment can carry any number (up to 20) of tags, to say what topic it is about.
- **Who may do what** is up to the user's permissions (see *Permissions and roles* in the README), one for each thing: `comments.view` to
  read the comments on a member (and to see how many there are: without it the member list says 0, so whoever may only view members does
  not learn that there are comments), `comments.write` to write a comment or a reply, `comments.edit` to **change any comment**,
  `comments.delete` to delete **one's own** comments and `comments.deleteAny` to delete everybody's (403 otherwise; the button is hidden
  for everybody else). Deleting a comment deletes the replies to it, with their history; the question says how many replies go along.
  Choosing tags for a comment needs reading them: `tags.view`, `comments.write` or `comments.edit` is enough for that.
- **Length.** A comment must contain words (markers alone, or blank space, do not count) and is at most 4000 characters long, markers
  included. Line breaks are stored as `\n` and blank space around the whole comment is removed.
- **When a member goes.** Comments are the planner's own data about a member and go with the member: when Spond no longer lists the member
  the sync deletes them, like it deletes the members' invitations, and the exams' saved members and recorded belts.

### The markup

| You write | You get |
|---|---|
| `**bold**` | **bold** |
| `*italic*` | *italic* |
| `__underlined__` | <u>underlined</u> |
| `~~crossed out~~` | ~~crossed out~~ |
| `\*` `\_` `\~` `\\` | the character itself |

Styles may overlap and run over several lines. A marker only counts when it has its partner and sits at the edge of the text it styles
(a marker next to blank space does not), so `5 * 3 * 2`, `snake_case` and an unfinished `**` stay as they were typed.

### History: who edited it and how

Every edit that changes something is recorded, and the comment keeps what it said before:

- **who** made it (the user's current **display name**, never the name they sign in with; the display name they had at the time if the
  user is deleted) and **when**;
- **how**: *the text changed*, or *the formatting changed* (the words are the same, only the styling differs: never both), and which
  **tags were added or removed** (by name as they were then, so the history reads after a tag is renamed or deleted);
- **what the comment said before** (for an edit of the text or the formatting).

Saving without a change records nothing. Taking a tag off by deleting the tag is nobody's edit. The comment shows **Edited by Ben, 15 Oct
2026, 14:10** (with the number of edits when there are several); a click opens the history, the latest edit first.

## Tags

A tag is a topic comments can be about, e.g. *Belt exam September*.

- **Names** are unique regardless of case, 1 to 50 characters.
- **Events.** A tag is connected to **one Spond event or to none**; one event can have **many tags**. The events offered are those of the
  selected group's search window (as for exams); a tag keeps the event's name and start, and follows the event when it is renamed or moved
  on Spond. A tag whose event has left the search window keeps what it knew, and can still be renamed or disconnected.
- **Making and changing.** On the Tags page, or from the comment being written (**New tag**, and the pencil next to every tag): the same
  form, with the name and the event.
- **Deleting** a tag asks first and says on how many comments it is; it is taken off those comments, **the comments stay**.
- Making, changing and deleting tags needs `tags.edit`; the tags page needs `tags.view`.

## API

| Call | |
|---|---|
| `GET /api/members/{memberId}/comments` | `comments.view`. The member's comments, oldest first (a reply comes after its parent), each with `parentId`, `body`, `authorId`, `authorName`, `createdAt`, `tags` and `edits`. |
| `POST /api/members/{memberId}/comments` | `comments.write`. `{ "body", "tagIds", "parentId" }`: write a comment as the signed-in user, or a reply (`parentId` must be a comment of this member). `400` for a body without words, longer than 4000 characters, more than 20 tags, a tag that does not exist or another member's parent; `404` for an unknown member. |
| `PUT /api/comments/{commentId}` | `comments.edit`. `{ "body", "tagIds" }`: replace the body and the tags; records an edit by the signed-in user when something differs. |
| `DELETE /api/comments/{commentId}` | Delete the comment and its replies. `403` unless the user wrote it and has `comments.delete`, or has `comments.deleteAny`. |
| `GET /api/tags` · `GET /api/tags/{tagId}` | The tags by name, each with `event` (`groupId`, `eventId`, `name`, `start`; `null` = none) and `commentCount`. |
| `POST /api/tags` · `PUT /api/tags/{tagId}` · `DELETE /api/tags/{tagId}` | `{ "name", "groupId", "eventId" }` (group and event both or neither; the event must be in the group's search window, else `404`); `409` when the name is taken. |

`GET /api/members` also reports `commentCount` for every member, but only to somebody with `comments.view` (else `0`).
