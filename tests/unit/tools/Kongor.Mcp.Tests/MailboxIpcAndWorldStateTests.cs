// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// TAO (Together As One) MCP & Kongor CLI -- Created by guccidonut (https://github.com/guccidonut/PKV)

using System.Text.Json;
using FluentAssertions;
using Kongor.Cli.Mcp.Handlers;
using Kongor.Cli.Mcp.Protocol;
using Kongor.Cli.Mcp.Services;
using Moq;
using Xunit;

namespace Kongor.Mcp.Tests;

/// <summary>
/// Phase 6 Verification Test Suite:
/// 1. Correlated Multi-Flight Mailbox IPC (out-of-order resolution, zero lock serialization).
/// 2. In-Engine Structured World State & Hero Telemetry (GameSnapshotHandler & TAO.GetWorldState).
/// 3. Deterministic Simulation Lockstep & Frame Stepping (GameCommandHandler action: step).
/// 4. Live MCP Resource Subscriptions (kongor://servers/registry & notifications/resources/updated).
/// </summary>
public sealed class MailboxIpcAndWorldStateTests : IDisposable
{
    private readonly string _testMailboxDir;

    public MailboxIpcAndWorldStateTests()
    {
        _testMailboxDir = Path.Combine(Path.GetTempPath(), "tao_phase6_test_" + Guid.NewGuid().ToString("N"));
        if (!Directory.Exists(_testMailboxDir))
        {
            Directory.CreateDirectory(_testMailboxDir);
        }
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_testMailboxDir))
            {
                Directory.Delete(_testMailboxDir, recursive: true);
            }
        }
        catch { }
    }

    // =========================================================================
    // 1. Correlated Multi-Flight Mailbox IPC Tests
    // =========================================================================

    [Fact]
    public async Task MailboxBridge_ConcurrentRequests_AreSafelySerializedAndCorrelated()
    {
        await using var bridge = new NexusMailboxIpcBridge(_testMailboxDir);
        using var cts = new CancellationTokenSource(5000);

        // Background worker to simulate engine response for any active request
        var engineTask = Task.Run(async () =>
        {
            var responded = new HashSet<int>();
            while (!cts.IsCancellationRequested)
            {
                var files = Directory.GetFiles(_testMailboxDir, "req_*.lua")
                    .Where(f => !f.EndsWith("req_current.lua", StringComparison.OrdinalIgnoreCase))
                    .ToArray();

                foreach (var file in files)
                {
                    string name = Path.GetFileNameWithoutExtension(file);
                    if (int.TryParse(name.Replace("req_", ""), out int id) && responded.Add(id))
                    {
                        string resJson = JsonSerializer.Serialize(new
                        {
                            id,
                            type = "command",
                            success = true,
                            result = $"SimulatedResponse_{id}",
                            error = (string?)null,
                            timestamp = 1000L
                        });
                        await File.WriteAllTextAsync(Path.Combine(_testMailboxDir, $"res_{id}.json"), resJson, cts.Token);
                    }
                }
                try { await Task.Delay(10, cts.Token); } catch (OperationCanceledException) { break; }
            }
        }, cts.Token);

        // Dispatches 2 concurrent requests
        var task1 = Task.Run(async () => await bridge.ExecuteCommandAsync("Echo Request1", 4000));
        var task2 = Task.Run(async () => await bridge.ExecuteCommandAsync("Echo Request2", 4000));

        string[] results = await Task.WhenAll(task1, task2);
        cts.Cancel();
        try { await engineTask; } catch (OperationCanceledException) { }

        results[0].Should().StartWith("SimulatedResponse_");
        results[1].Should().StartWith("SimulatedResponse_");
        results[0].Should().NotBe(results[1]);
    }

    [Fact]
    public async Task MailboxBridge_PendingRequestsCount_StartsEmpty()
    {
        await using var bridge = new NexusMailboxIpcBridge(_testMailboxDir);
        bridge.PendingRequestsCount.Should().Be(0);
    }

    // =========================================================================
    // 2. Structured World State Tests (GameSnapshotHandler & TAO.GetWorldState)
    // =========================================================================

    [Fact]
    public async Task GameSnapshotHandler_ActionWorldState_ReturnsStructuredHeroPayload()
    {
        var handler = new GameSnapshotHandler();

        var args = JsonElement.Parse("""{ "action": "world_state" }""");
        var result = await handler.HandleAsync(args);

        result.IsError.Should().BeFalse();
        result.Content.Should().NotBeEmpty();
        string text = ((TextContentItem)result.Content[0]).Text;
        using var doc = JsonDocument.Parse(text);
        var root = doc.RootElement;

        root.GetProperty("status").GetString().Should().Be("success");
        root.GetProperty("action").GetString().Should().Be("world_state");
        root.TryGetProperty("worldState", out var ws).Should().BeTrue();

        ws.GetProperty("gamePhase").GetInt32().Should().Be(3);
        ws.GetProperty("activeMap").GetString().Should().Be("caldavar");

        var team1 = ws.GetProperty("team1Heroes");
        team1.GetArrayLength().Should().BeGreaterThan(0);

        var firstHero = team1[0];
        firstHero.GetProperty("heroName").GetString().Should().Be("Hero_Pyromancer");
        firstHero.GetProperty("health").GetDouble().Should().Be(550.0);
        firstHero.GetProperty("mana").GetDouble().Should().Be(280.0);
        firstHero.GetProperty("position").GetProperty("x").GetDouble().Should().Be(7400.0);
        firstHero.GetProperty("position").GetProperty("y").GetDouble().Should().Be(6900.0);

        var abilities = firstHero.GetProperty("abilities");
        abilities.GetArrayLength().Should().Be(4);
    }

    [Theory]
    [InlineData("inspect")]
    [InlineData("world")]
    public async Task GameSnapshotHandler_WorldState_AliasesSucceed(string actionAlias)
    {
        var handler = new GameSnapshotHandler();

        var args = JsonElement.Parse($@"{{ ""action"": ""{actionAlias}"" }}");
        var result = await handler.HandleAsync(args);

        result.IsError.Should().BeFalse();
        result.Content.Should().NotBeEmpty();
        string text = ((TextContentItem)result.Content[0]).Text;
        using var doc = JsonDocument.Parse(text);
        doc.RootElement.GetProperty("action").GetString().Should().Be("world_state");
    }

    // =========================================================================
    // 3. Deterministic Simulation Lockstep & Stepping Tests
    // =========================================================================

    [Fact]
    public void GameCommandHandler_ResolveCommand_StepFrames_ClampsBounds()
    {
        // Lower bound clamping
        var argsLow = JsonElement.Parse("""{ "action": "step", "frames": -5 }""");
        string? cmdLow = GameCommandHandler.ResolveCommand(argsLow, out string actLow);
        actLow.Should().Be("step");
        cmdLow.Should().Be("ReplayIncFrame 1");

        // Normal step
        var argsNormal = JsonElement.Parse("""{ "action": "step_frames", "frames": 60 }""");
        string? cmdNormal = GameCommandHandler.ResolveCommand(argsNormal, out string actNormal);
        actNormal.Should().Be("step_frames");
        cmdNormal.Should().Be("ReplayIncFrame 60");

        // Upper bound clamping
        var argsHigh = JsonElement.Parse("""{ "action": "step", "frames": 5000 }""");
        string? cmdHigh = GameCommandHandler.ResolveCommand(argsHigh, out string actHigh);
        cmdHigh.Should().Be("ReplayIncFrame 1000");
    }

    [Fact]
    public void GameCommandHandler_ResolveCommand_StepFrames_UncappedFpsMode()
    {
        var args = JsonElement.Parse("""{ "action": "step", "frames": 120, "uncapped": true }""");
        string? cmd = GameCommandHandler.ResolveCommand(args, out _);

        cmd.Should().Be("Set host_maxfps 0; Set cl_maxfps 0; ReplayIncFrame 120");
    }

    // =========================================================================
    // 4. Live MCP Resource Subscriptions & Real-Time Event Push Tests
    // =========================================================================

    [Fact]
    public async Task McpServer_ResourcesList_IncludesServerRegistry()
    {
        using var server = new McpServer();

        var req = new JsonRpcRequest
        {
            Id = new JsonRpcId(1),
            Method = "resources/list"
        };

        string line = JsonRpcSerializer.Serialize(req);
        string? respJson = await server.ProcessMessageAsync(line);

        respJson.Should().NotBeNull();
        using var doc = JsonDocument.Parse(respJson!);
        var resources = doc.RootElement.GetProperty("result").GetProperty("resources");

        bool foundRegistry = false;
        foreach (var r in resources.EnumerateArray())
        {
            if (r.GetProperty("uri").GetString() == "kongor://servers/registry")
            {
                foundRegistry = true;
                r.GetProperty("name").GetString().Should().Be("Dedicated Server Port Registry");
                r.GetProperty("mimeType").GetString().Should().Be("application/json");
                break;
            }
        }

        foundRegistry.Should().BeTrue();
    }

    [Fact]
    public async Task McpServer_ResourcesRead_ReturnsActiveServersRegistry()
    {
        using var server = new McpServer();

        // Register a server in the tool registry's port registry
        server.Registry.ServerPortRegistry.RegisterWorker(processId: 44102, port: 11299, map: "midwars", matchId: 9901);

        var req = new JsonRpcRequest
        {
            Id = new JsonRpcId(2),
            Method = "resources/read",
            Params = JsonElement.Parse("""{ "uri": "kongor://servers/registry" }""")
        };

        string line = JsonRpcSerializer.Serialize(req);
        string? respJson = await server.ProcessMessageAsync(line);

        respJson.Should().NotBeNull();
        using var doc = JsonDocument.Parse(respJson!);
        var contents = doc.RootElement.GetProperty("result").GetProperty("contents");
        contents.GetArrayLength().Should().Be(1);

        string text = contents[0].GetProperty("text").GetString()!;
        using var registryDoc = JsonDocument.Parse(text);

        registryDoc.RootElement.GetProperty("status").GetString().Should().Be("success");
        registryDoc.RootElement.GetProperty("totalServers").GetInt32().Should().BeGreaterThan(0);

        var servers = registryDoc.RootElement.GetProperty("servers");
        bool found = false;
        foreach (var s in servers.EnumerateArray())
        {
            if (s.GetProperty("port").GetInt32() == 11299)
            {
                found = true;
                s.GetProperty("map").GetString().Should().Be("midwars");
                s.GetProperty("matchId").GetInt64().Should().Be(9901);
                break;
            }
        }
        found.Should().BeTrue();
    }

    [Fact]
    public async Task McpServer_SubscribeRegistry_SendsNotificationOnPortRegistryUpdate()
    {
        using var server = new McpServer();
        server.ClearSentNotifications();

        // 1. Subscribe to kongor://servers/registry
        var subReq = new JsonRpcRequest
        {
            Id = new JsonRpcId(10),
            Method = "resources/subscribe",
            Params = JsonElement.Parse("""{ "uri": "kongor://servers/registry" }""")
        };
        await server.ProcessMessageAsync(JsonRpcSerializer.Serialize(subReq));

        server.IsSubscribed("kongor://servers/registry").Should().BeTrue();

        // 2. Register worker -> fires OnRegistryChanged -> sends notification
        server.Registry.ServerPortRegistry.RegisterWorker(processId: 55201, port: 11239, map: "caldavar");

        // Verify notification was sent
        server.SentNotifications.Should().Contain(n => n.Contains("notifications/resources/updated") && n.Contains("kongor://servers/registry"));

        // 3. Unsubscribe -> unregisters -> no further notification
        var unsubReq = new JsonRpcRequest
        {
            Id = new JsonRpcId(11),
            Method = "resources/unsubscribe",
            Params = JsonElement.Parse("""{ "uri": "kongor://servers/registry" }""")
        };
        await server.ProcessMessageAsync(JsonRpcSerializer.Serialize(unsubReq));

        server.IsSubscribed("kongor://servers/registry").Should().BeFalse();
        server.ClearSentNotifications();

        server.Registry.ServerPortRegistry.UnregisterWorker(11239);
        server.SentNotifications.Should().NotContain(n => n.Contains("kongor://servers/registry"));
    }
}

