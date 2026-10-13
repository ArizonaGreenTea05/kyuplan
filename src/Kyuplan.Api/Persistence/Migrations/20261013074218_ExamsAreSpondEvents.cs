using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Kyuplan.Api.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class ExamsAreSpondEvents : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Exams used to be a free name and day; now every exam is a Spond event. An old exam names no event and
            // cannot be linked to one without asking Spond, so it goes, with its filters. (Exams were introduced just
            // before this change, so only databases from the time they were being built have any.)
            migrationBuilder.Sql("DELETE FROM \"SavedFilters\";");
            migrationBuilder.Sql("DELETE FROM \"Exams\";");

            migrationBuilder.DropIndex(
                name: "IX_Exams_Date_Name",
                table: "Exams");

            migrationBuilder.AddColumn<string>(
                name: "EventId",
                table: "Exams",
                type: "TEXT",
                maxLength: 200,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "GroupId",
                table: "Exams",
                type: "TEXT",
                maxLength: 200,
                nullable: false,
                defaultValue: "");

            migrationBuilder.CreateIndex(
                name: "IX_Exams_EventId",
                table: "Exams",
                column: "EventId",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Exams_EventId",
                table: "Exams");

            migrationBuilder.DropColumn(
                name: "EventId",
                table: "Exams");

            migrationBuilder.DropColumn(
                name: "GroupId",
                table: "Exams");

            migrationBuilder.CreateIndex(
                name: "IX_Exams_Date_Name",
                table: "Exams",
                columns: new[] { "Date", "Name" },
                unique: true);
        }
    }
}
