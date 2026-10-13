using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Options;
using Kyuplan.Client.Api;
using Kyuplan.Client.ViewModels;
using Kyuplan.Contracts;
using static Kyuplan.Tests.TestAccounts;

namespace Kyuplan.Tests;

/// <summary>Which themes exist for which client, and how the one in effect is found.</summary>
public class ThemeRulesTests
{
    [Fact]
    public void Both_clients_have_the_general_themes_but_only_the_Windows_client_has_the_Windows_ones()
    {
        foreach (var theme in ThemeRules.General)
        {
            Assert.True(ThemeRules.IsValid(ThemeClients.Web, theme));
            Assert.True(ThemeRules.IsValid(ThemeClients.Windows, theme));
        }

        foreach (var theme in new[] { ThemeIds.Windows10, ThemeIds.Aero, ThemeIds.Native })
        {
            Assert.False(ThemeRules.IsValid(ThemeClients.Web, theme));
            Assert.True(ThemeRules.IsValid(ThemeClients.Windows, theme));
        }

        Assert.True(ThemeRules.IsValid(ThemeClients.Web, ThemeIds.Default));
        Assert.False(ThemeRules.IsValid(ThemeClients.Web, "neon"));
        Assert.False(ThemeRules.IsValid("toaster", ThemeIds.Default));
        Assert.False(ThemeRules.IsValid(ThemeClients.Web, null));
    }

    [Fact]
    public void Every_theme_id_fits_the_database_columns()
    {
        Assert.All(ThemeRules.For(ThemeClients.Windows), id => Assert.InRange(id.Length, 1, ThemeRules.MaxLength));
    }

    [Fact]
    public void The_users_own_theme_wins_then_the_default_of_the_instance_then_the_plain_default()
    {
        Assert.Equal(ThemeIds.Dracula, ThemeRules.Resolve(ThemeClients.Web, ThemeIds.Dracula, ThemeIds.LiquidGlass));
        Assert.Equal(ThemeIds.LiquidGlass, ThemeRules.Resolve(ThemeClients.Web, null, ThemeIds.LiquidGlass));
        Assert.Equal(ThemeIds.Default, ThemeRules.Resolve(ThemeClients.Web, null, null));
    }

    [Fact]
    public void A_theme_the_client_does_not_have_is_not_used_even_if_it_was_saved_once()
    {
        // e.g. the Windows client's Aero chosen in the Windows client, then asked for by the web client
        Assert.Equal(ThemeIds.Default, ThemeRules.Resolve(ThemeClients.Web, ThemeIds.Aero, ThemeIds.Native));
        Assert.Equal(ThemeIds.Aero, ThemeRules.Resolve(ThemeClients.Windows, ThemeIds.Aero, ThemeIds.Native));
    }

    [Theory]
    [InlineData(ThemeIds.Dracula, "dark")]
    [InlineData(ThemeIds.CatppuccinMocha, "dark")]
    [InlineData(ThemeIds.CatppuccinLatte, "light")]
    [InlineData(ThemeIds.LiquidGlass, null)]
    [InlineData(ThemeIds.Default, null)]
    public void Some_themes_have_one_colour_scheme_only(string theme, string? scheme) => Assert.Equal(scheme, ThemeRules.FixedScheme(theme));
}

/// <summary>The user's own theme (kept with the account) and the default of the instance (kept with the branding), against the real API.</summary>
public sealed class ThemeApiTests(WebApplicationFactory<Program> factory) : IClassFixture<WebApplicationFactory<Program>>, IDisposable
{
    private const string UserPassword = "a good password";

    private readonly InMemoryDatabase _planner = new();
    private readonly InMemoryDatabase _accounts = new();

    public void Dispose()
    {
        _planner.Dispose();
        _accounts.Dispose();
    }

    private WebApplicationFactory<Program> CreateHost(Action<IWebHostBuilder>? configure = null) =>
        factory.WithWebHostBuilder(builder =>
        {
            builder.UseSetting("Spond:UseDemoData", "true");
            builder.UseSetting("Sync:Enabled", "false");
            builder.UseSetting("DefaultData:Enabled", "false");
            builder.UseSetting("Database:ConnectionString", _planner.ConnectionString);
            Configure(builder, _accounts);
            configure?.Invoke(builder);
        });

    private static BrandingInputDto BrandingInput(string? web = null, string? windows = null) => new("PlanIt", "Karate Club", "#1a5fb4", web, windows);

