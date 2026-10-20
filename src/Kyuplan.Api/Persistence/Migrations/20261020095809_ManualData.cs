using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Kyuplan.Api.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class ManualData : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "ManualGroups",
                columns: table => new
                {
                    Id = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                    Name = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ManualGroups", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "ManualEvents",
                columns: table => new
                {
                    Id = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                    GroupId = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                    Name = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                    Start = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    End = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    Cancelled = table.Column<bool>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ManualEvents", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ManualEvents_ManualGroups_GroupId",
                        column: x => x.GroupId,
                        principalTable: "ManualGroups",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "ManualMembers",
                columns: table => new
                {
                    Id = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                    GroupId = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                    Name = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                    BirthDate = table.Column<DateOnly>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ManualMembers", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ManualMembers_ManualGroups_GroupId",
                        column: x => x.GroupId,
                        principalTable: "ManualGroups",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "ManualSubGroups",
                columns: table => new
                {
                    Id = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                    GroupId = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                    Name = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ManualSubGroups", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ManualSubGroups_ManualGroups_GroupId",
                        column: x => x.GroupId,
                        principalTable: "ManualGroups",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "ManualInvitations",
                columns: table => new
                {
                    EventId = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                    MemberId = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                    Rsvp = table.Column<string>(type: "TEXT", maxLength: 20, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ManualInvitations", x => new { x.EventId, x.MemberId });
                    table.ForeignKey(
                        name: "FK_ManualInvitations_ManualEvents_EventId",
                        column: x => x.EventId,
                        principalTable: "ManualEvents",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_ManualInvitations_ManualMembers_MemberId",
                        column: x => x.MemberId,
                        principalTable: "ManualMembers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "ManualMemberSubGroups",
                columns: table => new
                {
                    MemberId = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                    SubGroupId = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ManualMemberSubGroups", x => new { x.MemberId, x.SubGroupId });
                    table.ForeignKey(
                        name: "FK_ManualMemberSubGroups_ManualMembers_MemberId",
                        column: x => x.MemberId,
                        principalTable: "ManualMembers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_ManualMemberSubGroups_ManualSubGroups_SubGroupId",
                        column: x => x.SubGroupId,
                        principalTable: "ManualSubGroups",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ManualEvents_GroupId",
                table: "ManualEvents",
                column: "GroupId");

            migrationBuilder.CreateIndex(
                name: "IX_ManualInvitations_MemberId",
                table: "ManualInvitations",
                column: "MemberId");

            migrationBuilder.CreateIndex(
                name: "IX_ManualMembers_GroupId",
                table: "ManualMembers",
                column: "GroupId");

            migrationBuilder.CreateIndex(
                name: "IX_ManualMemberSubGroups_SubGroupId",
                table: "ManualMemberSubGroups",
                column: "SubGroupId");

            migrationBuilder.CreateIndex(
                name: "IX_ManualSubGroups_GroupId",
                table: "ManualSubGroups",
                column: "GroupId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ManualInvitations");

            migrationBuilder.DropTable(
                name: "ManualMemberSubGroups");

            migrationBuilder.DropTable(
                name: "ManualEvents");

            migrationBuilder.DropTable(
                name: "ManualMembers");

            migrationBuilder.DropTable(
                name: "ManualSubGroups");

            migrationBuilder.DropTable(
                name: "ManualGroups");
        }
    }
}
