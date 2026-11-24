using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MERRICK.DatabaseContext.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddAccountStatisticsAwards : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "award_least_deaths",
                schema: "data",
                table: "AccountStatistics",
                type: "int",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "award_most_annihilations",
                schema: "data",
                table: "AccountStatistics",
                type: "int",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "award_most_assists",
                schema: "data",
                table: "AccountStatistics",
                type: "int",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "award_most_building_damage",
                schema: "data",
                table: "AccountStatistics",
                type: "int",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "award_most_creep_kills",
                schema: "data",
                table: "AccountStatistics",
                type: "int",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "award_most_hero_damage_dealt",
                schema: "data",
                table: "AccountStatistics",
                type: "int",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "award_most_kills",
                schema: "data",
                table: "AccountStatistics",
                type: "int",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "award_most_quad_kills",
                schema: "data",
                table: "AccountStatistics",
                type: "int",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "award_most_smackdowns",
                schema: "data",
                table: "AccountStatistics",
                type: "int",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "award_most_wards_killed",
                schema: "data",
                table: "AccountStatistics",
                type: "int",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "award_least_deaths",
                schema: "data",
                table: "AccountStatistics");

            migrationBuilder.DropColumn(
                name: "award_most_annihilations",
                schema: "data",
                table: "AccountStatistics");

            migrationBuilder.DropColumn(
                name: "award_most_assists",
                schema: "data",
                table: "AccountStatistics");

            migrationBuilder.DropColumn(
                name: "award_most_building_damage",
                schema: "data",
                table: "AccountStatistics");

            migrationBuilder.DropColumn(
                name: "award_most_creep_kills",
                schema: "data",
                table: "AccountStatistics");

            migrationBuilder.DropColumn(
                name: "award_most_hero_damage_dealt",
                schema: "data",
                table: "AccountStatistics");

            migrationBuilder.DropColumn(
                name: "award_most_kills",
                schema: "data",
                table: "AccountStatistics");

            migrationBuilder.DropColumn(
                name: "award_most_quad_kills",
                schema: "data",
                table: "AccountStatistics");

            migrationBuilder.DropColumn(
                name: "award_most_smackdowns",
                schema: "data",
                table: "AccountStatistics");

            migrationBuilder.DropColumn(
                name: "award_most_wards_killed",
                schema: "data",
                table: "AccountStatistics");
        }
    }
}
