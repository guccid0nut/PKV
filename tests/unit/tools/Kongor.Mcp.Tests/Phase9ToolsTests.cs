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

public sealed class Phase9ToolsTests : IDisposable
{
    private readonly string _testTempDir;

    public Phase9ToolsTests()
    {
        _testTempDir = Path.Combine(Path.GetTempPath(), "kongor_phase9_test_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_testTempDir);
        // Ensure standard client override path exists for watcher tests
        Directory.CreateDirectory(Path.Combine(_testTempDir, "games", "client", "override"));
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
    public void McpToolRegistry_RegistersPhase9Tools()
    {
        McpToolRegistry registry = new(registerDefaults: true);

        registry.GetTool("kongor_sync_mirrors").Should().NotBeNull();
        registry.GetTool("sync_mirrors").Should().NotBeNull();
        registry.GetTool("kongor_client_eval_lua").Should().NotBeNull();
        registry.GetTool("client_eval_lua").Should().NotBeNull();
        registry.GetTool("kongor_vfs_clean_cache").Should().NotBeNull();
        registry.GetTool("vfs_clean_cache").Should().NotBeNull();
    }

    [Fact]
    public async Task MirrorSyncService_SyncAndDiff_PerformsIncrementalSync()
    {
        // Setup mock repo structure
        string clientOverride = Path.Combine(_testTempDir, "games", "client", "override", "ui", "scripts", "newui");
        Directory.CreateDirectory(clientOverride);

        string srcFile = Path.Combine(clientOverride, "hero_definitions_cache.lua");
        await File.WriteAllTextAsync(srcFile, "-- hero cache content v1");

        using var service = new MirrorSyncService(
            repoRoot: _testTempDir,
            startWatching: false);

        // 1. Initial diff should detect drift
        var diff1 = await service.DiffAsync("scripts");
        diff1.HasDrift.Should().BeTrue();
        diff1.DriftCount.Should().BeGreaterThan(0);

        // 2. Perform sync
        var syncResult = await service.SyncAsync(new MirrorSyncOptions { Target = "scripts", DryRun = false });
        syncResult.Success.Should().BeTrue();
        syncResult.TotalFilesCopied.Should().BeGreaterThan(0);

        // 3. Second diff should detect zero drift for synced file
        var diff2 = await service.DiffAsync("scripts");
        diff2.DriftingFiles.Should().NotContain(d => d.SourcePath.Contains("hero_definitions_cache.lua"));

        // 4. Second sync should report 0 files copied (all unchanged)
        var syncResult2 = await service.SyncAsync(new MirrorSyncOptions { Target = "scripts", DryRun = false });
        syncResult2.TotalFilesCopied.Should().Be(0);
        syncResult2.TotalFilesUnchanged.Should().BeGreaterThan(0);
    }

    [Fact]
    public async Task MirrorSyncHandler_HandleAsync_ReturnsExpectedJson()
    {
        using var service = new MirrorSyncService(
            repoRoot: _testTempDir,
            startWatching: false);

        var handler = new MirrorSyncHandler(service);

        // Status action
        using var statusDoc = JsonDocument.Parse("{\"action\":\"status\"}");
        var statusResult = await handler.HandleAsync(statusDoc.RootElement);
        statusResult.IsError.Should().BeFalse();

        // Watch action
        using var watchDoc = JsonDocument.Parse("{\"action\":\"watch\",\"debounce_ms\":150}");
        var watchResult = await handler.HandleAsync(watchDoc.RootElement);
        watchResult.IsError.Should().BeFalse();
        service.IsWatching.Should().BeTrue();

        // Unwatch action
        using var unwatchDoc = JsonDocument.Parse("{\"action\":\"unwatch\"}");
        var unwatchResult = await handler.HandleAsync(unwatchDoc.RootElement);
        unwatchResult.IsError.Should().BeFalse();
        service.IsWatching.Should().BeFalse();
    }

    [Fact]
    public async Task ClientEvalLuaHandler_WhenIpcOffline_ReturnsHelpfulError()
    {
        var mockIpc = new Mock<IGameIpcBridge>();
        mockIpc.Setup(b => b.IsConnected).Returns(false);
        mockIpc.Setup(b => b.ConnectAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new TimeoutException("Named pipe not found"));

        var handler = new ClientEvalLuaHandler(ipcBridge: mockIpc.Object);

        using var doc = JsonDocument.Parse("{\"action\":\"reload_hero\",\"hero\":\"Hero_Pyromancer\"}");
        var result = await handler.HandleAsync(doc.RootElement);

        result.IsError.Should().BeTrue();
        string text = GetResultText(result);
        text.Should().Contain("K2 game client is not running or IPC pipe is offline");
    }

    [Fact]
    public async Task ClientEvalLuaHandler_WhenIpcConnected_DispatchesReloadHero()
    {
        var mockIpc = new Mock<IGameIpcBridge>();
        mockIpc.Setup(b => b.IsConnected).Returns(true);
        mockIpc.Setup(b => b.ExecuteCommandAsync(It.IsAny<string>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync("[HERO_RELOADED] Pyromancer");

        var handler = new ClientEvalLuaHandler(ipcBridge: mockIpc.Object);

        using var doc = JsonDocument.Parse("{\"action\":\"reload_hero\",\"hero\":\"Hero_Pyromancer\"}");
        var result = await handler.HandleAsync(doc.RootElement);

        result.IsError.Should().BeFalse();
        string text = GetResultText(result);
        text.Should().Contain("[HERO_RELOADED] Pyromancer");
    }

    [Fact]
    public async Task VfsCleanCacheHandler_StatusAndClean_ExecutesSuccessfully()
    {
        // Setup mock cache dir
        string cacheDir = Path.Combine(_testTempDir, "games", "client", "game", "cache");
        Directory.CreateDirectory(cacheDir);
        await File.WriteAllTextAsync(Path.Combine(cacheDir, "sample.cache"), "cache-data-12345");

        var handler = new VfsCleanCacheHandler(repoRoot: _testTempDir);

        // Status
        using var statusDoc = JsonDocument.Parse("{\"action\":\"status\",\"purge_user_cache\":false}");
        var statusResult = await handler.HandleAsync(statusDoc.RootElement);
        statusResult.IsError.Should().BeFalse();
        string statusText = GetResultText(statusResult);
        statusText.Should().Contain("\"totalFiles\": 1");

        // Clean
        using var cleanDoc = JsonDocument.Parse("{\"action\":\"clean\",\"purge_user_cache\":false}");
        var cleanResult = await handler.HandleAsync(cleanDoc.RootElement);
        cleanResult.IsError.Should().BeFalse();
        string cleanText = GetResultText(cleanResult);
        cleanText.Should().Contain("\"deletedFiles\": 1");
        File.Exists(Path.Combine(cacheDir, "sample.cache")).Should().BeFalse();
    }
}