    [Fact]
    public async Task A_user_chooses_a_theme_per_client_and_it_is_kept_with_the_account()
    {
        using var host = CreateHost();
        var admin = host.CreateSignedInClient();
        Assert.Equal(HttpStatusCode.Created, (await admin.PostAsJsonAsync("/api/users", Input("anna", UserPassword, false))).StatusCode);
        var anna = host.CreateSignedInClient("anna", UserPassword);

        Assert.Null((await anna.GetFromJsonAsync<UserDto>("/api/account"))!.WebTheme);

        var response = await anna.PutAsJsonAsync("/api/account/theme", new ThemePreferenceDto(ThemeClients.Web, ThemeIds.Dracula));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(ThemeIds.Dracula, (await response.Content.ReadFromJsonAsync<UserDto>())!.WebTheme);
        await anna.PutAsJsonAsync("/api/account/theme", new ThemePreferenceDto(ThemeClients.Windows, ThemeIds.Aero));

        // after an API restart, and not shared with the other users
        using var restarted = CreateHost();
        var again = (await restarted.CreateSignedInClient("anna", UserPassword).GetFromJsonAsync<UserDto>("/api/account"))!;
        Assert.Equal(ThemeIds.Dracula, again.WebTheme);
        Assert.Equal(ThemeIds.Aero, again.WindowsTheme);
        Assert.Null((await restarted.CreateSignedInClient().GetFromJsonAsync<UserDto>("/api/account"))!.WebTheme);

        // back to the default of the instance
        var cleared = await restarted.CreateSignedInClient("anna", UserPassword).PutAsJsonAsync("/api/account/theme", new ThemePreferenceDto(ThemeClients.Web, null));
        Assert.Null((await cleared.Content.ReadFromJsonAsync<UserDto>())!.WebTheme);
    }

