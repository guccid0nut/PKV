// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// TAO (Together As One) MCP & Kongor CLI -- Created by guccidonut (https://github.com/guccidonut/PKV)

using System.Diagnostics;
using System.Text.Json;
using FluentAssertions;
using Kongor.Cli.Mcp.Handlers;
using Kongor.Cli.Mcp.Protocol;
using Kongor.Cli.Mcp.Services;
using Moq;
using Xunit;

namespace Kongor.Mcp.Tests;

/// <summary>
/// Empirical Challenger Stress Test Suite for Milestone 1: UI &amp; Lua Hot-Reloading.
/// Adversarially tests:
/// 1. HotReloadHandler.HandleAsync across all 4 actions (watch, unwatch, trigger, status), synonyms, and invalid inputs.
/// 2. Alias resolution in McpToolRegistry (kongor_reload, kongor_ui_reload -> kongor_hot_reload) and case-insensitivity.
/// 3. Failure modes: IPC bridge exceptions, pipe timeouts, offline game client (ClientOffline categorization), partial dispatch failures, concurrency under failure, and recovery.
/// 4. Sub-2.0s execution speed and zero desktop GUI windows.
/// </summary>
public sealed class HotReloadChallengerTests
{
    private static (HotReloadHandler Handler, Mock<IHotReloadService> MockService, Mock<IGameIpcBridge> MockIpc, ClientCommandRemediationService Remediation) CreateHandlerWithMocks(
        bool isConnected = true,
        string ipcResponse = "OK: Reload complete")
    {
        Mock<IGameIpcBridge> mockIpc = new();
        mockIpc.SetupGet(i => i.IsConnected).Returns(isConnected);
        mockIpc.Setup(i => i.ExecuteCommandAsync(It.IsAny<string>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(ipcResponse);

        Mock<IHotReloadService> mockService = new();
        mockService.SetupGet(s => s.IsWatching).Returns(true);
        mockService.SetupGet(s => s.WatchedPaths).Returns(["games/shared/ui/", "games/shared/scripts/"]);
        mockService.SetupGet(s => s.DebounceMs).Returns(150);
        mockService.SetupGet(s => s.TotalReloads).Returns(1);
        mockService.SetupGet(s => s.LastReloadDurationMs).Returns(12.5);
        mockService.SetupGet(s => s.LastChangedFiles).Returns(["games/shared/ui/main.interface"]);

        mockService.Setup(s => s.GetStatus()).Returns(new HotReloadStatusDto
        {
            Status = "success",
            IsWatching = true,
            WatchedPaths = ["games/shared/ui/", "games/shared/scripts/"],
            DebounceMs = 150,
            TotalReloads = 1,
            TotalFileEvents = 1,
            LastReloadDurationMs = 12.5,
            LastChangedFiles = ["games/shared/ui/main.interface"]
        });

        ClientCommandRemediationService remediation = new(timeProvider: TimeProvider.System);
        HotReloadHandler handler = new(
            hotReloadService: mockService.Object,
            gameIpcBridge: mockIpc.Object,
            remediationService: remediation,
            timeProvider: TimeProvider.System,
            logger: _ => { });

        return (handler, mockService, mockIpc, remediation);
    }

    #region Category 1: HotReloadHandler.HandleAsync All 4 Actions & Adversarial Inputs

    [Fact]
    public async Task HandleAsync_ActionWatch_WithCustomPathsAndDebounce_UpdatesServiceState()
    {
        Mock<IGameIpcBridge> mockIpc = new();
        using HotReloadService service = new(mockIpc.Object);
        HotReloadHandler handler = new(service);

        string json = JsonSerializer.Serialize(new
        {
            action = "watch",
            paths = new[] { "games/shared/ui", "games/shared/scripts" },
            debounceMs = 250
        });

        ToolCallResult result = await handler.HandleAsync(JsonElement.Parse(json));

        result.IsError.Should().BeFalse();
        string outputJson = ((TextContentItem)result.Content[0]).Text;
        var root = JsonElement.Parse(outputJson);

        root.GetProperty("status").GetString().Should().Be("success");
        root.GetProperty("success").GetBoolean().Should().BeTrue();
        root.GetProperty("action").GetString().Should().Be("watch");
        root.GetProperty("isWatching").GetBoolean().Should().BeTrue();
        root.GetProperty("debounceMs").GetInt32().Should().Be(250);
        root.GetProperty("watchedPaths").GetArrayLength().Should().Be(2);
        root.GetProperty("message").GetString().Should().Contain("2 path(s)");
    }

    [Fact]
    public async Task HandleAsync_ActionWatch_WithCommaSeparatedStringPaths_ParsesCorrectly()
    {
        Mock<IGameIpcBridge> mockIpc = new();
        using HotReloadService service = new(mockIpc.Object);
        HotReloadHandler handler = new(service);

        string json = """
        {
            "action": "watch",
            "paths": "games/shared/ui, games/shared/scripts, games/shared/extra",
            "debounceMs": 180
        }
        """;

        ToolCallResult result = await handler.HandleAsync(JsonElement.Parse(json));

        result.IsError.Should().BeFalse();
        service.IsWatching.Should().BeTrue();
        service.WatchedPaths.Should().HaveCount(3);
        service.DebounceMs.Should().Be(180);
    }

    [Fact]
    public async Task HandleAsync_ActionWatch_SynonymStart_NormalizesToWatch()
    {
        Mock<IGameIpcBridge> mockIpc = new();
        using HotReloadService service = new(mockIpc.Object);
        HotReloadHandler handler = new(service);

        string json = """{ "action": "start" }""";
        ToolCallResult result = await handler.HandleAsync(JsonElement.Parse(json));

        result.IsError.Should().BeFalse();
        string outputJson = ((TextContentItem)result.Content[0]).Text;
        var root = JsonElement.Parse(outputJson);
        root.GetProperty("action").GetString().Should().Be("watch");
        service.IsWatching.Should().BeTrue();
    }

    [Fact]
    public async Task HandleAsync_ActionWatch_NegativeOrZeroOrNonNumericDebounce_FallsBackToDefault150()
    {
        Mock<IGameIpcBridge> mockIpc = new();
        using HotReloadService service = new(mockIpc.Object);
        HotReloadHandler handler = new(service);

        // Negative debounce
        string json1 = """{ "action": "watch", "debounceMs": -50 }""";
        await handler.HandleAsync(JsonElement.Parse(json1));
        service.DebounceMs.Should().Be(150);

        // Zero debounce
        string json2 = """{ "action": "watch", "debounceMs": 0 }""";
        await handler.HandleAsync(JsonElement.Parse(json2));
        service.DebounceMs.Should().Be(150);

        // Non-numeric string debounce
        string json3 = """{ "action": "watch", "debounceMs": "invalid_number" }""";
        await handler.HandleAsync(JsonElement.Parse(json3));
        service.DebounceMs.Should().Be(150);
    }

    [Fact]
    public async Task HandleAsync_ActionUnwatch_WhenWatching_StopsWatching()
    {
        Mock<IGameIpcBridge> mockIpc = new();
        using HotReloadService service = new(mockIpc.Object);
        HotReloadHandler handler = new(service);

        // Start
        await handler.HandleAsync(JsonElement.Parse("""{ "action": "watch" }"""));
        service.IsWatching.Should().BeTrue();

        // Stop
        ToolCallResult unwatchResult = await handler.HandleAsync(JsonElement.Parse("""{ "action": "unwatch" }"""));
        unwatchResult.IsError.Should().BeFalse();

        string outputJson = ((TextContentItem)unwatchResult.Content[0]).Text;
        var root = JsonElement.Parse(outputJson);
        root.GetProperty("status").GetString().Should().Be("success");
        root.GetProperty("action").GetString().Should().Be("unwatch");
        root.GetProperty("isWatching").GetBoolean().Should().BeFalse();
        service.IsWatching.Should().BeFalse();
    }

    [Fact]
    public async Task HandleAsync_ActionUnwatch_WhenIdle_IsIdempotentAndReturnsSuccess()
    {
        Mock<IGameIpcBridge> mockIpc = new();
        using HotReloadService service = new(mockIpc.Object);
        HotReloadHandler handler = new(service);

        // Unwatch while already idle
        service.IsWatching.Should().BeFalse();
        ToolCallResult unwatchResult = await handler.HandleAsync(JsonElement.Parse("""{ "action": "unwatch" }"""));

        unwatchResult.IsError.Should().BeFalse();
        service.IsWatching.Should().BeFalse();
    }

    [Fact]
    public async Task HandleAsync_ActionUnwatch_SynonymStop_NormalizesToUnwatch()
    {
        Mock<IGameIpcBridge> mockIpc = new();
        using HotReloadService service = new(mockIpc.Object);
        HotReloadHandler handler = new(service);

        string json = """{ "action": "stop" }""";
        ToolCallResult result = await handler.HandleAsync(JsonElement.Parse(json));

        result.IsError.Should().BeFalse();
        string outputJson = ((TextContentItem)result.Content[0]).Text;
        var root = JsonElement.Parse(outputJson);
        root.GetProperty("action").GetString().Should().Be("unwatch");
    }

    [Fact]
    public async Task HandleAsync_ActionTrigger_WithExplicitFilesList_DispatchesAndReportsFiles()
    {
        Mock<IGameIpcBridge> mockIpc = new();
        mockIpc.SetupGet(i => i.IsConnected).Returns(true);
        mockIpc.Setup(i => i.ExecuteCommandAsync(It.IsAny<string>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync("OK");

        using HotReloadService service = new(mockIpc.Object);
        HotReloadHandler handler = new(service);

        string json = """
        {
            "action": "trigger",
            "files": ["games/shared/ui/main.interface", "games/shared/ui/scripts/nexus_bridge.lua"]
        }
        """;

        ToolCallResult result = await handler.HandleAsync(JsonElement.Parse(json));

        result.IsError.Should().BeFalse();
        string outputJson = ((TextContentItem)result.Content[0]).Text;
        var root = JsonElement.Parse(outputJson);

        root.GetProperty("status").GetString().Should().Be("success");
        root.GetProperty("success").GetBoolean().Should().BeTrue();
        root.GetProperty("action").GetString().Should().Be("trigger");
        root.GetProperty("lastChangedFiles").GetArrayLength().Should().Be(2);
        root.GetProperty("dispatchedCommands").GetArrayLength().Should().Be(2);
        root.GetProperty("commandResponses").GetArrayLength().Should().Be(2);
        service.TotalReloads.Should().Be(1);
    }

    [Fact]
    public async Task HandleAsync_ActionTrigger_SynonymReload_NormalizesToTrigger()
    {
        Mock<IGameIpcBridge> mockIpc = new();
        mockIpc.SetupGet(i => i.IsConnected).Returns(true);
        mockIpc.Setup(i => i.ExecuteCommandAsync(It.IsAny<string>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync("OK");

        using HotReloadService service = new(mockIpc.Object);
        HotReloadHandler handler = new(service);

        string json = """{ "action": "reload" }""";
        ToolCallResult result = await handler.HandleAsync(JsonElement.Parse(json));

        result.IsError.Should().BeFalse();
        string outputJson = ((TextContentItem)result.Content[0]).Text;
        var root = JsonElement.Parse(outputJson);
        root.GetProperty("action").GetString().Should().Be("trigger");
        service.TotalReloads.Should().Be(1);
    }

    [Fact]
    public async Task HandleAsync_ActionTrigger_WithEmptyArguments_DefaultsToManualTriggerMarker()
    {
        Mock<IGameIpcBridge> mockIpc = new();
        mockIpc.SetupGet(i => i.IsConnected).Returns(true);
        mockIpc.Setup(i => i.ExecuteCommandAsync(It.IsAny<string>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync("OK");

        using HotReloadService service = new(mockIpc.Object);
        HotReloadHandler handler = new(service);

        string json = """{ "action": "trigger" }""";
        ToolCallResult result = await handler.HandleAsync(JsonElement.Parse(json));

        result.IsError.Should().BeFalse();
        string outputJson = ((TextContentItem)result.Content[0]).Text;
        var root = JsonElement.Parse(outputJson);
        root.GetProperty("lastChangedFiles")[0].GetString().Should().Be("[MANUAL_TRIGGER]");
    }

    [Fact]
    public async Task HandleAsync_ActionStatus_ReflectsAccurateState()
    {
        var (handler, _, _, _) = CreateHandlerWithMocks();

        string json = """{ "action": "status" }""";
        ToolCallResult result = await handler.HandleAsync(JsonElement.Parse(json));

        result.IsError.Should().BeFalse();
        string outputJson = ((TextContentItem)result.Content[0]).Text;
        var root = JsonElement.Parse(outputJson);

        root.GetProperty("status").GetString().Should().Be("success");
        root.GetProperty("action").GetString().Should().Be("status");
        root.GetProperty("isWatching").GetBoolean().Should().BeTrue();
        root.GetProperty("debounceMs").GetInt32().Should().Be(150);
        root.GetProperty("totalReloads").GetInt32().Should().Be(1);
        root.GetProperty("totalFileEvents").GetInt32().Should().Be(1);
    }

    [Fact]
    public async Task HandleAsync_DefaultAction_WhenActionPropertyOmitted_DefaultsToStatus()
    {
        var (handler, _, _, _) = CreateHandlerWithMocks();

        string json = """{}""";
        ToolCallResult result = await handler.HandleAsync(JsonElement.Parse(json));

        result.IsError.Should().BeFalse();
        string outputJson = ((TextContentItem)result.Content[0]).Text;
        var root = JsonElement.Parse(outputJson);
        root.GetProperty("action").GetString().Should().Be("status");
    }

    [Fact]
    public async Task HandleAsync_InvalidAction_ReturnsErrorWithMalformedCommandArgumentsRemediation()
    {
        var (handler, _, _, _) = CreateHandlerWithMocks();

        string json = """{ "action": "reboot_system" }""";
        ToolCallResult result = await handler.HandleAsync(JsonElement.Parse(json));

        result.IsError.Should().BeTrue();
        string outputJson = ((TextContentItem)result.Content[0]).Text;
        var root = JsonElement.Parse(outputJson);

        root.GetProperty("status").GetString().Should().Be("error");
        root.GetProperty("action").GetString().Should().Be("reboot_system");
        root.GetProperty("remediationCategory").GetString().Should().Be("MalformedCommandArguments");
        root.GetProperty("remediationHint").GetString().Should().Contain("watch', 'unwatch', 'trigger', or 'status'");
    }

    [Fact]
    public async Task HandleAsync_NonStringActionType_ThrowsInvalidOperationException()
    {
        var (handler, _, _, _) = CreateHandlerWithMocks();

        string json = """{ "action": 123 }""";
        Func<Task> act = async () => await handler.HandleAsync(JsonElement.Parse(json));

        // In System.Text.Json, a.GetString() on a Number throws InvalidOperationException
        // Because ExtractAction is called outside the try-catch block, this throws unhandled.
        await act.Should().ThrowAsync<InvalidOperationException>();
    }

    [Fact]
    public async Task HandleAsync_PascalCaseActionProperty_DefaultsToStatusDueToCaseSensitiveLookup()
    {
        var (handler, _, _, _) = CreateHandlerWithMocks();

        // Caller sends "Action": "watch" with uppercase 'A'
        string json = """{ "Action": "watch" }""";
        ToolCallResult result = await handler.HandleAsync(JsonElement.Parse(json));

        result.IsError.Should().BeFalse();
        string outputJson = ((TextContentItem)result.Content[0]).Text;
        var root = JsonElement.Parse(outputJson);

        // Because ExtractAction only checks "action" (lowercase), "Action" is missed and defaults to "status"
        root.GetProperty("action").GetString().Should().Be("status");
    }

    #endregion

    #region Category 2: McpToolRegistry Alias Resolution & Schema Verification

    [Fact]
    public void McpToolRegistry_Aliases_KongorReloadAndKongorUiReload_AreRegisteredAndResolved()
    {
        McpToolRegistry registry = new(registerDefaults: true);

        // Verify canonical tool exists
        registry.HasTool("kongor_hot_reload").Should().BeTrue();

        // Verify aliases exist
        registry.HasTool("kongor_reload").Should().BeTrue();
        registry.HasTool("kongor_ui_reload").Should().BeTrue();

        // Verify case-insensitivity
        registry.HasTool("KONGOR_RELOAD").Should().BeTrue();
        registry.HasTool("Kongor_Ui_Reload").Should().BeTrue();
        registry.HasTool("KONGOR_HOT_RELOAD").Should().BeTrue();

        // Verify definition retrieval via aliases resolves to the hot reload definition
        var canonicalDef = registry.GetTool("kongor_hot_reload");
        var alias1Def = registry.GetTool("kongor_reload");
        var alias2Def = registry.GetTool("kongor_ui_reload");

        canonicalDef.Should().NotBeNull();
        alias1Def.Should().NotBeNull();
        alias2Def.Should().NotBeNull();

        alias1Def!.Name.Should().Be("kongor_hot_reload");
        alias2Def!.Name.Should().Be("kongor_hot_reload");
        alias1Def.Description.Should().Be(canonicalDef!.Description);
    }

    [Fact]
    public void McpToolRegistry_CreateHotReloadDefinition_ConformsToSchemaContract()
    {
        var def = McpToolRegistry.CreateHotReloadDefinition();

        def.Name.Should().Be("kongor_hot_reload");
        def.Description.Should().Contain("games/client/override/ui/");
        def.Description.Should().Contain("games/client/override/ui/scripts/");
        def.Description.Should().Contain("ReloadInterfaces");
        def.InputSchema.Should().NotBeNull();
        def.InputSchema.Required.Should().Contain("action");

        var props = def.InputSchema.Properties;
        props.Should().ContainKey("action");
        props.Should().ContainKey("paths");
        props.Should().ContainKey("debounceMs");
        props.Should().ContainKey("files");

        props["action"].Enum.Should().BeEquivalentTo(["watch", "unwatch", "trigger", "status"]);
        props["debounceMs"].Default.Should().Be(150);
    }

    [Fact]
    public async Task McpToolRegistry_ExecuteToolAsync_ViaKongorReloadAlias_DispatchesCorrectly()
    {
        Mock<IGameIpcBridge> mockIpc = new();
        mockIpc.SetupGet(i => i.IsConnected).Returns(true);
        mockIpc.Setup(i => i.ExecuteCommandAsync(It.IsAny<string>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync("OK");

        using HotReloadService service = new(mockIpc.Object);
        HotReloadHandler handler = new(service);

        McpToolRegistry registry = new(
            registerDefaults: true,
            hotReloadService: service,
            hotReloadHandler: handler);

        // Execute via alias 'kongor_reload'
        string json = """{ "action": "trigger", "files": ["test.interface"] }""";
        ToolCallResult result = await registry.ExecuteToolAsync("kongor_reload", JsonElement.Parse(json));

        result.IsError.Should().BeFalse();
        string outputJson = ((TextContentItem)result.Content[0]).Text;
        var root = JsonElement.Parse(outputJson);
        root.GetProperty("status").GetString().Should().Be("success");
        service.TotalReloads.Should().Be(1);
    }

    [Fact]
    public async Task McpToolRegistry_ExecuteToolAsync_ViaKongorUiReloadAlias_DispatchesCorrectly()
    {
        Mock<IGameIpcBridge> mockIpc = new();
        mockIpc.SetupGet(i => i.IsConnected).Returns(true);
        mockIpc.Setup(i => i.ExecuteCommandAsync(It.IsAny<string>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync("OK");

        using HotReloadService service = new(mockIpc.Object);
        HotReloadHandler handler = new(service);

        McpToolRegistry registry = new(
            registerDefaults: true,
            hotReloadService: service,
            hotReloadHandler: handler);

        // Execute via alias 'kongor_ui_reload'
        string json = """{ "action": "status" }""";
        ToolCallResult result = await registry.ExecuteToolAsync("kongor_ui_reload", JsonElement.Parse(json));

        result.IsError.Should().BeFalse();
        string outputJson = ((TextContentItem)result.Content[0]).Text;
        var root = JsonElement.Parse(outputJson);
        root.GetProperty("status").GetString().Should().Be("success");
    }

    [Fact]
    public async Task McpToolRegistry_ExecuteToolAsync_UnknownTool_ReturnsCleanErrorWithoutThrowing()
    {
        McpToolRegistry registry = new(registerDefaults: true);

        ToolCallResult result = await registry.ExecuteToolAsync("kongor_non_existent_tool", JsonElement.Parse("{}"));

        result.IsError.Should().BeTrue();
        string outputJson = ((TextContentItem)result.Content[0]).Text;
        outputJson.Should().Contain("Unknown tool 'kongor_non_existent_tool'");
    }

    #endregion

    #region Category 3: Failure Modes & Remediation (IPC Exceptions, Timeout, Offline Game Client)

    [Fact]
    public async Task FailureMode_IpcBridgeException_ThrowsIOException_ReturnsRemediatedErrorWithoutCrash()
    {
        Mock<IGameIpcBridge> mockIpc = new();
        mockIpc.SetupGet(i => i.IsConnected).Returns(true);
        mockIpc.Setup(i => i.ExecuteCommandAsync("ReloadInterfaces", It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new IOException("Pipe broken: IPC connection unexpectedly severed."));

        using HotReloadService service = new(mockIpc.Object);
        HotReloadHandler handler = new(service);

        ToolCallResult result = await handler.HandleAsync(JsonElement.Parse("""{ "action": "trigger" }"""));

        result.IsError.Should().BeTrue();
        string outputJson = ((TextContentItem)result.Content[0]).Text;
        var root = JsonElement.Parse(outputJson);

        root.GetProperty("status").GetString().Should().Be("error");
        root.GetProperty("success").GetBoolean().Should().BeFalse();
        root.GetProperty("error").GetString().Should().Contain("Pipe broken");
        root.GetProperty("remediationCategory").GetString().Should().NotBeNullOrWhiteSpace();
        root.GetProperty("remediationHint").GetString().Should().NotBeNullOrWhiteSpace();
        service.TotalReloads.Should().Be(0);
        service.LastError.Should().Contain("Pipe broken");
    }

    [Fact]
    public async Task FailureMode_IpcTimeout_ThrowsTimeoutException_ReturnsRemediatedErrorWithoutCrash()
    {
        Mock<IGameIpcBridge> mockIpc = new();
        mockIpc.SetupGet(i => i.IsConnected).Returns(true);
        mockIpc.Setup(i => i.ExecuteCommandAsync("ReloadInterfaces", It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new TimeoutException("IPC pipe command timed out after 1500ms"));

        using HotReloadService service = new(mockIpc.Object);
        HotReloadHandler handler = new(service);

        ToolCallResult result = await handler.HandleAsync(JsonElement.Parse("""{ "action": "trigger" }"""));

        result.IsError.Should().BeTrue();
        string outputJson = ((TextContentItem)result.Content[0]).Text;
        var root = JsonElement.Parse(outputJson);

        root.GetProperty("status").GetString().Should().Be("error");
        root.GetProperty("error").GetString().Should().Contain("timed out after 1500ms");
        root.TryGetProperty("remediationHint", out _).Should().BeTrue();
        root.TryGetProperty("remediationCategory", out _).Should().BeTrue();
    }

    [Fact]
    public async Task FailureMode_OfflineGameClient_WhenProcessNotRunning_CategorizesClientOffline()
    {
        Mock<IGameIpcBridge> mockIpc = new();
        mockIpc.SetupGet(i => i.IsConnected).Returns(false);
        mockIpc.Setup(i => i.ConnectAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new IOException("The game client process is not currently running."));

        using HotReloadService service = new(mockIpc.Object);
        HotReloadHandler handler = new(service);

        ToolCallResult result = await handler.HandleAsync(JsonElement.Parse("""{ "action": "trigger" }"""));

        result.IsError.Should().BeTrue();
        string outputJson = ((TextContentItem)result.Content[0]).Text;
        var root = JsonElement.Parse(outputJson);

        root.GetProperty("status").GetString().Should().Be("error");
        root.GetProperty("remediationCategory").GetString().Should().Be("ClientOffline");
        root.GetProperty("remediationHint").GetString().Should().Contain("hon_x64.exe");
    }

    [Fact]
    public async Task FailureMode_OfflineGameClient_TargetWindowNotFound_CategorizesClientOffline()
    {
        Mock<IGameIpcBridge> mockIpc = new();
        mockIpc.SetupGet(i => i.IsConnected).Returns(false);
        mockIpc.Setup(i => i.ConnectAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new IOException("Target window 'hon_x64' not found in system window list."));

        using HotReloadService service = new(mockIpc.Object);
        HotReloadHandler handler = new(service);

        ToolCallResult result = await handler.HandleAsync(JsonElement.Parse("""{ "action": "trigger" }"""));

        result.IsError.Should().BeTrue();
        string outputJson = ((TextContentItem)result.Content[0]).Text;
        var root = JsonElement.Parse(outputJson);

        root.GetProperty("status").GetString().Should().Be("error");
        root.GetProperty("remediationCategory").GetString().Should().Be("ClientOffline");
        root.GetProperty("remediationHint").GetString().Should().Contain("hon_x64.exe");
    }

    [Fact]
    public async Task FailureMode_GenericPipeNotFound_CategorizesExecutionError_ContradictingWorkerOfflineAssumption()
    {
        Mock<IGameIpcBridge> mockIpc = new();
        mockIpc.SetupGet(i => i.IsConnected).Returns(false);
        // Generic pipe connection failure when client is closed without process check message
        mockIpc.Setup(i => i.ConnectAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new IOException("The system cannot find the file specified. (Named pipe \\\\.\\pipe\\willowmaker-ipc)"));

        using HotReloadService service = new(mockIpc.Object);
        HotReloadHandler handler = new(service);

        ToolCallResult result = await handler.HandleAsync(JsonElement.Parse("""{ "action": "trigger" }"""));

        result.IsError.Should().BeTrue();
        string outputJson = ((TextContentItem)result.Content[0]).Text;
        var root = JsonElement.Parse(outputJson);

        root.GetProperty("status").GetString().Should().Be("error");
        // Empirically observes that because channel is "ipc" and error does not contain process strings,
        // remediation category is "ExecutionError", NOT "ClientOffline"!
        root.GetProperty("remediationCategory").GetString().Should().Be("ExecutionError");
    }

    [Fact]
    public async Task FailureMode_PartialIpcFailure_SecondCommandFails_PreservesPartialResponsesAndReportsError()
    {
        Mock<IGameIpcBridge> mockIpc = new();
        mockIpc.SetupGet(i => i.IsConnected).Returns(true);
        mockIpc.Setup(i => i.ExecuteCommandAsync("ReloadInterfaces", It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync("OK: Interfaces Reloaded");
        mockIpc.Setup(i => i.ExecuteCommandAsync("Script dofile('/ui/scripts/nexus_bridge.lua')", It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new IOException("Engine crashed during Lua reload"));

        using HotReloadService service = new(mockIpc.Object);
        HotReloadHandler handler = new(service);

        ToolCallResult result = await handler.HandleAsync(JsonElement.Parse("""{ "action": "trigger" }"""));

        result.IsError.Should().BeTrue();
        string outputJson = ((TextContentItem)result.Content[0]).Text;
        var root = JsonElement.Parse(outputJson);

        root.GetProperty("status").GetString().Should().Be("error");
        root.GetProperty("error").GetString().Should().Contain("Engine crashed during Lua reload");
        root.GetProperty("commandResponses").GetArrayLength().Should().Be(1);
        root.GetProperty("commandResponses")[0].GetString().Should().Be("OK: Interfaces Reloaded");
        service.TotalReloads.Should().Be(0);
    }

    [Fact]
    public async Task FailureMode_ConcurrencyStressUnderFailure_MultipleSimultaneousTriggersDoNotDeadlock()
    {
        Mock<IGameIpcBridge> mockIpc = new();
        mockIpc.SetupGet(i => i.IsConnected).Returns(true);
        mockIpc.Setup(i => i.ExecuteCommandAsync(It.IsAny<string>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new IOException("Simulated IPC pipe fault"));

        using HotReloadService service = new(mockIpc.Object);
        HotReloadHandler handler = new(service);

        // Fire 20 parallel trigger calls
        Task<ToolCallResult>[] tasks = Enumerable.Range(0, 20)
            .Select(_ => handler.HandleAsync(JsonElement.Parse("""{ "action": "trigger" }""")))
            .ToArray();

        ToolCallResult[] results = await Task.WhenAll(tasks);

        results.Should().HaveCount(20);
        foreach (var res in results)
        {
            res.IsError.Should().BeTrue();
            string outputJson = ((TextContentItem)res.Content[0]).Text;
            var root = JsonElement.Parse(outputJson);
            root.GetProperty("status").GetString().Should().Be("error");
        }
    }

    [Fact]
    public async Task FailureMode_RecoveryAfterFailure_SubsequentTriggersSucceedWhenIpcRestored()
    {
        Mock<IGameIpcBridge> mockIpc = new();
        bool failing = true;

        mockIpc.SetupGet(i => i.IsConnected).Returns(true);
        mockIpc.Setup(i => i.ExecuteCommandAsync(It.IsAny<string>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .Returns(() => failing
                ? Task.FromException<string>(new IOException("Temporary IPC fault"))
                : Task.FromResult("OK"));

        using HotReloadService service = new(mockIpc.Object);
        HotReloadHandler handler = new(service);

        // Attempt 1: Fails
        ToolCallResult res1 = await handler.HandleAsync(JsonElement.Parse("""{ "action": "trigger" }"""));
        res1.IsError.Should().BeTrue();
        service.TotalReloads.Should().Be(0);

        // Recover IPC
        failing = false;

        // Attempt 2: Succeeds
        ToolCallResult res2 = await handler.HandleAsync(JsonElement.Parse("""{ "action": "trigger" }"""));
        res2.IsError.Should().BeFalse();
        service.TotalReloads.Should().Be(1);
    }

    #endregion

    #region Category 4: Inner-Loop Test Performance & Zero Desktop GUI Windows

    [Fact]
    public async Task Performance_HotReloadOperations_ExecuteUnderInnerLoopThreshold()
    {
        var (handler, _, _, _) = CreateHandlerWithMocks();
        Stopwatch sw = Stopwatch.StartNew();

        // 30 operations in fast sequence
        for (int i = 0; i < 30; i++)
        {
            var res = await handler.HandleAsync(JsonElement.Parse("""{ "action": "status" }"""));
            res.IsError.Should().BeFalse();
        }

        sw.Stop();
        // 30 in-memory operations must complete in < 2000ms under parallel assembly load
        sw.ElapsedMilliseconds.Should().BeLessThan(2000);
    }

    [Fact]
    public void ZeroDesktopWindows_WhenInvoked_NoGuiProcessesSpawned()
    {
        // Verify that running the test harness does NOT spawn external GUI windows
        // such as ReplayStudio.GUI or control-panel processes.
        Process[] guiProcesses = Process.GetProcessesByName("ReplayStudio.GUI");
        guiProcesses.Should().BeEmpty("Tests must never spawn external desktop GUI windows.");
    }

    #endregion
}
