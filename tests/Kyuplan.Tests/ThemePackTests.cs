using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Options;
using Kyuplan.Client.Api;
using Kyuplan.Client.Localization;
using Kyuplan.Client.ViewModels;
using Kyuplan.Contracts;
using static Kyuplan.Tests.TestAccounts;

namespace Kyuplan.Tests;

internal static class Packs
{
    /// <summary>A pack that passes every check; a test changes what it is about.</summary>
    public static ThemePackDto Valid(string id = "ocean", string scheme = "dark", bool highContrast = false, string? accent = null) =>
        BuiltInThemePacks.All.First(p => p.Scheme == scheme && !p.HighContrast) with
        {
            Id = id, Name = "Ocean", NameDe = "Ozean", HighContrast = highContrast, Accent = accent, BuiltIn = false,
        };

    public static ThemePackDto With(this ThemePackDto pack, string key, string color) =>
        pack with { Colors = new Dictionary<string, string>(pack.Colors) { [key] = color } };

    public static string Json(ThemePackDto pack) => JsonSerializer.Serialize(pack, new JsonSerializerOptions(JsonSerializerDefaults.Web));
}

/// <summary>The theme packs that ship with the program, and the rules an uploaded pack has to meet.</summary>
public class ThemePackRulesTests
{
    [Fact]
    public void Every_built_in_pack_is_a_valid_pack_with_its_own_id()
    {
        Assert.All(BuiltInThemePacks.All, pack =>
        {
            Assert.Null(ThemePackRules.Check(pack));
            Assert.True(pack.BuiltIn);
            Assert.InRange(pack.Id.Length, 1, ThemeRules.MaxLength);
        });
        Assert.Equal(BuiltInThemePacks.All.Count, BuiltInThemePacks.All.Select(p => p.Id).Distinct().Count());
    }

    [Fact]
    public void Every_example_pack_in_the_docs_is_valid_and_named_after_its_id()
    {
        var folder = Path.Combine(RepositoryRoot(), "docs", "examples");
        var files = Directory.GetFiles(folder, "*.json");
        Assert.Contains(files, f => Path.GetFileName(f) == "matrix.json");
        Assert.All(files, file =>
        {
            var pack = JsonSerializer.Deserialize<ThemePackDto>(File.ReadAllText(file), new JsonSerializerOptions(JsonSerializerDefaults.Web));
            Assert.Null(ThemePackRules.Check(pack));
            Assert.Equal(Path.GetFileNameWithoutExtension(file), pack!.Id);
            Assert.DoesNotContain(BuiltInThemePacks.All, b => b.Id == pack.Id);
        });
    }

