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

    private WebApplicationFactory<Program> CreateHost(string? syncEnabled = "false", string? metricsEnabled = null) =>
        factory.WithWebHostBuilder(builder =>
        {
            builder.UseSetting("Spond:UseDemoData", "true");
            builder.UseSetting("Sync:Enabled", syncEnabled);
            builder.UseSetting("DefaultData:Enabled", "false");
            if (metricsEnabled is not null) builder.UseSetting("Metrics:Enabled", metricsEnabled);
            builder.UseSetting("Database:ConnectionString", _planner.ConnectionString);
            Configure(builder, _accounts);
        });

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
    public async Task The_metrics_are_readable_without_a_login_in_the_prometheus_format()
    {
        using var host = CreateHost();

        var response = await host.CreateClient().GetAsync("/metrics");

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
        var client = host.CreateClient();

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
    public async Task The_metrics_can_be_switched_off()
    {
        using var host = CreateHost(metricsEnabled: "false");

        var response = await host.CreateClient().GetAsync("/metrics");

        // The web client's fallback page may answer the address; what it must not answer is the Prometheus text.
        Assert.DoesNotContain("kyuplan_sync", await response.Content.ReadAsStringAsync());
        Assert.DoesNotContain("text/plain", response.Content.Headers.ContentType?.MediaType ?? "");
    }
}
