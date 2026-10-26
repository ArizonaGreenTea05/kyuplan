using System.Globalization;
using Kyuplan.Client.Localization;
using Kyuplan.Client.Resources;
using Kyuplan.Client.ViewModels;
using Kyuplan.Contracts;

namespace Kyuplan.Tests;

/// <summary>Fields that belong together (a login: the email or the phone number, with the password) in the view models, and the passwords a row of backup logins keeps.</summary>
[Collection(LanguageStateCollection.Name)]
public sealed class PluginTogetherViewModelTests : IDisposable
{
    private const string Login = "A Spond login needs the email (or phone number) and the password.";
    private const string BackupLogin = "A backup Spond login needs the email (or phone number) and the password.";

    private readonly LanguageService _language = new();

    public PluginTogetherViewModelTests() => _language.Select("en");

    public void Dispose()
    {
        CultureInfo.DefaultThreadCurrentCulture = null;
        CultureInfo.DefaultThreadCurrentUICulture = null;
    }

    private static PluginFormViewModel Spond() => new(PluginTestKit.Layout(PluginTestKit.SpondForm));

    private static PluginFieldViewModel Field(PluginFormViewModel form, string id) => form.Fields.Single(f => f.Id == id);

    private static PluginListViewModel Accounts(PluginFormViewModel form) => form.Items.OfType<PluginListViewModel>().Single();

    private static PluginSettingsDto Stored(IEnumerable<string> secrets, params (string Key, object? Value)[] values) => new(PluginTestKit.Values(values), [.. secrets]);

    private static PluginFormViewModel WithBackup(string email = "a@x.org", string phone = "") =>
        Load(Spond(), Stored(["Accounts/0/Password"], ("Accounts", new object[] { new { Email = email, PhoneNumber = phone, _keep = 0 } })));

    private static PluginFormViewModel Load(PluginFormViewModel form, PluginSettingsDto settings)
    {
        form.Load(settings);
        return form;
    }

    [Fact]
    public void A_form_with_no_login_at_all_is_fine_and_half_a_login_is_a_problem_of_the_password_field_and_of_the_form()
    {
        var form = Spond();
        Assert.Null(form.Problem);
        Assert.True(form.IsValid);

        Field(form, "Email").Text = "me@example.org";

        Assert.Equal(Login, Field(form, "Password").Problem);
        Assert.Equal(Login, form.Problem);
        Assert.False(form.IsValid);
        Assert.Null(Field(form, "Email").Problem); // it is shown under the password, where the missing part goes

        Field(form, "Password").Text = "pw";
        Assert.Null(form.Problem);
        Assert.True(form.IsValid);
    }

    [Theory]
    [InlineData("me@example.org", "", "pw", true)]
    [InlineData("", "+491701234567", "pw", true)]
    [InlineData("", "", "", true)]
    [InlineData("me@example.org", "", "", false)]
    [InlineData("", "+491701234567", "", false)]
    [InlineData("", "", "pw", false)]
    [InlineData("  ", "", "pw", false)]
    public void The_view_models_and_the_rules_of_the_api_agree_on_what_a_login_is(string email, string phone, string password, bool fine)
    {
        var form = Spond();
        Field(form, "Email").Text = email;
        Field(form, "PhoneNumber").Text = phone;
        Field(form, "Password").Text = password;
        var layout = PluginTestKit.Layout(PluginTestKit.SpondForm);

        Assert.Equal(fine, form.IsValid);
        Assert.Equal(fine, PluginRules.CheckValues(layout, form.Values(), _ => false) is null);
    }

    [Fact]
    public void Typing_in_a_field_tells_the_field_the_rule_is_on_so_that_its_message_comes_and_goes()
    {
        var form = Spond();
        var password = Field(form, "Password");
        var told = new List<string?>();
        password.PropertyChanged += (_, e) => told.Add(e.PropertyName);

        Field(form, "PhoneNumber").Text = "+491701234567";

        Assert.Contains(nameof(PluginFieldViewModel.Problem), told);
        Assert.Equal(Login, password.Problem);

        told.Clear();
        Field(form, "PhoneNumber").Text = "";
        Assert.Contains(nameof(PluginFieldViewModel.Problem), told);
        Assert.Null(password.Problem);
    }

    [Fact]
    public void A_stored_password_is_a_value_and_alone_it_is_not_a_login()
    {
        var form = Load(Spond(), Stored(["Password"], ("Email", "me@example.org")));

        Assert.Null(form.Problem);
        Assert.True(Field(form, "Password").KeepsStoredSecret);
        Assert.True(Field(form, "Password").HasValue);

        Field(form, "Email").Text = "";

        Assert.Equal(Login, Field(form, "Password").Problem); // nothing for the password to go with
        Field(form, "Email").Text = "other@example.org";
        Assert.Null(Field(form, "Password").Problem); // the main login keeps its password when the email is changed, as before
    }

    [Fact]
    public void The_message_is_in_the_language_of_the_client()
    {
        var form = Spond();
        Field(form, "Email").Text = "me@example.org";

        _language.Select("de");

        Assert.Equal("Ein Spond-Zugang braucht die E-Mail (oder Telefonnummer) und das Passwort.", Field(form, "Password").Problem);
    }

