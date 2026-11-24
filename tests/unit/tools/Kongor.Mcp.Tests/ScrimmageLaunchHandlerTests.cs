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

public sealed class ScrimmageLaunchHandlerTests
{
    private sealed class ScrimmageTestContext : IDisposable
    {
        public ScrimmageLaunchHandler Handler { get; init; } = null!;
        public GameServerManageHandler ServerHandler { get; init; } = null!;
        public GameClientManageHandler ClientHandler { get; init; } = null!;
        public MatchStateHandler MatchStateHandler { get; init; } = null!;
        public Mock<IGameIpcBridge> MockClientIpc { get; init; } = null!;
        public MemoryStream ServerStdinStream { get; init; } = null!;
        public StreamWriter ServerStdinWriter { get; init; } = null!;
        public List<string> ClientDispatchedCommands { get; init; } = [];
        public List<string> CapturedLogs { get; init; } = [];
        public ConcurrentDictionary<int, StreamWriter> WorkerStdinMap { get; init; } = null!;
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

    private static ScrimmageTestContext CreateContext(int port = 11235, bool registerExistingWorker = true)
    {
        List<string> logs = [];
        Action<string> logger = msg => logs.Add(msg);

        GameServerManageHandler serverHandler = new(timeProvider: TimeProvider.System, logger: logger);

        MemoryStream serverStream = new();
        StreamWriter serverWriter = new(serverStream, Encoding.UTF8, leaveOpen: true);

        FieldInfo? stdinField = typeof(GameServerManageHandler).GetField("_workerStdin", BindingFlags.NonPublic | BindingFlags.Instance);
        var workerStdin = (ConcurrentDictionary<int, StreamWriter>)stdinField!.GetValue(serverHandler)!;
        workerStdin[port] = serverWriter;

        if (registerExistingWorker)
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

        List<string> clientCommands = [];
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
            .Returns((string cmd, int to, CancellationToken ct) =>
            {
                clientCommands.Add(cmd);
                if (cmd.Contains("GetCurrentPhase")) return Task.FromResult("3");
                return Task.FromResult("OK");
            });

        ClientCommandRemediationService remediation = new(timeProvider: TimeProvider.System);
        GameClientManageHandler clientHandler = new(
            ipcBridge: mockIpc.Object,
            remediationService: remediation,
            timeProvider: TimeProvider.System,
            logger: logger);

        MatchStateHandler matchStateHandler = new(
            gameIpcBridge: mockIpc.Object,
            serverManageHandler: serverHandler,
            timeProvider: TimeProvider.System,
            logger: logger);

        ScrimmageLaunchHandler handler = new(
            serverManageHandler: serverHandler,
            clientManageHandler: clientHandler,
            matchStateHandler: matchStateHandler,
            clientIpcBridge: mockIpc.Object,
            remediationService: remediation,
            timeProvider: TimeProvider.System,
            logger: logger);

        return new ScrimmageTestContext
        {
            Handler = handler,
            ServerHandler = serverHandler,
            ClientHandler = clientHandler,
            MatchStateHandler = matchStateHandler,
            MockClientIpc = mockIpc,
            ServerStdinStream = serverStream,
            ServerStdinWriter = serverWriter,
            ClientDispatchedCommands = clientCommands,
            CapturedLogs = logs,
            WorkerStdinMap = workerStdin,
            RemediationService = remediation,
            Port = port
        };
    }

    private static string GetContentText(ToolCallResult result)
    {
        if (result.Content.Count > 0 && result.Content[0] is TextContentItem textItem)
        {
            return textItem.Text;
        }
        return string.Empty;
    }

    private static JsonElement ParseResultJson(ToolCallResult result)
    {
        string text = GetContentText(result);
        return JsonElement.Parse(text);
    }

    private static int CountOccurrences(string source, string target)
    {
        if (string.IsNullOrEmpty(source) || string.IsNullOrEmpty(target)) return 0;
        int count = 0, index = 0;
        while ((index = source.IndexOf(target, index, StringComparison.Ordinal)) != -1)
        {
            count++;
            index += target.Length;
        }
        return count;
    }

