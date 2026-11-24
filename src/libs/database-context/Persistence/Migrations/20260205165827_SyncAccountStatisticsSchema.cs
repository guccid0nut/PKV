using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MERRICK.DatabaseContext.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class SyncAccountStatisticsSchema : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropPrimaryKey(
                name: "PK_AccountStatistics",
                schema: "data",
                table: "AccountStatistics");

            migrationBuilder.AddColumn<int>(
                name: "id",
                schema: "data",
                table: "AccountStatistics",
                type: "int",
                nullable: false,
                defaultValue: 0)
                .Annotation("SqlServer:Identity", "1, 1");

            migrationBuilder.AddColumn<int>(
                name: "type",
                schema: "data",
                table: "AccountStatistics",
                type: "int",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddPrimaryKey(
                name: "PK_AccountStatistics",
                schema: "data",
                table: "AccountStatistics",
                column: "id");

            migrationBuilder.CreateIndex(
                name: "IX_AccountStatistics_account_id_type",
                schema: "data",
                table: "AccountStatistics",
                columns: ["account_id", "type"],
                unique: true);

            migrationBuilder.AddForeignKey(
                name: "FK_AccountStatistics_Accounts_account_id",
                schema: "data",
                table: "AccountStatistics",
                column: "account_id",
                principalSchema: "core",
                principalTable: "Accounts",
                principalColumn: "ID",
                onDelete: ReferentialAction.Cascade);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_AccountStatistics_Accounts_account_id",
                schema: "data",
                table: "AccountStatistics");

            migrationBuilder.DropPrimaryKey(
                name: "PK_AccountStatistics",
                schema: "data",
                table: "AccountStatistics");

            migrationBuilder.DropIndex(
                name: "IX_AccountStatistics_account_id_type",
                schema: "data",
                table: "AccountStatistics");

            migrationBuilder.DropColumn(
                name: "id",
                schema: "data",
                table: "AccountStatistics");

            migrationBuilder.DropColumn(
                name: "type",
                schema: "data",
                table: "AccountStatistics");

            migrationBuilder.AddPrimaryKey(
                name: "PK_AccountStatistics",
                schema: "data",
                table: "AccountStatistics",
                column: "account_id");
        }
    }
}
