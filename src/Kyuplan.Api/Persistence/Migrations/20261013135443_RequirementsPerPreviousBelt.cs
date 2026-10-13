using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Kyuplan.Api.Persistence.Migrations
{
    /// <summary>
    /// The minimum age and the waiting time move from the belt to each of its prerequisites: what is required to take a belt depends
    /// on the belt the member comes from (blue to brown is not blue-brown to brown).
    /// </summary>
    public partial class RequirementsPerPreviousBelt : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "MinAgeYears",
                table: "BeltPrerequisites",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "MinWaitMonths",
                table: "BeltPrerequisites",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            // What a belt demanded is now demanded when coming from each of the belts it required. A belt that requires no belt (the
            // first one) has nobody to come from, so what it demanded goes.
            migrationBuilder.Sql(
                """
                UPDATE "BeltPrerequisites" SET
                    "MinAgeYears" = (SELECT b."MinAgeYears" FROM "Belts" b WHERE b."Id" = "BeltPrerequisites"."BeltId"),
                    "MinWaitMonths" = (SELECT b."MinWaitMonths" FROM "Belts" b WHERE b."Id" = "BeltPrerequisites"."BeltId");
                """);

            migrationBuilder.DropCheckConstraint(
                name: "CK_Belts_MinAgeYears",
                table: "Belts");

            migrationBuilder.DropCheckConstraint(
                name: "CK_Belts_MinWaitMonths",
                table: "Belts");

            migrationBuilder.DropColumn(
                name: "MinAgeYears",
                table: "Belts");

            migrationBuilder.DropColumn(
                name: "MinWaitMonths",
                table: "Belts");

            migrationBuilder.AddCheckConstraint(
                name: "CK_BeltPrerequisites_MinAgeYears",
                table: "BeltPrerequisites",
                sql: "\"MinAgeYears\" >= 0");

            migrationBuilder.AddCheckConstraint(
                name: "CK_BeltPrerequisites_MinWaitMonths",
                table: "BeltPrerequisites",
                sql: "\"MinWaitMonths\" >= 0");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "MinAgeYears",
                table: "Belts",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "MinWaitMonths",
                table: "Belts",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            // A belt had a single age and waiting time: the largest of those it had per previous belt, so that nobody is let in earlier.
            migrationBuilder.Sql(
                """
                UPDATE "Belts" SET
                    "MinAgeYears" = COALESCE((SELECT MAX(p."MinAgeYears") FROM "BeltPrerequisites" p WHERE p."BeltId" = "Belts"."Id"), 0),
                    "MinWaitMonths" = COALESCE((SELECT MAX(p."MinWaitMonths") FROM "BeltPrerequisites" p WHERE p."BeltId" = "Belts"."Id"), 0);
                """);

            migrationBuilder.DropCheckConstraint(
                name: "CK_BeltPrerequisites_MinAgeYears",
                table: "BeltPrerequisites");

            migrationBuilder.DropCheckConstraint(
                name: "CK_BeltPrerequisites_MinWaitMonths",
                table: "BeltPrerequisites");

            migrationBuilder.DropColumn(
                name: "MinAgeYears",
                table: "BeltPrerequisites");

            migrationBuilder.DropColumn(
                name: "MinWaitMonths",
                table: "BeltPrerequisites");

            migrationBuilder.AddCheckConstraint(
                name: "CK_Belts_MinAgeYears",
                table: "Belts",
                sql: "\"MinAgeYears\" >= 0");

            migrationBuilder.AddCheckConstraint(
                name: "CK_Belts_MinWaitMonths",
                table: "Belts",
                sql: "\"MinWaitMonths\" >= 0");
        }
    }
}
