using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Kyuplan.Api.Persistence.PostgresMigrations
{
    /// <inheritdoc />
    public partial class MemberRemovedAt : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "RemovedAt",
                table: "Members",
                type: "timestamp with time zone",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "RemovedAt",
                table: "Members");
        }
    }
}
