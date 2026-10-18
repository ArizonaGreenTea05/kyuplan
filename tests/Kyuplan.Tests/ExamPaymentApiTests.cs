using System.Net;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;
using Kyuplan.Client.Api;
using Kyuplan.Contracts;

namespace Kyuplan.Tests;

/// <summary>
/// The price list and the payments of an exam (<c>/api/exam-prices</c>, <c>/api/exams/{id}/payments</c>) against the real API, the demo club and
/// a real (in-memory) SQLite database, through the client so that the wire format is checked, too.
/// </summary>
public sealed class ExamPaymentApiTests(WebApplicationFactory<Program> factory) : IClassFixture<WebApplicationFactory<Program>>, IDisposable
{
    private const string Group = "demo-dojo";

    private static readonly DateTimeOffset PinnedNow = new(2026, 10, 13, 10, 0, 0, TimeSpan.Zero);
    private static readonly DateOnly September = new(2026, 9, 26);
    private static readonly DateOnly NextMarch = new(2027, 3, 27);

    private readonly InMemoryDatabase _database = new();
    private readonly InMemoryDatabase _accounts = new();

    public void Dispose()
    {
        _database.Dispose();
        _accounts.Dispose();
    }

    private sealed record Club(PlannerApiClient Api, ExamDto Past, ExamDto Next, BeltDto White, BeltDto Yellow, ExamMemberDto Member);

    /// <summary>The exam of September (past) and March 2027, White and Yellow (Yellow comes after White), and a member who accepted September's exam and holds White.</summary>
    private async Task<Club> StartAsync()
    {
        var http = factory.WithWebHostBuilder(builder =>
        {
            builder.UseSetting("Spond:UseDemoData", "true");
            builder.UseSetting("Sync:Enabled", "false");
            builder.UseSetting("DefaultData:Enabled", "false");
            builder.UseSetting("Database:ConnectionString", _database.ConnectionString);
            TestAccounts.Configure(builder, _accounts);
            builder.ConfigureTestServices(services => services.AddSingleton<TimeProvider>(new FakeTimeProvider(PinnedNow)));
        }).CreateSignedInClient();
        var api = new PlannerApiClient(http);

        var events = (await api.GetEventChoicesAsync(Group)).Events.Where(e => e.Name == "Gürtelprüfung").ToList();
        var past = await api.CreateExamAsync(new ExamInputDto(Group, events.Single(e => DateOnly.FromDateTime(e.Start.Date) == September).Id));
        var next = await api.CreateExamAsync(new ExamInputDto(Group, events.Single(e => DateOnly.FromDateTime(e.Start.Date) == NextMarch).Id));

        var white = await api.CreateBeltAsync(new BeltInputDto("White", "#FFFFFF", null, []));
        var yellow = await api.CreateBeltAsync(new BeltInputDto("Yellow", "#FFD800", null, [new BeltPrerequisiteDto(white.Id, 0, 0)]));

        var member = (await api.GetExamMembersAsync(past.Id)).First(m => m.Invitation == AttendanceMark.Attended);
        await api.UpdateMemberAsync(member.MemberId, new MemberInputDto(white.Id, null, null, new DateOnly(2026, 1, 1)));
        return new Club(api, past, next, white, yellow, member);
    }

    private static void AssertPrices(decimal general, (int BeltId, decimal Surcharge)[] surcharges, ExamPricesDto actual)
    {
        Assert.Equal(general, actual.GeneralFee);
        Assert.Equal(surcharges, actual.BeltPrices.Select(p => (p.BeltId, p.Surcharge)));
    }

    private static async Task<HttpStatusCode> StatusOfAsync(Func<Task> call) =>
        (await Assert.ThrowsAsync<PlannerApiException>(call)).StatusCode ?? throw new InvalidOperationException("no status");

    // --- Prices ---------------------------------------------------------------------------------------------

