using System.Globalization;
using System.Text.Json;
using Kyuplan.Client.Api;
using Kyuplan.Client.Localization;
using Kyuplan.Client.Resources;
using Kyuplan.Client.ViewModels;
using Kyuplan.Contracts;
using static Kyuplan.Tests.TestAccounts;

namespace Kyuplan.Tests;

/// <summary>The form a plugin describes in JSON, as the clients' view models make of it: what is typed, what is wrong with it, what is sent.</summary>
[Collection(LanguageStateCollection.Name)]
public sealed class PluginFormViewModelTests : IDisposable
{
    public PluginFormViewModelTests() => new LanguageService().Select("en");

    public void Dispose()
    {
        CultureInfo.DefaultThreadCurrentCulture = null;
        CultureInfo.DefaultThreadCurrentUICulture = null;
    }

    private static PluginFormViewModel Acme() => new(PluginTestKit.Layout(PluginTestKit.AcmeForm));

    private static PluginFieldViewModel Field(PluginFormViewModel form, string id) => form.Fields.Single(f => f.Id == id);

    private static PluginListViewModel Rows(PluginFormViewModel form) => form.Items.OfType<PluginListViewModel>().Single();

    private static PluginSettingsDto Stored(params (string Key, object? Value)[] values) =>
        new(PluginTestKit.Values(values), []);

    [Fact]
    public void A_new_form_starts_with_the_defaults_of_its_layout()
    {
        var form = Acme();

        Assert.Equal("eu", Field(form, "Region").Text);
        Assert.Equal("2", Field(form, "Retries").Text);
        Assert.False(Field(form, "Verbose").IsChecked);
        Assert.Equal("", Field(form, "Notes").Text);
        Assert.Empty(Rows(form).Rows);
        Assert.False(form.IsChanged);
    }

    [Fact]
    public void The_items_follow_the_layout_in_order_and_each_field_knows_its_kind()
    {
        var form = Acme();

        Assert.Equal(["heading", "field", "field", "field", "field", "field", "list"], form.Items.Select(i => i.Kind));
        Assert.True(((PluginTextViewModel)form.Items[0]).IsHeading);
        Assert.True(Field(form, "ApiKey").IsPassword);
        Assert.True(Field(form, "Region").IsSelect);
        Assert.True(Field(form, "Retries").IsNumber);
        Assert.True(Field(form, "Verbose").IsCheckbox);
        Assert.True(Field(form, "Notes").IsMultiline);
        Assert.False(Field(form, "Retries").IsText && Field(form, "Verbose").IsText);
        Assert.Equal((0, 5), (Field(form, "Retries").Min, Field(form, "Retries").Max));
    }

    [Fact]
    public void A_required_field_is_a_problem_until_it_has_a_value_and_so_is_a_value_that_does_not_fit()
    {
        var form = Acme();
        var key = Field(form, "ApiKey");

        Assert.Equal(Strings.Plugin_FieldRequired, key.Problem);
        Assert.Equal(Strings.Plugin_FieldRequired, form.Problem);
        Assert.False(form.IsValid);

        key.Text = "secret";
        Assert.Null(form.Problem);

        Field(form, "Retries").Text = "9";
        Assert.Equal(Strings.Plugin_FieldInvalid, form.Problem);
        Field(form, "Retries").Text = "abc";
        Assert.Equal(Strings.Plugin_FieldInvalid, form.Problem);
        Field(form, "Retries").Text = "";
        Assert.Null(form.Problem);
    }

    [Fact]
    public void A_stored_password_counts_as_the_required_value_and_says_so()
    {
        var form = Acme();

        form.Load(new PluginSettingsDto(PluginTestKit.Values(("Region", "us")), ["ApiKey"]));

        var key = Field(form, "ApiKey");
        Assert.True(key.HasStoredSecret);
        Assert.Null(key.Problem);
        Assert.Equal(Strings.Plugin_SecretStored, key.StoredSecretHint);
        Assert.Equal("", key.Text); // the stored one is never shown
        Assert.False(form.IsChanged);

        key.Text = "new";
        Assert.True(form.IsChanged); // typing a password is a change
    }

