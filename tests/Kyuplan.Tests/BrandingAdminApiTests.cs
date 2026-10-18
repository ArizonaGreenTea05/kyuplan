using System.Net;
using System.Net.Http.Json;
using System.Text;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Kyuplan.Api.Infrastructure;
using Kyuplan.Contracts;

namespace Kyuplan.Tests;

/// <summary>
/// Administrators changing the branding: who may, what is accepted, how it overlays the settings of the API, and that it lasts.
/// Real API, real (in-memory) SQLite databases. Reading the branding stays open to everybody (<see cref="BrandingApiTests"/>).
/// </summary>
public sealed class BrandingAdminApiTests(WebApplicationFactory<Program> factory) : IClassFixture<WebApplicationFactory<Program>>, IDisposable
{
    private const string UserPassword = "a good password";

    private readonly InMemoryDatabase _planner = new();
    private readonly InMemoryDatabase _accounts = new();
    private readonly string _folder = Directory.CreateTempSubdirectory("pp-branding-admin-").FullName;

    public void Dispose()
    {
        _planner.Dispose();
        _accounts.Dispose();
        Directory.Delete(_folder, recursive: true);
    }

    private static readonly Dictionary<string, string> NoImages = new()
    {
        ["Logo"] = "", ["LogoOnDark"] = "", ["Icon"] = "",
    };

    /// <summary>A host on the databases of this test; a second host on the same ones is "the API after a restart".</summary>
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

    // --- Images made of the first bytes that tell what they are -------------------------------------------------

    private static byte[] Png(byte marker = 1) => [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, marker, 2, 3, 4];

    private static byte[] Jpeg() => [0xFF, 0xD8, 0xFF, 0xE0, 0, 16, 0x4A, 0x46, 0x49, 0x46];

    private static byte[] Gif() => Encoding.ASCII.GetBytes("GIF89a\x01\x00\x01\x00");

    private static byte[] Bmp() => [(byte)'B', (byte)'M', .. new byte[20]];

    private static byte[] Webp() => [.. "RIFF"u8, 0x10, 0, 0, 0, .. "WEBPVP8 "u8];

    private static byte[] Ico() => [0, 0, 1, 0, 1, 0, 16, 16, 0, 0, 1, 0];

    private static byte[] Svg(string text = "<?xml version=\"1.0\"?>\n<!-- a logo -->\n<svg xmlns=\"http://www.w3.org/2000/svg\" viewBox=\"0 0 1 1\"/>") =>
        Encoding.UTF8.GetBytes(text);

    private static Task<HttpResponseMessage> PutImageAsync(HttpClient client, string slot, byte[] content, string contentType = "application/octet-stream")
    {
        var body = new ByteArrayContent(content);
        body.Headers.ContentType = new(contentType);
        return client.PutAsync($"/api/branding/{slot}", body);
    }

    private static async Task<T> ReadAsync<T>(HttpResponseMessage response)
    {
        Assert.True(response.IsSuccessStatusCode, $"{(int)response.StatusCode}: {await response.Content.ReadAsStringAsync()}");
        return (await response.Content.ReadFromJsonAsync<T>())!;
    }

    private static Task<BrandingDto> BrandingAsync(HttpClient client) => client.GetFromJsonAsync<BrandingDto>("/api/branding")!;

    private static async Task<byte[]> ImageAsync(HttpClient client, string? url)
    {
        Assert.NotNull(url);
        var response = await client.GetAsync("/" + url);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return await response.Content.ReadAsByteArrayAsync();
    }

    private static string? OwnUrl(BrandingSettingsDto settings, string slot) => settings.Images.Single(i => i.Slot == slot).Url;

    private static BrandingInputDto Input(string name = "PlanIt", string organization = "Karate Club Beispiel", string color = "#1a5fb4") =>
        new(name, organization, color);

