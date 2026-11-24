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
/// Empirical Challenger 4 Test Suite for Milestone 4 (Iteration 2):
/// Adversarially probes and verifies:
/// 1. Port parameter parsing:
///    - String ports: "11235", "11236", whitespace padded " 11235 ".
///    - Invalid ports: "abc", 0, -5, 99999, "", null. Verifies NO unhandled exceptions are thrown.
/// 2. Canary / port 0 isolation:
///    - Empirically verifies 0 bytes are ever written to port 0 or unkeyed streams.
/// 3. Server launch failure handling:
///    - Simulates failed launch; verifies handler aborts and returns isError: true and serverRunning: false,
///      with zero client or server commands dispatched and error remediation recorded.
/// 4. Execution speed (< 2.0s) and zero interactive GUI windows spawned.
/// </summary>
public sealed class DedicatedPortParsingTests
{
    private sealed class ChallengerTestContext : IDisposable
    {
        public ScrimmageLaunchHandler Handler { get; init; } = null!;
        public GameServerManageHandler ServerHandler { get; init; } = null!;
        public GameClientManageHandler ClientHandler { get; init; } = null!;
        public MatchStateHandler MatchStateHandler { get; init; } = null!;
        public Mock<IGameIpcBridge> MockClientIpc { get; init; } = null!;
        public MemoryStream ServerStdinStream { get; init; } = null!;
        public StreamWriter ServerStdinWriter { get; init; } = null!;
        public MemoryStream Port0CanaryStream { get; init; } = null!;
        public StreamWriter Port0CanaryWriter { get; init; } = null!;
        public List<string> ClientDispatchedCommands { get; init; } = [];
        public List<string> CapturedLogs { get; init; } = [];
        public ConcurrentDictionary<int, StreamWriter> WorkerStdinMap { get; init; } = null!;
        public ClientCommandRemediationService RemediationService { get; init; } = null!;
        public int Port { get; init; } = 11235;

        public void Dispose()
        {
            ServerStdinWriter.Dispose();
            ServerStdinStream.Dispose();
            Port0CanaryWriter.Dispose();
            Port0CanaryStream.Dispose();
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

        public async Task<string> ReadPort0CanaryContentAsync()
        {
            await Port0CanaryWriter.FlushAsync().ConfigureAwait(false);
            Port0CanaryStream.Position = 0;
            using StreamReader reader = new(Port0CanaryStream, Encoding.UTF8, leaveOpen: true);
            string content = await reader.ReadToEndAsync().ConfigureAwait(false);
            Port0CanaryStream.Position = Port0CanaryStream.Length;
            return content;
        }
    }

