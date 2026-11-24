// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// TAO (Together As One) MCP & Kongor CLI -- Created by guccidonut (https://github.com/guccidonut/PKV)

using System;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using Kongor.Cli.Mcp.Handlers;
using Kongor.Cli.Mcp.Protocol;
using Kongor.Cli.Mcp.Services;
using Moq;
using Xunit;

namespace Kongor.Mcp.Tests;

public sealed class Phase9CToolsTests : IDisposable
{
    private readonly string _testTempDir;

    public Phase9CToolsTests()
    {
        _testTempDir = Path.Combine(Path.GetTempPath(), "kongor_phase9c_test_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_testTempDir);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_testTempDir))
            {
                Directory.Delete(_testTempDir, recursive: true);
            }
        }
        catch { }
    }

    private static string GetResultText(ToolCallResult result)
    {
        if (result.Content.Count > 0 && result.Content[0] is TextContentItem textItem)
        {
            return textItem.Text;
        }
        return string.Empty;
    }

    [Fact]
    public void McpToolRegistry_RegistersPhase9CTools()
    {
        McpToolRegistry registry = new(registerDefaults: true);

        registry.GetTool("kongor_get_client_logs").Should().NotBeNull();
        registry.GetTool("get_client_logs").Should().NotBeNull();
        registry.GetTool("kongor_set_store_mode").Should().NotBeNull();
        registry.GetTool("set_store_mode").Should().NotBeNull();
        registry.GetTool("kongor_client_capture_preview").Should().NotBeNull();
        registry.GetTool("client_capture_preview").Should().NotBeNull();
    }

    [Fact]
    public async Task GetClientLogsHandler_WhenReadingDiskLogs_ParsesAndFiltersCorrectly()
    {
        string logDir = Path.Combine(_testTempDir, "games", "client", "override", "logs");
        Directory.CreateDirectory(logDir);

        string logFile = Path.Combine(logDir, "client_lua_debug.log");
        string sampleLogs = """
        [14:20:00][+1.234][AVATAR][WARN] Fallback avatar used for Hero_Fayde | Missing alt model
        [14:20:05][+6.234][STORE][INFO] Loaded 1400 store products | Cache verified
        [14:20:10][+11.234][CRASH_GUARD][ERROR] Crash trapped in Safecall | Null pointer in map tick
        """;
        await File.WriteAllTextAsync(logFile, sampleLogs);

        var handler = new GetClientLogsHandler(repoRoot: _testTempDir);

        // 1. Channel filter test
        using (var doc = JsonDocument.Parse("{\"channel\":\"AVATAR\"}"))
        {
            var result = await handler.HandleAsync(doc.RootElement);
            result.IsError.Should().BeFalse();
            string text = GetResultText(result);
            using var jsonDoc = JsonDocument.Parse(text);
            var root = jsonDoc.RootElement;

            root.GetProperty("totalLogsReturned").GetInt32().Should().Be(1);
            var log = root.GetProperty("logs")[0];
            log.GetProperty("channel").GetString().Should().Be("AVATAR");
            log.GetProperty("severity").GetString().Should().Be("WARN");
            log.GetProperty("message").GetString().Should().Contain("Hero_Fayde");
        }

        // 2. Errors only test
        using (var doc = JsonDocument.Parse("{\"errors_only\":true}"))
        {
            var result = await handler.HandleAsync(doc.RootElement);
            result.IsError.Should().BeFalse();
            string text = GetResultText(result);
            using var jsonDoc = JsonDocument.Parse(text);
            var root = jsonDoc.RootElement;

            root.GetProperty("totalLogsReturned").GetInt32().Should().Be(2); // WARN and ERROR
        }
    }

