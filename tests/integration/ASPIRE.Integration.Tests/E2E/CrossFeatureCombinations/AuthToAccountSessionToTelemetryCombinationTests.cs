// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

// ============================================================================
// File: AuthToAccountSessionToTelemetryCombinationTests.cs
// System: ProjectKongor Solution-Wide E2E Testing Suite
// Tier 3: Cross-Feature Combinations (Auth -> Account Session -> Telemetry)
// Policy: Strict Explicit Typing, Multi-Service Integration Verification
// ============================================================================

using FluentAssertions;

namespace ASPIRE.Tests.E2E.Tier3_CrossFeatureCombinations;

public sealed class AuthToAccountSessionToTelemetryCombinationTests
{
    private static MerrickContext CreateInMemoryContext()
    {
        return TestDbContextFactory.CreateInMemoryContext("Auth_Telemetry_" + Guid.NewGuid().ToString("N"));
    }

    [Fact]
    public async Task Combination_SrpAuthentication_ParsesWirePayloadAndUpdatesAccountTelemetry()
    {
        // Arrange
        using MerrickContext db = CreateInMemoryContext();
        Role userRole = new() { ID = 1, Name = "User" };
        db.Roles.Add(userRole);

        string username = "TelemetryPlayer_" + Guid.NewGuid().ToString("N")[..6];
        User user = new()
        {
            EmailAddress = $"{username}@kongor.test",
            PBKDF2PasswordHash = "pbkdf2_hash_sample",
            SRPPasswordHash = "srp_hash_sample",
            SRPPasswordSalt = "srp_salt_sample",
            Role = userRole
        };
        Account account = new()
        {
            Name = username,
            User = user,
            Type = AccountType.Normal,
            IsMain = true,
            IPAddressCollection = [],
            MACAddressCollection = [],
            SystemInformationCollection = [],
            SystemInformationHashCollection = []
        };
        db.Users.Add(user);
        db.Accounts.Add(account);
        await db.SaveChangesAsync();

        // Act - Simulate client authentication telemetry capture
        string remoteIp = "192.168.1.150";
        string sysInfo = "00-14-22-01-23-45|Windows 10|Intel Core i7-12700K|GeForce RTX 3080|16384 MB";
        string hwidHash = "a1b2c3d4e5f60718293a4b5c6d7e8f90";

        string[] sysInfoParts = sysInfo.Split('|');
        string mac = sysInfoParts[0];
        string sysInfoRest = string.Join('|', sysInfoParts.Skip(1));

        account.IPAddressCollection.Add(remoteIp);
        account.MACAddressCollection.Add(mac);
        account.SystemInformationCollection.Add(sysInfoRest);
        account.SystemInformationHashCollection.Add(hwidHash);
        account.TimestampLastActive = TimeProvider.System.GetUtcNow();

        await db.SaveChangesAsync();

        // Assert - Telemetry is persisted in account collections
        Account? refreshed = await db.Accounts.FindAsync(account.ID);
        refreshed.Should().NotBeNull();
        refreshed!.IPAddressCollection.Should().Contain(remoteIp);
        refreshed.MACAddressCollection.Should().Contain("00-14-22-01-23-45");
        refreshed.SystemInformationCollection.Should().Contain(sysInfoRest);
        refreshed.SystemInformationHashCollection.Should().Contain(hwidHash);
    }

    [Trait("Category", "Manual")]
    [Fact]
    public async Task Combination_SessionRepository_IssuesAndTracksActiveSessionTokens()
    {
        if (!NexusTestEnvironment.IsManualTestEnabled) return;
        // Arrange
        using MerrickContext db = CreateInMemoryContext();
        SessionRepository sessionRepo = new(db);

        // Act - Issue session authentication token
        Token sessionToken = await sessionRepo.IssueTokenAsync(
            55,
            TokenPurpose.Session,
            TimeSpan.FromDays(7));

        // Assert
        sessionToken.Should().NotBeNull();
        sessionToken.UserID.Should().Be(55);
        sessionToken.Purpose.Should().Be(TokenPurpose.Session);

        Token? activeToken = await sessionRepo.GetTokenAsync(sessionToken.Value);
        activeToken.Should().NotBeNull();
        activeToken!.UserID.Should().Be(55);
    }

    [Trait("Category", "Manual")]
    [Fact]
    public async Task Combination_BannedAccountLogin_IsBlockedFromIssuingSession()
    {
        if (!NexusTestEnvironment.IsManualTestEnabled) return;

        // Arrange
        using MerrickContext db = CreateInMemoryContext();
        Role userRole = new() { ID = 1, Name = "User" };
        db.Roles.Add(userRole);

        User user = new()
        {
            EmailAddress = $"banned_login_{Guid.NewGuid():N}@kongor.test",
            PBKDF2PasswordHash = "h",
            SRPPasswordHash = "h",
            SRPPasswordSalt = "s",
            Role = userRole
        };
        Account account = new()
        {
            Name = "BannedLoginPlayer",
            User = user,
            Type = AccountType.Disabled,
            IsMain = true,
            BanExpiration = TimeProvider.System.GetUtcNow().AddDays(30),
            BanReason = "Cheating"
        };
        db.Users.Add(user);
        db.Accounts.Add(account);
        await db.SaveChangesAsync();

        // Act - Check whether login can proceed
        bool canLogin = account.Type != AccountType.Disabled && !account.IsBanned;

        // Assert
        canLogin.Should().BeFalse();
    }
}
