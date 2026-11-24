// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// TAO (Together As One) MCP & Kongor CLI -- Created by guccidonut (https://github.com/guccidonut/PKV)

#pragma warning disable CA2026
using System.Diagnostics;
using System.Text.Json;
using FluentAssertions;
using Kongor.Cli.Mcp.Handlers;
using Kongor.Cli.Mcp.Protocol;
using Kongor.Cli.Mcp.Services;
using Moq;
using Xunit;

namespace Kongor.Mcp.Tests;

/// <summary>
/// Unit tests covering Phase 5 Modernization:
/// - Pillar 3: Global In-Engine Lua Error Trap & Telemetry Relay (tao.lua synchronization and defensive calls)
/// - Pillar 4: Visual Action Overlay for Headless Actuation (CaptureWindowHandler & UiFlowHandler actuationVisual)
/// - Pillar 5: Dynamic Dedicated Server Port Registry (ServerPortRegistry, multi-server routing, atomic persistence, stale worker pruning)
/// </summary>
public sealed class ServerPortRegistryOrchestrationTests : IDisposable
{
    private readonly McpToolRegistry _registry = new(registerDefaults: true);
    private readonly string _tempRegistryPath;

    public ServerPortRegistryOrchestrationTests()
    {
        _tempRegistryPath = Path.Combine(Path.GetTempPath(), $"hon_test_registry_{Guid.NewGuid():N}.json");
    }

    public void Dispose()
    {
        try
        {
            if (File.Exists(_tempRegistryPath))
            {
                File.Delete(_tempRegistryPath);
            }
        }
        catch
        {
            // Ignore cleanup failure in temp directory
        }
    }

    private static JsonElement ParseResult(ToolCallResult result)
    {
        result.IsError.Should().BeFalse(result.FirstText);
        result.Content.Should().NotBeEmpty();
        string text = result.Content[0] is TextContentItem textItem ? textItem.Text : result.FirstText;
        using var doc = JsonDocument.Parse(text);
        return doc.RootElement.Clone();
    }

    #region 1. ServerPortRegistry Unit Tests

    [Fact]
    public void ServerPortRegistry_RegisterAndResolve_ExplicitPort_MatchesExactly()
    {
        var registry = new ServerPortRegistry(_tempRegistryPath);
        registry.RegisterWorker(1001, 11236, map: "caldavar", matchId: 10);

        var (resolvedPort, reason) = registry.ResolveServerPort(11236);

        resolvedPort.Should().Be(11236);
        reason.Should().Be("explicit_port");
    }

    [Fact]
    public void ServerPortRegistry_Resolve_MapFilter_ReturnsMatchingServer()
    {
        var registry = new ServerPortRegistry(_tempRegistryPath);
        registry.RegisterWorker(1001, 11235, map: "caldavar", matchId: 1);
        registry.RegisterWorker(1002, 11237, map: "midwars", matchId: 2);

        var (resolvedPort, reason) = registry.ResolveServerPort(null, map: "midwars");

        resolvedPort.Should().Be(11237);
        reason.Should().Be("map_filter:midwars");
    }

    [Fact]
    public void ServerPortRegistry_Resolve_MatchIdFilter_ReturnsMatchingServer()
    {
        var registry = new ServerPortRegistry(_tempRegistryPath);
        registry.RegisterWorker(1001, 11235, map: "caldavar", matchId: 101);
        registry.RegisterWorker(1002, 11238, map: "caldavar", matchId: 202);

        var (resolvedPort, reason) = registry.ResolveServerPort(null, matchId: 202);

        resolvedPort.Should().Be(11238);
        reason.Should().Be("match_id_filter:202");
    }

    [Fact]
    public void ServerPortRegistry_Resolve_Fallback_ReturnsMostRecentActiveServerWithReason()
    {
        var registry = new ServerPortRegistry(_tempRegistryPath);
        registry.RegisterWorker(1001, 11235, map: "caldavar");
        registry.RegisterWorker(1002, 11239, map: "caldavar");

        var (resolvedPort, reason) = registry.ResolveServerPort(null);

        resolvedPort.Should().Be(11239);
        reason.Should().Be("most_recent_active");
    }

