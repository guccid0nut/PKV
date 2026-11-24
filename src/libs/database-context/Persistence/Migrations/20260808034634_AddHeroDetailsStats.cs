using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MERRICK.DatabaseContext.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddHeroDetailsStats : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "Category",
                schema: "misc",
                table: "HeroItems",
                type: "nvarchar(100)",
                maxLength: 100,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<double>(
                name: "AgilityGrowth",
                schema: "misc",
                table: "HeroDetails",
                type: "float",
                nullable: false,
                defaultValue: 0.0);

            migrationBuilder.AddColumn<double>(
                name: "Armor",
                schema: "misc",
                table: "HeroDetails",
                type: "float",
                nullable: false,
                defaultValue: 0.0);

            migrationBuilder.AddColumn<string>(
                name: "ArmorMitigation",
                schema: "misc",
                table: "HeroDetails",
                type: "nvarchar(20)",
                maxLength: 20,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "AttackType",
                schema: "misc",
                table: "HeroDetails",
                type: "nvarchar(50)",
                maxLength: 50,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<double>(
                name: "BaseAgility",
                schema: "misc",
                table: "HeroDetails",
                type: "float",
                nullable: false,
                defaultValue: 0.0);

            migrationBuilder.AddColumn<double>(
                name: "BaseIntelligence",
                schema: "misc",
                table: "HeroDetails",
                type: "float",
                nullable: false,
                defaultValue: 0.0);

            migrationBuilder.AddColumn<double>(
                name: "BaseStrength",
                schema: "misc",
                table: "HeroDetails",
                type: "float",
                nullable: false,
                defaultValue: 0.0);

            migrationBuilder.AddColumn<double>(
                name: "IntelligenceGrowth",
                schema: "misc",
                table: "HeroDetails",
                type: "float",
                nullable: false,
                defaultValue: 0.0);

            migrationBuilder.AddColumn<int>(
                name: "MaxDamage",
                schema: "misc",
                table: "HeroDetails",
                type: "int",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "MinDamage",
                schema: "misc",
                table: "HeroDetails",
                type: "int",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "MovementSpeed",
                schema: "misc",
                table: "HeroDetails",
                type: "int",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<double>(
                name: "StrengthGrowth",
                schema: "misc",
                table: "HeroDetails",
                type: "float",
                nullable: false,
                defaultValue: 0.0);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "Category",
                schema: "misc",
                table: "HeroItems");

            migrationBuilder.DropColumn(
                name: "AgilityGrowth",
                schema: "misc",
                table: "HeroDetails");

            migrationBuilder.DropColumn(
                name: "Armor",
                schema: "misc",
                table: "HeroDetails");

            migrationBuilder.DropColumn(
                name: "ArmorMitigation",
                schema: "misc",
                table: "HeroDetails");

            migrationBuilder.DropColumn(
                name: "AttackType",
                schema: "misc",
                table: "HeroDetails");

            migrationBuilder.DropColumn(
                name: "BaseAgility",
                schema: "misc",
                table: "HeroDetails");

            migrationBuilder.DropColumn(
                name: "BaseIntelligence",
                schema: "misc",
                table: "HeroDetails");

            migrationBuilder.DropColumn(
                name: "BaseStrength",
                schema: "misc",
                table: "HeroDetails");

            migrationBuilder.DropColumn(
                name: "IntelligenceGrowth",
                schema: "misc",
                table: "HeroDetails");

            migrationBuilder.DropColumn(
                name: "MaxDamage",
                schema: "misc",
                table: "HeroDetails");

            migrationBuilder.DropColumn(
                name: "MinDamage",
                schema: "misc",
                table: "HeroDetails");

            migrationBuilder.DropColumn(
                name: "MovementSpeed",
                schema: "misc",
                table: "HeroDetails");

            migrationBuilder.DropColumn(
                name: "StrengthGrowth",
                schema: "misc",
                table: "HeroDetails");
        }
    }
}
