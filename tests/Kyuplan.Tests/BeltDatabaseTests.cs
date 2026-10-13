using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Kyuplan.Api.Persistence;

namespace Kyuplan.Tests;

/// <summary>
/// The belts, their requirements and the belt a member holds, against the real schema (the migrations, in memory): what the
/// tables allow and what they refuse, regardless of who writes to them.
/// </summary>
public sealed class BeltDatabaseTests : IDisposable
{
    private readonly InMemoryDatabase _database = new();

    public BeltDatabaseTests()
    {
        using var db = Open();
        db.Database.Migrate();
    }

    public void Dispose() => _database.Dispose();

    private PlannerDbContext Open() => new(new DbContextOptionsBuilder<PlannerDbContext>().UseSqlite(_database.ConnectionString).Options);

    /// <summary>Runs <paramref name="change"/> on a context of its own and saves, so that what comes back is what the database holds.</summary>
    private void Save(Action<PlannerDbContext> change)
    {
        using var db = Open();
        change(db);
        db.SaveChanges();
    }

    private T Read<T>(Func<PlannerDbContext, T> query)
    {
        using var db = Open();
        return query(db);
    }

    private static Belt NewBelt(string name, string color = "#FFFFFF") => new() { Name = name, PrimaryColor = color };

    private static Member NewMember(string spondId, string groupId = "g-dojo") =>
        new() { GroupId = groupId, SpondId = spondId, Name = $"Member {spondId}", CreatedAt = DateTimeOffset.UnixEpoch };

    private int AddBelt(string name)
    {
        var belt = NewBelt(name);
        Save(db => db.Belts.Add(belt));
        return belt.Id;
    }

    [Fact]
    public void A_belt_has_a_name_and_a_colour_and_the_second_colour_is_optional()
    {
        Save(db => db.Belts.AddRange(
            new Belt { Name = "White", PrimaryColor = "#FFFFFF" },
            new Belt { Name = "Yellow with orange stripe", PrimaryColor = "#FFD800", SecondaryColor = "#ff8000" }));

        var belts = Read(db => db.Belts.AsNoTracking().OrderBy(b => b.Id).ToList());

        Assert.Equal(["White", "Yellow with orange stripe"], belts.Select(b => b.Name));
        Assert.Equal(["#FFFFFF", "#FFD800"], belts.Select(b => b.PrimaryColor));
        Assert.Equal([null, "#ff8000"], belts.Select(b => b.SecondaryColor));
    }

    [Fact]
    public void A_belt_name_is_unique_whatever_its_case()
    {
        AddBelt("Orange");

        Assert.Throws<DbUpdateException>(() => Save(db => db.Belts.Add(NewBelt("ORANGE"))));
    }

    [Fact]
    public void A_belt_requires_one_of_several_belts_and_a_minimum_age_and_waiting_time_for_each_of_them()
    {
        var white = AddBelt("White");
        var yellow = AddBelt("Yellow");
        Save(db => db.Belts.Add(new Belt
        {
            Name = "Orange",
            PrimaryColor = "#FF8000",
            Prerequisites =
            [
                new BeltPrerequisite { RequiredBeltId = white, MinAgeYears = 10, MinWaitMonths = 12 },
                new BeltPrerequisite { RequiredBeltId = yellow, MinAgeYears = 8, MinWaitMonths = 3 },
            ],
        }));

        var orange = Read(db => db.Belts.AsNoTracking()
            .Include(b => b.Prerequisites).ThenInclude(p => p.RequiredBelt).Single(b => b.Name == "Orange"));

        // What is required depends on the belt a member comes from.
        Assert.Equal([("White", 10, 12), ("Yellow", 8, 3)], orange.Prerequisites.OrderBy(p => p.RequiredBelt.Name).Select(p => (p.RequiredBelt.Name, p.MinAgeYears, p.MinWaitMonths)));
        // The ones it requires have no requirements: nothing is demanded by default.
        var white0 = Read(db => db.Belts.AsNoTracking().Include(b => b.Prerequisites).Single(b => b.Id == white));
        Assert.Empty(white0.Prerequisites);
    }

    [Fact]
    public void A_previous_belt_demands_nothing_until_somebody_says_so()
    {
        var white = AddBelt("White");
        Save(db => db.Belts.Add(new Belt { Name = "Yellow", PrimaryColor = "#FFD800", Prerequisites = [new BeltPrerequisite { RequiredBeltId = white }] }));

        var prerequisite = Read(db => db.BeltPrerequisites.AsNoTracking().Single());

        Assert.Equal((0, 0), (prerequisite.MinAgeYears, prerequisite.MinWaitMonths));
    }

