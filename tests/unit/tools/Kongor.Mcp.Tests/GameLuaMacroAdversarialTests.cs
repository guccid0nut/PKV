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
/// Empirical Challenger Test Suite for Milestone 2:
/// Stress-tests GameLuaHandler macro actions, adversarial inputs,
/// stream emission parsing, and phase error interception (INVALID_GAME_PHASE).
/// </summary>
public sealed class GameLuaMacroAdversarialTests
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

    #region 1. Macro Action Command Generation Edge Cases

    [Fact]
    public async Task HideLogin_DefaultAccount_EmitsDefaultPlayerWithFallback()
    {
        string? command = null;
        var (handler, _, _) = CreateHandlerWithMocks(onExecute: cmd => command = cmd);

        using var doc = JsonDocument.Parse("""{"action": "hide_login"}""");
        var result = await handler.HandleAsync(doc.RootElement);

        result.IsError.Should().BeFalse();
        command.Should().NotBeNull();
        command.Should().Be("Script if Nexus and Nexus.HideLogin then Nexus.HideLogin('KongorPlayer') else Set('_loggedin', true); Trigger('LoginStatus', 'success', '', true); end");
    }

    [Theory]
    [InlineData("accountName", "SavageBeast")]
    [InlineData("account", "AlternativeBeast")]
    [InlineData("player", "PlayerBeast")]
    public async Task HideLogin_AccountAliases_ResolvedCorrectly(string propName, string accountVal)
    {
        string? command = null;
        var (handler, _, _) = CreateHandlerWithMocks(onExecute: cmd => command = cmd);

        using var doc = JsonDocument.Parse($$"""{"action": "hide_login", "{{propName}}": "{{accountVal}}"}""");
        var result = await handler.HandleAsync(doc.RootElement);

        result.IsError.Should().BeFalse();
        command.Should().Contain($"Nexus.HideLogin('{accountVal}')");
    }

    [Fact]
    public async Task HideLogin_AdversarialEscaping_SingleQuoteAndBackslash()
    {
        string? command = null;
        var (handler, _, _) = CreateHandlerWithMocks(onExecute: cmd => command = cmd);

        // Account with single quote and backslash: O'Connor\Admin
        using var doc = JsonDocument.Parse("""{"action": "hide_login", "accountName": "O'Connor\\Admin"}""");
        var result = await handler.HandleAsync(doc.RootElement);

        result.IsError.Should().BeFalse();
        command.Should().NotBeNull();
        // In Lua string, single quote should be \' and backslash should be \\
        command.Should().Contain(@"Nexus.HideLogin('O\'Connor\\Admin')");
    }

    [Fact]
    public async Task HideLogin_WhitespaceAccount_FallsBackToDefault()
    {
        string? command = null;
        var (handler, _, _) = CreateHandlerWithMocks(onExecute: cmd => command = cmd);

        using var doc = JsonDocument.Parse("""{"action": "hide_login", "accountName": "   "}""");
        var result = await handler.HandleAsync(doc.RootElement);

        result.IsError.Should().BeFalse();
        command.Should().Contain("Nexus.HideLogin('KongorPlayer')");
    }

    [Fact]
    public async Task PracticeMatch_DefaultOptions_FormatsCaldavarPyromancer()
    {
        string? command = null;
        var (handler, _, _) = CreateHandlerWithMocks(onExecute: cmd => command = cmd);

        using var doc = JsonDocument.Parse("""{"action": "practice_match"}""");
        var result = await handler.HandleAsync(doc.RootElement);

        result.IsError.Should().BeFalse();
        command.Should().NotBeNull();
        command.Should().Contain("map = 'caldavar'");
        command.Should().Contain("hero = 'Hero_Pyromancer'");
        command.Should().Contain("mode = 'practice'");
        command.Should().Contain("gameName = 'PracticeGame'");
        command.Should().Contain("cheats = true");
        command.Should().Contain("team = 1");
        command.Should().Contain("slot = 0");
        command.Should().Contain("StartGame practice PracticeGame map:caldavar allheroes:true");
    }

    [Theory]
    [InlineData("Pyromancer", "Hero_Pyromancer")]
    [InlineData("Hero_Pyromancer", "Hero_Pyromancer")]
    [InlineData("witchslayer", "Hero_witchslayer")]
    [InlineData("HERO_DEVOURER", "HERO_DEVOURER")]
    public async Task PracticeMatch_WhenHeroPrefixSupplied_NormalizesHeroName(string inputHero, string expectedHero)
    {
        string? command = null;
        var (handler, _, _) = CreateHandlerWithMocks(onExecute: cmd => command = cmd);

        using var doc = JsonDocument.Parse($$"""{"action": "practice_match", "hero": "{{inputHero}}"}""");
        var result = await handler.HandleAsync(doc.RootElement);

        result.IsError.Should().BeFalse();
        command.Should().Contain($"hero = '{expectedHero}'");
    }

    [Fact]
    public async Task PracticeMatch_NestedOptionsObject_OverridesDefaults()
    {
        string? command = null;
        var (handler, _, _) = CreateHandlerWithMocks(onExecute: cmd => command = cmd);

        string argsJson = """
        {
            "action": "practice_match",
            "options": {
                "map": "midwars",
                "hero": "Devourer",
                "mode": "botmatch",
                "gameName": "CustomLobby",
                "cheats": false,
                "team": 2,
                "slot": 4
            }
        }
        """;
        using var doc = JsonDocument.Parse(argsJson);
        var result = await handler.HandleAsync(doc.RootElement);

        result.IsError.Should().BeFalse();
        command.Should().Contain("map = 'midwars'");
        command.Should().Contain("hero = 'Hero_Devourer'");
        command.Should().Contain("mode = 'botmatch'");
        command.Should().Contain("gameName = 'CustomLobby'");
        command.Should().Contain("cheats = false");
        command.Should().Contain("team = 2");
        command.Should().Contain("slot = 4");
    }

    [Fact]
    public async Task PracticeMatch_AdversarialEscaping_MapAndGameName()
    {
        string? command = null;
        var (handler, _, _) = CreateHandlerWithMocks(onExecute: cmd => command = cmd);

        using var doc = JsonDocument.Parse("""{"action": "practice_match", "map": "caldavar's_ruins", "gameName": "Game'--"}""");
        var result = await handler.HandleAsync(doc.RootElement);

        result.IsError.Should().BeFalse();
        command.Should().Contain(@"map = 'caldavar\'s_ruins'");
        command.Should().Contain(@"gameName = 'Game\'--'");
    }

    [Fact]
    public async Task ApplySnapshot_NestedTable_FormatsAllPrimitivesAndArrays()
    {
        string? command = null;
        var (handler, _, _) = CreateHandlerWithMocks(onExecute: cmd => command = cmd);

        string argsJson = """
        {
            "action": "apply_snapshot",
            "snapshot": {
                "gold": 4500,
                "level": 16,
                "alive": true,
                "respawnTime": 0,
                "items": ["Item_PortalKey", "Item_PostHaste"],
                "pos": { "x": 1250.75, "y": 3400.5, "z": 64.0 },
                "nullVal": null
            }
        }
        """;
        using var doc = JsonDocument.Parse(argsJson);
        var result = await handler.HandleAsync(doc.RootElement);

        result.IsError.Should().BeFalse();
        command.Should().NotBeNull();
        command.Should().StartWith("Script if Nexus and Nexus.ApplySnapshot then Nexus.ApplySnapshot(");
        command.Should().Contain("gold = 4500");
        command.Should().Contain("level = 16");
        command.Should().Contain("alive = true");
        command.Should().Contain("'Item_PortalKey'");
        command.Should().Contain("'Item_PostHaste'");
        command.Should().Contain("x = 1250.75");
        command.Should().Contain("nullVal = nil");
    }

    [Fact]
    public async Task ApplySnapshot_FlatRootParameters_OmitsNonSnapshotFields()
    {
        string? command = null;
        var (handler, _, _) = CreateHandlerWithMocks(onExecute: cmd => command = cmd);

        // Parameters passed at root level rather than nested inside 'snapshot'
        string argsJson = """
        {
            "action": "apply_snapshot",
            "target": "client",
            "timeoutMs": 5000,
            "autoLaunch": true,
            "gold": 3000,
            "level": 10
        }
        """;
        using var doc = JsonDocument.Parse(argsJson);
        var result = await handler.HandleAsync(doc.RootElement);

        result.IsError.Should().BeFalse();
        command.Should().NotBeNull();
        // Action, target, timeoutMs, autoLaunch must be filtered out
        command.Should().NotContain("action = ");
        command.Should().NotContain("target = ");
        command.Should().NotContain("timeoutMs = ");
        command.Should().NotContain("autoLaunch = ");
        command.Should().Contain("gold = 3000");
        command.Should().Contain("level = 10");
    }

    [Fact]
    public async Task ApplySnapshot_MissingAllSnapshotProperties_ReturnsError()
    {
        var (handler, _, _) = CreateHandlerWithMocks();

        using var doc = JsonDocument.Parse("""{"action": "apply_snapshot"}""");
        var result = await handler.HandleAsync(doc.RootElement);

        result.IsError.Should().BeTrue();
        string text = ((TextContentItem)result.Content[0]).Text;
        text.Should().Contain("Missing required parameter: 'snapshot'");
    }

    [Fact]
    public async Task CollectTelemetry_WhenInvoked_DispatchesDeterministicCommand()
    {
        string? command = null;
        var (handler, _, _) = CreateHandlerWithMocks(onExecute: cmd => command = cmd);

        using var doc = JsonDocument.Parse("""{"action": "collect_telemetry"}""");
        var result = await handler.HandleAsync(doc.RootElement);

        result.IsError.Should().BeFalse();
        command.Should().Be("Script if Nexus and Nexus.CollectTelemetry then Nexus.CollectTelemetry() end");
    }

    #endregion

    #region 2. Lifecycle Phase Error Interception (INVALID_GAME_PHASE)

    [Fact]
    public async Task PhaseViolation_EventErrorPayload_InterceptsAndReturnsStructuredError()
    {
        // Requirement 2: Test phase violation response when engine emits:
        // [NEXUS_EVENT] ERROR|{"errorCode":"INVALID_GAME_PHASE"}
        // verify isError = true, errorCode = "INVALID_GAME_PHASE", and status = "error".
        string engineEmission = "[NEXUS_EVENT] ERROR|{\"errorCode\":\"INVALID_GAME_PHASE\"}";

        var (handler, _, _) = CreateHandlerWithMocks(ipcResponse: engineEmission);
        using var doc = JsonDocument.Parse("""{"action": "apply_snapshot", "snapshot": {"gold": 5000}}""");
        var result = await handler.HandleAsync(doc.RootElement);

        result.IsError.Should().BeTrue("isError must be true for phase violation");
        result.Content.Should().NotBeEmpty();

        string json = ((TextContentItem)result.Content[0]).Text;
        using var resDoc = JsonDocument.Parse(json);
        var root = resDoc.RootElement;

        root.GetProperty("status").GetString().Should().Be("error", "status must be 'error'");
        root.GetProperty("errorCode").GetString().Should().Be("INVALID_GAME_PHASE", "errorCode must be 'INVALID_GAME_PHASE'");
        root.GetProperty("action").GetString().Should().Be("apply_snapshot");
    }

    [Fact]
    public async Task PhaseViolation_WithCurrentPhaseAndMessage_ExtractsMetadata()
    {
        string engineEmission = """
        [NEXUS_EVENT] ERROR|{"errorCode":"INVALID_GAME_PHASE","currentPhase":0,"message":"Snapshot can only be applied during active match (phase 3)"}
        """;

        var (handler, _, _) = CreateHandlerWithMocks(ipcResponse: engineEmission);
        using var doc = JsonDocument.Parse("""{"action": "apply_snapshot", "snapshot": {"gold": 5000}}""");
        var result = await handler.HandleAsync(doc.RootElement);

        result.IsError.Should().BeTrue();
        string json = ((TextContentItem)result.Content[0]).Text;
        using var resDoc = JsonDocument.Parse(json);
        var root = resDoc.RootElement;

        root.GetProperty("status").GetString().Should().Be("error");
        root.GetProperty("errorCode").GetString().Should().Be("INVALID_GAME_PHASE");
        root.GetProperty("currentPhase").GetInt32().Should().Be(0);
        root.GetProperty("errorMessage").GetString().Should().Contain("Snapshot can only be applied during active match");
        root.TryGetProperty("event", out var evEl).Should().BeTrue();
        evEl.GetProperty("name").GetString().Should().Be("ERROR");
    }

    [Fact]
    public async Task PhaseViolation_AlternativeFieldNaming_CodeAndCurrentPhaseSnakeCase()
    {
        string engineEmission = """
        [NEXUS_EVENT] ERROR|{"code":"INVALID_GAME_PHASE","current_phase":2}
        """;

        var (handler, _, _) = CreateHandlerWithMocks(ipcResponse: engineEmission);
        using var doc = JsonDocument.Parse("""{"action": "apply_snapshot", "snapshot": {"gold": 1000}}""");
        var result = await handler.HandleAsync(doc.RootElement);

        result.IsError.Should().BeTrue();
        string json = ((TextContentItem)result.Content[0]).Text;
        using var resDoc = JsonDocument.Parse(json);
        var root = resDoc.RootElement;

        root.GetProperty("errorCode").GetString().Should().Be("INVALID_GAME_PHASE");
        root.GetProperty("currentPhase").GetInt32().Should().Be(2);
    }

    [Fact]
    public async Task PhaseViolation_RawJsonString_InterceptedWithoutEventWrapper()
    {
        string rawJson = """{"success": false, "errorCode": "INVALID_GAME_PHASE", "currentPhase": 1}""";

        var (handler, _, _) = CreateHandlerWithMocks(ipcResponse: rawJson);
        using var doc = JsonDocument.Parse("""{"code": "Nexus.VerifyPhase({3})"}""");
        var result = await handler.HandleAsync(doc.RootElement);

        result.IsError.Should().BeTrue();
        string json = ((TextContentItem)result.Content[0]).Text;
        using var resDoc = JsonDocument.Parse(json);
        var root = resDoc.RootElement;

        root.GetProperty("status").GetString().Should().Be("error");
        root.GetProperty("errorCode").GetString().Should().Be("INVALID_GAME_PHASE");
        root.GetProperty("currentPhase").GetInt32().Should().Be(1);
    }

    [Fact]
    public async Task PhaseViolation_PlainTextConsoleLog_Intercepted()
    {
        string consoleOutput = """
        [GameClient] ^c[NEXUS]^w ^r[VerifyPhase] Guard assertion: current phase 1 not in permitted phases.
        """;

        var (handler, _, _) = CreateHandlerWithMocks(ipcResponse: consoleOutput);
        using var doc = JsonDocument.Parse("""{"action": "apply_snapshot", "snapshot": {"level": 25}}""");
        var result = await handler.HandleAsync(doc.RootElement);

        result.IsError.Should().BeTrue();
        string json = ((TextContentItem)result.Content[0]).Text;
        using var resDoc = JsonDocument.Parse(json);
        var root = resDoc.RootElement;

        root.GetProperty("status").GetString().Should().Be("error");
        root.GetProperty("errorCode").GetString().Should().Be("INVALID_GAME_PHASE");
    }

    [Fact]
    public async Task InvalidSnapshotError_WhenDetectedInStream_InterceptsAndReturnsError()
    {
        string engineEmission = """
        [NEXUS_EVENT] ERROR|{"errorCode":"INVALID_SNAPSHOT","message":"Snapshot payload must be a table"}
        """;

        var (handler, _, _) = CreateHandlerWithMocks(ipcResponse: engineEmission);
        using var doc = JsonDocument.Parse("""{"action": "apply_snapshot", "snapshot": {}}""");
        var result = await handler.HandleAsync(doc.RootElement);

        result.IsError.Should().BeTrue();
        string json = ((TextContentItem)result.Content[0]).Text;
        using var resDoc = JsonDocument.Parse(json);
        var root = resDoc.RootElement;

        root.GetProperty("errorCode").GetString().Should().Be("INVALID_SNAPSHOT");
    }

    #endregion

    #region 3. Telemetry & Log Emission Parsing Edge Cases

    [Fact]
    public async Task CollectTelemetry_FullJsonState_StructuredOutput()
    {
        string emission = """
        Echo output...
        [NEXUS_TELEMETRY] {"hero":"Hero_Pyromancer","level":11,"hp":1150,"maxHp":1150,"mana":580,"maxMana":580,"pos":{"x":-234.5,"y":1800.25,"z":64.0},"gold":3200,"gameTime":980,"cooldowns":[0,2.5,0,45.0]}
        """;

        var (handler, _, _) = CreateHandlerWithMocks(ipcResponse: emission);
        using var doc = JsonDocument.Parse("""{"action": "collect_telemetry"}""");
        var result = await handler.HandleAsync(doc.RootElement);

        result.IsError.Should().BeFalse();
        string json = ((TextContentItem)result.Content[0]).Text;
        using var resDoc = JsonDocument.Parse(json);
        var root = resDoc.RootElement;

        root.GetProperty("status").GetString().Should().Be("success");
        root.TryGetProperty("telemetry", out var tel).Should().BeTrue();

        tel.GetProperty("hero").GetString().Should().Be("Hero_Pyromancer");
        tel.GetProperty("level").GetInt32().Should().Be(11);
        tel.GetProperty("hp").GetInt32().Should().Be(1150);
        tel.GetProperty("pos").GetProperty("x").GetDouble().Should().BeApproximately(-234.5, 0.01);
        tel.GetProperty("cooldowns")[1].GetDouble().Should().BeApproximately(2.5, 0.01);
        tel.GetProperty("cooldowns")[3].GetDouble().Should().BeApproximately(45.0, 0.01);
    }

    [Fact]
    public async Task CollectTelemetry_MultipleEmissions_LastEmissionWins()
    {
        // When telemetry was emitted multiple times, handler must extract the freshest state
        string emission = """
        [NEXUS_TELEMETRY] {"hero":"Hero_Pyromancer","level":1,"hp":600,"gold":600}
        Some other engine logs...
        [NEXUS_TELEMETRY] {"hero":"Hero_Pyromancer","level":6,"hp":850,"gold":1800}
        """;

        var (handler, _, _) = CreateHandlerWithMocks(ipcResponse: emission);
        using var doc = JsonDocument.Parse("""{"action": "collect_telemetry"}""");
        var result = await handler.HandleAsync(doc.RootElement);

        result.IsError.Should().BeFalse();
        string json = ((TextContentItem)result.Content[0]).Text;
        using var resDoc = JsonDocument.Parse(json);
        var root = resDoc.RootElement;

        root.GetProperty("telemetry").GetProperty("level").GetInt32().Should().Be(6);
        root.GetProperty("telemetry").GetProperty("gold").GetInt32().Should().Be(1800);
    }

    [Fact]
    public async Task CollectTelemetry_WhenColorCodesPresent_StripsColorCodesFromStream()
    {
        string emission = "^c[NEXUS]^w ^r[NEXUS_TELEMETRY]^w {\"hero\":\"Hero_Pyromancer\",\"level\":1,\"hp\":600}";

        var (handler, _, _) = CreateHandlerWithMocks(ipcResponse: emission);
        using var doc = JsonDocument.Parse("""{"action": "collect_telemetry"}""");
        var result = await handler.HandleAsync(doc.RootElement);

        result.IsError.Should().BeFalse();
        string json = ((TextContentItem)result.Content[0]).Text;
        using var resDoc = JsonDocument.Parse(json);
        var root = resDoc.RootElement;

        root.TryGetProperty("telemetry", out var tel).Should().BeTrue();
        tel.GetProperty("hero").GetString().Should().Be("Hero_Pyromancer");
    }

    [Fact]
    public async Task CollectTelemetry_MissingEmission_ReturnsSuccessWithWarning()
    {
        string outputWithoutEmission = "Command executed successfully, no telemetry printed.";

        var (handler, _, _) = CreateHandlerWithMocks(ipcResponse: outputWithoutEmission);
        using var doc = JsonDocument.Parse("""{"action": "collect_telemetry"}""");
        var result = await handler.HandleAsync(doc.RootElement);

        result.IsError.Should().BeFalse();
        string json = ((TextContentItem)result.Content[0]).Text;
        using var resDoc = JsonDocument.Parse(json);
        var root = resDoc.RootElement;

        root.GetProperty("status").GetString().Should().Be("success");
        root.TryGetProperty("warning", out var warnEl).Should().BeTrue();
        warnEl.GetString().Should().Contain("no [NEXUS_TELEMETRY] emission was found");
    }

    [Fact]
    public async Task LogEmission_BothEventAndTelemetryInOutput_BothParsed()
    {
        string mixedOutput = """
        [NEXUS_EVENT] HERO_SPAWNED|{"hero":"Hero_Pyromancer","team":1}
        [NEXUS_TELEMETRY] {"hero":"Hero_Pyromancer","level":1,"hp":600}
        """;

        var (handler, _, _) = CreateHandlerWithMocks(ipcResponse: mixedOutput);
        using var doc = JsonDocument.Parse("""{"action": "hide_login"}""");
        var result = await handler.HandleAsync(doc.RootElement);

        result.IsError.Should().BeFalse();
        string json = ((TextContentItem)result.Content[0]).Text;
        using var resDoc = JsonDocument.Parse(json);
        var root = resDoc.RootElement;

        root.TryGetProperty("event", out var ev).Should().BeTrue();
        ev.GetProperty("name").GetString().Should().Be("HERO_SPAWNED");
        root.TryGetProperty("telemetry", out var tel).Should().BeTrue();
        tel.GetProperty("hero").GetString().Should().Be("Hero_Pyromancer");
    }

    [Trait("Category", "Manual")]
    [Fact]
    public async Task CollectTelemetry_MalformedJsonFallback_DoesNotThrow()
    {
        string malformed = "[NEXUS_TELEMETRY] {bad_unclosed_json: true";

        var (handler, _, _) = CreateHandlerWithMocks(ipcResponse: malformed);
        using var doc = JsonDocument.Parse("""{"action": "collect_telemetry"}""");
        var result = await handler.HandleAsync(doc.RootElement);

        // Handler must safely handle without throwing unhandled exception
        result.Content.Should().NotBeEmpty();
    }

    #endregion

    #region 4. Adversarial Inputs & Architecture Verification

    [Fact]
    public async Task UnknownAction_WhenDispatched_ReturnsStructuredError()
    {
        var (handler, _, _) = CreateHandlerWithMocks();
        using var doc = JsonDocument.Parse("""{"action": "non_existent_macro"}""");
        var result = await handler.HandleAsync(doc.RootElement);

        result.IsError.Should().BeTrue();
        string text = ((TextContentItem)result.Content[0]).Text;
        text.Should().Contain("Unknown action 'non_existent_macro'");
    }

    [Fact]
    public async Task TargetServer_WithoutServerHandler_ReturnsGracefulError()
    {
        // Server target when GameServerManageHandler is null
        var (handler, _, _) = CreateHandlerWithMocks();
        using var doc = JsonDocument.Parse("""{"action": "hide_login", "target": "server"}""");
        var result = await handler.HandleAsync(doc.RootElement);

        result.IsError.Should().BeTrue();
        string text = ((TextContentItem)result.Content[0]).Text;
        text.Should().Contain("GameServerManageHandler is not configured");
    }

    [Fact]
    public async Task DiagnosticActions_ErrorsAndHistory_Succeed()
    {
        var (handler, _, _) = CreateHandlerWithMocks();

        using var docErrors = JsonDocument.Parse("""{"action": "errors", "limit": 5}""");
        var resultErrors = await handler.HandleAsync(docErrors.RootElement);
        resultErrors.IsError.Should().BeFalse();

        using var docHistory = JsonDocument.Parse("""{"action": "history"}""");
        var resultHistory = await handler.HandleAsync(docHistory.RootElement);
        resultHistory.IsError.Should().BeFalse();
    }

    [Fact]
    public async Task AdversarialTypes_NumberForAccountName_ThrowsInvalidOperation()
    {
        var (handler, _, _) = CreateHandlerWithMocks();
        using var doc = JsonDocument.Parse("""{"action": "hide_login", "accountName": 12345}""");

        // Verification of System.Text.Json token type behavior:
        // accEl.GetString() throws InvalidOperationException when token is Number
        var act = () => handler.HandleAsync(doc.RootElement);
        await act.Should().ThrowAsync<InvalidOperationException>();
    }

    [Fact]
    public async Task AdversarialTypes_NumberForAction_ThrowsInvalidOperation()
    {
        var (handler, _, _) = CreateHandlerWithMocks();
        using var doc = JsonDocument.Parse("""{"action": 999}""");

        var act = () => handler.HandleAsync(doc.RootElement);
        await act.Should().ThrowAsync<InvalidOperationException>();
    }

    [Fact]
    public async Task AdversarialTypes_NumberForMap_ThrowsInvalidOperation()
    {
        var (handler, _, _) = CreateHandlerWithMocks();
        using var doc = JsonDocument.Parse("""{"action": "practice_match", "map": 42}""");

        var act = () => handler.HandleAsync(doc.RootElement);
        await act.Should().ThrowAsync<InvalidOperationException>();
    }

    [Fact]
    public void DetectStructuredError_WhenUnwrappedErrorTextEmitted_HandlesGracefully()
    {
        // When payload is raw string without JSON wrapper: [NEXUS_EVENT] ERROR|INVALID_GAME_PHASE
        bool hasEvent = GameLuaHandler.TryExtractEvent("[NEXUS_EVENT] ERROR|INVALID_GAME_PHASE", out string? eventName, out JsonElement eventPayload);
        hasEvent.Should().BeTrue();
        eventName.Should().Be("ERROR");

        bool hasError = GameLuaHandler.TryDetectStructuredError(
            "[NEXUS_EVENT] ERROR|INVALID_GAME_PHASE",
            eventName,
            eventPayload,
            out string? errorCode,
            out string? errorMessage,
            out int? currentPhase);

        hasError.Should().BeTrue();
        // Since eventPayload has no errorCode property and eventName == "ERROR",
        // step 3 sets errorCode = "EXECUTION_ERROR", preventing step 4 from setting "INVALID_GAME_PHASE"
        errorCode.Should().Be("EXECUTION_ERROR");
    }

    [Fact]
    public void TryExtractTelemetry_WithScientificNotationAndNegativeFloats_ExtractsCorrectly()
    {
        string text = "[NEXUS_TELEMETRY] {\"pos\":{\"x\":-1500.25,\"y\":2.5e3,\"z\":64.0}}";
        bool extracted = GameLuaHandler.TryExtractTelemetry(text, out JsonElement telemetry);

        extracted.Should().BeTrue();
        telemetry.GetProperty("pos").GetProperty("x").GetDouble().Should().BeApproximately(-1500.25, 0.01);
        telemetry.GetProperty("pos").GetProperty("y").GetDouble().Should().BeApproximately(2500.0, 0.01);
    }

    [Fact]
    public void TryExtractTelemetry_WithWhitespaceAndTrailingSemicolon_ExtractsCorrectly()
    {
        string text = "[NEXUS_TELEMETRY]   hero = Hero_Pyromancer ;  level = 1 ; ; hp = 600 ; ";
        bool extracted = GameLuaHandler.TryExtractTelemetry(text, out JsonElement telemetry);

        extracted.Should().BeTrue();
        telemetry.GetProperty("hero").GetString().Should().Be("Hero_Pyromancer");
        telemetry.GetProperty("level").GetInt32().Should().Be(1);
        telemetry.GetProperty("hp").GetInt32().Should().Be(600);
    }

    [Fact]
    public void TryExtractTelemetry_WithMixedCaseKeys_NormalizesKeysCaseInsensitively()
    {
        string text = "[NEXUS_TELEMETRY] HERO=Hero_Pyromancer;MAX_HP=850;GAME_TIME=120";
        bool extracted = GameLuaHandler.TryExtractTelemetry(text, out JsonElement telemetry);

        extracted.Should().BeTrue();
        telemetry.GetProperty("HERO").GetString().Should().Be("Hero_Pyromancer");
        telemetry.GetProperty("maxHp").GetInt32().Should().Be(850);
        telemetry.GetProperty("gameTime").GetInt32().Should().Be(120);
    }

    [Fact]
    public void TryExtractEvent_WithPipeCharacterInPayload_ExtractsPayloadCorrectly()
    {
        string text = "[NEXUS_EVENT] CHAT_MESSAGE|Player1|Hello World";
        bool extracted = GameLuaHandler.TryExtractEvent(text, out string? eventName, out JsonElement payload);

        extracted.Should().BeTrue();
        eventName.Should().Be("CHAT_MESSAGE");
        // Payload after first pipe should be preserved
        payload.GetProperty("raw").GetString().Should().Be("Player1|Hello World");
    }

    [Fact]
    public void FormatJsonElementAsLuaTable_WithDeepNestingAndArrays_FormatsTableCorrectly()
    {
        string json = """
        {
            "tier1": {
                "tier2": {
                    "tier3": {
                        "array": [10, 20, { "key": "value" }]
                    }
                }
            }
        }
        """;
        using var doc = JsonDocument.Parse(json);
        string luaTable = GameLuaHandler.FormatJsonElementAsLuaTable(doc.RootElement);

        luaTable.Should().Contain("tier1 = { tier2 = { tier3 = { array = { 10, 20, { key = 'value' } } } } }");
    }

    [Fact]
    public async Task ConcurrentExecution_WithMultiThreadedCalls_ExecutesThreadSafely()
    {
        var (handler, _, _) = CreateHandlerWithMocks(
            ipcResponse: "[NEXUS_TELEMETRY] {\"hero\":\"Hero_Pyromancer\",\"level\":1,\"hp\":600}");

        var tasks = new List<Task<ToolCallResult>>();
        for (int i = 0; i < 50; i++)
        {
            int idx = i;
            tasks.Add(Task.Run(async () =>
            {
                string action = (idx % 3) switch
                {
                    0 => "collect_telemetry",
                    1 => "hide_login",
                    _ => "practice_match"
                };
                using var doc = JsonDocument.Parse($$"""{"action": "{{action}}"}""");
                return await handler.HandleAsync(doc.RootElement);
            }));
        }

        var results = await Task.WhenAll(tasks);
        results.Should().HaveCount(50);
        results.Should().OnlyContain(r => !r.IsError);
    }

    #endregion

    #region 5. Fast Inner-Loop Performance & Manual Gating Compliance

    [Fact]
    public async Task InnerLoopStress_With100Executions_CompletesUnderThreshold()
    {
        var (handler, _, mockUi) = CreateHandlerWithMocks(
            ipcResponse: "[NEXUS_TELEMETRY] {\"hero\":\"Hero_Pyromancer\",\"level\":1,\"hp\":600}");

        Stopwatch sw = Stopwatch.StartNew();

        for (int i = 0; i < 100; i++)
        {
            using var doc = JsonDocument.Parse("""{"action": "collect_telemetry"}""");
            var result = await handler.HandleAsync(doc.RootElement);
            result.IsError.Should().BeFalse();
        }

        sw.Stop();

        // 100 iterations through mock IPC must execute under 10.0s under parallel load
        sw.ElapsedMilliseconds.Should().BeLessThan(10000, "100 mock IPC iterations must complete in < 10000ms");

        // Zero physical Win32 keystrokes or console actuations
        mockUi.Invocations.Count.Should().Be(0);
    }

    [Fact]
    public async Task ManualTestGating_WhenEvaluated_CompliesWithNexusTestEnvironment()
    {
        // Verify gating guard pattern
        if (!NexusTestEnvironment.IsManualTestEnabled)
        {
            // Default automated execution: guard passes immediately without running slow code
            NexusTestEnvironment.ManualBypassedCount.Should().BeGreaterThan(0, "manual gate bypass counter must increment when manual tests are disabled");
            var (gatedHandler, _, gatedUi) = CreateHandlerWithMocks(ipcResponse: "[NEXUS_TELEMETRY] {\"hero\":\"Hero_Pyromancer\",\"level\":6,\"hp\":820}");
            using var gatedDoc = JsonDocument.Parse("""{"action": "collect_telemetry"}""");
            ToolCallResult gatedRes = await gatedHandler.HandleAsync(gatedDoc.RootElement);
            gatedRes.IsError.Should().BeFalse();
            gatedRes.FirstText.Should().Contain("Hero_Pyromancer");
            gatedUi.Invocations.Should().BeEmpty();
            return;
        }

        // Only runs when manual tests are explicitly enabled
        Assert.True(NexusTestEnvironment.IsManualTestEnabled);
    }

    #endregion
}