    private static string RepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "global.json"))) directory = directory.Parent;
        return directory?.FullName ?? throw new InvalidOperationException("global.json not found above the test binaries.");
    }

    [Theory]
    [InlineData(BuiltInThemePacks.HighContrastDark)]
    [InlineData(BuiltInThemePacks.HighContrastLight)]
    [InlineData(BuiltInThemePacks.HighContrastYellow)]
    public void The_high_contrast_themes_keep_AAA_contrast_for_text_everywhere(string id)
    {
        var pack = BuiltInThemePacks.All.Single(p => p.Id == id);

        Assert.True(pack.HighContrast);
        foreach (var surface in new[] { ThemePackRules.Ground, ThemePackRules.Sheet, ThemePackRules.SheetAlt, ThemePackRules.Sidebar })
            Assert.True(ThemePackRules.Contrast(pack.Color(ThemePackRules.Text), pack.Color(surface)) >= 7, $"text on {surface}");
        Assert.True(ThemePackRules.Contrast(pack.Color(ThemePackRules.Muted), pack.Color(ThemePackRules.Sheet)) >= 7, "muted");
        Assert.True(ThemePackRules.Contrast(pack.Color(ThemePackRules.RuleStrong), pack.Color(ThemePackRules.Sheet)) >= 7, "borders");
        Assert.True(ThemePackRules.Contrast(pack.Accent, pack.Color(ThemePackRules.Sheet)) >= 7, "accent");
        Assert.True(ThemePackRules.Contrast(pack.Color(ThemePackRules.Amber), pack.Color(ThemePackRules.Sheet)) >= 7, "amber");
        Assert.True(ThemePackRules.Contrast(pack.Color(ThemePackRules.Red), pack.Color(ThemePackRules.Sheet)) >= 7, "red");
    }

    [Theory]
    [InlineData("ocean", true)]
    [InlineData("my-theme-2", true)]
    [InlineData("a", true)]
    [InlineData("", false)]
    [InlineData("-ocean", false)]
    [InlineData("ocean-", false)]
    [InlineData("ocean--blue", false)]
    [InlineData("Ocean", false)]
    [InlineData("ocean blue", false)]
    [InlineData("../ocean", false)]
    [InlineData("dracula", false)] // a theme the clients draw themselves
    [InlineData("default", false)]
    [InlineData("windows10", false)]
    [InlineData("a-very-long-id-that-does-not-fit-the-columns", false)]
    public void An_id_is_a_slug_that_is_no_built_in_theme(string id, bool valid) => Assert.Equal(valid, ThemePackRules.IsPackId(id));

    [Fact]
    public void A_good_pack_passes_and_a_theme_pack_id_is_a_valid_choice_for_both_clients()
    {
        Assert.Null(ThemePackRules.Check(Packs.Valid()));
        Assert.True(ThemeRules.IsValid(ThemeClients.Web, "ocean"));
        Assert.True(ThemeRules.IsValid(ThemeClients.Windows, "ocean"));
        Assert.Equal("ocean", ThemeRules.Resolve(ThemeClients.Web, "ocean", null));
    }

    public static TheoryData<string, ThemePackDto> BadPacks => new()
    {
        { "id", Packs.Valid("Bad Id") },
        { "name", Packs.Valid() with { Name = " " } },
        { "name", Packs.Valid() with { Name = new string('x', ThemePackRules.MaxNameLength + 1) } },
        { "nameDe", Packs.Valid() with { NameDe = new string('x', ThemePackRules.MaxNameLength + 1) } },
        { "scheme", Packs.Valid() with { Scheme = "sepia" } },
        { "accent", Packs.Valid() with { Accent = "blue" } },
        { "colors.sheet", Packs.Valid() with { Colors = Packs.Valid().Colors.Where(c => c.Key != "sheet").ToDictionary(c => c.Key, c => c.Value) } },
        { "colors.text", Packs.Valid().With("text", "#fff") },
        { "colors.text", Packs.Valid().With("text", "url(x)") },
        { "colors.unknown", Packs.Valid() with { Colors = new Dictionary<string, string>(Packs.Valid().Colors) { ["unknown"] = "#000000" } } },
        { "contrast", Packs.Valid().With("text", "#444444") }, // text barely visible on the dark sheet
        { "contrast", Packs.Valid().With("ground", "#eeeeee") },
        { "muted", Packs.Valid().With("muted", "#333333") },
        { "accent", Packs.Valid(accent: "#101010") }, // too faint on the dark sheet
        { "contrast", Packs.Valid(highContrast: true).With("text", "#bbbbbb") }, // good enough for a normal theme, not for a high contrast one
    };

    [Theory]
    [MemberData(nameof(BadPacks))]
    public void A_pack_with_a_problem_is_refused_and_says_what_is_wrong(string expectedInProblem, ThemePackDto pack)
    {
        var problem = ThemePackRules.Check(pack);

        Assert.NotNull(problem);
        Assert.Contains(expectedInProblem, problem, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void A_pack_may_be_nothing()
    {
        Assert.NotNull(ThemePackRules.Check(null));
    }

    [Fact]
    public void The_text_on_an_accent_colour_is_black_or_white_whichever_reads_better()
    {
        Assert.Equal("#000000", ThemePackRules.OnColor("#ffd400"));
        Assert.Equal("#ffffff", ThemePackRules.OnColor("#0033cc"));
    }
}

/// <summary>Installing, listing and deleting theme packs against the real API, with a folder of their own.</summary>
public sealed class ThemePackApiTests(WebApplicationFactory<Program> factory) : IClassFixture<WebApplicationFactory<Program>>, IDisposable
{
    private const string UserPassword = "a good password";

    private readonly InMemoryDatabase _planner = new();
    private readonly InMemoryDatabase _accounts = new();
    private readonly string _folder = Directory.CreateTempSubdirectory("pp-themes-").FullName;

    public void Dispose()
    {
        _planner.Dispose();
        _accounts.Dispose();
        Directory.Delete(_folder, recursive: true);
    }

    private WebApplicationFactory<Program> CreateHost() =>
        factory.WithWebHostBuilder(builder =>
        {
            builder.UseSetting("Spond:UseDemoData", "true");
            builder.UseSetting("Sync:Enabled", "false");
            builder.UseSetting("DefaultData:Enabled", "false");
            builder.UseSetting("Database:ConnectionString", _planner.ConnectionString);
            builder.UseSetting("Themes:Directory", _folder);
            Configure(builder, _accounts);
        });

    private static StringContent Body(string json) => new(json, Encoding.UTF8, "application/json");

    private static async Task<ThemeCatalogDto> CatalogAsync(HttpClient client) =>
        (await client.GetFromJsonAsync<ThemeCatalogDto>("/api/themes", new JsonSerializerOptions(JsonSerializerDefaults.Web)))!;

    [Fact]
    public async Task The_catalog_lists_the_built_in_packs_to_anybody_without_a_login()
    {
        using var host = CreateHost();

        var catalog = await CatalogAsync(host.CreateClient());

        Assert.Equal(BuiltInThemePacks.All.Select(p => p.Id), catalog.Packs.Select(p => p.Id));
        Assert.All(catalog.Packs, p => Assert.True(p.BuiltIn));
        Assert.Contains(catalog.Packs, p => p.Id == BuiltInThemePacks.HighContrastDark && p.HighContrast);
    }

    [Fact]
    public async Task An_administrator_installs_a_pack_everybody_sees_it_and_it_survives_a_restart()
    {
        using var host = CreateHost();
        var admin = host.CreateSignedInClient();
        var before = await host.CreateClient().GetFromJsonAsync<BrandingDto>("/api/branding");

        var response = await admin.PutAsync("/api/themes", Body(Packs.Json(Packs.Valid())));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var catalog = (await response.Content.ReadFromJsonAsync<ThemeCatalogDto>(new JsonSerializerOptions(JsonSerializerDefaults.Web)))!;
        var ocean = catalog.Packs.Single(p => p.Id == "ocean");
        Assert.False(ocean.BuiltIn);
        Assert.Equal("Ozean", ocean.NameDe);
        Assert.True(File.Exists(Path.Combine(_folder, "ocean.json")));

        // the branding tells the clients that the set changed
        var after = await host.CreateClient().GetFromJsonAsync<BrandingDto>("/api/branding");
        Assert.NotEqual(before!.ThemesVersion, after!.ThemesVersion);
        Assert.Equal(catalog.Version, after.ThemesVersion);

        using var restarted = CreateHost();
        Assert.Contains((await CatalogAsync(restarted.CreateClient())).Packs, p => p.Id == "ocean");
    }

    [Fact]
    public async Task Installing_a_pack_with_the_id_of_an_installed_one_replaces_it()
    {
        using var host = CreateHost();
        var admin = host.CreateSignedInClient();
        await admin.PutAsync("/api/themes", Body(Packs.Json(Packs.Valid())));

        await admin.PutAsync("/api/themes", Body(Packs.Json(Packs.Valid() with { Name = "Deep ocean" })));

        var packs = (await CatalogAsync(admin)).Packs.Where(p => !p.BuiltIn).ToList();
        Assert.Equal("Deep ocean", packs.Single().Name);
    }

    [Fact]
    public async Task Only_those_with_themes_edit_install_or_delete_packs()
    {
        using var host = CreateHost();
        Assert.Equal(HttpStatusCode.Created, (await host.CreateSignedInClient().PostAsJsonAsync("/api/users", Input("anna", UserPassword, false))).StatusCode);
        var anna = host.CreateSignedInClient("anna", UserPassword);

        Assert.Equal(HttpStatusCode.Forbidden, (await anna.PutAsync("/api/themes", Body(Packs.Json(Packs.Valid())))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await anna.DeleteAsync("/api/themes/ocean")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await host.CreateClient().PutAsync("/api/themes", Body(Packs.Json(Packs.Valid())))).StatusCode);
        Assert.False(File.Exists(Path.Combine(_folder, "ocean.json")));
        Assert.Equal(HttpStatusCode.OK, (await anna.GetAsync("/api/themes")).StatusCode); // reading is for everybody
    }

    [Fact]
    public async Task A_pack_that_cannot_be_used_is_refused_with_the_reason()
    {
        using var host = CreateHost();
        var admin = host.CreateSignedInClient();

        var lowContrast = await admin.PutAsync("/api/themes", Body(Packs.Json(Packs.Valid().With("text", "#444444"))));
        Assert.Equal(HttpStatusCode.BadRequest, lowContrast.StatusCode);
        Assert.Contains("contrast", await lowContrast.Content.ReadAsStringAsync(), StringComparison.OrdinalIgnoreCase);

        Assert.Equal(HttpStatusCode.BadRequest, (await admin.PutAsync("/api/themes", Body("{ not json"))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await admin.PutAsync("/api/themes", Body("null"))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await admin.PutAsync("/api/themes", Body(Packs.Json(Packs.Valid(BuiltInThemePacks.HighContrastDark))))).StatusCode); // a built-in id
        Assert.Equal(HttpStatusCode.BadRequest, (await admin.PutAsync("/api/themes", Body(Packs.Json(Packs.Valid("dracula"))))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await admin.PutAsync("/api/themes", Body(new string(' ', ThemePackRules.MaxBytes) + Packs.Json(Packs.Valid())))).StatusCode); // too large
        Assert.Empty(Directory.GetFiles(_folder));
    }

    [Fact]
    public async Task An_uploaded_pack_can_be_deleted_a_built_in_one_cannot()
    {
        using var host = CreateHost();
        var admin = host.CreateSignedInClient();
        await admin.PutAsync("/api/themes", Body(Packs.Json(Packs.Valid())));

        var deleted = await admin.DeleteAsync("/api/themes/ocean");

        Assert.Equal(HttpStatusCode.OK, deleted.StatusCode);
        Assert.DoesNotContain((await CatalogAsync(admin)).Packs, p => p.Id == "ocean");
        Assert.False(File.Exists(Path.Combine(_folder, "ocean.json")));
        Assert.Equal(HttpStatusCode.NotFound, (await admin.DeleteAsync("/api/themes/ocean")).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await admin.DeleteAsync("/api/themes/" + BuiltInThemePacks.HighContrastDark)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await admin.DeleteAsync("/api/themes/..%2Fsettings")).StatusCode);
        Assert.Contains((await CatalogAsync(admin)).Packs, p => p.Id == BuiltInThemePacks.HighContrastDark);
    }

    [Fact]
    public async Task A_file_put_into_the_folder_by_hand_is_a_pack_and_a_broken_one_is_skipped()
    {
        using var host = CreateHost();
        var client = host.CreateClient();
        Assert.DoesNotContain((await CatalogAsync(client)).Packs, p => p.Id == "ocean");

        await File.WriteAllTextAsync(Path.Combine(_folder, "ocean.json"), Packs.Json(Packs.Valid()));
        await File.WriteAllTextAsync(Path.Combine(_folder, "broken.json"), "{ nope");
        await File.WriteAllTextAsync(Path.Combine(_folder, "mismatch.json"), Packs.Json(Packs.Valid("other")));
        await File.WriteAllTextAsync(Path.Combine(_folder, "nord.json"), Packs.Json(Packs.Valid("nord")));

        var packs = (await CatalogAsync(client)).Packs;

        Assert.Contains(packs, p => p.Id == "ocean" && !p.BuiltIn);
        Assert.DoesNotContain(packs, p => p.Id is "other");
        Assert.Single(packs, p => p.Id == "nord"); // the built-in one stays
        Assert.True(packs.Single(p => p.Id == "nord").BuiltIn);
    }

    [Fact]
    public async Task An_installed_pack_can_be_chosen_by_a_user_and_made_the_default_a_missing_one_cannot()
    {
        using var host = CreateHost();
        var admin = host.CreateSignedInClient();
        await admin.PutAsync("/api/themes", Body(Packs.Json(Packs.Valid())));

        var chosen = await admin.PutAsJsonAsync("/api/account/theme", new ThemePreferenceDto(ThemeClients.Web, "ocean"));
        Assert.Equal(HttpStatusCode.OK, chosen.StatusCode);
        Assert.Equal("ocean", (await chosen.Content.ReadFromJsonAsync<UserDto>())!.WebTheme);
        Assert.Equal(HttpStatusCode.OK, (await admin.PutAsJsonAsync("/api/account/theme", new ThemePreferenceDto(ThemeClients.Windows, BuiltInThemePacks.HighContrastDark))).StatusCode);

        Assert.Equal(HttpStatusCode.BadRequest, (await admin.PutAsJsonAsync("/api/account/theme", new ThemePreferenceDto(ThemeClients.Web, "missing"))).StatusCode);

        var asDefault = await admin.PutAsJsonAsync("/api/branding", new BrandingInputDto("PlanIt", "Club", "#1a5fb4", "ocean", BuiltInThemePacks.HighContrastLight));
        Assert.Equal(HttpStatusCode.OK, asDefault.StatusCode);
        var branding = (await host.CreateClient().GetFromJsonAsync<BrandingDto>("/api/branding"))!;
        Assert.Equal("ocean", branding.DefaultWebTheme);
        Assert.Equal(BuiltInThemePacks.HighContrastLight, branding.DefaultWindowsTheme);

        Assert.Equal(HttpStatusCode.BadRequest, (await admin.PutAsJsonAsync("/api/branding", new BrandingInputDto("PlanIt", "Club", "#1a5fb4", "missing"))).StatusCode);
    }
}

