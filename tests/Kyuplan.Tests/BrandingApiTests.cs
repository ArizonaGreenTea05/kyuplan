using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Kyuplan.Api.Configuration;
using Kyuplan.Contracts;

namespace Kyuplan.Tests;

/// <summary>
/// How the instance presents itself: the <c>Branding</c> settings of the API, served to the clients at <c>/api/branding</c> before any
/// login. These tests use the real API with the settings that ship in appsettings.json, and with files and values of their own.
/// </summary>
public sealed class BrandingApiTests(WebApplicationFactory<Program> factory) : IClassFixture<WebApplicationFactory<Program>>, IDisposable
{
    private readonly InMemoryDatabase _planner = new();
    private readonly InMemoryDatabase _accounts = new();
    private readonly string _folder = Directory.CreateTempSubdirectory("pp-branding-").FullName;

    public void Dispose()
    {
        _planner.Dispose();
        _accounts.Dispose();
        Directory.Delete(_folder, recursive: true);
    }

    /// <summary>Settings that switch every image off, for a test that sets only the ones it is about.</summary>
    private static readonly Dictionary<string, string> NoImages = new()
    {
        ["Logo"] = "", ["LogoOnDark"] = "", ["Icon"] = "",
    };

    private WebApplicationFactory<Program> CreateHost(Dictionary<string, string>? branding = null, Action<IWebHostBuilder>? configure = null) =>
        factory.WithWebHostBuilder(builder =>
        {
            builder.UseSetting("Spond:UseDemoData", "true");
            builder.UseSetting("Sync:Enabled", "false");
            builder.UseSetting("DefaultData:Enabled", "false"); // the tests fill their own tables; DefaultDataTests starts from the defaults
            builder.UseSetting("Database:ConnectionString", _planner.ConnectionString);
            TestAccounts.Configure(builder, _accounts);
            foreach (var (key, value) in branding ?? []) builder.UseSetting($"Branding:{key}", value);
            configure?.Invoke(builder);
        });

    /// <summary>A file of its own; the bytes say which one it is.</summary>
    private string Image(string name, string content)
    {
        var path = Path.Combine(_folder, name);
        File.WriteAllText(path, content);
        return path;
    }

