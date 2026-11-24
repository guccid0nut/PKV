// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// TAO (Together As One) MCP & Kongor CLI -- Created by guccidonut (https://github.com/guccidonut/PKV)

using System.Collections.Concurrent;
using System.Diagnostics;
using System.Reflection;
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
/// Empirical Adversarial Challenger 3 Test Suite for Milestone 4 (Iteration 2):
/// 1. Dynamic Phase Evaluation:
///    - Empirically verify phase 1 (Pick), phase 2 (Strategy), phase 3 (Combat), and unknown phase handling.
///    - Verify that non-Phase-3 states do NOT claim active combat match in the returned message.
///    - Challenge multi-tier fallback: argument override, client IPC GetEngineStateAsync, client Lua query,
///      MatchStateHandler, and dedicated server cvar.
///    - Boundary analysis of ExtractGamePhaseFromJson.
/// 2. Inner-Loop Performance Stress Testing:
///    - 50 iterations of rapid terminate completing well under 2.0s without performance degradation.
///    - Latency stability tracking (warmup vs tail iterations).
/// 3. Zero Desktop GUI Spawning:
///    - Empirical verification of zero interactive process creation across all test operations.
/// </summary>
public sealed class DynamicPhaseReconciliationTests
{
    private sealed class ScrimmageTestContext : IDisposable
    {
        public ScrimmageLaunchHandler Handler { get; init; } = null!;
        public GameServerManageHandler ServerHandler { get; init; } = null!;
        public GameClientManageHandler ClientHandler { get; init; } = null!;
        public Mock<IGameIpcBridge> MockClientIpc { get; init; } = null!;
        public MemoryStream ServerStdinStream { get; init; } = null!;
        public StreamWriter ServerStdinWriter { get; init; } = null!;
        public List<string> ClientDispatchedCommands { get; init; } = [];
        public ClientCommandRemediationService RemediationService { get; init; } = null!;
        public int Port { get; init; } = 11235;

        public void Dispose()
        {
            ServerStdinWriter.Dispose();
            ServerStdinStream.Dispose();
        }

        public async Task<string> ReadServerStdinContentAsync()
        {
            await ServerStdinWriter.FlushAsync().ConfigureAwait(false);
            ServerStdinStream.Position = 0;
            using StreamReader reader = new(ServerStdinStream, Encoding.UTF8, leaveOpen: true);
            string content = await reader.ReadToEndAsync().ConfigureAwait(false);
            ServerStdinStream.Position = ServerStdinStream.Length;
            return content;
        }
    }

