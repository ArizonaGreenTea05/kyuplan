using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Kyuplan.Contracts;
using static Kyuplan.Tests.TestAccounts;

namespace Kyuplan.Tests;

/// <summary>The sync status and the metrics endpoint against the real API.</summary>
public sealed class SyncStatusApiTests(WebApplicationFactory<Program> factory) : IClassFixture<WebApplicationFactory<Program>>, IDisposable
{
    private readonly InMemoryDatabase _planner = new();
    private readonly InMemoryDatabase _accounts = new();

    public void Dispose()
    {
        _planner.Dispose();
        _accounts.Dispose();
    }

    private WebApplicationFactory<Program> CreateHost(string? syncEnabled = "false", string? metricsEnabled = "true", string? metricsToken = Token) =>
        factory.WithWebHostBuilder(builder =>
        {
            builder.UseSetting("Spond:UseDemoData", "true");
            builder.UseSetting("Sync:Enabled", syncEnabled);
            builder.UseSetting("DefaultData:Enabled", "false");
            if (metricsEnabled is not null) builder.UseSetting("Metrics:Enabled", metricsEnabled);
            if (metricsToken is not null) builder.UseSetting("Metrics:Token", metricsToken);
            builder.UseSetting("Database:ConnectionString", _planner.ConnectionString);
            Configure(builder, _accounts);
        });

    private const string Token = "scrape-token-0123456789";

    private static HttpClient Scraper(WebApplicationFactory<Program> host, string? token = Token)
    {
        var client = host.CreateClient();
        if (token is not null) client.DefaultRequestHeaders.Authorization = new("Bearer", token);
        return client;
    }

    [Fact]
    public async Task The_status_of_a_sync_that_is_switched_off_says_so()
    {
        using var host = CreateHost();

        var status = await host.CreateSignedInClient().GetFromJsonAsync<SyncStatusDto>("/api/sync/status");

        Assert.False(status!.Enabled);
        Assert.Empty(status.Recent);
        Assert.Null(status.LastSuccess);
    }

    [Fact]
    public async Task The_status_shows_the_run_the_api_made_when_it_started()
    {
        using var host = CreateHost(syncEnabled: "true");
        var admin = host.CreateSignedInClient();

        SyncStatusDto? status = null;
        for (var i = 0; i < 100 && status?.LastSuccess is null; i++)
        {
            status = await admin.GetFromJsonAsync<SyncStatusDto>("/api/sync/status");
            if (status!.LastSuccess is null) await Task.Delay(50);
        }

        Assert.True(status!.Enabled);
        Assert.NotNull(status.LastSuccess);
        Assert.Equal(0, status.ConsecutiveFailures);
    }

    [Fact]
    public async Task Without_a_login_the_status_is_refused()
    {
        using var host = CreateHost();

        var response = await host.CreateClient().GetAsync("/api/sync/status");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task A_sync_can_be_run_by_hand_and_needs_a_login()
    {
        using var host = CreateHost();

        Assert.Equal(HttpStatusCode.Unauthorized, (await host.CreateClient().PostAsync("/api/sync/run", null)).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await host.CreateSignedInClient().PostAsync("/api/sync/run", null)).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await host.CreateSignedInClient().PostAsync("/api/sync/run?confirmRemoval=true", null)).StatusCode);

        var status = await host.CreateSignedInClient().GetFromJsonAsync<SyncStatusDto>("/api/sync/status");
        Assert.Equal(2, status!.Recent.Count(r => r.Succeeded));
    }

    [Fact]
    public async Task The_metrics_are_readable_with_the_token_in_the_prometheus_format()
    {
        using var host = CreateHost();

        var response = await Scraper(host).GetAsync("/metrics");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.StartsWith("text/plain", response.Content.Headers.ContentType!.MediaType);
        var text = await response.Content.ReadAsStringAsync();
        Assert.Contains("kyuplan_sync_consecutive_failures", text);
        Assert.Contains("kyuplan_sync_last_success_timestamp", text);
    }

    [Fact]
    public async Task The_metrics_count_the_runs_of_the_sync()
    {
        using var host = CreateHost(syncEnabled: "true");
        var client = Scraper(host);

        const string Counted = "outcome=\"success\"} 1";
        var text = "";
        for (var i = 0; i < 100 && !text.Contains(Counted); i++)
        {
            text = await client.GetStringAsync("/metrics");
            if (!text.Contains(Counted)) await Task.Delay(50);
        }

        Assert.Contains("kyuplan_sync_runs_total{", text);
        Assert.Contains(Counted, text);
        Assert.Contains("kyuplan_sync_duration_seconds_bucket{otel_scope_name=\"Kyuplan\",le=\"2\"}", text); // buckets in seconds, not the milliseconds of the default
        Assert.Contains("kyuplan_sync_duration_seconds", text);
    }

    [Fact]
    public async Task The_metrics_are_off_unless_they_are_switched_on()
    {
        using var host = CreateHost(metricsEnabled: null);

        var response = await Scraper(host).GetAsync("/metrics");

        Assert.DoesNotContain("kyuplan_sync", await response.Content.ReadAsStringAsync());
    }

    [Theory]
    [InlineData(null)]
    [InlineData("wrong-token-0123456789")]
    [InlineData("")]
    public async Task The_metrics_refuse_a_missing_or_wrong_token(string? token)
    {
        using var host = CreateHost();
        var client = Scraper(host, token: null);
        if (token is not null) client.DefaultRequestHeaders.TryAddWithoutValidation("Authorization", "Bearer " + token);

        var response = await client.GetAsync("/metrics");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Contains("Bearer", response.Headers.WwwAuthenticate.ToString());
        Assert.DoesNotContain("kyuplan_sync", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task The_metrics_refuse_other_schemes_and_a_user_login()
    {
        using var host = CreateHost();
        var client = host.CreateSignedInClient();

        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/metrics")).StatusCode);
        client.DefaultRequestHeaders.Authorization = new("Basic", Token);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/metrics")).StatusCode);
    }

    [Fact]
    public async Task Switched_on_without_a_token_the_metrics_refuse_everybody()
    {
        using var host = CreateHost(metricsToken: null);

        Assert.Equal(HttpStatusCode.Unauthorized, (await Scraper(host, token: "").GetAsync("/metrics")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await host.CreateClient().GetAsync("/metrics")).StatusCode);
    }

    [Fact]
    public async Task The_metrics_can_be_switched_off()
    {
        using var host = CreateHost(metricsEnabled: "false");

        var response = await Scraper(host).GetAsync("/metrics");

        // The web client's fallback page may answer the address; what it must not answer is the Prometheus text.
        Assert.DoesNotContain("kyuplan_sync", await response.Content.ReadAsStringAsync());
        Assert.DoesNotContain("text/plain", response.Content.Headers.ContentType?.MediaType ?? "");
    }
}
