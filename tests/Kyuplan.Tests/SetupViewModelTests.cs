using Kyuplan.Client.Api;
using Kyuplan.Client.Localization;
using Kyuplan.Client.Resources;
using Kyuplan.Client.ViewModels;
using Kyuplan.Contracts;
using static Kyuplan.Tests.TestAccounts;

namespace Kyuplan.Tests;

/// <summary>The setup screen and the settings page: what they ask, when they let the user save, and how they wait for the API to restart.</summary>
public sealed class SetupViewModelTests : IDisposable
{
    private readonly FakePlannerApi _api = new() { NeedsSetup = true };

    public void Dispose()
    {
        // Selecting a language applies its culture process-wide; do not leak it into other tests.
        System.Globalization.CultureInfo.DefaultThreadCurrentCulture = null;
        System.Globalization.CultureInfo.DefaultThreadCurrentUICulture = null;
    }

    private ApiRestartWatcher Watcher() => new(_api) { Delay = (_, _) => Task.CompletedTask };

    private SetupViewModel Setup() => new(_api, Watcher());

    private static async Task<SetupViewModel> CheckedAsync(SetupViewModel vm)
    {
        await vm.CheckAsync();
        return vm;
    }

    private static void FillAdmin(SetupViewModel vm, string password = "a good password")
    {
        vm.AdminUserName = "boss";
        vm.AdminPassword = password;
        vm.AdminPasswordRepeat = password;
    }

    [Fact]
    public async Task The_form_starts_with_what_the_api_runs_with()
    {
        var vm = await CheckedAsync(Setup());

        Assert.True(vm.IsChecked);
        Assert.True(vm.NeedsSetup);
        Assert.Equal("data/kyuplan.db", vm.Form.DatabasePath);
        Assert.Equal("Europe/Berlin", vm.Form.TimeZone);
    }

    [Fact]
    public async Task An_api_that_is_set_up_needs_nothing()
    {
        _api.NeedsSetup = false;

        var vm = await CheckedAsync(Setup());

        Assert.True(vm.IsChecked);
        Assert.False(vm.NeedsSetup);
    }

    [Fact]
    public async Task A_failed_question_is_an_error_and_does_not_claim_the_api_needs_a_setup()
    {
        _api.FailWith = new PlannerApiException("Not reachable.", null);

        var vm = await CheckedAsync(Setup());

        Assert.False(vm.IsChecked);
        Assert.False(vm.NeedsSetup);
        Assert.Equal("Not reachable.", vm.ErrorMessage);
    }

    [Fact]
    public async Task Finishing_needs_a_good_administrator_and_valid_settings()
    {
        var vm = await CheckedAsync(Setup());
        Assert.False(vm.FinishCommand.CanExecute(null)); // no password yet

        FillAdmin(vm, "short");
        Assert.False(vm.FinishCommand.CanExecute(null));
        Assert.NotNull(vm.AdminPasswordProblem);

        FillAdmin(vm);
        Assert.True(vm.FinishCommand.CanExecute(null));

        vm.AdminPasswordRepeat = "something else";
        Assert.False(vm.FinishCommand.CanExecute(null));
        Assert.Equal(Strings.Setup_PasswordsDiffer, vm.RepeatProblem);

        FillAdmin(vm);
        vm.Form.AccountsDatabasePath = vm.Form.DatabasePath;
        Assert.False(vm.FinishCommand.CanExecute(null));
        Assert.Equal(Strings.Settings_DatabasesSame, vm.Form.AccountsDatabasePathProblem);
    }

    [Fact]
    public async Task The_form_of_a_plugin_data_source_is_checked_with_the_rules_the_plugin_gave_it()
    {
        _api.PluginCatalog = PluginTestKit.AcmeCatalog();
        var vm = await CheckedAsync(Setup());
        vm.Form.DataSource = "acme";
        Assert.Equal(Strings.Plugin_FieldRequired, vm.Form.PluginFormProblem); // the API key is required

        var apiKey = vm.Form.PluginForm!.Fields.Single(f => f.Id == "ApiKey");
        apiKey.Text = "secret";
        Assert.Null(vm.Form.PluginFormProblem);

        apiKey.Text = "";
        vm.Form.UseDemoData = true; // the demo does not need the plugin's settings
        Assert.Null(vm.Form.PluginFormProblem);
    }