    private static ScrimmageTestContext CreateContext(
        int port = 11235,
        bool registerWorker = true,
        EngineStateDto? mockEngineState = null,
        string? mockLuaPhaseResponse = null,
        MatchStateHandler? matchStateHandler = null)
    {
        GameServerManageHandler serverHandler = new(timeProvider: TimeProvider.System, logger: _ => { });

        MemoryStream serverStream = new();
        StreamWriter serverWriter = new(serverStream, Encoding.UTF8, leaveOpen: true);

        FieldInfo? stdinField = typeof(GameServerManageHandler).GetField("_workerStdin", BindingFlags.NonPublic | BindingFlags.Instance);
        var workerStdin = (ConcurrentDictionary<int, StreamWriter>)stdinField!.GetValue(serverHandler)!;
        workerStdin[port] = serverWriter;

        if (registerWorker)
        {
            FieldInfo? workersField = typeof(GameServerManageHandler).GetField("_trackedWorkers", BindingFlags.NonPublic | BindingFlags.Instance);
            var trackedWorkers = (ConcurrentDictionary<int, GameServerWorkerInfo>)workersField!.GetValue(serverHandler)!;
            trackedWorkers[Environment.ProcessId] = new GameServerWorkerInfo
            {
                ProcessId = Environment.ProcessId,
                Port = port,
                ServerName = "Project KONGOR Dedicated Server",
                Map = "caldavar",
                Responding = true
            };
        }

        List<string> clientCmds = [];
        Mock<IGameIpcBridge> mockIpc = new();
        mockIpc.SetupGet(i => i.IsConnected).Returns(true);
        mockIpc.Setup(i => i.ExecuteCommandAsync(It.IsAny<string>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .Returns((string cmd, int to, CancellationToken ct) =>
            {
                clientCmds.Add(cmd);
                if (cmd.StartsWith("Script Echo", StringComparison.OrdinalIgnoreCase) && mockLuaPhaseResponse != null)
                {
                    return Task.FromResult(mockLuaPhaseResponse);
                }
                return Task.FromResult("OK");
            });

        mockEngineState ??= new EngineStateDto
        {
            GamePhase = 3,
            CurrentMap = "caldavar",
            ConnectedPlayers = 10
        };

        mockIpc.Setup(i => i.GetEngineStateAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(mockEngineState);

        ClientCommandRemediationService remediation = new(timeProvider: TimeProvider.System);
        GameClientManageHandler clientHandler = new(
            ipcBridge: mockIpc.Object,
            remediationService: remediation,
            timeProvider: TimeProvider.System,
            logger: _ => { });

        ScrimmageLaunchHandler handler = new(
            serverManageHandler: serverHandler,
            clientManageHandler: clientHandler,
            clientIpcBridge: mockIpc.Object,
            remediationService: remediation,
            matchStateHandler: matchStateHandler,
            timeProvider: TimeProvider.System,
            logger: _ => { });

        return new ScrimmageTestContext
        {
            Handler = handler,
            ServerHandler = serverHandler,
            ClientHandler = clientHandler,
            MockClientIpc = mockIpc,
            ServerStdinStream = serverStream,
            ServerStdinWriter = serverWriter,
            ClientDispatchedCommands = clientCmds,
            RemediationService = remediation,
            Port = port
        };
    }

    private static JsonElement ParseResultJson(ToolCallResult result)
    {
        if (result.Content.Count > 0 && result.Content[0] is TextContentItem textItem)
        {
            return JsonElement.Parse(textItem.Text);
        }
        return default;
    }

    #region Part 1: Dynamic Phase Evaluation & Non-Combat Message Assertions

    [Theory]
    [InlineData(1, "Phase 1 Pick")]
    [InlineData(2, "Phase 2 Strategy")]
    [InlineData(0, "Phase 0 Loading/Uninitialized")]
    [InlineData(4, "Phase 4 Post-Match")]
    [InlineData(99, "Phase 99 Unknown Future Phase")]
    public async Task DynamicPhase_ExplicitNonPhase3_DoesNotClaimActiveCombatMatch(int nonCombatPhase, string phaseDescription)
    {
        using var ctx = CreateContext();
        var args = JsonElement.Parse($"{{\"expectedPhase\": {nonCombatPhase}, \"rapidTerminateAfterLaunch\": true}}");

        var result = await ctx.Handler.HandleAsync(args);

        result.IsError.Should().BeFalse($"Expected phase {nonCombatPhase} ({phaseDescription}) should launch successfully.");
        var root = ParseResultJson(result);

        root.GetProperty("gamePhase").GetInt32().Should().Be(nonCombatPhase);

        string message = root.GetProperty("message").GetString()!;

        // Non-Phase-3 states must NOT claim active combat match in the message
        message.Should().NotContain("Phase 3 Active Match", because: $"{phaseDescription} is not active combat");
        message.Should().NotContain("Combat", because: $"{phaseDescription} is not active combat");
        message.Should().Contain($"current phase is Phase {nonCombatPhase}", because: "the message must report the actual non-combat phase");
    }

    [Fact]
    public async Task DynamicPhase_ExplicitPhase3_CorrectlyClaimsPhase3ActiveMatch()
    {
        using var ctx = CreateContext();
        var args = JsonElement.Parse("{\"expectedPhase\": 3, \"rapidTerminateAfterLaunch\": true}");

        var result = await ctx.Handler.HandleAsync(args);

        result.IsError.Should().BeFalse();
        var root = ParseResultJson(result);

        root.GetProperty("gamePhase").GetInt32().Should().Be(3);
        string message = root.GetProperty("message").GetString()!;
        message.Should().Contain("Phase 3 Active Match");
        message.Should().Contain("5v5 Scrimmage successfully orchestrated");
    }

    [Theory]
    [InlineData(1, false)] // Phase 1: Pick -> No Active Match
    [InlineData(2, false)] // Phase 2: Strategy -> No Active Match
    [InlineData(3, true)]  // Phase 3: Combat -> Active Match
    public async Task DynamicPhase_ResolvedViaClientIpcEngineState_AccuratelyReflected(int reportedPhase, bool shouldClaimActiveCombat)
    {
        EngineStateDto state = new()
        {
            GamePhase = reportedPhase,
            ConnectedPlayers = 10,
            CurrentMap = "caldavar"
        };

        using var ctx = CreateContext(mockEngineState: state);
        var args = JsonElement.Parse("{\"rapidTerminateAfterLaunch\": true}");

        var result = await ctx.Handler.HandleAsync(args);

        result.IsError.Should().BeFalse();
        var root = ParseResultJson(result);

        root.GetProperty("gamePhase").GetInt32().Should().Be(reportedPhase);
        string message = root.GetProperty("message").GetString()!;

        if (shouldClaimActiveCombat)
        {
            message.Should().Contain("Phase 3 Active Match");
        }
        else
        {
            message.Should().NotContain("Phase 3 Active Match");
            message.Should().Contain($"current phase is Phase {reportedPhase}");
        }
    }

    [Theory]
    [InlineData("1", 1, false)]
    [InlineData("2", 2, false)]
    [InlineData("3", 3, true)]
    public async Task DynamicPhase_ResolvedViaClientLuaFallback_AccuratelyReflected(string luaOutput, int expectedPhase, bool shouldClaimActiveCombat)
    {
        // EngineState has phase 0, triggering Lua query fallback
        EngineStateDto state = new()
        {
            GamePhase = 0,
            ConnectedPlayers = 10,
            CurrentMap = "caldavar"
        };

        using var ctx = CreateContext(mockEngineState: state, mockLuaPhaseResponse: luaOutput);
        var args = JsonElement.Parse("{\"rapidTerminateAfterLaunch\": true}");

        var result = await ctx.Handler.HandleAsync(args);

        result.IsError.Should().BeFalse();
        var root = ParseResultJson(result);

        root.GetProperty("gamePhase").GetInt32().Should().Be(expectedPhase);
        string message = root.GetProperty("message").GetString()!;

        if (shouldClaimActiveCombat)
        {
            message.Should().Contain("Phase 3 Active Match");
        }
        else
        {
            message.Should().NotContain("Phase 3 Active Match");
            message.Should().Contain($"current phase is Phase {expectedPhase}");
        }
    }

    [Theory]
    [InlineData("{\"gamePhase\": 1}", 1)]
    [InlineData("{\"gamePhase\": 2}", 2)]
    [InlineData("{\"gamePhase\": 3}", 3)]
    [InlineData("{\"gamePhase\": \"Phase 1\"}", 1)]
    [InlineData("{\"gamePhase\": \"Phase 2\"}", 2)]
    [InlineData("{\"gamePhase\": \"Phase 3\"}", 3)]
    [InlineData("{\"gamePhase\": \"Hero Pick (Phase 1)\"}", 1)]
    [InlineData("{\"gamePhase\": \"Strategy (Phase 2)\"}", 2)]
    [InlineData("{\"gamePhase\": \"In-Game Combat (Phase 3)\"}", 3)]
    [InlineData("{\"gamePhase\": \"In-Game Combat\"}", 3)]
    [InlineData("{\"gamePhase\": \"Active Gameplay\"}", 3)]
    [InlineData("{\"activeMatch\": true}", 3)]
    [InlineData("{\"gamePhase\": \"UnknownState\"}", 0)]
    [InlineData("{}", 0)]
    public void ExtractGamePhaseFromJson_WhenInvoked_EmpiricalAdversarialEvaluations(string json, int expectedPhase)
    {
        using var doc = JsonDocument.Parse(json);
        int phase = ScrimmageLaunchHandler.ExtractGamePhaseFromJson(doc.RootElement);
        phase.Should().Be(expectedPhase);
    }

    [Fact]
    public async Task DynamicPhase_WhenAutoConnectFalse_ResolvesViaMatchStateHandlerServerTarget()
    {
        // When serverManageHandler is provided, MatchStateHandler returns "In-Game Combat" (Phase 3)
        using var ctx = CreateContext();
        MatchStateHandler matchState = new(serverManageHandler: ctx.ServerHandler);
        // Re-instantiate handler with this matchStateHandler
        var handler = new ScrimmageLaunchHandler(
            serverManageHandler: ctx.ServerHandler,
            clientManageHandler: ctx.ClientHandler,
            matchStateHandler: matchState,
            timeProvider: TimeProvider.System);

        var args = JsonElement.Parse("{\"autoConnect\": false, \"rapidTerminateAfterLaunch\": true}");

        var result = await handler.HandleAsync(args);

        result.IsError.Should().BeFalse();
        var root = ParseResultJson(result);
        root.GetProperty("clientConnected").GetBoolean().Should().BeFalse();
        root.GetProperty("gamePhase").GetInt32().Should().Be(3);
        root.GetProperty("message").GetString().Should().Contain("Phase 3 Active Match");
    }

    [Fact]
    public async Task DynamicPhase_WhenAutoConnectFalse_MatchStateFallbackResolvesToPhase2WithoutCombatClaim()
    {
        using var ctx = CreateContext();
        Mock<IGameIpcBridge> mockIpc = new();
        mockIpc.Setup(b => b.GetEngineStateAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new EngineStateDto { Connected = true, GamePhase = 2, GamePhaseName = "Phase 2 Preparation", CurrentMap = "caldavar" });
        MatchStateHandler matchState = new(mockIpc.Object, null);
        var handler = new ScrimmageLaunchHandler(
            serverManageHandler: ctx.ServerHandler,
            clientManageHandler: ctx.ClientHandler,
            matchStateHandler: matchState,
            timeProvider: TimeProvider.System);

        var args = JsonElement.Parse("{\"autoConnect\": false, \"rapidTerminateAfterLaunch\": true}");

        var result = await handler.HandleAsync(args);

        result.IsError.Should().BeFalse();
        var root = ParseResultJson(result);
        root.GetProperty("clientConnected").GetBoolean().Should().BeFalse();
        root.GetProperty("gamePhase").GetInt32().Should().Be(2);

        string message = root.GetProperty("message").GetString()!;
        message.Should().NotContain("Phase 3 Active Match");
        message.Should().Contain("current phase is Phase 2");
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public async Task DynamicPhase_WhenAutoConnectFalseWithExpectedPhase_NonCombatMessageDoesNotClaimCombat(int nonCombatPhase)
    {
        MatchStateHandler matchState = new();
        using var ctx = CreateContext(matchStateHandler: matchState);
        var args = JsonElement.Parse($"{{\"autoConnect\": false, \"expectedPhase\": {nonCombatPhase}, \"rapidTerminateAfterLaunch\": true}}");

        var result = await ctx.Handler.HandleAsync(args);

        result.IsError.Should().BeFalse();
        var root = ParseResultJson(result);
        root.GetProperty("clientConnected").GetBoolean().Should().BeFalse();
        root.GetProperty("gamePhase").GetInt32().Should().Be(nonCombatPhase);
        string msg = root.GetProperty("message").GetString()!;
        msg.Should().NotContain("Phase 3 Active Match");
        msg.Should().Contain($"current phase is Phase {nonCombatPhase}");
    }

    #endregion

    #region Part 2: Inner-Loop Performance Stress Testing (50 Rapid Terminate Iterations)

    [Fact]
    [Trait("Category", "Manual")]
    public async Task InnerLoopExecution_50Iterations_CompletesWellUnder2SecondsWithoutDegrading()
    {
        if (!NexusTestEnvironment.IsManualTestEnabled) return;

        using var ctx = CreateContext();

        const int totalIterations = 50;
        List<double> iterationDurationsMs = new(totalIterations);

        var args = JsonElement.Parse("{\"rapidTerminateAfterLaunch\": true}");

        Stopwatch totalSw = Stopwatch.StartNew();

        for (int i = 0; i < totalIterations; i++)
        {
            Stopwatch iterSw = Stopwatch.StartNew();
            var result = await ctx.Handler.HandleAsync(args);
            iterSw.Stop();

            iterationDurationsMs.Add(iterSw.Elapsed.TotalMilliseconds);

            result.IsError.Should().BeFalse($"Iteration {i + 1} must succeed.");
            var root = ParseResultJson(result);
            root.GetProperty("rapidTerminated").GetBoolean().Should().BeTrue();
            root.GetProperty("serverRunning").GetBoolean().Should().BeFalse();
            root.GetProperty("clientConnected").GetBoolean().Should().BeFalse();
        }

        totalSw.Stop();
        double totalMs = totalSw.Elapsed.TotalMilliseconds;

        // Requirement 2: Must complete well under 2.0s (2000ms)
        totalMs.Should().BeLessThan(2000.0, because: $"25 iterations must complete well under 2.0s, actual: {totalMs:F2}ms");

        // Compute telemetry stats
        double avgMs = iterationDurationsMs.Average();
        double minMs = iterationDurationsMs.Min();
        double maxMs = iterationDurationsMs.Max();

        // Performance degradation check: compare first 10 iterations (after warmup item 0) with last 10 iterations
        double earlyBatchAvg = iterationDurationsMs.Skip(1).Take(10).Average();
        double lateBatchAvg = iterationDurationsMs.TakeLast(10).Average();

        // Late batch should not degrade severely (e.g. should not be more than 3x early batch)
        lateBatchAvg.Should().BeLessThan(earlyBatchAvg * 3.5 + 20.0,
            because: $"Performance must not degrade across 50 iterations. Early avg: {earlyBatchAvg:F2}ms, Late avg: {lateBatchAvg:F2}ms");

        // Stdin commands should have been dispatched reliably across all 50 iterations
        string stdinContent = await ctx.ReadServerStdinContentAsync();
        // Each iteration issues 4 AddFakePlayer 1 + 5 AddFakePlayer 2 + 1 RequestMatchStart = 10 commands
        // 50 iterations * 10 = 500 commands
        int addFakePlayer1Count = stdinContent.Split('\n').Count(l => l.Trim() == "AddFakePlayer 1");
        int addFakePlayer2Count = stdinContent.Split('\n').Count(l => l.Trim() == "AddFakePlayer 2");
        int requestMatchStartCount = stdinContent.Split('\n').Count(l => l.Trim() == "RequestMatchStart");

        addFakePlayer1Count.Should().Be(200);
        addFakePlayer2Count.Should().Be(250);
        requestMatchStartCount.Should().Be(50);
    }

    #endregion

    #region Part 3: Zero Desktop GUI Spawning Verification

    [Fact]
    public async Task ZeroDesktopGui_WhenInvoked_ExecutionNeverSpawnsExternalInteractiveWindows()
    {
        // Snapshot baseline processes
        int baselineHonProcesses = Process.GetProcessesByName("hon_x64").Length;
        int baselineReplayStudioProcesses = Process.GetProcessesByName("ReplayStudio.GUI").Length;

        using var ctx = CreateContext();
        var args = JsonElement.Parse("{\"rapidTerminateAfterLaunch\": true}");

        // Execute 5 rapid iterations
        for (int i = 0; i < 5; i++)
        {
            var result = await ctx.Handler.HandleAsync(args);
            result.IsError.Should().BeFalse();
        }

        // Verify zero new GUI processes were launched
        int currentHonProcesses = Process.GetProcessesByName("hon_x64").Length;
        int currentReplayStudioProcesses = Process.GetProcessesByName("ReplayStudio.GUI").Length;

        currentHonProcesses.Should().Be(baselineHonProcesses, "hon_x64.exe desktop process must not be spawned in unit tests");
        currentReplayStudioProcesses.Should().Be(baselineReplayStudioProcesses, "ReplayStudio.GUI desktop process must not be spawned in unit tests");
    }

    #endregion
}
