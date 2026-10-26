using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Kyuplan.Api.Plugins;
using Kyuplan.Client.Api;
using Kyuplan.Client.Localization;
using Kyuplan.Client.Resources;
using Kyuplan.Client.ViewModels;
using Kyuplan.Contracts;
using Microsoft.Extensions.Configuration;
using static Kyuplan.Tests.TestAccounts;

namespace Kyuplan.Tests;

/// <summary>The plugins page: the installed plugins, installing, updating and removing, and the restart that each of them causes.</summary>
[Collection(LanguageStateCollection.Name)]
public sealed class PluginsViewModelTests : IDisposable
{
    private readonly FakePlannerApi _api = new();
    private readonly LanguageService _language = new();
    private readonly ShellViewModel _shell;
    private readonly PluginsViewModel _vm;

    public PluginsViewModelTests()
    {
        _language.Select("en");
        _api.Account = Dto(1, "admin", "admin", [], true);
        _shell = new ShellViewModel(_api, _language);
        _vm = new PluginsViewModel(_shell, _api, new ApiRestartWatcher(_api) { Delay = (_, _) => Task.CompletedTask });
    }

    public void Dispose()
    {
        CultureInfo.DefaultThreadCurrentCulture = null;
        CultureInfo.DefaultThreadCurrentUICulture = null;
    }

    private static PluginAdminDto Installed(string id, string source = "https://github.com/acme/kyuplan-acme", bool running = true, string? problem = null, string[]? sources = null, int pages = 0) =>
        new(id, id.ToUpperInvariant(), "1.2.0", $"The {id} plugin.", source, new DateTimeOffset(2026, 10, 1, 0, 0, 0, TimeSpan.Zero), running, problem, sources ?? [], pages, "abc");

    private async Task<PluginsViewModel> ShownAsync(params PluginAdminDto[] plugins)
    {
        _api.InstalledPlugins.AddRange(plugins);
        await _vm.ActivateAsync();
        return _vm;
    }

    [Fact]
    public async Task The_page_lists_the_installed_plugins_with_what_they_say_about_themselves()
    {
        var vm = await ShownAsync(
            Installed("acme", sources: ["acme"], pages: 2),
            Installed("spond", PluginStore.BundledSource, sources: ["spond"]),
            Installed("late", running: false),
            Installed("broken", running: false, problem: "it throws"));

        Assert.True(vm.HasPlugins);
        var acme = vm.Plugins[0];
        Assert.Equal(("acme", "ACME", "Version 1.2.0 · from https://github.com/acme/kyuplan-acme", "The acme plugin."), (acme.Id, acme.Name, acme.Details, acme.Description));
        Assert.Equal("Data sources: acme · 2 pages", acme.Offers);
        Assert.True(acme.HasOffers);
        Assert.Equal(Strings.Plugins_Running, acme.StatusText);
        Assert.True(acme.CanUpdate);
        Assert.Equal("Version 1.2.0 · from the program", vm.Plugins[1].Details);
        Assert.False(vm.Plugins[1].CanUpdate); // it did not come from GitHub
        Assert.Equal("Data sources: spond", vm.Plugins[1].Offers);
        Assert.Equal(Strings.Plugins_NotRunning, vm.Plugins[2].StatusText);
        Assert.False(vm.Plugins[2].HasOffers);
        Assert.Equal("Does not run: it throws", vm.Plugins[3].StatusText);
        Assert.True(vm.Plugins[3].HasProblem);
    }

    [Fact]
    public async Task Nobody_without_the_permission_sees_the_page_and_nothing_is_read()
    {
        _api.Account = Nobody(2, "nobody");
        await _shell.InitializeAsync();

        await _vm.ActivateAsync();

        Assert.False(_vm.CanView);
        Assert.Empty(_vm.Plugins);
    }

    [Fact]
    public async Task Somebody_who_may_only_look_cannot_change_anything()
    {
        _api.Account = WithPermissions(2, "looker", Permission.PluginsView);
        var vm = await ShownAsync(Installed("acme"));
        vm.Url = "https://github.com/acme/plugin";

        Assert.False(vm.CanEdit);
        Assert.False(vm.InstallCommand.CanExecute(null));
        Assert.False(vm.UpdateCommand.CanExecute(vm.Plugins[0]));
        Assert.False(vm.AskRemovalCommand.CanExecute(vm.Plugins[0]));
    }

