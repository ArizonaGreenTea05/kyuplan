using System.ComponentModel;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Options;
using Kyuplan.Client;
using Kyuplan.Client.Api;
using Kyuplan.Client.LinuxApp.Services;
using Kyuplan.Contracts;

namespace Kyuplan.Tests;

/// <summary>Where the Linux client keeps its small settings: <c>$XDG_CONFIG_HOME/kyuplan</c>, one text file each.</summary>
public sealed class UserPreferencesTests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "kyuplan-prefs-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_folder)) Directory.Delete(_folder, recursive: true);
    }

    [Fact]
    public void A_setting_that_was_never_chosen_reads_as_nothing()
    {
        Assert.Null(new UserPreferences(_folder).Get("language"));
    }

    [Fact]
    public void A_setting_is_kept_in_its_own_file_and_comes_back()
    {
        var preferences = new UserPreferences(_folder);

        preferences.Set("language", "de");
        preferences.Set("api-address", "http://planner.test/");

        Assert.Equal("de", new UserPreferences(_folder).Get("language"));
        Assert.Equal("http://planner.test/", preferences.Get("api-address"));
        Assert.True(File.Exists(Path.Combine(_folder, "language.txt")));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   \n")]
    public void A_setting_with_no_text_reads_as_nothing(string text)
    {
        Directory.CreateDirectory(_folder);
        File.WriteAllText(Path.Combine(_folder, "language.txt"), text);

        Assert.Null(new UserPreferences(_folder).Get("language"));
    }

    [Fact]
    public void A_setting_is_trimmed_and_the_last_one_wins()
    {
        var preferences = new UserPreferences(_folder);
        preferences.Set("language", "en");
        preferences.Set("language", "  de\n");

        Assert.Equal("de", preferences.Get("language"));
    }

    [Fact]
    public void A_folder_that_cannot_be_written_is_no_error()
    {
        Directory.CreateDirectory(_folder);
        var blocked = Path.Combine(_folder, "file");
        File.WriteAllText(blocked, "not a folder"); // a file where the folder should be

        var preferences = new UserPreferences(blocked);
        preferences.Set("language", "de");

        Assert.Null(preferences.Get("language"));
    }

    [Fact]
    public void The_default_folder_follows_the_xdg_base_directory_specification()
    {
        var before = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME");
        try
        {
            var root = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "xdg-config"));
            Environment.SetEnvironmentVariable("XDG_CONFIG_HOME", root);
            Assert.Equal(Path.Combine(root, "kyuplan"), UserPreferences.DefaultFolder());

            // A relative path is not allowed by the specification: it is ignored.
            Environment.SetEnvironmentVariable("XDG_CONFIG_HOME", "relative/config");
            Assert.EndsWith(Path.Combine(".config", "kyuplan"), UserPreferences.DefaultFolder());

            Environment.SetEnvironmentVariable("XDG_CONFIG_HOME", null);
            Assert.EndsWith(Path.Combine(".config", "kyuplan"), UserPreferences.DefaultFolder());
        }
        finally
        {
            Environment.SetEnvironmentVariable("XDG_CONFIG_HOME", before);
        }
    }

    [Fact]
    public void The_filter_of_the_statistics_page_is_remembered_in_a_file()
    {
        var store = new FileFilterSettingsStore(new UserPreferences(_folder));
        Assert.Null(store.Load());

        var settings = new FilterSettingsDto("group-1", new DateOnly(2026, 1, 1), new DateOnly(2026, 6, 30), null, null, ["sg-kids"], 80, 4, "Training", "Exam", 3.5);
        store.Save(settings);

        var loaded = new FileFilterSettingsStore(new UserPreferences(_folder)).Load();
        Assert.Equal("group-1", loaded?.GroupId);
        Assert.Equal(["sg-kids"], loaded?.SubGroupIds);
        Assert.Equal(3.5, loaded?.MinAverageStars);
    }

    [Fact]
    public void A_damaged_filter_file_is_no_filter()
    {
        var preferences = new UserPreferences(_folder);
        preferences.Set("last-filter", "{ not json");

        Assert.Null(new FileFilterSettingsStore(preferences).Load());
    }
}

