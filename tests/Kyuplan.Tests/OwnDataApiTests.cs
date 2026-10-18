using System.Net;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;
using Kyuplan.Api.Persistence;
using Kyuplan.Api.Statistics;
using Kyuplan.Client.Api;
using Kyuplan.Contracts;
using static Kyuplan.Tests.TestAccounts;

namespace Kyuplan.Tests;

/// <summary>
/// Users who are members: an administrator links them, and what is written about them (comments, evaluations) is hidden from them unless they
/// have the "own" permission, which works without the general one and the other way round. Against the real API and the demo club.
/// </summary>
public sealed class OwnDataApiTests(WebApplicationFactory<Program> factory) : IClassFixture<WebApplicationFactory<Program>>, IDisposable
{
    private const string Group = "demo-dojo";
    private const string Password = "a good password";

    /// <summary>The demo club's events are generated around "today", so the clock is pinned: 13 October 2026, 12:00 in Germany.</summary>
    private static readonly DateTimeOffset PinnedNow = new(2026, 10, 13, 10, 0, 0, TimeSpan.Zero);

    private readonly InMemoryDatabase _database = new();
    private readonly InMemoryDatabase _accounts = new();
    private readonly FakeTimeProvider _time = new(PinnedNow);

    public void Dispose()
    {
        _database.Dispose();
        _accounts.Dispose();
    }

    private WebApplicationFactory<Program> CreateHost() =>
        factory.WithWebHostBuilder(builder =>
        {
            builder.UseSetting("Spond:UseDemoData", "true");
            builder.UseSetting("Sync:Enabled", "false");
            builder.UseSetting("DefaultData:Enabled", "false");
            builder.UseSetting("Database:ConnectionString", _database.ConnectionString);
            Configure(builder, _accounts);
            builder.ConfigureTestServices(services => services.AddSingleton<TimeProvider>(_time));
        });

    private static async Task<int> AddMemberAsync(WebApplicationFactory<Program> host, string name)
    {
        using var scope = host.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<PlannerDbContext>();
        var member = new Member { GroupId = Group, SpondId = $"spond-{Guid.NewGuid():N}", Name = name, CreatedAt = PinnedNow };
        db.Members.Add(member);
        await db.SaveChangesAsync();
        return member.Id;
    }

    /// <summary>Puts the first invited members of an event into the database as the sync would.</summary>
    private static async Task<IReadOnlyList<int>> SyncInvitedAsync(WebApplicationFactory<Program> host, CalendarEventDto training, int count)
    {
        using var scope = host.Services.CreateScope();
        var statistics = scope.ServiceProvider.GetRequiredService<StatisticsService>();
        var group = await statistics.GetGroupInfoAsync(Group, default);
        var info = await statistics.FindEventAsync(Group, training.Id, default);

        var db = scope.ServiceProvider.GetRequiredService<PlannerDbContext>();
        var members = group.Members.Where(m => info.Responses.ContainsKey(m.Id)).Take(count)
            .Select(person => new Member { GroupId = Group, SpondId = person.Id, Name = person.Name, CreatedAt = PinnedNow }).ToList();
        db.Members.AddRange(members);
        await db.SaveChangesAsync();
        return [.. members.Select(m => m.Id)];
    }

    private static async Task<CalendarEventDto> AnEventAsync(PlannerApiClient api)
    {
        var week = await api.GetCalendarAsync(Group, CalendarViews.Week, new DateOnly(2026, 10, 14));
        return week.Events.First(e => !e.Cancelled && e.Invited > 1 && e.Start > PinnedNow);
    }

    private static PlannerApiClient Admin(WebApplicationFactory<Program> host) => new(host.CreateSignedInClient());

    /// <summary>An administrator creates a user with exactly these permissions; the user is returned with the user's client.</summary>
    private static async Task<(UserDto User, HttpClient Client, PlannerApiClient Api)> UserAsync(WebApplicationFactory<Program> host, string name, params string[] permissions)
    {
        var user = await Admin(host).CreateUserAsync(new UserInputDto(name, Password, Permissions: permissions));
        var client = host.CreateSignedInClient(name, Password);
        return (user, client, new PlannerApiClient(client));
    }

    private static async Task<HttpStatusCode> StatusAsync(HttpClient client, string url) => (await client.GetAsync(url)).StatusCode;