    [Fact]
    public void Upgrading_moves_what_a_belt_demanded_to_each_of_the_belts_it_required()
    {
        // A database of its own, as it was before the requirements went to the previous belts: they were two columns of the belt.
        using var database = new InMemoryDatabase();
        using var db = new PlannerDbContext(new DbContextOptionsBuilder<PlannerDbContext>().UseSqlite(database.ConnectionString).Options);
        var migrator = db.GetService<IMigrator>();
        migrator.Migrate("20261013125509_MembersBeltsAndExamParticipants");
        db.Database.ExecuteSqlRaw(
            """
            INSERT INTO "Belts" ("Id", "Name", "PrimaryColor", "MinAgeYears", "MinWaitMonths") VALUES
                (1, 'White', '#FFFFFF', 6, 0),
                (2, 'Yellow', '#FFD800', 8, 3),
                (3, 'Orange', '#FF8000', 10, 6);
            INSERT INTO "BeltPrerequisites" ("BeltId", "RequiredBeltId") VALUES (2, 1), (3, 1), (3, 2);
            """);

        migrator.Migrate();

        var prerequisites = db.BeltPrerequisites.AsNoTracking().OrderBy(p => p.BeltId).ThenBy(p => p.RequiredBeltId).ToList();
        // Each way into a belt demands what the belt did; White, which nobody comes to, has nobody to demand it of.
        Assert.Equal(
            [(2, 1, 8, 3), (3, 1, 10, 6), (3, 2, 10, 6)],
            prerequisites.Select(p => (p.BeltId, p.RequiredBeltId, p.MinAgeYears, p.MinWaitMonths)));
        Assert.Equal(["Orange", "White", "Yellow"], db.Belts.AsNoTracking().Select(b => b.Name).OrderBy(n => n).ToList());

        // And back: a belt gets the largest of what its previous belts demanded.
        migrator.Migrate("20261013125509_MembersBeltsAndExamParticipants");
        var belts = db.Database.SqlQueryRaw<int>("""SELECT "MinAgeYears" * 100 + "MinWaitMonths" AS "Value" FROM "Belts" ORDER BY "Id" """).ToList();
        Assert.Equal([0, 803, 1006], belts);
    }

    [Fact]
    public void A_belt_cannot_require_itself()
    {
        var white = AddBelt("White");

        Assert.Throws<DbUpdateException>(() => Save(db => db.BeltPrerequisites.Add(new BeltPrerequisite { BeltId = white, RequiredBeltId = white })));
    }

    [Theory]
    [InlineData("#000000", true)]
    [InlineData("#00a650", true)]
    [InlineData("#00A650", true)]
    [InlineData("green", false)]
    [InlineData("00A650", false)]
    [InlineData("#0A5", false)]
    [InlineData("#00A6500", false)]
    [InlineData("#00A65G", false)]
    [InlineData("", false)]
    public void Colours_are_written_as_RRGGBB(string color, bool accepted)
    {
        foreach (var asSecondary in new[] { false, true })
        {
            var belt = asSecondary
                ? new Belt { Name = $"Secondary {color}", PrimaryColor = "#FFFFFF", SecondaryColor = color }
                : NewBelt($"Primary {color}", color);

            if (accepted) Save(db => db.Belts.Add(belt));
            else Assert.Throws<DbUpdateException>(() => Save(db => db.Belts.Add(belt)));
        }
    }

    [Theory]
    [InlineData(-1, 0)]
    [InlineData(0, -1)]
    public void An_age_or_a_waiting_time_cannot_be_negative(int years, int months)
    {
        var white = AddBelt("White");

        Assert.Throws<DbUpdateException>(() => Save(db => db.Belts.Add(new Belt
        {
            Name = "Impossible",
            PrimaryColor = "#FFFFFF",
            Prerequisites = [new BeltPrerequisite { RequiredBeltId = white, MinAgeYears = years, MinWaitMonths = months }],
        })));
    }

    [Fact]
    public void A_belt_that_another_belt_requires_cannot_be_deleted()
    {
        var white = AddBelt("White");
        Save(db => db.Belts.Add(new Belt { Name = "Yellow", PrimaryColor = "#FFD800", Prerequisites = [new BeltPrerequisite { RequiredBeltId = white }] }));

        Assert.Throws<DbUpdateException>(() => Save(db => db.Belts.Remove(db.Belts.Single(b => b.Id == white))));
    }

    [Fact]
    public void A_belt_that_a_member_holds_cannot_be_deleted()
    {
        var white = AddBelt("White");
        var member = NewMember("m-1");
        member.BeltId = white;
        Save(db => db.Members.Add(member));

        Assert.Throws<DbUpdateException>(() => Save(db => db.Belts.Remove(db.Belts.Single(b => b.Id == white))));
    }

    [Fact]
    public void Deleting_a_belt_deletes_what_it_requires_but_not_the_belts_it_required()
    {
        var white = AddBelt("White");
        var yellow = new Belt { Name = "Yellow", PrimaryColor = "#FFD800", Prerequisites = [new BeltPrerequisite { RequiredBeltId = white }] };
        Save(db => db.Belts.Add(yellow));

        Save(db => db.Belts.Remove(db.Belts.Single(b => b.Id == yellow.Id)));

        Assert.Equal(["White"], Read(db => db.Belts.Select(b => b.Name).ToList()));
        Assert.Empty(Read(db => db.BeltPrerequisites.ToList()));
    }

    [Fact]
    public void A_member_has_no_belt_and_no_start_day_until_somebody_sets_them()
    {
        Save(db => db.Members.Add(NewMember("m-1")));
        var fresh = Read(db => db.Members.AsNoTracking().Single());
        Assert.Null(fresh.BeltId);
        Assert.Null(fresh.StartedAt);

        var orange = AddBelt("Orange");
        Save(db => { var m = db.Members.Single(); m.BeltId = orange; m.StartedAt = new DateOnly(2024, 9, 2); });

        var member = Read(db => db.Members.Include(m => m.Belt).AsNoTracking().Single());
        Assert.Equal(("Orange", new DateOnly(2024, 9, 2)), (member.Belt!.Name, member.StartedAt));

        Save(db => db.Members.Single().BeltId = null);
        Assert.Null(Read(db => db.Members.Single().BeltId));
    }

    [Fact]
    public void A_member_of_a_group_is_unique_by_its_Spond_id()
    {
        Save(db => db.Members.Add(NewMember("m-1")));

        Assert.Throws<DbUpdateException>(() => Save(db => db.Members.Add(NewMember("m-1"))));
        Save(db => db.Members.Add(NewMember("m-1", groupId: "g-team"))); // another group: another membership
    }
}
