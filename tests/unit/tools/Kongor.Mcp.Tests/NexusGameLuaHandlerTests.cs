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
/// Phase 2 Modernization Milestone 2 Unit Test Suite.
/// Validates GameLuaHandler macro action routing, command formatting,
/// telemetry deserialization, log event extraction, phase verification,
/// zero-window GUI suppression, and manual gating compliance.
/// </summary>
public sealed class NexusGameLuaHandlerTests
{
    private static (GameLuaHandler Handler, Mock<IGameIpcBridge> MockIpc, Mock<IUiControlService> MockUi) CreateHandlerWithMocks(
        string? ipcResponse = null,
        Action<string>? onExecute = null)
    {
        var mockIpc = new Mock<IGameIpcBridge>();
        if (ipcResponse != null || onExecute != null)
        {
            var setup = mockIpc.Setup(i => i.ExecuteCommandAsync(It.IsAny<string>(), It.IsAny<int>(), It.IsAny<CancellationToken>()));
            if (onExecute != null)
            {
                setup.Callback<string, int, CancellationToken>((cmd, _, _) => onExecute(cmd));
            }
            setup.ReturnsAsync(ipcResponse ?? "OK");
        }
        mockIpc.SetupGet(i => i.IsConnected).Returns(true);

        var mockUi = new Mock<IUiControlService>();
        var remediationService = new ClientCommandRemediationService(timeProvider: TimeProvider.System);

        var clientManageHandler = new GameClientManageHandler(
            ipcBridge: mockIpc.Object,
            uiControlService: mockUi.Object,
            remediationService: remediationService,
            timeProvider: TimeProvider.System,
            logger: _ => { });

        var handler = new GameLuaHandler(
            clientManageHandler: clientManageHandler,
            serverManageHandler: null,
            uiControlService: mockUi.Object,
            remediationService: remediationService,
            timeProvider: TimeProvider.System,
            logger: _ => { });

        return (handler, mockIpc, mockUi);
    }

    #region 1. Macro Action Command Formatting Tests

    [Fact]
    public async Task GameLuaHandler_HideLogin_DefaultAccount_FormatsDeterministicScript()
    {
        string? sentCommand = null;
        var (handler, _, _) = CreateHandlerWithMocks(
            ipcResponse: "[NEXUS_EVENT] LOGIN_SUCCESS|{\"player\":\"KongorPlayer\",\"loggedIn\":true}",
            onExecute: cmd => sentCommand = cmd);

        using JsonDocument doc = JsonDocument.Parse("{\"action\": \"hide_login\"}");
        ToolCallResult result = await handler.HandleAsync(doc.RootElement);

        result.IsError.Should().BeFalse();
        sentCommand.Should().NotBeNull();
        sentCommand.Should().StartWith("Script ");
        sentCommand.Should().Contain("Nexus.HideLogin('KongorPlayer')");
        sentCommand.Should().Contain("Set('_loggedin', true)");
        sentCommand.Should().Contain("Trigger('LoginStatus', 'success', '', true)");
    }

    [Fact]
    public async Task GameLuaHandler_HideLogin_CustomAccount_FormatsAccountParameter()
    {
        string? sentCommand = null;
        var (handler, _, _) = CreateHandlerWithMocks(
            ipcResponse: "[NEXUS_EVENT] LOGIN_SUCCESS|{\"player\":\"Savage_Beast\",\"loggedIn\":true}",
            onExecute: cmd => sentCommand = cmd);

        using JsonDocument doc = JsonDocument.Parse("{\"action\": \"hide_login\", \"accountName\": \"Savage_Beast\"}");
        ToolCallResult result = await handler.HandleAsync(doc.RootElement);

        result.IsError.Should().BeFalse();
        sentCommand.Should().NotBeNull();
        sentCommand.Should().Contain("Nexus.HideLogin('Savage_Beast')");
    }

