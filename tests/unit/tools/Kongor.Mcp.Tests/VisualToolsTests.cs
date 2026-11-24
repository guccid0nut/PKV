// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// TAO (Together As One) MCP & Kongor CLI -- Created by guccidonut (https://github.com/guccidonut/PKV)

using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using FluentAssertions;
using Kongor.Cli.Mcp.Handlers;
using Kongor.Cli.Mcp.Protocol;
using Kongor.Cli.Mcp.Services;
using Moq;
using Xunit;

namespace Kongor.Mcp.Tests;

[Collection("VisualTools")]
public sealed class VisualToolsTests
{
    private static readonly byte[] PngHeader = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];

    #region WindowCapturer Unit & Integration Tests

    [Fact]
    public void WindowCapturer_ResolveWindowHandle_WithInvalidQuery_ReturnsZero()
    {
        WindowCapturer capturer = new();
        IntPtr hwnd = capturer.ResolveWindowHandle("non_existent_process_abc_xyz_99999", out string title, out int pid);

        hwnd.Should().Be(IntPtr.Zero);
        title.Should().BeEmpty();
        pid.Should().Be(0);
    }

    [Fact]
    public void WindowCapturer_ResolveWindowHandle_WithForeground_ReturnsValidOrZero()
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows)) return;

        WindowCapturer capturer = new();
        IntPtr hwnd = capturer.ResolveWindowHandle("foreground", out string title, out int pid);

        // In Windows desktop environment, foreground window is either active HWND or Zero
        if (hwnd != IntPtr.Zero)
        {
            pid.Should().BeGreaterThan(0);
        }
    }

    [Fact]
    public void WindowCapturer_GetAccurateWindowBounds_ReturnsValidDimensions()
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows)) return;

        WindowCapturer.RECT bounds = WindowCapturer.GetAccurateWindowBounds(IntPtr.Zero);
        int width = bounds.Right - bounds.Left;
        int height = bounds.Bottom - bounds.Top;

        width.Should().BeGreaterThan(0);
        height.Should().BeGreaterThan(0);
    }

    [Fact]
    public async Task WindowCapturer_CaptureAsync_InvalidTarget_ReturnsError()
    {
        WindowCapturer capturer = new();
        WindowCaptureResult result = await capturer.CaptureAsync("non_existent_fake_target_window_12345");

        result.Success.Should().BeFalse();
        result.ErrorMessage.Should().NotBeNullOrWhiteSpace();
        result.ErrorMessage.Should().Contain("Target window not found");
    }

    [Fact]
    [Trait("Category", "Manual")]
    public async Task WindowCapturer_CaptureAsync_Foreground_ProducesValidPngWhenAvailable()
    {
        if (!NexusTestEnvironment.IsManualTestEnabled) return;
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows)) return;

        WindowCapturer capturer = new();
        WindowCaptureResult result = await capturer.CaptureAsync("foreground", captureMethod: "auto", saveArtifact: true);

        if (result.Success)
        {
            result.Width.Should().BeGreaterThan(0);
            result.Height.Should().BeGreaterThan(0);
            result.Base64Png.Should().NotBeNullOrWhiteSpace();

            byte[] decoded = Convert.FromBase64String(result.Base64Png);
            decoded.Length.Should().BeGreaterThan(8);
            decoded.Take(8).Should().Equal(PngHeader);

            if (!string.IsNullOrEmpty(result.ArtifactPath))
            {
                File.Exists(result.ArtifactPath).Should().BeTrue();
                new FileInfo(result.ArtifactPath).Length.Should().BeGreaterThan(0);
            }
        }
    }

    [Fact]
    [Trait("Category", "Manual")]
    public async Task WindowCapturer_CaptureAsync_CustomOutputPath_WritesToSpecifiedLocation()
    {
        if (!NexusTestEnvironment.IsManualTestEnabled) return;
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows)) return;

        string customPath = Path.Combine(Path.GetTempPath(), $"test_capture_{Guid.NewGuid():N}.png");
        try
        {
            WindowCapturer capturer = new();
            WindowCaptureResult result = await capturer.CaptureAsync("foreground", captureMethod: "auto", saveArtifact: true, customOutputPath: customPath);

            if (result.Success)
            {
                File.Exists(customPath).Should().BeTrue();
                new FileInfo(customPath).Length.Should().BeGreaterThan(0);
            }
        }
        finally
        {
            if (File.Exists(customPath)) File.Delete(customPath);
        }
    }

    #endregion

    #region HeadlessUiRenderer Unit & Integration Tests

    [Fact]
    public void HeadlessUiRenderer_GetSupportedViews_ContainsAllRequiredViews()
    {
        HeadlessUiRenderer renderer = new();
        IReadOnlyList<string> supported = renderer.GetSupportedViews();

        supported.Should().Contain("ModManagerView");
        supported.Should().Contain("ReplayStudioView");
        supported.Should().Contain("SecretsConfigView");
        supported.Should().Contain("DiagnosticsView");
        supported.Should().Contain("DashboardView");
        supported.Should().Contain("GameServersView");
        supported.Should().Contain("UsersView");
        supported.Should().Contain("LiveLogsView");
        supported.Should().Contain("MainView");
        supported.Should().Contain("ReplayConverterView");
    }

    [Fact]
    public async Task HeadlessUiRenderer_RenderModManagerView_ReturnsValidPng()
    {
        HeadlessUiRenderer renderer = new();
        UiRenderResult result = await renderer.RenderViewAsync("ModManagerView", width: 1280, height: 720, theme: "dark", mockData: true, saveArtifact: false);

        result.Success.Should().BeTrue(because: result.ErrorMessage);
        result.ViewName.Should().Be("ModManagerView");
        result.Width.Should().Be(1280);
        result.Height.Should().Be(720);
        result.Theme.Should().Be("dark");
        result.Base64Png.Should().NotBeNullOrWhiteSpace();

        byte[] bytes = Convert.FromBase64String(result.Base64Png);
        bytes.Length.Should().BeGreaterThan(100);
        bytes.Take(8).Should().Equal(PngHeader);
    }

    [Trait("Category", "Manual")]
    [Fact]
    public async Task HeadlessUiRenderer_RenderReplayStudioView_ReturnsValidPng()
    {
        if (!NexusTestEnvironment.IsManualTestEnabled) return;

        HeadlessUiRenderer renderer = new();
        UiRenderResult result = await renderer.RenderViewAsync("ReplayStudioView", width: 1280, height: 720, theme: "dark", mockData: true, saveArtifact: false);

        result.Success.Should().BeTrue(because: result.ErrorMessage);
        result.ViewName.Should().Be("ReplayStudioView");
        result.ResolvedType.Should().Contain("ReplayStudioView");

        byte[] bytes = Convert.FromBase64String(result.Base64Png);
        bytes.Take(8).Should().Equal(PngHeader);
    }

    [Trait("Category", "Manual")]
    [Fact]
    public async Task HeadlessUiRenderer_RenderSecretsConfigView_ReturnsValidPng()
    {
        if (!NexusTestEnvironment.IsManualTestEnabled) return;

        HeadlessUiRenderer renderer = new();
        UiRenderResult result = await renderer.RenderViewAsync("SecretsConfigView", width: 1280, height: 720, theme: "dark", mockData: true, saveArtifact: false);

        result.Success.Should().BeTrue(because: result.ErrorMessage);
        result.ResolvedType.Should().Contain("SecretsConfigView");

        byte[] bytes = Convert.FromBase64String(result.Base64Png);
        bytes.Take(8).Should().Equal(PngHeader);
    }

    [Trait("Category", "Manual")]
    [Fact]
    public async Task HeadlessUiRenderer_RenderDiagnosticsView_ReturnsValidPng()
    {
        if (!NexusTestEnvironment.IsManualTestEnabled) return;

        HeadlessUiRenderer renderer = new();
        UiRenderResult result = await renderer.RenderViewAsync("DiagnosticsView", width: 1280, height: 720, theme: "dark", mockData: true, saveArtifact: false);

        result.Success.Should().BeTrue(because: result.ErrorMessage);
        result.ResolvedType.Should().Contain("DiagnosticsView");

        byte[] bytes = Convert.FromBase64String(result.Base64Png);
        bytes.Take(8).Should().Equal(PngHeader);
    }

    [Trait("Category", "Manual")]
    [Fact]
    public async Task HeadlessUiRenderer_RenderDashboardView_ReturnsValidPng()
    {
        if (!NexusTestEnvironment.IsManualTestEnabled) return;

        HeadlessUiRenderer renderer = new();
        UiRenderResult result = await renderer.RenderViewAsync("DashboardView", width: 1280, height: 720, theme: "dark", mockData: true, saveArtifact: false);

        result.Success.Should().BeTrue(because: result.ErrorMessage);
        result.ResolvedType.Should().Contain("DashboardView");

        byte[] bytes = Convert.FromBase64String(result.Base64Png);
        bytes.Take(8).Should().Equal(PngHeader);
    }

    [Trait("Category", "Manual")]
    [Fact]
    public async Task HeadlessUiRenderer_RenderGameServersView_ReturnsValidPng()
    {
        if (!NexusTestEnvironment.IsManualTestEnabled) return;

        HeadlessUiRenderer renderer = new();
        UiRenderResult result = await renderer.RenderViewAsync("GameServersView", width: 1280, height: 720, theme: "dark", mockData: true, saveArtifact: false);

        result.Success.Should().BeTrue(because: result.ErrorMessage);
        result.ResolvedType.Should().Contain("GameServersView");

        byte[] bytes = Convert.FromBase64String(result.Base64Png);
        bytes.Take(8).Should().Equal(PngHeader);
    }

    [Trait("Category", "Manual")]
    [Fact]
    public async Task HeadlessUiRenderer_RenderUsersView_ReturnsValidPng()
    {
        if (!NexusTestEnvironment.IsManualTestEnabled) return;

        HeadlessUiRenderer renderer = new();
        UiRenderResult result = await renderer.RenderViewAsync("UsersView", width: 1280, height: 720, theme: "dark", mockData: true, saveArtifact: false);

        result.Success.Should().BeTrue(because: result.ErrorMessage);
        result.ResolvedType.Should().Contain("UsersView");

        byte[] bytes = Convert.FromBase64String(result.Base64Png);
        bytes.Take(8).Should().Equal(PngHeader);
    }

    [Trait("Category", "Manual")]
    [Fact]
    public async Task HeadlessUiRenderer_RenderLiveLogsView_ReturnsValidPng()
    {
        if (!NexusTestEnvironment.IsManualTestEnabled) return;

        HeadlessUiRenderer renderer = new();
        UiRenderResult result = await renderer.RenderViewAsync("LiveLogsView", width: 1280, height: 720, theme: "dark", mockData: true, saveArtifact: false);

        result.Success.Should().BeTrue(because: result.ErrorMessage);
        result.ResolvedType.Should().Contain("LiveLogsView");

        byte[] bytes = Convert.FromBase64String(result.Base64Png);
        bytes.Take(8).Should().Equal(PngHeader);
    }

    [Trait("Category", "Manual")]
    [Fact]
    public async Task HeadlessUiRenderer_RenderMainView_ReturnsValidPng()
    {
        if (!NexusTestEnvironment.IsManualTestEnabled) return;

        HeadlessUiRenderer renderer = new();
        UiRenderResult result = await renderer.RenderViewAsync("MainView", width: 1280, height: 720, theme: "dark", mockData: true, saveArtifact: false);

        result.Success.Should().BeTrue(because: result.ErrorMessage);
        result.ResolvedType.Should().Contain("MainView");

        byte[] bytes = Convert.FromBase64String(result.Base64Png);
        bytes.Take(8).Should().Equal(PngHeader);
    }

    [Trait("Category", "Manual")]
    [Fact]
    public async Task HeadlessUiRenderer_ThemeVariants_DarkAndLightProduceDistinctOutputs()
    {
        if (!NexusTestEnvironment.IsManualTestEnabled) return;

        HeadlessUiRenderer renderer = new();
        UiRenderResult darkResult = await renderer.RenderViewAsync("DashboardView", width: 800, height: 600, theme: "dark", mockData: true, saveArtifact: false);
        UiRenderResult lightResult = await renderer.RenderViewAsync("DashboardView", width: 800, height: 600, theme: "light", mockData: true, saveArtifact: false);

        darkResult.Success.Should().BeTrue();
        lightResult.Success.Should().BeTrue();

        darkResult.Theme.Should().Be("dark");
        lightResult.Theme.Should().Be("light");

        // The rendered pixel buffers for dark vs light theme must be visually distinct
        darkResult.Base64Png.Should().NotBe(lightResult.Base64Png);
    }

    [Trait("Category", "Manual")]
    [Fact]
    public async Task HeadlessUiRenderer_CustomOutputPath_WritesToSpecifiedFile()
    {
        if (!NexusTestEnvironment.IsManualTestEnabled) return;

        string customPath = Path.Combine(Path.GetTempPath(), $"test_render_{Guid.NewGuid():N}.png");
        try
        {
            HeadlessUiRenderer renderer = new();
            UiRenderResult result = await renderer.RenderViewAsync("ModManagerView", width: 800, height: 600, saveArtifact: true, customOutputPath: customPath);

            result.Success.Should().BeTrue();
            result.ArtifactPath.Should().Be(customPath);
            File.Exists(customPath).Should().BeTrue();
            new FileInfo(customPath).Length.Should().BeGreaterThan(100);
        }
        finally
        {
            if (File.Exists(customPath)) File.Delete(customPath);
        }
    }

    [Fact]
    public async Task HeadlessUiRenderer_InvalidViewName_ReturnsError()
    {
        HeadlessUiRenderer renderer = new();
        UiRenderResult result = await renderer.RenderViewAsync("CompletelyNonExistentView12345");

        result.Success.Should().BeFalse();
        result.ErrorMessage.Should().Contain("could not be resolved");
    }

    [Fact]
    public async Task HeadlessUiRenderer_EmptyViewName_ReturnsError()
    {
        HeadlessUiRenderer renderer = new();
        UiRenderResult result = await renderer.RenderViewAsync("");

        result.Success.Should().BeFalse();
        result.ErrorMessage.Should().Contain("cannot be null or whitespace");
    }

    #endregion

    #region Handler & McpToolRegistry Integration Tests

    [Fact]
    public async Task CaptureWindowHandler_HandleAsync_WithInvalidTarget_ReturnsError()
    {
        CaptureWindowHandler handler = new(processRegistry: new Mock<IClientProcessRegistry>().Object);
        using JsonDocument doc = JsonDocument.Parse("""{"target": "fake_non_existent_target_window_9999"}""");

        ToolCallResult result = await handler.HandleAsync(doc.RootElement);

        result.IsError.Should().BeTrue();
        result.Content.Should().NotBeEmpty();
        ((TextContentItem)result.Content[0]).Text.Should().Contain("Target window not found");
    }

    [Trait("Category", "Manual")]
    [Fact]
    public async Task UiRenderViewHandler_HandleAsync_WithValidView_ReturnsImageResult()
    {
        if (!NexusTestEnvironment.IsManualTestEnabled) return;

        UiRenderViewHandler handler = new();
        using JsonDocument doc = JsonDocument.Parse("""
        {
            "view_name": "ModManagerView",
            "width": 1024,
            "height": 768,
            "theme": "dark",
            "mock_data": true,
            "save_artifact": false
        }
        """);

        ToolCallResult result = await handler.HandleAsync(doc.RootElement);

        result.IsError.Should().BeFalse();
        result.Content.Should().HaveCount(2);

        ImageContentItem imageItem = result.Content.OfType<ImageContentItem>().Single();
        imageItem.MimeType.Should().Be("image/png");
        imageItem.Data.Should().NotBeNullOrWhiteSpace();

        TextContentItem textItem = result.Content.OfType<TextContentItem>().Single();
        textItem.Type.Should().Be("text");
        textItem.Text.Should().Contain("\"status\": \"success\"");
        textItem.Text.Should().Contain("\"view_name\": \"ModManagerView\"");
    }

    [Fact]
    public async Task UiRenderViewHandler_HandleAsync_MissingViewName_ReturnsError()
    {
        UiRenderViewHandler handler = new();
        using JsonDocument doc = JsonDocument.Parse("{}");

        ToolCallResult result = await handler.HandleAsync(doc.RootElement);

        result.IsError.Should().BeTrue();
        ((TextContentItem)result.Content[0]).Text.Should().Contain("Missing required parameter 'view_name'");
    }

    [Fact]
    public async Task McpToolRegistry_ExecuteToolAsync_UiRenderView_ExecutesSuccessfully()
    {
        McpToolRegistry registry = new(registerDefaults: true);
        using JsonDocument doc = JsonDocument.Parse("""
        {
            "view_name": "DashboardView",
            "width": 800,
            "height": 600,
            "theme": "dark",
            "mock_data": true,
            "save_artifact": false
        }
        """);

        ToolCallResult result = await registry.ExecuteToolAsync("kongor_ui_render_view", doc.RootElement);

        result.IsError.Should().BeFalse();
        ImageContentItem imageItem = result.Content.OfType<ImageContentItem>().Single();
        imageItem.Data.Should().NotBeNullOrWhiteSpace();
    }

    [Fact]
    public async Task McpToolRegistry_ExecuteToolAsync_CaptureWindow_ReturnsResult()
    {
        const string mockBase64 = "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mNk+A8AAQUBAScY42YAAAAASUVORK5CYII=";
        Mock<IWindowCapturer> mockCapturer = new();
        mockCapturer
            .Setup(c => c.CaptureAsync("foreground", "auto", false, null, false, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new WindowCaptureResult
            {
                Success = true,
                Target = "foreground",
                WindowTitle = "DeterministicTestWindow",
                Width = 1280,
                Height = 720,
                CaptureMethodUsed = "mock_test",
                Base64Png = mockBase64
            });

        McpToolRegistry registry = new(windowCapturer: mockCapturer.Object, registerDefaults: true);
        using JsonDocument doc = JsonDocument.Parse("""
        {
            "target": "foreground",
            "capture_method": "auto",
            "save_artifact": false
        }
        """);

        ToolCallResult result = await registry.ExecuteToolAsync("kongor_capture_window", doc.RootElement);

        result.IsError.Should().BeFalse();
        result.Content.Should().Contain(c => c.Type == "image");
        ImageContentItem? img = result.Content.OfType<ImageContentItem>().FirstOrDefault();
        img.Should().NotBeNull();
        img!.Data.Should().NotBeNullOrWhiteSpace();
    }

    [Fact]
    public async Task CaptureWindowHandler_DetectAnomalies_ReturnsPlaceholderAndTeapotInPayload()
    {
        using Kongor.Cli.Mcp.Imaging.Image<Kongor.Cli.Mcp.Imaging.Rgba32> img = new(200, 200, new Kongor.Cli.Mcp.Imaging.Rgba32(20, 20, 20, 255));
        for (int y = 50; y < 75; y++)
        {
            for (int x = 50; x < 75; x++)
            {
                img[x, y] = new Kongor.Cli.Mcp.Imaging.Rgba32(255, 255, 255, 255);
            }
        }
        using MemoryStream ms = new();
        img.SaveAsPng(ms);
        string base64 = Convert.ToBase64String(ms.ToArray());

        var mockCapturer = new Mock<IWindowCapturer>();
        mockCapturer.Setup(c => c.CaptureAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<bool>(), It.IsAny<string?>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new WindowCaptureResult
            {
                Success = true,
                Target = "hon_x64",
                Base64Png = base64,
                Width = 200,
                Height = 200
            });

        CaptureWindowHandler handler = new(
            windowCapturer: mockCapturer.Object,
            processRegistry: new Mock<IClientProcessRegistry>().Object);
        using JsonDocument doc = JsonDocument.Parse("""{"target": "hon_x64", "detect_anomalies": true}""");

        ToolCallResult result = await handler.HandleAsync(doc.RootElement);

        result.IsError.Should().BeFalse();
        TextContentItem textItem = result.Content.OfType<TextContentItem>().Single();
        using JsonDocument respDoc = JsonDocument.Parse(textItem.Text);
        JsonElement root = respDoc.RootElement;
        root.GetProperty("status").GetString().Should().Be("success");
        root.TryGetProperty("anomalyReport", out JsonElement anomReport).Should().BeTrue();
        anomReport.GetProperty("hasAnomalies").GetBoolean().Should().BeTrue();
        anomReport.GetProperty("hasBlankWhitePlaceholder").GetBoolean().Should().BeTrue();
        anomReport.GetProperty("blankWhitePlaceholderCount").GetInt32().Should().BeGreaterThan(0);
    }

    [Fact]
    public async Task CaptureWindowHandler_HandleTransientAsync_ReturnsSuccessAndRestoresOffscreen()
    {
        const string mockBase64 = "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mNk+A8AAQUBAScY42YAAAAASUVORK5CYII=";
        var mockCapturer = new Mock<IWindowCapturer>();
        mockCapturer.Setup(c => c.CaptureTransientAsync(
                It.IsAny<string>(),
                It.IsAny<int>(),
                It.IsAny<int>(),
                It.IsAny<int>(),
                It.IsAny<int>(),
                It.IsAny<int>(),
                It.IsAny<bool>(),
                It.IsAny<string?>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new WindowCaptureResult
            {
                Success = true,
                Target = "hon_x64",
                WindowTitle = "Heroes of Newerth",
                Hwnd = "0x000A1234",
                Pid = 43892,
                Width = 1280,
                Height = 720,
                CaptureMethodUsed = "transient_onscreen",
                Base64Png = mockBase64
            });

        CaptureWindowHandler handler = new(
            windowCapturer: mockCapturer.Object,
            processRegistry: new Mock<IClientProcessRegistry>().Object);
        using JsonDocument doc = JsonDocument.Parse("""
        {
            "target": "hon_x64",
            "dwell_ms": 350,
            "width": 1280,
            "height": 720,
            "offscreen_x": -32000,
            "offscreen_y": -32000,
            "detect_anomalies": false,
            "allowWindowExposure": true
        }
        """);

        ToolCallResult result = await handler.HandleTransientAsync(doc.RootElement);

        result.IsError.Should().BeFalse();
        result.Content.Should().Contain(c => c.Type == "image");
        TextContentItem textItem = result.Content.OfType<TextContentItem>().Single();
        using JsonDocument respDoc = JsonDocument.Parse(textItem.Text);
        JsonElement root = respDoc.RootElement;
        root.GetProperty("status").GetString().Should().Be("success");
        root.GetProperty("captureMethod").GetString().Should().Be("transient_onscreen");
        root.GetProperty("dwellMs").GetInt32().Should().Be(350);
        root.GetProperty("restoredOffscreen").GetProperty("x").GetInt32().Should().Be(-32000);
        root.GetProperty("restoredOffscreen").GetProperty("y").GetInt32().Should().Be(-32000);
    }

    [Fact]
    public async Task CaptureWindowHandler_HandleAsync_WithCaptureMethodTransient_DelegatesSuccessfully()
    {
        const string mockBase64 = "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mNk+A8AAQUBAScY42YAAAAASUVORK5CYII=";
        var mockCapturer = new Mock<IWindowCapturer>();
        mockCapturer.Setup(c => c.CaptureTransientAsync(
                It.IsAny<string>(),
                It.IsAny<int>(),
                It.IsAny<int>(),
                It.IsAny<int>(),
                It.IsAny<int>(),
                It.IsAny<int>(),
                It.IsAny<bool>(),
                It.IsAny<string?>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new WindowCaptureResult
            {
                Success = true,
                Target = "hon_x64",
                CaptureMethodUsed = "transient_onscreen",
                Base64Png = mockBase64
            });

        CaptureWindowHandler handler = new(
            windowCapturer: mockCapturer.Object,
            processRegistry: new Mock<IClientProcessRegistry>().Object);
        using JsonDocument doc = JsonDocument.Parse("""{"target": "hon_x64", "capture_method": "transient", "detect_anomalies": false, "allowWindowExposure": true}""");

        ToolCallResult result = await handler.HandleAsync(doc.RootElement);

        result.IsError.Should().BeFalse();
        TextContentItem textItem = result.Content.OfType<TextContentItem>().Single();
        textItem.Text.Should().Contain("\"captureMethod\": \"transient_onscreen\"");
    }

    [Fact]
    public async Task McpToolRegistry_ExecuteToolAsync_TransientCapture_ExecutesRegisteredTool()
    {
        const string mockBase64 = "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mNk+A8AAQUBAScY42YAAAAASUVORK5CYII=";
        var mockCapturer = new Mock<IWindowCapturer>();
        mockCapturer.Setup(c => c.CaptureTransientAsync(
                It.IsAny<string>(),
                It.IsAny<int>(),
                It.IsAny<int>(),
                It.IsAny<int>(),
                It.IsAny<int>(),
                It.IsAny<int>(),
                It.IsAny<bool>(),
                It.IsAny<string?>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new WindowCaptureResult
            {
                Success = true,
                Target = "hon_x64",
                CaptureMethodUsed = "transient_onscreen",
                Base64Png = mockBase64
            });

        McpToolRegistry registry = new(windowCapturer: mockCapturer.Object, registerDefaults: true);
        using JsonDocument doc = JsonDocument.Parse("""{"target": "hon_x64", "dwell_ms": 300, "detect_anomalies": false, "allowWindowExposure": true}""");

        ToolCallResult result = await registry.ExecuteToolAsync("kongor_transient_capture", doc.RootElement);

        result.IsError.Should().BeFalse();
        TextContentItem textItem = result.Content.OfType<TextContentItem>().Single();
        textItem.Text.Should().Contain("\"status\": \"success\"");
        textItem.Text.Should().Contain("\"captureMethod\": \"transient_onscreen\"");
    }

    [Fact]
    public async Task WindowCapturer_CaptureTransientAsync_WithInvalidTarget_ReturnsErrorResult()
    {
        WindowCapturer capturer = new();
        var result = await capturer.CaptureTransientAsync(target: "non_existent_process_xyz_12345");
        result.Success.Should().BeFalse();
        result.ErrorMessage.Should().Contain("Target window not found");
    }

    #endregion
}