/// <summary>What the clients do with the packs: list them, resolve the theme in effect, and let administrators manage them.</summary>
public sealed class ThemePackViewModelTests : IDisposable
{
    private readonly FakePlannerApi _api = new();
    private readonly LanguageService _language = new();
    private readonly LoginSession _session = new();
    private readonly ShellViewModel _shell;

    public ThemePackViewModelTests()
    {
        _language.Select("en");
        _api.Account = Dto(1, "admin", "admin", [], true);
        _shell = new ShellViewModel(_api, _language, _session);
    }

    public void Dispose()
    {
        CultureInfo.DefaultThreadCurrentCulture = null;
        CultureInfo.DefaultThreadCurrentUICulture = null;
    }

    private ThemeViewModel Themes(string client = ThemeClients.Web) =>
        new(_shell, _api, Options.Create(new PlannerClientOptions { ThemeClient = client }));

    [Fact]
    public async Task The_built_in_packs_are_offered_without_asking_the_api_high_contrast_ones_first()
    {
        var themes = Themes();

        var ids = themes.Choices.Select(c => c.Id).ToList();

        Assert.Equal("", ids[0]);
        Assert.Equal(ThemeIds.Default, ids[1]);
        Assert.Equal([BuiltInThemePacks.HighContrastDark, BuiltInThemePacks.HighContrastLight, BuiltInThemePacks.HighContrastYellow], ids.Skip(2).Take(3));
        Assert.Contains("nord", ids);
        Assert.Equal("High contrast (dark)", themes.Choices.Single(c => c.Id == BuiltInThemePacks.HighContrastDark).Name);
        await Task.CompletedTask;
    }

