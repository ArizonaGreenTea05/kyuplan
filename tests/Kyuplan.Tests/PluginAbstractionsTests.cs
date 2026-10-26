using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Kyuplan.Plugins;

namespace Kyuplan.Tests;

/// <summary>The <c>Kyuplan.Plugins.Abstractions</c> project: what a plugin compiles against. Its model and rules behave like the API's own.</summary>
public sealed class PluginAbstractionsTests
{
    private static EventInfo Event(IReadOnlyCollection<string>? trainers) => new(
        "e", "Training", DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch.AddHours(1), false,
        new Dictionary<string, Rsvp> { ["t"] = Rsvp.Accepted, ["a"] = Rsvp.Declined }, trainers);

    // ----- the model -----

    [Fact]
    public void Participants_are_the_responses_without_the_trainers()
    {
        var e = Event(["t", "not-invited"]);

        Assert.Equal(["a"], e.Participants.Select(p => p.Key));
        Assert.True(e.IsTrainer("t"));
        Assert.True(e.IsTrainer("not-invited"));
        Assert.False(e.IsTrainer("a"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void An_event_without_trainers_has_all_responses_as_participants(bool emptyList)
    {
        var e = Event(emptyList ? [] : null);

        Assert.Equal(["t", "a"], e.Participants.Select(p => p.Key));
        Assert.False(e.IsTrainer("t"));
    }

    [Fact]
    public void A_data_source_is_no_demo_unless_it_says_so()
    {
        IDataSource source = new StubSource();

        Assert.False(source.IsDemo);
        Assert.Equal("stub", source.Kind);
    }

    [Fact]
    public void The_unavailable_exception_keeps_message_and_cause()
    {
        var cause = new TimeoutException();

        var ex = new DataSourceUnavailableException("The service is down.", cause);

        Assert.Equal("The service is down.", ex.Message);
        Assert.Same(cause, ex.InnerException);
        Assert.Null(new DataSourceUnavailableException("x").InnerException);
    }

    // ----- RequirePermission -----

    [Fact]
    public async Task A_signed_in_user_with_one_of_the_permissions_is_let_in()
    {
        Assert.True(await Allows(User("demo.view"), "demo.view", "demo.edit"));
        Assert.True(await Allows(User("demo.edit", "other.x"), "demo.view", "demo.edit"));
    }

    [Fact]
    public async Task A_signed_in_user_without_the_permission_is_refused()
    {
        Assert.False(await Allows(User("other.view"), "demo.view"));
        Assert.False(await Allows(User(), "demo.view"));
        Assert.False(await Allows(User("DEMO.VIEW"), "demo.view"));
    }

    [Fact]
    public async Task Only_the_permission_claim_counts()
    {
        var user = new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.Role, "demo.view")], "test"));

        Assert.False(await Allows(user, "demo.view"));
        Assert.Equal("permission", PluginEndpointExtensions.PermissionClaimType);
    }

    [Fact]
    public async Task Nobody_is_let_in_without_a_login_even_with_the_claim()
    {
        var anonymous = new ClaimsPrincipal(new ClaimsIdentity([new Claim("permission", "demo.view")]));

        Assert.False(await Allows(anonymous, "demo.view"));
        Assert.False(await Allows(new ClaimsPrincipal(new ClaimsIdentity()), "demo.view"));
    }

    [Fact]
    public async Task No_permission_listed_lets_nobody_in()
    {
        Assert.False(await Allows(User("demo.view")));
    }

    private static ClaimsPrincipal User(params string[] permissions) =>
        new(new ClaimsIdentity(permissions.Select(p => new Claim("permission", p)), "test"));

    private static async Task<bool> Allows(ClaimsPrincipal user, params string[] anyOf)
    {
        var app = WebApplication.CreateBuilder().Build();
        app.MapGet("/x", () => "ok").RequirePermission(anyOf);
        var endpoint = ((IEndpointRouteBuilder)app).DataSources.Single().Endpoints.Single();
        var policy = Assert.IsType<AuthorizationPolicy>(endpoint.Metadata.GetMetadata<AuthorizationPolicy>());

        await using var services = new ServiceCollection().AddLogging().AddAuthorization().BuildServiceProvider();
        var result = await services.GetRequiredService<IAuthorizationService>().AuthorizeAsync(user, policy);
        return result.Succeeded;
    }

    private sealed class StubSource : IDataSource
    {
        public string Kind => "stub";

        public Task<IReadOnlyList<GroupInfo>> GetGroupsAsync(CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<GroupInfo>>([]);

        public Task<IReadOnlyList<EventInfo>> GetEventsAsync(string groupId, DateTimeOffset from, DateTimeOffset to, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<EventInfo>>([]);

        public void InvalidateCache()
        {
        }
    }
}
