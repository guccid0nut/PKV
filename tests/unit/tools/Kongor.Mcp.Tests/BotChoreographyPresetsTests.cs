// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// TAO (Together As One) MCP & Kongor CLI -- Created by guccidonut (https://github.com/guccidonut/PKV)

#pragma warning disable CA2026
using System.IO.Compression;
using System.Text.Json;
using FluentAssertions;
using Kongor.Cli.Mcp.Handlers;
using Kongor.Cli.Mcp.Protocol;
using Kongor.Cli.Mcp.Services;
using Moq;
using Xunit;

namespace Kongor.Mcp.Tests;

/// <summary>
/// Unit tests covering Phase 3 Modernization:
/// - Bot Choreography: Dual-target routing, combat presets, session tracking, client IPC bridge
/// - Replay Diff: Binary search desync detection (find_first_desync), 10-entity delta comparison
/// - Replay Inspect: Combat log timeline extraction and event/hero filtering
/// - Match Simulate E2E: Custom 5v5 hero lineups, timescale acceleration, simulated scoreboard
/// </summary>
public sealed class BotChoreographyPresetsTests
{
    private readonly McpToolRegistry _registry = new(registerDefaults: true);

    private static JsonElement ParseResult(ToolCallResult result)
    {
        result.IsError.Should().BeFalse(result.FirstText);
        result.Content.Should().NotBeEmpty();
        string text = result.Content[0] is TextContentItem textItem ? textItem.Text : result.FirstText;
        using var doc = JsonDocument.Parse(text);
        return doc.RootElement.Clone();
    }

    #region 1. Bot Choreography Tests

    [Fact]
    public async Task BotChoreography_PresetKongorPitClash_DispatchesTeamfightCommands()
    {
        var handler = _registry.BotChoreographyHandler;
        using var argsDoc = JsonDocument.Parse("""
        {
            "action": "preset",
            "preset": "kongor_pit_clash",
            "port": 11235
        }
        """);

        var result = await handler.HandleAsync(argsDoc.RootElement);
        var root = ParseResult(result);

        root.GetProperty("success").GetBoolean().Should().BeTrue();
        root.GetProperty("port").GetInt32().Should().Be(11235);
        root.GetProperty("preset").GetString().Should().Be("kongor_pit_clash");
        root.GetProperty("targetPipe").GetString().Should().Be(@"\\.\pipe\hon-server-11235");
        root.GetProperty("commandRoutingRuleSatisfied").GetBoolean().Should().BeTrue();

        var commands = root.GetProperty("commands");
        commands.GetArrayLength().Should().BeGreaterThanOrEqualTo(4);

        // Verify dual-channel: stdin AddFakePlayer and ipc_pipe BotOrder/BotCastAbility
        var first = commands[0];
        first.GetProperty("channel").GetString().Should().Be("stdin");
        first.GetProperty("command").GetString().Should().Contain("AddFakePlayer");

        bool hasIpcPipe = false;
        bool hasPitCoordinates = false;
        foreach (var cmd in commands.EnumerateArray())
        {
            if (cmd.GetProperty("channel").GetString() == "ipc_pipe")
            {
                hasIpcPipe = true;
                string cmdText = cmd.GetProperty("command").GetString() ?? "";
                if (cmdText.Contains("1000") || cmdText.Contains("500") || cmdText.Contains("BotCastAbility") || cmdText.Contains("BotOrder"))
                {
                    hasPitCoordinates = true;
                }
            }
        }
        hasIpcPipe.Should().BeTrue();
        hasPitCoordinates.Should().BeTrue();
    }

    [Fact]
    public async Task BotChoreography_PresetPushAndDefend_DispatchesLanePushCommands()
    {
        var handler = _registry.BotChoreographyHandler;
        using var argsDoc = JsonDocument.Parse("""
        {
            "action": "preset",
            "preset": "push_and_defend",
            "port": 11235
        }
        """);

        var result = await handler.HandleAsync(argsDoc.RootElement);
        var root = ParseResult(result);

        root.GetProperty("success").GetBoolean().Should().BeTrue();
        root.GetProperty("preset").GetString().Should().Be("push_and_defend");
        var commands = root.GetProperty("commands");
        commands.GetArrayLength().Should().BeGreaterThanOrEqualTo(4);
    }

