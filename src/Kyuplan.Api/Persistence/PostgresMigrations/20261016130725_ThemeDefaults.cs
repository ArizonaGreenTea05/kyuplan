using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Kyuplan.Api.Persistence.PostgresMigrations
{
    /// <inheritdoc />
    public partial class ThemeDefaults : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "DefaultWebTheme",
                table: "CustomBranding",
                type: "character varying(32)",
                maxLength: 32,
                nullable: false,
                defaultValue: "default");

            migrationBuilder.AddColumn<string>(
                name: "DefaultWindowsTheme",
                table: "CustomBranding",
                type: "character varying(32)",
                maxLength: 32,
                nullable: false,
                defaultValue: "default");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "DefaultWebTheme",
                table: "CustomBranding");

            migrationBuilder.DropColumn(
                name: "DefaultWindowsTheme",
                table: "CustomBranding");
        }
    }
}