    [Theory]
    [InlineData(ThemeClients.Web, "aero")] // a Windows theme in the web client
    [InlineData(ThemeClients.Web, "neon")]
    [InlineData("toaster", "dracula")]
    public async Task A_theme_the_client_does_not_have_is_refused(string client, string theme)
    {
        using var host = CreateHost();

        var response = await host.CreateSignedInClient().PutAsJsonAsync("/api/account/theme", new ThemePreferenceDto(client, theme));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Administrators_set_the_default_per_client_and_everybody_is_told_without_a_login()
    {
        using var host = CreateHost();
        var admin = host.CreateSignedInClient();
        Assert.Equal(ThemeIds.Default, (await host.CreateClient().GetFromJsonAsync<BrandingDto>("/api/branding"))!.DefaultWebTheme);

        var saved = await admin.PutAsJsonAsync("/api/branding", BrandingInput(ThemeIds.CatppuccinMocha, ThemeIds.Aero));
        Assert.Equal(HttpStatusCode.OK, saved.StatusCode);

        using var restarted = CreateHost();
        var branding = (await restarted.CreateClient().GetFromJsonAsync<BrandingDto>("/api/branding"))!;
        Assert.Equal(ThemeIds.CatppuccinMocha, branding.DefaultWebTheme);
        Assert.Equal(ThemeIds.Aero, branding.DefaultWindowsTheme);
        var settings = (await restarted.CreateSignedInClient().GetFromJsonAsync<BrandingSettingsDto>("/api/branding/settings"))!;
        Assert.Equal(ThemeIds.CatppuccinMocha, settings.DefaultWebTheme);

        // saving the texts without saying anything about the themes keeps them
        await restarted.CreateSignedInClient().PutAsJsonAsync("/api/branding", BrandingInput());
        Assert.Equal(ThemeIds.Aero, (await restarted.CreateClient().GetFromJsonAsync<BrandingDto>("/api/branding"))!.DefaultWindowsTheme);
    }

    [Theory]
    [InlineData("aero", null)] // the web client has no Aero
    [InlineData(null, "neon")]
    public async Task A_default_theme_the_client_does_not_have_is_refused(string? web, string? windows)
    {
        using var host = CreateHost();

        var response = await host.CreateSignedInClient().PutAsJsonAsync("/api/branding", BrandingInput(web, windows));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Only_those_who_may_edit_the_branding_change_the_default_themes()
    {
        using var host = CreateHost();
        Assert.Equal(HttpStatusCode.Created, (await host.CreateSignedInClient().PostAsJsonAsync("/api/users", Input("anna", UserPassword, false))).StatusCode);

        var response = await host.CreateSignedInClient("anna", UserPassword).PutAsJsonAsync("/api/branding", BrandingInput(ThemeIds.Dracula));

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task The_default_of_the_api_settings_applies_until_an_administrator_saves_one()
    {
        using var host = CreateHost(b =>
        {
            b.UseSetting("Branding:DefaultWebTheme", ThemeIds.Dracula);
            b.UseSetting("Branding:DefaultWindowsTheme", "aero");
        });

        var branding = (await host.CreateClient().GetFromJsonAsync<BrandingDto>("/api/branding"))!;

        Assert.Equal(ThemeIds.Dracula, branding.DefaultWebTheme);
        Assert.Equal(ThemeIds.Aero, branding.DefaultWindowsTheme);
    }

    [Fact]
    public async Task A_setting_that_is_no_theme_of_the_client_falls_back_to_the_plain_default()
    {
        using var host = CreateHost(b => b.UseSetting("Branding:DefaultWebTheme", "aero"));

        Assert.Equal(ThemeIds.Default, (await host.CreateClient().GetFromJsonAsync<BrandingDto>("/api/branding"))!.DefaultWebTheme);
    }
}

/// <summary>What the client shows: the user's own choice, else the default of the instance; and what picking one does.</summary>
public class ThemeViewModelTests
{
    private readonly FakePlannerApi _api = new();
    private readonly LoginSession _session = new();
    private readonly ShellViewModel _shell;

    public ThemeViewModelTests()
    {
        CultureInfo.CurrentCulture = CultureInfo.CurrentUICulture = new CultureInfo("en-GB");
        _shell = new ShellViewModel(_api, session: _session);
    }

    private ThemeViewModel Themes(string client = ThemeClients.Web) =>
        new(_shell, _api, Options.Create(new PlannerClientOptions { ThemeClient = client }));

    [Fact]
    public async Task Without_a_choice_the_default_of_the_instance_applies()
    {
        _api.Branding = _api.Branding with { DefaultWebTheme = ThemeIds.Dracula, DefaultWindowsTheme = ThemeIds.LiquidGlass };
        var web = Themes();
        var windows = Themes(ThemeClients.Windows);
        await _shell.Brand.RefreshAsync();

        Assert.Equal(ThemeIds.Dracula, web.Effective);
        Assert.Equal(ThemeIds.LiquidGlass, windows.Effective);
        Assert.Equal("", web.SelectedId);
        Assert.Equal("Server default (Dracula)", web.Choices[0].Name);
    }

    [Fact]
    public async Task The_first_answer_is_announced_even_when_it_is_the_plain_default()
    {
        var themes = Themes();
        var announced = new List<string>();
        themes.Changed += (_, _) => announced.Add(themes.Effective);

        await _shell.Brand.RefreshAsync();

        Assert.Equal([ThemeIds.Default], announced); // a page that painted the theme of the last visit has to be told to drop it
    }

    [Fact]
    public async Task Choosing_a_theme_saves_it_for_this_kind_of_client_and_announces_it()
    {
        _api.Account = Dto(1, "anna", "anna", []);
        await _shell.InitializeAsync();
        var themes = Themes();
        var announced = new List<string>();
        themes.Changed += (_, _) => announced.Add(themes.Effective);

        await themes.ChooseAsync(ThemeIds.CatppuccinLatte);

        Assert.Equal(new ThemePreferenceDto(ThemeClients.Web, ThemeIds.CatppuccinLatte), _api.ThemeChoices.Single());
        Assert.Equal(ThemeIds.CatppuccinLatte, themes.Effective);
        Assert.Equal(ThemeIds.CatppuccinLatte, themes.SelectedId);
        Assert.Equal(ThemeIds.CatppuccinLatte, _shell.CurrentUser!.WebTheme);
        Assert.Equal([ThemeIds.CatppuccinLatte], announced);
    }

    [Fact]
    public async Task Choosing_the_server_default_again_forgets_the_own_choice()
    {
        _api.Account = Dto(1, "anna", "anna", []) with { WebTheme = ThemeIds.Dracula };
        _api.Branding = _api.Branding with { DefaultWebTheme = ThemeIds.LiquidGlass };
        await _shell.InitializeAsync();
        await _shell.Brand.RefreshAsync();
        var themes = Themes();
        Assert.Equal(ThemeIds.Dracula, themes.Effective);

        await themes.ChooseAsync("");

        Assert.Null(_api.ThemeChoices.Single().Theme);
        Assert.Equal(ThemeIds.LiquidGlass, themes.Effective);
    }

    [Fact]
    public async Task The_web_client_does_not_offer_the_Windows_themes_and_a_choice_that_is_the_current_one_is_not_sent()
    {
        _api.Account = Dto(1, "anna", "anna", []);
        await _shell.InitializeAsync();
        var web = Themes();

        Assert.DoesNotContain(web.Choices, c => c.Id is ThemeIds.Aero or ThemeIds.Windows10 or ThemeIds.Native);
        Assert.Contains(Themes(ThemeClients.Windows).Choices, c => c.Id == ThemeIds.Aero);

        await web.ChooseAsync(ThemeIds.Aero); // not a theme of the web client
        await web.ChooseAsync(""); // the choice it already has
        Assert.Empty(_api.ThemeChoices);
    }

    [Fact]
    public async Task A_failed_save_is_reported_and_changes_nothing()
    {
        _api.Account = Dto(1, "anna", "anna", []);
        await _shell.InitializeAsync();
        var themes = Themes();
        _api.FailWith = new PlannerApiException("down", HttpStatusCode.ServiceUnavailable);

        await themes.ChooseAsync(ThemeIds.Dracula);

        Assert.Equal("down", themes.ErrorMessage);
        Assert.Equal("", themes.SelectedId);
    }
}
