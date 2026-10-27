using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using Kyuplan.Api.Configuration;
using Kyuplan.Api.Infrastructure;
using Kyuplan.Api.Sources;
using Kyuplan.Api.Spond;

namespace Kyuplan.Tests;

/// <summary>The data source of the custom script: what it returns is read as groups and events, and a script that fails is reported, not trusted.</summary>
public sealed class CustomScriptDataSourceTests
{
    private static readonly ClubClock Clock = new(new FakeTimeProvider(new DateTimeOffset(2026, 10, 13, 10, 0, 0, TimeSpan.Zero)), TimeZoneInfo.FindSystemTimeZoneById("Europe/Berlin"));

    private static CustomScriptDataSource Source(string script, TimeSpan? timeout = null, bool allowLocal = true, int? maxStatements = null, ILogger<CustomScriptDataSource>? logger = null, bool allowPrivate = false,
        TimeSpan? failureCache = null, ClubClock? clock = null, Dictionary<string, string?>? secrets = null) =>
        new(Options.Create(new DataSourceOptions { Kind = "custom", Script = script, Secrets = new(secrets ?? [], StringComparer.OrdinalIgnoreCase), ScriptTimeout = timeout ?? TimeSpan.FromSeconds(20), AllowLocalAddresses = allowLocal, AllowPrivateAddresses = allowPrivate, ScriptMaxStatements = maxStatements ?? 10_000_000,
            FailureCacheDuration = failureCache ?? TimeSpan.FromSeconds(30) }), clock ?? Clock, logger ?? NullLogger<CustomScriptDataSource>.Instance);

    private sealed class ListLogger : ILogger<CustomScriptDataSource>
    {
        public List<string> Messages { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            Messages.Add(formatter(state, exception));
    }

    private const string StaticScript = """
        function getGroups() {
          return [{
            id: 7, name: 'Dojo',
            subGroups: [{ id: 'k', name: 'Kids' }],
            members: [{ id: 'a', name: 'Anna', subGroupIds: ['k'], birthDate: '2014-05-01' }, { id: 'b', name: 'Ben' }]
          }];
        }
        function getEvents(groupId, from, to) {
          return [
            { id: 'e1', name: 'Training', start: '2026-10-01T16:00:00Z', end: '2026-10-01T17:30:00Z', responses: { a: 'accepted', b: 'Declined' } },
            { id: 'e2', name: 'Exam', start: Date.UTC(2026, 9, 5, 8, 0), end: Date.UTC(2026, 9, 5, 10, 0), cancelled: true, responses: { a: 'waiting' } }
          ].filter(function (e) { return groupId == '7'; });
        }
        """;

    [Fact]
    public async Task Groups_and_events_are_what_the_script_returns()
    {
        using var source = Source(StaticScript);

        var group = Assert.Single(await source.GetGroupsAsync(CancellationToken.None));
        Assert.Equal(("7", "Dojo"), (group.Id, group.Name)); // a number is an id too
        Assert.Equal("Kids", Assert.Single(group.SubGroups).Name);
        Assert.Equal(["Anna", "Ben"], group.Members.Select(m => m.Name));
        Assert.Equal(["k"], group.Members[0].SubGroupIds);
        Assert.Equal(new DateOnly(2014, 5, 1), group.Members[0].BirthDate);
        Assert.Null(group.Members[1].BirthDate);

        var events = await source.GetEventsAsync("7", DateTimeOffset.Parse("2026-09-01T00:00:00Z"), DateTimeOffset.Parse("2026-11-01T00:00:00Z"), CancellationToken.None);
        Assert.Equal(["e1", "e2"], events.Select(e => e.Id));
        Assert.Equal(new DateTimeOffset(2026, 10, 1, 18, 0, 0, TimeSpan.FromHours(2)), events[0].Start); // in the club's time zone
        Assert.Equal(new Dictionary<string, Rsvp> { ["a"] = Rsvp.Accepted, ["b"] = Rsvp.Declined }, events[0].Responses);
        Assert.True(events[1].Cancelled);
        Assert.Equal(new DateTimeOffset(2026, 10, 5, 10, 0, 0, TimeSpan.FromHours(2)), events[1].Start); // epoch milliseconds
        Assert.Equal(Rsvp.Other, events[1].Responses["a"]);
        Assert.Empty(await source.GetEventsAsync("other", DateTimeOffset.MinValue, DateTimeOffset.MaxValue, CancellationToken.None));
    }

    [Fact]
    public async Task The_script_gets_the_group_and_the_span_and_what_it_returned_is_kept_until_the_cache_is_dropped()
    {
        using var source = Source("""
            var calls = 0;
            function getGroups() { return []; }
            function getEvents(groupId, from, to) { return [{ id: groupId + '|' + from + '|' + to, name: 'x', start: from, end: to }]; }
            """);
        var from = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var to = new DateTimeOffset(2026, 2, 1, 0, 0, 0, TimeSpan.Zero);

        var first = await source.GetEventsAsync("g", from, to, CancellationToken.None);
        var again = await source.GetEventsAsync("g", from, to, CancellationToken.None);

        Assert.Equal("g|2026-01-01T00:00:00.0000000Z|2026-02-02T00:00:00.0000000Z", Assert.Single(first).Id);
        Assert.Same(first, again);

        source.InvalidateCache();
        Assert.NotSame(first, await source.GetEventsAsync("g", from, to, CancellationToken.None));
    }