    [Fact]
    public async Task A_chosen_pack_is_the_theme_in_effect_with_its_colour_scheme()
    {
        await _shell.InitializeAsync();
        var themes = Themes();
        var announced = new List<string>();
        themes.Changed += (_, _) => announced.Add(themes.Effective);

        await themes.ChooseAsync(BuiltInThemePacks.HighContrastDark);

        Assert.Equal(BuiltInThemePacks.HighContrastDark, themes.Effective);
        Assert.True(themes.EffectivePack!.HighContrast);
        Assert.Equal("dark", themes.FixedScheme); // the light/dark switch is of no use
        Assert.Equal([BuiltInThemePacks.HighContrastDark], announced);
        Assert.Equal(new ThemePreferenceDto(ThemeClients.Web, BuiltInThemePacks.HighContrastDark), _api.ThemeChoices.Single());
    }

    [Fact]
    public async Task The_names_of_a_pack_follow_the_language()
    {
        var themes = Themes();
        Assert.Equal("Solarized light", themes.NameOf("solarized-light"));

        _language.Select("de");
        Assert.Equal("Solarized hell", themes.NameOf("solarized-light"));
        Assert.Equal("Nord", themes.NameOf("nord")); // no German name: the same in every language
        await Task.CompletedTask;
    }

    [Fact]
    public async Task An_uploaded_pack_is_asked_for_once_the_branding_says_the_set_changed_and_nothing_flickers_before()
    {
        _api.UploadedPacks.Add(Packs.Valid());
        _api.Account = Dto(1, "admin", "admin", [], true) with { WebTheme = "ocean" };
        await _shell.InitializeAsync();
        var themes = Themes();
        var announced = new List<string>();
        themes.Changed += (_, _) => announced.Add(themes.Effective);
        _api.Branding = _api.Branding with { ThemesVersion = "t1" };

        // the branding arrives first: the pack is not known yet, which must not announce the plain default (a page painted the pack already)
        await _shell.Brand.RefreshAsync();
        await themes.LoadCatalogAsync("t1");

        Assert.Equal(["ocean"], announced);
        Assert.Equal("ocean", themes.Effective);
        Assert.Equal("Ocean", themes.NameOf("ocean"));
        Assert.Contains(themes.Choices, c => c.Id == "ocean");
    }