    [Fact]
    public void Loading_puts_the_stored_values_in_and_what_is_typed_is_sent_back_by_field_id()
    {
        var form = Acme();

        form.Load(Stored(("Region", "us"), ("Retries", 4), ("Verbose", true), ("Notes", "a\nb"), ("Rows", new object[] { new { Name = "one", _keep = 0 } })));
        Field(form, "ApiKey").Text = "typed";
        var values = form.Values();

        Assert.Equal(("us", 4d, true, "a\nb", "typed"), (values["Region"].GetString(), values["Retries"].GetDouble(), values["Verbose"].GetBoolean(), values["Notes"].GetString(), values["ApiKey"].GetString()));
        Assert.Equal("one", values["Rows"][0].GetProperty("Name").GetString());
        Assert.Equal(0, values["Rows"][0].GetProperty(PluginRules.KeepProperty).GetInt32());
    }

    [Fact]
    public void A_value_is_changed_when_it_differs_from_what_was_loaded_and_not_when_it_is_typed_back()
    {
        var form = Acme();
        form.Load(Stored(("Region", "us")));
        var changes = 0;
        form.ValueChanged += (_, _) => changes++;

        Field(form, "Region").Text = "eu";
        Assert.True(form.IsChanged);
        Field(form, "Region").Text = "us";
        Assert.False(form.IsChanged);
        Field(form, "Verbose").IsChecked = true;
        Assert.True(form.IsChanged);

        Assert.True(changes >= 3);
    }

    [Fact]
    public void A_text_is_trimmed_when_it_is_sent_but_a_password_and_a_multiline_text_are_not()
    {
        var form = Acme();
        Field(form, "ApiKey").Text = " pass ";
        Field(form, "Notes").Text = " note \n";
        Field(form, "Region").Text = "eu";

        var values = form.Values();

        Assert.Equal(" pass ", values["ApiKey"].GetString());
        Assert.Equal(" note \n", values["Notes"].GetString());
    }

    [Fact]
    public void A_number_is_typed_with_a_point_and_sent_as_a_number_and_an_empty_box_is_none()
    {
        var form = Acme();
        var retries = Field(form, "Retries");

        retries.Text = "3.5";
        Assert.Equal(3.5, form.Values()["Retries"].GetDouble());
        retries.Text = "";
        Assert.Equal(JsonValueKind.Null, form.Values()["Retries"].ValueKind);
        retries.Text = "x";
        Assert.Equal(JsonValueKind.String, form.Values()["Retries"].ValueKind); // not a number: the rules say so
    }

    [Fact]
    public void The_numbers_of_a_german_client_are_still_typed_with_a_point()
    {
        new LanguageService().Select("de");
        var form = Acme();

        form.Load(Stored(("Retries", 2.5)));

        Assert.Equal("2.5", Field(form, "Retries").Text);
        Assert.Equal(2.5, form.Values()["Retries"].GetDouble());
    }

    [Fact]
    public void A_select_offers_its_options_and_a_choice_is_its_value()
    {
        var region = Field(Acme(), "Region");

        Assert.Equal(["Europe", "America"], region.Choices.Select(c => c.Label));
        Assert.Equal("Europe", region.SelectedChoice!.Label);

        region.SelectedChoice = region.Choices[1];
        Assert.Equal("us", region.Text);
        region.SelectedChoice = null;
        Assert.Equal("", region.Text);
        Assert.Null(region.SelectedChoice);
        Assert.Equal("Europe", region.Choices[0].ToString());
    }

    [Fact]
    public void The_texts_are_german_in_a_german_client_and_the_english_ones_where_there_is_no_german_text()
    {
        var form = Acme();
        Assert.Equal("Europe", Field(form, "Region").Choices[0].Label);

        new LanguageService().Select("de");
        form.LanguageChanged();

        Assert.Equal("Europa", Field(form, "Region").Choices[0].Label);
        Assert.Equal("America", Field(form, "Region").Choices[1].Label); // no German text: the English one
        Assert.Equal("API key", Field(form, "ApiKey").Label);
    }

