// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

// ============================================================================
// File: HeadlessAssetStripperServiceTests.cs
// System: ASPIRE.Prebaking.Tests - Headless Server Asset Stripping Tests
// Policy: Strict Explicit Typing (NO 'var')
// ============================================================================

using ASPIRE.Common.Prebaking.Generators;
using FluentAssertions;
using Xunit;

namespace ASPIRE.Prebaking.Tests;

public sealed class HeadlessAssetStripperServiceTests
{
    [Theory]
    [InlineData("game/heroes/pyromancer/sounds/attack1.wav", ".wav", true)]
    [InlineData("game/heroes/pyromancer/sounds/ability_01.mp3", ".mp3", true)]
    [InlineData("game/heroes/pyromancer/sounds/voice.ogg", ".ogg", true)]
    [InlineData("game/sounds/ambience.sfx", ".sfx", true)]
    [InlineData("game/heroes/pyromancer/texture.dds", ".dds", true)]
    [InlineData("game/ui/textures/button.tga", ".tga", true)]
    [InlineData("game/ui/textures/icon.png", ".png", true)]
    [InlineData("game/cinematics/intro.bik", ".bik", true)]
    [InlineData("game/heroes/pyromancer/hero.entity", ".entity", false)]
    [InlineData("game/heroes/pyromancer/ability_01.lua", ".lua", false)]
    [InlineData("game/heroes/pyromancer/model.mdf", ".mdf", false)]
    [InlineData("game/heroes/pyromancer/clip.clip", ".clip", false)]
    [InlineData("game/heroes/pyromancer/model.smdl", ".smdl", false)]
    [InlineData("game/config.cfg", ".cfg", false)]
    [InlineData("game/resources0.resources", ".resources", false)]
    [InlineData("game/maps/caldavar.s2z", ".s2z", false)]
    [InlineData("hon-x86_64-server", "", false)]
    [InlineData("libgame_shared.so", ".so", false)]
    public void HeadlessAssetStripperService_ShouldStripFile_FiltersAssetsCorrectly(
        string relativePath,
        string extension,
        bool expectedShouldStrip)
    {
        bool result = HeadlessAssetStripperService.ShouldStripFile(relativePath, extension);
        result.Should().Be(expectedShouldStrip);
    }

    [Fact]
    public async Task StripServerDistributionAsync_CreatesStrippedDirectoryWithPreservedFilesOnly()
    {
        // Arrange
        string tempSource = Path.Combine(Path.GetTempPath(), "source_srv_" + Guid.NewGuid().ToString("N"));
        string tempTarget = Path.Combine(Path.GetTempPath(), "target_srv_" + Guid.NewGuid().ToString("N"));

        try
        {
            Directory.CreateDirectory(Path.Combine(tempSource, "game", "heroes", "pyromancer"));
            Directory.CreateDirectory(Path.Combine(tempSource, "game", "sounds"));

            // Create simulation files
            await File.WriteAllTextAsync(Path.Combine(tempSource, "hon-x86_64-server"), "ELF_BINARY");
            await File.WriteAllTextAsync(Path.Combine(tempSource, "game", "heroes", "pyromancer", "hero.entity"), "<entity name=\"Hero_Pyromancer\"/>");
            await File.WriteAllTextAsync(Path.Combine(tempSource, "game", "heroes", "pyromancer", "hero.lua"), "function Init() end");
            await File.WriteAllTextAsync(Path.Combine(tempSource, "game", "heroes", "pyromancer", "clip.clip"), "CLIP_DATA");

            // Create media files to be stripped
            await File.WriteAllTextAsync(Path.Combine(tempSource, "game", "heroes", "pyromancer", "texture.dds"), "DDS_TEXTURE_BYTES_1234567890");
            await File.WriteAllTextAsync(Path.Combine(tempSource, "game", "sounds", "attack.wav"), "WAV_SOUND_BYTES_1234567890");

            // Act
            HeadlessStrippingResult result = await HeadlessAssetStripperService.StripServerDistributionAsync(tempSource, tempTarget);

            // Assert
            result.Success.Should().BeTrue();
            result.FilesPreserved.Should().Be(4);
            result.FilesStripped.Should().Be(2);
            result.TotalFilesScanned.Should().Be(6);

            // Verify files on target disk
            File.Exists(Path.Combine(tempTarget, "hon-x86_64-server")).Should().BeTrue();
            File.Exists(Path.Combine(tempTarget, "game", "heroes", "pyromancer", "hero.entity")).Should().BeTrue();
            File.Exists(Path.Combine(tempTarget, "game", "heroes", "pyromancer", "hero.lua")).Should().BeTrue();
            File.Exists(Path.Combine(tempTarget, "game", "heroes", "pyromancer", "clip.clip")).Should().BeTrue();

            // Verify stripped media files do NOT exist
            File.Exists(Path.Combine(tempTarget, "game", "heroes", "pyromancer", "texture.dds")).Should().BeFalse();
            File.Exists(Path.Combine(tempTarget, "game", "sounds", "attack.wav")).Should().BeFalse();
        }
        finally
        {
            if (Directory.Exists(tempSource)) Directory.Delete(tempSource, true);
            if (Directory.Exists(tempTarget)) Directory.Delete(tempTarget, true);
        }
    }
}