    #region Category 1: Port Routing & Isolation (6 Tests)

    [Fact]
    public async Task Launch_DefaultPort_EnforcesPort11235()
    {
        using var ctx = CreateContext();
        var args = JsonElement.Parse("{}");

        var result = await ctx.Handler.HandleAsync(args);

        result.IsError.Should().BeFalse();
        var root = ParseResultJson(result);
        root.GetProperty("port").GetInt32().Should().Be(11235);
        root.GetProperty("serverRunning").GetBoolean().Should().BeTrue();

        string stdin = await ctx.ReadServerStdinContentAsync();
        stdin.Should().Contain("AddFakePlayer 1");
        stdin.Should().Contain("AddFakePlayer 2");
        stdin.Should().Contain("RequestMatchStart");
    }

    [Fact]
    public async Task Launch_ExplicitCustomPort_RoutesCommandsToExplicitPort()
    {
        using var ctx = CreateContext(port: 11240);
        var args = JsonElement.Parse("{\"port\": 11240}");

        var result = await ctx.Handler.HandleAsync(args);

        result.IsError.Should().BeFalse();
        var root = ParseResultJson(result);
        root.GetProperty("port").GetInt32().Should().Be(11240);

        string stdin = await ctx.ReadServerStdinContentAsync();
        stdin.Should().Contain("AddFakePlayer 1");
        stdin.Should().Contain("AddFakePlayer 2");
    }

    [Fact]
    public async Task Launch_InvalidPortZero_ReturnsValidationError()
    {
        using var ctx = CreateContext();
        var args = JsonElement.Parse("{\"port\": 0}");

        var result = await ctx.Handler.HandleAsync(args);

        result.IsError.Should().BeTrue();
        string err = GetContentText(result);
        err.Should().Contain("between 1 and 65535");
    }

    [Fact]
    public async Task Launch_InvalidPortNegative_ReturnsValidationError()
    {
        using var ctx = CreateContext();
        var args = JsonElement.Parse("{\"port\": -5}");

        var result = await ctx.Handler.HandleAsync(args);

        result.IsError.Should().BeTrue();
        string err = GetContentText(result);
        err.Should().Contain("between 1 and 65535");
    }

    [Fact]
    public async Task Launch_PortExceedsMax65535_ReturnsValidationError()
    {
        using var ctx = CreateContext();
        var args = JsonElement.Parse("{\"port\": 70000}");

        var result = await ctx.Handler.HandleAsync(args);

        result.IsError.Should().BeTrue();
        string err = GetContentText(result);
        err.Should().Contain("between 1 and 65535");
    }