    [Fact]
    public async Task GameLuaHandler_PracticeMatch_DefaultOptions_FormatsCaldavarAndPyromancer()
    {
        string? sentCommand = null;
        var (handler, _, _) = CreateHandlerWithMocks(
            ipcResponse: "[NEXUS_EVENT] PRACTICE_MATCH_START|{\"map\":\"caldavar\",\"hero\":\"Hero_Pyromancer\"}",
            onExecute: cmd => sentCommand = cmd);

        using JsonDocument doc = JsonDocument.Parse("{\"action\": \"practice_match\"}");
        ToolCallResult result = await handler.HandleAsync(doc.RootElement);

        result.IsError.Should().BeFalse();
        sentCommand.Should().NotBeNull();
        sentCommand.Should().StartWith("Script ");
        sentCommand.Should().Contain("Nexus.PracticeMatch");
        sentCommand.Should().Contain("map = 'caldavar'");
        sentCommand.Should().Contain("hero = 'Hero_Pyromancer'");
        sentCommand.Should().Contain("StartGame practice PracticeGame map:caldavar allheroes:true");
    }

    [Fact]
    public async Task GameLuaHandler_PracticeMatch_CustomOptions_FormatsCustomMapAndHero()
    {
        string? sentCommand = null;
        var (handler, _, _) = CreateHandlerWithMocks(
            ipcResponse: "[NEXUS_EVENT] PRACTICE_MATCH_START|{\"map\":\"midwars\",\"hero\":\"Hero_Witchslayer\"}",
            onExecute: cmd => sentCommand = cmd);

        using JsonDocument doc = JsonDocument.Parse("{\"action\": \"practice_match\", \"map\": \"midwars\", \"hero\": \"Hero_Witchslayer\"}");
        ToolCallResult result = await handler.HandleAsync(doc.RootElement);

        result.IsError.Should().BeFalse();
        sentCommand.Should().NotBeNull();
        sentCommand.Should().Contain("map = 'midwars'");
        sentCommand.Should().Contain("hero = 'Hero_Witchslayer'");
        sentCommand.Should().Contain("StartGame practice PracticeGame map:midwars allheroes:true");
    }

    [Fact]
    public async Task GameLuaHandler_OpenStore_DispatchesScriptWithStoreContainer2()
    {
        string? sentCommand = null;
        var (handler, _, _) = CreateHandlerWithMocks(
            ipcResponse: "OK",
            onExecute: cmd => sentCommand = cmd);

        using JsonDocument doc = JsonDocument.Parse("{\"action\": \"open_store\"}");
        ToolCallResult result = await handler.HandleAsync(doc.RootElement);

        result.IsError.Should().BeFalse();
        sentCommand.Should().NotBeNull();
        sentCommand.Should().StartWith("Script ");
        sentCommand.Should().Contain("cg_store2_");
        sentCommand.Should().Contain("store_container2");
        sentCommand.Should().Contain("MainMenuPanelSwitcher");
        sentCommand.Should().Contain("MainMenuPanelToggle");
    }

    [Fact]
    public async Task GameLuaHandler_OpenStore_WithTabSubpageAndItem_FormatsNavigateToScript()
    {
        string? sentCommand = null;
        var (handler, _, _) = CreateHandlerWithMocks(
            ipcResponse: "OK",
            onExecute: cmd => sentCommand = cmd);

        using JsonDocument doc = JsonDocument.Parse("""
        {
            "action": "open_store",
            "tab": "accountvanity",
            "subpage": "accounticons",
            "item": "1307"
        }
        """);
        ToolCallResult result = await handler.HandleAsync(doc.RootElement);

        result.IsError.Should().BeFalse();
        sentCommand.Should().NotBeNull();
        sentCommand.Should().StartWith("Script ");
        sentCommand.Should().Contain("HoN_Store:NavigateTo");
        sentCommand.Should().Contain("accountvanity");
        sentCommand.Should().Contain("accounticons");
        sentCommand.Should().Contain("1307");
    }