    // --- Who may ---------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Without_a_login_nothing_can_be_read_or_changed_beyond_the_public_branding()
    {
        using var host = CreateHost();
        var anonymous = host.CreateClient();

        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync("/api/branding/settings")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.PutAsJsonAsync("/api/branding", Input())).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await PutImageAsync(anonymous, "logo", Png())).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.DeleteAsync("/api/branding/logo")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.DeleteAsync("/api/branding")).StatusCode);

        Assert.Equal(HttpStatusCode.OK, (await anonymous.GetAsync("/api/branding")).StatusCode); // reading stays open
    }

    [Fact]
    public async Task A_user_who_is_no_administrator_may_read_the_branding_but_not_change_it_or_see_how_it_is_edited()
    {
        using var host = CreateHost();
        var admin = host.CreateSignedInClient();
        Assert.Equal(HttpStatusCode.Created, (await admin.PostAsJsonAsync("/api/users", TestAccounts.Input("anna", UserPassword, false))).StatusCode);
        var anna = host.CreateSignedInClient("anna", UserPassword);

        Assert.Equal(HttpStatusCode.OK, (await anna.GetAsync("/api/branding")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await anna.GetAsync("/api/branding/settings")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await anna.PutAsJsonAsync("/api/branding", Input())).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await PutImageAsync(anna, "logo", Png())).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await anna.DeleteAsync("/api/branding/logo")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await anna.DeleteAsync("/api/branding")).StatusCode);

        Assert.Equal("My Club", (await BrandingAsync(admin)).OrganizationName); // nothing was changed
    }

    [Fact]
    public async Task The_settings_route_is_not_mistaken_for_an_image_of_that_name()
    {
        using var host = CreateHost();

        var response = await host.CreateSignedInClient().GetAsync("/api/branding/settings");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("application/json", response.Content.Headers.ContentType?.MediaType);
    }

    [Fact]
    public async Task With_an_api_key_only_reading_is_exempt_and_changing_needs_the_key_on_top_of_the_login()
    {
        using var host = CreateHost(configure: b => b.UseSetting("Api:ApiKey", "s3cret"));
        var withoutKey = host.CreateSignedInClient();
        var withKey = host.CreateSignedInClient();
        withKey.DefaultRequestHeaders.Add("X-Api-Key", "s3cret");

        Assert.Equal(HttpStatusCode.OK, (await withoutKey.GetAsync("/api/branding")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await withoutKey.GetAsync("/api/branding/logo")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await withoutKey.GetAsync("/api/branding/settings")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await withoutKey.PutAsJsonAsync("/api/branding", Input())).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await PutImageAsync(withoutKey, "logo", Png())).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await withoutKey.DeleteAsync("/api/branding")).StatusCode);

        Assert.Equal(HttpStatusCode.OK, (await withKey.GetAsync("/api/branding/settings")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await withKey.PutAsJsonAsync("/api/branding", Input())).StatusCode);
    }

    // --- The settings as they are edited ---------------------------------------------------------------------------

    [Fact]
    public async Task Nothing_was_saved_so_the_settings_of_the_api_show_and_the_slots_name_their_own_images()
    {
        using var host = CreateHost();

        var settings = await ReadAsync<BrandingSettingsDto>(await host.CreateSignedInClient().GetAsync("/api/branding/settings"));

        Assert.False(settings.IsCustomized);
        Assert.Equal("kyuplan", settings.ProductName);
        Assert.Equal("My Club", settings.OrganizationName);
        Assert.Equal("#C93A26", settings.AccentColor); // the colour appsettings.json ships
        Assert.Equal(BrandingSlots.All, settings.Images.Select(i => i.Slot));
        Assert.All(settings.Images, image => Assert.Equal($"api/branding/{image.Slot}", image.Url)); // the shipped settings name all three
    }

    [Fact]
    public async Task A_slot_with_no_image_of_its_own_says_so_even_when_another_slot_stands_in_for_it()
    {
        using var host = CreateHost(new(NoImages) { ["Logo"] = Path.Combine(AppContext.BaseDirectory, "branding", "logo-black.svg") });
        var admin = host.CreateSignedInClient();

        var settings = await ReadAsync<BrandingSettingsDto>(await admin.GetAsync("/api/branding/settings"));
        var branding = await BrandingAsync(admin);

        Assert.NotNull(OwnUrl(settings, BrandingSlots.Logo));
        Assert.Null(OwnUrl(settings, BrandingSlots.LogoOnDark)); // it has none of its own ...
        Assert.NotNull(branding.LogoOnDarkUrl);                  // ... but the clients are shown the logo in its place
    }

    // --- Texts and colour ------------------------------------------------------------------------------------------

    [Fact]
    public async Task Saved_texts_and_colour_replace_the_settings_for_every_client_and_are_trimmed()
    {
        using var host = CreateHost();
        var admin = host.CreateSignedInClient();

        var response = await admin.PutAsJsonAsync("/api/branding", Input("  PlanIt ", " Karate Club Beispiel ", " #1A5fb4 "));

        var settings = await ReadAsync<BrandingSettingsDto>(response);
        Assert.True(settings.IsCustomized);
        Assert.Equal("PlanIt", settings.ProductName);
        Assert.Equal("Karate Club Beispiel", settings.OrganizationName);
        Assert.Equal("#1A5fb4", settings.AccentColor);

        var branding = await BrandingAsync(host.CreateClient()); // no login: what a client shows before anybody has signed in
        Assert.Equal("PlanIt", branding.ProductName);
        Assert.Equal("Karate Club Beispiel", branding.OrganizationName);
        Assert.Equal("#1A5fb4", branding.AccentColor);
    }

    [Fact]
    public async Task The_organisation_may_be_saved_empty_even_when_the_settings_name_one()
    {
        using var host = CreateHost();
        var admin = host.CreateSignedInClient();

        await ReadAsync<BrandingSettingsDto>(await admin.PutAsJsonAsync("/api/branding", Input(organization: "")));

        Assert.Equal("", (await BrandingAsync(admin)).OrganizationName);
    }

    [Theory]
    [InlineData("", "Club", "#1a5fb4", "ProductName")]
    [InlineData("   ", "Club", "#1a5fb4", "ProductName")]
    [InlineData("Name", "Club", "green", "AccentColor")]
    [InlineData("Name", "Club", "#12345", "AccentColor")]
    [InlineData("Name", "Club", "red; background: url(x)", "AccentColor")] // it ends up in CSS: nothing but a hex colour passes
    [InlineData("Name", "Club", "", "AccentColor")]
    [InlineData("Na\nme", "Club", "#1a5fb4", "ProductName")]
    [InlineData("Name", "Cl\tub", "#1a5fb4", "OrganizationName")]
    public async Task What_is_not_a_name_or_a_colour_is_refused_and_nothing_is_saved(string name, string organization, string color, string field)
    {
        using var host = CreateHost();
        var admin = host.CreateSignedInClient();

        var response = await admin.PutAsJsonAsync("/api/branding", new BrandingInputDto(name, organization, color));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var problem = await response.Content.ReadAsStringAsync();
        Assert.Contains(field, problem, StringComparison.OrdinalIgnoreCase);
        Assert.False((await ReadAsync<BrandingSettingsDto>(await admin.GetAsync("/api/branding/settings"))).IsCustomized);
    }

    [Fact]
    public async Task Names_that_are_too_long_are_refused()
    {
        using var host = CreateHost();
        var admin = host.CreateSignedInClient();

        var tooLongProduct = await admin.PutAsJsonAsync("/api/branding", Input(name: new string('x', BrandingRules.MaxProductNameLength + 1)));
        var tooLongOrganization = await admin.PutAsJsonAsync("/api/branding", Input(organization: new string('x', BrandingRules.MaxOrganizationNameLength + 1)));
        var longest = await admin.PutAsJsonAsync("/api/branding", Input(new string('x', BrandingRules.MaxProductNameLength), new string('y', BrandingRules.MaxOrganizationNameLength)));

        Assert.Equal(HttpStatusCode.BadRequest, tooLongProduct.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, tooLongOrganization.StatusCode);
        Assert.Equal(HttpStatusCode.OK, longest.StatusCode);
    }

    [Fact]
    public async Task A_missing_body_is_a_bad_request_not_a_crash()
    {
        using var host = CreateHost();

        var response = await host.CreateSignedInClient().PutAsync("/api/branding", content: null);

        Assert.True((int)response.StatusCode is >= 400 and < 500, $"answered {(int)response.StatusCode}");
    }

    [Fact]
    public async Task What_was_saved_is_still_there_after_the_api_restarted()
    {
        using (var first = CreateHost())
        {
            var admin = first.CreateSignedInClient();
            await ReadAsync<BrandingSettingsDto>(await admin.PutAsJsonAsync("/api/branding", Input()));
            await ReadAsync<BrandingSettingsDto>(await PutImageAsync(admin, "logo", Png(9)));
        }

        using var second = CreateHost();
        var client = second.CreateClient();

        var branding = await BrandingAsync(client);
        Assert.Equal("PlanIt", branding.ProductName);
        Assert.Equal(Png(9), await ImageAsync(client, branding.LogoUrl));
    }

    [Fact]
    public async Task Saved_values_win_over_what_the_settings_say_later_but_a_reset_brings_the_settings_back()
    {
        using (var saved = CreateHost(new(NoImages) { ["ProductName"] = "FromSettings", ["OrganizationName"] = "Settings Club", ["AccentColor"] = "#00a650" }))
            await ReadAsync<BrandingSettingsDto>(await saved.CreateSignedInClient().PutAsJsonAsync("/api/branding", Input()));

        using var restarted = CreateHost(new(NoImages) { ["ProductName"] = "ChangedInSettings" });
        var admin = restarted.CreateSignedInClient();
        Assert.Equal("PlanIt", (await BrandingAsync(admin)).ProductName); // what an administrator saved is not overruled by the settings

        var reset = await ReadAsync<BrandingSettingsDto>(await admin.DeleteAsync("/api/branding"));

        Assert.False(reset.IsCustomized);
        Assert.Equal("ChangedInSettings", reset.ProductName);
        Assert.Equal("ChangedInSettings", (await BrandingAsync(admin)).ProductName);
    }

    // --- Images ----------------------------------------------------------------------------------------------------

    [Fact]
    public async Task An_uploaded_image_is_served_to_everybody_in_place_of_the_one_of_the_settings()
    {
        using var host = CreateHost();
        var admin = host.CreateSignedInClient();
        var before = await ImageAsync(host.CreateClient(), (await BrandingAsync(admin)).LogoUrl);

        var settings = await ReadAsync<BrandingSettingsDto>(await PutImageAsync(admin, "logo", Png(42)));

        Assert.True(settings.IsCustomized);
        Assert.Equal("api/branding/logo", OwnUrl(settings, BrandingSlots.Logo));
        var anonymous = host.CreateClient();
        var response = await anonymous.GetAsync("/api/branding/logo");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("image/png", response.Content.Headers.ContentType?.MediaType);
        var after = await response.Content.ReadAsByteArrayAsync();
        Assert.Equal(Png(42), after);
        Assert.NotEqual(before, after);
    }

    [Fact]
    public async Task Every_change_shows_in_the_public_answer_so_that_a_client_that_asks_again_notices_it()
    {
        using var host = CreateHost(NoImages);
        var admin = host.CreateSignedInClient();
        var anonymous = host.CreateClient();
        var start = await BrandingAsync(anonymous);
        Assert.Equal(start, await BrandingAsync(anonymous)); // nothing changed: the answer did not either

        await ReadAsync<BrandingSettingsDto>(await admin.PutAsJsonAsync("/api/branding", Input()));
        var texts = await BrandingAsync(anonymous);
        Assert.NotEqual(start, texts);
        Assert.Equal(("PlanIt", "Karate Club Beispiel", "#1a5fb4"), (texts.ProductName, texts.OrganizationName, texts.AccentColor));

        await ReadAsync<BrandingSettingsDto>(await PutImageAsync(admin, "logo", Png(1)));
        var first = await BrandingAsync(anonymous);
        Assert.NotNull(first.LogoUrl);
        Assert.NotNull(first.LogoOnDarkUrl);

        await Task.Delay(20); // the version of an upload is its time, and two uploads should not share one
        await ReadAsync<BrandingSettingsDto>(await PutImageAsync(admin, "logo", Png(2))); // the same length, another image
        var second = await BrandingAsync(anonymous);
        Assert.NotEqual(first.LogoUrl, second.LogoUrl);
        Assert.NotEqual(first.LogoOnDarkUrl, second.LogoOnDarkUrl);
        Assert.Equal(Png(2), await ImageAsync(anonymous, second.LogoUrl));

        await admin.DeleteAsync("/api/branding/logo");
        Assert.Null((await BrandingAsync(anonymous)).LogoUrl);

        await admin.DeleteAsync("/api/branding");
        Assert.Equal(start, await BrandingAsync(anonymous)); // back to the settings: the answer is the one from the start
    }

    [Theory]
    [InlineData(BrandingSlots.Logo, "png", "image/png")]
    [InlineData(BrandingSlots.Logo, "jpeg", "image/jpeg")]
    [InlineData(BrandingSlots.LogoOnDark, "gif", "image/gif")]
    [InlineData(BrandingSlots.LogoOnDark, "bmp", "image/bmp")]
    [InlineData(BrandingSlots.Logo, "svg", "image/svg+xml")]
    [InlineData(BrandingSlots.LogoOnDark, "svg", "image/svg+xml")]
    [InlineData(BrandingSlots.Icon, "svg", "image/svg+xml")]
    [InlineData(BrandingSlots.Icon, "png", "image/png")]
    [InlineData(BrandingSlots.Icon, "ico", "image/x-icon")]
    public async Task Each_slot_takes_the_formats_it_is_meant_for_and_serves_them_as_what_they_are(string slot, string format, string contentType)
    {
        using var host = CreateHost(NoImages);
        var admin = host.CreateSignedInClient();

        var response = await PutImageAsync(admin, slot, Sample(format), contentType: "application/octet-stream");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var served = await admin.GetAsync($"/api/branding/{slot}");
        Assert.Equal(contentType, served.Content.Headers.ContentType?.MediaType);
        Assert.Equal(Sample(format), await served.Content.ReadAsByteArrayAsync());
    }

    private static byte[] Sample(string format) => format switch
    {
        "png" => Png(), "jpeg" => Jpeg(), "gif" => Gif(), "bmp" => Bmp(), "webp" => Webp(), "svg" => Svg(), "ico" => Ico(),
        _ => throw new ArgumentOutOfRangeException(nameof(format)),
    };

    [Theory]
    [InlineData(BrandingSlots.Logo, "webp")]      // the desktop client cannot draw it
    [InlineData(BrandingSlots.Logo, "ico")]
    [InlineData(BrandingSlots.LogoOnDark, "webp")]
    [InlineData(BrandingSlots.LogoOnDark, "ico")]
    [InlineData(BrandingSlots.Icon, "jpeg")]
    [InlineData(BrandingSlots.Icon, "gif")]
    public async Task An_image_of_a_format_the_slot_does_not_take_is_refused_and_the_message_says_which_it_takes(string slot, string format)
    {
        using var host = CreateHost(NoImages);
        var admin = host.CreateSignedInClient();

        var response = await PutImageAsync(admin, slot, Sample(format));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("must be", await response.Content.ReadAsStringAsync());
        Assert.False((await ReadAsync<BrandingSettingsDto>(await admin.GetAsync("/api/branding/settings"))).IsCustomized);
        Assert.Equal(HttpStatusCode.NotFound, (await admin.GetAsync($"/api/branding/{slot}")).StatusCode);
    }

    [Theory]
    [InlineData("this is no image at all")]
    [InlineData("<html><script>alert(1)</script></html>")]
    [InlineData("<script>alert(1)</script><svg/>")]  // the first element must be the svg
    [InlineData("<svgx/>")]
    [InlineData("")]
    public async Task What_is_not_an_image_is_refused_whatever_the_content_type_claims(string text)
    {
        using var host = CreateHost(NoImages);
        var admin = host.CreateSignedInClient();

        var asPng = await PutImageAsync(admin, "logo", Encoding.UTF8.GetBytes(text), contentType: "image/png");
        var asSvg = await PutImageAsync(admin, "logo", Encoding.UTF8.GetBytes(text), contentType: "image/svg+xml");

        Assert.Equal(HttpStatusCode.BadRequest, asPng.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, asSvg.StatusCode);
    }

    [Fact]
    public async Task A_picture_that_claims_to_be_text_is_still_what_its_bytes_say()
    {
        using var host = CreateHost(NoImages);
        var admin = host.CreateSignedInClient();

        var response = await PutImageAsync(admin, "logo", Png(), contentType: "text/html");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("image/png", (await admin.GetAsync("/api/branding/logo")).Content.Headers.ContentType?.MediaType);
    }

    [Fact]
    public async Task An_image_of_the_largest_size_is_accepted_and_one_byte_more_is_not()
    {
        using var host = CreateHost(NoImages);
        var admin = host.CreateSignedInClient();
        var largest = new byte[BrandingRules.MaxImageBytes];
        Png().CopyTo(largest, 0);

        Assert.Equal(HttpStatusCode.OK, (await PutImageAsync(admin, "logo", largest)).StatusCode);

        var tooLarge = new byte[BrandingRules.MaxImageBytes + 1];
        Png().CopyTo(tooLarge, 0);
        var response = await PutImageAsync(admin, "logo", tooLarge);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("larger than", await response.Content.ReadAsStringAsync());
        Assert.Equal(largest.Length, (await ImageAsync(admin, "api/branding/logo")).Length); // the one before stays
    }

    [Fact]
    public async Task An_unknown_slot_is_a_404_for_every_change()
    {
        using var host = CreateHost();
        var admin = host.CreateSignedInClient();

        Assert.Equal(HttpStatusCode.NotFound, (await PutImageAsync(admin, "banner", Png())).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await admin.DeleteAsync("/api/branding/banner")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await PutImageAsync(admin, "..%2Fappsettings.json", Png())).StatusCode);
    }

    [Fact]
    public async Task An_empty_upload_is_refused()
    {
        using var host = CreateHost(NoImages);

        var response = await host.CreateSignedInClient().PutAsync("/api/branding/logo", new ByteArrayContent([]));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task An_svg_may_start_with_a_byte_order_mark_a_declaration_comments_and_a_doctype()
    {
        using var host = CreateHost(NoImages);
        var admin = host.CreateSignedInClient();
        var svg = Encoding.UTF8.GetPreamble()
            .Concat(Svg("<?xml version=\"1.0\" encoding=\"UTF-8\"?>\n<!-- made with something -->\n<!DOCTYPE svg PUBLIC \"-//W3C//DTD SVG 1.1//EN\" \"http://www.w3.org/Graphics/SVG/1.1/DTD/svg11.dtd\">\n<SVG xmlns=\"http://www.w3.org/2000/svg\"></SVG>"))
            .ToArray();

        var response = await PutImageAsync(admin, "logo", svg);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    // --- What a client may do with the images ---------------------------------------------------------------

    [Fact]
    public async Task An_uploaded_image_is_checked_again_by_a_browser_every_time_and_may_not_run_anything_when_opened_on_its_own()
    {
        using var host = CreateHost(NoImages);
        var admin = host.CreateSignedInClient();
        await PutImageAsync(admin, "logo", Svg("<svg xmlns=\"http://www.w3.org/2000/svg\"><script>alert(1)</script></svg>"));
        var client = host.CreateClient();

        var response = await client.GetAsync("/api/branding/logo");

        Assert.Equal("no-cache", response.Headers.CacheControl?.ToString());
        Assert.Equal("nosniff", Assert.Single(response.Headers.GetValues("X-Content-Type-Options")));
        var policy = Assert.Single(response.Headers.GetValues("Content-Security-Policy"));
        Assert.Contains("sandbox", policy);
        Assert.Contains("default-src 'none'", policy);
        Assert.NotNull(response.Headers.ETag);
        Assert.NotNull(response.Content.Headers.LastModified);
    }

    [Fact]
    public async Task A_browser_gets_a_304_while_the_image_is_the_same_and_the_new_one_after_it_was_replaced()
    {
        using var host = CreateHost(NoImages);
        var admin = host.CreateSignedInClient();
        await PutImageAsync(admin, "logo", Png(1));
        var client = host.CreateClient();
        var tag = (await client.GetAsync("/api/branding/logo")).Headers.ETag!;

        var unchanged = new HttpRequestMessage(HttpMethod.Get, "/api/branding/logo");
        unchanged.Headers.IfNoneMatch.Add(tag);
        Assert.Equal(HttpStatusCode.NotModified, (await client.SendAsync(unchanged)).StatusCode);

        await PutImageAsync(admin, "logo", Png(2, 3)); // another length: another tag, even within the same clock tick
        var replaced = new HttpRequestMessage(HttpMethod.Get, "/api/branding/logo");
        replaced.Headers.IfNoneMatch.Add(tag);
        var response = await client.SendAsync(replaced);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(Png(2, 3), await response.Content.ReadAsByteArrayAsync());
        Assert.NotEqual(tag, response.Headers.ETag);
    }

    private static byte[] Png(byte first, byte second) => [.. Png(first), second];

    // --- Where an image is shown in place of another ---------------------------------------------------------

    [Fact]
    public async Task The_logo_for_dark_surfaces_shows_the_one_for_light_surfaces_until_it_has_an_image_of_its_own()
    {
        using var host = CreateHost(NoImages);
        var admin = host.CreateSignedInClient();
        var client = host.CreateClient();

        await PutImageAsync(admin, "logo", Svg());
        var branding = await BrandingAsync(client);
        foreach (var url in new[] { branding.LogoUrl, branding.LogoOnDarkUrl })
            Assert.Equal(Svg(), await ImageAsync(client, url));
        Assert.Null(branding.IconUrl);

        await PutImageAsync(admin, "logo-on-dark", Png(2));
        branding = await BrandingAsync(client);
        Assert.Equal(Svg(), await ImageAsync(client, branding.LogoUrl));
        Assert.Equal(Png(2), await ImageAsync(client, branding.LogoOnDarkUrl));
    }

    [Theory]
    [InlineData("web-logo")]
    [InlineData("web-logo-on-dark")]
    public async Task The_web_only_slots_of_earlier_versions_are_gone(string slot)
    {
        using var host = CreateHost(NoImages);
        var admin = host.CreateSignedInClient();

        Assert.Equal(HttpStatusCode.NotFound, (await PutImageAsync(admin, slot, Svg())).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await admin.DeleteAsync($"/api/branding/{slot}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await admin.GetAsync($"/api/branding/{slot}")).StatusCode);
    }

    // --- Taking an image away --------------------------------------------------------------------------------------

    [Fact]
    public async Task Removing_an_image_leaves_the_slot_without_one_even_though_the_settings_name_a_file()
    {
        using var host = CreateHost(); // the shipped settings: all three files
        var admin = host.CreateSignedInClient();
        var client = host.CreateClient();

        var settings = await ReadAsync<BrandingSettingsDto>(await admin.DeleteAsync("/api/branding/logo"));

        Assert.True(settings.IsCustomized);
        Assert.Null(OwnUrl(settings, BrandingSlots.Logo));
        Assert.NotNull(OwnUrl(settings, BrandingSlots.LogoOnDark)); // the others are as they were
        var branding = await BrandingAsync(client);
        Assert.Null(branding.LogoUrl);
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync("/api/branding/logo")).StatusCode);
        Assert.NotNull(branding.LogoOnDarkUrl); // the dark logo is its own, no fallback needed
    }

    [Fact]
    public async Task Removing_an_uploaded_image_does_not_bring_the_file_of_the_settings_back_a_reset_does()
    {
        using var host = CreateHost();
        var admin = host.CreateSignedInClient();
        var original = await ImageAsync(admin, "api/branding/logo");
        await PutImageAsync(admin, "logo", Png(7));

        await admin.DeleteAsync("/api/branding/logo");
        Assert.Equal(HttpStatusCode.NotFound, (await admin.GetAsync("/api/branding/logo")).StatusCode);

        await admin.DeleteAsync("/api/branding");
        Assert.Equal(original, await ImageAsync(admin, "api/branding/logo"));
    }

    [Fact]
    public async Task Removing_what_the_settings_do_not_have_either_changes_nothing()
    {
        using var host = CreateHost(NoImages);
        var admin = host.CreateSignedInClient();

        var settings = await ReadAsync<BrandingSettingsDto>(await admin.DeleteAsync("/api/branding/logo"));

        Assert.False(settings.IsCustomized); // no row says "none" already
    }

    [Fact]
    public async Task Removing_an_uploaded_image_where_the_settings_have_none_is_the_same_as_never_having_uploaded()
    {
        using var host = CreateHost(NoImages);
        var admin = host.CreateSignedInClient();
        await PutImageAsync(admin, "icon", Svg());
        Assert.True((await ReadAsync<BrandingSettingsDto>(await admin.GetAsync("/api/branding/settings"))).IsCustomized);

        var settings = await ReadAsync<BrandingSettingsDto>(await admin.DeleteAsync("/api/branding/icon"));

        Assert.False(settings.IsCustomized);
        Assert.Null(OwnUrl(settings, BrandingSlots.Icon));
    }

    // --- Back to the settings --------------------------------------------------------------------------------------

    [Fact]
    public async Task A_reset_forgets_texts_and_images_and_the_settings_of_the_api_apply_again()
    {
        using var host = CreateHost();
        var admin = host.CreateSignedInClient();
        var original = await BrandingAsync(admin);
        await admin.PutAsJsonAsync("/api/branding", Input());
        await PutImageAsync(admin, "logo", Png(5));
        await admin.DeleteAsync("/api/branding/icon");

        var reset = await ReadAsync<BrandingSettingsDto>(await admin.DeleteAsync("/api/branding"));

        Assert.False(reset.IsCustomized);
        Assert.Equal(original, await BrandingAsync(admin));
        Assert.All(reset.Images, image => Assert.NotNull(image.Url));
    }

    [Fact]
    public async Task A_reset_of_an_instance_nobody_changed_is_harmless()
    {
        using var host = CreateHost();
        var admin = host.CreateSignedInClient();

        var reset = await ReadAsync<BrandingSettingsDto>(await admin.DeleteAsync("/api/branding"));

        Assert.False(reset.IsCustomized);
    }

    // --- What does not change by itself ----------------------------------------------------------------------------

    [Fact]
    public async Task The_demo_data_keeps_the_organisation_of_the_settings()
    {
        using var host = CreateHost(new(NoImages) { ["OrganizationName"] = "Settings Club" });
        var admin = host.CreateSignedInClient();
        await admin.PutAsJsonAsync("/api/branding", Input(organization: "Saved Club"));

        var groups = await admin.GetFromJsonAsync<List<GroupDto>>("/api/groups");

        Assert.Equal("Settings Club (Demo)", groups!.Single(g => g.Id == "demo-dojo").Name);
    }
}

/// <summary>What an uploaded file is, found out from its bytes alone.</summary>
public sealed class BrandingImageFormatsTests
{
    [Theory]
    [InlineData(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0 }, "image/png")]
    [InlineData(new byte[] { 0xFF, 0xD8, 0xFF, 0xDB }, "image/jpeg")]
    [InlineData(new byte[] { 0, 0, 1, 0, 1, 0, 16, 16 }, "image/x-icon")]
    public void A_signature_decides(byte[] bytes, string expected) => Assert.Equal(expected, BrandingImageFormats.Detect(bytes));

    [Theory]
    [InlineData("GIF87a.....", "image/gif")]
    [InlineData("GIF89a.....", "image/gif")]
    [InlineData("<svg/>", "image/svg+xml")]
    [InlineData("<svg>", "image/svg+xml")]
    [InlineData("  \n<svg xmlns='http://www.w3.org/2000/svg'/>", "image/svg+xml")]
    [InlineData("<?xml version='1.0'?><svg/>", "image/svg+xml")]
    [InlineData("<!-- <svg> in a comment --><svg/>", "image/svg+xml")]
    [InlineData("<SVG/>", "image/svg+xml")]
    [InlineData("<!-- <svg/> -->", null)]            // only a comment
    [InlineData("<html><svg/></html>", null)]        // not the first element
    [InlineData("<svgfoo/>", null)]
    [InlineData("BM", null)]                         // too short to be a bitmap
    [InlineData("RIFF....WAVE....", null)]
    [InlineData("", null)]
    [InlineData("plain text", null)]
    public void Text_formats_are_recognised_by_what_they_start_with(string text, string? expected) =>
        Assert.Equal(expected, BrandingImageFormats.Detect(Encoding.UTF8.GetBytes(text)));

    [Fact]
    public void A_bitmap_needs_more_than_its_first_letters_and_a_webp_is_no_format_of_a_slot()
    {
        Assert.Equal("image/bmp", BrandingImageFormats.Detect([(byte)'B', (byte)'M', .. new byte[20]]));
        Assert.Null(BrandingImageFormats.Detect([.. "RIFF"u8, 1, 0, 0, 0, .. "WEBPVP8 "u8])); // the desktop client cannot draw it
    }

    [Theory]
    [InlineData("image/png", "PNG")]
    [InlineData("image/jpeg", "JPEG")]
    [InlineData("image/svg+xml", "SVG")]
    [InlineData("image/x-icon", "ICO")]
    public void A_format_has_a_name_for_a_message(string contentType, string name) => Assert.Equal(name, BrandingImageFormats.Name(contentType));

    [Fact]
    public void Every_format_a_slot_takes_can_be_recognised_and_has_a_name()
    {
        var all = BrandingSlots.All.SelectMany(slot => BrandingSlots.ContentTypes(slot)!).Distinct().ToList();

        Assert.Equal(6, all.Count);
        Assert.All(all, contentType => Assert.NotEmpty(BrandingImageFormats.Name(contentType)));
    }
}

