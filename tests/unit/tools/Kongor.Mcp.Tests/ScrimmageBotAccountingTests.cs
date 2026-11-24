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
/// Empirical Adversarial Challenger Test Suite for Milestone 4 (Automated 5v5 Scrimmage Orchestrator).
/// Adversarially challenges:
/// 1. Bot accounting and slot balancing:
///    - Custom bot counts: e.g. team1Bots: 2, team2Bots: 3 -> totalPlayers: 6, team1Count: 3, team2Count: 3.
///    - autoConnect: false -> human slot is NOT allocated and totalPlayers = team1Bots + team2Bots.
///    - AddFakePlayer 1 is dispatched exactly team1Bots times and AddFakePlayer 2 is dispatched exactly team2Bots times.
///    - Edge case bot counts: 0v0, unbalanced 1v4, asymmetric, negative values clamped to 0.
/// 2. Port isolation and multi-server routing:
///    - Strict routing of console commands to specified port (default 11235 vs non-default 11236, 11240, 65535).
///    - Verification that non-targeted port streams receive zero commands (zero cross-talk).
///    - Client connection command includes exact target port (connect 127.0.0.1:{port}).
///    - Port validation rejecting 0, negative, and > 65535.
/// 3. Inner-loop test execution threshold (< 2.0s) and zero GUI window spawning.
/// </summary>
public sealed class ScrimmageBotAccountingTests
{
    private sealed class ScrimmageHarness : IDisposable
    {
        public ScrimmageLaunchHandler Handler { get; init; } = null!;
        public GameServerManageHandler ServerHandler { get; init; } = null!;
        public GameClientManageHandler ClientHandler { get; init; } = null!;
        public Mock<IGameIpcBridge> MockClientIpc { get; init; } = null!;
        public MemoryStream ServerStdinStream { get; init; } = null!;
        public StreamWriter ServerStdinWriter { get; init; } = null!;
        public List<string> ClientCommands { get; init; } = [];
        public ClientCommandRemediationService RemediationService { get; init; } = null!;
        public int Port { get; init; } = 11235;

        // Multi-port cross-talk testing support
        public MemoryStream? SecondaryServerStdinStream { get; init; }
        public StreamWriter? SecondaryServerStdinWriter { get; init; }
        public int SecondaryPort { get; init; }

        public void Dispose()
        {
            ServerStdinWriter.Dispose();
            ServerStdinStream.Dispose();
            SecondaryServerStdinWriter?.Dispose();
            SecondaryServerStdinStream?.Dispose();
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

        public async Task<string> ReadSecondaryServerStdinContentAsync()
        {
            if (SecondaryServerStdinWriter == null || SecondaryServerStdinStream == null)
                return string.Empty;

            await SecondaryServerStdinWriter.FlushAsync().ConfigureAwait(false);
            SecondaryServerStdinStream.Position = 0;
            using StreamReader reader = new(SecondaryServerStdinStream, Encoding.UTF8, leaveOpen: true);
            string content = await reader.ReadToEndAsync().ConfigureAwait(false);
            SecondaryServerStdinStream.Position = SecondaryServerStdinStream.Length;
            return content;
        }
    }

    private static ScrimmageHarness CreateHarness(int port = 11235, int? secondaryPort = null, bool registerWorker = true)
    {
        GameServerManageHandler serverHandler = new(timeProvider: TimeProvider.System, logger: _ => { });

        MemoryStream serverStream = new();
        StreamWriter serverWriter = new(serverStream, Encoding.UTF8, leaveOpen: true);

        FieldInfo? stdinField = typeof(GameServerManageHandler).GetField("_workerStdin", BindingFlags.NonPublic | BindingFlags.Instance);
        var workerStdin = (ConcurrentDictionary<int, StreamWriter>)stdinField!.GetValue(serverHandler)!;
        workerStdin[port] = serverWriter;

        MemoryStream? secStream = null;
        StreamWriter? secWriter = null;
        if (secondaryPort.HasValue)
        {
            secStream = new MemoryStream();
            secWriter = new StreamWriter(secStream, Encoding.UTF8, leaveOpen: true);
            workerStdin[secondaryPort.Value] = secWriter;
        }

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

            if (secondaryPort.HasValue)
            {
                trackedWorkers[Environment.ProcessId + 1000] = new GameServerWorkerInfo
                {
                    ProcessId = Environment.ProcessId + 1000,
                    Port = secondaryPort.Value,
                    ServerName = "Project KONGOR Secondary Dedicated Server",
                    Map = "caldavar",
                    Responding = true
                };
            }
        }

