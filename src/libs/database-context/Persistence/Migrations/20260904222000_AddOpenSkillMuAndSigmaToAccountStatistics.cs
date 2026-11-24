using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MERRICK.DatabaseContext.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddOpenSkillMuAndSigmaToAccountStatistics : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<double>(
                name: "mu",
                schema: "data",
                table: "AccountStatistics",
                type: "float",
                nullable: false,
                defaultValue: 25.0);

            migrationBuilder.AddColumn<double>(
                name: "sigma",
                schema: "data",
                table: "AccountStatistics",
                type: "float",
                nullable: false,
                defaultValue: 8.333333333333334);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "mu",
                schema: "data",
                table: "AccountStatistics");

            migrationBuilder.DropColumn(
                name: "sigma",
                schema: "data",
                table: "AccountStatistics");
        }
    }
}