    [Fact]
    public async Task The_price_list_starts_empty_and_is_replaced_as_a_whole()
    {
        var club = await StartAsync();
        AssertPrices(0, [], await club.Api.GetExamPricesAsync());

        var saved = await club.Api.SetExamPricesAsync(new ExamPricesDto(20m, [new BeltPriceDto(club.Yellow.Id, 5.5m), new BeltPriceDto(club.White.Id, 0)]));

        Assert.Equal(20m, saved.GeneralFee);
        Assert.Equal([(club.Yellow.Id, 5.5m)], saved.BeltPrices.Select(p => (p.BeltId, p.Surcharge))); // 0 = no surcharge = no line

        var again = await club.Api.SetExamPricesAsync(new ExamPricesDto(18m, []));
        AssertPrices(18m, [], again);
        AssertPrices(18m, [], await club.Api.GetExamPricesAsync());
    }

    [Theory]
    [InlineData(-1, 0)]
    [InlineData(10.123, 0)]
    [InlineData(1000000, 0)]
    [InlineData(10, -1)]
    [InlineData(10, 2.005)]
    public async Task Prices_must_be_amounts_of_money(double general, double surcharge)
    {
        var club = await StartAsync();

        var status = await StatusOfAsync(() => club.Api.SetExamPricesAsync(new ExamPricesDto((decimal)general, [new BeltPriceDto(club.Yellow.Id, (decimal)surcharge)])));

        Assert.Equal(HttpStatusCode.BadRequest, status);
        AssertPrices(0, [], await club.Api.GetExamPricesAsync());
    }

    [Fact]
    public async Task A_belt_is_priced_at_most_once_and_must_exist()
    {
        var club = await StartAsync();

        var twice = await StatusOfAsync(() => club.Api.SetExamPricesAsync(new ExamPricesDto(1, [new BeltPriceDto(club.Yellow.Id, 1), new BeltPriceDto(club.Yellow.Id, 2)])));
        var unknown = await StatusOfAsync(() => club.Api.SetExamPricesAsync(new ExamPricesDto(1, [new BeltPriceDto(9999, 1)])));

        Assert.Equal((HttpStatusCode.BadRequest, HttpStatusCode.BadRequest), (twice, unknown));
    }

    [Fact]
    public async Task A_belt_that_is_deleted_takes_its_surcharge_with_it()
    {
        var club = await StartAsync();
        var orange = await club.Api.CreateBeltAsync(new BeltInputDto("Orange", "#FF8800", null, []));
        await club.Api.SetExamPricesAsync(new ExamPricesDto(20, [new BeltPriceDto(orange.Id, 3)]));

        await club.Api.DeleteBeltAsync(orange.Id);

        Assert.Empty((await club.Api.GetExamPricesAsync()).BeltPrices);
    }

    // --- Payments -------------------------------------------------------------------------------------------

    [Fact]
    public async Task A_member_owes_the_general_fee_plus_the_surcharge_of_the_next_belt_until_a_belt_is_recorded()
    {
        var club = await StartAsync();
        await club.Api.SetExamPricesAsync(new ExamPricesDto(20m, [new BeltPriceDto(club.Yellow.Id, 5m)]));

        var row = (await club.Api.GetExamPaymentsAsync(club.Past.Id)).Members.Single(m => m.MemberId == club.Member.MemberId);

        Assert.Equal((club.Yellow.Id, 25m, 0m, PaymentStatus.Open), (row.BeltId ?? 0, row.Due, row.Paid, row.Status));

        // The belt the member really received is what counts once it is recorded.
        var orange = await club.Api.CreateBeltAsync(new BeltInputDto("Orange", "#FF8800", null, []));
        await club.Api.SetExamPricesAsync(new ExamPricesDto(20m, [new BeltPriceDto(club.Yellow.Id, 5m), new BeltPriceDto(orange.Id, 9m)]));
        await club.Api.SetExamResultAsync(club.Past.Id, club.Member.MemberId, new ExamResultInputDto(orange.Id));

        row = (await club.Api.GetExamPaymentsAsync(club.Past.Id)).Members.Single(m => m.MemberId == club.Member.MemberId);
        Assert.Equal((orange.Id, 29m), (row.BeltId ?? 0, row.Due));
    }

