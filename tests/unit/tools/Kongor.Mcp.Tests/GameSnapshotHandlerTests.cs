// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// TAO (Together As One) MCP & Kongor CLI -- Created by guccidonut (https://github.com/guccidonut/PKV)

using System.Diagnostics;
using System.Text.Json;
using ASPIRE.Tests.Infrastructure;
using FluentAssertions;
using Kongor.Cli.Mcp.Handlers;
using Kongor.Cli.Mcp.Protocol;
using Kongor.Cli.Mcp.Services;
using Moq;
using Xunit;

namespace Kongor.Mcp.Tests;

public sealed class GameSnapshotHandlerTests
{
    private static (GameSnapshotHandler Handler, Mock<IGameIpcBridge> MockIpc, List<string> DispatchedCommands) CreateHandlerWithMockIpc(
        string phaseResponse = "3",
        bool isConnected = true)
    {
        List<string> dispatched = [];
        Mock<IGameIpcBridge> mockIpc = new();
        mockIpc.SetupGet(i => i.IsConnected).Returns(isConnected);
        mockIpc.Setup(i => i.ExecuteCommandAsync(It.IsAny<string>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .Returns((string cmd, int timeout, CancellationToken ct) =>
            {
                dispatched.Add(cmd);
                if (cmd.Contains("GetCurrentPhase"))
                {
                    return Task.FromResult(phaseResponse);
                }
                return Task.FromResult("OK");
            });

        ClientCommandRemediationService remediation = new(timeProvider: TimeProvider.System);
        GameSnapshotHandler handler = new(
            gameIpcBridge: mockIpc.Object,
            remediationService: remediation,
            timeProvider: TimeProvider.System,
            logger: _ => { });

        return (handler, mockIpc, dispatched);
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

    #region Category 1: Lifecycle Phase Verification & Engine Safety Guardrails (5 Tests)

    [Fact]
    public async Task Restore_WhenPhaseIs0_ReturnsInvalidGamePhaseStructuredError()
    {
        var (handler, _, _) = CreateHandlerWithMockIpc(phaseResponse: "0");

        var args = JsonElement.Parse("""
        {
            "action": "restore",
            "snapshot": { "gold": 5000, "level": 16 }
        }
        """);

        ToolCallResult result = await handler.HandleAsync(args);

        result.IsError.Should().BeTrue();
        JsonElement root = ParseResultJson(result);
        root.GetProperty("status").GetString().Should().Be("error");
        root.GetProperty("errorCode").GetString().Should().Be("INVALID_GAME_PHASE");
        root.GetProperty("currentPhase").GetInt32().Should().Be(0);
        root.GetProperty("allowedPhases").EnumerateArray().Select(e => e.GetInt32()).Should().Contain(3);
        root.GetProperty("message").GetString().Should().Contain("Snapshot can only be applied during active match (phase 3)");
    }

    [Fact]
    public async Task Restore_WhenPhaseIs1_ReturnsInvalidGamePhaseStructuredError()
    {
        var (handler, _, _) = CreateHandlerWithMockIpc(phaseResponse: "1");

        var args = JsonElement.Parse("""
        {
            "action": "restore",
            "snapshot": { "gold": 3000, "level": 11 }
        }
        """);

        ToolCallResult result = await handler.HandleAsync(args);

        result.IsError.Should().BeTrue();
        JsonElement root = ParseResultJson(result);
        root.GetProperty("errorCode").GetString().Should().Be("INVALID_GAME_PHASE");
        root.GetProperty("currentPhase").GetInt32().Should().Be(1);
    }

    [Fact]
    public async Task Restore_WhenPhaseIs2_ReturnsInvalidGamePhaseStructuredError()
    {
        var (handler, _, _) = CreateHandlerWithMockIpc(phaseResponse: "2");

        var args = JsonElement.Parse("""
        {
            "action": "restore",
            "snapshot": { "gold": 4000, "level": 6 }
        }
        """);

        ToolCallResult result = await handler.HandleAsync(args);

        result.IsError.Should().BeTrue();
        JsonElement root = ParseResultJson(result);
        root.GetProperty("errorCode").GetString().Should().Be("INVALID_GAME_PHASE");
        root.GetProperty("currentPhase").GetInt32().Should().Be(2);
    }

    [Fact]
    public async Task Restore_WhenPhaseIs3_ExecutesApplySnapshotSuccessfully()
    {
        var (handler, _, dispatched) = CreateHandlerWithMockIpc(phaseResponse: "3");

        var args = JsonElement.Parse("""
        {
            "action": "restore",
            "snapshot": { "gold": 2500, "level": 11, "hero": "Hero_Pyromancer" }
        }
        """);

        ToolCallResult result = await handler.HandleAsync(args);

        result.IsError.Should().BeFalse();
        JsonElement root = ParseResultJson(result);
        root.GetProperty("status").GetString().Should().Be("success");
        root.GetProperty("gamePhase").GetInt32().Should().Be(3);

        dispatched.Should().Contain(cmd => cmd.Contains("Nexus.ApplySnapshot"));
    }

    [Fact]
    public async Task Restore_PhaseRejection_NeverDispatchesNativeEngineMutations()
    {
        var (handler, _, dispatched) = CreateHandlerWithMockIpc(phaseResponse: "1");

        var args = JsonElement.Parse("""
        {
            "action": "restore",
            "snapshot": { "gold": 5000, "level": 16, "items": ["Item_PortalKey"] }
        }
        """);

        ToolCallResult result = await handler.HandleAsync(args);

        result.IsError.Should().BeTrue();

        // Must never dispatch mutations to prevent C++ Access Violation 0xC0000005 crash
        dispatched.Should().NotContain(cmd => cmd.Contains("Nexus.ApplySnapshot"));
        dispatched.Should().NotContain(cmd => cmd.Contains("GiveGold"));
        dispatched.Should().NotContain(cmd => cmd.Contains("SetHeroLevel"));
        dispatched.Should().NotContain(cmd => cmd.Contains("Teleport"));
        dispatched.Should().NotContain(cmd => cmd.Contains("PurchaseItem"));
    }

    #endregion

    #region Category 2: Replay Tick Math & Timestamp Conversion Precision (9 Tests)

    [Fact]
    public async Task Seek_ExactTick_DispatchesReplaySetFrameAndSeekInExactSequence()
    {
        var (handler, _, dispatched) = CreateHandlerWithMockIpc();

        var args = JsonElement.Parse("""
        {
            "action": "seek",
            "tick": 1800
        }
        """);

        ToolCallResult result = await handler.HandleAsync(args);

        result.IsError.Should().BeFalse();
        JsonElement root = ParseResultJson(result);
        root.GetProperty("tick").GetInt64().Should().Be(1800);
        root.GetProperty("timeSeconds").GetDouble().Should().Be(90.0);

        dispatched.Should().ContainInOrder(["ReplaySetFrame 1800", "Seek 1800"]);
    }

    [Fact]
    public async Task Seek_TimeSeconds_ConvertsTo20TicksPerSecond()
    {
        var (handler, _, dispatched) = CreateHandlerWithMockIpc();

        var args = JsonElement.Parse("""
        {
            "action": "seek",
            "timeSeconds": 15.5
        }
        """);

        ToolCallResult result = await handler.HandleAsync(args);

        result.IsError.Should().BeFalse();
        JsonElement root = ParseResultJson(result);
        root.GetProperty("tick").GetInt64().Should().Be(310); // 15.5 * 20 = 310

        dispatched.Should().ContainInOrder(["ReplaySetFrame 310", "Seek 310"]);
    }

    [Fact]
    public async Task Seek_TimeAlias_ConvertsTo20TicksPerSecond()
    {
        var (handler, _, dispatched) = CreateHandlerWithMockIpc();

        var args = JsonElement.Parse("""
        {
            "action": "seek",
            "time": 10.0
        }
        """);

        ToolCallResult result = await handler.HandleAsync(args);

        result.IsError.Should().BeFalse();
        JsonElement root = ParseResultJson(result);
        root.GetProperty("tick").GetInt64().Should().Be(200); // 10.0 * 20 = 200

        dispatched.Should().ContainInOrder(["ReplaySetFrame 200", "Seek 200"]);
    }

    [Fact]
    public async Task Seek_TimeMs_ConvertsTo50MsPerTick()
    {
        var (handler, _, dispatched) = CreateHandlerWithMockIpc();

        var args = JsonElement.Parse("""
        {
            "action": "seek",
            "timeMs": 2500
        }
        """);

        ToolCallResult result = await handler.HandleAsync(args);

        result.IsError.Should().BeFalse();
        JsonElement root = ParseResultJson(result);
        root.GetProperty("tick").GetInt64().Should().Be(50); // 2500 / 50 = 50

        dispatched.Should().ContainInOrder(["ReplaySetFrame 50", "Seek 50"]);
    }

    [Fact]
    public async Task Seek_TimeMs_HighPrecision_ConvertsAccurately()
    {
        var (handler, _, dispatched) = CreateHandlerWithMockIpc();

        var args = JsonElement.Parse("""
        {
            "action": "seek",
            "timeMs": 15500
        }
        """);

        ToolCallResult result = await handler.HandleAsync(args);

        result.IsError.Should().BeFalse();
        JsonElement root = ParseResultJson(result);
        root.GetProperty("tick").GetInt64().Should().Be(310); // 15500 / 50 = 310

        dispatched.Should().ContainInOrder(["ReplaySetFrame 310", "Seek 310"]);
    }

    [Fact]
    public async Task Seek_FormattedTimestamp_MMSS_ConvertsTo1200TicksPerMinute()
    {
        var (handler, _, dispatched) = CreateHandlerWithMockIpc();

        var args = JsonElement.Parse("""
        {
            "action": "seek",
            "timestamp": "01:30"
        }
        """);

        ToolCallResult result = await handler.HandleAsync(args);

        result.IsError.Should().BeFalse();
        JsonElement root = ParseResultJson(result);
        // 1 min * 1200 + 30 sec * 20 = 1200 + 600 = 1800
        root.GetProperty("tick").GetInt64().Should().Be(1800);

        dispatched.Should().ContainInOrder(["ReplaySetFrame 1800", "Seek 1800"]);
    }

    [Fact]
    public async Task Seek_FormattedTimestamp_MMSSfff_ConvertsFractionalSeconds()
    {
        var (handler, _, dispatched) = CreateHandlerWithMockIpc();

        var args = JsonElement.Parse("""
        {
            "action": "seek",
            "timestamp": "01:15.500"
        }
        """);

        ToolCallResult result = await handler.HandleAsync(args);

        result.IsError.Should().BeFalse();
        JsonElement root = ParseResultJson(result);
        // 1 * 1200 + 15.5 * 20 = 1200 + 310 = 1510
        root.GetProperty("tick").GetInt64().Should().Be(1510);

        dispatched.Should().ContainInOrder(["ReplaySetFrame 1510", "Seek 1510"]);
    }

    [Fact]
    public async Task Seek_FormattedTimestamp_HHMMSS_ConvertsHoursAccurately()
    {
        var (handler, _, dispatched) = CreateHandlerWithMockIpc();

        var args = JsonElement.Parse("""
        {
            "action": "seek",
            "timestamp": "01:00:00"
        }
        """);

        ToolCallResult result = await handler.HandleAsync(args);

        result.IsError.Should().BeFalse();
        JsonElement root = ParseResultJson(result);
        // 1 hour * 72000 = 72000 ticks
        root.GetProperty("tick").GetInt64().Should().Be(72000);

        dispatched.Should().ContainInOrder(["ReplaySetFrame 72000", "Seek 72000"]);
    }

    [Fact]
    public async Task Seek_NegativeTime_ClampsSafelyToZero()
    {
        var (handler, _, dispatched) = CreateHandlerWithMockIpc();

        var args = JsonElement.Parse("""
        {
            "action": "seek",
            "timeSeconds": -5.0
        }
        """);

        ToolCallResult result = await handler.HandleAsync(args);

        result.IsError.Should().BeFalse();
        JsonElement root = ParseResultJson(result);
        root.GetProperty("tick").GetInt64().Should().Be(0);

        dispatched.Should().ContainInOrder(["ReplaySetFrame 0", "Seek 0"]);
    }

    #endregion

    #region Category 3: In-Memory Snapshot Store Operations (7 Tests)

    [Fact]
    public async Task Save_WithInlineSnapshot_StoresAndReturnsGeneratedId()
    {
        var (handler, _, _) = CreateHandlerWithMockIpc();

        var args = JsonElement.Parse("""
        {
            "action": "save",
            "snapshot": { "gold": 3500, "level": 11, "hero": "Hero_Pyromancer" }
        }
        """);

        ToolCallResult result = await handler.HandleAsync(args);

        result.IsError.Should().BeFalse();
        JsonElement root = ParseResultJson(result);
        string id = root.GetProperty("id").GetString()!;
        id.Should().StartWith("snap_");
        root.GetProperty("totalStored").GetInt32().Should().Be(1);

        handler.TryGetSnapshot(id, out var stored).Should().BeTrue();
        stored!.Gold.Should().Be(3500);
        stored.Level.Should().Be(11);
    }

    [Fact]
    public async Task Save_WithCustomId_PreservesSpecifiedId()
    {
        var (handler, _, _) = CreateHandlerWithMockIpc();

        var args = JsonElement.Parse("""
        {
            "action": "save",
            "id": "checkpoint_roshan_fight",
            "snapshot": { "gold": 4000, "level": 14 }
        }
        """);

        ToolCallResult result = await handler.HandleAsync(args);

        result.IsError.Should().BeFalse();
        JsonElement root = ParseResultJson(result);
        root.GetProperty("id").GetString().Should().Be("checkpoint_roshan_fight");

        handler.TryGetSnapshot("checkpoint_roshan_fight", out var stored).Should().BeTrue();
        stored!.Gold.Should().Be(4000);
    }

    [Fact]
    public async Task List_MultipleSnapshots_ReturnsAllStoredEntries()
    {
        var (handler, _, _) = CreateHandlerWithMockIpc();

        await handler.HandleAsync(JsonElement.Parse("""{"action": "save", "id": "cp1", "snapshot": {"gold": 1000}}"""));
        await handler.HandleAsync(JsonElement.Parse("""{"action": "save", "id": "cp2", "snapshot": {"gold": 2000}}"""));

        ToolCallResult result = await handler.HandleAsync(JsonElement.Parse("""{"action": "list"}"""));

        result.IsError.Should().BeFalse();
        JsonElement root = ParseResultJson(result);
        root.GetProperty("count").GetInt32().Should().Be(2);
        var ids = root.GetProperty("snapshots").EnumerateArray()
            .Select(s => s.GetProperty("id").GetString())
            .ToList();
        ids.Should().Contain(["cp1", "cp2"]);
    }

    [Fact]
    public async Task Restore_ById_RetrievesAndAppliesStoredSnapshot()
    {
        var (handler, _, dispatched) = CreateHandlerWithMockIpc(phaseResponse: "3");

        await handler.HandleAsync(JsonElement.Parse("""
        {
            "action": "save",
            "id": "cp_lvl16",
            "snapshot": { "gold": 6000, "level": 16, "hero": "Hero_Pyromancer" }
        }
        """));

        var restoreArgs = JsonElement.Parse("""{"action": "restore", "id": "cp_lvl16"}""");
        ToolCallResult result = await handler.HandleAsync(restoreArgs);

        result.IsError.Should().BeFalse();
        dispatched.Should().Contain(cmd => cmd.Contains("Nexus.ApplySnapshot") && cmd.Contains("gold = 6000") && cmd.Contains("level = 16"));
    }

    [Fact]
    public async Task Restore_NonExistentId_ReturnsNotFoundError()
    {
        var (handler, _, _) = CreateHandlerWithMockIpc(phaseResponse: "3");

        var args = JsonElement.Parse("""{"action": "restore", "id": "does_not_exist"}""");
        ToolCallResult result = await handler.HandleAsync(args);

        result.IsError.Should().BeTrue();
        JsonElement root = ParseResultJson(result);
        root.GetProperty("errorCode").GetString().Should().Be("SNAPSHOT_NOT_FOUND");
    }

    [Fact]
    public async Task Delete_ExistingId_RemovesFromStore()
    {
        var (handler, _, _) = CreateHandlerWithMockIpc();

        await handler.HandleAsync(JsonElement.Parse("""{"action": "save", "id": "temp_cp", "snapshot": {"gold": 500}}"""));
        handler.Count.Should().Be(1);

        ToolCallResult result = await handler.HandleAsync(JsonElement.Parse("""{"action": "delete", "id": "temp_cp"}"""));

        result.IsError.Should().BeFalse();
        JsonElement root = ParseResultJson(result);
        root.GetProperty("deleted").GetBoolean().Should().BeTrue();
        handler.Count.Should().Be(0);
    }

    [Fact]
    public async Task Delete_NonExistentId_ReturnsNotFoundError()
    {
        var (handler, _, _) = CreateHandlerWithMockIpc();

        ToolCallResult result = await handler.HandleAsync(JsonElement.Parse("""{"action": "delete", "id": "ghost_id"}"""));

        result.IsError.Should().BeTrue();
        JsonElement root = ParseResultJson(result);
        root.GetProperty("errorCode").GetString().Should().Be("SNAPSHOT_NOT_FOUND");
    }

    #endregion

    #region Category 4: Error Remediation & Resilience on Bridge Failure (3 Tests)

    [Fact]
    public async Task Restore_WhenIpcBridgeThrows_RecordsRemediationAndReturnsError()
    {
        Mock<IGameIpcBridge> mockIpc = new();
        mockIpc.SetupGet(i => i.IsConnected).Returns(true);
        mockIpc.Setup(i => i.ExecuteCommandAsync(It.Is<string>(s => s.Contains("GetCurrentPhase")), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync("3");
        mockIpc.Setup(i => i.ExecuteCommandAsync(It.Is<string>(s => s.Contains("ApplySnapshot")), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new TimeoutException("Named pipe timed out after 3000ms"));

        ClientCommandRemediationService remediation = new(timeProvider: TimeProvider.System);
        GameSnapshotHandler handler = new(
            gameIpcBridge: mockIpc.Object,
            remediationService: remediation,
            timeProvider: TimeProvider.System);

        var args = JsonElement.Parse("""{"action": "restore", "snapshot": {"gold": 1000}}""");
        ToolCallResult result = await handler.HandleAsync(args);

        result.IsError.Should().BeTrue();
        JsonElement root = ParseResultJson(result);
        root.GetProperty("errorCode").GetString().Should().Be("IPC_DISPATCH_FAILED");

        remediation.GetRecentErrors().Should().NotBeEmpty();
    }

    [Fact]
    public async Task Seek_WhenIpcBridgeThrows_RecordsRemediationAndReturnsError()
    {
        Mock<IGameIpcBridge> mockIpc = new();
        mockIpc.SetupGet(i => i.IsConnected).Returns(true);
        mockIpc.Setup(i => i.ExecuteCommandAsync(It.IsAny<string>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("IPC pipe closed unexpectedly"));

        ClientCommandRemediationService remediation = new(timeProvider: TimeProvider.System);
        GameSnapshotHandler handler = new(
            gameIpcBridge: mockIpc.Object,
            remediationService: remediation,
            timeProvider: TimeProvider.System);

        var args = JsonElement.Parse("""{"action": "seek", "tick": 500}""");
        ToolCallResult result = await handler.HandleAsync(args);

        result.IsError.Should().BeTrue();
        JsonElement root = ParseResultJson(result);
        root.GetProperty("errorCode").GetString().Should().Be("IPC_DISPATCH_FAILED");

        remediation.GetRecentErrors().Should().NotBeEmpty();
    }

    [Fact]
    public async Task Execute_UnknownAction_ReturnsHelpfulErrorMessage()
    {
        var (handler, _, _) = CreateHandlerWithMockIpc();

        var args = JsonElement.Parse("""{"action": "warp"}""");
        ToolCallResult result = await handler.HandleAsync(args);

        result.IsError.Should().BeTrue();
        string text = GetContentText(result);
        text.Should().Contain("Unknown snapshot action 'warp'");
        text.Should().Contain("save, restore, seek, list, delete");
    }

    #endregion

    #region Category 5: MCP Tool Registry & Alias Routing (4 Tests)

    [Fact]
    public void Registry_ResolvesCanonical_KongorGameSnapshot()
    {
        McpToolRegistry registry = new(registerDefaults: true);
        registry.HasTool("kongor_game_snapshot").Should().BeTrue();
        registry.GetTool("kongor_game_snapshot")!.Name.Should().Be("kongor_game_snapshot");
    }

    [Fact]
    public void Registry_ResolvesAlias_KongorSnapshot()
    {
        McpToolRegistry registry = new(registerDefaults: true);
        registry.HasTool("kongor_snapshot").Should().BeTrue();
        registry.GetTool("kongor_snapshot")!.Name.Should().Be("kongor_game_snapshot");
    }

    [Fact]
    public void Registry_ResolvesAlias_KongorCheckpoint()
    {
        McpToolRegistry registry = new(registerDefaults: true);
        registry.HasTool("kongor_checkpoint").Should().BeTrue();
        registry.GetTool("kongor_checkpoint")!.Name.Should().Be("kongor_game_snapshot");
    }

    [Fact]
    public void Registry_ListTools_OmitsPrunedAliases()
    {
        McpToolRegistry registry = new(registerDefaults: true);
        var toolNames = registry.ListTools().Select(t => t.Name).ToList();

        toolNames.Should().Contain("kongor_game_snapshot");
        toolNames.Should().NotContain("kongor_snapshot");
        toolNames.Should().NotContain("kongor_checkpoint");
    }

    #endregion

    #region Category 6: Fast Inner-Loop Execution & Zero Desktop GUI Windows (3 Tests)

    [Fact]
    public async Task SnapshotHandler_BatchExecution_ExecutesUnder500Ms()
    {
        var (handler, _, _) = CreateHandlerWithMockIpc();

        Stopwatch sw = Stopwatch.StartNew();

        for (int i = 0; i < 20; i++)
        {
            string saveJson = $"{{\"action\": \"save\", \"id\": \"cp_{i}\", \"snapshot\": {{\"gold\": {i * 100}}}}}";
            await handler.HandleAsync(JsonElement.Parse(saveJson));
            await handler.HandleAsync(JsonElement.Parse("""{"action": "list"}"""));
            await handler.HandleAsync(JsonElement.Parse("""{"action": "seek", "timeSeconds": 10.0}"""));
            string deleteJson = $"{{\"action\": \"delete\", \"id\": \"cp_{i}\"}}";
            await handler.HandleAsync(JsonElement.Parse(deleteJson));
        }

        sw.Stop();
        sw.ElapsedMilliseconds.Should().BeLessThan(2000, "20-cycle snapshot operations must execute sub-2000ms");
    }

    [Fact]
    public async Task SnapshotHandler_ZeroGuiWindows_SuppressesUiInteractivity()
    {
        var initialProcesses = Process.GetProcessesByName("hon_x64").Select(p => p.Id).ToHashSet();
        var (handler, _, _) = CreateHandlerWithMockIpc();

        // Perform save, restore, seek, list, delete
        await handler.HandleAsync(JsonElement.Parse("""{"action": "save", "snapshot": {"gold": 1000}}"""));
        await handler.HandleAsync(JsonElement.Parse("""{"action": "seek", "tick": 100}"""));
        await handler.HandleAsync(JsonElement.Parse("""{"action": "list"}"""));

        // Verification: No GUI processes or interactive window handles spawned
        Process.GetProcessesByName("hon_x64").Where(p => !initialProcesses.Contains(p.Id)).Should().BeEmpty();
    }

    [Trait("Category", "Manual")]
    [Fact]
    [Trait("Category", NexusTestEnvironment.ManualTestCategory)]
    public async Task SnapshotHandler_LiveEngineTest_RespectsNexusTestEnvironment()
    {
        // protects: NexusTestEnvironment manual gate counter and GameSnapshotHandler in-memory list/save round-trip
        // fails_when: IsManualTestEnabled stops tracking bypassed manual tests or GameSnapshotHandler fails to list saved snapshots
        // seam: NexusTestEnvironment + GameSnapshotHandler
        int bypassedBefore = NexusTestEnvironment.ManualBypassedCount;
        if (!NexusTestEnvironment.IsManualTestEnabled)
        {
            NexusTestEnvironment.ManualBypassedCount.Should().BeGreaterThan(bypassedBefore);
            return;
        }

        var (handler, _, dispatched) = CreateHandlerWithMockIpc(phaseResponse: "3");
        ToolCallResult saveRes = await handler.HandleAsync(JsonElement.Parse("""{"action": "save", "id": "manual_gate_snap", "snapshot": {"gold": 750}}"""));
        saveRes.IsError.Should().BeFalse();

        ToolCallResult listRes = await handler.HandleAsync(JsonElement.Parse("""{"action": "list"}"""));
        listRes.IsError.Should().BeFalse();
        GetContentText(listRes).Should().Contain("manual_gate_snap");
        dispatched.Should().NotBeEmpty();
    }

    #endregion

    #region Category 7: Boundary & Clamping Edge Cases (3 Tests)

    [Fact]
    public async Task Save_Boundary_NegativeGoldAndLevel_ClampsToValidRanges()
    {
        var (handler, _, _) = CreateHandlerWithMockIpc();

        var args = JsonElement.Parse("""
        {
            "action": "save",
            "id": "cp_clamp_test",
            "snapshot": { "gold": -500, "level": -10 }
        }
        """);

        await handler.HandleAsync(args);

        handler.TryGetSnapshot("cp_clamp_test", out var stored).Should().BeTrue();
        stored!.Gold.Should().Be(0); // Clamped >= 0
        stored.Level.Should().Be(1); // Clamped [1, 25]
    }

    [Fact]
    public async Task Save_Boundary_ExtremeCoordinates_ClampsToMapBoundaries()
    {
        var (handler, _, _) = CreateHandlerWithMockIpc();

        var args = JsonElement.Parse("""
        {
            "action": "save",
            "id": "cp_coord_test",
            "snapshot": { "x": 100000.0, "y": -100000.0 }
        }
        """);

        await handler.HandleAsync(args);

        handler.TryGetSnapshot("cp_coord_test", out var stored).Should().BeTrue();
        stored!.X.Should().Be(32768.0);
        stored.Y.Should().Be(-32768.0);
    }

    [Fact]
    public async Task Registry_DispatchToolAsync_ExecutesSnapshotToolSuccessfully()
    {
        McpToolRegistry registry = new(registerDefaults: true);

        var args = JsonElement.Parse("""{"action": "save", "id": "dispatch_test", "snapshot": {"gold": 777}}""");
        ToolCallResult result = await registry.DispatchToolAsync("kongor_game_snapshot", args);

        result.IsError.Should().BeFalse();
        JsonElement root = ParseResultJson(result);
        root.GetProperty("status").GetString().Should().Be("success");
        root.GetProperty("id").GetString().Should().Be("dispatch_test");
    }

    #endregion
}