    [Fact]
    public async Task The_trainers_of_an_event_are_the_ids_in_its_trainers_list_and_anything_else_is_skipped()
    {
        using var source = Source("""
            function getGroups() { return []; }
            function getEvents() {
              return [
                { id: 'e1', start: '2026-10-01T16:00:00Z', responses: { a: 'accepted', t: 'accepted' }, trainers: ['t', ' t ', 'u', '', 7, null, {}] },
                { id: 'e2', start: '2026-10-02T16:00:00Z', trainers: 'not-a-list' },
                { id: 'e3', start: '2026-10-03T16:00:00Z' }
              ];
            }
            """);

        var events = await source.GetEventsAsync("g", DateTimeOffset.Parse("2026-09-01T00:00:00Z"), DateTimeOffset.Parse("2026-11-01T00:00:00Z"), CancellationToken.None);

        Assert.Equal(["t", "u"], events[0].Trainers);
        Assert.Equal(["a"], events[0].Participants.Select(p => p.Key));
        Assert.Empty(events[1].Trainers!);
        Assert.Empty(events[2].Trainers!);
    }

    [Fact]
    public async Task Windows_within_the_same_days_share_one_run_of_the_script()
    {
        using var source = Source("function getGroups() { return []; } function getEvents() { return []; }");
        var day = new DateTimeOffset(2026, 3, 1, 0, 0, 0, TimeSpan.Zero);

        var first = await source.GetEventsAsync("g", day, day.AddDays(5).AddHours(3), CancellationToken.None);
        var later = await source.GetEventsAsync("g", day.AddHours(2), day.AddDays(5).AddHours(9), CancellationToken.None);

        Assert.Same(first, later);
    }

    [Theory]
    [InlineData("http://127.0.0.1:1/")]
    [InlineData("http://localhost:1/")]
    [InlineData("http://169.254.169.254/")]
    public async Task Local_addresses_are_refused_unless_allowed(string url)
    {
        using var source = Source($"function getGroups() {{ http.get('{url}'); return []; }}", allowLocal: false);

        var ex = await Assert.ThrowsAsync<DataSourceUnavailableException>(() => source.GetGroupsAsync(CancellationToken.None));

        Assert.Contains("local address", ex.Message);
    }

    [Theory]
    [InlineData("http://10.0.0.1/")]
    [InlineData("http://172.17.0.1/")]
    [InlineData("http://192.168.1.1/")]
    [InlineData("http://100.64.0.1/")]
    [InlineData("http://[fd12:3456::1]/")]
    [InlineData("http://[::ffff:192.168.1.1]/")]
    public async Task Private_addresses_are_refused_unless_allowed(string url)
    {
        using var source = Source($"function getGroups() {{ http.get('{url}'); return []; }}", allowLocal: true, allowPrivate: false);

        var ex = await Assert.ThrowsAsync<DataSourceUnavailableException>(() => source.GetGroupsAsync(CancellationToken.None));

        Assert.Contains("private address", ex.Message);
        Assert.Contains("AllowPrivateAddresses", ex.Message);
    }

    [Fact]
    public async Task A_redirect_into_the_internal_network_is_refused()
    {
        await using var service = await StartServiceAsync();
        using var source = Source($"function getGroups() {{ http.get('{service.Url}/to-lan'); return []; }}", allowLocal: true, allowPrivate: false);

        var ex = await Assert.ThrowsAsync<DataSourceUnavailableException>(() => source.GetGroupsAsync(CancellationToken.None));

        Assert.Contains("private address", ex.Message);
    }

    [Fact]
    public async Task Local_addresses_stay_refused_when_only_private_ones_are_allowed()
    {
        using var source = Source("function getGroups() { http.get('http://127.0.0.1:1/'); return []; }", allowLocal: false, allowPrivate: true);

        var ex = await Assert.ThrowsAsync<DataSourceUnavailableException>(() => source.GetGroupsAsync(CancellationToken.None));

        Assert.Contains("local address", ex.Message);
    }

    [Fact]
    public async Task Allowed_private_addresses_are_tried_not_refused()
    {
        // Nothing answers at a unique-local address of a test machine: the request fails by connect or timeout, not by the guard.
        using var source = Source("function getGroups() { http.get('http://[fd12:3456::1]:1/'); return []; }", TimeSpan.FromSeconds(2), allowLocal: false, allowPrivate: true);

        var ex = await Assert.ThrowsAsync<DataSourceUnavailableException>(() => source.GetGroupsAsync(CancellationToken.None));

        Assert.DoesNotContain("private address", ex.Message);
        Assert.DoesNotContain("local address", ex.Message);
    }

    [Fact]
    public void Requests_never_go_through_a_proxy()
    {
        using var handler = CustomScriptDataSource.CreateHandler(Options.Create(new DataSourceOptions()));

        Assert.False(handler.UseProxy);
    }

