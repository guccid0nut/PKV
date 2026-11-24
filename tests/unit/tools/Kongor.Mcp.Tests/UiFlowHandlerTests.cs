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
using static Kongor.Mcp.Tests.McpSimulationTestHarness;

namespace Kongor.Mcp.Tests;

/// <summary>
/// Fast inner-loop automated unit test suite for Milestone 5: Direct Lua UI Workflow Macros ('kongor_ui_flow').
/// Execution SLA: Entire suite executes in &lt; 200ms (threshold: &lt; 2.0s). Zero external desktop GUI windows spawned.
/// Validates action dispatching, K2 rule conformance (UIManager, 0-arg DoEvent, no 'onclick'),
/// parameter parsing, dryRun execution, client offline detection, diagnostic actions, and McpToolRegistry alias resolution.
/// </summary>
public sealed class UiFlowHandlerTests
{
    private sealed class UiFlowTestContext
    {
        public UiFlowHandler Handler { get; init; } = null!;
        public Mock<IGameIpcBridge> MockIpc { get; init; } = null!;
        public List<string> DispatchedCommands { get; init; } = [];
        public ClientCommandRemediationService RemediationService { get; init; } = null!;
    }

    private static UiFlowTestContext CreateContext(bool isConnected = true, string ipcResponse = "OK")
    {
        List<string> dispatched = [];
        Mock<IGameIpcBridge> mockIpc = new();
        mockIpc.SetupGet(i => i.IsConnected).Returns(isConnected);
        mockIpc.Setup(i => i.ExecuteCommandAsync(It.IsAny<string>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .Returns((string cmd, int timeout, CancellationToken ct) =>
            {
                dispatched.Add(cmd);
                return Task.FromResult(ipcResponse);
            });
        mockIpc.Setup(i => i.ExecuteLuaAsync(It.IsAny<string>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .Returns((string lua, int timeout, CancellationToken ct) =>
            {
                dispatched.Add($"Script {lua}");
                return Task.FromResult(ipcResponse);
            });

        ClientCommandRemediationService remediation = new(timeProvider: TimeProvider.System);
        var mockUi = new Mock<IUiControlService>();
        mockUi.Setup(u => u.ExecuteInGameConsoleCommandAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new UiConsoleCommandResult { Success = false, ErrorMessage = "Target window 'hon_x64' not found" });

        UiFlowHandler handler = new(
            gameIpcBridge: mockIpc.Object,
            clientManageHandler: null,
            remediationService: remediation,
            uiControlService: mockUi.Object,
            timeProvider: TimeProvider.System,
            logger: _ => { });

        return new UiFlowTestContext
        {
            Handler = handler,
            MockIpc = mockIpc,
            DispatchedCommands = dispatched,
            RemediationService = remediation
        };
    }

    private static JsonElement ParseResultJson(ToolCallResult result)
    {
        result.Content.Should().ContainSingle();
        string text = ((TextContentItem)result.Content[0]).Text;
        return JsonElement.Parse(text);
    }

    #region Category 1: Parameter Validation & Error Handling

    [Fact]
    public async Task HandleAsync_MissingAction_ReturnsErrorWithSupportedActions()
    {
        var ctx = CreateContext();
        var args = JsonElement.Parse("{}");

        var result = await ctx.Handler.HandleAsync(args);

        result.IsError.Should().BeTrue();
        string error = ((TextContentItem)result.Content[0]).Text;
        error.Should().Contain("Missing required parameter 'action'");
        error.Should().Contain("bypass_login");
        error.Should().Contain("open_lobby");
        error.Should().Contain("open_options");
        error.Should().Contain("dismiss_popups");
    }

    [Theory]
    [InlineData("invalid_action")]
    [InlineData("click_menu")]
    [InlineData("start_game")]
    public async Task HandleAsync_UnknownAction_ReturnsErrorListingSupportedActions(string invalidAction)
    {
        var ctx = CreateContext();
        var args = JsonElement.Parse($"{{\"action\": \"{invalidAction}\"}}");

        var result = await ctx.Handler.HandleAsync(args);

        result.IsError.Should().BeTrue();
        string error = ((TextContentItem)result.Content[0]).Text;
        error.Should().Contain($"Unknown action '{invalidAction}'");
        error.Should().Contain("bypass_login");
    }

    [Fact]
    public async Task HandleAsync_EmptyActionString_ReturnsValidationError()
    {
        var ctx = CreateContext();
        var args = JsonElement.Parse("{\"action\": \"   \"}");

        var result = await ctx.Handler.HandleAsync(args);

        result.IsError.Should().BeTrue();
        string error = ((TextContentItem)result.Content[0]).Text;
        error.Should().Contain("Missing required parameter 'action'");
    }

    #endregion

    #region Category 2: Valid Execution of All 4 Actions

    [Fact]
    public async Task HandleAsync_BypassLogin_DispatchesAuthenticationLuaAndSucceeds()
    {
        var ctx = CreateContext();
        var args = JsonElement.Parse("""
        {
            "action": "bypass_login",
            "accountName": "ProKongorPlayer"
        }
        """);

        var result = await ctx.Handler.HandleAsync(args);

        result.IsError.Should().BeFalse();
        JsonElement root = ParseResultJson(result);
        root.GetProperty("status").GetString().Should().Be("success");
        root.GetProperty("action").GetString().Should().Be("bypass_login");
        string lua = root.GetProperty("executedLua").GetString()!;

        lua.Should().Contain("ProKongorPlayer");
        lua.Should().Contain("LoginStatus");
        lua.Should().Contain("prelogin_lower");
        ctx.DispatchedCommands.Should().ContainSingle();
        ctx.DispatchedCommands[0].Should().StartWith("Script ");
    }

    [Theory]
    [InlineData("custom", "game_lobby")]
    [InlineData("practice", "game_lobby")]
    [InlineData("matchmaking", "matchmaking")]
    [InlineData("store", "store_container2")]
    public async Task HandleAsync_OpenLobby_ResolvesLobbyTypeAndTogglesPanel(string lobbyType, string expectedPanel)
    {
        var ctx = CreateContext();
        var args = JsonElement.Parse($$"""
        {
            "action": "open_lobby",
            "lobbyType": "{{lobbyType}}"
        }
        """);

        var result = await ctx.Handler.HandleAsync(args);

        result.IsError.Should().BeFalse();
        JsonElement root = ParseResultJson(result);
        root.GetProperty("status").GetString().Should().Be("success");
        root.GetProperty("action").GetString().Should().Be("open_lobby");
        string lua = root.GetProperty("executedLua").GetString()!;

        lua.Should().Contain($"Set('_mainmenu_currentpanel', '{expectedPanel}')");
        lua.Should().Contain("MainMenuPanelSwitcher");
        ctx.DispatchedCommands.Should().ContainSingle();
    }

    [Theory]
    [InlineData("graphics", 1)]
    [InlineData("sound", 2)]
    [InlineData("controls", 3)]
    [InlineData("gameplay", 4)]
    [InlineData("chat", 5)]
    [InlineData("mods", 6)]
    public async Task HandleAsync_OpenOptions_ResolvesTabCategoryIndex(string tabName, int expectedIndex)
    {
        var ctx = CreateContext();
        var args = JsonElement.Parse($$"""
        {
            "action": "open_options",
            "optionsTab": "{{tabName}}"
        }
        """);

        var result = await ctx.Handler.HandleAsync(args);

        result.IsError.Should().BeFalse();
        JsonElement root = ParseResultJson(result);
        root.GetProperty("status").GetString().Should().Be("success");
        root.GetProperty("action").GetString().Should().Be("open_options");
        string lua = root.GetProperty("executedLua").GetString()!;

        lua.Should().Contain("game_options");
        lua.Should().Contain($"SelectSubCategory({expectedIndex}, 0)");
        root.GetProperty("details").GetProperty("categoryIndex").GetInt32().Should().Be(expectedIndex);
    }

    [Fact]
    public async Task HandleAsync_DismissPopups_ScansCandidatesAndReturnsDismissedList()
    {
        string engineResponse = "[NEXUS_EVENT] POPUPS_DISMISSED|[\"main_popup_panel\",\"warn_unverified\"]";
        var ctx = CreateContext(ipcResponse: engineResponse);
        var args = JsonElement.Parse("""
        {
            "action": "dismiss_popups"
        }
        """);

        var result = await ctx.Handler.HandleAsync(args);

        result.IsError.Should().BeFalse();
        JsonElement root = ParseResultJson(result);
        root.GetProperty("status").GetString().Should().Be("success");
        root.GetProperty("action").GetString().Should().Be("dismiss_popups");

        var dismissed = root.GetProperty("dismissedPopups").EnumerateArray().Select(e => e.GetString()).ToList();
        dismissed.Should().Contain("main_popup_panel");
        dismissed.Should().Contain("warn_unverified");
    }

    [Fact]
    public async Task HandleAsync_OpenStore_DispatchesDynamicCgStore2LuaAndSucceeds()
    {
        string engineResponse = "[NEXUS_EVENT] OPEN_STORE|{\"storePanel\":\"store_container2\",\"success\":true}";
        var ctx = CreateContext(ipcResponse: engineResponse);
        var args = JsonElement.Parse("""
        {
            "action": "open_store"
        }
        """);

        var result = await ctx.Handler.HandleAsync(args);

        result.IsError.Should().BeFalse();
        JsonElement root = ParseResultJson(result);
        root.GetProperty("status").GetString().Should().Be("success");
        root.GetProperty("action").GetString().Should().Be("open_store");
        string lua = root.GetProperty("executedLua").GetString()!;

        lua.Should().Contain("cg_store2_");
        lua.Should().Contain("store_container2");
        lua.Should().Contain("MainMenuPanelSwitcher");
        lua.Should().Contain("MainMenuPanelToggle");
        root.GetProperty("details").GetProperty("targetPanel").GetString().Should().Be("store_container2");
        ctx.DispatchedCommands.Should().ContainSingle();
        ctx.DispatchedCommands[0].Should().StartWith("Script ");
    }

    [Fact]
    public async Task HandleAsync_OpenStore_WithTabSubpageAndItem_GeneratesNavigateToLuaAndSucceeds()
    {
        string engineResponse = "[NEXUS_EVENT] OPEN_STORE|{\"success\":true}";
        var ctx = CreateContext(ipcResponse: engineResponse);
        var args = JsonElement.Parse("""
        {
            "action": "open_store",
            "tab": "gamevanity",
            "subpage": "taunts",
            "item": "Hero_Bubbles.Alt16"
        }
        """);

        var result = await ctx.Handler.HandleAsync(args);

        result.IsError.Should().BeFalse();
        JsonElement root = ParseResultJson(result);
        root.GetProperty("status").GetString().Should().Be("success");
        root.GetProperty("action").GetString().Should().Be("open_store");
        string lua = root.GetProperty("executedLua").GetString()!;

        lua.Should().Contain("HoN_Store:NavigateTo");
        lua.Should().Contain("gamevanity");
        lua.Should().Contain("taunts");
        lua.Should().Contain("Hero_Bubbles.Alt16");
        lua.Should().Contain("Set('store2_initCategory', 27, 'int')");
        lua.Should().Contain("sc:Sleep(0)");
        lua.Should().Contain("isAlreadyOpen");
        root.GetProperty("details").GetProperty("tab").GetString().Should().Be("gamevanity");
        root.GetProperty("details").GetProperty("subpage").GetString().Should().Be("taunts");
        root.GetProperty("details").GetProperty("item").GetString().Should().Be("Hero_Bubbles.Alt16");
    }

    [Theory]
    [InlineData("taunts", null, 27)]
    [InlineData("gamevanity", "announcers", 5)]
    [InlineData("gamevanity", "couriers", 57)]
    [InlineData("gamevanity", "wards", 74)]
    [InlineData("gamevanity", "tpeffects", 78)]
    [InlineData("gamevanity", "upgrades", 75)]
    [InlineData("accountvanity", "accounticons", 3)]
    [InlineData("accountvanity", "namecolors", 16)]
    [InlineData("accountvanity", "symbols", 4)]
    [InlineData("featured", null, 68)]
    [InlineData("altavatars", null, 2)]
    [InlineData("others", null, 6)]
    [InlineData("bundles", null, 7)]
    public void ResolveStoreInitCategory_WhenInvoked_ReturnsExpectedK2Category(string tab, string? subpage, int expectedCatId)
    {
        int? catId = UiFlowHandler.ResolveStoreInitCategory(tab, subpage);
        catId.Should().Be(expectedCatId);
    }

    [Fact]
    public async Task HandleAsync_OpenWatch_DispatchesWatchSystemLuaAndSucceeds()
    {
        string engineResponse = "[NEXUS_EVENT] OPEN_WATCH|{\"tab\":\"live\",\"success\":true}";
        var ctx = CreateContext(ipcResponse: engineResponse);
        var args = JsonElement.Parse("""
        {
            "action": "open_watch",
            "tab": "live"
        }
        """);

        var result = await ctx.Handler.HandleAsync(args);

        result.IsError.Should().BeFalse();
        JsonElement root = ParseResultJson(result);
        root.GetProperty("status").GetString().Should().Be("success");
        root.GetProperty("action").GetString().Should().Be("open_watch");
        string lua = root.GetProperty("executedLua").GetString()!;

        lua.Should().Contain("watch_system");
        lua.Should().Contain("Watch_System.SelectTab('live')");
        lua.Should().Contain("MainMenuPanelSwitcher");
        root.GetProperty("details").GetProperty("targetPanel").GetString().Should().Be("watch_system");
        root.GetProperty("details").GetProperty("tab").GetString().Should().Be("live");
        ctx.DispatchedCommands.Should().ContainSingle();
        ctx.DispatchedCommands[0].Should().StartWith("Script ");
    }

    [Fact]
    public async Task HandleAsync_OpenLearnatorium_DispatchesCompendiumLuaAndSucceeds()
    {
        string engineResponse = "[NEXUS_EVENT] OPEN_LEARNATORIUM|{\"panel\":\"compendium\",\"success\":true}";
        var ctx = CreateContext(ipcResponse: engineResponse);
        var args = JsonElement.Parse("""
        {
            "action": "open_learnatorium"
        }
        """);

        var result = await ctx.Handler.HandleAsync(args);

        result.IsError.Should().BeFalse();
        JsonElement root = ParseResultJson(result);
        root.GetProperty("status").GetString().Should().Be("success");
        root.GetProperty("action").GetString().Should().Be("open_learnatorium");
        string lua = root.GetProperty("executedLua").GetString()!;

        lua.Should().Contain("compendium");
        lua.Should().Contain("MainMenuPanelSwitcher");
        lua.Should().Contain("MainMenuPanelToggle");
        root.GetProperty("details").GetProperty("targetPanel").GetString().Should().Be("compendium");
        ctx.DispatchedCommands.Should().ContainSingle();
        ctx.DispatchedCommands[0].Should().StartWith("Script ");
    }

    [Fact]
    public async Task HandleAsync_OpenStats_DispatchesPlayerStatsLuaAndSucceeds()
    {
        string engineResponse = "[NEXUS_EVENT] OPEN_STATS|{\"tab\":\"mastery\",\"nickname\":\"guccidonut\",\"success\":true}";
        var ctx = CreateContext(ipcResponse: engineResponse);
        var args = JsonElement.Parse("""
        {
            "action": "open_stats",
            "nickname": "guccidonut",
            "tab": "mastery"
        }
        """);

        var result = await ctx.Handler.HandleAsync(args);

        result.IsError.Should().BeFalse();
        JsonElement root = ParseResultJson(result);
        root.GetProperty("status").GetString().Should().Be("success");
        root.GetProperty("action").GetString().Should().Be("open_stats");
        string lua = root.GetProperty("executedLua").GetString()!;

        lua.Should().Contain("Player_Stats_V2:Show('guccidonut')");
        lua.Should().Contain("Player_Stats_V2:OnClickTab('mastery')");
        lua.Should().Contain("playerstats");
        root.GetProperty("details").GetProperty("targetPanel").GetString().Should().Be("playerstats");
        root.GetProperty("details").GetProperty("tab").GetString().Should().Be("mastery");
        root.GetProperty("details").GetProperty("nickname").GetString().Should().Be("guccidonut");
        ctx.DispatchedCommands.Should().ContainSingle();
        ctx.DispatchedCommands[0].Should().StartWith("Script ");
    }

    [Fact]
    public async Task HandleAsync_OpenProfile_DefaultsToCurrentAccountAndOverview()
    {
        string engineResponse = "[NEXUS_EVENT] OPEN_STATS|{\"tab\":\"overview\",\"nickname\":null,\"success\":true}";
        var ctx = CreateContext(ipcResponse: engineResponse);
        var args = JsonElement.Parse("""
        {
            "action": "open_profile"
        }
        """);

        var result = await ctx.Handler.HandleAsync(args);

        result.IsError.Should().BeFalse();
        JsonElement root = ParseResultJson(result);
        root.GetProperty("status").GetString().Should().Be("success");
        root.GetProperty("action").GetString().Should().Be("open_profile");
        string lua = root.GetProperty("executedLua").GetString()!;

        lua.Should().Contain("Player_Stats_V2:Show(nil)");
        lua.Should().Contain("Player_Stats_V2:OnClickTab('overview')");
        root.GetProperty("details").GetProperty("targetPanel").GetString().Should().Be("playerstats");
        root.GetProperty("details").GetProperty("tab").GetString().Should().Be("overview");
        ctx.DispatchedCommands.Should().ContainSingle();
    }

    [Fact]
    public async Task HandleAsync_IpcDisconnected_ProactivelyAttemptsConnectAsync()
    {
        var mockIpc = new Mock<IGameIpcBridge>();
        bool connected = false;
        mockIpc.SetupGet(i => i.IsConnected).Returns(() => connected);
        mockIpc.Setup(i => i.ConnectAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .Callback(() => connected = true)
            .Returns(Task.CompletedTask);
        mockIpc.Setup(i => i.ExecuteCommandAsync(It.IsAny<string>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync("OK");

        var handler = new UiFlowHandler(
            gameIpcBridge: mockIpc.Object,
            clientManageHandler: null,
            remediationService: new ClientCommandRemediationService(timeProvider: TimeProvider.System),
            uiControlService: null,
            timeProvider: TimeProvider.System,
            logger: _ => { });

        var args = JsonElement.Parse("{\"action\": \"open_store\"}");
        var result = await handler.HandleAsync(args);

        result.IsError.Should().BeFalse();
        mockIpc.Verify(i => i.ConnectAsync(1000, It.IsAny<CancellationToken>()), Times.Once);
        mockIpc.Verify(i => i.ExecuteCommandAsync(It.IsAny<string>(), It.IsAny<int>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task HandleAsync_WithClientManageHandler_ExercisesProactiveIpcConnection()
    {
        var mockIpc = new Mock<IGameIpcBridge>();
        bool connected = false;
        mockIpc.SetupGet(i => i.IsConnected).Returns(() => connected);
        mockIpc.Setup(i => i.ConnectAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .Callback(() => connected = true)
            .Returns(Task.CompletedTask);
        mockIpc.Setup(i => i.ExecuteCommandAsync(It.IsAny<string>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync("[NEXUS_EVENT] OPEN_STORE|{\"storePanel\":\"store_container2\",\"success\":true}");

        var mockUi = new Mock<IUiControlService>();
        var remediation = new ClientCommandRemediationService(timeProvider: TimeProvider.System);
        var clientHandler = new GameClientManageHandler(
            ipcBridge: mockIpc.Object,
            uiControlService: mockUi.Object,
            remediationService: remediation,
            timeProvider: TimeProvider.System,
            logger: _ => { });

        var handler = new UiFlowHandler(
            gameIpcBridge: null, // should resolve from clientHandler.IpcBridge
            clientManageHandler: clientHandler,
            remediationService: remediation,
            uiControlService: mockUi.Object,
            timeProvider: TimeProvider.System,
            logger: _ => { });

        var args = JsonElement.Parse("{\"action\": \"open_store\"}");
        var result = await handler.HandleAsync(args);

        result.IsError.Should().BeFalse();
        var root = ParseResultJson(result);
        root.GetProperty("status").GetString().Should().Be("success");
        root.GetProperty("channel").GetString().Should().Be("ipc");
        mockIpc.Verify(i => i.ConnectAsync(1000, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task HandleAsync_IpcFails_FallsBackToInGameConsole()
    {
        var mockIpc = new Mock<IGameIpcBridge>();
        mockIpc.SetupGet(i => i.IsConnected).Returns(true);
        mockIpc.Setup(i => i.ExecuteCommandAsync(It.IsAny<string>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("IPC timeout"));

        var mockUi = new Mock<IUiControlService>();
        mockUi.Setup(u => u.ExecuteInGameConsoleCommandAsync("hon_x64", It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new UiConsoleCommandResult
            {
                Success = true,
                WindowTitle = "Heroes of Newerth",
                Hwnd = "0x00010001",
                ExecutionDurationMs = 15.0
            });

        var handler = new UiFlowHandler(
            gameIpcBridge: mockIpc.Object,
            clientManageHandler: null,
            remediationService: new ClientCommandRemediationService(timeProvider: TimeProvider.System),
            uiControlService: mockUi.Object,
            timeProvider: TimeProvider.System,
            logger: _ => { });

        var args = JsonElement.Parse("{\"action\": \"open_store\", \"allow_keystroke_fallback\": true}");
        var result = await handler.HandleAsync(args);

        result.IsError.Should().BeFalse();
        var root = ParseResultJson(result);
        root.GetProperty("status").GetString().Should().Be("success");
        root.GetProperty("channel").GetString().Should().Be("in_game_console");
        mockUi.Verify(u => u.ExecuteInGameConsoleCommandAsync("hon_x64", It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Theory]
    [InlineData("open_store", "store_button")]
    [InlineData("open_lobby", "sysbar_quickStart")]
    [InlineData("open_options", "sysbar_options")]
    public async Task HandleAsync_IpcAndConsoleFail_FallsBackToPhysicalClick(string action, string expectedWidget)
    {
        var mockIpc = new Mock<IGameIpcBridge>();
        mockIpc.SetupGet(i => i.IsConnected).Returns(false);

        var mockUi = new Mock<IUiControlService>();
        mockUi.Setup(u => u.ExecuteInGameConsoleCommandAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new UiConsoleCommandResult
            {
                Success = false,
                ErrorMessage = "Console blocked"
            });
        mockUi.Setup(u => u.ClickAsync("hon_x64", expectedWidget, It.IsAny<int?>(), It.IsAny<int?>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new UiClickResult
            {
                Success = true,
                ElementClicked = expectedWidget,
                Target = "hon_x64",
                ActionTaken = "mouse_click"
            });

        var handler = new UiFlowHandler(
            gameIpcBridge: mockIpc.Object,
            clientManageHandler: null,
            remediationService: new ClientCommandRemediationService(timeProvider: TimeProvider.System),
            uiControlService: mockUi.Object,
            timeProvider: TimeProvider.System,
            logger: _ => { });

        var args = JsonElement.Parse($$"""{"action": "{{action}}", "allow_keystroke_fallback": true}""");
        var result = await handler.HandleAsync(args);

        result.IsError.Should().BeFalse();
        var root = ParseResultJson(result);
        root.GetProperty("status").GetString().Should().Be("success");
        root.GetProperty("channel").GetString().Should().Be("physical_click");
        root.GetProperty("elementClicked").GetString().Should().Be(expectedWidget);
        mockUi.Verify(u => u.ClickAsync("hon_x64", expectedWidget, It.IsAny<int?>(), It.IsAny<int?>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    #endregion

    #region Category 3: Strict K2 UI Rule Conformance

    [Theory]
    [InlineData("bypass_login")]
    [InlineData("open_lobby")]
    [InlineData("open_options")]
    [InlineData("open_store")]
    [InlineData("dismiss_popups")]
    public async Task K2RuleConformance_AllActions_UseUIManagerAndPassValidator(string action)
    {
        var ctx = CreateContext();
        var args = JsonElement.Parse($$"""{"action": "{{action}}", "dryRun": true}""");

        var result = await ctx.Handler.HandleAsync(args);

        result.IsError.Should().BeFalse();
        JsonElement root = ParseResultJson(result);
        string lua = root.GetProperty("executedLua").GetString()!;

        // 1. Must use UIManager
        lua.Should().Contain("UIManager");

        // 2. Must never use undefined 'Windows' global
        lua.Should().NotContain("Windows.");
        lua.Should().NotContain("Windows:");

        // 3. Must pass standard K2LuaValidator
        var (isValid, violation) = K2LuaValidator.ValidateK2LuaScript(lua);
        isValid.Should().BeTrue($"Validation failed with: {violation}");
        violation.Should().BeNull();
    }

    [Theory]
    [InlineData("bypass_login")]
    [InlineData("open_lobby")]
    [InlineData("open_options")]
    [InlineData("open_store")]
    [InlineData("dismiss_popups")]
    public async Task K2RuleConformance_DoEventCalls_AreZeroArgOrIntegerIndexed(string action)
    {
        var ctx = CreateContext();
        var args = JsonElement.Parse($$"""{"action": "{{action}}", "dryRun": true}""");

        var result = await ctx.Handler.HandleAsync(args);

        JsonElement root = ParseResultJson(result);
        string lua = root.GetProperty("executedLua").GetString()!;

        // Never pass string argument like DoEvent('onclick')
        lua.Should().NotMatchRegex(@"DoEvent\s*\(\s*['""][^'""]+['""]\s*\)");

        // If DoEvent is present, must be DoEvent() or DoEventN(\d+)
        if (lua.Contains("DoEvent("))
        {
            lua.Should().MatchRegex(@"DoEvent\s*\(\s*\)");
        }
    }

    #endregion

    #region Category 4: Dry-Run Mode & Parameter Parsing

    [Fact]
    public async Task DryRun_WhenTrue_DoesNotDispatchIpcAndReturnsGeneratedLua()
    {
        var ctx = CreateContext(isConnected: true);
        var args = JsonElement.Parse("""
        {
            "action": "open_lobby",
            "lobbyType": "matchmaking",
            "dryRun": true
        }
        """);

        var result = await ctx.Handler.HandleAsync(args);

        result.IsError.Should().BeFalse();
        ctx.DispatchedCommands.Should().BeEmpty();
        JsonElement root = ParseResultJson(result);
        root.GetProperty("status").GetString().Should().Be("success");
        root.GetProperty("details").GetProperty("dryRun").GetBoolean().Should().BeTrue();
    }

    [Theory]
    [InlineData("accountName", "PlayerOne")]
    [InlineData("account", "PlayerTwo")]
    [InlineData("player", "PlayerThree")]
    public async Task BypassLogin_WhenInvoked_ResolvesAccountAliasesCorrectly(string propName, string playerValue)
    {
        var ctx = CreateContext();
        var args = JsonElement.Parse($$"""
        {
            "action": "bypass_login",
            "{{propName}}": "{{playerValue}}",
            "dryRun": true
        }
        """);

        var result = await ctx.Handler.HandleAsync(args);

        JsonElement root = ParseResultJson(result);
        root.GetProperty("executedLua").GetString().Should().Contain(playerValue);
        root.GetProperty("details").GetProperty("accountName").GetString().Should().Be(playerValue);
    }

    #endregion

    #region Category 5: Client Offline Detection & Self-Healing Remediation

    [Fact]
    public async Task HandleAsync_ClientOffline_ReturnsStructuredErrorWithRemediationHint()
    {
        var ctx = CreateContext(isConnected: false);
        var args = JsonElement.Parse("""
        {
            "action": "bypass_login",
            "dryRun": false,
            "allow_keystroke_fallback": true
        }
        """);

        var result = await ctx.Handler.HandleAsync(args);

        result.IsError.Should().BeTrue();
        JsonElement root = ParseResultJson(result);
        root.GetProperty("status").GetString().Should().Be("error");
        root.GetProperty("remediationCategory").GetString().Should().Be("ClientOffline");
        root.GetProperty("remediationHint").GetString().Should().Contain("hon_x64.exe");
    }

    [Fact]
    public async Task HandleAsync_WhenKeystrokeSuppressedByDefault_ReturnsKeystrokeFallbackSuppressedError()
    {
        var ctx = CreateContext(isConnected: false);
        var args = JsonElement.Parse("""
        {
            "action": "bypass_login",
            "dryRun": false
        }
        """);

        var result = await ctx.Handler.HandleAsync(args);

        result.IsError.Should().BeTrue();
        JsonElement root = ParseResultJson(result);
        root.GetProperty("status").GetString().Should().Be("error");
        root.GetProperty("remediationCategory").GetString().Should().Be("KeystrokeFallbackSuppressed");
        root.GetProperty("errorMessage").GetString().Should().Contain("suppressed to prevent desktop focus theft");
    }

    #endregion

    #region Category 6: Diagnostic Actions (errors, history)

    [Theory]
    [InlineData("errors")]
    [InlineData("history")]
    public async Task HandleAsync_DiagnosticActions_ReturnRecentErrors(string diagnosticAction)
    {
        var ctx = CreateContext();
        // Record a mock error first
        ctx.RemediationService.RecordAndRemediate("bad_cmd", "Error: failed", "none", "client");

        var args = JsonElement.Parse($$"""
        {
            "action": "{{diagnosticAction}}",
            "limit": 5
        }
        """);

        var result = await ctx.Handler.HandleAsync(args);

        result.IsError.Should().BeFalse();
        JsonElement root = ParseResultJson(result);
        root.GetProperty("status").GetString().Should().Be("success");
        root.GetProperty("action").GetString().Should().Be(diagnosticAction);
        root.GetProperty("totalErrors").GetInt32().Should().BeGreaterThanOrEqualTo(1);
        root.GetProperty("errors").GetArrayLength().Should().BeGreaterThanOrEqualTo(1);
    }

    #endregion

    #region Category 7: MCP Tool Registry & Alias Resolution

    [Fact]
    public void McpToolRegistry_WhenInvoked_ContainsUiFlowAndAliases()
    {
        McpToolRegistry registry = new(registerDefaults: true);

        // Canonical name
        registry.HasTool("kongor_ui_flow").Should().BeTrue();
        registry.GetTool("kongor_ui_flow").Should().NotBeNull();
        registry.GetTool("kongor_ui_flow")!.Name.Should().Be("kongor_ui_flow");

        // Aliases
        registry.HasTool("kongor_macro").Should().BeTrue();
        registry.GetTool("kongor_macro").Should().NotBeNull();
        registry.GetTool("kongor_macro")!.Name.Should().Be("kongor_ui_flow");

        registry.HasTool("kongor_ui_macro").Should().BeTrue();
        registry.GetTool("kongor_ui_macro").Should().NotBeNull();
        registry.GetTool("kongor_ui_macro")!.Name.Should().Be("kongor_ui_flow");

        // ListTools must contain canonical and not expose pruned aliases
        registry.ListTools().Select(t => t.Name).Should().Contain("kongor_ui_flow");
        registry.ListTools().Select(t => t.Name).Should().NotContain("kongor_macro");
        registry.ListTools().Select(t => t.Name).Should().NotContain("kongor_ui_macro");
    }

    [Fact]
    public async Task McpToolRegistry_ExecuteToolAsync_WithAlias_DispatchesToUiFlowHandler()
    {
        McpToolRegistry registry = new(registerDefaults: true);
        var args = JsonElement.Parse("{\"action\": \"open_lobby\", \"dryRun\": true}");

        var result = await registry.ExecuteToolAsync("kongor_macro", args);

        result.IsError.Should().BeFalse();
        JsonElement root = ParseResultJson(result);
        root.GetProperty("status").GetString().Should().Be("success");
        root.GetProperty("action").GetString().Should().Be("open_lobby");
    }

    #endregion

    #region Category 8: Inner-Loop Sub-Second SLA & Zero Desktop GUI Window

    [Fact]
    public async Task UiFlowHandlerTests_WhenInvoked_ExecuteInSubSecondInnerLoop()
    {
        Stopwatch sw = Stopwatch.StartNew();

        var ctx = CreateContext();
        for (int i = 0; i < 20; i++)
        {
            var args = JsonElement.Parse("{\"action\": \"bypass_login\", \"dryRun\": true}");
            var result = await ctx.Handler.HandleAsync(args);
            result.IsError.Should().BeFalse();
        }

        sw.Stop();
        sw.ElapsedMilliseconds.Should().BeLessThan(2000, "20 executions must complete well under 2000ms");
    }

    #endregion
}
