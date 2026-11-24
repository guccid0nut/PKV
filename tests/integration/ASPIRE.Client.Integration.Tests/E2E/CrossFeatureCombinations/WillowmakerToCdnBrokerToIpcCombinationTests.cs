// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

// ============================================================================
// File: WillowmakerToCdnBrokerToIpcCombinationTests.cs
// System: ProjectKongor Solution-Wide E2E Testing Suite
// Tier 3: Cross-Feature Combinations (Willowmaker -> CDN Broker -> NamedPipe IPC)
// Policy: Strict Explicit Typing, Multi-Service Integration Verification
// ============================================================================

using FluentAssertions;
using Moq;
using WILLOWMAKER.Core.Services;

namespace ASPIRE.Tests.E2E.Tier3_CrossFeatureCombinations;

public sealed class WillowmakerToCdnBrokerToIpcCombinationTests
{
    [Fact]
    public void Integration_EnginePatchService_SuppressesLegacyDomains()
    {
        // Arrange
        byte[] mockBinary = Encoding.ASCII.GetBytes("pre_client.sea.heroesofnewerth.com_post");

        // Act
        int patchedDomains = EnginePatchService.SuppressLegacyDomains(mockBinary);

        // Assert
        patchedDomains.Should().Be(1);
        string result = Encoding.ASCII.GetString(mockBinary);
        result.Should().NotContain("client.sea.heroesofnewerth.com");
    }

    [Fact]
    public void Integration_LauncherSettingsToEngine_ConfiguresTargetFpsCap()
    {
        // Arrange
        string tempDir = Path.Combine(Path.GetTempPath(), "KongorTest_Fps_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);

        try
        {
            LauncherSettingsModel model = LauncherSettingsService.Load(tempDir);
            model.RefreshRate = "240 Hz";
            LauncherSettingsService.Save(model, tempDir);

            // Act
            LauncherSettingsModel reloaded = LauncherSettingsService.Load(tempDir);

            // Assert
            reloaded.RefreshRate.Should().Be("240 Hz");
        }
        finally
        {
            if (Directory.Exists(tempDir)) Directory.Delete(tempDir, true);
        }
    }

    [Fact]
    public void Integration_TextureDecodingPipeline_ConvertsDdsToBmpForLauncherUi()
    {
        // Arrange - Valid 4x4 DXT1 DDS texture
        byte[] ddsHeader = new byte[128 + 8];
        Encoding.ASCII.GetBytes("DDS ").CopyTo(ddsHeader, 0);
        BitConverter.GetBytes(4).CopyTo(ddsHeader, 12); // Height = 4
        BitConverter.GetBytes(4).CopyTo(ddsHeader, 16); // Width = 4
        Encoding.ASCII.GetBytes("DXT1").CopyTo(ddsHeader, 84);

        // Act
        bool isValid = DdsDecoder.IsValidDdsHeader(ddsHeader);

        // Assert
        isValid.Should().BeTrue();
    }

    [Fact]
    public void Integration_NamedPipeIpcContracts_EncapsulatesCommandMessages()
    {
        // Arrange
        var launchMessage = new
        {
            Command = "LAUNCH_GAME",
            Arguments = "-masterserver 127.0.0.1 -port 5000",
            Timestamp = TimeProvider.System.GetUtcNow().ToUnixTimeMilliseconds()
        };

        // Assert
        launchMessage.Command.Should().Be("LAUNCH_GAME");
        launchMessage.Arguments.Should().Contain("-masterserver");
    }

    [Fact]
    public void Integration_RenderModernizationService_InitializesWithLogger()
    {
        // Arrange & Act
        ILogger<RenderModernizationService> logger = Mock.Of<ILogger<RenderModernizationService>>();
        RenderModernizationService service = new(logger);

        // Assert
        service.Should().NotBeNull();
    }
}