    // --- Linking ------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task An_administrator_says_which_members_a_user_is_and_the_user_and_the_lists_know()
    {
        using var host = CreateHost();
        var admin = Admin(host);
        var first = await AddMemberAsync(host, "Anna Schmidt");
        var second = await AddMemberAsync(host, "Anna Schmidt (Kids)");
        var (user, _, api) = await UserAsync(host, "anna");
        Assert.Empty(user.MemberIds ?? []);

        var linked = await admin.SetUserMembersAsync(user.Id, [second, first]);

        Assert.Equal([first, second], linked.MemberIds);
        Assert.Equal([first, second], (await api.GetAccountAsync()).MemberIds);
        Assert.Equal(["Anna Schmidt", "Anna Schmidt (Kids)"], (await api.GetOwnMembersAsync()).Select(m => m.Name));
        Assert.Equal([first, second], (await admin.GetUsersAsync()).Single(u => u.Id == user.Id).MemberIds);
        Assert.All(await admin.GetMemberChoicesAsync(), choice => Assert.Equal(user.Id, choice.UserId));

        // The members are exactly those that are named; the others are released.
        var released = await admin.SetUserMembersAsync(user.Id, [first]);
        Assert.Equal([first], released.MemberIds);
        Assert.Equal(
            new (int, int?)[] { (first, user.Id), (second, null) },
            (await admin.GetMemberChoicesAsync()).OrderBy(c => c.Id).Select(c => (c.Id, c.UserId)));
    }

    [Fact]
    public async Task A_member_is_one_users_only_and_has_to_exist()
    {
        using var host = CreateHost();
        var admin = Admin(host);
        var member = await AddMemberAsync(host, "Anna Schmidt");
        var (anna, _, _) = await UserAsync(host, "anna");
        var (ben, _, _) = await UserAsync(host, "ben");
        await admin.SetUserMembersAsync(anna.Id, [member]);

        var taken = await Assert.ThrowsAsync<PlannerApiException>(() => admin.SetUserMembersAsync(ben.Id, [member]));
        var unknown = await Assert.ThrowsAsync<PlannerApiException>(() => admin.SetUserMembersAsync(ben.Id, [member + 100]));

        Assert.Equal(HttpStatusCode.Conflict, taken.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, unknown.StatusCode);
        Assert.Equal([member], (await admin.GetUsersAsync()).Single(u => u.Id == anna.Id).MemberIds); // nothing changed
    }

    [Fact]
    public async Task Only_whoever_may_edit_users_links_them_and_only_such_users_or_member_readers_list_the_choices()
    {
        using var host = CreateHost();
        var member = await AddMemberAsync(host, "Anna Schmidt");
        var (anna, _, annaApi) = await UserAsync(host, "anna");
        var (_, _, viewer) = await UserAsync(host, "viewer", Permission.UsersView);
        var (_, _, editor) = await UserAsync(host, "editor", Permission.UsersView, Permission.UsersEdit);
        var (_, _, reader) = await UserAsync(host, "reader", Permission.MembersView);

        var refused = await Assert.ThrowsAsync<PlannerApiException>(() => viewer.SetUserMembersAsync(anna.Id, [member]));
        var noChoices = await Assert.ThrowsAsync<PlannerApiException>(() => viewer.GetMemberChoicesAsync());
        await editor.SetUserMembersAsync(anna.Id, [member]);

        Assert.Equal(HttpStatusCode.Forbidden, refused.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, noChoices.StatusCode);
        Assert.Single(await editor.GetMemberChoicesAsync());
        Assert.Single(await reader.GetMemberChoicesAsync());
        Assert.Single(await annaApi.GetOwnMembersAsync()); // everybody may ask who they are themselves
    }

    [Fact]
    public async Task A_deleted_user_is_no_longer_a_member()
    {
        using var host = CreateHost();
        var admin = Admin(host);
        var member = await AddMemberAsync(host, "Anna Schmidt");
        var (anna, _, _) = await UserAsync(host, "anna");
        await admin.SetUserMembersAsync(anna.Id, [member]);

        await admin.DeleteUserAsync(anna.Id);

        Assert.Null(Assert.Single(await admin.GetMemberChoicesAsync()).UserId);
    }

    // --- What is written about oneself ----------------------------------------------------------------------------------