/// <summary>What a name and a colour must look like: the same rules the API and the page apply.</summary>
public sealed class BrandingRulesTests
{
    [Theory]
    [InlineData("#abc", true)]
    [InlineData("#ABCDEF", true)]
    [InlineData(" #00A650 ", true)]
    [InlineData("abc", false)]
    [InlineData("#abcd", false)]
    [InlineData("#abcdeg", false)]
    [InlineData("#", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    [InlineData("rgb(0,0,0)", false)]
    public void An_accent_colour_is_a_hash_and_three_or_six_hex_digits(string? color, bool valid) =>
        Assert.Equal(valid, BrandingRules.IsAccentColor(color));

    [Theory]
    [InlineData("€", true)]
    [InlineData(" CHF ", true)]
    [InlineData("", false)]
    [InlineData("  ", false)]
    [InlineData(null, false)]
    [InlineData("123456789", false)]
    [InlineData("a\tb", false)]
    public void A_currency_is_a_short_text_without_control_characters(string? currency, bool valid) =>
        Assert.Equal(valid, BrandingRules.CheckCurrency(currency) is null);

    [Fact]
    public void Money_is_shown_with_the_set_currency()
    {
        var before = Kyuplan.Client.ViewModels.Display.Currency;
        try
        {
            Kyuplan.Client.ViewModels.Display.Currency = "CHF";
            Assert.EndsWith(" CHF", Kyuplan.Client.ViewModels.Display.Money(12.5m));
        }
        finally
        {
            Kyuplan.Client.ViewModels.Display.Currency = before;
        }
    }

    [Fact]
    public void An_unknown_slot_takes_no_format()
    {
        Assert.Null(BrandingSlots.ContentTypes("banner"));
        Assert.Null(BrandingSlots.ContentTypes(null));
        Assert.Equal(3, BrandingSlots.All.Count);
        Assert.All(BrandingSlots.All, slot => Assert.NotNull(BrandingSlots.ContentTypes(slot)));
    }
}
