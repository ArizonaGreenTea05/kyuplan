using System.Globalization;
using Microsoft.Extensions.Time.Testing;
using Kyuplan.Client.Api;
using Kyuplan.Client.Localization;
using Kyuplan.Client.ViewModels;
using Kyuplan.Contracts;

namespace Kyuplan.Tests;

/// <summary>The price list page, and the payments of the exam on the exams page. "Now" is 30 June 2026: the exam of 28 March is past.</summary>
[Collection(DisplayStateCollection.Name)]
public sealed class ExamPaymentsViewModelTests : IDisposable
{
    private readonly FakePlannerApi _api = new();
    private readonly LanguageService _language = new();
    private readonly ShellViewModel _shell;
    private readonly ExamPricesViewModel _prices;
    private readonly ExamsViewModel _exams;

    private readonly BeltDto _white;
    private readonly BeltDto _yellow;
    private readonly MemberDto _anna;
    private readonly MemberDto _ben;
    private readonly ExamDto _past;

    public ExamPaymentsViewModelTests()
    {
        _language.Select("en");
        _api.Account = TestAccounts.Dto(1, "admin", "admin", [], true);
        _white = _api.AddBelt("White");
        _yellow = _api.AddBelt("Yellow", "#FFD800", "#000000", requires: [new(_white.Id, 8, 3)]);
        _anna = _api.AddMember("Anna", beltId: _white.Id);
        _ben = _api.AddMember("Ben", beltId: _white.Id);
        _past = _api.AddExam("exam-1");

        _shell = new ShellViewModel(_api, _language);
        _prices = new ExamPricesViewModel(_shell, _api);
        _exams = new ExamsViewModel(_shell, _api, new FakeTimeProvider(new DateTimeOffset(2026, 6, 30, 12, 0, 0, TimeSpan.FromHours(2))));
    }

    public void Dispose()
    {
        CultureInfo.DefaultThreadCurrentCulture = null;
        CultureInfo.DefaultThreadCurrentUICulture = null;
    }

    /// <summary>A row that owes <paramref name="due"/> in total, of which <paramref name="stamp"/> is the stamp fee and <paramref name="beltPrice"/> the belt price.</summary>
    private static ExamPaymentDto Row(MemberDto member, int? belt, decimal due, decimal paid = 0, string? note = null, decimal stamp = 0, decimal beltPrice = 0, bool ownBelt = false) =>
        new(member.Id, member.Name, belt, due - stamp - beltPrice, stamp, beltPrice, ownBelt, paid, paid > 0 ? new DateOnly(2026, 4, 1) : null, note, PaymentRules.StatusOf(due, paid));

    private async Task<ExamDetailViewModel> PaymentsShownAsync(params ExamPaymentDto[] rows)
    {
        _api.ExamPayments[_past.Id] = [.. rows];
        await _exams.ActivateAsync();
        _exams.SelectedExam = _exams.Exams.Single(e => e.Id == _past.Id);
        await _exams.Detail.Loaded;
        return _exams.Detail;
    }

    // --- The price list ---------------------------------------------------------------------------------------

    [Fact]
    public async Task Export_asks_the_API_for_the_exam_on_show_and_hands_the_file_to_the_client()
    {
        var detail = await PaymentsShownAsync(Row(_anna, _yellow.Id, 30));
        var saved = new List<BackupFile>();
        detail.ExportReady += (_, file) => saved.Add(file);

        await detail.ExportPaymentsExcelCommand.ExecuteAsync(null);
        await detail.ExportResultsCsvCommand.ExecuteAsync(null);

        Assert.Equal(new[] { $"payments:{_past.Id}:xlsx", $"results:{_past.Id}:csv" }, _api.ExportCalls);
        Assert.Equal(2, saved.Count);
        Assert.Null(detail.ErrorMessage);
    }

    [Fact]
    public async Task The_price_page_shows_the_general_fee_and_a_line_for_every_belt()
    {
        _api.ExamPrices = new ExamPricesDto(20m, [new BeltPriceDto(_yellow.Id, 5.5m, 0)]);

        await _prices.ActivateAsync();

        Assert.Equal("Exam prices", _prices.Title);
        Assert.Equal(Display.MoneyInput(20m), _prices.General.Text);
        Assert.Equal(["White", "Yellow"], _prices.Rows.Select(r => r.Name));
        Assert.Equal([Display.MoneyInput(0), Display.MoneyInput(5.5m)], _prices.Rows.Select(r => r.StampFee.Text));
        Assert.Equal([Display.MoneyInput(0), Display.MoneyInput(0)], _prices.Rows.Select(r => r.BeltPrice.Text));
        Assert.False(_prices.HasChanges);
        Assert.False(_prices.CanSave);
        Assert.False(_prices.CanDiscard);
    }