    [Theory]
    [InlineData(Permission.CommentsView, false, true)]
    [InlineData(Permission.CommentsViewOwn, true, false)]
    [InlineData(Permission.CommentsView + "," + Permission.CommentsViewOwn, true, true)]
    [InlineData("", false, false)]
    public async Task Comments_about_oneself_need_the_own_permission_and_those_about_others_the_general_one(string permissions, bool readsOwn, bool readsOthers)
    {
        using var host = CreateHost();
        var own = await AddMemberAsync(host, "Anna Schmidt");
        var other = await AddMemberAsync(host, "Ben Weber");
        var admin = Admin(host);
        await admin.CreateCommentAsync(own, new CommentInputDto("About Anna", []));
        await admin.CreateCommentAsync(other, new CommentInputDto("About Ben", []));
        var (anna, client, _) = await UserAsync(host, "anna", [.. permissions.Split(',', StringSplitOptions.RemoveEmptyEntries)]);
        await admin.SetUserMembersAsync(anna.Id, [own]);

        Assert.Equal(readsOwn ? HttpStatusCode.OK : HttpStatusCode.Forbidden, await StatusAsync(client, $"/api/members/{own}/comments"));
        Assert.Equal(readsOthers ? HttpStatusCode.OK : HttpStatusCode.Forbidden, await StatusAsync(client, $"/api/members/{other}/comments"));
    }

    [Theory]
    [InlineData(Permission.RatingsView, false, true)]
    [InlineData(Permission.RatingsViewOwn, true, false)]
    [InlineData(Permission.RatingsView + "," + Permission.RatingsViewOwn, true, true)]
    [InlineData("", false, false)]
    public async Task Evaluations_of_oneself_need_the_own_permission_and_those_of_others_the_general_one(string permissions, bool readsOwn, bool readsOthers)
    {
        using var host = CreateHost();
        var own = await AddMemberAsync(host, "Anna Schmidt");
        var other = await AddMemberAsync(host, "Ben Weber");
        var (anna, client, _) = await UserAsync(host, "anna", [.. permissions.Split(',', StringSplitOptions.RemoveEmptyEntries)]);
        await Admin(host).SetUserMembersAsync(anna.Id, [own]);

        Assert.Equal(readsOwn ? HttpStatusCode.OK : HttpStatusCode.Forbidden, await StatusAsync(client, $"/api/members/{own}/ratings"));
        Assert.Equal(readsOthers ? HttpStatusCode.OK : HttpStatusCode.Forbidden, await StatusAsync(client, $"/api/members/{other}/ratings"));
    }

    [Fact]
    public async Task The_number_of_comments_is_told_to_those_who_may_read_them_and_hidden_about_oneself_otherwise()
    {
        using var host = CreateHost();
        var admin = Admin(host);
        var own = await AddMemberAsync(host, "Anna Schmidt");
        var other = await AddMemberAsync(host, "Ben Weber");
        await admin.CreateCommentAsync(own, new CommentInputDto("About Anna", []));
        await admin.CreateCommentAsync(other, new CommentInputDto("About Ben", []));
        var (general, _, generalApi) = await UserAsync(host, "general", Permission.MembersView, Permission.CommentsView);
        var (withOwn, _, withOwnApi) = await UserAsync(host, "withown", Permission.MembersView, Permission.CommentsView, Permission.CommentsViewOwn);
        var (_, _, nobodyApi) = await UserAsync(host, "nobody", Permission.MembersView, Permission.CommentsView);
        await admin.SetUserMembersAsync(general.Id, [own]);
        await admin.SetUserMembersAsync(withOwn.Id, [other]);

        async Task<int[]> CountsAsync(PlannerApiClient api) => [.. (await api.GetMembersAsync(Group)).OrderBy(m => m.Id).Select(m => m.CommentCount)];

        Assert.Equal(new[] { 0, 1 }, await CountsAsync(generalApi)); // not told how many comments there are about themselves
        Assert.Equal(new[] { 1, 1 }, await CountsAsync(withOwnApi)); // the count about themselves is told with the own permission, the other one with the general
        Assert.Equal(new[] { 1, 1 }, await CountsAsync(nobodyApi)); // somebody who is nobody's member: everything is told
    }

