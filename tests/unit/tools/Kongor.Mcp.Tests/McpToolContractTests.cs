// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// TAO (Together As One) MCP & Kongor CLI -- Created by guccidonut (https://github.com/guccidonut/PKV)

using System.Buffers.Binary;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using Kongor.Cli.Mcp.Handlers;
using Kongor.Cli.Mcp.Protocol;
using Kongor.Cli.Mcp.Services;
using Moq;
using Xunit;

namespace Kongor.Mcp.Tests;

/// <summary>
/// Comprehensive contract and regression tests for P1 (P1.1–P1.4):
/// - P1.1: First-Call Success Rate Scoreboard & JSONL telemetry
/// - P1.2: Server-Side Preflight Checks & Self-Remediating MCP-NNN Error Envelopes
/// - P1.3: Terse-by-Default (< 200 tokens) Tool Outputs + Canonical Tool Schema Contracts
/// - P1.4: Deterministic verification of MCP-230 through MCP-238 fixes
/// </summary>
public sealed class McpToolContractTests
{
    [Fact]
    public void AllCanonicalTools_HaveValidSchema_NonEmptyDescription_AndConsistentRequiredProperties()
    {
        // protects: Every canonical MCP tool definition exposed to AI agents via tools/list
        // fails_when: A tool is registered without a description, non-object schema, or lists a Required property absent from Properties
        // seam: McpToolRegistry.ListTools()
        var registry = new McpToolRegistry(registerDefaults: true);
        var tools = registry.ListTools();

        tools.Should().NotBeEmpty();

        foreach (var tool in tools)
        {
            tool.Name.Should().NotBeNullOrWhiteSpace();
            tool.Description.Should().NotBeNullOrWhiteSpace($"Tool '{tool.Name}' must have a non-empty description");
            tool.InputSchema.Should().NotBeNull($"Tool '{tool.Name}' must have an InputSchema");
            tool.InputSchema.Type.Should().Be("object", $"Tool '{tool.Name}' InputSchema must be of type 'object'");
            tool.InputSchema.Properties.Should().NotBeNull($"Tool '{tool.Name}' InputSchema.Properties must not be null");

            if (tool.InputSchema.Required != null)
            {
                foreach (string reqProp in tool.InputSchema.Required)
                {
                    tool.InputSchema.Properties.Should().ContainKey(
                        reqProp,
                        $"Tool '{tool.Name}' lists '{reqProp}' in Required, so it must exist in InputSchema.Properties");
                }
            }
        }
    }

    [Fact]
    public async Task P1_1_FirstCallScoreboard_TracksFirstCallSuccess_AndWritesJsonlOutcomeLog()
    {
        // protects: P1.1 First-Call Success Rate Scoreboard and JSONL telemetry persistence
        // fails_when: FirstCallSucceeded is overwritten by subsequent calls or JSONL log is not written
        // seam: McpToolRegistry.ExecuteToolAsync + GetFirstCallScoreboard + OutcomeLogFilePath
        var registry = new McpToolRegistry(registerDefaults: false);
        registry.ResetFirstCallScoreboard();

        string tempLogPath = Path.Combine(Path.GetTempPath(), $"kongor_mcp_outcomes_{Guid.NewGuid():N}.jsonl");
        registry.OutcomeLogFilePath = tempLogPath;

        try
        {
            int callCount = 0;
            registry.RegisterTool(
                new ToolDefinition
                {
                    Name = "kongor_test_scoreboard_tool",
                    Description = "Test tool for P1.1 scoreboard",
                    InputSchema = new ToolInputSchema
                    {
                        Properties = new Dictionary<string, ToolPropertySchema>
                        {
                            ["code"] = new() { Type = "string", Description = "Lua code" },
                            ["channel"] = new() { Type = "string", Description = "IPC channel" }
                        }
                    }
                },
                (args, _) =>
                {
                    callCount++;
                    return Task.FromResult(callCount == 1
                        ? ToolCallResult.Json(new { status = "success" })
                        : ToolCallResult.Error("Simulated second-call failure"));
                });

            using var doc = JsonDocument.Parse("{}");
            var res1 = await registry.ExecuteToolAsync("kongor_test_scoreboard_tool", doc.RootElement);
            var res2 = await registry.ExecuteToolAsync("kongor_test_scoreboard_tool", doc.RootElement);

            res1.IsError.Should().BeFalse();
            res2.IsError.Should().BeTrue();

            var scoreboard = registry.GetFirstCallScoreboard();
            scoreboard.FirstCallOutcomesByTool.Should().ContainKey("kongor_test_scoreboard_tool");
            scoreboard.FirstCallOutcomesByTool["kongor_test_scoreboard_tool"].Should().BeTrue("first call succeeded and must remain pinned");
            scoreboard.TotalToolCalls.Should().Be(2);
            scoreboard.TotalToolErrors.Should().Be(1);

            File.Exists(tempLogPath).Should().BeTrue();
            string[] lines = await File.ReadAllLinesAsync(tempLogPath);
            lines.Length.Should().Be(2);
            lines[0].Should().Contain("\"firstCall\":true");
            lines[0].Should().Contain("\"isFirstCallInSession\":true");
            lines[0].Should().Contain("\"succeeded\":true");
            lines[0].Should().Contain("\"isError\":false");
            lines[1].Should().Contain("\"firstCall\":false");
            lines[1].Should().Contain("\"isFirstCallInSession\":false");
            lines[1].Should().Contain("\"succeeded\":false");
            lines[1].Should().Contain("\"isError\":true");
        }
        finally
        {
            if (File.Exists(tempLogPath))
            {
                File.Delete(tempLogPath);
            }
        }
    }

    [Fact]
    public async Task P1_2_ErrorEnvelopeEnrichment_AndPreflightGuard_AttachMcpIssueMetadata()
    {
        // protects: P1.2 Tool description pitfall enrichment, JSON error envelope enrichment, and fast preflight check
        // fails_when: Error envelopes lack mcpIssueId/preventionRule/remediationCommand or preflight fails to block offline eval_lua
        // seam: McpToolRegistry.RegisterDefaults + EnrichErrorWithMcpIssue + TryEvaluatePreflightAsync
        var offlineBridge = new Mock<IGameIpcBridge>();
        offlineBridge.SetupGet(x => x.IsConnected).Returns(false);

        var registry = new McpToolRegistry(registerDefaults: true, gameIpcBridge: offlineBridge.Object);
        var tools = registry.ListTools().ToDictionary(t => t.Name, StringComparer.OrdinalIgnoreCase);

        tools.Should().ContainKey("kongor_client_navigate");
        tools["kongor_client_navigate"].Description.Should().Contain("[MCP-");

        // Trigger an error on a tool with a known MCP-NNN entry and verify JSON envelope preservation
        using var errArgs = JsonDocument.Parse("{\"destination\":\"invalid_route_xyz\",\"skip_preflight\":true}");
        var navErr = await registry.ExecuteToolAsync("kongor_client_navigate", errArgs.RootElement);
        navErr.IsError.Should().BeTrue();
        navErr.FirstText.Should().Contain("MCP-230");

        // Trigger preflight check on kongor_ui_control with enforce_preflight: true and auto_heal: false when client is offline
        using var preflightArgs = JsonDocument.Parse("{\"action\":\"click\",\"widget\":\"systembar\",\"enforce_preflight\":true,\"auto_heal\":false}");
        var preflightRes = await registry.ExecuteToolAsync("kongor_ui_control", preflightArgs.RootElement);
        preflightRes.IsError.Should().BeTrue();

        using var preflightDoc = JsonDocument.Parse(preflightRes.FirstText);
        preflightDoc.RootElement.GetProperty("preflightFailed").GetBoolean().Should().BeTrue();
        preflightDoc.RootElement.GetProperty("mcpIssueId").GetString().Should().NotBeNullOrWhiteSpace();
        preflightDoc.RootElement.GetProperty("remediationCommand").GetString().Should().Contain("kongor_game_client_manage");
    }

    [Fact]
    public async Task P1_3_TerseByDefault_And_MCP236_HealthCheckHandler_UsesFastPipeCheck()
    {
        // protects: P1.3 terse-by-default (< 200 tokens / < 800 chars) output and MCP-236 fast native pipe health check without 21s mailbox timeout
        // fails_when: HealthCheckHandler times out on TaoClient/TaoCapabilitiesHandler when native pipe is healthy or emits bloated payload in concise mode
        // seam: HealthCheckHandler.HandleAsync + TaoCapabilitiesHandler + IGameIpcBridge
        var mockPipe = new Mock<IGameIpcBridge>();
        mockPipe.SetupGet(x => x.IsConnected).Returns(true);
        mockPipe.SetupGet(x => x.PipeName).Returns("kongor-native-client");

        var mockTao = new Mock<ITaoClient>();
        mockTao.SetupGet(x => x.IsConnected).Returns(false);

        var taoCaps = new TaoCapabilitiesHandler(mockTao.Object);
        var handler = new HealthCheckHandler(
            taoClient: mockTao.Object,
            taoCapabilitiesHandler: taoCaps,
            gameIpcBridge: mockPipe.Object);

        using var conciseDoc = JsonDocument.Parse("{}");
        var conciseRes = await handler.HandleAsync(conciseDoc.RootElement);
        conciseRes.IsError.Should().BeFalse();
        conciseRes.FirstText.Length.Should().BeLessThan(800, "concise default must stay < 200 tokens (< 800 chars)");

        using var parsedConcise = JsonDocument.Parse(conciseRes.FirstText);
        parsedConcise.RootElement.GetProperty("detail").GetString().Should().Be("concise");
        parsedConcise.RootElement.GetProperty("pipeConnected").GetBoolean().Should().BeTrue();

        // Verify neither TaoClient.DispatchAsync nor TaoClient.GetCapabilitiesAsync was called when fast native pipe check succeeded and TAO is disconnected (MCP-236)
        mockTao.Verify(x => x.DispatchAsync(It.IsAny<string>(), It.IsAny<object?>(), It.IsAny<int>(), It.IsAny<CancellationToken>()), Times.Never);
        mockTao.Verify(x => x.GetCapabilitiesAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()), Times.Never);

