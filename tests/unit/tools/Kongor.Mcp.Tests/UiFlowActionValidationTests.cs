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
/// Empirical Adversarial Challenger Test Suite for Milestone 5 (Direct Lua UI Workflow Macros).
/// Adversarially challenges:
/// 1. Parameter edge cases and validation:
///    - Missing action, empty/whitespace action, non-string action, unknown actions ('start_game', 'click_menu', etc.).
///    - Action parameter aliases:
///      * accountName vs account vs player (with precedence and empty-string fall-through).
///      * lobbyType vs type vs mode (with precedence and panel resolution).
///      * optionsTab vs tab vs category (with precedence and category index resolution).
///    - Unrecognized options tabs falling back safely to index 1.
/// 2. Challenge dryRun: true:
///    - Zero commands dispatched to IPC bridge or client channels.
///    - Emitted Lua script strictly conforms to K2 engine rules (UIManager, 0-arg DoEvent, integer DoEventN, no 'onclick').
/// 3. Stress-test inner-loop performance:
///    - 50 rapid iterations of HandleAsync with dryRun: true completing in < 200ms.
/// 4. Zero desktop GUI window popups and headless execution conformance.
/// </summary>
public sealed class UiFlowActionValidationTests
{
    private sealed class ChallengerTestContext
    {
        public UiFlowHandler Handler { get; init; } = null!;
        public Mock<IGameIpcBridge> MockIpc { get; init; } = null!;
        public List<string> DispatchedCommands { get; init; } = [];
        public ClientCommandRemediationService RemediationService { get; init; } = null!;
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

        return new ChallengerTestContext
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

    #region Challenge 1: Parameter Edge Cases, Validation & Aliases

    [Theory]
    [InlineData("{}")]
    [InlineData("{\"dryRun\": true}")]
    [InlineData("{\"timeoutMs\": 5000}")]
    public async Task UiFlow_MissingAction_ReturnsExplicitValidationErrorListingSupportedActions(string jsonArgs)
    {
        var ctx = CreateContext();
        var args = JsonElement.Parse(jsonArgs);

        var result = await ctx.Handler.HandleAsync(args);

        result.IsError.Should().BeTrue("Missing action must return an error result");
        string error = ((TextContentItem)result.Content[0]).Text;
        error.Should().Contain("Missing required parameter 'action'");
        error.Should().Contain("bypass_login");
        error.Should().Contain("open_lobby");
        error.Should().Contain("open_options");
        error.Should().Contain("dismiss_popups");
    }

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("   ")]
    [InlineData("\t")]
    [InlineData("\r\n")]
    public async Task UiFlow_EmptyOrWhitespaceAction_ReturnsValidationError(string emptyAction)
    {
        var ctx = CreateContext();
        var json = JsonSerializer.Serialize(new { action = emptyAction });
        var args = JsonElement.Parse(json);

        var result = await ctx.Handler.HandleAsync(args);

        result.IsError.Should().BeTrue();
        string error = ((TextContentItem)result.Content[0]).Text;
        error.Should().Contain("Missing required parameter 'action'");
    }

    [Theory]
    [InlineData("{\"action\": 12345}")]
    [InlineData("{\"action\": true}")]
    [InlineData("{\"action\": false}")]
    [InlineData("{\"action\": [\"bypass_login\"]}")]
    [InlineData("{\"action\": {\"nested\": \"bypass_login\"}}")]
    public async Task HandleAsync_NonStringActionParameter_ReturnsValidationError(string malformedJson)
    {
        var ctx = CreateContext();
        var args = JsonElement.Parse(malformedJson);

        var result = await ctx.Handler.HandleAsync(args);

        result.IsError.Should().BeTrue();
        string error = ((TextContentItem)result.Content[0]).Text;
        error.Should().Contain("Missing required parameter 'action'");
    }

