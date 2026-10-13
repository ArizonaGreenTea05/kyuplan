using System.Net;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;

namespace Kyuplan.Tests;

/// <summary>The API serves the web client (Blazor WebAssembly) itself: open to everybody, every page address leads to it, /api stays the API's.</summary>
public sealed class WebClientHostingTests(WebApplicationFactory<Program> factory) : IClassFixture<WebApplicationFactory<Program>>, IDisposable
{
    private readonly InMemoryDatabase _planner = new();
    private readonly InMemoryDatabase _accounts = new();

    public void Dispose()
    {
        _planner.Dispose();
        _accounts.Dispose();
    }

    private HttpClient CreateClient() => factory.WithWebHostBuilder(builder =>
    {
        builder.UseSetting("Spond:UseDemoData", "true");
        builder.UseSetting("Sync:Enabled", "false");
        builder.UseSetting("DefaultData:Enabled", "false");
        builder.UseSetting("Database:ConnectionString", _planner.ConnectionString);
        TestAccounts.Configure(builder, _accounts);
    }).CreateClient();

    [Theory]
    [InlineData("/")]
    [InlineData("/exams")] // a page of the client, opened directly (reload, bookmark)
    public async Task Pages_are_the_web_client_without_a_login(string path)
    {
        var response = await CreateClient().GetAsync(path);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("blazor.webassembly.js", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task The_framework_files_are_served_without_a_login()
    {
        var response = await CreateClient().GetAsync("/_framework/blazor.webassembly.js");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task The_api_still_needs_a_login_and_is_not_replaced_by_the_client()
    {
        var response = await CreateClient().GetAsync("/api/unknown");

        Assert.NotEqual(HttpStatusCode.OK, response.StatusCode);
        Assert.DoesNotContain("blazor", await response.Content.ReadAsStringAsync());
    }
}