    [Fact]
    public async Task A_configured_proxy_neither_receives_the_request_nor_hides_the_destination()
    {
        var proxy = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
        proxy.Start();
        try
        {
            var connections = 0;
            _ = Task.Run(async () =>
            {
                try { while (true) { using var _ = await proxy.AcceptTcpClientAsync(); Interlocked.Increment(ref connections); } }
                catch (Exception) { /* listener stopped */ }
            });
            using var handler = CustomScriptDataSource.CreateHandler(Options.Create(new DataSourceOptions()));
            handler.Proxy = new System.Net.WebProxy($"http://127.0.0.1:{((System.Net.IPEndPoint)proxy.LocalEndpoint).Port}");
            using var http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(10) };

            // Without the guard this goes to the proxy; with it the destination itself is checked and refused.
            var ex = await Assert.ThrowsAsync<HttpRequestException>(() => http.GetAsync("http://192.168.1.1/"));

            Assert.Contains("private address", ex.InnerException?.Message);
            Assert.Equal(0, connections);
        }
        finally
        {
            proxy.Stop();
        }
    }

    [Theory]
    [InlineData("127.0.0.1", AddressScope.Local)]
    [InlineData("127.255.255.254", AddressScope.Local)]
    [InlineData("0.0.0.0", AddressScope.Local)]
    [InlineData("0.1.2.3", AddressScope.Local)]
    [InlineData("169.254.169.254", AddressScope.Local)]
    [InlineData("::", AddressScope.Local)]
    [InlineData("::1", AddressScope.Local)]
    [InlineData("fe80::1", AddressScope.Local)]
    [InlineData("febf::1", AddressScope.Local)]
    [InlineData("fd00:ec2::254", AddressScope.Local)]
    [InlineData("::ffff:127.0.0.1", AddressScope.Local)]
    [InlineData("::ffff:169.254.169.254", AddressScope.Local)]
    [InlineData("::127.0.0.1", AddressScope.Local)]
    [InlineData("64:ff9b::7f00:1", AddressScope.Local)] // NAT64 of 127.0.0.1
    [InlineData("64:ff9b::a9fe:a9fe", AddressScope.Local)] // NAT64 of 169.254.169.254
    [InlineData("2002:7f00:1::", AddressScope.Local)] // 6to4 of 127.0.0.1
    [InlineData("2001:0:7f00:1::", AddressScope.Local)] // Teredo server 127.0.0.1
    [InlineData("2001:0:808:808:0:0:80ff:fffe", AddressScope.Local)] // Teredo client 127.0.0.1 (inverted)
    [InlineData("10.0.0.1", AddressScope.Private)]
    [InlineData("10.255.255.255", AddressScope.Private)]
    [InlineData("172.16.0.1", AddressScope.Private)]
    [InlineData("172.31.255.255", AddressScope.Private)]
    [InlineData("192.168.0.1", AddressScope.Private)]
    [InlineData("100.64.0.1", AddressScope.Private)]
    [InlineData("100.127.255.255", AddressScope.Private)]
    [InlineData("198.18.0.1", AddressScope.Private)]
    [InlineData("198.19.255.255", AddressScope.Private)]
    [InlineData("192.0.0.1", AddressScope.Private)]
    [InlineData("192.0.2.1", AddressScope.Private)]
    [InlineData("198.51.100.1", AddressScope.Private)]
    [InlineData("203.0.113.1", AddressScope.Private)]
    [InlineData("224.0.0.1", AddressScope.Private)]
    [InlineData("239.255.255.250", AddressScope.Private)]
    [InlineData("240.0.0.1", AddressScope.Private)]
    [InlineData("255.255.255.255", AddressScope.Private)]
    [InlineData("fc00::1", AddressScope.Private)]
    [InlineData("fdff::1", AddressScope.Private)]
    [InlineData("fd00:ec3::1", AddressScope.Private)]
    [InlineData("fec0::1", AddressScope.Private)]
    [InlineData("ff02::1", AddressScope.Private)]
    [InlineData("2001:db8::1", AddressScope.Private)]
    [InlineData("100::1", AddressScope.Private)]
    [InlineData("64:ff9b:1::1", AddressScope.Private)]
    [InlineData("::ffff:10.1.2.3", AddressScope.Private)]
    [InlineData("::10.1.2.3", AddressScope.Private)]
    [InlineData("64:ff9b::c0a8:101", AddressScope.Private)] // NAT64 of 192.168.1.1
    [InlineData("2002:c0a8:101::", AddressScope.Private)] // 6to4 of 192.168.1.1
    [InlineData("2001:0:c0a8:101::1", AddressScope.Private)] // Teredo server 192.168.1.1
    [InlineData("8.8.8.8", AddressScope.Public)]
    [InlineData("1.1.1.1", AddressScope.Public)]
    [InlineData("172.15.255.255", AddressScope.Public)]
    [InlineData("172.32.0.1", AddressScope.Public)]
    [InlineData("100.63.255.255", AddressScope.Public)]
    [InlineData("100.128.0.1", AddressScope.Public)]
    [InlineData("198.17.255.255", AddressScope.Public)]
    [InlineData("198.20.0.1", AddressScope.Public)]
    [InlineData("192.0.1.1", AddressScope.Public)]
    [InlineData("223.255.255.255", AddressScope.Public)]
    [InlineData("169.253.0.1", AddressScope.Public)]
    [InlineData("2606:4700:4700::1111", AddressScope.Public)]
    [InlineData("2001:4860:4860::8888", AddressScope.Public)]
    [InlineData("::ffff:8.8.8.8", AddressScope.Public)]
    [InlineData("64:ff9b::808:808", AddressScope.Public)] // NAT64 of 8.8.8.8
    [InlineData("2002:808:808::", AddressScope.Public)] // 6to4 of 8.8.8.8
    public void Addresses_are_classified_by_where_they_lead(string address, AddressScope expected) =>
        Assert.Equal(expected, ScriptAddressPolicy.Classify(System.Net.IPAddress.Parse(address)));

