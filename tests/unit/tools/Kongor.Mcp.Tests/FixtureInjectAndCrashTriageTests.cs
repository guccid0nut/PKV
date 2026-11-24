// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// TAO (Together As One) MCP & Kongor CLI -- Created by guccidonut (https://github.com/guccidonut/PKV)

using System.Diagnostics;
using System.IO;
using System.Text.Json;
using FluentAssertions;
using Kongor.Cli.Mcp.Handlers;
using Kongor.Cli.Mcp.Protocol;
using Kongor.Cli.Mcp.Registration;
using Kongor.Cli.Mcp.Services;
using Xunit;

namespace Kongor.Mcp.Tests;

public sealed class FixtureInjectAndCrashTriageTests : IDisposable
{
    private readonly string _tempDir;

    public FixtureInjectAndCrashTriageTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "TaoPhase5Tests_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, recursive: true); } catch { }
    }

    [Fact]
    public async Task FixtureInjectHandler_InjectScenario_AppliesHeroLevelItemsAndCreeps()
    {
        var timeProvider = TimeProvider.System;
        var clientHandler = new GameClientManageHandler(timeProvider);
        var serverHandler = new GameServerManageHandler(timeProvider);
        var ipcBridge = new GameIpcBridge();
        var handler = new FixtureInjectHandler(clientHandler, serverHandler, ipcBridge, timeProvider);

        var args = JsonElement.Parse("""
        {
            "action": "inject",
            "hero": "Hero_Pyromancer",
            "level": 16,
            "gold": 5000,
            "teleport": { "x": 100.5, "y": -200.5, "z": 50.0 },
            "items": ["Item_PortalKey", "Item_PostHaste"],
            "health_percent": 0.85,
            "mana_percent": 0.90,
            "spawn_creeps": [
                { "type": "Creep_LegionMelee", "count": 2 }
            ],
            "target": "both",
            "allowSimulation": true
        }
        """);

        ToolCallResult result = await handler.HandleAsync(args, CancellationToken.None);

        result.Should().NotBeNull();
        result.IsError.Should().BeFalse();

        using var doc = JsonDocument.Parse(result.FirstText);
        var root = doc.RootElement;

        root.GetProperty("status").GetString().Should().Be("success");
        root.GetProperty("executionMode").GetString().Should().NotBeNullOrWhiteSpace();

        root.GetProperty("action").GetString().Should().Be("inject");
        root.GetProperty("hero").GetString().Should().Be("Hero_Pyromancer");
        root.GetProperty("level").GetInt32().Should().Be(16);
        root.GetProperty("gold").GetInt32().Should().Be(5000);
        root.GetProperty("healthPercent").GetDouble().Should().Be(0.85);
        root.GetProperty("manaPercent").GetDouble().Should().Be(0.90);
        root.GetProperty("creepsSpawned").GetInt32().Should().Be(2);

        var cmds = root.GetProperty("dispatchedCommands").EnumerateArray().Select(e => e.GetString()!).ToList();
        cmds.Should().Contain(c => c.Contains("Morph Hero_Pyromancer"));
        cmds.Should().Contain(c => c.Contains("SetLevel 16"));
        cmds.Should().Contain(c => c.Contains("GiveGold 5000"));
        cmds.Should().Contain(c => c.Contains("Teleport 100.50 -200.50 50.00"));
        cmds.Should().Contain(c => c.Contains("GiveItem Item_PortalKey"));
        cmds.Should().Contain(c => c.Contains("SpawnUnit Creep_LegionMelee"));
    }

    [Fact]
    public async Task FixtureInjectHandler_PresetScenario_ResolvesRoshanContest()
    {
        var timeProvider = TimeProvider.System;
        var clientHandler = new GameClientManageHandler(timeProvider);
        var serverHandler = new GameServerManageHandler(timeProvider);
        var ipcBridge = new GameIpcBridge();
        var handler = new FixtureInjectHandler(clientHandler, serverHandler, ipcBridge, timeProvider);

        var args = JsonElement.Parse("""
        {
            "action": "preset",
            "preset": "roshan_contest",
            "allowSimulation": true
        }
        """);

        ToolCallResult result = await handler.HandleAsync(args, CancellationToken.None);

        result.Should().NotBeNull();
        result.IsError.Should().BeFalse();

        using var doc = JsonDocument.Parse(result.FirstText);
        var root = doc.RootElement;

        root.GetProperty("hero").GetString().Should().Be("Hero_Devourer");
        root.GetProperty("level").GetInt32().Should().Be(11);
        root.GetProperty("creepsSpawned").GetInt32().Should().Be(1);

        var cmds = root.GetProperty("dispatchedCommands").EnumerateArray().Select(e => e.GetString()!).ToList();
        cmds.Should().Contain(c => c.Contains("Morph Hero_Devourer"));
        cmds.Should().Contain(c => c.Contains("GiveItem Item_BehemothsHeart"));
        cmds.Should().Contain(c => c.Contains("SpawnUnit Creep_Kongor"));
    }

    [Fact]
    public async Task CrashTriageHandler_Triage_ReturnsGhidraTriangulationAndRootCause()
    {
        var timeProvider = TimeProvider.System;
        var handler = new CrashTriageHandler(timeProvider, logsDirectory: _tempDir);

        var args = JsonElement.Parse("""
        {
            "action": "triage",
            "module": "cgame_x64.dll",
            "address": "0x1e5de0"
        }
        """);

        ToolCallResult result = await handler.HandleAsync(args, CancellationToken.None);

        result.Should().NotBeNull();
        result.IsError.Should().BeFalse();

        using var doc = JsonDocument.Parse(result.FirstText);
        var root = doc.RootElement;

        root.GetProperty("status").GetString().Should().Be("success");

        root.GetProperty("triaged").GetBoolean().Should().BeTrue();
        root.GetProperty("faultingModule").GetString().Should().Be("cgame_x64.dll");
        root.GetProperty("faultingRva").GetString().Should().Be("0x1e5de0");
        root.GetProperty("exceptionCode").GetString().Should().Be("0xC0000005");

        var ghidra = root.GetProperty("ghidraTriangulation");
        ghidra.GetProperty("suggestedTool").GetString().Should().Be("ghidra");
        ghidra.GetProperty("command").GetString().Should().Be("decompile_function(file_name: \"cgame_x64.dll\", address: \"0x1e5de0\")");

        var windbg = root.GetProperty("windbgInspection");
        windbg.GetProperty("command").GetString().Should().Contain(".ecxr; k; r; lmDvm cgame_x64");
    }

    [Fact]
    public async Task NetPacketFuzzHandler_DissectAndFuzz_ProducesMutatedFramesAndVerifiesDesync()
    {
        var timeProvider = TimeProvider.System;
        var handler = new NetPacketFuzzHandler(timeProvider);

        // 1. Dissect standard K2 packet
        var dissectArgs = JsonElement.Parse("""
        {
            "action": "dissect",
            "mode": "offline_parser"
        }
        """);

        ToolCallResult dissectResult = await handler.HandleAsync(dissectArgs, CancellationToken.None);
        dissectResult.IsError.Should().BeFalse();

        using (var doc = JsonDocument.Parse(dissectResult.FirstText))
        {
            var root = doc.RootElement;
            var dissection = root.GetProperty("dissection");
            dissection.GetProperty("opcodeName").GetString().Should().Be("NET_GAME_DATA");
            dissection.GetProperty("isReliable").GetBoolean().Should().BeTrue();
            dissection.GetProperty("sequenceNumber").GetUInt16().Should().Be(1050);
            dissection.GetProperty("ackNumber").GetUInt16().Should().Be(1049);
        }

        // 2. Fuzz with bit_flip strategy
        var fuzzArgs = JsonElement.Parse("""
        {
            "action": "fuzz",
            "mode": "offline_parser",
            "mutation_strategy": "bit_flip",
            "mutation_rate": 0.1,
            "packet_count": 5
        }
        """);

        ToolCallResult fuzzResult = await handler.HandleAsync(fuzzArgs, CancellationToken.None);
        fuzzResult.IsError.Should().BeFalse();

        using (var doc = JsonDocument.Parse(fuzzResult.FirstText))
        {
            var root = doc.RootElement;
            root.GetProperty("packetsGenerated").GetInt32().Should().Be(5);
            root.GetProperty("samples").GetArrayLength().Should().Be(5);
        }

        // 3. Verify desync resilience
        var desyncArgs = JsonElement.Parse("""
        {
            "action": "verify_desync",
            "mode": "offline_parser",
            "target_port": 11235
        }
        """);

        ToolCallResult desyncResult = await handler.HandleAsync(desyncArgs, CancellationToken.None);
        desyncResult.IsError.Should().BeFalse();

        using (var doc = JsonDocument.Parse(desyncResult.FirstText))
        {
            var root = doc.RootElement;
            root.GetProperty("stateDesyncDetected").GetBoolean().Should().BeFalse();
            root.GetProperty("sequenceDrift").GetInt32().Should().Be(0);
        }
    }

    [Fact]
    public void Registry_ContainsPhase5Tools_WithCanonicalNamesAndAliases()
    {
        var registry = new McpToolRegistry(registerDefaults: true);

        string[] expectedCanonicalTools =
        [
            "kongor_fixture_inject",
            "kongor_crash_triage",
            "kongor_net_packet_fuzz",

        ];

        foreach (var name in expectedCanonicalTools)
        {
            var tool = registry.GetTool(name);
            tool.Should().NotBeNull($"Tool '{name}' must be registered in McpToolRegistry");
            tool!.Description.Should().NotBeNullOrWhiteSpace();
            tool.InputSchema.Should().NotBeNull();
        }

        // Aliases
        registry.GetTool("fixture_inject").Should().NotBeNull();
        registry.GetTool("crash_triage").Should().NotBeNull();
        registry.GetTool("packet_fuzz").Should().NotBeNull();
        registry.GetTool("profile_trace").Should().BeNull();
        registry.GetTool("kongor_profile_trace").Should().BeNull();
    }

    [Fact]
    public void AntigravityRegistrar_WithValidArguments_GeneratesValidPhase5Schemas()
    {
        string schemaDir = Path.Combine(_tempDir, "schemas");
        var registry = new McpToolRegistry(registerDefaults: true);

        string? prevSim = Environment.GetEnvironmentVariable("KONGOR_MCP_INCLUDE_SIMULATED");
        RegistrationResult result;
        try
        {
            Environment.SetEnvironmentVariable("KONGOR_MCP_INCLUDE_SIMULATED", "1");
            result = AntigravityRegistrar.Register(schemaDir, registry);
        }
        finally
        {
            Environment.SetEnvironmentVariable("KONGOR_MCP_INCLUDE_SIMULATED", prevSim);
        }

        result.Success.Should().BeTrue();
        result.GeneratedFiles.Should().NotBeEmpty();

        string[] expectedFiles =
        [
            "kongor_fixture_inject.json",
            "kongor_crash_triage.json",
            "kongor_net_packet_fuzz.json",

            "instructions.md"
        ];

        foreach (var expected in expectedFiles)
        {
            string fullPath = Path.Combine(schemaDir, expected);
            File.Exists(fullPath).Should().BeTrue($"Schema file '{expected}' must exist");

            if (expected.EndsWith(".json"))
            {
                string json = File.ReadAllText(fullPath);
                using var doc = JsonDocument.Parse(json);
                doc.RootElement.GetProperty("name").GetString().Should().NotBeNullOrWhiteSpace();
                doc.RootElement.GetProperty("description").GetString().Should().NotBeNullOrWhiteSpace();
                doc.RootElement.GetProperty("parameters").Should().NotBeNull();
            }
        }
    }
}