    [Fact]
    public async Task Finishing_sends_the_settings_and_the_administrator_and_is_done_when_nothing_restarts()
    {
        _api.SaveRestarts = false;
        var vm = await CheckedAsync(Setup());
        FillAdmin(vm);
        vm.Form.TimeZone = "UTC";

        await vm.FinishCommand.ExecuteAsync(null);

        var call = Assert.Single(_api.SetupCalls);
        Assert.Equal("boss", call.AdminUserName);
        Assert.Equal("a good password", call.AdminPassword);
        Assert.Equal("UTC", call.Settings.TimeZone);
        Assert.Null(call.Settings.PluginSettings); // the data source has no plugin form
        Assert.True(vm.IsDone);
        Assert.False(vm.FinishCommand.CanExecute(null)); // no second setup
    }

    [Fact]
    public async Task When_the_api_restarts_the_screen_waits_until_it_answers_again()
    {
        var vm = await CheckedAsync(Setup());
        FillAdmin(vm);
        _api.SetupQuestionsFailing = 3; // the API is down for three questions
        var questionsBefore = _api.SetupQuestions;
        var states = new List<bool>();
        vm.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(SetupViewModel.IsRestarting)) states.Add(vm.IsRestarting);
        };

        await vm.FinishCommand.ExecuteAsync(null);

        Assert.Equal([true, false], states);
        Assert.Equal(4, _api.SetupQuestions - questionsBefore); // three that failed, one that worked
        Assert.True(vm.IsDone);
        Assert.Null(vm.ErrorMessage);
    }

    [Fact]
    public async Task An_api_that_does_not_come_back_is_reported_and_the_screen_is_not_done()
    {
        var vm = await CheckedAsync(Setup());
        FillAdmin(vm);
        _api.SetupQuestionsFailing = int.MaxValue;

        await vm.FinishCommand.ExecuteAsync(null);

        Assert.False(vm.IsDone);
        Assert.False(vm.IsRestarting);
        Assert.Equal(Strings.Setup_NotBack, vm.ErrorMessage);
    }

    [Fact]
    public async Task A_refused_setup_keeps_what_was_typed_and_says_why()
    {
        var vm = await CheckedAsync(Setup());
        FillAdmin(vm);
        _api.NeedsSetup = false; // somebody else was faster

        await vm.FinishCommand.ExecuteAsync(null);

        Assert.False(vm.IsDone);
        Assert.Equal("The API is set up already.", vm.ErrorMessage);
        Assert.Equal("boss", vm.AdminUserName);
    }

    // --- The settings page -------------------------------------------------------------------------------------

    private SettingsViewModel Page(UserDto? user = null)
    {
        _api.NeedsSetup = false;
        _api.Account = user ?? Dto(1, "admin", "admin", [], isAdmin: true);
        var language = new LanguageService();
        language.Select("en");
        return new SettingsViewModel(new ShellViewModel(_api, language, new LoginSession()), _api, Watcher());
    }

    [Fact]
    public async Task The_page_shows_the_settings_to_a_user_who_may_see_them_and_offers_no_save_before_something_changed()
    {
        var page = Page();

        await page.ActivateAsync();

        Assert.True(page.IsLoaded);
        Assert.Equal("Europe/Berlin", page.Form.TimeZone);
        Assert.False(page.CanSave);

        page.Form.TimeZone = "UTC";
        Assert.True(page.CanSave);

        page.Form.TimeZone = "Europe/Berlin"; // typed back: nothing to save again
        Assert.False(page.CanSave);

        page.Form.TimeZone = "UTC";
        Assert.True(page.SaveCommand.CanExecute(null));
    }

    [Fact]
    public async Task A_user_who_may_only_look_cannot_save_and_one_who_may_not_look_loads_nothing()
    {
        var viewer = Page(WithPermissions(2, "viewer", Permission.SettingsView));
        await viewer.ActivateAsync();
        viewer.Form.TimeZone = "UTC";

        Assert.True(viewer.IsLoaded);
        Assert.False(viewer.CanEdit);
        Assert.False(viewer.CanSave);

        var nobody = Page(Nobody(3, "nobody"));
        await nobody.ActivateAsync();

        Assert.False(nobody.IsLoaded);
        Assert.False(nobody.CanView);
    }

    [Fact]
    public async Task An_invalid_form_cannot_be_saved()
    {
        var page = Page();
        await page.ActivateAsync();

        page.Form.DefaultMinAttendancePercent = 120;

        Assert.Equal(Strings.Settings_PercentInvalid, page.Form.PercentProblem);
        Assert.False(page.CanSave);
    }

    [Fact]
    public async Task Saving_something_that_restarts_the_api_waits_for_it_and_reads_the_settings_again()
    {
        var page = Page();
        await page.ActivateAsync();
        page.Form.ExamKeyword = "Exam";
        _api.SetupQuestionsFailing = 2;

        await page.SaveCommand.ExecuteAsync(null);

        Assert.Equal("Exam", Assert.Single(_api.SettingsSaves).ExamKeyword);
        Assert.False(page.IsRestarting);
        Assert.Equal(Strings.Settings_Restarted, page.NoticeText);
        Assert.False(page.CanSave); // what is shown is what the API runs with now
    }

    [Fact]
    public async Task Saving_something_the_api_applies_without_a_restart_does_not_wait()
    {
        _api.SaveRestarts = false;
        var page = Page();
        await page.ActivateAsync();
        page.Form.ExamKeyword = "Exam";
        var questionsBefore = _api.SetupQuestions;

        await page.SaveCommand.ExecuteAsync(null);

        Assert.Equal(questionsBefore, _api.SetupQuestions);
        Assert.Equal(Strings.Settings_NothingChanged, page.NoticeText);
    }

    [Fact]
    public async Task A_refused_save_keeps_what_was_typed_and_shows_the_reason()
    {
        var page = Page();
        await page.ActivateAsync();
        page.Form.ExamKeyword = "Exam";
        _api.FailWith = new PlannerApiException("The database cannot be opened.", System.Net.HttpStatusCode.BadRequest);

        await page.SaveCommand.ExecuteAsync(null);

        Assert.Equal("The database cannot be opened.", page.ErrorMessage);
        Assert.Equal("Exam", page.Form.ExamKeyword);
        Assert.False(page.IsRestarting);
    }

    [Fact]
    public void The_metrics_form_needs_a_token_when_they_are_on_and_sends_it_only_when_typed()
    {
        var form = new SettingsForm();
        form.Load(FakePlannerApi.DefaultSettings);
        Assert.False(form.MetricsEnabled);
        Assert.Null(form.MetricsTokenProblem);
        Assert.False(form.ToInput().MetricsEnabled!.Value);
        Assert.Null(form.ToInput().MetricsToken);

        form.MetricsEnabled = true;
        Assert.Equal(Strings.Settings_MetricsTokenMissing, form.MetricsTokenProblem);
        Assert.False(form.IsValid);

        form.MetricsToken = "short";
        Assert.NotNull(form.MetricsTokenProblem);

        form.MetricsToken = "scrape-token-0123456789";
        Assert.Null(form.MetricsTokenProblem);
        Assert.Equal("scrape-token-0123456789", form.ToInput().MetricsToken);
        Assert.False(form.Matches(FakePlannerApi.DefaultSettings));

        form.Load(FakePlannerApi.DefaultSettings with { MetricsEnabled = true, HasMetricsToken = true });
        Assert.Null(form.MetricsTokenProblem); // the stored token is enough
        Assert.Equal("", form.MetricsToken);
        Assert.True(form.Matches(FakePlannerApi.DefaultSettings with { MetricsEnabled = true, HasMetricsToken = true }));
    }

    private static SettingsForm CustomForm(params string[] stored)
    {
        var form = new SettingsForm();
        form.Load(FakePlannerApi.DefaultSettings with { DataSource = DataSourceKinds.Custom, UseDemoData = false, CustomScript = "function getGroups() { return []; }", ScriptSecrets = stored });
        return form;
    }

    [Fact]
    public void The_stored_script_secrets_are_listed_by_name_and_their_values_are_sent_only_when_typed()
    {
        var form = CustomForm("API_TOKEN", "Other");

        Assert.Equal(["API_TOKEN", "Other"], form.ScriptSecrets.Select(s => s.Name));
        Assert.All(form.ScriptSecrets, s => Assert.True(s.HasValue));
        Assert.All(form.ScriptSecrets, s => Assert.Equal("", s.Value));
        Assert.Null(form.ScriptSecretsProblem);
        Assert.True(form.Matches(FakePlannerApi.DefaultSettings with { DataSource = DataSourceKinds.Custom, UseDemoData = false, CustomScript = form.CustomScript, ScriptSecrets = ["other", "API_TOKEN"] }));
        Assert.Equal(new[] { new ScriptSecretInputDto("API_TOKEN", null), new ScriptSecretInputDto("Other", null) }, form.ToInput().ScriptSecrets);

        form.ScriptSecrets[0].Value = "new-value-0123";
        Assert.Equal(new ScriptSecretInputDto("API_TOKEN", "new-value-0123"), form.ToInput().ScriptSecrets![0]);
        Assert.False(form.Matches(FakePlannerApi.DefaultSettings with { DataSource = DataSourceKinds.Custom, UseDemoData = false, CustomScript = form.CustomScript, ScriptSecrets = ["API_TOKEN", "Other"] }));
    }

    [Fact]
    public void Script_secrets_are_added_removed_and_checked_while_they_are_typed()
    {
        var form = CustomForm("API_TOKEN");
        var changes = new List<string?>();
        form.PropertyChanged += (_, e) => changes.Add(e.PropertyName);

        form.AddScriptSecretCommand.Execute(null);
        var added = form.ScriptSecrets[^1];
        Assert.Contains(nameof(SettingsForm.ScriptSecrets), changes);
        Assert.True(added.IsEmpty);
        Assert.Null(form.ScriptSecretsProblem); // an empty row is nothing
        Assert.Single(form.ToInput().ScriptSecrets!);

        Assert.Contains(nameof(SettingsForm.ScriptSecretsProblem), changes); // the Linux label watches the problem by name
        changes.Clear();
        added.Name = "1bad";
        Assert.Contains(nameof(SettingsForm.ScriptSecretsProblem), changes);
        Assert.Equal(Strings.Settings_ScriptSecretsInvalid, form.ScriptSecretsProblem);
        Assert.False(form.IsValid);

        added.Name = "New_One";
        Assert.True(added.IsInvalid); // a new secret needs a value
        added.Value = "line\nbreak";
        Assert.True(added.IsInvalid);
        added.Value = "good-value-0123";
        Assert.Null(form.ScriptSecretsProblem);
        Assert.True(form.IsValid);
        Assert.False(form.Matches(FakePlannerApi.DefaultSettings with { DataSource = DataSourceKinds.Custom, UseDemoData = false, CustomScript = form.CustomScript, ScriptSecrets = ["API_TOKEN"] }));

        added.Name = "api_token"; // the same name as a stored one, written otherwise
        Assert.Equal(Strings.Settings_ScriptSecretsInvalid, form.ScriptSecretsProblem);

        form.RemoveScriptSecretCommand.Execute(added);
        Assert.Null(form.ScriptSecretsProblem);
        Assert.Single(form.ScriptSecrets);
    }

    [Fact]
    public void A_renamed_stored_secret_has_no_value_any_more_and_needs_one()
    {
        var form = CustomForm("API_TOKEN");
        var secret = form.ScriptSecrets[0];

        secret.Name = "api_token"; // the case does not matter
        Assert.True(secret.HasValue);
        Assert.False(secret.IsInvalid);

        secret.Name = "TOKEN_2";
        Assert.False(secret.HasValue);
        Assert.True(secret.IsInvalid);
        Assert.Equal(Strings.Settings_ScriptSecretsInvalid, form.ScriptSecretsProblem);

        secret.Value = "typed-value-0123";
        Assert.Null(form.ScriptSecretsProblem);
    }

    [Fact]
    public void There_are_at_most_as_many_script_secrets_as_the_api_takes()
    {
        var form = CustomForm();
        for (var i = 0; i <= SettingsRules.MaxScriptSecrets; i++)
        {
            form.AddScriptSecretCommand.Execute(null);
            form.ScriptSecrets[^1].Name = "S" + i;
            form.ScriptSecrets[^1].Value = "value-" + i;
            Assert.Equal(i < SettingsRules.MaxScriptSecrets, form.ScriptSecretsProblem is null);
        }
    }

    [Fact]
    public void Another_data_source_neither_checks_nor_sends_the_script_secrets()
    {
        var form = CustomForm("API_TOKEN");
        form.AddScriptSecretCommand.Execute(null);
        form.ScriptSecrets[^1].Name = "1bad";
        Assert.NotNull(form.ScriptSecretsProblem);

        form.DataSource = DataSourceKinds.Spond;

        Assert.Null(form.ScriptSecretsProblem);
        Assert.Null(form.ToInput().ScriptSecrets); // the stored ones stay
    }

    [Fact]
    public void A_script_with_a_token_in_it_is_warned_about_but_can_still_be_saved()
    {
        var form = CustomForm();
        Assert.Null(form.CustomScriptWarning);

        form.CustomScript = "function getGroups() { return http.get(url, { Authorization: 'Bearer abcdef0123456789' }).json(); }";
        Assert.Equal(Strings.Settings_ScriptCredentialsWarning, form.CustomScriptWarning);
        Assert.True(form.IsValid);

        form.CustomScript = "function getGroups() { return http.get(url, { Authorization: 'Bearer ' + secrets.get('T') }).json(); }";
        Assert.Null(form.CustomScriptWarning);

        form.CustomScript = "var token = 'abcdef0123456789';";
        form.DataSource = DataSourceKinds.Spond;
        Assert.Null(form.CustomScriptWarning); // the script is not in use
    }

    [Fact]
    public void A_script_that_was_kept_back_is_neither_checked_compared_nor_sent_back()
    {
        var hidden = FakePlannerApi.DefaultSettings with { DataSource = DataSourceKinds.Custom, UseDemoData = false, CustomScript = "", CustomScriptHidden = true, ScriptSecrets = ["API_TOKEN"] };
        var form = new SettingsForm();

        form.Load(hidden);

        Assert.True(form.CustomScriptHidden);
        Assert.Null(form.CustomScriptProblem); // an empty script is not a mistake here
        Assert.Null(form.CustomScriptWarning);
        Assert.Null(form.ToInput().CustomScript); // it would erase the script
        Assert.True(form.Matches(hidden));

        form.Load(hidden with { CustomScriptHidden = false, CustomScript = "function getGroups() { return []; }" });
        Assert.False(form.CustomScriptHidden);
        Assert.Equal("function getGroups() { return []; }", form.ToInput().CustomScript);
    }

    [Fact]
    public void The_example_script_cannot_be_inserted_while_the_script_is_kept_back()
    {
        var form = new SettingsForm();
        form.Load(FakePlannerApi.DefaultSettings with { DataSource = DataSourceKinds.Custom, UseDemoData = false, CustomScript = "", CustomScriptHidden = true });
        Assert.False(form.InsertScriptTemplateCommand.CanExecute(null));

        form.Load(FakePlannerApi.DefaultSettings with { DataSource = DataSourceKinds.Custom, UseDemoData = false, CustomScript = "", CustomScriptHidden = false });
        Assert.True(form.InsertScriptTemplateCommand.CanExecute(null));
        form.InsertScriptTemplateCommand.Execute(null);
        Assert.Equal(CustomScriptTemplate.Text, form.CustomScript);
        Assert.Null(form.CustomScriptWarning); // the shipped example holds no credentials itself
    }

    [Theory]
    [InlineData("", true)]
    [InlineData("https://planner.example.org/kyuplan", true)]
    [InlineData("ftp://planner.example.org", false)]
    [InlineData("not a url", false)]
    public void The_public_url_is_checked_trimmed_loaded_and_compared(string url, bool valid)
    {
        var form = new SettingsForm();
        form.Load(FakePlannerApi.DefaultSettings with { PublicUrl = null });
        Assert.Equal("", form.PublicUrl);
        Assert.True(form.Matches(FakePlannerApi.DefaultSettings with { PublicUrl = null }));

        form.PublicUrl = "  " + url + "  ";
        Assert.Equal(valid, form.PublicUrlProblem is null);
        Assert.Equal(valid, form.IsValid);
        if (!valid) Assert.Equal(Strings.Settings_PublicUrlInvalid, form.PublicUrlProblem);
        Assert.Equal(url, form.ToInput().PublicUrl);
        Assert.Equal(url.Length == 0, form.Matches(FakePlannerApi.DefaultSettings with { PublicUrl = "" }));

        form.Load(FakePlannerApi.DefaultSettings with { PublicUrl = "https://a.example" });
        Assert.Equal("https://a.example", form.PublicUrl);
        Assert.True(form.Matches(FakePlannerApi.DefaultSettings with { PublicUrl = "https://a.example" }));
    }
}
