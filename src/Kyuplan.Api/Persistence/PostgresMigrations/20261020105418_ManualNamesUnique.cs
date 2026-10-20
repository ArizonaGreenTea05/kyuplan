using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Kyuplan.Api.Persistence.PostgresMigrations
{
    /// <inheritdoc />
    public partial class ManualNamesUnique : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_ManualSubGroups_GroupId",
                table: "ManualSubGroups");

            migrationBuilder.AlterColumn<string>(
                name: "Name",
                table: "ManualSubGroups",
                type: "citext",
                maxLength: 200,
                nullable: false,
                oldClrType: typeof(string),
                oldType: "character varying(200)",
                oldMaxLength: 200);

            migrationBuilder.AlterColumn<string>(
                name: "Name",
                table: "ManualGroups",
                type: "citext",
                maxLength: 200,
                nullable: false,
                oldClrType: typeof(string),
                oldType: "character varying(200)",
                oldMaxLength: 200);

            migrationBuilder.CreateIndex(
                name: "IX_ManualSubGroups_GroupId_Name",
                table: "ManualSubGroups",
                columns: new[] { "GroupId", "Name" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ManualGroups_Name",
                table: "ManualGroups",
                column: "Name",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_ManualSubGroups_GroupId_Name",
                table: "ManualSubGroups");

            migrationBuilder.DropIndex(
                name: "IX_ManualGroups_Name",
                table: "ManualGroups");

            migrationBuilder.AlterColumn<string>(
                name: "Name",
                table: "ManualSubGroups",
                type: "character varying(200)",
                maxLength: 200,
                nullable: false,
                oldClrType: typeof(string),
                oldType: "citext",
                oldMaxLength: 200);

            migrationBuilder.AlterColumn<string>(
                name: "Name",
                table: "ManualGroups",
                type: "character varying(200)",
                maxLength: 200,
                nullable: false,
                oldClrType: typeof(string),
                oldType: "citext",
                oldMaxLength: 200);

            migrationBuilder.CreateIndex(
                name: "IX_ManualSubGroups_GroupId",
                table: "ManualSubGroups",
                column: "GroupId");
        }
    }
}
