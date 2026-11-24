// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// TAO (Together As One) MCP & Kongor CLI -- Created by guccidonut (https://github.com/guccidonut/PKV)

// ============================================================================
// File: NativeAotProfileValidationTests.cs
// System: Kongor MCP / Tooling Unit Tests - NativeAOT Publish Profile Governance
// ============================================================================

using System.IO;
using System.Xml.Linq;
using Xunit;

namespace Kongor.Mcp.Tests;

public sealed class NativeAotProfileValidationTests
{
    private static string FindRepositoryRoot()
    {
        string? current = AppContext.BaseDirectory;
        while (!string.IsNullOrEmpty(current))
        {
            if (File.Exists(Path.Combine(current, "ProjectKongor.slnx")) ||
                File.Exists(Path.Combine(current, "ProjectKongor.sln")) ||
                Directory.Exists(Path.Combine(current, ".git")))
            {
                return current;
            }

            current = Directory.GetParent(current)?.FullName;
        }

        throw new DirectoryNotFoundException("Could not locate repository root directory.");
    }

    [Theory]
    [InlineData("src/apps/compel/Properties/PublishProfiles/Windows.x64.NativeAOT.pubxml", "win-x64", "net11.0")]
    [InlineData("src/apps/compel/Properties/PublishProfiles/Linux.x64.NativeAOT.pubxml", "linux-x64", "net11.0")]
    [InlineData("src/tools/kongor-cli/Properties/PublishProfiles/Windows.x64.NativeAOT.pubxml", "win-x64", "net11.0")]
    [InlineData("src/tools/kongor-cli/Properties/PublishProfiles/Linux.x64.NativeAOT.pubxml", "linux-x64", "net11.0")]
    public void NativeAotPublishProfile_IsWellFormedAndEnforcesNet11(string relativePath, string expectedRid, string expectedTfm)
    {
        // Arrange
        string repoRoot = FindRepositoryRoot();
        string fullPath = Path.Combine(repoRoot, relativePath);

        // Assert existence
        Assert.True(File.Exists(fullPath), $"Publish profile does not exist at {fullPath}");

        // Act - Parse XML
        XDocument doc = XDocument.Load(fullPath);
        XElement? root = doc.Root;
        Assert.NotNull(root);
        Assert.Equal("Project", root.Name.LocalName);

        // Verify TargetFramework
        string? tfm = root.Descendants("TargetFramework").FirstOrDefault()?.Value;
        Assert.Equal(expectedTfm, tfm);

        // Verify RuntimeIdentifier
        string? rid = root.Descendants("RuntimeIdentifier").FirstOrDefault()?.Value;
        Assert.Equal(expectedRid, rid);

        // Verify PublishAot is true
        string? publishAot = root.Descendants("PublishAot").FirstOrDefault()?.Value;
        Assert.Equal("true", publishAot, ignoreCase: true);

        // Verify SelfContained is true
        string? selfContained = root.Descendants("SelfContained").FirstOrDefault()?.Value;
        Assert.Equal("true", selfContained, ignoreCase: true);
    }
}
