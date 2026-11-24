using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MERRICK.DatabaseContext.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddDiscordFields : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "DiscordAvatar",
                schema: "core",
                table: "Users",
                type: "nvarchar(128)",
                maxLength: 128,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "DiscordId",
                schema: "core",
                table: "Users",
                type: "nvarchar(32)",
                maxLength: 32,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "DiscordUsername",
                schema: "core",
                table: "Users",
                type: "nvarchar(64)",
                maxLength: 64,
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_Users_DiscordId",
                schema: "core",
                table: "Users",
                column: "DiscordId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Users_DiscordId",
                schema: "core",
                table: "Users");

            migrationBuilder.DropColumn(
                name: "DiscordAvatar",
                schema: "core",
                table: "Users");

            migrationBuilder.DropColumn(
                name: "DiscordId",
                schema: "core",
                table: "Users");

            migrationBuilder.DropColumn(
                name: "DiscordUsername",
                schema: "core",
                table: "Users");
        }
    }
}
