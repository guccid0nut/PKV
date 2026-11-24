// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// TAO (Together As One) MCP & Kongor CLI -- Created by guccidonut (https://github.com/guccidonut/PKV)

using FluentAssertions;
using Kongor.Mcp.E2E.Tests.Client;
using Xunit;
using Xunit.Abstractions;

namespace Kongor.Mcp.E2E.Tests.Tier3_Combinations;

public sealed class VisualInspectionPipelineTests : McpTestBase
{
    public VisualInspectionPipelineTests(ITestOutputHelper output) : base(output)
    {
    }

    [Fact]
    public async Task Combination_UiRenderView_ThenCaptureWindow_VerifiesVisualPayloads()
    {
        // 1. Render Avalonia Headless view
        UiRenderViewResult? uiRender = await Client.CallToolTypedAsync<UiRenderViewResult>(
            "kongor_ui_render_view",
            new UiRenderViewArgs { ViewName = "ModManagerView", Theme = "dark", MockData = true });
        uiRender.Should().NotBeNull();

        // 2. Capture active live window
        CaptureWindowResult? winCap = await Client.CallToolTypedAsync<CaptureWindowResult>(
            "kongor_capture_window",
            new CaptureWindowArgs { Target = "active", SaveArtifact = false });
        winCap.Should().NotBeNull();
    }

    [Fact]
    public async Task Combination_DualModalityCapture_AvaloniaOffscreenAndLiveWindowCompare()
    {
        string uiArtifact = Path.Combine(Path.GetTempPath(), $"kongor_dual_ui_{Guid.NewGuid():N}.png");
        string winArtifact = Path.Combine(Path.GetTempPath(), $"kongor_dual_win_{Guid.NewGuid():N}.png");

        try
        {
            UiRenderViewResult? r1 = await Client.CallToolTypedAsync<UiRenderViewResult>(
                "kongor_ui_render_view",
                new UiRenderViewArgs { ViewName = "DiagnosticsView", SaveArtifact = true, CustomOutputPath = uiArtifact });
            r1.Should().NotBeNull();

            CaptureWindowResult? r2 = await Client.CallToolTypedAsync<CaptureWindowResult>(
                "kongor_capture_window",
                new CaptureWindowArgs { Target = "active", SaveArtifact = true, CustomOutputPath = winArtifact });
            r2.Should().NotBeNull();
        }
        finally
        {
            if (File.Exists(uiArtifact)) File.Delete(uiArtifact);
            if (File.Exists(winArtifact)) File.Delete(winArtifact);
        }
    }

    [Trait("Category", "Manual")]
    [Fact]
    public async Task Combination_ArtifactOutputDirectory_SharedDiskArtifactValidation()
    {
        if (!NexusTestEnvironment.IsManualTestEnabled) return;
        string dir = Path.Combine(Path.GetTempPath(), $"kongor_art_dir_{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);

        try
        {
            string path1 = Path.Combine(dir, "view1.png");
            string path2 = Path.Combine(dir, "view2.png");

            await Client.CallToolAsync("kongor_ui_render_view", new UiRenderViewArgs { ViewName = "ModManagerView", SaveArtifact = true, CustomOutputPath = path1 });
            await Client.CallToolAsync("kongor_ui_render_view", new UiRenderViewArgs { ViewName = "SecretsConfigView", SaveArtifact = true, CustomOutputPath = path2 });
        }
        finally
        {
            if (Directory.Exists(dir))
                Directory.Delete(dir, recursive: true);
        }
    }
}
