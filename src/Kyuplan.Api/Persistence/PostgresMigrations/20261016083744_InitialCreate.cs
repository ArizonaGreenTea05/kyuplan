using System;
using System.Collections.Generic;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace Kyuplan.Api.Persistence.PostgresMigrations
{
    /// <inheritdoc />
    public partial class InitialCreate : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AlterDatabase()
                .Annotation("Npgsql:PostgresExtension:citext", ",,");

            migrationBuilder.CreateTable(
                name: "Belts",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    Name = table.Column<string>(type: "citext", maxLength: 200, nullable: false),
                    PrimaryColor = table.Column<string>(type: "character varying(7)", maxLength: 7, nullable: false),
                    SecondaryColor = table.Column<string>(type: "character varying(7)", maxLength: 7, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Belts", x => x.Id);
                    table.CheckConstraint("CK_Belts_PrimaryColor", "\"PrimaryColor\" ~ '^#[0-9A-Fa-f]{6}$'");
                    table.CheckConstraint("CK_Belts_SecondaryColor", "\"SecondaryColor\" IS NULL OR \"SecondaryColor\" ~ '^#[0-9A-Fa-f]{6}$'");
                });

            migrationBuilder.CreateTable(
                name: "CustomBranding",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false),
                    ProductName = table.Column<string>(type: "character varying(60)", maxLength: 60, nullable: false),
                    OrganizationName = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: false),
                    AccentColor = table.Column<string>(type: "character varying(7)", maxLength: 7, nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CustomBranding", x => x.Id);
                    table.CheckConstraint("CK_CustomBranding_Single", "\"Id\" = 1");
                });

            migrationBuilder.CreateTable(
                name: "CustomBrandingImages",
                columns: table => new
                {
                    Slot = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    ContentType = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: true),
                    Content = table.Column<byte[]>(type: "bytea", nullable: true),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CustomBrandingImages", x => x.Slot);
                    table.CheckConstraint("CK_CustomBrandingImages_ContentAndType", "(\"Content\" IS NULL) = (\"ContentType\" IS NULL)");
                });

            migrationBuilder.CreateTable(
                name: "Exams",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    GroupId = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    EventId = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    Name = table.Column<string>(type: "citext", maxLength: 200, nullable: false),
                    Date = table.Column<DateOnly>(type: "date", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    SubGroupIds = table.Column<List<string>>(type: "text[]", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Exams", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Tags",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    Name = table.Column<string>(type: "citext", maxLength: 50, nullable: false),
                    EventGroupId = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    EventId = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    EventName = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    EventStart = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Tags", x => x.Id);
                    table.CheckConstraint("CK_Tags_Event", "(\"EventId\" IS NULL AND \"EventGroupId\" IS NULL AND \"EventName\" IS NULL AND \"EventStart\" IS NULL) OR (\"EventId\" IS NOT NULL AND \"EventGroupId\" IS NOT NULL AND \"EventName\" IS NOT NULL AND \"EventStart\" IS NOT NULL)");
                });

            migrationBuilder.CreateTable(
                name: "BeltPrerequisites",
                columns: table => new
                {
                    BeltId = table.Column<int>(type: "integer", nullable: false),
                    RequiredBeltId = table.Column<int>(type: "integer", nullable: false),
                    MinAgeYears = table.Column<int>(type: "integer", nullable: false),
                    MinWaitMonths = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_BeltPrerequisites", x => new { x.BeltId, x.RequiredBeltId });
                    table.CheckConstraint("CK_BeltPrerequisites_MinAgeYears", "\"MinAgeYears\" >= 0");
                    table.CheckConstraint("CK_BeltPrerequisites_MinWaitMonths", "\"MinWaitMonths\" >= 0");
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
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    GroupId = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    SpondId = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    Name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    BeltId = table.Column<int>(type: "integer", nullable: true),
                    StartedAt = table.Column<DateOnly>(type: "date", nullable: true),
                    BeltSince = table.Column<DateOnly>(type: "date", nullable: true),
                    BirthDate = table.Column<DateOnly>(type: "date", nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
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
                name: "SavedFilters",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    ExamId = table.Column<int>(type: "integer", nullable: false),
                    Name = table.Column<string>(type: "citext", maxLength: 200, nullable: false),
                    GroupId = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    From = table.Column<DateOnly>(type: "date", nullable: false),
                    To = table.Column<DateOnly>(type: "date", nullable: false),
                    FromEventId = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    ToEventId = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    SubGroupIds = table.Column<List<string>>(type: "text[]", nullable: false),
                    MinAttendancePercent = table.Column<int>(type: "integer", nullable: false),
                    MinInvitedEvents = table.Column<int>(type: "integer", nullable: false),
                    EventNameContains = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    EventNameExcludes = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SavedFilters", x => x.Id);
                    table.ForeignKey(
                        name: "FK_SavedFilters_Exams_ExamId",
                        column: x => x.ExamId,
                        principalTable: "Exams",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "Comments",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    MemberId = table.Column<int>(type: "integer", nullable: false),
                    ParentId = table.Column<int>(type: "integer", nullable: true),
                    Body = table.Column<string>(type: "character varying(4000)", maxLength: 4000, nullable: false),
                    AuthorId = table.Column<int>(type: "integer", nullable: false),
                    AuthorName = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Comments", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Comments_Comments_ParentId",
                        column: x => x.ParentId,
                        principalTable: "Comments",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_Comments_Members_MemberId",
                        column: x => x.MemberId,
                        principalTable: "Members",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "ExamCandidates",
                columns: table => new
                {
                    ExamId = table.Column<int>(type: "integer", nullable: false),
                    MemberId = table.Column<int>(type: "integer", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ExamCandidates", x => new { x.ExamId, x.MemberId });
                    table.ForeignKey(
                        name: "FK_ExamCandidates_Exams_ExamId",
                        column: x => x.ExamId,
                        principalTable: "Exams",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_ExamCandidates_Members_MemberId",
                        column: x => x.MemberId,
                        principalTable: "Members",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "ExamParticipants",
                columns: table => new
                {
                    ExamId = table.Column<int>(type: "integer", nullable: false),
                    MemberId = table.Column<int>(type: "integer", nullable: false),
                    Rsvp = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false)
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

            migrationBuilder.CreateTable(
                name: "ExamResults",
                columns: table => new
                {
                    ExamId = table.Column<int>(type: "integer", nullable: false),
                    MemberId = table.Column<int>(type: "integer", nullable: false),
                    BeltId = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ExamResults", x => new { x.ExamId, x.MemberId });
                    table.ForeignKey(
                        name: "FK_ExamResults_Belts_BeltId",
                        column: x => x.BeltId,
                        principalTable: "Belts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_ExamResults_Exams_ExamId",
                        column: x => x.ExamId,
                        principalTable: "Exams",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_ExamResults_Members_MemberId",
                        column: x => x.MemberId,
                        principalTable: "Members",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "CommentEdits",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    CommentId = table.Column<int>(type: "integer", nullable: false),
                    EditorId = table.Column<int>(type: "integer", nullable: false),
                    EditorName = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    EditedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    TextChanged = table.Column<bool>(type: "boolean", nullable: false),
                    FormattingChanged = table.Column<bool>(type: "boolean", nullable: false),
                    TagsAdded = table.Column<List<string>>(type: "text[]", nullable: false),
                    TagsRemoved = table.Column<List<string>>(type: "text[]", nullable: false),
                    PreviousBody = table.Column<string>(type: "character varying(4000)", maxLength: 4000, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CommentEdits", x => x.Id);
                    table.ForeignKey(
                        name: "FK_CommentEdits_Comments_CommentId",
                        column: x => x.CommentId,
                        principalTable: "Comments",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "CommentTags",
                columns: table => new
                {
                    CommentId = table.Column<int>(type: "integer", nullable: false),
                    TagId = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CommentTags", x => new { x.CommentId, x.TagId });
                    table.ForeignKey(
                        name: "FK_CommentTags_Comments_CommentId",
                        column: x => x.CommentId,
                        principalTable: "Comments",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_CommentTags_Tags_TagId",
                        column: x => x.TagId,
                        principalTable: "Tags",
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
                name: "IX_CommentEdits_CommentId",
                table: "CommentEdits",
                column: "CommentId");

            migrationBuilder.CreateIndex(
                name: "IX_Comments_MemberId",
                table: "Comments",
                column: "MemberId");

            migrationBuilder.CreateIndex(
                name: "IX_Comments_ParentId",
                table: "Comments",
                column: "ParentId");

            migrationBuilder.CreateIndex(
                name: "IX_CommentTags_TagId",
                table: "CommentTags",
                column: "TagId");

            migrationBuilder.CreateIndex(
                name: "IX_ExamCandidates_MemberId",
                table: "ExamCandidates",
                column: "MemberId");

            migrationBuilder.CreateIndex(
                name: "IX_ExamParticipants_MemberId",
                table: "ExamParticipants",
                column: "MemberId");

            migrationBuilder.CreateIndex(
                name: "IX_ExamResults_BeltId",
                table: "ExamResults",
                column: "BeltId");

            migrationBuilder.CreateIndex(
                name: "IX_ExamResults_MemberId",
                table: "ExamResults",
                column: "MemberId");

            migrationBuilder.CreateIndex(
                name: "IX_Exams_EventId",
                table: "Exams",
                column: "EventId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Members_BeltId",
                table: "Members",
                column: "BeltId");

            migrationBuilder.CreateIndex(
                name: "IX_Members_GroupId_SpondId",
                table: "Members",
                columns: new[] { "GroupId", "SpondId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_SavedFilters_ExamId_Name",
                table: "SavedFilters",
                columns: new[] { "ExamId", "Name" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Tags_Name",
                table: "Tags",
                column: "Name",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "BeltPrerequisites");

            migrationBuilder.DropTable(
                name: "CommentEdits");

            migrationBuilder.DropTable(
                name: "CommentTags");

            migrationBuilder.DropTable(
                name: "CustomBranding");

            migrationBuilder.DropTable(
                name: "CustomBrandingImages");

            migrationBuilder.DropTable(
                name: "ExamCandidates");

            migrationBuilder.DropTable(
                name: "ExamParticipants");

            migrationBuilder.DropTable(
                name: "ExamResults");

            migrationBuilder.DropTable(
                name: "SavedFilters");

            migrationBuilder.DropTable(
                name: "Comments");

            migrationBuilder.DropTable(
                name: "Tags");

            migrationBuilder.DropTable(
                name: "Exams");

            migrationBuilder.DropTable(
                name: "Members");

            migrationBuilder.DropTable(
                name: "Belts");
        }
    }
}
