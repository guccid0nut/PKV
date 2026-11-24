using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MERRICK.DatabaseContext.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddHostApplications : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "HostApplications",
                schema: "core",
                columns: table => new
                {
                    ID = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    UserID = table.Column<int>(type: "int", nullable: false),
                    MainAccountID = table.Column<int>(type: "int", nullable: false),
                    HostAccountID = table.Column<int>(type: "int", nullable: true),
                    HostUsername = table.Column<string>(type: "nvarchar(15)", maxLength: 15, nullable: false),
                    EncryptedHostPassword = table.Column<string>(type: "nvarchar(512)", maxLength: 512, nullable: true),
                    Status = table.Column<int>(type: "int", nullable: false),
                    Location = table.Column<string>(type: "nvarchar(16)", maxLength: 16, nullable: false),
                    Instances = table.Column<int>(type: "int", nullable: false),
                    ServerNamePrefix = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    ApplicationNotes = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    StaffNotes = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    ReviewedByStaffName = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: true),
                    TimestampApplied = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    TimestampReviewed = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    TimestampLastHostActivity = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_HostApplications", x => x.ID);
                    table.ForeignKey(
                        name: "FK_HostApplications_Accounts_HostAccountID",
                        column: x => x.HostAccountID,
                        principalSchema: "core",
                        principalTable: "Accounts",
                        principalColumn: "ID",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_HostApplications_Accounts_MainAccountID",
                        column: x => x.MainAccountID,
                        principalSchema: "core",
                        principalTable: "Accounts",
                        principalColumn: "ID",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_HostApplications_Users_UserID",
                        column: x => x.UserID,
                        principalSchema: "core",
                        principalTable: "Users",
                        principalColumn: "ID",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_HostApplications_HostAccountID",
                schema: "core",
                table: "HostApplications",
                column: "HostAccountID");

            migrationBuilder.CreateIndex(
                name: "IX_HostApplications_MainAccountID",
                schema: "core",
                table: "HostApplications",
                column: "MainAccountID");

            migrationBuilder.CreateIndex(
                name: "IX_HostApplications_Status",
                schema: "core",
                table: "HostApplications",
                column: "Status");

            migrationBuilder.CreateIndex(
                name: "IX_HostApplications_UserID",
                schema: "core",
                table: "HostApplications",
                column: "UserID");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "HostApplications",
                schema: "core");
        }
    }
}
