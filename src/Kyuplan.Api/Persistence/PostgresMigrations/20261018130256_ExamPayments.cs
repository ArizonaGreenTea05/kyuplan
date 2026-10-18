using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace Kyuplan.Api.Persistence.PostgresMigrations
{
    /// <inheritdoc />
    public partial class ExamPayments : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "ExamPayments",
                columns: table => new
                {
                    ExamId = table.Column<int>(type: "integer", nullable: false),
                    MemberId = table.Column<int>(type: "integer", nullable: false),
                    AmountDue = table.Column<decimal>(type: "numeric(10,2)", precision: 10, scale: 2, nullable: false),
                    AmountPaid = table.Column<decimal>(type: "numeric(10,2)", precision: 10, scale: 2, nullable: false),
                    PaidOn = table.Column<DateOnly>(type: "date", nullable: true),
                    Note = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ExamPayments", x => new { x.ExamId, x.MemberId });
                    table.CheckConstraint("CK_ExamPayments_Amounts", "\"AmountDue\" >= 0 AND \"AmountPaid\" >= 0");
                    table.ForeignKey(
                        name: "FK_ExamPayments_Exams_ExamId",
                        column: x => x.ExamId,
                        principalTable: "Exams",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_ExamPayments_Members_MemberId",
                        column: x => x.MemberId,
                        principalTable: "Members",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "ExamPrices",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    BeltId = table.Column<int>(type: "integer", nullable: true),
                    Amount = table.Column<decimal>(type: "numeric(10,2)", precision: 10, scale: 2, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ExamPrices", x => x.Id);
                    table.CheckConstraint("CK_ExamPrices_Amount", "\"Amount\" >= 0");
                    table.ForeignKey(
                        name: "FK_ExamPrices_Belts_BeltId",
                        column: x => x.BeltId,
                        principalTable: "Belts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ExamPayments_MemberId",
                table: "ExamPayments",
                column: "MemberId");

            migrationBuilder.CreateIndex(
                name: "IX_ExamPrices_BeltId",
                table: "ExamPrices",
                column: "BeltId",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ExamPayments");

            migrationBuilder.DropTable(
                name: "ExamPrices");
        }
    }
}
