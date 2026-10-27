using System.Text.Json;
using Kyuplan.Plugins;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;

namespace Kyuplan.TestPlugin;

/// <summary>
/// The plugin of the tests. Which one it plays depends on the id its manifest gives it: <c>spond</c> offers a data source <c>spond</c> (like the real Spond plugin),
/// <c>acme</c> offers a data source <c>acme</c> with pages, permissions and endpoints, <c>broken</c> throws when it is initialised.
/// </summary>
public sealed class TestPlugin : IKyuplanPlugin
{
    public void Initialize(IPluginBuilder builder)
    {
        switch (builder.Id)
        {
            case "spond":
                builder.AddDataSource("spond", context => new StaticDataSource("spond", context.Configuration));
                break;

            case "acme":
                builder.AddDataSource("acme", context => new StaticDataSource("acme", context.Configuration));
                builder.MapEndpoints(group => MapAcme(group, builder));
                break;

            case "broken":
                throw new InvalidOperationException("this plugin is broken");
        }
    }

    private static void MapAcme(RouteGroupBuilder group, IPluginBuilder builder)
    {
        var items = new List<string> { "Alpha", "Beta" };

        group.MapGet("items", () => items.Select((name, index) => new { name, count = index + 1, done = index == 0 }))
            .RequirePermission(builder.Permission("view"));

        group.MapPost("items/add", (JsonElement body) =>
            {
                items.Add(body.GetProperty("name").GetString() ?? "");
                return Results.Ok(new { added = true });
            })
            .RequirePermission(builder.Permission("edit"));

        group.MapDelete("items", () =>
            {
                items.Clear();
                return Results.NoContent();
            })
            .RequirePermission(builder.Permission("edit"));

        group.MapGet("open", () => Results.Ok(new { hello = "world" }));

        group.MapGet("config", (IConfiguration configuration) => Results.Ok(new { apiKey = configuration["Plugins:acme:ApiKey"], first = configuration["Plugins:acme:Rows:0:Name"] }))
            .RequirePermission(builder.Permission("view"));
    }
}

/// <summary>A data source that serves one group, whatever it is asked. Its settings are the ones it was built from.</summary>
public sealed class StaticDataSource(string kind, IConfigurationSection settings) : IDataSource
{
    public string Kind => kind;

    /// <summary>What the data source was configured with: the value of a key of its section.</summary>
    public string? Setting(string key) => settings[key];

    public Task<IReadOnlyList<GroupInfo>> GetGroupsAsync(CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<GroupInfo>>([new GroupInfo("g1", settings["Email"] ?? settings["ApiKey"] ?? "Group", [], [new MemberInfo("m1", "Anna", [])])]);

    public Task<IReadOnlyList<EventInfo>> GetEventsAsync(string groupId, DateTimeOffset from, DateTimeOffset to, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<EventInfo>>([]);

    public void InvalidateCache()
    {
    }
}