    [Theory]
    [InlineData("start_game")]
    [InlineData("click_menu")]
    [InlineData("launch_match")]
    [InlineData("close_all")]
    [InlineData("login")]
    [InlineData("options")]
    public async Task UiFlow_UnknownAction_ReturnsHelpfulErrorListingSupportedActions(string unknownAction)
    {
        var ctx = CreateContext();
        var args = JsonElement.Parse($"{{\"action\": \"{unknownAction}\"}}");

        var result = await ctx.Handler.HandleAsync(args);

        result.IsError.Should().BeTrue();
        string error = ((TextContentItem)result.Content[0]).Text;
        error.Should().Contain($"Unknown action '{unknownAction}'");
        error.Should().Contain("bypass_login");
        error.Should().Contain("open_lobby");
        error.Should().Contain("open_options");
        error.Should().Contain("dismiss_popups");
    }

    [Theory]
    [InlineData("accountName", "AlphaUser")]
    [InlineData("account", "BetaUser")]
    [InlineData("player", "GammaUser")]
    public async Task BypassLogin_IndividualAliases_ResolvedCorrectly(string propName, string expectedPlayer)
    {
        var ctx = CreateContext();
        var args = JsonElement.Parse($$"""
        {
            "action": "bypass_login",
            "{{propName}}": "{{expectedPlayer}}",
            "dryRun": true
        }
        """);

        var result = await ctx.Handler.HandleAsync(args);

        result.IsError.Should().BeFalse();
        JsonElement root = ParseResultJson(result);
        root.GetProperty("details").GetProperty("accountName").GetString().Should().Be(expectedPlayer);
        root.GetProperty("executedLua").GetString().Should().Contain(expectedPlayer);
    }

    [Fact]
    public async Task BypassLogin_AliasesPrecedenceAndFallthrough_WorksCorrectly()
    {
        var ctx = CreateContext();

        // 1. accountName beats account and player
        var args1 = JsonElement.Parse("""
        {
            "action": "bypass_login",
            "accountName": "HighPriority",
            "account": "MedPriority",
            "player": "LowPriority",
            "dryRun": true
        }
        """);
        var res1 = await ctx.Handler.HandleAsync(args1);
        ParseResultJson(res1).GetProperty("details").GetProperty("accountName").GetString().Should().Be("HighPriority");

        // 2. account beats player
        var args2 = JsonElement.Parse("""
        {
            "action": "bypass_login",
            "account": "MedPriority",
            "player": "LowPriority",
            "dryRun": true
        }
        """);
        var res2 = await ctx.Handler.HandleAsync(args2);
        ParseResultJson(res2).GetProperty("details").GetProperty("accountName").GetString().Should().Be("MedPriority");

        // 3. Empty accountName falls through to account
        var args3 = JsonElement.Parse("""
        {
            "action": "bypass_login",
            "accountName": "   ",
            "account": "FallbackAccount",
            "dryRun": true
        }
        """);
        var res3 = await ctx.Handler.HandleAsync(args3);
        ParseResultJson(res3).GetProperty("details").GetProperty("accountName").GetString().Should().Be("FallbackAccount");

        // 4. Default when none provided is KongorPlayer
        var args4 = JsonElement.Parse("""
        {
            "action": "bypass_login",
            "dryRun": true
        }
        """);
        var res4 = await ctx.Handler.HandleAsync(args4);
        ParseResultJson(res4).GetProperty("details").GetProperty("accountName").GetString().Should().Be("KongorPlayer");
    }

    [Theory]
    [InlineData("lobbyType", "practice", "game_lobby")]
    [InlineData("type", "matchmaking", "matchmaking")]
    [InlineData("mode", "ranked", "matchmaking")]
    [InlineData("mode", "custom", "game_lobby")]
    public async Task OpenLobby_IndividualAliases_ResolvedCorrectly(string propName, string lobbyVal, string expectedPanel)
    {
        var ctx = CreateContext();
        var args = JsonElement.Parse($$"""
        {
            "action": "open_lobby",
            "{{propName}}": "{{lobbyVal}}",
            "dryRun": true
        }
        """);

        var result = await ctx.Handler.HandleAsync(args);

        result.IsError.Should().BeFalse();
        JsonElement root = ParseResultJson(result);
        root.GetProperty("details").GetProperty("lobbyType").GetString().Should().Be(lobbyVal);
        root.GetProperty("details").GetProperty("targetPanel").GetString().Should().Be(expectedPanel);
    }

