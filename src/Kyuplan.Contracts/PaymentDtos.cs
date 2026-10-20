using System.Text.Json.Serialization;

namespace Kyuplan.Contracts;

/// <summary>One belt's share of the exam price: what a member pays on top of the general fee when this is the belt they will receive.</summary>
/// <param name="BeltId">The belt the member receives.</param>
/// <param name="StampFee">The fee for the exam stamp (Prüfungsmarke) of this belt, 0 or more.</param>
/// <param name="BeltPrice">The price of the belt itself, 0 or more. Not due from a member who got the belt elsewhere (<see cref="ExamPaymentDto.OwnBelt"/>).</param>
public sealed record BeltPriceDto(int BeltId, decimal StampFee, decimal BeltPrice);

/// <summary>
/// The price list of the exams: every member pays the general fee, plus the stamp fee and the price of the belt they receive. The same shape
/// is read and written; writing replaces the list, so a belt that is not named costs nothing extra.
/// </summary>
/// <param name="GeneralFee">What every member pays for an exam, 0 or more.</param>
/// <param name="BeltPrices">The belts that cost extra (a stamp fee, a belt price or both). Each belt at most once.</param>
public sealed record ExamPricesDto(decimal GeneralFee, IReadOnlyList<BeltPriceDto> BeltPrices);

/// <summary>How far a member has paid what they owe for an exam.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<PaymentStatus>))]
public enum PaymentStatus
{
    /// <summary>Nothing paid yet.</summary>
    Open,

    /// <summary>Something was paid, less than is due.</summary>
    Partial,

    /// <summary>The amount due is paid (or nothing is due).</summary>
    Paid,
}

/// <summary>What one member owes for one exam and what they paid so far.</summary>
/// <param name="BeltId">The belt the member receives: the one recorded at the exam, else the belt after the one they hold. <c>null</c> = not known (no belt, or none comes next); only the general fee is due then.</param>
/// <param name="GeneralFee">The general fee every member pays, at the prices of the day the payment was last saved (live prices until then).</param>
/// <param name="StampFee">The fee for the exam stamp of <paramref name="BeltId"/>; 0 when the belt is not known.</param>
/// <param name="BeltPrice">The price of the belt <paramref name="BeltId"/>; 0 when the belt is not known or <paramref name="OwnBelt"/> is set.</param>
/// <param name="OwnBelt">The member got the belt elsewhere (e.g. handed down by a sibling), so the belt itself is not charged.</param>
/// <param name="Paid">What the member paid so far; may exceed <paramref name="Due"/>.</param>
/// <param name="PaidOn">The day of the last payment; <c>null</c> = nothing paid.</param>
/// <param name="Note">Free text of the administrator, e.g. "paid in cash"; <c>null</c> = none.</param>
public sealed record ExamPaymentDto(
    int MemberId,
    string Name,
    int? BeltId,
    decimal GeneralFee,
    decimal StampFee,
    decimal BeltPrice,
    bool OwnBelt,
    decimal Paid,
    DateOnly? PaidOn,
    string? Note,
    PaymentStatus Status)
{
    /// <summary>What the member owes in total: general fee, stamp fee and belt price.</summary>
    [JsonIgnore]
    public decimal Due => GeneralFee + StampFee + BeltPrice;
}

/// <summary>Who has to pay for an exam: every member who accepted it, is saved for it (and did not decline) or received a belt at it, by name.</summary>
/// <param name="TotalDue">The sum of what the members owe.</param>
/// <param name="TotalPaid">The sum of what they paid.</param>
public sealed record ExamPaymentsDto(int ExamId, IReadOnlyList<ExamPaymentDto> Members, decimal TotalDue, decimal TotalPaid);

/// <summary>What a member paid for an exam, in total. Replaces what was recorded before; the amount due is worked out again from the current prices.</summary>
/// <param name="OwnBelt">The member got the belt elsewhere, so its price is not charged.</param>
/// <param name="Paid">The total paid, 0 or more. 0 takes the payment back.</param>
/// <param name="PaidOn">The day of the (last) payment; <c>null</c> = today. Ignored when nothing was paid.</param>
/// <param name="Note">Free text, at most <see cref="PaymentRules.MaxNoteLength"/> characters; <c>null</c> or empty = none.</param>
public sealed record ExamPaymentInputDto(decimal Paid, DateOnly? PaidOn, string? Note, bool OwnBelt = false);

/// <summary>Limits of prices and payments, shared by the API and the clients.</summary>
public static class PaymentRules
{
    public const decimal MaxAmount = 100_000m;
    public const int MaxNoteLength = 500;

    /// <summary>An amount of money: not negative, at most <see cref="MaxAmount"/>, whole cents.</summary>
    public static bool IsAmount(decimal amount) => amount is >= 0 and <= MaxAmount && decimal.Round(amount, 2) == amount;

    public static PaymentStatus StatusOf(decimal due, decimal paid) =>
        paid >= due ? PaymentStatus.Paid : paid > 0 ? PaymentStatus.Partial : PaymentStatus.Open;
}