    [Fact]
    public async Task DedicatedServerCommands_WhenInvoked_NeverRouteToGenericUnnamedPipe()
    {
        using var ctx = CreateContext(port: 11235);

        // Setup canary stream for port 0 to trap any unkeyed / generic command dispatch
        using MemoryStream canaryStream = new();
        using StreamWriter canaryWriter = new(canaryStream, new UTF8Encoding(false), leaveOpen: true);
        ctx.WorkerStdinMap[0] = canaryWriter;

        var args = JsonElement.Parse("{\"port\": 11235}");

        var result = await ctx.Handler.HandleAsync(args);

        result.IsError.Should().BeFalse();
        var root = ParseResultJson(result);
        root.GetProperty("port").GetInt32().Should().Be(11235);
        root.GetProperty("serverRunning").GetBoolean().Should().BeTrue();

        // 1. Primary Channel Verification: Verify dedicated server commands were captured in _workerStdinMap[11235]
        string stdinContent = await ctx.ReadServerStdinContentAsync();
        stdinContent.Should().NotBeNullOrWhiteSpace();

        // Verify exact command counts and targeting
        CountOccurrences(stdinContent, "AddFakePlayer 1").Should().Be(4);
        CountOccurrences(stdinContent, "AddFakePlayer 2").Should().Be(5);
        CountOccurrences(stdinContent, "RequestMatchStart").Should().Be(1);

        string[] stdinLines = stdinContent.Split(["\r\n", "\n"], StringSplitOptions.RemoveEmptyEntries);
        stdinLines.Should().HaveCount(10);
        stdinLines.Should().OnlyContain(cmd => cmd.StartsWith("AddFakePlayer") || cmd == "RequestMatchStart");

        // 2. Canary / Port 0 Negative Verification: Verify zero commands routed to port 0
        await canaryWriter.FlushAsync();
        canaryStream.Length.Should().Be(0, "Dedicated server commands must never route to unkeyed / port 0 stdin stream");

        // 3. Client IPC Isolation: Verify dedicated server commands never leaked to client IPC (willowmaker-ipc)
        ctx.ClientDispatchedCommands.Should().NotContain(
            cmd => cmd.Contains("AddFakePlayer") || cmd.Contains("RequestMatchStart"),
            "Dedicated server commands must never route through generic client IPC bridge");

        // 4. Secondary Channel & Logger Verification: Verify all dedicated server dispatches target port 11235
        var serverLogs = ctx.CapturedLogs.Where(l => l.Contains("DedicatedServer") || l.Contains("hon-server")).ToList();
        serverLogs.Should().NotBeEmpty();
        serverLogs.Should().OnlyContain(
            l => l.Contains("11235"),
            "All dedicated server logs and pipe addresses must strictly specify the target listen port 11235");
        serverLogs.Should().NotContain(
            l => l.Contains(@"\\.\pipe\hon-server") && !l.Contains(@"\\.\pipe\hon-server-11235"),
            "Dedicated server commands must never target generic unnamed pipe \\\\.\\pipe\\hon-server");
    }

    [Fact]
    public async Task Launch_StringPortParameter_ParsesSuccessfully()
    {
        using var ctx = CreateContext(port: 11235);
        var args = JsonElement.Parse("{\"port\": \"11235\"}");

        var result = await ctx.Handler.HandleAsync(args);

        result.IsError.Should().BeFalse();
        var root = ParseResultJson(result);
        root.GetProperty("port").GetInt32().Should().Be(11235);
    }

    [Fact]
    public async Task Launch_InvalidNonNumericPortString_ReturnsValidationError()
    {
        using var ctx = CreateContext();
        var args = JsonElement.Parse("{\"port\": \"invalid_port\"}");

        var result = await ctx.Handler.HandleAsync(args);

        result.IsError.Should().BeTrue();
        string err = GetContentText(result);
        err.Should().Contain("Invalid port parameter");
    }

    #endregion

    #region Category 2: Deterministic 5v5 Bot Population (6 Tests)

    [Fact]
    public async Task Launch_DefaultBotCounts_Adds4BotsToTeam1And5BotsToTeam2()
    {
        using var ctx = CreateContext();
        var args = JsonElement.Parse("{}");

        var result = await ctx.Handler.HandleAsync(args);

        result.IsError.Should().BeFalse();
        string stdin = await ctx.ReadServerStdinContentAsync();

        CountOccurrences(stdin, "AddFakePlayer 1").Should().Be(4);
        CountOccurrences(stdin, "AddFakePlayer 2").Should().Be(5);
    }

    [Fact]
    public async Task Launch_CustomBotCounts_DispatchesRequestedCountsPerTeam()
    {
        using var ctx = CreateContext();
        var args = JsonElement.Parse("{\"team1Bots\": 2, \"team2Bots\": 3}");

        var result = await ctx.Handler.HandleAsync(args);

        result.IsError.Should().BeFalse();
        var root = ParseResultJson(result);
        root.GetProperty("team1Count").GetInt32().Should().Be(3); // 1 human + 2 bots
        root.GetProperty("team2Count").GetInt32().Should().Be(3); // 3 bots
        root.GetProperty("totalPlayers").GetInt32().Should().Be(6);

        string stdin = await ctx.ReadServerStdinContentAsync();
        CountOccurrences(stdin, "AddFakePlayer 1").Should().Be(2);
        CountOccurrences(stdin, "AddFakePlayer 2").Should().Be(3);
    }

