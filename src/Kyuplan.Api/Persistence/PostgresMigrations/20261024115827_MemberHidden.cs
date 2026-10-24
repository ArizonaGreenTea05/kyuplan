using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Kyuplan.Api.Persistence.PostgresMigrations
{
    /// <inheritdoc />
    public partial class MemberHidden : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "Hidden",
                table: "Members",
                type: "boolean",
                nullable: false,
                defaultValue: false);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "Hidden",
                table: "Members");
        }
    }
}