    [Theory]
    [InlineData("", true, false)]
    [InlineData("   ", true, false)]
    [InlineData("https://github.com/acme/plugin", true, true)]
    [InlineData("https://github.com/acme/plugin.git", true, true)]
    [InlineData("http://github.com/acme/plugin", false, false)]
    [InlineData("https://example.org/acme/plugin", false, false)]
    [InlineData("acme/plugin", false, false)]
    public async Task Only_the_address_of_a_github_repository_can_be_installed(string url, bool noProblem, bool canInstall)
    {
        await ShownAsync();

        _vm.Url = url;

        Assert.Equal(noProblem, _vm.UrlProblem is null);
        if (!noProblem) Assert.Equal(Strings.Plugins_UrlInvalid, _vm.UrlProblem);
        Assert.Equal(canInstall, _vm.InstallCommand.CanExecute(null));
    }

    [Fact]
    public async Task Installing_sends_the_address_waits_for_the_restart_and_reads_the_list_and_the_catalog_again()
    {
        await ShownAsync();
        _vm.Url = " https://github.com/acme/kyuplan-acme ";
        _api.SetupQuestionsFailing = 2; // the API is away for a moment
        var catalogs = 0;
        _shell.PluginsChanged += (_, _) => catalogs++;

        await _vm.InstallCommand.ExecuteAsync(null);

        Assert.Equal(["install:https://github.com/acme/kyuplan-acme"], _api.PluginCalls);
        Assert.Equal("", _vm.Url);
        Assert.False(_vm.IsRestarting);
        Assert.Equal(Strings.Plugins_Restarted, _vm.NoticeText);
        Assert.Equal("kyuplan-acme", Assert.Single(_vm.Plugins).Id);
        Assert.Equal(1, catalogs);
        Assert.Equal(3, _api.SetupQuestions); // two failed answers, the third one came back
    }

    [Fact]
    public async Task A_change_that_needs_no_restart_says_so_and_does_not_wait()
    {
        await ShownAsync(Installed("acme"));
        _api.PluginChangeRestarts = false;

        await _vm.UpdateCommand.ExecuteAsync(_vm.Plugins[0]);

        Assert.Equal(["update:acme"], _api.PluginCalls);
        Assert.Equal(Strings.Plugins_NothingChanged, _vm.NoticeText);
        Assert.Equal(0, _api.SetupQuestions);
        Assert.False(_vm.IsRestarting);
    }

    [Fact]
    public async Task Removing_asks_first_and_does_nothing_before_the_answer()
    {
        await ShownAsync(Installed("acme"));
        var row = _vm.Plugins[0];

        _vm.AskRemovalCommand.Execute(row);

        Assert.True(_vm.IsConfirmingRemoval);
        Assert.Equal("Remove the plugin ACME? The API restarts. What was allowed through its permissions works again when it is installed again.", _vm.RemovalQuestion);
        Assert.Empty(_api.PluginCalls);

        _vm.CancelRemovalCommand.Execute(null);
        Assert.False(_vm.IsConfirmingRemoval);
        Assert.Equal("", _vm.RemovalQuestion);
        await _vm.RemoveCommand.ExecuteAsync(null); // nothing is waiting
        Assert.Empty(_api.PluginCalls);

        _vm.AskRemovalCommand.Execute(row);
        await _vm.RemoveCommand.ExecuteAsync(null);

        Assert.Equal(["remove:acme"], _api.PluginCalls);
        Assert.False(_vm.IsConfirmingRemoval);
        Assert.Empty(_vm.Plugins);
        Assert.Equal(Strings.Plugins_Restarted, _vm.NoticeText);
    }

    [Fact]
    public async Task A_change_the_api_refuses_shows_the_reason_and_nothing_restarts()
    {
        await ShownAsync();
        _vm.Url = "https://github.com/acme/plugin";
        _api.FailWith = new PlannerApiException("The repository has no release.", System.Net.HttpStatusCode.BadRequest);

        await _vm.InstallCommand.ExecuteAsync(null);

        Assert.Equal("The repository has no release.", _vm.ErrorMessage);
        Assert.False(_vm.IsRestarting);
        Assert.False(_vm.HasNotice);
        Assert.Equal(0, _api.SetupQuestions);
    }

