using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Kyuplan.Api.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class LogosAreOneImage : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // The web client had logos of its own ('web-logo', 'web-logo-on-dark'); there is one logo pair for every client now. What
            // the browser showed is what stays: an uploaded web logo replaces the logo of the same kind. A web logo that was removed
            // ("none") has nothing to hand over, and the browser showed the plain logo then anyway.
            foreach (var (web, logo) in new[] { ("web-logo", "logo"), ("web-logo-on-dark", "logo-on-dark") })
            {
                migrationBuilder.Sql($"""
                    DELETE FROM "CustomBrandingImages" WHERE "Slot" = '{logo}'
                      AND EXISTS (SELECT 1 FROM "CustomBrandingImages" WHERE "Slot" = '{web}' AND "Content" IS NOT NULL);
                    """);
                migrationBuilder.Sql($"""UPDATE "CustomBrandingImages" SET "Slot" = '{logo}' WHERE "Slot" = '{web}' AND "Content" IS NOT NULL;""");
                migrationBuilder.Sql($"""DELETE FROM "CustomBrandingImages" WHERE "Slot" = '{web}';""");
            }
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // The web logos were folded into the logos; there is no telling which of them were one.
        }
    }
}
