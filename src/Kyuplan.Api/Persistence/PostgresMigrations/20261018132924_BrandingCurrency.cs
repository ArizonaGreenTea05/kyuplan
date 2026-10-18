using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Kyuplan.Api.Persistence.PostgresMigrations
{
    /// <inheritdoc />
    public partial class BrandingCurrency : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "Currency",
                table: "CustomBranding",
                type: "character varying(8)",
                maxLength: 8,
                nullable: false,
                defaultValue: "€");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "Currency",
                table: "CustomBranding");
        }
    }
}
