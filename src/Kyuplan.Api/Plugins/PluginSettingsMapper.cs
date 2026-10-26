using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Kyuplan.Contracts;

namespace Kyuplan.Api.Plugins;

/// <summary>
/// Turns the settings form of a data source (<see cref="PluginUiLayout"/>) into configuration keys and back: a field <c>Email</c> of the section
/// <c>Spond</c> is the key <c>Spond:Email</c>, a <c>list</c> is an array of objects. The values live in the settings file like every other setting
/// (the <c>password</c> fields encrypted, see <see cref="Configuration.SettingsFile"/>) and reach the plugin through its section of the configuration.
/// What the form shows never contains a password: only <see cref="PluginSettingsDto.SecretsSet"/> says which ones are stored.
/// </summary>
public static class PluginSettingsMapper
{
    /// <summary>The settings as the form shows them.</summary>
    public static PluginSettingsDto Read(PluginUiLayout form, IConfigurationSection section)
    {
        var values = new Dictionary<string, JsonElement>();
        var secrets = new List<string>();

        foreach (var e in form.Elements)
        {
            if (e.Id is null) continue;

            if (e.Type == "field")
            {
                if (e.Input == "password")
                {
                    if (!string.IsNullOrEmpty(section[e.Id])) secrets.Add(e.Id);
                }
                else
                {
                    values[e.Id] = ToElement(e, section[e.Id] ?? e.Default);
                }
            }
            else if (e.Type == "list")
            {
                var rows = new JsonArray();
                var stored = Rows(section, e.Id);
                for (var i = 0; i < stored.Count; i++)
                {
                    var row = new JsonObject { [PluginRules.KeepProperty] = i };
                    foreach (var f in e.Elements ?? [])
                    {
                        if (f.Id is null) continue;

                        var raw = stored[i].GetValueOrDefault(f.Id);
                        if (f.Input == "password") { if (!string.IsNullOrEmpty(raw)) secrets.Add($"{e.Id}/{i}/{f.Id}"); }
                        else row[f.Id] = JsonNode.Parse(ToElement(f, raw).GetRawText());
                    }

                    rows.Add(row);
                }

                values[e.Id] = JsonSerializer.SerializeToElement(rows);
            }
        }

        return new PluginSettingsDto(values, secrets);
    }

    /// <summary>The configuration keys (<c>Section:Field</c>) that differ from what is configured now, with their new values: text, or a <see cref="JsonNode"/> for a list.</summary>
    public static Dictionary<string, object?> Changes(PluginUiLayout form, string sectionName, IReadOnlyDictionary<string, JsonElement> values, IConfigurationSection current)
    {
        var changes = new Dictionary<string, object?>();
        foreach (var e in form.Elements)
        {
            if (e.Id is null || !values.TryGetValue(e.Id, out var value)) continue; // not sent: it stays

            if (e.Type == "field")
            {
                var next = Text(e, value);
                if (e.Input == "password" ? next.Length > 0 && next != current[e.Id] : next != Normalized(e, current[e.Id] ?? e.Default)) changes[$"{sectionName}:{e.Id}"] = next;
            }
            else if (e.Type == "list" && value.ValueKind == JsonValueKind.Array)
            {
                var stored = Rows(current, e.Id);
                var next = new List<Dictionary<string, string>>();
                foreach (var item in value.EnumerateArray())
                {
                    var kept = KeptRow(e, item, stored);
                    var row = new Dictionary<string, string>();
                    foreach (var f in e.Elements ?? [])
                    {
                        if (f.Id is null) continue;

                        var text = item.TryGetProperty(f.Id, out var field) ? Text(f, field) : "";
                        if (f.Input == "password" && text.Length == 0) text = kept?.GetValueOrDefault(f.Id) ?? "";
                        if (text.Length > 0) row[f.Id] = text;
                    }

                    if (row.Count > 0) next.Add(row); // a row without anything in it is a row nobody filled in
                }

                if (!SameRows(next, stored)) changes[$"{sectionName}:{e.Id}"] = new JsonArray([.. next.Select(r => (JsonNode)new JsonObject([.. r.Select(p => KeyValuePair.Create(p.Key, (JsonNode?)JsonValue.Create(p.Value)))]))]);
            }
        }

        return changes;
    }

    /// <summary>
    /// The stored row a row of <paramref name="list"/> that was sent comes from and whose passwords it keeps, or <c>null</c> (a new row, an index that is not there, or a
    /// row whose login was changed). A password belongs to what the row says it is for: change the login of a row (the fields of <see cref="PluginRules.RowIdentity"/>) and
    /// its stored password is not carried over to the new one.
    /// </summary>
    private static Dictionary<string, string>? KeptRow(PluginUiElement list, JsonElement item, List<Dictionary<string, string>> stored)
    {
        if (!item.TryGetProperty(PluginRules.KeepProperty, out var keep) || keep.ValueKind != JsonValueKind.Number || !keep.TryGetInt32(out var index) || index < 0 || index >= stored.Count) return null;

        foreach (var id in PluginRules.RowIdentity(list.Elements ?? []))
        {
            if ((list.Elements ?? []).FirstOrDefault(x => x.Type == "field" && string.Equals(x.Id, id, StringComparison.OrdinalIgnoreCase)) is not { Id: not null } f) continue;

            var sent = item.TryGetProperty(f.Id!, out var value) ? Text(f, value) : "";
            if (!string.Equals(Normalized(f, sent), Normalized(f, stored[index].GetValueOrDefault(f.Id!)), StringComparison.OrdinalIgnoreCase)) return null;
        }

        return stored[index];
    }

