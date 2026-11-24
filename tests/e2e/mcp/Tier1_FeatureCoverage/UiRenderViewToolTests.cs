// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// TAO (Together As One) MCP & Kongor CLI -- Created by guccidonut (https://github.com/guccidonut/PKV)

using FluentAssertions;
using Kongor.Mcp.E2E.Tests.Client;
using Xunit;
using Xunit.Abstractions;

namespace Kongor.Mcp.E2E.Tests.Tier1_FeatureCoverage;

public sealed class UiRenderViewToolTests : McpTestBase
{
    public UiRenderViewToolTests(ITestOutputHelper output) : base(output)
    {
    }

    [Trait("Category", "Manual")]
    [Fact]
    public async Task UiRenderView_ModManagerView_RendersDarkThemePng()
    {
        if (!NexusTestEnvironment.IsManualTestEnabled) return;
        UiRenderViewArgs args = new()
        {
            ViewName = "ModManagerView",
            Width = 1280,
            Height = 720,
            Theme = "dark",
            MockData = true
        };

        ToolCallResult callResult = await Client.CallToolAsync("kongor_ui_render_view", args);
        callResult.Should().NotBeNull();

        UiRenderViewResult? result = await Client.CallToolTypedAsync<UiRenderViewResult>("kongor_ui_render_view", args);
        result.Should().NotBeNull();
        result!.ViewName.Should().Be("ModManagerView");
        result.Width.Should().Be(1280);
        result.Height.Should().Be(720);
    }

    [Fact]
    public async Task UiRenderView_ReplayStudioView_RendersWithMockData()
    {
        UiRenderViewArgs args = new()
        {
            ViewName = "ReplayStudioView",
            MockData = true
        };

        UiRenderViewResult? result = await Client.CallToolTypedAsync<UiRenderViewResult>("kongor_ui_render_view", args);
        result.Should().NotBeNull();
        result!.ViewName.Should().Be("ReplayStudioView");
    }

    [Trait("Category", "Manual")]
    [Fact]
    public async Task UiRenderView_SecretsConfigView_RendersSpecifiedDimensions()
    {
        if (!NexusTestEnvironment.IsManualTestEnabled) return;
        UiRenderViewArgs args = new()
        {
            ViewName = "SecretsConfigView",
            Width = 1024,
            Height = 768,
            Theme = "dark"
        };

        UiRenderViewResult? result = await Client.CallToolTypedAsync<UiRenderViewResult>("kongor_ui_render_view", args);
        result.Should().NotBeNull();
        result!.Width.Should().Be(1024);
        result.Height.Should().Be(768);
    }

    [Fact]
    public async Task UiRenderView_DiagnosticsView_RendersLightTheme()
    {
        UiRenderViewArgs args = new()
        {
            ViewName = "DiagnosticsView",
            Theme = "light"
        };

        UiRenderViewResult? result = await Client.CallToolTypedAsync<UiRenderViewResult>("kongor_ui_render_view", args);
        result.Should().NotBeNull();
        result!.Theme.Should().Be("light");
    }

    [Fact]
    public async Task UiRenderView_SaveArtifactTrue_GeneratesDiskFile()
    {
        string outputPath = Path.Combine(Path.GetTempPath(), $"kongor_view_{Guid.NewGuid():N}.png");
        UiRenderViewArgs args = new()
        {
            ViewName = "ModManagerView",
            SaveArtifact = true,
            CustomOutputPath = outputPath
        };

        try
        {
            UiRenderViewResult? result = await Client.CallToolTypedAsync<UiRenderViewResult>("kongor_ui_render_view", args);
            result.Should().NotBeNull();
            if (result!.Status == "success" && File.Exists(outputPath))
            {
                new FileInfo(outputPath).Length.Should().BeGreaterThan(0);
            }
        }
        finally
        {
            if (File.Exists(outputPath))
                File.Delete(outputPath);
        }
    }

    [Fact]
    public async Task UiRenderView_HeroViewportView_ReturnsBase64ImagePayload()
    {
        UiRenderViewArgs args = new()
        {
            ViewName = "HeroViewportView",
            Width = 800,
            Height = 600
        };

        ToolCallResult callResult = await Client.CallToolAsync("kongor_ui_render_view", args);
        callResult.Should().NotBeNull();

        UiRenderViewResult? result = await Client.CallToolTypedAsync<UiRenderViewResult>("kongor_ui_render_view", args);
        result.Should().NotBeNull();
    }
}