    [Fact]
    public async Task BotChoreography_PresetGankRotation_DispatchesFlankCommands()
    {
        var handler = _registry.BotChoreographyHandler;
        using var argsDoc = JsonDocument.Parse("""
        {
            "action": "preset",
            "preset": "gank_rotation",
            "port": 11235
        }
        """);

        var result = await handler.HandleAsync(argsDoc.RootElement);
        var root = ParseResult(result);

        root.GetProperty("success").GetBoolean().Should().BeTrue();
        root.GetProperty("preset").GetString().Should().Be("gank_rotation");
        var commands = root.GetProperty("commands");
        commands.GetArrayLength().Should().BeGreaterThanOrEqualTo(4);
    }

    [Fact]
    public async Task BotChoreography_ClientTarget_RoutesThroughIpcBridge()
    {
        var mockIpc = new Mock<IGameIpcBridge>();
        mockIpc.SetupGet(m => m.IsConnected).Returns(true);
        mockIpc.Setup(m => m.ExecuteCommandAsync(It.Is<string>(s => s.Contains("bot_choreography")), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync("[TAO_RESPONSE]{\"status\":\"success\",\"preset\":\"pyro_mid_combo\",\"actions_executed\":4}[/TAO_RESPONSE]");

        var handler = new BotChoreographyHandler(gameIpcBridge: mockIpc.Object);
        using var argsDoc = JsonDocument.Parse("""
        {
            "action": "preset",
            "preset": "pyro_mid_combo",
            "target": "client"
        }
        """);

        var result = await handler.HandleAsync(argsDoc.RootElement);
        var root = ParseResult(result);

        root.GetProperty("success").GetBoolean().Should().BeTrue();
        root.GetProperty("target").GetString().Should().Be("client");
        root.GetProperty("dispatchedScript").GetString().Should().Contain("TAO.Dispatch('bot_choreography'");
        mockIpc.Verify(m => m.ExecuteCommandAsync(It.Is<string>(s => s.Contains("bot_choreography")), It.IsAny<int>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task BotChoreography_ListAndStatus_TracksActiveSessions()
    {
        var handler = new BotChoreographyHandler();

        // 1. Run a preset
        using var execDoc = JsonDocument.Parse("""
        {
            "action": "preset",
            "preset": "kongor_pit_clash",
            "port": 11999
        }
        """);
        await handler.HandleAsync(execDoc.RootElement);

        // 2. Query list
        using var listDoc = JsonDocument.Parse("""{"action": "list"}""");
        var listRes = await handler.HandleAsync(listDoc.RootElement);
        var listRoot = ParseResult(listRes);

        listRoot.GetProperty("status").GetString().Should().Be("success");
        listRoot.GetProperty("count").GetInt32().Should().BeGreaterThanOrEqualTo(1);
        var sessions = listRoot.GetProperty("sessions");
        sessions.GetArrayLength().Should().BeGreaterThanOrEqualTo(1);

        // 3. Query status for port 11999
        using var statusDoc = JsonDocument.Parse("""{"action": "status", "port": 11999}""");
        var statusRes = await handler.HandleAsync(statusDoc.RootElement);
        var statusRoot = ParseResult(statusRes);

        statusRoot.GetProperty("status").GetString().Should().Be("success");
        statusRoot.GetProperty("session").GetProperty("port").GetInt32().Should().Be(11999);
        statusRoot.GetProperty("session").GetProperty("preset").GetString().Should().Be("kongor_pit_clash");
    }

    #endregion

    #region 2. Replay Diff Tests

    [Fact]
    public async Task ReplayDiff_FindFirstDesync_BinarySearchPinpointsExactTick()
    {
        var handler = _registry.ReplayDiffHandler;
        string serverPath = Path.GetTempFileName();
        string clientPath = Path.GetTempFileName();
        try
        {
            byte[] bytesA = new byte[600];
            byte[] bytesB = new byte[600];
            bytesB[250] = 0xFF; // Injected desync at tick/offset 250
            await File.WriteAllBytesAsync(serverPath, bytesA);
            await File.WriteAllBytesAsync(clientPath, bytesB);

            using var argsDoc = JsonDocument.Parse($$"""
            {
                "action": "find_first_desync",
                "server_replay": "{{serverPath.Replace("\\", "\\\\")}}",
                "client_replay": "{{clientPath.Replace("\\", "\\\\")}}",
                "start_tick": 100,
                "end_tick": 500,
                "tolerance": 0.01
            }
            """);

            var result = await handler.HandleAsync(argsDoc.RootElement);
            var root = ParseResult(result);

            root.GetProperty("status").GetString().Should().Be("success");
            root.GetProperty("action").GetString().Should().Be("find_first_desync");
            root.GetProperty("desyncFound").GetBoolean().Should().BeTrue();
            int exactTick = root.GetProperty("exactDesyncTick").GetInt32();
            exactTick.Should().BeInRange(100, 500);
            root.GetProperty("searchIterations").GetInt32().Should().BeGreaterThan(0);

            var divergences = root.GetProperty("entityDivergences");
            divergences.GetArrayLength().Should().BeGreaterThan(0);
            var firstDiv = divergences[0];
            firstDiv.GetProperty("positionDelta").GetDouble().Should().BeGreaterThan(0.0);
        }
        finally
        {
            if (File.Exists(serverPath)) File.Delete(serverPath);
            if (File.Exists(clientPath)) File.Delete(clientPath);
        }
    }

    [Fact]
    public async Task ReplayDiff_CompareTicks_Detailed10EntityDeltas()
    {
        var handler = _registry.ReplayDiffHandler;
        string serverPath = Path.GetTempFileName();
        string clientPath = Path.GetTempFileName();
        try
        {
            await File.WriteAllBytesAsync(serverPath, new byte[] { 1, 2, 3, 4 });
            await File.WriteAllBytesAsync(clientPath, new byte[] { 1, 2, 3, 4 });

            using var argsDoc = JsonDocument.Parse($$"""
            {
                "action": "compare_ticks",
                "server_replay": "{{serverPath.Replace("\\", "\\\\")}}",
                "client_replay": "{{clientPath.Replace("\\", "\\\\")}}",
                "tick": 1200,
                "tolerance": 0.01
            }
            """);

            var result = await handler.HandleAsync(argsDoc.RootElement);
            var root = ParseResult(result);

            root.GetProperty("status").GetString().Should().Be("success");
            root.GetProperty("entitiesCompared").GetInt32().Should().Be(10);

            var entities = root.GetProperty("results");
            entities.GetArrayLength().Should().Be(10);

            // Check Legion and Hellbourne entities
            int legionCount = 0;
            int hellbourneCount = 0;
            foreach (var entity in entities.EnumerateArray())
            {
                string team = entity.GetProperty("team").GetString() ?? "";
                if (team == "Legion") legionCount++;
                if (team == "Hellbourne") hellbourneCount++;

                entity.GetProperty("entityId").GetInt32().Should().BeInRange(1, 10);
                entity.GetProperty("entityName").GetString().Should().NotBeNullOrEmpty();
                entity.TryGetProperty("positionDelta", out _).Should().BeTrue();
                entity.TryGetProperty("hpDelta", out _).Should().BeTrue();
                entity.TryGetProperty("manaDelta", out _).Should().BeTrue();
                entity.TryGetProperty("inSync", out _).Should().BeTrue();
            }

            legionCount.Should().Be(5);
            hellbourneCount.Should().Be(5);
        }
        finally
        {
            if (File.Exists(serverPath)) File.Delete(serverPath);
            if (File.Exists(clientPath)) File.Delete(clientPath);
        }
    }

    #endregion

    #region 3. Replay Inspect Combat Log Tests

    [Fact]
    public async Task ReplayInspect_CombatLog_ExtractsAndFiltersTimeline()
    {
        string tempDir = Path.Combine(Path.GetTempPath(), $"replay_inspect_test_{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        string replayPath = Path.Combine(tempDir, "test_combat_log.honreplay");

        try
        {
            using (ZipArchive archive = ZipFile.Open(replayPath, ZipArchiveMode.Create))
            {
                ZipArchiveEntry summaryEntry = archive.CreateEntry("summary.xml");
                using StreamWriter writer = new(summaryEntry.Open());
                writer.WriteLine("<match id='99500' map='caldavar' duration='2400' winner='1' version='5.0.0'>");
                writer.WriteLine("  <player id='1' name='MidPyro' hero='Hero_Pyromancer' kills='15' deaths='3' assists='10' gold='18500' />");
                writer.WriteLine("  <player id='2' name='GankValk' hero='Hero_Valkyrie' kills='8' deaths='4' assists='14' gold='15200' />");
                writer.WriteLine("</match>");
            }

            var handler = _registry.ReplayInspectHandler;

            // 1. Unfiltered combat log
            using var unfilteredDoc = JsonDocument.Parse($$"""
            {
                "action": "combat_log",
                "path": "{{replayPath.Replace('\\', '/')}}",
                "allowSimulation": true
            }
            """);

            var result = await handler.HandleAsync(unfilteredDoc.RootElement);
            var root = ParseResult(result);

            root.GetProperty("status").GetString().Should().Be("success");
            root.GetProperty("matchId").GetString().Should().Be("99500");
            root.GetProperty("totalEvents").GetInt32().Should().BeGreaterThan(0);

            var events = root.GetProperty("events");
            events.GetArrayLength().Should().BeGreaterThanOrEqualTo(4);

            // Verify event types exist
            var eventTypes = events.EnumerateArray().Select(e => e.GetProperty("type").GetString()).ToList();
            eventTypes.Should().Contain("FirstBlood");
            eventTypes.Should().Contain("HeroKill");
            eventTypes.Should().Contain("KongorSlay");

            // 2. Filtered by hero
            using var heroFilterDoc = JsonDocument.Parse($$"""
            {
                "action": "combat_log",
                "path": "{{replayPath.Replace('\\', '/')}}",
                "allowSimulation": true,
                "hero": "Hero_Pyromancer"
            }
            """);

            var heroRes = await handler.HandleAsync(heroFilterDoc.RootElement);
            var heroRoot = ParseResult(heroRes);

            var filteredEvents = heroRoot.GetProperty("events");
            filteredEvents.GetArrayLength().Should().BeGreaterThan(0);
            foreach (var ev in filteredEvents.EnumerateArray())
            {
                string h = ev.GetProperty("hero").GetString() ?? "";
                string v = ev.GetProperty("victimHero").GetString() ?? "";
                (h.Contains("Pyromancer") || v.Contains("Pyromancer")).Should().BeTrue();
            }

            // 3. Filtered by eventType
            using var typeFilterDoc = JsonDocument.Parse($$"""
            {
                "action": "combat_log",
                "path": "{{replayPath.Replace('\\', '/')}}",
                "allowSimulation": true,
                "eventType": "KongorSlay"
            }
            """);

            var typeRes = await handler.HandleAsync(typeFilterDoc.RootElement);
            var typeRoot = ParseResult(typeRes);

            foreach (var ev in typeRoot.GetProperty("events").EnumerateArray())
            {
                ev.GetProperty("type").GetString().Should().Be("KongorSlay");
            }
        }
        finally
        {
            if (Directory.Exists(tempDir))
            {
                Directory.Delete(tempDir, true);
            }
        }
    }

    #endregion

}