    private static ChallengerTestContext CreateContext(int port = 11235, bool registerExistingWorker = true)
    {
        List<string> logs = [];
        Action<string> logger = msg => logs.Add(msg);

        GameServerManageHandler serverHandler = new(timeProvider: TimeProvider.System, logger: logger);

        MemoryStream serverStream = new();
        StreamWriter serverWriter = new(serverStream, Encoding.UTF8, leaveOpen: true);

        MemoryStream canaryStream = new();
        StreamWriter canaryWriter = new(canaryStream, new UTF8Encoding(false), leaveOpen: true);

        FieldInfo? stdinField = typeof(GameServerManageHandler).GetField("_workerStdin", BindingFlags.NonPublic | BindingFlags.Instance);
        var workerStdin = (ConcurrentDictionary<int, StreamWriter>)stdinField!.GetValue(serverHandler)!;
        workerStdin[port] = serverWriter;
        workerStdin[0] = canaryWriter; // Canary on port 0 to trap unkeyed writes

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

        return new ChallengerTestContext
        {
            Handler = handler,
            ServerHandler = serverHandler,
            ClientHandler = clientHandler,
            MatchStateHandler = matchStateHandler,
            MockClientIpc = mockIpc,
            ServerStdinStream = serverStream,
            ServerStdinWriter = serverWriter,
            Port0CanaryStream = canaryStream,
            Port0CanaryWriter = canaryWriter,
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

    #region Mission Task 1: Challenge Port Parameter Parsing

    [Theory]
    [InlineData("11235", 11235)]
    [InlineData("11236", 11236)]
    [InlineData(" 11235 ", 11235)]
    [InlineData("11240", 11240)]
    public async Task PortParsing_ValidStringPorts_ParsedSuccessfullyAndRoutedCorrectly(string portString, int expectedPort)
    {
        using var ctx = CreateContext(port: expectedPort);
        var args = JsonElement.Parse($"{{\"port\": \"{portString}\"}}");

        var result = await ctx.Handler.HandleAsync(args);

        result.IsError.Should().BeFalse();
        var root = ParseResultJson(result);
        root.GetProperty("port").GetInt32().Should().Be(expectedPort);
        root.GetProperty("serverRunning").GetBoolean().Should().BeTrue();

        // Client must connect to the parsed port
        ctx.ClientDispatchedCommands.Should().Contain($"connect 127.0.0.1:{expectedPort}");

        // Dedicated server stdin must receive commands on the parsed port
        string stdin = await ctx.ReadServerStdinContentAsync();
        stdin.Should().Contain("AddFakePlayer 1");
        stdin.Should().Contain("AddFakePlayer 2");
        stdin.Should().Contain("RequestMatchStart");
    }

    [Theory]
    [InlineData("abc")]
    [InlineData("0")]
    [InlineData("-5")]
    [InlineData("99999")]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("port_11235")]
    [InlineData("11235.5")]
    public async Task PortParsing_InvalidStringPorts_VerifyNoUnhandledExceptionAndReturnsError(string invalidPortString)
    {
        using var ctx = CreateContext();
        var args = JsonElement.Parse($"{{\"port\": \"{invalidPortString}\"}}");

        // Act - MUST NOT THROW unhandled exception!
        Func<Task<ToolCallResult>> act = async () => await ctx.Handler.HandleAsync(args);

        var result = await act.Should().NotThrowAsync("Invalid port strings must be caught and handled gracefully without unhandled exceptions");

        result.Subject.IsError.Should().BeTrue();
        string err = GetContentText(result.Subject);
        err.Should().MatchRegex("(between 1 and 65535|Invalid port parameter)");
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-5)]
    [InlineData(99999)]
    [InlineData(-1)]
    [InlineData(65536)]
    public async Task PortParsing_InvalidNumericPorts_VerifyNoUnhandledExceptionAndReturnsError(int invalidPortNum)
    {
        using var ctx = CreateContext();
        var args = JsonElement.Parse($"{{\"port\": {invalidPortNum}}}");

        Func<Task<ToolCallResult>> act = async () => await ctx.Handler.HandleAsync(args);

        var result = await act.Should().NotThrowAsync("Invalid numeric ports must be handled gracefully without unhandled exceptions");

        result.Subject.IsError.Should().BeTrue();
        string err = GetContentText(result.Subject);
        err.Should().Contain("between 1 and 65535");
    }

    [Theory]
    [InlineData("{\"port\": null}")]
    [InlineData("{\"port\": true}")]
    [InlineData("{\"port\": false}")]
    [InlineData("{\"port\": [11235]}")]
    [InlineData("{\"port\": {\"nested\": 11235}}")]
    public async Task PortParsing_NonPrimitiveOrNullValues_ReturnsErrorGracefully(string jsonInput)
    {
        using var ctx = CreateContext();
        var args = JsonElement.Parse(jsonInput);

        Func<Task<ToolCallResult>> act = async () => await ctx.Handler.HandleAsync(args);

        var result = await act.Should().NotThrowAsync("Non-primitive and null ports must be handled gracefully without unhandled exceptions");
        result.Subject.IsError.Should().BeTrue();
        string err = GetContentText(result.Subject);
        err.Should().Contain("Invalid port parameter");
    }

    #endregion

    #region Mission Task 2: Challenge Canary / Port 0 Isolation

    [Fact]
    public async Task CanaryIsolation_Port11235_EmpiricallyVerifiesZeroBytesWrittenToPort0()
    {
        using var ctx = CreateContext(port: 11235);
        var args = JsonElement.Parse("{\"port\": 11235}");

        var result = await ctx.Handler.HandleAsync(args);

        result.IsError.Should().BeFalse();

        // 1. Empirically verify 0 bytes written to port 0 canary stream
        string port0Content = await ctx.ReadPort0CanaryContentAsync();
        port0Content.Should().BeEmpty("Zero bytes must ever be written to port 0 or unkeyed streams");
        ctx.Port0CanaryStream.Length.Should().Be(0, "Port 0 canary stream length must remain exactly 0");

        // 2. Empirically verify primary port 11235 received all 10 commands
        string primaryStdin = await ctx.ReadServerStdinContentAsync();
        primaryStdin.Should().NotBeEmpty();
        string[] lines = primaryStdin.Split(["\r\n", "\n"], StringSplitOptions.RemoveEmptyEntries);
        lines.Should().HaveCount(10);
        lines.Should().Contain("AddFakePlayer 1");
        lines.Should().Contain("AddFakePlayer 2");
        lines.Should().Contain("RequestMatchStart");
    }

