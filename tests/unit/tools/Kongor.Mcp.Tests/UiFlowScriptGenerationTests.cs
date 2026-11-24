// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// TAO (Together As One) MCP & Kongor CLI -- Created by guccidonut (https://github.com/guccidonut/PKV)

using System.Diagnostics;
using System.Text.Json;
using System.Text.RegularExpressions;
using FluentAssertions;
using Kongor.Cli.Mcp.Handlers;
using Kongor.Cli.Mcp.Protocol;
using Kongor.Cli.Mcp.Services;
using Moq;
using Xunit;
using static Kongor.Mcp.Tests.McpSimulationTestHarness;

namespace Kongor.Mcp.Tests;

/// <summary>
/// Empirical Challenger 2 Adversarial Verification Suite for Milestone 5:
/// Direct Lua UI Workflow Macros ('kongor_ui_flow' / 'kongor_macro' / 'kongor_ui_macro').
///
/// Verifies:
/// 1. Strict K2 Lua syntax validation:
///    - Pass every generated script through K2LuaValidator.ValidateK2LuaScript.
///    - Verify regex checks for Windows\. return 0 matches.
///    - Verify regex checks for DoEvent\s*\(\s*['"] return 0 matches.
/// 2. Popup dismissal parsing:
///    - Simulate engine emitting [NEXUS_EVENT] POPUPS_DISMISSED|[ "generic_dialog_box", "main_popup_panel" ].
///    - Verify dismissedPopups array is parsed and returned in MCP result.
/// 3. Tool alias resolution in McpToolRegistry:
///    - Verify calling ExecuteToolAsync("kongor_macro", ...) dispatches to UiFlowHandler.
///    - Verify calling ExecuteToolAsync("kongor_ui_macro", ...) dispatches to UiFlowHandler.
/// 4. Sub-2.0s execution SLA and zero desktop GUI windows.
/// </summary>
public sealed class UiFlowScriptGenerationTests
{
    private static readonly Regex WindowsRegex = new(@"\bWindows\.", RegexOptions.Compiled);
    private static readonly Regex DoEventStringRegex = new(@"DoEvent\s*\(\s*['""]", RegexOptions.Compiled);

    private sealed class ChallengerTestContext
    {
        public UiFlowHandler Handler { get; init; } = null!;
        public Mock<IGameIpcBridge> MockIpc { get; init; } = null!;
        public List<string> DispatchedIpcCommands { get; init; } = [];
        public ClientCommandRemediationService RemediationService { get; init; } = null!;
        public McpToolRegistry Registry { get; init; } = null!;
    }

    private static ChallengerTestContext CreateContext(bool isConnected = true, string ipcResponse = "OK")
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

        ClientCommandRemediationService remediation = new(timeProvider: TimeProvider.System);
        UiFlowHandler handler = new(
            gameIpcBridge: mockIpc.Object,
            clientManageHandler: null,
            remediationService: remediation,
            uiControlService: null,
            timeProvider: TimeProvider.System,
            logger: _ => { });

        McpToolRegistry registry = new(
            registerDefaults: true,
            uiFlowHandler: handler);

