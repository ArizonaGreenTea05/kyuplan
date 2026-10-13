using System.Security.Claims;
using Kyuplan.Api.Accounts;
using Kyuplan.Api.Persistence;
using Kyuplan.Contracts;

namespace Kyuplan.Api.Endpoints;

/// <summary>
/// What the users write about members: comments with replies and tags (<c>/api/members/{id}/comments</c>, <c>/api/comments</c>), and the tags
/// themselves (<c>/api/tags</c>), which can be connected to a Spond event.
/// </summary>
public static class CommentEndpoints
{
    public static IEndpointRouteBuilder MapCommentEndpoints(this IEndpointRouteBuilder app)
    {
        var comments = app.MapGroup("/api").WithTags("Comments");

        comments.MapGet("/members/{memberId:int}/comments", (int memberId, CommentService service, CancellationToken ct) => service.ListAsync(memberId, ct))
            .WithSummary("The comments on a member, oldest first (a reply comes after the comment it replies to), each with its tags and the history of its edits.")
            .RequirePermission(Permission.CommentsView);

        comments.MapPost("/members/{memberId:int}/comments", async (int memberId, CommentInputDto input, ClaimsPrincipal principal, CommentService service, CancellationToken ct) =>
            {
                if (Validate(input) is { } errors) return Results.ValidationProblem(errors);

                var comment = await service.CreateAsync(memberId, input, CommentUser.From(principal), ct);
                return Results.Created($"/api/comments/{comment.Id}", comment);
            })
            .WithSummary("Write a comment on a member as the signed-in user, or a reply to one of the member's comments (parentId). The body is written in the markup of CommentFormat: **bold**, *italic*, __underlined__, ~~crossed out~~.")
            .RequirePermission(Permission.CommentsWrite);

        comments.MapPut("/comments/{commentId:int}", async (int commentId, CommentUpdateDto input, ClaimsPrincipal principal, CommentService service, CancellationToken ct) =>
            {
                if (Validate(input) is { } errors) return Results.ValidationProblem(errors);

                return Results.Ok(await service.UpdateAsync(commentId, input, CommentUser.From(principal), ct));
            })
            .WithSummary("Replace the body and the tags of a comment. Any signed-in user may; the change is recorded with who made it and how, and what the comment said before is kept. A request that changes nothing records nothing.")
            .RequirePermission(Permission.CommentsEdit);

        comments.MapDelete("/comments/{commentId:int}", async (int commentId, ClaimsPrincipal principal, CommentService service, CancellationToken ct) =>
            {
                await service.DeleteAsync(commentId, CommentUser.From(principal), ct);
                return Results.NoContent();
            })
            .WithSummary("Delete a comment and the replies to it. Its author needs comments.delete, anybody else comments.deleteAny (403 otherwise).")
            .RequirePermission(Permission.CommentsDelete, Permission.CommentsDeleteAny);

        var tags = app.MapGroup("/api/tags").WithTags("Tags");

        tags.MapGet("/", (TagService service, CancellationToken ct) => service.ListAsync(ct))
            .WithSummary("All tags by name, each with the event it is connected to (if any) and how many comments carry it.")
            .RequirePermission(Permission.ReadsTags);

        tags.MapGet("/{tagId:int}", (int tagId, TagService service, CancellationToken ct) => service.GetAsync(tagId, ct))
            .WithName("GetTag")
            .WithSummary("One tag.")
            .RequirePermission(Permission.ReadsTags);

        tags.MapPost("/", async (TagInputDto input, TagService service, CancellationToken ct) =>
            {
                if (Validate(input) is { } errors) return Results.ValidationProblem(errors);

                var tag = await service.CreateAsync(input, ct);
                return Results.CreatedAtRoute("GetTag", new { tagId = tag.Id }, tag);
            })
            .WithSummary("Create a tag. Its name must be new (regardless of case). With groupId and eventId it is connected to that Spond event (it must be in the search window); a tag is about one event or none.")
            .RequirePermission(Permission.TagsEdit);

        tags.MapPut("/{tagId:int}", async (int tagId, TagInputDto input, TagService service, CancellationToken ct) =>
            {
                if (Validate(input) is { } errors) return Results.ValidationProblem(errors);

                return Results.Ok(await service.UpdateAsync(tagId, input, ct));
            })
            .WithSummary("Replace the name and the event of a tag; leaving the event out disconnects it. The comments that carry the tag keep it.")
            .RequirePermission(Permission.TagsEdit);

        tags.MapDelete("/{tagId:int}", async (int tagId, TagService service, CancellationToken ct) =>
            {
                await service.DeleteAsync(tagId, ct);
                return Results.NoContent();
            })
            .WithSummary("Delete a tag and take it off the comments that carry it; the comments stay.")
            .RequirePermission(Permission.TagsEdit);

        return app;
    }

    internal static Dictionary<string, string[]>? Validate(CommentInputDto? input)
    {
        var errors = new Dictionary<string, string[]>();
        if (input is null) return Fail(errors, nameof(input), "A request body is required.");

        Check(errors, nameof(input.Body), CommentRules.CheckBody(input.Body));
        Check(errors, nameof(input.TagIds), CommentRules.CheckTagIds(input.TagIds));

        return errors.Count == 0 ? null : errors;
    }

    internal static Dictionary<string, string[]>? Validate(CommentUpdateDto? input)
    {
        var errors = new Dictionary<string, string[]>();
        if (input is null) return Fail(errors, nameof(input), "A request body is required.");

        Check(errors, nameof(input.Body), CommentRules.CheckBody(input.Body));
        Check(errors, nameof(input.TagIds), CommentRules.CheckTagIds(input.TagIds));

        return errors.Count == 0 ? null : errors;
    }

    internal static Dictionary<string, string[]>? Validate(TagInputDto? input)
    {
        var errors = new Dictionary<string, string[]>();
        if (input is null) return Fail(errors, nameof(input), "A request body is required.");

        Check(errors, nameof(input.Name), TagRules.CheckName(input.Name));
        Check(errors, nameof(input.EventId), TagRules.CheckEvent(input.GroupId, input.EventId));

        return errors.Count == 0 ? null : errors;
    }

    private static void Check(Dictionary<string, string[]> errors, string key, string? problem)
    {
        if (problem is not null) errors[key] = [problem];
    }

    private static Dictionary<string, string[]> Fail(Dictionary<string, string[]> errors, string key, string message)
    {
        errors[key] = [message];
        return errors;
    }
}