/// <summary>The login the user chose to remember lives in the desktop's keyring (through <c>secret-tool</c>), for one API address only.</summary>
public sealed class SecretServiceLoginStoreTests
{
    private static IOptions<PlannerClientOptions> Api(string address) => Options.Create(new PlannerClientOptions { BaseAddress = address });

    /// <summary>A keyring in memory: what <c>secret-tool store</c> gets is what <c>lookup</c> gives, by attributes.</summary>
    private sealed class FakeKeyring
    {
        public Dictionary<string, string> Secrets { get; } = [];

        public List<IReadOnlyList<string>> Calls { get; } = [];

        public (int, string) Run(IReadOnlyList<string> arguments, string? input)
        {
            Calls.Add(arguments);
            var key = string.Join('|', arguments.SkipWhile(a => a is not "application"));
            switch (arguments[0])
            {
                case "store":
                    Secrets[key] = input!;
                    return (0, "");
                case "lookup":
                    return Secrets.TryGetValue(key, out var secret) ? (0, secret) : (1, "");
                case "clear":
                    Secrets.Remove(key);
                    return (0, "");
                default:
                    return (2, "");
            }
        }
    }

    [Fact]
    public void A_saved_login_comes_back()
    {
        var keyring = new FakeKeyring();
        var store = new SecretServiceLoginStore(Api("http://planner.test/"), keyring.Run);

        store.Save(new Login("anna", "pa:ss\"word ä"));

        Assert.Equal(new Login("anna", "pa:ss\"word ä"), store.Load());
    }

    [Fact]
    public void The_password_goes_to_the_keyring_through_the_input_and_never_through_the_arguments()
    {
        var keyring = new FakeKeyring();
        new SecretServiceLoginStore(Api("http://planner.test/"), keyring.Run).Save(new Login("anna", "s3cret-Pass"));

        Assert.DoesNotContain(keyring.Calls, call => call.Any(a => a.Contains("s3cret-Pass")));
        Assert.Contains("s3cret-Pass", keyring.Secrets.Single().Value);
    }

    [Fact]
    public void A_login_is_only_handed_out_for_the_api_it_was_saved_for()
    {
        var keyring = new FakeKeyring();
        new SecretServiceLoginStore(Api("http://planner.test/"), keyring.Run).Save(new Login("anna", "secret-password"));

        Assert.Null(new SecretServiceLoginStore(Api("http://other.test/"), keyring.Run).Load());
        Assert.NotNull(new SecretServiceLoginStore(Api("http://PLANNER.test"), keyring.Run).Load()); // the same API, written differently
    }

    [Fact]
    public void Forgetting_the_login_removes_it_from_the_keyring()
    {
        var keyring = new FakeKeyring();
        var store = new SecretServiceLoginStore(Api("http://planner.test/"), keyring.Run);
        store.Save(new Login("anna", "secret-password"));

        store.Clear();

        Assert.Null(store.Load());
        Assert.Empty(keyring.Secrets);
    }

    [Theory]
    [InlineData("")]
    [InlineData("not json")]
    [InlineData("{\"UserName\":\"\",\"Password\":\"x\"}")]
    [InlineData("{\"UserName\":\"anna\",\"Password\":\"\"}")]
    public void A_damaged_or_empty_entry_is_no_login(string entry)
    {
        var store = new SecretServiceLoginStore(Api("http://planner.test/"), (_, _) => (0, entry));

        Assert.Null(store.Load());
    }

    [Fact]
    public void Without_secret_tool_nothing_is_remembered_and_nothing_fails()
    {
        static (int, string) Missing(IReadOnlyList<string> arguments, string? input) => throw new Win32Exception("No such file or directory");
        var store = new SecretServiceLoginStore(Api("http://planner.test/"), Missing);

        store.Save(new Login("anna", "secret-password"));
        store.Clear();

        Assert.Null(store.Load());
    }