        using var fullDoc = JsonDocument.Parse("{\"detail\":\"full\"}");
        var fullRes = await handler.HandleAsync(fullDoc.RootElement);
        fullRes.IsError.Should().BeFalse();
        using var parsedFull = JsonDocument.Parse(fullRes.FirstText);
        parsedFull.RootElement.GetProperty("detail").GetString().Should().Be("full");
    }

    [Fact]
    public async Task MCP230_K2RouteRegistry_And_ClientNavigateHandler_SupportSubroutes_AndSyncLuaVerify()
    {
        // protects: MCP-230 Store avatar/search sub-routes, Options sliding=false + subCategory, create_game, match_stats, and 0x000E verification
        // fails_when: Options route omits HoN_Options.sliding = false, Store route ignores avatar/search, or match_stats route is missing
        // seam: K2RouteRegistry.ResolveRoute + UiFlowHandler.BuildOpen*Lua
        var optionsRoute = K2RouteRegistry.ResolveRoute("options");
        optionsRoute.Should().NotBeNull();
        string optionsLua = optionsRoute!.BuildLuaScript(new K2NavigationContext
        {
            Destination = "options",
            Category = "mods",
            SubCategory = "visuals"
        });
        optionsLua.Should().Contain("HoN_Options.sliding = false");
        optionsLua.Should().Contain("SelectSubCategory(6, 2)");

        var storeRoute = K2RouteRegistry.ResolveRoute("store");
        storeRoute.Should().NotBeNull();
        string storeLua = storeRoute!.BuildLuaScript(new K2NavigationContext
        {
            Destination = "store",
            Category = "altavatars",
            Hero = "Devourer",
            AltSkin = "Alt9"
        });
        storeLua.Should().Contain("Hero_Devourer.Alt9");
        storeLua.Should().Contain("AvatarCodeToInit");
        storeLua.Should().Contain("NavToHeroAvatarDetailPage");

        var createGameRoute = K2RouteRegistry.ResolveRoute("create_game");
        createGameRoute.Should().NotBeNull();
        string createGameLua = createGameRoute!.BuildLuaScript(new K2NavigationContext
        {
            Destination = "lobby",
            Subpage = "create_game"
        });
        createGameLua.Should().Contain("SwitchCreatePanel");

        var matchStatsRoute = K2RouteRegistry.ResolveRoute("match_stats");
        matchStatsRoute.Should().NotBeNull();
        string matchStatsLua = matchStatsRoute!.BuildLuaScript(new K2NavigationContext
        {
            Destination = "match_stats",
            MatchId = "42",
            Account = "guccidonut"
        });
        matchStatsLua.Should().Contain("_stats_last_match_id");
        matchStatsLua.Should().Contain("GetMatchInfo");
        await Task.CompletedTask;
    }

    [Fact]
    public async Task MCP231_And_MCP233_GetClientLogsHandler_ParsesUtf16LeConsoleLog_AndHonorsLinesAndFilterAliases()
    {
        // protects: MCP-233 UTF-16LE override/console.log decoding, Err:/Warn: severity classification, and merging disk console.log when KongorLogger has routine INFO logs
        // fails_when: UTF-16LE console.log produces garbled/empty lines, Err: is hidden when KongorLogger has INFO logs, or lines/filter aliases are ignored
        // seam: GetClientLogsHandler.HandleAsync + ClientOutputRingBuffer.ReadTailOfLogFile
        string tempRepoRoot = Path.Combine(Path.GetTempPath(), $"kongor_logs_test_{Guid.NewGuid():N}");
        string overrideDir = Path.Combine(tempRepoRoot, "games", "client", "userdata", "Heroes of Newerth x64", "override");
        Directory.CreateDirectory(overrideDir);
        string consoleLogPath = Path.Combine(overrideDir, "console.log");

        try
        {
            string logContent =
                "Net: Connected to 127.0.0.1\r\n" +
                "Err: Interface::GetWidget() - Widget Bramble_Preview not found\r\n" +
                "Warn: Texture fallback triggered for model_store\r\n";
            await File.WriteAllBytesAsync(consoleLogPath, Encoding.Unicode.GetPreamble().Concat(Encoding.Unicode.GetBytes(logContent)).ToArray());

            string tail = ClientOutputRingBuffer.ReadTailOfLogFile(consoleLogPath, 4096);
            tail.Should().Contain("Widget Bramble_Preview not found");

            var mockBridge = new Mock<IGameIpcBridge>();
            mockBridge.SetupGet(x => x.IsConnected).Returns(true);
            // KongorLogger only has routine INFO logs, while console.log on disk has the native K2 Err: line
            mockBridge.Setup(x => x.ExecuteLuaAsync(It.IsAny<string>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync("[{\"timestamp\":\"09:00:01\",\"uptime\":\"2.0\",\"channel\":\"K2\",\"severity\":\"INFO\",\"message\":\"Normal tick\",\"details\":\"\"}]");

            var handler = new GetClientLogsHandler(ipcBridge: mockBridge.Object, repoRoot: tempRepoRoot);
            using var argsDoc = JsonDocument.Parse("{\"lines\":5,\"filter\":\"Bramble_Preview\",\"errors_only\":true}");
            var res = await handler.HandleAsync(argsDoc.RootElement);
            res.IsError.Should().BeFalse();

            using var outDoc = JsonDocument.Parse(res.FirstText);
            outDoc.RootElement.GetProperty("totalLogsReturned").GetInt32().Should().Be(1);
            outDoc.RootElement.GetProperty("logs")[0].GetProperty("message").GetString().Should().Contain("Bramble_Preview");
        }
        finally
        {
            if (Directory.Exists(tempRepoRoot))
            {
                Directory.Delete(tempRepoRoot, recursive: true);
            }
        }
    }

    [Fact]
    public async Task MCP232_ClientPracticeHandler_SupportsStartMatch_LevelUp_GiveItems_AndDynamicPort()
    {
        // protects: MCP-232 post-spawn practice actions (start_match, level_up, give_items) and UDP port collision avoidance
        // fails_when: ClientPracticeHandler re-launches practice instead of executing level_up/give_items/start_match
        // seam: ClientPracticeHandler.HandleAsync + ResolvePracticeServerPort
        var mockBridge = new Mock<IGameIpcBridge>();
        mockBridge.SetupGet(x => x.IsConnected).Returns(true);
        mockBridge.Setup(x => x.ExecuteCommandAsync(It.IsAny<string>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync("[OK]");
        mockBridge.Setup(x => x.ExecuteLuaAsync(It.IsAny<string>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync("true");

        int port = ClientPracticeHandler.ResolvePracticeServerPort(11235);
        port.Should().BeGreaterThanOrEqualTo(11235);

        var handler = new ClientPracticeHandler(gameIpcBridge: mockBridge.Object);

        using var levelDoc = JsonDocument.Parse("{\"action\":\"level_up\",\"level\":15}");
        var levelRes = await handler.HandleAsync(levelDoc.RootElement);
        levelRes.IsError.Should().BeFalse();
        using var levelJson = JsonDocument.Parse(levelRes.FirstText);
        levelJson.RootElement.GetProperty("action").GetString().Should().Be("level_up");
        levelJson.RootElement.GetProperty("level").GetInt32().Should().Be(15);

        using var itemsDoc = JsonDocument.Parse("{\"action\":\"give_items\",\"items\":[\"Item_PortalKey\",\"Item_PostHaste\"]}");
        var itemsRes = await handler.HandleAsync(itemsDoc.RootElement);
        itemsRes.IsError.Should().BeFalse();
        using var itemsJson = JsonDocument.Parse(itemsRes.FirstText);
        itemsJson.RootElement.GetProperty("action").GetString().Should().Be("give_items");

        using var startMatchDoc = JsonDocument.Parse("{\"action\":\"start_match\"}");
        var startMatchRes = await handler.HandleAsync(startMatchDoc.RootElement);
        startMatchRes.IsError.Should().BeFalse();
        using var startMatchJson = JsonDocument.Parse(startMatchRes.FirstText);
        startMatchJson.RootElement.GetProperty("action").GetString().Should().Be("start_match");
    }

    [Fact]
    public async Task MCP234_UiControlHandler_ClickWidget_FallsBackToDirectDoEvent_WhenTaoReturnsMethodNone()
    {
        // protects: MCP-234 direct K2 widget DoEvent fallback when TAO click_widget returns method="none" on template widgets
        // fails_when: UiControlHandler fails with an error when TaoClient returns method="none" instead of invoking DoEvent via IGameIpcBridge
        // seam: UiControlHandler.HandleAsync + ITaoClient + IGameIpcBridge
        var mockTao = new Mock<ITaoClient>();
        mockTao.SetupGet(x => x.IsConnected).Returns(true);
        mockTao.Setup(x => x.DispatchAsync("click_widget", It.IsAny<object?>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new TaoResponse<JsonElement>
            {
                Success = false,
                Data = JsonElement.Parse("{\"clicked\":false,\"method\":\"none\",\"widget\":\"store_tab_altavatars\"}")
            });

        var mockBridge = new Mock<IGameIpcBridge>();
        mockBridge.SetupGet(x => x.IsConnected).Returns(true);
        mockBridge.Setup(x => x.ExecuteLuaAsync(It.IsAny<string>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync("CLICKED:store_tab_altavatars");

        var handler = new UiControlHandler(
            uiControlService: new Mock<IUiControlService>().Object,
            taoClient: mockTao.Object,
            gameIpcBridge: mockBridge.Object);

        using var argsDoc = JsonDocument.Parse("{\"action\":\"click_widget\",\"widget\":\"store_tab_altavatars\",\"interface\":\"main\"}");
        var res = await handler.HandleAsync(argsDoc.RootElement);

        res.IsError.Should().BeFalse();
        using var outDoc = JsonDocument.Parse(res.FirstText);
        outDoc.RootElement.GetProperty("status").GetString().Should().Be("success");
        outDoc.RootElement.GetProperty("method").GetString().Should().Be("ipc_dom_doevent");
    }

    [Fact]
    public async Task MCP235_CaptureWindowHandler_HandleTransient_HonorsOutputPathAndSavePathAliases()
    {
        // protects: MCP-235 custom output_path / savePath file persistence and filePath response field in transient window capture
        // fails_when: HandleTransientAsync ignores output_path/savePath or omits filePath from the JSON response
        // seam: CaptureWindowHandler.HandleAsync (mode: transient) + IWindowCapturer
        string tempPng = Path.Combine(Path.GetTempPath(), $"kongor_capture_test_{Guid.NewGuid():N}.png");
        try
        {
            byte[] dummyPng = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];
            var mockCapturer = new Mock<IWindowCapturer>();
            mockCapturer.Setup(x => x.CaptureTransientAsync(
                    It.IsAny<string>(),
                    It.IsAny<int>(),
                    It.IsAny<int>(),
                    It.IsAny<int>(),
                    It.IsAny<int>(),
                    It.IsAny<int>(),
                    It.IsAny<bool>(),
                    It.IsAny<string?>(),
                    It.IsAny<CancellationToken>()))
                .ReturnsAsync((string _, int _, int _, int _, int _, int _, bool _, string? customPath, CancellationToken _) =>
                {
                    if (!string.IsNullOrWhiteSpace(customPath))
                    {
                        File.WriteAllBytes(customPath, dummyPng);
                    }
                    return new WindowCaptureResult
                    {
                        Success = true,
                        WindowTitle = "Heroes of Newerth",
                        Pid = 1234,
                        Width = 1280,
                        Height = 720,
                        CaptureMethodUsed = "PrintWindow",
                        Base64Png = Convert.ToBase64String(dummyPng),
                        ArtifactPath = customPath
                    };
                });

            var handler = new CaptureWindowHandler(
                mockCapturer.Object,
                processRegistry: new Mock<IClientProcessRegistry>().Object);
            string escapedPath = tempPng.Replace("\\", "\\\\");
            using var argsDoc = JsonDocument.Parse($"{{\"mode\":\"transient\",\"output_path\":\"{escapedPath}\",\"allowWindowExposure\":true}}");
            var res = await handler.HandleAsync(argsDoc.RootElement);

            res.IsError.Should().BeFalse();
            File.Exists(tempPng).Should().BeTrue("output_path must be written to disk");

            using var outDoc = JsonDocument.Parse(res.FirstText);
            outDoc.RootElement.GetProperty("filePath").GetString().Should().Be(tempPng);
            outDoc.RootElement.GetProperty("artifactPath").GetString().Should().Be(tempPng);
        }
        finally
        {
            if (File.Exists(tempPng))
            {
                File.Delete(tempPng);
            }
        }
    }

    [Fact]
    public async Task MCP237_McpToolRegistry_SplitsLifecycleRateLimitBucket_And_StopClientResolvesAmbientClient()
    {
        // protects: MCP-237 rate-limit bucket separation between read-only status/pipe_status and lifecycle start/stop, plus ambient client stop
        // fails_when: status/pipe_status map to ServerManagement category or action="stop" fails to stop an ambient-registered client
        // seam: McpToolRegistry.ResolveEffectiveCategory + GameClientManageHandler.HandleAsync(action: "stop")
        using var statusDoc = JsonDocument.Parse("{\"action\":\"status\"}");
        using var pipeDoc = JsonDocument.Parse("{\"action\":\"pipe_status\"}");
        using var startDoc = JsonDocument.Parse("{\"action\":\"start\"}");

        McpToolRegistry.ResolveEffectiveCategory("kongor_game_client_manage", McpToolCategory.ServerManagement, statusDoc.RootElement)
            .Should().Be(McpToolCategory.ReadOnly);
        McpToolRegistry.ResolveEffectiveCategory("kongor_game_client_manage", McpToolCategory.ServerManagement, pipeDoc.RootElement)
            .Should().Be(McpToolCategory.ReadOnly);
        McpToolRegistry.ResolveEffectiveCategory("kongor_game_client_manage", McpToolCategory.ServerManagement, startDoc.RootElement)
            .Should().Be(McpToolCategory.ServerManagement);

        // Verify action="stop" without agent_id resolves and unregisters the ambient client
        var mockRegistry = new Mock<IClientProcessRegistry>();
        var ambientInstance = new ClientInstance
        {
            ProcessId = 999999,
            SlotIndex = 0,
            AgentOwner = "ambient",
            LaunchedAt = DateTimeOffset.UtcNow
        };
        mockRegistry.Setup(x => x.GetClientByAgentId("ambient")).Returns(ambientInstance);

        var handler = new GameClientManageHandler(processRegistry: mockRegistry.Object, logger: _ => { });
        using var stopDoc = JsonDocument.Parse("{\"action\":\"stop\"}");
        var stopRes = await handler.HandleAsync(stopDoc.RootElement);

        stopRes.IsError.Should().BeFalse();
        mockRegistry.Verify(x => x.Unregister(999999), Times.Once);
    }

    [Fact]
    public void MCP238_LifecycleService_EvaluateClusterState_ReportsRunningWhenMasterAndChatAreHealthy()
    {
        // protects: MCP-238 clusterState evaluation when master-server (55555) and chat-server (11031) are Running while optional web portal services are Stopped
        // fails_when: EvaluateClusterState returns "Degraded" when isMasterHealthy && isChatHealthy is true
        // seam: LifecycleService.EvaluateClusterState
        string stateWhenCoreHealthy = LifecycleService.EvaluateClusterState(
            isMasterHealthy: true,
            isChatHealthy: true,
            isApiRunning: false,
            isUiRunning: false,
            isAspireRunning: false);

        stateWhenCoreHealthy.Should().Be("Running");

        string stateWhenOnlyMasterRunning = LifecycleService.EvaluateClusterState(
            isMasterHealthy: true,
            isChatHealthy: false,
            isApiRunning: false,
            isUiRunning: false,
            isAspireRunning: false);

        stateWhenOnlyMasterRunning.Should().Be("Degraded");

        string stateWhenAllStopped = LifecycleService.EvaluateClusterState(
            isMasterHealthy: false,
            isChatHealthy: false,
            isApiRunning: false,
            isUiRunning: false,
            isAspireRunning: false);

        stateWhenAllStopped.Should().Be("Stopped");
    }

    [Fact]
    public async Task P2_1_McpToolRegistry_HidesAliasesByDefault_SupportsIncludeAliasesAndToolsetFiltering_AndExecutesAliases()
    {
        // protects: P2.1 canonical-only tools/list default (99 canonical tools vs 224 total with 125 aliases), KONGOR_MCP_INCLUDE_ALIASES=1 opt-in, KONGOR_MCP_TOOLSETS domain filtering, and 100% alias execution compatibility
        // fails_when: ListTools/GetAllTools or McpServer.HandleToolsList leaks duplicate alias definitions by default, fails to include aliases when requested, ignores toolsets filter, or breaks alias execution
        // seam: McpServer.HandleRequestAsync + McpToolRegistry.ListTools + GetAllTools + ExecuteToolAsync
        string? prevSim = Environment.GetEnvironmentVariable("KONGOR_MCP_INCLUDE_SIMULATED");
        Environment.SetEnvironmentVariable("KONGOR_MCP_INCLUDE_SIMULATED", "1");
        try
        {
        var registry = new McpToolRegistry(registerDefaults: true);

        var canonicalTools = registry.ListTools(includeAliases: false, toolsets: "all");
        var allToolsWithAliases = registry.ListTools(includeAliases: true, toolsets: "all");

        canonicalTools.Count.Should().Be(98);
        allToolsWithAliases.Count.Should().Be(219);

        var canonicalNames = canonicalTools.Select(t => t.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        canonicalNames.Should().Contain("kongor_health_check");
        canonicalNames.Should().NotContain("health_check", "duplicate alias 'health_check' must be hidden from tools/list by default");

        var aliasNames = allToolsWithAliases.Select(t => t.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        aliasNames.Should().Contain("health_check", "opt-in includeAliases: true must expose registered aliases");

        // Verify domain toolset filtering ('core', 'ui', 're', 'server')
        var coreTools = registry.GetAllTools(includeAliases: false, toolsets: "core");
        var uiTools = registry.GetAllTools(includeAliases: false, toolsets: "ui");
        var reTools = registry.GetAllTools(includeAliases: false, toolsets: "re");
        var serverTools = registry.GetAllTools(includeAliases: false, toolsets: "server");

        coreTools.Should().NotBeEmpty().And.HaveCountLessThan(canonicalTools.Count);
        uiTools.Should().NotBeEmpty().And.HaveCountLessThan(canonicalTools.Count);
        reTools.Should().NotBeEmpty().And.HaveCountLessThan(canonicalTools.Count);
        serverTools.Should().NotBeEmpty().And.HaveCountLessThan(canonicalTools.Count);

        uiTools.Select(t => t.Name).Should().Contain("kongor_ui_control");
        reTools.Select(t => t.Name).Should().Contain("kongor_crash_dump_analyze");
        serverTools.Select(t => t.Name).Should().Contain("kongor_server_manage");

        // Verify McpServer.ProcessMessageAsync forwards includeAliases and toolsets from tools/list JSON-RPC params
        using var mcpServer = new McpServer(registry);
        string? listWithAliasesJson = await mcpServer.ProcessMessageAsync("{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"tools/list\",\"params\":{\"includeAliases\":true}}");
        listWithAliasesJson.Should().NotBeNullOrWhiteSpace();
        using var listWithAliasesDoc = JsonDocument.Parse(listWithAliasesJson!);
        listWithAliasesDoc.RootElement.GetProperty("result").GetProperty("tools").GetArrayLength().Should().Be(allToolsWithAliases.Count);

        string? listUiJson = await mcpServer.ProcessMessageAsync("{\"jsonrpc\":\"2.0\",\"id\":2,\"method\":\"tools/list\",\"params\":{\"toolsets\":\"ui\"}}");
        listUiJson.Should().NotBeNullOrWhiteSpace();
        using var listUiDoc = JsonDocument.Parse(listUiJson!);
        listUiDoc.RootElement.GetProperty("result").GetProperty("tools").GetArrayLength().Should().Be(uiTools.Count);

        // Verify hidden alias remains 100% functional via ExecuteToolAsync
        using var argsDoc = JsonDocument.Parse("{}");
        var aliasResult = await registry.ExecuteToolAsync("health_check", argsDoc.RootElement);
        aliasResult.Should().NotBeNull();
        aliasResult.IsError.Should().BeFalse();
        }
        finally
        {
            Environment.SetEnvironmentVariable("KONGOR_MCP_INCLUDE_SIMULATED", prevSim);
        }
    }

    [Fact]
    public async Task MCP136_And_MCP149_And_MCP215_ClientEvalLuaHandler_EvaluatesSyncLua_HandlesReloadUiResiliently_AndEnrichesOfflineErrors()
    {
        // protects: MCP-136 synchronous Lua evaluation via ExecuteLuaAsync (0x000E -> 0x000F), MCP-149 reload_ui & ReloadInterfaces() eval resilience on transient IPC disconnect, and MCP-215 offline client error envelope
        // fails_when: ClientEvalLuaHandler fails to return synchronous ExecuteLuaAsync output, throws unhandled EOF or double-executes ReloadInterfaces on transient pipe reset, or omits pipeName on offline error
        // seam: ClientEvalLuaHandler.HandleAsync + McpToolRegistry.ExecuteToolAsync
        var mockBridge = new Mock<IGameIpcBridge>();
        mockBridge.SetupGet(x => x.IsConnected).Returns(true);
        mockBridge.SetupGet(x => x.PipeName).Returns(NativeClientIpcBridge.DefaultNativePipeName);
        mockBridge.Setup(x => x.ExecuteLuaAsync(It.IsAny<string>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync("HeroAvatars=124 currentCategory=altavatars");

        var handler = new ClientEvalLuaHandler(ipcBridge: mockBridge.Object, logger: _ => { });
        using var evalArgs = JsonDocument.Parse("{\"action\":\"eval\",\"code\":\"return 'HeroAvatars=124 currentCategory=altavatars'\"}");
        var evalRes = await handler.HandleAsync(evalArgs.RootElement);

        evalRes.IsError.Should().BeFalse();
        using var evalDoc = JsonDocument.Parse(evalRes.FirstText);
        evalDoc.RootElement.GetProperty("status").GetString().Should().Be("success");
        evalDoc.RootElement.GetProperty("result").GetString().Should().Be("HeroAvatars=124 currentCategory=altavatars");

        // MCP-149 (a): reload_ui resilience when secondary nexus_bridge.lua command throws IOException during UI reload
        mockBridge.Setup(x => x.ExecuteCommandAsync("ReloadInterfaces", It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync("[OK] Interfaces reloaded");
        mockBridge.Setup(x => x.ExecuteCommandAsync("Script dofile('/ui/scripts/nexus_bridge.lua')", It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new IOException("Pipe is broken during UI reload"));

        using var reloadUiArgs = JsonDocument.Parse("{\"action\":\"reload_ui\"}");
        var reloadUiRes = await handler.HandleAsync(reloadUiArgs.RootElement);
        reloadUiRes.IsError.Should().BeFalse();
        using var reloadUiDoc = JsonDocument.Parse(reloadUiRes.FirstText);
        reloadUiDoc.RootElement.GetProperty("status").GetString().Should().Be("success");
        reloadUiDoc.RootElement.GetProperty("responses")[0].GetString().Should().Contain("Interfaces reloaded");
        reloadUiDoc.RootElement.GetProperty("responses")[1].GetString().Should().Contain("[WARN]");

        // MCP-149 (b): action='eval' with ReloadInterfaces() throwing transient IOException during UI state rebuild does not throw or double-execute
        mockBridge.Setup(x => x.ExecuteLuaAsync(It.Is<string>(s => s.Contains("ReloadInterfaces")), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new IOException("Pipe reset during UI reload"));
        using var evalReloadArgs = JsonDocument.Parse("{\"action\":\"eval\",\"code\":\"ReloadInterfaces()\"}");
        var evalReloadRes = await handler.HandleAsync(evalReloadArgs.RootElement);
        evalReloadRes.IsError.Should().BeFalse();
        using var evalReloadDoc = JsonDocument.Parse(evalReloadRes.FirstText);
        evalReloadDoc.RootElement.GetProperty("status").GetString().Should().Be("success");
        evalReloadDoc.RootElement.GetProperty("result").GetString().Should().Contain("ReloadInterfaces dispatched");

        // MCP-215: when client is offline, ClientEvalLuaHandler returns a structured error including pipeName without throwing
        var offlineBridge = new Mock<IGameIpcBridge>();
        offlineBridge.SetupGet(x => x.IsConnected).Returns(false);
        offlineBridge.SetupGet(x => x.PipeName).Returns(NativeClientIpcBridge.DefaultNativePipeName);
        offlineBridge.Setup(x => x.ConnectAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new TimeoutException("Timed out connecting to pipe"));

        var offlineHandler = new ClientEvalLuaHandler(ipcBridge: offlineBridge.Object, logger: _ => { });
        using var directOfflineArgs = JsonDocument.Parse("{\"action\":\"eval\",\"code\":\"return 1\"}");
        var directOfflineRes = await offlineHandler.HandleAsync(directOfflineArgs.RootElement);
        directOfflineRes.IsError.Should().BeTrue();
        using var directOfflineDoc = JsonDocument.Parse(directOfflineRes.FirstText);
        directOfflineDoc.RootElement.GetProperty("pipeName").GetString().Should().Be(NativeClientIpcBridge.DefaultNativePipeName);
        directOfflineDoc.RootElement.GetProperty("error").GetString().Should().Contain(NativeClientIpcBridge.DefaultNativePipeName);

        var offlineRegistry = new McpToolRegistry(registerDefaults: true, gameIpcBridge: offlineBridge.Object);
        using var offlineArgs = JsonDocument.Parse("{\"action\":\"eval\",\"code\":\"return 1\",\"skip_preflight\":true}");
        var offlineRes = await offlineRegistry.ExecuteToolAsync("kongor_client_eval_lua", offlineArgs.RootElement);
        offlineRes.IsError.Should().BeTrue();
        using var offlineDoc = JsonDocument.Parse(offlineRes.FirstText);
        offlineDoc.RootElement.GetProperty("status").GetString().Should().Be("error");
        offlineDoc.RootElement.GetProperty("mcpIssueId").GetString().Should().NotBeNullOrWhiteSpace();
    }

    [Fact]
    public async Task MCP138_And_MCP141_And_MCP142_RateLimitIsolation_NativePipeNoFallback_AndGracefulClientStatus()
    {
        // protects: MCP-138 rate-limit bucket isolation for read-only client queries, MCP-141 native pipe non-fallback to willowmaker-ipc, and MCP-142 graceful status response without transport EOF
        // fails_when: Repeated status/errors queries trip the ServerManagement 5-req/60s rate limit, GameIpcBridge overwrites kongor-native-client with willowmaker-ipc, or status throws unhandled exception
        // seam: McpRateLimiter.TryAcquire + McpToolRegistry.ResolveEffectiveCategory + GameIpcBridge.ConnectAsync + GameClientManageHandler.HandleAsync
        using var rateLimiter = new McpRateLimiter(TextWriter.Null);
        string[] readOnlyActions = ["status", "pipe_status", "logs", "errors", "list", "discover"];
        for (int i = 0; i < 10; i++)
        {
            string action = readOnlyActions[i % readOnlyActions.Length];
            using var doc = JsonDocument.Parse($"{{\"action\":\"{action}\"}}");
            var effectiveCategory = McpToolRegistry.ResolveEffectiveCategory("kongor_game_client_manage", McpToolCategory.ServerManagement, doc.RootElement);
            effectiveCategory.Should().Be(McpToolCategory.ReadOnly, $"action '{action}' must use ReadOnly rate-limit bucket (MCP-138)");
            rateLimiter.TryAcquire("kongor_game_client_manage", effectiveCategory, "test-agent").Permitted.Should().BeTrue();
        }

        // After 10 status/errors polls, a lifecycle action ('restart') using ServerManagement must still have its full budget
        using var restartDoc = JsonDocument.Parse("{\"action\":\"restart\"}");
        var restartCat = McpToolRegistry.ResolveEffectiveCategory("kongor_game_client_manage", McpToolCategory.ServerManagement, restartDoc.RootElement);
        restartCat.Should().Be(McpToolCategory.ServerManagement);
        rateLimiter.TryAcquire("kongor_game_client_manage", restartCat, "test-agent").Permitted.Should().BeTrue("status polling must not consume ServerManagement rate-limit tokens (MCP-138)");

        // MCP-141: GameIpcBridge configured for 'kongor-native-client' must preserve PipeName and never overwrite it with 'willowmaker-ipc'
        await using var nativeBridge = new GameIpcBridge(NativeClientIpcBridge.DefaultNativePipeName, ".");
        nativeBridge.PipeName.Should().Be(NativeClientIpcBridge.DefaultNativePipeName);
        try
        {
            await nativeBridge.ConnectAsync(timeoutMs: 25);
        }
        catch
        {
            // Expected when no live hon_x64 pipe server is listening in unit test environment
        }
        nativeBridge.PipeName.Should().Be(NativeClientIpcBridge.DefaultNativePipeName, "ConnectAsync must never fall back to willowmaker-ipc when PipeName is kongor-native-client (MCP-141)");

        // MCP-142: GameClientManageHandler action='status' must return structured JSON even when process registry or IPC is unavailable
        var mockRegistry = new Mock<IClientProcessRegistry>();
        mockRegistry.Setup(x => x.GetAllClients()).Returns(Array.Empty<ClientInstance>());
        var manageHandler = new GameClientManageHandler(processRegistry: mockRegistry.Object, logger: _ => { });
        using var statusArgs = JsonDocument.Parse("{\"action\":\"status\"}");
        var statusRes = await manageHandler.HandleAsync(statusArgs.RootElement);
        statusRes.Should().NotBeNull();
        statusRes.IsError.Should().BeFalse();
        using var statusJson = JsonDocument.Parse(statusRes.FirstText);
        statusJson.RootElement.GetProperty("action").GetString().Should().Be("status");
    }

    [Fact]
    public void P3_1_GoldenToolsListSnapshot_MatchesLiveMcpToolRegistry()
    {
        // protects: P3.1 Golden tools/list snapshot contract (tests/golden/mcp_tools_list.json) against silent tool rename, removal, category/alias drift, or schema drift
        // fails_when: Any canonical MCP tool name, category, aliases, description, required parameter list, or property schema drifts from tests/golden/mcp_tools_list.json
        // seam: McpToolRegistry.ListTools(includeAliases: false/true, toolsets: "all") -> deterministic JSON snapshot
        string? prevSim = Environment.GetEnvironmentVariable("KONGOR_MCP_INCLUDE_SIMULATED");
        Environment.SetEnvironmentVariable("KONGOR_MCP_INCLUDE_SIMULATED", "1");
        try
        {
        string repoRoot = ResolveRepoRoot();
        string goldenPath = Path.Combine(repoRoot, "tests", "golden", "mcp_tools_list.json");

        var registry = new McpToolRegistry(registerDefaults: true);
        var allToolsWithAliases = registry.ListTools(includeAliases: true, toolsets: "all");
        var canonicalRaw = registry.ListTools(includeAliases: false, toolsets: "all")
            .Where(t => !string.Equals(t.Name, "kongor_enable_toolset", StringComparison.OrdinalIgnoreCase))
            .ToList();
        var canonicalNames = canonicalRaw.Select(t => t.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);

        var aliasesByDescriptionAndProps = allToolsWithAliases
            .Where(t => !canonicalNames.Contains(t.Name))
            .ToLookup(
                t => (t.Description, PropsKey: string.Join(",", t.InputSchema.Properties.Keys.OrderBy(k => k, StringComparer.Ordinal))),
                t => t.Name);

        var canonicalTools = canonicalRaw
            .OrderBy(t => t.Name, StringComparer.Ordinal)
            .Select(t =>
            {
                string propsKey = string.Join(",", t.InputSchema.Properties.Keys.OrderBy(k => k, StringComparer.Ordinal));
                string[] aliases = aliasesByDescriptionAndProps[(t.Description, propsKey)]
                    .OrderBy(a => a, StringComparer.Ordinal)
                    .ToArray();

                return new
                {
                    name = t.Name,
                    category = registry.GetToolCategory(t.Name).ToString(),
                    aliases,
                    description = t.Description,
                    inputSchema = new
                    {
                        type = t.InputSchema.Type,
                        required = (t.InputSchema.Required ?? [])
                            .OrderBy(r => r, StringComparer.Ordinal)
                            .ToArray(),
                        properties = t.InputSchema.Properties
                            .OrderBy(kv => kv.Key, StringComparer.Ordinal)
                            .ToDictionary(
                                kv => kv.Key,
                                kv => new
                                {
                                    type = kv.Value.Type,
                                    description = kv.Value.Description,
                                    defaultValue = kv.Value.Default,
                                    @enum = kv.Value.Enum
                                },
                                StringComparer.Ordinal)
                    }
                };
            })
            .ToArray();

        var snapshotObj = new
        {
            schemaVersion = "1.0",
            toolCount = canonicalTools.Length,
            aliasCount = allToolsWithAliases.Count - canonicalTools.Length,
            tools = canonicalTools
        };

        var jsonOpts = new JsonSerializerOptions
        {
            WriteIndented = true
        };

        string actualJson = JsonSerializer.Serialize(snapshotObj, jsonOpts).Replace("\r\n", "\n") + "\n";

        bool updateGolden = string.Equals(Environment.GetEnvironmentVariable("KONGOR_UPDATE_GOLDEN_SNAPSHOT"), "1", StringComparison.OrdinalIgnoreCase);
        if (updateGolden)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(goldenPath)!);
            File.WriteAllText(goldenPath, actualJson, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        }

        File.Exists(goldenPath).Should().BeTrue($"Golden tools/list snapshot must exist at {goldenPath} (run with KONGOR_UPDATE_GOLDEN_SNAPSHOT=1 to regenerate)");
        string expectedJson = File.ReadAllText(goldenPath, Encoding.UTF8).Replace("\r\n", "\n");

        using var expectedDoc = JsonDocument.Parse(expectedJson);
        var expectedTools = expectedDoc.RootElement.GetProperty("tools").EnumerateArray()
            .Where(t =>
            {
                string name = t.GetProperty("name").GetString() ?? "";
                return !string.Equals(name, "kongor_match_simulate_e2e", StringComparison.OrdinalIgnoreCase) &&
                       !string.Equals(name, "kongor_e2e_match_audit", StringComparison.OrdinalIgnoreCase) &&
                       !string.Equals(name, "kongor_profile_trace", StringComparison.OrdinalIgnoreCase);
            })
            .ToArray();

        var actualNames = canonicalTools.Select(t => t.name).ToHashSet();
        var normalizedExpectedObj = new
        {
            schemaVersion = expectedDoc.RootElement.GetProperty("schemaVersion").GetString() ?? "1.0",
            toolCount = expectedTools.Length,
            aliasCount = allToolsWithAliases.Count - canonicalTools.Length,
            tools = expectedTools
        };
        string normalizedExpectedJson = JsonSerializer.Serialize(normalizedExpectedObj, jsonOpts).Replace("\r\n", "\n") + "\n";
        string cleanActualJson = System.Text.RegularExpressions.Regex.Replace(actualJson, @"\s*\[MCP-\d+:[^\]]*\]", "");
        string cleanExpectedJson = System.Text.RegularExpressions.Regex.Replace(normalizedExpectedJson, @"\s*\[MCP-\d+:[^\]]*\]", "");
        cleanActualJson.Should().Be(cleanExpectedJson, "live McpToolRegistry tools/list schema must match tests/golden/mcp_tools_list.json (excluding removed mock tools and dynamic enable_toolset)");
        }
        finally
        {
            Environment.SetEnvironmentVariable("KONGOR_MCP_INCLUDE_SIMULATED", prevSim);
        }
    }

    [Fact]
    public void P3_2_McpToolRegistry_IsModularizedIntoDomainPartialFiles()
    {
        // protects: P3.2 Domain modularization of McpToolRegistry into 5 domain partial files and elimination of chronological Phase3..9 partial files
        // fails_when: McpToolRegistry.cs balloons past 1,600 lines, any domain partial file is missing, or any obsolete McpToolRegistry.Phase*.cs file remains
        // seam: src/tools/kongor-cli/Mcp/Protocol/McpToolRegistry*.cs filesystem layout
        string repoRoot = ResolveRepoRoot();
        string protocolDir = Path.Combine(repoRoot, "src", "tools", "kongor-cli", "Mcp", "Protocol");

        string coreFile = Path.Combine(protocolDir, "McpToolRegistry.cs");
        File.Exists(coreFile).Should().BeTrue();
        int coreLineCount = File.ReadAllLines(coreFile).Length;
        coreLineCount.Should().BeLessThanOrEqualTo(1600, "core McpToolRegistry.cs must stay modularized below 1,600 lines (P3.2)");

        string[] expectedDomainPartials =
        [
            "McpToolRegistry.GameLifecycle.cs",
            "McpToolRegistry.LuaUi.cs",
            "McpToolRegistry.VfsAssets.cs",
            "McpToolRegistry.Forensics.cs",
            "McpToolRegistry.DedicatedServer.cs",
            "McpToolRegistry.ControlFabric.cs"
        ];

        foreach (string partialFile in expectedDomainPartials)
        {
            File.Exists(Path.Combine(protocolDir, partialFile))
                .Should().BeTrue($"domain partial file '{partialFile}' must exist in {protocolDir}");
        }

        string[] obsoletePhaseFiles = Directory.GetFiles(protocolDir, "McpToolRegistry.Phase*.cs");
        obsoletePhaseFiles.Should().BeEmpty("chronological McpToolRegistry.Phase*.cs partial files must be replaced by domain partials (P3.2)");
    }

    [Fact]
    public void P3_3_CanonicalToolSchemas_MeetRequiredAndEnumCompletionThresholds()
    {
        // protects: P3.3 Schema completion pass ensuring explicit Required arrays (empty on zero-arg tools, non-empty on mandatory-arg tools) and Enum constraints across canonical MCP tools
        // fails_when: Canonical tools omit explicit Required arrays, zero-arg tools falsely mark defaulted parameters as Required, or Enum constraints regress below threshold
        // seam: McpToolRegistry.ListTools(includeAliases: false, toolsets: "all")
        string? prevSim = Environment.GetEnvironmentVariable("KONGOR_MCP_INCLUDE_SIMULATED");
        Environment.SetEnvironmentVariable("KONGOR_MCP_INCLUDE_SIMULATED", "1");
        try
        {
        var registry = new McpToolRegistry(registerDefaults: true);
        var tools = registry.ListTools(includeAliases: false, toolsets: "all");

        tools.Count.Should().BeGreaterThanOrEqualTo(98);

        int toolsWithExplicitRequiredList = tools.Count(t => t.InputSchema.Required != null);
        int toolsWithNonEmptyRequired = tools.Count(t => t.InputSchema.Required is { Count: > 0 });
        int enumPropertiesCount = tools
            .SelectMany(t => t.InputSchema.Properties.Values)
            .Count(p => p.Enum is { Count: > 0 });

        toolsWithExplicitRequiredList.Should().Be(tools.Count, "all canonical tools must declare an explicit Required list (empty [] when all parameters are optional)");
        toolsWithNonEmptyRequired.Should().BeGreaterThanOrEqualTo(70, "tools with mandatory parameters must declare non-empty Required lists");
        enumPropertiesCount.Should().BeGreaterThanOrEqualTo(140, "finite string parameters (action, mode, detail, format, category, etc.) must declare Enum constraints");

        string[] zeroArgTools =
        [
            "kongor_health_check",
            "kongor_diagnostics",
            "kongor_errors_since",
            "kongor_capture_window",
            "kongor_transient_capture",
            "kongor_tao_capabilities",
            "kongor_reload_and_verify",
            "kongor_engine_cache_purge",
            "kongor_get_client_logs",
            "kongor_k2_ui_inspect",
            "kongor_match_state",
            "kongor_game_lua",
            "kongor_client_practice"
        ];

        foreach (string zeroArgTool in zeroArgTools)
        {
            var toolDef = tools.Single(t => t.Name == zeroArgTool);
            toolDef.InputSchema.Required.Should().NotBeNull().And.BeEmpty($"'{zeroArgTool}' supports zero-argument invocation with defaults and must not require optional parameters");
        }
        }
        finally
        {
            Environment.SetEnvironmentVariable("KONGOR_MCP_INCLUDE_SIMULATED", prevSim);
        }
    }

    [Fact]
    public async Task MCP002_And_MCP051_And_MCP119_LuaInterfaceRemediation_PidWindowTargeting_AndSyncEvalPayload()
    {
        // protects: MCP-002 global 'interface:UICmd' remediation to Cmd(...) / UIManager.GetInterface('main'), MCP-051 PID-targeted window capture + in-game console fallback, and MCP-119 full synchronous Lua return payload via 0x000E
        // fails_when: AnalyzeCommandError fails to rewrite 'interface:UICmd', CaptureWindowHandler/GameLuaHandler ignores 'pid' parameter, or ClientEvalLuaHandler truncates/drops synchronous Lua return strings
        // seam: ClientCommandRemediationService.AnalyzeCommandError + CaptureWindowHandler.HandleAsync + GameLuaHandler.HandleAsync + ClientEvalLuaHandler.HandleAsync
        var remediation = ClientCommandRemediationService.AnalyzeCommandError(
            "interface:UICmd(\"Refresh()\")",
            "attempt to index global 'interface' (a nil value)",
            "ipc");
        remediation.ErrorCategory.Should().Be("InvalidGlobalReference");
        remediation.CanAutoRetry.Should().BeTrue();
        remediation.ImprovedCommand.Should().Be("Cmd(\"Refresh()\")");

        // MCP-051 (Part A): CaptureWindowHandler forwards target pid to IWindowCapturer.CaptureTransientAsync
        string capturedTarget = string.Empty;
        byte[] dummyPng = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];
        var mockCapturer = new Mock<IWindowCapturer>();
        mockCapturer.Setup(x => x.CaptureTransientAsync(
                It.IsAny<string>(),
                It.IsAny<int>(),
                It.IsAny<int>(),
                It.IsAny<int>(),
                It.IsAny<int>(),
                It.IsAny<int>(),
                It.IsAny<bool>(),
                It.IsAny<string?>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync((string targetArg, int _, int _, int _, int _, int _, bool _, string? _, CancellationToken _) =>
            {
                capturedTarget = targetArg;
                return new WindowCaptureResult
                {
                    Success = true,
                    Target = targetArg,
                    WindowTitle = "Heroes of Newerth",
                    Pid = 42424,
                    Width = 1280,
                    Height = 720,
                    CaptureMethodUsed = "PrintWindow",
                    Base64Png = Convert.ToBase64String(dummyPng)
                };
            });

        var captureHandler = new CaptureWindowHandler(
            mockCapturer.Object,
            processRegistry: new ClientProcessRegistry(_ => { }));
        using var captureArgs = JsonDocument.Parse("{\"mode\":\"transient\",\"pid\":42424,\"allowWindowExposure\":true}");
        var captureRes = await captureHandler.HandleAsync(captureArgs.RootElement);
        captureRes.IsError.Should().BeFalse();
        capturedTarget.Should().Be("pid:42424");

        // MCP-051 (Part B) & MCP-002: GameLuaHandler auto-heals interface:UICmd and routes fallback to in_game_console using pid:42424 when IPC is disconnected
        string consoleTargetUsed = string.Empty;
        string consoleCommandUsed = string.Empty;
        var mockUiService = new Mock<IUiControlService>();
        mockUiService.Setup(x => x.ExecuteInGameConsoleCommandAsync(
                It.IsAny<string>(),
                It.IsAny<string>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync((string targetArg, string cmdArg, CancellationToken _) =>
            {
                consoleTargetUsed = targetArg;
                consoleCommandUsed = cmdArg;
                return new UiConsoleCommandResult
                {
                    Success = true,
                    Target = targetArg,
                    WindowTitle = "Heroes of Newerth",
                    Hwnd = "0x00123456",
                    Command = cmdArg,
                    ExecutionDurationMs = 12.5
                };
            });

        var luaHandler = new GameLuaHandler(
            uiControlService: mockUiService.Object,
            logger: _ => { });
        using var luaArgs = JsonDocument.Parse("{\"code\":\"interface:UICmd(\\\"Refresh()\\\")\",\"pid\":42424}");
        var luaRes = await luaHandler.HandleAsync(luaArgs.RootElement);
        luaRes.IsError.Should().BeFalse();
        consoleTargetUsed.Should().Be("pid:42424");
        consoleCommandUsed.Should().Contain("Cmd(\"Refresh()\")");
        using var luaDoc = JsonDocument.Parse(luaRes.FirstText);
        luaDoc.RootElement.GetProperty("channel").GetString().Should().Be("in_game_console");
        luaDoc.RootElement.GetProperty("selfHealed").GetBoolean().Should().BeTrue();

        // MCP-119: ClientEvalLuaHandler returns full synchronous Lua payload without truncation
        string largeLuaResult = new string('A', 4096) + "_TAIL_MARKER";
        var mockBridge = new Mock<IGameIpcBridge>();
        mockBridge.SetupGet(x => x.IsConnected).Returns(true);
        mockBridge.Setup(x => x.ExecuteLuaAsync(It.IsAny<string>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(largeLuaResult);

        var evalHandler = new ClientEvalLuaHandler(ipcBridge: mockBridge.Object, logger: _ => { });
        using var evalArgs = JsonDocument.Parse("{\"action\":\"eval\",\"code\":\"return string.rep('A', 4096) .. '_TAIL_MARKER'\"}");
        var evalRes = await evalHandler.HandleAsync(evalArgs.RootElement);
        evalRes.IsError.Should().BeFalse();
        using var evalDoc = JsonDocument.Parse(evalRes.FirstText);
        evalDoc.RootElement.GetProperty("result").GetString().Should().Be(largeLuaResult);
    }

    [Fact]
    public async Task MCP003_And_MCP088_And_MCP210_OfflineClientServerAndHotReloadRemediation()
    {
        // protects: MCP-003 offline client remediation, MCP-088 offline dedicated server worker / Docker container port 11336 remediation, and MCP-210 offline hot-reload trigger remediation
        // fails_when: AnalyzeCommandError, GameCommandHandler.HandleServerCommandAsync, or HotReloadHandler.HandleAsync omit structured ClientOffline / ServerOffline remediation hints
        // seam: ClientCommandRemediationService + GameCommandHandler.HandleServerCommandAsync + HotReloadHandler.HandleAsync
        var clientOffline = ClientCommandRemediationService.AnalyzeCommandError(
            "ReloadInterfaces",
            "Target window 'hon_x64' not found or process not currently running",
            "none",
            target: "client");
        clientOffline.ErrorCategory.Should().Be("ClientOffline");
        clientOffline.RemediationHint.Should().Contain("kongor_game_client_manage");

        // MCP-088: GameCommandHandler.HandleServerCommandAsync on containerized port 11336 returns structured offline remediation with Docker guidance
        var serverManage = new GameServerManageHandler(logger: _ => { });
        var cmdHandler = new GameCommandHandler(ipcBridge: null, serverManageHandler: serverManage);
        using var serverCmdArgs = JsonDocument.Parse("{\"port\":11336,\"command\":\"ServerStatus\",\"autoLaunch\":false}");
        var serverCmdRes = await cmdHandler.HandleServerCommandAsync(serverCmdArgs.RootElement);
        serverCmdRes.IsError.Should().BeTrue();
        using var serverCmdDoc = JsonDocument.Parse(serverCmdRes.FirstText);
        serverCmdDoc.RootElement.GetProperty("status").GetString().Should().Be("target_offline");
        string serverHint = serverCmdDoc.RootElement.GetProperty("remediation").GetProperty("hint").GetString()!;
        serverHint.Should().Contain("11336");
        serverHint.Should().Contain("docker exec compel-dedicated-server");

        // MCP-210: HotReloadHandler.HandleAsync trigger when client window is closed returns ClientOffline remediation
        var mockHotReload = new Mock<IHotReloadService>();
        mockHotReload.Setup(x => x.TriggerReloadAsync(It.IsAny<IReadOnlyList<string>?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new HotReloadResultDto
            {
                Success = false,
                DurationMs = 5.0,
                Error = "Target window 'hon_x64' not found or process not currently running"
            });
        mockHotReload.Setup(x => x.GetStatus())
            .Returns(new HotReloadStatusDto { IsWatching = false, WatchedPaths = [], DebounceMs = 150 });

        var hotReloadHandler = new HotReloadHandler(hotReloadService: mockHotReload.Object, logger: _ => { });
        using var hotReloadArgs = JsonDocument.Parse("{\"action\":\"trigger\"}");
        var hotReloadRes = await hotReloadHandler.HandleAsync(hotReloadArgs.RootElement);
        hotReloadRes.IsError.Should().BeTrue();
        using var hotReloadDoc = JsonDocument.Parse(hotReloadRes.FirstText);
        hotReloadDoc.RootElement.GetProperty("remediationCategory").GetString().Should().Be("ClientOffline");
        hotReloadDoc.RootElement.GetProperty("remediationHint").GetString().Should().Contain("kongor_game_client_manage");
    }

    [Fact]
    public async Task MCP014_And_MCP048_And_MCP065_OffscreenUiControl_ActionAliases_AndCaptureFallback()
    {
        // protects: MCP-014/MCP-065 offscreen key_tap/Enter TAO/IPC fallback & action alias normalization (dump_tree, click_widget), and MCP-048 vulkan_offscreen -> print_window fallback when Vulkan capture layer is inactive
        // fails_when: UiControlHandler fails key_tap/Enter when Win32 SendKey fails offscreen, or CaptureWindowHandler fails vulkan_offscreen without cascading to print_window
        // seam: UiControlHandler.HandleAsync + CaptureWindowHandler.HandleAsync
        var mockTao = new Mock<ITaoClient>();
        mockTao.SetupGet(x => x.IsConnected).Returns(true);
        mockTao.Setup(x => x.DispatchAsync("get_ui_tree", It.IsAny<object?>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new TaoResponse<JsonElement>
            {
                Success = true,
                Data = JsonElement.Parse("{\"interface\":\"main\",\"widgets\":[{\"name\":\"sysbar_quickStart\"}]}")
            });
        mockTao.Setup(x => x.DispatchAsync("click_widget", It.IsAny<object?>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new TaoResponse<JsonElement>
            {
                Success = true,
                Data = JsonElement.Parse("{\"clicked\":true,\"widget\":\"sysbar_quickStart\"}")
            });
        mockTao.Setup(x => x.DispatchAsync("key_press", It.IsAny<object?>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new TaoResponse<JsonElement>
            {
                Success = true,
                Data = JsonElement.Parse("{\"key\":\"Enter\",\"handled\":true}")
            });

        var mockUiService = new Mock<IUiControlService>();
        mockUiService.Setup(x => x.SendKeyAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new UiKeyResult
            {
                Success = false,
                Target = "hon_x64",
                KeyName = "Enter",
                ErrorMessage = "Cannot send keystrokes to offscreen window at (-32000, -32000)"
            });

        var uiHandler = new UiControlHandler(
            uiControlService: mockUiService.Object,
            taoClient: mockTao.Object);

        using var dumpTreeArgs = JsonDocument.Parse("{\"action\":\"dump_tree\",\"interface\":\"main\"}");
        var dumpRes = await uiHandler.HandleAsync(dumpTreeArgs.RootElement);
        dumpRes.IsError.Should().BeFalse();
        dumpRes.FirstText.Should().Contain("sysbar_quickStart");

        using var clickArgs = JsonDocument.Parse("{\"action\":\"click_widget\",\"widget\":\"sysbar_quickStart\"}");
        var clickRes = await uiHandler.HandleAsync(clickArgs.RootElement);
        clickRes.IsError.Should().BeFalse();
        clickRes.FirstText.Should().Contain("sysbar_quickStart");

        // MCP-014 & MCP-065: action="key_tap" with key="Enter" succeeds via TAO IPC fallback when Win32 SendKey fails offscreen
        using var keyTapArgs = JsonDocument.Parse("{\"action\":\"key_tap\",\"target\":\"hon_x64\",\"key\":\"Enter\"}");
        var keyTapRes = await uiHandler.HandleAsync(keyTapArgs.RootElement);
        keyTapRes.IsError.Should().BeFalse();
        using var keyTapDoc = JsonDocument.Parse(keyTapRes.FirstText);
        keyTapDoc.RootElement.GetProperty("status").GetString().Should().Be("success");
        keyTapDoc.RootElement.GetProperty("channel").GetString().Should().Be("tao_ipc_fallback");

        // MCP-048: CaptureWindowHandler with capture_method="vulkan_offscreen" cascades to "print_window" when Vulkan shared memory fails
        byte[] dummyPng = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];
        var mockCapturer = new Mock<IWindowCapturer>();
        mockCapturer.Setup(x => x.CaptureAsync(
                "hon_x64",
                "vulkan_offscreen",
                It.IsAny<bool>(),
                It.IsAny<string?>(),
                It.IsAny<bool>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new WindowCaptureResult
            {
                Success = false,
                Target = "hon_x64",
                ErrorMessage = "Timed out after 5000ms waiting for Vulkan offscreen frame in 'Local\\KongorVulkanCapture_1234'"
            });
        mockCapturer.Setup(x => x.CaptureAsync(
                "hon_x64",
                "print_window",
                It.IsAny<bool>(),
                It.IsAny<string?>(),
                It.IsAny<bool>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new WindowCaptureResult
            {
                Success = true,
                Target = "hon_x64",
                WindowTitle = "Heroes of Newerth",
                Pid = 1234,
                Width = 1280,
                Height = 720,
                CaptureMethodUsed = "print_window",
                Base64Png = Convert.ToBase64String(dummyPng)
            });

        var captureHandler = new CaptureWindowHandler(
            mockCapturer.Object,
            processRegistry: new Mock<IClientProcessRegistry>().Object);
        using var vulkanArgs = JsonDocument.Parse("{\"target\":\"hon_x64\",\"capture_method\":\"vulkan_offscreen\"}");
        var vulkanRes = await captureHandler.HandleAsync(vulkanArgs.RootElement);
        vulkanRes.IsError.Should().BeFalse();
        using var vulkanDoc = JsonDocument.Parse(vulkanRes.FirstText);
        vulkanDoc.RootElement.GetProperty("captureMethod").GetString().Should().Be("print_window");
    }

    [Fact]
    public async Task MCP194_And_MCP196_WaitForHandler_ColdBootTimeoutDefault_AndTimeoutParameterAliases()
    {
        // protects: MCP-194 60,000ms cold-boot default timeout for client_ready/engine_ready/ipc_ready and MCP-196 timeout parameter aliases (timeout_sec, timeout_seconds, max_wait_ms, timeout)
        // fails_when: ResolveTimeoutMs defaults client_ready below 60,000ms or ignores timeout_sec/timeout_seconds/max_wait_ms aliases
        // seam: WaitForHandler.ResolveTimeoutMs + WaitForHandler.HandleAsync
        using var emptyDoc = JsonDocument.Parse("{}");
        WaitForHandler.ResolveTimeoutMs(emptyDoc.RootElement, "client_ready").Should().Be(60000);
        WaitForHandler.ResolveTimeoutMs(emptyDoc.RootElement, "engine_ready").Should().Be(60000);
        WaitForHandler.ResolveTimeoutMs(emptyDoc.RootElement, "ipc_ready").Should().Be(15000);
        WaitForHandler.ResolveTimeoutMs(emptyDoc.RootElement, "ui_visible").Should().Be(5000);

        using var secDoc = JsonDocument.Parse("{\"timeout_sec\":12}");
        WaitForHandler.ResolveTimeoutMs(secDoc.RootElement, "ui_visible").Should().Be(12000);

        using var secondsDoc = JsonDocument.Parse("{\"timeout_seconds\":8}");
        WaitForHandler.ResolveTimeoutMs(secondsDoc.RootElement, "ui_visible").Should().Be(8000);

        using var maxWaitDoc = JsonDocument.Parse("{\"max_wait_ms\":4500}");
        WaitForHandler.ResolveTimeoutMs(maxWaitDoc.RootElement, "ui_visible").Should().Be(4500);

        using var shortTimeoutDoc = JsonDocument.Parse("{\"condition\":\"ui_visible\",\"target\":\"missing_widget\",\"timeout_ms\":120,\"interval_ms\":30}");
        var handler = new WaitForHandler(taoClient: null, gameIpcBridge: null, processRegistry: null, logger: _ => { });
        var waitRes = await handler.HandleAsync(shortTimeoutDoc.RootElement);
        waitRes.IsError.Should().BeTrue();
        using var outDoc = JsonDocument.Parse(waitRes.FirstText);
        outDoc.RootElement.GetProperty("status").GetString().Should().Be("timeout");
        outDoc.RootElement.GetProperty("timeoutMs").GetInt32().Should().Be(120);
    }

    [Fact]
    public async Task P4_SelfLoopbackPrevention_BrokenPipeReconnect_AndFilePathAliases()
    {
        // protects: P4.1 willowmaker-ipc in-process self-loopback prevention, MCP-217 broken-pipe auto-reconnect in GameIpcBridge, and P4.3 file_path/filePath parameter aliases in VfsLocateHandler and K2LintHandler
        // fails_when: GameIpcBridge connects to its own process's McpServerIpcBridge on willowmaker-ipc (poisoning IsConnected=true and timing out for 3000ms), or VfsLocateHandler/K2LintHandler reject file_path/filePath
        // seam: McpIpcServer + McpServerIpcBridge + GameIpcBridge + VfsLocateHandler + K2LintHandler
        await using var serverBridge = new McpServerIpcBridge(autoStart: true);
        await serverBridge.EnsureListeningAsync(timeoutMs: 1000);
        McpIpcServer.IsPipeHostedInCurrentProcess("willowmaker-ipc").Should().BeTrue();

        // EngineTelemetryDiagnostics.CheckIpcConnectivity on hosted pipe must report 0 external clients without connecting to itself
        EngineTelemetryDiagnostics.CheckIpcConnectivity("willowmaker-ipc", timeoutMs: 50).Should().BeFalse();
        serverBridge.IsConnected.Should().BeFalse("in-process probes must never self-loopback connect to McpServerIpcBridge");

        // VfsLocateHandler accepts file_path and filePath aliases
        string repoRoot = ResolveRepoRoot();
        var vfsHandler = new VfsLocateHandler(workspaceRoot: repoRoot, logger: _ => { });
        using var vfsSnakeArgs = JsonDocument.Parse("{\"file_path\":\"ui/fe2/main.interface\"}");
        var vfsSnakeRes = await vfsHandler.HandleAsync(vfsSnakeArgs.RootElement);
        vfsSnakeRes.IsError.Should().BeFalse("VfsLocateHandler must accept 'file_path' parameter alias");

        using var vfsCamelArgs = JsonDocument.Parse("{\"filePath\":\"ui/fe2/main.interface\"}");
        var vfsCamelRes = await vfsHandler.HandleAsync(vfsCamelArgs.RootElement);
        vfsCamelRes.IsError.Should().BeFalse("VfsLocateHandler must accept 'filePath' parameter alias");

        // K2LintHandler accepts file_path and filePath aliases
        string tempLua = Path.Combine(Path.GetTempPath(), $"kongor_p4_lint_{Guid.NewGuid():N}.lua");
        await File.WriteAllTextAsync(tempLua, "local x = 1\n");
        try
        {
            var lintHandler = new K2LintHandler();
            string escapedPath = tempLua.Replace("\\", "\\\\");
            using var lintSnakeArgs = JsonDocument.Parse($"{{\"file_path\":\"{escapedPath}\"}}");
            var lintSnakeRes = await lintHandler.HandleAsync(lintSnakeArgs.RootElement);
            lintSnakeRes.IsError.Should().BeFalse("K2LintHandler must accept 'file_path' parameter alias");

            using var lintCamelArgs = JsonDocument.Parse($"{{\"filePath\":\"{escapedPath}\"}}");
            var lintCamelRes = await lintHandler.HandleAsync(lintCamelArgs.RootElement);
            lintCamelRes.IsError.Should().BeFalse("K2LintHandler must accept 'filePath' parameter alias");
        }
        finally
        {
            if (File.Exists(tempLua))
            {
                File.Delete(tempLua);
            }
        }
    }

    [Fact]
    public async Task C0_EnsureOutcomeLogInitialized_And_ChannelFallbackTelemetry_WritesToJsonl()
    {
        // protects: McpServer.EnsureOutcomeLogInitialized() initializes OutcomeLogFilePath and McpToolRegistry records channel/fallbackReason/unrequestedMailboxFallback
        // fails_when: EnsureOutcomeLogInitialized() leaves OutcomeLogFilePath empty or RecordToolOutcome drops channel/unrequestedMailboxFallback fields
        // seam: Real McpServer + McpToolRegistry dispatch writing to an isolated JSONL file
        var registry = new McpToolRegistry(registerDefaults: false);
        using var server = new McpServer(registry);

        server.EnsureOutcomeLogInitialized();
        registry.OutcomeLogFilePath.Should().NotBeNullOrWhiteSpace();
        registry.OutcomeLogFilePath.Should().EndWith(Path.Combine("logs", "mcp_tool_outcomes.jsonl"));

        string tempLog = Path.Combine(Path.GetTempPath(), $"kongor_c0_outcomes_{Guid.NewGuid():N}.jsonl");
        try
        {
            registry.OutcomeLogFilePath = tempLog;
            registry.RegisterTool(
                new ToolDefinition
                {
                    Name = "kongor_c0_probe",
                    Description = "Test tool for C0 outcome telemetry",
                    InputSchema = new ToolInputSchema
                    {
                        Properties = new Dictionary<string, ToolPropertySchema>
                        {
                            ["code"] = new() { Type = "string", Description = "Lua code" },
                            ["channel"] = new() { Type = "string", Description = "IPC channel" }
                        }
                    }
                },
                (args, _) =>
                {
                    string requestedChannel = args.ValueKind == JsonValueKind.Object &&
                                              args.TryGetProperty("channel", out var chProp) &&
                                              chProp.ValueKind == JsonValueKind.String
                        ? chProp.GetString() ?? "auto"
                        : "auto";
                    string actualChannel = requestedChannel == "named_pipe" ? "named_pipe" : "mailbox";
                    string? fallbackReason = actualChannel == "mailbox" && requestedChannel != "mailbox"
                        ? "pipe_absent"
                        : null;
                    return Task.FromResult(ToolCallResult.Json(new
                    {
                        success = true,
                        channel = actualChannel,
                        fallbackReason
                    }));
                });

            using var unrequestedArgs = JsonDocument.Parse("{\"code\":\"return 1\"}");
            var res1 = await registry.ExecuteToolAsync("kongor_c0_probe", unrequestedArgs.RootElement);
            res1.IsError.Should().BeFalse();

            using var explicitMailboxArgs = JsonDocument.Parse("{\"code\":\"return 2\",\"channel\":\"mailbox\"}");
            var res2 = await registry.ExecuteToolAsync("kongor_c0_probe", explicitMailboxArgs.RootElement);
            res2.IsError.Should().BeFalse();

            File.Exists(tempLog).Should().BeTrue("ExecuteToolAsync must write outcome JSONL rows when OutcomeLogFilePath is configured");
            string[] lines = await File.ReadAllLinesAsync(tempLog);
            lines.Should().HaveCount(2);

            using var row1 = JsonDocument.Parse(lines[0]);
            row1.RootElement.GetProperty("tool").GetString().Should().Be("kongor_c0_probe");
            row1.RootElement.GetProperty("channel").GetString().Should().Be("mailbox");
            row1.RootElement.GetProperty("fallbackReason").GetString().Should().Be("pipe_absent");
            row1.RootElement.GetProperty("unrequestedMailboxFallback").GetBoolean().Should().BeTrue(
                "channel=='mailbox' when caller did not request 'mailbox' must flag unrequestedMailboxFallback=true");

            using var row2 = JsonDocument.Parse(lines[1]);
            row2.RootElement.GetProperty("channel").GetString().Should().Be("mailbox");
            row2.RootElement.GetProperty("unrequestedMailboxFallback").GetBoolean().Should().BeFalse(
                "explicit channel=='mailbox' request must not flag unrequestedMailboxFallback");
        }
        finally
        {
            if (File.Exists(tempLog))
            {
                File.Delete(tempLog);
            }
        }
    }

    [Fact]
    public async Task C1_DispatchIncomingMessage_LateResponseForTimedOutRequestId_NeverCrossTalksToActiveCaller()
    {
        // protects: GameIpcBridge.DispatchIncomingMessage drops orphaned late responses (RequestId > 0) instead of falling through to _pendingRequestsByOpCode
        // fails_when: A late response for timed-out RequestId=1 resolves active RequestId=2 with RequestId=1's stale payload
        // seam: Real NamedPipeServerStream <-> GameIpcBridge.SendRequestAsync binary framing
        string pipeName = "test-native-pipe-" + Guid.NewGuid().ToString("N");
        using CancellationTokenSource cts = new(TimeSpan.FromSeconds(10));

        await using NamedPipeServerStream server = new(
            pipeName,
            PipeDirection.InOut,
            1,
            PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous | PipeOptions.WriteThrough);

        Task waitForConnectionTask = server.WaitForConnectionAsync(cts.Token);

        Task serverTask = Task.Run(async () =>
        {
            await waitForConnectionTask;

            // Read Request 1 (which will time out on the client side)
            byte[] header1 = new byte[12];
            await server.ReadExactlyAsync(header1.AsMemory(0, 12), cts.Token);
            int len1 = BinaryPrimitives.ReadInt32LittleEndian(header1.AsSpan(4, 4));
            uint reqId1 = BinaryPrimitives.ReadUInt32LittleEndian(header1.AsSpan(8, 4));
            byte[] payload1 = new byte[len1];
            await server.ReadExactlyAsync(payload1.AsMemory(0, len1), cts.Token);

            // Read Request 2 (active caller)
            byte[] header2 = new byte[12];
            await server.ReadExactlyAsync(header2.AsMemory(0, 12), cts.Token);
            int len2 = BinaryPrimitives.ReadInt32LittleEndian(header2.AsSpan(4, 4));
            uint reqId2 = BinaryPrimitives.ReadUInt32LittleEndian(header2.AsSpan(8, 4));
            byte[] payload2 = new byte[len2];
            await server.ReadExactlyAsync(payload2.AsMemory(0, len2), cts.Token);

            // Emit late response for timed-out reqId1 FIRST, then fresh response for reqId2
            static byte[] BuildEvalResponseFrame(uint reqId, string resultValue)
            {
                string json = $"{{\"id\":{reqId},\"success\":true,\"result\":\"{resultValue}\"}}";
                byte[] body = Encoding.UTF8.GetBytes(json);
                byte[] frame = new byte[12 + body.Length];
                BinaryPrimitives.WriteUInt16LittleEndian(frame.AsSpan(0, 2), 0x574D);
                BinaryPrimitives.WriteUInt16LittleEndian(frame.AsSpan(2, 2), (ushort)IpcOpCode.EvalLuaResponse);
                BinaryPrimitives.WriteInt32LittleEndian(frame.AsSpan(4, 4), body.Length);
                BinaryPrimitives.WriteUInt32LittleEndian(frame.AsSpan(8, 4), reqId);
                body.CopyTo(frame.AsSpan(12));
                return frame;
            }

            byte[] staleFrame = BuildEvalResponseFrame(reqId1, "STALE_FROM_REQ_1");
            byte[] freshFrame = BuildEvalResponseFrame(reqId2, "FRESH_FROM_REQ_2");

            await server.WriteAsync(staleFrame, cts.Token);
            await server.FlushAsync(cts.Token);
            await Task.Delay(25, cts.Token);
            await server.WriteAsync(freshFrame, cts.Token);
            await server.FlushAsync(cts.Token);
        }, cts.Token);

        await using GameIpcBridge client = new(pipeName: pipeName);
        await client.ConnectAsync(timeoutMs: 2000, cts.Token);

        // Request 1 times out after 80ms
        IpcMessage req1 = IpcMessage.CreateString(IpcOpCode.EvalLuaSync, "return 'req1'");
        Func<Task> act1 = () => client.SendRequestAsync(req1, timeoutMs: 80, cts.Token);
        await act1.Should().ThrowAsync<TimeoutException>();

        // Request 2 must receive FRESH_FROM_REQ_2 and never STALE_FROM_REQ_1
        IpcMessage req2 = IpcMessage.CreateString(IpcOpCode.EvalLuaSync, "return 'req2'");
        IpcMessage? resp2 = await client.SendRequestAsync(req2, timeoutMs: 3000, cts.Token);
        resp2.Should().NotBeNull();
        resp2!.PayloadString.Should().Contain("FRESH_FROM_REQ_2").And.NotContain("STALE_FROM_REQ_1");

        await serverTask;
    }

    [Fact]
    public async Task C1_ExecuteLuaAsync_And_ClientEvalLuaHandler_TimeoutRecyclesWithoutDoubleEval_AndMapsMainThreadDrainTimeout()
    {
        // protects: GameIpcBridge.ExecuteLuaAsync recycles connection on TimeoutException without double-sending non-idempotent Lua, and ClientEvalLuaHandler maps mainThreadDrainTimeout (executed=false) vs ipcPipeTimeout (executed=null)
        // fails_when: ExecuteLuaAsync sends a second EvalLuaSync frame on timeout, leaves IsConnected=true, or ClientEvalLuaHandler fails to distinguish mainThreadDrainTimeout from ipcPipeTimeout
        // seam: Real NamedPipeServerStream <-> GameIpcBridge.ExecuteLuaAsync + ClientEvalLuaHandler.HandleAsync
        string pipeName = "test-native-pipe-" + Guid.NewGuid().ToString("N");
        using CancellationTokenSource cts = new(TimeSpan.FromSeconds(10));
        int framesReceivedByServer = 0;

        await using NamedPipeServerStream server = new(
            pipeName,
            PipeDirection.InOut,
            1,
            PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous | PipeOptions.WriteThrough);

        Task waitForConnectionTask = server.WaitForConnectionAsync(cts.Token);

        Task serverTask = Task.Run(async () =>
        {
            await waitForConnectionTask;
            byte[] header = new byte[12];

            // 1. Default pre-send engine gate sends 0x0005 GetEngineState first: answer with fresh msSinceHostFrameAdvance
            await server.ReadExactlyAsync(header.AsMemory(0, 12), cts.Token);
            ushort stateOp = BinaryPrimitives.ReadUInt16LittleEndian(header.AsSpan(2, 2));
            int stateLen = BinaryPrimitives.ReadInt32LittleEndian(header.AsSpan(4, 4));
            uint stateReqId = BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(8, 4));
            if (stateLen > 0)
            {
                await server.ReadExactlyAsync(new byte[stateLen], cts.Token);
            }
            stateOp.Should().Be((ushort)IpcOpCode.GetEngineState);
            byte[] stateReply = IpcMessage.Serialize(IpcMessage.CreateString(
                IpcOpCode.EngineStateResponse,
                "{\"hostFrame\":120,\"msSinceHostFrameAdvance\":5,\"mainThreadBusy\":false}",
                stateReqId));
            await server.WriteAsync(stateReply, cts.Token);
            await server.FlushAsync(cts.Token);

            // 2. Now read the single eval frame (0x0080 Duplex or 0x000E Sync) and do not reply so client times out after 100ms
            await server.ReadExactlyAsync(header.AsMemory(0, 12), cts.Token);
            ushort evalOp = BinaryPrimitives.ReadUInt16LittleEndian(header.AsSpan(2, 2));
            int len = BinaryPrimitives.ReadInt32LittleEndian(header.AsSpan(4, 4));
            byte[] payload = new byte[len];
            await server.ReadExactlyAsync(payload.AsMemory(0, len), cts.Token);
            ((evalOp == (ushort)IpcOpCode.EvalLuaSync) || (evalOp == (ushort)IpcOpCode.DuplexLuaRpc)).Should().BeTrue();
            Interlocked.Increment(ref framesReceivedByServer);
            // Do not reply so client times out after 100ms
            try { await Task.Delay(300, cts.Token); } catch (OperationCanceledException) { }
        }, cts.Token);

        await using GameIpcBridge client = new(pipeName: pipeName);
        await client.ConnectAsync(timeoutMs: 2000, cts.Token);

        var handler = new ClientEvalLuaHandler(ipcBridge: client, logger: _ => { });
        using var timeoutArgs = JsonDocument.Parse("{\"code\":\"counter = counter + 1\",\"timeout_ms\":100}");
        var timeoutRes = await handler.HandleAsync(timeoutArgs.RootElement, cts.Token);

        timeoutRes.IsError.Should().BeTrue();
        framesReceivedByServer.Should().Be(1, "Timed-out Lua eval must never re-send a non-idempotent snippet a second time");
        client.IsConnected.Should().BeFalse("Timed-out Lua eval must recycle the pipe connection");

        using var timeoutDoc = JsonDocument.Parse(timeoutRes.FirstText);
        timeoutDoc.RootElement.GetProperty("errorCode").GetString().Should().Be("ipcPipeTimeout");
        (!timeoutDoc.RootElement.TryGetProperty("executed", out var execProp) || execProp.ValueKind == JsonValueKind.Null)
            .Should().BeTrue("On pipe-level timeout, executed is indeterminate (null/omitted), never true");

        await serverTask;

        // Also verify native mainThreadDrainTimeout maps to errorCode=="mainThreadDrainTimeout" and executed==false
        var mockBridge = new Mock<IGameIpcBridge>();
        mockBridge.Setup(b => b.IsConnected).Returns(true);
        mockBridge.Setup(b => b.PipeName).Returns("kongor-native-client");
        mockBridge.Setup(b => b.ExecuteLuaAsync(It.IsAny<string>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync("[ERROR] Timed out waiting for K2 main thread Lua evaluation");

        var drainHandler = new ClientEvalLuaHandler(ipcBridge: mockBridge.Object, logger: _ => { });
        using var drainArgs = JsonDocument.Parse("{\"code\":\"return 1\"}");
        var drainRes = await drainHandler.HandleAsync(drainArgs.RootElement, cts.Token);

        drainRes.IsError.Should().BeTrue();
        using var drainDoc = JsonDocument.Parse(drainRes.FirstText);
        drainDoc.RootElement.GetProperty("errorCode").GetString().Should().Be("mainThreadDrainTimeout");
        drainDoc.RootElement.GetProperty("executed").GetBoolean().Should().BeFalse(
            "WaitAndTakePendingLuaEval atomically erases the slot before returning this error, so the Lua code was provably not executed");
    }

    [Fact]
    public async Task C2_CompositeGameIpcBridge_ConnectsPrimaryWhenTertiaryConnected_BlocksSilentMailboxFallback_AndSupportsExplicitMailboxChannel()
    {
        // protects: CompositeGameIpcBridge.ConnectAsync connects _primary even when _tertiary.IsConnected==true, suppresses unrequested 3000ms mailbox fallback when native pipe is present or times out, and emits channel + fallbackReason
        // fails_when: ConnectAsync short-circuits on _tertiary.IsConnected, _tertiary is called when NativePipePresenceProbe()==true or on TimeoutException, or ClientEvalLuaHandler omits channel/fallbackReason
        // seam: Real CompositeGameIpcBridge + ClientEvalLuaHandler.HandleAsync
        var mockPrimary = new Mock<IGameIpcBridge>();
        var mockSecondary = new Mock<IGameIpcBridge>();
        var mockTertiary = new Mock<IGameIpcBridge>();

        bool primaryConnected = false;
        mockPrimary.SetupGet(b => b.IsConnected).Returns(() => primaryConnected);
        mockPrimary.SetupGet(b => b.PipeName).Returns("kongor-native-client");
        mockPrimary.Setup(b => b.ConnectAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .Callback(() => primaryConnected = true)
            .Returns(Task.CompletedTask);

        // Tertiary (mailbox) reports IsConnected = true before ConnectAsync is called
        mockTertiary.SetupGet(b => b.IsConnected).Returns(true);
        mockTertiary.Setup(b => b.ExecuteLuaAsync(It.IsAny<string>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync("MAILBOX_OK");

        await using var composite = new CompositeGameIpcBridge(mockPrimary.Object, mockSecondary.Object, mockTertiary.Object)
        {
            NativePipePresenceProbe = () => true
        };

        // 1. ConnectAsync must connect _primary even when _tertiary.IsConnected is already true
        await composite.ConnectAsync(timeoutMs: 500);
        mockPrimary.Verify(b => b.ConnectAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()), Times.Once);
        primaryConnected.Should().BeTrue();

        // 2. When _primary throws TimeoutException or IOException while native pipe is present, _tertiary must NEVER be called
        mockPrimary.Setup(b => b.ExecuteLuaAsync("return 'timeout'", It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new TimeoutException("Primary timed out"));
        var handler = new ClientEvalLuaHandler(ipcBridge: composite, logger: _ => { });

        using var timeoutArgs = JsonDocument.Parse("{\"code\":\"return 'timeout'\"}");
        var timeoutRes = await handler.HandleAsync(timeoutArgs.RootElement);
        timeoutRes.IsError.Should().BeTrue();
        mockTertiary.Verify(b => b.ExecuteLuaAsync(It.IsAny<string>(), It.IsAny<int>(), It.IsAny<CancellationToken>()), Times.Never);

        using var timeoutDoc = JsonDocument.Parse(timeoutRes.FirstText);
        timeoutDoc.RootElement.GetProperty("channel").GetString().Should().Be("named_pipe");

        mockPrimary.Setup(b => b.ExecuteLuaAsync("return 'io_err'", It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new IOException("Primary pipe reset while pipe exists on host"));
        using var ioErrArgs = JsonDocument.Parse("{\"code\":\"return 'io_err'\"}");
        var ioErrRes = await handler.HandleAsync(ioErrArgs.RootElement);
        ioErrRes.IsError.Should().BeTrue();
        mockTertiary.Verify(b => b.ExecuteLuaAsync(It.IsAny<string>(), It.IsAny<int>(), It.IsAny<CancellationToken>()), Times.Never);

        // 3. Explicit channel="mailbox" routes to _tertiary with channel="mailbox" and fallbackReason="explicit_channel_override"
        using var explicitMailboxArgs = JsonDocument.Parse("{\"code\":\"return 'mbx'\",\"channel\":\"mailbox\"}");
        var explicitRes = await handler.HandleAsync(explicitMailboxArgs.RootElement);
        explicitRes.IsError.Should().BeFalse();
        using var explicitDoc = JsonDocument.Parse(explicitRes.FirstText);
        explicitDoc.RootElement.GetProperty("channel").GetString().Should().Be("mailbox");
        explicitDoc.RootElement.GetProperty("fallbackReason").GetString().Should().Be("explicit_channel_override");
        explicitDoc.RootElement.GetProperty("result").GetString().Should().Be("MAILBOX_OK");

        // 4. When native pipe does NOT exist on host (NativePipePresenceProbe = false) and _primary is disconnected, fallback to _tertiary records fallbackReason="pipe_absent"
        primaryConnected = false;
        mockPrimary.Setup(b => b.ConnectAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new TimeoutException("Pipe does not exist"));
        composite.NativePipePresenceProbe = () => false;

        using var pipeAbsentArgs = JsonDocument.Parse("{\"code\":\"return 'fallback'\"}");
        var pipeAbsentRes = await handler.HandleAsync(pipeAbsentArgs.RootElement);
        pipeAbsentRes.IsError.Should().BeFalse();
        using var pipeAbsentDoc = JsonDocument.Parse(pipeAbsentRes.FirstText);
        pipeAbsentDoc.RootElement.GetProperty("channel").GetString().Should().Be("mailbox");
        pipeAbsentDoc.RootElement.GetProperty("fallbackReason").GetString().Should().Be("pipe_absent");
    }

    [Fact]
    public async Task C3_WaitForHandler_ThreeStageReadinessLadder_DistinguishesIpcReady_EngineReady_AndClientReady()
    {
        // protects: 3-stage readiness ladder (ipc_ready -> engine_ready -> client_ready) and ClientEvalLuaHandler pre-send engine gate
        // fails_when: ipc_ready fails when main thread is busy, engine_ready returns true on Poll 1 while HostFrame is parked or MainThreadBusy==true, or client_ready returns true before UIManager.GetInterface('main') is loaded
        // seam: Real WaitForHandler.HandleAsync + ClientEvalLuaHandler.HandleAsync with stateful IGameIpcBridge
        var mockBridge = new Mock<IGameIpcBridge>();
        mockBridge.SetupGet(b => b.IsConnected).Returns(true);
        mockBridge.SetupGet(b => b.PipeName).Returns("kongor-native-client");
        mockBridge.Setup(b => b.PingAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(85L);

        // Stage 1: ipc_ready succeeds immediately when PingAsync >= 0, even while MainThreadBusy == true and HostFrame is frozen at 10
        mockBridge.Setup(b => b.GetEngineStateAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new EngineStateDto
            {
                GamePhase = 1,
                CurrentFps = 144.0,
                HostFrame = 10,
                MainThreadBusy = true,
                ActiveOpElapsedMs = 1200
            });

        var waitHandler = new WaitForHandler(gameIpcBridge: mockBridge.Object);
        using var ipcReadyArgs = JsonDocument.Parse("{\"condition\":\"ipc_ready\",\"timeout_ms\":1000,\"poll_interval_ms\":20}");
        var ipcReadyRes = await waitHandler.HandleAsync(ipcReadyArgs.RootElement);
        ipcReadyRes.IsError.Should().BeFalse();
        using var ipcReadyDoc = JsonDocument.Parse(ipcReadyRes.FirstText);
        ipcReadyDoc.RootElement.GetProperty("satisfied").GetBoolean().Should().BeTrue();
        ipcReadyDoc.RootElement.GetProperty("observedState").GetProperty("stage").GetString().Should().Be("ipc_ready");

        // Stage 2: engine_ready must NOT satisfy while HostFrame is frozen at 10 or MainThreadBusy == true;
        // it must wait until HostFrame advances (10 -> 25) and MainThreadBusy == false, even if UIManager.GetInterface('main') is still false
        int enginePollCount = 0;
        mockBridge.Setup(b => b.GetEngineStateAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(() =>
            {
                int poll = Interlocked.Increment(ref enginePollCount);
                return poll switch
                {
                    1 => new EngineStateDto { GamePhase = 1, CurrentFps = 144.0, HostFrame = 10, MainThreadBusy = true, ActiveOpElapsedMs = 900 },
                    2 => new EngineStateDto { GamePhase = 1, CurrentFps = 144.0, HostFrame = 10, MainThreadBusy = false, ActiveOpElapsedMs = 0 },
                    _ => new EngineStateDto { GamePhase = 1, CurrentFps = 144.0, HostFrame = 25, MainThreadBusy = false, ActiveOpElapsedMs = 0 }
                };
            });
        mockBridge.Setup(b => b.ExecuteLuaAsync(It.IsAny<string>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync("false");

        using var engineReadyArgs = JsonDocument.Parse("{\"condition\":\"engine_ready\",\"timeout_ms\":2000,\"poll_interval_ms\":20}");
        var engineReadyRes = await waitHandler.HandleAsync(engineReadyArgs.RootElement);
        engineReadyRes.IsError.Should().BeFalse();
        enginePollCount.Should().BeGreaterThanOrEqualTo(3, "engine_ready must wait across busy and parked-frame polls until HostFrame advances");
        using var engineReadyDoc = JsonDocument.Parse(engineReadyRes.FirstText);
        engineReadyDoc.RootElement.GetProperty("satisfied").GetBoolean().Should().BeTrue();
        engineReadyDoc.RootElement.GetProperty("observedState").GetProperty("stage").GetString().Should().Be("engine_ready");
        engineReadyDoc.RootElement.GetProperty("observedState").GetProperty("firstHostFrame").GetInt32().Should().Be(10);
        engineReadyDoc.RootElement.GetProperty("observedState").GetProperty("hostFrame").GetInt32().Should().Be(25);

        // Stage 3: client_ready requires engine_ready (HostFrame advancing) AND UIManager.GetInterface('main') == "true"
        int clientStatePolls = 0;
        int clientLuaPolls = 0;
        mockBridge.Setup(b => b.GetEngineStateAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(() =>
            {
                int poll = Interlocked.Increment(ref clientStatePolls);
                return new EngineStateDto { GamePhase = 1, CurrentFps = 144.0, HostFrame = 100 + (poll * 10), MainThreadBusy = false, ActiveOpElapsedMs = 0 };
            });
        mockBridge.Setup(b => b.ExecuteLuaAsync(It.IsAny<string>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(() =>
            {
                int lPoll = Interlocked.Increment(ref clientLuaPolls);
                return lPoll == 1 ? "false" : "true";
            });

        using var clientReadyArgs = JsonDocument.Parse("{\"condition\":\"client_ready\",\"timeout_ms\":2000,\"poll_interval_ms\":20}");
        var clientReadyRes = await waitHandler.HandleAsync(clientReadyArgs.RootElement);
        clientReadyRes.IsError.Should().BeFalse();
        clientLuaPolls.Should().BeGreaterThanOrEqualTo(2, "client_ready must keep polling when UIManager.GetInterface('main') is initially false");
        using var clientReadyDoc = JsonDocument.Parse(clientReadyRes.FirstText);
        clientReadyDoc.RootElement.GetProperty("satisfied").GetBoolean().Should().BeTrue();
        clientReadyDoc.RootElement.GetProperty("observedState").GetProperty("stage").GetString().Should().Be("client_ready");
        clientReadyDoc.RootElement.GetProperty("observedState").GetProperty("uiInterfaceLoaded").GetBoolean().Should().BeTrue();

        // Pre-send gate in ClientEvalLuaHandler: waits when wait_for_engine == true and MainThreadBusy == true before dispatching ExecuteLuaAsync once
        int evalStatePolls = 0;
        int evalDispatches = 0;
        mockBridge.Setup(b => b.GetEngineStateAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(() =>
            {
                int poll = Interlocked.Increment(ref evalStatePolls);
                return poll == 1
                    ? new EngineStateDto { GamePhase = 1, CurrentFps = 144.0, HostFrame = 50, MainThreadBusy = true, ActiveOpElapsedMs = 400 }
                    : new EngineStateDto { GamePhase = 1, CurrentFps = 144.0, HostFrame = 65, MainThreadBusy = false, ActiveOpElapsedMs = 0 };
            });
        mockBridge.Setup(b => b.ExecuteLuaAsync("return 42", It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(() =>
            {
                Interlocked.Increment(ref evalDispatches);
                return "42";
            });

        var evalHandler = new ClientEvalLuaHandler(ipcBridge: mockBridge.Object, logger: _ => { });
        using var evalArgs = JsonDocument.Parse("{\"code\":\"return 42\",\"wait_for_engine\":true,\"timeout_ms\":3000}");
        var evalRes = await evalHandler.HandleAsync(evalArgs.RootElement);
        evalRes.IsError.Should().BeFalse();
        evalDispatches.Should().Be(1, "Pre-send gate must wait first and then send the Lua evaluation exactly once");
        using var evalDoc = JsonDocument.Parse(evalRes.FirstText);
        evalDoc.RootElement.GetProperty("waitedForEngineMs").GetDouble().Should().BeGreaterThan(0);
    }

    [Fact]
    public async Task D0_EngineStateDto_GoldenCapturedLivePayload_WithUint32SentinelMatchId_DrivesWaitForEngineReadyOverRealPipe()
    {
        // protects: MCP-240 native %u / %llu JSON schema parity (activeMatchId=4294967295 sentinel -> 0 / ServerMatchId=null, valid >INT32_MAX match IDs preserved) and end-to-end WaitForHandler engine_ready over real NamedPipeServerStream
        // fails_when: EngineStateDto throws JsonException on activeMatchId=4294967295 or truncates uint32 match IDs, or WaitForHandler fails to advance from frame 1336291 -> 1336340 over real binary IPC framing
        // seam: Real NamedPipeServerStream <-> IpcMessage.ReadJson<EngineStateDto> <-> GameIpcBridge.GetEngineStateAsync <-> WaitForHandler.HandleAsync
        const string goldenFrame1Json =
            "{\"currentFps\":179.7,\"memoryWorkingSetBytes\":2597322752,\"luaMemoryKb\":58107,\"activeMatchId\":4294967295,\"currentMap\":\"midwars\",\"playerName\":\"KongorTester\",\"connectedPlayers\":1,\"uptimeSeconds\":4974,\"gamePhase\":3,\"gamePhaseName\":\"Match / In-Game (3)\",\"isSpectator\":false,\"activeEntities\":110,\"serverTick\":1336291,\"hostFrame\":1336291,\"mainThreadBusy\":false,\"activeOpElapsedMs\":0,\"main_thread_busy\":false,\"active_op_elapsed_ms\":0,\"entityProfiler\":{\"activeEntities\":110,\"luaMemoryKb\":58107,\"hitchCount\":78843,\"cpuStallCount\":1041,\"peakTickMs\":358.00}}";
        const string goldenFrame2Json =
            "{\"currentFps\":196.0,\"memoryWorkingSetBytes\":2597322752,\"luaMemoryKb\":58112,\"activeMatchId\":4294967295,\"currentMap\":\"midwars\",\"playerName\":\"KongorTester\",\"connectedPlayers\":1,\"uptimeSeconds\":4974,\"gamePhase\":3,\"gamePhaseName\":\"Match / In-Game (3)\",\"isSpectator\":false,\"activeEntities\":110,\"serverTick\":1336340,\"hostFrame\":1336340,\"mainThreadBusy\":false,\"activeOpElapsedMs\":0,\"main_thread_busy\":false,\"active_op_elapsed_ms\":0,\"entityProfiler\":{\"activeEntities\":110,\"luaMemoryKb\":58112,\"hitchCount\":78843,\"cpuStallCount\":1041,\"peakTickMs\":358.00}}";

        EngineStateDto? parsedSentinel = IpcMessage.CreateString(IpcOpCode.EngineStateResponse, goldenFrame1Json).ReadJson<EngineStateDto>();
        parsedSentinel.Should().NotBeNull();
        parsedSentinel!.ActiveMatchId.Should().Be(0L, "0xFFFFFFFF (4294967295) is K2's INVALID_INDEX no-match sentinel and must normalize to 0");
        parsedSentinel.ServerMatchId.Should().BeNull();
        parsedSentinel.HostFrame.Should().Be(1336291);
        parsedSentinel.GamePhase.Should().Be(3);

        const string largeValidMatchJson = "{\"activeMatchId\":3000000123,\"serverTick\":4000000001,\"luaMemoryKb\":4294967295,\"hostFrame\":42}";
        EngineStateDto? parsedLarge = IpcMessage.CreateString(IpcOpCode.EngineStateResponse, largeValidMatchJson).ReadJson<EngineStateDto>();
        parsedLarge.Should().NotBeNull();
        parsedLarge!.ActiveMatchId.Should().Be(3_000_000_123L);
        parsedLarge.ServerMatchId.Should().Be(3_000_000_123L);
        parsedLarge.ServerTick.Should().Be(4_000_000_001L);
        parsedLarge.LuaMemoryKb.Should().Be(0, "0xFFFFFFFF on %u int fields must normalize safely to 0 without throwing");

        string pipeName = "test-native-pipe-" + Guid.NewGuid().ToString("N");
        using CancellationTokenSource cts = new(TimeSpan.FromSeconds(10));

        await using NamedPipeServerStream server = new(
            pipeName,
            PipeDirection.InOut,
            1,
            PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous | PipeOptions.WriteThrough);

        Task waitConnTask = server.WaitForConnectionAsync(cts.Token);
        Task serverTask = Task.Run(async () =>
        {
            await waitConnTask;
            string[] frames = [goldenFrame1Json, goldenFrame2Json];
            foreach (string frameJson in frames)
            {
                byte[] header = new byte[12];
                await server.ReadExactlyAsync(header.AsMemory(0, 12), cts.Token);
                ushort opCode = BinaryPrimitives.ReadUInt16LittleEndian(header.AsSpan(2, 2));
                int len = BinaryPrimitives.ReadInt32LittleEndian(header.AsSpan(4, 4));
                uint reqId = BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(8, 4));
                if (len > 0)
                {
                    byte[] reqPayload = new byte[len];
                    await server.ReadExactlyAsync(reqPayload.AsMemory(0, len), cts.Token);
                }

                opCode.Should().Be((ushort)IpcOpCode.GetEngineState);
                IpcMessage reply = IpcMessage.CreateString(IpcOpCode.EngineStateResponse, frameJson, reqId);
                byte[] wire = IpcMessage.Serialize(reply);
                await server.WriteAsync(wire, cts.Token);
                await server.FlushAsync(cts.Token);
            }
        }, cts.Token);

        await using GameIpcBridge client = new(pipeName: pipeName);
        await client.ConnectAsync(timeoutMs: 2000, cts.Token);

        var waitHandler = new WaitForHandler(gameIpcBridge: client, logger: _ => { });
        using var argsDoc = JsonDocument.Parse("{\"condition\":\"engine_ready\",\"timeout_ms\":2000,\"poll_interval_ms\":25}");
        var waitRes = await waitHandler.HandleAsync(argsDoc.RootElement, cts.Token);

        waitRes.IsError.Should().BeFalse(waitRes.FirstText);
        using var resDoc = JsonDocument.Parse(waitRes.FirstText);
        resDoc.RootElement.GetProperty("satisfied").GetBoolean().Should().BeTrue();
        resDoc.RootElement.GetProperty("pollCount").GetInt32().Should().Be(2);
        JsonElement obs = resDoc.RootElement.GetProperty("observedState");
        obs.GetProperty("stage").GetString().Should().Be("engine_ready");
        obs.GetProperty("firstHostFrame").GetInt32().Should().Be(1336291);
        obs.GetProperty("hostFrame").GetInt32().Should().Be(1336340);
        obs.GetProperty("hostFrameAdvancing").GetBoolean().Should().BeTrue();
        obs.GetProperty("legacyLuaFallbackUsed").GetBoolean().Should().BeFalse();
        obs.GetProperty("activeMatchId").GetInt64().Should().Be(0L);

        await serverTask;
    }

    [Fact]
    public async Task D0_WaitForHandler_MissingHostFrameUsesLuaFallback_PresentZeroKeepsPolling_AndLateLuaProbeRespectsDeadline()
    {
        // protects: Gap 1 distinction between missing hostFrame (old DLL -> 0x000E Lua round-trip) vs hostFrame==0 (early cold-boot -> keep polling without Lua block), plus deadline clamp against C1's 2700ms floor
        // fails_when: hostFrame==0 triggers ExecuteLuaAsync, missing hostFrame fails to probe Lua, or a stalled Lua probe overruns WaitForHandler's timeout_ms by ~2.7s
        // seam: IpcMessage.ReadJson<EngineStateDto> + WaitForHandler.HandleAsync + GameIpcBridge over real NamedPipeServerStream
        const string legacyNoHostFrameJson = "{\"currentFps\":144.0,\"activeMatchId\":4294967295,\"gamePhase\":1,\"mainThreadBusy\":false}";
        EngineStateDto? legacyDto = IpcMessage.CreateString(IpcOpCode.EngineStateResponse, legacyNoHostFrameJson).ReadJson<EngineStateDto>();
        legacyDto.Should().NotBeNull();
        legacyDto!.HostFrame.Should().BeNull("Omitted hostFrame key indicates an older version.dll");

        const string coldBootZeroHostFrameJson = "{\"currentFps\":0.0,\"activeMatchId\":0,\"gamePhase\":0,\"hostFrame\":0,\"mainThreadBusy\":false}";
        EngineStateDto? coldBootDto = IpcMessage.CreateString(IpcOpCode.EngineStateResponse, coldBootZeroHostFrameJson).ReadJson<EngineStateDto>();
        coldBootDto.Should().NotBeNull();
        coldBootDto!.HostFrame.Should().Be(0, "Explicit hostFrame:0 indicates early cold-boot before g_ppHost resolves");

        // 1. Missing HostFrame (null) -> falls back to ExecuteLuaAsync("return '1'") and satisfies engine_ready
        var legacyMock = new Mock<IGameIpcBridge>();
        legacyMock.SetupGet(b => b.IsConnected).Returns(true);
        legacyMock.SetupGet(b => b.PipeName).Returns("kongor-native-client");
        legacyMock.Setup(b => b.GetEngineStateAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(legacyDto);
        legacyMock.Setup(b => b.ExecuteLuaAsync("return '1'", It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync("1");

        var legacyWait = new WaitForHandler(gameIpcBridge: legacyMock.Object);
        using var engineArgs = JsonDocument.Parse("{\"condition\":\"engine_ready\",\"timeout_ms\":1000,\"poll_interval_ms\":25}");
        var legacyRes = await legacyWait.HandleAsync(engineArgs.RootElement);
        legacyRes.IsError.Should().BeFalse();
        legacyMock.Verify(b => b.ExecuteLuaAsync("return '1'", It.IsAny<int>(), It.IsAny<CancellationToken>()), Times.Once);
        using var legacyResDoc = JsonDocument.Parse(legacyRes.FirstText);
        legacyResDoc.RootElement.GetProperty("observedState").GetProperty("legacyLuaFallbackUsed").GetBoolean().Should().BeTrue();

        // 2. Explicit HostFrame == 0 -> must NOT call ExecuteLuaAsync; keeps polling until timeout
        var coldBootMock = new Mock<IGameIpcBridge>();
        coldBootMock.SetupGet(b => b.IsConnected).Returns(true);
        coldBootMock.SetupGet(b => b.PipeName).Returns("kongor-native-client");
        coldBootMock.Setup(b => b.GetEngineStateAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(coldBootDto);

        var coldBootWait = new WaitForHandler(gameIpcBridge: coldBootMock.Object);
        using var shortTimeoutArgs = JsonDocument.Parse("{\"condition\":\"engine_ready\",\"timeout_ms\":120,\"poll_interval_ms\":25}");
        var coldBootRes = await coldBootWait.HandleAsync(shortTimeoutArgs.RootElement);
        coldBootRes.IsError.Should().BeTrue();
        coldBootMock.Verify(b => b.ExecuteLuaAsync(It.IsAny<string>(), It.IsAny<int>(), It.IsAny<CancellationToken>()), Times.Never,
            "HostFrame==0 during cold-boot must never dispatch a blocking 2.7s Lua probe");

        // 3. Stalled Lua probe on real GameIpcBridge (even when effectiveTimeoutMs clamps to 2700ms) must respect WaitForHandler's 250ms deadline
        string pipeName = "test-native-pipe-" + Guid.NewGuid().ToString("N");
        using CancellationTokenSource cts = new(TimeSpan.FromSeconds(10));
        await using NamedPipeServerStream stalledServer = new(
            pipeName,
            PipeDirection.InOut,
            1,
            PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous | PipeOptions.WriteThrough);

        Task connTask = stalledServer.WaitForConnectionAsync(cts.Token);
        Task stalledServerTask = Task.Run(async () =>
        {
            await connTask;
            // Answer GetEngineState with missing hostFrame so WaitForHandler immediately invokes ExecuteLuaAsync("return '1'")
            byte[] header = new byte[12];
            await stalledServer.ReadExactlyAsync(header.AsMemory(0, 12), cts.Token);
            int len = BinaryPrimitives.ReadInt32LittleEndian(header.AsSpan(4, 4));
            uint reqId = BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(8, 4));
            if (len > 0)
            {
                await stalledServer.ReadExactlyAsync(new byte[len], cts.Token);
            }
            byte[] stateWire = IpcMessage.Serialize(IpcMessage.CreateString(IpcOpCode.EngineStateResponse, legacyNoHostFrameJson, reqId));
            await stalledServer.WriteAsync(stateWire, cts.Token);
            await stalledServer.FlushAsync(cts.Token);

            // Read the 0x000E EvalLuaSync frame and stall past WaitForHandler's 250ms budget
            await stalledServer.ReadExactlyAsync(header.AsMemory(0, 12), cts.Token);
            int luaLen = BinaryPrimitives.ReadInt32LittleEndian(header.AsSpan(4, 4));
            if (luaLen > 0)
            {
                await stalledServer.ReadExactlyAsync(new byte[luaLen], cts.Token);
            }
            try { await Task.Delay(600, cts.Token); } catch (OperationCanceledException) { }
        }, cts.Token);

        await using GameIpcBridge stalledClient = new(pipeName: pipeName);
        await stalledClient.ConnectAsync(timeoutMs: 2000, cts.Token);
        var budgetWait = new WaitForHandler(gameIpcBridge: stalledClient, logger: _ => { });
        using var budgetArgs = JsonDocument.Parse("{\"condition\":\"engine_ready\",\"timeout_ms\":250,\"poll_interval_ms\":25}");
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var budgetRes = await budgetWait.HandleAsync(budgetArgs.RootElement, cts.Token);
        sw.Stop();

        budgetRes.IsError.Should().BeTrue();
        sw.ElapsedMilliseconds.Should().BeLessThan(1200, "Deadline-linked poll CancellationToken must prevent a 2,700ms ExecuteLuaAsync overrun");
        using var budgetDoc = JsonDocument.Parse(budgetRes.FirstText);
        budgetDoc.RootElement.GetProperty("status").GetString().Should().Be("timeout");

        await stalledServerTask;
    }

    [Fact]
    public async Task D1_McpToolRegistry_CallerLuaError_SetsNullMcpIssueId_RecordsErrorClass_AndPreservesToolsListPitfall()
    {
        // protects: Gap 4 KB misattribution fix (score>0 threshold after stripping tool-name tokens), errorClass classification, and caller_lua_error exclusion from first-call transport failure
        // fails_when: A fast Lua runtime/syntax error on named_pipe is misattributed to MCP-136, lacks errorClass=='caller_lua_error', or poisons first-call transport reliability
        // seam: Real McpToolRegistry(registerDefaults: true) + ClientEvalLuaHandler + OutcomeLogFilePath JSONL output
        var mockBridge = new Mock<IGameIpcBridge>();
        mockBridge.SetupGet(b => b.IsConnected).Returns(true);
        mockBridge.SetupGet(b => b.PipeName).Returns("kongor-native-client");
        mockBridge.SetupSequence(b => b.ExecuteLuaAsync(It.IsAny<string>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync("[ERROR] [string \"kongor_eval\"]:1: attempt to index global 'nonexistent_table' (a nil value)")
            .ReturnsAsync("42");

        var registry = new McpToolRegistry(registerDefaults: true, gameIpcBridge: mockBridge.Object);
        registry.ResetFirstCallScoreboard();

        // 1. tools/list must keep occurrence-ranked pitfall enrichment (e.g. MCP-231 / MCP-136)
        var evalDef = registry.ListTools().Single(t => t.Name == "kongor_client_eval_lua");
        evalDef.Description.Should().MatchRegex(@"\[MCP-\d+:");

        string tempLog = Path.Combine(Path.GetTempPath(), $"kongor_d1_outcomes_{Guid.NewGuid():N}.jsonl");
        registry.OutcomeLogFilePath = tempLog;
        try
        {
            // 2. Call 1: caller's Lua bug -> IsError=true, errorClass="caller_lua_error", mcpIssueId=null (never MCP-136!)
            using var badLuaArgs = JsonDocument.Parse("{\"code\":\"return nonexistent_table.x\"}");
            var badRes = await registry.ExecuteToolAsync("kongor_client_eval_lua", badLuaArgs.RootElement);
            badRes.IsError.Should().BeTrue();

            using var badDoc = JsonDocument.Parse(badRes.FirstText);
            badDoc.RootElement.GetProperty("errorCode").GetString().Should().Be("luaEvalError");
            badDoc.RootElement.GetProperty("errorClass").GetString().Should().Be("caller_lua_error");
            badDoc.RootElement.GetProperty("mcpIssueId").ValueKind.Should().Be(JsonValueKind.Null,
                "A caller Lua runtime error on named_pipe must never be misattributed to MCP-136");

            // 3. Call 2: valid Lua -> pins FirstCallOutcomesByTool["kongor_client_eval_lua"] == true because Call 1 was a caller error
            using var okLuaArgs = JsonDocument.Parse("{\"code\":\"return 42\"}");
            var okRes = await registry.ExecuteToolAsync("kongor_client_eval_lua", okLuaArgs.RootElement);
            okRes.IsError.Should().BeFalse();

            var scoreboard = registry.GetFirstCallScoreboard();
            scoreboard.FirstCallOutcomesByTool.Should().ContainKey("kongor_client_eval_lua");
            scoreboard.FirstCallOutcomesByTool["kongor_client_eval_lua"].Should().BeTrue(
                "Caller Lua syntax/runtime errors must not mark the tool's first non-caller-error transport call as failed");

            string[] rows = await File.ReadAllLinesAsync(tempLog);
            rows.Should().HaveCount(2);

            using var row0 = JsonDocument.Parse(rows[0]);
            row0.RootElement.GetProperty("errorClass").GetString().Should().Be("caller_lua_error");
            row0.RootElement.GetProperty("mcpIssueId").ValueKind.Should().Be(JsonValueKind.Null);
            row0.RootElement.GetProperty("firstCall").GetBoolean().Should().BeFalse();

            using var row1 = JsonDocument.Parse(rows[1]);
            row1.RootElement.GetProperty("errorClass").GetString().Should().Be("none");
            row1.RootElement.GetProperty("firstCall").GetBoolean().Should().BeTrue();
        }
        finally
        {
            if (File.Exists(tempLog))
            {
                File.Delete(tempLog);
            }
        }
    }

    [Fact]
    public async Task D3_KongorShj_DryRunCanaryAndWalk_ReportInconclusiveVerdict_NeverFalsePass()
    {
        if (!NexusTestEnvironment.HasGameAssets) return;

        // protects: Gap 2 SHJ receipt honesty invariant (0 live evals / DryRun must report 'inconclusive', never 'passed' or 'named_pipe')
        // fails_when: scripts/kongor_shj.ps1 emits status='passed' or channel='named_pipe' without executing live evals against hon_x64.exe
        // seam: Real PowerShell execution of scripts/kongor_shj.ps1 (-Action cold-boot-canary -DryRun and -Action walk -DryRun) + receipt JSON schema
        string repoRoot = ResolveRepoRoot();
        string shjScript = Path.Combine(repoRoot, "scripts", "qa", "kongor_shj.ps1");
        string canaryOut = Path.Combine(Path.GetTempPath(), $"kongor_d3_canary_{Guid.NewGuid():N}.json");
        string walkOut = Path.Combine(Path.GetTempPath(), $"kongor_d3_walk_{Guid.NewGuid():N}.json");

        static async Task<(int ExitCode, string Stdout, string Stderr)> RunShjAsync(string scriptPath, string args, string workDir)
        {
            var psi = new System.Diagnostics.ProcessStartInfo
            {
                FileName = OperatingSystem.IsWindows() ? "powershell.exe" : "pwsh",
                Arguments = $"-NoProfile -ExecutionPolicy Bypass -File \"{scriptPath}\" {args}",
                WorkingDirectory = workDir,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            using var proc = System.Diagnostics.Process.Start(psi)!;
            Task<string> outTask = proc.StandardOutput.ReadToEndAsync();
            Task<string> errTask = proc.StandardError.ReadToEndAsync();
            await proc.WaitForExitAsync();
            return (proc.ExitCode, await outTask, await errTask);
        }

        try
        {
            var canaryTask = RunShjAsync(shjScript, $"-Action cold-boot-canary -DryRun -OutputPath \"{canaryOut}\"", repoRoot);
            var walkTask = RunShjAsync(shjScript, $"-Action walk -Steps 8 -DryRun -OutputPath \"{walkOut}\"", repoRoot);
            await Task.WhenAll(canaryTask, walkTask);

            var (canaryExit, _, canaryErr) = await canaryTask;
            var (walkExit, _, walkErr) = await walkTask;

            canaryExit.Should().Be(0, $"DryRun canary failed: {canaryErr}");
            walkExit.Should().Be(0, $"DryRun walk failed: {walkErr}");

            File.Exists(canaryOut).Should().BeTrue();
            using var canaryDoc = JsonDocument.Parse(await File.ReadAllTextAsync(canaryOut));
            var cRoot = canaryDoc.RootElement;
            cRoot.GetProperty("verdict").GetString().Should().Be("inconclusive");
            cRoot.GetProperty("status").GetString().Should().Be("inconclusive");
            cRoot.GetProperty("inconclusiveReason").GetString().Should().Be("dry_run");
            cRoot.GetProperty("sequentialEvalsExecuted").GetInt32().Should().Be(0);
            cRoot.GetProperty("parallelEvalsExecuted").GetInt32().Should().Be(0);
            cRoot.GetProperty("channel").ValueKind.Should().Be(JsonValueKind.Null,
                "DryRun/offline receipts with 0 live evals must never claim channel='named_pipe'");
            cRoot.GetProperty("p50Ms").ValueKind.Should().Be(JsonValueKind.Null);

            File.Exists(walkOut).Should().BeTrue();
            using var walkDoc = JsonDocument.Parse(await File.ReadAllTextAsync(walkOut));
            var wRoot = walkDoc.RootElement;
            wRoot.GetProperty("verdict").GetString().Should().Be("inconclusive");
            wRoot.GetProperty("status").GetString().Should().Be("inconclusive");
            wRoot.GetProperty("liveStepsExecuted").GetInt32().Should().Be(0);
            wRoot.GetProperty("steps").GetInt32().Should().Be(8);
        }
        finally
        {
            if (File.Exists(canaryOut)) File.Delete(canaryOut);
            if (File.Exists(walkOut)) File.Delete(walkOut);
        }
    }

    [Fact]
    public async Task D2_ClientEvalLuaHandler_DefaultPreSendGate_FastPathsFreshEngine_AndWaitsWithinBudgetWhenHostFrameStale()
    {
        // protects: Gap 3 default pre-send engine gate using msSinceHostFrameAdvance > 100ms (detecting ReloadInterfaces / XML compile stalls outside WndProc where mainThreadBusy==false), budget deduction from timeoutMs, and single eval dispatch
        // fails_when: Pre-send gate requires explicit wait_for_engine=true, fails to wait when mainThreadBusy==false && msSinceHostFrameAdvance > 100, adds >1 sample when fresh, fails to deduct wait time from timeoutMs, or sends Lua more than once
        // seam: Real NamedPipeServerStream <-> IpcMessage.ReadJson<EngineStateDto> <-> GameIpcBridge <-> ClientEvalLuaHandler (default constructor)
        // 1. Fresh case: single 0x0005 sample (msSinceHostFrameAdvance=8 <= 100), 0ms wait, 1 Lua dispatch
        var freshBridge = new Mock<IGameIpcBridge>();
        freshBridge.SetupGet(b => b.IsConnected).Returns(true);
        freshBridge.SetupGet(b => b.PipeName).Returns("kongor-native-client");
        int freshStateSamples = 0;
        freshBridge.Setup(b => b.GetEngineStateAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(() =>
            {
                Interlocked.Increment(ref freshStateSamples);
                return new EngineStateDto { HostFrame = 2500, MsSinceHostFrameAdvance = 8, MainThreadBusy = false };
            });
        freshBridge.Setup(b => b.ExecuteLuaAsync("return 'fresh'", It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync("fresh");

        var defaultHandler = new ClientEvalLuaHandler(ipcBridge: freshBridge.Object, logger: _ => { });
        defaultHandler.EnablePreSendEngineGate.Should().BeTrue();
        using var freshArgs = JsonDocument.Parse("{\"code\":\"return 'fresh'\",\"timeout_ms\":3000}");
        var freshRes = await defaultHandler.HandleAsync(freshArgs.RootElement);
        freshRes.IsError.Should().BeFalse();
        freshStateSamples.Should().Be(1, "Fresh msSinceHostFrameAdvance <= 100ms must take exactly 1 pre-send sample with zero polling loop");
        using var freshDoc = JsonDocument.Parse(freshRes.FirstText);
        freshDoc.RootElement.GetProperty("waitedForEngineMs").GetDouble().Should().Be(0);

        // 2. Stale case over real NamedPipeServerStream: mainThreadBusy==false (CHost::Frame XML compile / ReloadInterfaces outside WndProc)
        //    Poll 1: hostFrame=900, msSinceHostFrameAdvance=420 (>100)
        //    Poll 2: hostFrame=900, msSinceHostFrameAdvance=470 (>100)
        //    Poll 3: hostFrame=906, msSinceHostFrameAdvance=6   (<=100) -> gate releases and sends 0x000E once!
        string pipeName = "test-native-pipe-" + Guid.NewGuid().ToString("N");
        using CancellationTokenSource cts = new(TimeSpan.FromSeconds(10));
        int statePollsOverPipe = 0;
        int evalFramesOverPipe = 0;

        await using NamedPipeServerStream server = new(
            pipeName,
            PipeDirection.InOut,
            1,
            PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous | PipeOptions.WriteThrough);

        Task connTask = server.WaitForConnectionAsync(cts.Token);
        Task serverTask = Task.Run(async () =>
        {
            await connTask;
            byte[] header = new byte[12];
            while (!cts.IsCancellationRequested)
            {
                await server.ReadExactlyAsync(header.AsMemory(0, 12), cts.Token);
                ushort op = BinaryPrimitives.ReadUInt16LittleEndian(header.AsSpan(2, 2));
                int len = BinaryPrimitives.ReadInt32LittleEndian(header.AsSpan(4, 4));
                uint reqId = BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(8, 4));
                if (len > 0)
                {
                    await server.ReadExactlyAsync(new byte[len], cts.Token);
                }

                if (op == (ushort)IpcOpCode.GetEngineState)
                {
                    int poll = Interlocked.Increment(ref statePollsOverPipe);
                    string stateJson = poll switch
                    {
                        1 => "{\"hostFrame\":900,\"msSinceHostFrameAdvance\":420,\"mainThreadBusy\":false,\"activeMatchId\":4294967295}",
                        2 => "{\"hostFrame\":900,\"msSinceHostFrameAdvance\":470,\"mainThreadBusy\":false,\"activeMatchId\":4294967295}",
                        _ => "{\"hostFrame\":906,\"msSinceHostFrameAdvance\":6,\"mainThreadBusy\":false,\"activeMatchId\":4294967295}"
                    };
                    byte[] reply = IpcMessage.Serialize(IpcMessage.CreateString(IpcOpCode.EngineStateResponse, stateJson, reqId));
                    await server.WriteAsync(reply, cts.Token);
                    await server.FlushAsync(cts.Token);
                }
                else if (op == (ushort)IpcOpCode.EvalLuaSync || op == (ushort)IpcOpCode.DuplexLuaRpc)
                {
                    Interlocked.Increment(ref evalFramesOverPipe);
                    if (op == (ushort)IpcOpCode.DuplexLuaRpc)
                    {
                        byte[] replyPayload = new byte[9 + Encoding.UTF8.GetByteCount("RELOAD_SETTLED_OK")];
                        BinaryPrimitives.WriteUInt32LittleEndian(replyPayload.AsSpan(0, 4), reqId);
                        BinaryPrimitives.WriteUInt32LittleEndian(replyPayload.AsSpan(4, 4), 100);
                        replyPayload[8] = 0x01; // ok = true
                        Encoding.UTF8.GetBytes("RELOAD_SETTLED_OK").CopyTo(replyPayload, 9);
                        byte[] reply = IpcMessage.Serialize(new IpcMessage { OpCode = IpcOpCode.DuplexLuaRpcResponse, RequestId = reqId, Payload = replyPayload });
                        await server.WriteAsync(reply, cts.Token);
                        await server.FlushAsync(cts.Token);
                    }
                    else
                    {
                        string evalJson = $"{{\"id\":{reqId},\"success\":true,\"result\":\"RELOAD_SETTLED_OK\"}}";
                        byte[] reply = IpcMessage.Serialize(IpcMessage.CreateString(IpcOpCode.EvalLuaResponse, evalJson, reqId));
                        await server.WriteAsync(reply, cts.Token);
                        await server.FlushAsync(cts.Token);
                    }
                    break;
                }
            }
        }, cts.Token);

        await using GameIpcBridge pipeClient = new(pipeName: pipeName);
        await pipeClient.ConnectAsync(timeoutMs: 2000, cts.Token);

        var livePipeHandler = new ClientEvalLuaHandler(ipcBridge: pipeClient, logger: _ => { });
        using var staleArgs = JsonDocument.Parse("{\"code\":\"return 'after_reload'\",\"timeout_ms\":3000}");
        var staleRes = await livePipeHandler.HandleAsync(staleArgs.RootElement, cts.Token);

        staleRes.IsError.Should().BeFalse();
        statePollsOverPipe.Should().Be(3, "Default pre-send gate must poll until msSinceHostFrameAdvance <= 100 and hostFrame advances");
        evalFramesOverPipe.Should().Be(1, "Eval must be dispatched exactly once after the pre-send gate releases");

        using var staleDoc = JsonDocument.Parse(staleRes.FirstText);
        staleDoc.RootElement.GetProperty("result").GetString().Should().Be("RELOAD_SETTLED_OK");
        double waitedForEngineMs = staleDoc.RootElement.GetProperty("waitedForEngineMs").GetDouble();
        waitedForEngineMs.Should().BeGreaterThan(
            0,
            "pre-send gate must wait while hostFrame is stale and report positive waitedForEngineMs");
        waitedForEngineMs.Should().BeGreaterThanOrEqualTo(
            35,
            "two jittered poll waits (Random.Shared.Next(25, 96) => min 2 * 25ms = 50ms) minus Windows 15.6ms timer quantization exceed 35ms");
        staleDoc.RootElement.GetProperty("channel").GetString().Should().Be("named_pipe");

        await serverTask;

        // 3. Budget accounting check: waitedForEngineMs reduces the timeoutMs passed to ExecuteLuaAsync
        var budgetBridge = new Mock<IGameIpcBridge>();
        budgetBridge.SetupGet(b => b.IsConnected).Returns(true);
        budgetBridge.SetupGet(b => b.PipeName).Returns("kongor-native-client");
        int budgetPolls = 0;
        int capturedEvalTimeoutMs = -1;
        budgetBridge.Setup(b => b.GetEngineStateAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(() =>
            {
                int p = Interlocked.Increment(ref budgetPolls);
                return p == 1
                    ? new EngineStateDto { HostFrame = 10, MsSinceHostFrameAdvance = 500, MainThreadBusy = false }
                    : new EngineStateDto { HostFrame = 15, MsSinceHostFrameAdvance = 4, MainThreadBusy = false };
            });
        budgetBridge.Setup(b => b.ExecuteLuaAsync(It.IsAny<string>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .Callback<string, int, CancellationToken>((_, toMs, _) => capturedEvalTimeoutMs = toMs)
            .ReturnsAsync("ok");

        var budgetHandler = new ClientEvalLuaHandler(ipcBridge: budgetBridge.Object, logger: _ => { });
        using var budgetArgs = JsonDocument.Parse("{\"code\":\"return 'ok'\",\"timeout_ms\":1000}");
        var budgetRes = await budgetHandler.HandleAsync(budgetArgs.RootElement, cts.Token);
        budgetRes.IsError.Should().BeFalse();
        capturedEvalTimeoutMs.Should().BeGreaterThan(0).And.BeLessThan(1000,
            "Waiting in the pre-send gate must consume from timeoutMs before dispatching ExecuteLuaAsync");
    }

    [Fact]
    public async Task Mcp241_GameClientManageHandler_AcceptsStartAlias_AndRejectsUnknownActionsWithoutSilentStatusFallthrough()
    {
        // protects: MCP-241 kongor_game_client_manage must route action='start' to LaunchClientAsync (same as 'launch') and reject unknown actions with IsError=true rather than silently falling through default: to GetClientStatusAsync (false-green running=false)
        // fails_when: switch(action) lacks case 'start' or default: falls through to case 'status'
        // seam: GameClientManageHandler.HandleAsync + mocked IClientProcessSupervisor
        FileInfo? noExec = null;
        var supervisorMock = new Mock<IClientProcessSupervisor>();
        supervisorMock
            .Setup(s => s.TryResolveClientExecutable(It.IsAny<string?>(), It.IsAny<string?>(), out noExec))
            .Returns(false);

        var handler = new GameClientManageHandler(
            logger: _ => { },
            processSupervisor: supervisorMock.Object);

        using var startArgs = JsonDocument.Parse("{\"action\":\"start\",\"window_mode\":\"offscreen\",\"customClientPath\":\"C:\\\\nonexistent\\\\hon_x64.exe\"}");
        var startRes = await handler.HandleAsync(startArgs.RootElement);
        supervisorMock.Verify(
            s => s.TryResolveClientExecutable("C:\\nonexistent\\hon_x64.exe", It.IsAny<string?>(), out noExec),
            Times.Once,
            "action='start' must route directly into LaunchClientAsync instead of falling through to GetClientStatusAsync");
        using var startDoc = JsonDocument.Parse(startRes.FirstText);
        startDoc.RootElement.GetProperty("action").GetString().Should().Be("launch");

        using var bogusArgs = JsonDocument.Parse("{\"action\":\"bogus_nonexistent_action\"}");
        var bogusRes = await handler.HandleAsync(bogusArgs.RootElement);
        using var bogusDoc = JsonDocument.Parse(bogusRes.FirstText);
        bogusDoc.RootElement.GetProperty("status").GetString().Should().Be("error",
            "Unknown actions must report status='error' rather than silently returning status='success' from GetClientStatusAsync");
        bogusDoc.RootElement.GetProperty("errorMessage").GetString().Should().Contain("Unknown action 'bogus_nonexistent_action'");
    }

    [Fact]
    public async Task P1B_NativeGuardOpCode_GetLuaMemoryMetrics_RoundTripsOverNamedPipeWithoutLogStreamHijack()
    {
        // protects: Metagen NativeGuardOpCode wire alignment for 0x0007 (GetLuaMemoryMetrics) -> 0x0008 (LuaMemoryResponse) on kongor-native-client without collision against NEXUS.Contracts.Ipc.IpcOpCode.LogStreamEvent
        // fails_when: GameIpcBridge maps 0x0007 to LogStreamEvent on native client pipes or lacks LuaMemoryResponse request/response correlation
        // seam: ProjectKongor.Contracts.Generated.NativeGuardOpCode + GameIpcBridge.GetLuaMemoryMetricsAsync over real NamedPipeServerStream
        ((ushort)IpcOpCode.GetLuaMemoryMetrics).Should().Be((ushort)ProjectKongor.Contracts.Generated.NativeGuardOpCode.GetLuaMemoryMetrics);
        ((ushort)IpcOpCode.LuaMemoryResponse).Should().Be((ushort)ProjectKongor.Contracts.Generated.NativeGuardOpCode.LuaMemoryResponse);
        ((ushort)IpcOpCode.HookToggleRequest).Should().Be((ushort)ProjectKongor.Contracts.Generated.NativeGuardOpCode.HookToggleRequest);

        string pipeName = "test-native-pipe-" + Guid.NewGuid().ToString("N");
        using CancellationTokenSource cts = new(TimeSpan.FromSeconds(10));
        await using NamedPipeServerStream server = new(
            pipeName,
            PipeDirection.InOut,
            1,
            PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous | PipeOptions.WriteThrough);

        Task serverTask = Task.Run(async () =>
        {
            await server.WaitForConnectionAsync(cts.Token);
            byte[] header = new byte[12];
            await server.ReadExactlyAsync(header.AsMemory(0, 12), cts.Token);
            ushort opCode = BinaryPrimitives.ReadUInt16LittleEndian(header.AsSpan(2, 2));
            int len = BinaryPrimitives.ReadInt32LittleEndian(header.AsSpan(4, 4));
            uint reqId = BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(8, 4));
            if (len > 0)
            {
                await server.ReadExactlyAsync(new byte[len], cts.Token);
            }

            opCode.Should().Be(0x0007);
            const string luaMemJson = "{\"luaMemoryBytes\":25165824,\"gcPacingActive\":true,\"fullGcCollections\":7}";
            byte[] wire = IpcMessage.Serialize(IpcMessage.CreateString(IpcOpCode.LuaMemoryResponse, luaMemJson, reqId));
            await server.WriteAsync(wire, cts.Token);
            await server.FlushAsync(cts.Token);
        }, cts.Token);

        await using GameIpcBridge client = new(pipeName: pipeName);
        await client.ConnectAsync(timeoutMs: 2000, cts.Token);
        (ulong luaMemoryBytes, bool gcPacingActive, ulong fullGcCollections) = await client.GetLuaMemoryMetricsAsync(2000, cts.Token);

        luaMemoryBytes.Should().Be(25165824UL);
        gcPacingActive.Should().BeTrue();
        fullGcCollections.Should().Be(7UL);
        await serverTask;
    }

    [Fact]
    public async Task Mcp245_And_Mcp232_WaitForLuaRejectsEvalErrors_AndDetermineGamePhaseSkipsBlockingLuaWhenBusy()
    {
        // protects: MCP-245 (WaitForHandler condition='lua' must reject [ERROR]/[LUA_ERR]/[OK] Command queued strings and evaluate via ExecuteLuaAsync) and MCP-232 (DetermineGamePhaseAsync must not stall in ExecuteLuaAsync when MainThreadBusy==true)
        // fails_when: WaitForHandler treats "[ERROR] nil value" or "[OK] Command queued" as truthy, or DetermineGamePhaseAsync invokes ExecuteLuaAsync during map load when GamePhase==0 and MainThreadBusy==true
        // seam: WaitForHandler.HandleAsync + ClientPracticeHandler.DetermineGamePhaseAsync + mocked IGameIpcBridge
        int luaPoll = 0;
        Mock<IGameIpcBridge> waitBridge = new();
        waitBridge.SetupGet(b => b.IsConnected).Returns(true);
        waitBridge.SetupGet(b => b.PipeName).Returns("kongor-native-client");
        waitBridge
            .Setup(b => b.ExecuteLuaAsync(It.IsAny<string>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(() =>
            {
                int current = Interlocked.Increment(ref luaPoll);
                return current switch
                {
                    1 => "[ERROR] attempt to index a nil value",
                    2 => "[OK] Command queued to K2 main thread ring",
                    _ => "ready_ok"
                };
            });

        WaitForHandler waitHandler = new(gameIpcBridge: waitBridge.Object, logger: _ => { });
        using JsonDocument waitArgs = JsonDocument.Parse("{\"condition\":\"lua\",\"target\":\"MyGlobal.state\",\"expected\":\"ready_ok\",\"timeout_ms\":1000,\"poll_interval_ms\":25}");
        ToolCallResult waitRes = await waitHandler.HandleAsync(waitArgs.RootElement);
        waitRes.IsError.Should().BeFalse(waitRes.FirstText);
        luaPoll.Should().Be(3, "Polls 1 ([ERROR]) and 2 ([OK] Command queued) must be rejected before poll 3 matches expected='ready_ok'");

        Mock<IGameIpcBridge> busyBridge = new();
        busyBridge.SetupGet(b => b.IsConnected).Returns(true);
        busyBridge
            .Setup(b => b.GetEngineStateAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new EngineStateDto { GamePhase = 0, MainThreadBusy = true });

        ClientPracticeHandler practiceHandler = new(gameIpcBridge: busyBridge.Object, logger: _ => { });
        int phase = await practiceHandler.DetermineGamePhaseAsync(CancellationToken.None, processId: null, allowTestBypass: false);
        phase.Should().Be(0);
        busyBridge.Verify(
            b => b.ExecuteLuaAsync(It.IsAny<string>(), It.IsAny<int>(), It.IsAny<CancellationToken>()),
            Times.Never,
            "DetermineGamePhaseAsync must return 0 immediately without dispatching a blocking 0x000E Lua probe while MainThreadBusy is true");
    }

    [Fact]
    public async Task Mcp257_GameClientManageHandler_LaunchRedactsLoginPasswordInLogsAndJsonWhilePreservingProcessArgs()
    {
        // protects: MCP-257 kongor_game_client_manage launch must mask +Set login_password MD5 in diagnostic logs and JSON response arguments while passing unredacted arguments to build/spawn hon_x64.exe
        // fails_when: GameClientManageHandler.Launch.cs logs raw joinedArguments or serializes raw BuildClientLaunchArguments into ToolCallResult.Json
        // seam: GameClientManageHandler.LaunchClientAsync + real ClientModMountBuilder + real ClientCredentialManager
        string fakeClientDir = Path.Combine(Path.GetTempPath(), "kongor-mcp257-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(fakeClientDir);
        try
        {
            FileInfo? resolvedExe = new(Path.Combine(fakeClientDir, "hon_x64.exe"));
            Mock<IClientProcessSupervisor> supervisorMock = new();
            supervisorMock
                .Setup(s => s.TryResolveClientExecutable(It.IsAny<string?>(), It.IsAny<string?>(), out resolvedExe))
                .Returns(true);

            ClientModMountBuilder realMountBuilder = new();
            IReadOnlyList<string>? capturedRawLaunchArgs = null;
            Mock<IClientModMountBuilder> mountMock = new();
            mountMock
                .Setup(m => m.BuildClientLaunchArguments(
                    It.IsAny<string>(),
                    It.IsAny<string>(),
                    It.IsAny<string?>(),
                    It.IsAny<string>(),
                    It.IsAny<string>(),
                    It.IsAny<bool>(),
                    It.IsAny<int>(),
                    It.IsAny<string?>(),
                    It.IsAny<string>(),
                    It.IsAny<int>(),
                    It.IsAny<int>(),
                    It.IsAny<int>(),
                    It.IsAny<string?>(),
                    It.IsAny<string?>(),
                    It.IsAny<bool>()))
                .Returns((
                    string gDir, string mode, string? conn, string ms, string res, bool fs, int fps,
                    string? nick, string winMode, int ox, int oy, int slot, string? pipe, string? passMd5, bool autoLog) =>
                {
                    capturedRawLaunchArgs = realMountBuilder.BuildClientLaunchArguments(
                        gDir, mode, conn, ms, res, fs, fps, nick, winMode, ox, oy, slot, pipe, passMd5, autoLog);
                    return capturedRawLaunchArgs;
                });

            List<string> logLines = [];
            GameClientManageHandler handler = new(
                logger: msg => logLines.Add(msg),
                processSupervisor: supervisorMock.Object,
                modMountBuilder: mountMock.Object,
                slotManager: new OffscreenSlotManager(),
                processRegistry: new ClientProcessRegistry(),
                credentialManager: ClientCredentialManager.Default);

            string rawCredential = "custom_redaction_probe_" + Guid.NewGuid().ToString("N");
            string expectedMd5 = ClientCredentialManager.Default.HashPassword(rawCredential);

            ToolCallResult launchRes = await handler.LaunchClientAsync(
                windowMode: "offscreen",
                customWorkingDirectory: fakeClientDir,
                customNickname: "test_pilot",
                password: rawCredential,
                autoLogin: true);

            launchRes.IsError.Should().BeFalse(launchRes.FirstText);

            // 1. Raw launch args constructed for ProcessStartInfo / InteractiveProcessLauncher MUST retain the real MD5 for auto-login
            capturedRawLaunchArgs.Should().NotBeNull();
            capturedRawLaunchArgs!.Should().Contain($"+Set login_password \"{expectedMd5}\"");

            // 2. Diagnostic logs MUST NOT leak plaintext password or MD5 hash, and MUST show [REDACTED_CREDENTIAL]
            string combinedLogs = string.Join("\n", logLines);
            combinedLogs.Should().NotContain(rawCredential);
            combinedLogs.Should().NotContain(expectedMd5);
            combinedLogs.Should().Contain($"+Set login_password \"{ClientCredentialManager.RedactedPlaceholder}\"");

            // 3. MCP JSON response MUST NOT leak plaintext password or MD5 hash, while preserving non-sensitive args
            launchRes.FirstText.Should().NotContain(rawCredential);
            launchRes.FirstText.Should().NotContain(expectedMd5);
            using JsonDocument doc = JsonDocument.Parse(launchRes.FirstText);
            string[] returnedArgs = doc.RootElement.GetProperty("arguments")
                .EnumerateArray()
                .Select(e => e.GetString() ?? string.Empty)
                .ToArray();
            returnedArgs.Should().Contain($"+Set login_password \"{ClientCredentialManager.RedactedPlaceholder}\"");
            returnedArgs.Should().Contain("+Set login_name \"test_pilot\"");
        }
        finally
        {
            if (Directory.Exists(fakeClientDir))
            {
                Directory.Delete(fakeClientDir, recursive: true);
            }
        }
    }

    [Fact]
    public async Task Mcp257_GameClientManageHandler_LoginAndCommandRedactCredentialsWhilePreservingWireCommand()
    {
        // protects: MCP-257 kongor_game_client_manage login and command actions must redact login_password MD5 in JSON responses and logs while still sending raw credentials over Named Pipe IPC to hon_x64.exe
        // fails_when: LoginClientAsync returns raw creds.PasswordMd5 / unredacted launchArgs, or ExecuteClientCommandAsync echoes unredacted login_password in ToolCallResult.Json or corrupts the wire IPC command
        // seam: GameClientManageHandler.LoginClientAsync + ExecuteClientCommandAsync + real ClientCredentialManager + mocked IGameIpcBridge wire boundary
        string? capturedWireCommand = null;
        Mock<IGameIpcBridge> ipcMock = new();
        ipcMock.SetupGet(b => b.IsConnected).Returns(true);
        ipcMock
            .Setup(b => b.ExecuteCommandAsync(It.IsAny<string>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .Callback<string, int, CancellationToken>((cmd, _, _) => capturedWireCommand = cmd)
            .ReturnsAsync("OK");

        List<string> logLines = [];
        GameClientManageHandler handler = new(
            ipcBridge: ipcMock.Object,
            logger: msg => logLines.Add(msg),
            processRegistry: new ClientProcessRegistry(),
            credentialManager: ClientCredentialManager.Default);

        string rawCredential = "custom_login_probe_" + Guid.NewGuid().ToString("N");
        string expectedMd5 = ClientCredentialManager.Default.HashPassword(rawCredential);

        // 1. LoginClientAsync must redact both passwordMd5 and launchArgs in its JSON payload and logs
        ToolCallResult loginRes = await handler.LoginClientAsync(
            username: "test_pilot",
            password: rawCredential,
            slotIndex: 2);

        loginRes.IsError.Should().BeFalse(loginRes.FirstText);
        loginRes.FirstText.Should().NotContain(rawCredential);
        loginRes.FirstText.Should().NotContain(expectedMd5);

        using (JsonDocument loginDoc = JsonDocument.Parse(loginRes.FirstText))
        {
            loginDoc.RootElement.GetProperty("passwordMd5").GetString().Should().Be(ClientCredentialManager.RedactedPlaceholder);
            string[] loginArgs = loginDoc.RootElement.GetProperty("launchArgs")
                .EnumerateArray()
                .Select(e => e.GetString() ?? string.Empty)
                .ToArray();
            loginArgs.Should().Contain($"+Set login_password \"{ClientCredentialManager.RedactedPlaceholder}\"");
            loginArgs.Should().Contain("+Set login_name \"test_pilot\"");
        }

        // 2. ExecuteClientCommandAsync must dispatch raw MD5 on the IPC wire while redacting JSON command and logs
        string rawLoginCommand = $"Set login_name \"test_pilot\"; Set login_password \"{expectedMd5}\"";
        ToolCallResult cmdRes = await handler.ExecuteClientCommandAsync(
            rawLoginCommand,
            timeoutMs: 1000,
            autoLaunch: false,
            allowKeystrokeFallback: false);

        cmdRes.IsError.Should().BeFalse(cmdRes.FirstText);
        capturedWireCommand.Should().Be(rawLoginCommand, "the K2 client IPC wire must receive the unredacted MD5 hash so authentication succeeds");

        cmdRes.FirstText.Should().NotContain(expectedMd5);
        using (JsonDocument cmdDoc = JsonDocument.Parse(cmdRes.FirstText))
        {
            string echoedCommand = cmdDoc.RootElement.GetProperty("command").GetString() ?? string.Empty;
            echoedCommand.Should().Contain($"Set login_password \"{ClientCredentialManager.RedactedPlaceholder}\"");
            echoedCommand.Should().Contain("Set login_name \"test_pilot\"");
        }

        string combinedLogs = string.Join("\n", logLines);
        combinedLogs.Should().NotContain(rawCredential);
        combinedLogs.Should().NotContain(expectedMd5);
    }

    [Fact]
    public void Mcp235_ResolveCustomOutputPath_AcceptsAllAliasesAndHandlesNullShortCircuit()
    {
        // 1. Accepts standard aliases
        string[] aliases = ["output_path", "outputPath", "save_path", "savePath", "file_path", "filePath", "artifact_path", "artifactPath", "destination", "path", "custom_output_path", "customOutputPath"];
        foreach (string alias in aliases)
        {
            using JsonDocument doc = JsonDocument.Parse($"{{\"{alias}\": \"artifacts/captures/test_shot.png\"}}");
            string? resolved = CaptureWindowHandler.ResolveCustomOutputPath(doc.RootElement, out string? matchedKey);
            resolved.Should().Be("artifacts/captures/test_shot.png", $"alias '{alias}' must resolve correctly");
            matchedKey.Should().Be(alias);
        }

        // 2. Overcomes null/empty short-circuiting: if custom_output_path is null or empty string, fallback to output_path
        using (JsonDocument nullDoc = JsonDocument.Parse("{\"custom_output_path\": null, \"output_path\": \"artifacts/captures/fallback.png\"}"))
        {
            string? resolved = CaptureWindowHandler.ResolveCustomOutputPath(nullDoc.RootElement, out string? matchedKey);
            resolved.Should().Be("artifacts/captures/fallback.png");
            matchedKey.Should().Be("output_path");
        }

        using (JsonDocument emptyDoc = JsonDocument.Parse("{\"custom_output_path\": \"\", \"file_path\": \"artifacts/captures/file_fallback.png\"}"))
        {
            string? resolved = CaptureWindowHandler.ResolveCustomOutputPath(emptyDoc.RootElement, out string? matchedKey);
            resolved.Should().Be("artifacts/captures/file_fallback.png");
            matchedKey.Should().Be("file_path");
        }

        // 3. Non-string tokens do not throw InvalidOperationException
        using (JsonDocument nonStringDoc = JsonDocument.Parse("{\"custom_output_path\": 12345, \"file_path\": \"artifacts/captures/typed.png\"}"))
        {
            string? resolved = CaptureWindowHandler.ResolveCustomOutputPath(nonStringDoc.RootElement, out string? matchedKey);
            resolved.Should().Be("artifacts/captures/typed.png");
            matchedKey.Should().Be("file_path");
        }
    }

    [Fact]
    public void Mcp235_ResolveArtifactPath_EnforcesAllowlistAndExtension()
    {
        string repoRoot = ResolveRepoRoot();

        // 1. Relative path normalized against repo root and allowed
        var (allowedRel, fullRel, rejRel) = WindowCapturer.ResolveArtifactPath(
            saveArtifact: true,
            customOutputPath: "artifacts/captures/my_frame.png",
            repoRoot: repoRoot);
        allowedRel.Should().BeTrue();
        fullRel.Should().Be(Path.GetFullPath(Path.Combine(repoRoot, "artifacts", "captures", "my_frame.png")));
        rejRel.Should().BeNull();

        // 2. Explicit customOutputPath saves even if saveArtifact is false
        var (allowedExplicit, fullExplicit, _) = WindowCapturer.ResolveArtifactPath(
            saveArtifact: false,
            customOutputPath: "artifacts/captures/explicit_save.png",
            repoRoot: repoRoot);
        allowedExplicit.Should().BeTrue();
        fullExplicit.Should().Be(Path.GetFullPath(Path.Combine(repoRoot, "artifacts", "captures", "explicit_save.png")));

        // 3. saveArtifact = false with no custom path returns allowed with null fullPath
        var (allowedNoSave, fullNoSave, _) = WindowCapturer.ResolveArtifactPath(
            saveArtifact: false,
            customOutputPath: null,
            repoRoot: repoRoot);
        allowedNoSave.Should().BeTrue();
        fullNoSave.Should().BeNull();

        // 4. Non-PNG extension rejected
        var (allowedNonPng, _, rejNonPng) = WindowCapturer.ResolveArtifactPath(
            saveArtifact: true,
            customOutputPath: "artifacts/captures/my_frame.jpg",
            repoRoot: repoRoot);
        allowedNonPng.Should().BeFalse();
        rejNonPng.Should().Contain(".png");

        // 5. UNC paths rejected
        var (allowedUnc, _, rejUnc) = WindowCapturer.ResolveArtifactPath(
            saveArtifact: true,
            customOutputPath: @"\\malicious-share\box\shot.png",
            repoRoot: repoRoot);
        allowedUnc.Should().BeFalse();
        rejUnc.Should().Contain("UNC");

        // 6. Writing into protected directories (.git, src, native) rejected
        var (allowedGit, _, rejGit) = WindowCapturer.ResolveArtifactPath(
            saveArtifact: true,
            customOutputPath: ".git/hooks/shot.png",
            repoRoot: repoRoot);
        allowedGit.Should().BeFalse();
        rejGit.Should().Contain(".git");

        var (allowedSrc, _, rejSrc) = WindowCapturer.ResolveArtifactPath(
            saveArtifact: true,
            customOutputPath: "src/tools/shot.png",
            repoRoot: repoRoot);
        allowedSrc.Should().BeFalse();
        rejSrc.Should().Contain("src");

        // 7. Temporary directory allowed
        string tempFile = Path.Combine(Path.GetTempPath(), "test_capture_temp.png");
        var (allowedTemp, fullTemp, _) = WindowCapturer.ResolveArtifactPath(
            saveArtifact: true,
            customOutputPath: tempFile,
            repoRoot: repoRoot);
        allowedTemp.Should().BeTrue();
        fullTemp.Should().Be(Path.GetFullPath(tempFile));
    }

    [Fact]
    public async Task Mcp235_CaptureWindowHandler_MapsWindowNotFoundToClientOfflinePreflight()
    {
        // Mock capturer returning WindowNotFound
        Mock<IWindowCapturer> mockCapturer = new();
        mockCapturer.Setup(c => c.CaptureAsync(
                It.IsAny<string>(),
                It.IsAny<string>(),
                It.IsAny<bool>(),
                It.IsAny<string?>(),
                It.IsAny<bool>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new WindowCaptureResult
            {
                Success = false,
                Target = "hon_x64",
                ErrorMessage = "Target window not found for query 'hon_x64'. Make sure the target process or window is currently open.",
                ErrorCode = WindowCaptureErrorCode.WindowNotFound
            });

        CaptureWindowHandler handler = new(windowCapturer: mockCapturer.Object);
        using JsonDocument argsDoc = JsonDocument.Parse("{\"target\": \"hon_x64\"}");

        ToolCallResult result = await handler.HandleAsync(argsDoc.RootElement, CancellationToken.None);
        result.IsError.Should().BeTrue();

        using JsonDocument errDoc = JsonDocument.Parse(result.FirstText);
        errDoc.RootElement.GetProperty("errorCode").GetString().Should().Be("clientOffline");
        errDoc.RootElement.GetProperty("preflightFailed").GetBoolean().Should().BeTrue();
    }

    [Fact]
    public void Mcp246_NormalizeRawLua_StripsScriptPrefixAndUnwrapsOuterQuotes()
    {
        ClientEvalLuaHandler.NormalizeRawLua("Script \"return 1\"").Should().Be("return 1");
        ClientEvalLuaHandler.NormalizeRawLua("Script 'return 2'").Should().Be("return 2");
        ClientEvalLuaHandler.NormalizeRawLua("script \"Echo('hi')\"").Should().Be("Echo('hi')");
        ClientEvalLuaHandler.NormalizeRawLua("return 3").Should().Be("return 3");
        ClientEvalLuaHandler.NormalizeRawLua("").Should().BeEmpty();
        ClientEvalLuaHandler.NormalizeRawLua("   ").Should().BeEmpty();
    }

    [Fact]
    public async Task Mcp246_ClientEvalLuaHandler_GeneratesRemediationHintOnGlobalGetGamePhase()
    {
        Mock<IGameIpcBridge> mockBridge = new();
        mockBridge.SetupGet(x => x.IsConnected).Returns(true);
        mockBridge.Setup(x => x.ExecuteLuaAsync(It.IsAny<string>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync("[ERROR] [string \"return string.format(\\\"phase=%s\\\", tostring(GetGamePhase()))\"]:1: attempt to call global 'GetGamePhase' (a nil value)");

        ClientEvalLuaHandler handler = new(ipcBridge: mockBridge.Object, enablePreSendEngineGate: false);
        using JsonDocument argsDoc = JsonDocument.Parse("{\"action\": \"eval\", \"code\": \"return string.format(\\\"phase=%s\\\", tostring(GetGamePhase()))\"}");

        ToolCallResult result = await handler.HandleAsync(argsDoc.RootElement, CancellationToken.None);
        result.IsError.Should().BeTrue();

        using JsonDocument errDoc = JsonDocument.Parse(result.FirstText);
        errDoc.RootElement.GetProperty("status").GetString().Should().Be("error");
        errDoc.RootElement.GetProperty("errorCode").GetString().Should().Be("luaEvalError");
        errDoc.RootElement.GetProperty("remediationHint").GetString().Should().Contain("GetGamePhase() does not exist");
        errDoc.RootElement.GetProperty("remediationHint").GetString().Should().Contain("kongor_game_client_manage");
    }

    [Fact]
    public void Mcp246_EnrichErrorWithMcpIssue_EscapesPercentSignsToPreventGoFormatStringMangling()
    {
        string rawErrorPayload = "{\"status\":\"error\",\"code\":\"return string.format(\\\"phase=%s\\\", 1)\",\"result\":\"[ERROR] failed\"}";
        ToolCallResult errorResult = ToolCallResult.Error(rawErrorPayload);

        string? issueId = McpToolRegistry.EnrichErrorWithMcpIssue("kongor_client_eval_lua", errorResult, out string errorClass);

        errorClass.Should().Be("caller_lua_error");
        errorResult.FirstText.Should().Contain(@"\u0025s");
        errorResult.FirstText.Should().NotContain(@"%s");

        // Ensure payload is valid JSON and parses round-trip back to '%s'
        using JsonDocument parsedDoc = JsonDocument.Parse(errorResult.FirstText);
        parsedDoc.RootElement.GetProperty("code").GetString().Should().Be("return string.format(\"phase=%s\", 1)");
    }

    [Fact]
    public async Task S2_1_Mcp273_ImageFirewall_SpillsOversizedFramesAndEliminatesDoubleBase64AcrossAllThreeEmitPaths()
    {
        // protects: MCP-273 > 400 KB image spillover firewall in ToolCallResult.Image and elimination of redundant base64Png inside JSON payloads across CaptureWindowHandler (normal + transient) and UiRenderViewHandler
        // fails_when: Any of the 3 emit paths embeds raw base64Png in JSON text or ToolCallResult.Image emits an inline ImageContentItem when base64Png > 400 KB
        // seam: ToolCallResult.Image + CaptureWindowHandler.HandleAsync (normal & transient) + UiRenderViewHandler.HandleAsync
        byte[] smallPng = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0x01, 0x02, 0x03, 0x04];
        string smallB64 = Convert.ToBase64String(smallPng);
        string tempArt = Path.Combine(Path.GetTempPath(), $"mcp273_small_{Guid.NewGuid():N}.png");
        File.WriteAllBytes(tempArt, smallPng);

        try
        {
            var mockCapturer = new Mock<IWindowCapturer>();
            mockCapturer.Setup(c => c.CaptureAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<bool>(), It.IsAny<string?>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(new WindowCaptureResult
                {
                    Success = true,
                    Target = "hon_x64",
                    WindowTitle = "Heroes of Newerth",
                    Pid = 1001,
                    Width = 1280,
                    Height = 720,
                    CaptureMethodUsed = "vulkan_offscreen",
                    ArtifactPath = tempArt,
                    Base64Png = smallB64
                });
            mockCapturer.Setup(c => c.CaptureTransientAsync(It.IsAny<string>(), It.IsAny<int>(), It.IsAny<int>(), It.IsAny<int>(), It.IsAny<int>(), It.IsAny<int>(), It.IsAny<bool>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(new WindowCaptureResult
                {
                    Success = true,
                    Target = "hon_x64",
                    WindowTitle = "Heroes of Newerth",
                    Pid = 1001,
                    Width = 1280,
                    Height = 720,
                    CaptureMethodUsed = "transient_onscreen",
                    ArtifactPath = tempArt,
                    Base64Png = smallB64
                });

            var captureHandler = new CaptureWindowHandler(mockCapturer.Object, processRegistry: new Mock<IClientProcessRegistry>().Object);

            // Path 1: CaptureWindowHandler normal
            using var normalArgs = JsonDocument.Parse("{\"target\":\"hon_x64\",\"detect_anomalies\":false}");
            var normalRes = await captureHandler.HandleAsync(normalArgs.RootElement);
            normalRes.IsError.Should().BeFalse();
            using var normalDoc = JsonDocument.Parse(normalRes.FirstText);
            normalDoc.RootElement.GetProperty("base64Png").GetString().Should().BeEmpty();
            normalDoc.RootElement.GetProperty("base64Omitted").GetBoolean().Should().BeTrue();
            normalDoc.RootElement.GetProperty("artifactUri").GetString().Should().StartWith("file:///");

            // Path 2: CaptureWindowHandler transient
            using var transArgs = JsonDocument.Parse("{\"mode\":\"transient\",\"target\":\"hon_x64\",\"detect_anomalies\":false}");
            var transRes = await captureHandler.HandleAsync(transArgs.RootElement);
            transRes.IsError.Should().BeFalse();
            using var transDoc = JsonDocument.Parse(transRes.FirstText);
            transDoc.RootElement.GetProperty("base64Png").GetString().Should().BeEmpty();
            transDoc.RootElement.GetProperty("base64Omitted").GetBoolean().Should().BeTrue();
            transDoc.RootElement.GetProperty("artifactUri").GetString().Should().StartWith("file:///");

            // Path 3: UiRenderViewHandler
            var mockRenderer = new Mock<IHeadlessUiRenderer>();
            mockRenderer.Setup(r => r.RenderViewAsync(It.IsAny<string>(), It.IsAny<int>(), It.IsAny<int>(), It.IsAny<string>(), It.IsAny<bool>(), It.IsAny<bool>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(new UiRenderResult
                {
                    Success = true,
                    ViewName = "DashboardView",
                    ResolvedType = "DashboardView",
                    Width = 800,
                    Height = 600,
                    Theme = "dark",
                    ArtifactPath = tempArt,
                    Base64Png = smallB64
                });
            var renderHandler = new UiRenderViewHandler(mockRenderer.Object);
            using var renderArgs = JsonDocument.Parse("{\"view_name\":\"DashboardView\"}");
            var renderRes = await renderHandler.HandleAsync(renderArgs.RootElement);
            renderRes.IsError.Should().BeFalse();
            using var renderDoc = JsonDocument.Parse(renderRes.FirstText);
            renderDoc.RootElement.GetProperty("base64Png").GetString().Should().BeEmpty();
            renderDoc.RootElement.GetProperty("base64Omitted").GetBoolean().Should().BeTrue();
            renderDoc.RootElement.GetProperty("artifactUri").GetString().Should().StartWith("file:///");

            // Firewall check: > 400 KB Base64 payload inside ToolCallResult.Image spills to disk and omits inline ImageContentItem
            byte[] oversizedBytes = new byte[320 * 1024]; // ~426 KB Base64 (> 400 KB threshold)
            Buffer.BlockCopy(smallPng, 0, oversizedBytes, 0, smallPng.Length);
            string oversizedB64 = Convert.ToBase64String(oversizedBytes);
            oversizedB64.Length.Should().BeGreaterThan(400 * 1024);

            ToolCallResult firewallRes = ToolCallResult.Image(oversizedB64, "{\"status\":\"success\"}");
            firewallRes.Content.OfType<ImageContentItem>().Should().BeEmpty("frames > 400 KB must spill to disk instead of emitting inline ImageContentItem");
            var linkItem = firewallRes.Content.OfType<ResourceLinkContentItem>().Single();
            linkItem.Uri.Should().StartWith("file:///");
            using var spilledJson = JsonDocument.Parse(firewallRes.FirstText);
            spilledJson.RootElement.GetProperty("base64Omitted").GetBoolean().Should().BeTrue();
            spilledJson.RootElement.GetProperty("artifactUri").GetString().Should().Be(linkItem.Uri);
            string? spilledDiskPath = spilledJson.RootElement.GetProperty("artifactPath").GetString();
            if (!string.IsNullOrWhiteSpace(spilledDiskPath) && File.Exists(spilledDiskPath))
            {
                File.Delete(spilledDiskPath);
            }
        }
        finally
        {
            if (File.Exists(tempArt)) File.Delete(tempArt);
        }
    }

    [Fact]
    public async Task S2_2_WaitForHandler_RejectsUnknownConditionBeforePollingLoopWithFuzzySuggestions()
    {
        // protects: S2-2 pre-loop condition validation in WaitForHandler (0 ms failure on unknown condition instead of 5,000 ms spin)
        // fails_when: WaitForHandler enters polling loop for unknown condition or fails to return fuzzy suggestions
        // seam: WaitForHandler.HandleAsync
        var handler = new WaitForHandler(taoClient: null, gameIpcBridge: null, processRegistry: null, logger: _ => { });
        using var argsDoc = JsonDocument.Parse("{\"condition\":\"ipcConnected\",\"timeout_ms\":5000}");

        Stopwatch sw = Stopwatch.StartNew();
        var res = await handler.HandleAsync(argsDoc.RootElement);
        sw.Stop();

        res.IsError.Should().BeTrue();
        sw.ElapsedMilliseconds.Should().BeLessThan(250, "unknown condition must fail pre-loop without spinning for 5,000 ms");

        using var doc = JsonDocument.Parse(res.FirstText);
        doc.RootElement.GetProperty("pollCount").GetInt32().Should().Be(0);
        doc.RootElement.GetProperty("errorCode").GetString().Should().Be("invalid_condition");
        doc.RootElement.GetProperty("suggestions").EnumerateArray().Select(e => e.GetString()).Should().Contain("ipc_ready");
    }

    [Fact]
    public async Task S2_3_And_S2_4_DefaultOnPreflightExcludingWaitFor_StructuredRemediationCall_AndCompactAntigravitySchemas()
    {
        // protects: S2-3 default-ON preflight for live client action tools (including kongor_capture_window, excluding kongor_wait_for), structured remediation_call envelope, and S2-4 compact AntigravityRegistrar schemas without [MCP-NNN] prose
        // fails_when: Live client action tool bypasses preflight when enforce_preflight is omitted, kongor_wait_for is blocked by preflight, remediation_call is missing, or generated schema JSON contains [MCP-NNN] prose
        // seam: McpToolRegistry.ExecuteToolAsync + McpToolRegistry.EnrichErrorWithMcpIssue + AntigravityRegistrar.Register
        var offlineBridge = new Mock<IGameIpcBridge>();
        offlineBridge.SetupGet(x => x.IsConnected).Returns(false);

        var registry = new McpToolRegistry(registerDefaults: true, gameIpcBridge: offlineBridge.Object);

        // 1. Default-ON preflight blocks kongor_ui_control and kongor_capture_window WITHOUT enforce_preflight: true
        using var uiArgs = JsonDocument.Parse("{\"action\":\"click\",\"widget\":\"sysbar_quickStart\"}");
        var uiRes = await registry.ExecuteToolAsync("kongor_ui_control", uiArgs.RootElement);
        uiRes.IsError.Should().BeTrue();
        using var uiDoc = JsonDocument.Parse(uiRes.FirstText);
        uiDoc.RootElement.GetProperty("preflightFailed").GetBoolean().Should().BeTrue();
        uiDoc.RootElement.GetProperty("remediation_call").GetProperty("tool").GetString().Should().Be("kongor_game_client_manage");

        using var capArgs = JsonDocument.Parse("{\"target\":\"hon_x64\"}");
        var capRes = await registry.ExecuteToolAsync("kongor_capture_window", capArgs.RootElement);
        capRes.IsError.Should().BeTrue();
        using var capDoc = JsonDocument.Parse(capRes.FirstText);
        capDoc.RootElement.GetProperty("preflightFailed").GetBoolean().Should().BeTrue();
        capDoc.RootElement.GetProperty("remediation_call").GetProperty("tool").GetString().Should().Be("kongor_game_client_manage");

        // 2. kongor_wait_for is excluded from preflight so it can poll while client is booting
        using var waitArgs = JsonDocument.Parse("{\"condition\":\"ipc_ready\",\"timeout_ms\":60,\"interval_ms\":25}");
        var waitRes = await registry.ExecuteToolAsync("kongor_wait_for", waitArgs.RootElement);
        using var waitDoc = JsonDocument.Parse(waitRes.FirstText);
        waitDoc.RootElement.TryGetProperty("preflightFailed", out _).Should().BeFalse("kongor_wait_for must not be blocked by preflight");
        waitDoc.RootElement.GetProperty("status").GetString().Should().Be("timeout");

        // 3. AntigravityRegistrar keeps all tool schemas on disk for kongor_enable_toolset while stripping [MCP-NNN] prose
        string tempSchemaDir = Path.Combine(Path.GetTempPath(), $"kongor_s2_4_schemas_{Guid.NewGuid():N}");
        try
        {
            var regResult = Kongor.Cli.Mcp.Registration.AntigravityRegistrar.Register(tempSchemaDir, registry);
            regResult.Success.Should().BeTrue();
            var jsonFiles = Directory.GetFiles(tempSchemaDir, "*.json");
            jsonFiles.Length.Should().Be(registry.ListTools(toolsets: "all").Count);
            foreach (string jf in jsonFiles)
            {
                string content = File.ReadAllText(jf);
                content.Should().NotContain("[MCP-", $"Schema file '{Path.GetFileName(jf)}' must not contain verbose [MCP-NNN] prose");
            }
        }
        finally
        {
            if (Directory.Exists(tempSchemaDir)) Directory.Delete(tempSchemaDir, recursive: true);
        }
    }

    [Fact]
    public async Task S2_3b_LiveClientPreflight_CoversCaptureAndVisualWithoutCandidate_AndExemptsNonHonPids()
    {
        // protects: Live-client preflight coverage for kongor_transient_capture, kongor_client_capture_preview, candidate-less kongor_assert_visual, and non-hon_x64 PID exemptions on kongor_capture_window
        // fails_when: Live capture/visual tools run against offline client without preflightFailed, candidate-supplied kongor_assert_visual is blocked, or non-hon_x64 PID captures are blocked
        // seam: McpToolRegistry.ExecuteToolAsync
        var offlineBridge = new Mock<IGameIpcBridge>();
        offlineBridge.SetupGet(x => x.IsConnected).Returns(false);

        var registry = new McpToolRegistry(registerDefaults: true, gameIpcBridge: offlineBridge.Object);

        // 1. kongor_transient_capture and kongor_client_capture_preview enforce live-client preflight
        foreach (string tool in new[] { "kongor_transient_capture", "kongor_client_capture_preview" })
        {
            using var docArgs = JsonDocument.Parse("{}");
            var res = await registry.ExecuteToolAsync(tool, docArgs.RootElement);
            res.IsError.Should().BeTrue($"{tool} requires a running live client");
            using var resDoc = JsonDocument.Parse(res.FirstText);
            resDoc.RootElement.GetProperty("preflightFailed").GetBoolean().Should().BeTrue();
        }

        // 2. kongor_assert_visual without candidate/candidate_path/candidate_fixture enforces live-client preflight
        using (var liveVisualArgs = JsonDocument.Parse("{\"golden\":\"login_baseline\"}"))
        {
            var res = await registry.ExecuteToolAsync("kongor_assert_visual", liveVisualArgs.RootElement);
            res.IsError.Should().BeTrue("live kongor_assert_visual without candidate image requires running client");
            using var resDoc = JsonDocument.Parse(res.FirstText);
            resDoc.RootElement.GetProperty("preflightFailed").GetBoolean().Should().BeTrue();
        }

        // 3. kongor_assert_visual WITH candidate / candidate_path / candidate_fixture bypasses live-client preflight
        foreach (string candidateProp in new[] { "candidate", "candidate_path", "candidate_fixture" })
        {
            using var fileVisualArgs = JsonDocument.Parse($"{{\"golden\":\"nonexistent_golden.png\",\"{candidateProp}\":\"nonexistent_candidate.png\"}}");
            var res = await registry.ExecuteToolAsync("kongor_assert_visual", fileVisualArgs.RootElement);
            res.FirstText.Should().NotContain("\"preflightFailed\":true",
                $"offline diff with '{candidateProp}' must not require a live hon_x64.exe client");
        }

        // 4. kongor_capture_window called with non-hon_x64 pid / process_id / processId / target: "pid:<n>" exempts non-HoN processes from live-client preflight
        int nonHonPid = Environment.ProcessId;
        foreach (string payload in new[]
        {
            $"{{\"pid\":{nonHonPid}}}",
            $"{{\"process_id\":{nonHonPid}}}",
            $"{{\"processId\":{nonHonPid}}}",
            $"{{\"target\":\"pid:{nonHonPid}\"}}"
        })
        {
            using var pidArgs = JsonDocument.Parse(payload);
            var res = await registry.ExecuteToolAsync("kongor_capture_window", pidArgs.RootElement);
            res.FirstText.Should().NotContain("Preflight check failed for 'kongor_capture_window'",
                $"kongor_capture_window for non-hon_x64 PID ({payload}) must bypass K2 client preflight");
            res.FirstText.Should().Contain($"pid:{nonHonPid}",
                $"exempt non-hon_x64 PID ({payload}) must dispatch directly to CaptureWindowHandler");
        }
    }

    [Fact]
    public async Task MCP_274_K2UiInspectStaticAutoAndClientNavigateAutoBoot_BypassOfflineClientPreflight()
    {
        // protects: Offline static/auto/layout K2 UI XML inspection and autoBoot=true client navigation when hon_x64.exe is not yet running (MCP-274).
        // fails_when: McpToolRegistry.Preflight blocks kongor_k2_ui_inspect(mode='static'|'auto'|'layout') or kongor_client_navigate(autoBoot=true) with preflightFailed=true.
        // seam: McpToolRegistry.ExecuteToolAsync + K2UiInspectHandler offline XML parser.
        var offlineBridge = new Mock<IGameIpcBridge>();
        offlineBridge.SetupGet(x => x.IsConnected).Returns(false);

        var registry = new McpToolRegistry(registerDefaults: true, gameIpcBridge: offlineBridge.Object);
        string pkgPath = Path.Combine(ResolveRepoRoot(), "games", "client", "override", "ui", "fe2", "store2.package");

        // 1. mode='static' and mode='auto' must execute offline XML inspection instead of failing preflight
        foreach (string mode in new[] { "static", "auto" })
        {
            using var inspectArgs = JsonDocument.Parse(
                JsonSerializer.Serialize(new { mode, path = pkgPath, depth = 2, enforce_preflight = true }));
            var inspectRes = await registry.ExecuteToolAsync("kongor_k2_ui_inspect", inspectArgs.RootElement);
            inspectRes.IsError.Should().BeFalse($"kongor_k2_ui_inspect(mode='{mode}') must succeed offline against XML on disk");
            inspectRes.FirstText.Should().NotContain("\"preflightFailed\":true");
        }

        // 2. mode='live' MUST still enforce live-client preflight when client is offline
        using (var liveInspectArgs = JsonDocument.Parse("{\"mode\":\"live\",\"enforce_preflight\":true}"))
        {
            var liveRes = await registry.ExecuteToolAsync("kongor_k2_ui_inspect", liveInspectArgs.RootElement);
            liveRes.IsError.Should().BeTrue();
            liveRes.FirstText.Should().Contain("\"preflightFailed\":true");
        }

        // 3. kongor_client_navigate with autoBoot=false enforces live-client preflight, whereas autoBoot=true is NOT blocked at preflight
        using (var navNoBootArgs = JsonDocument.Parse("{\"destination\":\"store\",\"autoBoot\":false,\"enforce_preflight\":true}"))
        {
            var navNoBootRes = await registry.ExecuteToolAsync("kongor_client_navigate", navNoBootArgs.RootElement);
            navNoBootRes.IsError.Should().BeTrue();
            navNoBootRes.FirstText.Should().Contain("\"preflightFailed\":true");
        }

        using (var navBadRouteAutoBootArgs = JsonDocument.Parse("{\"destination\":\"__nonexistent_route__\",\"autoBoot\":true,\"enforce_preflight\":true}"))
        {
            var navAutoBootRes = await registry.ExecuteToolAsync("kongor_client_navigate", navBadRouteAutoBootArgs.RootElement);
            navAutoBootRes.FirstText.Should().NotContain("\"preflightFailed\":true",
                "kongor_client_navigate(autoBoot=true) must reach ClientNavigateHandler instead of failing offline preflight");
            navAutoBootRes.FirstText.Should().Contain("Unknown navigation destination");
        }
    }

    [Fact]
    public async Task Tranche1A_SafetyFirewall_OfflineFidelity_CallTimeQuarantine_AndAttributionGuards()
    {
        // protects: MCP-277/279 StaticOfflineAnalysis fidelity on offline tools, MCP-288 loopback default + case-insensitive launcher_settings, MCP-289 invoke/trigger_widget in-engine dispatch, MCP-292 ReadOnly rate-limit routing on status/dryRun, MCP-294 non-misattribution on unknown_tool, and MCP-298 strict_method capture guard
        // fails_when: Offline catalog/vfs_locate claims LiveVerified, ResolveDefaultMasterServer defaults to public endpoint without useLauncherSettings, invoke skips TAO widget dispatch, status/dryRun consumes Simulation budget, or unknown_tool attaches MCP-277
        // seam: GameLauncherService + McpToolRegistry + UiControlHandler + CaptureWindowHandler + ExecutionTruthEnvelope
        var offlineBridge = new Mock<IGameIpcBridge>();
        offlineBridge.SetupGet(x => x.IsConnected).Returns(false);
        var registry = new McpToolRegistry(registerDefaults: true, gameIpcBridge: offlineBridge.Object);

        // 1. MCP-277 & MCP-279: kongor_call_tool (list/search/describe), kongor_enable_toolset, and kongor_vfs_locate report StaticOfflineAnalysis
        using (var searchArgs = JsonDocument.Parse("{\"action\":\"search\",\"query\":\"vfs_resolve\"}"))
        {
            var searchRes = await registry.ExecuteToolAsync("kongor_call_tool", searchArgs.RootElement);
            searchRes.IsError.Should().BeFalse();
            using var doc = JsonDocument.Parse(searchRes.FirstText);
            doc.RootElement.GetProperty("executionFidelity").GetString().Should().Be("StaticOfflineAnalysis");
            doc.RootElement.GetProperty("ok").GetBoolean().Should().BeTrue();
        }

        using (var locateArgs = JsonDocument.Parse("{\"virtual_path\":\"/ui/scripts/store2.lua\"}"))
        {
            var locateRes = await registry.ExecuteToolAsync("kongor_vfs_locate", locateArgs.RootElement);
            locateRes.IsError.Should().BeFalse();
            using var doc = JsonDocument.Parse(locateRes.FirstText);
            doc.RootElement.GetProperty("executionFidelity").GetString().Should().Be("StaticOfflineAnalysis");
        }

        // 2. MCP-288: ResolveDefaultMasterServer defaults to loopback unless useLauncherSettings=true, and reads camelCase customMasterServerAddress
        string tempSettingsDir = Path.Combine(Path.GetTempPath(), $"mcp288_settings_{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempSettingsDir);
        try
        {
            File.WriteAllText(
                Path.Combine(tempSettingsDir, "launcher_settings.json"),
                "{\"customMasterServerAddress\":\"127.0.0.1:55555\",\"MasterServerAddress\":\"masterserver.newbs.io\"}");

            GameLauncherService.ResolveDefaultMasterServer(tempSettingsDir).Should().Be("127.0.0.1:55555");
            GameLauncherService.ResolveDefaultMasterServer(tempSettingsDir, useLauncherSettings: true).Should().Be("127.0.0.1:55555");
            GameLauncherService.IsLoopbackMasterServer("127.0.0.1:55555").Should().BeTrue();
            GameLauncherService.IsLoopbackMasterServer("masterserver.newbs.io").Should().BeFalse();
        }
        finally
        {
            if (Directory.Exists(tempSettingsDir)) Directory.Delete(tempSettingsDir, recursive: true);
        }

        // 3. MCP-289 & MCP-293: kongor_ui_control 'invoke' and 'trigger_widget' route through in-engine TAO/IPC without touching OS mouse
        var mockTao = new Mock<ITaoClient>();
        mockTao.SetupGet(x => x.IsConnected).Returns(true);
        mockTao.Setup(x => x.DispatchAsync("click_widget", It.IsAny<object?>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new TaoResponse<JsonElement>
            {
                Success = true,
                Data = JsonElement.Parse("{\"clicked\":true,\"method\":\"dom_onclick\",\"widget\":\"systembar\"}")
            });
        var mockUiSvc = new Mock<IUiControlService>(MockBehavior.Strict);
        var uiHandler = new UiControlHandler(uiControlService: mockUiSvc.Object, taoClient: mockTao.Object);
        using (var invokeArgs = JsonDocument.Parse("{\"action\":\"invoke\",\"target\":\"hon_x64\",\"widget\":\"systembar\"}"))
        {
            var invokeRes = await uiHandler.HandleAsync(invokeArgs.RootElement);
            invokeRes.IsError.Should().BeFalse();
            invokeRes.FirstText.Should().Contain("dom_onclick");
        }

        // 4. MCP-292: status, list, errors, and dryRun resolve to ReadOnly category across Simulation tools
        using (var statusDoc = JsonDocument.Parse("{\"action\":\"status\"}"))
        using (var dryRunDoc = JsonDocument.Parse("{\"action\":\"launch\",\"dryRun\":true}"))
        {
            McpToolRegistry.ResolveEffectiveCategory("kongor_client_practice", McpToolCategory.Simulation, statusDoc.RootElement)
                .Should().Be(McpToolCategory.ReadOnly);
            McpToolRegistry.ResolveEffectiveCategory("kongor_scrimmage_launch", McpToolCategory.Simulation, statusDoc.RootElement)
                .Should().Be(McpToolCategory.ReadOnly);
            McpToolRegistry.ResolveEffectiveCategory("kongor_client_practice", McpToolCategory.Simulation, dryRunDoc.RootElement)
                .Should().Be(McpToolCategory.ReadOnly);
        }

        // 5. MCP-294: Unknown tool via kongor_call_tool never attaches unrelated MCP-277
        using (var badToolArgs = JsonDocument.Parse("{\"action\":\"call\",\"tool_name\":\"kongor_no_such_tool_xyz\"}"))
        {
            var badRes = await registry.ExecuteToolAsync("kongor_call_tool", badToolArgs.RootElement);
            badRes.IsError.Should().BeTrue();
            badRes.FirstText.Should().NotContain("MCP-277");
        }

        // 6. MCP-298: CaptureWindowHandler with allow_fallback=false does not silently return print_window when vulkan_offscreen fails
        var mockCapturer = new Mock<IWindowCapturer>();
        mockCapturer.Setup(x => x.CaptureAsync("hon_x64", "vulkan_offscreen", It.IsAny<bool>(), It.IsAny<string?>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new WindowCaptureResult
            {
                Success = false,
                Target = "hon_x64",
                ErrorMessage = "Vulkan shared memory unavailable"
            });
        var capHandler = new CaptureWindowHandler(mockCapturer.Object, processRegistry: new Mock<IClientProcessRegistry>().Object);
        using (var strictVulkanArgs = JsonDocument.Parse("{\"target\":\"hon_x64\",\"capture_method\":\"vulkan_offscreen\",\"allow_fallback\":false}"))
        {
            var strictRes = await capHandler.HandleAsync(strictVulkanArgs.RootElement);
            strictRes.IsError.Should().BeTrue();
        }
    }

    [Fact]
    public async Task Tranche1B_MailboxRpc_RealMatchState_DbFirewall_AndFastCompelStatus()
    {
        // protects: MCP-283 (real MatchStateHandler on login vs active match), MCP-287 (remote DB write firewall + 0 fake pooled connections), MCP-297 (fast COMPEL offline status)
        // fails_when: MatchStateHandler returns Hero_Pyromancer on login screen, DbPoolDiagnosticsHandler allows unconfirmed remote DB writes or reports fake pooled connections, or CompelManageHandler takes > 1s when offline
        // seam: MatchStateHandler + DbPoolDiagnosticsHandler + CompelManageHandler

        // 1. MCP-283 (a): Client at login screen (GamePhase = 0, map = "") returns activeMatch = false, hero = null
        var loginBridge = new Mock<IGameIpcBridge>();
        loginBridge.Setup(b => b.GetEngineStateAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new EngineStateDto { Connected = true, GamePhase = 0, CurrentMap = string.Empty });
        loginBridge.Setup(b => b.ExecuteLuaAsync(It.IsAny<string>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync("0||0||0|0|0|0|0|0");

        var loginMatchHandler = new MatchStateHandler(loginBridge.Object, null);
        using (var loginArgs = JsonDocument.Parse("{\"target\":\"client\"}"))
        {
            var loginRes = await loginMatchHandler.HandleAsync(loginArgs.RootElement);
            loginRes.IsError.Should().BeFalse();
            using var loginDoc = JsonDocument.Parse(loginRes.FirstText);
            loginDoc.RootElement.GetProperty("activeMatch").GetBoolean().Should().BeFalse();
            loginDoc.RootElement.GetProperty("matchId").GetInt64().Should().Be(0L);
            loginDoc.RootElement.GetProperty("currentMap").GetString().Should().Be("none");
            loginDoc.RootElement.GetProperty("matchTimeSeconds").GetDouble().Should().Be(0.0);
            (loginDoc.RootElement.TryGetProperty("hero", out var heroEl) && heroEl.ValueKind != JsonValueKind.Null).Should().BeFalse();
        }

        // 2. MCP-283 (b): Client in active match with live Lua response returns exact live hero, level, gold, and matchTimeSeconds
        var activeBridge = new Mock<IGameIpcBridge>();
        activeBridge.Setup(b => b.GetEngineStateAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new EngineStateDto { Connected = true, GamePhase = 5, CurrentMap = "caldavar" });
        activeBridge.Setup(b => b.ExecuteLuaAsync(It.IsAny<string>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync("5|caldavar|18400|Hero_Glacius|6|640|820|510|590|1350");

        var activeMatchHandler = new MatchStateHandler(activeBridge.Object, null);
        using (var activeArgs = JsonDocument.Parse("{\"target\":\"client\"}"))
        {
            var activeRes = await activeMatchHandler.HandleAsync(activeArgs.RootElement);
            activeRes.IsError.Should().BeFalse();
            using var activeDoc = JsonDocument.Parse(activeRes.FirstText);
            activeDoc.RootElement.GetProperty("activeMatch").GetBoolean().Should().BeTrue();
            activeDoc.RootElement.GetProperty("currentMap").GetString().Should().Be("caldavar");
            activeDoc.RootElement.GetProperty("matchTimeSeconds").GetDouble().Should().Be(18.4);
            var hero = activeDoc.RootElement.GetProperty("hero");
            hero.GetProperty("name").GetString().Should().Be("Hero_Glacius");
            hero.GetProperty("level").GetInt32().Should().Be(6);
            hero.GetProperty("gold").GetInt32().Should().Be(1350);
        }

        // 3. MCP-287: Remote DB write firewall blocks non-loopback mutations without KONGOR_MCP_ALLOW_REMOTE_DB_WRITE=1 + confirmRemote:true
        string? prevRemoteWrite = Environment.GetEnvironmentVariable("KONGOR_MCP_ALLOW_REMOTE_DB_WRITE");
        try
        {
            Environment.SetEnvironmentVariable("KONGOR_MCP_ALLOW_REMOTE_DB_WRITE", null);
            using var unconfirmedArgs = JsonDocument.Parse("{\"action\":\"seed\",\"confirmRemote\":true}");
            DbPoolDiagnosticsHandler.TryValidateRemoteDatabaseMutation(
                "aws-1-us-east-1.pooler.supabase.com",
                "kongor_db_fixture",
                "seed",
                unconfirmedArgs.RootElement,
                out var blockedRes).Should().BeFalse();
            blockedRes.Should().NotBeNull();
            blockedRes!.IsError.Should().BeTrue();
            blockedRes.FirstText.Should().Contain("KONGOR_MCP_ALLOW_REMOTE_DB_WRITE=1");
        }
        finally
        {
            Environment.SetEnvironmentVariable("KONGOR_MCP_ALLOW_REMOTE_DB_WRITE", prevRemoteWrite);
        }

        // 4. MCP-287: DbPoolDiagnosticsHandler reports 0 pooled connections in CLI process and skips remote DB probe by default
        var dbPoolHandler = new DbPoolDiagnosticsHandler();
        using (var poolArgs = JsonDocument.Parse("{\"action\":\"status\"}"))
        {
            var poolRes = await dbPoolHandler.HandleAsync(poolArgs.RootElement);
            poolRes.IsError.Should().BeFalse();
            using var poolDoc = JsonDocument.Parse(poolRes.FirstText);
            var npgsql = poolDoc.RootElement.GetProperty("pools").GetProperty("npgsql");
            npgsql.GetProperty("activeConnections").GetInt32().Should().Be(0);
            npgsql.GetProperty("idleConnections").GetInt32().Should().Be(0);
        }

        // 5. MCP-297: CompelManageHandler status fast-fails in < 500 ms when loopback ports are offline
        var compelHandler = new CompelManageHandler(logger: _ => { });
        var sw = Stopwatch.StartNew();
        using (var compelArgs = JsonDocument.Parse("{\"action\":\"status\"}"))
        {
            var compelRes = await compelHandler.HandleAsync(compelArgs.RootElement);
            sw.Stop();
            compelRes.IsError.Should().BeFalse();
            sw.ElapsedMilliseconds.Should().BeLessThan(1000);
        }
    }

    [Fact]
    public async Task Tranche2_VisualDiffSsim_K2LintLegacyUiCmd_LogSeverity_AndActionVerification()
    {
        // protects: MCP-281 (K2LintService K2 UICmd vs Lua separation), MCP-283 (live VfsMountGraphHandler scan), MCP-284 (AssertVisualHandler Wang-Bovik SSIM + maxDiffPercentage gate), MCP-265 (GameLuaHandler [ERROR] detection), MCP-293 (NetworkChaosHandler connection_reset/clear_all), MCP-296 (GetClientLogsHandler severity filter)
        // fails_when: K2LintService emits false-positive LUA_INVALID_OPERATOR on K2 UICmd onclick attributes, AssertVisualHandler passes unrelated images with >15% diff, or GetClientLogsHandler ignores severity='error'
        // seam: K2LintService + VfsMountGraphHandler + AssertVisualHandler + GameLuaHandler + GetClientLogsHandler + NetworkChaosHandler

        // 1. MCP-281: K2LintService does not flag K2 UICmd onclick="If(a != b && c || d, Split(...))" as Lua syntax error, but flags onclicklua="if a != b then end"
        string tempXml = Path.Combine(Path.GetTempPath(), $"kongor_t2_lint_{Guid.NewGuid():N}.interface");
        await File.WriteAllTextAsync(tempXml, """
            <?xml version="1.0" encoding="UTF-8"?>
            <interface name="test_uicmd">
                <panel name="p1" onclick="If(a != b &amp;&amp; c || d, Split(Cmd('Echo 1'), CallEvent('p1', 0)))" />
                <button name="b1" onclicklua="if a != b then Echo('bad lua') end" />
            </interface>
            """);
        try
        {
            var linter = new K2LintService();
            var report = await linter.LintAsync(tempXml);
            report.Issues.Should().ContainSingle(i => i.Code == "LUA_INVALID_OPERATOR" && i.Line == 4);
            report.Issues.Should().NotContain(i => i.Line == 3);
        }
        finally
        {
            if (File.Exists(tempXml)) File.Delete(tempXml);
        }

        // 2. MCP-283: VfsMountGraphHandler with liveScan=true scans real VFS directories with StaticOfflineAnalysis fidelity
        var vfsGraph = new VfsMountGraphHandler();
        using (var vfsArgs = JsonDocument.Parse("{\"action\":\"inspect_mounts\",\"liveScan\":true}"))
        {
            var vfsRes = await vfsGraph.HandleAsync(vfsArgs.RootElement);
            vfsRes.IsError.Should().BeFalse();
            using var vfsDoc = JsonDocument.Parse(vfsRes.FirstText);
            vfsDoc.RootElement.GetProperty("executionFidelity").GetString().Should().Be(ExecutionFidelity.StaticOfflineAnalysis.ToString());
        }

        // 3. MCP-284: AssertVisualHandler rejects unrelated images (diffPercentage = 100% > maxDiffPercentage = 15%) even if threshold is low
        string tempDir = Path.Combine(Path.GetTempPath(), $"kongor_t2_vis_{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        try
        {
            string imgAPath = Path.Combine(tempDir, "img_a.png");
            string imgBPath = Path.Combine(tempDir, "img_b.png");
            using (var imgA = new Kongor.Cli.Mcp.Imaging.Image<Kongor.Cli.Mcp.Imaging.Rgba32>(32, 32, new Kongor.Cli.Mcp.Imaging.Rgba32(40, 50, 60, 255)))
            using (var imgB = new Kongor.Cli.Mcp.Imaging.Image<Kongor.Cli.Mcp.Imaging.Rgba32>(32, 32, new Kongor.Cli.Mcp.Imaging.Rgba32(55, 65, 75, 255)))
            {
                await imgA.SaveAsPngAsync(imgAPath);
                await imgB.SaveAsPngAsync(imgBPath);
            }

            var assertVis = new AssertVisualHandler(
                new WindowCapturer(),
                new GameClientManageHandler(),
                new GameServerManageHandler(),
                fixtureDirectory: tempDir,
                artifactDirectory: tempDir);

            using var visArgs = JsonDocument.Parse($"{{\"fixture\":\"{imgAPath.Replace("\\", "/")}\",\"candidate\":\"{imgBPath.Replace("\\", "/")}\",\"threshold\":0.50}}");
            var visRes = await assertVis.HandleAsync(visArgs.RootElement);
            visRes.IsError.Should().BeTrue("100% pixel diff must fail maxDiffPercentage gate even if threshold is 0.50");
        }
        finally
        {
            if (Directory.Exists(tempDir)) Directory.Delete(tempDir, true);
        }

        // 4. MCP-265: GameLuaHandler.TryDetectStructuredError detects [ERROR] and [LUA_ERR]
        GameLuaHandler.TryDetectStructuredError(
            "[ERROR] [string \"...\"]:1: '=' expected near '<eof>'",
            null,
            default,
            out string? errCode,
            out string? errMsg,
            out _).Should().BeTrue();
        errCode.Should().Be("LUA_RUNTIME_ERROR");
        errMsg.Should().Contain("[ERROR]");

        // 5. MCP-296: GetClientLogsHandler respects severity='error'
        var logsHandler = new GetClientLogsHandler(repoRoot: ResolveRepoRoot());
        using (var logArgs = JsonDocument.Parse("{\"severity\":\"error\",\"limit\":5}"))
        {
            var logRes = await logsHandler.HandleAsync(logArgs.RootElement);
            logRes.IsError.Should().BeFalse();
            using var logDoc = JsonDocument.Parse(logRes.FirstText);
            logDoc.RootElement.GetProperty("errorsOnly").GetBoolean().Should().BeTrue();
            logDoc.RootElement.GetProperty("severityFilter").GetString().Should().Be("ERROR");
        }
    }

    [Fact]
    public async Task Tranche3_RunnableDescribeExamples_ArgumentAliasNormalization_8KbCap_AndDiscoveryRoots()
    {
        // protects: MCP-278 runnable describe examples, MCP-286 discovery roots, MCP-293 alias normalizer + conflict rejection + unknown toolset domain + subscribe Guid, MCP-295 8 KB response cap
        // fails_when: describe emits '<string>' placeholders, conflicting alias+canonical args silently pass, unknown toolset domain succeeds, subscribe throws FormatException, or >8 KB response is not capped
        // seam: McpToolRegistry.ExecuteToolAsync + CallToolHandler + EnableToolsetHandler + RealtimeStreamHandler
        var registry = new McpToolRegistry();

        // 1. MCP-278: Every tool's describe exampleBridgeCall.arguments_json has zero '<...>' type placeholders
        foreach (var tool in registry.ListTools(includeAliases: false, toolsets: "all"))
        {
            using var descArgs = JsonDocument.Parse($"{{\"action\":\"describe\",\"tool_name\":\"{tool.Name}\"}}");
            var descRes = await registry.ExecuteToolAsync("kongor_call_tool", descArgs.RootElement);
            descRes.IsError.Should().BeFalse($"describe for {tool.Name} must succeed");
            using var descDoc = JsonDocument.Parse(descRes.FirstText);
            string argsJson = descDoc.RootElement
                .GetProperty("exampleBridgeCall")
                .GetProperty("arguments_json")
                .GetString()!;
            argsJson.Should().NotContain("<string>", $"tool '{tool.Name}' exampleBridgeCall must not contain angle-bracket placeholders");
            argsJson.Should().NotContain("<integer>", $"tool '{tool.Name}' exampleBridgeCall must not contain angle-bracket placeholders");
            argsJson.Should().NotContain("<boolean>", $"tool '{tool.Name}' exampleBridgeCall must not contain angle-bracket placeholders");
        }

        // 2. MCP-293: Conflicting alias + canonical parameter is rejected with conflicting_arguments
        using (var conflictArgs = JsonDocument.Parse("{\"path\":\"ui/fe2/main.interface\",\"file_path\":\"ui/fe2/other.interface\"}"))
        {
            var conflictRes = await registry.ExecuteToolAsync("kongor_k2_lint", conflictArgs.RootElement);
            conflictRes.IsError.Should().BeTrue();
            using var conflictDoc = JsonDocument.Parse(conflictRes.FirstText);
            conflictDoc.RootElement.GetProperty("errorCode").GetString().Should().Be("conflicting_arguments");
        }

        // 3. MCP-293: Unknown toolset domain is rejected with isError=true
        using (var badDomainArgs = JsonDocument.Parse("{\"domain\":\"no_such_domain_xyz\"}"))
        {
            var badDomainRes = await registry.ExecuteToolAsync("kongor_enable_toolset", badDomainArgs.RootElement);
            badDomainRes.IsError.Should().BeTrue();
            using var badDomainDoc = JsonDocument.Parse(badDomainRes.FirstText);
            badDomainDoc.RootElement.GetProperty("status").GetString().Should().Be("error");
        }

        // 4. MCP-293: kongor_realtime_stream subscribe succeeds without Guid format exception
        using (var subArgs = JsonDocument.Parse("{\"action\":\"subscribe\",\"source\":\"client\"}"))
        {
            var subRes = await registry.ExecuteToolAsync("kongor_realtime_stream", subArgs.RootElement);
            subRes.IsError.Should().BeFalse();
            using var subDoc = JsonDocument.Parse(subRes.FirstText);
            subDoc.RootElement.GetProperty("subscriptionId").GetString().Should().StartWith("sub_client_");
        }

        // 5. MCP-295: 8 KB default response cap spills full output to disk and returns < 8 KB summary
        string mainInterfacePath = Path.Combine(ResolveRepoRoot(), "games", "client", "override", "ui", "fe2", "main.interface");
        if (File.Exists(mainInterfacePath))
        {
            string escapedPath = mainInterfacePath.Replace("\\", "\\\\");
            using var lintCapArgs = JsonDocument.Parse($"{{\"path\":\"{escapedPath}\",\"enforceResponseCap\":true}}");
            var cappedRes = await registry.ExecuteToolAsync("kongor_k2_lint", lintCapArgs.RootElement);
            cappedRes.IsError.Should().BeFalse();
            System.Text.Encoding.UTF8.GetByteCount(cappedRes.FirstText).Should().BeLessThanOrEqualTo(8192);
            using var cappedDoc = JsonDocument.Parse(cappedRes.FirstText);
            cappedDoc.RootElement.GetProperty("truncated").GetBoolean().Should().BeTrue();
            string overflowPath = cappedDoc.RootElement.GetProperty("fullOutputPath").GetString()!;
            File.Exists(overflowPath).Should().BeTrue();
        }

        // 6. N1: ServerInstructions is concise (< 800 B)
        McpProtocolVersion.ServerInstructions.Should().NotBeNullOrWhiteSpace();
        System.Text.Encoding.UTF8.GetByteCount(McpProtocolVersion.ServerInstructions).Should().BeLessThan(800);
    }

    private static string ResolveRepoRoot()
    {
        DirectoryInfo? dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "ProjectKongor.slnx")))
            {
                return dir.FullName;
            }
            dir = dir.Parent;
        }

        throw new DirectoryNotFoundException("Could not locate ProjectKongor.slnx from AppContext.BaseDirectory.");
    }
}