    [Fact]
    public void ServerPortRegistry_Resolve_EmptyRegistry_ReturnsDefaultFallback()
    {
        var registry = new ServerPortRegistry(_tempRegistryPath);

        var (resolvedPort, reason) = registry.ResolveServerPort(null);

        resolvedPort.Should().Be(11235);
        reason.Should().Be("default_fallback");
    }

    [Fact]
    public void ServerPortRegistry_StaleWorkerPruning_PurgesExitedPids()
    {
        var registry = new ServerPortRegistry(_tempRegistryPath);
        // Use high PIDs that are guaranteed not to be running
        int nonExistentPid1 = 9999991;
        int nonExistentPid2 = 9999992;
        registry.RegisterWorker(nonExistentPid1, 11235, map: "caldavar");
        registry.RegisterWorker(nonExistentPid2, 11236, map: "caldavar");

        registry.GetActiveWorkers(pruneStale: false).Should().HaveCount(2);

        int pruned = registry.PruneStaleWorkers(force: true);

        pruned.Should().Be(2);
        registry.GetActiveWorkers(pruneStale: false).Should().BeEmpty();
    }

    [Fact]
    public void ServerPortRegistry_AtomicFilePersistence_SavesAndLoadsJson()
    {
        var registry = new ServerPortRegistry(_tempRegistryPath);
        registry.RegisterWorker(Environment.ProcessId, 11240, map: "caldavar", matchId: 555);

        // Load into a new registry instance from disk
        var reloaded = new ServerPortRegistry(_tempRegistryPath);
        var active = reloaded.GetActiveWorkers();

        active.Should().ContainSingle();
        active[0].Port.Should().Be(11240);
        active[0].Map.Should().Be("caldavar");
        active[0].MatchId.Should().Be(555);
        active[0].Status.Should().Be("active");
    }

    #endregion

    #region 2. GameCommandHandler Auto-Resolution Tests

    [Fact]
    public async Task GameCommandHandler_GenericServerTarget_ResolvesPortAndReturnsReason()
    {
        var customRegistry = new ServerPortRegistry(_tempRegistryPath);
        customRegistry.RegisterWorker(Environment.ProcessId, 11245, map: "midwars");

        var serverHandler = new GameServerManageHandler(TimeProvider.System, portRegistry: customRegistry);
        var commandHandler = new GameCommandHandler(
            ipcBridge: null,
            serverManageHandler: serverHandler,
            clientManageHandler: null,
            uiControlService: null,
            remediationService: null,
            portRegistry: customRegistry,
            timeProvider: TimeProvider.System);

        using var argsDoc = JsonDocument.Parse("""
        {
            "target": "server",
            "command": "echo test"
        }
        """);

        var result = await commandHandler.HandleAsync(argsDoc.RootElement);
        // Note: Even when the worker is offline, port resolution succeeds and returns resolvedPort and resolutionReason
        result.Content.Should().NotBeEmpty();
        string text = result.Content[0] is TextContentItem textItem ? textItem.Text : result.FirstText;
        using var doc = JsonDocument.Parse(text);
        var root = doc.RootElement;

        root.GetProperty("target").GetString().Should().Be("server:11245");
        root.GetProperty("resolvedPort").GetInt32().Should().Be(11245);
        root.GetProperty("resolutionReason").GetString().Should().Be("most_recent_active");
    }

    #endregion

    #region 3. Visual Actuation Overlay Tests (CaptureWindowHandler & UiFlowHandler)

