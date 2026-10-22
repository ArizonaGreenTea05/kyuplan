using System.Globalization;
using Kyuplan.Client.Localization;
using Kyuplan.Client.ViewModels;
using Kyuplan.Contracts;
using static Kyuplan.Tests.TestAccounts;

namespace Kyuplan.Tests;

/// <summary>The sync status page: what it says about the runs, in both languages, and who gets to see it.</summary>
public sealed class SyncStatusViewModelTests : IDisposable
{
    private static readonly DateTimeOffset Start = new(2026, 10, 13, 10, 0, 0, TimeSpan.Zero);

    private readonly FakePlannerApi _api = new();
    private readonly LanguageService _language = new();
    private readonly ShellViewModel _shell;
    private readonly SyncStatusViewModel _vm;

    public SyncStatusViewModelTests()
    {
        _language.Select("en");
        _api.Account = Dto(1, "admin", "admin", [], true);
        _shell = new ShellViewModel(_api, _language);
        _vm = new SyncStatusViewModel(_shell, _api);
    }

    public void Dispose()
    {
        CultureInfo.DefaultThreadCurrentCulture = null;
        CultureInfo.DefaultThreadCurrentUICulture = null;
    }

    private static SyncRunDto Ok(double seconds = 1.5) => new(Start, seconds, true, null);

    private static SyncRunDto Failed(string error = "Spond is unreachable") => new(Start, 0.2, false, error);

    private async Task<SyncStatusViewModel> ShownAsync(SyncStatusDto status)
    {
        _api.SyncStatus = status;
        await _vm.ActivateAsync();
        return _vm;
    }

    [Fact]
    public async Task A_sync_that_works_says_so_and_lists_its_runs()
    {
        var vm = await ShownAsync(new(true, 15, false, 0, Ok(), null, [Ok()]));

        Assert.True(vm.HasLoaded);
        Assert.False(vm.IsFailing);
        Assert.Equal("The sync works.", vm.Headline);
        Assert.Equal("Runs every 15 min.", vm.IntervalText);
        Assert.Contains("took 1.5 s", vm.LastSuccessText);
        Assert.Equal("", vm.LastFailureText);
        Assert.True(vm.HasRuns);
        Assert.Contains("succeeded", vm.Runs[0].Text);
        Assert.True(vm.Runs[0].Succeeded);
    }

    [Fact]
    public async Task Failing_runs_are_called_out_with_the_error()
    {
        var vm = await ShownAsync(new(true, 15, false, 3, Ok(), Failed(), [Failed(), Failed(), Failed(), Ok()]));

        Assert.True(vm.IsFailing);
        Assert.Equal("The last 3 sync(s) in a row failed.", vm.Headline);
        Assert.Contains("Spond is unreachable", vm.LastFailureText);
        Assert.Contains("failed: Spond is unreachable", vm.Runs[0].Text);
        Assert.False(vm.Runs[0].Succeeded);
    }

    [Fact]
    public async Task Before_any_run_the_page_says_nothing_has_finished()
    {
        var vm = await ShownAsync(new(true, 15, false, 0, null, null, []));

        Assert.Equal("No sync has finished yet.", vm.Headline);
        Assert.Equal("No sync has worked since the API started.", vm.LastSuccessText);
        Assert.False(vm.HasRuns);
    }

    [Fact]
    public async Task A_running_sync_is_named()
    {
        var vm = await ShownAsync(new(true, 15, true, 0, Ok(), null, [Ok()]));

        Assert.Equal("A sync is running now.", vm.Headline);
    }

    [Fact]
    public async Task Failures_win_over_a_running_sync_in_the_headline()
    {
        var vm = await ShownAsync(new(true, 15, true, 1, null, Failed(), [Failed()]));

        Assert.Equal("The last 1 sync(s) in a row failed.", vm.Headline);
    }

    [Theory]
    [InlineData(true, 0, "Runs only once, when the API starts.")]
    [InlineData(true, 60, "Runs every 60 min.")]
    [InlineData(false, 15, "The automatic sync is switched off.")]
    public async Task The_interval_text_follows_the_settings(bool enabled, int minutes, string expected)
    {
        var vm = await ShownAsync(new(enabled, minutes, false, 0, null, null, []));

        Assert.Equal(expected, vm.IntervalText);
    }

    [Fact]
    public async Task Texts_follow_the_language_and_numbers_its_culture()
    {
        var vm = await ShownAsync(new(true, 15, false, 0, Ok(1.5), null, [Ok(1.5)]));

        _language.Select("de");

        Assert.Equal("Der Abgleich funktioniert.", vm.Headline);
        Assert.Equal("Läuft alle 15 Min.", vm.IntervalText);
        Assert.Contains("1,5 s", vm.LastSuccessText);
        Assert.Contains("erfolgreich", vm.Runs[0].Text);
    }

    [Fact]
    public async Task Somebody_without_the_permission_gets_nothing_and_the_api_is_not_asked()
    {
        _api.Account = Dto(2, "Anna", "Anna", [], false);
        _api.SyncStatus = new(true, 15, false, 0, Ok(), null, [Ok()]);

        await _vm.ActivateAsync();

        Assert.False(_vm.CanView);
        Assert.False(_vm.HasLoaded);
        Assert.False(_vm.HasError);
        Assert.Equal("", _vm.Headline);
    }

    [Fact]
    public async Task Retrying_reads_the_status_again()
    {
        var vm = await ShownAsync(new(true, 15, false, 0, null, null, []));
        _api.SyncStatus = new(true, 15, false, 0, Ok(), null, [Ok()]);

        await vm.RetryCommand.ExecuteAsync(null);

        Assert.True(vm.HasRuns);
    }

    [Fact]
    public async Task A_refusal_of_the_api_is_shown_as_an_error()
    {
        var vm = await ShownAsync(new(true, 15, false, 0, null, null, []));
        _api.Account = Dto(2, "Anna", "Anna", [], false); // the permission is taken away behind the page's back (the shell does not know yet)

        await vm.RetryCommand.ExecuteAsync(null);

        Assert.True(vm.HasError);
    }
}
