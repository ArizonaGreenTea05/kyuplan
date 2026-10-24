using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using Kyuplan.Api.Configuration;
using Kyuplan.Api.Domain;
using Kyuplan.Api.Infrastructure;
using Kyuplan.Api.Spond;
using Newtonsoft.Json;
using Spond.API.Models;

namespace Kyuplan.Tests;

/// <summary>
/// Spond answers an account that sent too many requests with nothing at all. The API can hold several accounts and asks the next one
/// when an account yields no groups or events; a fake Spond client per account stands in for Spond here.
/// </summary>
public class SpondAccountFallbackTests
{
    private static readonly TimeZoneInfo Berlin = TimeZoneInfo.FindSystemTimeZoneById("Europe/Berlin");

    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 3, 1, 8, 0, 0, TimeSpan.Zero));
    private readonly List<string> _calls = [];
    private readonly Dictionary<string, FakeAccountClient> _clients = [];

    private static SpondAccount Account(string name) => new() { Email = $"{name}@example.org", Password = "secret" };

    private SpondApiDataSource CreateSource(params string[] accountNames) => new(
        Options.Create(new SpondOptions { Accounts = accountNames.Select(Account).ToList() }),
        new ClubClock(_time, Berlin),
        new MemoryCache(new MemoryCacheOptions { Clock = new FakeCacheClock(_time) }),
        _time,
        NullLogger<SpondApiDataSource>.Instance,
        account =>
        {
            var name = account.Email!.Split('@')[0];
            return _clients[name] = new FakeAccountClient(name, _calls);
        });

    /// <summary>The events of one month: the day is in the middle of it, so that one month is fetched and cached on its own.</summary>
    private static Task<IReadOnlyList<EventInfo>> EventsOf(SpondApiDataSource source, int month)
    {
        var day = new DateTimeOffset(2026, month, 15, 12, 0, 0, TimeSpan.Zero);
        return source.GetEventsAsync("g1", day, day, CancellationToken.None);
    }

    [Fact]
    public void The_single_login_is_the_first_account_and_the_list_follows_without_unusable_entries()
    {
        var options = new SpondOptions
        {
            Email = "first@example.org",
            Password = "secret",
            Accounts =
            [
                Account("second"),
                new SpondAccount { Email = "no-password@example.org" },
                new SpondAccount { PhoneNumber = "+491701234567", Password = "secret" },
            ],
        };

        Assert.Equal(["first@example.org", "second@example.org", null], options.GetAccounts().Select(a => a.Email));
        Assert.Equal("+491701234567", options.GetAccounts()[2].PhoneNumber);
        Assert.Empty(new SpondOptions { Email = "only@example.org" }.GetAccounts());
    }

    [Fact]
    public void The_accounts_are_read_from_a_list_in_the_configuration()
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Spond:Email"] = "first@example.org",
            ["Spond:Password"] = "one",
            ["Spond:Accounts:0:Email"] = "second@example.org",
            ["Spond:Accounts:0:Password"] = "two",
            ["Spond:Accounts:1:PhoneNumber"] = "+491701234567",
            ["Spond:Accounts:1:Password"] = "three",
        }).Build();

        var accounts = configuration.GetSection(SpondOptions.Section).Get<SpondOptions>()!.GetAccounts();

        Assert.Equal(["one", "two", "three"], accounts.Select(a => a.Password));
        Assert.Equal(["first@example.org", "second@example.org", null], accounts.Select(a => a.Email));
    }

    [Fact]
    public async Task Events_come_from_the_first_account_as_long_as_it_delivers()
    {
        using var source = CreateSource("first", "second");

        var events = await EventsOf(source, 3);

        Assert.Equal(["first"], events.Select(e => e.Id));
        Assert.DoesNotContain("second:events", _calls);
    }

    [Fact]
    public async Task When_the_first_account_yields_no_events_the_second_is_used()
    {
        using var source = CreateSource("first", "second", "third");
        _clients["first"].Throttled = true;

        var events = await EventsOf(source, 3);

        Assert.Equal(["second"], events.Select(e => e.Id));

        // The groups, asked for first, (asked twice, with a new login in between) already showed that the first account yields nothing: it is not asked for the events any more.
        Assert.Equal(["first:groups", "first:groups", "second:groups", "second:events"], _calls.Where(c => c.EndsWith(":events") || c.EndsWith(":groups")));
    }

    [Fact]
    public async Task The_next_account_is_tried_until_one_delivers()
    {
        using var source = CreateSource("first", "second", "third");
        _clients["first"].Throttled = true;
        _clients["second"].Throttled = true;

        var events = await EventsOf(source, 3);

        Assert.Equal(["third"], events.Select(e => e.Id));
    }

    [Fact]
    public async Task An_account_that_came_back_empty_while_another_delivered_is_asked_last_until_its_cooldown_is_over()
    {
        using var source = CreateSource("first", "second");
        _clients["first"].Throttled = true;
        await EventsOf(source, 3);
        _calls.Clear();

        // March is cached; in April the first account is not asked at all.
        Assert.Equal(["second"], (await EventsOf(source, 4)).Select(e => e.Id));
        Assert.Equal(["second:events"], _calls);

        // The cooldown is over and the first account has recovered: it is asked first again (after the login that has run out by now).
        _time.Advance(new SpondOptions().AccountCooldown + TimeSpan.FromSeconds(1));
        _clients["first"].Throttled = false;
        _calls.Clear();

        Assert.Equal(["first"], (await EventsOf(source, 5)).Select(e => e.Id));
        Assert.Equal(["first:events"], _calls.Where(c => c.EndsWith(":events")));
    }

    [Fact]
    public async Task When_no_account_has_events_the_month_is_empty_and_the_order_stays()
    {
        using var source = CreateSource("first", "second");
        _clients["first"].Empty = true;
        _clients["second"].Empty = true;

        Assert.Empty(await EventsOf(source, 3));
        _calls.Clear();

        // An empty month is nothing unusual, so nobody is put last for it.
        Assert.Empty(await EventsOf(source, 4));
        Assert.Equal(["first:events", "second:events"], _calls);
    }

    [Fact]
    public async Task An_account_whose_login_is_rejected_is_skipped()
    {
        using var source = CreateSource("first", "second");
        _clients["first"].LoginSucceeds = false;

        var events = await EventsOf(source, 3);

        Assert.Equal(["second"], events.Select(e => e.Id));
    }

    [Fact]
    public async Task When_every_login_is_rejected_the_failure_is_reported()
    {
        using var source = CreateSource("first", "second");
        _clients["first"].LoginSucceeds = false;
        _clients["second"].LoginSucceeds = false;

        var ex = await Assert.ThrowsAsync<SpondUnavailableException>(() => EventsOf(source, 3));

        Assert.Contains("rejected the login of Spond account 2 (second@example.org)", ex.Message);
    }

    [Fact]
    public async Task Groups_come_from_the_next_account_when_one_yields_none()
    {
        using var source = CreateSource("first", "second");
        _clients["first"].Throttled = true;

        var groups = await source.GetGroupsAsync(CancellationToken.None);

        Assert.Equal(["second"], groups.Select(g => g.Id));
    }

    [Fact]
    public async Task Without_groups_from_any_account_spond_is_reported_as_unavailable()
    {
        using var source = CreateSource("first", "second");
        _clients["first"].Throttled = true;
        _clients["second"].Throttled = true;

        var ex = await Assert.ThrowsAsync<SpondUnavailableException>(() => source.GetGroupsAsync(CancellationToken.None));

        Assert.Equal("Spond returned no groups for any of the 2 accounts.", ex.Message);
    }

    [Fact]
    public async Task The_hosts_of_events_are_recognised_even_when_the_groups_were_not_asked_for_before()
    {
        using var source = CreateSource("first");

        var events = await EventsOf(source, 3);

        Assert.Equal(["m-first"], Assert.Single(events).Trainers); // the owner is named by profile, the member id comes from the groups
        Assert.Contains("first:groups", _calls);
    }

    [Fact]
    public async Task The_groups_are_not_asked_again_for_every_month()
    {
        using var source = CreateSource("first");

        await source.GetGroupsAsync(CancellationToken.None);
        await EventsOf(source, 3);
        await EventsOf(source, 4);

        Assert.Single(_calls, "first:groups");
    }

    [Fact]
    public async Task Events_mapped_without_the_groups_are_cached_only_briefly_and_mapped_again_once_the_groups_are_there()
    {
        using var source = CreateSource("first");
        _clients["first"].NoGroups = true;

        var withoutGroups = await EventsOf(source, 3);
        Assert.Equal(["p-first"], Assert.Single(withoutGroups).Trainers); // the owner's profile id: who that is, is not known
        await EventsOf(source, 3);
        Assert.Single(_calls, "first:events"); // cached for the moment

        _clients["first"].NoGroups = false;
        _time.Advance(TimeSpan.FromMinutes(3));
        var again = await EventsOf(source, 3);

        Assert.Equal(["m-first"], Assert.Single(again).Trainers);
    }

    [Fact]
    public async Task Without_accounts_spond_is_reported_as_not_configured()
    {
        using var source = CreateSource();

        var ex = await Assert.ThrowsAsync<SpondUnavailableException>(() => source.GetGroupsAsync(CancellationToken.None));

        Assert.Contains("credentials are not configured", ex.Message);
    }

    /// <summary>The cache expires entries by the test's clock, so that a test can let them run out.</summary>
