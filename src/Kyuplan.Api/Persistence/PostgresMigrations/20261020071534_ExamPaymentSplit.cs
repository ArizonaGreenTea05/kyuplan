using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Kyuplan.Api.Persistence.PostgresMigrations
{
    /// <inheritdoc />
    public partial class ExamPaymentSplit : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "CK_ExamPrices_Amount",
                table: "ExamPrices");

            migrationBuilder.AddColumn<decimal>(
                name: "BeltPrice",
                table: "ExamPrices",
                type: "numeric(10,2)",
                precision: 10,
                scale: 2,
                nullable: false,
                defaultValue: 0m);

            migrationBuilder.AddColumn<decimal>(
                name: "BeltPrice",
                table: "ExamPayments",
                type: "numeric(10,2)",
                precision: 10,
                scale: 2,
                nullable: false,
                defaultValue: 0m);

            migrationBuilder.AddColumn<bool>(
                name: "OwnBelt",
                table: "ExamPayments",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<decimal>(
                name: "StampFee",
                table: "ExamPayments",
                type: "numeric(10,2)",
                precision: 10,
                scale: 2,
                nullable: false,
                defaultValue: 0m);

            migrationBuilder.AddCheckConstraint(
                name: "CK_ExamPrices_Amount",
                table: "ExamPrices",
                sql: "\"Amount\" >= 0 AND \"BeltPrice\" >= 0");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "CK_ExamPrices_Amount",
                table: "ExamPrices");

            migrationBuilder.DropColumn(
                name: "BeltPrice",
                table: "ExamPrices");

            migrationBuilder.DropColumn(
                name: "BeltPrice",
                table: "ExamPayments");

            migrationBuilder.DropColumn(
                name: "OwnBelt",
                table: "ExamPayments");

            migrationBuilder.DropColumn(
                name: "StampFee",
                table: "ExamPayments");

            migrationBuilder.AddCheckConstraint(
                name: "CK_ExamPrices_Amount",
                table: "ExamPrices",
                sql: "\"Amount\" >= 0");
        }
    }
}