    [Fact]
    public async Task Without_a_belt_that_comes_next_only_the_general_fee_is_due()
    {
        var club = await StartAsync();
        await club.Api.SetExamPricesAsync(new ExamPricesDto(20m, [new BeltPriceDto(club.Yellow.Id, 5m)]));
        await club.Api.UpdateMemberAsync(club.Member.MemberId, new MemberInputDto(club.Yellow.Id, null, null, new DateOnly(2026, 1, 1))); // nothing asks for Yellow

        var row = (await club.Api.GetExamPaymentsAsync(club.Past.Id)).Members.Single(m => m.MemberId == club.Member.MemberId);

        Assert.Equal((null, 20m), (row.BeltId, row.Due));
    }

    [Fact]
    public async Task What_a_member_paid_is_tracked_as_open_partly_paid_and_paid()
    {
        var club = await StartAsync();
        await club.Api.SetExamPricesAsync(new ExamPricesDto(20m, [new BeltPriceDto(club.Yellow.Id, 5m)]));
        var id = club.Member.MemberId;

        var partly = await club.Api.SetExamPaymentAsync(club.Past.Id, id, new ExamPaymentInputDto(10m, null, " cash "));
        Assert.Equal((10m, PaymentStatus.Partial, new DateOnly(2026, 10, 13), "cash"), (partly.Paid, partly.Status, partly.PaidOn ?? default, partly.Note));

        var paid = await club.Api.SetExamPaymentAsync(club.Past.Id, id, new ExamPaymentInputDto(25m, new DateOnly(2026, 10, 1), null));
        Assert.Equal((25m, PaymentStatus.Paid, new DateOnly(2026, 10, 1), null), (paid.Paid, paid.Status, paid.PaidOn, paid.Note));

        var all = await club.Api.GetExamPaymentsAsync(club.Past.Id);
        Assert.Equal((all.Members.Sum(m => m.Due), 25m), (all.TotalDue, all.TotalPaid));

        var overpaid = await club.Api.SetExamPaymentAsync(club.Past.Id, id, new ExamPaymentInputDto(30m, null, null));
        Assert.Equal(PaymentStatus.Paid, overpaid.Status);

        var taken = await club.Api.SetExamPaymentAsync(club.Past.Id, id, new ExamPaymentInputDto(0m, null, null));
        Assert.Equal((0m, PaymentStatus.Open, null), (taken.Paid, taken.Status, taken.PaidOn));
    }

    [Fact]
    public async Task A_recorded_payment_keeps_its_amount_due_when_the_prices_change_later()
    {
        var club = await StartAsync();
        await club.Api.SetExamPricesAsync(new ExamPricesDto(20m, [new BeltPriceDto(club.Yellow.Id, 5m)]));
        var other = (await club.Api.GetExamPaymentsAsync(club.Past.Id)).Members.First(m => m.MemberId != club.Member.MemberId);
        await club.Api.SetExamPaymentAsync(club.Past.Id, club.Member.MemberId, new ExamPaymentInputDto(25m, null, null));

        await club.Api.SetExamPricesAsync(new ExamPricesDto(30m, [new BeltPriceDto(club.Yellow.Id, 5m)]));

        var all = (await club.Api.GetExamPaymentsAsync(club.Past.Id)).Members;
        Assert.Equal((25m, PaymentStatus.Paid), (all.Single(m => m.MemberId == club.Member.MemberId).Due, all.Single(m => m.MemberId == club.Member.MemberId).Status));
        Assert.Equal(other.Due + 10m, all.Single(m => m.MemberId == other.MemberId).Due); // members without a record follow the new prices
    }

