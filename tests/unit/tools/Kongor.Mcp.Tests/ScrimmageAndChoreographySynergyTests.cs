// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// TAO (Together As One) MCP & Kongor CLI -- Created by guccidonut (https://github.com/guccidonut/PKV)

using System.Collections.Concurrent;
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
/// Comprehensive fast automated unit test suite (< 2.0s) validating:
/// - Scrimmage Orchestration (kongor_scrimmage_launch) & Bot Choreography (kongor_bot_choreography) synergy.
/// - Dynamic IServerPortRegistry authoritative registration on scrimmage launch and unregistration on stop.
/// - BotChoreography dynamic port resolution via ServerPortRegistry when port is omitted.
/// - target: "both" synchronized concurrent execution across dedicated server and client IPC.
/// - Live dedicated server command piping during choreography presets and custom sequences.
/// - Scrimmage session lifecycle tracking and 'list' action query.
/// </summary>
public sealed class ScrimmageAndChoreographySynergyTests : IDisposable
{
    private readonly List<string> _tempFiles = [];

    private static JsonElement ParseJson(string json)
    {
        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.Clone();
    }

    private static JsonElement ParseResult(ToolCallResult result)
    {
        result.IsError.Should().BeFalse(result.FirstText);
        result.Content.Should().NotBeEmpty();
        string text = result.Content[0] is TextContentItem tc ? tc.Text : result.FirstText;
        return ParseJson(text);
    }

    private sealed class SynergyTestContext : IDisposable
    {
        public ScrimmageLaunchHandler ScrimmageHandler { get; init; } = null!;
        public BotChoreographyHandler BotHandler { get; init; } = null!;
        public GameServerManageHandler ServerManage { get; init; } = null!;
        public GameClientManageHandler ClientManage { get; init; } = null!;
        public ServerPortRegistry PortRegistry { get; init; } = null!;
        public Mock<IGameIpcBridge> MockClientIpc { get; init; } = null!;
        public ClientCommandRemediationService RemediationService { get; init; } = null!;
        public MemoryStream ServerStdinStream { get; init; } = null!;
        public StreamWriter ServerStdinWriter { get; init; } = null!;
        public List<string> ClientCommands { get; init; } = [];
        public int Port { get; init; }

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