    [Fact]
    public async Task An_api_that_does_not_come_back_is_reported()
    {
        await ShownAsync(Installed("acme"));
        _api.SetupQuestionsFailing = int.MaxValue;
        _vm.AskRemovalCommand.Execute(_vm.Plugins[0]);

        await _vm.RemoveCommand.ExecuteAsync(null);

        Assert.Equal(Strings.Setup_NotBack, _vm.ErrorMessage);
        Assert.False(_vm.IsRestarting);
        Assert.False(_vm.HasNotice);
    }

    [Fact]
    public async Task The_page_follows_the_language_and_the_permissions()
    {
        var vm = await ShownAsync(Installed("acme"));
        var changed = new List<string?>();
        vm.PropertyChanged += (_, e) => changed.Add(e.PropertyName);

        _language.Select("de");
        Assert.Equal("Plugins", vm.Title);
        Assert.Contains(nameof(PluginsViewModel.NoticeText), changed);

        _shell.Session.User = Nobody(5, "nobody");
        Assert.False(vm.CanView);
        Assert.Empty(vm.Plugins);
    }

    [Fact]
    public async Task Another_try_reads_the_list_again()
    {
        var vm = await ShownAsync();
        _api.InstalledPlugins.Add(Installed("late"));

        await vm.RetryCommand.ExecuteAsync(null);

        Assert.Equal("late", Assert.Single(vm.Plugins).Id);
    }
}

/// <summary>The settings form with the data sources that plugins offer.</summary>
[Collection(LanguageStateCollection.Name)]
public sealed class PluginSettingsFormTests : IDisposable
{
    public PluginSettingsFormTests() => new LanguageService().Select("en");

    public void Dispose()
    {
        CultureInfo.DefaultThreadCurrentCulture = null;
        CultureInfo.DefaultThreadCurrentUICulture = null;
    }

    private static SettingsForm Form(PluginCatalogDto? catalog = null, string source = "spond")
    {
        var form = new SettingsForm();
        form.SetPlugins(catalog ?? PluginTestKit.AcmeCatalog());
        form.Load(FakePlannerApi.DefaultSettings with { UseDemoData = false, DataSource = source });
        return form;
    }

    [Fact]
    public void The_data_sources_of_the_plugins_come_first_and_have_their_names()
    {
        var form = Form(source: "acme");

        Assert.Equal(["acme", "custom", "manual", "demo"], form.DataSources);
        Assert.Equal("Acme", form.DataSourceText("acme"));
        Assert.Equal(Strings.Settings_DataSource_custom, form.DataSourceText("custom"));
        Assert.Equal("Acme's data.", form.DataSourceHint);
        Assert.True(form.IsPluginSource);
        Assert.False(form.IsMissingPlugin);
    }

    [Fact]
    public void A_data_source_whose_plugin_is_missing_stays_in_the_list_and_says_so()
    {
        var form = Form(source: "spond"); // the catalog is Acme's: nobody offers spond

        Assert.Equal(["acme", "custom", "manual", "demo", "spond"], form.DataSources);
        Assert.True(form.IsMissingPlugin);
        Assert.Equal("spond (plugin not installed)", form.DataSourceText("spond"));
        Assert.Equal(Strings.Settings_DataSourceHint_missing, form.DataSourceHint);
        Assert.Null(form.PluginForm);
    }

    [Fact]
    public void The_builtin_sources_are_no_plugin_sources_and_have_no_plugin_form()
    {
        var form = Form(source: DataSourceKinds.Custom);

        Assert.False(form.IsPluginSource);
        Assert.False(form.IsMissingPlugin);
        Assert.Null(form.PluginForm);
        Assert.Null(form.PluginFormProblem);
        Assert.Null(form.ToInput().PluginSettings);
    }

    [Fact]
    public void Choosing_a_plugin_source_shows_its_form_and_only_its_settings_are_sent()
    {
        var form = Form(source: "acme");
        form.PluginForm!.Fields.Single(f => f.Id == "ApiKey").Text = "secret";
        form.PluginForm.Fields.Single(f => f.Id == "Region").Text = "us";

        var input = form.ToInput();

        var sent = Assert.Single(input.PluginSettings!);
        Assert.Equal("acme", sent.Key);
        Assert.Equal(("secret", "us"), (sent.Value["ApiKey"].GetString(), sent.Value["Region"].GetString()));
        Assert.Equal("acme", input.DataSource);
    }

