using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;
using Kyuplan.Api.Persistence;
using Kyuplan.Client.Api;
using Kyuplan.Contracts;
using static Kyuplan.Tests.TestAccounts;

namespace Kyuplan.Tests;

/// <summary>
/// Comments on members (with replies, tags and the history of their edits) and the tags, against the real API and a real (in-memory) SQLite
/// database. The <see cref="PlannerApiClient"/> calls double as a wire-format check, like in <see cref="BeltApiTests"/>.
/// </summary>
public sealed class CommentApiTests(WebApplicationFactory<Program> factory) : IClassFixture<WebApplicationFactory<Program>>, IDisposable
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
            builder.UseSetting("Sync:Enabled", "false"); // a background write would collide with the test's own reads of the shared in-memory database
            builder.UseSetting("DefaultData:Enabled", "false"); // the tests fill their own tables; DefaultDataTests starts from the defaults
            builder.UseSetting("Database:ConnectionString", _database.ConnectionString);
            TestAccounts.Configure(builder, _accounts);
            builder.ConfigureTestServices(services => services.AddSingleton<TimeProvider>(_time));
        });

    /// <summary>Puts a member into the database as the sync with Spond would.</summary>
    private static async Task<int> AddMemberAsync(WebApplicationFactory<Program> host, string name, string groupId = Group)
    {
        using var scope = host.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<PlannerDbContext>();
        var member = new Member { GroupId = groupId, SpondId = $"spond-{Guid.NewGuid():N}", Name = name, CreatedAt = PinnedNow };
        db.Members.Add(member);
        await db.SaveChangesAsync();
        return member.Id;
    }

    /// <summary>An administrator creates a user; the client of that user is returned with the user.</summary>
    private static async Task<(PlannerApiClient Api, UserDto User, HttpClient Client)> SignInAsUserAsync(
        WebApplicationFactory<Program> host, string userName, bool isAdmin = false)
    {
        var created = await new PlannerApiClient(host.CreateSignedInClient()).CreateUserAsync(Input(userName, Password, isAdmin));
        var client = host.CreateSignedInClient(userName, Password);
        return (new PlannerApiClient(client), created, client);
    }

    private static async Task<EventChoiceDto> ExamAsync(PlannerApiClient api, int index = 0) =>
        (await api.GetEventChoicesAsync(Group)).Events.Where(e => e.Name == "Gürtelprüfung").OrderBy(e => e.Start).ElementAt(index);

    private static async Task<EventChoiceDto> TrainingAsync(PlannerApiClient api) =>
        (await api.GetEventChoicesAsync(Group)).Events.First(e => e.Name != "Gürtelprüfung");

    // --- Tags -------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task There_are_no_tags_to_begin_with()
    {
        using var host = CreateHost();

        Assert.Empty(await new PlannerApiClient(host.CreateSignedInClient()).GetTagsAsync());
    }

    [Fact]
    public async Task A_tag_is_created_trimmed_found_by_its_location_and_listed_by_name()
    {
        using var host = CreateHost();
        var client = host.CreateSignedInClient();

        var response = await client.PostAsJsonAsync("/api/tags", new TagInputDto("  Spring  "));
        await client.PostAsJsonAsync("/api/tags", new TagInputDto("autumn"));

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var spring = (await response.Content.ReadFromJsonAsync<TagDto>())!;
        Assert.Equal(new TagDto(spring.Id, "Spring", null, 0), spring);
        Assert.EndsWith($"/api/tags/{spring.Id}", response.Headers.Location!.ToString());
        Assert.Equal(spring, await client.GetFromJsonAsync<TagDto>(response.Headers.Location));
        Assert.Equal(["autumn", "Spring"], (await new PlannerApiClient(client).GetTagsAsync()).Select(t => t.Name));
    }

    [Fact]
    public async Task A_tag_can_be_about_an_event_and_an_event_can_have_many_tags()
    {
        using var host = CreateHost();
        var api = new PlannerApiClient(host.CreateSignedInClient());
        var exam = await ExamAsync(api);

        var first = await api.CreateTagAsync(new TagInputDto("Exam prep", Group, exam.Id));
        var second = await api.CreateTagAsync(new TagInputDto("Exam result", Group, exam.Id));
        var loose = await api.CreateTagAsync(new TagInputDto("General"));

        // The event's name and start are the event's: nobody typed them.
        Assert.Equal(new TagEventDto(Group, exam.Id, "Gürtelprüfung", exam.Start), first.Event);
        Assert.Equal(first.Event, second.Event);
        Assert.Null(loose.Event);
        Assert.Equal(first.Event, (await api.GetTagsAsync()).Single(t => t.Name == "Exam prep").Event);
    }

    [Fact]
    public async Task A_tag_can_be_connected_to_another_event_and_disconnected_again()
    {
        using var host = CreateHost();
        var api = new PlannerApiClient(host.CreateSignedInClient());
        var exam = await ExamAsync(api);
        var training = await TrainingAsync(api);
        var tag = await api.CreateTagAsync(new TagInputDto("Topic", Group, exam.Id));

        var moved = await api.UpdateTagAsync(tag.Id, new TagInputDto("Topic", Group, training.Id));
        var same = await api.UpdateTagAsync(tag.Id, new TagInputDto("Topic renamed", Group, training.Id));
        var loose = await api.UpdateTagAsync(tag.Id, new TagInputDto("Topic renamed"));

        Assert.Equal(training.Id, moved.Event!.EventId);
        Assert.Equal(moved.Event, same.Event);
        Assert.Equal("Topic renamed", same.Name);
        Assert.Null(loose.Event);
        Assert.Null((await api.GetTagsAsync()).Single().Event);
    }

    [Fact]
    public async Task A_tag_whose_event_is_no_longer_where_it_was_keeps_what_was_stored()
    {
        using var host = CreateHost();
        var api = new PlannerApiClient(host.CreateSignedInClient());
        var exam = await ExamAsync(api);
        await api.CreateTagAsync(new TagInputDto("Exam prep", Group, exam.Id));

        // Years later the event has left the search window (and Spond's list): the tag still says what it was about, and can still be renamed.
        _time.Advance(TimeSpan.FromDays(1500));
        var tag = Assert.Single(await api.GetTagsAsync());
        Assert.Equal(exam.Start, tag.Event!.Start);

        var renamed = await api.UpdateTagAsync(tag.Id, new TagInputDto("Old exam", Group, exam.Id));
        Assert.Equal("Gürtelprüfung", renamed.Event!.Name);
    }

    [Fact]
    public async Task A_tag_name_is_unique_regardless_of_case()
    {
        using var host = CreateHost();
        var client = host.CreateSignedInClient();
        var api = new PlannerApiClient(client);
        await api.CreateTagAsync(new TagInputDto("Spring"));
        var other = await api.CreateTagAsync(new TagInputDto("Autumn"));

        Assert.Equal(HttpStatusCode.Conflict, (await client.PostAsJsonAsync("/api/tags", new TagInputDto("SPRING"))).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await client.PutAsJsonAsync($"/api/tags/{other.Id}", new TagInputDto(" spring "))).StatusCode);

        // A tag may be changed to its own name in another case.
        Assert.Equal("AUTUMN", (await api.UpdateTagAsync(other.Id, new TagInputDto("AUTUMN"))).Name);
    }

    [Fact]
    public async Task A_tag_needs_a_name_and_an_event_is_given_with_its_group_or_not_at_all()
    {
        using var host = CreateHost();
        var client = host.CreateSignedInClient();

        var blank = await client.PostAsJsonAsync("/api/tags", new TagInputDto("   "));
        var tooLong = await client.PostAsJsonAsync("/api/tags", new TagInputDto(new string('x', TagRules.MaxNameLength + 1)));
        var half = await client.PostAsJsonAsync("/api/tags", new TagInputDto("Half", Group));
        var otherHalf = await client.PostAsJsonAsync("/api/tags", new TagInputDto("Other half", EventId: "e-1"));

        Assert.All([blank, tooLong, half, otherHalf], r => Assert.Equal(HttpStatusCode.BadRequest, r.StatusCode));
        Assert.Contains("name", (await blank.Content.ReadAsStringAsync()).ToLowerInvariant());
        Assert.Contains("eventId", await half.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task An_event_that_does_not_exist_is_not_found()
    {
        using var host = CreateHost();
        var client = host.CreateSignedInClient();

        Assert.Equal(HttpStatusCode.NotFound, (await client.PostAsJsonAsync("/api/tags", new TagInputDto("Ghost", Group, "no-such-event"))).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.PostAsJsonAsync("/api/tags", new TagInputDto("Ghost", "no-such-group", "e-1"))).StatusCode);
        Assert.Empty(await new PlannerApiClient(client).GetTagsAsync());
    }

    [Fact]
    public async Task An_unknown_tag_is_not_found()
    {
        using var host = CreateHost();
        var client = host.CreateSignedInClient();

        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync("/api/tags/99")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.PutAsJsonAsync("/api/tags/99", new TagInputDto("Ghost"))).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.DeleteAsync("/api/tags/99")).StatusCode);
    }

    [Fact]
    public async Task Deleting_a_tag_takes_it_off_the_comments_which_stay()
    {
        using var host = CreateHost();
        var api = new PlannerApiClient(host.CreateSignedInClient());
        var memberId = await AddMemberAsync(host, "Anna");
        var keep = await api.CreateTagAsync(new TagInputDto("Keep"));
        var drop = await api.CreateTagAsync(new TagInputDto("Drop"));
        await api.CreateCommentAsync(memberId, new CommentInputDto("Both", [keep.Id, drop.Id]));
        Assert.Equal([1, 1], (await api.GetTagsAsync()).Select(t => t.CommentCount));

        await api.DeleteTagAsync(drop.Id);

        var comment = Assert.Single(await api.GetCommentsAsync(memberId));
        Assert.Equal(["Keep"], comment.Tags.Select(t => t.Name));
        Assert.Equal("Both", comment.Body);
        Assert.Empty(comment.Edits); // taking a tag off by deleting it is not anybody's edit of the comment
    }

    // --- Comments: writing and reading ---------------------------------------------------------------------------

    [Fact]
    public async Task A_comment_is_written_by_the_signed_in_user_with_its_tags_and_found_in_the_list()
    {
        using var host = CreateHost();
        var (api, anna, client) = await SignInAsUserAsync(host, "Anna");
        var memberId = await AddMemberAsync(host, "Ben");
        var exam = await ExamAsync(api);
        var tag = await api.CreateTagAsync(new TagInputDto("Exam prep", Group, exam.Id));

        var response = await client.PostAsJsonAsync($"/api/members/{memberId}/comments", new CommentInputDto("  Needs **more** practice\r\nat home \n", [tag.Id]));

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var comment = (await response.Content.ReadFromJsonAsync<CommentDto>())!;
        // Trimmed, line breaks as \n; the moment is the club's: 12:00 in Germany on the pinned day (10:00 UTC, CEST).
        Assert.Equal(
            new CommentDto(comment.Id, memberId, null, "Needs **more** practice\nat home", anna.Id, "Anna", comment.CreatedAt,
                [new CommentTagDto(tag.Id, "Exam prep", new TagEventDto(Group, exam.Id, "Gürtelprüfung", exam.Start))], []),
            comment,
            new CommentComparer());
        Assert.Equal(new DateTimeOffset(2026, 10, 13, 12, 0, 0, TimeSpan.FromHours(2)), comment.CreatedAt);
        Assert.Equal(TimeSpan.FromHours(2), comment.CreatedAt.Offset);
        Assert.EndsWith($"/api/comments/{comment.Id}", response.Headers.Location!.ToString());

        Assert.Equal(comment.Id, Assert.Single(await api.GetCommentsAsync(memberId)).Id);
    }

    [Fact]
    public async Task Comments_belong_to_their_member_and_are_counted_in_the_member_list()
    {
        using var host = CreateHost();
        var api = new PlannerApiClient(host.CreateSignedInClient());
        var anna = await AddMemberAsync(host, "Anna");
        var ben = await AddMemberAsync(host, "Ben");
        var clara = await AddMemberAsync(host, "Clara");
        var first = await api.CreateCommentAsync(anna, new CommentInputDto("One", []));
        await api.CreateCommentAsync(anna, new CommentInputDto("Two", []));
        await api.CreateCommentAsync(anna, new CommentInputDto("A reply", [], first.Id));
        await api.CreateCommentAsync(ben, new CommentInputDto("Other", []));

        Assert.Equal(["One", "Two", "A reply"], (await api.GetCommentsAsync(anna)).Select(c => c.Body));
        Assert.Equal(["Other"], (await api.GetCommentsAsync(ben)).Select(c => c.Body));
        Assert.Empty(await api.GetCommentsAsync(clara));
        Assert.Equal(
            [("Anna", 3), ("Ben", 1), ("Clara", 0)],
            (await api.GetMembersAsync(Group)).Select(m => (m.Name, m.CommentCount)));
    }

    [Fact]
    public async Task A_reply_names_the_comment_it_answers_and_that_must_be_the_same_members()
    {
        using var host = CreateHost();
        var client = host.CreateSignedInClient();
        var api = new PlannerApiClient(client);
        var anna = await AddMemberAsync(host, "Anna");
        var ben = await AddMemberAsync(host, "Ben");
        var comment = await api.CreateCommentAsync(anna, new CommentInputDto("Question", []));

        var reply = await api.CreateCommentAsync(anna, new CommentInputDto("Answer", [], comment.Id));
        var deeper = await api.CreateCommentAsync(anna, new CommentInputDto("Answer to the answer", [], reply.Id));
        var elsewhere = await client.PostAsJsonAsync($"/api/members/{ben}/comments", new CommentInputDto("Wrong place", [], comment.Id));
        var missing = await client.PostAsJsonAsync($"/api/members/{anna}/comments", new CommentInputDto("Nobody", [], 999));

        Assert.Equal([null, comment.Id, reply.Id], [comment.ParentId, reply.ParentId, deeper.ParentId]);
        Assert.Equal(HttpStatusCode.BadRequest, elsewhere.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, missing.StatusCode);
        Assert.Empty(await api.GetCommentsAsync(ben));
    }

    [Fact]
    public async Task A_comment_needs_words_a_reasonable_length_and_tags_that_exist()
    {
        using var host = CreateHost();
        var client = host.CreateSignedInClient();
        var memberId = await AddMemberAsync(host, "Anna");
        var url = $"/api/members/{memberId}/comments";

        var blank = await client.PostAsJsonAsync(url, new CommentInputDto(" \r\n ", []));
        var tooLong = await client.PostAsJsonAsync(url, new CommentInputDto(new string('x', CommentRules.MaxBodyLength + 1), []));
        var noTags = await client.PostAsJsonAsync(url, new { body = "Words" }); // tagIds is required, it may be empty
        var unknownTag = await client.PostAsJsonAsync(url, new CommentInputDto("Words", [42]));
        var tooManyTags = await client.PostAsJsonAsync(url, new CommentInputDto("Words", [.. Enumerable.Range(1, CommentRules.MaxTagsPerComment + 1)]));
        var unknownMember = await client.PostAsJsonAsync("/api/members/999/comments", new CommentInputDto("Words", []));

        Assert.Equal(
            [HttpStatusCode.BadRequest, HttpStatusCode.BadRequest, HttpStatusCode.BadRequest, HttpStatusCode.BadRequest, HttpStatusCode.BadRequest, HttpStatusCode.NotFound],
            [blank.StatusCode, tooLong.StatusCode, noTags.StatusCode, unknownTag.StatusCode, tooManyTags.StatusCode, unknownMember.StatusCode]);
        Assert.Empty(await new PlannerApiClient(client).GetCommentsAsync(memberId));
    }

    [Fact]
    public async Task The_comments_of_an_unknown_member_are_not_found()
    {
        using var host = CreateHost();

        Assert.Equal(HttpStatusCode.NotFound, (await host.CreateSignedInClient().GetAsync("/api/members/999/comments")).StatusCode);
    }

    [Fact]
    public async Task Comments_go_with_the_member_when_the_sync_removes_them()
    {
        using var host = CreateHost();
        var api = new PlannerApiClient(host.CreateSignedInClient());
        var memberId = await AddMemberAsync(host, "Anna");
        var tag = await api.CreateTagAsync(new TagInputDto("Topic"));
        var comment = await api.CreateCommentAsync(memberId, new CommentInputDto("One", [tag.Id]));
        await api.CreateCommentAsync(memberId, new CommentInputDto("Reply", [], comment.Id));

        using (var scope = host.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<PlannerDbContext>();
            db.Members.Remove(await db.Members.SingleAsync(m => m.Id == memberId));
            await db.SaveChangesAsync();

            Assert.Empty(db.Comments);
            Assert.Empty(db.CommentEdits);
            Assert.Empty(db.CommentTags);
        }

        Assert.Equal("Topic", Assert.Single(await api.GetTagsAsync()).Name); // the tag is not the member's
    }

    // --- Comments: editing and its history ---------------------------------------------------------------------

    [Fact]
    public async Task Anybody_can_edit_a_comment_and_the_edit_says_who_made_it_when_and_how()
    {
        using var host = CreateHost();
        var (anna, _, _) = await SignInAsUserAsync(host, "Anna");
        var (ben, benUser, _) = await SignInAsUserAsync(host, "Ben");
        var memberId = await AddMemberAsync(host, "Clara");
        var exam = await ExamAsync(anna);
        var prep = await anna.CreateTagAsync(new TagInputDto("Prep", Group, exam.Id));
        var comment = await anna.CreateCommentAsync(memberId, new CommentInputDto("Needs practice", [prep.Id]));

        _time.Advance(TimeSpan.FromHours(3));
        var edited = await ben.UpdateCommentAsync(comment.Id, new CommentUpdateDto("Needs more practice", [prep.Id]));

        // The comment is still Anna's; Ben is the editor.
        Assert.Equal("Anna", edited.AuthorName);
        Assert.Equal("Needs more practice", edited.Body);
        var edit = Assert.Single(edited.Edits);
        Assert.Equal(("Ben", benUser.Id), (edit.EditorName, edit.EditorId));
        Assert.Equal(new DateTimeOffset(2026, 10, 13, 15, 0, 0, TimeSpan.FromHours(2)), edit.EditedAt);
        Assert.Equal((true, false), (edit.TextChanged, edit.FormattingChanged));
        Assert.Equal("Needs practice", edit.PreviousBody);
        Assert.Empty(edit.TagsAdded);
        Assert.Empty(edit.TagsRemoved);
        Assert.Equivalent(edited, Assert.Single(await anna.GetCommentsAsync(memberId)), strict: true);
    }

    [Fact]
    public async Task An_edit_that_only_changes_the_styling_or_the_tags_says_so()
    {
        using var host = CreateHost();
        var (api, _, _) = await SignInAsUserAsync(host, "Anna");
        var memberId = await AddMemberAsync(host, "Clara");
        var one = await api.CreateTagAsync(new TagInputDto("One"));
        var two = await api.CreateTagAsync(new TagInputDto("Two"));
        var three = await api.CreateTagAsync(new TagInputDto("Three"));
        var comment = await api.CreateCommentAsync(memberId, new CommentInputDto("Needs practice", [one.Id, two.Id]));

        var styled = await api.UpdateCommentAsync(comment.Id, new CommentUpdateDto("Needs **practice**", [one.Id, two.Id]));
        var retagged = await api.UpdateCommentAsync(comment.Id, new CommentUpdateDto("Needs **practice**", [two.Id, three.Id]));
        var both = await api.UpdateCommentAsync(comment.Id, new CommentUpdateDto("Needs more **practice**", [three.Id]));

        Assert.Equal(3, both.Edits.Count);
        var (first, second, third) = (both.Edits[0], both.Edits[1], both.Edits[2]);
        Assert.Equal((false, true), (first.TextChanged, first.FormattingChanged));
        Assert.Empty(first.TagsAdded);
        Assert.Empty(first.TagsRemoved);
        Assert.Equal("Needs practice", first.PreviousBody);
        Assert.Equal((false, false), (second.TextChanged, second.FormattingChanged));
        Assert.Equal(["Three"], second.TagsAdded);
        Assert.Equal(["One"], second.TagsRemoved);
        Assert.Equal((true, false), (third.TextChanged, third.FormattingChanged));
        Assert.Empty(third.TagsAdded);
        Assert.Equal(["Two"], third.TagsRemoved);
        Assert.Equal("Needs **practice**", third.PreviousBody);
        Assert.Equal(["Three"], both.Tags.Select(t => t.Name));
        Assert.Equal([styled.Edits[0].Id, retagged.Edits[1].Id, both.Edits[2].Id], both.Edits.Select(e => e.Id));
    }

    [Fact]
    public async Task A_save_that_changes_nothing_leaves_no_edit_behind()
    {
        using var host = CreateHost();
        var (api, _, _) = await SignInAsUserAsync(host, "Anna");
        var memberId = await AddMemberAsync(host, "Clara");
        var tag = await api.CreateTagAsync(new TagInputDto("Topic"));
        var comment = await api.CreateCommentAsync(memberId, new CommentInputDto("Words", [tag.Id]));

        // The same words with other line endings and blank space around them are the same words.
        var saved = await api.UpdateCommentAsync(comment.Id, new CommentUpdateDto("  Words\r\n", [tag.Id, tag.Id]));

        Assert.Equivalent(comment, saved, strict: true);
        Assert.Empty(saved.Edits);
    }

    [Fact]
    public async Task Editing_checks_the_input_like_writing_does_and_an_unknown_comment_is_not_found()
    {
        using var host = CreateHost();
        var client = host.CreateSignedInClient();
        var memberId = await AddMemberAsync(host, "Anna");
        var comment = await new PlannerApiClient(client).CreateCommentAsync(memberId, new CommentInputDto("Words", []));

        Assert.Equal(HttpStatusCode.BadRequest, (await client.PutAsJsonAsync($"/api/comments/{comment.Id}", new CommentUpdateDto(" ", []))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PutAsJsonAsync($"/api/comments/{comment.Id}", new CommentUpdateDto("Words", [7]))).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.PutAsJsonAsync("/api/comments/999", new CommentUpdateDto("Words", []))).StatusCode);
        Assert.Empty(Assert.Single(await new PlannerApiClient(client).GetCommentsAsync(memberId)).Edits);
    }

    [Fact]
    public async Task Users_are_shown_by_their_current_name_and_by_the_name_they_had_once_they_are_gone()
    {
        using var host = CreateHost();
        var admin = new PlannerApiClient(host.CreateSignedInClient());
        var (anna, annaUser, annaClient) = await SignInAsUserAsync(host, "Anna");
        var (ben, benUser, benClient) = await SignInAsUserAsync(host, "Ben");
        var memberId = await AddMemberAsync(host, "Clara");
        var comment = await anna.CreateCommentAsync(memberId, new CommentInputDto("Words", []));
        await ben.UpdateCommentAsync(comment.Id, new CommentUpdateDto("More words", []));

        // Anna gets a display name and an alias: her comment shows the display name, whatever she signs in with, and never the login names.
        await annaClient.PutAsJsonAsync("/api/account", new AccountUpdateDto("Anna", null, null, "Anna K.", ["anna.k"]));
        var renamed = Assert.Single(await ben.GetCommentsAsync(memberId));
        Assert.Equal("Anna K.", renamed.AuthorName);
        Assert.Equal("Ben", renamed.Edits.Single().EditorName);

        // Signed in by the alias, a new comment is by the same display name, and so is the name it is kept under once Anna is gone.
        var byAlias = new PlannerApiClient(host.CreateSignedInClient("anna.k", Password));
        var second = await byAlias.CreateCommentAsync(memberId, new CommentInputDto("By alias", []));
        Assert.Equal("Anna K.", second.AuthorName);
        Assert.Equal(["Anna K.", "Anna K."], (await ben.GetCommentsAsync(memberId)).Select(c => c.AuthorName));
        using (var scope = host.Services.CreateScope())
            Assert.Equal("Anna K.", await scope.ServiceProvider.GetRequiredService<PlannerDbContext>().Comments
                .Where(c => c.Id == second.Id).Select(c => c.AuthorName).SingleAsync());

        // Ben is deleted: what he did stays, under the name he had.
        await admin.DeleteUserAsync(benUser.Id);
        var gone = Assert.Single(await admin.GetCommentsAsync(memberId), c => c.Id == comment.Id);
        Assert.Equal("Ben", gone.Edits.Single().EditorName);
        Assert.Equal(annaUser.Id, gone.AuthorId);
        Assert.NotNull(benClient);
    }

    // --- Comments: deleting ----------------------------------------------------------------------------------------

    [Fact]
    public async Task A_comment_is_deleted_by_its_author_or_by_an_administrator_but_not_by_somebody_else()
    {
        using var host = CreateHost();
        var (anna, _, _) = await SignInAsUserAsync(host, "Anna");
        var (ben, _, _) = await SignInAsUserAsync(host, "Ben");
        var admin = new PlannerApiClient(host.CreateSignedInClient());
        var memberId = await AddMemberAsync(host, "Clara");
        var mine = await anna.CreateCommentAsync(memberId, new CommentInputDto("Mine", []));
        var theirs = await anna.CreateCommentAsync(memberId, new CommentInputDto("Theirs", []));

        var refused = await Assert.ThrowsAsync<PlannerApiException>(() => ben.DeleteCommentAsync(mine.Id));
        Assert.Equal(HttpStatusCode.Forbidden, refused.StatusCode);
        Assert.Contains("comments.deleteAny", refused.Message); // the reason, not just "forbidden"
        Assert.Equal(2, (await anna.GetCommentsAsync(memberId)).Count);

        await anna.DeleteCommentAsync(mine.Id);
        await admin.DeleteCommentAsync(theirs.Id);
        Assert.Empty(await anna.GetCommentsAsync(memberId));

        var unknown = await Assert.ThrowsAsync<PlannerApiException>(() => anna.DeleteCommentAsync(mine.Id));
        Assert.Equal(HttpStatusCode.NotFound, unknown.StatusCode);
    }

    [Fact]
    public async Task Deleting_a_comment_deletes_the_replies_to_it_and_nothing_else()
    {
        using var host = CreateHost();
        var api = new PlannerApiClient(host.CreateSignedInClient());
        var memberId = await AddMemberAsync(host, "Anna");
        var tag = await api.CreateTagAsync(new TagInputDto("Topic"));
        var question = await api.CreateCommentAsync(memberId, new CommentInputDto("Question", [tag.Id]));
        var answer = await api.CreateCommentAsync(memberId, new CommentInputDto("Answer", [tag.Id], question.Id));
        await api.CreateCommentAsync(memberId, new CommentInputDto("Answer to the answer", [], answer.Id));
        await api.UpdateCommentAsync(answer.Id, new CommentUpdateDto("Better answer", [tag.Id]));
        var other = await api.CreateCommentAsync(memberId, new CommentInputDto("Another thread", []));

        await api.DeleteCommentAsync(question.Id);

        Assert.Equal([other.Id], (await api.GetCommentsAsync(memberId)).Select(c => c.Id));
        using var scope = host.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<PlannerDbContext>();
        Assert.Empty(db.CommentEdits); // the history went with the comment
        Assert.Empty(db.CommentTags);
        Assert.Single(db.Tags);
    }

    // --- Who may do it -----------------------------------------------------------------------------------------------

    [Fact]
    public async Task Everything_about_comments_and_tags_needs_a_login()
    {
        using var host = CreateHost();
        var anonymous = host.CreateClient();

        var responses = new[]
        {
            await anonymous.GetAsync("/api/tags"),
            await anonymous.PostAsJsonAsync("/api/tags", new TagInputDto("Topic")),
            await anonymous.GetAsync("/api/members/1/comments"),
            await anonymous.PostAsJsonAsync("/api/members/1/comments", new CommentInputDto("Words", [])),
            await anonymous.PutAsJsonAsync("/api/comments/1", new CommentUpdateDto("Words", [])),
            await anonymous.DeleteAsync("/api/comments/1"),
        };

        Assert.All(responses, r => Assert.Equal(HttpStatusCode.Unauthorized, r.StatusCode));
    }

    /// <summary>Compares two comments by their content; the moment is checked on its own.</summary>
    private sealed class CommentComparer : IEqualityComparer<CommentDto>
    {
        public bool Equals(CommentDto? x, CommentDto? y) =>
            x is not null && y is not null
            && (x.Id, x.MemberId, x.ParentId, x.Body, x.AuthorId, x.AuthorName) == (y.Id, y.MemberId, y.ParentId, y.Body, y.AuthorId, y.AuthorName)
            && x.Tags.SequenceEqual(y.Tags) && x.Edits.SequenceEqual(y.Edits);

        public int GetHashCode(CommentDto obj) => obj.Id;
    }
}
