using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MERRICK.DatabaseContext.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class DropOwnedStoreItemsFromUsers : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(@"
            DO $$
            BEGIN
                IF EXISTS (
                    SELECT 1 
                    FROM information_schema.columns 
                    WHERE table_schema = 'core' 
                      AND table_name = 'Users' 
                      AND column_name = 'OwnedStoreItems'
                ) THEN
                    INSERT INTO ""core"".""UserInventory"" (""UserID"", ""ItemCode"", ""Quantity"", ""IsPermanent"", ""TimestampAcquired"", ""Source"")
                    SELECT u.""ID"", jsonb_array_elements_text(u.""OwnedStoreItems""::jsonb), 1, true, NOW(), 'PreMigrationBackfill'
                    FROM ""core"".""Users"" u
                    WHERE u.""OwnedStoreItems"" IS NOT NULL AND u.""OwnedStoreItems""::text <> '[]'
                    ON CONFLICT (""UserID"", ""ItemCode"") DO NOTHING;
                END IF;
            END $$;");

            migrationBuilder.DropColumn(
                name: "OwnedStoreItems",
                schema: "core",
                table: "Users");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "OwnedStoreItems",
                schema: "core",
                table: "Users",
                type: "text",
                nullable: false,
                defaultValue: "[]");
        }
    }
}
