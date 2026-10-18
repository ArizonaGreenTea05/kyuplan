using System.Security.Claims;
using Microsoft.EntityFrameworkCore;
using Kyuplan.Api.Accounts;
using Kyuplan.Api.Infrastructure;
using Kyuplan.Contracts;

namespace Kyuplan.Api.Persistence;

public sealed class CommentNotFoundException(int commentId) : Exception($"No comment with id {commentId}.");

/// <summary>Who is writing, changing or deleting a comment: what the login says, and whether they may delete their own comments or all.</summary>
public sealed record CommentUser(int Id, string Name, bool CanDeleteOwn, bool CanDeleteAny)
{
    public static CommentUser From(ClaimsPrincipal principal)
    {
        var any = principal.Has(Permission.CommentsDeleteAny);
        return new(
            int.Parse(principal.FindFirstValue(ClaimTypes.NameIdentifier)!),
            principal.FindFirstValue(ClaimTypes.Name)!,
            any || principal.Has(Permission.CommentsDelete),
            any);
    }
}

/// <summary>
/// Reads and writes the comments on members. Who may read, write, change and delete them is up to the permissions of the user, which the
/// endpoints check (every change is recorded with who made it and how, see <see cref="CommentEdit"/>); a comment is deleted by whoever may
/// delete comments of others, or by its author who may delete their own, and takes its replies
/// with it. Input is expected to be validated by the endpoints (<see cref="CommentRules"/>). Users live in the accounts database: the
/// names shown are the users' current display names (never the names they sign in with), and the ones written down at the time for users
/// who are gone. Moments are given in the club's time zone.
/// </summary>
public sealed class CommentService(PlannerDbContext db, AccountsDbContext accounts, TimeProvider time, ClubClock clock)
{
    /// <summary>The comments on a member, oldest first; a reply comes after the comment it replies to.</summary>
    /// <exception cref="MemberNotFoundException"/>
    public async Task<IReadOnlyList<CommentDto>> ListAsync(int memberId, CancellationToken cancellationToken)
    {
        await EnsureMemberAsync(memberId, cancellationToken);

        var comments = await Query().Where(c => c.MemberId == memberId).ToListAsync(cancellationToken);
        return await ToDtosAsync(comments, cancellationToken);
    }

    /// <summary>The member a comment is about, or <c>null</c> when there is no such comment.</summary>
    public async Task<int?> MemberIdOfAsync(int commentId, CancellationToken cancellationToken) =>
        await db.Comments.AsNoTracking().Where(c => c.Id == commentId).Select(c => (int?)c.MemberId).FirstOrDefaultAsync(cancellationToken);

    /// <summary>The comments that carry one of these tags, oldest first; a calendar page lists what was written about the members of one event.</summary>
    public async Task<IReadOnlyList<CommentDto>> ListForTagsAsync(IReadOnlyList<int> tagIds, CancellationToken cancellationToken)
    {
        var comments = await Query().Where(c => c.Tags.Any(t => tagIds.Contains(t.TagId))).ToListAsync(cancellationToken);
        return await ToDtosAsync(comments, cancellationToken);
    }

    /// <summary>Writes a comment on a member, or a reply to one of the member's comments.</summary>
    /// <exception cref="MemberNotFoundException"/>
    /// <exception cref="InvalidInputException">The comment to reply to is not one of this member's, or a tag does not exist.</exception>
    public async Task<CommentDto> CreateAsync(int memberId, CommentInputDto input, CommentUser user, CancellationToken cancellationToken)
    {
        await EnsureMemberAsync(memberId, cancellationToken);

        if (input.ParentId is { } parentId && !await db.Comments.AnyAsync(c => c.Id == parentId && c.MemberId == memberId, cancellationToken))
            throw new InvalidInputException($"There is no comment with id {parentId} on this member to reply to.");

        var comment = new Comment
        {
            MemberId = memberId,
            ParentId = input.ParentId,
            Body = CommentFormat.Clean(input.Body),
            AuthorId = user.Id,
            AuthorName = user.Name,
            CreatedAt = time.GetUtcNow(),
            Tags = [.. (await FindTagsAsync(input.TagIds, cancellationToken)).Select(tag => new CommentTag { Tag = tag })],
        };
        db.Comments.Add(comment);
        await db.SaveChangesAsync(cancellationToken);

        return await GetAsync(comment.Id, cancellationToken);
    }

