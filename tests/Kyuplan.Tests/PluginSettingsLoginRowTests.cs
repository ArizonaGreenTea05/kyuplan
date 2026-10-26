using System.Text.Json;
using System.Text.Json.Nodes;
using Kyuplan.Api.Plugins;
using Kyuplan.Contracts;
using Microsoft.Extensions.Configuration;

namespace Kyuplan.Tests;

/// <summary>
/// The rows of a list whose passwords are tied to a login (the Spond backup accounts): a stored password is only kept while the row still says what it was stored for.
/// </summary>
public sealed class PluginSettingsLoginRowTests
{
    private static readonly PluginUiLayout Spond = PluginTestKit.Layout(PluginTestKit.SpondForm);
    private static readonly PluginUiLayout Acme = PluginTestKit.Layout(PluginTestKit.AcmeForm);

    private static IConfigurationSection Section(string name, params (string Key, string Value)[] values) =>
        new ConfigurationBuilder().AddInMemoryCollection(values.ToDictionary(v => $"{name}:{v.Key}", v => (string?)v.Value)).Build().GetSection(name);

    private static IConfigurationSection Stored() => Section("Spond",
        ("Accounts:0:Email", "one@example.org"), ("Accounts:0:Password", "pw1"),
        ("Accounts:1:PhoneNumber", "+491701234567"), ("Accounts:1:Password", "pw2"));

    private static IReadOnlyDictionary<string, JsonElement> Rows(params object[] rows) => PluginTestKit.Values(("Accounts", rows));

    private static List<string> Written(Dictionary<string, object?> changes) =>
        [.. ((JsonArray)changes["Spond:Accounts"]!).Select(r => $"{(string?)r!["Email"]}|{(string?)r["PhoneNumber"]}|{(string?)r["Password"]}")];

    [Fact]
    public void A_row_that_still_has_its_login_keeps_its_password_even_when_the_email_is_in_other_letters()
    {
        var changes = PluginSettingsMapper.Changes(Spond, "Spond", Rows(
            new { Email = "ONE@example.org", PhoneNumber = "", Password = "", _keep = 0 },
            new { Email = "", PhoneNumber = "+491701234567", Password = "", _keep = 1 }), Stored());

        Assert.Equal(["ONE@example.org||pw1", "|+491701234567|pw2"], Written(changes));
    }

    [Fact]
    public void A_row_whose_email_or_phone_number_was_changed_does_not_take_the_password_of_the_row_it_was()
    {
        var changes = PluginSettingsMapper.Changes(Spond, "Spond", Rows(
            new { Email = "other@example.org", PhoneNumber = "", Password = "", _keep = 0 },
            new { Email = "", PhoneNumber = "+49999", Password = "", _keep = 1 },
            new { Email = "third@example.org", PhoneNumber = "", Password = "typed", _keep = 0 }), Stored());

        Assert.Equal(["other@example.org||", "|+49999|", "third@example.org||typed"], Written(changes)); // the typed one is always taken
    }

    [Fact]
    public void A_phone_number_added_to_a_row_is_a_change_of_its_login_too_and_a_row_without_a_stored_place_keeps_nothing()
    {
        var changes = PluginSettingsMapper.Changes(Spond, "Spond", Rows(
            new { Email = "one@example.org", PhoneNumber = "+49", Password = "", _keep = 0 },
            new { Email = "one@example.org", PhoneNumber = "", Password = "" }, // no place: a new row
            new { Email = "one@example.org", PhoneNumber = "", Password = "", _keep = 9 }, // a place that is not there
            new { Email = "one@example.org", PhoneNumber = "", Password = "", _keep = -1 }), Stored());

        Assert.Equal(["one@example.org|+49|", "one@example.org||", "one@example.org||", "one@example.org||"], Written(changes));
    }

    [Fact]
    public void Rows_that_were_not_changed_are_no_change_at_all_and_a_row_missing_a_property_counts_as_empty_there()
    {
        Assert.Empty(PluginSettingsMapper.Changes(Spond, "Spond", Rows(
            new { Email = "one@example.org", PhoneNumber = "", Password = "", _keep = 0 },
            new { Email = "", PhoneNumber = "+491701234567", Password = "", _keep = 1 }), Stored()));

        var changes = PluginSettingsMapper.Changes(Spond, "Spond", Rows(new { Password = "", _keep = 0 }), Stored()); // no email property: the login is gone
        Assert.Empty((JsonArray)changes["Spond:Accounts"]!);
    }