    [Fact]
    public void A_list_adds_and_removes_rows_up_to_its_maximum_and_a_new_row_has_the_defaults()
    {
        var rows = Rows(Acme());

        Assert.True(rows.CanAdd);
        for (var i = 0; i < 3; i++) rows.AddRowCommand.Execute(null);
        Assert.False(rows.CanAdd);
        Assert.False(rows.AddRowCommand.CanExecute(null));
        Assert.Equal(3, rows.Rows.Count);
        Assert.All(rows.Rows, row => Assert.Null(row.KeepIndex));

        rows.RemoveRowCommand.Execute(rows.Rows[1]);
        rows.RemoveRowCommand.Execute(null);
        Assert.Equal(2, rows.Rows.Count);
        Assert.True(rows.CanAdd);
    }

    [Fact]
    public void A_row_nobody_filled_in_is_not_sent_and_a_row_that_was_there_keeps_its_place_and_password()
    {
        var form = Acme();
        form.Load(new PluginSettingsDto(PluginTestKit.Values(("Region", "eu"), ("Rows", new object[] { new { Name = "a", _keep = 0 }, new { Name = "b", _keep = 1 } })), ["ApiKey", "Rows/0/Token", "Rows/1/Token"]));
        var rows = Rows(form);

        Assert.All(rows.Rows.SelectMany(r => r.Fields.Where(f => f.IsPassword)), f => Assert.True(f.HasStoredSecret));
        rows.AddRowCommand.Execute(null); // empty
        rows.RemoveRowCommand.Execute(rows.Rows[0]);

        var sent = form.Values()["Rows"];
        Assert.Equal(1, sent.GetArrayLength());
        Assert.Equal(1, sent[0].GetProperty(PluginRules.KeepProperty).GetInt32()); // the row that was the second one of the stored list
        Assert.Equal("b", sent[0].GetProperty("Name").GetString());
        Assert.True(rows.Rows[0].IsEmpty == false && rows.Rows[1].IsEmpty);
    }

    [Fact]
    public void A_problem_in_a_row_is_a_problem_of_the_form_unless_the_row_is_empty()
    {
        var form = Acme();
        Field(form, "ApiKey").Text = "k";
        var rows = Rows(form);
        rows.AddRowCommand.Execute(null);
        Assert.Null(form.Problem);

        rows.Rows[0].Fields[0].Text = new string('x', PluginRules.MaxLabelLength + 1);

        Assert.Equal(Strings.Plugin_FieldInvalid, rows.Problem);
        Assert.Equal(Strings.Plugin_FieldInvalid, form.Problem);
    }

    [Fact]
    public void The_texts_of_a_list_follow_the_language_too()
    {
        var form = Acme();
        var rows = Rows(form);
        rows.AddRowCommand.Execute(null);
        var changes = new List<string?>();
        rows.PropertyChanged += (_, e) => changes.Add(e.PropertyName);

        rows.LanguageChanged();

        Assert.Contains(string.Empty, changes);
        Assert.Equal("Rows", rows.Label);
        Assert.Null(rows.Hint);
    }

    [Theory]
    [InlineData("heading", true)]
    [InlineData("text", false)]
    public void A_heading_and_a_paragraph_say_their_text(string type, bool heading)
    {
        var item = new PluginTextViewModel(new PluginUiElement(type, Text: "Hello", TextDe: "Hallo"));

        Assert.Equal((type, heading, "Hello"), (item.Kind, item.IsHeading, item.Text));
    }

