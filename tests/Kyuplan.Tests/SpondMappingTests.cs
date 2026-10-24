using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Newtonsoft.Json;
using Kyuplan.Api.Configuration;
using Kyuplan.Api.Domain;
using Kyuplan.Api.Infrastructure;
using Kyuplan.Api.Spond;
using Spond.API.Models;

namespace Kyuplan.Tests;

/// <summary>
/// The mapping from Spond.API's models to the domain model. The live Spond service is not reachable
/// from tests, so these pin down how raw Spond data is interpreted.
/// </summary>
public class SpondMappingTests
{
    private static readonly TimeZoneInfo Berlin = TimeZoneInfo.FindSystemTimeZoneById("Europe/Berlin");

    private static SpondApiDataSource CreateSource() => new(
        Options.Create(new SpondOptions()),
        new ClubClock(TimeProvider.System, Berlin),
        new MemoryCache(new MemoryCacheOptions()),
        TimeProvider.System,
        NullLogger<SpondApiDataSource>.Instance,
        NullLoggerFactory.Instance);

    [Fact]
    public void Event_times_come_from_the_iso_timestamp_and_are_expressed_in_club_time()
    {
        using var source = CreateSource();

        var mapped = source.MapEvent(new SpondEvent
        {
            Id = "e1",
            Heading = "Training Erwachsene",
            StartTimestamp = "2026-03-10T17:30:00Z",
            EndTimestamp = "2026-03-10T19:00:00Z",
            Responses = new SpondEventResponses(),
        });

        Assert.Equal(new DateTimeOffset(2026, 3, 10, 17, 30, 0, TimeSpan.Zero), mapped.Start);
        Assert.Equal(TimeSpan.FromHours(1), mapped.Start.Offset); // CET in March
        Assert.Equal(18, mapped.Start.Hour);
        Assert.Equal("Training Erwachsene", mapped.Name);
    }

    [Fact]
    public void Summer_time_events_get_the_summer_offset()
    {
        using var source = CreateSource();

        var mapped = source.MapEvent(new SpondEvent
        {
            Id = "e1",
            Heading = "Training",
            StartTimestamp = "2026-07-10T17:30:00Z",
            EndTimestamp = "2026-07-10T19:00:00Z",
            Responses = new SpondEventResponses(),
        });

        Assert.Equal(TimeSpan.FromHours(2), mapped.Start.Offset);
        Assert.Equal(19, mapped.Start.Hour);
    }

    [Fact]
    public void Every_answer_type_is_mapped_and_waiting_list_counts_as_other()
    {
        using var source = CreateSource();

        var mapped = source.MapEvent(new SpondEvent
        {
            Id = "e1",
            Heading = "Training",
            StartTimestamp = "2026-03-10T17:30:00Z",
            EndTimestamp = "2026-03-10T19:00:00Z",
            Responses = new SpondEventResponses
            {
                AcceptedIds = ["a"],
                DeclinedIds = ["d"],
                UnansweredIds = ["u"],
                WaitinglistIds = ["w"],
                UnconfirmedIds = ["c"],
            },
        });

        Assert.Equal(Rsvp.Accepted, mapped.Responses["a"]);
        Assert.Equal(Rsvp.Declined, mapped.Responses["d"]);
        Assert.Equal(Rsvp.Unanswered, mapped.Responses["u"]);
        Assert.Equal(Rsvp.Other, mapped.Responses["w"]);
        Assert.Equal(Rsvp.Other, mapped.Responses["c"]);
        Assert.Equal(5, mapped.Responses.Count);
    }

    [Fact]
    public void A_member_listed_twice_keeps_the_most_definite_answer()
    {
        using var source = CreateSource();

        var mapped = source.MapEvent(new SpondEvent
        {
            Id = "e1",
            Heading = "Training",
            StartTimestamp = "2026-03-10T17:30:00Z",
            EndTimestamp = "2026-03-10T19:00:00Z",
            Responses = new SpondEventResponses { AcceptedIds = ["a"], UnansweredIds = ["a"] },
        });

        Assert.Equal(Rsvp.Accepted, mapped.Responses["a"]);
    }

    [Fact]
    public void Missing_response_lists_and_cancelled_flag_are_handled()
    {
        using var source = CreateSource();

        // Spond.API leaves Responses null when the event JSON has none.
        var mapped = source.MapEvent(new SpondEvent
        {
            Id = "e1",
            StartTimestamp = "2026-03-10T17:30:00Z",
            EndTimestamp = "2026-03-10T19:00:00Z",
            Cancelled = true,
        });

        Assert.True(mapped.Cancelled);
        Assert.Empty(mapped.Responses);
        Assert.Equal("", mapped.Name);
    }

    [Fact]
    public void An_end_before_the_start_is_clamped_to_the_start()
    {
        using var source = CreateSource();

        var mapped = source.MapEvent(new SpondEvent
        {
            Id = "e1",
            StartTimestamp = "2026-03-10T17:30:00Z",
            EndTimestamp = "2026-03-10T16:00:00Z",
        });

        Assert.Equal(mapped.Start, mapped.End);
    }

    [Fact]
    public void Group_mapping_builds_member_names_and_subgroup_links()
    {
        var mapped = SpondApiDataSource.MapGroup(new SpondGroup
        {
            Id = "g1",
            Name = "Dojo",
            SubGroups = [new SpondSubGroup { Id = "s1", Name = "Kinder" }],
            Members = [new SpondMember { Id = "m1", FirstName = "Emma", LastName = "Becker", SubGroups = ["s1"] }],
        });

        Assert.Equal("Dojo", mapped.Name);
        Assert.Equal(new SubGroupInfo("s1", "Kinder"), Assert.Single(mapped.SubGroups));
        var member = Assert.Single(mapped.Members);
        Assert.Equal("Emma Becker", member.Name);
        Assert.Equal(["s1"], member.SubGroupIds);
        Assert.Null(member.BirthDate);
    }

