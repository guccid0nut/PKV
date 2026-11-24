using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MERRICK.DatabaseContext.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddStorageStatusColumns : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "AutomaticCloudUpload",
                schema: "core",
                table: "Accounts",
                type: "bit",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "BackupLastUpdatedTime",
                schema: "core",
                table: "Accounts",
                type: "datetimeoffset",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "UseCloud",
                schema: "core",
                table: "Accounts",
                type: "bit",
                nullable: false,
                defaultValue: false);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "AutomaticCloudUpload",
                schema: "core",
                table: "Accounts");

            migrationBuilder.DropColumn(
                name: "BackupLastUpdatedTime",
                schema: "core",
                table: "Accounts");

            migrationBuilder.DropColumn(
                name: "UseCloud",
                schema: "core",
                table: "Accounts");
        }
    }
}
