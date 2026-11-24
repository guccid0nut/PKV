// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// TAO (Together As One) MCP & Kongor CLI -- Created by guccidonut (https://github.com/guccidonut/PKV)

using FluentAssertions;
using Kongor.Mcp.E2E.Tests.Client;
using Xunit;
using Xunit.Abstractions;

namespace Kongor.Mcp.E2E.Tests.Tier1_FeatureCoverage;

public sealed class WindowCaptureToolTests : McpTestBase
{
    public WindowCaptureToolTests(ITestOutputHelper output) : base(output)
    {
    }

    [Fact]
    public async Task CaptureWindow_TargetActive_ReturnsValidWindowCaptureResult()
    {
        CaptureWindowArgs args = new()
        {
            Target = "active",
            SaveArtifact = false
        };

        ToolCallResult callResult = await Client.CallToolAsync("kongor_capture_window", args);
        callResult.Should().NotBeNull();

        CaptureWindowResult? result = await Client.CallToolTypedAsync<CaptureWindowResult>("kongor_capture_window", args);
        result.Should().NotBeNull();
        result!.Target.Should().Be("active");
    }

    [Fact]
    public async Task CaptureWindow_TargetNexusControlPanel_AttemptsCapture()
    {
        CaptureWindowArgs args = new()
        {
            Target = "NEXUS Control Panel",
            CaptureMethod = "auto",
            SaveArtifact = false
        };

        CaptureWindowResult? result = await Client.CallToolTypedAsync<CaptureWindowResult>("kongor_capture_window", args);
        result.Should().NotBeNull();
        result!.Target.Should().Be("NEXUS Control Panel");
    }

    [Fact]
    public async Task CaptureWindow_SaveArtifactTrue_GeneratesDiskArtifact()
    {
        string testArtifactPath = Path.Combine(Path.GetTempPath(), $"kongor_test_cap_{Guid.NewGuid():N}.png");
        CaptureWindowArgs args = new()
        {
            Target = "active",
            SaveArtifact = true,
            CustomOutputPath = testArtifactPath
        };

        try
        {
            CaptureWindowResult? result = await Client.CallToolTypedAsync<CaptureWindowResult>("kongor_capture_window", args);
            result.Should().NotBeNull();
            if (result!.Status == "success" && File.Exists(testArtifactPath))
            {
                new FileInfo(testArtifactPath).Length.Should().BeGreaterThan(0);
            }
        }
        finally
        {
            if (File.Exists(testArtifactPath))
                File.Delete(testArtifactPath);
        }
    }

    [Fact]
    public async Task CaptureWindow_CustomOutputPath_WritesToSpecifiedPath()
    {
        string customPath = Path.Combine(Path.GetTempPath(), $"kongor_custom_{Guid.NewGuid():N}.png");
        CaptureWindowArgs args = new()
        {
            Target = "active",
            SaveArtifact = true,
            CustomOutputPath = customPath
        };

        try
        {
            CaptureWindowResult? result = await Client.CallToolTypedAsync<CaptureWindowResult>("kongor_capture_window", args);
            result.Should().NotBeNull();
        }
        finally
        {
            if (File.Exists(customPath))
                File.Delete(customPath);
        }
    }

    [Fact]
    public async Task CaptureWindow_CaptureMethodPrintWindow_Succeeds()
    {
        CaptureWindowArgs args = new()
        {
            Target = "active",
            CaptureMethod = "print_window",
            SaveArtifact = false
        };

        CaptureWindowResult? result = await Client.CallToolTypedAsync<CaptureWindowResult>("kongor_capture_window", args);
        result.Should().NotBeNull();
    }

    [Fact]
    public async Task CaptureWindow_CaptureMethodBitBlt_Succeeds()
    {
        CaptureWindowArgs args = new()
        {
            Target = "active",
            CaptureMethod = "window_dc",
            SaveArtifact = false
        };

        CaptureWindowResult? result = await Client.CallToolTypedAsync<CaptureWindowResult>("kongor_capture_window", args);
        result.Should().NotBeNull();
    }
}
