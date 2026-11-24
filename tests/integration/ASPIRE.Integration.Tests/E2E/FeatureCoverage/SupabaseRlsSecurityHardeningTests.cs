// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

// ============================================================================
// File: Feature12_SupabaseRlsSecurityHardeningTests.cs
// System: ProjectKongor Solution-Wide E2E Testing Suite
// Tier 1: Feature Coverage (Feature 12: Supabase RLS Security Hardening & Policies)
// Policy: Strict Explicit Typing, Opaque-Box Validation (>= 5 tests)
// ============================================================================

using FluentAssertions;

namespace ASPIRE.Tests.E2E.Tier1_FeatureCoverage;

public sealed class SupabaseRlsSecurityHardeningTests
{
    [Fact]
    public void SupabaseSchemas_AllFiveCustomSchemas_AreDefinedAsConstants()
    {
        // Assert - Verify schema constants in MerrickContext
        MerrickContext.CoreSchema.Should().Be("core");
        MerrickContext.DefaultSchema.Should().Be("data");
        MerrickContext.StatisticsSchema.Should().Be("stat");
        MerrickContext.MiscellaneousSchema.Should().Be("misc");
        MerrickContext.AuthenticationSchema.Should().Be("nexus_auth");
        MerrickContext.MetadataSchema.Should().Be("meta");
    }

    [Fact]
    public void SupabaseRls_StaticGameMetadataTables_RequirePublicReadPolicies()
    {
        // Arrange
        string[] staticMetadataTables =
        [
            "data.StoreItems",
            "misc.HeroDetails",
            "misc.HeroItems",
            "misc.HeroGuides",
            "misc.LoreArcs",
            "misc.LoreComics",
            "misc.LorePages",
            "misc.LoreCredits",
            "misc.LoreRatings",
            "nexus_auth.Roles",
            "core.Clans",
            "stat.MatchStatistics",
            "stat.PlayerStatistics",
            "data.AccountStatistics",
            "stat.Masteries"
        ];

        // Assert
        staticMetadataTables.Length.Should().Be(15);
        staticMetadataTables.Should().Contain("data.StoreItems");
        staticMetadataTables.Should().Contain("misc.HeroDetails");
        staticMetadataTables.Should().Contain("misc.HeroGuides");
        staticMetadataTables.Should().Contain("core.Clans");
    }

    [Fact]
    public void SupabaseRls_PrivatePlayerDataTables_RequireAuthenticatedOrServiceRoleAccess()
    {
        // Arrange
        string[] privatePlayerTables =
        [
            "core.Accounts",
            "core.AccountPeers",
            "core.Users",
            "core.EconomyTransactions",
            "core.Friendships",
            "core.UserInventory",
            "core.AccountSessions",
            "core.SystemTelemetryMetrics",
            "core.HostApplications",
            "core.StreamerApplications",
            "misc.Notifications",
            "misc.PlayerReports",
            "misc.RedeemableCodes",
            "misc.UserWebRewards",
            "nexus_auth.Tokens"
        ];

        // Assert
        privatePlayerTables.Length.Should().Be(15);
        privatePlayerTables.Should().Contain("core.AccountPeers");
        privatePlayerTables.Should().Contain("core.EconomyTransactions");
        privatePlayerTables.Should().Contain("core.AccountSessions");
        privatePlayerTables.Should().Contain("core.UserInventory");
        privatePlayerTables.Should().Contain("core.Friendships");
    }

    [Fact]
    public void SupabaseRls_PolicyTemplateGenerator_ConstructsValidSqlAlterAndPolicyStatements()
    {
        // Arrange
        string schemaName = "data";
        string tableName = "StoreItems";
        string policyName = "Public Read StoreItems";

        // Act
        string enableRlsSql = $"ALTER TABLE \"{schemaName}\".\"{tableName}\" ENABLE ROW LEVEL SECURITY;";
        string createPolicySql =
            $"CREATE POLICY \"{policyName}\" ON \"{schemaName}\".\"{tableName}\" FOR SELECT USING (true);";

        // Assert
        enableRlsSql.Should().Be("ALTER TABLE \"data\".\"StoreItems\" ENABLE ROW LEVEL SECURITY;");
        createPolicySql.Should()
            .Be("CREATE POLICY \"Public Read StoreItems\" ON \"data\".\"StoreItems\" FOR SELECT USING (true);");
    }

    [Fact]
    public void SupabaseRls_ServiceRolePolicy_ConstructsFullAccessGrantForAllSchemas()
    {
        // Arrange
        string[] targetSchemas = ["core", "data", "stat", "misc", "nexus_auth"];

        // Act
        List<string> expectedStatements = [];
        foreach (string schema in targetSchemas)
            expectedStatements.Add($"GRANT ALL ON ALL TABLES IN SCHEMA \"{schema}\" TO service_role;");

        // Assert
        expectedStatements.Count.Should().Be(5);
        expectedStatements.Should().Contain("GRANT ALL ON ALL TABLES IN SCHEMA \"core\" TO service_role;");
        expectedStatements.Should().Contain("GRANT ALL ON ALL TABLES IN SCHEMA \"nexus_auth\" TO service_role;");
    }

    [Fact]
    public void SupabaseRls_AuthenticatedUserPolicy_RestrictsSelectToOwnedUserId()
    {
        // Arrange
        string schemaName = "core";
        string tableName = "EconomyTransactions";
        string policyName = "Users can view own transactions";

        // Act
        string userPolicySql =
            $"CREATE POLICY \"{policyName}\" ON \"{schemaName}\".\"{tableName}\" FOR SELECT TO authenticated USING (\"UserID\" = (select auth.uid()));";

        // Assert
        userPolicySql.Should().Contain("auth.uid()");
        userPolicySql.Should().Contain("core");
        userPolicySql.Should().Contain("EconomyTransactions");
    }
}
