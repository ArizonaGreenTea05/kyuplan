using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Kyuplan.Api.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class CalendarFeedTokenHash : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.RenameColumn(
                name: "Token",
                table: "CalendarFeeds",
                newName: "TokenHash");

            migrationBuilder.RenameIndex(
                name: "IX_CalendarFeeds_Token",
                table: "CalendarFeeds",
                newName: "IX_CalendarFeeds_TokenHash");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.RenameColumn(
                name: "TokenHash",
                table: "CalendarFeeds",
                newName: "Token");

            migrationBuilder.RenameIndex(
                name: "IX_CalendarFeeds_TokenHash",
                table: "CalendarFeeds",
                newName: "IX_CalendarFeeds_Token");
        }
    }
}