    [Fact]
    public async Task GameLuaHandler_ApplySnapshot_ValidPayload_FormatsTableSerialization()
    {
        string? sentCommand = null;
        var (handler, _, _) = CreateHandlerWithMocks(
            ipcResponse: "[NEXUS_EVENT] SNAPSHOT_APPLIED|{\"gold\":3500,\"level\":11}",
            onExecute: cmd => sentCommand = cmd);

        string argsJson = """
        {
            "action": "apply_snapshot",
            "snapshot": {
                "gold": 3500,
                "level": 11,
                "items": ["Item_PortalKey", "Item_Marchers"],
                "pos": { "x": 1500.0, "y": 2500.0, "z": 64.0 }
            }
        }
        """;
        using JsonDocument doc = JsonDocument.Parse(argsJson);
        ToolCallResult result = await handler.HandleAsync(doc.RootElement);

        result.IsError.Should().BeFalse();
        sentCommand.Should().NotBeNull();
        sentCommand.Should().StartWith("Script ");
        sentCommand.Should().Contain("Nexus.ApplySnapshot");
        sentCommand.Should().Contain("gold = 3500");
        sentCommand.Should().Contain("level = 11");
        sentCommand.Should().Contain("Item_PortalKey");
    }

    [Fact]
    public async Task GameLuaHandler_ApplySnapshot_MissingSnapshot_ReturnsError()
    {
        var (handler, _, _) = CreateHandlerWithMocks();
        using JsonDocument doc = JsonDocument.Parse("{\"action\": \"apply_snapshot\"}");
        ToolCallResult result = await handler.HandleAsync(doc.RootElement);

        result.IsError.Should().BeTrue();
        string text = ((TextContentItem)result.Content[0]).Text;
        text.Should().Contain("Missing required parameter: 'snapshot'");
    }

    [Fact]
    public async Task GameLuaHandler_CollectTelemetry_FormatsTelemetryCall()
    {
        string? sentCommand = null;
        var (handler, _, _) = CreateHandlerWithMocks(
            ipcResponse: "[NEXUS_TELEMETRY] {\"hero\":\"Hero_Pyromancer\",\"level\":1,\"hp\":600,\"maxHp\":600}",
            onExecute: cmd => sentCommand = cmd);

        using JsonDocument doc = JsonDocument.Parse("{\"action\": \"collect_telemetry\"}");
        ToolCallResult result = await handler.HandleAsync(doc.RootElement);

        result.IsError.Should().BeFalse();
        sentCommand.Should().NotBeNull();
        sentCommand.Should().StartWith("Script ");
        sentCommand.Should().Contain("Nexus.CollectTelemetry()");
    }

    [Fact]
    public async Task GameLuaHandler_UnknownAction_ReturnsError()
    {
        var (handler, _, _) = CreateHandlerWithMocks();
        using JsonDocument doc = JsonDocument.Parse("{\"action\": \"invalid_action_name\"}");
        ToolCallResult result = await handler.HandleAsync(doc.RootElement);

        result.IsError.Should().BeTrue();
        string text = ((TextContentItem)result.Content[0]).Text;
        text.Should().Contain("Unknown action 'invalid_action_name'");
    }

    #endregion

    #region 2. Telemetry Deserialization Tests

    [Fact]
    public async Task GameLuaHandler_CollectTelemetry_JsonPayload_DeserializesStructuredTelemetry()
    {
        string engineOutput = """
        Script if Nexus and Nexus.CollectTelemetry then Nexus.CollectTelemetry() end
        [NEXUS_TELEMETRY] {"hero":"Hero_Pyromancer","level":6,"hp":850,"maxHp":850,"mana":420,"maxMana":420,"pos":{"x":100.5,"y":200.0,"z":50.2},"gold":1200,"gameTime":450,"cooldowns":[0,4.5,0,60.0]}
        """;

        var (handler, _, _) = CreateHandlerWithMocks(ipcResponse: engineOutput);
        using JsonDocument doc = JsonDocument.Parse("{\"action\": \"collect_telemetry\"}");
        ToolCallResult result = await handler.HandleAsync(doc.RootElement);

        result.IsError.Should().BeFalse();
        result.Content.Should().NotBeEmpty();

        string jsonText = ((TextContentItem)result.Content[0]).Text;
        using JsonDocument resDoc = JsonDocument.Parse(jsonText);
        JsonElement root = resDoc.RootElement;

        root.GetProperty("status").GetString().Should().Be("success");
        root.TryGetProperty("telemetry", out JsonElement tel).Should().BeTrue();

        tel.GetProperty("hero").GetString().Should().Be("Hero_Pyromancer");
        tel.GetProperty("level").GetInt32().Should().Be(6);
        tel.GetProperty("hp").GetInt32().Should().Be(850);
        tel.GetProperty("maxHp").GetInt32().Should().Be(850);
        tel.GetProperty("mana").GetInt32().Should().Be(420);
        tel.GetProperty("maxMana").GetInt32().Should().Be(420);
        tel.GetProperty("gold").GetInt32().Should().Be(1200);
        tel.GetProperty("gameTime").GetInt32().Should().Be(450);

        tel.GetProperty("pos").GetProperty("x").GetDouble().Should().BeApproximately(100.5, 0.01);
        tel.GetProperty("pos").GetProperty("y").GetDouble().Should().BeApproximately(200.0, 0.01);
        tel.GetProperty("pos").GetProperty("z").GetDouble().Should().BeApproximately(50.2, 0.01);

        tel.GetProperty("cooldowns")[1].GetDouble().Should().BeApproximately(4.5, 0.01);
        tel.GetProperty("cooldowns")[3].GetDouble().Should().BeApproximately(60.0, 0.01);
    }