    [Fact]
    public void A_keyring_that_refuses_gives_no_login()
    {
        var store = new SecretServiceLoginStore(Api("http://planner.test/"), (_, _) => (1, "{\"UserName\":\"anna\",\"Password\":\"secret-password\"}"));

        Assert.Null(store.Load()); // a non-zero exit code means no secret, whatever was printed
    }
}

/// <summary>The update of the Linux client: the pacman package the server carries, installed with polkit, only when the release signed it.</summary>
public sealed class LinuxClientUpdaterTests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "kyuplan-update-" + Guid.NewGuid().ToString("N"));

    public LinuxClientUpdaterTests() => Directory.CreateDirectory(_folder);

    public void Dispose() => Directory.Delete(_folder, recursive: true);

    [Fact]
    public void It_updates_the_linux_installer_which_is_a_pacman_package()
    {
        var updater = new LinuxClientUpdater(() => { });

        Assert.Equal(ClientPlatform.Linux, updater.Platform);
        Assert.Equal("Kyuplan-linux-1.2.3.pkg.tar.zst", ClientDownload.FileName(updater.Platform, "1.2.3", updater.InstallerExtension));
        Assert.Equal(("linux", "1.2.3"), ClientDownload.Parse("Kyuplan-linux-1.2.3.pkg.tar.zst"));
        Assert.Equal(("linux", "1.2.3-beta.1"), ClientDownload.Parse("Kyuplan-linux-1.2.3-beta.1.pkg.tar.zst"));
        Assert.NotEmpty(updater.CurrentVersion);
    }

    [Fact]
    public void The_install_command_installs_the_package_with_polkit_and_starts_the_program_again()
    {
        var command = LinuxClientUpdater.InstallCommand("/tmp/Kyuplan-linux-1.2.3.pkg.tar.zst");

        Assert.Equal("sh", command[0]);
        Assert.Contains("pkexec pacman -U --noconfirm \"$1\"", command[2]);
        Assert.EndsWith("exec kyuplan", command[2]);
        Assert.Equal("/tmp/Kyuplan-linux-1.2.3.pkg.tar.zst", command[^1]); // the path is an argument, never part of the script: no quoting problems, no injection
    }

    [Fact]
    public void A_path_with_spaces_and_quotes_stays_one_argument()
    {
        var command = LinuxClientUpdater.InstallCommand("/tmp/it's a \"path\"/pkg.tar.zst");

        Assert.DoesNotContain("it's", command[2]);
        Assert.Equal("/tmp/it's a \"path\"/pkg.tar.zst", command[^1]);
    }

    [Fact]
    public void A_package_without_a_signature_is_not_installed_and_is_deleted()
    {
        var quit = false;
        var path = Path.Combine(_folder, "Kyuplan-linux-9.9.9.pkg.tar.zst");
        File.WriteAllText(path, "not a package");
        Environment.SetEnvironmentVariable(LinuxClientUpdater.AllowUnsignedVariable, null);

        var updater = new LinuxClientUpdater(() => quit = true);

        Assert.Throws<InvalidOperationException>(() => updater.Install(path));
        Assert.False(File.Exists(path));
        Assert.False(quit);
    }

    [Fact]
    public void A_package_signed_with_another_key_is_not_installed()
    {
        var path = Path.Combine(_folder, "Kyuplan-linux-9.9.9.pkg.tar.zst");
        File.WriteAllText(path, "a package");
        using var other = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        File.WriteAllBytes(path + ClientDownload.SignatureExtension,
            other.SignData(Encoding.UTF8.GetBytes("a package"), HashAlgorithmName.SHA256, DSASignatureFormat.Rfc3279DerSequence));
        Environment.SetEnvironmentVariable(LinuxClientUpdater.AllowUnsignedVariable, null);

        Assert.Throws<InvalidOperationException>(() => new LinuxClientUpdater(() => { }).Install(path));
        Assert.False(File.Exists(path));
        Assert.False(File.Exists(path + ClientDownload.SignatureExtension));
    }
}

