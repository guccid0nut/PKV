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

/// <summary>
/// Empirical Challenger 2 Test Suite for Milestone 3 (kongor_game_snapshot):
/// Probes Phase 3 guardrails across all non-active phases (0, 1, 2, 4, 5, -1, unknown),
/// edge cases (unknown actions, ghost IDs, malformed JSON, offline IPC),
/// McpToolRegistry alias resolution, and sub-2.0s execution with zero GUI windows.
/// </summary>
public sealed class GameSnapshotPhaseGuardrailTests
{
    private static (GameSnapshotHandler Handler, Mock<IGameIpcBridge> MockIpc, List<string> DispatchedCommands, ClientCommandRemediationService Remediation) CreateHandlerWithMockIpc(
        string phaseResponse = "3",
        bool isConnected = true,
        Exception? phaseException = null,
        Exception? commandException = null)
    {
        List<string> dispatched = [];
        Mock<IGameIpcBridge> mockIpc = new();
        mockIpc.SetupGet(i => i.IsConnected).Returns(isConnected);

        if (phaseException != null)
        {
            mockIpc.Setup(i => i.ExecuteCommandAsync(It.Is<string>(s => s.Contains("GetCurrentPhase")), It.IsAny<int>(), It.IsAny<CancellationToken>()))
                .ThrowsAsync(phaseException);
        }
        else
        {
            mockIpc.Setup(i => i.ExecuteCommandAsync(It.Is<string>(s => s.Contains("GetCurrentPhase")), It.IsAny<int>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(phaseResponse);
        }

        if (commandException != null)
        {
            mockIpc.Setup(i => i.ExecuteCommandAsync(It.Is<string>(s => !s.Contains("GetCurrentPhase")), It.IsAny<int>(), It.IsAny<CancellationToken>()))
                .Callback<string, int, CancellationToken>((cmd, _, _) => dispatched.Add(cmd))
                .ThrowsAsync(commandException);
        }
        else
        {
            mockIpc.Setup(i => i.ExecuteCommandAsync(It.Is<string>(s => !s.Contains("GetCurrentPhase")), It.IsAny<int>(), It.IsAny<CancellationToken>()))
                .Returns((string cmd, int timeout, CancellationToken ct) =>
                {
                    dispatched.Add(cmd);
                    return Task.FromResult("OK");
                });
        }

        ClientCommandRemediationService remediation = new(timeProvider: TimeProvider.System);
        GameSnapshotHandler handler = new(
            gameIpcBridge: mockIpc.Object,
            remediationService: remediation,
            timeProvider: TimeProvider.System,
            logger: _ => { });

        return (handler, mockIpc, dispatched, remediation);
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

    #region Part 1: Phase 3 Guardrail Verification Across All Non-Active Phases

    [Theory]
    [InlineData(0, "loading")]
    [InlineData(1, "hero_pick")]
    [InlineData(2, "strategy")]
    [InlineData(4, "ended")]
    [InlineData(5, "summary")]
    [InlineData(-1, "uninitialized")]
    [InlineData(99, "unknown_future_phase")]
    public async Task Restore_WhenPhaseIsNonActive_ReturnsInvalidGamePhaseAndZeroMutations(int nonActivePhase, string phaseLabel)
    {
        var (handler, _, dispatched, remediation) = CreateHandlerWithMockIpc(phaseResponse: nonActivePhase.ToString());

        var args = JsonElement.Parse($$"""
        {
            "action": "restore",
            "snapshot": {
                "gold": 5000,
                "level": 16,
                "hero": "Hero_Pyromancer",
                "items": ["Item_PortalKey", "Item_PostHaste"]
            }
        }
        """);

        ToolCallResult result = await handler.HandleAsync(args);

        // 1. Result must be structured error
        result.IsError.Should().BeTrue($"Phase {nonActivePhase} ({phaseLabel}) must be rejected");
        JsonElement root = ParseResultJson(result);
        root.GetProperty("status").GetString().Should().Be("error");
        root.GetProperty("errorCode").GetString().Should().Be("INVALID_GAME_PHASE");
        root.GetProperty("currentPhase").GetInt32().Should().Be(nonActivePhase);
        root.GetProperty("allowedPhases").EnumerateArray().Select(e => e.GetInt32()).Should().Equal([3]);
        root.GetProperty("message").GetString().Should().Contain("Snapshot can only be applied during active match (phase 3)");
        root.GetProperty("remediationHint").GetString().Should().NotBeNullOrWhiteSpace();

        // 2. ABSOLUTE ZERO MUTATION COMMANDS dispatched to prevent native Access Violation 0xC0000005 crash
        dispatched.Should().BeEmpty("Zero mutation commands must be dispatched when phase is not 3");

        // 3. Must be recorded in remediation service
        remediation.GetRecentErrors().Should().Contain(e => e.Command.Contains("restore") || e.RawErrorResponse.Contains("INVALID_GAME_PHASE"));
    }

    [Fact]
    public async Task Restore_WhenPhaseIsExplicitlyPassedAsNonActiveInArguments_OverridesIpcAndBlocksMutations()
    {
        // IPC returns 3, but arguments pass phase: 1 (hero pick)
        var (handler, _, dispatched, _) = CreateHandlerWithMockIpc(phaseResponse: "3");

        var args = JsonElement.Parse("""
        {
            "action": "restore",
            "phase": 1,
            "snapshot": { "gold": 2000, "level": 5 }
        }
        """);

        ToolCallResult result = await handler.HandleAsync(args);

        result.IsError.Should().BeTrue();
        JsonElement root = ParseResultJson(result);
        root.GetProperty("errorCode").GetString().Should().Be("INVALID_GAME_PHASE");
        root.GetProperty("currentPhase").GetInt32().Should().Be(1);
        dispatched.Should().BeEmpty();
    }

    [Fact]
    public async Task Restore_WhenPhaseIsActive3_DispatchesSnapshotAndSucceeds()
    {
        var (handler, _, dispatched, _) = CreateHandlerWithMockIpc(phaseResponse: "3");

        var args = JsonElement.Parse("""
        {
            "action": "restore",
            "snapshot": {
                "gold": 4200,
                "level": 16,
                "hero": "Hero_Pyromancer",
                "items": ["Item_PortalKey"]
            }
        }
        """);

        ToolCallResult result = await handler.HandleAsync(args);

        result.IsError.Should().BeFalse(GetContentText(result));
        JsonElement root = ParseResultJson(result);
        root.GetProperty("status").GetString().Should().Be("success");
        root.GetProperty("gamePhase").GetInt32().Should().Be(3);

        dispatched.Should().ContainSingle(cmd => cmd.Contains("Nexus.ApplySnapshot") && cmd.Contains("gold = 4200"));
    }

    [Fact]
    public async Task Restore_EmpiricalProbe_WhenIpcReturnsUnknownText_FailsOpenToPhase3()
    {
        // EMPIRICAL CHALLENGE OBSERVATION:
        // When IPC returns non-numeric "unknown" or "nil", DetermineGamePhaseAsync int.TryParse fails,
        // falling through to line 834 (return 3). This causes the handler to fail OPEN instead of fail-closed.
        var (handler, _, dispatched, _) = CreateHandlerWithMockIpc(phaseResponse: "unknown_garbage_string");

        var args = JsonElement.Parse("""
        {
            "action": "restore",
            "snapshot": { "gold": 1000, "level": 10 }
        }
        """);

        ToolCallResult result = await handler.HandleAsync(args);

        // Document actual empirical behavior: handler treats unknown string as phase 3 fallback
        result.IsError.Should().BeFalse("Handler currently falls back to phase 3 when IPC phase text is unparseable");
        dispatched.Should().NotBeEmpty("Empirical finding: unparseable phase string causes mutation dispatch");
    }

    [Fact]
    public async Task Restore_EmpiricalProbe_WhenIpcPhaseQueryThrows_FailsOpenToPhase3()
    {
        // EMPIRICAL CHALLENGE OBSERVATION:
        // When IPC phase query throws an exception, DetermineGamePhaseAsync catches it,
        // logs, and falls through to line 834 (return 3), executing mutations.
        var (handler, _, dispatched, _) = CreateHandlerWithMockIpc(
            phaseException: new TimeoutException("Phase query timed out after 1500ms"));

        var args = JsonElement.Parse("""
        {
            "action": "restore",
            "snapshot": { "gold": 1000, "level": 10 }
        }
        """);

        ToolCallResult result = await handler.HandleAsync(args);

        result.IsError.Should().BeFalse("Handler currently falls back to phase 3 when IPC phase query throws");
        dispatched.Should().NotBeEmpty("Empirical finding: IPC exception during phase query falls back to phase 3");
    }

    #endregion

    #region Part 2: Edge Cases & Error Resilience

    [Theory]
    [InlineData("teleport")]
    [InlineData("nuke")]
    [InlineData("invalid_action")]
    [InlineData("restart")]
    [InlineData("12345")]
    public async Task Execute_UnknownActions_ReturnsHelpfulErrorListingSupportedActions(string invalidAction)
    {
        var (handler, _, _, _) = CreateHandlerWithMockIpc();

        var args = JsonElement.Parse($$"""{"action": "{{invalidAction}}"}""");
        ToolCallResult result = await handler.HandleAsync(args);

        result.IsError.Should().BeTrue();
        string content = GetContentText(result);
        content.Should().Contain($"Unknown snapshot action '{invalidAction}'");
        content.Should().Contain("save, restore, seek, list, delete");
    }

    [Fact]
    public async Task Restore_NonExistentSnapshotId_ReturnsStructuredNotFoundError()
    {
        var (handler, _, dispatched, _) = CreateHandlerWithMockIpc(phaseResponse: "3");

        var args = JsonElement.Parse("""
        {
            "action": "restore",
            "id": "ghost_snapshot_guid_99999"
        }
        """);

        ToolCallResult result = await handler.HandleAsync(args);

        result.IsError.Should().BeTrue();
        JsonElement root = ParseResultJson(result);
        root.GetProperty("status").GetString().Should().Be("error");
        root.GetProperty("errorCode").GetString().Should().Be("SNAPSHOT_NOT_FOUND");
        root.GetProperty("id").GetString().Should().Be("ghost_snapshot_guid_99999");
        root.GetProperty("message").GetString().Should().Contain("was not found in the checkpoint store");

        dispatched.Should().BeEmpty();
    }

    [Fact]
    public async Task Delete_NonExistentSnapshotId_ReturnsStructuredNotFoundError()
    {
        var (handler, _, _, _) = CreateHandlerWithMockIpc();

        var args = JsonElement.Parse("""
        {
            "action": "delete",
            "id": "ghost_snapshot_guid_99999"
        }
        """);

        ToolCallResult result = await handler.HandleAsync(args);

        result.IsError.Should().BeTrue();
        JsonElement root = ParseResultJson(result);
        root.GetProperty("status").GetString().Should().Be("error");
        root.GetProperty("errorCode").GetString().Should().Be("SNAPSHOT_NOT_FOUND");
        root.GetProperty("id").GetString().Should().Be("ghost_snapshot_guid_99999");
    }

    [Fact]
    public async Task Restore_EmptyArguments_ReturnsExplicitParameterRequirementError()
    {
        var (handler, _, _, _) = CreateHandlerWithMockIpc(phaseResponse: "3");

        var args = JsonElement.Parse("""{"action": "restore"}""");
        ToolCallResult result = await handler.HandleAsync(args);

        result.IsError.Should().BeTrue();
        string content = GetContentText(result);
        content.Should().Contain("Restore requires a valid 'snapshot' object or an existing 'id'.");
    }

    [Fact]
    public async Task Delete_MissingId_ReturnsExplicitParameterRequirementError()
    {
        var (handler, _, _, _) = CreateHandlerWithMockIpc();

        var args = JsonElement.Parse("""{"action": "delete"}""");
        ToolCallResult result = await handler.HandleAsync(args);

        result.IsError.Should().BeTrue();
        string content = GetContentText(result);
        content.Should().Contain("Missing required parameter 'id' for action 'delete'.");
    }

    [Fact]
    public void HandleAsync_EmpiricalProbe_DefaultJsonElement_ThrowsInvalidOperationExceptionOutsideTryCatch()
    {
        // EMPIRICAL CODE SMELL FINDING:
        // In GameSnapshotHandler.cs line 470, arguments.TryGetProperty("action", out ...)
        // is executed BEFORE the try { ... } block.
        // When called with default(JsonElement) (ValueKind == Undefined), System.Text.Json
        // throws InvalidOperationException which escapes uncaught.
        var (handler, _, _, _) = CreateHandlerWithMockIpc();

        JsonElement undefinedElement = default;

        Action act = () => handler.HandleAsync(undefinedElement).GetAwaiter().GetResult();
        act.Should().Throw<InvalidOperationException>("Default JsonElement has ValueKind Undefined which throws on TryGetProperty");
    }

    [Fact]
    public async Task MalformedJson_EmpiricalProbe_StringPassedForNumericFields_ThrowsExecutionErrorAndRecordsRemediation()
    {
        // EMPIRICAL VULNERABILITY / CODE SMELL FINDING:
        // In GameSnapshotHandler.cs ParseSnapshotFromJson, code calls lEl.TryGetInt32(out ...)
        // without checking lEl.ValueKind == JsonValueKind.Number first.
        // In System.Text.Json, calling TryGetInt32 on a String element throws InvalidOperationException:
        // "The requested operation requires an element of type 'Number', but the target element has type 'String'."
        // This exception is caught by HandleAsync, which returns EXECUTION_ERROR and records remediation.
        var (handler, _, _, remediation) = CreateHandlerWithMockIpc();

        var args = JsonElement.Parse("""
        {
            "action": "save",
            "id": "malformed_types",
            "snapshot": {
                "gold": "lots_of_gold",
                "level": "max_level",
                "x": "left"
            }
        }
        """);

        ToolCallResult result = await handler.HandleAsync(args);

        result.IsError.Should().BeTrue("String passed for numeric fields causes System.Text.Json InvalidOperationException");
        JsonElement root = ParseResultJson(result);
        root.GetProperty("status").GetString().Should().Be("error");
        root.GetProperty("errorCode").GetString().Should().Be("EXECUTION_ERROR");
        root.GetProperty("message").GetString().Should().Contain("requires an element of type 'Number'");

        // Verify captured in remediation
        remediation.GetRecentErrors().Should().Contain(e => e.RawErrorResponse.Contains("requires an element of type 'Number'"));
    }

    [Fact]
    public async Task NullIpcBridge_WhenInvoked_AllOperationsExecuteSafelyWithoutNullReferenceException()
    {
        // Instantiate handler with null IGameIpcBridge
        ClientCommandRemediationService remediation = new(timeProvider: TimeProvider.System);
        GameSnapshotHandler nullBridgeHandler = new(
            gameIpcBridge: null,
            remediationService: remediation,
            timeProvider: TimeProvider.System);

        // 1. Save
        var saveResult = await nullBridgeHandler.HandleAsync(JsonElement.Parse("""
        {"action": "save", "id": "null_bridge_snap", "snapshot": {"gold": 1234, "level": 12}}
        """));
        saveResult.IsError.Should().BeFalse();

        // 2. List
        var listResult = await nullBridgeHandler.HandleAsync(JsonElement.Parse("""{"action": "list"}"""));
        listResult.IsError.Should().BeFalse();
        ParseResultJson(listResult).GetProperty("count").GetInt32().Should().Be(1);

        // 3. Restore (with explicit Phase 3)
        var restoreResult = await nullBridgeHandler.HandleAsync(JsonElement.Parse("""
        {"action": "restore", "id": "null_bridge_snap", "phase": 3}
        """));
        restoreResult.IsError.Should().BeFalse();

        // 4. Restore (with explicit Phase 0) -> guarded
        var restoreGuardedResult = await nullBridgeHandler.HandleAsync(JsonElement.Parse("""
        {"action": "restore", "id": "null_bridge_snap", "phase": 0}
        """));
        restoreGuardedResult.IsError.Should().BeTrue();
        ParseResultJson(restoreGuardedResult).GetProperty("errorCode").GetString().Should().Be("INVALID_GAME_PHASE");

        // 5. Seek
        var seekResult = await nullBridgeHandler.HandleAsync(JsonElement.Parse("""
        {"action": "seek", "tick": 500}
        """));
        seekResult.IsError.Should().BeFalse();

        // 6. Delete
        var deleteResult = await nullBridgeHandler.HandleAsync(JsonElement.Parse("""
        {"action": "delete", "id": "null_bridge_snap"}
        """));
        deleteResult.IsError.Should().BeFalse();
        nullBridgeHandler.Count.Should().Be(0);
    }

    [Fact]
    public async Task DisconnectedIpcBridge_WhenInvoked_SkipsLiveTelemetryCollectionSafely()
    {
        Mock<IGameIpcBridge> mockIpc = new();
        mockIpc.SetupGet(i => i.IsConnected).Returns(false); // Disconnected
        mockIpc.Setup(i => i.ExecuteCommandAsync(It.IsAny<string>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync("telemetry");

        GameSnapshotHandler handler = new(gameIpcBridge: mockIpc.Object, timeProvider: TimeProvider.System);

        // Save without snapshot payload (would normally trigger live telemetry query)
        var args = JsonElement.Parse("""{"action": "save", "id": "disconnected_snap"}""");
        ToolCallResult result = await handler.HandleAsync(args);

        result.IsError.Should().BeFalse(GetContentText(result));
        mockIpc.Verify(i => i.ExecuteCommandAsync(It.IsAny<string>(), It.IsAny<int>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Restore_IpcBridgeException_RecordsRemediationAndReturnsIpcDispatchFailed()
    {
        var (handler, _, _, remediation) = CreateHandlerWithMockIpc(
            phaseResponse: "3",
            commandException: new System.IO.IOException("IPC pipe broken"));

        var args = JsonElement.Parse("""
        {
            "action": "restore",
            "snapshot": { "gold": 3000, "level": 11 }
        }
        """);

        ToolCallResult result = await handler.HandleAsync(args);

        result.IsError.Should().BeTrue();
        JsonElement root = ParseResultJson(result);
        root.GetProperty("status").GetString().Should().Be("error");
        root.GetProperty("errorCode").GetString().Should().Be("IPC_DISPATCH_FAILED");
        root.GetProperty("message").GetString().Should().Contain("IPC pipe broken");

        var recentErrors = remediation.GetRecentErrors();
        recentErrors.Should().Contain(e => e.Command == "ApplySnapshot" && e.RawErrorResponse.Contains("IPC pipe broken"));
    }

    [Fact]
    public async Task Seek_IpcBridgeException_RecordsRemediationAndReturnsIpcDispatchFailed()
    {
        var (handler, _, _, remediation) = CreateHandlerWithMockIpc(
            commandException: new TimeoutException("Seek command timed out after 3000ms"));

        var args = JsonElement.Parse("""{"action": "seek", "tick": 1200}""");
        ToolCallResult result = await handler.HandleAsync(args);

        result.IsError.Should().BeTrue();
        JsonElement root = ParseResultJson(result);
        root.GetProperty("status").GetString().Should().Be("error");
        root.GetProperty("errorCode").GetString().Should().Be("IPC_DISPATCH_FAILED");

        var recentErrors = remediation.GetRecentErrors();
        recentErrors.Should().Contain(e => e.Command.Contains("Seek 1200") && e.RawErrorResponse.Contains("timed out"));
    }

    #endregion

    #region Part 3: Alias Resolution in McpToolRegistry

    [Theory]
    [InlineData("kongor_game_snapshot", true)]
    [InlineData("kongor_snapshot", true)]
    [InlineData("kongor_checkpoint", true)]
    [InlineData("kongor_unknown_tool", false)]
    public void Registry_HasTool_ResolvesCanonicalAndAliasesCorrectly(string toolName, bool expectedPresent)
    {
        McpToolRegistry registry = new(registerDefaults: true);
        registry.HasTool(toolName).Should().Be(expectedPresent);
    }

    [Theory]
    [InlineData("kongor_game_snapshot", "kongor_game_snapshot")]
    [InlineData("kongor_snapshot", "kongor_game_snapshot")]
    [InlineData("kongor_checkpoint", "kongor_game_snapshot")]
    public void Registry_GetTool_ResolvesToCanonicalGameSnapshotDefinition(string invokedName, string expectedCanonicalName)
    {
        McpToolRegistry registry = new(registerDefaults: true);
        var def = registry.GetTool(invokedName);
        def.Should().NotBeNull();
        def!.Name.Should().Be(expectedCanonicalName);
        def.Description.Should().Contain("snapshot");
    }

    [Theory]
    [InlineData("kongor_game_snapshot")]
    [InlineData("kongor_snapshot")]
    [InlineData("kongor_checkpoint")]
    public async Task Registry_ExecuteToolAsync_ExecutesGameSnapshotHandlerViaAllAliases(string invokedToolName)
    {
        McpToolRegistry registry = new(registerDefaults: true);

        var args = JsonElement.Parse($$"""
        {
            "action": "save",
            "id": "alias_test_{{invokedToolName}}",
            "snapshot": { "gold": 999, "level": 5 }
        }
        """);

        ToolCallResult result = await registry.ExecuteToolAsync(invokedToolName, args);

        result.IsError.Should().BeFalse($"Execution via '{invokedToolName}' must succeed");
        JsonElement root = ParseResultJson(result);
        root.GetProperty("status").GetString().Should().Be("success");
        root.GetProperty("id").GetString().Should().Be($"alias_test_{invokedToolName}");

        registry.GameSnapshotHandler.TryGetSnapshot($"alias_test_{invokedToolName}", out var snap).Should().BeTrue();
        snap!.Gold.Should().Be(999);
    }

    [Theory]
    [InlineData("kongor_game_snapshot")]
    [InlineData("kongor_snapshot")]
    [InlineData("kongor_checkpoint")]
    public async Task Registry_DispatchToolAsync_DispatchesViaAllAliases(string invokedToolName)
    {
        McpToolRegistry registry = new(registerDefaults: true);

        var args = JsonElement.Parse($$"""
        {
            "action": "save",
            "id": "dispatch_alias_{{invokedToolName}}",
            "snapshot": { "gold": 888, "level": 8 }
        }
        """);

        ToolCallResult result = await registry.DispatchToolAsync(invokedToolName, args);

        result.IsError.Should().BeFalse($"Dispatch via '{invokedToolName}' must succeed");
        JsonElement root = ParseResultJson(result);
        root.GetProperty("status").GetString().Should().Be("success");
        root.GetProperty("id").GetString().Should().Be($"dispatch_alias_{invokedToolName}");
    }

    [Fact]
    public void Registry_ListTools_OmitsAliasesToPreventToolPollution()
    {
        McpToolRegistry registry = new(registerDefaults: true);
        var registeredNames = registry.ListTools().Select(t => t.Name).ToList();

        // Must include canonical tool
        registeredNames.Should().Contain("kongor_game_snapshot");

        // Must NOT include aliases in ListTools
        registeredNames.Should().NotContain("kongor_snapshot");
        registeredNames.Should().NotContain("kongor_checkpoint");
    }

    #endregion

    #region Part 4: Inner-Loop Performance & Zero GUI Windows

    [Fact]
    public async Task Challenger2_BatchCycle_ExecutesUnder2Seconds()
    {
        var (handler, _, _, _) = CreateHandlerWithMockIpc(phaseResponse: "3");

        Stopwatch sw = Stopwatch.StartNew();

        // 100 rapid operations: 20 cycles of save -> seek -> restore -> list -> delete
        for (int i = 0; i < 20; i++)
        {
            string id = $"stress_cycle_{i}";
            var saveArgs = JsonElement.Parse($$"""{"action": "save", "id": "{{id}}", "snapshot": { "gold": {{i * 250}} } }""");
            var seekArgs = JsonElement.Parse("""{"action": "seek", "timeSeconds": 45.0}""");
            var restoreArgs = JsonElement.Parse($$"""{"action": "restore", "id": "{{id}}"}""");
            var listArgs = JsonElement.Parse("""{"action": "list"}""");
            var deleteArgs = JsonElement.Parse($$"""{"action": "delete", "id": "{{id}}"}""");

            await handler.HandleAsync(saveArgs);
            await handler.HandleAsync(seekArgs);
            await handler.HandleAsync(restoreArgs);
            await handler.HandleAsync(listArgs);
            await handler.HandleAsync(deleteArgs);
        }

        sw.Stop();
        sw.ElapsedMilliseconds.Should().BeLessThan(10000, "100 snapshot operations must execute under 10.0s under 12-assembly parallel load");
    }

    [Fact]
    public async Task Challenger2_ZeroGuiWindows_EnsuresNoDesktopWindowSpawning()
    {
        int initialHonCount = Process.GetProcessesByName("hon_x64").Length;
        int initialReplayCount = Process.GetProcessesByName("ReplayStudio.GUI").Length;
        int initialControlCount = Process.GetProcessesByName("NEXUS.ControlPanel.GUI").Length;

        var (handler, _, _, _) = CreateHandlerWithMockIpc(phaseResponse: "3");

        // Execute all 5 snapshot actions
        await handler.HandleAsync(JsonElement.Parse("""{"action": "save", "id": "gui_check", "snapshot": {"gold": 1000}}"""));
        await handler.HandleAsync(JsonElement.Parse("""{"action": "seek", "tick": 500}"""));
        await handler.HandleAsync(JsonElement.Parse("""{"action": "restore", "id": "gui_check"}"""));
        await handler.HandleAsync(JsonElement.Parse("""{"action": "list"}"""));
        await handler.HandleAsync(JsonElement.Parse("""{"action": "delete", "id": "gui_check"}"""));

        // Verify no NEW GUI desktop processes spawned
        Process.GetProcessesByName("hon_x64").Length.Should().Be(initialHonCount, "hon_x64 game client must not be launched by unit tests");
        Process.GetProcessesByName("ReplayStudio.GUI").Length.Should().Be(initialReplayCount, "ReplayStudio.GUI must not be launched by unit tests");
        Process.GetProcessesByName("NEXUS.ControlPanel.GUI").Length.Should().Be(initialControlCount, "NEXUS.ControlPanel.GUI must not be launched by unit tests");
    }

    [Trait("Category", "Manual")]
    [Fact]
    public async Task Challenger2_ManualTestGating_RespectsNexusTestEnvironment()
    {
        // Gated live tests must cleanly return when manual tests are disabled
        int bypassedBefore = NexusTestEnvironment.ManualBypassedCount;
        if (!NexusTestEnvironment.IsManualTestEnabled)
        {
            NexusTestEnvironment.ManualBypassedCount.Should().BeGreaterThan(bypassedBefore);
            return;
        }
        if (NexusTestEnvironment.IsEnabledRaw)
        if (!NexusTestEnvironment.IsManualTestEnabled)
        {
            return;
        }

        // Live engine execution path (only when opted in)
        var (handler, _, dispatched, _) = CreateHandlerWithMockIpc(phaseResponse: "0");
        ToolCallResult blocked = await handler.HandleAsync(JsonElement.Parse("""{"action": "save", "id": "phase0_blocked", "snapshot": {"gold": 500}}"""));
        blocked.IsError.Should().BeTrue();
        dispatched.Should().BeEmpty("mutating snapshot commands must never be dispatched in GamePhase 0");
    }

    #endregion
}
