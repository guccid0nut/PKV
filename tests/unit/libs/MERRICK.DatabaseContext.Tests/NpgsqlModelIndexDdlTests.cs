// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

using FluentAssertions;
using MERRICK.DatabaseContext.Entities.Core;
using MERRICK.DatabaseContext.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using Xunit;

namespace MERRICK.DatabaseContext.Tests;

public sealed class NpgsqlModelIndexDdlTests
{
    [Fact]
    public void GenerateCreateScript_WhenNpgsqlProvider_GeneratesBothBtreeUniqueAndNonUniqueGinTrgmIndexes()
    {
        // Protects: Vanilla PostgreSQL 17 DDL creation from "access method 'gin' does not support unique indexes" crash
        // Fails when: EF Core unnamed index slot reuse mutates Account.Name unique btree index into a unique GIN index
        // Seam: Real EF Core Npgsql Database.GenerateCreateScript() without active database connection

        // Arrange
        DbContextOptionsBuilder<MerrickContext> optionsBuilder = new();
        optionsBuilder.UseNpgsql("Host=127.0.0.1;Database=test;Username=test;Password=test");

        using MerrickContext context = new(optionsBuilder.Options);

        // Act
        string ddlScript = context.Database.GenerateCreateScript();

        // Assert
        // 1. Account.Name must have BOTH unique btree and non-unique GIN trigram index
        ddlScript.Should().Contain("\"IX_Accounts_Name\"", "unique btree index on Account.Name must be present");
        ddlScript.Should().Contain("\"IX_Accounts_Name_Trgm\"", "non-unique GIN trigram index on Account.Name must be present");

        // 2. Must NEVER create a UNIQUE GIN index (Postgres rejects 'access method gin does not support unique indexes')
        ddlScript.Should().NotContain("CREATE UNIQUE INDEX \"IX_Accounts_Name_Trgm\"");
        ddlScript.Should().NotMatchRegex(@"CREATE\s+UNIQUE\s+INDEX\s+[^;]*USING\s+gin",
            "PostgreSQL access method 'gin' does not support UNIQUE indexes");

        // 3. Clans and Users also emit separate btree and GIN indexes
        ddlScript.Should().Contain("\"IX_Clans_Name\"");
        ddlScript.Should().Contain("\"IX_Clans_Name_Trgm\"");
        ddlScript.Should().Contain("\"IX_Users_DiscordUsername\"");
        ddlScript.Should().Contain("\"IX_Users_DiscordUsername_Trgm\"");

        // 4. MatchStatistics emits separate btree and BRIN indexes
        ddlScript.Should().Contain("\"IX_MatchStatistics_TimestampRecorded\"");
        ddlScript.Should().Contain("\"IX_MatchStatistics_TimestampRecorded_Brin\"");

        // 5. Model metadata checks
        IEntityType? accountEntity = context.Model.FindEntityType(typeof(Account));
        accountEntity.Should().NotBeNull();

        IIndex? btreeIdx = accountEntity!.GetIndexes().FirstOrDefault(i => i.GetDatabaseName() == "IX_Accounts_Name");
        btreeIdx.Should().NotBeNull();
        btreeIdx!.IsUnique.Should().BeTrue("IX_Accounts_Name must be a unique btree index");
        (btreeIdx.GetMethod() ?? "btree").Should().Be("btree");

        IIndex? ginIdx = accountEntity.GetIndexes().FirstOrDefault(i => i.GetDatabaseName() == "IX_Accounts_Name_Trgm");
        ginIdx.Should().NotBeNull();
        ginIdx!.IsUnique.Should().BeFalse("IX_Accounts_Name_Trgm must be non-unique for GIN support");
        ginIdx.GetMethod().Should().Be("gin");
    }
}
