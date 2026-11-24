using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MERRICK.DatabaseContext.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddGuideSelectionsToAccount : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "DefaultGuideIDs",
                schema: "core",
                table: "Accounts",
                type: "nvarchar(max)",
                nullable: false,
                defaultValue: "[]");

            migrationBuilder.AddColumn<string>(
                name: "FavoriteGuideIDs",
                schema: "core",
                table: "Accounts",
                type: "nvarchar(max)",
                nullable: false,
                defaultValue: "[]");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "DefaultGuideIDs",
                schema: "core",
                table: "Accounts");

            migrationBuilder.DropColumn(
                name: "FavoriteGuideIDs",
                schema: "core",
                table: "Accounts");
        }
    }
}