    [Fact]
    public async Task Launch_TotalPlayerCount_AccuratelyCalculated()
    {
        using var ctx = CreateContext();
        var args = JsonElement.Parse("{}");

        var result = await ctx.Handler.HandleAsync(args);

        result.IsError.Should().BeFalse();
        var root = ParseResultJson(result);
        root.GetProperty("team1Count").GetInt32().Should().Be(5);
        root.GetProperty("team2Count").GetInt32().Should().Be(5);
        root.GetProperty("totalPlayers").GetInt32().Should().Be(10);
    }

    [Fact]
    public async Task Launch_AutoConnectFalse_PopulatesBotsWithoutConnectingClient()
    {
        using var ctx = CreateContext();
        var args = JsonElement.Parse("{\"autoConnect\": false}");

        var result = await ctx.Handler.HandleAsync(args);

        result.IsError.Should().BeFalse();
        var root = ParseResultJson(result);
        root.GetProperty("clientConnected").GetBoolean().Should().BeFalse();
        root.GetProperty("team1Count").GetInt32().Should().Be(4); // 0 human + 4 bots
        root.GetProperty("team2Count").GetInt32().Should().Be(5);
        root.GetProperty("totalPlayers").GetInt32().Should().Be(9);

        ctx.ClientDispatchedCommands.Should().BeEmpty();
    }

    [Fact]
    public async Task Launch_ZeroBots_CalculatesSinglePlayerCorrectly()
    {
        using var ctx = CreateContext();
        var args = JsonElement.Parse("{\"team1Bots\": 0, \"team2Bots\": 0}");

        var result = await ctx.Handler.HandleAsync(args);

        result.IsError.Should().BeFalse();
        var root = ParseResultJson(result);
        root.GetProperty("team1Count").GetInt32().Should().Be(1); // 1 human + 0 bots
        root.GetProperty("team2Count").GetInt32().Should().Be(0);
        root.GetProperty("totalPlayers").GetInt32().Should().Be(1);

        string stdin = await ctx.ReadServerStdinContentAsync();
        CountOccurrences(stdin, "AddFakePlayer 1").Should().Be(0);
        CountOccurrences(stdin, "AddFakePlayer 2").Should().Be(0);
    }

    [Fact]
    public async Task Launch_BotPopulationCommands_DispatchedInDeterministicOrder()
    {
        using var ctx = CreateContext();
        var args = JsonElement.Parse("{}");

        var result = await ctx.Handler.HandleAsync(args);

        result.IsError.Should().BeFalse();
        var root = ParseResultJson(result);

        var cmds = root.GetProperty("dispatchedCommands").EnumerateArray().Select(c => c.GetString()!).ToList();
        cmds.Should().ContainInConsecutiveOrder(
            "connect 127.0.0.1:11235",
            "Team 1 0",
            "SelectHero Hero_Pyromancer");

        int firstBot1 = cmds.IndexOf("AddFakePlayer 1");
        int lastBot1 = cmds.LastIndexOf("AddFakePlayer 1");
        int firstBot2 = cmds.IndexOf("AddFakePlayer 2");
        int lastBot2 = cmds.LastIndexOf("AddFakePlayer 2");
        int matchStart = cmds.IndexOf("RequestMatchStart");

        firstBot1.Should().BeLessThan(firstBot2);
        lastBot1.Should().BeLessThan(firstBot2);
        lastBot2.Should().BeLessThan(matchStart);
    }

    #endregion

    #region Category 3: Lifecycle & Phase 3 Verification (5 Tests)

    [Fact]
    public async Task Launch_WhenServerActive_ConfirmsGamePhase3()
    {
        using var ctx = CreateContext();
        var args = JsonElement.Parse("{}");

        var result = await ctx.Handler.HandleAsync(args);

        result.IsError.Should().BeFalse();
        var root = ParseResultJson(result);
        root.GetProperty("gamePhase").GetInt32().Should().Be(3);
        root.GetProperty("status").GetString().Should().Be("success");
    }

