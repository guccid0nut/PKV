// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

using System.IO.Compression;
using Moq;
using NEXUS.ControlPanel.GUI.Services;
using NEXUS.ControlPanel.GUI.Services.Models;

namespace ASPIRE.Tests.NEXUS.ControlPanel.GUI;

public sealed class GameClientAssetServiceTests
{
    [Fact]
    public async Task EnsureAssetsExtractedAsync_WithInvalidPath_ReturnsFalse()
    {
        // Arrange
        Mock<ISecretsManagerService> mockSecrets = new();
        mockSecrets.Setup(s => s.LoadSecretsAsync(CancellationToken.None)).ReturnsAsync(new NexusSecrets
        {
            GameClientPath = @"C:\NonExistent\Directory\Path_12345"
        });

        GameClientAssetService service = new(mockSecrets.Object);

        // Act
        bool result = await service.EnsureAssetsExtractedAsync(@"C:\NonExistent\Directory\Path_12345");

        // Assert
        Assert.False(result);
    }

    [Fact]
    public async Task EnsureAssetsExtractedAsync_WithValidS2zPackage_ExtractsVoiceLines()
    {
        // Arrange
        string tempDir = Path.Combine(Path.GetTempPath(), "NEXUS_Test_HoNClient_" + Guid.NewGuid().ToString("N"));
        string gameDir = Path.Combine(tempDir, "game");
        Directory.CreateDirectory(gameDir);

        string s2zPath = Path.Combine(gameDir, "resources1.s2z");
        await using (ZipArchive zip = await ZipFile.OpenAsync(s2zPath, ZipArchiveMode.Create))
        {
            ZipArchiveEntry entry1 = zip.CreateEntry("heroes/empath/sounds/voice/select_1.ogg");
            await using (Stream entryStream = await entry1.OpenAsync())
            await using (StreamWriter writer = new(entryStream))
            {
                await writer.WriteAsync("OGG_TEST_AUDIO_DATA_SELECT_1");
            }

            ZipArchiveEntry entry2 = zip.CreateEntry("heroes/empath/sounds/voice/select_flavour_1.ogg");
            await using (Stream entryStream = await entry2.OpenAsync())
            await using (StreamWriter writer = new(entryStream))
            {
                await writer.WriteAsync("OGG_TEST_AUDIO_DATA_FLAVOUR_1");
            }
        }

        Mock<ISecretsManagerService> mockSecrets = new();
        mockSecrets.Setup(s => s.LoadSecretsAsync(CancellationToken.None))
            .ReturnsAsync(new NexusSecrets { GameClientPath = tempDir });

        GameClientAssetService service = new(mockSecrets.Object);

        try
        {
            // Act
            bool result = await service.EnsureAssetsExtractedAsync(tempDir);

            // Assert
            Assert.True(result);
            string baseDir = AppContext.BaseDirectory;
            string extracted1 = Path.Combine(baseDir, "Assets", "sounds", "empath", "select_1.ogg");
            string extracted2 = Path.Combine(baseDir, "Assets", "sounds", "empath", "select_flavour_1.ogg");

            Assert.True(File.Exists(extracted1));
            Assert.True(File.Exists(extracted2));
        }
        finally
        {
            if (Directory.Exists(tempDir)) Directory.Delete(tempDir, true);
        }
    }
}
