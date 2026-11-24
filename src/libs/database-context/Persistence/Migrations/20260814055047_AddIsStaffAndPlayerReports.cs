using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MERRICK.DatabaseContext.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddIsStaffAndPlayerReports : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "IsStaff",
                schema: "core",
                table: "Accounts",
                type: "bit",
                nullable: false,
                defaultValue: false);

            migrationBuilder.CreateTable(
                name: "PlayerReports",
                schema: "misc",
                columns: table => new
                {
                    ID = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    ReporterAccountID = table.Column<int>(type: "int", nullable: true),
                    ReporterAccountName = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: false),
                    ReportedAccountID = table.Column<int>(type: "int", nullable: true),
                    ReportedAccountName = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: false),
                    MatchID = table.Column<int>(type: "int", nullable: true),
                    Reason = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    Description = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: true),
                    Status = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: false),
                    TimestampCreated = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    TimestampReviewed = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    ReviewedByStaffName = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: true),
                    StaffNotes = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PlayerReports", x => x.ID);
                });

            migrationBuilder.CreateIndex(
                name: "IX_PlayerReports_MatchID",
                schema: "misc",
                table: "PlayerReports",
                column: "MatchID");

            migrationBuilder.CreateIndex(
                name: "IX_PlayerReports_ReportedAccountName",
                schema: "misc",
                table: "PlayerReports",
                column: "ReportedAccountName");

            migrationBuilder.CreateIndex(
                name: "IX_PlayerReports_ReporterAccountName",
                schema: "misc",
                table: "PlayerReports",
                column: "ReporterAccountName");

            migrationBuilder.CreateIndex(
                name: "IX_PlayerReports_Status",
                schema: "misc",
                table: "PlayerReports",
                column: "Status");

            migrationBuilder.CreateIndex(
                name: "IX_PlayerReports_TimestampCreated",
                schema: "misc",
                table: "PlayerReports",
                column: "TimestampCreated");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "PlayerReports",
                schema: "misc");

            migrationBuilder.DropColumn(
                name: "IsStaff",
                schema: "core",
                table: "Accounts");
        }
    }
}
