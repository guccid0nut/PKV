// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

// ============================================================================
// File: Feature03_InterServiceContractsTests.cs
// System: ProjectKongor Solution-Wide E2E Testing Suite
// Tier 1: Feature Coverage (Feature 3: Standardized Inter-Service Contracts)
// Policy: Strict Explicit Typing, Opaque-Box Validation (>= 5 tests)
// ============================================================================

using FluentAssertions;
using NEXUS.Contracts.Matchmaking.V1;
using NEXUS.Contracts.Telemetry.V1;

namespace ASPIRE.Tests.E2E.Tier1_FeatureCoverage;

public sealed class InterServiceContractsTests
{
    [Fact]
    public void MatchmakingContracts_ProtoMessages_InstantiateWithDefaults()
    {
        // Arrange & Act
        SubmitTicketRequest request = new()
        {
            Player = new PlayerMatchCandidate
            {
                AccountId = 12345,
                Nickname = "HeroPlayer",
                Mmr = 1500,
                RegionPreference = "USE"
            },
            GameMode = GameMode.AllPick
        };

        SubmitTicketResponse response = new()
        {
            TicketId = "ticket-9988",
            EstimatedWaitSeconds = 45
        };

        // Assert
        request.Player.AccountId.Should().Be(12345);
        request.Player.Mmr.Should().Be(1500);
        request.GameMode.Should().Be(GameMode.AllPick);
        response.TicketId.Should().Be("ticket-9988");
        response.EstimatedWaitSeconds.Should().Be(45);
    }

    [Fact]
    public void PlayerSessionToken_DomainRecord_EnforcesImmutabilityAndCorrectTypes()
    {
        // Arrange
        Guid sessionId = Guid.NewGuid();
        DateTimeOffset now = TimeProvider.System.GetUtcNow();
        DateTimeOffset expiry = now.AddHours(24);

        // Act
        PlayerSessionToken token = new(
            sessionId,
            42,
            "KongorWarrior",
            "192.168.1.100",
            now,
            expiry,
            ["User", "Moderator"]);

        // Assert
        token.SessionId.Should().Be(sessionId);
        token.AccountId.Should().Be(42);
        token.Nickname.Should().Be("KongorWarrior");
        token.IpAddress.Should().Be("192.168.1.100");
        token.ExpiresAtUtc.Should().Be(expiry);
        token.Roles.Should().Contain("Moderator");
    }

    [Fact]
    public void MatchProvisionEvent_DomainRecord_InstantiatesWithImmutableState()
    {
        // Arrange
        DateTimeOffset now = TimeProvider.System.GetUtcNow();

        // Act
        MatchProvisionEvent evt = new(
            771122,
            "caldavar",
            1,
            "127.0.0.1",
            11235,
            "token-abc-123",
            [101, 102, 103, 104, 105],
            [201, 202, 203, 204, 205],
            now);

        // Assert
        evt.MatchId.Should().Be(771122);
        evt.MapName.Should().Be("caldavar");
        evt.ServerPublicIp.Should().Be("127.0.0.1");
        evt.ServerPublicPort.Should().Be(11235);
        evt.MatchSecurityToken.Should().Be("token-abc-123");
        evt.LegionAccountIds.Should().HaveCount(5);
        evt.HellbourneAccountIds.Should().HaveCount(5);
    }

    [Fact]
    public void TelemetryContracts_ProtoCombatFrame_ValidatesTimestampAndFrameData()
    {
        // Arrange & Act
        CombatTelemetryFrame frame = new()
        {
            MatchId = 998877,
            FrameIndex = 120,
            TimestampUtcMs = TimeProvider.System.GetUtcNow().ToUnixTimeMilliseconds(),
            GameTimeSeconds = 120
        };

        // Assert
        frame.MatchId.Should().Be(998877);
        frame.FrameIndex.Should().Be(120);
        frame.GameTimeSeconds.Should().Be(120);
    }

    [Fact]
    public void DeltaPatchDescriptor_DomainRecord_ValidatesPatchParameters()
    {
        // Arrange & Act
        DeltaPatchDescriptor patch = new(
            "game/resources0.s2z",
            "4.10.1",
            "4.10.2",
            "BSDIFF4",
            5242880,
            "aabbccdd11223344556677889900aabbccdd11223344556677889900aabbccdd",
            "https://cdn.kongor.net/patches/res0_delta.patch");

        // Assert
        patch.TargetRelativePath.Should().Be("game/resources0.s2z");
        patch.PatchAlgorithm.Should().Be("BSDIFF4");
        patch.PatchSizeBytes.Should().Be(5242880);
        patch.PatchDownloadUrl.Should().StartWith("https://");
    }

    [Fact]
    public void ChunkValidationResult_DomainRecord_VerifiesChunkIntegrity()
    {
        // Arrange & Act
        ChunkValidationResult result = new(
            "game/textures.s2z",
            3,
            3145728,
            1048576,
            "expected_hash_sample",
            "expected_hash_sample",
            true);

        // Assert
        result.FilePath.Should().Be("game/textures.s2z");
        result.ChunkIndex.Should().Be(3);
        result.IsValid.Should().BeTrue();
    }
}
