using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Options;
using Kyuplan.Client.Api;
using Kyuplan.Client.ViewModels;
using Kyuplan.Contracts;
using static Kyuplan.Tests.TestAccounts;

namespace Kyuplan.Tests;

/// <summary>Who may read which part of the documentation (<see cref="Documentation"/>).</summary>
public class DocumentationRulesTests
{
    private static string[] Audiences(params string[] permissions) => [.. Documentation.For(permissions.Contains).Select(a => a.Id)];

    [Fact]
    public void Everybody_may_read_the_user_guide_and_nothing_else_without_the_permissions_for_it()
    {
        Assert.Equal([Documentation.User], Audiences());
        Assert.Equal([Documentation.User], Audiences(Permission.MembersEdit, Permission.ExamsEdit));
    }

    [Theory]
    [InlineData(Permission.SettingsView)]
    [InlineData(Permission.UsersView)]
    [InlineData(Permission.RolesView)]
    [InlineData(Permission.BrandingEdit)]
    public void Whoever_may_look_at_the_administration_may_read_the_administrator_guide(string permission) =>
        Assert.Equal([Documentation.User, Documentation.Admin], Audiences(permission));

    [Fact]
    public void Only_whoever_may_change_the_settings_reads_the_deployment_guide() =>
        Assert.Equal([Documentation.User, Documentation.Admin, Documentation.Deployment], Audiences(Permission.SettingsEdit, Permission.SettingsView));

    [Fact]
    public void The_developer_guide_is_not_part_of_the_server() => Assert.Null(Documentation.Find("developer"));

    [Fact]
    public void The_sites_are_below_docs() => Assert.Equal("docs/admin/", Documentation.PathFor(Documentation.Admin));
}

/// <summary>The help links of the clients: the documentation the signed-in user may read, only where the server has any.</summary>
public class HelpLinksTests
{
    private readonly FakePlannerApi _api = new();
    private readonly ShellViewModel _shell;

    public HelpLinksTests() =>
        _shell = new ShellViewModel(_api, options: Options.Create(new PlannerClientOptions { BaseAddress = "https://planner.example.org/" }));

    [Fact]
    public async Task A_server_without_documentation_has_no_help_link()
    {
        _api.Account = Dto(1, "admin", "admin", [], true);
        await _shell.InitializeAsync();

        Assert.Empty(_shell.HelpLinks);
    }

    [Fact]
    public async Task Everybody_is_led_to_the_user_guide_and_an_administrator_to_the_other_guides_too()
    {
        _api.Info = _api.Info with { HasDocumentation = true };
        await _shell.InitializeAsync();

        Assert.Equal([new Uri("https://planner.example.org/docs/user/")], _shell.HelpLinks.Select(l => l.Address));

        _api.Account = Dto(1, "admin", "admin", [], true);
        var other = new ShellViewModel(_api, options: Options.Create(new PlannerClientOptions { BaseAddress = "https://planner.example.org/" }));
        await other.InitializeAsync();

        Assert.Equal(
            ["https://planner.example.org/docs/user/", "https://planner.example.org/docs/admin/", "https://planner.example.org/docs/deployment/"],
            other.HelpLinks.Select(l => l.Address.AbsoluteUri));
    }

    [Fact]
    public async Task The_links_change_with_the_permissions()
    {
        _api.Info = _api.Info with { HasDocumentation = true };
        await _shell.InitializeAsync();
        var changed = 0;
        _shell.PropertyChanged += (_, e) => changed += e.PropertyName == nameof(ShellViewModel.HelpLinks) ? 1 : 0;

        _shell.Session.User = Dto(2, "boss", "boss", [], true);

        Assert.Equal(3, _shell.HelpLinks.Count);
        Assert.True(changed > 0);
    }
}

/// <summary>The server shows each user the part of the documentation that applies to them, and nothing of the other parts.</summary>
public sealed class DocumentationApiTests(WebApplicationFactory<Program> factory) : IClassFixture<WebApplicationFactory<Program>>, IDisposable
{
    private const string Password = "a good password";

    private readonly InMemoryDatabase _database = new();
    private readonly InMemoryDatabase _accounts = new();
    private readonly string _folder = Directory.CreateTempSubdirectory("kyuplan-docs").FullName;

    public void Dispose()
    {
        _database.Dispose();
        _accounts.Dispose();
        Directory.Delete(_folder, true);
    }

