using System.Net;
using Microsoft.Extensions.Options;
using Kyuplan.Client.Api;
using Kyuplan.Client.ViewModels;
using Kyuplan.Contracts;

namespace Kyuplan.Tests;

public class BrandingViewModelTests
{
    private readonly FakePlannerApi _api = new();

    private static readonly BrandingDto Club = new(
        "PlanIt", "Karate Club Beispiel", "#1a5fb4",
        "api/branding/logo", "api/branding/logo-on-dark", "api/branding/icon");

    private static IOptions<PlannerClientOptions> At(string address) => Options.Create(new PlannerClientOptions { BaseAddress = address });

    [Fact]
    public void Until_the_api_answered_it_holds_the_product_name_and_no_logo()
    {
        var brand = new BrandingViewModel(_api);

        Assert.Equal("kyuplan", brand.ProductName);
        Assert.Equal("kyuplan", brand.WindowTitle);
        Assert.False(brand.HasLogo);
        Assert.Null(brand.IconUrl);
    }

    [Fact]
    public async Task What_the_api_says_is_taken_over_and_the_image_addresses_are_made_absolute()
    {
        _api.Branding = Club;
        var brand = new BrandingViewModel(_api, At("https://planner.example.org/"));
        var changed = new List<string?>();
        brand.PropertyChanged += (_, e) => changed.Add(e.PropertyName);

        await brand.LoadAsync();

        Assert.Equal("PlanIt", brand.ProductName);
        Assert.Equal("Karate Club Beispiel", brand.OrganizationName);
        Assert.Equal("#1a5fb4", brand.AccentColor);
        Assert.Equal("https://planner.example.org/api/branding/logo", brand.LogoUrl);
        Assert.Equal("https://planner.example.org/api/branding/logo-on-dark", brand.LogoOnDarkUrl);
        Assert.Equal("https://planner.example.org/api/branding/icon", brand.IconUrl);
        Assert.True(brand.HasLogo);
        Assert.Contains(nameof(BrandingViewModel.WindowTitle), changed); // a bound window title follows
    }

    [Theory]
    [InlineData("http://localhost:5100", "http://localhost:5100/api/branding/logo")] // no trailing slash, like the clients' own
    [InlineData("https://example.org/planner/", "https://example.org/planner/api/branding/logo")] // the API below a path
    public async Task The_address_of_the_api_may_have_a_path_or_no_trailing_slash(string baseAddress, string expected)
    {
        _api.Branding = Club;
        var brand = new BrandingViewModel(_api, At(baseAddress));

        await brand.LoadAsync();

        Assert.Equal(expected, brand.LogoUrl);
    }

    [Fact]
    public async Task The_window_title_names_the_organisation_only_when_there_is_one()
    {
        _api.Branding = Club;
        var brand = new BrandingViewModel(_api);
        await brand.LoadAsync();
        Assert.Equal("PlanIt · Karate Club Beispiel", brand.WindowTitle);
        Assert.Equal("Karate Club Beispiel", brand.LogoDescription);

        _api.Branding = Club with { OrganizationName = "" };
        var plain = new BrandingViewModel(_api);
        await plain.LoadAsync();
        Assert.Equal("PlanIt", plain.WindowTitle);
        Assert.Equal("PlanIt", plain.LogoDescription);
    }

    [Fact]
    public async Task An_instance_without_logos_has_none()
    {
        _api.Branding = Club with { LogoUrl = null, LogoOnDarkUrl = null, IconUrl = null };
        var brand = new BrandingViewModel(_api, At("http://localhost:5100/"));

        await brand.LoadAsync();

        Assert.False(brand.HasLogo);
        Assert.Null(brand.LogoUrl);
        Assert.Null(brand.IconUrl);
    }

