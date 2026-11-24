using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MERRICK.DatabaseContext.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class UpdateHeroItemSchemaForRelationalComponents : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.RenameColumn(
                name: "ComponentsJson",
                schema: "misc",
                table: "HeroItems",
                newName: "ComponentCodesJson");

            migrationBuilder.AddColumn<int>(
                name: "RecipeCost",
                schema: "misc",
                table: "HeroItems",
                type: "int",
                nullable: false,
                defaultValue: 0);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "RecipeCost",
                schema: "misc",
                table: "HeroItems");

            migrationBuilder.RenameColumn(
                name: "ComponentCodesJson",
                schema: "misc",
                table: "HeroItems",
                newName: "ComponentsJson");
        }
    }
}