    [Fact]
    public void A_table_shows_the_rows_of_the_answer_as_text_and_says_when_it_is_empty()
    {
        var page = PluginTestKit.Layout(PluginTestKit.AcmePage).Elements[2];
        var table = new PluginTableViewModel(page);

        Assert.True(table.IsEmpty);
        Assert.Equal("Nothing yet.", table.EmptyText);
        Assert.Equal(["Name", "Count", "Done"], table.Headers);
        Assert.Equal("Items", table.Title);

        table.Load(JsonSerializer.SerializeToElement(new object[] { new { name = "Alpha", count = 1, done = true }, new { name = "Beta", count = 2.5, done = false }, new { name = (string?)null }, "not an object" }));

        Assert.False(table.IsEmpty);
        Assert.Equal(["Alpha", "1", Strings.Plugin_Yes], table.Rows[0]);
        Assert.Equal(["Beta", "2.5", Strings.Plugin_No], table.Rows[1]);
        Assert.Equal(["", "", ""], table.Rows[2]); // null and missing properties are empty cells
        Assert.Equal(["", "", ""], table.Rows[3]);

        table.Load(JsonSerializer.SerializeToElement(new { notAnArray = true }));
        Assert.True(table.IsEmpty);
        table.Load(default);
        Assert.True(table.IsEmpty);
    }

    [Fact]
    public void A_table_without_a_text_of_its_own_says_that_there_is_nothing_to_show()
    {
        var table = new PluginTableViewModel(new PluginUiElement("table", Source: "x", Columns: [new PluginUiColumn("a", "A")]));

        Assert.Equal(Strings.Plugin_TableEmpty, table.EmptyText);
        Assert.Null(table.Title);
    }
}

/// <summary>A page a plugin added: what it loads, what its buttons and forms call and how it asks before it does.</summary>
[Collection(LanguageStateCollection.Name)]
public sealed class PluginPageViewModelTests : IDisposable
{
    private readonly FakePlannerApi _api = new();
    private readonly LanguageService _language = new();
    private readonly ShellViewModel _shell;

    public PluginPageViewModelTests()
    {
        _language.Select("en");
        _api.Account = WithPermissions(1, "anna", "acme.view", "acme.edit");
        _api.PluginCatalog = PluginTestKit.AcmeCatalog();
        _api.PluginData["acme/items"] = JsonSerializer.SerializeToElement(new object[] { new { name = "Alpha", count = 1, done = true } });
        _shell = new ShellViewModel(_api, _language);
    }

    public void Dispose()
    {
        CultureInfo.DefaultThreadCurrentCulture = null;
        CultureInfo.DefaultThreadCurrentUICulture = null;
    }

    private async Task<PluginPageViewModel> OpenAsync(string page = "items")
    {
        await _shell.InitializeAsync();
        var vm = _shell.PluginPages.Single(p => p.PageId == page);
        await vm.ActivateAsync();
        return vm;
    }

    [Fact]
    public async Task A_page_reads_its_tables_when_it_is_shown_and_knows_who_it_belongs_to()
    {
        var vm = await OpenAsync();

        Assert.Equal(("acme", "items", "acme/items", "Acme items", "chart"), (vm.PluginId, vm.PageId, vm.Key, vm.Title, vm.Icon));
        var table = Assert.Single(vm.Items.OfType<PluginTableViewModel>());
        Assert.Equal(["Alpha", "1", Strings.Plugin_Yes], Assert.Single(table.Rows));
        Assert.Contains("get:acme/items", _api.PluginCalls);
        Assert.True(vm.CanView);
    }

    [Fact]
    public async Task The_title_is_german_in_a_german_client_and_the_page_builds_headings_texts_tables_buttons_and_forms()
    {
        _language.Select("de");

        var vm = await OpenAsync();

        Assert.Equal("Acme-Dinge", vm.Title);
        Assert.Equal(["heading", "text", "table", "form", "button"], vm.Items.Select(i => i.Kind));
    }

    [Fact]
    public async Task A_form_sends_its_values_to_the_endpoint_of_the_plugin_and_the_tables_are_read_again()
    {
        var vm = await OpenAsync();
        var block = vm.Items.OfType<PluginFormBlockViewModel>().Single();
        Assert.False(block.Submit.CanExecute(null)); // the name is required

        block.Form.Fields.Single().Text = "Gamma";
        Assert.True(block.Submit.CanExecute(null));
        _api.PluginCalls.Clear();
        await block.Submit.ExecuteAsync(null);

        Assert.Equal(["POST:acme/items/add:{\"name\":\"Gamma\"}", "get:acme/items"], _api.PluginCalls);
        Assert.Equal(Strings.Plugin_ActionDone, vm.Notice);
        Assert.True(vm.HasNotice);
        Assert.Equal("Add", block.SubmitLabel);
    }

