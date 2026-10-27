using Microsoft.EntityFrameworkCore;
using Kyuplan.Api.Infrastructure;
using Kyuplan.Contracts;

namespace Kyuplan.Api.Persistence;

/// <summary>
/// The price list of the exams and who paid what for an exam. A member owes the general fee plus the stamp fee and the price of the belt
/// they receive; the belt price falls away for a member who got the belt elsewhere (see docs/exam-planning.md). Input is expected to be validated by the endpoints.
/// </summary>
public sealed class ExamPaymentService(PlannerDbContext db, ClubClock clock)
{
    // --- Prices ----------------------------------------------------------------------------------------------

    public async Task<ExamPricesDto> GetPricesAsync(CancellationToken cancellationToken)
    {
        var rows = await db.ExamPrices.AsNoTracking().ToListAsync(cancellationToken);
        return ToDto(rows);
    }

    /// <summary>Replaces the price list. A belt that is not named costs nothing extra afterwards.</summary>
    /// <exception cref="InvalidInputException">A belt does not exist.</exception>
    public async Task<ExamPricesDto> SetPricesAsync(ExamPricesDto prices, CancellationToken cancellationToken)
    {
        var wanted = prices.BeltPrices.Where(p => p.StampFee > 0 || p.BeltPrice > 0).ToDictionary(p => p.BeltId);

        var beltIds = await db.Belts.AsNoTracking().Select(b => b.Id).ToListAsync(cancellationToken);
        if (wanted.Keys.Except(beltIds).Cast<int?>().FirstOrDefault() is { } unknown)
            throw new InvalidInputException($"There is no belt with id {unknown}.");

        var rows = await db.ExamPrices.ToListAsync(cancellationToken);

        var general = rows.FirstOrDefault(r => r.BeltId is null);
        if (general is null) db.ExamPrices.Add(new ExamPrice { Amount = prices.GeneralFee });
        else general.Amount = prices.GeneralFee;

        foreach (var row in rows.Where(r => r.BeltId is { } id && !wanted.ContainsKey(id)))
            db.ExamPrices.Remove(row);
        foreach (var (beltId, price) in wanted)
        {
            var row = rows.FirstOrDefault(r => r.BeltId == beltId);
            if (row is null) db.ExamPrices.Add(new ExamPrice { BeltId = beltId, Amount = price.StampFee, BeltPrice = price.BeltPrice });
            else (row.Amount, row.BeltPrice) = (price.StampFee, price.BeltPrice);
        }

        await db.SaveChangesAsync(cancellationToken);
        return await GetPricesAsync(cancellationToken);
    }

    private static ExamPricesDto ToDto(List<ExamPrice> rows) => new(
        rows.FirstOrDefault(r => r.BeltId is null)?.Amount ?? 0,
        [.. rows.Where(r => r.BeltId is not null).OrderBy(r => r.BeltId).Select(r => new BeltPriceDto(r.BeltId!.Value, r.Amount, r.BeltPrice))]);

    // --- Payments --------------------------------------------------------------------------------------------

    /// <exception cref="ExamNotFoundException"/>
    public async Task<ExamPaymentsDto> GetPaymentsAsync(int examId, CancellationToken cancellationToken)
    {
        var rows = await ListAsync(await FindExamAsync(examId, cancellationToken), cancellationToken);
        return new ExamPaymentsDto(examId, rows, rows.Sum(r => r.Due), rows.Sum(r => r.Paid));
    }

    /// <summary>
    /// Records what a member paid for the exam in total, and keeps the amount due as of now with it. A total of 0, no note and the belt
    /// not marked as the member's own take the record back.
    /// </summary>
    /// <exception cref="ExamNotFoundException"/>
    /// <exception cref="InvalidInputException">The member does not owe anything for this exam: they did not accept it, are not saved for it and received no belt at it.</exception>
    public async Task<ExamPaymentDto> SetPaymentAsync(int examId, int memberId, ExamPaymentInputDto input, CancellationToken cancellationToken)
    {
        var exam = await FindExamAsync(examId, cancellationToken);
        var row = (await ListAsync(exam, cancellationToken)).FirstOrDefault(r => r.MemberId == memberId)
                  ?? throw new InvalidInputException($"The member {memberId} does not pay for this exam: they did not accept it, are not saved for it and received no belt at it.");

        var note = string.IsNullOrWhiteSpace(input.Note) ? null : input.Note.Trim();
        var stored = await db.ExamPayments.FirstOrDefaultAsync(p => p.ExamId == examId && p.MemberId == memberId, cancellationToken);

        if (input.Paid == 0 && note is null && !input.OwnBelt)
        {
            if (stored is not null) db.ExamPayments.Remove(stored);
        }
        else
        {
            var live = await LiveDueAsync(exam, new HashSet<int> { memberId }, cancellationToken);
            if (stored is null)
            {
                stored = new ExamPayment { ExamId = examId, MemberId = memberId };
                db.ExamPayments.Add(stored);
            }

            var due = live[memberId];
            stored.OwnBelt = input.OwnBelt;
            stored.StampFee = due.StampFee;
            stored.BeltPrice = input.OwnBelt ? 0 : due.BeltPrice;
            stored.AmountDue = due.GeneralFee + stored.StampFee + stored.BeltPrice;
            stored.AmountPaid = input.Paid;
            stored.PaidOn = input.Paid > 0 ? input.PaidOn ?? clock.Today : null;
            stored.Note = note;
        }

        await db.SaveChangesAsync(cancellationToken);
        return (await ListAsync(exam, cancellationToken)).First(r => r.MemberId == memberId);
    }

