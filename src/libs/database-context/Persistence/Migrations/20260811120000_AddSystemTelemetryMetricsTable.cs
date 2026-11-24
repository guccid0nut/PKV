using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MERRICK.DatabaseContext.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddSystemTelemetryMetricsTable : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "SystemTelemetryMetrics",
                schema: MerrickContext.CoreSchema,
                columns: table => new
                {
                    ID = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    ConcurrentConnectedUsers = table.Column<int>(type: "int", nullable: false),
                    ActiveMatchServers = table.Column<int>(type: "int", nullable: false),
                    ActiveMatchmakingQueues = table.Column<int>(type: "int", nullable: false),
                    AverageWaitTimeSeconds = table.Column<double>(type: "float", nullable: false),
                    ActiveRegionsJson = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    TimestampLastUpdated = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SystemTelemetryMetrics", x => x.ID);
                });

            migrationBuilder.CreateTable(
                name: "MatchmakingQueueWaitEntries",
                schema: MerrickContext.DefaultSchema,
                columns: table => new
                {
                    ID = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    QueueType = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: false),
                    PlayerCount = table.Column<int>(type: "int", nullable: false),
                    WaitDurationSeconds = table.Column<double>(type: "float", nullable: false),
                    TimestampMatched = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MatchmakingQueueWaitEntries", x => x.ID);
                });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "SystemTelemetryMetrics",
                schema: MerrickContext.CoreSchema);

            migrationBuilder.DropTable(
                name: "MatchmakingQueueWaitEntries",
                schema: MerrickContext.DefaultSchema);
        }
    }
}