    [Fact]
    public async Task GameLuaHandler_CollectTelemetry_KeyValueFallback_DeserializesNumericTypes()
    {
        string engineOutput = "[NEXUS_TELEMETRY] hero=hero_witchslayer;level=11;k=5;d=2;a=8;hp=1120;max_hp=1120;mana=500;max_mana=500;gold=2450";

        var (handler, _, _) = CreateHandlerWithMocks(ipcResponse: engineOutput);
        using JsonDocument doc = JsonDocument.Parse("{\"action\": \"collect_telemetry\"}");
        ToolCallResult result = await handler.HandleAsync(doc.RootElement);

        result.IsError.Should().BeFalse();
        string jsonText = ((TextContentItem)result.Content[0]).Text;
        using JsonDocument resDoc = JsonDocument.Parse(jsonText);
        JsonElement root = resDoc.RootElement;

        root.TryGetProperty("telemetry", out JsonElement tel).Should().BeTrue();
        tel.GetProperty("hero").GetString().Should().Be("hero_witchslayer");
        tel.GetProperty("level").GetInt32().Should().Be(11);
        tel.GetProperty("hp").GetInt32().Should().Be(1120);
        tel.GetProperty("max_hp").GetInt32().Should().Be(1120);
        tel.GetProperty("mana").GetInt32().Should().Be(500);
        tel.GetProperty("gold").GetInt32().Should().Be(2450);
    }

    [Trait("Category", "Manual")]
    [Fact]
    public async Task GameLuaHandler_CollectTelemetry_MalformedPayload_HandlesGracefullyWithoutCrashing()
    {
        string engineOutput = "[NEXUS_TELEMETRY] {bad_json_not_terminated";

        var (handler, _, _) = CreateHandlerWithMocks(ipcResponse: engineOutput);
        using JsonDocument doc = JsonDocument.Parse("{\"action\": \"collect_telemetry\"}");
        ToolCallResult result = await handler.HandleAsync(doc.RootElement);

        // Handler must not throw unhandled exception
        result.Content.Should().NotBeEmpty();
    }

    #endregion

    #region 3. Log Event Extraction Tests

    [Fact]
    public async Task GameLuaHandler_LogEmission_ExtractsNexusEvent()
    {
        string engineOutput = "[NEXUS_EVENT] LOGIN_SUCCESS|{\"player\":\"NexusPlayer\",\"loggedIn\":true}";

        var (handler, _, _) = CreateHandlerWithMocks(ipcResponse: engineOutput);
        using JsonDocument doc = JsonDocument.Parse("{\"action\": \"hide_login\"}");
        ToolCallResult result = await handler.HandleAsync(doc.RootElement);

        result.IsError.Should().BeFalse();
        string jsonText = ((TextContentItem)result.Content[0]).Text;
        using JsonDocument resDoc = JsonDocument.Parse(jsonText);
        JsonElement root = resDoc.RootElement;

        root.TryGetProperty("event", out JsonElement ev).Should().BeTrue();
        ev.GetProperty("name").GetString().Should().Be("LOGIN_SUCCESS");
        ev.GetProperty("payload").GetProperty("player").GetString().Should().Be("NexusPlayer");
        ev.GetProperty("payload").GetProperty("loggedIn").GetBoolean().Should().BeTrue();
    }

