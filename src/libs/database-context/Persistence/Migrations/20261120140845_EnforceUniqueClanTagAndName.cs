// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable
#pragma warning disable CA1861

namespace MERRICK.DatabaseContext.Persistence.Migrations;

/// <inheritdoc />
public partial class _20261120140845_EnforceUniqueClanTagAndName : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        if (migrationBuilder.ActiveProvider == "Npgsql.EntityFrameworkCore.PostgreSQL")
        {
            migrationBuilder.Sql(@"
                DROP INDEX IF EXISTS ""core"".""IX_Clans_Name_Tag"";
                DROP INDEX IF EXISTS ""core"".""IX_Clans_Tag"";
                DROP INDEX IF EXISTS ""core"".""IX_Clans_Name"";

                CREATE UNIQUE INDEX IF NOT EXISTS ""IX_Clans_Tag""
                    ON ""core"".""Clans"" (lower(""Tag""));

                CREATE UNIQUE INDEX IF NOT EXISTS ""IX_Clans_Name""
                    ON ""core"".""Clans"" (lower(""Name""));
            ");
            return;
        }

        migrationBuilder.DropIndex(
            name: "IX_Clans_Name",
            schema: "core",
            table: "Clans");

        migrationBuilder.DropIndex(
            name: "IX_Clans_Name_Tag",
            schema: "core",
            table: "Clans");

        migrationBuilder.DropIndex(
            name: "IX_Clans_Tag",
            schema: "core",
            table: "Clans");

        migrationBuilder.AlterColumn<string>(
            name: "Tag",
            schema: "core",
            table: "Clans",
            type: "nvarchar(4)",
            maxLength: 4,
            nullable: false,
            collation: "SQL_Latin1_General_CP1_CI_AS",
            oldClrType: typeof(string),
            oldType: "nvarchar(4)",
            oldMaxLength: 4);

        migrationBuilder.CreateIndex(
            name: "IX_Clans_Name",
            schema: "core",
            table: "Clans",
            column: "Name",
            unique: true);

        migrationBuilder.CreateIndex(
            name: "IX_Clans_Tag",
            schema: "core",
            table: "Clans",
            column: "Tag",
            unique: true);
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        if (migrationBuilder.ActiveProvider == "Npgsql.EntityFrameworkCore.PostgreSQL")
        {
            migrationBuilder.Sql(@"
                DROP INDEX IF EXISTS ""core"".""IX_Clans_Tag"";
                DROP INDEX IF EXISTS ""core"".""IX_Clans_Name"";

                CREATE INDEX IF NOT EXISTS ""IX_Clans_Name""
                    ON ""core"".""Clans"" (""Name"");

                CREATE INDEX IF NOT EXISTS ""IX_Clans_Tag""
                    ON ""core"".""Clans"" (""Tag"");

                CREATE UNIQUE INDEX IF NOT EXISTS ""IX_Clans_Name_Tag""
                    ON ""core"".""Clans"" (""Name"", ""Tag"");
            ");
            return;
        }

        migrationBuilder.DropIndex(
            name: "IX_Clans_Name",
            schema: "core",
            table: "Clans");

        migrationBuilder.DropIndex(
            name: "IX_Clans_Tag",
            schema: "core",
            table: "Clans");

        migrationBuilder.AlterColumn<string>(
            name: "Tag",
            schema: "core",
            table: "Clans",
            type: "nvarchar(4)",
            maxLength: 4,
            nullable: false,
            oldClrType: typeof(string),
            oldType: "nvarchar(4)",
            oldMaxLength: 4,
            oldCollation: "SQL_Latin1_General_CP1_CI_AS");

        migrationBuilder.CreateIndex(
            name: "IX_Clans_Name",
            schema: "core",
            table: "Clans",
            column: "Name");

        migrationBuilder.CreateIndex(
            name: "IX_Clans_Name_Tag",
            schema: "core",
            table: "Clans",
            columns: new[] { "Name", "Tag" },
            unique: true);

        migrationBuilder.CreateIndex(
            name: "IX_Clans_Tag",
            schema: "core",
            table: "Clans",
            column: "Tag");
    }
}
