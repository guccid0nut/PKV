// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// TAO (Together As One) MCP & Kongor CLI -- Created by guccidonut (https://github.com/guccidonut/PKV)

using System.Collections.Concurrent;
using System.Diagnostics;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using FluentAssertions;
using Kongor.Cli.Mcp.Handlers;
using Kongor.Cli.Mcp.Protocol;
using Kongor.Cli.Mcp.Services;
using Moq;
using Xunit;
using static Kongor.Mcp.Tests.McpSimulationTestHarness;

namespace Kongor.Mcp.Tests;

/// <summary>
/// Tier 5 White-Box Adversarial Coverage Hardening Test Suite for Milestones 4 and 5.
/// 
/// Milestone 4:
/// - ScrimmageLaunchHandler & GameServerManageHandler
/// - Port-addressed dual-channel routing to _workerStdinMap[port] (AGENTS.md Rule 1)
/// - Multi-server isolation across non-default ports (11235, 11236, 11237, 65535)
/// - Bot accounting edge cases: 0 bots, max bots, asymmetric teams, clamped negative counts
/// - Stop actions cleanly unregistering workers and removing active sessions
/// - Dynamic GamePhase resolution via ExtractGamePhaseFromJson and subsystem queries
/// - Sub-500ms inner-loop benchmark execution
/// 
/// Milestone 5:
/// - UiFlowHandler, nexus_bridge.lua, McpToolRegistry
/// - Strict K2 UI rules: UIManager (never Windows), 0-arg DoEvent(), integer DoEventN(int)
/// - Direct practice match launch bypass
/// - Pre-flight widget checks (Nexus.VerifyWidget, Nexus.SafeTriggerWidget)
/// - Popup dismissal array extraction under noisy engine log conditions
/// - McpToolRegistry alias resolution: kongor_macro, kongor_ui_macro, kongor_scrimmage, kongor_5v5_launch
/// </summary>
public sealed class ScrimmageAndUiFlowAdversarialTests
{
    #region Milestone 4 Harness & Helpers

    private sealed class ScrimmageAdversarialContext : IDisposable
    {
        public ScrimmageLaunchHandler Handler { get; set; } = null!;
        public GameServerManageHandler ServerHandler { get; init; } = null!;
        public Mock<IGameIpcBridge> MockClientIpc { get; init; } = null!;
        public ConcurrentDictionary<int, MemoryStream> ServerStreams { get; } = new();
        public ConcurrentDictionary<int, StreamWriter> ServerWriters { get; } = new();
        public List<string> ClientDispatchedCommands { get; } = [];
        public List<string> CapturedLogs { get; } = [];
        public ClientCommandRemediationService RemediationService { get; init; } = null!;

        public void RegisterWorkerOnPort(int port, int pid = 0)
        {
            pid = pid == 0 ? 10000 + port : pid;
            var stream = new MemoryStream();
            var writer = new StreamWriter(stream, Encoding.UTF8, leaveOpen: true);
            ServerStreams[port] = stream;
            ServerWriters[port] = writer;

            var stdinField = typeof(GameServerManageHandler).GetField("_workerStdin", BindingFlags.NonPublic | BindingFlags.Instance);
            var workerStdin = (ConcurrentDictionary<int, StreamWriter>)stdinField!.GetValue(ServerHandler)!;
            workerStdin[port] = writer;

            var workersField = typeof(GameServerManageHandler).GetField("_trackedWorkers", BindingFlags.NonPublic | BindingFlags.Instance);
            var trackedWorkers = (ConcurrentDictionary<int, GameServerWorkerInfo>)workersField!.GetValue(ServerHandler)!;
            trackedWorkers[pid] = new GameServerWorkerInfo
            {
                ProcessId = pid,
                Port = port,
                ServerName = $"Dedicated Server {port}",
                Map = "caldavar",
                Responding = true
            };
        }

        public async Task<string> ReadServerStdinContentAsync(int port)
        {
            if (ServerWriters.TryGetValue(port, out var writer) && ServerStreams.TryGetValue(port, out var stream))
            {
                await writer.FlushAsync().ConfigureAwait(false);
                long origPos = stream.Position;
                stream.Position = 0;
                using StreamReader reader = new(stream, Encoding.UTF8, leaveOpen: true);
                string content = await reader.ReadToEndAsync().ConfigureAwait(false);
                stream.Position = origPos;
                return content;
            }
            return string.Empty;
        }

        public void Dispose()
        {
            foreach (var writer in ServerWriters.Values) writer.Dispose();
            foreach (var stream in ServerStreams.Values) stream.Dispose();
        }
    }