    [Fact]
    public void A_new_backup_row_with_half_a_login_is_a_problem_of_the_row_and_the_list_and_an_empty_one_is_not()
    {
        var form = Spond();
        var accounts = Accounts(form);
        accounts.AddRowCommand.Execute(null);
        Assert.Null(accounts.Problem);

        accounts.Rows[0].Fields[0].Text = "b@x.org";
        Assert.Equal(BackupLogin, accounts.Rows[0].Problem);
        Assert.Equal(BackupLogin, accounts.Problem);
        Assert.Equal(BackupLogin, form.Problem);
        Assert.False(form.IsValid);

        accounts.Rows[0].Fields[2].Text = "pw";
        Assert.Null(form.Problem);

        accounts.AddRowCommand.Execute(null); // the second row stays empty
        Assert.Null(form.Problem);
        Assert.Single(form.Values()["Accounts"].EnumerateArray());
    }

    [Fact]
    public void A_stored_backup_row_keeps_its_password_until_its_login_is_changed_and_gets_it_back_when_changed_back()
    {
        var form = WithBackup();
        var row = Accounts(form).Rows[0];
        var password = row.Fields.Single(f => f.IsPassword);
        Assert.Null(row.Problem);
        Assert.True(password.KeepsStoredSecret);
        Assert.Equal(Strings.Plugin_SecretStored, password.StoredSecretHint);

        row.Fields.Single(f => f.Id == "Email").Text = "other@x.org";

        Assert.True(password.HasStoredSecret); // the API still has one for this place ...
        Assert.False(password.KeepsStoredSecret); // ... but it was for another login
        Assert.Null(password.StoredSecretHint);
        Assert.Equal(BackupLogin, row.Problem);
        Assert.False(form.IsValid);

        row.Fields.Single(f => f.Id == "Email").Text = "a@x.org";
        Assert.True(password.KeepsStoredSecret);
        Assert.Null(form.Problem);
    }

    [Theory]
    [InlineData("Email", "A@X.ORG", true)] // the same email in other letters is the same login
    [InlineData("Email", " a@x.org ", true)] // spaces are not typed in
    [InlineData("Email", "b@x.org", false)]
    [InlineData("PhoneNumber", "+491701234567", false)] // a phone number is part of the login too
    [InlineData("Password", "typed", true)] // typing a new password is what it is for
    public void Only_the_login_of_a_stored_row_decides_whether_its_password_is_kept(string field, string value, bool keeps)
    {
        var form = WithBackup();
        var row = Accounts(form).Rows[0];

        row.Fields.Single(f => f.Id == field).Text = value;

        Assert.Equal(keeps, row.Fields.Single(f => f.IsPassword).KeepsStoredSecret);
        Assert.Equal(keeps || field == "Password", form.IsValid);
    }

    [Fact]
    public void A_changed_backup_row_with_the_password_typed_again_is_complete_and_still_says_which_row_it_was()
    {
        var form = WithBackup();
        var row = Accounts(form).Rows[0];
        row.Fields.Single(f => f.Id == "Email").Text = "other@x.org";
        row.Fields.Single(f => f.IsPassword).Text = "new-pw";

        Assert.Null(form.Problem);
        var sent = form.Values()["Accounts"][0];
        Assert.Equal("other@x.org", sent.GetProperty("Email").GetString());
        Assert.Equal("new-pw", sent.GetProperty("Password").GetString());
        Assert.Equal(0, sent.GetProperty(PluginRules.KeepProperty).GetInt32()); // the API takes the typed one
    }

    [Fact]
    public void A_stored_backup_row_whose_login_was_taken_away_is_a_row_nobody_filled_in()
    {
        var form = WithBackup();
        var row = Accounts(form).Rows[0];

        row.Fields.Single(f => f.Id == "Email").Text = "";

        Assert.True(row.IsEmpty); // its password is not the password of anything now
        Assert.Null(form.Problem);
        Assert.Empty(form.Values()["Accounts"].EnumerateArray());
    }

    [Fact]
    public void Loading_again_after_a_save_starts_from_what_was_saved()
    {
        var form = WithBackup();
        var row = Accounts(form).Rows[0];
        row.Fields.Single(f => f.Id == "Email").Text = "other@x.org";
        Assert.False(row.Fields.Single(f => f.IsPassword).KeepsStoredSecret);

        form.Load(Stored(["Accounts/0/Password"], ("Accounts", new object[] { new { Email = "other@x.org", PhoneNumber = "", _keep = 0 } })));

        var saved = Accounts(form).Rows[0];
        Assert.True(saved.Fields.Single(f => f.IsPassword).KeepsStoredSecret);
        Assert.Null(form.Problem);
    }

    [Fact]
    public void A_password_without_a_rule_keeps_its_place_whatever_else_is_changed_in_the_row()
    {
        var form = new PluginFormViewModel(PluginTestKit.Layout(PluginTestKit.AcmeForm));
        form.Load(new PluginSettingsDto(PluginTestKit.Values(("Region", "eu"), ("Rows", new object[] { new { Name = "a", _keep = 0 } })), ["ApiKey", "Rows/0/Token"]));
        var row = form.Items.OfType<PluginListViewModel>().Single().Rows[0];

        row.Fields.Single(f => f.Id == "Name").Text = "renamed";

        Assert.True(row.Fields.Single(f => f.IsPassword).KeepsStoredSecret);
        Assert.False(row.IsEmpty);
    }

    [Fact]
    public void The_identity_of_a_row_is_what_the_other_groups_of_the_rule_of_its_passwords_name()
    {
        var spond = PluginTestKit.Layout(PluginTestKit.SpondForm).Elements.Single(e => e.Type == "list").Elements!;
        var acme = PluginTestKit.Layout(PluginTestKit.AcmeForm).Elements.Single(e => e.Type == "list").Elements!;

        Assert.Equal(["Email", "PhoneNumber"], PluginRules.RowIdentity(spond));
        Assert.Empty(PluginRules.RowIdentity(acme));
        Assert.Empty(PluginRules.RowIdentity([]));
    }
}