    [Fact]
    public async Task Launch_DispatchesRequestMatchStart_ToTriggerActiveMatch()
    {
        using var ctx = CreateContext();
        var args = JsonElement.Parse("{}");

        var result = await ctx.Handler.HandleAsync(args);

        result.IsError.Should().BeFalse();
        string stdin = await ctx.ReadServerStdinContentAsync();
        stdin.Should().Contain("RequestMatchStart");
    }

    [Fact]
    public async Task Launch_HeroAssignment_SelectsSpecifiedHero()
    {
        using var ctx = CreateContext();
        var args = JsonElement.Parse("{\"hero\": \"Hero_Devourer\"}");

        var result = await ctx.Handler.HandleAsync(args);

        result.IsError.Should().BeFalse();
        var root = ParseResultJson(result);
        root.GetProperty("hero").GetString().Should().Be("Hero_Devourer");

        ctx.ClientDispatchedCommands.Should().Contain("SelectHero Hero_Devourer");
    }

    [Fact]
    public async Task Launch_MapAssignment_LoadsSpecifiedMap()
    {
        using var ctx = CreateContext();
        var args = JsonElement.Parse("{\"map\": \"midwars\"}");

        var result = await ctx.Handler.HandleAsync(args);

        result.IsError.Should().BeFalse();
        var root = ParseResultJson(result);
        root.GetProperty("map").GetString().Should().Be("midwars");
    }

    [Fact]
    public async Task Launch_WhenInvoked_ClientAssignsToTeam1Slot0()
    {
        using var ctx = CreateContext();
        var args = JsonElement.Parse("{}");

        var result = await ctx.Handler.HandleAsync(args);

        result.IsError.Should().BeFalse();
        ctx.ClientDispatchedCommands.Should().Contain("Team 1 0");
    }

    [Fact]
    public async Task Launch_WhenPhaseIsNot3_ReportsActualPhaseDynamically()
    {
        using var ctx = CreateContext();
        ctx.MockClientIpc.Setup(i => i.GetEngineStateAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new EngineStateDto
            {
                ActiveMatchId = 10001,
                GamePhase = 1,
                GamePhaseName = "Hero Pick (Phase 1)",
                CurrentMap = "caldavar"
            });

        var args = JsonElement.Parse("{\"expectedPhase\": 1}");
        var result = await ctx.Handler.HandleAsync(args);

        result.IsError.Should().BeFalse();
        var root = ParseResultJson(result);
        root.GetProperty("gamePhase").GetInt32().Should().Be(1);
        root.GetProperty("message").GetString().Should().Contain("Phase 1");
    }

    [Fact]
    public async Task Launch_WhenEngineStateReportsPhase2_ResolvesPhaseDynamically()
    {
        using var ctx = CreateContext();
        ctx.MockClientIpc.Setup(i => i.GetEngineStateAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new EngineStateDto
            {
                ActiveMatchId = 10001,
                GamePhase = 2,
                GamePhaseName = "Pre-Match Hero Pick (Phase 2)",
                CurrentMap = "caldavar"
            });

        var args = JsonElement.Parse("{}");
        var result = await ctx.Handler.HandleAsync(args);

        result.IsError.Should().BeFalse();
        var root = ParseResultJson(result);
        root.GetProperty("gamePhase").GetInt32().Should().Be(2);
        root.GetProperty("message").GetString().Should().Contain("Phase 2");
    }