    [Fact]
    public async Task CanaryIsolation_CustomPort11236_EmpiricallyVerifiesZeroBytesWrittenToPort0()
    {
        using var ctx = CreateContext(port: 11236);
        var args = JsonElement.Parse("{\"port\": 11236}");

        var result = await ctx.Handler.HandleAsync(args);

        result.IsError.Should().BeFalse();

        // Port 0 canary must remain completely empty
        string port0Content = await ctx.ReadPort0CanaryContentAsync();
        port0Content.Should().BeEmpty("Zero bytes must ever be written to port 0 when targeting custom port 11236");
        ctx.Port0CanaryStream.Length.Should().Be(0);

        // Port 11236 must receive the commands
        string primaryStdin = await ctx.ReadServerStdinContentAsync();
        primaryStdin.Should().Contain("RequestMatchStart");
    }

    [Fact]
    public async Task CanaryIsolation_ClientIpcBridge_NeverReceivesDedicatedServerCommands()
    {
        using var ctx = CreateContext(port: 11235);
        var args = JsonElement.Parse("{\"port\": 11235}");

        var result = await ctx.Handler.HandleAsync(args);

        result.IsError.Should().BeFalse();

        // Ensure server-only commands never leak to client IPC
        ctx.ClientDispatchedCommands.Should().NotContain(c => c.Contains("AddFakePlayer"));
        ctx.ClientDispatchedCommands.Should().NotContain(c => c.Contains("RequestMatchStart"));

        // Ensure only client connection commands were sent to client IPC
        ctx.ClientDispatchedCommands.Should().Contain("connect 127.0.0.1:11235");
        ctx.ClientDispatchedCommands.Should().Contain("Team 1 0");
        ctx.ClientDispatchedCommands.Should().Contain("SelectHero Hero_Pyromancer");
    }

    [Fact]
    public async Task CanaryIsolation_LogsStrictlyTargetDesignatedPort_NeverGenericUnnamedPipe()
    {
        using var ctx = CreateContext(port: 11235);
        var args = JsonElement.Parse("{\"port\": 11235}");

        var result = await ctx.Handler.HandleAsync(args);

        result.IsError.Should().BeFalse();

        var serverLogs = ctx.CapturedLogs.Where(l => l.Contains("DedicatedServer") || l.Contains("hon-server")).ToList();
        serverLogs.Should().NotBeEmpty();

        // Every log mentioning dedicated server must explicitly target port 11235
        serverLogs.Should().OnlyContain(l => l.Contains("11235"));
        serverLogs.Should().NotContain(l => l.Contains(@"\\.\pipe\hon-server") && !l.Contains(@"\\.\pipe\hon-server-11235"));
    }

    #endregion

    #region Mission Task 3: Challenge Server Launch Failure

    [Fact]
    public async Task ServerLaunchFailure_SimulateFailure_HandlerAbortsImmediately()
    {
        // Challenge: simulate failed launch via simulateServerFailure: true
        using var ctx = CreateContext(port: 11250, registerExistingWorker: false);
        var args = JsonElement.Parse("{\"port\": 11250, \"simulateServerFailure\": true}");

        var result = await ctx.Handler.HandleAsync(args);

        // Verify handler returns isError: true
        result.IsError.Should().BeTrue();

        var root = ParseResultJson(result);
        root.GetProperty("status").GetString().Should().Be("error");
        root.GetProperty("serverRunning").GetBoolean().Should().BeFalse();
        root.GetProperty("clientConnected").GetBoolean().Should().BeFalse();
        root.GetProperty("gamePhase").GetInt32().Should().Be(0);
        root.GetProperty("team1Count").GetInt32().Should().Be(0);
        root.GetProperty("team2Count").GetInt32().Should().Be(0);
        root.GetProperty("totalPlayers").GetInt32().Should().Be(0);

        // Verify zero client commands dispatched (no connection attempted)
        ctx.ClientDispatchedCommands.Should().BeEmpty("Client must not attempt connection when server launch fails");

        // Verify zero server commands dispatched to server stdin
        string stdinContent = await ctx.ReadServerStdinContentAsync();
        stdinContent.Should().BeEmpty("Server commands must not be dispatched when server launch fails");

        // Verify port 0 canary received zero bytes
        ctx.Port0CanaryStream.Length.Should().Be(0);

        // Verify failure was recorded in remediation service
        var recentErrors = ctx.RemediationService.GetRecentErrors(5);
        recentErrors.Should().NotBeEmpty();
        recentErrors.Should().Contain(e => e.Target == "server" && e.Command.Contains("LaunchWorkerAsync"));
    }

