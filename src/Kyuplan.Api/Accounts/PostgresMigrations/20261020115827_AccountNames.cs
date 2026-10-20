using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace Kyuplan.Api.Accounts.PostgresMigrations
{
    /// <inheritdoc />
    public partial class AccountNames : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "AccountNames",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    UserId = table.Column<int>(type: "integer", nullable: false),
                    Name = table.Column<string>(type: "citext", maxLength: 64, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AccountNames", x => x.Id);
                    table.ForeignKey(
                        name: "FK_AccountNames_Users_UserId",
                        column: x => x.UserId,
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_AccountNames_Name",
                table: "AccountNames",
                column: "Name",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_AccountNames_UserId",
                table: "AccountNames",
                column: "UserId");

            // Existing user names and aliases; an alias that clashes with a user name (the old check let a race through) is left out.
            migrationBuilder.Sql("INSERT INTO \"AccountNames\" (\"UserId\", \"Name\") SELECT \"Id\", \"UserName\" FROM \"Users\"");
            migrationBuilder.Sql("INSERT INTO \"AccountNames\" (\"UserId\", \"Name\") SELECT a.\"UserId\", a.\"Alias\" FROM \"UserAliases\" a "
                + "WHERE NOT EXISTS (SELECT 1 FROM \"AccountNames\" n WHERE n.\"Name\" = a.\"Alias\")");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "AccountNames");
        }
    }
}
