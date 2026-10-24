using System.Security.Claims;
using Kyuplan.Api.Accounts;
using Kyuplan.Api.Infrastructure;
using Kyuplan.Api.Persistence;
using Kyuplan.Contracts;

namespace Kyuplan.Api.Endpoints;

/// <summary>Belts and what they require (<c>/api/belts</c>), and what the users know about the club's members that Spond does not (<c>/api/members</c>): the belt and the day it was received, the start day, the birth date.</summary>
public static class BeltEndpoints
{
    private const int MaxPrerequisites = 100;

    public static IEndpointRouteBuilder MapBeltEndpoints(this IEndpointRouteBuilder app)
    {
        var belts = app.MapGroup("/api/belts").WithTags("Belts");

        belts.MapGet("/", (BeltService service, CancellationToken ct) => service.ListAsync(ct))
            .WithSummary("All belts in the order of the progression (a belt comes after the belts it requires), each with what it requires and how many members hold it.")
            .RequirePermission(Permission.ReadsBelts);

        belts.MapGet("/{beltId:int}", (int beltId, BeltService service, CancellationToken ct) => service.GetAsync(beltId, ct))
            .WithName("GetBelt")
            .WithSummary("One belt.")
            .RequirePermission(Permission.ReadsBelts);

        belts.MapPost("/", async (BeltInputDto input, BeltService service, CancellationToken ct) =>
            {
                if (Validate(input) is { } errors) return Results.ValidationProblem(errors);

                var belt = await service.CreateAsync(input, ct);
                return Results.CreatedAtRoute("GetBelt", new { beltId = belt.Id }, belt);
            })
            .WithSummary("Create a belt. Its name must be new (regardless of case); it is taken from one of the belts in prerequisites, each with the minimum age and waiting time that apply when coming from it, or from none (the first belt).")
            .Audited(AuditAreas.Belts, AuditActions.Create)
            .RequirePermission(Permission.BeltsEdit);

        belts.MapPut("/{beltId:int}", async (int beltId, BeltInputDto input, BeltService service, CancellationToken ct) =>
            {
                if (Validate(input) is { } errors) return Results.ValidationProblem(errors);

                return Results.Ok(await service.UpdateAsync(beltId, input, ct));
            })
            .WithSummary("Replace the name, colours and requirements of a belt. A belt cannot require itself, directly or through other belts.")
            .Audited(AuditAreas.Belts, AuditActions.Update)
            .RequirePermission(Permission.BeltsEdit);

        belts.MapDelete("/{beltId:int}", async (int beltId, BeltService service, CancellationToken ct) =>
            {
                await service.DeleteAsync(beltId, ct);
                return Results.NoContent();
            })
            .WithSummary("Delete a belt. Refused (409) while a member holds it, another belt requires it or it was handed out at an exam.")
            .Audited(AuditAreas.Belts, AuditActions.Delete)
            .RequirePermission(Permission.BeltsEdit);

        var members = app.MapGroup("/api/members").WithTags("Members");

        members.MapGet("/", async (string? groupId, ClaimsPrincipal principal, MemberService service, CancellationToken ct) =>
            {
                var list = await service.ListAsync(groupId, ct);

                // How many comments a member has is part of the comments: whoever may not read them is not told that there are any.
                var mayRead = OwnData.MayRead(principal, await service.IdsOfUserAsync(principal.UserId(), ct), Permission.CommentsView, Permission.CommentsViewOwn);
                return list.Select(m => mayRead(m.Id) ? m : m with { CommentCount = 0 }).ToList();
            })
            .WithSummary("The members the API holds, by name, optionally only those of one Spond group. They are a copy of Spond's, as of the last sync. The number of comments on a member is only given to whoever may read them (comments.view; about a member the user is themselves comments.viewOwn), else it is 0.")
            .RequirePermission(Permission.MembersView);

        members.MapGet("/hidden", (string? groupId, MemberService service, CancellationToken ct) => service.ListHiddenAsync(groupId, ct))
            .WithSummary("The members a user hid, by name, optionally only those of one Spond group: what the page of hidden members shows. Hidden members are in no other response.")
            .RequirePermission(Permission.MembersHidden);

        members.MapPost("/{memberId:int}/hide", async (int memberId, MemberService service, CancellationToken ct) =>
            {
                await service.HideAsync(memberId, ct);
                return Results.NoContent();
            })
            .WithSummary("Hide a member: no statistic counts them and no response lists them (404 for a member that is hidden already). Only the list of hidden members shows them again.")
            .Audited(AuditAreas.Members, AuditActions.Update)
            .RequirePermission(Permission.MembersEdit);

        members.MapPost("/{memberId:int}/unhide", async (int memberId, MemberService service, CancellationToken ct) =>
                Results.Ok(await service.UnhideAsync(memberId, ct)))
            .WithSummary("Make a hidden member visible again (404 when the member is not hidden).")
            .Audited(AuditAreas.Members, AuditActions.Update)
            .RequirePermission(Permission.MembersHidden);

        members.MapGet("/choices", (MemberService service, CancellationToken ct) => service.ChoicesAsync(ct))
            .WithSummary("Every member by name with the user who already is that member (if any): what the users page offers when an administrator says which members a user is. Nothing else about the members.")
            .RequirePermission(Permission.MembersView, Permission.UsersEdit);

        members.MapPut("/{memberId:int}", async (int memberId, MemberInputDto input, MemberService service, ClubClock clock, CancellationToken ct) =>
            {
                if (Validate(input, clock.Today) is { } errors) return Results.ValidationProblem(errors);

                return Results.Ok(await service.UpdateAsync(memberId, input, ct));
            })
            .WithSummary("Set the belt (or none), the day it was received, the start day and the birth date (each or none) of a member. Name and group are Spond's and cannot be changed.")
            .Audited(AuditAreas.Members, AuditActions.Update)
            .RequirePermission(Permission.MembersEdit);

        return app;
    }

