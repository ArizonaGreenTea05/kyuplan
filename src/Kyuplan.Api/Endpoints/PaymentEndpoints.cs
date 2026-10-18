using Kyuplan.Api.Accounts;
using Kyuplan.Api.Persistence;
using Kyuplan.Contracts;

namespace Kyuplan.Api.Endpoints;

/// <summary>The price list of the exams (<c>/api/exam-prices</c>) and what members paid for an exam (<c>/api/exams/{id}/payments</c>).</summary>
public static class PaymentEndpoints
{
    private const int MaxBeltPrices = 1000;

    public static IEndpointRouteBuilder MapPaymentEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/exam-prices", (ExamPaymentService service, CancellationToken ct) => service.GetPricesAsync(ct))
            .WithTags("Exam prices and payments")
            .WithSummary("The price list: the general fee every member pays for an exam, and the surcharge of each belt that costs extra.")
            .RequirePermission(Permission.PaymentsView, Permission.PricesEdit);

        app.MapPut("/api/exam-prices", async (ExamPricesDto input, ExamPaymentService service, CancellationToken ct) =>
            {
                if (Validate(input) is { } errors) return Results.ValidationProblem(errors);

                return Results.Ok(await service.SetPricesAsync(input, ct));
            })
            .WithTags("Exam prices and payments")
            .WithSummary("Replace the price list. A belt that is not named has no surcharge. Payments that were recorded keep the amount due they were saved with.")
            .RequirePermission(Permission.PricesEdit);

        var payments = app.MapGroup("/api/exams/{examId:int}/payments").WithTags("Exam prices and payments");

        payments.MapGet("/", (int examId, ExamPaymentService service, CancellationToken ct) => service.GetPaymentsAsync(examId, ct))
            .WithSummary("Who pays for the exam, what each owes (general fee plus the surcharge of the belt they receive), what they paid and whether that settles it.")
            .RequirePermission(Permission.PaymentsView);

        payments.MapPut("/{memberId:int}", async (int examId, int memberId, ExamPaymentInputDto input, ExamPaymentService service, CancellationToken ct) =>
            {
                if (Validate(input) is { } errors) return Results.ValidationProblem(errors);

                return Results.Ok(await service.SetPaymentAsync(examId, memberId, input, ct));
            })
            .WithSummary("Record the total a member paid for the exam (0 and no note takes the record back). The amount due is worked out again from the current prices.")
            .RequirePermission(Permission.PaymentsEdit);

        return app;
    }

    internal static Dictionary<string, string[]>? Validate(ExamPricesDto? input)
    {
        var errors = new Dictionary<string, string[]>();
        if (input is null) return Fail(errors, nameof(input), "A request body is required.");

        if (!PaymentRules.IsAmount(input.GeneralFee))
            errors[nameof(input.GeneralFee)] = [AmountMessage("generalFee")];

        if (input.BeltPrices is null)
            errors[nameof(input.BeltPrices)] = ["beltPrices is required (it may be empty)."];
        else if (input.BeltPrices.Count > MaxBeltPrices || input.BeltPrices.Select(p => p.BeltId).Distinct().Count() != input.BeltPrices.Count)
            errors[nameof(input.BeltPrices)] = [$"At most {MaxBeltPrices} belts, each one once."];
        else if (input.BeltPrices.Any(p => !PaymentRules.IsAmount(p.Surcharge)))
            errors[nameof(input.BeltPrices)] = [AmountMessage("surcharge")];

        return errors.Count == 0 ? null : errors;
    }

    internal static Dictionary<string, string[]>? Validate(ExamPaymentInputDto? input)
    {
        var errors = new Dictionary<string, string[]>();
        if (input is null) return Fail(errors, nameof(input), "A request body is required.");

        if (!PaymentRules.IsAmount(input.Paid))
            errors[nameof(input.Paid)] = [AmountMessage("paid")];
        if (input.Note?.Trim().Length > PaymentRules.MaxNoteLength)
            errors[nameof(input.Note)] = [$"note must be at most {PaymentRules.MaxNoteLength} characters long."];

        return errors.Count == 0 ? null : errors;
    }

    private static string AmountMessage(string name) => $"{name} must be between 0 and {PaymentRules.MaxAmount:0} with at most two decimals.";

    private static Dictionary<string, string[]> Fail(Dictionary<string, string[]> errors, string key, string message)
    {
        errors[key] = [message];
        return errors;
    }
}
