using Microsoft.EntityFrameworkCore;
using Kyuplan.Api.Domain;
using Kyuplan.Api.Spond;
using Kyuplan.Api.Statistics;
using Kyuplan.Api.Infrastructure;
using Kyuplan.Contracts;

namespace Kyuplan.Api.Persistence;

public sealed class TagNotFoundException(int tagId) : Exception($"No tag with id {tagId}.");

/// <summary>
/// Reads and writes the tags comments can carry. A tag may be connected to one Spond event: connecting one looks the event up on Spond and
/// takes its name and start, and reading the tags keeps those current (an event that was renamed or moved is still the same tag's event).
/// Input is expected to be validated by the endpoints (<see cref="TagRules"/>).
/// </summary>
public sealed class TagService(PlannerDbContext db, StatisticsService statistics, TimeProvider time, ILogger<TagService> logger)
{
    /// <summary>All tags by name, each with how many comments carry it.</summary>
    public async Task<IReadOnlyList<TagDto>> ListAsync(CancellationToken cancellationToken)
    {
        var tags = await db.Tags.ToListAsync(cancellationToken);
        await FollowEventsAsync(tags, cancellationToken);
        var counts = await CountsAsync(cancellationToken);

        return tags
            .OrderBy(t => t.Name, StringComparer.CurrentCultureIgnoreCase).ThenBy(t => t.Id)
            .Select(t => ToDto(t, counts.GetValueOrDefault(t.Id)))
            .ToList();
    }

    /// <exception cref="TagNotFoundException"/>
    public async Task<TagDto> GetAsync(int tagId, CancellationToken cancellationToken)
    {
        var tag = await FindAsync(tagId, cancellationToken);
        await FollowEventsAsync([tag], cancellationToken);
        return ToDto(tag, await CountAsync(tagId, cancellationToken));
    }

    /// <exception cref="GroupNotFoundException">The event's group is not one of the account's.</exception>
    /// <exception cref="EventNotFoundException">The event is not in the search window of the group.</exception>
    /// <exception cref="AlreadyExistsException">The name is taken.</exception>
    public async Task<TagDto> CreateAsync(TagInputDto input, CancellationToken cancellationToken)
    {
        var tag = new Tag { Name = input.Name.Trim(), CreatedAt = time.GetUtcNow() };
        await ConnectAsync(tag, input, cancellationToken);
        db.Tags.Add(tag);

        await SaveAsync(tag.Name, cancellationToken);
        return ToDto(tag, 0);
    }

    /// <summary>Replaces the name and the event of a tag; the comments that carry it keep it.</summary>
    /// <exception cref="TagNotFoundException"/>
    /// <exception cref="AlreadyExistsException">The name is taken.</exception>
    public async Task<TagDto> UpdateAsync(int tagId, TagInputDto input, CancellationToken cancellationToken)
    {
        var tag = await FindAsync(tagId, cancellationToken);
        tag.Name = input.Name.Trim();
        await ConnectAsync(tag, input, cancellationToken);

        await SaveAsync(tag.Name, cancellationToken);
        return ToDto(tag, await CountAsync(tagId, cancellationToken));
    }

    /// <summary>Deletes a tag and takes it off the comments that carry it; the comments stay.</summary>
    /// <exception cref="TagNotFoundException"/>
    public async Task DeleteAsync(int tagId, CancellationToken cancellationToken)
    {
        db.Tags.Remove(await FindAsync(tagId, cancellationToken));
        await db.SaveChangesAsync(cancellationToken);
    }

    /// <summary>The event of a tag as the clients get it, or <c>null</c> for a tag without one.</summary>
    internal static TagEventDto? EventOf(Tag tag) =>
        tag.EventId is null ? null : new TagEventDto(tag.EventGroupId!, tag.EventId, tag.EventName!, tag.EventStart!.Value);

    private async Task<Tag> FindAsync(int tagId, CancellationToken cancellationToken) =>
        await db.Tags.FirstOrDefaultAsync(t => t.Id == tagId, cancellationToken) ?? throw new TagNotFoundException(tagId);

    /// <summary>
    /// Makes the tag about the event the input names, or about none. The event that is the tag's already is not looked up again: it may be
    /// out of the search window by now, which is no reason to refuse a rename.
    /// </summary>
    private async Task ConnectAsync(Tag tag, TagInputDto input, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(input.EventId))
        {
            tag.EventGroupId = tag.EventId = tag.EventName = null;
            tag.EventStart = null;
            return;
        }

        if (tag.EventId == input.EventId && tag.EventGroupId == input.GroupId) return;

        var current = await statistics.FindEventAsync(input.GroupId!, input.EventId, cancellationToken);
        tag.EventGroupId = input.GroupId;
        tag.EventId = current.Id;
        tag.EventName = current.Name;
        tag.EventStart = current.Start;
    }

    /// <summary>
    /// Brings the name and start of the tags' events in line with Spond. An event outside the search window, or a group that cannot be
    /// read (Spond is down), keeps what was stored: listing tags must not depend on Spond.
    /// </summary>
    private async Task FollowEventsAsync(IReadOnlyList<Tag> tags, CancellationToken cancellationToken)
    {
        foreach (var group in tags.Where(t => t.EventId is not null).GroupBy(t => t.EventGroupId!))
        {
            IReadOnlyList<EventInfo> events;
            try
            {
                events = await statistics.GetWindowEventsAsync(group.Key, cancellationToken);
            }
            catch (Exception ex) when (ex is SpondUnavailableException or GroupNotFoundException)
            {
                logger.LogWarning(ex, "Could not read the events of group {GroupId} from Spond; its tags keep the event name and start stored.", group.Key);
                continue;
            }

            foreach (var tag in group)
            {
                if (events.FirstOrDefault(e => e.Id == tag.EventId) is not { } current) continue;

                tag.EventName = current.Name;
                tag.EventStart = current.Start;
            }
        }

        await db.SaveChangesAsync(cancellationToken);
    }

    private async Task<Dictionary<int, int>> CountsAsync(CancellationToken cancellationToken) =>
        await db.CommentTags.AsNoTracking().GroupBy(t => t.TagId).Select(g => new { TagId = g.Key, Count = g.Count() })
            .ToDictionaryAsync(g => g.TagId, g => g.Count, cancellationToken);

    private Task<int> CountAsync(int tagId, CancellationToken cancellationToken) =>
        db.CommentTags.AsNoTracking().CountAsync(t => t.TagId == tagId, cancellationToken);

    private static TagDto ToDto(Tag tag, int commentCount) => new(tag.Id, tag.Name, EventOf(tag), commentCount);

    /// <summary>The unique index decides what a duplicate is, so two requests racing for one name cannot both win.</summary>
    private async Task SaveAsync(string name, CancellationToken cancellationToken)
    {
        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException ex) when (ex.IsUniqueViolation())
        {
            throw new AlreadyExistsException($"There already is a tag named '{name}'.");
        }
    }
}
