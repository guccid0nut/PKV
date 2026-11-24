using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MERRICK.DatabaseContext.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddAccountPenaltiesAndReportPunishments : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "PunishmentDurationHours",
                schema: "misc",
                table: "PlayerReports",
                type: "int",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "PunishmentExpiration",
                schema: "misc",
                table: "PlayerReports",
                type: "datetimeoffset",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "PunishmentType",
                schema: "misc",
                table: "PlayerReports",
                type: "nvarchar(50)",
                maxLength: 50,
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "BanExpiration",
                schema: "core",
                table: "Accounts",
                type: "datetimeoffset",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "BanReason",
                schema: "core",
                table: "Accounts",
                type: "nvarchar(255)",
                maxLength: 255,
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "MuteExpiration",
                schema: "core",
                table: "Accounts",
                type: "datetimeoffset",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "MuteReason",
                schema: "core",
                table: "Accounts",
                type: "nvarchar(255)",
                maxLength: 255,
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "SuspensionExpiration",
                schema: "core",
                table: "Accounts",
                type: "datetimeoffset",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "SuspensionReason",
                schema: "core",
                table: "Accounts",
                type: "nvarchar(255)",
                maxLength: 255,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "PunishmentDurationHours",
                schema: "misc",
                table: "PlayerReports");

            migrationBuilder.DropColumn(
                name: "PunishmentExpiration",
                schema: "misc",
                table: "PlayerReports");

            migrationBuilder.DropColumn(
                name: "PunishmentType",
                schema: "misc",
                table: "PlayerReports");

            migrationBuilder.DropColumn(
                name: "BanExpiration",
                schema: "core",
                table: "Accounts");

            migrationBuilder.DropColumn(
                name: "BanReason",
                schema: "core",
                table: "Accounts");

            migrationBuilder.DropColumn(
                name: "MuteExpiration",
                schema: "core",
                table: "Accounts");

            migrationBuilder.DropColumn(
                name: "MuteReason",
                schema: "core",
                table: "Accounts");

            migrationBuilder.DropColumn(
                name: "SuspensionExpiration",
                schema: "core",
                table: "Accounts");

            migrationBuilder.DropColumn(
                name: "SuspensionReason",
                schema: "core",
                table: "Accounts");
        }
    }
}