    [Fact]
    public void A_problem_in_the_plugin_form_is_a_problem_of_the_settings_and_goes_when_another_source_is_chosen()
    {
        var form = Form(source: "acme");

        Assert.Equal(Strings.Plugin_FieldRequired, form.PluginFormProblem);
        Assert.False(form.IsValid);

        form.DataSource = DataSourceKinds.Demo;
        Assert.Null(form.PluginFormProblem);
        Assert.True(form.IsValid);

        form.DataSource = "acme";
        form.PluginForm!.Fields.Single(f => f.Id == "ApiKey").Text = "k";
        Assert.True(form.IsValid);
    }

    [Fact]
    public void The_form_is_changed_when_the_plugin_form_is_and_typing_it_back_is_not_a_change()
    {
        var settings = FakePlannerApi.DefaultSettings with
        {
            UseDemoData = false, DataSource = "acme",
            PluginSettings = new Dictionary<string, PluginSettingsDto> { ["acme"] = new(PluginTestKit.Values(("Region", "us")), ["ApiKey"]) },
        };
        var form = new SettingsForm();
        form.SetPlugins(PluginTestKit.AcmeCatalog());
        form.Load(settings);

        Assert.True(form.Matches(settings));
        Assert.True(form.PluginForm!.Fields.Single(f => f.Id == "ApiKey").HasStoredSecret);
        Assert.Equal("us", form.PluginForm.Fields.Single(f => f.Id == "Region").Text);

        form.PluginForm.Fields.Single(f => f.Id == "Region").Text = "eu";
        Assert.False(form.Matches(settings));
        form.PluginForm.Fields.Single(f => f.Id == "Region").Text = "us";
        Assert.True(form.Matches(settings));
    }

    [Fact]
    public void A_change_of_the_plugin_form_is_announced_to_those_who_watch_the_settings()
    {
        var form = Form(source: "acme");
        var changed = new List<string?>();
        form.PropertyChanged += (_, e) => changed.Add(e.PropertyName);

        form.PluginForm!.Fields.Single(f => f.Id == "ApiKey").Text = "k";

        Assert.Contains(nameof(SettingsForm.PluginFormProblem), changed);
        Assert.Contains(nameof(SettingsForm.IsValid), changed);
    }

    [Fact]
    public void The_data_source_of_a_catalog_without_a_form_has_none_and_a_new_catalog_replaces_the_forms()
    {
        var withoutForm = new PluginCatalogDto([new PluginDto("acme", "Acme", null, "1", null, null, [], [], [new PluginDataSourceDto("acme", "Acme", null, null, null, null)])]);
        var form = Form(withoutForm, "acme");
        Assert.Null(form.PluginForm);
        Assert.False(form.IsMissingPlugin); // the plugin is there, it only has no settings

        form.SetPlugins(PluginTestKit.AcmeCatalog());
        Assert.NotNull(form.PluginForm);
        form.SetPlugins(null);
        Assert.Null(form.PluginForm);
        Assert.True(form.IsMissingPlugin);
    }

    [Fact]
    public void The_names_follow_the_language()
    {
        var form = Form(source: "acme");
        new LanguageService().Select("de");
        var changed = new List<string?>();
        form.PropertyChanged += (_, e) => changed.Add(e.PropertyName);

        form.LanguageChanged();

        Assert.Equal("Acme DE", form.DataSourceText("acme"));
        Assert.Contains(nameof(SettingsForm.DataSources), changed);
        Assert.Contains(nameof(SettingsForm.DataSourceHint), changed);
        Assert.Equal("Europa", form.PluginForm!.Fields.Single(f => f.Id == "Region").Choices[0].Label);
    }

    [Fact]
    public void The_demo_switch_still_chooses_between_demo_and_the_default_source()
    {
        var form = Form(source: "acme");

        form.UseDemoData = true;
        Assert.Equal(DataSourceKinds.Demo, form.DataSource);
        form.UseDemoData = false;
        Assert.Equal(DataSourceKinds.Spond, form.DataSource);
    }

    [Fact]
    public async Task The_settings_page_gets_the_forms_from_the_shell_and_saves_the_typed_values()
    {
        var api = new FakePlannerApi { Account = Dto(1, "admin", "admin", [], true), PluginCatalog = PluginTestKit.AcmeCatalog() };
        var shell = new ShellViewModel(api, new LanguageService());
        var page = new SettingsViewModel(shell, api, new ApiRestartWatcher(api) { Delay = (_, _) => Task.CompletedTask });
        api.Settings = FakePlannerApi.DefaultSettings with { UseDemoData = false, DataSource = "acme" };
        await page.ActivateAsync();

        Assert.True(page.Form.IsPluginSource);
        Assert.False(page.CanSave); // the API key is missing

        page.Form.PluginForm!.Fields.Single(f => f.Id == "ApiKey").Text = "k";
        Assert.True(page.CanSave);
        await page.SaveCommand.ExecuteAsync(null);

        var saved = Assert.Single(api.SettingsSaves);
        Assert.Equal("k", saved.PluginSettings!["acme"]["ApiKey"].GetString());
    }