    [Fact]
    public async Task GameLuaHandler_PracticeMatch_ExtractsHeroSelectedEvent()
    {
        string engineOutput = "[NEXUS_EVENT] PRACTICE_MATCH_HERO_SELECTED|{\"hero\":\"Hero_Pyromancer\",\"team\":1}";

        var (handler, _, _) = CreateHandlerWithMocks(ipcResponse: engineOutput);
        using JsonDocument doc = JsonDocument.Parse("{\"action\": \"practice_match\"}");
        ToolCallResult result = await handler.HandleAsync(doc.RootElement);

        result.IsError.Should().BeFalse();
        string jsonText = ((TextContentItem)result.Content[0]).Text;
        using JsonDocument resDoc = JsonDocument.Parse(jsonText);
        JsonElement root = resDoc.RootElement;

        root.TryGetProperty("event", out JsonElement ev).Should().BeTrue();
        ev.GetProperty("name").GetString().Should().Be("PRACTICE_MATCH_HERO_SELECTED");
        ev.GetProperty("payload").GetProperty("hero").GetString().Should().Be("Hero_Pyromancer");
        ev.GetProperty("payload").GetProperty("team").GetInt32().Should().Be(1);
    }

    #endregion

    #region 4. Lifecycle Phase Verification & Error Handling Tests

    [Fact]
    public async Task GameLuaHandler_ApplySnapshot_InvalidGamePhase_ReturnsStructuredError()
    {
        string engineOutput = "[NEXUS_EVENT] ERROR|{\"errorCode\":\"INVALID_GAME_PHASE\",\"currentPhase\":0,\"message\":\"Snapshot can only be applied during active match (phase 3)\"}";

        var (handler, _, _) = CreateHandlerWithMocks(ipcResponse: engineOutput);
        using JsonDocument doc = JsonDocument.Parse("{\"action\": \"apply_snapshot\", \"snapshot\": {\"gold\": 5000}}");
        ToolCallResult result = await handler.HandleAsync(doc.RootElement);

        result.IsError.Should().BeTrue();
        string jsonText = ((TextContentItem)result.Content[0]).Text;
        using JsonDocument resDoc = JsonDocument.Parse(jsonText);
        JsonElement root = resDoc.RootElement;

        root.GetProperty("status").GetString().Should().Be("error");
        root.GetProperty("errorCode").GetString().Should().Be("INVALID_GAME_PHASE");
        root.GetProperty("currentPhase").GetInt32().Should().Be(0);
    }

    [Fact]
    public async Task GameLuaHandler_DirectPhaseVerify_InvalidPhase_ReturnsStructuredErrorResult()
    {
        string engineOutput = "{\"success\": false, \"errorCode\": \"INVALID_GAME_PHASE\", \"currentPhase\": 1}";

        var (handler, _, _) = CreateHandlerWithMocks(ipcResponse: engineOutput);
        using JsonDocument doc = JsonDocument.Parse("{\"code\": \"Nexus.VerifyPhase({3})\"}");
        ToolCallResult result = await handler.HandleAsync(doc.RootElement);

        result.IsError.Should().BeTrue();
        string jsonText = ((TextContentItem)result.Content[0]).Text;
        using JsonDocument resDoc = JsonDocument.Parse(jsonText);
        JsonElement root = resDoc.RootElement;

        root.GetProperty("errorCode").GetString().Should().Be("INVALID_GAME_PHASE");
    }

    #endregion

    #region 5. Fast Inner-Loop Performance, Zero-GUI, and Manual Gating Tests

