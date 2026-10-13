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
    public void Members_who_are_not_respondents_are_the_administrators_and_left_out_only_when_asked_for()
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
        Assert.Equal(["trainer", "m1"], SpondApiDataSource.MapGroup(group, ignoreAdministrators: false).Members.Select(m => m.Id));
        Assert.Equal(["m1"], SpondApiDataSource.MapGroup(group, ignoreAdministrators: true).Members.Select(m => m.Id));
    }

    [Fact]
    public void Respondent_is_read_from_the_member_json_of_the_group()
    {
        var group = JsonConvert.DeserializeObject<SpondGroup>("""
            { "id": "g1", "members": [{ "id": "boss", "respondent": false }, { "id": "m1", "respondent": true }] }
            """)!;

        Assert.Equal(["m1"], SpondApiDataSource.MapGroup(group, ignoreAdministrators: true).Members.Select(m => m.Id));
    }

    [Fact]
    public void Leaving_administrators_out_is_off_by_default_and_set_in_the_spond_section()
    {
        Assert.False(new SpondOptions().IgnoreAdministrators);

        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["Spond:IgnoreAdministrators"] = "true" })
            .Build();
        Assert.True(configuration.GetSection(SpondOptions.Section).Get<SpondOptions>()!.IgnoreAdministrators);
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
