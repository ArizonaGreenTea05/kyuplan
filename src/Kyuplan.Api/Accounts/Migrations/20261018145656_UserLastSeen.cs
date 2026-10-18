using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Kyuplan.Api.Accounts.Migrations
{
    /// <inheritdoc />
    public partial class UserLastSeen : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "LastSeenAt",
                table: "Users",
                type: "TEXT",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "LastSeenAt",
                table: "Users");
        }
    }
}
