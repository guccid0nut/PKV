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
/// Empirical Challenger 5 Test Suite for Milestone 4 (Iteration 3):
/// 1. Stress-test rapid multi-cycle execution:
///    - Run 50 iterations of launch -> rapidTerminate and verify duration is well under 2.0s (and < 500ms).
///    - Run 50 iterations of explicit launch -> stop and verify duration and state consistency.
///    - Verify command dispatch counts and absence of worker leaks across iterations.
/// 2. Multi-port state synchronization:
///    - Run launch -> stop -> status multiple times across different ports (11235, 11236, 11237).
///    - Verify absolute stdin command routing isolation per port with zero crosstalk.
///    - Round-robin interleaved rapidTerminate across all three ports.
///    - Empirical investigation of concurrent multi-port test mode behavior.
/// 3. Zero GUI window spawning:
///    - Verify zero interactive desktop processes or GUI windows are spawned.
/// </summary>
public sealed class ScrimmageLifecycleStressTests
{
    private sealed class MultiPortTestContext : IDisposable
    {
        public ScrimmageLaunchHandler Handler { get; init; } = null!;
        public GameServerManageHandler ServerHandler { get; init; } = null!;
        public GameClientManageHandler ClientHandler { get; init; } = null!;
        public MatchStateHandler MatchStateHandler { get; init; } = null!;
        public Mock<IGameIpcBridge> MockClientIpc { get; init; } = null!;
        public Dictionary<int, MemoryStream> StdinStreams { get; init; } = [];
        public Dictionary<int, StreamWriter> StdinWriters { get; init; } = [];
        public List<string> DispatchedClientCommands { get; init; } = [];
        public List<string> CapturedLogs { get; init; } = [];
        public ClientCommandRemediationService RemediationService { get; init; } = null!;

        public void Dispose()
        {
            foreach (var writer in StdinWriters.Values)
            {
                writer.Dispose();
            }
            foreach (var stream in StdinStreams.Values)
            {
                stream.Dispose();
            }
        }

        public async Task<string> ReadStdinContentAsync(int port)
        {
            if (!StdinWriters.TryGetValue(port, out var writer) || !StdinStreams.TryGetValue(port, out var stream))
            {
                return string.Empty;
            }

            await writer.FlushAsync().ConfigureAwait(false);
            stream.Position = 0;
            using StreamReader reader = new(stream, Encoding.UTF8, leaveOpen: true);
            string content = await reader.ReadToEndAsync().ConfigureAwait(false);
            stream.Position = stream.Length;
            return content;
        }
    }