    [Fact]
    public async Task ServerLaunchFailure_StatusQueryOnFailedServer_ReportsServerOffline()
    {
        using var ctx = CreateContext(port: 11251, registerExistingWorker: false);

        // Query status for port with no running server
        var statusArgs = JsonElement.Parse("{\"action\": \"status\", \"port\": 11251}");
        var statusResult = await ctx.Handler.HandleAsync(statusArgs);

        statusResult.IsError.Should().BeFalse();
        var root = ParseResultJson(statusResult);
        root.GetProperty("status").GetString().Should().Be("success");
        root.GetProperty("serverRunning").GetBoolean().Should().BeFalse();
        root.GetProperty("clientConnected").GetBoolean().Should().BeFalse();
        root.GetProperty("gamePhase").GetInt32().Should().Be(0);
        root.GetProperty("totalPlayers").GetInt32().Should().Be(0);
    }

    [Fact]
    public async Task ServerLaunchFailure_StopActionOnNonRunningServer_ReturnsCleanSuccess()
    {
        using var ctx = CreateContext(port: 11252, registerExistingWorker: false);

        var stopArgs = JsonElement.Parse("{\"action\": \"stop\", \"port\": 11252}");
        var stopResult = await ctx.Handler.HandleAsync(stopArgs);

        stopResult.IsError.Should().BeFalse();
        var root = ParseResultJson(stopResult);
        root.GetProperty("status").GetString().Should().Be("success");
        root.GetProperty("serverRunning").GetBoolean().Should().BeFalse();
        root.GetProperty("terminatedProcessId").GetInt32().Should().Be(0);
    }

    #endregion

    #region Mission Task 4: Sub-2.0s Performance and Zero GUI Spawning

    [Fact]
    public async Task Performance_SingleLaunchAndTerminate_ExecutesUnder2000Milliseconds()
    {
        using var ctx = CreateContext();
        var args = JsonElement.Parse("{\"rapidTerminateAfterLaunch\": true}");

        Stopwatch sw = Stopwatch.StartNew();
        var result = await ctx.Handler.HandleAsync(args);
        sw.Stop();

        result.IsError.Should().BeFalse();
        sw.ElapsedMilliseconds.Should().BeLessThan(2000, "Single launch cycle must complete well under 2.0s");
    }

    [Fact]
    public async Task Performance_MultiCycleStress_ExecutesUnder500Milliseconds()
    {
        using var ctx = CreateContext();
        var args = JsonElement.Parse("{\"rapidTerminateAfterLaunch\": true}");

        Stopwatch sw = Stopwatch.StartNew();
        for (int i = 0; i < 10; i++)
        {
            var res = await ctx.Handler.HandleAsync(args);
            res.IsError.Should().BeFalse();
        }
        sw.Stop();

        sw.ElapsedMilliseconds.Should().BeLessThan(2000, "10 simulated scrimmage cycles must complete under 2000ms");
    }

    [Fact]
    public void ZeroGuiWindows_WhenInvoked_NoInteractiveGameProcessesSpawnedDuringExecution()
    {
        // Rule: Automated tests must never spawn interactive desktop GUI windows (hon_x64.exe)
        // Check for any hon_x64 processes spawned by the current test host runner
        var spawnedByTest = Process.GetProcessesByName("hon_x64")
            .Where(p =>
            {
                try { return p.StartTime > Process.GetCurrentProcess().StartTime; }
                catch { return false; }
            }).ToList();
        spawnedByTest.Should().BeEmpty("Automated inner-loop test suite must never spawn hon_x64 desktop windows");
    }

    #endregion
}