    [Fact]
    public async Task A_pack_that_is_gone_is_as_good_as_not_chosen_and_the_default_of_the_instance_takes_over()
    {
        _api.UploadedPacks.Add(Packs.Valid());
        _api.Account = Dto(1, "admin", "admin", [], true) with { WebTheme = "ocean" };
        _api.Branding = _api.Branding with { DefaultWebTheme = BuiltInThemePacks.HighContrastLight };
        await _shell.InitializeAsync();
        var themes = Themes();
        await themes.LoadCatalogAsync();
        Assert.Equal("ocean", themes.Effective);

        _api.UploadedPacks.Clear();
        await themes.LoadCatalogAsync();

        Assert.Equal(BuiltInThemePacks.HighContrastLight, themes.Effective);
        Assert.Equal("", themes.SelectedId);
    }

    [Fact]
    public async Task A_pack_whose_colours_changed_is_announced_again()
    {
        _api.UploadedPacks.Add(Packs.Valid());
        _api.Account = Dto(1, "admin", "admin", [], true) with { WebTheme = "ocean" };
        await _shell.InitializeAsync();
        var themes = Themes();
        await themes.LoadCatalogAsync();
        var announced = 0;
        themes.Changed += (_, _) => announced++;

        _api.UploadedPacks[0] = Packs.Valid().With("amber", "#ffcc00");
        await themes.LoadCatalogAsync();
        await themes.LoadCatalogAsync(); // nothing changed this time

        Assert.Equal(1, announced);
    }