#pragma warning disable CS0618 // MemoryCacheOptions.Clock is the only way to hand the cache a clock
    private sealed class FakeCacheClock(TimeProvider time) : Microsoft.Extensions.Internal.ISystemClock
    {
        public DateTimeOffset UtcNow => time.GetUtcNow();
    }
#pragma warning restore CS0618

    /// <summary>
    /// Spond.API reports a failed request as an empty list: <see cref="Throttled"/> is that, for every call;
    /// <see cref="Empty"/> is an account that works and simply has no events.
    /// </summary>
    private sealed class FakeAccountClient(string name, List<string> calls) : ISpondAccountClient
    {
        public bool LoginSucceeds { get; set; } = true;

        public bool Throttled { get; set; }

        public bool Empty { get; set; }

        /// <summary>The account delivers events but no groups, so there is no member lookup.</summary>
        public bool NoGroups { get; set; }

        public Task<bool> LoginAsync()
        {
            calls.Add($"{name}:login");
            return Task.FromResult(LoginSucceeds);
        }

        public Task<List<SpondGroup>> GetGroupsAsync()
        {
            calls.Add($"{name}:groups");
            return Task.FromResult(Throttled || NoGroups
                ? []
                : new List<SpondGroup>
                {
                    new() { Id = name, Name = name, Members = [new SpondMember { Id = $"m-{name}", Profile = new SpondUserProfile { Id = $"p-{name}" } }] },
                });
        }

        public Task<List<SpondEvent>> GetEventsAsync(string groupId, DateTime minEnd, DateTime maxEnd)
        {
            calls.Add($"{name}:events");
            return Task.FromResult(Throttled || Empty
                ? []
                : new List<SpondEvent>
                {
                    new()
                    {
                        Id = name,
                        Heading = "Training",
                        StartTimestamp = "2026-03-10T17:30:00Z",
                        EndTimestamp = "2026-03-10T19:00:00Z",
                        Responses = new SpondEventResponses(),
                        Owners = JsonConvert.DeserializeObject<SpondEvent>($$"""{ "owners": [{ "id": "p-{{name}}" }] }""")!.Owners,
                    },
                });
        }
    }
}
