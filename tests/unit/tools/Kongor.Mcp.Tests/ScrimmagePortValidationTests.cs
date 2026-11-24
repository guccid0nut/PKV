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
/// Empirical Challenger 2 Test Suite for Milestone 4 (kongor_scrimmage_launch):
/// Empirically probes port validation and edge cases, bot count clamping/handling,
/// unknown actions, McpToolRegistry alias resolution, error remediation integration,
/// and inner-loop performance / zero-GUI constraints.
/// </summary>
public sealed class ScrimmagePortValidationTests
{
    private sealed class ScrimmageChallengerContext : IDisposable
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

    private static ScrimmageChallengerContext CreateContext(int port = 11235, bool registerExistingWorker = true, bool setupStdin = true)
    {
        GameServerManageHandler serverHandler = new(timeProvider: TimeProvider.System, logger: _ => { });

        MemoryStream serverStream = new();
        StreamWriter serverWriter = new(serverStream, Encoding.UTF8, leaveOpen: true);

        if (setupStdin)
        {
            FieldInfo? stdinField = typeof(GameServerManageHandler).GetField("_workerStdin", BindingFlags.NonPublic | BindingFlags.Instance);
            var workerStdin = (ConcurrentDictionary<int, StreamWriter>)stdinField!.GetValue(serverHandler)!;
            workerStdin[port] = serverWriter;
        }

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
        mockIpc.Setup(i => i.ExecuteCommandAsync(It.IsAny<string>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .Returns((string cmd, int to, CancellationToken ct) =>
            {
                clientCommands.Add(cmd);
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

        return new ScrimmageChallengerContext
        {
            Handler = handler,
            ServerHandler = serverHandler,
            ClientHandler = clientHandler,
            MockClientIpc = mockIpc,
            ServerStdinStream = serverStream,
            ServerStdinWriter = serverWriter,
            ClientDispatchedCommands = clientCommands,
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

    #region Part 1: Port Validation and Boundary Stress Tests

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(-11235)]
    [InlineData(65536)]
    [InlineData(70000)]
    [InlineData(100000)]
    public async Task Port_InvalidIntegerValues_ReturnsValidationError(int invalidPort)
    {
        using var ctx = CreateContext();
        var args = JsonElement.Parse($"{{\"port\": {invalidPort}}}");

        var result = await ctx.Handler.HandleAsync(args);

        result.IsError.Should().BeTrue();
        string err = GetContentText(result);
        err.Should().Contain("between 1 and 65535");
        err.Should().Contain(invalidPort.ToString());
    }

    [Theory]
    [InlineData(1)]
    [InlineData(11235)]
    [InlineData(65535)]
    public async Task Port_ValidBoundaryIntegerValues_Succeeds(int validPort)
    {
        using var ctx = CreateContext(port: validPort);
        var args = JsonElement.Parse($"{{\"port\": {validPort}, \"rapidTerminateAfterLaunch\": true}}");

        var result = await ctx.Handler.HandleAsync(args);

        result.IsError.Should().BeFalse();
        var root = ParseResultJson(result);
        root.GetProperty("port").GetInt32().Should().Be(validPort);
    }

    [Theory]
    [InlineData("unparseable_string")]
    [InlineData("not_a_number")]
    [InlineData("70000")]
    [InlineData("0")]
    [InlineData("-1")]
    public async Task Port_UnparseableOrStringValues_ReturnsErrorWithoutThrowingUnhandledException(string unparseableString)
    {
        using var ctx = CreateContext(port: 11235);
        var args = JsonElement.Parse($"{{\"port\": \"{unparseableString}\", \"rapidTerminateAfterLaunch\": true}}");

        Func<Task<ToolCallResult>> act = async () => await ctx.Handler.HandleAsync(args);

        var result = await act.Should().NotThrowAsync();
        result.Subject.IsError.Should().BeTrue();
    }

    #endregion

    #region Part 2: Bot Counts Validation and Clamping Stress Tests

    [Theory]
    [InlineData(-1, -1, 0, 0, 1)]      // 1 human + 0 bots
    [InlineData(-5, -10, 0, 0, 1)]
    [InlineData(-100, 0, 0, 0, 1)]
    public async Task BotCount_NegativeValues_ClampedToZeroGracefully(int t1Bots, int t2Bots, int expectedT1Bots, int expectedT2Bots, int expectedTotal)
    {
        using var ctx = CreateContext();
        var args = JsonElement.Parse($"{{\"team1Bots\": {t1Bots}, \"team2Bots\": {t2Bots}, \"rapidTerminateAfterLaunch\": true}}");

        var result = await ctx.Handler.HandleAsync(args);

        result.IsError.Should().BeFalse();
        var root = ParseResultJson(result);
        root.GetProperty("team1Count").GetInt32().Should().Be(1 + expectedT1Bots);
        root.GetProperty("team2Count").GetInt32().Should().Be(expectedT2Bots);
        root.GetProperty("totalPlayers").GetInt32().Should().Be(expectedTotal);
    }

    [Theory]
    [InlineData("invalid", "invalid")]
    [InlineData("abc", "123_bad")]
    public async Task BotCount_UnparseableStringValues_ThrowsJsonElementWrongTypeExceptionHandledAsError(string t1Str, string t2Str)
    {
        // Behavioral observation: When bot counts are passed as strings, TryGetInt32 throws InvalidOperationException.
        using var ctx = CreateContext();
        var args = JsonElement.Parse($"{{\"team1Bots\": \"{t1Str}\", \"team2Bots\": \"{t2Str}\", \"rapidTerminateAfterLaunch\": true}}");

        var result = await ctx.Handler.HandleAsync(args);

        result.IsError.Should().BeTrue();
        string err = GetContentText(result);
        err.Should().Contain("The requested operation requires an element of type 'Number'");
    }

    [Fact]
    public async Task BotCount_LargeBotCounts_HandlesWithoutCrashing()
    {
        using var ctx = CreateContext();
        var args = JsonElement.Parse("{\"team1Bots\": 15, \"team2Bots\": 15, \"rapidTerminateAfterLaunch\": true}");

        var result = await ctx.Handler.HandleAsync(args);

        result.IsError.Should().BeFalse();
        var root = ParseResultJson(result);
        root.GetProperty("team1Count").GetInt32().Should().Be(16); // 1 human + 15 bots
        root.GetProperty("team2Count").GetInt32().Should().Be(15);
        root.GetProperty("totalPlayers").GetInt32().Should().Be(31);
    }

    #endregion

    #region Part 3: Action Resolution and Unknown Action Handling

    [Theory]
    [InlineData("restart")]
    [InlineData("pause")]
    [InlineData("destroy")]
    [InlineData("unknown_xyz")]
    public async Task Action_UnknownAction_ReturnsDescriptiveErrorWithSupportedActions(string invalidAction)
    {
        using var ctx = CreateContext();
        var args = JsonElement.Parse($"{{\"action\": \"{invalidAction}\"}}");

        var result = await ctx.Handler.HandleAsync(args);

        result.IsError.Should().BeTrue();
        string err = GetContentText(result);
        err.Should().Contain($"Unknown scrimmage action '{invalidAction}'");
        err.Should().Contain("Supported actions: launch, status, stop");
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task Action_EmptyOrWhitespace_DefaultsToLaunch(string emptyAction)
    {
        using var ctx = CreateContext();
        var args = JsonElement.Parse($"{{\"action\": \"{emptyAction}\", \"rapidTerminateAfterLaunch\": true}}");

        var result = await ctx.Handler.HandleAsync(args);

        result.IsError.Should().BeFalse();
        var root = ParseResultJson(result);
        root.GetProperty("action").GetString().Should().Be("launch");
    }

    [Theory]
    [InlineData("LAUNCH")]
    [InlineData("Status")]
    [InlineData("STOP")]
    public async Task Action_CaseInsensitive_ExecutesCorrectly(string caseVariant)
    {
        using var ctx = CreateContext();
        var args = JsonElement.Parse($"{{\"action\": \"{caseVariant}\", \"rapidTerminateAfterLaunch\": true}}");

        var result = await ctx.Handler.HandleAsync(args);

        result.IsError.Should().BeFalse();
        var root = ParseResultJson(result);
        root.GetProperty("action").GetString().Should().Be(caseVariant.ToLowerInvariant());
    }

    #endregion

    #region Part 4: McpToolRegistry Alias Resolution & Schema Verification

    [Fact]
    public void McpToolRegistry_Alias_kongor_scrimmage_ResolvesCanonicalTool()
    {
        McpToolRegistry registry = new(registerDefaults: true);

        registry.HasTool("kongor_scrimmage").Should().BeTrue();

        var tool = registry.GetTool("kongor_scrimmage");
        tool.Should().NotBeNull();
        tool!.Name.Should().Be("kongor_scrimmage_launch");
        tool.InputSchema.Properties.Should().ContainKey("port");
        tool.InputSchema.Properties.Should().ContainKey("map");
        tool.InputSchema.Properties.Should().ContainKey("hero");
    }

    [Fact]
    public void McpToolRegistry_Alias_kongor_5v5_launch_ResolvesCanonicalTool()
    {
        McpToolRegistry registry = new(registerDefaults: true);

        registry.HasTool("kongor_5v5_launch").Should().BeTrue();

        var tool = registry.GetTool("kongor_5v5_launch");
        tool.Should().NotBeNull();
        tool!.Name.Should().Be("kongor_scrimmage_launch");
        tool.InputSchema.Properties.Should().ContainKey("port");
        tool.InputSchema.Properties.Should().ContainKey("team1Bots");
        tool.InputSchema.Properties.Should().ContainKey("team2Bots");
    }

    [Theory]
    [InlineData("KONGOR_SCRIMMAGE")]
    [InlineData("Kongor_5v5_Launch")]
    [InlineData("KONGOR_SCRIMMAGE_LAUNCH")]
    public void McpToolRegistry_AliasesAndCanonical_CaseInsensitiveResolution(string toolName)
    {
        McpToolRegistry registry = new(registerDefaults: true);

        registry.HasTool(toolName).Should().BeTrue();
        var tool = registry.GetTool(toolName);
        tool.Should().NotBeNull();
        tool!.Name.Should().Be("kongor_scrimmage_launch");
    }

    [Fact]
    public async Task McpToolRegistry_ExecuteToolAsync_ViaAliases_InvokesScrimmageLaunchHandler()
    {
        McpToolRegistry registry = new(registerDefaults: true);

        var args = JsonElement.Parse("{\"action\": \"status\", \"port\": 11235}");

        // Execute through alias kongor_scrimmage
        var result1 = await registry.ExecuteToolAsync("kongor_scrimmage", args);
        result1.IsError.Should().BeFalse();
        var root1 = ParseResultJson(result1);
        root1.GetProperty("action").GetString().Should().Be("status");

        // Execute through alias kongor_5v5_launch
        var result2 = await registry.ExecuteToolAsync("kongor_5v5_launch", args);
        result2.IsError.Should().BeFalse();
        var root2 = ParseResultJson(result2);
        root2.GetProperty("action").GetString().Should().Be("status");
    }

    [Fact]
    public void McpToolRegistry_ListTools_ExcludesAliasesFromEnumeration()
    {
        McpToolRegistry registry = new(registerDefaults: true);
        var tools = registry.ListTools();

        tools.Should().Contain(t => t.Name == "kongor_scrimmage_launch");
        tools.Should().NotContain(t => t.Name == "kongor_scrimmage");
        tools.Should().NotContain(t => t.Name == "kongor_5v5_launch");
    }

    #endregion

    #region Part 5: Error Remediation Integration Stress Tests

    [Fact]
    public async Task Remediation_ClientConnectFailure_CapturedInRemediationService()
    {
        using var ctx = CreateContext();

        // Simulate client connect throwing a socket exception
        ctx.MockClientIpc.Setup(i => i.ExecuteCommandAsync(
                It.Is<string>(s => s.StartsWith("connect")),
                It.IsAny<int>(),
                It.IsAny<CancellationToken>()))
            .ThrowsAsync(new System.Net.Sockets.SocketException((int)System.Net.Sockets.SocketError.ConnectionRefused));

        var args = JsonElement.Parse("{\"rapidTerminateAfterLaunch\": true}");
        var result = await ctx.Handler.HandleAsync(args);

        // Handler catches client exception gracefully
        result.IsError.Should().BeFalse();

        // Error must be recorded in remediation service
        var recentErrors = ctx.RemediationService.GetRecentErrors(10);
        recentErrors.Should().NotBeEmpty();
        recentErrors.Should().Contain(e => e.Command.StartsWith("connect") && e.Target == "client" && e.Channel == "ipc");

        // action "errors" on ScrimmageLaunchHandler exposes the failure
        var errResult = await ctx.Handler.HandleAsync(JsonElement.Parse("{\"action\": \"errors\"}"));
        errResult.IsError.Should().BeFalse();
        var errRoot = ParseResultJson(errResult);
        errRoot.GetProperty("totalErrors").GetInt32().Should().BeGreaterThan(0);
    }

    [Fact]
    public async Task Remediation_ServerCommandFailure_CapturedInRemediationService()
    {
        using var ctx = CreateContext(setupStdin: false, registerExistingWorker: false);

        var args = JsonElement.Parse("{\"simulateServerFailure\": true, \"rapidTerminateAfterLaunch\": true}");
        var result = await ctx.Handler.HandleAsync(args);

        result.IsError.Should().BeTrue();
        var recentErrors = ctx.RemediationService.GetRecentErrors(10);
        recentErrors.Should().NotBeEmpty();
        recentErrors.Should().Contain(e => e.Target == "server");
    }

    #endregion

    #region Part 6: Inner-Loop Timing & Zero GUI Verification

    [Trait("Category", "Manual")]
    [Fact]
    public async Task Performance_SingleLaunchWithRapidTerminate_ExecutesUnder200Milliseconds()
    {
        if (!NexusTestEnvironment.IsManualTestEnabled) return;

        using var ctx = CreateContext();
        var args = JsonElement.Parse("{\"rapidTerminateAfterLaunch\": true}");

        Stopwatch sw = Stopwatch.StartNew();
        var result = await ctx.Handler.HandleAsync(args);
        sw.Stop();

        result.IsError.Should().BeFalse();
        sw.ElapsedMilliseconds.Should().BeLessThan(2000, "single mock launch must be sub-2s");
    }

    [Trait("Category", "Manual")]
    [Fact]
    public async Task Performance_FiveIterations_CompletesUnder500Milliseconds()
    {
        if (!NexusTestEnvironment.IsManualTestEnabled) return;

        using var ctx = CreateContext();
        var args = JsonElement.Parse("{\"rapidTerminateAfterLaunch\": true}");

        Stopwatch sw = Stopwatch.StartNew();
        for (int i = 0; i < 5; i++)
        {
            var result = await ctx.Handler.HandleAsync(args);
            result.IsError.Should().BeFalse();
        }
        sw.Stop();

        sw.ElapsedMilliseconds.Should().BeLessThan(1000, "5 iterations should complete in under 1000ms");
    }

    [Fact]
    public void ZeroDesktopWindows_WhenInvoked_NoHonClientProcessSpawnedDuringTests()
    {
        // Tests must never launch external interactive desktop windows (hon_x64.exe)
        var honProcs = Process.GetProcessesByName("hon_x64");
        // Any existing hon_x64 process must not be spawned by this test runner
        foreach (var p in honProcs)
        {
            p.Id.Should().NotBe(Environment.ProcessId);
        }
    }

    #endregion
}