    [Fact]
    public async Task The_setup_screen_gets_the_forms_from_the_setup_status()
    {
        var api = new FakePlannerApi { NeedsSetup = true, PluginCatalog = PluginTestKit.SpondCatalog() };
        var setup = new SetupViewModel(api, new ApiRestartWatcher(api) { Delay = (_, _) => Task.CompletedTask });
        await setup.CheckAsync();

        Assert.Contains("spond", setup.Form.DataSources);
        Assert.NotNull(setup.Form.PluginForm); // the default source is spond
        Assert.Equal(["Email", "PhoneNumber", "Password", "Accounts"], setup.Form.PluginForm!.Items.Select(i => i switch { PluginFieldViewModel f => f.Id, PluginListViewModel l => l.Id, _ => "" }));
    }
}

/// <summary>The part of the API that turns the settings form into configuration keys and back.</summary>
public sealed class PluginSettingsMapperTests
{
    private static readonly PluginUiLayout Form = PluginTestKit.Layout(PluginTestKit.AcmeForm);

    private static IConfigurationSection Section(params (string Key, string Value)[] values) =>
        new ConfigurationBuilder().AddInMemoryCollection(values.ToDictionary(v => v.Key, v => (string?)v.Value)).Build().GetSection("Acme");

    private static IReadOnlyDictionary<string, JsonElement> Values(params (string, object?)[] values) => PluginTestKit.Values(values);

    [Fact]
    public void The_form_shows_what_is_configured_with_the_defaults_for_what_is_not_and_no_password()
    {
        var read = PluginSettingsMapper.Read(Form, Section(("Acme:Region", "us"), ("Acme:Retries", "4"), ("Acme:Verbose", "TRUE"), ("Acme:ApiKey", "secret"), ("Acme:Notes", "n")));

        Assert.Equal(("us", 4d, true, "n"), (read.Values["Region"].GetString(), read.Values["Retries"].GetDouble(), read.Values["Verbose"].GetBoolean(), read.Values["Notes"].GetString()));
        Assert.DoesNotContain("ApiKey", read.Values.Keys);
        Assert.Equal(["ApiKey"], read.SecretsSet);

        var defaults = PluginSettingsMapper.Read(Form, Section());
        Assert.Equal(("eu", 2d, false, ""), (defaults.Values["Region"].GetString(), defaults.Values["Retries"].GetDouble(), defaults.Values["Verbose"].GetBoolean(), defaults.Values["Notes"].GetString()));
        Assert.Empty(defaults.SecretsSet);
    }

    [Fact]
    public void A_number_that_is_no_number_is_shown_as_empty()
    {
        var read = PluginSettingsMapper.Read(Form, Section(("Acme:Retries", "many")));

        Assert.Equal(JsonValueKind.Null, read.Values["Retries"].ValueKind);
    }

    [Fact]
    public void The_rows_of_a_list_are_numbered_in_the_order_of_their_index_and_their_passwords_are_listed_by_place()
    {
        var read = PluginSettingsMapper.Read(Form, Section(("Acme:Rows:1:Name", "second"), ("Acme:Rows:0:Name", "first"), ("Acme:Rows:0:Token", "t0"), ("Acme:Rows:1:Token", "t1"), ("Acme:Rows:10:Name", "eleventh")));

        var rows = read.Values["Rows"].EnumerateArray().ToList();
        Assert.Equal(["first", "second", "eleventh"], rows.Select(r => r.GetProperty("Name").GetString()));
        Assert.Equal([0, 1, 2], rows.Select(r => r.GetProperty(PluginRules.KeepProperty).GetInt32()));
        Assert.Equal(["Rows/0/Token", "Rows/1/Token"], read.SecretsSet);
    }