    [Fact]
    public async Task CaptureWindowHandler_WithOverlayWidget_IncludesActuationVisualMetadata()
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
                Target = "client",
                WindowTitle = "Heroes of Newerth",
                Base64Png = "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mNk+A8AAQUBAScY42YAAAAASUVORK5CYII="
            });

        var handler = new CaptureWindowHandler(
            mockCapturer.Object,
            TimeProvider.System,
            processRegistry: new Mock<IClientProcessRegistry>().Object);
        using var argsDoc = JsonDocument.Parse("""
        {
            "target": "client",
            "overlay_widget": "main_play_button",
            "overlay_label": "CLICK_TEST",
            "save_artifact": false
        }
        """);

        var result = await handler.HandleAsync(argsDoc.RootElement);
        result.IsError.Should().BeFalse();

        // The metadata JSON is returned in result.FirstText
        using var doc = JsonDocument.Parse(result.FirstText);
        var root = doc.RootElement;

        root.TryGetProperty("actuationVisual", out JsonElement visualEl).Should().BeTrue();
        visualEl.GetProperty("overlayFlashed").GetBoolean().Should().BeTrue();
        visualEl.GetProperty("targetWidget").GetString().Should().Be("main_play_button");
        visualEl.GetProperty("label").GetString().Should().Be("CLICK_TEST");
        visualEl.TryGetProperty("bounds", out _).Should().BeTrue();
    }

    [Fact]
    public async Task UiFlowHandler_WithOverlayAndDryRun_IncludesActuationVisualMetadata()
    {
        var handler = _registry.UiFlowHandler;
        using var argsDoc = JsonDocument.Parse("""
        {
            "action": "open_lobby",
            "lobbyType": "practice",
            "dryRun": true,
            "showOverlay": true
        }
        """);

        var result = await handler.HandleAsync(argsDoc.RootElement);
        var root = ParseResult(result);

        root.GetProperty("status").GetString().Should().Be("success");
        root.GetProperty("action").GetString().Should().Be("open_lobby");
        root.TryGetProperty("actuationVisual", out JsonElement visualEl).Should().BeTrue();
        visualEl.GetProperty("overlayFlashed").GetBoolean().Should().BeTrue();
        visualEl.GetProperty("targetWidget").GetString().Should().Be("game_lobby");
        visualEl.GetProperty("label").GetString().Should().Be("OPEN_LOBBY");
    }

    #endregion

    #region 4. In-Engine Companion tao.lua Synchronization & Defensive Contract Tests

    [Fact]
    public void TaoLua_WithInvalidInput_FilesAreSynchronizedAndContainErrorTrapAndOverlay()
    {
        if (!NexusTestEnvironment.HasGameAssets) return;

        string rootDir = AppContext.BaseDirectory;
        // Locate repo root
        DirectoryInfo? cur = new DirectoryInfo(rootDir);
        while (cur != null && !File.Exists(Path.Combine(cur.FullName, "ProjectKongor.slnx")) && !File.Exists(Path.Combine(cur.FullName, "ProjectKongor.sln")))
        {
            cur = cur.Parent;
        }
        cur.Should().NotBeNull("Repository root containing ProjectKongor.slnx should be found.");

        string repoRoot = cur!.FullName;
        string clientTao = File.Exists(Path.Combine(repoRoot, "games", "client", "override", "ui", "scripts", "tao.lua"))
            ? Path.Combine(repoRoot, "games", "client", "override", "ui", "scripts", "tao.lua")
            : Path.Combine(repoRoot, "games", "client", "ClientPackageFiles", "ui", "scripts", "tao.lua");

        string[] taoPaths =
        [
            Path.Combine(repoRoot, "games", "shared", "ui", "scripts", "tao.lua"),
            clientTao,
            Path.Combine(repoRoot, "games", "client", "game", "ui", "scripts", "tao.lua")
        ];

        byte[]? masterBytes = null;
        foreach (string path in taoPaths)
        {
            File.Exists(path).Should().BeTrue($"File {path} must exist");
            byte[] bytes = File.ReadAllBytes(path);
            string content = File.ReadAllText(path);

            // Verify Key Phase 5/6 constructs
            content.Should().ContainAny("TAO.Version = \"2.1.0\"", "TAO.Version = \"2.2.0\"");
            content.Should().Contain("TAO.RelayError");
            content.Should().Contain("TAO.TrapErrors");
            content.Should().Contain("TAO.IsValidWidget");
            content.Should().Contain("TAO.SafeWidgetCall");
            content.Should().Contain("TAO.FlashWidgetOverlay");
            content.Should().Contain("TAO.Handlers[\"flash_overlay\"]");

            if (masterBytes == null)
            {
                masterBytes = bytes;
            }
            else
            {
                bytes.Should().Equal(masterBytes, $"All tao.lua files must be identical byte-for-byte with {path}");
            }
        }
    }

    #endregion
}