    [Fact]
    public void A_password_that_has_no_rule_keeps_its_place_in_the_list_whatever_is_changed_in_the_row()
    {
        var current = Section("Acme", ("Rows:0:Name", "a"), ("Rows:0:Token", "pa"));

        var changes = PluginSettingsMapper.Changes(Acme, "Acme", PluginTestKit.Values(("Rows", new object[] { new { Name = "renamed", Token = "", _keep = 0 } })), current);

        var rows = ((JsonArray)changes["Acme:Rows"]!).Select(r => (string?)r!["Name"] + "/" + (string?)r["Token"]).ToList();
        Assert.Equal(["renamed/pa"], rows);
    }

    [Fact]
    public void The_values_to_check_lose_the_place_of_a_changed_row_and_nothing_else()
    {
        var values = PluginTestKit.Values(
            ("Email", "me@example.org"),
            ("Accounts", new object[]
            {
                new { Email = "one@example.org", Password = "", _keep = 0 },
                new { Email = "changed@example.org", Password = "", _keep = 1 },
                new { Email = "new@example.org", Password = "x" },
            }));

        var checkedValues = PluginSettingsMapper.WithoutStaleKeeps(Spond, Stored(), values);

        var rows = checkedValues["Accounts"].EnumerateArray().ToList();
        Assert.True(rows[0].TryGetProperty(PluginRules.KeepProperty, out var kept) && kept.GetInt32() == 0);
        Assert.False(rows[1].TryGetProperty(PluginRules.KeepProperty, out _));
        Assert.Equal("changed@example.org", rows[1].GetProperty("Email").GetString()); // what was typed stays
        Assert.False(rows[2].TryGetProperty(PluginRules.KeepProperty, out _));
        Assert.Equal("me@example.org", checkedValues["Email"].GetString());
        Assert.True(values["Accounts"].EnumerateArray().ElementAt(1).TryGetProperty(PluginRules.KeepProperty, out _)); // the values that were sent are not touched
    }

    [Fact]
    public void Values_that_are_no_list_of_rows_are_left_to_the_checks_of_the_values()
    {
        var values = new Dictionary<string, JsonElement>
        {
            ["Accounts"] = JsonSerializer.SerializeToElement("not a list"),
            ["Email"] = JsonSerializer.SerializeToElement("me@example.org"),
        };
        Assert.Equal(JsonValueKind.String, PluginSettingsMapper.WithoutStaleKeeps(Spond, Stored(), values)["Accounts"].ValueKind);
        Assert.Empty(PluginSettingsMapper.WithoutStaleKeeps(Spond, Stored(), PluginTestKit.Values())); // a list that was not sent is not made up

        var odd = PluginTestKit.Values(("Accounts", new object?[] { 5, null, new { Email = "x@example.org", _keep = "zero" } }));
        var rows = PluginSettingsMapper.WithoutStaleKeeps(Spond, Stored(), odd)["Accounts"].EnumerateArray().ToList();
        Assert.Equal(3, rows.Count);
        Assert.Equal(JsonValueKind.Number, rows[0].ValueKind);
        Assert.Equal(JsonValueKind.Null, rows[1].ValueKind);
        Assert.False(rows[2].TryGetProperty(PluginRules.KeepProperty, out _)); // a place that is no number is no place
    }

    [Fact]
    public void A_form_without_a_rule_on_its_passwords_is_checked_as_it_was_sent()
    {
        var values = PluginTestKit.Values(("ApiKey", "k"), ("Rows", new object[] { new { Name = "renamed", Token = "", _keep = 0 } }));

        var same = PluginSettingsMapper.WithoutStaleKeeps(Acme, Section("Acme", ("Rows:0:Name", "a"), ("Rows:0:Token", "pa")), values);

        Assert.True(same["Rows"][0].TryGetProperty(PluginRules.KeepProperty, out _));
    }
}