    /// <summary>
    /// Replaces the body and the tags of a comment. What differs is recorded as an edit by <paramref name="user"/>; a request that changes
    /// nothing leaves no edit behind.
    /// </summary>
    /// <exception cref="CommentNotFoundException"/>
    /// <exception cref="InvalidInputException">A tag does not exist.</exception>
    public async Task<CommentDto> UpdateAsync(int commentId, CommentUpdateDto input, CommentUser user, CancellationToken cancellationToken)
    {
        var comment = await db.Comments
                          .Include(c => c.Tags).ThenInclude(t => t.Tag)
                          .FirstOrDefaultAsync(c => c.Id == commentId, cancellationToken)
                      ?? throw new CommentNotFoundException(commentId);
        var tags = await FindTagsAsync(input.TagIds, cancellationToken);
        var body = CommentFormat.Clean(input.Body);

        var held = comment.Tags.Select(t => t.Tag).ToList();
        var added = tags.Where(t => held.All(h => h.Id != t.Id)).ToList();
        var removed = held.Where(h => tags.All(t => t.Id != h.Id)).ToList();
        var (textChanged, formattingChanged) = CommentFormat.Compare(comment.Body, body);

        if (textChanged || formattingChanged || added.Count + removed.Count > 0)
        {
            db.CommentEdits.Add(new CommentEdit
            {
                CommentId = comment.Id,
                EditorId = user.Id,
                EditorName = user.Name,
                EditedAt = time.GetUtcNow(),
                TextChanged = textChanged,
                FormattingChanged = formattingChanged,
                TagsAdded = Names(added),
                TagsRemoved = Names(removed),
                PreviousBody = comment.Body,
            });

            comment.Body = body;
            db.CommentTags.RemoveRange(comment.Tags.Where(link => removed.Any(r => r.Id == link.TagId)));
            db.CommentTags.AddRange(added.Select(tag => new CommentTag { CommentId = comment.Id, TagId = tag.Id }));
            await db.SaveChangesAsync(cancellationToken);
        }

        return await GetAsync(commentId, cancellationToken);
    }

    /// <summary>Deletes a comment and the replies to it.</summary>
    /// <exception cref="CommentNotFoundException"/>
    /// <exception cref="ForbiddenException">The user wrote the comment and may not delete their own, or somebody else did and the user may not delete those.</exception>
    public async Task DeleteAsync(int commentId, CommentUser user, CancellationToken cancellationToken)
    {
        var comment = await db.Comments.FirstOrDefaultAsync(c => c.Id == commentId, cancellationToken)
                      ?? throw new CommentNotFoundException(commentId);
        if (!(comment.AuthorId == user.Id ? user.CanDeleteOwn : user.CanDeleteAny))
            throw new ForbiddenException(comment.AuthorId == user.Id
                ? $"Deleting comments needs the permission {Permission.CommentsDelete}."
                : $"Deleting the comments of others needs the permission {Permission.CommentsDeleteAny}.");

        db.Comments.Remove(comment); // the replies, the edits and the tags on them go with it (cascade)
        await db.SaveChangesAsync(cancellationToken);
    }

    private IQueryable<Comment> Query() => db.Comments.AsNoTracking().AsSplitQuery()
        .Include(c => c.Tags).ThenInclude(t => t.Tag)
        .Include(c => c.Edits.OrderBy(e => e.Id))
        .OrderBy(c => c.Id);

    private async Task<CommentDto> GetAsync(int commentId, CancellationToken cancellationToken) =>
        (await ToDtosAsync(await Query().Where(c => c.Id == commentId).ToListAsync(cancellationToken), cancellationToken)).Single();

    private async Task EnsureMemberAsync(int memberId, CancellationToken cancellationToken)
    {
        if (!await db.Members.AnyAsync(m => m.Id == memberId, cancellationToken)) throw new MemberNotFoundException(memberId);
    }

    private async Task<List<Tag>> FindTagsAsync(IReadOnlyList<int> tagIds, CancellationToken cancellationToken)
    {
        var ids = tagIds.Distinct().ToList();
        var tags = await db.Tags.Where(t => ids.Contains(t.Id)).ToListAsync(cancellationToken);
        if (tags.Count != ids.Count)
            throw new InvalidInputException($"There is no tag with id {ids.First(id => tags.All(t => t.Id != id))}.");

        return tags;
    }

    private static List<string> Names(IEnumerable<Tag> tags) =>
        [.. tags.Select(t => t.Name).Order(StringComparer.CurrentCultureIgnoreCase)];

    private async Task<IReadOnlyList<CommentDto>> ToDtosAsync(IReadOnlyList<Comment> comments, CancellationToken cancellationToken)
    {
        var userIds = comments.SelectMany(c => c.Edits.Select(e => e.EditorId).Append(c.AuthorId)).ToHashSet();
        var names = await accounts.Users.AsNoTracking().Where(u => userIds.Contains(u.Id)).ToDictionaryAsync(u => u.Id, u => u.DisplayName, cancellationToken);

        return comments.Select(c => new CommentDto(
            c.Id, c.MemberId, c.ParentId, c.Body,
            c.AuthorId, names.GetValueOrDefault(c.AuthorId, c.AuthorName), clock.ToClub(c.CreatedAt),
            [.. c.Tags.Select(t => t.Tag).OrderBy(t => t.Name, StringComparer.CurrentCultureIgnoreCase).Select(t => new CommentTagDto(t.Id, t.Name, TagService.EventOf(t)))],
            [.. c.Edits.OrderBy(e => e.Id).Select(e => new CommentEditDto(
                e.Id, e.EditorId, names.GetValueOrDefault(e.EditorId, e.EditorName), clock.ToClub(e.EditedAt),
                e.TextChanged, e.FormattingChanged, [.. e.TagsAdded], [.. e.TagsRemoved], e.PreviousBody))])).ToList();
    }
}