    // --- The administrators' page ------------------------------------------------------------------------------

    private async Task<BrandingAdminViewModel> PageAsync()
    {
        var vm = new BrandingAdminViewModel(_shell, _api, Options.Create(new PlannerClientOptions { BaseAddress = "https://planner.example.org/" }));
        await vm.ActivateAsync();
        return vm;
    }

    private static Task InstallAsync(BrandingAdminViewModel vm, string json, long? size = null) =>
        vm.InstallThemeAsync(size ?? Encoding.UTF8.GetByteCount(json), () => Task.FromResult(json));

    [Fact]
    public async Task The_page_lists_the_built_in_packs_and_offers_them_as_defaults()
    {
        var vm = await PageAsync();

        Assert.Equal(BuiltInThemePacks.All.Count, vm.ThemePacks.Count);
        Assert.All(vm.ThemePacks, row => Assert.True(row.BuiltIn));
        Assert.Contains(vm.WebThemes, c => c.Id == BuiltInThemePacks.HighContrastYellow);
        Assert.Contains(vm.WindowsThemes, c => c.Id == "tokyo-night");
        Assert.Contains("high contrast", vm.ThemePacks.Single(r => r.Id == BuiltInThemePacks.HighContrastDark).Details);
    }

    [Fact]
    public async Task An_administrator_installs_a_pack_from_a_file_and_the_clients_are_told()
    {
        var vm = await PageAsync();
        var versionBefore = _shell.Brand.ThemesVersion;

        await InstallAsync(vm, Packs.Json(Packs.Valid()));

        Assert.Equal(["install-theme:ocean"], _api.ThemeCalls);
        Assert.Contains(vm.ThemePacks, r => r.Id == "ocean" && !r.BuiltIn && r.Details.StartsWith("dark"));
        Assert.Contains(vm.WebThemes, c => c.Id == "ocean" && c.Name == "Ocean");
        Assert.Equal("Theme pack \"Ocean\" installed.", vm.NoticeText);
        Assert.NotEqual(versionBefore, _shell.Brand.ThemesVersion); // the branding was asked again
        Assert.False(vm.HasError);
    }

