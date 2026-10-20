using System.Globalization;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Jint;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;
using Kyuplan.Api.Configuration;
using Kyuplan.Api.Domain;
using Kyuplan.Api.Infrastructure;
using Kyuplan.Api.Persistence;
using Kyuplan.Api.Spond;
using Kyuplan.Contracts;

namespace Kyuplan.Api.Sources;

/// <summary>
/// The data source of <see cref="DataSourceKinds.Custom"/>: the endpoints are two JavaScript functions the administrator wrote
/// (<c>DataSource:Script</c>, edited in the settings), which can ask any service over HTTP:
/// <code>
/// function getGroups() { return [{ id, name, subGroups: [{ id, name }], members: [{ id, name, subGroupIds: [id], birthDate: "2012-05-31" }] }]; }
/// function getEvents(groupId, from, to) { return [{ id, name, start, end, cancelled, responses: { memberId: "accepted" } }]; }
/// </code>
/// <c>from</c> and <c>to</c> are ISO 8601 strings; <c>start</c> and <c>end</c> may be ISO 8601 strings (without an offset: UTC), Date objects or epoch
/// milliseconds; an answer is <c>accepted</c>, <c>declined</c>, <c>unanswered</c> or <c>other</c>; the members in <c>responses</c> are the invited ones.
/// The script gets <c>http.get/post/put/delete(url, [body,] headers)</c> (answering <c>{ status, ok, text, json() }</c>) and <c>console.log</c>, nothing else:
/// no file system, no .NET. It runs in a fresh engine for every call, one call at a time, with a time, memory and recursion limit. What it returns is cached
/// for <see cref="DataSourceOptions.CacheDuration"/>.
/// </summary>
public sealed class CustomScriptDataSource(
    IOptions<DataSourceOptions> options, ClubClock clock, ILogger<CustomScriptDataSource> logger) : IDataSource, IDisposable
{
    private const int MaxMemoryBytes = 128 * 1024 * 1024;
    private const int MaxRecursion = 300;
    private const int MaxResponseBytes = 10 * 1024 * 1024;

    private readonly HttpClient _http = CreateHttp(options);

    private readonly MemoryCache _cache = new(new MemoryCacheOptions());
    private readonly SemaphoreSlim _gate = new(1, 1);

    public bool IsDemo => false;

    public string Kind => DataSourceKinds.Custom;

    public Task<IReadOnlyList<GroupInfo>> GetGroupsAsync(CancellationToken cancellationToken) =>
        CachedAsync("groups", () => RunAsync(engine => ParseGroups(Call(engine, "getGroups")), cancellationToken));

    public Task<IReadOnlyList<EventInfo>> GetEventsAsync(string groupId, DateTimeOffset from, DateTimeOffset to, CancellationToken cancellationToken)
    {
        // The window is widened to whole UTC days (the interface allows a superset): callers pass "now + n days", which would otherwise be a new
        // cache key, and a new run of the script, on every call.
        var start = from.UtcDateTime.Date;
        var end = to.UtcDateTime.Date is var last && last < DateTime.MaxValue.Date ? last.AddDays(1) : last;
        return CachedAsync($"events|{groupId}|{start.Ticks}|{end.Ticks}", () => RunAsync(
            engine => ParseEvents(Call(engine, "getEvents", groupId, start.ToString("o", CultureInfo.InvariantCulture), end.ToString("o", CultureInfo.InvariantCulture))),
            cancellationToken));
    }

    public void InvalidateCache() => _cache.Compact(1.0);

    public void Dispose()
    {
        _http.Dispose();
        _cache.Dispose();
        _gate.Dispose();
    }

    /// <summary>The problem with a script that cannot even be read (a syntax error), or <c>null</c>. Nothing is run.</summary>
    public static string? CheckSyntax(string? script)
    {
        if (string.IsNullOrWhiteSpace(script)) return "The script is empty.";

        try
        {
            Engine.PrepareScript(script);
            return null;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return ex.Message;
        }
    }

    private async Task<IReadOnlyList<T>> CachedAsync<T>(string key, Func<Task<IReadOnlyList<T>>> load)
    {
        if (_cache.TryGetValue(key, out IReadOnlyList<T>? cached) && cached is not null) return cached;

        var fresh = await load();
        _cache.Set(key, fresh, options.Value.CacheDuration);
        return fresh;
    }

    private Task<T> RunAsync<T>(Func<Engine, T> work, CancellationToken cancellationToken)
    {
        var script = options.Value.Script;
        if (string.IsNullOrWhiteSpace(script))
            throw new SpondUnavailableException("The data source is a custom script, but there is none (DataSource:Script; the settings page saves it).");

        // The script asks over HTTP synchronously, so it runs on a thread of its own and never blocks a request thread.
        return Task.Run(async () =>
        {
            await _gate.WaitAsync(cancellationToken);
            try
            {
                // The time limit also ends a request that is waiting for an answer, which Jint's own timeout cannot interrupt.
                using var limit = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                limit.CancelAfter(options.Value.ScriptTimeout);
                var engine = CreateEngine(limit.Token);
                engine.Execute(script);
                return work(engine);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                throw new SpondUnavailableException($"The custom data script took longer than {options.Value.ScriptTimeout.TotalSeconds:0} seconds.");
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogWarning(ex, "The custom data script failed.");
                throw new SpondUnavailableException($"The custom data script failed: {ex.Message}", ex);
            }
            finally
            {
                _gate.Release();
            }
        }, cancellationToken);
    }

    private Engine CreateEngine(CancellationToken cancellationToken)
    {
        var engine = new Engine(o => o
            .TimeoutInterval(options.Value.ScriptTimeout)
            .LimitMemory(MaxMemoryBytes)
            .LimitRecursion(MaxRecursion)
            .CancellationToken(cancellationToken));

        engine.SetValue("__request", new Func<string, string, string?, string?, string>((method, url, body, headers) => Request(method, url, body, headers, cancellationToken)));
        engine.SetValue("__log", new Action<string>(message => logger.LogInformation("Custom data script: {Message}", message)));
        engine.Execute(Prelude);
        return engine;
    }

    private static string Call(Engine engine, string function, params object[] arguments)
    {
        if (!engine.GetValue(function).IsObject() || engine.GetValue(function).AsObject() is not Jint.Native.Function.Function)
            throw new InvalidOperationException($"The script does not define a function {function}().");

        var result = engine.Invoke(function, arguments);
        if (result.IsUndefined() || result.IsNull()) throw new InvalidOperationException($"{function}() returned nothing; it must return an array.");

        var json = new Jint.Native.Json.JsonSerializer(engine).Serialize(result);
        return json.IsString() ? json.AsString() : throw new InvalidOperationException($"{function}() returned something that cannot be read as data.");
    }

    /// <summary>What <c>http.get</c> and friends do: one request, answered as JSON <c>{ status, text }</c>.</summary>
    private string Request(string method, string url, string? body, string? headersJson, CancellationToken cancellationToken)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https"))
            throw new InvalidOperationException($"http: '{url}' is not an http or https address.");

        using var request = new HttpRequestMessage(new HttpMethod(method), uri);
        var contentType = "text/plain";
        if (!string.IsNullOrEmpty(headersJson))
        {
            foreach (var header in JsonDocument.Parse(headersJson).RootElement.EnumerateObject())
            {
                var value = header.Value.ToString();
                if (header.Name.Equals("content-type", StringComparison.OrdinalIgnoreCase)) contentType = value;
                else request.Headers.TryAddWithoutValidation(header.Name, value);
            }
        }

        if (body is not null)
        {
            request.Content = new StringContent(body, Encoding.UTF8);
            request.Content.Headers.ContentType = MediaTypeHeaderValue.Parse(contentType);
        }

        using var response = _http.Send(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        using var stream = response.Content.ReadAsStream(cancellationToken);
        using var buffer = new MemoryStream();
        var chunk = new byte[16 * 1024];
        int read;
        while ((read = stream.Read(chunk)) > 0)
        {
            buffer.Write(chunk, 0, read);
            if (buffer.Length > MaxResponseBytes) throw new InvalidOperationException($"http: the answer of {uri.Host} is larger than {MaxResponseBytes / 1024 / 1024} MB.");
        }

        return System.Text.Json.JsonSerializer.Serialize(new { status = (int)response.StatusCode, text = Encoding.UTF8.GetString(buffer.GetBuffer(), 0, (int)buffer.Length) });
    }

    private static HttpClient CreateHttp(IOptions<DataSourceOptions> options) => new(new SocketsHttpHandler
    {
        ConnectCallback = async (context, cancellationToken) =>
        {
            // Checked on the addresses the name resolved to (redirects and DNS tricks included), not on the text of the URL.
            var addresses = await System.Net.Dns.GetHostAddressesAsync(context.DnsEndPoint.Host, cancellationToken);
            if (!options.Value.AllowLocalAddresses && addresses.Any(IsLocal))
                throw new InvalidOperationException($"http: {context.DnsEndPoint.Host} is a local address (loopback, link-local or the cloud metadata service); DataSource:AllowLocalAddresses allows it.");

            var socket = new System.Net.Sockets.Socket(System.Net.Sockets.SocketType.Stream, System.Net.Sockets.ProtocolType.Tcp) { NoDelay = true };
            try
            {
                await socket.ConnectAsync(addresses, context.DnsEndPoint.Port, cancellationToken);
                return new System.Net.Sockets.NetworkStream(socket, ownsSocket: true);
            }
            catch
            {
                socket.Dispose();
                throw;
            }
        },
    }) { Timeout = TimeSpan.FromSeconds(30) };

    private static bool IsLocal(System.Net.IPAddress address)
    {
        if (address.IsIPv4MappedToIPv6) address = address.MapToIPv4();
        if (System.Net.IPAddress.IsLoopback(address) || address.Equals(System.Net.IPAddress.Any) || address.Equals(System.Net.IPAddress.IPv6Any)) return true;
        if (address.IsIPv6LinkLocal) return true;
        if (address.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork) return false;

        var b = address.GetAddressBytes();
        return b[0] == 169 && b[1] == 254;
    }

    private const string Prelude = """
        var console = { log: function () { __log(Array.prototype.slice.call(arguments).map(String).join(' ')); } };
        var http = (function () {
            function call(method, url, body, headers) {
                var h = {};
                for (var k in (headers || {})) h[k] = headers[k];
                var payload = null;
                if (body !== undefined && body !== null) {
                    if (typeof body === 'string') payload = body;
                    else {
                        payload = JSON.stringify(body);
                        if (!h['Content-Type'] && !h['content-type']) h['Content-Type'] = 'application/json';
                    }
                }
                var r = JSON.parse(__request(method, String(url), payload, JSON.stringify(h)));
                return { status: r.status, ok: r.status >= 200 && r.status < 300, text: r.text, json: function () { return JSON.parse(r.text); } };
            }
            return {
                get: function (url, headers) { return call('GET', url, null, headers); },
                post: function (url, body, headers) { return call('POST', url, body, headers); },
                put: function (url, body, headers) { return call('PUT', url, body, headers); },
                delete: function (url, headers) { return call('DELETE', url, null, headers); }
            };
        })();
        """;

    private static IReadOnlyList<GroupInfo> ParseGroups(string json)
    {
        var groups = new List<GroupInfo>();
        foreach (var (item, index) in Items(json, "getGroups").Select((e, i) => (e, i)))
        {
            var where = $"getGroups()[{index}]";
            var id = Id(item, "id", where);
            var subGroups = Array(item, "subGroups").Select((s, i) => new SubGroupInfo(Id(s, "id", $"{where}.subGroups[{i}]"), Text(s, "name") ?? Id(s, "id", where))).DistinctBy(s => s.Id).ToList();
            var members = Array(item, "members").Select((m, i) =>
            {
                var memberWhere = $"{where}.members[{i}]";
                return new MemberInfo(
                    Id(m, "id", memberWhere),
                    Text(m, "name") ?? throw new FormatException($"{memberWhere} has no name."),
                    Array(m, "subGroupIds").Select(v => v.ValueKind == JsonValueKind.String ? v.GetString()! : v.ToString()).ToList(),
                    BirthDate(m, memberWhere));
            }).DistinctBy(m => m.Id).ToList();
            groups.Add(new GroupInfo(id, Text(item, "name") ?? id, subGroups, members));
        }

        return [.. groups.DistinctBy(g => g.Id)];
    }

    private IReadOnlyList<EventInfo> ParseEvents(string json)
    {
        var events = new List<EventInfo>();
        foreach (var (item, index) in Items(json, "getEvents").Select((e, i) => (e, i)))
        {
            var where = $"getEvents()[{index}]";
            var id = Id(item, "id", where);
            var start = Moment(item, "start", where);
            var end = item.TryGetProperty("end", out var endValue) && endValue.ValueKind != JsonValueKind.Null ? Moment(item, "end", where) : start;
            var responses = new Dictionary<string, Rsvp>();
            if (item.TryGetProperty("responses", out var map) && map.ValueKind == JsonValueKind.Object)
                foreach (var response in map.EnumerateObject())
                    responses[response.Name] = ManualDataService.ParseRsvp(response.Value.ValueKind == JsonValueKind.String ? response.Value.GetString()!.ToLowerInvariant() : "");

            events.Add(new EventInfo(
                id, Text(item, "name") ?? id, clock.ToClub(start), clock.ToClub(end),
                item.TryGetProperty("cancelled", out var cancelled) && cancelled.ValueKind == JsonValueKind.True, responses));
        }

        return [.. events.DistinctBy(e => e.Id)];
    }

    private static List<JsonElement> Items(string json, string function)
    {
        var root = JsonDocument.Parse(json).RootElement.Clone();
        return root.ValueKind == JsonValueKind.Array ? [.. root.EnumerateArray()] : throw new FormatException($"{function}() must return an array.");
    }

    private static IEnumerable<JsonElement> Array(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Array ? value.EnumerateArray() : [];

    private static string? Text(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value) && value.ValueKind is JsonValueKind.String or JsonValueKind.Number
            ? value.ToString().Trim() is { Length: > 0 } text ? text : null
            : null;

    private static string Id(JsonElement element, string name, string where) =>
        Text(element, name) ?? throw new FormatException($"{where} has no {name}.");

    private static DateOnly? BirthDate(JsonElement member, string where)
    {
        if (Text(member, "birthDate") is not { } text) return null;
        return DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var date)
            ? DateOnly.FromDateTime(date.UtcDateTime)
            : throw new FormatException($"{where}.birthDate is not a date: '{text}'.");
    }

    private static DateTimeOffset Moment(JsonElement item, string name, string where)
    {
        if (item.TryGetProperty(name, out var value))
        {
            if (value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out var millis)) return DateTimeOffset.FromUnixTimeMilliseconds(millis);
            if (value.ValueKind == JsonValueKind.String &&
                DateTimeOffset.TryParse(value.GetString(), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var parsed)) return parsed;
        }

        throw new FormatException($"{where}.{name} is not a date and time (use an ISO 8601 string, a Date or epoch milliseconds).");
    }
}