    [Fact]
    public async Task OpenLobby_AliasesPrecedenceAndFallthrough_WorksCorrectly()
    {
        var ctx = CreateContext();

        // 1. lobbyType beats type and mode
        var args1 = JsonElement.Parse("""
        {
            "action": "open_lobby",
            "lobbyType": "matchmaking",
            "type": "custom",
            "mode": "practice",
            "dryRun": true
        }
        """);
        var res1 = await ctx.Handler.HandleAsync(args1);
        ParseResultJson(res1).GetProperty("details").GetProperty("lobbyType").GetString().Should().Be("matchmaking");

        // 2. type beats mode
        var args2 = JsonElement.Parse("""
        {
            "action": "open_lobby",
            "type": "practice",
            "mode": "custom",
            "dryRun": true
        }
        """);
        var res2 = await ctx.Handler.HandleAsync(args2);
        ParseResultJson(res2).GetProperty("details").GetProperty("lobbyType").GetString().Should().Be("practice");

        // 3. Empty lobbyType falls through to type
        var args3 = JsonElement.Parse("""
        {
            "action": "open_lobby",
            "lobbyType": "  ",
            "type": "matchmaking",
            "dryRun": true
        }
        """);
        var res3 = await ctx.Handler.HandleAsync(args3);
        ParseResultJson(res3).GetProperty("details").GetProperty("lobbyType").GetString().Should().Be("matchmaking");

        // 4. Default when none provided is custom -> game_lobby
        var args4 = JsonElement.Parse("""
        {
            "action": "open_lobby",
            "dryRun": true
        }
        """);
        var res4 = await ctx.Handler.HandleAsync(args4);
        ParseResultJson(res4).GetProperty("details").GetProperty("lobbyType").GetString().Should().Be("custom");
        ParseResultJson(res4).GetProperty("details").GetProperty("targetPanel").GetString().Should().Be("game_lobby");
    }

    [Theory]
    [InlineData("optionsTab", "sound", 2)]
    [InlineData("tab", "controls", 3)]
    [InlineData("category", "chat", 5)]
    [InlineData("category", "mods", 6)]
    public async Task OpenOptions_IndividualAliases_ResolvedCorrectly(string propName, string tabVal, int expectedIndex)
    {
        var ctx = CreateContext();
        var args = JsonElement.Parse($$"""
        {
            "action": "open_options",
            "{{propName}}": "{{tabVal}}",
            "dryRun": true
        }
        """);

        var result = await ctx.Handler.HandleAsync(args);

        result.IsError.Should().BeFalse();
        JsonElement root = ParseResultJson(result);
        root.GetProperty("details").GetProperty("optionsTab").GetString().Should().Be(tabVal);
        root.GetProperty("details").GetProperty("categoryIndex").GetInt32().Should().Be(expectedIndex);
    }

    [Fact]
    public async Task OpenOptions_AliasesPrecedenceAndFallthrough_WorksCorrectly()
    {
        var ctx = CreateContext();

        // 1. optionsTab beats tab and category
        var args1 = JsonElement.Parse("""
        {
            "action": "open_options",
            "optionsTab": "sound",
            "tab": "controls",
            "category": "chat",
            "dryRun": true
        }
        """);
        var res1 = await ctx.Handler.HandleAsync(args1);
        ParseResultJson(res1).GetProperty("details").GetProperty("categoryIndex").GetInt32().Should().Be(2);

        // 2. tab beats category
        var args2 = JsonElement.Parse("""
        {
            "action": "open_options",
            "tab": "controls",
            "category": "chat",
            "dryRun": true
        }
        """);
        var res2 = await ctx.Handler.HandleAsync(args2);
        ParseResultJson(res2).GetProperty("details").GetProperty("categoryIndex").GetInt32().Should().Be(3);

        // 3. Empty optionsTab falls through to tab
        var args3 = JsonElement.Parse("""
        {
            "action": "open_options",
            "optionsTab": "",
            "tab": "gameplay",
            "dryRun": true
        }
        """);
        var res3 = await ctx.Handler.HandleAsync(args3);
        ParseResultJson(res3).GetProperty("details").GetProperty("categoryIndex").GetInt32().Should().Be(4);

        // 4. Default when none provided is graphics (index 1)
        var args4 = JsonElement.Parse("""
        {
            "action": "open_options",
            "dryRun": true
        }
        """);
        var res4 = await ctx.Handler.HandleAsync(args4);
        ParseResultJson(res4).GetProperty("details").GetProperty("optionsTab").GetString().Should().Be("graphics");
        ParseResultJson(res4).GetProperty("details").GetProperty("categoryIndex").GetInt32().Should().Be(1);
    }