    [Fact]
    public void Every_member_of_the_group_is_kept_whether_they_answer_events_or_administer_the_group()
    {
        var group = new SpondGroup
        {
            Id = "g1",
            Members =
            [
                new SpondMember { Id = "trainer", FirstName = "Tina", LastName = "Trainer", Respondent = false },
                new SpondMember { Id = "m1", FirstName = "Emma", LastName = "Becker", Respondent = true },
            ],
        };

        Assert.Equal(["trainer", "m1"], SpondApiDataSource.MapGroup(group).Members.Select(m => m.Id));
    }

    [Fact]
    public void The_owners_of_an_event_are_its_trainers()
    {
        using var source = CreateSource();

        var mapped = source.MapEvent(JsonConvert.DeserializeObject<SpondEvent>("""
            {
              "id": "e1", "heading": "Training", "startTimestamp": "2026-03-10T17:30:00Z", "endTimestamp": "2026-03-10T19:00:00Z",
              "owners": [{ "id": "t1", "response": "accepted" }, { "id": "t2" }, { "id": "t1" }, { "id": "" }],
              "responses": { "acceptedIds": ["t1", "m1"], "declinedIds": ["m2"] }
            }
            """)!);

        Assert.Equal(["t1", "t2"], mapped.Trainers);
        Assert.True(mapped.IsTrainer("t1"));
        Assert.False(mapped.IsTrainer("m1"));
        Assert.Equal(["m1", "m2"], mapped.Participants.Select(p => p.Key).Order());
        Assert.Equal(3, mapped.Responses.Count); // what Spond sent stays; the statistics read Participants
    }

    [Fact]
    public void Owners_named_by_their_profile_become_the_members_they_are()
    {
        using var source = CreateSource();
        var groups = JsonConvert.DeserializeObject<List<SpondGroup>>("""
            [{ "id": "g1", "members": [
                { "id": "m-tina", "profile": { "id": "p-tina" } },
                { "id": "m-ben", "profile": { "id": "p-ben" } },
                { "id": "m-kid" }] }]
            """)!;
        var members = SpondApiDataSource.MemberIdsByProfile(groups);

        var mapped = source.MapEvent(JsonConvert.DeserializeObject<SpondEvent>("""
            {
              "id": "e1", "startTimestamp": "2026-03-10T17:30:00Z", "endTimestamp": "2026-03-10T19:00:00Z",
              "owners": [{ "id": "p-tina" }, { "id": "m-ben" }, { "id": "p-ben" }, { "id": "p-stranger" }],
              "responses": { "acceptedIds": ["m-tina", "m-kid"] }
            }
            """)!, members);

        Assert.Equal(["m-tina", "m-ben"], mapped.Trainers); // the profile and the member id both find the member; a stranger is no trainer
        Assert.Equal(["m-kid"], mapped.Participants.Select(p => p.Key));
    }

    [Fact]
    public void Members_are_found_by_member_id_and_by_profile_id_across_the_groups()
    {
        var members = SpondApiDataSource.MemberIdsByProfile(
        [
            new SpondGroup { Id = "g1", Members = [new SpondMember { Id = "a", Profile = new SpondUserProfile { Id = "pa" } }, new SpondMember { Id = "b" }] },
            new SpondGroup { Id = "g2", Members = [new SpondMember { Id = "a", Profile = new SpondUserProfile { Id = "pa" } }] },
        ]);

        Assert.Equal(["a", "a", "b"], new[] { members["a"], members["pa"], members["b"] });
        Assert.Equal(3, members.Count);
    }

    [Fact]
    public void An_event_without_owners_has_no_trainers()
    {
        using var source = CreateSource();

        var mapped = source.MapEvent(new SpondEvent
        {
            Id = "e1",
            StartTimestamp = "2026-03-10T17:30:00Z",
            EndTimestamp = "2026-03-10T19:00:00Z",
            Responses = new SpondEventResponses { AcceptedIds = ["a"] },
        });

        Assert.Empty(mapped.Trainers!);
        Assert.Equal(["a"], mapped.Participants.Select(p => p.Key));
    }

    [Fact]
    public void The_option_to_ignore_administrators_is_gone()
    {
        Assert.Null(typeof(SpondOptions).GetProperty("IgnoreAdministrators"));
    }

    [Theory]
    [InlineData("2012-05-03T00:00:00Z", "2012-05-03")]
    [InlineData("2012-05-03T23:30:00-05:00", "2012-05-03")] // the day as written, not moved by a time zone
    [InlineData("2012-05-03", "2012-05-03")]
    [InlineData("", null)]
    [InlineData(null, null)]
    [InlineData("May 3rd", null)]
    [InlineData("2012-13-45", null)]
    public void A_members_date_of_birth_is_the_day_spond_wrote(string? text, string? expected)
    {
        var mapped = SpondApiDataSource.MapGroup(new SpondGroup
        {
            Id = "g1",
            Members = [new SpondMember { Id = "m1", FirstName = "Emma", LastName = "Becker", DateOfBirth = text }],
        });

        Assert.Equal(expected is null ? null : DateOnly.Parse(expected), Assert.Single(mapped.Members).BirthDate);
    }
}