    [Fact]
    public async Task Typing_a_price_enables_saving_and_only_the_belts_that_cost_extra_are_sent()
    {
        await _prices.ActivateAsync();

        _prices.General.Text = "18" + CultureInfo.CurrentCulture.NumberFormat.NumberDecimalSeparator + "50";
        _prices.Rows[1].StampFee.Text = "4";
        _prices.Rows[1].BeltPrice.Text = "7";
        Assert.True(_prices.CanSave);
        Assert.True(_prices.CanDiscard);

        await _prices.SaveCommand.ExecuteAsync(null);

        Assert.Equal(18.5m, _api.ExamPrices.GeneralFee);
        Assert.Equal([(_yellow.Id, 4m, 7m)], _api.ExamPrices.BeltPrices.Select(p => (p.BeltId, p.StampFee, p.BeltPrice)));
        Assert.Equal(["set-prices"], _api.PaymentCalls);
        Assert.False(_prices.HasChanges);
        Assert.False(_prices.CanSave);
        Assert.Equal("The prices are saved.", _prices.NoticeText);
    }

    [Theory]
    [InlineData("abc")]
    [InlineData("-1")]
    [InlineData("1.234")]
    [InlineData("9999999")]
    public async Task A_price_that_is_no_amount_is_pointed_out_and_cannot_be_saved(string text)
    {
        await _prices.ActivateAsync();

        _prices.Rows[0].BeltPrice.Text = text;

        Assert.True(_prices.HasInvalid);
        Assert.True(_prices.HasChanges);
        Assert.False(_prices.CanSave);
        Assert.True(_prices.CanDiscard);
        Assert.Empty(_api.PaymentCalls);
    }

    [Fact]
    public async Task Discarding_puts_the_prices_back_to_what_is_saved()
    {
        _api.ExamPrices = new ExamPricesDto(20m, []);
        await _prices.ActivateAsync();
        _prices.General.Text = "99";
        _prices.Rows[0].StampFee.Text = "x";

        _prices.DiscardCommand.Execute(null);

        Assert.Equal((Display.MoneyInput(20m), Display.MoneyInput(0)), (_prices.General.Text, _prices.Rows[0].StampFee.Text));
        Assert.False(_prices.HasChanges);
        Assert.False(_prices.HasInvalid);
    }

    [Fact]
    public async Task Somebody_who_may_only_look_cannot_change_the_prices()
    {
        _api.Account = TestAccounts.WithPermissions(1, "anna", Permission.PaymentsView);
        await _prices.ActivateAsync();

        Assert.True(_prices.CanView);
        Assert.False(_prices.CanEdit);

        _prices.General.Text = "5";
        Assert.False(_prices.CanSave);
        Assert.False(_prices.CanDiscard);
    }

    [Fact]
    public async Task Somebody_without_the_right_to_see_payments_has_no_price_page()
    {
        _api.Account = TestAccounts.WithPermissions(1, "anna", Permission.ExamsView);

        await _prices.ActivateAsync();

        Assert.False(_prices.CanView);
        Assert.Empty(_prices.Rows);
        Assert.Empty(_api.PaymentCalls);
    }

    // --- The payments of an exam ------------------------------------------------------------------------------

    [Fact]
    public async Task The_payments_of_the_exam_show_what_is_due_what_was_paid_and_the_totals()
    {
        var detail = await PaymentsShownAsync(Row(_anna, _yellow.Id, 25m, 10m, "cash"), Row(_ben, null, 20m));

        Assert.True(detail.CanViewPayments);
        Assert.Equal(["Anna", "Ben"], detail.PaymentRows.Select(r => r.Name));
        var anna = detail.PaymentRows[0];
        Assert.Equal((Display.Money(25m), "Partly paid", PaymentStatus.Partial, "cash"), (anna.DueText, anna.StatusText, anna.Status, anna.Note));
        Assert.Equal("Yellow", anna.Belt.Label);
        Assert.Equal("Not paid", detail.PaymentRows[1].StatusText);
        Assert.Equal($"Paid {Display.Money(10m)} of {Display.Money(45m)}", detail.PaymentTotalsText);
        Assert.Equal("0 paid · 1 partly paid · 1 not paid", detail.PaymentCountsText);
        Assert.False(detail.NobodyPays);
    }

    [Fact]
    public async Task An_exam_nobody_pays_for_says_so()
    {
        var detail = await PaymentsShownAsync();

        Assert.True(detail.NobodyPays);
        Assert.False(detail.HasPayments);
    }