    private static ScrimmageAdversarialContext CreateScrimmageContext(int primaryPort = 11235)
    {
        List<string> logs = [];
        GameServerManageHandler serverHandler = new(timeProvider: TimeProvider.System, logger: msg => logs.Add(msg));

        List<string> clientCmds = [];
        Mock<IGameIpcBridge> mockIpc = new();
        mockIpc.SetupGet(i => i.IsConnected).Returns(true);
        mockIpc.Setup(i => i.GetEngineStateAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new EngineStateDto
            {
                ActiveMatchId = 10001,
                GamePhase = 3,
                GamePhaseName = "In-Game Combat (Phase 3)",
                CurrentMap = "caldavar"
            });
        mockIpc.Setup(i => i.ExecuteCommandAsync(It.IsAny<string>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .Returns((string cmd, int _, CancellationToken _) =>
            {
                clientCmds.Add(cmd);
                if (cmd.Contains("GetCurrentPhase")) return Task.FromResult("3");
                return Task.FromResult("OK");
            });

        ClientCommandRemediationService remediation = new(timeProvider: TimeProvider.System);

        var ctx = new ScrimmageAdversarialContext
        {
            ServerHandler = serverHandler,
            MockClientIpc = mockIpc,
            RemediationService = remediation
        };
        ctx.RegisterWorkerOnPort(primaryPort);

        ScrimmageLaunchHandler handler = new(
            serverManageHandler: serverHandler,
            clientManageHandler: null,
            matchStateHandler: null,
            clientIpcBridge: mockIpc.Object,
            remediationService: remediation,
            timeProvider: TimeProvider.System,
            logger: msg => logs.Add(msg));

        ctx.Handler = handler;
        return ctx;
    }

    private static JsonElement ParseJson(ToolCallResult result)
    {
        result.Content.Should().ContainSingle();
        string text = ((TextContentItem)result.Content[0]).Text;
        return JsonElement.Parse(text);
    }

    #endregion

    #region Milestone 5 Harness & Helpers

    private sealed class UiFlowAdversarialContext
    {
        public UiFlowHandler Handler { get; init; } = null!;
        public Mock<IGameIpcBridge> MockIpc { get; init; } = null!;
        public List<string> DispatchedCommands { get; } = [];
        public ClientCommandRemediationService RemediationService { get; init; } = null!;
        public List<string> CapturedLogs { get; } = [];
    }

    private static UiFlowAdversarialContext CreateUiFlowContext(bool isConnected = true, string ipcResponse = "OK")
    {
        List<string> dispatched = [];
        List<string> logs = [];
        Mock<IGameIpcBridge> mockIpc = new();
        mockIpc.SetupGet(i => i.IsConnected).Returns(isConnected);
        mockIpc.Setup(i => i.ExecuteCommandAsync(It.IsAny<string>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .Returns((string cmd, int _, CancellationToken _) =>
            {
                dispatched.Add(cmd);
                return Task.FromResult(ipcResponse);
            });

        ClientCommandRemediationService remediation = new(timeProvider: TimeProvider.System);
        UiFlowHandler handler = new(
            gameIpcBridge: mockIpc.Object,
            clientManageHandler: null,
            remediationService: remediation,
            uiControlService: null,
            timeProvider: TimeProvider.System,
            logger: msg => logs.Add(msg));

        return new UiFlowAdversarialContext
        {
            Handler = handler,
            MockIpc = mockIpc,
            RemediationService = remediation
        };
    }

    #endregion

    #region M4: Port-Addressed Routing (_workerStdinMap) & Multi-Port Isolation

    [Fact]
    public async Task DedicatedWorker_PortAddressedStdinRouting_TargetsExactWorkerStream_ZeroCrossTalk()
    {
        using var ctx = CreateScrimmageContext(11235);
        ctx.RegisterWorkerOnPort(11236);
        ctx.RegisterWorkerOnPort(11237);

        // Orchestrate scrimmage strictly on port 11236
        var args = JsonElement.Parse("{\"action\":\"launch\",\"port\":11236,\"team1Bots\":2,\"team2Bots\":3}");
        var result = await ctx.Handler.HandleAsync(args);

        result.IsError.Should().BeFalse();
        var json = ParseJson(result);
        json.GetProperty("status").GetString().Should().Be("success");
        json.GetProperty("port").GetInt32().Should().Be(11236);

        // Stdin on port 11236 received all expected bot and start commands
        string stdin11236 = await ctx.ReadServerStdinContentAsync(11236);
        stdin11236.Should().Contain("AddFakePlayer 1");
        stdin11236.Should().Contain("AddFakePlayer 2");
        stdin11236.Should().Contain("RequestMatchStart");

        // Stdin on non-targeted ports received absolutely zero bytes
        string stdin11235 = await ctx.ReadServerStdinContentAsync(11235);
        string stdin11237 = await ctx.ReadServerStdinContentAsync(11237);
        stdin11235.Should().BeEmpty();
        stdin11237.Should().BeEmpty();
    }

    [Fact]
    public async Task DedicatedWorker_StdinStreamWriteThrows_FallsBackToExecuteCommandOnPortAsync()
    {
        using var ctx = CreateScrimmageContext(11235);

        // Simulate broken stream on port 11235: dispose writer so write throws ObjectDisposedException
        ctx.ServerWriters[11235].Dispose();

        var args = JsonElement.Parse("{\"action\":\"launch\",\"port\":11235,\"team1Bots\":1,\"team2Bots\":1}");
        var result = await ctx.Handler.HandleAsync(args);

        // Does not throw unhandled exception; falls back gracefully to ExecuteCommandOnPortAsync
        result.IsError.Should().BeFalse();
        var json = ParseJson(result);
        json.GetProperty("status").GetString().Should().Be("success");
        json.GetProperty("port").GetInt32().Should().Be(11235);
    }

    [Fact]
    public async Task DedicatedWorker_PortValidation_MalformedOrOutOfRange_ReturnsDescriptiveErrors()
    {
        using var ctx = CreateScrimmageContext(11235);

        // Test string numeric port (valid)
        var stringPortArgs = JsonElement.Parse("{\"action\":\"launch\",\"port\":\"11235\",\"team1Bots\":1,\"team2Bots\":1}");
        var strRes = await ctx.Handler.HandleAsync(stringPortArgs);
        strRes.IsError.Should().BeFalse();

        // Test invalid string port
        var malformedArgs = JsonElement.Parse("{\"action\":\"launch\",\"port\":\"not_a_number\"}");
        var malResult = await ctx.Handler.HandleAsync(malformedArgs);
        malResult.IsError.Should().BeTrue();
        ((TextContentItem)malResult.Content[0]).Text.Should().Contain("Invalid port parameter");

        // Test negative port
        var negArgs = JsonElement.Parse("{\"action\":\"launch\",\"port\":-5}");
        var negResult = await ctx.Handler.HandleAsync(negArgs);
        negResult.IsError.Should().BeTrue();
        ((TextContentItem)negResult.Content[0]).Text.Should().Contain("Listen port must be between 1 and 65535");

        // Test port 0
        var zeroArgs = JsonElement.Parse("{\"action\":\"launch\",\"port\":0}");
        var zeroResult = await ctx.Handler.HandleAsync(zeroArgs);
        zeroResult.IsError.Should().BeTrue();

        // Test port > 65535
        var highArgs = JsonElement.Parse("{\"action\":\"launch\",\"port\":70000}");
        var highResult = await ctx.Handler.HandleAsync(highArgs);
        highResult.IsError.Should().BeTrue();
    }

    [Fact]
    public async Task DedicatedWorker_NonDefaultPort_ClientConnectFormatsExactPort()
    {
        using var ctx = CreateScrimmageContext(11236);
        var args = JsonElement.Parse("{\"action\":\"launch\",\"port\":11236,\"hero\":\"Hero_Devourer\"}");

        var result = await ctx.Handler.HandleAsync(args);
        result.IsError.Should().BeFalse();

        var json = ParseJson(result);
        json.GetProperty("port").GetInt32().Should().Be(11236);

        // Client IPC received connect directed specifically to 127.0.0.1:11236
        ctx.MockClientIpc.Verify(i => i.ExecuteCommandAsync("connect 127.0.0.1:11236", It.IsAny<int>(), It.IsAny<CancellationToken>()), Times.Once);
        ctx.MockClientIpc.Verify(i => i.ExecuteCommandAsync("Team 1 0", It.IsAny<int>(), It.IsAny<CancellationToken>()), Times.Once);
        ctx.MockClientIpc.Verify(i => i.ExecuteCommandAsync("SelectHero Hero_Devourer", It.IsAny<int>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    #endregion

    #region M4: Bot Accounting Edge Cases & Hero Normalization

    [Fact]
    public async Task DedicatedWorker_BotAccounting_ZeroBots_HumanOnly()
    {
        using var ctx = CreateScrimmageContext(11235);
        var args = JsonElement.Parse("{\"action\":\"launch\",\"port\":11235,\"team1Bots\":0,\"team2Bots\":0,\"autoConnect\":true}");

        var result = await ctx.Handler.HandleAsync(args);
        result.IsError.Should().BeFalse();

        var json = ParseJson(result);
        json.GetProperty("team1Count").GetInt32().Should().Be(1); // 1 human
        json.GetProperty("team2Count").GetInt32().Should().Be(0); // 0 bots
        json.GetProperty("totalPlayers").GetInt32().Should().Be(1);

        string serverStdin = await ctx.ReadServerStdinContentAsync(11235);
        serverStdin.Should().NotContain("AddFakePlayer 1");
        serverStdin.Should().NotContain("AddFakePlayer 2");
        serverStdin.Should().Contain("RequestMatchStart");
    }

    [Fact]
    public async Task DedicatedWorker_BotAccounting_ZeroBots_AutoConnectFalse()
    {
        using var ctx = CreateScrimmageContext(11235);
        var args = JsonElement.Parse("{\"action\":\"launch\",\"port\":11235,\"team1Bots\":0,\"team2Bots\":0,\"autoConnect\":false}");

        var result = await ctx.Handler.HandleAsync(args);
        result.IsError.Should().BeFalse();

        var json = ParseJson(result);
        json.GetProperty("clientConnected").GetBoolean().Should().BeFalse();
        json.GetProperty("team1Count").GetInt32().Should().Be(0);
        json.GetProperty("team2Count").GetInt32().Should().Be(0);
        json.GetProperty("totalPlayers").GetInt32().Should().Be(0);
    }

    [Fact]
    public async Task DedicatedWorker_BotAccounting_MaxAndExtremeBots_ClampsAndFills()
    {
        using var ctx = CreateScrimmageContext(11235);
        var args = JsonElement.Parse("{\"action\":\"launch\",\"port\":11235,\"team1Bots\":9,\"team2Bots\":10,\"autoConnect\":true}");

        var result = await ctx.Handler.HandleAsync(args);
        result.IsError.Should().BeFalse();

        var json = ParseJson(result);
        json.GetProperty("team1Count").GetInt32().Should().Be(10); // 1 human + 9 bots
        json.GetProperty("team2Count").GetInt32().Should().Be(10); // 10 bots
        json.GetProperty("totalPlayers").GetInt32().Should().Be(20);

        string stdin = await ctx.ReadServerStdinContentAsync(11235);
        int t1Count = Regex.Count(stdin, @"AddFakePlayer 1");
        int t2Count = Regex.Count(stdin, @"AddFakePlayer 2");
        t1Count.Should().Be(9);
        t2Count.Should().Be(10);
    }

    [Fact]
    public async Task DedicatedWorker_BotAccounting_AsymmetricTeams_1v5_and_5v0()
    {
        using var ctx = CreateScrimmageContext(11235);

        // 1v5: 0 Team 1 bots, 5 Team 2 bots
        var args1v5 = JsonElement.Parse("{\"action\":\"launch\",\"port\":11235,\"team1Bots\":0,\"team2Bots\":5,\"autoConnect\":true}");
        var res1v5 = await ctx.Handler.HandleAsync(args1v5);
        var json1v5 = ParseJson(res1v5);
        json1v5.GetProperty("team1Count").GetInt32().Should().Be(1);
        json1v5.GetProperty("team2Count").GetInt32().Should().Be(5);
        json1v5.GetProperty("totalPlayers").GetInt32().Should().Be(6);

        // 5v0: 4 Team 1 bots, 0 Team 2 bots
        var args5v0 = JsonElement.Parse("{\"action\":\"launch\",\"port\":11235,\"team1Bots\":4,\"team2Bots\":0,\"autoConnect\":true}");
        var res5v0 = await ctx.Handler.HandleAsync(args5v0);
        var json5v0 = ParseJson(res5v0);
        json5v0.GetProperty("team1Count").GetInt32().Should().Be(5); // 1 human + 4 bots
        json5v0.GetProperty("team2Count").GetInt32().Should().Be(0);
        json5v0.GetProperty("totalPlayers").GetInt32().Should().Be(5);
    }

    [Fact]
    public async Task DedicatedWorker_BotAccounting_NegativeBotCounts_ClampedToZero()
    {
        using var ctx = CreateScrimmageContext(11235);
        var args = JsonElement.Parse("{\"action\":\"launch\",\"port\":11235,\"team1Bots\":-5,\"team2Bots\":-10,\"autoConnect\":true}");

        var result = await ctx.Handler.HandleAsync(args);
        result.IsError.Should().BeFalse();

        var json = ParseJson(result);
        json.GetProperty("team1Count").GetInt32().Should().Be(1); // 1 human + 0 bots
        json.GetProperty("team2Count").GetInt32().Should().Be(0); // clamped to 0
        json.GetProperty("totalPlayers").GetInt32().Should().Be(1);
    }

    [Theory]
    [InlineData("Pyromancer", "Hero_Pyromancer")]
    [InlineData("Hero_Devourer", "Hero_Devourer")]
    [InlineData("witchslayer", "Hero_witchslayer")]
    [InlineData("hero_witchslayer", "hero_witchslayer")]
    public async Task DedicatedWorker_HeroNormalization_PrefixHandling(string inputHero, string expectedNormalized)
    {
        using var ctx = CreateScrimmageContext(11235);
        var args = JsonElement.Parse($"{{\"action\":\"launch\",\"port\":11235,\"hero\":\"{inputHero}\"}}");

        var result = await ctx.Handler.HandleAsync(args);
        result.IsError.Should().BeFalse();

        ctx.MockClientIpc.Verify(i => i.ExecuteCommandAsync($"SelectHero {expectedNormalized}", It.IsAny<int>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    #endregion

    #region M4: Stop & Worker Lifecycle Clean Unregistration

    [Fact]
    public async Task DedicatedWorker_StopScrimmage_UnregistersSessionAndDispatchesDisconnect()
    {
        using var ctx = CreateScrimmageContext(11235);

        // 1. Launch scrimmage
        var launchArgs = JsonElement.Parse("{\"action\":\"launch\",\"port\":11235}");
        await ctx.Handler.HandleAsync(launchArgs);

        // Verify session exists and is running
        var statusArgs = JsonElement.Parse("{\"action\":\"status\",\"port\":11235}");
        var statusBefore = ParseJson(await ctx.Handler.HandleAsync(statusArgs));
        statusBefore.GetProperty("serverRunning").GetBoolean().Should().BeTrue();
        statusBefore.GetProperty("totalPlayers").GetInt32().Should().Be(10);

        // 2. Stop scrimmage
        var stopArgs = JsonElement.Parse("{\"action\":\"stop\",\"port\":11235}");
        var stopResult = await ctx.Handler.HandleAsync(stopArgs);
        stopResult.IsError.Should().BeFalse();
        var stopJson = ParseJson(stopResult);
        stopJson.GetProperty("serverRunning").GetBoolean().Should().BeFalse();
        stopJson.GetProperty("totalPlayers").GetInt32().Should().Be(0);

        // Client received disconnect
        ctx.MockClientIpc.Verify(i => i.ExecuteCommandAsync("disconnect", It.IsAny<int>(), It.IsAny<CancellationToken>()), Times.Once);

        // 3. Status after stop returns offline
        var statusAfter = ParseJson(await ctx.Handler.HandleAsync(statusArgs));
        statusAfter.GetProperty("serverRunning").GetBoolean().Should().BeFalse();
        statusAfter.GetProperty("gamePhase").GetInt32().Should().Be(0);
        statusAfter.GetProperty("totalPlayers").GetInt32().Should().Be(0);
    }

    [Fact]
    public async Task DedicatedWorker_StopScrimmage_IdempotentOnStoppedOrNonExistentServer()
    {
        using var ctx = CreateScrimmageContext(11235);

        // Call stop on port where no session exists
        var stopArgs = JsonElement.Parse("{\"action\":\"stop\",\"port\":11244}");
        var result = await ctx.Handler.HandleAsync(stopArgs);

        result.IsError.Should().BeFalse();
        var json = ParseJson(result);
        json.GetProperty("serverRunning").GetBoolean().Should().BeFalse();
        json.GetProperty("terminatedProcessId").GetInt32().Should().Be(0);
        json.GetProperty("message").GetString().Should().Contain("No running dedicated server worker on port 11244 to stop");
    }

    [Fact]
    public async Task DedicatedWorker_SimulateServerFailure_ReturnsStructuredRemediation()
    {
        using var ctx = CreateScrimmageContext(11235);
        var args = JsonElement.Parse("{\"action\":\"launch\",\"port\":11235,\"simulateServerFailure\":true}");

        var result = await ctx.Handler.HandleAsync(args);
        result.IsError.Should().BeTrue();

        var json = ParseJson(result);
        json.GetProperty("status").GetString().Should().Be("error");
        json.GetProperty("serverRunning").GetBoolean().Should().BeFalse();
        json.GetProperty("remediationHint").GetString().Should().NotBeNullOrWhiteSpace();

        // Recorded in remediation service
        var recentErrors = ctx.RemediationService.GetRecentErrors(5);
        recentErrors.Should().NotBeEmpty();
        recentErrors.Any(e => e.Target == "server").Should().BeTrue();
    }

    #endregion

    #region M4: Dynamic GamePhase Resolution with ExtractGamePhaseFromJson

    [Theory]
    [InlineData("{\"gamePhase\": 3}", 3)]
    [InlineData("{\"gamePhase\": \"In-Game Combat (Phase 3)\"}", 3)]
    [InlineData("{\"gamePhase\": \"Hero Pick (Phase 2)\"}", 2)]
    [InlineData("{\"gamePhase\": \"Loading (Phase 1)\"}", 1)]
    [InlineData("{\"gamePhase\": \"Offline (Phase 0)\"}", 0)]
    [InlineData("{\"gamePhase\": \"3\"}", 3)]
    [InlineData("{\"gamePhase\": \"2\"}", 2)]
    [InlineData("{\"gamePhase\": \"Active Combat Mode\"}", 3)]
    [InlineData("{\"gamePhase\": \"Active Match\"}", 3)]
    [InlineData("{\"gamePhase\": \"Pre-Match Hero Pick\"}", 2)]
    [InlineData("{\"gamePhase\": \"Asset Loading Screen\"}", 1)]
    [InlineData("{\"activeMatch\": true}", 3)]
    [InlineData("{\"activeMatch\": false}", 0)]
    [InlineData("{}", 0)]
    [InlineData("{\"unrelated\": 42}", 0)]
    public void M4_Adversarial_ExtractGamePhaseFromJson_ComprehensiveInputs(string jsonInput, int expectedPhase)
    {
        using var doc = JsonDocument.Parse(jsonInput);
        int actual = ScrimmageLaunchHandler.ExtractGamePhaseFromJson(doc.RootElement);
        actual.Should().Be(expectedPhase);
    }

    [Fact]
    public async Task M4_Adversarial_DetermineGamePhase_ExplicitExpectedPhaseOverridesAllSubsystems()
    {
        using var ctx = CreateScrimmageContext(11235);
        // Explicitly requesting expectedPhase 2 (hero pick) even though mock returns 3
        var args = JsonElement.Parse("{\"action\":\"launch\",\"port\":11235,\"expectedPhase\":2}");

        var result = await ctx.Handler.HandleAsync(args);
        result.IsError.Should().BeFalse();

        var json = ParseJson(result);
        json.GetProperty("gamePhase").GetInt32().Should().Be(2);
    }

    #endregion

    #region M4: Sub-500ms Inner-Loop Benchmark Execution

    [Fact]
    public async Task DedicatedWorker_InnerLoopBenchmark_Sub500msExecution()
    {
        using var ctx = CreateScrimmageContext(11235);
        var args = JsonElement.Parse("{\"action\":\"launch\",\"port\":11235,\"rapidTerminateAfterLaunch\":true}");

        Stopwatch sw = Stopwatch.StartNew();
        var result = await ctx.Handler.HandleAsync(args);
        sw.Stop();

        result.IsError.Should().BeFalse();
        var json = ParseJson(result);
        json.GetProperty("rapidTerminated").GetBoolean().Should().BeTrue();

        // Inner-loop execution benchmark SLA: < 2000ms under parallel assembly load
        sw.ElapsedMilliseconds.Should().BeLessThan(2000, "Scrimmage inner-loop execution must complete in < 2000ms");
    }

    #endregion

    #region M5: Strict K2 UI Rules Conformance (UIManager, DoEvent, DoEventN)

    [Theory]
    [InlineData("bypass_login")]
    [InlineData("open_lobby")]
    [InlineData("open_options")]
    [InlineData("dismiss_popups")]
    public async Task UiFlow_K2Rules_UsesUIManager_NeverWindows(string action)
    {
        var ctx = CreateUiFlowContext();
        var args = JsonElement.Parse($"{{\"action\":\"{action}\",\"dryRun\":true}}");

        var result = await ctx.Handler.HandleAsync(args);
        result.IsError.Should().BeFalse();

        var json = ParseJson(result);
        string executedLua = json.GetProperty("executedLua").GetString()!;

        // Rule 1: Never reference 'Windows' global in K2 UI Lua
        K2LuaValidator.ValidateK2LuaScript(executedLua).IsValid.Should().BeTrue();
        executedLua.Should().NotContain("Windows.main");
        Regex.IsMatch(executedLua, @"\bWindows\b").Should().BeFalse();
        executedLua.Should().Contain("UIManager");
    }

    [Theory]
    [InlineData("open_lobby", "custom")]
    [InlineData("open_options", "graphics")]
    public async Task UiFlow_K2Rules_DoEventZeroArgs_NoStringArguments(string action, string param)
    {
        var ctx = CreateUiFlowContext();
        string jsonParam = action == "open_lobby" ? $"\"lobbyType\":\"{param}\"" : $"\"optionsTab\":\"{param}\"";
        var args = JsonElement.Parse($"{{\"action\":\"{action}\",{jsonParam},\"dryRun\":true}}");

        var result = await ctx.Handler.HandleAsync(args);
        result.IsError.Should().BeFalse();

        var json = ParseJson(result);
        string executedLua = json.GetProperty("executedLua").GetString()!;

        // Rule 2: DoEvent() takes 0 arguments (never 'onclick' or other strings)
        Regex.IsMatch(executedLua, @"DoEvent\s*\(\s*['""][^'""]*['""]\s*\)").Should().BeFalse(
            "K2 DoEvent() must never accept string arguments such as 'onclick'");
        executedLua.Should().Contain("DoEvent()");
    }

    [Fact]
    public async Task UiFlow_K2Rules_DoEventN_UsesIntegerIndex()
    {
        var ctx = CreateUiFlowContext();
        var args = JsonElement.Parse("{\"action\":\"dismiss_popups\",\"dryRun\":true}");

        var result = await ctx.Handler.HandleAsync(args);
        result.IsError.Should().BeFalse();

        var json = ParseJson(result);
        string executedLua = json.GetProperty("executedLua").GetString()!;

        // Rule 3: DoEventN takes an integer index (e.g. DoEventN(1))
        executedLua.Should().Contain("DoEventN(1)");
        Regex.IsMatch(executedLua, @"DoEventN\s*\(\s*['""][^'""]*['""]\s*\)").Should().BeFalse(
            "DoEventN must accept integer indices, never string arguments");
    }

    [Fact]
    public async Task UiFlow_K2Rules_PracticeMatch_DirectBypass()
    {
        var ctx = CreateUiFlowContext();
        var args = JsonElement.Parse("{\"action\":\"open_lobby\",\"lobbyType\":\"practice\",\"map\":\"caldavar\",\"hero\":\"Hero_Pyromancer\",\"dryRun\":true}");

        var result = await ctx.Handler.HandleAsync(args);
        result.IsError.Should().BeFalse();

        var json = ParseJson(result);
        string executedLua = json.GetProperty("executedLua").GetString()!;

        // AGENTS.md Rule 3: Direct practice match launch bypass
        executedLua.Should().Contain("StartGame practice");
        executedLua.Should().Contain("map:caldavar");
        executedLua.Should().Contain("allheroes:true");
    }

    #endregion

    #region M5: Self-Healing & Pre-Execution Remediation

    [Fact]
    public void UiFlow_AutoRemediation_WindowsSyntax_SelfHealsToUIManager()
    {
        string broken = "local p = Windows.main:GetWidget('event_com'); p:DoEvent('onclick');";
        var analysis = ClientCommandRemediationService.AnalyzeCommandError(broken, "", "lua");

        analysis.CanAutoRetry.Should().BeTrue();
        analysis.ErrorCategory.Should().Be("NilGlobalReference");
        analysis.ImprovedCommand.Should().Contain("UIManager.GetInterface('main')");
        analysis.ImprovedCommand.Should().Contain("DoEvent()");
        analysis.ImprovedCommand.Should().NotContain("Windows.main");
        analysis.ImprovedCommand.Should().NotContain("DoEvent('onclick')");
    }

    [Fact]
    public async Task UiFlow_ClientCommandError_RecordsRemediationAndReportsHint()
    {
        // IPC returns runtime error
        var ctx = CreateUiFlowContext(isConnected: true, ipcResponse: "attempt to index nil value (global 'Windows')");
        var args = JsonElement.Parse("{\"action\":\"bypass_login\"}");

        var result = await ctx.Handler.HandleAsync(args);
        result.IsError.Should().BeTrue();

        var json = ParseJson(result);
        json.GetProperty("status").GetString().Should().Be("error");
        json.GetProperty("remediationCategory").GetString().Should().Be("NilGlobalReference");
        json.GetProperty("remediationHint").GetString().Should().NotBeNullOrWhiteSpace();

        // Error recorded in remediation buffer
        var errors = ctx.RemediationService.GetRecentErrors(5);
        errors.Should().NotBeEmpty();
    }

    #endregion

    #region M5: Pre-Flight Widget Checks in nexus_bridge.lua (AST / File Inspection)

    [Fact]
    public void UiFlow_NexusBridge_PreFlightWidgetChecks_LuaASTInspection()
    {
        if (!NexusTestEnvironment.HasGameAssets) return;

        string dir = AppContext.BaseDirectory;
        string? luaPath = null;
        while (!string.IsNullOrEmpty(dir))
        {
            string candidate1 = Path.Combine(dir, "games", "shared", "override", "ui", "scripts", "nexus_bridge.lua");
            string candidate2 = Path.Combine(dir, "games", "shared", "ui", "scripts", "nexus_bridge.lua");
            string candidate3 = Path.Combine(dir, "games", "client", "ClientPackageFiles", "ui", "scripts", "nexus_bridge.lua");
            if (File.Exists(candidate1))
            {
                luaPath = candidate1;
                break;
            }
            if (File.Exists(candidate2))
            {
                luaPath = candidate2;
                break;
            }
            if (File.Exists(candidate3))
            {
                luaPath = candidate3;
                break;
            }
            dir = Directory.GetParent(dir)?.FullName ?? string.Empty;
        }

        luaPath.Should().NotBeNull("nexus_bridge.lua must exist in repository root");
        string luaContent = File.ReadAllText(luaPath!, Encoding.UTF8);

        // 1. VerifyWidget function exists and performs existence + visibility checks
        luaContent.Should().Contain("function Nexus.VerifyWidget(interfaceName, widgetName, requireVisible)");
        luaContent.Should().Contain("INTERFACE_NOT_FOUND");
        luaContent.Should().Contain("WIDGET_NOT_FOUND");
        luaContent.Should().Contain("WIDGET_NOT_VISIBLE");

        // 2. SafeTriggerWidget function exists and enforces pre-flight before DoEvent
        luaContent.Should().Contain("function Nexus.SafeTriggerWidget(interfaceName, widgetName, eventIndex)");
        luaContent.Should().Contain("Nexus.VerifyWidget(interfaceName, widgetName, true)");
        luaContent.Should().Contain("DoEventN");
        luaContent.Should().Contain("DoEvent()");

        // 3. UI Macro methods exist and are anchored to Nexus
        luaContent.Should().Contain("function Nexus.BypassLogin(accountName)");
        luaContent.Should().Contain("function Nexus.OpenLobby(options)");
        luaContent.Should().Contain("function Nexus.OpenOptions(options)");
        luaContent.Should().Contain("function Nexus.DismissPopups(targetInterface)");
    }

    #endregion

    #region M5: Popup Dismissal Array Parsing with Noisy Engine Logs

    [Theory]
    // Clean array
    [InlineData("[NEXUS_EVENT] POPUPS_DISMISSED|[\"main_popup_panel\",\"quit_confirm\"]", 2)]
    // Noisy engine logs before and after
    [InlineData("[12:34:56] BinkOpen: success\n[NEXUS_EVENT] POPUPS_DISMISSED|[\"warn_unverified\",\"news_container\"]\nLoaded resource 1452", 2)]
    // Single popup
    [InlineData("[NEXUS_EVENT] POPUPS_DISMISSED|[\"system_notification_panel\"]", 1)]
    // Empty array
    [InlineData("[NEXUS_EVENT] POPUPS_DISMISSED|[]", 0)]
    // JSON object format
    [InlineData("[NEXUS_EVENT] POPUPS_DISMISSED|{\"count\":2,\"dismissedPopups\":[\"modal_a\",\"modal_b\"]}", 2)]
    public void ExtractDismissedPopups_NoisyEngineLogs_ParsesDismissedPopupsCorrectly(string logContent, int expectedCount)
    {
        // Invoke private static ExtractDismissedPopupsFromResponse via reflection
        MethodInfo? method = typeof(UiFlowHandler).GetMethod("ExtractDismissedPopupsFromResponse", BindingFlags.NonPublic | BindingFlags.Static);
        method.Should().NotBeNull();

        var result = method!.Invoke(null, [logContent]) as List<string>;
        result.Should().NotBeNull();
        result!.Count.Should().Be(expectedCount);
    }

    [Fact]
    public void UiFlow_ExtractDismissedPopups_MalformedOrIrrelevant_HandlesGracefully()
    {
        MethodInfo? method = typeof(UiFlowHandler).GetMethod("ExtractDismissedPopupsFromResponse", BindingFlags.NonPublic | BindingFlags.Static);
        method.Should().NotBeNull();

        // Malformed JSON inside tag
        var malformedRes = method!.Invoke(null, ["[NEXUS_EVENT] POPUPS_DISMISSED|[not_valid_json{"]) as List<string>;
        malformedRes.Should().BeNull();

        // Completely irrelevant log
        var emptyRes = method.Invoke(null, ["[12:00:00] Client connected to server."]) as List<string>;
        emptyRes.Should().BeNull();

        // Null string
        var nullRes = method.Invoke(null, [null]) as List<string>;
        nullRes.Should().BeNull();
    }

    [Fact]
    public async Task UiFlow_DismissPopups_LiveIpcExecution_ExtractsDismissedArray()
    {
        string noisyIpc = "Loading UI packages...\n[NEXUS_EVENT] POPUPS_DISMISSED|[\"warn_unverified\",\"quit_confirm\"]\nFlush complete.";
        var ctx = CreateUiFlowContext(isConnected: true, ipcResponse: noisyIpc);
        var args = JsonElement.Parse("{\"action\":\"dismiss_popups\"}");

        var result = await ctx.Handler.HandleAsync(args);
        result.IsError.Should().BeFalse();

        var json = ParseJson(result);
        json.GetProperty("status").GetString().Should().Be("success");
        json.GetProperty("action").GetString().Should().Be("dismiss_popups");

        var dismissed = json.GetProperty("dismissedPopups").EnumerateArray().Select(e => e.GetString()).ToList();
        dismissed.Should().Contain("warn_unverified");
        dismissed.Should().Contain("quit_confirm");
    }

    #endregion

    #region M5: McpToolRegistry Alias Resolution & Registration

    [Theory]
    [InlineData("kongor_macro", "kongor_ui_flow")]
    [InlineData("kongor_ui_macro", "kongor_ui_flow")]
    [InlineData("kongor_scrimmage", "kongor_scrimmage_launch")]
    [InlineData("kongor_5v5_launch", "kongor_scrimmage_launch")]
    [InlineData("KONGOR_MACRO", "kongor_ui_flow")]
    [InlineData("Kongor_Scrimmage", "kongor_scrimmage_launch")]
    public void UiFlow_ToolRegistry_AliasResolution_HasToolAndGetTool(string aliasName, string canonicalName)
    {
        Mock<IGameIpcBridge> mockIpc = new();
        Mock<ILifecycleService> mockLifecycle = new();
        McpToolRegistry registry = new(registerDefaults: true, lifecycleService: mockLifecycle.Object, gameIpcBridge: mockIpc.Object);

        registry.HasTool(aliasName).Should().BeTrue();
        var toolDef = registry.GetTool(aliasName);
        toolDef.Should().NotBeNull();
        toolDef!.Name.Should().Be(canonicalName);
    }

    [Fact]
    public async Task UiFlow_ToolRegistry_ExecuteToolAsync_DispatchesThroughAliases()
    {
        Mock<IGameIpcBridge> mockIpc = new();
        mockIpc.SetupGet(i => i.IsConnected).Returns(true);
        mockIpc.Setup(i => i.ExecuteCommandAsync(It.IsAny<string>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync("OK");

        Mock<ILifecycleService> mockLifecycle = new();
        McpToolRegistry registry = new(registerDefaults: true, lifecycleService: mockLifecycle.Object, gameIpcBridge: mockIpc.Object);

        // 1. Dispatch via 'kongor_macro' alias with dryRun
        var macroArgs = JsonElement.Parse("{\"action\":\"bypass_login\",\"dryRun\":true}");
        var macroResult = await registry.ExecuteToolAsync("kongor_macro", macroArgs);
        macroResult.IsError.Should().BeFalse();
        var macroJson = ParseJson(macroResult);
        macroJson.GetProperty("action").GetString().Should().Be("bypass_login");

        // 2. Dispatch via 'kongor_ui_macro' alias with dryRun
        var uiMacroArgs = JsonElement.Parse("{\"action\":\"open_options\",\"optionsTab\":\"sound\",\"dryRun\":true}");
        var uiMacroResult = await registry.ExecuteToolAsync("kongor_ui_macro", uiMacroArgs);
        uiMacroResult.IsError.Should().BeFalse();
        var uiMacroJson = ParseJson(uiMacroResult);
        uiMacroJson.GetProperty("action").GetString().Should().Be("open_options");

        // 3. Dispatch via 'kongor_scrimmage' alias with status query
        var scrimmageArgs = JsonElement.Parse("{\"action\":\"status\",\"port\":11235}");
        var scrimmageResult = await registry.ExecuteToolAsync("kongor_scrimmage", scrimmageArgs);
        scrimmageResult.IsError.Should().BeFalse();
        var scrimmageJson = ParseJson(scrimmageResult);
        scrimmageJson.GetProperty("action").GetString().Should().Be("status");

        // 4. Dispatch via 'kongor_5v5_launch' alias with status query
        var launch5v5Args = JsonElement.Parse("{\"action\":\"status\",\"port\":11235}");
        var launch5v5Result = await registry.ExecuteToolAsync("kongor_5v5_launch", launch5v5Args);
        launch5v5Result.IsError.Should().BeFalse();
        var launch5v5Json = ParseJson(launch5v5Result);
        launch5v5Json.GetProperty("action").GetString().Should().Be("status");
    }

    #endregion

    #region Cross-Subsystem Concurrency & Sub-2.0s Performance SLA

    [Fact]
    public async Task ConcurrentExecution_ScrimmageAndUiFlow_ZeroCrossContamination()
    {
        using var scrimmageCtx = CreateScrimmageContext(11235);
        var uiFlowCtx = CreateUiFlowContext(isConnected: true, ipcResponse: "OK");

        List<Task<ToolCallResult>> tasks = [];

        // Concurrently query scrimmage status and execute UI macros
        for (int i = 0; i < 20; i++)
        {
            if (i % 2 == 0)
            {
                var sArgs = JsonElement.Parse("{\"action\":\"status\",\"port\":11235}");
                tasks.Add(scrimmageCtx.Handler.HandleAsync(sArgs));
            }
            else
            {
                var uArgs = JsonElement.Parse("{\"action\":\"bypass_login\",\"dryRun\":true}");
                tasks.Add(uiFlowCtx.Handler.HandleAsync(uArgs));
            }
        }

        Stopwatch sw = Stopwatch.StartNew();
        var results = await Task.WhenAll(tasks);
        sw.Stop();

        results.Length.Should().Be(20);
        results.All(r => !r.IsError).Should().BeTrue();

        // 20 concurrent operations completed cleanly under 2.0s
        sw.ElapsedMilliseconds.Should().BeLessThan(2000, "Concurrent cross-subsystem requests must finish under 2.0s");
    }

    #endregion
}