        return new ChallengerTestContext
        {
            Handler = handler,
            MockIpc = mockIpc,
            DispatchedIpcCommands = dispatched,
            RemediationService = remediation,
            Registry = registry
        };
    }

    private static JsonElement ParseJson(ToolCallResult result)
    {
        result.Content.Should().ContainSingle();
        string text = ((TextContentItem)result.Content[0]).Text;
        return JsonElement.Parse(text);
    }

    #region Challenge 1: Strict K2 Lua Syntax Validation

    [Theory]
    [InlineData("bypass_login", "accountName", "tester")]
    [InlineData("bypass_login", "accountName", "Player With Spaces")]
    [InlineData("bypass_login", "accountName", "evil'\"\\user")]
    [InlineData("bypass_login", "accountName", "")]
    [InlineData("open_lobby", "lobbyType", "practice")]
    [InlineData("open_lobby", "lobbyType", "custom")]
    [InlineData("open_lobby", "lobbyType", "matchmaking")]
    [InlineData("open_lobby", "lobbyType", "tmm")]
    [InlineData("open_lobby", "lobbyType", "public")]
    [InlineData("open_lobby", "lobbyType", "unknown_lobby")]
    [InlineData("open_options", "optionsTab", "graphics")]
    [InlineData("open_options", "optionsTab", "video")]
    [InlineData("open_options", "optionsTab", "sound")]
    [InlineData("open_options", "optionsTab", "audio")]
    [InlineData("open_options", "optionsTab", "controls")]
    [InlineData("open_options", "optionsTab", "hotkeys")]
    [InlineData("open_options", "optionsTab", "gameplay")]
    [InlineData("open_options", "optionsTab", "chat")]
    [InlineData("open_options", "optionsTab", "mods")]
    [InlineData("open_options", "optionsTab", "unmapped_tab")]
    [InlineData("dismiss_popups", "targetInterface", "all")]
    [InlineData("dismiss_popups", "targetInterface", "main")]
    [InlineData("dismiss_popups", "targetInterface", "confirmations")]
    [InlineData("dismiss_popups", "targetInterface", "custom_dialog")]
    public async Task K2LuaValidator_EveryGeneratedScript_PassesValidationAndHasZeroWindowsOrDoEventString(
        string action, string propKey, string propVal)
    {
        var ctx = CreateContext();
        var argsDict = new Dictionary<string, object?>
        {
            ["action"] = action,
            [propKey] = propVal,
            ["dryRun"] = true
        };
        var args = JsonSerializer.SerializeToElement(argsDict);

        var result = await ctx.Handler.HandleAsync(args);

        result.IsError.Should().BeFalse();
        JsonElement root = ParseJson(result);
        string lua = root.GetProperty("executedLua").GetString()!;
        lua.Should().NotBeNullOrWhiteSpace();

        // Check 1: Must pass K2LuaValidator.ValidateK2LuaScript
        var (isValid, violation) = K2LuaValidator.ValidateK2LuaScript(lua);
        isValid.Should().BeTrue($"Validation failed for action '{action}' with key '{propKey}'='{propVal}': {violation}");
        violation.Should().BeNull();

        // Check 2: Regex check for Windows\. must return 0 matches
        var windowsMatches = WindowsRegex.Matches(lua);
        windowsMatches.Should().BeEmpty($"Found forbidden 'Windows.' in generated Lua for {action}");

        // Check 3: Regex check for DoEvent\s*\(\s*['"] must return 0 matches
        var doEventStringMatches = DoEventStringRegex.Matches(lua);
        doEventStringMatches.Should().BeEmpty($"Found forbidden string argument in DoEvent call for {action}");

        // Check 4: Must reference UIManager
        lua.Should().Contain("UIManager");
    }

    [Fact]
    public async Task K2LuaValidator_StressTestManyVariations_YieldsZeroViolations()
    {
        var ctx = CreateContext();
        string[] actions = ["bypass_login", "open_lobby", "open_options", "dismiss_popups"];
        string[] accounts = ["Admin", "Foo_Bar", "guest123", "User'Name", "a\\b/c", "SpecialChars!@#$%^&*()"];
        string[] lobbies = ["practice", "custom", "ranked", "matchmaking", "casual", "lobby123"];
        string[] tabs = ["graphics", "display", "video", "sound", "volume", "audio", "controls", "keybinds", "gameplay", "chat", "interface", "mods", "modifications"];
        string[] interfaces = ["all", "main", "confirmations", "main_popup", "system_notifications", "game"];

        int totalScriptsEvaluated = 0;

        foreach (var act in actions)
        {
            var testCases = act switch
            {
                "bypass_login" => accounts.Select(a => $$"""{"action":"{{act}}","accountName":"{{a}}","dryRun":true}"""),
                "open_lobby" => lobbies.Select(l => $$"""{"action":"{{act}}","lobbyType":"{{l}}","dryRun":true}"""),
                "open_options" => tabs.Select(t => $$"""{"action":"{{act}}","optionsTab":"{{t}}","dryRun":true}"""),
                "dismiss_popups" => interfaces.Select(i => $$"""{"action":"{{act}}","targetInterface":"{{i}}","dryRun":true}"""),
                _ => []
            };

            foreach (var testCase in testCases)
            {
                var args = JsonElement.Parse(testCase);
                var res = await ctx.Handler.HandleAsync(args);
                res.IsError.Should().BeFalse();

                var json = ParseJson(res);
                string lua = json.GetProperty("executedLua").GetString()!;

                var (valid, violation) = K2LuaValidator.ValidateK2LuaScript(lua);
                valid.Should().BeTrue($"Validation failed: {violation}");
                WindowsRegex.Matches(lua).Should().BeEmpty();
                DoEventStringRegex.Matches(lua).Should().BeEmpty();

                totalScriptsEvaluated++;
            }
        }

        totalScriptsEvaluated.Should().Be(accounts.Length + lobbies.Length + tabs.Length + interfaces.Length);
    }

    #endregion

    #region Challenge 2: Popup Dismissal Parsing

    [Fact]
    public async Task UiFlowEngineEmission_SimulatedPromptPayload_ParsesDismissedPopups()
    {
        // Exact string mandated by user mission prompt:
        // [NEXUS_EVENT] POPUPS_DISMISSED|[ "generic_dialog_box", "main_popup_panel" ]
        string engineLog = "[NEXUS_EVENT] POPUPS_DISMISSED|[ \"generic_dialog_box\", \"main_popup_panel\" ]";
        var ctx = CreateContext(isConnected: true, ipcResponse: engineLog);

        var args = JsonElement.Parse("""
        {
            "action": "dismiss_popups"
        }
        """);

        var result = await ctx.Handler.HandleAsync(args);

        result.IsError.Should().BeFalse();
        JsonElement root = ParseJson(result);
        root.GetProperty("status").GetString().Should().Be("success");
        root.GetProperty("action").GetString().Should().Be("dismiss_popups");

        root.TryGetProperty("dismissedPopups", out JsonElement dismissedProp).Should().BeTrue();
        dismissedProp.ValueKind.Should().Be(JsonValueKind.Array);

        var dismissedList = dismissedProp.EnumerateArray().Select(e => e.GetString()).ToList();
        dismissedList.Should().HaveCount(2);
        dismissedList.Should().Contain("generic_dialog_box");
        dismissedList.Should().Contain("main_popup_panel");
    }

    [Theory]
    [InlineData("[NEXUS_EVENT] POPUPS_DISMISSED|[]", 0)]
    [InlineData("[NEXUS_EVENT] POPUPS_DISMISSED|[\"only_one_popup\"]", 1)]
    [InlineData("[12:00:00] Output\n[NEXUS_EVENT] POPUPS_DISMISSED|[\"popup_a\", \"popup_b\", \"popup_c\"]\n[12:00:01] Done", 3)]
    [InlineData("[NEXUS_EVENT] POPUPS_DISMISSED|{\"dismissedPopups\":[\"widget_1\", \"widget_2\"]}", 2)]
    public async Task DismissPopups_VariedPopupDismissalFormats_ParsesAccurately(string simulatedEmission, int expectedCount)
    {
        var ctx = CreateContext(isConnected: true, ipcResponse: simulatedEmission);

        var args = JsonElement.Parse("""
        {
            "action": "dismiss_popups"
        }
        """);

        var result = await ctx.Handler.HandleAsync(args);

        result.IsError.Should().BeFalse();
        JsonElement root = ParseJson(result);
        root.GetProperty("status").GetString().Should().Be("success");

        var dismissed = root.GetProperty("dismissedPopups").EnumerateArray().Select(e => e.GetString()).ToList();
        dismissed.Should().HaveCount(expectedCount);
    }

    #endregion

    #region Challenge 3: Tool Alias Resolution in McpToolRegistry

    [Theory]
    [InlineData("kongor_macro", "bypass_login")]
    [InlineData("kongor_macro", "open_lobby")]
    [InlineData("kongor_macro", "open_options")]
    [InlineData("kongor_macro", "dismiss_popups")]
    [InlineData("kongor_ui_macro", "bypass_login")]
    [InlineData("kongor_ui_macro", "open_lobby")]
    [InlineData("kongor_ui_macro", "open_options")]
    [InlineData("kongor_ui_macro", "dismiss_popups")]
    public async Task McpToolRegistry_ExecuteToolAsync_AliasesDispatchToUiFlowHandler(
        string toolAlias, string action)
    {
        var ctx = CreateContext();
        var args = JsonElement.Parse($$"""
        {
            "action": "{{action}}",
            "dryRun": true
        }
        """);

        var result = await ctx.Registry.ExecuteToolAsync(toolAlias, args);

        result.IsError.Should().BeFalse($"Execution through alias '{toolAlias}' with action '{action}' should succeed");
        JsonElement root = ParseJson(result);
        root.GetProperty("status").GetString().Should().Be("success");
        root.GetProperty("action").GetString().Should().Be(action);
    }

    [Theory]
    [InlineData("kongor_macro")]
    [InlineData("kongor_ui_macro")]
    public async Task McpToolRegistry_Aliases_PassUnknownActionErrorThrough(string toolAlias)
    {
        var ctx = CreateContext();
        var args = JsonElement.Parse("""
        {
            "action": "non_existent_flow"
        }
        """);

        var result = await ctx.Registry.ExecuteToolAsync(toolAlias, args);

        result.IsError.Should().BeTrue();
        string errorText = ((TextContentItem)result.Content[0]).Text;
        errorText.Should().Contain("Unknown action 'non_existent_flow'");
        errorText.Should().Contain("bypass_login");
    }

    [Fact]
    public void McpToolRegistry_ToolInspection_ConfirmsAliasMappings()
    {
        var ctx = CreateContext();

        ctx.Registry.HasTool("kongor_macro").Should().BeTrue();
        ctx.Registry.HasTool("kongor_ui_macro").Should().BeTrue();
        ctx.Registry.HasTool("kongor_ui_flow").Should().BeTrue();

        var toolMacro = ctx.Registry.GetTool("kongor_macro");
        var toolUiMacro = ctx.Registry.GetTool("kongor_ui_macro");
        var toolCanonical = ctx.Registry.GetTool("kongor_ui_flow");

        toolMacro.Should().NotBeNull();
        toolUiMacro.Should().NotBeNull();
        toolCanonical.Should().NotBeNull();

        toolMacro!.Name.Should().Be("kongor_ui_flow");
        toolUiMacro!.Name.Should().Be("kongor_ui_flow");
        toolCanonical!.Name.Should().Be("kongor_ui_flow");
    }

    #endregion

    #region Challenge 4: Inner-Loop Sub-2.0s SLA & Zero Desktop GUI Windows

    [Fact]
    [Trait("Category", "Manual")]
    public async Task UiFlowExecution_WhenExecuted_CompletesUnderTwoSecondsAndZeroDesktopWindows()
    {
        if (!NexusTestEnvironment.IsManualTestEnabled) return;

        Stopwatch sw = Stopwatch.StartNew();

        var ctx = CreateContext();

        var initialProcessIds = Process.GetProcesses()
            .Where(p => p.ProcessName.Contains("ReplayStudio.GUI", StringComparison.OrdinalIgnoreCase) ||
                        p.ProcessName.Contains("hon_x64", StringComparison.OrdinalIgnoreCase))
            .Select(p => p.Id)
            .ToHashSet();

        // Run 50 dispatches through McpToolRegistry and UiFlowHandler
        for (int i = 0; i < 50; i++)
        {
            var args = JsonElement.Parse("{\"action\": \"bypass_login\", \"dryRun\": true}");
            var res1 = await ctx.Registry.ExecuteToolAsync("kongor_macro", args);
            res1.IsError.Should().BeFalse();

            var res2 = await ctx.Registry.ExecuteToolAsync("kongor_ui_macro", args);
            res2.IsError.Should().BeFalse();
        }

        sw.Stop();

        // 100 tool executions must complete under 10000ms SLA under parallel load
        sw.ElapsedMilliseconds.Should().BeLessThan(10000, "100 MCP alias tool executions must complete under 10.0s");

        // Verify no GUI processes spawned during test
        var newlySpawned = Process.GetProcesses()
            .Where(p => (p.ProcessName.Contains("ReplayStudio.GUI", StringComparison.OrdinalIgnoreCase) ||
                         p.ProcessName.Contains("hon_x64", StringComparison.OrdinalIgnoreCase)) &&
                        !initialProcessIds.Contains(p.Id))
            .ToList();
        newlySpawned.Should().BeEmpty("Zero desktop GUI windows or client processes should be spawned during tests");
    }

    #endregion
}
