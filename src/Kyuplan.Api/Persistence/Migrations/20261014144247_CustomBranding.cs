using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Kyuplan.Api.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class CustomBranding : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "CustomBranding",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false),
                    ProductName = table.Column<string>(type: "TEXT", maxLength: 60, nullable: false),
                    OrganizationName = table.Column<string>(type: "TEXT", maxLength: 80, nullable: false),
                    AccentColor = table.Column<string>(type: "TEXT", maxLength: 7, nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CustomBranding", x => x.Id);
                    table.CheckConstraint("CK_CustomBranding_Single", "\"Id\" = 1");
                });

            migrationBuilder.CreateTable(
                name: "CustomBrandingImages",
                columns: table => new
                {
                    Slot = table.Column<string>(type: "TEXT", maxLength: 30, nullable: false),
                    ContentType = table.Column<string>(type: "TEXT", maxLength: 50, nullable: true),
                    Content = table.Column<byte[]>(type: "BLOB", nullable: true),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CustomBrandingImages", x => x.Slot);
                    table.CheckConstraint("CK_CustomBrandingImages_ContentAndType", "(\"Content\" IS NULL) = (\"ContentType\" IS NULL)");
                });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "CustomBranding");

            migrationBuilder.DropTable(
                name: "CustomBrandingImages");
        }
    }
}
