// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// TAO (Together As One) MCP & Kongor CLI -- Created by guccidonut (https://github.com/guccidonut/PKV)

using System;
using System.Collections.Generic;
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

public sealed class MirrorSyncAndFastReloadTests : IDisposable
{
    private readonly string _testTempDir;

    public MirrorSyncAndFastReloadTests()
    {
        _testTempDir = Path.Combine(Path.GetTempPath(), $"kongor_mirror_reload_test_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_testTempDir);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_testTempDir))
            {
                Directory.Delete(_testTempDir, true);
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Trace.WriteLine($"[MirrorSyncAndFastReloadTests] Temp dir cleanup warning: {ex.Message}");
        }
    }

    [Fact]
    public async Task MirrorSyncService_DynamicCounterpartDiscovery_SyncsAcrossAllTier5Directories()
    {
        // Protects: Tier 5 counterpart synchronization across the 5 active counterpart directories (verify_lua retired in Wave 3).
        // Fails when: MirrorSyncService fails to discover counterpart files in ClientPackageFiles, game, shared, server/override, or server/game.
        // Seam: Real MirrorSyncService.DiffAsync and SyncAsync against mock repository structure.

        string overrideDir = Path.Combine(_testTempDir, "games", "client", "override", "ui", "scripts", "newui");
        Directory.CreateDirectory(overrideDir);
        string srcFile = Path.Combine(overrideDir, "custom_hud.lua");
        await File.WriteAllTextAsync(srcFile, "-- v2 updated custom hud code\nreturn true\n");

        string[] counterpartRoots =
        [
            Path.Combine("games", "client", "ClientPackageFiles"),
            Path.Combine("games", "client", "game"),
            Path.Combine("games", "shared"),
            Path.Combine("games", "server", "override"),
            Path.Combine("games", "server", "game")
        ];

        foreach (string root in counterpartRoots)
        {
            string targetDir = Path.Combine(_testTempDir, root, "ui", "scripts", "newui");
            Directory.CreateDirectory(targetDir);
            string targetFile = Path.Combine(targetDir, "custom_hud.lua");
            await File.WriteAllTextAsync(targetFile, "-- v1 old code\n");
        }

        using var service = new MirrorSyncService(repoRoot: _testTempDir, startWatching: false);

        // 1. Initial diff should detect drift across all counterpart files
        var diff1 = await service.DiffAsync("all");
        diff1.HasDrift.Should().BeTrue();
        diff1.DriftCount.Should().BeGreaterThanOrEqualTo(5);

        // 2. Perform sync
        var syncResult = await service.SyncAsync(new MirrorSyncOptions { Target = "all", DryRun = false });
        syncResult.Success.Should().BeTrue();
        syncResult.TotalFilesCopied.Should().BeGreaterThanOrEqualTo(5);

        // 3. Counterpart contents must now match source
        foreach (string root in counterpartRoots)
        {
            string targetFile = Path.Combine(_testTempDir, root, "ui", "scripts", "newui", "custom_hud.lua");
            string content = await File.ReadAllTextAsync(targetFile);
            content.Should().Be("-- v2 updated custom hud code\nreturn true\n");
        }

        // 4. Second diff should detect zero drift
        var diff2 = await service.DiffAsync("all");
        diff2.HasDrift.Should().BeFalse();
        diff2.DriftCount.Should().Be(0);
    }

    [Fact]
    public async Task MirrorSyncService_FileSystemWatcher_AutoSyncsModifiedFileInUnder500ms()
    {
        // Protects: Real-time debounced FileSystemWatcher counterpart synchronization.
        // Fails when: Modifying an override file does not propagate to counterpart files or exceeds 500ms.
        // Seam: Real MirrorSyncService watcher + debounce timer.

        string overrideDir = Path.Combine(_testTempDir, "games", "client", "override", "ui");
        Directory.CreateDirectory(overrideDir);
        string srcFile = Path.Combine(overrideDir, "live_widget.lua");
        await File.WriteAllTextAsync(srcFile, "-- initial live widget\n");

        string destDir = Path.Combine(_testTempDir, "games", "client", "ClientPackageFiles", "ui");
        Directory.CreateDirectory(destDir);
        string destFile = Path.Combine(destDir, "live_widget.lua");
        await File.WriteAllTextAsync(destFile, "-- initial live widget\n");

        using var service = new MirrorSyncService(repoRoot: _testTempDir, startWatching: false);
        service.StartAutoSyncWatcher(debounceMs: 50);
        service.IsWatching.Should().BeTrue();

        try
        {
            // Modify source file in override
            await File.WriteAllTextAsync(srcFile, "-- updated live widget content 123\n");

            // Wait with a max timeout of 500ms for debounce + sync
            bool synchronized = false;
            for (int i = 0; i < 20; i++)
            {
                await Task.Delay(25);
                if (File.Exists(destFile))
                {
                    try
                    {
                        using var fs = new FileStream(destFile, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                        using var sr = new StreamReader(fs);
                        string content = await sr.ReadToEndAsync();
                        if (content.Contains("updated live widget content 123"))
                        {
                            synchronized = true;
                            break;
                        }
                    }
                    catch (IOException)
                    {
                        // Concurrent write in progress on this 25ms poll tick; retry on next poll
                    }
                }
            }

            synchronized.Should().BeTrue("FileSystemWatcher must auto-sync changed file to counterpart within 500ms");
        }
        finally
        {
            service.StopAutoSyncWatcher();
            service.IsWatching.Should().BeFalse();
        }
    }

    [Fact]
    public async Task ReloadAndVerifyHandler_SubSecondExecution_WithoutPowerShellSpawn()
    {
        // Protects: Sub-second hot-reload and canary verification loop (<1000ms).
        // Fails when: In-process reload verification blocks on external powershell cold-start or exceeds 1.0s.
        // Seam: ReloadAndVerifyHandler with mocked hot reload, Tao client, and short canary settlement.

        string overrideLogDir = Path.Combine(_testTempDir, "games", "client", "userdata", "Heroes of Newerth x64", "override");
        Directory.CreateDirectory(overrideLogDir);
        string consoleLogPath = Path.Combine(overrideLogDir, "console.log");
        string localTimeStr = TimeProvider.System.GetLocalNow().ToString("HH:mm:ss");
        await File.WriteAllTextAsync(consoleLogPath, $"Cl: [{localTimeStr}] Initial clean console log.\r\n", System.Text.Encoding.Unicode);

        Mock<IHotReloadService> mockHotReloadSvc = new();
        mockHotReloadSvc.Setup(s => s.GetStatus())
            .Returns(new HotReloadStatusDto
            {
                IsWatching = true,
                TotalReloads = 1
            });
        mockHotReloadSvc.Setup(s => s.TriggerReloadAsync(It.IsAny<IEnumerable<string>?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new HotReloadResultDto
            {
                Success = true,
                DurationMs = 12.5
            });

        Mock<IGameIpcBridge> mockIpc = new();
        mockIpc.SetupGet(i => i.IsConnected).Returns(true);

        HotReloadHandler hotReloadHandler = new(
            mockHotReloadSvc.Object,
            mockIpc.Object,
            new ClientCommandRemediationService(timeProvider: TimeProvider.System),
            TimeProvider.System);

        Mock<ITaoClient> mockTao = new();
        mockTao.Setup(t => t.GetCapabilitiesAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new TaoCapabilitiesDto
            {
                IsStale = false,
                EngineBuild = "test_build",
                HashedDiskPath = "override/ui"
            });

        using JsonDocument healthDoc = JsonDocument.Parse("""{"status":"success","phase":1,"fps":144.0}""");
        JsonElement healthClone = healthDoc.RootElement.Clone();
        mockTao.Setup(t => t.DispatchAsync("health_check", It.IsAny<object>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(TaoResponse.Ok("health_check", healthClone, 1.0));

        TaoCapabilitiesHandler capsHandler = new(mockTao.Object);
        ErrorsSinceHandler errorsHandler = new(repoRoot: _testTempDir);
        HealthCheckHandler healthHandler = new(mockTao.Object, capsHandler, errorsHandler);

        ReloadAndVerifyHandler reloadVerify = new(
            hotReloadHandler,
            capsHandler,
            healthHandler,
            errorsHandler,
            mockTao.Object,
            repoRoot: _testTempDir);

        using JsonDocument reqDoc = JsonDocument.Parse("""
        {
            "skipLint": true,
            "skipBuildSync": true,
            "reason": "Hermetic unit test fast verification",
            "canarySettleMs": 50
        }
        """);

        ToolCallResult result = await reloadVerify.HandleAsync(reqDoc.RootElement);

        Assert.False(result.IsError, result.FirstText);
        using JsonDocument resJson = JsonDocument.Parse(result.FirstText);
        resJson.RootElement.GetProperty("verdict").GetString().Should().Be("pass");
        resJson.RootElement.GetProperty("status").GetString().Should().Be("success");

        double totalMs = resJson.RootElement.GetProperty("totalReloadVerifyMs").GetDouble();
        totalMs.Should().BeLessThan(1000.0, "clean in-process hot reload verification must complete in under 1.0s");
    }

    [Theory]
    [InlineData("GetGamePhase() == 5", "attempt to call global 'GetGamePhase'", "NilGlobalGetGamePhase", "Host.GetGamePhase()")]
    [InlineData("Testing.GetEntityStatus(1).health > 0", "attempt to index local 's' (a number value)", "ScalarEntityStatusIndexing", "numeric enum")]
    [InlineData("Testing.SpawnUnit('Hero_Glacius', 0, 0)", "Entity not found", "InvalidHeroEntityName", "Hero_Frosty")]
    [InlineData("pcall(loadstring('return 42'))", "ExecuteCommand timed out after 3000ms", "PcallLoadstringSpinwait", "0x000E")]
    [InlineData("Script local x = 1", "{\"status\": \"error\", \"success\": false, \"response\": \"{\\\"success\\\":true", "AsyncPipeAckMismatch", "0x000E EvalLuaSync")]
    public void ClientCommandRemediationService_CodifiedPitfalls_ProvideDeterministicRemediation(
        string command,
        string error,
        string expectedCategory,
        string expectedKeywordInHintOrCommand)
    {
        // Protects: Codified guardrails for recurring high-frequency Lua/CLI pitfalls (MCP-246, MCP-243, MCP-244, MCP-231, MCP-136).
        // Fails when: High-frequency error patterns fail to trigger category detection or miss actionable remediation hints.
        // Seam: Pure ClientCommandRemediationService.AnalyzeCommandError analysis.

        var remediation = ClientCommandRemediationService.AnalyzeCommandError(command, error, "lua");

        remediation.ErrorCategory.Should().Be(expectedCategory);
        string hintAndCommand = $"{remediation.RemediationHint} {remediation.ImprovedCommand}";
        hintAndCommand.Should().Contain(expectedKeywordInHintOrCommand);
    }
}
