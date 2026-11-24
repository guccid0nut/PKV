using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MERRICK.DatabaseContext.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddPerformanceIndexesForGuidesClansAndMatchmaking : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateIndex(
                name: "IX_MatchmakingQueueWaitEntries_QueueType_TimestampMatched",
                schema: "data",
                table: "MatchmakingQueueWaitEntries",
                columns: ["QueueType", "TimestampMatched"]);

            migrationBuilder.CreateIndex(
                name: "IX_HeroGuides_HeroIdentifier",
                schema: "misc",
                table: "HeroGuides",
                column: "HeroIdentifier");

            migrationBuilder.CreateIndex(
                name: "IX_HeroGuides_HeroIdentifier_Public",
                schema: "misc",
                table: "HeroGuides",
                columns: ["HeroIdentifier", "Public"]);

            migrationBuilder.CreateIndex(
                name: "IX_Clans_Name",
                schema: "core",
                table: "Clans",
                column: "Name");

            migrationBuilder.CreateIndex(
                name: "IX_Clans_Tag",
                schema: "core",
                table: "Clans",
                column: "Tag");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_MatchmakingQueueWaitEntries_QueueType_TimestampMatched",
                schema: "data",
                table: "MatchmakingQueueWaitEntries");

            migrationBuilder.DropIndex(
                name: "IX_HeroGuides_HeroIdentifier",
                schema: "misc",
                table: "HeroGuides");

            migrationBuilder.DropIndex(
                name: "IX_HeroGuides_HeroIdentifier_Public",
                schema: "misc",
                table: "HeroGuides");

            migrationBuilder.DropIndex(
                name: "IX_Clans_Name",
                schema: "core",
                table: "Clans");

            migrationBuilder.DropIndex(
                name: "IX_Clans_Tag",
                schema: "core",
                table: "Clans");
        }
    }
}