    [Fact]
    public void A_teredo_address_is_as_strict_as_its_stricter_half()
    {
        // public server 8.8.8.8, client 192.168.1.1 (inverted: 3f57fefe)
        Assert.Equal(AddressScope.Private, ScriptAddressPolicy.Classify(System.Net.IPAddress.Parse("2001:0:808:808:0:0:3f57:fefe")));
        // public server and public client (8.8.4.4 inverted: f7f7fbfb)
        Assert.Equal(AddressScope.Public, ScriptAddressPolicy.Classify(System.Net.IPAddress.Parse("2001:0:808:808:0:0:f7f7:fbfb")));
    }

    [Theory]
    [InlineData("", "there is none")]
    [InlineData("function getEvents() { return []; }", "does not define a function getGroups")]
    [InlineData("function getGroups() { return 5; }", "must return an array")]
    [InlineData("function getGroups() { return [{ name: 'x' }]; }", "has no id")]
    [InlineData("function getGroups() { throw new Error('boom'); }", "boom")]
    [InlineData("function getGroups() { return [{ id: 'g', members: [{ id: 'm' }] }]; }", "has no name")]
    [InlineData("function getGroups() { return [{ id: 'g', members: [{ id: 'm', name: 'M', birthDate: 'soon' }] }]; }", "is not a date")]
    [InlineData("function getGroups( {", "")]
    public async Task A_script_that_cannot_deliver_is_reported_as_unavailable(string script, string reason)
    {
        using var source = Source(script);

        var ex = await Assert.ThrowsAsync<DataSourceUnavailableException>(() => source.GetGroupsAsync(CancellationToken.None));

        Assert.Contains(reason, ex.Message);
    }

    [Fact]
    public async Task A_script_that_runs_forever_is_stopped()
    {
        using var source = Source("function getGroups() { while (true) { } }", TimeSpan.FromMilliseconds(300), maxStatements: 0);

        var ex = await Assert.ThrowsAsync<DataSourceUnavailableException>(() => source.GetGroupsAsync(CancellationToken.None));

        Assert.Contains("took longer", ex.Message);
    }

    [Fact]
    public async Task A_script_that_runs_too_many_statements_is_stopped_before_the_timeout()
    {
        using var source = Source("function getGroups() { var i = 0; while (true) { i++; } }", maxStatements: 10_000);

        var ex = await Assert.ThrowsAsync<DataSourceUnavailableException>(() => source.GetGroupsAsync(CancellationToken.None));

        Assert.IsType<Jint.Runtime.StatementsCountOverflowException>(ex.InnerException);
    }

    [Fact]
    public async Task A_script_that_allocates_too_much_is_stopped()
    {
        using var source = Source("function getGroups() { var s = 'xxxxxxxxxxxxxxxx'; while (true) { s = s + s; } }", maxStatements: 0);

        var ex = await Assert.ThrowsAsync<DataSourceUnavailableException>(() => source.GetGroupsAsync(CancellationToken.None));

        Assert.IsType<Jint.Runtime.MemoryLimitExceededException>(ex.InnerException);
    }

    [Fact]
    public async Task A_script_that_allocates_arrays_without_end_is_stopped()
    {
        using var source = Source("function getGroups() { var a = []; while (true) { a.push(new Array(100000).fill(1)); } }", maxStatements: 0);

        var ex = await Assert.ThrowsAsync<DataSourceUnavailableException>(() => source.GetGroupsAsync(CancellationToken.None));

        Assert.IsType<Jint.Runtime.MemoryLimitExceededException>(ex.InnerException);
    }

    [Fact]
    public async Task A_script_that_recurses_without_end_is_stopped()
    {
        using var source = Source("function f(n) { return f(n + 1) + 1; } function getGroups() { return f(0); }");

        var ex = await Assert.ThrowsAsync<DataSourceUnavailableException>(() => source.GetGroupsAsync(CancellationToken.None));

        Assert.IsType<Jint.Runtime.RecursionDepthOverflowException>(ex.InnerException);
    }

    [Fact]
    public async Task A_stopped_script_does_not_poison_the_next_call()
    {
        using var source = Source("function getGroups() { while (true) { } }", maxStatements: 10_000, failureCache: TimeSpan.Zero);

        await Assert.ThrowsAsync<DataSourceUnavailableException>(() => source.GetGroupsAsync(CancellationToken.None));
        // The gate is released and the engine is fresh: the second call is stopped the same way instead of hanging.
        await Assert.ThrowsAsync<DataSourceUnavailableException>(() => source.GetGroupsAsync(CancellationToken.None));
    }

    private static string CountingScript(string url, int status) => $$"""
        function getGroups() {
          var r = http.get('{{url}}/hit/{{status}}');
          if (!r.ok) throw new Error('upstream answered ' + r.status);
          return [{ id: 'g', name: 'G' }];
        }
        function getEvents() { return []; }
        """;

