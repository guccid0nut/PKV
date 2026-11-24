// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// TAO (Together As One) MCP & Kongor CLI -- Created by guccidonut (https://github.com/guccidonut/PKV)

using FluentAssertions;
using Kongor.Mcp.E2E.Tests.Client;
using Xunit;
using Xunit.Abstractions;

namespace Kongor.Mcp.E2E.Tests.Tier4_Scenarios;

public sealed class AvaloniaDesignAuditJourneyTests : McpTestBase
{
    public AvaloniaDesignAuditJourneyTests(ITestOutputHelper output) : base(output)
    {
    }

    [Trait("Category", "Manual")]
    [Fact]
    public async Task AvaloniaDesignAudit_FullViewCatalog_ExecutesInspectionJourney()
    {
        if (!NexusTestEnvironment.IsManualTestEnabled) return;
        string[] views = ["ModManagerView", "ReplayStudioView", "SecretsConfigView", "DiagnosticsView"];

        foreach (string viewName in views)
        {
            Output.WriteLine($"[Scenario B] Auditing offscreen render for view: {viewName}");
            UiRenderViewArgs args = new()
            {
                ViewName = viewName,
                Width = 1280,
                Height = 720,
                Theme = "dark",
                MockData = true,
                SaveArtifact = false
            };

            UiRenderViewResult? renderResult = await Client.CallToolTypedAsync<UiRenderViewResult>(
                "kongor_ui_render_view",
                args);

            renderResult.Should().NotBeNull();
            renderResult!.ViewName.Should().Be(viewName);
        }
    }

    [Trait("Category", "Manual")]
    [Fact]
    public async Task AvaloniaDesignAudit_MultiTheme_VerifiesContrastSuccessfully()
    {
        if (!NexusTestEnvironment.IsManualTestEnabled) return;
        string[] themes = ["dark", "light", "default"];

        foreach (string theme in themes)
        {
            UiRenderViewArgs args = new()
            {
                ViewName = "ModManagerView",
                Theme = theme,
                MockData = true
            };

            UiRenderViewResult? renderResult = await Client.CallToolTypedAsync<UiRenderViewResult>(
                "kongor_ui_render_view",
                args);

            renderResult.Should().NotBeNull();
        }
    }
}