    [Fact]
    public async Task An_api_that_cannot_be_reached_is_no_error_and_the_next_call_asks_again()
    {
        _api.BrandingFailWith = new PlannerApiException("down", null);
        var brand = new BrandingViewModel(_api);

        await brand.LoadAsync(); // does not throw

        Assert.Equal("kyuplan", brand.ProductName);

        _api.BrandingFailWith = null;
        _api.Branding = Club;
        await brand.LoadAsync();

        Assert.Equal("PlanIt", brand.ProductName);
        Assert.Equal(2, _api.BrandingRequests);
    }

    [Fact]
    public async Task It_asks_once_and_tells_listeners_once()
    {
        _api.Branding = Club;
        var brand = new BrandingViewModel(_api);
        var changed = 0;
        brand.Changed += (_, _) => changed++;

        await Task.WhenAll(brand.LoadAsync(), brand.LoadAsync()); // concurrent callers share one request
        await brand.LoadAsync();

        Assert.Equal(1, _api.BrandingRequests);
        Assert.Equal(1, changed);
    }

    // --- Following changes --------------------------------------------------------------------------------------

    private static async Task Eventually(Func<bool> condition)
    {
        for (var i = 0; i < 300 && !condition(); i++) await Task.Delay(10);
        Assert.True(condition(), "condition was not met in time");
    }

    [Fact]
    public async Task A_refresh_takes_over_what_changed_and_tells_listeners()
    {
        _api.Branding = Club;
        var brand = new BrandingViewModel(_api, At("https://planner.example.org/"));
        await brand.LoadAsync();
        var changed = 0;
        var properties = new List<string?>();
        brand.Changed += (_, _) => changed++;
        brand.PropertyChanged += (_, e) => properties.Add(e.PropertyName);

        _api.Branding = Club with { ProductName = "PlanIt 2", AccentColor = "#c01c28", LogoUrl = "api/branding/logo?v=2" };
        await brand.RefreshAsync();

        Assert.Equal("PlanIt 2 · Karate Club Beispiel", brand.WindowTitle);
        Assert.Equal("#c01c28", brand.AccentColor);
        Assert.Equal("https://planner.example.org/api/branding/logo?v=2", brand.LogoUrl); // another image behind the same address: a new address
        Assert.Equal("https://planner.example.org/api/branding/logo-on-dark", brand.LogoOnDarkUrl);
        Assert.Equal(1, changed);
        Assert.Contains(nameof(BrandingViewModel.WindowTitle), properties); // what is bound follows
        Assert.Contains(nameof(BrandingViewModel.AccentColor), properties);
        Assert.Contains(nameof(BrandingViewModel.LogoUrl), properties);
        Assert.DoesNotContain(nameof(BrandingViewModel.IconUrl), properties); // it did not change
    }

    [Fact]
    public async Task A_refresh_that_finds_nothing_new_tells_nobody()
    {
        _api.Branding = Club;
        var brand = new BrandingViewModel(_api);
        await brand.LoadAsync();
        var changed = 0;
        brand.Changed += (_, _) => changed++;

        await brand.RefreshAsync();
        await brand.RefreshAsync();

        Assert.Equal(3, _api.BrandingRequests); // it did ask
        Assert.Equal(0, changed);
    }

    [Fact]
    public async Task A_logo_that_was_taken_away_is_gone_after_a_refresh()
    {
        _api.Branding = Club;
        var brand = new BrandingViewModel(_api);
        await brand.LoadAsync();
        Assert.True(brand.HasLogo);

        _api.Branding = Club with { LogoUrl = null, LogoOnDarkUrl = null, IconUrl = null };
        await brand.RefreshAsync();

        Assert.False(brand.HasLogo);
        Assert.Null(brand.LogoOnDarkUrl);
        Assert.Null(brand.IconUrl);
    }