    /// <summary>
    /// The values as they were sent, but a row of a list whose login was changed no longer says which stored row it was (<see cref="PluginRules.KeepProperty"/>), so that it does not
    /// count as having the passwords of that row (see <see cref="KeptRow"/>). The values are checked like this.
    /// </summary>
    public static IReadOnlyDictionary<string, JsonElement> WithoutStaleKeeps(PluginUiLayout form, IConfigurationSection current, IReadOnlyDictionary<string, JsonElement> values)
    {
        var result = new Dictionary<string, JsonElement>(values);
        foreach (var list in form.Elements.Where(e => e.Type == "list" && e.Id is not null))
        {
            if (!values.TryGetValue(list.Id!, out var sent) || sent.ValueKind != JsonValueKind.Array) continue;

            var stored = Rows(current, list.Id!);
            var rows = new JsonArray();
            foreach (var item in sent.EnumerateArray())
            {
                var row = JsonNode.Parse(item.GetRawText());
                if (item.ValueKind == JsonValueKind.Object && item.TryGetProperty(PluginRules.KeepProperty, out _) && KeptRow(list, item, stored) is null) ((JsonObject)row!).Remove(PluginRules.KeepProperty);
                rows.Add(row);
            }

            result[list.Id!] = JsonSerializer.SerializeToElement(rows);
        }

        return result;
    }

    /// <summary>The values as the form sends them with their stored secrets found (a password path of <see cref="PluginSettingsDto.SecretsSet"/>).</summary>
    public static Func<string, bool> HasSecret(PluginUiLayout form, IConfigurationSection section)
    {
        var stored = Read(form, section).SecretsSet.ToHashSet(StringComparer.Ordinal);
        return stored.Contains;
    }

    private static List<Dictionary<string, string>> Rows(IConfigurationSection section, string list) =>
        [.. section.GetSection(list).GetChildren().OrderBy(c => int.TryParse(c.Key, out var i) ? i : int.MaxValue)
            .Select(c => c.GetChildren().Where(v => !string.IsNullOrEmpty(v.Value)).ToDictionary(v => v.Key, v => v.Value!, StringComparer.OrdinalIgnoreCase))];

    private static bool SameRows(List<Dictionary<string, string>> next, List<Dictionary<string, string>> stored) =>
        next.Count == stored.Count && next.Zip(stored).All(p => p.First.Count == p.Second.Count && p.First.All(kv => p.Second.TryGetValue(kv.Key, out var v) && v == kv.Value));

    /// <summary>The value of a field as the text the configuration keeps it in: <c>true</c>/<c>false</c> for a checkbox, invariant digits for a number, trimmed text otherwise (a password and a multiline text as typed).</summary>
    private static string Text(PluginUiElement field, JsonElement value)
    {
        switch (field.Input)
        {
            case "checkbox":
                return value.ValueKind == JsonValueKind.True ? "true" : "false";

            case "number":
                return PluginRules.TryNumber(value, out var number) ? number.ToString("0.################", CultureInfo.InvariantCulture) : "";

            default:
                var text = value.ValueKind == JsonValueKind.String ? value.GetString() ?? "" : "";
                return field.Input is "password" or "multiline" ? text : text.Trim();
        }
    }

    /// <summary>What is configured as <see cref="Text"/> would write it, so that <c>2.0</c> and <c>2</c>, or <c>TRUE</c> and <c>true</c>, are no change; nothing configured is an empty text, and a flag that is off.</summary>
    private static string Normalized(PluginUiElement field, string? raw) => field.Input switch
    {
        "checkbox" => string.Equals(raw, "true", StringComparison.OrdinalIgnoreCase) ? "true" : "false",
        "number" => double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out var number) ? number.ToString("0.################", CultureInfo.InvariantCulture) : raw ?? "",
        _ => raw ?? "",
    };

    private static JsonElement ToElement(PluginUiElement field, string? raw) => field.Input switch
    {
        "checkbox" => JsonSerializer.SerializeToElement(string.Equals(raw, "true", StringComparison.OrdinalIgnoreCase)),
        "number" => double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out var number) ? JsonSerializer.SerializeToElement(number) : JsonSerializer.SerializeToElement<string?>(null),
        _ => JsonSerializer.SerializeToElement(raw ?? ""),
    };
}
