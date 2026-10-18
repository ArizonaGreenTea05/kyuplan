using Microsoft.EntityFrameworkCore;
using Kyuplan.Api.Domain;
using Kyuplan.Api.Infrastructure;
using Kyuplan.Contracts;

namespace Kyuplan.Api.Persistence;

/// <summary>
/// The price list of the exams and who paid what for an exam. A member owes the general fee plus the surcharge of the belt they receive
/// (see docs/exam-planning.md). Input is expected to be validated by the endpoints.
/// </summary>
public sealed class ExamPaymentService(PlannerDbContext db, ClubClock clock)
{
    // --- Prices ----------------------------------------------------------------------------------------------

    public async Task<ExamPricesDto> GetPricesAsync(CancellationToken cancellationToken)
    {
        var rows = await db.ExamPrices.AsNoTracking().ToListAsync(cancellationToken);
        return ToDto(rows);
    }

    /// <summary>Replaces the price list. A belt that is not named has no surcharge afterwards.</summary>
    /// <exception cref="InvalidInputException">A belt does not exist.</exception>
    public async Task<ExamPricesDto> SetPricesAsync(ExamPricesDto prices, CancellationToken cancellationToken)
    {
        var wanted = prices.BeltPrices.Where(p => p.Surcharge > 0).ToDictionary(p => p.BeltId, p => p.Surcharge);

        var beltIds = await db.Belts.AsNoTracking().Select(b => b.Id).ToListAsync(cancellationToken);
        if (wanted.Keys.Except(beltIds).Cast<int?>().FirstOrDefault() is { } unknown)
            throw new InvalidInputException($"There is no belt with id {unknown}.");

        var rows = await db.ExamPrices.ToListAsync(cancellationToken);

        var general = rows.FirstOrDefault(r => r.BeltId is null);
        if (general is null) db.ExamPrices.Add(new ExamPrice { Amount = prices.GeneralFee });
        else general.Amount = prices.GeneralFee;

        foreach (var row in rows.Where(r => r.BeltId is { } id && !wanted.ContainsKey(id)))
            db.ExamPrices.Remove(row);
        foreach (var (beltId, amount) in wanted)
        {
            var row = rows.FirstOrDefault(r => r.BeltId == beltId);
            if (row is null) db.ExamPrices.Add(new ExamPrice { BeltId = beltId, Amount = amount });
            else row.Amount = amount;
        }

        await db.SaveChangesAsync(cancellationToken);
        return await GetPricesAsync(cancellationToken);
    }

    private static ExamPricesDto ToDto(List<ExamPrice> rows) => new(
        rows.FirstOrDefault(r => r.BeltId is null)?.Amount ?? 0,
        [.. rows.Where(r => r.BeltId is not null).OrderBy(r => r.BeltId).Select(r => new BeltPriceDto(r.BeltId!.Value, r.Amount))]);

    // --- Payments --------------------------------------------------------------------------------------------

    /// <exception cref="ExamNotFoundException"/>
    public async Task<ExamPaymentsDto> GetPaymentsAsync(int examId, CancellationToken cancellationToken)
    {
        var rows = await ListAsync(await FindExamAsync(examId, cancellationToken), cancellationToken);
        return new ExamPaymentsDto(examId, rows, rows.Sum(r => r.Due), rows.Sum(r => r.Paid));
    }

    /// <summary>
    /// Records what a member paid for the exam in total, and keeps the amount due as of now with it. A total of 0 and no note take the
    /// record back.
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

        if (input.Paid == 0 && note is null)
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

            stored.AmountDue = live[memberId].Due;
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
                var due = record?.AmountDue ?? live[id].Due;
                var paid = record?.AmountPaid ?? 0;
                return new ExamPaymentDto(id, names[id], live[id].BeltId, due, paid, record?.PaidOn, record?.Note, PaymentRules.StatusOf(due, paid));
            })
            .OrderBy(r => r.Name, StringComparer.CurrentCultureIgnoreCase).ThenBy(r => r.MemberId)
            .ToList();
    }

    /// <summary>
    /// What each member would owe at today's prices, and for which belt: the belt recorded at the exam, else the first belt (in the order of the
    /// progression) that asks for the belt the member holds. Without either only the general fee is due.
    /// </summary>
    private async Task<Dictionary<int, (int? BeltId, decimal Due)>> LiveDueAsync(Exam exam, IReadOnlySet<int> memberIds, CancellationToken cancellationToken)
    {
        var prices = await db.ExamPrices.AsNoTracking().ToListAsync(cancellationToken);
        var general = prices.FirstOrDefault(p => p.BeltId is null)?.Amount ?? 0;
        var surcharges = prices.Where(p => p.BeltId is not null).ToDictionary(p => p.BeltId!.Value, p => p.Amount);

        var received = await db.ExamResults.AsNoTracking().Where(r => r.ExamId == exam.Id).ToDictionaryAsync(r => r.MemberId, r => r.BeltId, cancellationToken);
        var held = await db.Members.AsNoTracking().Where(m => memberIds.Contains(m.Id)).ToDictionaryAsync(m => m.Id, m => m.BeltId, cancellationToken);
        var belts = BeltService.InProgressionOrder(await db.Belts.AsNoTracking().Include(b => b.Prerequisites).ToListAsync(cancellationToken));

        return memberIds.ToDictionary(id => id, id =>
        {
            int? belt = received.TryGetValue(id, out var result) ? result
                : held.GetValueOrDefault(id) is { } holds ? belts.FirstOrDefault(b => b.Prerequisites.Any(p => p.RequiredBeltId == holds))?.Id
                : null;

            return (belt, general + (belt is { } id2 && surcharges.TryGetValue(id2, out var extra) ? extra : 0));
        });
    }

    private async Task<Exam> FindExamAsync(int examId, CancellationToken cancellationToken) =>
        await db.Exams.AsNoTracking().FirstOrDefaultAsync(e => e.Id == examId, cancellationToken)
        ?? throw new ExamNotFoundException(examId);
}
