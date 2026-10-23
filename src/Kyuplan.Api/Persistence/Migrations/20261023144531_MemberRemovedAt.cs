using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Kyuplan.Api.Persistence.Migrations
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
                type: "TEXT",
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