        List<string> clientCmds = [];
        Mock<IGameIpcBridge> mockIpc = new();
        mockIpc.SetupGet(i => i.IsConnected).Returns(true);
        mockIpc.Setup(i => i.ExecuteCommandAsync(It.IsAny<string>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .Returns((string cmd, int to, CancellationToken ct) =>
            {
                clientCmds.Add(cmd);
                return Task.FromResult("OK");
            });

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
            timeProvider: TimeProvider.System,
            logger: _ => { });

        return new ScrimmageHarness
        {
            Handler = handler,
            ServerHandler = serverHandler,
            ClientHandler = clientHandler,
            MockClientIpc = mockIpc,
            ServerStdinStream = serverStream,
            ServerStdinWriter = serverWriter,
            ClientCommands = clientCmds,
            RemediationService = remediation,
            Port = port,
            SecondaryPort = secondaryPort ?? 0,
            SecondaryServerStdinStream = secStream,
            SecondaryServerStdinWriter = secWriter
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

    private static JsonElement ParseJson(ToolCallResult result)
    {
        return JsonElement.Parse(GetContentText(result));
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

    #region Challenge 1: Bot Accounting and Slot Balancing

    [Fact]
    public async Task BotAccounting_CustomBotCounts_2Legion_3Hellbourne_BalancesTo6Total()
    {
        // User Requirement: team1Bots: 2, team2Bots: 3 -> verify totalPlayers = 6, team1Count = 3, team2Count = 3.
        using var harness = CreateHarness();
        var args = JsonElement.Parse("{\"team1Bots\": 2, \"team2Bots\": 3}");

        var result = await harness.Handler.HandleAsync(args);

        result.IsError.Should().BeFalse();
        var json = ParseJson(result);
        json.GetProperty("team1Count").GetInt32().Should().Be(3, "1 human player + 2 Legion bots = 3");
        json.GetProperty("team2Count").GetInt32().Should().Be(3, "3 Hellbourne bots = 3");
        json.GetProperty("totalPlayers").GetInt32().Should().Be(6, "3 Legion + 3 Hellbourne = 6 players total");

        string stdin = await harness.ReadServerStdinContentAsync();
        CountOccurrences(stdin, "AddFakePlayer 1").Should().Be(2, "AddFakePlayer 1 must be dispatched exactly team1Bots (2) times");
        CountOccurrences(stdin, "AddFakePlayer 2").Should().Be(3, "AddFakePlayer 2 must be dispatched exactly team2Bots (3) times");
    }

    [Fact]
    public async Task BotAccounting_AutoConnectFalse_HumanSlotNotAllocated_TotalIsSumOfBots()
    {
        // User Requirement: autoConnect: false -> verify human slot is not allocated and totalPlayers = team1Bots + team2Bots.
        using var harness = CreateHarness();
        var args = JsonElement.Parse("{\"team1Bots\": 2, \"team2Bots\": 3, \"autoConnect\": false}");

        var result = await harness.Handler.HandleAsync(args);

        result.IsError.Should().BeFalse();
        var json = ParseJson(result);
        json.GetProperty("clientConnected").GetBoolean().Should().BeFalse("Local client should not connect when autoConnect is false");
        json.GetProperty("team1Count").GetInt32().Should().Be(2, "Team 1 has only the 2 bots without human player");
        json.GetProperty("team2Count").GetInt32().Should().Be(3, "Team 2 has only the 3 bots");
        json.GetProperty("totalPlayers").GetInt32().Should().Be(5, "Total players must be exactly team1Bots (2) + team2Bots (3) = 5");

        // Verify client IPC received ZERO commands (no connect, no Team 1 0, no SelectHero)
        harness.ClientCommands.Should().BeEmpty("Client commands must not be dispatched when autoConnect is false");

        // Verify server stdin still received exact bot counts
        string stdin = await harness.ReadServerStdinContentAsync();
        CountOccurrences(stdin, "AddFakePlayer 1").Should().Be(2);
        CountOccurrences(stdin, "AddFakePlayer 2").Should().Be(3);
    }

    [Fact]
    public async Task BotAccounting_Default5v5_DispatchesExactly4LegionBotsAnd5HellbourneBots()
    {
        // Standard 5v5 default: 1 human + 4 bots on Team 1, 5 bots on Team 2
        using var harness = CreateHarness();
        var args = JsonElement.Parse("{}");

        var result = await harness.Handler.HandleAsync(args);

        result.IsError.Should().BeFalse();
        var json = ParseJson(result);
        json.GetProperty("team1Count").GetInt32().Should().Be(5);
        json.GetProperty("team2Count").GetInt32().Should().Be(5);
        json.GetProperty("totalPlayers").GetInt32().Should().Be(10);

        string stdin = await harness.ReadServerStdinContentAsync();
        CountOccurrences(stdin, "AddFakePlayer 1").Should().Be(4, "AddFakePlayer 1 dispatched exactly 4 times");
        CountOccurrences(stdin, "AddFakePlayer 2").Should().Be(5, "AddFakePlayer 2 dispatched exactly 5 times");
    }

    [Fact]
    public async Task BotAccounting_ZeroBotsBothTeams_AutoConnectFalse_ZeroTotalPlayers()
    {
        // Edge case: 0 bots, autoConnect: false -> 0 total players
        using var harness = CreateHarness();
        var args = JsonElement.Parse("{\"team1Bots\": 0, \"team2Bots\": 0, \"autoConnect\": false}");

        var result = await harness.Handler.HandleAsync(args);

        result.IsError.Should().BeFalse();
        var json = ParseJson(result);
        json.GetProperty("team1Count").GetInt32().Should().Be(0);
        json.GetProperty("team2Count").GetInt32().Should().Be(0);
        json.GetProperty("totalPlayers").GetInt32().Should().Be(0);

        string stdin = await harness.ReadServerStdinContentAsync();
        CountOccurrences(stdin, "AddFakePlayer 1").Should().Be(0);
        CountOccurrences(stdin, "AddFakePlayer 2").Should().Be(0);
    }

    [Fact]
    public async Task BotAccounting_NegativeBotCounts_ClampedSafelyToZero()
    {
        // Adversarial input: negative bot counts should be clamped via Math.Max(0, count)
        using var harness = CreateHarness();
        var args = JsonElement.Parse("{\"team1Bots\": -5, \"team2Bots\": -10, \"autoConnect\": true}");

        var result = await harness.Handler.HandleAsync(args);

        result.IsError.Should().BeFalse();
        var json = ParseJson(result);
        json.GetProperty("team1Count").GetInt32().Should().Be(1, "1 human + 0 clamped bots = 1");
        json.GetProperty("team2Count").GetInt32().Should().Be(0, "0 clamped bots = 0");
        json.GetProperty("totalPlayers").GetInt32().Should().Be(1);

        string stdin = await harness.ReadServerStdinContentAsync();
        CountOccurrences(stdin, "AddFakePlayer 1").Should().Be(0);
        CountOccurrences(stdin, "AddFakePlayer 2").Should().Be(0);
    }

    [Fact]
    public async Task BotAccounting_AsymmetricUnbalancedCounts_CalculatesCorrectly()
    {
        // Asymmetric match: 1 human + 1 bot on Team 1 vs 4 bots on Team 2
        using var harness = CreateHarness();
        var args = JsonElement.Parse("{\"team1Bots\": 1, \"team2Bots\": 4, \"autoConnect\": true}");

        var result = await harness.Handler.HandleAsync(args);

        result.IsError.Should().BeFalse();
        var json = ParseJson(result);
        json.GetProperty("team1Count").GetInt32().Should().Be(2);
        json.GetProperty("team2Count").GetInt32().Should().Be(4);
        json.GetProperty("totalPlayers").GetInt32().Should().Be(6);

        string stdin = await harness.ReadServerStdinContentAsync();
        CountOccurrences(stdin, "AddFakePlayer 1").Should().Be(1);
        CountOccurrences(stdin, "AddFakePlayer 2").Should().Be(4);
    }

    #endregion

    #region Challenge 2: Port Isolation and Multi-Server Routing

    [Fact]
    public async Task PortIsolation_NonDefaultPort_StrictlyRoutesCommandsToSpecifiedPort()
    {
        // User Requirement: verify all server commands strictly routed to specified port (e.g. 11236)
        int customPort = 11236;
        using var harness = CreateHarness(port: customPort);
        var args = JsonElement.Parse($"{{\"port\": {customPort}}}");

        var result = await harness.Handler.HandleAsync(args);

        result.IsError.Should().BeFalse();
        var json = ParseJson(result);
        json.GetProperty("port").GetInt32().Should().Be(customPort);

        // Verify client connect string contains exact custom port
        harness.ClientCommands.Should().Contain($"connect 127.0.0.1:{customPort}");

        // Verify dedicated server stdin stream received the commands on that port
        string stdin = await harness.ReadServerStdinContentAsync();
        stdin.Should().Contain("AddFakePlayer 1");
        stdin.Should().Contain("AddFakePlayer 2");
        stdin.Should().Contain("RequestMatchStart");
    }

    [Fact]
    public async Task PortIsolation_MultiServerEnvironment_ZeroCrossTalkBetweenPorts()
    {
        // Adversarial challenge: two ports active simultaneously (11235 and 11236).
        // Commands dispatched to port 11236 MUST NOT leak into port 11235!
        int primaryPort = 11236;
        int isolatedPort = 11235;
        using var harness = CreateHarness(port: primaryPort, secondaryPort: isolatedPort);

        var args = JsonElement.Parse($"{{\"port\": {primaryPort}, \"team1Bots\": 2, \"team2Bots\": 3}}");

        var result = await harness.Handler.HandleAsync(args);

        result.IsError.Should().BeFalse();

        // 1. Primary port 11236 MUST receive all bot and match start commands
        string primaryStdin = await harness.ReadServerStdinContentAsync();
        primaryStdin.Should().Contain("AddFakePlayer 1");
        primaryStdin.Should().Contain("AddFakePlayer 2");
        primaryStdin.Should().Contain("RequestMatchStart");
        CountOccurrences(primaryStdin, "AddFakePlayer 1").Should().Be(2);
        CountOccurrences(primaryStdin, "AddFakePlayer 2").Should().Be(3);

        // 2. Isolated secondary port 11235 MUST NOT receive any commands (zero cross-talk)
        string secondaryStdin = await harness.ReadSecondaryServerStdinContentAsync();
        secondaryStdin.Should().BeEmpty("Port 11235 should receive ZERO commands when targeting port 11236");
    }

    [Fact]
    public async Task PortIsolation_BoundaryPort65535_AcceptedAndRoutedProperly()
    {
        // Boundary check: maximum valid UDP port 65535
        int maxPort = 65535;
        using var harness = CreateHarness(port: maxPort);
        var args = JsonElement.Parse($"{{\"port\": {maxPort}}}");

        var result = await harness.Handler.HandleAsync(args);

        result.IsError.Should().BeFalse();
        var json = ParseJson(result);
        json.GetProperty("port").GetInt32().Should().Be(maxPort);
        harness.ClientCommands.Should().Contain($"connect 127.0.0.1:{maxPort}");
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(-65535)]
    [InlineData(65536)]
    [InlineData(100000)]
    public async Task PortIsolation_InvalidPorts_RejectedWithValidationErrors(int invalidPort)
    {
        // Adversarial challenge: port numbers out of valid UDP range [1, 65535] must fail immediately
        using var harness = CreateHarness();
        var args = JsonElement.Parse($"{{\"port\": {invalidPort}}}");

        var result = await harness.Handler.HandleAsync(args);

        result.IsError.Should().BeTrue();
        string err = GetContentText(result);
        err.Should().Contain("between 1 and 65535");
    }

    #endregion

    #region Challenge 3: Performance, Phase 3 Confirmation & Zero GUI Windows

    [Fact]
    public async Task Execution_SingleLaunch_CompletesInSubSecond_InnerLoopCompliant()
    {
        // Benchmark: single launch execution must be well under 2.0s inner-loop threshold
        using var harness = CreateHarness();
        var args = JsonElement.Parse("{\"rapidTerminateAfterLaunch\": false}");

        Stopwatch sw = Stopwatch.StartNew();
        var result = await harness.Handler.HandleAsync(args);
        sw.Stop();

        result.IsError.Should().BeFalse();
        sw.ElapsedMilliseconds.Should().BeLessThan(2000, "Single launch should complete in under 2000ms");
    }

    [Fact]
    public async Task Execution_ZeroGuiWindows_SuppressesDesktopPopups()
    {
        // Rule: Tests must never spawn external interactive desktop windows
        // Inspect running desktop windows or processes before and after
        var initialProcessIds = Process.GetProcessesByName("hon_x64").Select(p => p.Id).ToHashSet();

        using var harness = CreateHarness();
        var args = JsonElement.Parse("{\"rapidTerminateAfterLaunch\": true}");

        var result = await harness.Handler.HandleAsync(args);
        result.IsError.Should().BeFalse();

        var spawnedAfter = Process.GetProcessesByName("hon_x64").Where(p => !initialProcessIds.Contains(p.Id)).ToList();
        spawnedAfter.Should().BeEmpty("No lingering hon_x64 GUI processes should be created during unit test execution");
    }

    [Fact]
    public async Task StatusQuery_SessionReflection_AccurateSlotState()
    {
        // Challenge: query status on active session reflects exact custom bot counts
        using var harness = CreateHarness(port: 11238);
        var launchArgs = JsonElement.Parse("{\"port\": 11238, \"team1Bots\": 3, \"team2Bots\": 2, \"hero\": \"Hero_Chronos\"}");
        await harness.Handler.HandleAsync(launchArgs);

        var statusArgs = JsonElement.Parse("{\"action\": \"status\", \"port\": 11238}");
        var statusRes = await harness.Handler.HandleAsync(statusArgs);

        statusRes.IsError.Should().BeFalse();
        var json = ParseJson(statusRes);
        json.GetProperty("port").GetInt32().Should().Be(11238);
        json.GetProperty("team1Count").GetInt32().Should().Be(4, "1 human + 3 bots");
        json.GetProperty("team2Count").GetInt32().Should().Be(2, "2 bots");
        json.GetProperty("totalPlayers").GetInt32().Should().Be(6);
        json.GetProperty("hero").GetString().Should().Be("Hero_Chronos");
        json.GetProperty("gamePhase").GetInt32().Should().Be(3);
    }

    #endregion
}
