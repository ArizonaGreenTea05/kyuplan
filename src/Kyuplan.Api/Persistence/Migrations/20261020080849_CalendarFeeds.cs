using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Kyuplan.Api.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class CalendarFeeds : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "CalendarFeeds",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    Name = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false, collation: "NOCASE"),
                    Token = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    SourcesJson = table.Column<string>(type: "TEXT", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CalendarFeeds", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_CalendarFeeds_Name",
                table: "CalendarFeeds",
                column: "Name",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_CalendarFeeds_Token",
                table: "CalendarFeeds",
                column: "Token",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "CalendarFeeds");
        }
    }
}
