using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Kyuplan.Api.Accounts.Migrations
{
    /// <inheritdoc />
    public partial class UserRolesAndPermissions : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "Permissions",
                table: "Users",
                type: "TEXT",
                nullable: false,
                defaultValue: "[]");

            migrationBuilder.CreateTable(
                name: "Roles",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    Name = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false, collation: "NOCASE"),
                    IsSystem = table.Column<bool>(type: "INTEGER", nullable: false),
                    Permissions = table.Column<string>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Roles", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "UserRoles",
                columns: table => new
                {
                    RolesId = table.Column<int>(type: "INTEGER", nullable: false),
                    UsersId = table.Column<int>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_UserRoles", x => new { x.RolesId, x.UsersId });
                    table.ForeignKey(
                        name: "FK_UserRoles_Roles_RolesId",
                        column: x => x.RolesId,
                        principalTable: "Roles",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_UserRoles_Users_UsersId",
                        column: x => x.UsersId,
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Roles_Name",
                table: "Roles",
                column: "Name",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_UserRoles_UsersId",
                table: "UserRoles",
                column: "UsersId");

            // Everybody keeps what they could do: the administrators get the administrator role (which has every permission), everybody else a
            // role "User" with what a user without the flag could do until now (everything but users and branding). The keys are written out
            // on purpose: a migration describes what the catalogue was when it was made.
            migrationBuilder.Sql("INSERT INTO \"Roles\" (\"Name\", \"IsSystem\", \"Permissions\") VALUES ('Administrator', 1, '[]')");
            migrationBuilder.Sql(
                "INSERT INTO \"Roles\" (\"Name\", \"IsSystem\", \"Permissions\") VALUES ('User', 0, " +
                "'[\"dashboard.view\",\"statistics.view\",\"exams.view\",\"exams.edit\",\"exams.filters\",\"exams.candidates\",\"exams.results\"," +
                "\"members.view\",\"members.edit\",\"belts.view\",\"belts.edit\",\"comments.view\",\"comments.write\",\"comments.edit\"," +
                "\"comments.delete\",\"tags.view\",\"tags.edit\"]')");
            migrationBuilder.Sql(
                "INSERT INTO \"UserRoles\" (\"RolesId\", \"UsersId\") " +
                "SELECT (SELECT \"Id\" FROM \"Roles\" WHERE \"Name\" = CASE WHEN u.\"IsAdmin\" THEN 'Administrator' ELSE 'User' END), u.\"Id\" FROM \"Users\" u");

            migrationBuilder.DropColumn(
                name: "IsAdmin",
                table: "Users");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "IsAdmin",
                table: "Users",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            migrationBuilder.Sql(
                "UPDATE \"Users\" SET \"IsAdmin\" = 1 WHERE \"Id\" IN " +
                "(SELECT ur.\"UsersId\" FROM \"UserRoles\" ur JOIN \"Roles\" r ON r.\"Id\" = ur.\"RolesId\" WHERE r.\"IsSystem\" = 1)");

            migrationBuilder.DropTable(
                name: "UserRoles");

            migrationBuilder.DropTable(
                name: "Roles");

            migrationBuilder.DropColumn(
                name: "Permissions",
                table: "Users");
        }
    }
}
