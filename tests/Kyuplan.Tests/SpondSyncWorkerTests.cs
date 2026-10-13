using System.Collections.Concurrent;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Kyuplan.Api.Configuration;
using Kyuplan.Api.Persistence;
using Kyuplan.Api.Spond;
using static Kyuplan.Tests.SyncTestClub;

namespace Kyuplan.Tests;

/// <summary>The sync that runs by itself: when the API starts and then every interval, and what it does when Spond is down.</summary>
public sealed class SpondSyncWorkerTests : IDisposable
{
    private static readonly TimeSpan Interval = TimeSpan.FromMinutes(15);

    private readonly SyncTestClub _club = new();
    private readonly CapturingLogger _log = new();

    public void Dispose() => _club.Dispose();

    private SpondSyncWorker CreateWorker(bool enabled = true, TimeSpan? interval = null) => new(
        _club.Services.GetRequiredService<IServiceScopeFactory>(),
        Options.Create(new SyncOptions { Enabled = enabled, Interval = interval ?? Interval }),
        _club.Time,
        _log);

    private async Task<bool> HasMembersAsync(int count) => (await _club.MembersAsync()).Count == count;

    /// <summary>
    /// Moves the clock on until the worker has synced again. A tick is only seen once the worker waits for it, and it waits only
    /// after its first sync, so a single step right after the first sync could come too early: step until it counts.
    /// </summary>
    private Task SyncedAgainAsync(int members) => EventuallyAsync(async () =>
    {
        _club.Time.Advance(Interval);
        return (await _club.MembersAsync()).Count == members;
    }, $"the worker has synced {members} members");

    [Fact]
    public async Task It_syncs_when_it_starts_and_then_every_interval()
    {
        _club.Spond.SetGroups(Group(Dojo, Person("m-anna", "Anna Beck")));
        using var worker = CreateWorker();

        await worker.StartAsync(CancellationToken.None);
        await EventuallyAsync(() => HasMembersAsync(1), "the worker has synced once");

        _club.Spond.SetGroups(Group(Dojo, Person("m-anna", "Anna Beck"), Person("m-ben", "Ben Koch")));
        await SyncedAgainAsync(2);

        await worker.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task It_does_not_give_up_when_a_sync_fails()
    {
        _club.Spond.SetGroups(Group(Dojo, Person("m-anna", "Anna Beck")));
        _club.Spond.Unreachable = true;
        using var worker = CreateWorker();

        await worker.StartAsync(CancellationToken.None);
        await EventuallyAsync(() => Task.FromResult(_log.Messages.Any(m => m.Contains("Spond is unreachable"))), "the failure was logged");
        Assert.Empty(await _club.MembersAsync());

        _club.Spond.Unreachable = false;
        await SyncedAgainAsync(1);

        await worker.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task It_does_nothing_when_it_is_switched_off()
    {
        _club.Spond.SetGroups(Group(Dojo, Person("m-anna", "Anna Beck")));
        using var worker = CreateWorker(enabled: false);

        await worker.StartAsync(CancellationToken.None);
        _club.Time.Advance(Interval * 3);
        await worker.StopAsync(CancellationToken.None);

        Assert.Equal(0, _club.Spond.GroupsCalls);
    }

    [Fact]
    public async Task It_syncs_only_once_without_an_interval()
    {
        _club.Spond.SetGroups(Group(Dojo, Person("m-anna", "Anna Beck")));
        using var worker = CreateWorker(interval: TimeSpan.Zero);

        await worker.StartAsync(CancellationToken.None);
        await EventuallyAsync(() => HasMembersAsync(1), "the worker has synced once");
        _club.Spond.SetGroups(Group(Dojo, Person("m-anna", "Anna Beck"), Person("m-ben", "Ben Koch")));
        _club.Time.Advance(TimeSpan.FromDays(1));
        await Task.Delay(100); // a second sync would have taken this long
        await worker.StopAsync(CancellationToken.None);

        Assert.Single(await _club.MembersAsync());
    }

    /// <summary>Keeps what is logged so that a test can see that a failure was noticed.</summary>
    private sealed class CapturingLogger : ILogger<SpondSyncWorker>
    {
        public ConcurrentQueue<string> Messages { get; } = new();

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            Messages.Enqueue(formatter(state, exception));
    }
}

/// <summary>The worker as it is wired into the API: a host that starts with the demo club fills the members table without being asked.</summary>
public sealed class SpondSyncHostTests(WebApplicationFactory<Program> factory) : IClassFixture<WebApplicationFactory<Program>>, IDisposable
{
    private readonly InMemoryDatabase _database = new();
    private readonly InMemoryDatabase _accounts = new();

    public void Dispose()
    {
        _database.Dispose();
        _accounts.Dispose();
    }

    [Fact]
    public async Task The_api_syncs_the_members_with_Spond_when_it_starts()
    {
        using var host = factory.WithWebHostBuilder(builder =>
        {
            builder.UseSetting("Spond:UseDemoData", "true");
            builder.UseSetting("Database:ConnectionString", _database.ConnectionString);
            builder.UseSetting("Sync:Enabled", "true");
            builder.UseSetting("DefaultData:Enabled", "false"); // the tests fill their own tables; DefaultDataTests starts from the defaults
            TestAccounts.Configure(builder, _accounts);
        });
        _ = host.CreateClient(); // starts the host, and with it the worker

        var inSpond = (await host.Services.GetRequiredService<ISpondDataSource>().GetGroupsAsync(CancellationToken.None))
            .Sum(g => g.Members.Count);
        Assert.True(inSpond > 0);

        await EventuallyAsync(async () =>
        {
            await using var scope = host.Services.CreateAsyncScope();
            return await scope.ServiceProvider.GetRequiredService<PlannerDbContext>().Members.CountAsync() == inSpond;
        }, "the demo members are in the database");
    }
}