    private WebApplicationFactory<Program> Host(bool withDocumentation = true)
    {
        if (withDocumentation)
        {
            Put("user/index.html", "<h1>User guide</h1>");
            Put("user/assets/site.css", "body{}");
            Put("user/exam-planning/index.html", "<h1>Exams</h1>");
            Put("admin/index.html", "<h1>Administrator guide</h1>");
            Put("deployment/index.html", "<h1>Deployment guide</h1>");
            Put("developer/index.html", "<h1>Developer guide</h1>");
            Put("secret.txt", "outside every site");
        }

        return factory.WithWebHostBuilder(builder =>
        {
            builder.UseSetting("Spond:UseDemoData", "true");
            builder.UseSetting("Sync:Enabled", "false");
            builder.UseSetting("DefaultData:Enabled", "false");
            builder.UseSetting("Database:ConnectionString", _database.ConnectionString);
            builder.UseSetting("Documentation:Directory", _folder);
            Configure(builder, _accounts);
        });
    }

    private void Put(string name, string content)
    {
        var path = Path.Combine(_folder, name);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
    }

    private static async Task<HttpClient> UserWithAsync(WebApplicationFactory<Program> host, string userName, params string[] permissions)
    {
        var response = await host.CreateSignedInClient().PostAsJsonAsync("/api/users", new UserInputDto(userName, Password, null, null, [], permissions));
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return host.CreateSignedInClient(userName, Password);
    }

    [Fact]
    public async Task The_documentation_needs_a_login()
    {
        var client = Host().CreateClient();

        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/docs/user/")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/docs/admin/")).StatusCode);
    }

    [Fact]
    public async Task Every_user_reads_the_user_guide_and_only_those_who_may_do_what_the_other_guides_describe_read_those()
    {
        using var host = Host();
        var plain = await UserWithAsync(host, "plain", Permission.MembersView);
        var manager = await UserWithAsync(host, "manager", Permission.UsersView);
        var admin = host.CreateSignedInClient();

        foreach (var client in new[] { plain, manager, admin })
        {
            var guide = await client.GetAsync("/docs/user/");
            Assert.Equal(HttpStatusCode.OK, guide.StatusCode);
            Assert.Equal("<h1>User guide</h1>", await guide.Content.ReadAsStringAsync());
            Assert.Equal("text/css", (await client.GetAsync("/docs/user/assets/site.css")).Content.Headers.ContentType?.MediaType);
        }

        Assert.Equal(HttpStatusCode.Forbidden, (await plain.GetAsync("/docs/admin/")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await plain.GetAsync("/docs/deployment/")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await manager.GetAsync("/docs/admin/")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await manager.GetAsync("/docs/deployment/")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await admin.GetAsync("/docs/deployment/")).StatusCode);
    }

    [Fact]
    public async Task Only_the_documentation_may_run_the_inline_script_that_sets_its_colour_theme()
    {
        using var host = Host();
        var client = host.CreateSignedInClient();

        var docs = (await client.GetAsync("/docs/user/")).Headers.GetValues("Content-Security-Policy").Single();
        var api = (await client.GetAsync("/api/info")).Headers.GetValues("Content-Security-Policy").Single();

        Assert.Contains("script-src 'self' 'unsafe-inline'", docs);
        Assert.DoesNotContain("unsafe-inline'; style", api);
        Assert.Contains("script-src 'self' 'wasm-unsafe-eval'", api);
    }

    [Fact]
    public async Task What_is_not_a_site_of_an_audience_is_not_served()
    {
        using var host = Host();
        var admin = host.CreateSignedInClient();

        Assert.Equal(HttpStatusCode.NotFound, (await admin.GetAsync("/docs/developer/")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await admin.GetAsync("/docs/user/missing.html")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await admin.GetAsync("/docs/user/..%2Fsecret.txt")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await admin.GetAsync("/docs/user/%2e%2e/secret.txt")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await admin.GetAsync("/docs/user/..%5Csecret.txt")).StatusCode);
    }

    [Fact]
    public async Task A_page_asked_for_without_the_closing_slash_is_sent_to_the_one_with()
    {
        using var host = Host();
        var response = await host.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false }).SignedIn().GetAsync("/docs/user/exam-planning");

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Equal("/docs/user/exam-planning/", response.Headers.Location?.OriginalString);
    }

    [Fact]
    public async Task The_start_of_the_help_is_the_user_guide()
    {
        using var host = Host();

        var response = await host.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false }).SignedIn().GetAsync("/docs");

        Assert.Equal("/docs/user/", response.Headers.Location?.OriginalString);
    }

    [Fact]
    public async Task The_server_says_whether_it_has_documentation()
    {
        using var with = Host();
        Assert.True((await new PlannerApiClient(with.CreateSignedInClient()).GetInfoAsync()).HasDocumentation);

        Directory.Delete(Path.Combine(_folder, "user"), true);
        Assert.False((await new PlannerApiClient(with.CreateSignedInClient()).GetInfoAsync()).HasDocumentation);
    }
}
