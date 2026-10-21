using Kyuplan.Api.Accounts;
using Kyuplan.Api.Infrastructure;
using Kyuplan.Api.Persistence;
using Kyuplan.Contracts;

namespace Kyuplan.Api.Endpoints;

/// <summary>
/// Exams (Spond events that are exams) and the filters saved for them (<c>/api/exams</c>); one exam has many filters. Around an exam:
/// who is fit for it, who is saved for it, and who received which belt at it.
/// </summary>
public static class ExamEndpoints
{
    private const int MaxSubGroups = 100;
    private const int MaxCandidates = 2000;

    public static IEndpointRouteBuilder MapExamEndpoints(this IEndpointRouteBuilder app)
    {
        var exams = app.MapGroup("/api/exams").WithTags("Exams and saved filters");

        exams.MapGet("/", (ExamService service, CancellationToken ct) => service.ListAsync(ct))
            .WithSummary("All exams, the next one first, each with its saved filters.")
            .RequirePermission(Permission.ExamsView);

        exams.MapGet("/{examId:int}", (int examId, ExamService service, CancellationToken ct) => service.GetAsync(examId, ct))
            .WithName("GetExam")
            .WithSummary("One exam with its saved filters.")
            .RequirePermission(Permission.ExamsView);

        exams.MapPost("/", async (ExamInputDto input, ExamService service, CancellationToken ct) =>
            {
                if (Validate(input) is { } errors) return Results.ValidationProblem(errors);

                var exam = await service.CreateAsync(input, ct);
                return Results.CreatedAtRoute("GetExam", new { examId = exam.Id }, exam);
            })
            .WithSummary("Make a Spond event an exam. Its name and day are the event's; an event is at most one exam.")
            .Audited(AuditAreas.Exams, AuditActions.Create)
            .RequirePermission(Permission.ExamsEdit);

        exams.MapPut("/{examId:int}", async (int examId, ExamInputDto input, ExamService service, CancellationToken ct) =>
            {
                if (Validate(input) is { } errors) return Results.ValidationProblem(errors);

                return Results.Ok(await service.UpdateAsync(examId, input, ct));
            })
            .WithSummary("Point an exam at another Spond event (e.g. after it was moved to a new event); its filters stay.")
            .Audited(AuditAreas.Exams, AuditActions.Update)
            .RequirePermission(Permission.ExamsEdit);

        exams.MapDelete("/{examId:int}", async (int examId, ExamService service, CancellationToken ct) =>
            {
                await service.DeleteAsync(examId, ct);
                return Results.NoContent();
            })
            .WithSummary("Delete an exam together with all filters saved for it.")
            .Audited(AuditAreas.Exams, AuditActions.Delete)
            .RequirePermission(Permission.ExamsEdit);

        exams.MapGet("/{examId:int}/members", (int examId, ExamPlanningService service, CancellationToken ct) =>
                service.GetMembersAsync(examId, ct))
            .WithSummary("Everybody the exam knows, by name: those invited on Spond (with their answer), those saved for it, and those who received a belt at it.")
            .RequirePermission(Permission.ExamsView);

        exams.MapGet("/{examId:int}/suggestions", (int examId, int? filterId, ExamPlanningService service, CancellationToken ct) =>
                service.SuggestAsync(examId, filterId, ct))
            .WithSummary("The members of the exam's group, judged for the exam: the belt each would take and whether they meet its age, waiting time and the attendance of the filter, on the day of the exam. Attendance is measured with the saved filter filterId (of this exam's group), or without one with the default: previous exam to this one.")
            .RequirePermission(Permission.ExamsView);

        exams.MapPut("/{examId:int}/candidates", async (int examId, ExamCandidatesInputDto input, ExamPlanningService service, CancellationToken ct) =>
            {
                if (Validate(input) is { } errors) return Results.ValidationProblem(errors);

                return Results.Ok(await service.SetCandidatesAsync(examId, input.Add, input.Remove, ct));
            })
            .WithSummary("Save the members in add for the exam and take those in remove off its list, to be invited on Spond later; whoever is not named stays as they are. Nobody is invited by this.")
            .Audited(AuditAreas.Exams, AuditActions.Update)
            .RequirePermission(Permission.ExamsCandidates);

        exams.MapPut("/{examId:int}/results/{memberId:int}", async (int examId, int memberId, ExamResultInputDto input, ExamPlanningService service, CancellationToken ct) =>
            {
                if (input is null) return Results.ValidationProblem(new Dictionary<string, string[]> { [nameof(input)] = ["A request body is required."] });

                return Results.Ok(await service.SetResultAsync(examId, memberId, input.BeltId, ct));
            })
            .WithSummary("Record the belt a member received at the exam (it becomes the belt they hold, with the day of the exam as the day they got it, unless they got one later), or take the result back with beltId null. Only once the exam has taken place.")
            .Audited(AuditAreas.Exams, AuditActions.Update)
            .RequirePermission(Permission.ExamsResults);

        var filters = exams.MapGroup("/{examId:int}/filters");

        filters.MapGet("/{filterId:int}", (int examId, int filterId, ExamService service, CancellationToken ct) =>
                service.GetFilterAsync(examId, filterId, ct))
            .WithName("GetSavedFilter")
            .WithSummary("One saved filter of an exam.")
            .RequirePermission(Permission.ExamsView);

        filters.MapPost("/", async (int examId, SavedFilterInputDto input, ExamService service, CancellationToken ct) =>
            {
                if (Validate(input) is { } errors) return Results.ValidationProblem(errors);

                var filter = await service.CreateFilterAsync(examId, input, ct);
                return Results.CreatedAtRoute("GetSavedFilter", new { examId, filterId = filter.Id }, filter);
            })
            .WithSummary("Save a filter for an exam. Its name must be new within the exam.")
            .RequirePermission(Permission.ExamsFilters);

        filters.MapPut("/{filterId:int}", async (int examId, int filterId, SavedFilterInputDto input, ExamService service, CancellationToken ct) =>
            {
                if (Validate(input) is { } errors) return Results.ValidationProblem(errors);

                return Results.Ok(await service.UpdateFilterAsync(examId, filterId, input, ct));
            })
            .WithSummary("Replace the name and settings of a saved filter.")
            .RequirePermission(Permission.ExamsFilters);

        filters.MapDelete("/{filterId:int}", async (int examId, int filterId, ExamService service, CancellationToken ct) =>
            {
                await service.DeleteFilterAsync(examId, filterId, ct);
                return Results.NoContent();
            })
            .WithSummary("Delete a saved filter.")
            .RequirePermission(Permission.ExamsFilters);

        return app;
    }