    [Fact]
    public async Task A_payment_is_saved_with_the_typed_amount_and_note_and_the_row_follows_the_answer()
    {
        var detail = await PaymentsShownAsync(Row(_anna, _yellow.Id, 25m), Row(_ben, null, 20m));
        var row = detail.PaymentRows[0];
        Assert.False(row.CanSave); // nothing typed yet

        row.Paid.Text = Display.MoneyInput(25m);
        row.Note = " in cash ";
        Assert.True(row.CanSave);
        await row.SaveCommand.ExecuteAsync(null);

        Assert.Equal([$"set-payment:{_past.Id}:{_anna.Id}"], _api.PaymentCalls);
        Assert.Equal((25m, "in cash"), (_api.PaymentInputs.Single().Paid, _api.PaymentInputs.Single().Note));
        Assert.Equal(PaymentStatus.Paid, row.Status);
        Assert.Equal("Paid", row.StatusText);
        Assert.False(row.HasChange);
        Assert.False(row.CanSave);
        Assert.Equal($"Paid {Display.Money(25m)} of {Display.Money(45m)}", detail.PaymentTotalsText);
        Assert.Equal("1 paid · 0 partly paid · 1 not paid", detail.PaymentCountsText);
        Assert.Contains("Anna", detail.NoticeText);
    }

    [Fact]
    public async Task Marking_as_paid_pays_the_amount_due_and_is_only_offered_while_something_is_missing()
    {
        var detail = await PaymentsShownAsync(Row(_anna, _yellow.Id, 25m, 25m), Row(_ben, null, 20m, 5m));
        var paid = detail.PaymentRows[0];
        var partly = detail.PaymentRows[1];

        Assert.False(paid.MarkPaidCommand.CanExecute(null));
        Assert.True(partly.MarkPaidCommand.CanExecute(null));

        await partly.MarkPaidCommand.ExecuteAsync(null);

        Assert.Equal(20m, _api.PaymentInputs.Single().Paid);
        Assert.Equal(PaymentStatus.Paid, partly.Status);
        Assert.False(partly.MarkPaidCommand.CanExecute(null));
    }

    [Fact]
    public async Task An_amount_that_is_no_money_cannot_be_saved_and_is_not_sent()
    {
        var detail = await PaymentsShownAsync(Row(_anna, _yellow.Id, 25m));
        var row = detail.PaymentRows[0];

        row.Paid.Text = "ten";

        Assert.False(row.Paid.IsValid);
        Assert.False(row.CanSave);
        await row.SaveCommand.ExecuteAsync(null);
        Assert.Empty(_api.PaymentCalls);
    }

    [Fact]
    public async Task What_was_typed_survives_reading_the_payments_again()
    {
        var detail = await PaymentsShownAsync(Row(_anna, _yellow.Id, 25m), Row(_ben, null, 20m));
        detail.PaymentRows[1].Paid.Text = "7";
        detail.PaymentRows[1].Note = "half";

        await _exams.ActivateAsync(); // the page reads again when it is shown
        await detail.Loaded;

        var ben = detail.PaymentRows.Single(r => r.Name == "Ben");
        Assert.Equal(("7", "half", true), (ben.Paid.Text, ben.Note, ben.HasChange));
        Assert.False(detail.PaymentRows.Single(r => r.Name == "Anna").HasChange);
    }

    [Fact]
    public async Task Somebody_who_may_only_look_sees_the_payments_but_cannot_change_them()
    {
        _api.Account = TestAccounts.WithPermissions(1, "anna", Permission.ExamsView, Permission.PaymentsView);
        var detail = await PaymentsShownAsync(Row(_anna, _yellow.Id, 25m));

        var row = detail.PaymentRows.Single();
        Assert.True(detail.CanViewPayments);
        Assert.False(detail.CanEditPayments);
        row.Paid.Text = "25";
        Assert.False(row.CanSave);
        Assert.False(row.MarkPaidCommand.CanExecute(null));
    }

    [Fact]
    public async Task Somebody_without_the_right_to_see_payments_never_asks_for_them()
    {
        _api.Account = TestAccounts.WithPermissions(1, "anna", Permission.ExamsView);

        var detail = await PaymentsShownAsync(Row(_anna, _yellow.Id, 25m));

        Assert.False(detail.CanViewPayments);
        Assert.Empty(detail.PaymentRows);
    }

    [Fact]
    public async Task A_row_shows_the_three_parts_of_what_is_due_and_the_own_belt_is_saved_with_it()
    {
        var detail = await PaymentsShownAsync(Row(_anna, _yellow.Id, 33m, stamp: 5m, beltPrice: 8m));
        var row = detail.PaymentRows.Single();

        Assert.Equal((Display.Money(20m), Display.Money(5m), Display.Money(8m), Display.Money(33m)), (row.GeneralFeeText, row.StampFeeText, row.BeltPriceText, row.DueText));
        Assert.False(row.CanSave);
        Assert.True(row.CanMarkPaid);

        row.OwnBelt = true;
        Assert.True(row.HasChange);
        Assert.True(row.CanSave);
        Assert.False(row.CanMarkPaid); // what is due changes with the belt: save that first
        await row.SaveCommand.ExecuteAsync(null);

        Assert.True(_api.PaymentInputs.Single().OwnBelt);
        Assert.Equal((Display.Money(0m), Display.Money(25m), true), (row.BeltPriceText, row.DueText, row.OwnBelt));
        Assert.False(row.CanSave);
        Assert.True(row.CanMarkPaid);
    }
}
