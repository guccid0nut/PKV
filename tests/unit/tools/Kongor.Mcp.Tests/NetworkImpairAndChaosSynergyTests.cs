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
/// - Network Impairment Simulation (kongor_network_impair_simulate) & Chaos Simulation (kongor_network_chaos) synergy.
/// - Dynamic IServerPortRegistry authoritative resolution when port is omitted.
/// - Dual-channel execution (dedicated server stdin + client IPC bridge) under strict port targeting rules.
/// - target: "server", "client", and "both" multi-target orchestration.
/// - In-engine K2 network cvars (net_lag, net_drop, net_jitter) live dispatch and reset/heal behavior.
/// - Multi-region profile emulation (oceanic_lag, high_jitter_mobile, packet_loss_heavy).
/// </summary>
public sealed class NetworkImpairAndChaosSynergyTests : IDisposable
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

    private sealed class NetworkSynergyTestContext : IDisposable
    {
        public NetworkImpairSimulateHandler ImpairHandler { get; init; } = null!;
        public NetworkChaosHandler ChaosHandler { get; init; } = null!;
        public GameServerManageHandler ServerManage { get; init; } = null!;
        public ServerPortRegistry PortRegistry { get; init; } = null!;
        public Mock<IGameIpcBridge> MockClientIpc { get; init; } = null!;
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

    private NetworkSynergyTestContext CreateContext(int port = 11235, bool registerWorker = true)
    {
        string tempRegPath = Path.Combine(Path.GetTempPath(), $"reg_net_synergy_{Guid.NewGuid():N}.json");
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
            reg.RegisterWorker(Environment.ProcessId, port, $"Project KONGOR Test Worker {port}", "caldavar");

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
        mockIpc.Setup(x => x.ExecuteCommandAsync(It.IsAny<string>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .Returns((string cmd, int to, CancellationToken ct) =>
            {
                clientCommands.Add(cmd);
                return Task.FromResult("[TAO_RESPONSE]{\"status\":\"ok\"}[/TAO_RESPONSE]");
            });

        var impairHandler = new NetworkImpairSimulateHandler(
            serverManageHandler: serverManage,
            portRegistry: reg,
            clientIpcBridge: mockIpc.Object);

        var chaosHandler = new NetworkChaosHandler(
            serverManageHandler: serverManage,
            portRegistry: reg,
            clientIpcBridge: mockIpc.Object);

        return new NetworkSynergyTestContext
        {
            ImpairHandler = impairHandler,
            ChaosHandler = chaosHandler,
            ServerManage = serverManage,
            PortRegistry = reg,
            MockClientIpc = mockIpc,
            ServerStdinStream = ms,
            ServerStdinWriter = writer,
            ClientCommands = clientCommands,
            Port = port
        };
    }

    public void Dispose()
    {
        NetworkChaosHandler.ClearAllRules();
        foreach (var file in _tempFiles)
        {
            try { if (File.Exists(file)) File.Delete(file); } catch { }
        }
    }

    [Fact]
    public async Task NetworkImpair_ResolvesPortFromPortRegistry_WhenPortOmitted()
    {
        using var ctx = CreateContext(port: 11245);

        using var argsDoc = JsonDocument.Parse("""
        {
            "action": "simulate",
            "latencyMs": 120,
            "packetLossPercent": 1.5,
            "jitterMs": 10
        }
        """);

        var result = await ctx.ImpairHandler.HandleAsync(argsDoc.RootElement);
        var root = ParseResult(result);

        root.GetProperty("status").GetString().Should().Be("active");
        root.GetProperty("port").GetInt32().Should().Be(11245);
        root.GetProperty("resolvedPort").GetInt32().Should().Be(11245);
        root.GetProperty("dispatchedLive").GetBoolean().Should().BeTrue();

        string stdin = await ctx.ReadServerStdinContentAsync();
        stdin.Should().Contain("net_lag 120");
        stdin.Should().Contain("net_drop 1.5");
        stdin.Should().Contain("net_jitter 10");
    }

    [Fact]
    public async Task NetworkImpair_LiveExecution_PipesCvarsToDedicatedServerStdin()
    {
        using var ctx = CreateContext(port: 11235);

        using var argsDoc = JsonDocument.Parse("""
        {
            "action": "simulate",
            "target": "server",
            "port": 11235,
            "latencyMs": 85,
            "packetLossPercent": 3.0,
            "jitterMs": 12
        }
        """);

        var result = await ctx.ImpairHandler.HandleAsync(argsDoc.RootElement);
        var root = ParseResult(result);

        root.GetProperty("status").GetString().Should().Be("active");
        root.GetProperty("target").GetString().Should().Be("server");
        root.GetProperty("serverDispatchedLive").GetBoolean().Should().BeTrue();

        string stdin = await ctx.ReadServerStdinContentAsync();
        stdin.Should().Contain("net_lag 85");
        stdin.Should().Contain("net_drop 3.0");
        stdin.Should().Contain("net_jitter 12");
    }

    [Fact]
    public async Task NetworkImpair_TargetClient_DispatchesCvarsToClientIpc()
    {
        using var ctx = CreateContext(port: 11235);

        using var argsDoc = JsonDocument.Parse("""
        {
            "action": "simulate",
            "target": "client",
            "latencyMs": 105,
            "packetLossPercent": 2.0,
            "jitterMs": 8
        }
        """);

        var result = await ctx.ImpairHandler.HandleAsync(argsDoc.RootElement);
        var root = ParseResult(result);

        root.GetProperty("status").GetString().Should().Be("active");
        root.GetProperty("target").GetString().Should().Be("client");
        root.GetProperty("clientDispatchedLive").GetBoolean().Should().BeTrue();

        ctx.ClientCommands.Should().Contain("net_lag 105");
        ctx.ClientCommands.Should().Contain("net_drop 2.0");
        ctx.ClientCommands.Should().Contain("net_jitter 8");
    }

    [Fact]
    public async Task NetworkImpair_TargetBoth_DispatchesToBothServerAndClient()
    {
        using var ctx = CreateContext(port: 11235);

        using var argsDoc = JsonDocument.Parse("""
        {
            "action": "simulate",
            "target": "both",
            "port": 11235,
            "latencyMs": 140,
            "packetLossPercent": 4.0,
            "jitterMs": 20
        }
        """);

        var result = await ctx.ImpairHandler.HandleAsync(argsDoc.RootElement);
        var root = ParseResult(result);

        root.GetProperty("status").GetString().Should().Be("active");
        root.GetProperty("target").GetString().Should().Be("both");
        root.GetProperty("serverDispatchedLive").GetBoolean().Should().BeTrue();
        root.GetProperty("clientDispatchedLive").GetBoolean().Should().BeTrue();

        string stdin = await ctx.ReadServerStdinContentAsync();
        stdin.Should().Contain("net_lag 140");
        stdin.Should().Contain("net_drop 4.0");
        stdin.Should().Contain("net_jitter 20");

        ctx.ClientCommands.Should().Contain("net_lag 140");
        ctx.ClientCommands.Should().Contain("net_drop 4.0");
        ctx.ClientCommands.Should().Contain("net_jitter 20");
    }

    [Fact]
    public async Task NetworkImpair_Reset_PipesZeroCvarsToTargetLive()
    {
        using var ctx = CreateContext(port: 11235);

        using var argsDoc = JsonDocument.Parse("""
        {
            "action": "reset",
            "target": "both",
            "port": 11235
        }
        """);

        var result = await ctx.ImpairHandler.HandleAsync(argsDoc.RootElement);
        var root = ParseResult(result);

        root.GetProperty("status").GetString().Should().Be("cleared");
        root.GetProperty("target").GetString().Should().Be("both");
        root.GetProperty("serverResetDispatched").GetBoolean().Should().BeTrue();
        root.GetProperty("clientResetDispatched").GetBoolean().Should().BeTrue();

        string stdin = await ctx.ReadServerStdinContentAsync();
        stdin.Should().Contain("net_lag 0");
        stdin.Should().Contain("net_drop 0");
        stdin.Should().Contain("net_jitter 0");

        ctx.ClientCommands.Should().Contain("net_lag 0");
        ctx.ClientCommands.Should().Contain("net_drop 0");
        ctx.ClientCommands.Should().Contain("net_jitter 0");
    }

    [Fact]
    public async Task NetworkImpair_ProfileOceanicLag_AppliesPresetAndDispatchesLive()
    {
        using var ctx = CreateContext(port: 11235);

        using var argsDoc = JsonDocument.Parse("""
        {
            "action": "simulate",
            "profile": "oceanic_lag",
            "port": 11235
        }
        """);

        var result = await ctx.ImpairHandler.HandleAsync(argsDoc.RootElement);
        var root = ParseResult(result);

        root.GetProperty("status").GetString().Should().Be("active");
        var impairment = root.GetProperty("impairment");
        impairment.GetProperty("latencyMs").GetInt32().Should().Be(250);
        impairment.GetProperty("packetLossPercent").GetDouble().Should().Be(1.0);
        impairment.GetProperty("jitterMs").GetInt32().Should().Be(15);

        string stdin = await ctx.ReadServerStdinContentAsync();
        stdin.Should().Contain("net_lag 250");
        stdin.Should().Contain("net_drop 1.0");
        stdin.Should().Contain("net_jitter 15");
    }

    [Fact]
    public async Task NetworkChaos_ResolvesPortFromPortRegistry_WhenPortOmitted()
    {
        using var ctx = CreateContext(port: 11248);

        using var argsDoc = JsonDocument.Parse("""
        {
            "action": "inject",
            "type": "latency",
            "latencyMs": 175
        }
        """);

        var result = await ctx.ChaosHandler.HandleAsync(argsDoc.RootElement);
        var root = ParseResult(result);

        root.GetProperty("status").GetString().Should().Be("success");
        root.GetProperty("action").GetString().Should().Be("inject");
        root.GetProperty("type").GetString().Should().Be("latency");
        root.GetProperty("latencyMs").GetInt32().Should().Be(175);
        root.GetProperty("rule").GetProperty("targetId").GetString().Should().Be("11248");

        string stdin = await ctx.ReadServerStdinContentAsync();
        stdin.Should().Contain("net_lag 175");
    }

    [Fact]
    public async Task NetworkChaos_InjectDrop_PipesDropCvarsToLiveServerWorker()
    {
        using var ctx = CreateContext(port: 11235);

        using var argsDoc = JsonDocument.Parse("""
        {
            "action": "drop",
            "dropPercentage": 25,
            "targetType": "port",
            "targetId": "11235"
        }
        """);

        var result = await ctx.ChaosHandler.HandleAsync(argsDoc.RootElement);
        var root = ParseResult(result);

        root.GetProperty("status").GetString().Should().Be("success");
        root.GetProperty("action").GetString().Should().Be("inject");
        root.GetProperty("type").GetString().Should().Be("packet_drop");
        root.GetProperty("dropPercentage").GetInt32().Should().Be(25);

        string stdin = await ctx.ReadServerStdinContentAsync();
        stdin.Should().Contain("net_drop 25.0");
    }

    [Fact]
    public async Task NetworkChaos_TargetBoth_AppliesToBothServerAndClient()
    {
        using var ctx = CreateContext(port: 11235);

        using var argsDoc = JsonDocument.Parse("""
        {
            "action": "spike",
            "targetType": "both",
            "targetId": "11235",
            "latencyMs": 250,
            "durationSeconds": 5
        }
        """);

        var result = await ctx.ChaosHandler.HandleAsync(argsDoc.RootElement);
        var root = ParseResult(result);

        root.GetProperty("status").GetString().Should().Be("success");
        root.GetProperty("action").GetString().Should().Be("inject");
        root.GetProperty("latencyMs").GetInt32().Should().Be(250);

        string stdin = await ctx.ReadServerStdinContentAsync();
        stdin.Should().Contain("net_lag 250");

        ctx.ClientCommands.Should().Contain("net_lag 250");
    }

    [Fact]
    public async Task NetworkChaos_Heal_RestoresZeroCvarsLive()
    {
        using var ctx = CreateContext(port: 11235);

        // Inject first
        using var injectDoc = JsonDocument.Parse("""
        {
            "action": "inject",
            "type": "jitter",
            "jitterMs": 35,
            "targetType": "both",
            "targetId": "11235"
        }
        """);
        var injectResult = await ctx.ChaosHandler.HandleAsync(injectDoc.RootElement);
        var injectRoot = ParseResult(injectResult);
        string ruleId = injectRoot.GetProperty("ruleId").GetString()!;

        // Heal
        using var healDoc = JsonDocument.Parse($$"""
        {
            "action": "heal",
            "ruleId": "{{ruleId}}",
            "targetType": "both",
            "targetId": "11235"
        }
        """);
        var healResult = await ctx.ChaosHandler.HandleAsync(healDoc.RootElement);
        var healRoot = ParseResult(healResult);

        healRoot.GetProperty("status").GetString().Should().Be("success");
        healRoot.GetProperty("action").GetString().Should().Be("heal");
        healRoot.GetProperty("target").GetString().Should().Be("both:11235");

        string stdin = await ctx.ReadServerStdinContentAsync();
        stdin.Should().Contain("net_jitter 0");

        ctx.ClientCommands.Should().Contain("net_jitter 0");
    }

    [Fact]
    public async Task NetworkChaos_Reset_InjectsConnectionResetRuleAndPipes100PercentDrop()
    {
        using var ctx = CreateContext(port: 11235);

        using var resetDoc = JsonDocument.Parse("""
        {
            "action": "reset",
            "targetType": "both",
            "targetId": "11235"
        }
        """);
        var resetResult = await ctx.ChaosHandler.HandleAsync(resetDoc.RootElement);
        var resetRoot = ParseResult(resetResult);

        resetRoot.GetProperty("status").GetString().Should().Be("success");
        resetRoot.GetProperty("action").GetString().Should().Be("reset");
        resetRoot.GetProperty("type").GetString().Should().Be("reset");

        string stdin = await ctx.ReadServerStdinContentAsync();
        stdin.Should().Contain("net_lag 0");
        stdin.Should().Contain("net_drop 100.0");
        stdin.Should().Contain("net_jitter 0");

        ctx.ClientCommands.Should().Contain("net_lag 0");
        ctx.ClientCommands.Should().Contain("net_drop 100.0");
        ctx.ClientCommands.Should().Contain("net_jitter 0");
    }

    [Fact]
    public void McpToolRegistry_WhenInvoked_RegistersNetworkToolsWithCorrectSignatures()
    {
        var registry = new McpToolRegistry();
        var tools = registry.ListTools();

        var impairTool = tools.FirstOrDefault(t => t.Name == "kongor_network_impair_simulate");
        impairTool.Should().NotBeNull();
        impairTool!.Description.Should().Contain("latency");

        var chaosTool = tools.FirstOrDefault(t => t.Name == "kongor_network_chaos");
        chaosTool.Should().NotBeNull();
        chaosTool!.Description.Should().Contain("chaos");
    }
}
