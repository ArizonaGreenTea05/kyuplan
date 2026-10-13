using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Kyuplan.Client.Api;
using Kyuplan.Client.Localization;
using Kyuplan.Client.ViewModels;
using Kyuplan.Contracts;
using static Kyuplan.Tests.TestAccounts;

namespace Kyuplan.Tests;

/// <summary>The language of a user (kept with the account), against the real API.</summary>
public sealed class LanguageApiTests(WebApplicationFactory<Program> factory) : IClassFixture<WebApplicationFactory<Program>>, IDisposable
{
    private const string UserPassword = "a good password";

    private readonly InMemoryDatabase _planner = new();
    private readonly InMemoryDatabase _accounts = new();

    public void Dispose()
    {
        _planner.Dispose();
        _accounts.Dispose();
    }

    private WebApplicationFactory<Program> CreateHost() =>
        factory.WithWebHostBuilder(builder =>
        {
            builder.UseSetting("Spond:UseDemoData", "true");
            builder.UseSetting("Sync:Enabled", "false");
            builder.UseSetting("DefaultData:Enabled", "false");
            builder.UseSetting("Database:ConnectionString", _planner.ConnectionString);
            Configure(builder, _accounts);
        });

    [Fact]
    public async Task A_user_chooses_a_language_and_it_is_kept_with_the_account()
    {
        using var host = CreateHost();
        var admin = host.CreateSignedInClient();
        Assert.Equal(HttpStatusCode.Created, (await admin.PostAsJsonAsync("/api/users", Input("anna", UserPassword, false))).StatusCode);
        var anna = host.CreateSignedInClient("anna", UserPassword);

        Assert.Null((await anna.GetFromJsonAsync<UserDto>("/api/account"))!.Language);

        var response = await anna.PutAsJsonAsync("/api/account/language", new LanguagePreferenceDto("de"));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("de", (await response.Content.ReadFromJsonAsync<UserDto>())!.Language);

        // after an API restart, and not shared with the other users
        using var restarted = CreateHost();
        Assert.Equal("de", (await restarted.CreateSignedInClient("anna", UserPassword).GetFromJsonAsync<UserDto>("/api/account"))!.Language);
        Assert.Null((await restarted.CreateSignedInClient().GetFromJsonAsync<UserDto>("/api/account"))!.Language);

        // forgotten again
        var cleared = await restarted.CreateSignedInClient("anna", UserPassword).PutAsJsonAsync("/api/account/language", new LanguagePreferenceDto(null));
        Assert.Null((await cleared.Content.ReadFromJsonAsync<UserDto>())!.Language);
    }

    [Theory]
    [InlineData("fr")]
    [InlineData("")]
    [InlineData("DE")]
    public async Task A_language_that_is_not_translated_is_refused(string language)
    {
        using var host = CreateHost();

        var response = await host.CreateSignedInClient().PutAsJsonAsync("/api/account/language", new LanguagePreferenceDto(language));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }
}

/// <summary>What the client does with the language of the user.</summary>
public sealed class LanguagePreferenceViewModelTests : IDisposable
{
    private readonly CultureInfo _culture = CultureInfo.CurrentCulture;
    private readonly CultureInfo _uiCulture = CultureInfo.CurrentUICulture;
    private readonly FakePlannerApi _api = new();
    private readonly LoginSession _session = new();
    private readonly ShellViewModel _shell;

    public LanguagePreferenceViewModelTests()
    {
        CultureInfo.CurrentCulture = CultureInfo.CurrentUICulture = new CultureInfo("en-GB");
        _shell = new ShellViewModel(_api, new LanguageService(), _session);
    }

    public void Dispose()
    {
        CultureInfo.CurrentCulture = _culture;
        CultureInfo.CurrentUICulture = _uiCulture;
        CultureInfo.DefaultThreadCurrentCulture = null;
        CultureInfo.DefaultThreadCurrentUICulture = null;
    }

    [Fact]
    public void The_languages_of_the_clients_are_the_ones_the_API_accepts()
    {
        Assert.Equal(LanguageRules.Codes, LanguageService.Supported.Select(l => l.Code));
    }

    [Fact]
    public void Signing_in_switches_to_the_language_of_the_user()
    {
        _session.User = Dto(1, "anna", "anna", []) with { Language = "de" };

        Assert.Equal("de", _shell.Language.Current.Code);
    }

    [Fact]
    public void Without_a_choice_the_client_keeps_its_language()
    {
        _shell.Language.Select("de");

        _session.User = Dto(1, "anna", "anna", []);

        Assert.Equal("de", _shell.Language.Current.Code);
    }

    [Fact]
    public async Task Choosing_a_language_saves_it_and_switches()
    {
        _api.Account = Dto(1, "anna", "anna", []);
        await _shell.InitializeAsync();
        var preferences = new PreferencesViewModel(_shell, _api, new ThemeViewModel(_shell, _api));

        await preferences.ChooseLanguageAsync(LanguageService.Resolve("de"));

        Assert.Equal(new LanguagePreferenceDto("de"), _api.LanguageChoices.Single());
        Assert.Equal("de", _shell.CurrentUser!.Language);
        Assert.Equal("de", _shell.Language.Current.Code);
        Assert.Equal("de", preferences.SelectedLanguage.Code);

        await preferences.ChooseLanguageAsync(LanguageService.Resolve("de")); // the language in use is not sent again
        Assert.Single(_api.LanguageChoices);
    }
}
