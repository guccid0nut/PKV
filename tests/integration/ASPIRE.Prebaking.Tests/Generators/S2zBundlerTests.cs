// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

using System.Text;
using ASPIRE.Common.Prebaking.Generators;
using FluentAssertions;
using Kongor.Modding.Core.Packaging;
using Kongor.Modding.Vfs;
using Xunit;

namespace ASPIRE.Prebaking.Tests.Generators;

public sealed class S2zBundlerTests
{
    [Fact]
    public async Task BundleToS2zAsync_CreatesValidS2zArchive_ReadableByS2zArchiveReader()
    {
        // Arrange
        string tempDir = Path.Combine(Path.GetTempPath(), $"s2z_test_{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        string s2zPath = Path.Combine(tempDir, "test_prebaked.s2z");

        Dictionary<string, byte[]> files = new(StringComparer.OrdinalIgnoreCase)
        {
            ["ui/scripts/newui/hero_definitions_cache.lua"] = Encoding.UTF8.GetBytes("-- test lua cache"),
            ["prebaked_assets/entity_cache.bin"] = [0x4B, 0x4E, 0x45, 0x43, 0x01, 0x00]
        };

        try
        {
            // Act
            BundlingResult result = await S2zBundlerService.BundleToS2zAsync(s2zPath, files);

            // Assert
            result.Success.Should().BeTrue();
            result.TotalFiles.Should().Be(2);
            File.Exists(s2zPath).Should().BeTrue();

            using S2zArchiveReader reader = S2zArchiveReader.Open(s2zPath);
            reader.HasEntry("ui/scripts/newui/hero_definitions_cache.lua").Should().BeTrue();
            reader.HasEntry("prebaked_assets/entity_cache.bin").Should().BeTrue();

            byte[] luaBytes = await reader.ReadEntryBytesAsync("ui/scripts/newui/hero_definitions_cache.lua");
            Encoding.UTF8.GetString(luaBytes).Should().Be("-- test lua cache");
        }
        finally
        {
            if (Directory.Exists(tempDir)) Directory.Delete(tempDir, true);
        }
    }

    [Fact]
    public async Task StageLooseFilesAsync_CreatesOverrideDirectory_WithTriggerAndJournal()
    {
        // Arrange
        string tempGameDir = Path.Combine(Path.GetTempPath(), $"game_test_{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempGameDir);

        Dictionary<string, byte[]> files = new(StringComparer.OrdinalIgnoreCase)
        {
            ["ui/scripts/newui/hero_definitions_cache.lua"] = Encoding.UTF8.GetBytes("-- test lua cache"),
            ["prebaked_assets/entity_cache.bin"] = [0x01, 0x02, 0x03]
        };

        try
        {
            // Act
            StagingResult result = await VfsOverrideStagingService.StageLooseFilesAsync(tempGameDir, files);

            // Assert
            result.Success.Should().BeTrue();
            string luaTarget = Path.Combine(tempGameDir, "override", "ui", "scripts", "newui",
                "hero_definitions_cache.lua");
            File.Exists(luaTarget).Should().BeTrue();

            string triggerFile = Path.Combine(tempGameDir, "override", ".hotreload.trigger");
            File.Exists(triggerFile).Should().BeTrue();

            string journalFile = Path.Combine(tempGameDir, "rollback_journal.json");
            File.Exists(journalFile).Should().BeTrue();

            // Test Rollback
            bool rolledBack = await RollbackJournal.ExecuteRollbackAsync(tempGameDir);
            rolledBack.Should().BeTrue();
            File.Exists(luaTarget).Should().BeFalse();
        }
        finally
        {
            if (Directory.Exists(tempGameDir)) Directory.Delete(tempGameDir, true);
        }
    }
}