    private static async Task<BrandingDto> GetBrandingAsync(HttpClient client)
    {
        var response = await client.GetAsync("/api/branding");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<BrandingDto>())!;
    }

    private static async Task<string?> ReadImageAsync(HttpClient client, string? url)
    {
        if (url is null) return null;

        var response = await client.GetAsync("/" + url);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return await response.Content.ReadAsStringAsync();
    }

    // --- The settings that ship ----------------------------------------------------------------------------------

    [Fact]
    public async Task The_shipped_settings_are_placeholders_and_all_their_files_are_there()
    {
        using var host = CreateHost();
        var client = host.CreateClient(); // no login

        var branding = await GetBrandingAsync(client);

        Assert.Equal("kyuplan", branding.ProductName);
        Assert.Equal("My Club", branding.OrganizationName);
        Assert.Equal("#C93A26", branding.AccentColor); // the colour appsettings.json ships
        string?[] urls = [branding.LogoUrl, branding.LogoOnDarkUrl, branding.IconUrl];
        Assert.All(urls, url => Assert.NotNull(url)); // every file that appsettings.json names was shipped with the API

        foreach (var (url, contentType) in new[]
                 {
                     (branding.LogoUrl, "image/svg+xml"), (branding.LogoOnDarkUrl, "image/svg+xml"), (branding.IconUrl, "image/svg+xml"),
                 })
        {
            var response = await client.GetAsync("/" + url);

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.Equal(contentType, response.Content.Headers.ContentType?.MediaType);
            Assert.NotEmpty(await response.Content.ReadAsByteArrayAsync());
            Assert.NotNull(response.Content.Headers.LastModified); // so a browser can ask "has it changed?"
        }
    }

    [Fact]
    public async Task The_branding_needs_neither_a_login_nor_the_api_key_but_the_rest_still_does()
    {
        using var host = CreateHost(configure: b => b.UseSetting("Api:ApiKey", "s3cret"));
        var client = host.CreateClient(); // no login, no key

        var branding = await GetBrandingAsync(client);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/" + branding.LogoUrl)).StatusCode);

        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/info")).StatusCode);
    }

    [Fact]
    public async Task A_browser_page_on_another_origin_may_read_the_branding_for_its_splash_screen()
    {
        using var host = CreateHost(configure: b => b.UseSetting("Api:AllowedOrigins:0", "http://localhost:5200"));
        var request = new HttpRequestMessage(HttpMethod.Get, "/api/branding");
        request.Headers.Add("Origin", "http://localhost:5200");

        var response = await host.CreateClient().SendAsync(request);

        Assert.Equal("http://localhost:5200", Assert.Single(response.Headers.GetValues("Access-Control-Allow-Origin")));
    }

    [Fact]
    public async Task An_image_the_instance_does_not_have_or_that_does_not_exist_is_a_404()
    {
        using var host = CreateHost(NoImages);
        var client = host.CreateClient();

        var branding = await GetBrandingAsync(client);

        Assert.All(new[] { branding.LogoUrl, branding.LogoOnDarkUrl, branding.IconUrl }, Assert.Null);
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync("/api/branding/logo")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync("/api/branding/nothing-of-that-name")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync("/api/branding/..%2Fappsettings.json")).StatusCode);
    }

    // --- What the settings mean ----------------------------------------------------------------------------------

    [Fact]
    public async Task Name_accent_and_images_are_what_the_settings_say()
    {
        using var host = CreateHost(new(NoImages)
        {
            ["ProductName"] = "  PlanIt ",
            ["OrganizationName"] = "Karate Club Beispiel",
            ["AccentColor"] = " #1A5fB4 ",
            ["Logo"] = Image("logo.svg", "svg-light"),
            ["LogoOnDark"] = Image("logo-dark.png", "png-dark"),
            ["Icon"] = Image("tab.ico", "ico"),
        });
        var client = host.CreateClient();

        var branding = await GetBrandingAsync(client);

        Assert.Equal("PlanIt", branding.ProductName);
        Assert.Equal("Karate Club Beispiel", branding.OrganizationName);
        Assert.Equal("#1A5fB4", branding.AccentColor);
        Assert.Equal("svg-light", await ReadImageAsync(client, branding.LogoUrl));
        Assert.Equal("png-dark", await ReadImageAsync(client, branding.LogoOnDarkUrl));
        Assert.Equal("image/svg+xml", (await client.GetAsync("/" + branding.LogoUrl)).Content.Headers.ContentType?.MediaType);
        Assert.Equal("ico", await ReadImageAsync(client, branding.IconUrl));
        Assert.Equal("image/x-icon", (await client.GetAsync("/" + branding.IconUrl)).Content.Headers.ContentType?.MediaType);
    }

    [Fact]
    public async Task Without_a_logo_for_dark_surfaces_the_one_for_light_surfaces_is_used_there_too()
    {
        using var host = CreateHost(new(NoImages) { ["Logo"] = Image("logo.svg", "the-logo") });
        var client = host.CreateClient();

        var branding = await GetBrandingAsync(client);

        Assert.Equal("the-logo", await ReadImageAsync(client, branding.LogoUrl));
        Assert.Equal("the-logo", await ReadImageAsync(client, branding.LogoOnDarkUrl));
        Assert.Null(branding.IconUrl);
    }

    [Fact]
    public async Task Settings_for_the_web_only_logos_of_earlier_versions_are_ignored()
    {
        // There is one logo for every client now; Branding:WebLogo and Branding:WebLogoOnDark no longer mean anything.
        using var host = CreateHost(new(NoImages) { ["WebLogo"] = Image("web.svg", "svg"), ["WebLogoOnDark"] = Image("web-dark.svg", "svg") });

        var branding = await GetBrandingAsync(host.CreateClient());

        Assert.Null(branding.LogoUrl);
        Assert.Null(branding.LogoOnDarkUrl);
    }

    [Fact]
    public async Task An_image_is_read_again_for_every_request_so_replacing_the_file_needs_no_restart()
    {
        var logo = Image("logo.png", "first");
        using var host = CreateHost(new(NoImages) { ["Logo"] = logo });
        var client = host.CreateClient();
        var branding = await GetBrandingAsync(client);
        Assert.Equal("first", await ReadImageAsync(client, branding.LogoUrl));

        File.WriteAllText(logo, "second");

        Assert.Equal("second", await ReadImageAsync(client, branding.LogoUrl));
    }

    [Fact]
    public async Task The_address_of_an_image_carries_a_version_that_changes_with_the_image_and_only_then()
    {
        // The clients ask again from time to time and compare the answers: a swapped logo must make a difference, an unchanged branding none.
        var logo = Image("logo.png", "first");
        using var host = CreateHost(new(NoImages) { ["Logo"] = logo });
        var client = host.CreateClient();

        var before = await GetBrandingAsync(client);

        Assert.StartsWith("api/branding/logo?v=", before.LogoUrl);
        Assert.StartsWith("api/branding/logo-on-dark?v=", before.LogoOnDarkUrl); // the light logo, shown there, too
        Assert.Null(before.IconUrl);
        Assert.Equal(before, await GetBrandingAsync(client));

        File.WriteAllText(logo, "second, with another length");
        var after = await GetBrandingAsync(client);

        Assert.NotEqual(before.LogoUrl, after.LogoUrl);
        Assert.NotEqual(before.LogoOnDarkUrl, after.LogoOnDarkUrl); // it shows the light logo, so it changed as well
        Assert.Equal("second, with another length", await ReadImageAsync(client, after.LogoUrl));
        Assert.Equal("second, with another length", await ReadImageAsync(client, after.LogoOnDarkUrl));
    }

    [Fact]
    public async Task A_browser_revalidates_an_image_and_notices_a_replaced_file_even_when_its_time_is_older()
    {
        // The address of an image never changes, so a browser that kept it for hours would show the old logo after the setting was changed.
        var logo = Image("logo.png", "first");
        using var host = CreateHost(new(NoImages) { ["Logo"] = logo });
        var client = host.CreateClient();
        var url = "/" + (await GetBrandingAsync(client)).LogoUrl;

        var first = await client.GetAsync(url);
        Assert.Equal("no-cache", first.Headers.CacheControl?.ToString());
        var tag = first.Headers.ETag!;

        var unchanged = new HttpRequestMessage(HttpMethod.Get, url);
        unchanged.Headers.IfNoneMatch.Add(tag);
        Assert.Equal(HttpStatusCode.NotModified, (await client.SendAsync(unchanged)).StatusCode);

        File.WriteAllText(logo, "second, with another length");
        File.SetLastWriteTimeUtc(logo, DateTime.UtcNow.AddDays(-30)); // what a file from a mounted volume or an image layer may look like
        var replaced = new HttpRequestMessage(HttpMethod.Get, url);
        replaced.Headers.IfNoneMatch.Add(tag);
        var response = await client.SendAsync(replaced);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("second, with another length", await response.Content.ReadAsStringAsync());
    }

    // --- Settings that cannot be used: never a reason for the API not to start -------------------------------------

    [Fact]
    public async Task A_file_that_is_missing_or_no_image_is_left_out()
    {
        using var host = CreateHost(new(NoImages)
        {
            ["Logo"] = Path.Combine(_folder, "missing.png"),
            ["Icon"] = Image("notes.txt", "not an image"),
            ["LogoOnDark"] = Image("logo-dark.png", "png-dark"),
        });
        var client = host.CreateClient();

        var branding = await GetBrandingAsync(client);

        Assert.Null(branding.LogoUrl);
        Assert.Null(branding.IconUrl);
        Assert.Equal("png-dark", await ReadImageAsync(client, branding.LogoOnDarkUrl));
    }

    [Theory]
    [InlineData("#abc")]
    [InlineData("#00A650")]
    public async Task A_colour_in_the_short_or_long_form_is_kept(string color)
    {
        using var host = CreateHost(new(NoImages) { ["AccentColor"] = color });

        Assert.Equal(color, (await GetBrandingAsync(host.CreateClient())).AccentColor);
    }

    [Theory]
    [InlineData("green")]
    [InlineData("#12345")]
    [InlineData("#gg0000")]
    [InlineData("red; background: url(x)")] // it ends up in CSS: nothing but a hex colour passes
    [InlineData("")]
    public async Task A_colour_that_is_none_falls_back_to_the_default(string color)
    {
        using var host = CreateHost(new(NoImages) { ["AccentColor"] = color });

        Assert.Equal(BrandingOptions.DefaultAccentColor, (await GetBrandingAsync(host.CreateClient())).AccentColor);
    }

    [Fact]
    public async Task Without_a_product_name_the_default_is_used_and_the_organisation_may_be_empty()
    {
        using var host = CreateHost(new(NoImages) { ["ProductName"] = " ", ["OrganizationName"] = "" });

        var branding = await GetBrandingAsync(host.CreateClient());

        Assert.Equal("kyuplan", branding.ProductName);
        Assert.Equal("", branding.OrganizationName);
    }

    // --- Demo data -----------------------------------------------------------------------------------------------

    [Theory]
    [InlineData("Karate Club Beispiel", "Karate Club Beispiel (Demo)")]
    [InlineData("", "Demo Dojo (Demo)")]
    public async Task The_demo_group_is_named_after_the_organisation(string organization, string expected)
    {
        using var host = CreateHost(new(NoImages) { ["OrganizationName"] = organization });

        var groups = await host.CreateSignedInClient().GetFromJsonAsync<List<GroupDto>>("/api/groups");

        Assert.Equal(expected, groups!.Single(g => g.Id == "demo-dojo").Name);
    }
}
