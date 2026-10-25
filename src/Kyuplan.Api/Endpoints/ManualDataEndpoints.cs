using Kyuplan.Api.Accounts;
using Kyuplan.Api.Infrastructure;
using Kyuplan.Api.Persistence;
using Kyuplan.Api.Spond;
using Kyuplan.Contracts;

namespace Kyuplan.Api.Endpoints;

/// <summary>
/// The groups, members and events users keep by hand (<c>/api/manual</c>), which the rest of the API reads as if Spond had delivered them while the data
/// source is <see cref="DataSourceKinds.Manual"/>. Every change is synced into the members and exams at once (<see cref="SpondSync"/>), so that
/// the other pages show it without waiting for the next sync.
/// </summary>
public static class ManualDataEndpoints
{
    public static IEndpointRouteBuilder MapManualDataEndpoints(this IEndpointRouteBuilder app)
    {
        var manual = app.MapGroup("/api/manual").WithTags("Manual data").RequirePermission(Permission.ManualDataEdit);

        // The rows only matter (and are synced) while they are the data source; anything else would write into a table nobody reads.
        manual.AddEndpointFilter(async (context, next) =>
            context.HttpContext.RequestServices.GetRequiredService<IDataSource>().Kind == DataSourceKinds.Manual
                ? await next(context)
                : Results.Problem("The data source is not 'manual'; choose it in the settings first.", statusCode: StatusCodes.Status409Conflict, title: "Manual data is not the data source"));

        manual.MapGet("/groups", (ManualDataService service, CancellationToken ct) => service.ListGroupsAsync(ct))
            .WithSummary("The groups kept by hand, with their subgroups.");

        manual.MapPost("/groups", (ManualNameInputDto input, ManualDataService service, SpondSync sync, CancellationToken ct) =>
                Write(Check(ManualDataRules.Check(input)), async () => await service.CreateGroupAsync(input, ct), sync, ct))
            .Audited(AuditAreas.Manual, AuditActions.Create)
            .WithSummary("Add a group.");

        manual.MapPut("/groups/{groupId}", (string groupId, ManualNameInputDto input, ManualDataService service, SpondSync sync, CancellationToken ct) =>
                Write(Check(ManualDataRules.Check(input)), async () => await service.RenameGroupAsync(groupId, input, ct), sync, ct))
            .Audited(AuditAreas.Manual, AuditActions.Update)
            .WithSummary("Rename a group.");

        manual.MapDelete("/groups/{groupId}", (string groupId, ManualDataService service, SpondSync sync, CancellationToken ct) =>
                Delete(() => service.DeleteGroupAsync(groupId, ct), sync, ct))
            .Audited(AuditAreas.Manual, AuditActions.Delete)
            .WithSummary("Delete a group with its subgroups, members and events. The planner forgets the members' belts and exams of the group at once.");

        manual.MapPost("/groups/{groupId}/subgroups", (string groupId, ManualNameInputDto input, ManualDataService service, SpondSync sync, CancellationToken ct) =>
                Write(Check(ManualDataRules.Check(input)), async () => await service.CreateSubGroupAsync(groupId, input, ct), sync, ct))
            .Audited(AuditAreas.Manual, AuditActions.Create)
            .WithSummary("Add a subgroup to a group.");

        manual.MapPut("/subgroups/{subGroupId}", (string subGroupId, ManualNameInputDto input, ManualDataService service, SpondSync sync, CancellationToken ct) =>
                Write(Check(ManualDataRules.Check(input)), async () => await service.RenameSubGroupAsync(subGroupId, input, ct), sync, ct))
            .Audited(AuditAreas.Manual, AuditActions.Update)
            .WithSummary("Rename a subgroup.");

        manual.MapDelete("/subgroups/{subGroupId}", (string subGroupId, ManualDataService service, SpondSync sync, CancellationToken ct) =>
                Delete(() => service.DeleteSubGroupAsync(subGroupId, ct), sync, ct))
            .Audited(AuditAreas.Manual, AuditActions.Delete)
            .WithSummary("Delete a subgroup; its members stay in the group.");

        manual.MapGet("/groups/{groupId}/members", (string groupId, ManualDataService service, CancellationToken ct) => service.ListMembersAsync(groupId, ct))
            .WithSummary("The members of a group.");

        manual.MapPost("/groups/{groupId}/members", (string groupId, ManualMemberInputDto input, ManualDataService service, SpondSync sync, CancellationToken ct) =>
                Write(Check(ManualDataRules.Check(input)), async () => await service.CreateMemberAsync(groupId, input, ct), sync, ct))
            .Audited(AuditAreas.Manual, AuditActions.Create)
            .WithSummary("Add a member to a group.");

        manual.MapPut("/members/{memberId}", (string memberId, ManualMemberInputDto input, ManualDataService service, SpondSync sync, CancellationToken ct) =>
                Write(Check(ManualDataRules.Check(input)), async () => await service.UpdateMemberAsync(memberId, input, ct), sync, ct))
            .Audited(AuditAreas.Manual, AuditActions.Update)
            .WithSummary("Replace the name, birth date and subgroups of a member.");

        manual.MapDelete("/members/{memberId}", (string memberId, ManualDataService service, SpondSync sync, CancellationToken ct) =>
                Delete(() => service.DeleteMemberAsync(memberId, ct), sync, ct))
            .Audited(AuditAreas.Manual, AuditActions.Delete)
            .WithSummary("Delete a member; the planner forgets their belt, comments and results at once.");

        manual.MapGet("/groups/{groupId}/events", (string groupId, ManualDataService service, CancellationToken ct) => service.ListEventsAsync(groupId, ct))
            .WithSummary("The events of a group, the latest first, with who is invited and what each answered.");

        manual.MapPost("/groups/{groupId}/events", (string groupId, ManualEventInputDto input, ManualDataService service, SpondSync sync, CancellationToken ct) =>
                Write(Check(ManualDataRules.Check(input)), async () => await service.CreateEventAsync(groupId, input, ct), sync, ct))
            .Audited(AuditAreas.Manual, AuditActions.Create)
            .WithSummary("Add an event to a group.");

        manual.MapPut("/events/{eventId}", (string eventId, ManualEventInputDto input, ManualDataService service, SpondSync sync, CancellationToken ct) =>
                Write(Check(ManualDataRules.Check(input)), async () => await service.UpdateEventAsync(eventId, input, ct), sync, ct))
            .Audited(AuditAreas.Manual, AuditActions.Update)
            .WithSummary("Replace an event, its invitations and answers included.");

        manual.MapDelete("/events/{eventId}", (string eventId, ManualDataService service, SpondSync sync, CancellationToken ct) =>
                Delete(() => service.DeleteEventAsync(eventId, ct), sync, ct))
            .Audited(AuditAreas.Manual, AuditActions.Delete)
            .WithSummary("Delete an event.");

        return app;
    }

    private static Dictionary<string, string[]>? Check(Dictionary<string, string[]> errors) => errors.Count == 0 ? null : errors;

    /// <summary>Runs a change (unless the input was wrong) and brings the members and exams in line with it.</summary>
    private static async Task<IResult> Write<T>(Dictionary<string, string[]>? errors, Func<Task<T>> change, SpondSync sync, CancellationToken cancellationToken)
    {
        if (errors is not null) return Results.ValidationProblem(errors);

        var result = await change();
        await sync.SyncAllAsync(cancellationToken, confirmRemoval: true); // the user just did this by hand
        return Results.Ok(result);
    }

    private static async Task<IResult> Delete(Func<Task> delete, SpondSync sync, CancellationToken cancellationToken)
    {
        await delete();
        await sync.SyncAllAsync(cancellationToken, confirmRemoval: true); // the user just did this by hand
        return Results.NoContent();
    }
}