    [Fact]
    public async Task Those_who_accepted_are_saved_for_or_received_a_belt_pay_but_not_those_who_declined()
    {
        var club = await StartAsync();
        var members = await club.Api.GetExamMembersAsync(club.Past.Id);
        var declined = members.First(m => m.Invitation == AttendanceMark.Declined);
        var accepted = members.Where(m => m.Invitation == AttendanceMark.Attended).Select(m => m.MemberId).ToHashSet();
        await club.Api.SetExamCandidatesAsync(club.Past.Id, new ExamCandidatesInputDto([declined.MemberId], []));

        var payers = (await club.Api.GetExamPaymentsAsync(club.Past.Id)).Members.Select(m => m.MemberId).ToHashSet();

        Assert.Equal(accepted, payers); // a declined member who is saved for the exam still does not pay

        // Somebody who is saved for an exam and has not answered yet is expected to pay.
        var unanswered = (await club.Api.GetExamMembersAsync(club.Next.Id)).First(m => m.Invitation is null or AttendanceMark.Unanswered).MemberId;
        await club.Api.SetExamCandidatesAsync(club.Next.Id, new ExamCandidatesInputDto([unanswered], []));
        Assert.Contains(unanswered, (await club.Api.GetExamPaymentsAsync(club.Next.Id)).Members.Select(m => m.MemberId));
    }

    [Fact]
    public async Task A_payment_is_refused_for_somebody_who_does_not_pay_and_for_amounts_that_are_no_money()
    {
        var club = await StartAsync();
        var declined = (await club.Api.GetExamMembersAsync(club.Past.Id)).First(m => m.Invitation == AttendanceMark.Declined);

        var notPaying = await StatusOfAsync(() => club.Api.SetExamPaymentAsync(club.Past.Id, declined.MemberId, new ExamPaymentInputDto(5m, null, null)));
        var negative = await StatusOfAsync(() => club.Api.SetExamPaymentAsync(club.Past.Id, club.Member.MemberId, new ExamPaymentInputDto(-1m, null, null)));
        var cents = await StatusOfAsync(() => club.Api.SetExamPaymentAsync(club.Past.Id, club.Member.MemberId, new ExamPaymentInputDto(1.234m, null, null)));
        var longNote = await StatusOfAsync(() => club.Api.SetExamPaymentAsync(club.Past.Id, club.Member.MemberId, new ExamPaymentInputDto(1m, null, new string('x', PaymentRules.MaxNoteLength + 1))));
        var noExam = await StatusOfAsync(() => club.Api.GetExamPaymentsAsync(9999));

        Assert.Equal([HttpStatusCode.BadRequest, HttpStatusCode.BadRequest, HttpStatusCode.BadRequest, HttpStatusCode.BadRequest, HttpStatusCode.NotFound],
            [notPaying, negative, cents, longNote, noExam]);
    }

    [Fact]
    public async Task Payments_go_with_the_exam_and_survive_a_restart()
    {
        var club = await StartAsync();
        await club.Api.SetExamPaymentAsync(club.Past.Id, club.Member.MemberId, new ExamPaymentInputDto(7m, null, "note"));

        var again = new PlannerApiClient(factory.WithWebHostBuilder(builder =>
        {
            builder.UseSetting("Spond:UseDemoData", "true");
            builder.UseSetting("Sync:Enabled", "false");
            builder.UseSetting("DefaultData:Enabled", "false");
            builder.UseSetting("Database:ConnectionString", _database.ConnectionString);
            TestAccounts.Configure(builder, _accounts);
        }).CreateSignedInClient());

        Assert.Equal((7m, "note"), (
            (await again.GetExamPaymentsAsync(club.Past.Id)).Members.Single(m => m.MemberId == club.Member.MemberId).Paid,
            (await again.GetExamPaymentsAsync(club.Past.Id)).Members.Single(m => m.MemberId == club.Member.MemberId).Note));

        await again.DeleteExamAsync(club.Past.Id);
        Assert.Equal(HttpStatusCode.NotFound, await StatusOfAsync(() => again.GetExamPaymentsAsync(club.Past.Id)));
    }
}