    [Fact]
    public async Task An_api_that_cannot_be_reached_leaves_the_branding_as_it_is()
    {
        _api.Branding = Club;
        var brand = new BrandingViewModel(_api);
        await brand.LoadAsync();
        var changed = 0;
        brand.Changed += (_, _) => changed++;

        _api.BrandingFailWith = new PlannerApiException("down", HttpStatusCode.BadGateway);
        await brand.RefreshAsync(); // does not throw

        Assert.Equal("PlanIt", brand.ProductName);
        Assert.Equal(0, changed);

        _api.BrandingFailWith = null;
        _api.Branding = Club with { ProductName = "Back again" };
        await brand.RefreshAsync();

        Assert.Equal("Back again", brand.ProductName); // and it carries on when the API is back
    }

    [Fact]
    public async Task Watching_picks_up_a_change_by_itself_and_stops_when_disposed()
    {
        _api.Branding = Club;
        var brand = new BrandingViewModel(_api);
        await brand.LoadAsync();
        var changed = 0;
        brand.Changed += (_, _) => changed++;

        brand.Watch(TimeSpan.FromMilliseconds(20));
        brand.Watch(TimeSpan.FromMilliseconds(20)); // a second call does not start a second loop
        _api.Branding = Club with { AccentColor = "#c01c28" };
        await Eventually(() => changed == 1);

        Assert.Equal("#c01c28", brand.AccentColor);

        brand.Dispose();
        await Task.Delay(60); // a tick that was already under way
        var requests = _api.BrandingRequests;
        _api.Branding = Club with { AccentColor = "#26a269" };
        await Task.Delay(100);

        Assert.Equal(requests, _api.BrandingRequests);
        Assert.Equal("#c01c28", brand.AccentColor);
    }

    [Fact]
    public async Task Watching_goes_on_after_a_listener_failed_and_while_the_api_is_down()
    {
        _api.Branding = Club;
        var brand = new BrandingViewModel(_api);
        await brand.LoadAsync();
        var failing = true;
        brand.Changed += (_, _) =>
        {
            if (failing) throw new InvalidOperationException("a faulty listener");
        };
        brand.Watch(TimeSpan.FromMilliseconds(20));

        _api.BrandingFailWith = new PlannerApiException("down", null);
        await Task.Delay(60);
        _api.BrandingFailWith = null;
        _api.Branding = Club with { ProductName = "One" };
        await Eventually(() => brand.ProductName == "One");

        failing = false;
        _api.Branding = Club with { ProductName = "Two" };
        await Eventually(() => brand.ProductName == "Two"); // the loop survived the exception
        brand.Dispose();
    }

    [Fact]
    public async Task A_refresh_started_after_a_change_is_answered_from_after_that_change()
    {
        // The administrators' page asks right after it saved: a regular look that was already under way must not answer for it.
        _api.Branding = Club;
        var brand = new BrandingViewModel(_api);
        await brand.LoadAsync();

        var slow = new TaskCompletionSource<BrandingDto>();
        _api.BrandingResponder = () => slow.Task;
        var regular = brand.RefreshAsync(); // asked before the change; its answer is the old branding
        _api.BrandingResponder = null;
        _api.Branding = Club with { ProductName = "After the change" };
        var afterSave = brand.RefreshAsync();

        slow.SetResult(Club);
        await Task.WhenAll(regular, afterSave);

        Assert.Equal("After the change", brand.ProductName); // the later answer wins, whichever came back first
    }

    [Fact]
    public async Task The_shell_loads_the_branding_with_its_data()
    {
        _api.Branding = Club;
        var shell = new ShellViewModel(_api);

        await shell.InitializeAsync();

        Assert.Equal("PlanIt", shell.Brand.ProductName);
    }

    [Fact]
    public async Task A_failing_branding_does_not_stop_the_shell()
    {
        _api.BrandingFailWith = new PlannerApiException("down", HttpStatusCode.BadGateway);
        var shell = new ShellViewModel(_api);

        await shell.InitializeAsync();

        Assert.False(shell.HasError);
        Assert.NotEmpty(shell.Groups);
    }
}