    [Fact]
    public async Task A_button_that_asks_waits_for_the_answer_and_does_nothing_before_it()
    {
        var vm = await OpenAsync();
        var button = vm.Items.OfType<PluginButtonViewModel>().Single();
        _api.PluginCalls.Clear();

        await button.Command.ExecuteAsync(null);

        Assert.True(vm.IsConfirming);
        Assert.Equal("Really clear?", vm.Confirmation);
        Assert.Empty(_api.PluginCalls); // nothing was called yet

        await vm.ConfirmCommand.ExecuteAsync(null);

        Assert.False(vm.IsConfirming);
        Assert.Equal("", vm.Confirmation);
        Assert.Equal(["DELETE:acme/items:", "get:acme/items"], _api.PluginCalls);
    }

    [Fact]
    public async Task Saying_no_to_the_question_calls_nothing_and_the_question_is_german_in_a_german_client()
    {
        _language.Select("de");
        var vm = await OpenAsync();
        await vm.Items.OfType<PluginButtonViewModel>().Single().Command.ExecuteAsync(null);
        _api.PluginCalls.Clear();

        Assert.Equal("Wirklich leeren?", vm.Confirmation);
        vm.CancelConfirmationCommand.Execute(null);
        await vm.ConfirmCommand.ExecuteAsync(null); // nothing waits any more

        Assert.False(vm.IsConfirming);
        Assert.Empty(_api.PluginCalls);
    }

    [Fact]
    public async Task A_failed_action_shows_the_error_and_no_notice()
    {
        var vm = await OpenAsync();
        var block = vm.Items.OfType<PluginFormBlockViewModel>().Single();
        block.Form.Fields.Single().Text = "x";
        _api.FailWith = new PlannerApiException("The plugin said no.", System.Net.HttpStatusCode.BadRequest);

        await block.Submit.ExecuteAsync(null);

        Assert.Equal("The plugin said no.", vm.ErrorMessage);
        Assert.False(vm.HasNotice);
    }

    [Fact]
    public async Task A_page_is_only_for_those_who_have_its_permission_and_reads_nothing_without_it()
    {
        _api.Account = Nobody(2, "nobody");
        await _shell.InitializeAsync();

        // The catalog the API gave a user without permissions has the pages that need none; the shell also checks.
        Assert.Equal(["about"], _shell.PluginPages.Select(p => p.PageId));
        Assert.Equal("Acme is a test.", ((PluginTextViewModel)_shell.PluginPages[0].Items[0]).Text);
    }

    [Fact]
    public async Task A_page_that_loses_its_permission_clears_its_tables()
    {
        var vm = await OpenAsync();
        Assert.False(vm.Items.OfType<PluginTableViewModel>().Single().IsEmpty);

        _shell.Session.User = Nobody(3, "anna");

        Assert.False(vm.CanView);
        Assert.True(vm.Items.OfType<PluginTableViewModel>().Single().IsEmpty);
    }

    [Fact]
    public async Task A_page_follows_the_language()
    {
        var vm = await OpenAsync();
        var changed = new List<string?>();
        vm.PropertyChanged += (_, e) => changed.Add(e.PropertyName);

        _language.Select("de");

        Assert.Contains(nameof(PluginPageViewModel.Title), changed);
        Assert.Equal("Acme-Dinge", vm.Title);
        Assert.Equal("Dinge", ((PluginTextViewModel)vm.Items[0]).Text);
    }

    [Fact]
    public async Task A_page_can_be_asked_to_try_again()
    {
        var vm = await OpenAsync();
        _api.PluginCalls.Clear();

        await vm.RetryCommand.ExecuteAsync(null);

        Assert.Contains("get:acme/items", _api.PluginCalls);
    }
}

/// <summary>What the shell does with the plugin catalog: pages for the navigation, kept across refreshes.</summary>
[Collection(LanguageStateCollection.Name)]
public sealed class PluginShellTests : IDisposable
{
    private readonly FakePlannerApi _api = new();
    private readonly LanguageService _language = new();