    [Fact]
    public async Task Launch_WhenClientConnectionFails_DoesNotCountClientInLegionTeam()
    {
        using var ctx = CreateContext();
        ctx.MockClientIpc.Setup(i => i.ExecuteCommandAsync(It.Is<string>(s => s.StartsWith("connect")), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("Failed to connect client"));

        var args = JsonElement.Parse("{\"team1Bots\": 4, \"team2Bots\": 5}");
        var result = await ctx.Handler.HandleAsync(args);

        result.IsError.Should().BeFalse();
        var root = ParseResultJson(result);
        root.GetProperty("clientConnected").GetBoolean().Should().BeFalse();
        // Since client is not connected, Legion team count should only include the 4 bots, not 5
        root.GetProperty("team1Count").GetInt32().Should().Be(4);
        root.GetProperty("team2Count").GetInt32().Should().Be(5);
        root.GetProperty("totalPlayers").GetInt32().Should().Be(9);
    }

    #endregion

    #region Category 4: Actions (Status & Stop) (5 Tests)

    [Fact]
    public async Task Status_WhenServerRunning_ReturnsActivePhaseAndPlayerCounts()
    {
        using var ctx = CreateContext();
        // Launch first to populate session
        await ctx.Handler.HandleAsync(JsonElement.Parse("{}"));

        var statusResult = await ctx.Handler.HandleAsync(JsonElement.Parse("{\"action\": \"status\"}"));

        statusResult.IsError.Should().BeFalse();
        var root = ParseResultJson(statusResult);
        root.GetProperty("status").GetString().Should().Be("success");
        root.GetProperty("action").GetString().Should().Be("status");
        root.GetProperty("serverRunning").GetBoolean().Should().BeTrue();
        root.GetProperty("gamePhase").GetInt32().Should().Be(3);
        root.GetProperty("totalPlayers").GetInt32().Should().Be(10);
    }

    [Fact]
    public async Task Status_WhenServerOffline_ReturnsOfflineStatusGracefully()
    {
        using var ctx = CreateContext(port: 11299, registerExistingWorker: false);
        var statusResult = await ctx.Handler.HandleAsync(JsonElement.Parse("{\"action\": \"status\", \"port\": 11299}"));

        statusResult.IsError.Should().BeFalse();
        var root = ParseResultJson(statusResult);
        root.GetProperty("status").GetString().Should().Be("success");
        root.GetProperty("serverRunning").GetBoolean().Should().BeFalse();
        root.GetProperty("gamePhase").GetInt32().Should().Be(0);
        root.GetProperty("totalPlayers").GetInt32().Should().Be(0);
    }

    [Fact]
    public async Task Stop_WhenServerRunning_TerminatesServerAndResetsSession()
    {
        using var ctx = CreateContext();
        await ctx.Handler.HandleAsync(JsonElement.Parse("{}"));

        var stopResult = await ctx.Handler.HandleAsync(JsonElement.Parse("{\"action\": \"stop\"}"));

        stopResult.IsError.Should().BeFalse();
        var root = ParseResultJson(stopResult);
        root.GetProperty("status").GetString().Should().Be("success");
        root.GetProperty("action").GetString().Should().Be("stop");
        root.GetProperty("serverRunning").GetBoolean().Should().BeFalse();
        root.GetProperty("totalPlayers").GetInt32().Should().Be(0);

        // Verify immediate status after stop reports serverRunning: false and totalPlayers: 0
        var statusResult = await ctx.Handler.HandleAsync(JsonElement.Parse("{\"action\": \"status\"}"));
        statusResult.IsError.Should().BeFalse();
        var statusRoot = ParseResultJson(statusResult);
        statusRoot.GetProperty("status").GetString().Should().Be("success");
        statusRoot.GetProperty("serverRunning").GetBoolean().Should().BeFalse();
        statusRoot.GetProperty("totalPlayers").GetInt32().Should().Be(0);
    }

    [Fact]
    public async Task LaunchStopStatus_Cycle_ReportsServerOfflineAndZeroPlayers()
    {
        using var ctx = CreateContext();
        // 1. Launch scrimmage
        var launchResult = await ctx.Handler.HandleAsync(JsonElement.Parse("{}"));
        launchResult.IsError.Should().BeFalse();
        var launchRoot = ParseResultJson(launchResult);
        launchRoot.GetProperty("serverRunning").GetBoolean().Should().BeTrue();
        launchRoot.GetProperty("totalPlayers").GetInt32().Should().Be(10);

        // 2. Stop scrimmage
        var stopResult = await ctx.Handler.HandleAsync(JsonElement.Parse("{\"action\": \"stop\"}"));
        stopResult.IsError.Should().BeFalse();
        var stopRoot = ParseResultJson(stopResult);
        stopRoot.GetProperty("status").GetString().Should().Be("success");
        stopRoot.GetProperty("serverRunning").GetBoolean().Should().BeFalse();

        // 3. Status immediately after stop
        var statusResult = await ctx.Handler.HandleAsync(JsonElement.Parse("{\"action\": \"status\"}"));
        statusResult.IsError.Should().BeFalse();
        var statusRoot = ParseResultJson(statusResult);
        statusRoot.GetProperty("status").GetString().Should().Be("success");
        statusRoot.GetProperty("serverRunning").GetBoolean().Should().BeFalse();
        statusRoot.GetProperty("clientConnected").GetBoolean().Should().BeFalse();
        statusRoot.GetProperty("gamePhase").GetInt32().Should().Be(0);
        statusRoot.GetProperty("team1Count").GetInt32().Should().Be(0);
        statusRoot.GetProperty("team2Count").GetInt32().Should().Be(0);
        statusRoot.GetProperty("totalPlayers").GetInt32().Should().Be(0);
    }

    [Fact]
    public async Task Stop_WhenServerOffline_ReturnsCleanSuccess()
    {
        using var ctx = CreateContext(port: 11298, registerExistingWorker: false);
        var stopResult = await ctx.Handler.HandleAsync(JsonElement.Parse("{\"action\": \"stop\", \"port\": 11298}"));

        stopResult.IsError.Should().BeFalse();
        var root = ParseResultJson(stopResult);
        root.GetProperty("status").GetString().Should().Be("success");
        root.GetProperty("serverRunning").GetBoolean().Should().BeFalse();
        root.GetProperty("terminatedProcessId").GetInt32().Should().Be(0);
    }

    [Fact]
    public async Task HandleAsync_UnknownAction_ReturnsErrorWithSupportedActions()
    {
        using var ctx = CreateContext();
        var result = await ctx.Handler.HandleAsync(JsonElement.Parse("{\"action\": \"explode\"}"));

        result.IsError.Should().BeTrue();
        string err = GetContentText(result);
        err.Should().Contain("Supported actions: launch, status, stop");
    }

    #endregion

    #region Category 5: McpToolRegistry & Aliases Integration (4 Tests)

    [Fact]
    public void McpToolRegistry_WhenInvoked_ContainsCanonicalScrimmageLaunchTool()
    {
        McpToolRegistry registry = new(registerDefaults: true);
        registry.HasTool("kongor_scrimmage_launch").Should().BeTrue();

        var tool = registry.GetTool("kongor_scrimmage_launch");
        tool.Should().NotBeNull();
        tool!.Name.Should().Be("kongor_scrimmage_launch");
        tool.InputSchema.Properties.Should().ContainKey("port");
        tool.InputSchema.Properties.Should().ContainKey("team1Bots");
        tool.InputSchema.Properties.Should().ContainKey("team2Bots");
    }

    [Fact]
    public void McpToolRegistry_Alias_kongor_scrimmage_ResolvesToCanonicalTool()
    {
        McpToolRegistry registry = new(registerDefaults: true);
        registry.HasTool("kongor_scrimmage").Should().BeTrue();

        var tool = registry.GetTool("kongor_scrimmage");
        tool.Should().NotBeNull();
        tool!.Name.Should().Be("kongor_scrimmage_launch");
    }

    [Fact]
    public void McpToolRegistry_Alias_kongor_5v5_launch_ResolvesToCanonicalTool()
    {
        McpToolRegistry registry = new(registerDefaults: true);
        registry.HasTool("kongor_5v5_launch").Should().BeTrue();

        var tool = registry.GetTool("kongor_5v5_launch");
        tool.Should().NotBeNull();
        tool!.Name.Should().Be("kongor_scrimmage_launch");
    }

    [Fact]
    public void McpToolRegistry_ListTools_ExcludesPrunedAliases()
    {
        McpToolRegistry registry = new(registerDefaults: true);
        var tools = registry.ListTools();

        tools.Should().Contain(t => t.Name == "kongor_scrimmage_launch");
        tools.Should().NotContain(t => t.Name == "kongor_scrimmage");
        tools.Should().NotContain(t => t.Name == "kongor_5v5_launch");
    }

    #endregion

    #region Category 6: Performance, Safety & Error Remediation (3 Tests)

    [Fact]
    public async Task RapidTerminate_WhenRequested_TerminatesCleanlyWithinThreshold()
    {
        using var ctx = CreateContext();
        var args = JsonElement.Parse("{\"rapidTerminateAfterLaunch\": true}");

        Stopwatch sw = Stopwatch.StartNew();
        var result = await ctx.Handler.HandleAsync(args);
        sw.Stop();

        result.IsError.Should().BeFalse();
        var root = ParseResultJson(result);
        root.GetProperty("rapidTerminated").GetBoolean().Should().BeTrue();
        root.GetProperty("serverRunning").GetBoolean().Should().BeFalse();
        sw.ElapsedMilliseconds.Should().BeLessThan(2000);
    }

    [Fact]
    public async Task InnerLoopExecution_20Iterations_CompletesUnder500Milliseconds()
    {
        using var ctx = CreateContext();
        var args = JsonElement.Parse("{\"rapidTerminateAfterLaunch\": true}");

        // Single warmup iteration prior to benchmark timing to eliminate cold JIT compilation overhead
        var warmupRes = await ctx.Handler.HandleAsync(args);
        warmupRes.IsError.Should().BeFalse();

        Stopwatch sw = Stopwatch.StartNew();
        for (int i = 0; i < 20; i++)
        {
            var res = await ctx.Handler.HandleAsync(args);
            res.IsError.Should().BeFalse();
        }
        sw.Stop();

        sw.ElapsedMilliseconds.Should().BeLessThan(2000, "20 simulated scrimmage launch and terminate cycles must execute under 2000ms");
    }

    [Fact]
    public async Task Launch_WhenServerFailsToStart_ReturnsStructuredErrorAndRemediation()
    {
        using var ctx = CreateContext(port: 11255, registerExistingWorker: false);
        var args = JsonElement.Parse("{\"port\": 11255, \"simulateServerFailure\": true}");

        var result = await ctx.Handler.HandleAsync(args);

        result.IsError.Should().BeTrue();
        var root = ParseResultJson(result);
        root.GetProperty("status").GetString().Should().Be("error");
        root.GetProperty("serverRunning").GetBoolean().Should().BeFalse();
        root.GetProperty("message").GetString().Should().Contain("Failed to start dedicated server worker");
        root.GetProperty("remediationHint").GetString().Should().NotBeNullOrWhiteSpace();

        var recentErrors = ctx.RemediationService.GetRecentErrors(5);
        recentErrors.Should().NotBeEmpty();
        recentErrors.Should().Contain(e => e.Target == "server");
    }

    [Fact]
    public async Task Remediation_ClientError_RecordedInRemediationService()
    {
        using var ctx = CreateContext();
        ctx.MockClientIpc.Setup(i => i.ExecuteCommandAsync(It.Is<string>(s => s.StartsWith("connect")), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("Failed to bind socket: connection refused"));

        var args = JsonElement.Parse("{}");
        var result = await ctx.Handler.HandleAsync(args);

        result.IsError.Should().BeFalse();
        var errors = ctx.RemediationService.GetRecentErrors(10);
        errors.Should().NotBeEmpty();
        errors.Should().Contain(e => e.Command.StartsWith("connect") && e.RawErrorResponse.Contains("connection refused"));
    }

    #endregion
}