    private static MultiPortTestContext CreateContext(params int[] preconfiguredPorts)
    {
        List<string> logs = [];
        Action<string> logger = msg => logs.Add(msg);

        GameServerManageHandler serverHandler = new(timeProvider: TimeProvider.System, logger: logger);

        FieldInfo? stdinField = typeof(GameServerManageHandler).GetField("_workerStdin", BindingFlags.NonPublic | BindingFlags.Instance);
        var workerStdinMap = (ConcurrentDictionary<int, StreamWriter>)stdinField!.GetValue(serverHandler)!;

        Dictionary<int, MemoryStream> streams = [];
        Dictionary<int, StreamWriter> writers = [];

        int[] ports = preconfiguredPorts.Length > 0 ? preconfiguredPorts : [11235, 11236, 11237];
        foreach (int port in ports)
        {
            MemoryStream ms = new();
            StreamWriter sw = new(ms, Encoding.UTF8, leaveOpen: true);
            streams[port] = ms;
            writers[port] = sw;
            workerStdinMap[port] = sw;
        }

        List<string> clientCmds = [];
        Mock<IGameIpcBridge> mockIpc = new();
        mockIpc.SetupGet(i => i.IsConnected).Returns(true);
        mockIpc.Setup(i => i.GetEngineStateAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new EngineStateDto
            {
                ActiveMatchId = 11235,
                GamePhase = 3,
                GamePhaseName = "In-Game Combat (Phase 3)",
                CurrentMap = "caldavar",
                ConnectedPlayers = 10
            });

        mockIpc.Setup(i => i.ExecuteCommandAsync(It.IsAny<string>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .Returns((string cmd, int to, CancellationToken ct) =>
            {
                clientCmds.Add(cmd);
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

        return new MultiPortTestContext
        {
            Handler = handler,
            ServerHandler = serverHandler,
            ClientHandler = clientHandler,
            MatchStateHandler = matchStateHandler,
            MockClientIpc = mockIpc,
            StdinStreams = streams,
            StdinWriters = writers,
            DispatchedClientCommands = clientCmds,
            CapturedLogs = logs,
            RemediationService = remediation
        };
    }

    private static JsonElement ParseResult(ToolCallResult result)
    {
        if (result.Content.Count > 0 && result.Content[0] is TextContentItem textItem)
        {
            return JsonElement.Parse(textItem.Text);
        }
        return default;
    }

    #region Mission Task 1: Rapid Multi-Cycle Execution Stress Testing

    [Trait("Category", "Manual")]
    [Fact]
    public async Task StressTest_50Iterations_RapidTerminate_CompletesWellUnder2Seconds_AndUnder500Milliseconds()
    {
        if (!NexusTestEnvironment.IsManualTestEnabled) return;

        using var ctx = CreateContext(11235);
        var args = JsonElement.Parse("{\"port\": 11235, \"rapidTerminateAfterLaunch\": true}");

        // Warmup 1 iteration to isolate JIT compilation
        var warmupResult = await ctx.Handler.HandleAsync(args);
        warmupResult.IsError.Should().BeFalse();

        const int totalIterations = 50;
        List<double> iterationTimesMs = new(totalIterations);

        Stopwatch sw = Stopwatch.StartNew();

        for (int i = 0; i < totalIterations; i++)
        {
            Stopwatch iterSw = Stopwatch.StartNew();
            var result = await ctx.Handler.HandleAsync(args);
            iterSw.Stop();

            iterationTimesMs.Add(iterSw.Elapsed.TotalMilliseconds);

            result.IsError.Should().BeFalse($"Iteration {i + 1} must succeed.");
            var root = ParseResult(result);
            root.GetProperty("status").GetString().Should().Be("success");
            root.GetProperty("rapidTerminated").GetBoolean().Should().BeTrue();
            root.GetProperty("serverRunning").GetBoolean().Should().BeFalse();
            root.GetProperty("clientConnected").GetBoolean().Should().BeFalse();
            root.GetProperty("gamePhase").GetInt32().Should().Be(3);
            root.GetProperty("totalPlayers").GetInt32().Should().Be(10);
        }

        sw.Stop();
        double totalDurationMs = sw.Elapsed.TotalMilliseconds;

        // Verify total benchmark duration: must be well under 2.0s
        totalDurationMs.Should().BeLessThan(2000.0, "50 iterations must complete well under 2.0s per AGENTS.md inner loop rules");

        // Latency profiling
        double avgMs = iterationTimesMs.Average();
        double minMs = iterationTimesMs.Min();
        double maxMs = iterationTimesMs.Max();
        double p95Ms = iterationTimesMs.OrderBy(t => t).ElementAt((int)(totalIterations * 0.95));

        avgMs.Should().BeLessThan(10.0, $"Average latency per iteration should be under 10ms, actual: {avgMs:F2}ms");
        p95Ms.Should().BeLessThan(30.0, $"P95 latency should be under 30ms, actual: {p95Ms:F2}ms");

        // Verify server worker state was cleanly drained (0 leaked workers)
        var activeWorkers = ctx.ServerHandler.DiscoverRunningDedicatedServers();
        activeWorkers.Should().BeEmpty("All workers must be terminated and evicted after rapidTerminate cycles");

        // Verify command stream fidelity: 51 total launches (1 warmup + 50 loop) = 51 * 10 = 510 server commands
        string stdinContent = await ctx.ReadStdinContentAsync(11235);
        int fakePlayer1Count = stdinContent.Split('\n').Count(l => l.Trim() == "AddFakePlayer 1");
        int fakePlayer2Count = stdinContent.Split('\n').Count(l => l.Trim() == "AddFakePlayer 2");
        int requestMatchStartCount = stdinContent.Split('\n').Count(l => l.Trim() == "RequestMatchStart");

        fakePlayer1Count.Should().Be(204); // 51 * 4
        fakePlayer2Count.Should().Be(255); // 51 * 5
        requestMatchStartCount.Should().Be(51); // 51 * 1
    }

    [Trait("Category", "Manual")]
    [Fact]
    public async Task StressTest_50Iterations_ExplicitLaunchThenStop_CompletesUnder500Milliseconds()
    {
        if (!NexusTestEnvironment.IsManualTestEnabled) return;

        using var ctx = CreateContext(11235);
        var launchArgs = JsonElement.Parse("{\"port\": 11235, \"rapidTerminateAfterLaunch\": false}");
        var stopArgs = JsonElement.Parse("{\"action\": \"stop\", \"port\": 11235}");

        // Warmup 1 cycle
        var wLaunch = await ctx.Handler.HandleAsync(launchArgs);
        wLaunch.IsError.Should().BeFalse();
        var wStop = await ctx.Handler.HandleAsync(stopArgs);
        wStop.IsError.Should().BeFalse();

        const int iterations = 50;
        List<double> cycleTimesMs = new(iterations);

        Stopwatch totalSw = Stopwatch.StartNew();

        for (int i = 0; i < iterations; i++)
        {
            Stopwatch cycleSw = Stopwatch.StartNew();

            // 1. Launch
            var launchRes = await ctx.Handler.HandleAsync(launchArgs);
            launchRes.IsError.Should().BeFalse($"Iteration {i + 1} launch failed.");
            var launchDoc = ParseResult(launchRes);
            launchDoc.GetProperty("serverRunning").GetBoolean().Should().BeTrue();
            launchDoc.GetProperty("totalPlayers").GetInt32().Should().Be(10);

            // 2. Stop
            var stopRes = await ctx.Handler.HandleAsync(stopArgs);
            stopRes.IsError.Should().BeFalse($"Iteration {i + 1} stop failed.");
            var stopDoc = ParseResult(stopRes);
            stopDoc.GetProperty("serverRunning").GetBoolean().Should().BeFalse();
            stopDoc.GetProperty("totalPlayers").GetInt32().Should().Be(0);

            cycleSw.Stop();
            cycleTimesMs.Add(cycleSw.Elapsed.TotalMilliseconds);
        }

        totalSw.Stop();
        double totalMs = totalSw.Elapsed.TotalMilliseconds;

        totalMs.Should().BeLessThan(2000.0, "50 explicit launch/stop cycles must complete well under 2.0s");
        totalMs.Should().BeLessThan(500.0, $"50 explicit launch/stop cycles should complete under 500ms, actual: {totalMs:F2}ms");

        // Verify zero leaked workers
        ctx.ServerHandler.DiscoverRunningDedicatedServers().Should().BeEmpty();
    }

    #endregion

    #region Mission Task 2: Multi-Port State Synchronization (Ports 11235, 11236, 11237)

    [Fact]
    public async Task MultiPort_SequentialCycles_Ports11235_11236_11237_StateSynchronization()
    {
        using var ctx = CreateContext(11235, 11236, 11237);
        int[] targetPorts = [11235, 11236, 11237];
        const int cyclesPerPort = 5;

        for (int cycle = 1; cycle <= cyclesPerPort; cycle++)
        {
            foreach (int port in targetPorts)
            {
                var statusArgs = JsonElement.Parse($"{{\"action\": \"status\", \"port\": {port}}}");
                var launchArgs = JsonElement.Parse($"{{\"action\": \"launch\", \"port\": {port}, \"rapidTerminateAfterLaunch\": false}}");
                var stopArgs = JsonElement.Parse($"{{\"action\": \"stop\", \"port\": {port}}}");

                // 1. Initial status before launch: must be offline
                var initStatusRes = await ctx.Handler.HandleAsync(statusArgs);
                initStatusRes.IsError.Should().BeFalse();
                var initDoc = ParseResult(initStatusRes);
                initDoc.GetProperty("serverRunning").GetBoolean().Should().BeFalse($"Port {port} should be offline before launch in cycle {cycle}");
                initDoc.GetProperty("totalPlayers").GetInt32().Should().Be(0);
                initDoc.GetProperty("gamePhase").GetInt32().Should().Be(0);

                // 2. Launch
                var launchRes = await ctx.Handler.HandleAsync(launchArgs);
                launchRes.IsError.Should().BeFalse($"Port {port} launch should succeed in cycle {cycle}");
                var launchDoc = ParseResult(launchRes);
                launchDoc.GetProperty("serverRunning").GetBoolean().Should().BeTrue();
                launchDoc.GetProperty("port").GetInt32().Should().Be(port);
                launchDoc.GetProperty("gamePhase").GetInt32().Should().Be(3);
                launchDoc.GetProperty("totalPlayers").GetInt32().Should().Be(10);

                // 3. Status immediately after launch: must be online and synchronized
                var postLaunchStatusRes = await ctx.Handler.HandleAsync(statusArgs);
                postLaunchStatusRes.IsError.Should().BeFalse();
                var postLaunchDoc = ParseResult(postLaunchStatusRes);
                postLaunchDoc.GetProperty("serverRunning").GetBoolean().Should().BeTrue();
                postLaunchDoc.GetProperty("port").GetInt32().Should().Be(port);
                postLaunchDoc.GetProperty("gamePhase").GetInt32().Should().Be(3);
                postLaunchDoc.GetProperty("totalPlayers").GetInt32().Should().Be(10);

                // 4. Stop
                var stopRes = await ctx.Handler.HandleAsync(stopArgs);
                stopRes.IsError.Should().BeFalse($"Port {port} stop should succeed in cycle {cycle}");
                var stopDoc = ParseResult(stopRes);
                stopDoc.GetProperty("serverRunning").GetBoolean().Should().BeFalse();
                stopDoc.GetProperty("port").GetInt32().Should().Be(port);
                stopDoc.GetProperty("totalPlayers").GetInt32().Should().Be(0);

                // 5. Status immediately after stop: must report offline and 0 players
                var postStopStatusRes = await ctx.Handler.HandleAsync(statusArgs);
                postStopStatusRes.IsError.Should().BeFalse();
                var postStopDoc = ParseResult(postStopStatusRes);
                postStopDoc.GetProperty("serverRunning").GetBoolean().Should().BeFalse($"Port {port} status after stop must report offline");
                postStopDoc.GetProperty("totalPlayers").GetInt32().Should().Be(0);
                postStopDoc.GetProperty("gamePhase").GetInt32().Should().Be(0);
                postStopDoc.GetProperty("clientConnected").GetBoolean().Should().BeFalse();
            }
        }

        // Final sanity check: zero lingering workers across all ports
        ctx.ServerHandler.DiscoverRunningDedicatedServers().Should().BeEmpty();
    }

    [Fact]
    public async Task MultiPort_StdinCommandRouting_StrictPortIsolation()
    {
        using var ctx = CreateContext(11235, 11236, 11237);

        // Perform unequal iterations per port to verify strict routing isolation:
        // Port 11235: 2 cycles
        // Port 11236: 3 cycles
        // Port 11237: 4 cycles
        int[] portCounts = [2, 3, 4];
        int[] ports = [11235, 11236, 11237];

        for (int p = 0; p < ports.Length; p++)
        {
            int port = ports[p];
            int count = portCounts[p];

            for (int i = 0; i < count; i++)
            {
                var launchArgs = JsonElement.Parse($"{{\"action\": \"launch\", \"port\": {port}, \"rapidTerminateAfterLaunch\": true}}");
                var res = await ctx.Handler.HandleAsync(launchArgs);
                res.IsError.Should().BeFalse();
            }
        }

        // Verify stdin content on each port
        string stdin11235 = await ctx.ReadStdinContentAsync(11235);
        string stdin11236 = await ctx.ReadStdinContentAsync(11236);
        string stdin11237 = await ctx.ReadStdinContentAsync(11237);

        // Port 11235: 2 cycles * 10 commands = 20 commands (8 bot1, 10 bot2, 2 matchStart)
        stdin11235.Split('\n').Count(l => l.Trim() == "AddFakePlayer 1").Should().Be(8);
        stdin11235.Split('\n').Count(l => l.Trim() == "AddFakePlayer 2").Should().Be(10);
        stdin11235.Split('\n').Count(l => l.Trim() == "RequestMatchStart").Should().Be(2);

        // Port 11236: 3 cycles * 10 commands = 30 commands (12 bot1, 15 bot2, 3 matchStart)
        stdin11236.Split('\n').Count(l => l.Trim() == "AddFakePlayer 1").Should().Be(12);
        stdin11236.Split('\n').Count(l => l.Trim() == "AddFakePlayer 2").Should().Be(15);
        stdin11236.Split('\n').Count(l => l.Trim() == "RequestMatchStart").Should().Be(3);

        // Port 11237: 4 cycles * 10 commands = 40 commands (16 bot1, 20 bot2, 4 matchStart)
        stdin11237.Split('\n').Count(l => l.Trim() == "AddFakePlayer 1").Should().Be(16);
        stdin11237.Split('\n').Count(l => l.Trim() == "AddFakePlayer 2").Should().Be(20);
        stdin11237.Split('\n').Count(l => l.Trim() == "RequestMatchStart").Should().Be(4);
    }

    [Fact]
    public async Task MultiPort_RoundRobinInterleaved_RapidTerminate_StateCleanAfterEveryCall()
    {
        using var ctx = CreateContext(11235, 11236, 11237);
        int[] ports = [11235, 11236, 11237];

        const int totalRounds = 10; // 30 total executions

        Stopwatch sw = Stopwatch.StartNew();

        for (int r = 0; r < totalRounds; r++)
        {
            foreach (int port in ports)
            {
                var args = JsonElement.Parse($"{{\"port\": {port}, \"rapidTerminateAfterLaunch\": true}}");
                var res = await ctx.Handler.HandleAsync(args);

                res.IsError.Should().BeFalse();
                var doc = ParseResult(res);
                doc.GetProperty("port").GetInt32().Should().Be(port);
                doc.GetProperty("rapidTerminated").GetBoolean().Should().BeTrue();
                doc.GetProperty("serverRunning").GetBoolean().Should().BeFalse();

                // Confirm that server manager state has 0 workers left immediately after each call
                ctx.ServerHandler.DiscoverRunningDedicatedServers().Should().BeEmpty();
            }
        }

        sw.Stop();
        sw.Elapsed.TotalMilliseconds.Should().BeLessThan(2000.0, "30 round-robin rapidTerminate calls must complete under 2.0s");
    }

    [Fact]
    public async Task MultiPort_ConcurrentLaunch_DocumentsTestHarnessSingleWorkerKeyingBehavior()
    {
        // Adversarial Exploration:
        // In unit test mode (IsTestExecution = true), GameServerManageHandler simulates workers
        // using simulatedPid = Environment.ProcessId.
        // If port 11235 is launched without stopping, and then port 11236 is launched,
        // _trackedWorkers[Environment.ProcessId] is reassigned to port 11236.
        // This test empirically documents this behavior.
        using var ctx = CreateContext(11235, 11236);

        var launch11235 = JsonElement.Parse("{\"port\": 11235, \"rapidTerminateAfterLaunch\": false}");
        var launch11236 = JsonElement.Parse("{\"port\": 11236, \"rapidTerminateAfterLaunch\": false}");

        // 1. Launch 11235
        var res1 = await ctx.Handler.HandleAsync(launch11235);
        res1.IsError.Should().BeFalse();
        var doc1 = ParseResult(res1);
        doc1.GetProperty("serverRunning").GetBoolean().Should().BeTrue();

        // Status of 11235 is active
        var status1Res = await ctx.Handler.HandleAsync(JsonElement.Parse("{\"action\": \"status\", \"port\": 11235}"));
        ParseResult(status1Res).GetProperty("serverRunning").GetBoolean().Should().BeTrue();

        // 2. Launch 11236 concurrently without stopping 11235
        var res2 = await ctx.Handler.HandleAsync(launch11236);
        res2.IsError.Should().BeFalse();
        var doc2 = ParseResult(res2);
        doc2.GetProperty("serverRunning").GetBoolean().Should().BeTrue();

        // Status of 11236 is active
        var status2Res = await ctx.Handler.HandleAsync(JsonElement.Parse("{\"action\": \"status\", \"port\": 11236}"));
        ParseResult(status2Res).GetProperty("serverRunning").GetBoolean().Should().BeTrue();

        // 3. Status of 11235 now reports serverRunning: false in test mode because
        // _trackedWorkers is keyed by PID (Environment.ProcessId)
        var status1AfterRes = await ctx.Handler.HandleAsync(JsonElement.Parse("{\"action\": \"status\", \"port\": 11235}"));
        var doc1After = ParseResult(status1AfterRes);
        doc1After.GetProperty("serverRunning").GetBoolean().Should().BeFalse(
            "In test execution, simulated workers share Environment.ProcessId, so launching 11236 reassigns the single simulated worker slot");

        // Clean up 11236
        await ctx.Handler.HandleAsync(JsonElement.Parse("{\"action\": \"stop\", \"port\": 11236}"));
        ctx.ServerHandler.DiscoverRunningDedicatedServers().Should().BeEmpty();
    }

    #endregion

    #region Mission Task 3: Zero GUI Popups and Window Spawning Verification

    [Fact]
    public async Task ZeroDesktopGui_MultiCycleExecution_NeverSpawnsInteractiveProcesses()
    {
        // Snapshot baseline processes
        int initialHonProcesses = Process.GetProcessesByName("hon_x64").Length;
        int initialReplayStudioProcesses = Process.GetProcessesByName("ReplayStudio.GUI").Length;

        using var ctx = CreateContext(11235, 11236, 11237);

        // Execute multiple rapid cycles across ports
        for (int i = 0; i < 15; i++)
        {
            int targetPort = 11235 + (i % 3);
            var args = JsonElement.Parse($"{{\"port\": {targetPort}, \"rapidTerminateAfterLaunch\": true}}");
            var result = await ctx.Handler.HandleAsync(args);
            result.IsError.Should().BeFalse();
        }

        // Verify zero new GUI processes were launched
        int finalHonProcesses = Process.GetProcessesByName("hon_x64").Length;
        int finalReplayStudioProcesses = Process.GetProcessesByName("ReplayStudio.GUI").Length;

        finalHonProcesses.Should().Be(initialHonProcesses, "hon_x64 desktop process count must not increase during test execution");
        finalReplayStudioProcesses.Should().Be(initialReplayStudioProcesses, "ReplayStudio.GUI desktop process count must not increase during test execution");
    }

    #endregion
}