    public PluginShellTests() => _language.Select("en");

    public void Dispose()
    {
        CultureInfo.DefaultThreadCurrentCulture = null;
        CultureInfo.DefaultThreadCurrentUICulture = null;
    }

    [Fact]
    public async Task The_shell_makes_a_page_of_every_page_the_user_may_open()
    {
        _api.Account = WithPermissions(1, "viewer", "acme.view");
        _api.PluginCatalog = PluginTestKit.AcmeCatalog();
        var shell = new ShellViewModel(_api, _language);

        await shell.InitializeAsync();

        Assert.Equal(["acme/items", "acme/about"], shell.PluginPages.Select(p => p.Key));
        Assert.Equal("acme", shell.Plugins!.Plugins.Single().Id);
    }

    [Fact]
    public async Task A_page_that_needs_a_permission_the_user_lacks_is_left_out_even_when_the_catalog_has_it()
    {
        _api.Account = Nobody(1, "nobody");
        _api.PluginCatalog = PluginTestKit.AcmeCatalog(); // a catalog that is too generous: the client checks as well
        var shell = new ShellViewModel(_api, _language);

        await shell.InitializeAsync();

        Assert.Equal(["acme/about"], shell.PluginPages.Select(p => p.Key));
    }

    [Fact]
    public async Task A_refresh_keeps_the_view_model_of_a_page_that_did_not_change_and_makes_a_new_one_for_one_that_did()
    {
        _api.Account = WithPermissions(1, "viewer", "acme.view");
        _api.PluginCatalog = PluginTestKit.AcmeCatalog();
        var shell = new ShellViewModel(_api, _language);
        await shell.InitializeAsync();
        var before = shell.PluginPages.ToList();
        var raised = 0;
        shell.PluginsChanged += (_, _) => raised++;

        await shell.RefreshDataCommand.ExecuteAsync(null);

        Assert.Equal(1, raised);
        Assert.Same(before[0], shell.PluginPages[0]);
        Assert.Same(before[1], shell.PluginPages[1]);

        var changed = PluginTestKit.AcmeCatalog();
        _api.PluginCatalog = new PluginCatalogDto([changed.Plugins[0] with { Pages = [changed.Plugins[0].Pages[0] with { Title = "Renamed" }, changed.Plugins[0].Pages[1]] }]);
        await shell.RefreshPluginsAsync();

        Assert.NotSame(before[0], shell.PluginPages[0]);
        Assert.Equal("Renamed", shell.PluginPages[0].Title);
        Assert.Same(before[1], shell.PluginPages[1]);
    }

    [Fact]
    public async Task Another_user_who_signs_in_gets_the_pages_of_their_permissions()
    {
        _api.Account = WithPermissions(1, "viewer", "acme.view");
        _api.PluginCatalog = PluginTestKit.AcmeCatalog();
        var shell = new ShellViewModel(_api, _language);
        await shell.InitializeAsync();
        Assert.Equal(2, shell.PluginPages.Count);

        shell.Session.User = Nobody(2, "nobody");

        Assert.Equal(["acme/about"], shell.PluginPages.Select(p => p.Key));
    }

    [Fact]
    public async Task A_refresh_that_cannot_reach_the_api_leaves_the_pages_as_they_are()
    {
        _api.Account = WithPermissions(1, "viewer", "acme.view");
        _api.PluginCatalog = PluginTestKit.AcmeCatalog();
        var shell = new ShellViewModel(_api, _language);
        await shell.InitializeAsync();

        _api.FailWith = new PlannerApiException("down", null);
        await shell.RefreshPluginsAsync();

        Assert.Equal(2, shell.PluginPages.Count);
    }

    [Fact]
    public void A_shell_that_has_not_loaded_has_no_plugin_pages()
    {
        var shell = new ShellViewModel(_api, _language);

        Assert.Empty(shell.PluginPages);
        Assert.Null(shell.Plugins);
    }