    /// <summary>Everybody who owes for the exam, by name: those who accepted it, those saved for it who did not decline, those who received a belt at it, and those with a record.</summary>
    private async Task<List<ExamPaymentDto>> ListAsync(Exam exam, CancellationToken cancellationToken)
    {
        var participants = await db.ExamParticipants.AsNoTracking().Where(p => p.ExamId == exam.Id)
            .ToDictionaryAsync(p => p.MemberId, p => p.Rsvp, cancellationToken);
        var saved = await db.ExamCandidates.AsNoTracking().Where(c => c.ExamId == exam.Id).Select(c => c.MemberId).ToListAsync(cancellationToken);
        var received = await db.ExamResults.AsNoTracking().Where(r => r.ExamId == exam.Id).Select(r => r.MemberId).ToListAsync(cancellationToken);
        var records = await db.ExamPayments.AsNoTracking().Where(p => p.ExamId == exam.Id).ToDictionaryAsync(p => p.MemberId, cancellationToken);

        var ids = participants.Where(p => p.Value == Rsvp.Accepted).Select(p => p.Key)
            .Concat(saved.Where(id => !participants.TryGetValue(id, out var rsvp) || rsvp != Rsvp.Declined))
            .Concat(received).Concat(records.Keys)
            .ToHashSet();

        var live = await LiveDueAsync(exam, ids, cancellationToken);
        var names = await db.Members.AsNoTracking().Where(m => ids.Contains(m.Id)).ToDictionaryAsync(m => m.Id, m => m.Name, cancellationToken);

        return ids.Where(names.ContainsKey)
            .Select(id =>
            {
                records.TryGetValue(id, out var record);
                var paid = record?.AmountPaid ?? 0;

                // A record keeps the amounts of the day it was saved; the general fee is what is left of its total.
                var (general, stamp, belt) = record is null
                    ? (live[id].GeneralFee, live[id].StampFee, live[id].BeltPrice)
                    : (record.AmountDue - record.StampFee - record.BeltPrice, record.StampFee, record.BeltPrice);
                return new ExamPaymentDto(id, names[id], live[id].BeltId, general, stamp, belt, record?.OwnBelt ?? false, paid, record?.PaidOn, record?.Note,
                    PaymentRules.StatusOf(general + stamp + belt, paid));
            })
            .OrderBy(r => r.Name, StringComparer.CurrentCultureIgnoreCase).ThenBy(r => r.MemberId)
            .ToList();
    }

    /// <summary>
    /// What each member would owe at today's prices, and for which belt: the belt recorded at the exam, else the first belt (in the order of the
    /// progression) that asks for the belt the member holds. Without either only the general fee is due. The belt price is the full one here:
    /// a member's own belt is applied where it is known (<see cref="SetPaymentAsync"/>, <see cref="ListAsync"/>).
    /// </summary>
    private async Task<Dictionary<int, (int? BeltId, decimal GeneralFee, decimal StampFee, decimal BeltPrice)>> LiveDueAsync(Exam exam, IReadOnlySet<int> memberIds, CancellationToken cancellationToken)
    {
        var prices = await db.ExamPrices.AsNoTracking().ToListAsync(cancellationToken);
        var general = prices.FirstOrDefault(p => p.BeltId is null)?.Amount ?? 0;
        var beltPrices = prices.Where(p => p.BeltId is not null).ToDictionary(p => p.BeltId!.Value);

        var received = await db.ExamResults.AsNoTracking().Where(r => r.ExamId == exam.Id).ToDictionaryAsync(r => r.MemberId, r => r.BeltId, cancellationToken);
        var held = await db.Members.AsNoTracking().Where(m => memberIds.Contains(m.Id)).ToDictionaryAsync(m => m.Id, m => m.BeltId, cancellationToken);
        var belts = BeltService.InProgressionOrder(await db.Belts.AsNoTracking().Include(b => b.Prerequisites).ToListAsync(cancellationToken));

        return memberIds.ToDictionary(id => id, id =>
        {
            int? belt = received.TryGetValue(id, out var result) ? result
                : held.GetValueOrDefault(id) is { } holds ? belts.FirstOrDefault(b => b.Prerequisites.Any(p => p.RequiredBeltId == holds))?.Id
                : null;

            var price = belt is { } beltId && beltPrices.TryGetValue(beltId, out var row) ? row : null;
            return (belt, general, price?.Amount ?? 0, price?.BeltPrice ?? 0);
        });
    }

    private async Task<Exam> FindExamAsync(int examId, CancellationToken cancellationToken) =>
        await db.Exams.AsNoTracking().FirstOrDefaultAsync(e => e.Id == examId, cancellationToken)
        ?? throw new ExamNotFoundException(examId);
}
