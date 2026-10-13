using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Configuration.Memory;
using Microsoft.Extensions.Configuration.Json;
using Microsoft.Extensions.FileProviders;

namespace Kyuplan.Api.Configuration;

/// <summary>
/// The settings file the web client writes (<c>Setup:SettingsFile</c>, default <c>data/settings.json</c>): the same JSON as
/// <c>appsettings.json</c>, holding only what was set in the client. It is read on top of <c>appsettings.json</c>, so it survives restarts and
/// updates of the program files; environment variables and command line arguments still win over it. It lives apart from the databases,
/// because it says where they are. In a container it belongs on the volume (<c>Setup__SettingsFile=/data/settings.json</c>).
/// The Spond passwords in it are encrypted (ASP.NET Data Protection, see <see cref="CreateProtector"/>): a copy of the file alone, e.g. in a backup,
/// does not give them away. The configuration sees them decrypted.
/// </summary>
public sealed class SettingsFile(string path, IDataProtector? protector = null)
{
    public const string PathSetting = "Setup:SettingsFile";

    /// <summary>Where the key for the encrypted passwords lives; default: the folder <c>keys</c> next to the settings file. Keep it apart from the data volume's backups.</summary>
    public const string KeyDirectorySetting = "Setup:KeyDirectory";

    private const string Prefix = "enc:v1:";

    /// <summary>The same settings file kind at another place, with the same key.</summary>
    public SettingsFile At(string otherPath) => new(otherPath, protector);

    /// <summary>The key ring is a folder of files; whoever can read them can decrypt the file, so the folder gets the rights of the settings file's owner only where the system allows it.</summary>
    public static IDataProtector CreateProtector(IConfiguration configuration, string settingsFilePath)
    {
        var directory = configuration[KeyDirectorySetting] is { Length: > 0 } configured
            ? System.IO.Path.GetFullPath(configured)
            : System.IO.Path.Combine(System.IO.Path.GetDirectoryName(settingsFilePath)!, "keys");
        Directory.CreateDirectory(directory);
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(directory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);

        return DataProtectionProvider.Create(new DirectoryInfo(directory), options => options.SetApplicationName("kyuplan")).CreateProtector("Kyuplan.SettingsFile.v1");
    }

