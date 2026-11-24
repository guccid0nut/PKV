using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MERRICK.DatabaseContext.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddHeroMasteriesTable : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "Masteries",
                schema: "stat",
                columns: table => new
                {
                    ID = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    AccountID = table.Column<int>(type: "int", nullable: false),
                    HeroExperiences = table.Column<string>(type: "nvarchar(max)", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Masteries", x => x.ID);
                    table.ForeignKey(
                        name: "FK_Masteries_Accounts_AccountID",
                        column: x => x.AccountID,
                        principalSchema: "core",
                        principalTable: "Accounts",
                        principalColumn: "ID",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "RedeemableCodes",
                schema: "misc",
                columns: table => new
                {
                    ID = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    Code = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: false),
                    GoldCoinsReward = table.Column<int>(type: "int", nullable: false),
                    SilverCoinsReward = table.Column<int>(type: "int", nullable: false),
                    PlinkoTicketsReward = table.Column<int>(type: "int", nullable: false),
                    ProductID = table.Column<int>(type: "int", nullable: true),
                    TimestampCreated = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    TimestampRedeemed = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    RedeemedByAccountID = table.Column<int>(type: "int", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_RedeemableCodes", x => x.ID);
                    table.ForeignKey(
                        name: "FK_RedeemableCodes_Accounts_RedeemedByAccountID",
                        column: x => x.RedeemedByAccountID,
                        principalSchema: "core",
                        principalTable: "Accounts",
                        principalColumn: "ID");
                });

            migrationBuilder.CreateIndex(
                name: "IX_Masteries_AccountID",
                schema: "stat",
                table: "Masteries",
                column: "AccountID",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_RedeemableCodes_Code",
                schema: "misc",
                table: "RedeemableCodes",
                column: "Code",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_RedeemableCodes_RedeemedByAccountID",
                schema: "misc",
                table: "RedeemableCodes",
                column: "RedeemedByAccountID");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "Masteries",
                schema: "stat");

            migrationBuilder.DropTable(
                name: "RedeemableCodes",
                schema: "misc");
        }
    }
}