/// <summary>The colours the Linux client paints with: the twelve colours of a theme (or the system's look) turned into a stylesheet.</summary>
public sealed class ThemePaletteTests
{
    [Theory]
    [InlineData(ThemeIds.Dracula)]
    [InlineData(ThemeIds.CatppuccinMocha)]
    [InlineData(ThemeIds.CatppuccinLatte)]
    public void The_themes_the_clients_draw_themselves_are_packs_that_keep_the_contrast_the_rules_ask_for(string id)
    {
        var pack = ThemePalette.BuiltIn(id);

        Assert.NotNull(pack);
        Assert.Null(ThemePackRules.Check(pack with { Id = "test-pack" })); // the id of a built-in theme is reserved: the colours are what is checked here
    }

    [Theory]
    [InlineData(ThemeIds.Default)]
    [InlineData(ThemeIds.LiquidGlass)]
    [InlineData("unknown")]
    public void The_system_look_and_liquid_glass_have_no_colour_set_of_their_own(string id) => Assert.Null(ThemePalette.BuiltIn(id));

    [Fact]
    public void The_system_look_only_gets_the_accent_and_the_status_colours()
    {
        var css = ThemePalette.Css(null, "#2563eb", dark: false);

        Assert.Contains("@define-color accent_bg_color #2563eb;", css);
        Assert.DoesNotContain("window_bg_color", css);
        Assert.Contains(".chip-warn", css);
        Assert.Contains("progressbar.rate-bar", css);
    }

    [Fact]
    public void A_pack_replaces_the_colours_of_the_window_the_cards_and_the_sidebar()
    {
        var pack = ThemePalette.BuiltIn(ThemeIds.Dracula)!;

        var css = ThemePalette.Css(pack, "#2563eb", dark: true);

        Assert.Contains("@define-color window_bg_color #1E1F29;", css);
        Assert.Contains("@define-color card_bg_color #282A36;", css);
        Assert.Contains("@define-color sidebar_bg_color #21222C;", css);
        Assert.Contains("@define-color window_fg_color #F8F8F2;", css);
    }

    [Fact]
    public void The_accent_of_a_high_contrast_pack_wins_over_the_accent_of_the_instance()
    {
        var pack = BuiltInThemePacks.All.First(p => p.HighContrast && p.Accent is not null);

        var css = ThemePalette.Css(pack, "#ff00ff", pack.Scheme == "dark");

        Assert.Contains($"@define-color accent_bg_color {pack.Accent};", css);
        Assert.DoesNotContain("#ff00ff", css);
        Assert.Contains("outline", css); // thick focus rings
    }

    [Theory]
    [InlineData("not a colour")]
    [InlineData("#12")]
    [InlineData("")]
    public void An_accent_that_is_no_colour_falls_back_to_the_default_blue(string accent)
    {
        Assert.Contains("@define-color accent_bg_color #2563eb;", ThemePalette.Css(null, accent, dark: false));
    }

    [Theory]
    [InlineData("#FFFFFF", "#000000")] // white accent: black text on it
    [InlineData("#000000", "#FFFFFF")]
    [InlineData("#2563eb", "#FFFFFF")]
    [InlineData("#FFD700", "#000000")]
    public void The_text_on_the_accent_is_the_one_that_can_be_read(string accent, string expected)
    {
        Assert.Contains($"@define-color accent_fg_color {expected};", ThemePalette.Css(null, accent, dark: false));
    }

    [Theory]
    [InlineData("#2563eb", "#FFFFFF")]
    [InlineData("#2563eb", "#1E1E2E")]
    [InlineData("#FFFF00", "#FFFFFF")] // yellow on white: made darker until it reads
    [InlineData("#000080", "#000000")] // navy on black: made lighter
    public void The_accent_as_text_is_readable_on_its_background(string accent, string background)
    {
        var readable = ThemePalette.Readable(accent, background);

        Assert.True(ThemePackRules.Contrast(readable, background) >= 3.0, $"{readable} on {background}");
    }

    [Fact]
    public void The_accent_is_left_alone_when_it_is_already_readable()
    {
        Assert.Equal("#1D4ED8", ThemePalette.Readable("#1D4ED8", "#FFFFFF"));
        Assert.Equal("not a colour", ThemePalette.Readable("not a colour", "#FFFFFF"));
    }
}