    internal static Dictionary<string, string[]>? Validate(ExamInputDto? input)
    {
        var errors = new Dictionary<string, string[]>();
        if (input is null) return Fail(errors, nameof(input), "A request body is required.");

        CheckId(errors, nameof(input.GroupId), "groupId", input.GroupId);
        CheckId(errors, nameof(input.EventId), "eventId", input.EventId);
        CheckSubGroups(errors, nameof(input.SubGroupIds), input.SubGroupIds);

        return errors.Count == 0 ? null : errors;
    }

    internal static Dictionary<string, string[]>? Validate(ExamCandidatesInputDto? input)
    {
        var errors = new Dictionary<string, string[]>();
        if (input is null) return Fail(errors, nameof(input), "A request body is required.");

        if (input.Add is null)
            errors[nameof(input.Add)] = ["add is required (it may be empty)."];
        else if (input.Add.Count > MaxCandidates)
            errors[nameof(input.Add)] = [$"At most {MaxCandidates} members."];

        if (input.Remove is null)
            errors[nameof(input.Remove)] = ["remove is required (it may be empty)."];
        else if (input.Remove.Count > MaxCandidates)
            errors[nameof(input.Remove)] = [$"At most {MaxCandidates} members."];

        return errors.Count == 0 ? null : errors;
    }

    internal static Dictionary<string, string[]>? Validate(SavedFilterInputDto? input)
    {
        var errors = new Dictionary<string, string[]>();
        if (input is null) return Fail(errors, nameof(input), "A request body is required.");

        CheckName(errors, input.Name);

        if (input.Settings is not { } s)
            return Fail(errors, nameof(input.Settings), "settings are required.");

        CheckId(errors, nameof(s.GroupId), "groupId", s.GroupId);

        if (s.From == default || s.To == default)
            errors[nameof(s.From)] = ["from and to are required."];
        else if (s.From > s.To)
            errors[nameof(s.From)] = ["from must not be after to."];
        else if (s.To.DayNumber - s.From.DayNumber > PlannerEndpoints.MaxRangeDays)
            errors[nameof(s.From)] = [$"The time span must not exceed {PlannerEndpoints.MaxRangeDays} days."];

        var hasFrom = !string.IsNullOrWhiteSpace(s.FromEventId);
        var hasTo = !string.IsNullOrWhiteSpace(s.ToEventId);
        if (hasFrom != hasTo)
            errors[nameof(s.FromEventId)] = ["fromEventId and toEventId must be given together."];
        else if (s.FromEventId?.Length > PlannerDbContext.IdLength || s.ToEventId?.Length > PlannerDbContext.IdLength)
            errors[nameof(s.FromEventId)] = [$"Event ids must be at most {PlannerDbContext.IdLength} characters long."];

        if (s.MinAttendancePercent is < 0 or > 100)
            errors[nameof(s.MinAttendancePercent)] = ["minAttendancePercent must be between 0 and 100."];
        if (s.MinInvitedEvents is < 1 or > 1000)
            errors[nameof(s.MinInvitedEvents)] = ["minInvitedEvents must be between 1 and 1000."];
        if (s.MinAverageStars is { } minStars && !SettingsRules.IsStars(minStars))
            errors[nameof(s.MinAverageStars)] = ["minAverageStars must be between 0 and 5."];

        if (s.EventNameContains?.Length > PlannerDbContext.TermsLength || s.EventNameExcludes?.Length > PlannerDbContext.TermsLength)
            errors[nameof(s.EventNameContains)] = [$"The event name terms must be at most {PlannerDbContext.TermsLength} characters long."];

        CheckSubGroups(errors, nameof(s.SubGroupIds), s.SubGroupIds);

        return errors.Count == 0 ? null : errors;
    }

    private static void CheckSubGroups(Dictionary<string, string[]> errors, string key, IReadOnlyList<string>? subGroups)
    {
        if (subGroups is not null && (subGroups.Count > MaxSubGroups || subGroups.Any(id => id is null || id.Length > PlannerDbContext.IdLength)))
            errors[key] = [$"At most {MaxSubGroups} subgroups, each id at most {PlannerDbContext.IdLength} characters long."];
    }

    private static void CheckId(Dictionary<string, string[]> errors, string key, string name, string? id)
    {
        if (string.IsNullOrWhiteSpace(id) || id.Length > PlannerDbContext.IdLength)
            errors[key] = [$"{name} is required and at most {PlannerDbContext.IdLength} characters long."];
    }

    private static void CheckName(Dictionary<string, string[]> errors, string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
            errors["Name"] = ["name is required."];
        else if (name.Trim().Length > PlannerDbContext.NameLength)
            errors["Name"] = [$"name must be at most {PlannerDbContext.NameLength} characters long."];
    }

    private static Dictionary<string, string[]> Fail(Dictionary<string, string[]> errors, string key, string message)
    {
        errors[key] = [message];
        return errors;
    }
}
