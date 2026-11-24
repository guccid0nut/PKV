// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// TAO (Together As One) MCP & Kongor CLI -- Created by guccidonut (https://github.com/guccidonut/PKV)

// ============================================================================
// File: CanonicalVfsMountOrderTests.cs
// System: Kongor.Mcp.Tests - Phase 2 VFS Canonical Mount Hierarchy
// ============================================================================

using FluentAssertions;
using Kongor.Cli.Mcp.Services;
using Xunit;

namespace Kongor.Mcp.Tests;

public sealed class CanonicalVfsMountOrderTests
{
    [Fact]
    public void ClientModMountBuilder_BuildClientLaunchArguments_InjectsCanonical6TierVfsOrder()
    {
        // Arrange
        ClientModMountBuilder builder = new();

        // Act
        IReadOnlyList<string> args = builder.BuildClientLaunchArguments(
            gameDir: "C:\\Games\\HoN",
            mode: "practice",
            connectAddress: null,
            masterServer: "127.0.0.1:55555",
            resolution: "1920x1080",
            fullscreen: false,
            maxFps: 1000,
            customNickname: "TestPlayer",
            windowMode: "headless");

        // Assert
        string expectedModParam = ClientModMountBuilder.GetCanonicalModString();
        args.Should().Contain(expectedModParam, "Canonical VFS priority must mount base and game before overlays, with override at the end.");
    }

    [Fact]
    public void ClientModMountBuilder_GetCanonicalMountHierarchy_ReturnsStrictHierarchy()
    {
        // Arrange
        ClientModMountBuilder builder = new();

        // Act
        IReadOnlyList<string> hierarchy = builder.GetCanonicalMountHierarchy();

        // Assert
        hierarchy.Should().NotBeNull();
        hierarchy.Should().HaveCount(8);
        hierarchy[0].Should().Be("base", "Tier 0 (Engine Base Archive)");
        hierarchy[1].Should().Be("game", "Tier 1 (Base Asset Packages)");
        hierarchy[2].Should().Be("ClientPackageFiles", "Tier 2 (Client Hot Packages)");
        hierarchy[3].Should().Be("../shared", "Tier 3 (Shared Core VFS)");
        hierarchy[4].Should().Be("KONGOR/configuration", "Tier 4 (Platform Configuration)");
        hierarchy[5].Should().Be("KONGOR/updates", "Tier 5 (Platform Updates)");
        hierarchy[6].Should().Be("KONGOR/extensions", "Tier 6 (Platform Extensions)");
        hierarchy[7].Should().Be("override", "Tier 7 (Highest Priority Overrides - Evaluated FIRST by K2 reverse iterator)");
    }

    [Fact]
    public void Exhaustigen_EnumeratesAllMountAndBackupRotationStatesWithoutMisses()
    {
        int verifiedCount = ClientModMountBuilder.VerifyExhaustiveMountAndBackupStates();
        // 8 canonical mount tiers * 6 VfsBackupRotationTier values * 2 boolean override states = 96 combinations
        verifiedCount.Should().Be(96);

        Exhaustigen gen = new();
        HashSet<(int, bool, VfsBackupRotationTier)> visited = [];
        while (!gen.Done())
        {
            int tier = gen.Index(4);
            bool flag = gen.Bool();
            VfsBackupRotationTier backup = gen.EnumValue<VfsBackupRotationTier>();
            visited.Add((tier, flag, backup)).Should().BeTrue("Exhaustigen must visit each decision tuple exactly once");
        }

        visited.Should().HaveCount(4 * 2 * 6);
    }
}

