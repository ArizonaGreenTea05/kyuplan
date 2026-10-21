using System.Text.RegularExpressions;
using Kyuplan.Api.Accounts;
using Kyuplan.Api.Export;
using Kyuplan.Api.Persistence;
using Kyuplan.Contracts;

namespace Kyuplan.Api.Endpoints;

/// <summary>Spreadsheet downloads for the treasurer: the results (<c>/api/exams/{id}/results/export</c>) and the payment list (<c>/api/exams/{id}/payments/export</c>) of an exam, as CSV or Excel.</summary>
public static partial class ExportEndpoints
{
    public static IEndpointRouteBuilder MapExportEndpoints(this IEndpointRouteBuilder app)
    {
        var exams = app.MapGroup("/api/exams/{examId:int}").WithTags("Export");

        exams.MapGet("/results/export", async (int examId, string? format, ExamService examService, ExamPlanningService planning, BeltService belts, CancellationToken ct) =>
            {
                if (!TableExport.TryParseFormat(format, out var kind)) return BadFormat();
                var exam = await examService.GetAsync(examId, ct);
                var beltNames = (await belts.ListAsync(ct)).ToDictionary(b => b.Id, b => b.Name);
                string? Belt(int? id) => id is { } b && beltNames.TryGetValue(b, out var name) ? name : null;

                var rows = (await planning.GetMembersAsync(examId, ct))
                    .Select(m => new object?[] { m.Name, Belt(m.BeltId), Answer(m.Invitation), m.IsPreSaved, Belt(m.ReceivedBeltId) })
                    .ToList();
                var table = new ExportTable("Results", ["Name", "Belt", "Invitation", "Saved for exam", "Belt received"], rows);
                return File(table, kind, "results", exam);
            })
            .WithSummary("The members of the exam with their answer and the belt they received, as a CSV (default, `format=csv`) or Excel (`format=xlsx`) file.")
            .RequirePermission(Permission.ExamsView);

        exams.MapGet("/payments/export", async (int examId, string? format, ExamService examService, ExamPaymentService payments, BeltService belts, CancellationToken ct) =>
            {
                if (!TableExport.TryParseFormat(format, out var kind)) return BadFormat();
                var exam = await examService.GetAsync(examId, ct);
                var beltNames = (await belts.ListAsync(ct)).ToDictionary(b => b.Id, b => b.Name);
                var list = await payments.GetPaymentsAsync(examId, ct);

                var rows = list.Members.Select(p => new object?[]
                {
                    p.Name, p.BeltId is { } b && beltNames.TryGetValue(b, out var name) ? name : null, p.GeneralFee, p.StampFee, p.BeltPrice,
                    p.OwnBelt, p.Due, p.Paid, p.PaidOn, p.Note, p.Status.ToString(),
                }).ToList();
                rows.Add(["Total", null, null, null, null, null, list.TotalDue, list.TotalPaid, null, null, null]);
                var table = new ExportTable("Payments",
                    ["Name", "Belt", "General fee", "Stamp fee", "Belt price", "Own belt", "Due", "Paid", "Paid on", "Note", "Status"], rows);
                return File(table, kind, "payments", exam);
            })
            .WithSummary("Who owes what for the exam and what was paid, with totals, as a CSV (default, `format=csv`) or Excel (`format=xlsx`) file.")
            .RequirePermission(Permission.PaymentsView);

        return app;
    }

    private static string? Answer(AttendanceMark? mark) => mark?.ToString();

    private static IResult BadFormat() => Results.ValidationProblem(new Dictionary<string, string[]> { ["format"] = ["format must be csv or xlsx."] });

    private static IResult File(ExportTable table, ExportFormat format, string what, ExamDto exam)
    {
        var name = $"{Slug(exam.Name)}-{exam.Date:yyyy-MM-dd}-{what}.{TableExport.Extension(format)}";
        return Results.File(TableExport.Write(table, format), TableExport.ContentType(format), name);
    }

    private static string Slug(string text)
    {
        var slug = NotFileName().Replace(text, "-").Trim('-');
        return slug.Length == 0 ? "exam" : slug;
    }

    [GeneratedRegex("[^A-Za-z0-9]+")]
    private static partial Regex NotFileName();
}