    [Fact]
    public async Task GameLuaHandler_AutomatedSuite_ExecutesFastWithZeroGuiWindows()
    {
        var (handler, mockIpc, mockUi) = CreateHandlerWithMocks(
            ipcResponse: "[NEXUS_TELEMETRY] {\"hero\":\"Hero_Pyromancer\",\"level\":1,\"hp\":600,\"maxHp\":600}");

        Stopwatch sw = Stopwatch.StartNew();

        for (int i = 0; i < 10; i++)
        {
            using JsonDocument doc = JsonDocument.Parse("{\"action\": \"collect_telemetry\"}");
            var res = await handler.HandleAsync(doc.RootElement);
            res.IsError.Should().BeFalse();
        }

        sw.Stop();

        // Must run well under the 2.0s manual test gating threshold
        sw.ElapsedMilliseconds.Should().BeLessThan(2000);

        // Zero physical Win32 keystrokes / console actuations dispatched
        mockUi.Invocations.Count.Should().Be(0);
    }

    [Fact]
    public async Task GameLuaHandler_LiveNamedPipeExecution_GatedByNexusTestEnvironment()
    {
        // Standard Manual Gating Rule from AGENTS.md:
        // Any test requiring live running engines, pipe connections, or > 2.0s must be manually gated.
        if (!NexusTestEnvironment.IsManualTestEnabled) return;

        // The following block executes ONLY when NEXUS_RUN_MANUAL_TESTS=1 or KONGOR_RUN_MANUAL_TESTS=1.
        // It tests real Named Pipe IPC against an active hon_x64.exe client.
        var bridge = new GameIpcBridge();
        await bridge.ConnectAsync(timeoutMs: 100);
    }

    [Fact]
    public async Task GameLuaHandler_LegacyStoreContainerInCode_AutoHealsToStoreContainer2()
    {
        string? sentCommand = null;
        var (handler, _, _) = CreateHandlerWithMocks(
            ipcResponse: "OK",
            onExecute: cmd => sentCommand = cmd);

        using JsonDocument doc = JsonDocument.Parse("{\"code\": \"UIManager.GetInterface('main'):HoNMainF('MainMenuPanelToggle', 'store_container', nil, false)\"}");
        ToolCallResult result = await handler.HandleAsync(doc.RootElement);

        result.IsError.Should().BeFalse();
        sentCommand.Should().NotBeNull();
        sentCommand.Should().Contain("store_container2");
        sentCommand.Should().NotContain("'store_container'");
        string text = ((TextContentItem)result.Content[0]).Text;
        text.Should().Contain("store_container2");
        text.Should().Contain("StoreContainerMismatch");
    }

    [Fact]
    public async Task GameLuaHandler_StoreContainerWidget_AutoHealsToStoreContainer2()
    {
        string? sentCommand = null;
        var (handler, _, _) = CreateHandlerWithMocks(
            ipcResponse: "OK",
            onExecute: cmd => sentCommand = cmd);

        using JsonDocument doc = JsonDocument.Parse("{\"invokeWidget\": \"store_container\", \"event\": \"onclick\"}");
        ToolCallResult result = await handler.HandleAsync(doc.RootElement);

        result.IsError.Should().BeFalse();
        sentCommand.Should().NotBeNull();
        sentCommand.Should().Be("Call store_container2 onclick");
        string text = ((TextContentItem)result.Content[0]).Text;
        text.Should().Contain("store_container2");
    }