    [Fact]
    public async Task Concurrent_calls_for_the_same_data_share_one_run_of_the_script()
    {
        await using var service = await StartServiceAsync();
        using var source = Source(CountingScript(service.Url, 200));

        var results = await Task.WhenAll(Enumerable.Range(0, 5).Select(_ => source.GetGroupsAsync(CancellationToken.None)));

        Assert.Equal(1, service.Hits);
        Assert.All(results, r => Assert.Same(results[0], r));
    }

    [Fact]
    public async Task Concurrent_calls_for_a_failing_service_share_one_failed_run()
    {
        await using var service = await StartServiceAsync();
        using var source = Source(CountingScript(service.Url, 503));

        var calls = Enumerable.Range(0, 5).Select(_ => source.GetGroupsAsync(CancellationToken.None)).ToList();
        var errors = new List<DataSourceUnavailableException>();
        foreach (var call in calls) errors.Add(await Assert.ThrowsAsync<DataSourceUnavailableException>(() => call));

        Assert.Equal(1, service.Hits);
        Assert.All(errors, e => Assert.Contains("upstream answered 503", e.Message));
    }

    [Fact]
    public async Task A_failed_run_is_not_repeated_within_the_failure_window_and_the_message_says_how_long_is_left()
    {
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 10, 13, 10, 0, 0, TimeSpan.Zero));
        await using var service = await StartServiceAsync();
        using var source = Source(CountingScript(service.Url, 500), clock: new ClubClock(time, Clock.Zone), failureCache: TimeSpan.FromSeconds(30));

        var first = await Assert.ThrowsAsync<DataSourceUnavailableException>(() => source.GetGroupsAsync(CancellationToken.None));
        time.Advance(TimeSpan.FromSeconds(10));
        var second = await Assert.ThrowsAsync<DataSourceUnavailableException>(() => source.GetGroupsAsync(CancellationToken.None));

        Assert.Equal(1, service.Hits);
        Assert.Contains("upstream answered 500", first.Message);
        Assert.Contains("upstream answered 500", second.Message);
        Assert.DoesNotContain("not asked again", first.Message);
        Assert.Contains("another 20 seconds", second.Message);
        Assert.Same(first.InnerException, second.InnerException);
    }

    [Fact]
    public async Task A_failed_run_is_tried_again_once_the_failure_window_is_over_and_a_success_is_cached_as_usual()
    {
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 10, 13, 10, 0, 0, TimeSpan.Zero));
        await using var service = await StartServiceAsync();
        service.Status = 500;
        using var source = Source(CountingScript(service.Url, 0), clock: new ClubClock(time, Clock.Zone), failureCache: TimeSpan.FromSeconds(30));

        await Assert.ThrowsAsync<DataSourceUnavailableException>(() => source.GetGroupsAsync(CancellationToken.None));
        time.Advance(TimeSpan.FromSeconds(29));
        await Assert.ThrowsAsync<DataSourceUnavailableException>(() => source.GetGroupsAsync(CancellationToken.None));
        Assert.Equal(1, service.Hits);

        service.Status = 200;
        time.Advance(TimeSpan.FromSeconds(1)); // exactly the end of the window
        var groups = await source.GetGroupsAsync(CancellationToken.None);
        Assert.Equal(2, service.Hits);
        Assert.Same(groups, await source.GetGroupsAsync(CancellationToken.None));
        Assert.Equal(2, service.Hits);
    }

    [Fact]
    public async Task A_timeout_is_remembered_like_any_other_failure()
    {
        await using var service = await StartServiceAsync();
        service.Delay = TimeSpan.FromSeconds(5);
        using var source = Source(CountingScript(service.Url, 200), TimeSpan.FromMilliseconds(300));

        var first = await Assert.ThrowsAsync<DataSourceUnavailableException>(() => source.GetGroupsAsync(CancellationToken.None));
        var watch = System.Diagnostics.Stopwatch.StartNew();
        var second = await Assert.ThrowsAsync<DataSourceUnavailableException>(() => source.GetGroupsAsync(CancellationToken.None));

        Assert.Contains("took longer", first.Message);
        Assert.Contains("took longer", second.Message);
        Assert.True(watch.Elapsed < TimeSpan.FromMilliseconds(250), "the second call must not run the script again");
    }

    [Fact]
    public async Task A_timeout_holds_for_all_data_because_the_service_does_not_answer_for_any()
    {
        await using var service = await StartServiceAsync();
        service.Delay = TimeSpan.FromSeconds(5);
        using var source = Source($$"""
            function getGroups() { return [{ id: 'g', name: 'G' }]; }
            function getEvents(groupId) { http.get('{{service.Url}}/hit/200'); return []; }
            """, TimeSpan.FromMilliseconds(300));
        var from = DateTimeOffset.Parse("2026-09-01T00:00:00Z");
        var to = DateTimeOffset.Parse("2026-10-01T00:00:00Z");

        await Assert.ThrowsAsync<DataSourceUnavailableException>(() => source.GetEventsAsync("a", from, to, CancellationToken.None));
        var watch = System.Diagnostics.Stopwatch.StartNew();
        var other = await Assert.ThrowsAsync<DataSourceUnavailableException>(() => source.GetEventsAsync("b", from, to, CancellationToken.None));
        var groups = await Assert.ThrowsAsync<DataSourceUnavailableException>(() => source.GetGroupsAsync(CancellationToken.None));

        Assert.Contains("took longer", other.Message);
        Assert.Contains("took longer", groups.Message);
        Assert.Equal(1, service.Hits);
        Assert.True(watch.Elapsed < TimeSpan.FromMilliseconds(250), "no further call may wait out the timeout");
    }

    [Fact]
    public async Task A_request_that_times_out_holds_for_all_data_like_the_script_timeout()
    {
        await using var service = await StartServiceAsync();
        service.Delay = TimeSpan.FromSeconds(5);
        using var source = Source($$"""
            function getGroups() { var r = http.get('{{service.Url}}/hit/200'); return [{ id: 'g', name: 'G' }]; }
            function getEvents(groupId) { http.get('{{service.Url}}/hit/200'); return []; }
            """);
        source.RequestTimeout = TimeSpan.FromMilliseconds(300);
        var from = DateTimeOffset.Parse("2026-09-01T00:00:00Z");
        var to = DateTimeOffset.Parse("2026-10-01T00:00:00Z");

        await Assert.ThrowsAsync<DataSourceUnavailableException>(() => source.GetGroupsAsync(CancellationToken.None));
        var watch = System.Diagnostics.Stopwatch.StartNew();
        await Assert.ThrowsAsync<DataSourceUnavailableException>(() => source.GetEventsAsync("a", from, to, CancellationToken.None));
        await Assert.ThrowsAsync<DataSourceUnavailableException>(() => source.GetEventsAsync("b", from, to, CancellationToken.None));

        Assert.Equal(1, service.Hits);
        Assert.True(watch.Elapsed < TimeSpan.FromMilliseconds(250), "no further call may wait out the request timeout");
    }

    [Fact]
    public async Task A_failure_is_not_remembered_when_the_window_is_zero_or_negative()
    {
        await using var service = await StartServiceAsync();
        foreach (var window in new[] { TimeSpan.Zero, TimeSpan.FromSeconds(-5) })
        {
            service.Hits = 0;
            using var source = Source(CountingScript(service.Url, 500), failureCache: window);

            await Assert.ThrowsAsync<DataSourceUnavailableException>(() => source.GetGroupsAsync(CancellationToken.None));
            await Assert.ThrowsAsync<DataSourceUnavailableException>(() => source.GetGroupsAsync(CancellationToken.None));

            Assert.Equal(2, service.Hits);
        }
    }

    [Fact]
    public async Task Refreshing_the_cache_also_forgets_a_failure()
    {
        await using var service = await StartServiceAsync();
        service.Status = 500;
        using var source = Source(CountingScript(service.Url, 0));

        await Assert.ThrowsAsync<DataSourceUnavailableException>(() => source.GetGroupsAsync(CancellationToken.None));
        service.Status = 200;
        await Assert.ThrowsAsync<DataSourceUnavailableException>(() => source.GetGroupsAsync(CancellationToken.None)); // still remembered
        source.InvalidateCache();

        Assert.Single(await source.GetGroupsAsync(CancellationToken.None));
        Assert.Equal(2, service.Hits);
    }

    [Fact]
    public async Task A_failure_only_holds_for_the_data_that_failed()
    {
        using var source = Source("""
            function getGroups() { throw new Error('groups down'); }
            function getEvents(groupId) { if (groupId == 'bad') throw new Error('events down'); return []; }
            """);
        var from = DateTimeOffset.Parse("2026-09-01T00:00:00Z");
        var to = DateTimeOffset.Parse("2026-10-01T00:00:00Z");

        await Assert.ThrowsAsync<DataSourceUnavailableException>(() => source.GetGroupsAsync(CancellationToken.None));
        await Assert.ThrowsAsync<DataSourceUnavailableException>(() => source.GetEventsAsync("bad", from, to, CancellationToken.None));

        Assert.Empty(await source.GetEventsAsync("good", from, to, CancellationToken.None));
        // Another window of the failing group is another key and is tried by itself.
        await Assert.ThrowsAsync<DataSourceUnavailableException>(() => source.GetEventsAsync("bad", from.AddYears(1), to.AddYears(1), CancellationToken.None));
    }

    [Fact]
    public async Task A_caller_who_gives_up_while_waiting_neither_disturbs_the_run_nor_leaves_a_failure_behind()
    {
        await using var service = await StartServiceAsync();
        using var source = Source(CountingScript(service.Url, 200));
        var running = source.GetGroupsAsync(CancellationToken.None);
        await Task.Delay(50); // the first caller holds the gate
        using var cancel = new CancellationTokenSource(TimeSpan.FromMilliseconds(50));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => source.GetGroupsAsync(cancel.Token));

        Assert.Single(await running);
        Assert.Single(await source.GetGroupsAsync(CancellationToken.None));
        Assert.Equal(1, service.Hits);
    }

    [Fact]
    public async Task A_run_cancelled_by_its_caller_is_not_remembered_as_a_failure()
    {
        await using var service = await StartServiceAsync();
        service.Delay = TimeSpan.FromSeconds(5);
        using var source = Source(CountingScript(service.Url, 200));
        using var cancel = new CancellationTokenSource(TimeSpan.FromMilliseconds(200));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => source.GetGroupsAsync(cancel.Token));
        service.Delay = TimeSpan.Zero;

        Assert.Single(await source.GetGroupsAsync(CancellationToken.None));
    }

    [Theory]
    [InlineData("function getGroups() { return [{ id: 'x', name: String(System.Environment.MachineName) }]; }")]
    [InlineData("function getGroups() { return [{ id: 'x', name: String(importNamespace('System')) }]; }")]
    [InlineData("function getGroups() { return [{ id: 'x', name: Function('return System')().IO.Path.GetTempPath() }]; }")]
    [InlineData("function getGroups() { return [{ id: 'x', name: (function () {}).constructor.constructor('return process')().cwd() }]; }")]
    public async Task A_script_cannot_call_into_the_clr(string script)
    {
        using var source = Source(script);

        var ex = await Assert.ThrowsAsync<DataSourceUnavailableException>(() => source.GetGroupsAsync(CancellationToken.None));

        Assert.IsType<Jint.Runtime.JavaScriptException>(ex.InnerException);
    }

    [Fact]
    public async Task Every_run_is_logged_with_its_duration()
    {
        var logger = new ListLogger();
        using var ok = Source(StaticScript, logger: logger);
        using var bad = Source("function getGroups() { throw new Error('boom'); }", logger: logger);
        using var slow = Source("function getGroups() { while (true) { } }", TimeSpan.FromMilliseconds(200), maxStatements: 0, logger: logger);

        await ok.GetGroupsAsync(CancellationToken.None);
        await Assert.ThrowsAsync<DataSourceUnavailableException>(() => bad.GetGroupsAsync(CancellationToken.None));
        await Assert.ThrowsAsync<DataSourceUnavailableException>(() => slow.GetGroupsAsync(CancellationToken.None));

        Assert.Single(logger.Messages, m => m.StartsWith("The custom data script succeeded after ") && m.EndsWith(" ms."));
        Assert.Single(logger.Messages, m => m.StartsWith("The custom data script failed after "));
        Assert.Single(logger.Messages, m => m.StartsWith("The custom data script timed out after "));
    }

    [Fact]
    public async Task The_script_cannot_reach_the_system_it_runs_on()
    {
        using var source = Source("function getGroups() { return [{ id: typeof System + '|' + typeof process + '|' + typeof require, name: 'x' }]; }");

        Assert.Equal("undefined|undefined|undefined", Assert.Single(await source.GetGroupsAsync(CancellationToken.None)).Id);
    }

    [Fact]
    public void The_syntax_of_a_script_is_checked_without_running_it()
    {
        Assert.Null(CustomScriptDataSource.CheckSyntax("function getGroups() { return []; }"));
        Assert.NotNull(CustomScriptDataSource.CheckSyntax("function getGroups( {"));
        Assert.NotNull(CustomScriptDataSource.CheckSyntax("  "));
    }

    [Fact]
    public async Task The_script_asks_other_services_with_http()
    {
        await using var service = await StartServiceAsync();
        using var source = Source($$"""
            function getGroups() {
              var answer = http.get('{{service.Url}}/groups', { 'X-Token': 'secret' }).json();
              var posted = http.post('{{service.Url}}/echo', { hello: 'world' });
              return answer.map(function (g) { return { id: g.id, name: g.name + ' ' + posted.status + ' ' + posted.json().hello }; });
            }
            """);

        var group = Assert.Single(await source.GetGroupsAsync(CancellationToken.None));

        Assert.Equal(("g1", "Dojo 200 world"), (group.Id, group.Name));
        Assert.Equal("secret", service.LastToken);
    }

    [Fact]
    public async Task An_address_that_is_not_http_is_refused()
    {
        using var source = Source("function getGroups() { http.get('file:///etc/passwd'); return []; }");

        var ex = await Assert.ThrowsAsync<DataSourceUnavailableException>(() => source.GetGroupsAsync(CancellationToken.None));

        Assert.Contains("not an http or https address", ex.Message);
    }

    // --- Secrets of the script ---------------------------------------------------------------------------------

    private const string Token = "tok-live-0123456789";

    [Fact]
    public async Task A_secret_is_read_by_name_without_regard_to_case()
    {
        using var source = Source("function getGroups() { return [{ id: secrets.get('API_TOKEN') + '|' + secrets.get('api_token'), name: 'G' }]; }",
            secrets: new() { ["Api_Token"] = Token });

        Assert.Equal($"{Token}|{Token}", Assert.Single(await source.GetGroupsAsync(CancellationToken.None)).Id);
    }

    [Fact]
    public async Task A_secret_goes_into_the_header_of_a_request()
    {
        await using var service = await StartServiceAsync();
        using var source = Source($$"""
            function getGroups() {
              http.get('{{service.Url}}/groups', { 'X-Token': 'Bearer ' + secrets.get('API_TOKEN') });
              return [{ id: 'g', name: 'G' }];
            }
            """, secrets: new() { ["API_TOKEN"] = Token });

        await source.GetGroupsAsync(CancellationToken.None);

        Assert.Equal("Bearer " + Token, service.LastToken);
    }

    [Theory]
    [InlineData("secrets.get('MISSING')")]
    [InlineData("secrets.get('EMPTY')")] // no value is no secret
    [InlineData("secrets.get('')")]
    [InlineData("secrets.get()")]
    public async Task A_secret_that_is_not_set_is_an_error_of_the_script_that_names_it_and_gives_nothing_else_away(string call)
    {
        using var source = Source($"function getGroups() {{ var s = {call}; return [{{ id: s, name: 'G' }}]; }}", secrets: new() { ["EMPTY"] = "", ["OTHER"] = Token });

        var ex = await Assert.ThrowsAsync<DataSourceUnavailableException>(() => source.GetGroupsAsync(CancellationToken.None));

        Assert.Contains("asks for the secret", ex.Message);
        Assert.DoesNotContain(Token, ex.Message);
    }

    [Fact]
    public async Task The_values_of_secrets_are_kept_out_of_errors_and_logs()
    {
        var logger = new ListLogger();
        using var source = Source("""
            function getGroups() {
              console.log('using ' + secrets.get('API_TOKEN'));
              throw new Error('refused: ' + secrets.get('API_TOKEN') + ' / ' + secrets.get('LONGER'));
            }
            """, logger: logger, secrets: new() { ["API_TOKEN"] = Token, ["LONGER"] = Token + "-extra" });

        var ex = await Assert.ThrowsAsync<DataSourceUnavailableException>(() => source.GetGroupsAsync(CancellationToken.None));

        Assert.Contains("refused: *** / ***", ex.Message); // the longer value goes whole, not as the shorter one with "-extra" left
        Assert.Contains(logger.Messages, m => m == "Custom data script: using ***");
        Assert.DoesNotContain(logger.Messages.Append(ex.Message).Append(ex.ToString()), m => m.Contains(Token));
        Assert.Null(ex.InnerException);
    }

    [Fact]
    public async Task A_secret_that_ends_up_in_an_address_is_not_given_away_by_the_error_about_the_address()
    {
        var logger = new ListLogger();
        using var source = Source("function getGroups() { http.get('ftp://example.org/?key=' + secrets.get('API_TOKEN')); return []; }",
            logger: logger, secrets: new() { ["API_TOKEN"] = Token });

        var ex = await Assert.ThrowsAsync<DataSourceUnavailableException>(() => source.GetGroupsAsync(CancellationToken.None));

        Assert.Contains("not an http or https address", ex.Message);
        Assert.Contains("ftp://example.org/?key=***", ex.Message);
        Assert.DoesNotContain(logger.Messages.Append(ex.Message), m => m.Contains(Token));
    }

    [Fact]
    public async Task A_script_cannot_list_the_secrets_or_reach_the_options()
    {
        using var source = Source("function getGroups() { return [{ id: typeof secrets.list + '|' + typeof secrets.all + '|' + Object.keys(secrets).join(','), name: 'G' }]; }",
            secrets: new() { ["API_TOKEN"] = Token });

        Assert.Equal("undefined|undefined|get", Assert.Single(await source.GetGroupsAsync(CancellationToken.None)).Id);
    }

    [Fact]
    public async Task A_failure_that_is_not_a_timeout_stays_an_ordinary_failure_and_a_request_timeout_is_still_recognised()
    {
        await using var service = await StartServiceAsync();
        service.Delay = TimeSpan.FromSeconds(10);
        using var source = Source($"function getGroups() {{ http.get('{service.Url}/hit/200?key=' + secrets.get('API_TOKEN')); return []; }}",
            secrets: new() { ["API_TOKEN"] = Token });
        source.RequestTimeout = TimeSpan.FromMilliseconds(200);

        var first = await Assert.ThrowsAsync<DataSourceUnavailableException>(() => source.GetGroupsAsync(CancellationToken.None));
        var second = await Assert.ThrowsAsync<DataSourceUnavailableException>(() => source.GetEventsAsync("g", DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch.AddDays(1), CancellationToken.None));

        Assert.DoesNotContain(Token, first.Message);
        Assert.Contains("not asked again", second.Message); // a timeout holds for all data
    }

    private sealed class Service(WebApplication app) : IAsyncDisposable
    {
        public string Url => app.Urls.First();

        public string? LastToken { get; set; }

        /// <summary>How often <c>/hit/{status}</c> was asked.</summary>
        public int Hits;

        /// <summary>The answer of <c>/hit/0</c> (other values are answered as given); 0 = 200.</summary>
        public int Status { get; set; }

        /// <summary>How long <c>/hit/*</c> waits before it answers.</summary>
        public TimeSpan Delay { get; set; } = TimeSpan.FromMilliseconds(300);

        public async ValueTask DisposeAsync() => await app.DisposeAsync();
    }

    private static async Task<Service> StartServiceAsync()
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Logging.ClearProviders();
        var app = builder.Build();
        var service = new Service(app);
        app.MapGet("/groups", (HttpContext context) =>
        {
            service.LastToken = context.Request.Headers["X-Token"];
            return Results.Json(new[] { new { id = "g1", name = "Dojo" } });
        });
        app.MapGet("/hit/{status:int}", async (int status, CancellationToken aborted) =>
        {
            Interlocked.Increment(ref service.Hits);
            await Task.Delay(service.Delay, aborted);
            return Results.StatusCode(status == 0 ? (service.Status == 0 ? 200 : service.Status) : status);
        });
        app.MapPost("/echo", (System.Text.Json.JsonElement body) => Results.Json(body));
        app.MapGet("/to-lan", () => Results.Redirect("http://192.168.1.1/"));
        await app.StartAsync();
        return service;
    }
}