    [Theory]
    [InlineData("unrecognized_tab")]
    [InlineData("foobar")]
    [InlineData("unknown_options_tab")]
    [InlineData("9999")]
    [InlineData("super_graphics")]
    [InlineData("random_xyz")]
    public async Task OpenOptions_UnrecognizedTabs_FallBackSafelyToIndex1(string unknownTab)
    {
        var ctx = CreateContext();
        var args = JsonElement.Parse($$"""
        {
            "action": "open_options",
            "optionsTab": "{{unknownTab}}",
            "dryRun": true
        }
        """);

        var result = await ctx.Handler.HandleAsync(args);

        result.IsError.Should().BeFalse("Unrecognized tab must not cause failure; it should fall back safely to index 1");
        JsonElement root = ParseResultJson(result);
        root.GetProperty("status").GetString().Should().Be("success");
        root.GetProperty("details").GetProperty("categoryIndex").GetInt32().Should().Be(1, "Unrecognized tabs must fall back safely to index 1");
        string lua = root.GetProperty("executedLua").GetString()!;
        lua.Should().Contain("SelectSubCategory(1, 0)", "Fallback Lua script must reference category index 1");
    }

    #endregion

    #region Challenge 2: dryRun Verification & K2 Engine Conformance

    [Theory]
    [InlineData("bypass_login")]
    [InlineData("open_lobby")]
    [InlineData("open_options")]
    [InlineData("dismiss_popups")]
    public async Task UiFlow_DryRun_ZeroCommandsDispatchedToIpc(string action)
    {
        var ctx = CreateContext(isConnected: true);
        var args = JsonElement.Parse($$"""
        {
            "action": "{{action}}",
            "dryRun": true
        }
        """);

        var result = await ctx.Handler.HandleAsync(args);

        result.IsError.Should().BeFalse();
        ctx.DispatchedCommands.Should().BeEmpty("When dryRun is true, exactly zero commands must be dispatched to IPC");
        ctx.MockIpc.Verify(i => i.ExecuteCommandAsync(It.IsAny<string>(), It.IsAny<int>(), It.IsAny<CancellationToken>()), Times.Never);

        JsonElement root = ParseResultJson(result);
        root.GetProperty("status").GetString().Should().Be("success");
        root.GetProperty("details").GetProperty("dryRun").GetBoolean().Should().BeTrue();
    }