    [Fact]
    public async Task A_data_source_that_cannot_deliver_the_groups_does_not_hide_the_plugins()
    {
        // No login for Spond yet, a wrong password, Spond is down: the groups fail with 503, and the settings page with the form of the plugin is where it is put right.
        _api.Account = WithPermissions(1, "viewer", "acme.view", Permission.DashboardView);
        _api.PluginCatalog = PluginTestKit.AcmeCatalog();
        _api.GroupsFailWith = new PlannerApiException("Spond credentials are not configured.", System.Net.HttpStatusCode.ServiceUnavailable);
        var shell = new ShellViewModel(_api, _language) { Session = { User = _api.Account } }; // signed in, as the login leaves it

        await shell.InitializeAsync();

        Assert.True(shell.HasError);
        Assert.Equal("acme", shell.Plugins!.Plugins.Single().Id);
        Assert.Equal(["acme/items", "acme/about"], shell.PluginPages.Select(p => p.Key));
    }

    [Fact]
    public async Task A_refresh_after_the_groups_failed_still_has_the_plugins_and_a_later_one_brings_the_groups()
    {
        _api.Account = WithPermissions(1, "viewer", "acme.view", Permission.DashboardView);
        _api.PluginCatalog = PluginTestKit.AcmeCatalog();
        var shell = new ShellViewModel(_api, _language);
        await shell.InitializeAsync();
        Assert.Equal(2, shell.PluginPages.Count);

        _api.PluginCatalog = PluginTestKit.SpondCatalog();
        _api.GroupsFailWith = new PlannerApiException("Could not reach Spond.", System.Net.HttpStatusCode.ServiceUnavailable);
        await shell.RefreshDataCommand.ExecuteAsync(null);

        Assert.True(shell.HasError);
        Assert.Equal("spond", shell.Plugins!.Plugins.Single().Id); // the catalog is the new one although the groups failed

        _api.GroupsFailWith = null;
        await shell.RefreshDataCommand.ExecuteAsync(null);
        Assert.False(shell.HasError);
        Assert.NotEmpty(shell.Groups);
    }

    [Fact]
    public async Task A_catalog_that_cannot_be_read_does_not_keep_the_user_from_the_app()
    {
        _api.Account = WithPermissions(1, "viewer", Permission.DashboardView);
        _api.PluginsFailWith = new PlannerApiException("The plugins are not available.", System.Net.HttpStatusCode.InternalServerError);
        var shell = new ShellViewModel(_api, _language);

        await shell.InitializeAsync();

        Assert.False(shell.HasError);
        Assert.NotEmpty(shell.Groups);
        Assert.Null(shell.Plugins);
        Assert.Empty(shell.PluginPages);

        await shell.RefreshDataCommand.ExecuteAsync(null); // and neither does the refresh
        Assert.False(shell.HasError);
    }

    [Fact]
    public async Task The_settings_page_still_has_the_form_of_the_data_source_when_the_groups_cannot_be_read()
    {
        var api = new FakePlannerApi
        {
            Account = Dto(1, "admin", "admin", [], true), PluginCatalog = PluginTestKit.SpondCatalog(),
            GroupsFailWith = new PlannerApiException("Spond credentials are not configured.", System.Net.HttpStatusCode.ServiceUnavailable),
        };
        api.Settings = FakePlannerApi.DefaultSettings with { UseDemoData = false, DataSource = "spond" };
        var shell = new ShellViewModel(api, new LanguageService()) { Session = { User = api.Account } }; // signed in, as the login leaves it
        var page = new SettingsViewModel(shell, api, new ApiRestartWatcher(api) { Delay = (_, _) => Task.CompletedTask });

        await page.ActivateAsync();

        Assert.False(page.Form.IsMissingPlugin);
        Assert.NotNull(page.Form.PluginForm);
        Assert.Equal("Spond", page.Form.DataSourceText("spond"));
        page.Form.PluginForm!.Fields.Single(f => f.Id == "Email").Text = "me@example.org";
        page.Form.PluginForm.Fields.Single(f => f.Id == "Password").Text = "pw";
        Assert.True(page.CanSave); // the credentials can be typed in and saved although Spond answers nothing
    }
}

