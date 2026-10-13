using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Kyuplan.Api.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class MembersBeltsAndExamParticipants : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "Belts",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    Name = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false, collation: "NOCASE"),
                    PrimaryColor = table.Column<string>(type: "TEXT", maxLength: 7, nullable: false),
                    SecondaryColor = table.Column<string>(type: "TEXT", maxLength: 7, nullable: true),
                    MinAgeYears = table.Column<int>(type: "INTEGER", nullable: false),
                    MinWaitMonths = table.Column<int>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Belts", x => x.Id);
                    table.CheckConstraint("CK_Belts_MinAgeYears", "\"MinAgeYears\" >= 0");
                    table.CheckConstraint("CK_Belts_MinWaitMonths", "\"MinWaitMonths\" >= 0");
                    table.CheckConstraint("CK_Belts_PrimaryColor", "\"PrimaryColor\" GLOB '#[0-9A-Fa-f][0-9A-Fa-f][0-9A-Fa-f][0-9A-Fa-f][0-9A-Fa-f][0-9A-Fa-f]'");
                    table.CheckConstraint("CK_Belts_SecondaryColor", "\"SecondaryColor\" IS NULL OR \"SecondaryColor\" GLOB '#[0-9A-Fa-f][0-9A-Fa-f][0-9A-Fa-f][0-9A-Fa-f][0-9A-Fa-f][0-9A-Fa-f]'");
                });

            migrationBuilder.CreateTable(
                name: "BeltPrerequisites",
                columns: table => new
                {
                    BeltId = table.Column<int>(type: "INTEGER", nullable: false),
                    RequiredBeltId = table.Column<int>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_BeltPrerequisites", x => new { x.BeltId, x.RequiredBeltId });
                    table.CheckConstraint("CK_BeltPrerequisites_NotItself", "\"BeltId\" <> \"RequiredBeltId\"");
                    table.ForeignKey(
                        name: "FK_BeltPrerequisites_Belts_BeltId",
                        column: x => x.BeltId,
                        principalTable: "Belts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_BeltPrerequisites_Belts_RequiredBeltId",
                        column: x => x.RequiredBeltId,
                        principalTable: "Belts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "Members",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    GroupId = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                    SpondId = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                    Name = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                    BeltId = table.Column<int>(type: "INTEGER", nullable: true),
                    StartedAt = table.Column<DateOnly>(type: "TEXT", nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Members", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Members_Belts_BeltId",
                        column: x => x.BeltId,
                        principalTable: "Belts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "ExamParticipants",
                columns: table => new
                {
                    ExamId = table.Column<int>(type: "INTEGER", nullable: false),
                    MemberId = table.Column<int>(type: "INTEGER", nullable: false),
                    Rsvp = table.Column<string>(type: "TEXT", maxLength: 20, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ExamParticipants", x => new { x.ExamId, x.MemberId });
                    table.ForeignKey(
                        name: "FK_ExamParticipants_Exams_ExamId",
                        column: x => x.ExamId,
                        principalTable: "Exams",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_ExamParticipants_Members_MemberId",
                        column: x => x.MemberId,
                        principalTable: "Members",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_BeltPrerequisites_RequiredBeltId",
                table: "BeltPrerequisites",
                column: "RequiredBeltId");

            migrationBuilder.CreateIndex(
                name: "IX_Belts_Name",
                table: "Belts",
                column: "Name",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ExamParticipants_MemberId",
                table: "ExamParticipants",
                column: "MemberId");

            migrationBuilder.CreateIndex(
                name: "IX_Members_BeltId",
                table: "Members",
                column: "BeltId");

            migrationBuilder.CreateIndex(
                name: "IX_Members_GroupId_SpondId",
                table: "Members",
                columns: new[] { "GroupId", "SpondId" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "BeltPrerequisites");

            migrationBuilder.DropTable(
                name: "ExamParticipants");

            migrationBuilder.DropTable(
                name: "Members");

            migrationBuilder.DropTable(
                name: "Belts");
        }
    }
}