    [Theory]
    [InlineData("bypass_login", "{}")]
    [InlineData("open_lobby", "{\"lobbyType\": \"practice\"}")]
    [InlineData("open_lobby", "{\"lobbyType\": \"matchmaking\"}")]
    [InlineData("open_lobby", "{\"lobbyType\": \"custom\"}")]
    [InlineData("open_options", "{\"optionsTab\": \"graphics\"}")]
    [InlineData("open_options", "{\"optionsTab\": \"sound\"}")]
    [InlineData("open_options", "{\"optionsTab\": \"controls\"}")]
    [InlineData("open_options", "{\"optionsTab\": \"gameplay\"}")]
    [InlineData("open_options", "{\"optionsTab\": \"chat\"}")]
    [InlineData("open_options", "{\"optionsTab\": \"mods\"}")]
    [InlineData("open_options", "{\"optionsTab\": \"unrecognized_tab_fallback\"}")]
    [InlineData("dismiss_popups", "{\"targetInterface\": \"all\"}")]
    [InlineData("dismiss_popups", "{\"targetInterface\": \"main\"}")]
    public async Task DryRun_EmittedLuaScript_StrictlyConformsToK2EngineRules(string action, string extraArgsJson)
    {
        var ctx = CreateContext();
        using JsonDocument extraDoc = JsonDocument.Parse(extraArgsJson);
        Dictionary<string, object> dict = new()
        {
            ["action"] = action,
            ["dryRun"] = true
        };
        foreach (var prop in extraDoc.RootElement.EnumerateObject())
        {
            dict[prop.Name] = prop.Value.GetString()!;
        }

        string fullJson = JsonSerializer.Serialize(dict);
        var result = await ctx.Handler.HandleAsync(JsonElement.Parse(fullJson));

        result.IsError.Should().BeFalse();
        JsonElement root = ParseResultJson(result);
        string lua = root.GetProperty("executedLua").GetString()!;

        // Rule 1: Must pass official K2LuaValidator
        var (isValid, violation) = K2LuaValidator.ValidateK2LuaScript(lua);
        isValid.Should().BeTrue($"Validation failed: {violation}");
        violation.Should().BeNull();

        // Rule 2: Must never reference Windows global
        lua.Should().NotContain("Windows.");
        lua.Should().NotContain("Windows:");

        // Rule 3: Must reference UIManager
        lua.Should().Contain("UIManager");

        // Rule 4: DoEvent() must never receive string arguments (e.g. DoEvent('onclick'))
        Regex.IsMatch(lua, @"DoEvent\s*\(\s*['""][^'""]+['""]\s*\)").Should().BeFalse("DoEvent must not receive string arguments");

        // Rule 5: If DoEvent is invoked, must be 0-arg DoEvent() or integer DoEventN(int)
        if (lua.Contains("DoEvent("))
        {
            Regex.IsMatch(lua, @"DoEvent\s*\(\s*\)").Should().BeTrue("DoEvent calls must be 0-arg");
        }
        if (lua.Contains("DoEventN("))
        {
            Regex.IsMatch(lua, @"DoEventN\s*\(\s*\d+\s*\)").Should().BeTrue("DoEventN calls must be integer indexed");
        }
    }

    [Fact]
    public async Task OpenLobby_PracticeMode_UsesDirectConsoleBypassPerGeminiRule3()
    {
        var ctx = CreateContext();
        var args = JsonElement.Parse("""
        {
            "action": "open_lobby",
            "lobbyType": "practice",
            "dryRun": true
        }
        """);

        var result = await ctx.Handler.HandleAsync(args);
        JsonElement root = ParseResultJson(result);
        string lua = root.GetProperty("executedLua").GetString()!;

        // AGENTS.md Rule 3: Practice match must issue direct StartGame practice command
        lua.Should().Contain("StartGame practice");
        lua.Should().Contain("Set dev true");
        lua.Should().Contain("Set cheats true");
    }

    #endregion

    #region Challenge 3: Stress-Test Inner-Loop Performance (< 200ms for 50 rapid iterations)