    [Fact]
    public async Task GetClientLogsHandler_WhenIpcOnline_ParsesRingBufferJson()
    {
        var mockIpc = new Mock<IGameIpcBridge>();
        mockIpc.Setup(b => b.IsConnected).Returns(true);
        mockIpc.Setup(b => b.ExecuteCommandAsync(It.IsAny<string>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync("[{\"timestamp\":\"12:00:00\",\"uptime\":\"0.500\",\"channel\":\"HERO\",\"severity\":\"INFO\",\"message\":\"Hero loaded\",\"details\":\"\"}]");

        var handler = new GetClientLogsHandler(ipcBridge: mockIpc.Object, repoRoot: _testTempDir);

        using var doc = JsonDocument.Parse("{\"source\":\"ring_buffer\"}");
        var result = await handler.HandleAsync(doc.RootElement);

        result.IsError.Should().BeFalse();
        string text = GetResultText(result);
        using var jsonDoc = JsonDocument.Parse(text);
        var root = jsonDoc.RootElement;

        root.GetProperty("source").GetString().Should().Be("ring_buffer");
        root.GetProperty("totalLogsReturned").GetInt32().Should().Be(1);
        root.GetProperty("logs")[0].GetProperty("message").GetString().Should().Be("Hero loaded");
    }

    [Fact]
    public async Task SetStoreModeHandler_TogglesModeAndPersists()
    {
        string msDir = Path.Combine(_testTempDir, "src", "services", "master-server");
        Directory.CreateDirectory(msDir);

        string appSettingsPath = Path.Combine(msDir, "appsettings.Development.json");
        await File.WriteAllTextAsync(appSettingsPath, "{\n  \"Store\": {\n    \"UnlockAllItems\": true\n  }\n}");

        var handler = new SetStoreModeHandler(repoRoot: _testTempDir);

        // 1. Switch to progression mode
        using (var doc = JsonDocument.Parse("{\"mode\":\"progression\",\"refresh_client\":false,\"persist\":true}"))
        {
            var result = await handler.HandleAsync(doc.RootElement);
            result.IsError.Should().BeFalse();
            string text = GetResultText(result);
            using var jsonDoc = JsonDocument.Parse(text);
            var root = jsonDoc.RootElement;

            root.GetProperty("mode").GetString().Should().Be("progression");
            root.GetProperty("unlockAllItems").GetBoolean().Should().BeFalse();
            root.GetProperty("persisted").GetBoolean().Should().BeTrue();

            string updatedContent = await File.ReadAllTextAsync(appSettingsPath);
            updatedContent.Should().Contain("\"UnlockAllItems\": false");
        }

        // 2. Switch back to sandbox mode
        using (var doc = JsonDocument.Parse("{\"mode\":\"sandbox\",\"refresh_client\":false,\"persist\":true}"))
        {
            var result = await handler.HandleAsync(doc.RootElement);
            result.IsError.Should().BeFalse();
            string text = GetResultText(result);
            using var jsonDoc = JsonDocument.Parse(text);
            var root = jsonDoc.RootElement;

            root.GetProperty("mode").GetString().Should().Be("sandbox");
            root.GetProperty("unlockAllItems").GetBoolean().Should().BeTrue();

            string updatedContent = await File.ReadAllTextAsync(appSettingsPath);
            updatedContent.Should().Contain("\"UnlockAllItems\": true");
        }
    }

    [Fact]
    public async Task ClientCapturePreviewHandler_WhenWindowCapturerSucceeds_ReturnsArtifactMetadata()
    {
        var mockCapturer = new Mock<IWindowCapturer>();
        mockCapturer.Setup(c => c.CaptureAsync(
                It.IsAny<string>(),
                It.IsAny<string>(),
                It.IsAny<bool>(),
                It.IsAny<string?>(),
                It.IsAny<bool>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new WindowCaptureResult
            {
                Success = true,
                Target = "hon_x64",
                WindowTitle = "Heroes of Newerth",
                Width = 1920,
                Height = 1080,
                ArtifactPath = "artifacts/previews/pyro_alt.png",
                CaptureMethodUsed = "BitBlt"
            });

        var mockIpc = new Mock<IGameIpcBridge>();
        mockIpc.Setup(b => b.IsConnected).Returns(true);
        mockIpc.Setup(b => b.ExecuteCommandAsync(It.IsAny<string>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync("PREVIEW_STAGED");

        var handler = new ClientCapturePreviewHandler(
            windowCapturer: mockCapturer.Object,
            ipcBridge: mockIpc.Object,
            repoRoot: _testTempDir);

        using var doc = JsonDocument.Parse("{\"hero\":\"Hero_Pyromancer\",\"alt\":\"alt\",\"delay_ms\":0}");
        var result = await handler.HandleAsync(doc.RootElement);

        result.IsError.Should().BeFalse();
        string text = GetResultText(result);
        using var jsonDoc = JsonDocument.Parse(text);
        var root = jsonDoc.RootElement;

        root.GetProperty("status").GetString().Should().Be("success");
        root.GetProperty("hero").GetString().Should().Be("Hero_Pyromancer");
        root.GetProperty("width").GetInt32().Should().Be(1920);
        root.GetProperty("height").GetInt32().Should().Be(1080);
        root.GetProperty("stageCommandSent").GetBoolean().Should().BeTrue();
    }
}
