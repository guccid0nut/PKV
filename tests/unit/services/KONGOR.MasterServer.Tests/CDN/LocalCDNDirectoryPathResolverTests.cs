// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

using FluentAssertions;
using KONGOR.MasterServer.Handlers.CDN;
using Xunit;

namespace KONGOR.MasterServer.Tests.CDN;

public sealed class LocalCDNDirectoryPathResolverTests
{
    [Fact]
    public void Resolve_WithRootAndServerTokens_ResolvesProperly()
    {
        // Arrange
        const string template = "{ROOT}/{SERVER}/CDN_Content";

        // Act
        string resolved = LocalCDNDirectoryPathResolver.Resolve(template, "KONGOR");

        // Assert
        resolved.Should().NotBeNullOrWhiteSpace();
        resolved.Should().EndWith(Path.Combine("KONGOR", "CDN_Content"));
        Path.IsPathFullyQualified(resolved).Should().BeTrue();
    }

    [Fact]
    public void Resolve_WithMasterServerToken_ExpandsServerName()
    {
        // Arrange
        const string template = "{ROOT}/{MASTERSERVER}/CDN_Content";

        // Act
        string resolved = LocalCDNDirectoryPathResolver.Resolve(template, "TEST_SERVER");

        // Assert
        resolved.Should().EndWith(Path.Combine("TEST_SERVER", "CDN_Content"));
    }

    [Fact]
    public void Resolve_WithTempToken_ExpandsSystemTempPath()
    {
        // Arrange
        const string template = "{TEMP}/CDN_Cache";

        // Act
        string resolved = LocalCDNDirectoryPathResolver.Resolve(template);

        // Assert
        resolved.Should().StartWith(Path.TrimEndingDirectorySeparator(Path.GetTempPath()));
        resolved.Should().EndWith("CDN_Cache");
    }

    [Fact]
    public void Resolve_WithFullyQualifiedPath_PreservesPath()
    {
        // Arrange
        string testPath = Path.Combine(Path.GetTempPath(), "KongorStaticCdn");

        // Act
        string resolved = LocalCDNDirectoryPathResolver.Resolve(testPath);

        // Assert
        resolved.Should().Be(Path.GetFullPath(testPath));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Resolve_WithNullOrWhitespace_ReturnsEmptyString(string? input)
    {
        // Act
        string resolved = LocalCDNDirectoryPathResolver.Resolve(input!);

        // Assert
        resolved.Should().BeEmpty();
    }
}