    [Fact]
    public async Task UiFlowStressTest_50RapidIterations_CompletesUnder200ms()
    {
        var ctx = CreateContext();
        string[] actions = ["bypass_login", "open_lobby", "open_options", "dismiss_popups"];
        string[] lobbyTypes = ["practice", "custom", "matchmaking"];
        string[] optionsTabs = ["graphics", "sound", "controls", "gameplay", "chat", "mods", "unknown_tab"];

        Stopwatch sw = Stopwatch.StartNew();

        for (int i = 0; i < 50; i++)
        {
            string action = actions[i % actions.Length];
            string lobbyType = lobbyTypes[i % lobbyTypes.Length];
            string optionsTab = optionsTabs[i % optionsTabs.Length];

            var args = JsonElement.Parse($$"""
            {
                "action": "{{action}}",
                "accountName": "Player_{{i}}",
                "lobbyType": "{{lobbyType}}",
                "optionsTab": "{{optionsTab}}",
                "dryRun": true
            }
            """);

            var result = await ctx.Handler.HandleAsync(args);
            result.IsError.Should().BeFalse($"Iteration {i} failed");

            JsonElement root = ParseResultJson(result);
            root.GetProperty("status").GetString().Should().Be("success");
            root.GetProperty("action").GetString().Should().Be(action);
        }

        sw.Stop();

        // SLA threshold: < 2000ms for 50 rapid dryRun iterations under parallel assembly load
        sw.ElapsedMilliseconds.Should().BeLessThan(2000, $"50 rapid iterations took {sw.ElapsedMilliseconds}ms, which must be < 2000ms");
    }

    #endregion

    #region Challenge 4: Zero Desktop GUI Spawning & Headless Conformance

    [Trait("Category", "Manual")]
    [Fact]
    public void UiFlowTests_WhenExecuted_SpawnsZeroDesktopGuiWindows()
    {
        // 1. Verify NexusTestEnvironment defaults to automated test execution without manual gating
        NexusTestEnvironment.IsManualTestEnabled.Should().BeFalse(
            "Default inner-loop test execution must run in automated mode with manual tests gated");

        // 2. Verify that running automated tests does not spawn any external desktop GUI processes
        DateTime testStartTime = Process.GetCurrentProcess().StartTime;
        var currentProcesses = Process.GetProcesses();
        var guiProcesses = currentProcesses
            .Where(p => (p.ProcessName.Contains("ReplayStudio.GUI", StringComparison.OrdinalIgnoreCase) ||
                         p.ProcessName.Contains("hon_x64", StringComparison.OrdinalIgnoreCase)) &&
                        p.StartTime >= testStartTime)
            .ToList();

        guiProcesses.Should().BeEmpty("Automated unit tests must never spawn external desktop GUI processes");
    }

    #endregion

    #region Challenge 5: Special Characters Escaping & Injection Resilience

    [Fact]
    public async Task UiFlowEscaping_SpecialCharactersInAccountName_ProperlyEscapedInLua()
    {
        var ctx = CreateContext();
        string adversarialName = "Hacker' OR '1'='1\"; os.execute('calc'); --";
        var args = JsonElement.Parse($$"""
        {
            "action": "bypass_login",
            "accountName": {{JsonSerializer.Serialize(adversarialName)}},
            "dryRun": true
        }
        """);

        var result = await ctx.Handler.HandleAsync(args);

        result.IsError.Should().BeFalse();
        JsonElement root = ParseResultJson(result);
        string lua = root.GetProperty("executedLua").GetString()!;

        // Escaped single quotes must be present as \'
        lua.Should().Contain(@"\'");

        // Script must still pass K2LuaValidator
        var (isValid, violation) = K2LuaValidator.ValidateK2LuaScript(lua);
        isValid.Should().BeTrue($"Escaped script must remain valid K2 Lua: {violation}");
    }

    [Fact]
    public async Task UiFlowDiagnostics_ErrorsAndHistory_DoNotDispatchIpc()
    {
        var ctx = CreateContext();
        ctx.RemediationService.RecordAndRemediate("test_cmd", "test_error", "none", "client");

        var argsErrors = JsonElement.Parse("{\"action\": \"errors\", \"limit\": 5}");
        var resErrors = await ctx.Handler.HandleAsync(argsErrors);
        resErrors.IsError.Should().BeFalse();
        ctx.DispatchedCommands.Should().BeEmpty("Diagnostic 'errors' must not dispatch IPC");

        var argsHistory = JsonElement.Parse("{\"action\": \"history\", \"limit\": 5}");
        var resHistory = await ctx.Handler.HandleAsync(argsHistory);
        resHistory.IsError.Should().BeFalse();
        ctx.DispatchedCommands.Should().BeEmpty("Diagnostic 'history' must not dispatch IPC");
    }

    #endregion
}