    [Fact]
    public async Task A_refused_pack_shows_what_the_api_says_and_installs_nothing()
    {
        var vm = await PageAsync();

        await InstallAsync(vm, Packs.Json(Packs.Valid().With("text", "#444444")));

        Assert.Contains("contrast", vm.ErrorMessage, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(_api.ThemeCalls);
        Assert.DoesNotContain(vm.ThemePacks, r => r.Id == "ocean");
    }

    [Fact]
    public async Task A_file_that_is_too_large_is_refused_without_being_read()
    {
        var vm = await PageAsync();
        var read = false;

        await vm.InstallThemeAsync(ThemePackRules.MaxBytes + 1, () =>
        {
            read = true;
            return Task.FromResult("{}");
        });

        Assert.False(read);
        Assert.Contains($"{ThemePackRules.MaxBytes / 1024} KB", vm.ErrorMessage);
    }

    [Fact]
    public async Task Only_an_uploaded_pack_can_be_deleted_and_a_default_that_pointed_to_it_goes_back_to_the_plain_default()
    {
        var vm = await PageAsync();
        await InstallAsync(vm, Packs.Json(Packs.Valid()));
        var ocean = vm.ThemePacks.Single(r => r.Id == "ocean");
        vm.FormDefaultWebTheme = "ocean";

        Assert.True(vm.CanDeleteTheme(ocean));
        Assert.False(vm.CanDeleteTheme(vm.ThemePacks.First(r => r.BuiltIn)));

        await vm.DeleteThemeCommand.ExecuteAsync(ocean);

        Assert.Equal(["install-theme:ocean", "delete-theme:ocean"], _api.ThemeCalls);
        Assert.DoesNotContain(vm.ThemePacks, r => r.Id == "ocean");
        Assert.Equal(ThemeIds.Default, vm.FormDefaultWebTheme);
        Assert.Equal("Theme pack \"Ocean\" deleted.", vm.NoticeText);
    }

    [Fact]
    public async Task Somebody_without_themes_edit_sees_the_packs_but_cannot_change_them()
    {
        _api.Account = WithPermissions(2, "anna", Permission.BrandingView);
        _shell.Session.User = _api.Account;
        var vm = await PageAsync();

        Assert.NotEmpty(vm.ThemePacks);
        Assert.False(vm.CanManageThemes);
        Assert.False(vm.CanInstallTheme);

        await InstallAsync(vm, Packs.Json(Packs.Valid()));

        Assert.Empty(_api.ThemeCalls);
    }
}
