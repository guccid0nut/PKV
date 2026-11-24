// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// TAO (Together As One) MCP & Kongor CLI -- Created by guccidonut (https://github.com/guccidonut/PKV)

using System.Text.Json;
using FluentAssertions;
using Kongor.Cli.Mcp.Handlers;
using Kongor.Cli.Mcp.Protocol;
using Kongor.Cli.Mcp.Services;
using Moq;
using Xunit;

namespace Kongor.Mcp.Tests;

public sealed class ClosedLoopVerificationTests
{
    [Fact]
    public async Task UiFlowHandler_WithVerifyWidget_ReturnsVerifiedTrueWhenPresent()
    {
        Mock<IGameIpcBridge> mockIpc = new();
        mockIpc.SetupGet(i => i.IsConnected).Returns(true);
        mockIpc.Setup(i => i.ExecuteCommandAsync(It.IsAny<string>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .Returns((string cmd, int timeout, CancellationToken ct) =>
            {
                if (cmd.Contains("WIDGET_VERIFIED", StringComparison.OrdinalIgnoreCase))
                {
                    return Task.FromResult("[OUTPUT] WIDGET_VERIFIED|true\n");
                }
                return Task.FromResult("OK");
            });

        UiFlowHandler handler = new(
            gameIpcBridge: mockIpc.Object,
            clientManageHandler: null,
            remediationService: new ClientCommandRemediationService(timeProvider: TimeProvider.System),
            uiControlService: new Mock<IUiControlService>().Object,
            timeProvider: TimeProvider.System,
            logger: _ => { });

        using JsonDocument doc = JsonDocument.Parse("""
        {
            "action": "open_lobby",
            "lobbyType": "practice",
            "verifyWidget": "sysbar_quickStart"
        }
        """);

        ToolCallResult result = await handler.HandleAsync(doc.RootElement);
        result.IsError.Should().BeFalse();

        using JsonDocument resDoc = JsonDocument.Parse(result.FirstText);
        var root = resDoc.RootElement;
        root.GetProperty("status").GetString().Should().Be("success");
        root.GetProperty("verified").GetBoolean().Should().BeTrue();
        root.GetProperty("verifiedWidget").GetString().Should().Be("sysbar_quickStart");
        root.TryGetProperty("verificationDurationMs", out _).Should().BeTrue();
    }

    [Fact]
    public async Task UiFlowHandler_WithVerifyWidget_ReturnsVerifiedFalseWhenNotFound()
    {
        Mock<IGameIpcBridge> mockIpc = new();
        mockIpc.SetupGet(i => i.IsConnected).Returns(true);
        mockIpc.Setup(i => i.ExecuteCommandAsync(It.IsAny<string>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .Returns((string cmd, int timeout, CancellationToken ct) =>
            {
                if (cmd.Contains("WIDGET_VERIFIED", StringComparison.OrdinalIgnoreCase))
                {
                    return Task.FromResult("[OUTPUT] WIDGET_VERIFIED|false\n");
                }
                return Task.FromResult("OK");
            });

        UiFlowHandler handler = new(
            gameIpcBridge: mockIpc.Object,
            clientManageHandler: null,
            remediationService: new ClientCommandRemediationService(timeProvider: TimeProvider.System),
            uiControlService: new Mock<IUiControlService>().Object,
            timeProvider: TimeProvider.System,
            logger: _ => { });

        using JsonDocument doc = JsonDocument.Parse("""
        {
            "action": "open_store",
            "tab": "heroes",
            "verifyWidget": "missing_nonexistent_widget"
        }
        """);

        ToolCallResult result = await handler.HandleAsync(doc.RootElement);
        result.IsError.Should().BeTrue();

        using JsonDocument resDoc = JsonDocument.Parse(result.FirstText);
        var root = resDoc.RootElement;
        root.GetProperty("status").GetString().Should().Be("error");
        root.GetProperty("verdict").GetString().Should().Be("fail");
        root.GetProperty("verified").GetBoolean().Should().BeFalse();
        root.GetProperty("verifiedWidget").GetString().Should().Be("missing_nonexistent_widget");
    }

    [Fact]
    public async Task UiFlowHandler_WithCaptureProof_CapturesVisualArtifact()
    {
        Mock<IGameIpcBridge> mockIpc = new();
        mockIpc.SetupGet(i => i.IsConnected).Returns(true);
        mockIpc.Setup(i => i.ExecuteCommandAsync(It.IsAny<string>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync("OK");

        Mock<IWindowCapturer> mockCapturer = new();
        mockCapturer.Setup(c => c.CaptureAsync("hon_x64", "auto", true, null, false, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new WindowCaptureResult
            {
                Success = true,
                ArtifactPath = "proofs/screenshot_123.png",
                CaptureMethodUsed = "vulkan_offscreen",
                Width = 1920,
                Height = 1080
            });

        UiFlowHandler handler = new(
            gameIpcBridge: mockIpc.Object,
            clientManageHandler: null,
            remediationService: new ClientCommandRemediationService(timeProvider: TimeProvider.System),
            uiControlService: new Mock<IUiControlService>().Object,
            timeProvider: TimeProvider.System,
            logger: _ => { },
            windowCapturer: mockCapturer.Object);

        using JsonDocument doc = JsonDocument.Parse("""
        {
            "action": "open_options",
            "optionsTab": "graphics",
            "captureProof": true
        }
        """);

        ToolCallResult result = await handler.HandleAsync(doc.RootElement);
        result.IsError.Should().BeFalse();

        using JsonDocument resDoc = JsonDocument.Parse(result.FirstText);
        var root = resDoc.RootElement;
        root.GetProperty("status").GetString().Should().Be("success");
        root.TryGetProperty("visualProof", out JsonElement proofEl).Should().BeTrue();
        proofEl.GetProperty("success").GetBoolean().Should().BeTrue();
        proofEl.GetProperty("artifactPath").GetString().Should().Be("proofs/screenshot_123.png");
        proofEl.GetProperty("zeroWindowExposure").GetBoolean().Should().BeTrue();
        proofEl.GetProperty("captureMethodUsed").GetString().Should().Be("vulkan_offscreen");
    }

    [Fact]
    public async Task UiFlowHandler_TriggerWidget_DispatchesNativeUiEvent()
    {
        Mock<IGameIpcBridge> mockIpc = new();
        mockIpc.SetupGet(i => i.IsConnected).Returns(true);
        mockIpc.Setup(i => i.DispatchUiEventAsync("main", "sysbar_quickStart", "click", null, It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync("OK")
            .Verifiable();

        UiFlowHandler handler = new(
            gameIpcBridge: mockIpc.Object,
            clientManageHandler: null,
            remediationService: new ClientCommandRemediationService(timeProvider: TimeProvider.System),
            uiControlService: new Mock<IUiControlService>().Object,
            timeProvider: TimeProvider.System,
            logger: _ => { });

        using JsonDocument doc = JsonDocument.Parse("""
        {
            "action": "trigger_widget",
            "targetWidget": "sysbar_quickStart",
            "eventType": "click"
        }
        """);

        ToolCallResult result = await handler.HandleAsync(doc.RootElement);
        result.IsError.Should().BeFalse();

        mockIpc.Verify(i => i.DispatchUiEventAsync("main", "sysbar_quickStart", "click", null, It.IsAny<int>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task GameLuaHandler_WithVerifyState_EvaluatesExpressionAndReturnsResult()
    {
        Mock<IGameIpcBridge> mockIpc = new();
        mockIpc.SetupGet(i => i.IsConnected).Returns(true);
        mockIpc.Setup(i => i.ExecuteCommandAsync(It.IsAny<string>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .Returns((string cmd, int timeout, CancellationToken ct) =>
            {
                if (cmd.Contains("STATE_VERIFIED", StringComparison.OrdinalIgnoreCase))
                {
                    return Task.FromResult("[OUTPUT] STATE_VERIFIED|true\n");
                }
                return Task.FromResult("OK");
            });

        GameLuaHandler handler = new(
            clientManageHandler: null,
            serverManageHandler: null,
            uiControlService: new Mock<IUiControlService>().Object,
            remediationService: new ClientCommandRemediationService(timeProvider: TimeProvider.System),
            timeProvider: TimeProvider.System,
            logger: _ => { },
            gameIpcBridge: mockIpc.Object);

        using JsonDocument doc = JsonDocument.Parse("""
        {
            "code": "Echo('test')",
            "verifyState": "Nexus ~= nil"
        }
        """);

        ToolCallResult result = await handler.HandleAsync(doc.RootElement);
        result.IsError.Should().BeFalse();

        using JsonDocument resDoc = JsonDocument.Parse(result.FirstText);
        var root = resDoc.RootElement;
        root.GetProperty("status").GetString().Should().Be("success");
        root.GetProperty("verifiedState").GetBoolean().Should().BeTrue();
        root.GetProperty("stateExpression").GetString().Should().Be("Nexus ~= nil");
        root.GetProperty("stateResult").GetString().Should().Be("true");
    }

    [Fact]
    public async Task GameLuaHandler_WithVerifyWidget_And_CaptureProof_EnrichesOutput()
    {
        Mock<IGameIpcBridge> mockIpc = new();
        mockIpc.SetupGet(i => i.IsConnected).Returns(true);
        mockIpc.Setup(i => i.ExecuteCommandAsync(It.IsAny<string>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .Returns((string cmd, int timeout, CancellationToken ct) =>
            {
                if (cmd.Contains("WIDGET_VERIFIED", StringComparison.OrdinalIgnoreCase))
                {
                    return Task.FromResult("[OUTPUT] WIDGET_VERIFIED|true\n");
                }
                return Task.FromResult("OK");
            });

        Mock<IWindowCapturer> mockCapturer = new();
        mockCapturer.Setup(c => c.CaptureAsync("hon_x64", "auto", true, null, false, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new WindowCaptureResult
            {
                Success = true,
                ArtifactPath = "proofs/lua_action.png",
                CaptureMethodUsed = "vulkan_offscreen",
                Width = 1920,
                Height = 1080
            });

        GameLuaHandler handler = new(
            clientManageHandler: null,
            serverManageHandler: null,
            uiControlService: new Mock<IUiControlService>().Object,
            remediationService: new ClientCommandRemediationService(timeProvider: TimeProvider.System),
            timeProvider: TimeProvider.System,
            logger: _ => { },
            gameIpcBridge: mockIpc.Object,
            windowCapturer: mockCapturer.Object);

        using JsonDocument doc = JsonDocument.Parse("""
        {
            "code": "Echo('navigating')",
            "verifyWidget": "store_button",
            "captureProof": true
        }
        """);

        ToolCallResult result = await handler.HandleAsync(doc.RootElement);
        result.IsError.Should().BeFalse();

        using JsonDocument resDoc = JsonDocument.Parse(result.FirstText);
        var root = resDoc.RootElement;
        root.GetProperty("status").GetString().Should().Be("success");
        root.GetProperty("verified").GetBoolean().Should().BeTrue();
        root.GetProperty("verifiedWidget").GetString().Should().Be("store_button");
        root.TryGetProperty("visualProof", out JsonElement proofEl).Should().BeTrue();
        proofEl.GetProperty("artifactPath").GetString().Should().Be("proofs/lua_action.png");
    }

    [Fact]
    public async Task GameClientManageHandler_ActionPipeStatus_ProbesNativeClientPipe()
    {
        Mock<IGameIpcBridge> mockIpc = new();
        mockIpc.SetupGet(i => i.IsConnected).Returns(true);
        mockIpc.Setup(i => i.PingAsync(It.IsAny<int>(), It.IsAny<CancellationToken>())).ReturnsAsync(8L);
        mockIpc.Setup(i => i.GetEngineStateAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new EngineStateDto
            {
                CurrentFps = 144.0,
                MemoryWorkingSetBytes = 1024 * 1024 * 512,
                CurrentMap = "caldavar",
                PlayerName = "Hero_Pyromancer",
                GamePhase = 3,
                ActiveMatchId = 12345
            });

        GameClientManageHandler handler = new(
            ipcBridge: mockIpc.Object,
            uiControlService: new Mock<IUiControlService>().Object,
            remediationService: new ClientCommandRemediationService(timeProvider: TimeProvider.System),
            timeProvider: TimeProvider.System);

        using JsonDocument doc = JsonDocument.Parse("""
        {
            "action": "pipe_status"
        }
        """);

        ToolCallResult result = await handler.HandleAsync(doc.RootElement);
        result.IsError.Should().BeFalse();

        using JsonDocument resDoc = JsonDocument.Parse(result.FirstText);
        var root = resDoc.RootElement;
        root.GetProperty("status").GetString().Should().Be("success");
        root.GetProperty("action").GetString().Should().Be("pipe_status");
        root.GetProperty("totalPipes").GetInt32().Should().Be(3);

        var pipes = root.GetProperty("pipes");
        var nativeClient = pipes.GetProperty("nativeClient");
        nativeClient.GetProperty("active").GetBoolean().Should().BeTrue();
        nativeClient.GetProperty("pingLatencyMs").GetInt64().Should().Be(8L);

        var engineState = nativeClient.GetProperty("engineState");
        engineState.GetProperty("fps").GetDouble().Should().Be(144.0);
        engineState.GetProperty("workingSetMb").GetDouble().Should().Be(512.0);
        engineState.GetProperty("gamePhase").GetInt32().Should().Be(3);
        engineState.GetProperty("map").GetString().Should().Be("caldavar");
    }

    [Fact]
    public async Task GameSnapshotHandler_DetermineGamePhase_UsesNativeEngineState()
    {
        Mock<IGameIpcBridge> mockIpc = new();
        mockIpc.SetupGet(i => i.IsConnected).Returns(true);
        mockIpc.Setup(i => i.GetEngineStateAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new EngineStateDto
            {
                GamePhase = 3,
                CurrentFps = 120.0
            });
        mockIpc.Setup(i => i.ExecuteCommandAsync(It.IsAny<string>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync("OK");

        GameSnapshotHandler handler = new(
            gameIpcBridge: mockIpc.Object,
            gameLuaHandler: null,
            remediationService: new ClientCommandRemediationService(timeProvider: TimeProvider.System),
            timeProvider: TimeProvider.System);

        // Pre-populate snapshot
        handler.SnapshotStore["test_snap"] = new GameSnapshotDto
        {
            Id = "test_snap",
            Hero = "Hero_Pyromancer",
            Level = 5,
            Gold = 2000,
            GamePhase = 3
        };

        using JsonDocument doc = JsonDocument.Parse("""
        {
            "action": "restore",
            "id": "test_snap"
        }
        """);

        ToolCallResult result = await handler.HandleAsync(doc.RootElement);
        result.IsError.Should().BeFalse(result.FirstText);

        using JsonDocument resDoc = JsonDocument.Parse(result.FirstText);
        resDoc.RootElement.GetProperty("status").GetString().Should().Be("success");
    }

    [Fact]
    public async Task GameSnapshotHandler_DetermineGamePhase_BlocksWhenNativeEngineStatePhaseNot3()
    {
        Mock<IGameIpcBridge> mockIpc = new();
        mockIpc.SetupGet(i => i.IsConnected).Returns(true);
        mockIpc.Setup(i => i.GetEngineStateAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new EngineStateDto
            {
                GamePhase = 1, // Loading phase
                CurrentFps = 60.0
            });

        GameSnapshotHandler handler = new(
            gameIpcBridge: mockIpc.Object,
            gameLuaHandler: null,
            remediationService: new ClientCommandRemediationService(timeProvider: TimeProvider.System),
            timeProvider: TimeProvider.System);

        handler.SnapshotStore["test_snap"] = new GameSnapshotDto
        {
            Id = "test_snap",
            Hero = "Hero_Pyromancer",
            Level = 5,
            Gold = 2000,
            GamePhase = 3
        };

        using JsonDocument doc = JsonDocument.Parse("""
        {
            "action": "restore",
            "id": "test_snap"
        }
        """);

        ToolCallResult result = await handler.HandleAsync(doc.RootElement);
        result.IsError.Should().BeTrue();

        using JsonDocument resDoc = JsonDocument.Parse(result.FirstText);
        var root = resDoc.RootElement;
        root.GetProperty("errorCode").GetString().Should().Be("INVALID_GAME_PHASE");
        root.GetProperty("currentPhase").GetInt32().Should().Be(1);
    }

    [Fact]
    public void ErrorsSinceHandler_Utf16LeConsoleLog_CatchesMainMenuPanelToggleFaults_AndFiltersBaselinedNoise()
    {
        // protects: UTF-16LE BOM-aware seeking (>256KB tail & byte-offset checkpoints), K2DiagnosticPattern matching for '^oMainMenuPanelToggle: ... without a complete table entry', and console_noise_baseline.json startup noise separation
        // fails_when: StreamReader seeks past the FF FE BOM without setting Encoding.Unicode, BracketTimePattern rejects 'Cl: [HH:mm:ss]', K2DiagnosticPattern misses 'without a complete table entry', or baselined startup noise fails IsClean
        // seam: real filesystem UTF-16LE console.log + data/gate/console_noise_baseline.json + ErrorsSinceHandler byte-offset checkpoint pipeline
        string tempDir = Path.Combine(Path.GetTempPath(), "kongor_utf16_err_" + Guid.NewGuid().ToString("N"));
        string consoleDir = Path.Combine(tempDir, "games", "client", "userdata", "Heroes of Newerth x64", "override");
        Directory.CreateDirectory(consoleDir);

        try
        {
            string consoleLogPath = Path.Combine(consoleDir, "console.log");
            string localTimeStr = TimeProvider.System.GetLocalNow().ToString("HH:mm:ss");

            // Write > 260 KB of UTF-16LE startup padding + baselined startup noise so tail-seek (>256 KB) is exercised
            System.Text.StringBuilder sb = new();
            for (int i = 0; i < 2200; i++)
            {
                sb.AppendLine($"Cl: [{localTimeStr}] Normal K2 engine initialization trace line #{i:D4} padding for 256KB seek window.");
            }
            sb.AppendLine($"Warning: [{localTimeStr}] CGame::StartClient() - Unable to load entity definitions for botAIHelperBot!");
            sb.AppendLine($"Warning: [{localTimeStr}] IResourceLibrary::Register(/heroes/hammerstorm/hero.entity) - Name collision for \"ability\"");
            sb.AppendLine($"Error: [{localTimeStr}] CFileManager::GetFile() [/ui/fe2/elements/store2/combobox/] - Invalid file type");

            File.WriteAllText(consoleLogPath, sb.ToString(), System.Text.Encoding.Unicode);
            new FileInfo(consoleLogPath).Length.Should().BeGreaterThan(256 * 1024, "UTF-16LE log must exceed 256 KB to test BOM-aware tail seeking");

            ErrorsSinceHandler handler = new(repoRoot: tempDir);

            // 1. Only baselined noise present -> IsClean must be true, BaselinedNoiseCount == 3, K2WarnCount == 0
            ErrorsScanReport baselineOnlyReport = handler.ScanLogs(TimeProvider.System.GetUtcNow().AddMinutes(-5));
            baselineOnlyReport.IsClean.Should().BeTrue("baselined startup noise must not fail IsClean");
            baselineOnlyReport.Verdict.Should().Be("pass");
            baselineOnlyReport.BaselinedNoiseCount.Should().Be(3);
            baselineOnlyReport.K2WarnCount.Should().Be(0);

            // 2. Capture byte-offset checkpoint, then append real Wave 4 regression lines in UTF-16LE
            string checkpoint = handler.CaptureCheckpointToken();
            checkpoint.Should().Contain("|0:", "checkpoint token must encode byte offset of console.log");

            string appendedFaults =
                $"Cl: [{localTimeStr}] ^oMainMenuPanelToggle: Found widget compendium without a complete table entry. Add one in main.lua GetMainMenuPanels()\r\n" +
                $"Cl: [{localTimeStr}] ^oMainMenuPanelToggle: Found widget player_ladder without a complete table entry. Add one in main.lua GetMainMenuPanels()\r\n" +
                $"Cl: [{localTimeStr}] ^rGetWidget Main Failed: missing_panel\r\n";
            File.AppendAllText(consoleLogPath, appendedFaults, System.Text.Encoding.Unicode);

            ErrorsScanReport deltaReport = handler.ScanLogsSinceCheckpoint(checkpoint);
            deltaReport.IsClean.Should().BeFalse("unbaselined MainMenuPanelToggle and GetWidget faults must fail IsClean");
            deltaReport.Verdict.Should().Be("fail");
            deltaReport.K2WarnCount.Should().Be(3);
            deltaReport.Errors.Count.Should().Be(3);
            deltaReport.Errors.Should().Contain(e => e.Message.Contains("Found widget compendium without a complete table entry", StringComparison.Ordinal));
            deltaReport.Errors.Should().Contain(e => e.Message.Contains("Found widget player_ladder without a complete table entry", StringComparison.Ordinal));
            deltaReport.Errors.Should().Contain(e => e.Message.Contains("GetWidget Main Failed", StringComparison.Ordinal));
        }
        finally
        {
            if (Directory.Exists(tempDir))
            {
                Directory.Delete(tempDir, true);
            }
        }
    }

    [Fact]
    public async Task ErrorsSinceHandler_AllowDiagnosticWarnings_RequiresNonEmptyReason_AndWaivesWhenReasonProvided()
    {
        // protects: Mandatory 'reason' audit trail whenever allowDiagnosticWarnings=true is passed to kongor_errors_since
        // fails_when: allowDiagnosticWarnings=true succeeds without a non-empty 'reason' parameter
        // seam: ErrorsSinceHandler.HandleAsync JSON contract + UTF-16LE log scan
        string tempDir = Path.Combine(Path.GetTempPath(), "kongor_escape_hatch_" + Guid.NewGuid().ToString("N"));
        string consoleDir = Path.Combine(tempDir, "games", "client", "userdata", "Heroes of Newerth x64", "override");
        Directory.CreateDirectory(consoleDir);

        try
        {
            string consoleLogPath = Path.Combine(consoleDir, "console.log");
            string localTimeStr = TimeProvider.System.GetLocalNow().ToString("HH:mm:ss");
            File.WriteAllText(
                consoleLogPath,
                $"Cl: [{localTimeStr}] ^oMainMenuPanelToggle: Found widget store_container2 without a complete table entry.\r\n",
                System.Text.Encoding.Unicode);

            ErrorsSinceHandler handler = new(repoRoot: tempDir);

            // 1. allowDiagnosticWarnings=true WITHOUT reason must fail with MISSING_ESCAPE_HATCH_REASON
            using JsonDocument noReasonDoc = JsonDocument.Parse("""{"allowDiagnosticWarnings": true}""");
            ToolCallResult noReasonRes = await handler.HandleAsync(noReasonDoc.RootElement);
            noReasonRes.IsError.Should().BeTrue();
            using JsonDocument noReasonJson = JsonDocument.Parse(noReasonRes.FirstText);
            noReasonJson.RootElement.GetProperty("errorCode").GetString().Should().Be("MISSING_ESCAPE_HATCH_REASON");
            noReasonJson.RootElement.GetProperty("verdict").GetString().Should().Be("fail");

            // 2. allowDiagnosticWarnings=true WITH non-empty reason waives K2Diagnostic into warnings while keeping clean=true
            using JsonDocument withReasonDoc = JsonDocument.Parse("""{"allowDiagnosticWarnings": true, "reason": "Investigating known store_container2 toggle during repro"}""");
            ToolCallResult withReasonRes = await handler.HandleAsync(withReasonDoc.RootElement);
            withReasonRes.IsError.Should().BeFalse();
            using JsonDocument withReasonJson = JsonDocument.Parse(withReasonRes.FirstText);
            withReasonJson.RootElement.GetProperty("clean").GetBoolean().Should().BeTrue();
            withReasonJson.RootElement.GetProperty("verdict").GetString().Should().Be("pass");
            withReasonJson.RootElement.GetProperty("k2WarnCount").GetInt32().Should().Be(1);
            withReasonJson.RootElement.GetProperty("escapeHatchReason").GetString().Should().Contain("store_container2");
        }
        finally
        {
            if (Directory.Exists(tempDir))
            {
                Directory.Delete(tempDir, true);
            }
        }
    }

    [Fact]
    public async Task ReloadAndVerifyHandler_WhenCanaryScanDetectsK2DiagnosticFault_ReturnsErrorVerdictFailAndRecordsFalseGreen()
    {
        // protects: Closed-loop reload_and_verify canary scan catching post-reload K2 diagnostic warnings and recording a false green against kongor_hot_reload
        // fails_when: ReloadAndVerifyHandler ignores K2DiagnosticPattern warnings during canary_scan, returns IsError=false on failure, or fails to increment McpToolRegistry false-green counter
        // seam: ReloadAndVerifyHandler + ErrorsSinceHandler + HotReloadHandler + McpToolRegistry false-green tracker
        string tempDir = Path.Combine(Path.GetTempPath(), "kongor_reload_verify_" + Guid.NewGuid().ToString("N"));
        string consoleDir = Path.Combine(tempDir, "games", "client", "userdata", "Heroes of Newerth x64", "override");
        Directory.CreateDirectory(consoleDir);

        try
        {
            McpToolRegistry.ResetFalseGreenCounts();
            string consoleLogPath = Path.Combine(consoleDir, "console.log");
            string localTimeStr = TimeProvider.System.GetLocalNow().ToString("HH:mm:ss");
            File.WriteAllText(consoleLogPath, $"Cl: [{localTimeStr}] Initial clean console log.\r\n", System.Text.Encoding.Unicode);

            Mock<IHotReloadService> mockHotReloadSvc = new();
            mockHotReloadSvc.Setup(s => s.GetStatus())
                .Returns(new HotReloadStatusDto
                {
                    IsWatching = true,
                    TotalReloads = 1
                });
            mockHotReloadSvc.Setup(s => s.TriggerReloadAsync(It.IsAny<IEnumerable<string>?>(), It.IsAny<CancellationToken>()))
                .Callback(() =>
                {
                    // Simulate K2 emitting a MainMenuPanelToggle diagnostic warning right after hot reload triggers
                    File.AppendAllText(
                        consoleLogPath,
                        $"Cl: [{localTimeStr}] ^oMainMenuPanelToggle: Found widget hontour without a complete table entry. Add one in main.lua GetMainMenuPanels()\r\n",
                        System.Text.Encoding.Unicode);
                })
                .ReturnsAsync(new HotReloadResultDto
                {
                    Success = true,
                    DurationMs = 5.0
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
                    EngineBuild = "test_123",
                    HashedDiskPath = "override/ui"
                });
            using JsonDocument healthDoc = JsonDocument.Parse("""{"status":"success","phase":1,"fps":144.0}""");
            JsonElement healthClone = healthDoc.RootElement.Clone();
            mockTao.Setup(t => t.DispatchAsync("health_check", It.IsAny<object>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(TaoResponse.Ok("health_check", healthClone, 1.0));

            TaoCapabilitiesHandler capsHandler = new(mockTao.Object);
            ErrorsSinceHandler errorsHandler = new(repoRoot: tempDir);
            HealthCheckHandler healthHandler = new(mockTao.Object, capsHandler, errorsHandler);

            ReloadAndVerifyHandler reloadVerify = new(
                hotReloadHandler,
                capsHandler,
                healthHandler,
                errorsHandler,
                mockTao.Object,
                repoRoot: tempDir);

            // 1. Verify skipLint=true without reason is blocked
            using JsonDocument noReasonDoc = JsonDocument.Parse("""{"skipLint": true, "skipBuildSync": true, "canaryDurationSeconds": 1}""");
            ToolCallResult noReasonRes = await reloadVerify.HandleAsync(noReasonDoc.RootElement);
            noReasonRes.IsError.Should().BeTrue();
            using JsonDocument noReasonOut = JsonDocument.Parse(noReasonRes.FirstText);
            noReasonOut.RootElement.GetProperty("stepFailed").GetString().Should().Be("escape_hatch_validation");

            // 2. Run with valid reason for skipLint -> hot_reload succeeds, canary_scan catches the hontour diagnostic fault and records false green
            using JsonDocument runDoc = JsonDocument.Parse("""{"skipLint": true, "skipBuildSync": true, "reason": "Unit test isolated temp repo without scripts/", "canaryDurationSeconds": 1}""");
            ToolCallResult res = await reloadVerify.HandleAsync(runDoc.RootElement);
            res.IsError.Should().BeTrue("canary_scan must fail when MainMenuPanelToggle warning is emitted after hot reload");

            using JsonDocument resJson = JsonDocument.Parse(res.FirstText);
            resJson.RootElement.GetProperty("status").GetString().Should().Be("failed");
            resJson.RootElement.GetProperty("verdict").GetString().Should().Be("fail");
            resJson.RootElement.GetProperty("stepFailed").GetString().Should().Be("canary_scan");
            resJson.RootElement.GetProperty("error").GetString().Should().Contain("Found widget hontour without a complete table entry");

            McpToolRegistry.GetFalseGreenCount("kongor_hot_reload").Should().BeGreaterThanOrEqualTo(1,
                "when hot_reload returns green but canary_scan detects a post-reload console fault, a false green must be recorded");
        }
        finally
        {
            if (Directory.Exists(tempDir))
            {
                Directory.Delete(tempDir, true);
            }
        }
    }

    [Fact]
    public async Task UiFlow_SweepMainPanels_ShouldVerifyAllActivePanels_AndSupportHumanFidelity()
    {
        // Protects: Authoritative 10-panel sweep_main_panels matrix, C4 screen-space bounds verification, and zero-focus-theft human click mode.
        // Fails when: Any active panel is skipped, DispatchBackgroundHumanClickAsync is not invoked in fidelity="human", or matrix coverage drifts.
        // Seam: Real UiFlowHandler + ErrorsSinceHandler + data/gate/main_menu_panel_matrix.json over mocked IGameIpcBridge.
        string tempDir = Path.Combine(Path.GetTempPath(), $"kongor_sweep_test_{Guid.NewGuid():N}");
        string overrideLogDir = Path.Combine(tempDir, "games", "client", "userdata", "Heroes of Newerth x64", "override");
        Directory.CreateDirectory(overrideLogDir);
        try
        {
            string consoleLogPath = Path.Combine(overrideLogDir, "console.log");
            string localTimeStr = DateTime.Now.ToString("HH:mm:ss");
            File.WriteAllText(consoleLogPath, $"Cl: [{localTimeStr}] Clean console baseline.\r\n", System.Text.Encoding.Unicode);

            Mock<IGameIpcBridge> mockIpc = new();
            mockIpc.SetupGet(i => i.IsConnected).Returns(true);

            int humanClickCalls = 0;
            mockIpc.Setup(i => i.DispatchBackgroundHumanClickAsync("main", It.IsAny<string>(), It.IsAny<int>(), It.IsAny<int>(), false, It.IsAny<int>(), It.IsAny<CancellationToken>()))
                .Callback(() => humanClickCalls++)
                .ReturnsAsync("OK|bg_human_click");

            mockIpc.Setup(i => i.ExecuteCommandAsync(It.IsAny<string>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync((string cmd, int _, CancellationToken _) =>
                {
                    if (cmd.Contains("PANEL_SWEEP_BTN", StringComparison.Ordinal))
                    {
                        return "PANEL_SWEEP_BTN|btn=test|exists=true|visible=true|enabled=true|cx=512|cy=44";
                    }
                    if (cmd.Contains("PANEL_SWEEP_VERIFY", StringComparison.Ordinal))
                    {
                        return "PANEL_SWEEP_VERIFY|panel=test|target=test|visible=true|onScreen=true|registryNonEmpty=true|absX=120|absY=80|w=1200|h=800|screenW=1920|screenH=1080";
                    }
                    return "OK";
                });

            ErrorsSinceHandler errorsHandler = new(repoRoot: tempDir);
            UiFlowHandler uiFlow = new(
                gameIpcBridge: mockIpc.Object,
                errorsSinceHandler: errorsHandler);

            using JsonDocument doc = JsonDocument.Parse("""{"action": "sweep_main_panels", "fidelity": "human", "captureProof": false}""");
            ToolCallResult res = await uiFlow.HandleAsync(doc.RootElement);

            res.IsError.Should().BeFalse($"sweep_main_panels should pass when all 11 panels open cleanly on-screen: {res.FirstText}");
            using JsonDocument outDoc = JsonDocument.Parse(res.FirstText);
            outDoc.RootElement.GetProperty("status").GetString().Should().Be("success");
            outDoc.RootElement.GetProperty("totalPanels").GetInt32().Should().Be(11);
            outDoc.RootElement.GetProperty("passedPanels").GetInt32().Should().Be(11);
            outDoc.RootElement.GetProperty("failedPanelCount").GetInt32().Should().Be(0);
            humanClickCalls.Should().Be(11, "each of the 11 active panels must trigger a zero-focus-theft background human click when fidelity='human'");
        }
        finally
        {
            if (Directory.Exists(tempDir))
            {
                Directory.Delete(tempDir, true);
            }
        }
    }

    [Fact]
    public async Task UiFlow_SweepMainPanels_ShouldFailOnOffscreenGeometryOrConsoleDelta()
    {
        // Protects: C4 screen-space viewport bounds check and zero-tolerance console.log delta check per panel click.
        // Fails when: Off-screen panel (absX = -2500) or K2 MainMenuPanelToggle warning is reported as a passing panel.
        // Seam: Real UiFlowHandler + real ErrorsSinceHandler byte-offset checkpoint scanner.
        string tempDir = Path.Combine(Path.GetTempPath(), $"kongor_sweep_fail_{Guid.NewGuid():N}");
        string overrideLogDir = Path.Combine(tempDir, "games", "client", "userdata", "Heroes of Newerth x64", "override");
        Directory.CreateDirectory(overrideLogDir);
        try
        {
            string consoleLogPath = Path.Combine(overrideLogDir, "console.log");
            string localTimeStr = DateTime.Now.ToString("HH:mm:ss");
            File.WriteAllText(consoleLogPath, $"Cl: [{localTimeStr}] Clean console baseline.\r\n", System.Text.Encoding.Unicode);

            Mock<IGameIpcBridge> mockIpc = new();
            mockIpc.SetupGet(i => i.IsConnected).Returns(true);

            mockIpc.Setup(i => i.ExecuteCommandAsync(It.IsAny<string>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync((string cmd, int _, CancellationToken _) =>
                {
                    if (cmd.Contains("PANEL_SWEEP_BTN", StringComparison.Ordinal))
                    {
                        if (cmd.Contains("sysbar_replay_button", StringComparison.Ordinal) ||
                            cmd.Contains("midbar_button_hontour", StringComparison.Ordinal))
                        {
                            File.AppendAllText(
                                consoleLogPath,
                                $"Cl: [{localTimeStr}] ^oMainMenuPanelToggle: Found widget hontour without a complete table entry. Add one in main.lua GetMainMenuPanels()\r\n",
                                System.Text.Encoding.Unicode);
                        }
                        return "PANEL_SWEEP_BTN|btn=test|exists=true|visible=true|enabled=true|cx=400|cy=40";
                    }
                    if (cmd.Contains("PANEL_SWEEP_VERIFY", StringComparison.Ordinal))
                    {
                        if (cmd.Contains("panel=plinko", StringComparison.Ordinal))
                        {
                            // Off-screen geometry (-2500, 0, w=400, h=300 -> absX + w = -2100 <= 0)
                            return "PANEL_SWEEP_VERIFY|panel=plinko|target=plinko|visible=true|onScreen=true|registryNonEmpty=true|absX=-2500|absY=0|w=400|h=300|screenW=1920|screenH=1080";
                        }
                        return "PANEL_SWEEP_VERIFY|panel=test|target=test|visible=true|onScreen=true|registryNonEmpty=true|absX=100|absY=100|w=800|h=600|screenW=1920|screenH=1080";
                    }
                    return "OK";
                });

            ErrorsSinceHandler errorsHandler = new(repoRoot: tempDir);
            UiFlowHandler uiFlow = new(
                gameIpcBridge: mockIpc.Object,
                errorsSinceHandler: errorsHandler);

            using JsonDocument doc = JsonDocument.Parse("""{"action": "sweep_main_panels", "fidelity": "dom", " panels": ["watch_system", "plinko", "map_selection"], "captureProof": false}""".Replace("\" panels\"", "\"panels\""));
            ToolCallResult res = await uiFlow.HandleAsync(doc.RootElement);

            res.IsError.Should().BeTrue("sweep_main_panels must fail when watch_system emits a console warning or plinko is off-screen");
            using JsonDocument outDoc = JsonDocument.Parse(res.FirstText);
            outDoc.RootElement.GetProperty("status").GetString().Should().Be("error");
            outDoc.RootElement.GetProperty("passedPanels").GetInt32().Should().Be(1);
            outDoc.RootElement.GetProperty("failedPanelCount").GetInt32().Should().Be(2);

            List<string> failed = outDoc.RootElement.GetProperty("failedPanels")
                .EnumerateArray()
                .Select(e => e.GetString()!)
                .ToList();
            failed.Should().Contain("watch_system");
            failed.Should().Contain("plinko");
        }
        finally
        {
            if (Directory.Exists(tempDir))
            {
                Directory.Delete(tempDir, true);
            }
        }
    }

    [Fact]
    public async Task UiFlow_WalkMainPanels_ShouldPassSeededWalk_AndWriteReproBundleOnInvariantFailure()
    {
        // Protects: Seeded random walk engine (walk_main_panels), 6 Universal UI Invariants (I1-I6), and deterministic repro_seed_<N>.json replay.
        // Fails when: Invariant violations (e.g., I1 two simultaneous active panels or I4 stuck sliding latch) are missed or repro bundle is not written/replayable.
        // Seam: Real UiFlowHandler + ErrorsSinceHandler + repro bundle serialization and replay over mocked IGameIpcBridge.
        string tempDir = Path.Combine(Path.GetTempPath(), $"kongor_walk_test_{Guid.NewGuid():N}");
        string overrideLogDir = Path.Combine(tempDir, "games", "client", "userdata", "Heroes of Newerth x64", "override");
        Directory.CreateDirectory(overrideLogDir);
        try
        {
            string consoleLogPath = Path.Combine(overrideLogDir, "console.log");
            string localTimeStr = DateTime.Now.ToString("HH:mm:ss");
            File.WriteAllText(consoleLogPath, $"Cl: [{localTimeStr}] Clean console baseline.\r\n", System.Text.Encoding.Unicode);

            Mock<IGameIpcBridge> mockIpc = new();
            mockIpc.SetupGet(i => i.IsConnected).Returns(true);

            int invariantProbes = 0;
            bool injectI1AndI4FailureOnStep4 = false;

            mockIpc.Setup(i => i.ExecuteCommandAsync(It.IsAny<string>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync((string cmd, int _, CancellationToken _) =>
                {
                    if (cmd.Contains("UI_INVARIANTS_PROBE", StringComparison.Ordinal))
                    {
                        invariantProbes++;
                        if (injectI1AndI4FailureOnStep4 && invariantProbes == 4)
                        {
                            // Simulate 2 mutually-exclusive main panels visible at rest (I1 violation) and stuck sliding latch (I4 violation)
                            return "UI_INVARIANTS_PROBE|visCount=2|regOk=true|geomOk=true|noLatch=false|tickMs=0.45";
                        }
                        return "UI_INVARIANTS_PROBE|visCount=1|regOk=true|geomOk=true|noLatch=true|tickMs=0.35";
                    }
                    return "PANEL_SWEEP_BTN|btn=test|exists=true|visible=true|enabled=true|cx=500|cy=40";
                });

            ErrorsSinceHandler errorsHandler = new(repoRoot: tempDir);
            UiFlowHandler uiFlow = new(
                gameIpcBridge: mockIpc.Object,
                errorsSinceHandler: errorsHandler);

            // 1. Clean 12-step seeded walk passes
            using JsonDocument cleanDoc = JsonDocument.Parse("""{"action": "walk_main_panels", "seed": 1337, "steps": 12, "fidelity": "dom"}""");
            ToolCallResult cleanRes = await uiFlow.HandleAsync(cleanDoc.RootElement);
            cleanRes.IsError.Should().BeFalse($"clean seeded walk should pass: {cleanRes.FirstText}");
            using JsonDocument cleanOut = JsonDocument.Parse(cleanRes.FirstText);
            cleanOut.RootElement.GetProperty("completedSteps").GetInt32().Should().Be(12);

            // 2. Inject I1 + I4 failure on step 4 -> fails at step 4 and writes repro_seed_1337.json
            invariantProbes = 0;
            injectI1AndI4FailureOnStep4 = true;
            string reproPath = Path.Combine(tempDir, "repro_seed_1337.json");
            string failJson = JsonSerializer.Serialize(new
            {
                action = "walk_main_panels",
                seed = 1337,
                steps = 12,
                fidelity = "dom",
                reproOutputPath = reproPath
            });
            using JsonDocument failDoc = JsonDocument.Parse(failJson);
            ToolCallResult failRes = await uiFlow.HandleAsync(failDoc.RootElement);
            failRes.IsError.Should().BeTrue("walk_main_panels must fail immediately when I1 or I4 is violated");

            using JsonDocument failOut = JsonDocument.Parse(failRes.FirstText);
            failOut.RootElement.GetProperty("failedStepIndex").GetInt32().Should().Be(4);
            List<string> failedInvariants = failOut.RootElement.GetProperty("failedInvariantIds")
                .EnumerateArray()
                .Select(e => e.GetString()!)
                .ToList();
            failedInvariants.Should().Contain("I1_SingleActiveMainPanel");
            failedInvariants.Should().Contain("I4_NoStuckTransitionOrInputLatch");
            File.Exists(reproPath).Should().BeTrue("repro bundle must be written to disk on invariant failure");

            // 3. Replay from saved repro bundle once fixed -> executes exact 4-step history and passes
            invariantProbes = 0;
            injectI1AndI4FailureOnStep4 = false;
            string replayJson = JsonSerializer.Serialize(new
            {
                action = "walk_main_panels",
                replayBundlePath = reproPath
            });
            using JsonDocument replayDoc = JsonDocument.Parse(replayJson);
            ToolCallResult replayRes = await uiFlow.HandleAsync(replayDoc.RootElement);
            replayRes.IsError.Should().BeFalse($"replaying the 4-step repro bundle should pass after fix: {replayRes.FirstText}");
            using JsonDocument replayOut = JsonDocument.Parse(replayRes.FirstText);
            replayOut.RootElement.GetProperty("completedSteps").GetInt32().Should().Be(4);
            replayOut.RootElement.GetProperty("seed").GetInt32().Should().Be(1337);
        }
        finally
        {
            if (Directory.Exists(tempDir))
            {
                Directory.Delete(tempDir, true);
            }
        }
    }
}