    private SynergyTestContext CreateContext(int port = 11235, bool registerWorker = true)
    {
        string tempRegPath = Path.Combine(Path.GetTempPath(), $"reg_synergy_{Guid.NewGuid():N}.json");
        _tempFiles.Add(tempRegPath);
        var reg = new ServerPortRegistry(tempRegPath);

        var serverManage = new GameServerManageHandler(portRegistry: reg);

        MemoryStream ms = new();
        StreamWriter writer = new(ms, Encoding.UTF8, leaveOpen: true);

        FieldInfo? stdinField = typeof(GameServerManageHandler).GetField("_workerStdin", BindingFlags.NonPublic | BindingFlags.Instance);
        var workerStdin = (ConcurrentDictionary<int, StreamWriter>)stdinField!.GetValue(serverManage)!;
        workerStdin[port] = writer;

        if (registerWorker)
        {
            FieldInfo? workersField = typeof(GameServerManageHandler).GetField("_trackedWorkers", BindingFlags.NonPublic | BindingFlags.Instance);
            var trackedWorkers = (ConcurrentDictionary<int, GameServerWorkerInfo>)workersField!.GetValue(serverManage)!;
            trackedWorkers[Environment.ProcessId] = new GameServerWorkerInfo
            {
                ProcessId = Environment.ProcessId,
                Port = port,
                ServerName = $"Project KONGOR Test Worker {port}",
                Map = "caldavar",
                Responding = true
            };
        }

        List<string> clientCommands = [];
        Mock<IGameIpcBridge> mockIpc = new();
        mockIpc.SetupGet(x => x.IsConnected).Returns(true);
        mockIpc.Setup(x => x.GetEngineStateAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new EngineStateDto
            {
                ActiveMatchId = 20002,
                GamePhase = 3,
                GamePhaseName = "In-Game Combat (Phase 3)",
                CurrentMap = "caldavar"
            });
        mockIpc.Setup(x => x.ExecuteCommandAsync(It.IsAny<string>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .Returns((string cmd, int to, CancellationToken ct) =>
            {
                clientCommands.Add(cmd);
                if (cmd.Contains("GetCurrentPhase")) return Task.FromResult("3");
                return Task.FromResult("[TAO_RESPONSE]{\"status\":\"success\",\"preset\":\"pyro_mid_combo\",\"actions_executed\":3}[/TAO_RESPONSE]");
            });

        var remediation = new ClientCommandRemediationService();
        var clientManage = new GameClientManageHandler(ipcBridge: mockIpc.Object, remediationService: remediation);
        var matchState = new MatchStateHandler(gameIpcBridge: mockIpc.Object, serverManageHandler: serverManage, portRegistry: reg);

        var scrimmageHandler = new ScrimmageLaunchHandler(
            serverManageHandler: serverManage,
            clientManageHandler: clientManage,
            matchStateHandler: matchState,
            clientIpcBridge: mockIpc.Object,
            remediationService: remediation,
            portRegistry: reg);

        var botHandler = new BotChoreographyHandler(
            gameIpcBridge: mockIpc.Object,
            clientManageHandler: clientManage,
            serverManageHandler: serverManage,
            portRegistry: reg,
            remediationService: remediation);

        return new SynergyTestContext
        {
            ScrimmageHandler = scrimmageHandler,
            BotHandler = botHandler,
            ServerManage = serverManage,
            ClientManage = clientManage,
            PortRegistry = reg,
            MockClientIpc = mockIpc,
            RemediationService = remediation,
            ServerStdinStream = ms,
            ServerStdinWriter = writer,
            ClientCommands = clientCommands,
            Port = port
        };
    }

    public void Dispose()
    {
        foreach (var file in _tempFiles)
        {
            try { if (File.Exists(file)) File.Delete(file); } catch { }
        }
    }

    [Fact]
    public async Task ScrimmageLaunch_RegistersDedicatedServerWorkerInPortRegistry_OnLaunch()
    {
        using var ctx = CreateContext(port: 11240);

        using var argsDoc = JsonDocument.Parse("""
        {
            "action": "launch",
            "port": 11240,
            "map": "caldavar",
            "hero": "Hero_Pyromancer",
            "autoConnect": true,
            "team1Bots": 4,
            "team2Bots": 5
        }
        """);

        var result = await ctx.ScrimmageHandler.HandleAsync(argsDoc.RootElement);
        var root = ParseResult(result);

        root.GetProperty("status").GetString().Should().Be("success");
        root.GetProperty("port").GetInt32().Should().Be(11240);
        root.GetProperty("gamePhase").GetInt32().Should().Be(3);
        root.GetProperty("totalPlayers").GetInt32().Should().Be(10);

        // Verify authoritative registration in port registry
        var activeWorkers = ctx.PortRegistry.GetActiveWorkers(pruneStale: false);
        var registered = activeWorkers.FirstOrDefault(w => w.Port == 11240);
        registered.Should().NotBeNull();
        registered!.Map.Should().Be("caldavar");
        registered.ProcessId.Should().Be(Environment.ProcessId);
    }

    [Fact]
    public async Task ScrimmageLaunch_UnregistersDedicatedServerWorkerFromPortRegistry_OnStop()
    {
        using var ctx = CreateContext(port: 11241);

        // 1. Launch
        using var launchDoc = JsonDocument.Parse("""{"action": "launch", "port": 11241}""");
        await ctx.ScrimmageHandler.HandleAsync(launchDoc.RootElement);
        ctx.PortRegistry.GetActiveWorkers(pruneStale: false).Any(w => w.Port == 11241).Should().BeTrue();

        // 2. Stop
        using var stopDoc = JsonDocument.Parse("""{"action": "stop", "port": 11241}""");
        var stopResult = await ctx.ScrimmageHandler.HandleAsync(stopDoc.RootElement);
        var root = ParseResult(stopResult);

        root.GetProperty("status").GetString().Should().Be("success");
        root.GetProperty("action").GetString().Should().Be("stop");
        root.GetProperty("serverRunning").GetBoolean().Should().BeFalse();

        // Verify worker unregistered from port registry
        ctx.PortRegistry.GetActiveWorkers(pruneStale: false).Any(w => w.Port == 11241).Should().BeFalse();
    }

    [Fact]
    public async Task ScrimmageLaunch_ActionList_ReturnsAllActiveScrimmageSessions()
    {
        using var ctx = CreateContext(port: 11242);

        // Launch session
        using var launchDoc = JsonDocument.Parse("""{"action": "launch", "port": 11242, "map": "caldavar", "hero": "Hero_Devourer"}""");
        await ctx.ScrimmageHandler.HandleAsync(launchDoc.RootElement);

        // Query list
        using var listDoc = JsonDocument.Parse("""{"action": "list"}""");
        var listResult = await ctx.ScrimmageHandler.HandleAsync(listDoc.RootElement);
        var root = ParseResult(listResult);

        root.GetProperty("status").GetString().Should().Be("success");
        root.GetProperty("action").GetString().Should().Be("list");
        root.GetProperty("count").GetInt32().Should().BeGreaterThanOrEqualTo(1);

        var sessions = root.GetProperty("sessions");
        bool found = false;
        foreach (var s in sessions.EnumerateArray())
        {
            if (s.GetProperty("port").GetInt32() == 11242)
            {
                s.GetProperty("hero").GetString().Should().Be("Hero_Devourer");
                s.GetProperty("gamePhase").GetInt32().Should().Be(3);
                found = true;
                break;
            }
        }
        found.Should().BeTrue();
    }

    [Fact]
    public async Task BotChoreography_ResolvesPortFromPortRegistry_WhenPortOmitted()
    {
        using var ctx = CreateContext(port: 11243);

        // Pre-register worker in port registry
        ctx.PortRegistry.RegisterWorker(Environment.ProcessId, 11243, map: "caldavar");

        // Execute bot choreography without specifying port
        using var argsDoc = JsonDocument.Parse("""
        {
            "action": "preset",
            "preset": "pyro_mid_combo",
            "target": "server"
        }
        """);

        var result = await ctx.BotHandler.HandleAsync(argsDoc.RootElement);
        var root = ParseResult(result);

        root.GetProperty("success").GetBoolean().Should().BeTrue();
        root.GetProperty("port").GetInt32().Should().Be(11243);
        root.GetProperty("targetPipe").GetString().Should().Be(@"\\.\pipe\hon-server-11243");
    }

    [Fact]
    public async Task BotChoreography_TargetBoth_DispatchesConcurrentlyToClientAndServer()
    {
        using var ctx = CreateContext(port: 11244);
        ctx.PortRegistry.RegisterWorker(Environment.ProcessId, 11244, map: "caldavar");

        using var argsDoc = JsonDocument.Parse("""
        {
            "action": "preset",
            "preset": "dual_lane_patrol",
            "target": "both",
            "port": 11244
        }
        """);

        var result = await ctx.BotHandler.HandleAsync(argsDoc.RootElement);
        var root = ParseResult(result);

        root.GetProperty("success").GetBoolean().Should().BeTrue();
        root.GetProperty("target").GetString().Should().Be("both");
        root.GetProperty("port").GetInt32().Should().Be(11244);

        // Verify client IPC was called
        ctx.ClientCommands.Any(cmd => cmd.Contains("bot_choreography")).Should().BeTrue();
    }

    [Fact]
    public async Task BotChoreography_TargetBoth_ReturnsAggregatedSuccessAndDetails()
    {
        using var ctx = CreateContext(port: 11245);
        ctx.PortRegistry.RegisterWorker(Environment.ProcessId, 11245, map: "caldavar");

        using var argsDoc = JsonDocument.Parse("""
        {
            "action": "preset",
            "preset": "kongor_pit_clash",
            "target": "both",
            "port": 11245
        }
        """);

        var result = await ctx.BotHandler.HandleAsync(argsDoc.RootElement);
        var root = ParseResult(result);

        root.GetProperty("success").GetBoolean().Should().BeTrue();
        root.GetProperty("status").GetString().Should().Be("completed");
        root.TryGetProperty("server", out var srvObj).Should().BeTrue();
        srvObj.ValueKind.Should().Be(JsonValueKind.Object);
        srvObj.GetProperty("preset").GetString().Should().Be("kongor_pit_clash");

        root.TryGetProperty("client", out var cliObj).Should().BeTrue();
        cliObj.ValueKind.Should().Be(JsonValueKind.Object);
        cliObj.GetProperty("target").GetString().Should().Be("client");
    }

    [Fact]
    public async Task BotChoreography_LiveExecution_PipesCommandsToRunningServerWorker()
    {
        using var ctx = CreateContext(port: 11246);

        using var argsDoc = JsonDocument.Parse("""
        {
            "action": "preset",
            "preset": "pyro_mid_combo",
            "port": 11246,
            "target": "server",
            "dryRun": false
        }
        """);

        var result = await ctx.BotHandler.HandleAsync(argsDoc.RootElement);
        var root = ParseResult(result);

        root.GetProperty("success").GetBoolean().Should().BeTrue();

        // Verify commands were written to server stdin stream
        string stdinLog = await ctx.ReadServerStdinContentAsync();
        stdinLog.Should().Contain("AddFakePlayer");
    }

    [Fact]
    public async Task BotChoreography_PresetTeamfight5v5_DualChannelRouting()
    {
        using var ctx = CreateContext(port: 11247);

        using var argsDoc = JsonDocument.Parse("""
        {
            "action": "preset",
            "preset": "teamfight_5v5",
            "port": 11247,
            "target": "server"
        }
        """);

        var result = await ctx.BotHandler.HandleAsync(argsDoc.RootElement);
        var root = ParseResult(result);

        root.GetProperty("success").GetBoolean().Should().BeTrue();
        root.GetProperty("preset").GetString().Should().Be("teamfight_5v5");
        var commands = root.GetProperty("commands");
        commands.GetArrayLength().Should().BeGreaterThanOrEqualTo(8);

        bool hasFakePlayer1 = false;
        bool hasFakePlayer2 = false;
        bool hasAttackMove = false;

        foreach (var c in commands.EnumerateArray())
        {
            string cmd = c.GetProperty("command").GetString() ?? "";
            if (cmd.Contains("AddFakePlayer 1")) hasFakePlayer1 = true;
            if (cmd.Contains("AddFakePlayer 2")) hasFakePlayer2 = true;
            if (cmd.Contains("BotGroupAttackMove")) hasAttackMove = true;
        }

        hasFakePlayer1.Should().BeTrue();
        hasFakePlayer2.Should().BeTrue();
        hasAttackMove.Should().BeTrue();
    }

    [Fact]
    public async Task ScrimmageAndBotChoreography_E2ESynergy_LaunchPopulatesRosterAndChoreographyExecutesOnSamePort()
    {
        using var ctx = CreateContext(port: 11248);

        // 1. Launch scrimmage on port 11248
        using var launchDoc = JsonDocument.Parse("""
        {
            "action": "launch",
            "port": 11248,
            "map": "caldavar",
            "hero": "Hero_Pyromancer",
            "team1Bots": 4,
            "team2Bots": 5
        }
        """);
        var launchResult = await ctx.ScrimmageHandler.HandleAsync(launchDoc.RootElement);
        var launchRoot = ParseResult(launchResult);
        launchRoot.GetProperty("totalPlayers").GetInt32().Should().Be(10);

        // Verify worker is registered in port registry
        ctx.PortRegistry.GetActiveWorkers(pruneStale: false).Any(w => w.Port == 11248).Should().BeTrue();

        // 2. Call bot choreography without passing port -> resolves 11248 automatically!
        using var choreoDoc = JsonDocument.Parse("""
        {
            "action": "preset",
            "preset": "push_and_defend",
            "target": "both"
        }
        """);
        var choreoResult = await ctx.BotHandler.HandleAsync(choreoDoc.RootElement);
        var choreoRoot = ParseResult(choreoResult);

        choreoRoot.GetProperty("success").GetBoolean().Should().BeTrue();
        choreoRoot.GetProperty("port").GetInt32().Should().Be(11248);
        choreoRoot.GetProperty("target").GetString().Should().Be("both");

        // 3. Stop scrimmage
        using var stopDoc = JsonDocument.Parse("""{"action": "stop", "port": 11248}""");
        await ctx.ScrimmageHandler.HandleAsync(stopDoc.RootElement);
        ctx.PortRegistry.GetActiveWorkers(pruneStale: false).Any(w => w.Port == 11248).Should().BeFalse();
    }

    [Fact]
    public void McpToolRegistry_WhenInvoked_WiresPortRegistryToBothHandlers()
    {
        var registry = new McpToolRegistry(registerDefaults: true);

        registry.ScrimmageLaunchHandler.Should().NotBeNull();
        registry.BotChoreographyHandler.Should().NotBeNull();
        registry.GameServerManageHandler.Should().NotBeNull();

        registry.ScrimmageLaunchHandler.PortRegistry.Should().BeSameAs(registry.GameServerManageHandler.PortRegistry);
        registry.BotChoreographyHandler.PortRegistry.Should().BeSameAs(registry.GameServerManageHandler.PortRegistry);
    }

    [Fact]
    public async Task BotChoreography_Stop_HaltsSessionAndReportsHalted()
    {
        using var ctx = CreateContext(port: 11249);

        // Start a preset session
        using var presetDoc = JsonDocument.Parse("""{"action": "preset", "preset": "gank_rotation", "port": 11249}""");
        await ctx.BotHandler.HandleAsync(presetDoc.RootElement);

        // Stop session
        using var stopDoc = JsonDocument.Parse("""{"action": "stop", "port": 11249}""");
        var stopResult = await ctx.BotHandler.HandleAsync(stopDoc.RootElement);
        var root = ParseResult(stopResult);

        root.GetProperty("success").GetBoolean().Should().BeTrue();
        root.GetProperty("action").GetString().Should().Be("stop");
        root.GetProperty("port").GetInt32().Should().Be(11249);
        root.GetProperty("wasActive").GetBoolean().Should().BeTrue();
        root.GetProperty("status").GetString().Should().Be("halted");
    }

    [Fact]
    public async Task ScrimmageLaunch_SimulatedServerFailure_CapturesRemediationError()
    {
        using var ctx = CreateContext(port: 11250, registerWorker: false);

        using var argsDoc = JsonDocument.Parse("""
        {
            "action": "launch",
            "port": 11250,
            "simulateServerFailure": true
        }
        """);

        var result = await ctx.ScrimmageHandler.HandleAsync(argsDoc.RootElement);
        result.IsError.Should().BeTrue();

        using var doc = JsonDocument.Parse(((TextContentItem)result.Content[0]).Text);
        var root = doc.RootElement;
        root.GetProperty("status").GetString().Should().Be("error");
        root.GetProperty("serverRunning").GetBoolean().Should().BeFalse();

        // Verify error captured in remediation service
        var recentErrors = ctx.RemediationService.GetRecentErrors(5);
        recentErrors.Should().NotBeEmpty();
        recentErrors.Any(e => e.Channel == "process" && e.Target == "server").Should().BeTrue();
    }
}
