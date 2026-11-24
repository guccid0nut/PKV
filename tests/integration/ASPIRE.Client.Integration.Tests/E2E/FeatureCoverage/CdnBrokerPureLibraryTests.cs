// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

// ============================================================================
// File: Feature05_CdnBrokerPureLibraryTests.cs
// System: ProjectKongor Solution-Wide E2E Testing Suite
// Tier 1: Feature Coverage (Feature 5: NEXUS.CdnBroker Pure Library Refactoring)
// Policy: Strict Explicit Typing, Opaque-Box Validation (>= 5 tests)
// ============================================================================

using FluentAssertions;
using WILLOWMAKER.Core.Services;

namespace ASPIRE.Tests.E2E.Tier1_FeatureCoverage;

public sealed class CdnBrokerPureLibraryTests
{
    [Fact]
    public void DeploymentManifest_ApplicationConstants_ExposesConsistentNames()
    {
        // Assert
        DeploymentManifest.ApplicationName.Should().Be("WILLOWMAKER");
        DeploymentManifest.LogFileName.Should().Be("WILLOWMAKER.log");
        DeploymentManifest.UpdateArchiveFileName.Should().Be("WILLOWMAKER.update.zip");
    }

    [Fact]
    public void DeploymentManifest_ExecutableName_MatchesCurrentPlatform()
    {
        // Act
        string exeName = DeploymentManifest.ApplicationExecutableFileName;

        // Assert
        if (OperatingSystem.IsWindows())
            exeName.Should().Be("WILLOWMAKER.exe");
        else
            exeName.Should().Be("WILLOWMAKER");
    }

    [Fact]
    public void DeploymentManifest_NativeLibraries_ListsCoreGraphicsAndFontLayers()
    {
        // Act
        string[] libs = DeploymentManifest.NativeLibrariesFileNames;

        // Assert
        libs.Should().NotBeNullOrEmpty();
        libs.Should().Contain(lib => lib.Contains("SkiaSharp") || lib.Contains("HarfBuzzSharp"));
    }

    [Fact]
    public void DdsDecoder_InvalidStream_ReturnsNullSafelyWithoutThrowing()
    {
        // Arrange
        byte[] invalidData = [0x00, 0x01, 0x02, 0x03, 0x04];
        using MemoryStream ms = new(invalidData);

        // Act
        byte[]? result = DdsDecoder.DecodeDdsStreamToBmpBytes(ms);

        // Assert
        result.Should().BeNull("Invalid DDS header must safely yield null without unhandled exceptions");
    }

    [Fact]
    public void DdsDecoder_CorruptMagicHeader_RejectsNonDdsSignatures()
    {
        // Arrange - 128 bytes with invalid magic bytes
        byte[] fakeHeader = new byte[128];
        Encoding.ASCII.GetBytes("PNG ").CopyTo(fakeHeader, 0);
        using MemoryStream ms = new(fakeHeader);

        // Act
        byte[]? result = DdsDecoder.DecodeDdsStreamToBmpBytes(ms);

        // Assert
        result.Should().BeNull();
    }

    [Fact]
    public void DeploymentManifest_HeroesOfNewerthExecutable_ReturnsPlatformName()
    {
        // Act
        string gameExe = DeploymentManifest.HeroesOfNewerthExecutableFileName;

        // Assert
        if (OperatingSystem.IsWindows())
            gameExe.Should().Be("hon_x64.exe");
        else if (OperatingSystem.IsLinux()) gameExe.Should().Be("hon-x86_64");
    }
}
