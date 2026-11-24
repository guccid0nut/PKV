using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MERRICK.DatabaseContext.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddMatchEvidenceTable : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "MatchEvidence",
                schema: "misc",
                columns: table => new
                {
                    ID = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    MatchID = table.Column<int>(type: "int", nullable: false),
                    HasReplay = table.Column<bool>(type: "bit", nullable: false),
                    HasVoice = table.Column<bool>(type: "bit", nullable: false),
                    ChatLogJson = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    VoiceTrackLegionUrl = table.Column<string>(type: "nvarchar(1024)", maxLength: 1024, nullable: true),
                    VoiceTrackHellbourneUrl = table.Column<string>(type: "nvarchar(1024)", maxLength: 1024, nullable: true),
                    TimestampExtracted = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MatchEvidence", x => x.ID);
                });

            migrationBuilder.CreateIndex(
                name: "IX_MatchEvidence_MatchID",
                schema: "misc",
                table: "MatchEvidence",
                column: "MatchID",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "MatchEvidence",
                schema: "misc");
        }
    }
}