    [Fact]
    public async Task GameLuaHandler_TransportHierarchy_IpcPreferredOverConsole()
    {
        var mockIpc = new Mock<IGameIpcBridge>();
        mockIpc.SetupGet(i => i.IsConnected).Returns(true);
        mockIpc.Setup(i => i.ExecuteCommandAsync(It.IsAny<string>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync("OK_IPC");

        var mockUi = new Mock<IUiControlService>();

        var handler = new GameLuaHandler(
            gameIpcBridge: mockIpc.Object,
            clientManageHandler: null,
            serverManageHandler: null,
            uiControlService: mockUi.Object,
            remediationService: new ClientCommandRemediationService(timeProvider: TimeProvider.System),
            timeProvider: TimeProvider.System,
            logger: _ => { });

        using JsonDocument doc = JsonDocument.Parse("{\"code\": \"echo hello\"}");
        var result = await handler.HandleAsync(doc.RootElement);

        result.IsError.Should().BeFalse();
        string text = ((TextContentItem)result.Content[0]).Text;
        text.Should().Contain("ipc");
        mockIpc.Verify(i => i.ExecuteCommandAsync(It.IsAny<string>(), It.IsAny<int>(), It.IsAny<CancellationToken>()), Times.Once);
        mockUi.Verify(u => u.ExecuteInGameConsoleCommandAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task GameLuaHandler_TransportHierarchy_ConsoleUsedWhenIpcFails()
    {
        var mockIpc = new Mock<IGameIpcBridge>();
        mockIpc.SetupGet(i => i.IsConnected).Returns(true);
        mockIpc.Setup(i => i.ExecuteCommandAsync(It.IsAny<string>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("IPC pipe closed"));

        var mockUi = new Mock<IUiControlService>();
        mockUi.Setup(u => u.ExecuteInGameConsoleCommandAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new UiConsoleCommandResult { Success = true, WindowTitle = "Heroes of Newerth", Hwnd = "0x1234" });

        var handler = new GameLuaHandler(
            gameIpcBridge: mockIpc.Object,
            clientManageHandler: null,
            serverManageHandler: null,
            uiControlService: mockUi.Object,
            remediationService: new ClientCommandRemediationService(timeProvider: TimeProvider.System),
            timeProvider: TimeProvider.System,
            logger: _ => { });

        using JsonDocument doc = JsonDocument.Parse("{\"code\": \"echo hello\"}");
        var result = await handler.HandleAsync(doc.RootElement);

        result.IsError.Should().BeFalse();
        string text = ((TextContentItem)result.Content[0]).Text;
        text.Should().Contain("in_game_console");
        mockUi.Verify(u => u.ExecuteInGameConsoleCommandAsync("hon_x64", It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task GameLuaHandler_TransportHierarchy_ClickFallbackWhenConsoleFailsForWidget()
    {
        var mockIpc = new Mock<IGameIpcBridge>();
        mockIpc.SetupGet(i => i.IsConnected).Returns(false);

        var mockUi = new Mock<IUiControlService>();
        mockUi.Setup(u => u.ExecuteInGameConsoleCommandAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new UiConsoleCommandResult { Success = false, ErrorMessage = "Console not responding" });
        mockUi.Setup(u => u.ClickAsync("hon_x64", "sysbar_store", It.IsAny<int?>(), It.IsAny<int?>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new UiClickResult { Success = true, ElementClicked = "sysbar_store", Target = "hon_x64" });

        var handler = new GameLuaHandler(
            gameIpcBridge: mockIpc.Object,
            clientManageHandler: null,
            serverManageHandler: null,
            uiControlService: mockUi.Object,
            remediationService: new ClientCommandRemediationService(timeProvider: TimeProvider.System),
            timeProvider: TimeProvider.System,
            logger: _ => { });

        using JsonDocument doc = JsonDocument.Parse("{\"invokeWidget\": \"sysbar_store\"}");
        var result = await handler.HandleAsync(doc.RootElement);

        result.IsError.Should().BeFalse();
        string text = ((TextContentItem)result.Content[0]).Text;
        text.Should().Contain("physical_click");
        text.Should().Contain("sysbar_store");
        mockUi.Verify(u => u.ClickAsync("hon_x64", "sysbar_store", It.IsAny<int?>(), It.IsAny<int?>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task GameLuaHandler_OpenStore_EmitsNexusEvent_AndEnrichesEventPayload()
    {
        var mockIpc = new Mock<IGameIpcBridge>();
        mockIpc.SetupGet(i => i.IsConnected).Returns(true);
        mockIpc.Setup(i => i.ExecuteCommandAsync(It.IsAny<string>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync("[NEXUS_EVENT] OPEN_STORE|{\"storePanel\":\"store_container2\",\"success\":true}");

        var handler = new GameLuaHandler(
            gameIpcBridge: mockIpc.Object,
            clientManageHandler: null,
            serverManageHandler: null,
            uiControlService: null,
            remediationService: new ClientCommandRemediationService(timeProvider: TimeProvider.System),
            timeProvider: TimeProvider.System,
            logger: _ => { });

        using JsonDocument doc = JsonDocument.Parse("{\"action\": \"open_store\"}");
        var result = await handler.HandleAsync(doc.RootElement);

        result.IsError.Should().BeFalse();
        string text = ((TextContentItem)result.Content[0]).Text;
        using JsonDocument resDoc = JsonDocument.Parse(text);
        var root = resDoc.RootElement;
        root.GetProperty("status").GetString().Should().Be("success");
        root.GetProperty("channel").GetString().Should().Be("ipc");
        root.GetProperty("event").GetProperty("name").GetString().Should().Be("OPEN_STORE");
        root.GetProperty("event").GetProperty("payload").GetProperty("storePanel").GetString().Should().Be("store_container2");
    }

    [Fact]
    public async Task GameLuaHandler_OpenStore_ClickFallback_ClicksStoreButton()
    {
        var mockIpc = new Mock<IGameIpcBridge>();
        mockIpc.SetupGet(i => i.IsConnected).Returns(false);

        var mockUi = new Mock<IUiControlService>();
        mockUi.Setup(u => u.ExecuteInGameConsoleCommandAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new UiConsoleCommandResult { Success = false, ErrorMessage = "Console not responding" });
        mockUi.Setup(u => u.ClickAsync("hon_x64", "store_button", It.IsAny<int?>(), It.IsAny<int?>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new UiClickResult { Success = true, ElementClicked = "store_button", Target = "hon_x64" });

        var handler = new GameLuaHandler(
            gameIpcBridge: mockIpc.Object,
            clientManageHandler: null,
            serverManageHandler: null,
            uiControlService: mockUi.Object,
            remediationService: new ClientCommandRemediationService(timeProvider: TimeProvider.System),
            timeProvider: TimeProvider.System,
            logger: _ => { });

        using JsonDocument doc = JsonDocument.Parse("{\"action\": \"open_store\"}");
        var result = await handler.HandleAsync(doc.RootElement);

        result.IsError.Should().BeFalse();
        string text = ((TextContentItem)result.Content[0]).Text;
        using JsonDocument resDoc = JsonDocument.Parse(text);
        var root = resDoc.RootElement;
        root.GetProperty("status").GetString().Should().Be("success");
        root.GetProperty("channel").GetString().Should().Be("physical_click");
        root.GetProperty("elementClicked").GetString().Should().Be("store_button");
        mockUi.Verify(u => u.ClickAsync("hon_x64", "store_button", It.IsAny<int?>(), It.IsAny<int?>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task GameLuaHandler_WithClientManageHandler_EnrichesPhysicalClickResult()
    {
        var mockIpc = new Mock<IGameIpcBridge>();
        mockIpc.SetupGet(i => i.IsConnected).Returns(false);

        var mockUi = new Mock<IUiControlService>();
        mockUi.Setup(u => u.ExecuteInGameConsoleCommandAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new UiConsoleCommandResult { Success = false, ErrorMessage = "Console not responding" });
        mockUi.Setup(u => u.ClickAsync("hon_x64", "store_button", It.IsAny<int?>(), It.IsAny<int?>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new UiClickResult { Success = true, ElementClicked = "store_button", Target = "hon_x64" });

        var remediation = new ClientCommandRemediationService(timeProvider: TimeProvider.System);
        var clientHandler = new GameClientManageHandler(
            ipcBridge: mockIpc.Object,
            uiControlService: mockUi.Object,
            remediationService: remediation,
            timeProvider: TimeProvider.System,
            logger: _ => { });

        var handler = new GameLuaHandler(
            clientManageHandler: clientHandler,
            serverManageHandler: null,
            uiControlService: mockUi.Object,
            remediationService: remediation,
            timeProvider: TimeProvider.System,
            logger: _ => { });

        using JsonDocument doc = JsonDocument.Parse("{\"action\": \"open_store\"}");
        var result = await handler.HandleAsync(doc.RootElement);

        result.IsError.Should().BeFalse();
        string text = ((TextContentItem)result.Content[0]).Text;
        using JsonDocument resDoc = JsonDocument.Parse(text);
        var root = resDoc.RootElement;
        root.GetProperty("status").GetString().Should().Be("success");
        root.GetProperty("channel").GetString().Should().Be("physical_click");
        root.GetProperty("elementClicked").GetString().Should().Be("store_button");
    }

    #endregion
}
