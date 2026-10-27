using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using Kyuplan.Api.Configuration;
using Kyuplan.Api.Infrastructure;
using Kyuplan.Api.Plugins;
using Kyuplan.Contracts;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using static Kyuplan.Tests.TestAccounts;

namespace Kyuplan.Tests;

/// <summary>
/// Plugins against the real API: a plugin that is installed in the plugin folder is loaded when the API starts and adds its pages, permissions, endpoints and data source;
/// administrators install and remove plugins; what a plugin brings reaches roles, users and the settings.
/// </summary>
public sealed class PluginApiTests(WebApplicationFactory<Program> factory) : IClassFixture<WebApplicationFactory<Program>>, IDisposable
{
    private const string Password = "a good password 123";

    private readonly InMemoryDatabase _planner = new();
    private readonly InMemoryDatabase _accounts = new();
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "pp-plugins-" + Guid.NewGuid().ToString("N"));
    private readonly CountingRestarter _restarter = new();
    private readonly FakePluginFetcher _fetcher = new();

    private string SettingsFile => Path.Combine(_folder, "settings.json");

    public void Dispose()
    {
        _planner.Dispose();
        _accounts.Dispose();
        SqliteConnection.ClearAllPools();
        if (Directory.Exists(_folder)) Directory.Delete(_folder, recursive: true);
    }

    /// <summary>An API whose plugin folder holds these packages. The settings file is this test's own.</summary>
    private WebApplicationFactory<Program> CreateHost(IEnumerable<byte[]> packages, Action<IWebHostBuilder>? configure = null)
    {
        foreach (var package in packages) PluginTestKit.Install(SettingsFile, package);
        return factory.WithWebHostBuilder(builder =>
        {
            builder.UseSetting("Spond:UseDemoData", "true");
            builder.UseSetting("Sync:Enabled", "false");
            builder.UseSetting("DefaultData:Enabled", "false");
            builder.UseSetting("Database:ConnectionString", _planner.ConnectionString);
            builder.UseSetting("Setup:SettingsFile", SettingsFile);
            Configure(builder, _accounts);
            builder.ConfigureServices(services =>
            {
                services.AddSingleton<IAppRestarter>(_restarter);
                services.AddSingleton<IPluginFetcher>(_fetcher);
            });
            configure?.Invoke(builder);
        });
    }

    private WebApplicationFactory<Program> CreateHost(Action<IWebHostBuilder>? configure = null) => CreateHost([PluginTestKit.AcmePackage()], configure);

    private static async Task<HttpClient> UserWithAsync(WebApplicationFactory<Program> host, string userName, params string[] permissions)
    {
        var response = await host.CreateSignedInClient().PostAsJsonAsync("/api/users", new UserInputDto(userName, Password, null, null, [], permissions));
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return host.CreateSignedInClient(userName, Password);
    }

    private static async Task<PluginCatalogDto> CatalogAsync(HttpClient client) => (await client.GetFromJsonAsync<PluginCatalogDto>("/api/plugins"))!;

    // --- What a plugin adds to the clients ---------------------------------------------------------------------

    [Fact]
    public async Task The_catalog_lists_what_a_plugin_adds_to_somebody_who_may_see_everything_of_it()
    {
        using var host = CreateHost();

        var plugin = Assert.Single((await CatalogAsync(host.CreateSignedInClient())).Plugins);

        Assert.Equal(("acme", "Acme", "Acme DE", "2.1.0"), (plugin.Id, plugin.Name, plugin.NameDe, plugin.Version));
        Assert.Equal(["acme.view", "acme.edit"], plugin.Permissions.Select(p => p.Id));
        Assert.Equal(["items", "about"], plugin.Pages.Select(p => p.Id));
        Assert.Equal("acme.view", plugin.Pages[0].Permission);
        Assert.Null(plugin.Pages[1].Permission);
        Assert.Equal("table", plugin.Pages[0].Layout.Elements[2].Type);
        var source = Assert.Single(plugin.DataSources);
        Assert.Equal("acme", source.Id);
        Assert.Equal("ApiKey", source.Form!.Elements[1].Id);
    }

    [Fact]
    public async Task A_page_and_its_layout_are_only_given_to_somebody_who_may_open_it_and_a_form_only_to_somebody_who_sees_the_settings()
    {
        using var host = CreateHost();
        var nobody = await UserWithAsync(host, "nobody");
        var viewer = await UserWithAsync(host, "viewer", "acme.view");
        var configurer = await UserWithAsync(host, "configurer", Permission.SettingsView);

        var anyone = Assert.Single((await CatalogAsync(nobody)).Plugins);
        Assert.Equal(["about"], anyone.Pages.Select(p => p.Id)); // the one that needs no permission
        Assert.Null(Assert.Single(anyone.DataSources).Form);
        Assert.Equal(["acme.view", "acme.edit"], anyone.Permissions.Select(p => p.Id)); // listed for everybody: a role is given them

        Assert.Equal(["items", "about"], Assert.Single((await CatalogAsync(viewer)).Plugins).Pages.Select(p => p.Id));
        Assert.Null(Assert.Single(Assert.Single((await CatalogAsync(viewer)).Plugins).DataSources).Form);

        Assert.NotNull(Assert.Single(Assert.Single((await CatalogAsync(configurer)).Plugins).DataSources).Form);
    }

    [Fact]
    public async Task The_catalog_needs_a_login_and_is_empty_without_plugins()
    {
        using var host = CreateHost([]);

        Assert.Equal(HttpStatusCode.Unauthorized, (await host.CreateClient().GetAsync("/api/plugins")).StatusCode);
        Assert.Empty((await CatalogAsync(host.CreateSignedInClient())).Plugins);
    }

    // --- The endpoints of a plugin -----------------------------------------------------------------------------

    [Fact]
    public async Task The_endpoints_of_a_plugin_need_a_login_and_the_permission_the_plugin_asks_for()
    {
        using var host = CreateHost();
        var nobody = await UserWithAsync(host, "nobody");
        var viewer = await UserWithAsync(host, "viewer", "acme.view");
        var editor = await UserWithAsync(host, "editor", "acme.view", "acme.edit");

        Assert.Equal(HttpStatusCode.Unauthorized, (await host.CreateClient().GetAsync("/api/plugins/acme/open")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await host.CreateClient().GetAsync("/api/plugins/acme/items")).StatusCode);

        Assert.Equal(HttpStatusCode.OK, (await nobody.GetAsync("/api/plugins/acme/open")).StatusCode); // no permission asked for
        Assert.Equal(HttpStatusCode.Forbidden, (await nobody.GetAsync("/api/plugins/acme/items")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await viewer.PostAsJsonAsync("/api/plugins/acme/items/add", new { name = "Gamma" })).StatusCode);

        var items = await viewer.GetFromJsonAsync<JsonArray>("/api/plugins/acme/items");
        Assert.Equal(["Alpha", "Beta"], items!.Select(i => (string?)i!["name"]));

        Assert.Equal(HttpStatusCode.OK, (await editor.PostAsJsonAsync("/api/plugins/acme/items/add", new { name = "Gamma" })).StatusCode);
        Assert.Equal(3, (await editor.GetFromJsonAsync<JsonArray>("/api/plugins/acme/items"))!.Count);
        Assert.Equal(HttpStatusCode.NoContent, (await editor.DeleteAsync("/api/plugins/acme/items")).StatusCode);
        Assert.Empty((await editor.GetFromJsonAsync<JsonArray>("/api/plugins/acme/items"))!);
    }

    [Fact]
    public async Task The_administrator_may_use_the_permissions_of_every_plugin_and_an_address_that_no_plugin_has_is_a_404()
    {
        using var host = CreateHost();
        var admin = host.CreateSignedInClient();

        var me = (await admin.GetFromJsonAsync<UserDto>("/api/account"))!;

        Assert.Equal([.. Permission.All, "acme.view", "acme.edit"], me.EffectivePermissions);
        Assert.Equal(HttpStatusCode.OK, (await admin.GetAsync("/api/plugins/acme/items")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await admin.GetAsync("/api/plugins/acme/nothing")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await admin.GetAsync("/api/plugins/ghost/items")).StatusCode);
    }

    [Fact]
    public async Task What_a_plugin_stores_in_the_configuration_reaches_it_and_a_data_source_is_built_from_it()
    {
        using var host = CreateHost(b =>
        {
            b.UseSetting("Spond:UseDemoData", "false");
            b.UseSetting("DataSource:Kind", "acme");
            b.UseSetting("Plugins:acme:ApiKey", "key-from-the-section");
        });
        var admin = host.CreateSignedInClient();

        var groups = await admin.GetFromJsonAsync<JsonArray>("/api/groups");
        var info = (await admin.GetFromJsonAsync<ServerInfoDto>("/api/info"))!;
        var config = await admin.GetFromJsonAsync<JsonObject>("/api/plugins/acme/config");

        Assert.Equal("key-from-the-section", (string?)Assert.Single(groups!)!["name"]); // the data source read its section
        Assert.Equal(("acme", false), (info.DataSource, info.IsDemoData));
        Assert.Equal("key-from-the-section", (string?)config!["apiKey"]);
    }

    // --- Plugins that do not run ------------------------------------------------------------------------------

    [Fact]
    public async Task A_data_source_whose_plugin_is_not_installed_answers_503_with_the_reason()
    {
        using var host = CreateHost([], b =>
        {
            b.UseSetting("Spond:UseDemoData", "false");
            b.UseSetting("DataSource:Kind", "acme");
        });

        var response = await host.CreateSignedInClient().GetAsync("/api/groups");

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Contains("plugin that is not installed", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task A_plugin_that_throws_when_it_starts_does_not_run_and_does_not_keep_the_api_from_starting()
    {
        using var host = CreateHost([PluginTestKit.AcmePackage(), PluginTestKit.Package(PluginTestKit.BrokenManifest)]);
        var admin = host.CreateSignedInClient();

        var list = (await admin.GetFromJsonAsync<List<PluginAdminDto>>("/api/plugin-admin"))!;

        Assert.Equal(["acme", "broken"], list.Select(p => p.Id).Order());
        Assert.True(list.Single(p => p.Id == "acme").IsRunning);
        var broken = list.Single(p => p.Id == "broken");
        Assert.False(broken.IsRunning);
        Assert.Contains("this plugin is broken", broken.Problem);
        Assert.Equal(["acme"], (await CatalogAsync(admin)).Plugins.Select(p => p.Id)); // what does not run adds nothing
    }

    [Fact]
    public async Task A_plugin_whose_assembly_is_no_assembly_is_reported_and_the_data_source_says_why()
    {
        var bad = PluginTestKit.Package(PluginTestKit.AcmeManifest with { Assembly = "Fake.dll" },
            new Dictionary<string, string> { ["Fake.dll"] = "this is no assembly", ["ui/settings.json"] = PluginTestKit.AcmeForm, ["ui/items.json"] = PluginTestKit.AcmePage, ["ui/about.json"] = PluginTestKit.AboutPage },
            withAssembly: false);
        using var host = CreateHost([bad], b =>
        {
            b.UseSetting("Spond:UseDemoData", "false");
            b.UseSetting("DataSource:Kind", "acme");
        });
        var admin = host.CreateSignedInClient();

        var plugin = Assert.Single((await admin.GetFromJsonAsync<List<PluginAdminDto>>("/api/plugin-admin"))!);
        var data = await admin.GetAsync("/api/groups");

        Assert.False(plugin.IsRunning);
        Assert.NotNull(plugin.Problem);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, data.StatusCode);
        Assert.Contains("does not run", await data.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task A_plugin_whose_data_source_clashes_with_an_earlier_one_does_not_run()
    {
        // Both plugins offer the data source "spond": the store refuses the second one, so the clash is made by hand in the index.
        PluginTestKit.Install(SettingsFile, PluginTestKit.SpondPackage());
        var other = PluginTestKit.Package(PluginTestKit.SpondManifest with { Id = "spond2" }, new Dictionary<string, string> { ["ui/settings.json"] = PluginTestKit.SpondForm });
        var package = PluginPackage.Open(other);
        var store = PluginTestKit.StoreOf(SettingsFile);
        var index = Path.Combine(store.Directory, PluginStore.IndexFile);
        var stored = new StoredPlugin("spond2", "1.0.0", "x", DateTimeOffset.UtcNow, "0123456789abcdef", "1.0.0-0123456");
        package.ExtractTo(Path.Combine(store.Directory, "spond2", stored.Folder));
        File.WriteAllText(index, JsonSerializer.Serialize(store.List().Append(stored).ToList(), PluginPackage.Json));

        using var second = CreateHost([]);
        var list = (await second.CreateSignedInClient().GetFromJsonAsync<List<PluginAdminDto>>("/api/plugin-admin"))!;

        Assert.Contains("offered by the plugin", list.Single(p => p.Id == "spond2").Problem);
        Assert.True(list.Single(p => p.Id == "spond").IsRunning);
    }

    // --- Roles and users ---------------------------------------------------------------------------------------

    [Fact]
    public async Task A_role_can_be_given_the_permissions_of_a_plugin_and_keeps_them_when_the_plugin_is_removed()
    {
        using var host = CreateHost();
        var admin = host.CreateSignedInClient();

        var role = await admin.PostAsJsonAsync("/api/roles", new RoleInputDto("Acme viewers", ["acme.view", Permission.ExamsView]));

        Assert.Equal(HttpStatusCode.Created, role.StatusCode);
        Assert.Equal([Permission.ExamsView, "acme.view"], (await role.Content.ReadFromJsonAsync<RoleDto>())!.Permissions);
        Assert.Equal(HttpStatusCode.BadRequest, (await admin.PostAsJsonAsync("/api/roles", new RoleInputDto("Bad", ["not a permission"]))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await admin.PostAsJsonAsync("/api/roles", new RoleInputDto("Bad2", ["members.nonsense.view"]))).StatusCode);

        // The plugin is removed: the role still holds what it was given, so that it works again when the plugin comes back.
        Assert.True(PluginTestKit.StoreOf(SettingsFile).Remove("acme"));
        using var without = CreateHost([]);
        var roles = (await without.CreateSignedInClient().GetFromJsonAsync<List<RoleDto>>("/api/roles"))!;
        Assert.Equal([Permission.ExamsView, "acme.view"], roles.Single(r => r.Name == "Acme viewers").Permissions);
        Assert.Equal(HttpStatusCode.NotFound, (await without.CreateSignedInClient().GetAsync("/api/plugins/acme/items")).StatusCode);
    }

    [Fact]
    public async Task Somebody_cannot_give_a_role_a_permission_of_a_plugin_that_they_do_not_have()
    {
        using var host = CreateHost([]);

        var response = await host.CreateSignedInClient().PostAsJsonAsync("/api/roles", new RoleInputDto("Ghosts", ["ghost.view"]));

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode); // the administrator has the permissions of the plugins that run, not those of plugins that are not there
    }

    [Fact]
    public async Task A_user_with_a_role_that_has_a_plugin_permission_may_use_it_and_one_without_may_not()
    {
        using var host = CreateHost();
        var admin = host.CreateSignedInClient();
        var role = (await (await admin.PostAsJsonAsync("/api/roles", new RoleInputDto("Acme viewers", ["acme.view"]))).Content.ReadFromJsonAsync<RoleDto>())!;
        Assert.Equal(HttpStatusCode.Created, (await admin.PostAsJsonAsync("/api/users", new UserInputDto("anna", Password, null, null, [role.Id]))).StatusCode);
        var anna = host.CreateSignedInClient("anna", Password);

        Assert.Equal(["acme.view"], (await anna.GetFromJsonAsync<UserDto>("/api/account"))!.EffectivePermissions);
        Assert.Equal(HttpStatusCode.OK, (await anna.GetAsync("/api/plugins/acme/items")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await anna.PostAsJsonAsync("/api/plugins/acme/items/add", new { name = "x" })).StatusCode);
    }

    [Fact]
    public async Task Somebody_cannot_give_a_permission_of_a_plugin_that_they_do_not_have_themselves()
    {
        using var host = CreateHost();
        var helper = await UserWithAsync(host, "helper", Permission.UsersView, Permission.UsersEdit, Permission.UsersGrant, "acme.view");

        var allowed = await helper.PostAsJsonAsync("/api/users", new UserInputDto("one", Password, null, null, [], ["acme.view"]));
        var refused = await helper.PostAsJsonAsync("/api/users", new UserInputDto("two", Password, null, null, [], ["acme.edit"]));

        Assert.Equal(HttpStatusCode.Created, allowed.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, refused.StatusCode);
    }

    // --- Installing, updating and removing ---------------------------------------------------------------------

    [Fact]
    public async Task A_plugin_is_installed_from_github_and_the_api_restarts_to_run_it()
    {
        using var host = CreateHost([]);
        var admin = host.CreateSignedInClient();

        var response = await admin.PostAsJsonAsync("/api/plugin-admin", new PluginInstallInputDto("https://github.com/acme/plugin"));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var changed = (await response.Content.ReadFromJsonAsync<PluginChangedDto>())!;
        Assert.True(changed.Restarting);
        Assert.Equal(("acme", "2.1.0", false, "https://github.com/acme/plugin"), (changed.Plugin!.Id, changed.Plugin.Version, changed.Plugin.IsRunning, changed.Plugin.Source)); // runs after the restart
        Assert.Equal(1, _restarter.Requests);
        Assert.Equal(["acme/plugin"], _fetcher.Asked);
        Assert.Equal("acme", Assert.Single((await admin.GetFromJsonAsync<List<PluginAdminDto>>("/api/plugin-admin"))!).Id);
        Assert.Equal(64, changed.Plugin.Sha256.Length);
    }

    [Fact]
    public async Task Installing_what_is_installed_already_changes_nothing_and_restarts_nothing_and_an_update_follows_a_new_release()
    {
        using var host = CreateHost([]);
        var admin = host.CreateSignedInClient();
        await admin.PostAsJsonAsync("/api/plugin-admin", new PluginInstallInputDto("https://github.com/acme/plugin"));

        var same = (await (await admin.PostAsJsonAsync("/api/plugin-admin", new PluginInstallInputDto("https://github.com/acme/plugin"))).Content.ReadFromJsonAsync<PluginChangedDto>())!;
        Assert.False(same.Restarting);
        Assert.Equal(1, _restarter.Requests);

        _fetcher.Package = (_, _) => PluginTestKit.AcmePackage("3.0.0");
        var update = (await (await admin.PostAsync("/api/plugin-admin/acme/update", null)).Content.ReadFromJsonAsync<PluginChangedDto>())!;
        Assert.True(update.Restarting);
        Assert.Equal("3.0.0", update.Plugin!.Version);
        Assert.Equal(2, _restarter.Requests);
        Assert.Equal(HttpStatusCode.NotFound, (await admin.PostAsync("/api/plugin-admin/nothing/update", null)).StatusCode);
    }

    [Fact]
    public async Task A_repository_that_cannot_be_used_is_a_bad_request_with_the_reason_and_nothing_is_installed_or_restarted()
    {
        using var host = CreateHost([]);
        var admin = host.CreateSignedInClient();
        _fetcher.Failure = "The repository has no release.";

        var noRelease = await admin.PostAsJsonAsync("/api/plugin-admin", new PluginInstallInputDto("https://github.com/acme/plugin"));
        var noAddress = await admin.PostAsJsonAsync("/api/plugin-admin", new PluginInstallInputDto("https://example.org/x"));
        var noBody = await admin.PostAsJsonAsync("/api/plugin-admin", new { });

        Assert.All([noRelease, noAddress, noBody], r => Assert.Equal(HttpStatusCode.BadRequest, r.StatusCode));
        Assert.Contains("no release", await noRelease.Content.ReadAsStringAsync());
        Assert.Empty((await admin.GetFromJsonAsync<List<PluginAdminDto>>("/api/plugin-admin"))!);
        Assert.Equal(0, _restarter.Requests);
    }

    [Fact]
    public async Task A_plugin_that_was_installed_by_hand_cannot_be_updated_from_github()
    {
        using var host = CreateHost([]);
        PluginTestKit.StoreOf(SettingsFile).Install(PluginPackage.Open(PluginTestKit.AcmePackage()), PluginStore.BundledSource, TimeProvider.System);

        var response = await host.CreateSignedInClient().PostAsync("/api/plugin-admin/acme/update", null);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("not installed from GitHub", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task A_plugin_is_removed_and_the_api_restarts_and_an_unknown_one_is_a_404()
    {
        using var host = CreateHost();
        var admin = host.CreateSignedInClient();

        var removed = await admin.DeleteAsync("/api/plugin-admin/acme");
        var again = await admin.DeleteAsync("/api/plugin-admin/acme");

        Assert.Equal(HttpStatusCode.OK, removed.StatusCode);
        Assert.True((await removed.Content.ReadFromJsonAsync<PluginChangedDto>())!.Restarting);
        Assert.Equal(1, _restarter.Requests);
        Assert.Equal(HttpStatusCode.NotFound, again.StatusCode);
        Assert.Empty((await admin.GetFromJsonAsync<List<PluginAdminDto>>("/api/plugin-admin"))!);
    }

    [Fact]
    public async Task The_list_says_where_a_plugin_came_from_what_it_offers_and_whether_it_runs()
    {
        using var host = CreateHost([PluginTestKit.AcmePackage(), PluginTestKit.SpondPackage()]);

        var list = (await host.CreateSignedInClient().GetFromJsonAsync<List<PluginAdminDto>>("/api/plugin-admin"))!;

        var acme = list.Single(p => p.Id == "acme");
        Assert.Equal((2, true, "Things of Acme."), (acme.PageCount, acme.IsRunning, acme.Description));
        Assert.Equal(["acme"], acme.DataSources);
        Assert.Equal(["Acme", "Spond"], list.Select(p => p.Name)); // by name
    }

    [Fact]
    public async Task A_plugin_that_was_installed_after_the_api_started_is_listed_as_not_running_yet_and_one_whose_files_are_gone_says_so()
    {
        using var host = CreateHost();
        var admin = host.CreateSignedInClient();
        var store = PluginTestKit.StoreOf(SettingsFile);
        store.Install(PluginPackage.Open(PluginTestKit.SpondPackage()), "https://github.com/test/spond", TimeProvider.System); // after the start
        var acme = store.List().Single(p => p.Id == "acme");
        File.Delete(Path.Combine(store.Directory, "acme", acme.Folder, PluginRules.ManifestFile));

        var list = (await admin.GetFromJsonAsync<List<PluginAdminDto>>("/api/plugin-admin"))!;

        var spond = list.Single(p => p.Id == "spond");
        Assert.False(spond.IsRunning);
        Assert.Null(spond.Problem); // it simply has not been started yet
        Assert.Equal("Spond", spond.Name);
        var gone = list.Single(p => p.Id == "acme");
        Assert.Equal("The files of the plugin cannot be read.", gone.Problem);
        Assert.Equal("Acme", gone.Name); // what the running plugin said about itself
    }

    [Fact]
    public async Task Installing_a_plugin_is_logged_with_what_it_was_and_never_the_package()
    {
        using var host = CreateHost([]);
        var admin = host.CreateSignedInClient();
        await admin.PostAsJsonAsync("/api/plugin-admin", new PluginInstallInputDto("https://github.com/acme/plugin"));
        await admin.DeleteAsync("/api/plugin-admin/acme");

        var log = (await admin.GetFromJsonAsync<AuditPageDto>("/api/audit?area=" + AuditAreas.Plugins))!.Entries;

        Assert.Equal([AuditActions.Delete, AuditActions.Create], log.Select(e => e.Action));
        var install = JsonNode.Parse(log[1].Details!)!;
        Assert.Equal(("acme", "2.1.0"), ((string?)install["id"], (string?)install["version"]));
    }

    // --- The settings form of a data source ---------------------------------------------------------------------

    private static async Task<SettingsInputDto> UnchangedAsync(HttpClient admin)
    {
        var s = (await admin.GetFromJsonAsync<SettingsDto>("/api/settings"))!;
        return new SettingsInputDto(s.DatabasePath, s.AccountsDatabasePath, s.UseDemoData, s.TimeZone, s.ExamKeyword, s.DefaultMinAttendancePercent, s.DefaultMinInvitedEvents, s.SyncEnabled, s.SyncIntervalMinutes);
    }

    [Fact]
    public async Task The_settings_show_a_form_the_plugin_describes_with_its_defaults_and_without_any_password()
    {
        using var host = CreateHost();

        var acme = (await host.CreateSignedInClient().GetFromJsonAsync<SettingsDto>("/api/settings"))!.PluginSettings!["acme"];

        Assert.Equal("eu", acme.Values["Region"].GetString()); // the default of the layout
        Assert.Equal(2, acme.Values["Retries"].GetDouble());
        Assert.False(acme.Values["Verbose"].GetBoolean());
        Assert.Empty(acme.Values["Rows"].EnumerateArray());
        Assert.DoesNotContain("ApiKey", acme.Values.Keys); // a password is never part of what is sent
        Assert.Empty(acme.SecretsSet);
    }

    [Fact]
    public async Task The_values_of_a_plugin_form_are_saved_into_its_section_the_passwords_encrypted_and_read_back_after_the_restart()
    {
        using var host = CreateHost();
        var admin = host.CreateSignedInClient();
        var settings = await UnchangedAsync(admin);

        var response = await admin.PutAsJsonAsync("/api/settings", settings with
        {
            PluginSettings = PluginTestKit.Settings("acme", ("ApiKey", "secret-api-key"), ("Region", "us"), ("Retries", 4), ("Verbose", true), ("Notes", "line one\nline two"),
                ("Rows", new object[] { new { Name = "first", Token = "row-token" }, new { Name = "", Token = "" } })),
        });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(1, _restarter.Requests);
        var text = File.ReadAllText(SettingsFile);
        Assert.DoesNotContain("secret-api-key", text);
        Assert.DoesNotContain("row-token", text);
        var written = JsonNode.Parse(text)!["Plugins"]!["acme"]!;
        Assert.StartsWith("enc:v1:", (string?)written["ApiKey"]);
        Assert.StartsWith("enc:v1:", (string?)written["Rows"]![0]!["Token"]);
        Assert.Equal(("us", "4", "true", "line one\nline two", "first"), ((string?)written["Region"], (string?)written["Retries"], (string?)written["Verbose"], (string?)written["Notes"], (string?)written["Rows"]![0]!["Name"]));
        Assert.Single(written["Rows"]!.AsArray()); // the blank row was dropped

        using var restarted = CreateHost([]);
        var again = (await restarted.CreateSignedInClient().GetFromJsonAsync<SettingsDto>("/api/settings"))!.PluginSettings!["acme"];
        Assert.Equal("us", again.Values["Region"].GetString());
        Assert.Equal(4, again.Values["Retries"].GetDouble());
        Assert.True(again.Values["Verbose"].GetBoolean());
        Assert.Equal(["ApiKey", "Rows/0/Token"], again.SecretsSet.Order());
        Assert.DoesNotContain("secret-api-key", await (await restarted.CreateSignedInClient().GetAsync("/api/settings")).Content.ReadAsStringAsync());
        // The plugin reads what the administrator typed: decrypted.
        Assert.Equal("secret-api-key", (string?)(await restarted.CreateSignedInClient().GetFromJsonAsync<JsonObject>("/api/plugins/acme/config"))!["apiKey"]);
    }

    [Fact]
    public async Task A_form_value_that_breaks_the_rules_of_its_layout_is_refused_and_nothing_is_saved()
    {
        using var host = CreateHost();
        var admin = host.CreateSignedInClient();
        var settings = await UnchangedAsync(admin);
        var tooMany = Enumerable.Range(0, 4).Select(i => (object)new { Name = $"n{i}" }).ToArray();

        var responses = new[]
        {
            await admin.PutAsJsonAsync("/api/settings", settings with { PluginSettings = PluginTestKit.Settings("acme", ("Region", "eu")) }), // the required API key is missing
            await admin.PutAsJsonAsync("/api/settings", settings with { PluginSettings = PluginTestKit.Settings("acme", ("ApiKey", "k"), ("Region", "mars")) }),
            await admin.PutAsJsonAsync("/api/settings", settings with { PluginSettings = PluginTestKit.Settings("acme", ("ApiKey", "k"), ("Region", "eu"), ("Retries", 6)) }),
            await admin.PutAsJsonAsync("/api/settings", settings with { PluginSettings = PluginTestKit.Settings("acme", ("ApiKey", "k"), ("Region", "eu"), ("Rows", tooMany)) }),
            await admin.PutAsJsonAsync("/api/settings", settings with { PluginSettings = PluginTestKit.Settings("acme", ("ApiKey", "k"), ("Region", "eu"), ("Verbose", "yes")) }),
        };

        Assert.All(responses, r => Assert.Equal(HttpStatusCode.BadRequest, r.StatusCode));
        Assert.Equal(0, _restarter.Requests);
        Assert.False(File.Exists(SettingsFile));
    }

    [Fact]
    public async Task A_data_source_of_a_plugin_is_chosen_with_its_id()
    {
        using var host = CreateHost();
        var admin = host.CreateSignedInClient();
        var settings = await UnchangedAsync(admin);

        var acme = await admin.PutAsJsonAsync("/api/settings", settings with
        {
            UseDemoData = false, DataSource = "acme", PluginSettings = PluginTestKit.Settings("acme", ("ApiKey", "k"), ("Region", "eu")),
        });

        Assert.Equal(HttpStatusCode.OK, acme.StatusCode);
        Assert.Equal("acme", (string?)JsonNode.Parse(File.ReadAllText(SettingsFile))!["DataSource"]!["Kind"]);
    }

    [Fact]
    public async Task The_settings_of_plugins_are_logged_as_names_never_values()
    {
        using var host = CreateHost();
        var admin = host.CreateSignedInClient();

        await admin.PutAsJsonAsync("/api/settings", (await UnchangedAsync(admin)) with { PluginSettings = PluginTestKit.Settings("acme", ("ApiKey", "audit-secret-1"), ("Region", "us")) });

        var entry = Assert.Single((await admin.GetFromJsonAsync<AuditPageDto>("/api/audit?area=" + AuditAreas.Settings))!.Entries);
        var changed = JsonNode.Parse(entry.Details!)!["changed"]!.AsObject();
        Assert.Equal("changed", (string?)changed["Plugins:acme:ApiKey"]);
        Assert.Equal("changed", (string?)changed["Plugins:acme:Region"]);
        Assert.DoesNotContain("audit-secret-1", entry.Details);
    }

    [Fact]
    public async Task The_setup_gets_the_forms_of_the_data_sources_before_anybody_can_sign_in_but_no_pages()
    {
        PluginTestKit.Install(SettingsFile, PluginTestKit.AcmePackage());
        using var host = factory.WithWebHostBuilder(builder =>
        {
            builder.UseSetting("Spond:UseDemoData", "true");
            builder.UseSetting("Sync:Enabled", "false");
            builder.UseSetting("DefaultData:Enabled", "false");
            builder.UseSetting("Database:ConnectionString", _planner.ConnectionString);
            builder.UseSetting("Accounts:ConnectionString", _accounts.ConnectionString);
            builder.UseSetting("Accounts:InitialAdminPassword", "");
            builder.UseSetting("Setup:SettingsFile", SettingsFile);
            builder.ConfigureServices(services => services.AddSingleton<IAppRestarter>(_restarter));
        });

        var status = (await host.CreateClient().GetFromJsonAsync<SetupStatusDto>("/api/setup"))!;

        Assert.True(status.NeedsSetup);
        var plugin = Assert.Single(status.Plugins!.Plugins);
        Assert.NotNull(Assert.Single(plugin.DataSources).Form);
        Assert.Empty(plugin.Pages);
    }

    [Fact]
    public async Task A_data_source_id_that_two_installed_plugins_claim_does_not_keep_the_settings_from_loading()
    {
        var other = PluginTestKit.Package(
            PluginTestKit.AcmeManifest with { Id = "other", Pages = null, Permissions = null, DataSources = [PluginTestKit.AcmeManifest.DataSources![0] with { Id = "other" }] },
            new Dictionary<string, string> { ["ui/settings.json"] = PluginTestKit.AcmeForm });
        using var host = CreateHost([PluginTestKit.AcmePackage(), other]);
        // Installing refuses it, so the clash is made by hand: the index of an older version, a folder copied from elsewhere.
        var store = PluginTestKit.StoreOf(SettingsFile);
        var manifestFile = Path.Combine(store.Directory, "other", store.List().Single(p => p.Id == "other").Folder, PluginRules.ManifestFile);
        var manifest = PluginPackage.ReadFile<PluginManifest>(manifestFile)!;
        File.WriteAllText(manifestFile, JsonSerializer.Serialize(manifest with { DataSources = [manifest.DataSources![0] with { Id = "acme" }] }, PluginPackage.Json));

        var response = await host.CreateSignedInClient().GetAsync("/api/settings");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("acme", (await response.Content.ReadFromJsonAsync<SettingsDto>())!.PluginSettings!.Keys);
        var plugins = await host.CreateSignedInClient().GetFromJsonAsync<List<PluginAdminDto>>("/api/plugin-admin");
        Assert.False(plugins!.Single(p => p.Id == "other").IsRunning); // the one that came second does not run
        Assert.True(plugins!.Single(p => p.Id == "acme").IsRunning);
    }

    // --- What the host does with the settings file ---------------------------------------------------------------

    [Fact]
    public async Task The_secret_places_of_an_installed_plugin_are_encrypted_in_a_settings_file_that_was_written_by_hand()
    {
        Directory.CreateDirectory(_folder);
        File.WriteAllText(SettingsFile, """{ "Plugins": { "acme": { "ApiKey": "typed-by-hand", "Rows": [ { "Name": "n", "Token": "tok-by-hand" } ] } } }""");
        using var host = CreateHost();

        var acme = (await host.CreateSignedInClient().GetFromJsonAsync<SettingsDto>("/api/settings"))!.PluginSettings!["acme"];

        Assert.Equal(["ApiKey", "Rows/0/Token"], acme.SecretsSet.Order());
        var text = File.ReadAllText(SettingsFile);
        Assert.DoesNotContain("typed-by-hand", text);
        Assert.DoesNotContain("tok-by-hand", text);
        Assert.Equal("typed-by-hand", host.Services.GetRequiredService<IConfiguration>()["Plugins:acme:ApiKey"]);
    }

    [Fact]
    public void The_settings_file_encrypts_the_places_it_is_told_about_and_each_entry_of_a_list_or_an_object()
    {
        Directory.CreateDirectory(_folder);
        File.WriteAllText(SettingsFile, """{ "Acme": { "Keys": { "a": "one", "b": "two" }, "Rows": [ { "Token": "t0" }, { "Token": "t1" }, "not an object" ], "Plain": "visible" } }""");
        var configuration = new ConfigurationManager();
        var protector = Api.Configuration.SettingsFile.CreateProtector(configuration, SettingsFile);
        var file = new Api.Configuration.SettingsFile(SettingsFile, protector);
        file.AddSecretPaths(["Acme:Keys:*", "Acme:Rows:*:Token"]);
        file.AddSecretPaths(["Acme:Keys:*"]); // twice is once

        file.AddTo(configuration);

        var text = File.ReadAllText(SettingsFile);
        foreach (var secret in new[] { "one", "two", "t0", "t1" }) Assert.DoesNotContain($"\"{secret}\"", text);
        Assert.Contains("visible", text);
        Assert.Equal(("one", "two", "t0", "t1", "visible"), (configuration["Acme:Keys:a"], configuration["Acme:Keys:b"], configuration["Acme:Rows:0:Token"], configuration["Acme:Rows:1:Token"], configuration["Acme:Plain"]));
    }
}