    internal static Dictionary<string, string[]>? Validate(BeltInputDto? input)
    {
        var errors = new Dictionary<string, string[]>();
        if (input is null) return Fail(errors, nameof(input), "A request body is required.");

        Check(errors, nameof(input.Name), BeltRules.CheckName(input.Name));
        Check(errors, nameof(input.PrimaryColor), BeltRules.CheckPrimaryColor(input.PrimaryColor));
        Check(errors, nameof(input.SecondaryColor), BeltRules.CheckSecondaryColor(input.SecondaryColor));

        if (input.Prerequisites is not { } prerequisites)
            errors[nameof(input.Prerequisites)] = ["prerequisites is required (it may be empty)."];
        else if (prerequisites.Count > MaxPrerequisites)
            errors[nameof(input.Prerequisites)] = [$"At most {MaxPrerequisites} prerequisites."];
        else
        {
            for (var i = 0; i < prerequisites.Count; i++)
            {
                var key = $"{nameof(input.Prerequisites)}[{i}]";
                if (prerequisites[i] is not { } prerequisite)
                {
                    errors[key] = ["A prerequisite must not be null."];
                    continue;
                }

                Check(errors, $"{key}.{nameof(prerequisite.MinAgeYears)}", BeltRules.CheckMinAgeYears(prerequisite.MinAgeYears));
                Check(errors, $"{key}.{nameof(prerequisite.MinWaitMonths)}", BeltRules.CheckMinWaitMonths(prerequisite.MinWaitMonths));
                if (prerequisites.Take(i).Any(earlier => earlier?.BeltId == prerequisite.BeltId))
                    errors[$"{key}.{nameof(prerequisite.BeltId)}"] = [$"The belt {prerequisite.BeltId} is listed more than once."];
            }
        }

        return errors.Count == 0 ? null : errors;
    }

    internal static Dictionary<string, string[]>? Validate(MemberInputDto? input, DateOnly today)
    {
        var errors = new Dictionary<string, string[]>();
        if (input is null) return Fail(errors, nameof(input), "A request body is required.");

        Check(errors, nameof(input.StartedAt), MemberRules.CheckStartedAt(input.StartedAt, today));
        Check(errors, nameof(input.BirthDate), MemberRules.CheckBirthDate(input.BirthDate, today));
        Check(errors, nameof(input.BeltSince), MemberRules.CheckBeltSince(input.BeltSince, today));

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
