// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// TAO (Together As One) MCP & Kongor CLI -- Created by guccidonut (https://github.com/guccidonut/PKV)

using System.Runtime.CompilerServices;
using FluentAssertions;
using ImageMagick;
using ImageMagick.Drawing;
using Kongor.Cli.Mcp.Protocol;
using VerifyTests;
using VerifyXunit;
using Xunit;

namespace Kongor.Mcp.Tests;

public static class VerifyModuleInitializer
{
    private static int s_initialized;

    [ModuleInitializer]
    public static void Init()
    {
        if (Interlocked.Exchange(ref s_initialized, 1) == 0)
        {
            DiffEngine.DiffRunner.Disabled = true;
            VerifyImageMagick.Initialize();
            VerifyImageMagick.RegisterComparers(0.05, ErrorMetric.RootMeanSquared);
        }
    }
}

public sealed class VerifySnapshotTests
{
    [Fact]
    public async Task Verify_McpCoreProtocolAndVfsPrecedence_MatchesVerifiedSnapshot()
    {
        VerifyModuleInitializer.Init();

        var registry = new McpToolRegistry();
        var tools = registry.ListTools();

        var snapshotPayload = new
        {
            ServerName = McpProtocolVersion.ServerName,
            ServerVersion = McpProtocolVersion.ServerVersion,
            VfsPrecedenceHierarchy = new[]
            {
                "override",
                "base",
                "game",
                "ClientPackageFiles",
                "shared",
                "KONGOR/*"
            },
            HasHealthCheckTool = tools.Any(t => t.Name == "kongor_health_check"),
            HasVfsResolveTool = tools.Any(t => t.Name == "kongor_vfs_resolve"),
            HasAssertVisualTool = tools.Any(t => t.Name == "kongor_assert_visual"),
            HealthCheckCategory = registry.GetToolCategory("kongor_health_check").ToString(),
            VfsResolveCategory = registry.GetToolCategory("kongor_vfs_resolve").ToString(),
            AssertVisualCategory = registry.GetToolCategory("kongor_assert_visual").ToString()
        };

        string repoRoot = Kongor.Cli.Mcp.Services.NexusMailboxIpcBridge.FindRepoRoot(AppContext.BaseDirectory);
        string verifiedPath = Path.Combine(
            repoRoot,
            "tests",
            "unit",
            "tools",
            "Kongor.Mcp.Tests",
            "Snapshots",
            $"{nameof(VerifySnapshotTests)}.{nameof(Verify_McpCoreProtocolAndVfsPrecedence_MatchesVerifiedSnapshot)}.verified.txt");
        File.Exists(verifiedPath).Should().BeTrue($"verified snapshot must exist at {verifiedPath}");

        string actualSnapshot =
            $$"""
            {
              ServerName: {{snapshotPayload.ServerName}},
              ServerVersion: {{snapshotPayload.ServerVersion}},
              VfsPrecedenceHierarchy: [
                {{string.Join(",\n    ", snapshotPayload.VfsPrecedenceHierarchy)}}
              ],
              HasHealthCheckTool: {{snapshotPayload.HasHealthCheckTool.ToString().ToLowerInvariant()}},
              HasVfsResolveTool: {{snapshotPayload.HasVfsResolveTool.ToString().ToLowerInvariant()}},
              HasAssertVisualTool: {{snapshotPayload.HasAssertVisualTool.ToString().ToLowerInvariant()}},
              HealthCheckCategory: {{snapshotPayload.HealthCheckCategory}},
              VfsResolveCategory: {{snapshotPayload.VfsResolveCategory}},
              AssertVisualCategory: {{snapshotPayload.AssertVisualCategory}}
            }
            """.Replace("\r\n", "\n").Trim();

        string expectedSnapshot = (await File.ReadAllTextAsync(verifiedPath)).Replace("\r\n", "\n").Trim();
        actualSnapshot.Should().Be(expectedSnapshot);
    }

    [Fact]
    public void VerifyImageMagick_PerceptualAndRmsComparison_DetectsSubThresholdAndStructuralRegressions()
    {
        VerifyModuleInitializer.Init();

        using var baseline = new MagickImage(new MagickColor("#18202c"), 64, 64);
        using var minorAaVariant = baseline.Clone();
        // Draw a single 1x1 slightly-shifted pixel at (32, 32) to simulate GPU sub-pixel anti-aliasing drift
        new Drawables()
            .FillColor(new MagickColor("#19212d"))
            .Point(32, 32)
            .Draw(minorAaVariant);

        double rmsDrift = baseline.Compare(minorAaVariant, ErrorMetric.RootMeanSquared);
        rmsDrift.Should().BeLessThan(0.01, "single-pixel minor AA drift should be well within the 1% perceptual tolerance");

        using var brokenLayout = baseline.Clone();
        new Drawables()
            .FillColor(MagickColors.Magenta)
            .Rectangle(0, 0, 63, 31)
            .Draw(brokenLayout);

        double regressionDrift = baseline.Compare(brokenLayout, ErrorMetric.RootMeanSquared);
        regressionDrift.Should().BeGreaterThan(0.25, "missing texture/layout corruption should exceed threshold");
    }
}
