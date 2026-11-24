using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MERRICK.DatabaseContext.Persistence.Migrations;

/// <inheritdoc />
public partial class AddHeroIdentifierToPlayerStatistics : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<string>(
            name: "HeroIdentifier",
            schema: "stat",
            table: "PlayerStatistics",
            type: "nvarchar(50)",
            maxLength: 50,
            nullable: false,
            defaultValue: "");

        migrationBuilder.CreateIndex(
            name: "IX_PlayerStatistics_AccountID_HeroIdentifier",
            schema: "stat",
            table: "PlayerStatistics",
            columns: ["AccountID", "HeroIdentifier"]);
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropIndex(
            name: "IX_PlayerStatistics_AccountID_HeroIdentifier",
            schema: "stat",
            table: "PlayerStatistics");

        migrationBuilder.DropColumn(
            name: "HeroIdentifier",
            schema: "stat",
            table: "PlayerStatistics");
    }
}