    private static readonly JsonSerializerOptions Indented = new() { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    public string Path => path;

    /// <summary>Where the setup remembers a settings file that is not in the default place (the file cannot say where it is itself).</summary>
    public static string PointerPath(string contentRoot) => System.IO.Path.Combine(contentRoot, "settings-location.txt");

    /// <summary>The location is set by the configuration (environment variable, command line, appsettings), which the setup does not override.</summary>
    public static bool IsConfigured(IConfiguration configuration) => configuration[PathSetting] is { Length: > 0 };

    /// <summary>The configured path, else the one the setup remembered, else <c>data/settings.json</c>.</summary>
    public static string Resolve(IConfiguration configuration, string contentRoot)
    {
        var pointer = PointerPath(contentRoot);
        var remembered = !IsConfigured(configuration) && File.Exists(pointer) ? File.ReadAllText(pointer).Trim() : "";
        var chosen = IsConfigured(configuration) ? configuration[PathSetting]! : remembered.Length > 0 ? remembered : "data/settings.json";
        return System.IO.Path.GetFullPath(chosen, contentRoot);
    }

    /// <summary>Remembers that the settings file lives at <paramref name="path"/> from the next start on.</summary>
    public static void RememberLocation(string contentRoot, string path) => File.WriteAllText(PointerPath(contentRoot), path);

    /// <summary>
    /// Puts the file into the configuration, behind the JSON files and before the environment variables. A file that is not valid JSON is
    /// ignored (and said so on the console): the API must start to let somebody put it right.
    /// </summary>
    public void AddTo(ConfigurationManager configuration)
    {
        if (!File.Exists(path)) return;

        try
        {
            JsonNode.Parse(File.ReadAllText(path));
        }
        catch (JsonException ex)
        {
            Console.Error.WriteLine($"The settings file {path} is ignored, it is not valid JSON: {ex.Message}");
            return;
        }

        EncryptInPlace();

        var source = new JsonConfigurationSource
        {
            Path = System.IO.Path.GetFileName(path),
            FileProvider = new PhysicalFileProvider(System.IO.Path.GetDirectoryName(path)!),
            Optional = true,
        };
        var sources = ((IConfigurationBuilder)configuration).Sources;
        var at = Math.Max(0, sources.ToList().FindLastIndex(s => s is JsonConfigurationSource)) + 1;
        sources.Insert(at, source);
        // The JSON source holds the passwords as they are in the file (encrypted); these are the same ones in plain text and win over it.
        if (protector is not null && PlainPasswords() is { Count: > 0 } plain)
            sources.Insert(at + 1, new MemoryConfigurationSource { InitialData = plain });
    }

    /// <summary>The configuration keys of the encrypted passwords, with the passwords in plain text. A password that cannot be decrypted (the key is gone) is left out and said so on the console.</summary>
    private Dictionary<string, string?> PlainPasswords()
    {
        var plain = new Dictionary<string, string?>();
        if (JsonNode.Parse(File.ReadAllText(path)) is not JsonObject root) return plain;

        foreach (var (key, holder, name) in PasswordsIn(root))
        {
            if (holder[name]?.GetValue<string>() is not { } value || !value.StartsWith(Prefix, StringComparison.Ordinal)) continue;

            try
            {
                plain[key] = protector!.Unprotect(value[Prefix.Length..]);
            }
            catch (System.Security.Cryptography.CryptographicException)
            {
                Console.Error.WriteLine($"A password in {path} cannot be decrypted (the key in the key folder is missing or changed): enter it again on the settings page.");
                plain[key] = null;
            }
        }

        return plain;
    }

    /// <summary>The places in the file where a Spond password stands: the configuration key, the object that holds it and its name.</summary>
    private static IEnumerable<(string Key, JsonObject Holder, string Name)> PasswordsIn(JsonObject root)
    {
        if (root["Spond"] is not JsonObject spond) yield break;

        if (spond["Password"] is JsonValue) yield return ("Spond:Password", spond, "Password");
        if (spond["Accounts"] is not JsonArray accounts) yield break;

        for (var i = 0; i < accounts.Count; i++)
            if (accounts[i] is JsonObject account && account["Password"] is JsonValue)
                yield return ($"Spond:Accounts:{i}:Password", account, "Password");
    }

    /// <summary>Encrypts every password that is still in plain text, so that a file from an earlier version is protected from the first start on.</summary>
    private void EncryptInPlace()
    {
        if (protector is null || !File.Exists(path) || JsonNode.Parse(File.ReadAllText(path)) is not JsonObject root) return;

        if (Encrypt(root)) Write(root);
    }

    private bool Encrypt(JsonObject root)
    {
        var changed = false;
        foreach (var (_, holder, name) in PasswordsIn(root))
        {
            if (holder[name]?.GetValue<string>() is not { Length: > 0 } value || value.StartsWith(Prefix, StringComparison.Ordinal)) continue;

            holder[name] = Prefix + protector!.Protect(value);
            changed = true;
        }

        return changed;
    }

    private void Write(JsonObject root)
    {
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);
        var temporary = path + ".tmp";
        File.WriteAllText(temporary, root.ToJsonString(Indented));
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(temporary, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        File.Move(temporary, path, overwrite: true);
    }

    /// <summary>Sets these values (keys as in the configuration: <c>Spond:Email</c>; text, or a <see cref="JsonNode"/> for lists) in the file and keeps everything else in it. Written in one piece, so a crash leaves the old file.</summary>
    public void Merge(IReadOnlyDictionary<string, object?> values)
    {
        var root = File.Exists(path) && JsonNode.Parse(File.ReadAllText(path)) is JsonObject existing ? existing : [];

        foreach (var (key, value) in values)
        {
            var parts = key.Split(':');
            var node = root;
            foreach (var part in parts[..^1])
            {
                if (node[part] is not JsonObject child) node[part] = child = [];
                node = child;
            }

            node[parts[^1]] = value switch { null => null, JsonNode json => json, _ => JsonValue.Create(value.ToString()) };
        }

        Encrypt(root);
        Write(root);
    }
}
