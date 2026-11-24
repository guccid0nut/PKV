using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable
#pragma warning disable CA1861

namespace MERRICK.DatabaseContext.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddDeveloperIssuesTable : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "DeveloperIssues",
                schema: MerrickContext.CoreSchema,
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Fingerprint = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    Subsystem = table.Column<int>(type: "int", nullable: false),
                    Severity = table.Column<int>(type: "int", nullable: false),
                    Status = table.Column<int>(type: "int", nullable: false),
                    Title = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: false),
                    RawMessage = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    NormalizedSignature = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    StackTrace = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    FaultingModule = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: true),
                    FaultingAddress = table.Column<decimal>(type: "decimal(20,0)", nullable: true),
                    OccurrenceCount = table.Column<int>(type: "int", nullable: false, defaultValue: 1),
                    FirstSeenUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    LastSeenUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    ResolutionRecipeApplied = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: true),
                    ResolutionReceipt = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    AssociatedIssueTrackerId = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DeveloperIssues", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_DeveloperIssues_Fingerprint",
                schema: MerrickContext.CoreSchema,
                table: "DeveloperIssues",
                column: "Fingerprint",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_DeveloperIssues_Status",
                schema: MerrickContext.CoreSchema,
                table: "DeveloperIssues",
                column: "Status");

            migrationBuilder.CreateIndex(
                name: "IX_DeveloperIssues_Subsystem",
                schema: MerrickContext.CoreSchema,
                table: "DeveloperIssues",
                column: "Subsystem");

            migrationBuilder.CreateIndex(
                name: "IX_DeveloperIssues_Status_Severity",
                schema: MerrickContext.CoreSchema,
                table: "DeveloperIssues",
                columns: new[] { "Status", "Severity" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "DeveloperIssues",
                schema: MerrickContext.CoreSchema);
        }
    }
}
