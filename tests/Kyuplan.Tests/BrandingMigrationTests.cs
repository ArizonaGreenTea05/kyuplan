using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Kyuplan.Api.Persistence;

namespace Kyuplan.Tests;

/// <summary>An instance whose administrators uploaded images before the web client's logos were folded into the logos.</summary>
public sealed class BrandingMigrationTests : IDisposable
{
    private const string Before = "20261014144247_CustomBranding";

    private readonly InMemoryDatabase _database = new();

    public void Dispose() => _database.Dispose();

    private void Insert(PlannerDbContext db, string slot, string? content) => db.Database.ExecuteSql(
        $"""INSERT INTO "CustomBrandingImages" ("Slot", "ContentType", "Content", "UpdatedAt") VALUES ({slot}, {(content is null ? null : "image/png")}, {(content is null ? null : System.Text.Encoding.ASCII.GetBytes(content))}, '2026-10-14 10:00:00+00:00')""");

    [Fact]
    public void An_uploaded_web_logo_becomes_the_logo_and_the_web_slots_are_gone()
    {
        using var db = new PlannerDbContext(new DbContextOptionsBuilder<PlannerDbContext>().UseSqlite(_database.ConnectionString).Options);
        var migrator = db.GetService<IMigrator>();
        migrator.Migrate(Before);
        Insert(db, "logo", "bitmap-light");
        Insert(db, "web-logo", "svg-light");        // the browser showed this one, so it replaces the logo
        Insert(db, "logo-on-dark", "bitmap-dark");
        Insert(db, "web-logo-on-dark", null);       // "none": the browser showed the plain dark logo, which stays
        Insert(db, "icon", "tab");

        migrator.Migrate();

        var rows = db.CustomBrandingImages.AsNoTracking().OrderBy(i => i.Slot).ToList();
        Assert.Equal(["icon", "logo", "logo-on-dark"], rows.Select(r => r.Slot));
        Assert.Equal(["tab", "svg-light", "bitmap-dark"], rows.Select(r => System.Text.Encoding.ASCII.GetString(r.Content!)));
    }

    [Fact]
    public void A_web_logo_without_a_logo_next_to_it_is_just_renamed_and_nothing_else_changes()
    {
        using var db = new PlannerDbContext(new DbContextOptionsBuilder<PlannerDbContext>().UseSqlite(_database.ConnectionString).Options);
        var migrator = db.GetService<IMigrator>();
        migrator.Migrate(Before);
        Insert(db, "web-logo-on-dark", "svg-dark");
        Insert(db, "web-logo", null);               // removed on purpose, nothing to hand over

        migrator.Migrate();

        var rows = db.CustomBrandingImages.AsNoTracking().ToList();
        var row = Assert.Single(rows);
        Assert.Equal("logo-on-dark", row.Slot);
        Assert.Equal("svg-dark", System.Text.Encoding.ASCII.GetString(row.Content!));
    }

    [Fact]
    public void A_database_without_any_image_is_left_alone()
    {
        using var db = new PlannerDbContext(new DbContextOptionsBuilder<PlannerDbContext>().UseSqlite(_database.ConnectionString).Options);
        db.Database.Migrate();

        Assert.Empty(db.CustomBrandingImages.AsNoTracking().ToList());
    }
}