    [Fact]
    public async Task A_comment_about_oneself_cannot_be_changed_to_read_it_without_the_own_permission()
    {
        using var host = CreateHost();
        var admin = Admin(host);
        var own = await AddMemberAsync(host, "Anna Schmidt");
        var other = await AddMemberAsync(host, "Ben Weber");
        var aboutAnna = await admin.CreateCommentAsync(own, new CommentInputDto("About Anna", []));
        var aboutBen = await admin.CreateCommentAsync(other, new CommentInputDto("About Ben", []));
        var (anna, _, api) = await UserAsync(host, "anna", Permission.CommentsEdit);
        var (reader, _, readerApi) = await UserAsync(host, "reader", Permission.CommentsEdit, Permission.CommentsViewOwn);
        await admin.SetUserMembersAsync(anna.Id, [own]);

        // The answer to a change carries the earlier wording: no way around the hidden list.
        var refused = await Assert.ThrowsAsync<PlannerApiException>(() => api.UpdateCommentAsync(aboutAnna.Id, new CommentUpdateDto("Changed", [])));
        var others = await api.UpdateCommentAsync(aboutBen.Id, new CommentUpdateDto("Changed", []));
        await admin.SetUserMembersAsync(anna.Id, []);
        await admin.SetUserMembersAsync(reader.Id, [own]);
        var allowed = await readerApi.UpdateCommentAsync(aboutAnna.Id, new CommentUpdateDto("Changed", []));

        Assert.Equal(HttpStatusCode.Forbidden, refused.StatusCode);
        Assert.Equal("Changed", others.Body);
        Assert.Equal("Changed", allowed.Body);
    }

    [Fact]
    public async Task An_event_shows_the_comments_and_evaluations_of_each_member_by_the_permission_that_applies_to_that_member()
    {
        using var host = CreateHost();
        var admin = Admin(host);
        var training = await AnEventAsync(admin);
        var members = await SyncInvitedAsync(host, training, 2);
        var (own, other) = (members[0], members[1]);
        _time.SetUtcNow(training.Start.AddMinutes(1)); // it can be evaluated from now on

        foreach (var member in members)
        {
            await admin.CreateEventCommentAsync(Group, training.Id, member, new CalendarCommentInputDto($"About {member}"));
            await admin.RateMemberAsync(Group, training.Id, member, 4);
        }

        async Task<(int[] Commented, int[] Rated)> SeenAsync(PlannerApiClient api)
        {
            var seen = (await api.GetCalendarEventAsync(Group, training.Id)).Attendees.Where(a => a.MemberId is not null).ToList();
            return ([.. seen.Where(a => a.Comments.Count > 0).Select(a => a.MemberId!.Value).Order()],
                    [.. seen.Where(a => a.Ratings?.Count > 0).Select(a => a.MemberId!.Value).Order()]);
        }

        var (general, _, generalApi) = await UserAsync(host, "general", Permission.CalendarView, Permission.CommentsView, Permission.RatingsView);
        var (onlyOwn, _, onlyOwnApi) = await UserAsync(host, "onlyown", Permission.CalendarView, Permission.CommentsViewOwn, Permission.RatingsViewOwn);
        var (both, _, bothApi) = await UserAsync(host, "both", Permission.CalendarView, Permission.CommentsView, Permission.CommentsViewOwn, Permission.RatingsView, Permission.RatingsViewOwn);

        // Each of them is the first member in turn (a member is one user's only).
        await admin.SetUserMembersAsync(general.Id, [own]);
        var (generalComments, generalRatings) = await SeenAsync(generalApi);
        await admin.SetUserMembersAsync(general.Id, []);
        await admin.SetUserMembersAsync(onlyOwn.Id, [own]);
        var (ownComments, ownRatings) = await SeenAsync(onlyOwnApi);
        await admin.SetUserMembersAsync(onlyOwn.Id, []);
        await admin.SetUserMembersAsync(both.Id, [own]);
        var (bothComments, bothRatings) = await SeenAsync(bothApi);

        Assert.Equal([other], generalComments); // not those about themselves, but the others
        Assert.Equal([other], generalRatings);
        Assert.Equal([own], ownComments); // only those about themselves, whatever else they may not read
        Assert.Equal([own], ownRatings);
        Assert.Equal([own, other], bothComments.Order());
        Assert.Equal([own, other], bothRatings.Order());
    }
}