    [Fact]
    public void Only_what_differs_from_the_configuration_is_a_change_and_keys_that_were_not_sent_stay()
    {
        var current = Section(("Acme:Region", "eu"), ("Acme:Retries", "2"), ("Acme:ApiKey", "old"));

        var changes = PluginSettingsMapper.Changes(Form, "Acme", Values(("Region", "us"), ("Retries", 2), ("ApiKey", "old"), ("Verbose", false)), current);

        Assert.Equal(["Acme:Region"], changes.Keys); // retries and the key are as they were; verbose false is the default
        Assert.Equal("us", changes["Acme:Region"]);
    }

    [Fact]
    public void A_password_is_a_change_only_when_something_new_is_typed()
    {
        var current = Section(("Acme:ApiKey", "old"));

        Assert.Empty(PluginSettingsMapper.Changes(Form, "Acme", Values(("ApiKey", "")), current));
        Assert.Empty(PluginSettingsMapper.Changes(Form, "Acme", Values(("ApiKey", "old")), current));
        Assert.Equal("new", PluginSettingsMapper.Changes(Form, "Acme", Values(("ApiKey", "new")), current)["Acme:ApiKey"]);
    }

    [Fact]
    public void The_values_are_written_as_the_configuration_keeps_them()
    {
        var changes = PluginSettingsMapper.Changes(Form, "Acme", Values(("Region", " us "), ("Retries", 3.5), ("Verbose", true), ("Notes", " keep\nspace "), ("ApiKey", " pw ")), Section());

        Assert.Equal(("us", "3.5", "true", " keep\nspace ", " pw "), ((string?)changes["Acme:Region"], (string?)changes["Acme:Retries"], (string?)changes["Acme:Verbose"], (string?)changes["Acme:Notes"], (string?)changes["Acme:ApiKey"]));
    }

    [Fact]
    public void A_list_is_written_as_a_whole_with_the_passwords_of_the_rows_that_keep_their_place()
    {
        var current = Section(("Acme:Rows:0:Name", "a"), ("Acme:Rows:0:Token", "pa"), ("Acme:Rows:1:Name", "b"), ("Acme:Rows:1:Token", "pb"));

        var changes = PluginSettingsMapper.Changes(Form, "Acme", Values(("Rows", new object[]
        {
            new { Name = "b2", Token = "", _keep = 1 }, // the second row, renamed, keeps its password
            new { Name = "new", Token = "pn" },
            new { Name = "", Token = "" }, // nobody filled it in
            new { Name = "bad", Token = "", _keep = 7 }, // a place that was never there keeps nothing
        })), current);

        var rows = ((JsonArray)changes["Acme:Rows"]!).Select(r => (string?)r!["Name"] + "/" + (string?)r["Token"]).ToList();
        Assert.Equal(["b2/pb", "new/pn", "bad/"], rows);
    }

    [Fact]
    public void A_list_that_is_the_same_is_no_change_and_an_empty_list_clears_it()
    {
        var current = Section(("Acme:Rows:0:Name", "a"), ("Acme:Rows:0:Token", "pa"));

        Assert.Empty(PluginSettingsMapper.Changes(Form, "Acme", Values(("Rows", new object[] { new { Name = "a", Token = "", _keep = 0 } })), current));
        Assert.Empty(((JsonArray)PluginSettingsMapper.Changes(Form, "Acme", Values(("Rows", Array.Empty<object>())), current)["Acme:Rows"]!));
        Assert.Empty(PluginSettingsMapper.Changes(Form, "Acme", Values(("Rows", "not a list")), current)); // only an array is a list
    }

    [Fact]
    public void The_stored_passwords_are_found_by_place()
    {
        var has = PluginSettingsMapper.HasSecret(Form, Section(("Acme:ApiKey", "k"), ("Acme:Rows:0:Token", "t")));

        Assert.True(has("ApiKey"));
        Assert.True(has("Rows/0/Token"));
        Assert.False(has("Rows/1/Token"));
        Assert.False(has("Other"));
    }

    [Fact]
    public void The_secret_places_of_a_plugin_are_places_in_the_settings_file()
    {
        var installed = new InstalledPlugin(
            new StoredPlugin("acme", "2.1.0", "x", DateTimeOffset.UtcNow, "h", "f"), "dir", PluginTestKit.AcmeManifest,
            new Dictionary<string, PluginUiLayout> { ["ui/settings.json"] = Form });

        Assert.Equal(["Plugins:acme:ApiKey", "Plugins:acme:Rows:*:Token"], PluginSecrets.Of(installed));
    }
}
