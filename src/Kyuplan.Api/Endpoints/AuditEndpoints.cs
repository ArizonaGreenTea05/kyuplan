using Kyuplan.Api.Accounts;
using Kyuplan.Api.Persistence;
using Kyuplan.Contracts;

namespace Kyuplan.Api.Endpoints;

/// <summary>The audit log (<c>/api/audit</c>): who changed exams, payments, belts, members, users and roles, and when.</summary>
public static class AuditEndpoints
{
    public static IEndpointRouteBuilder MapAuditEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/audit", async (string? area, int? userId, long? before, int? limit, AuditService service, CancellationToken ct) =>
            {
                if (area is not null && !AuditAreas.IsKnown(area))
                    return Results.ValidationProblem(new Dictionary<string, string[]> { [nameof(area)] = [$"area must be one of: {string.Join(", ", AuditAreas.All)}."] });

                return Results.Ok(await service.ListAsync(area, userId, before, limit, ct));
            })
            .WithTags("Audit log")
            .Produces<AuditPageDto>()
            .WithSummary("The changes users made, newest first, at most limit (default 50, at most 200) at a time. Narrow it down with area and/or userId; pass the nextBefore of a page as before for the next, older page. Nothing in the log can be changed or deleted.")
            .RequirePermission(Permission.AuditView);

        return app;
    }
}
