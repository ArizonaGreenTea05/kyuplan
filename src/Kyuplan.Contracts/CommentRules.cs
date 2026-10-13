namespace Kyuplan.Contracts;

/// <summary>
/// What a comment must look like. The API enforces it; the clients check the same rules so that they only offer to save what the API
/// will accept. Each check returns the problem in words, or <c>null</c> when it is fine.
/// </summary>
public static class CommentRules
{
    /// <summary>The length of the body as written, markers included.</summary>
    public const int MaxBodyLength = 4000;

    public const int MaxTagsPerComment = 20;

    public static string? CheckBody(string? body)
    {
        if (body is null || CommentFormat.ToPlainText(body).Trim().Length == 0) return "body must contain some text.";
        return body.Length > MaxBodyLength ? $"body must be at most {MaxBodyLength} characters long." : null;
    }

    public static string? CheckTagIds(IReadOnlyList<int>? tagIds)
    {
        if (tagIds is null) return "tagIds is required (it may be empty).";
        return tagIds.Distinct().Count() > MaxTagsPerComment ? $"At most {MaxTagsPerComment} tags per comment." : null;
    }
}

/// <summary>What a tag must look like, see <see cref="CommentRules"/>.</summary>
public static class TagRules
{
    /// <summary>The length of the name column of the database.</summary>
    public const int MaxNameLength = 50;

    public static string? CheckName(string? name)
    {
        var trimmed = name?.Trim();
        if (string.IsNullOrEmpty(trimmed)) return "name is required.";
        return trimmed.Length > MaxNameLength ? $"name must be at most {MaxNameLength} characters long." : null;
    }

    /// <summary>A tag is connected to one event or to none, so the group and the event are both there or both missing.</summary>
    public static string? CheckEvent(string? groupId, string? eventId)
    {
        var hasGroup = !string.IsNullOrWhiteSpace(groupId);
        var hasEvent = !string.IsNullOrWhiteSpace(eventId);
        return hasGroup == hasEvent ? null : "groupId and eventId must both be given, or both left out.";
    }
}