/// <summary>The permissions a plugin adds are ticked like the program's own, and the ones of a plugin that is not there are kept.</summary>
[Collection(LanguageStateCollection.Name)]
public sealed class PluginPermissionPickerTests
{
    [Fact]
    public void The_permissions_of_a_plugin_are_listed_under_its_name_after_the_programs_own()
    {
        var picker = new PermissionPickerViewModel();
        var programs = picker.Groups.Count;

        picker.SetPlugins(PluginTestKit.AcmeCatalog());

        var group = picker.Groups[^1];
        Assert.Equal(programs + 1, picker.Groups.Count);
        Assert.Equal("Acme", group.Label);
        Assert.Equal(["acme.view", "acme.edit"], group.Permissions.Select(p => p.Key));
        Assert.Equal(["See the Acme items", "Change the Acme items"], group.Permissions.Select(p => p.Label));
    }

    [Fact]
    public void Ticking_a_permission_of_a_plugin_selects_it_and_what_was_ticked_stays_when_the_catalog_changes()
    {
        var picker = new PermissionPickerViewModel();
        picker.SetPlugins(PluginTestKit.AcmeCatalog());
        picker.Set([Permission.ExamsView, "acme.edit"]);

        picker.SetPlugins(PluginTestKit.AcmeCatalog()); // read again

        Assert.Equal([Permission.ExamsView, "acme.edit"], picker.Selected);
        picker.Groups[^1].Permissions[0].IsSelected = true;
        Assert.Equal([Permission.ExamsView, "acme.view", "acme.edit"], picker.Selected);
    }

    [Fact]
    public void A_permission_of_a_plugin_that_is_not_installed_is_kept_so_that_saving_a_role_does_not_take_it_away()
    {
        var picker = new PermissionPickerViewModel();
        picker.SetPlugins(PluginTestKit.AcmeCatalog());
        picker.Set([Permission.ExamsView, "acme.view", "ghost.view", "rubbish"]);

        picker.SetPlugins(new PluginCatalogDto([])); // the plugin is gone

        Assert.Equal([Permission.ExamsView, "acme.view", "ghost.view"], picker.Selected);
        Assert.DoesNotContain(picker.Groups, g => g.Label == "Acme");
    }

    [Fact]
    public void The_texts_of_a_plugin_are_german_in_a_german_client()
    {
        var language = new LanguageService();
        language.Select("de");
        try
        {
            var picker = new PermissionPickerViewModel();
            picker.SetPlugins(PluginTestKit.AcmeCatalog());

            Assert.Equal("Acme DE", picker.Groups[^1].Label);
            Assert.Equal("Acme-Dinge sehen", picker.Groups[^1].Permissions[0].Label);
            Assert.Equal("Change the Acme items", picker.Groups[^1].Permissions[1].Label); // no German text
        }
        finally
        {
            CultureInfo.DefaultThreadCurrentCulture = null;
            CultureInfo.DefaultThreadCurrentUICulture = null;
        }
    }

    [Fact]
    public void A_plugin_without_permissions_adds_no_group()
    {
        var picker = new PermissionPickerViewModel();

        picker.SetPlugins(PluginTestKit.SpondCatalog());

        Assert.DoesNotContain(picker.Groups, g => g.Label == "Spond");
    }

    [Fact]
    public async Task The_role_form_offers_what_the_plugins_add_and_follows_when_they_change()
    {
        var api = new FakePlannerApi { Account = Dto(1, "admin", "admin", [], true), PluginCatalog = PluginTestKit.AcmeCatalog() };
        var shell = new ShellViewModel(api, new LanguageService());
        var roles = new RolesViewModel(shell, api);
        await shell.InitializeAsync();

        Assert.Equal("Acme", roles.Picker.Groups[^1].Label);

        api.PluginCatalog = new PluginCatalogDto([]);
        await shell.RefreshPluginsAsync();

        Assert.DoesNotContain(roles.Picker.Groups, g => g.Label == "Acme");
    }
}
