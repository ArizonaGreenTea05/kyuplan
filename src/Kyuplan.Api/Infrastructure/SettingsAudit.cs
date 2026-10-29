using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;

namespace Kyuplan.Api.Infrastructure;

/// <summary>
/// What the audit log says about a settings save: the configuration keys that changed, and for a key that is not known to be harmless only
/// that it changed, never its value (Spond logins, the metrics token, connection strings, ...). The script is described by its length and a hash,
/// so that two saves can be told apart without the code (which often holds tokens) being in the log, and in every backup.
/// </summary>
internal static class SettingsAudit
{
    /// <summary>Keys whose new value is written down as it is.</summary>
    private static readonly string[] Plain =
        ["Statistics:", "Sync:", "Spond:UseDemoData", "DataSource:Kind", "Metrics:Enabled", SettingsService.AllowedHostsKey, SettingsService.PublicUrlKey];

    private const string ScriptKey = "DataSource:Script";

    public static string Describe(IReadOnlyDictionary<string, object?> changes, JsonObject? extra = null)
    {
        var changed = new JsonObject();
        foreach (var (key, value) in changes.OrderBy(c => c.Key, StringComparer.Ordinal))
        {
            changed[key] = key == ScriptKey ? Script(value as string ?? "")
                : Plain.Any(p => key.StartsWith(p, StringComparison.Ordinal)) && value is string text ? JsonValue.Create(text)
                : JsonValue.Create("changed");
        }

        var result = new JsonObject { ["changed"] = changed };
        if (extra is not null)
            foreach (var (key, value) in extra.ToList())
            {
                extra.Remove(key);
                result[key] = value;
            }

        return result.ToJsonString();
    }

    private static JsonObject Script(string script) => new()
    {
        ["length"] = script.Length,
        ["sha256"] = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(script)))[..16].ToLowerInvariant(),
    };
}
