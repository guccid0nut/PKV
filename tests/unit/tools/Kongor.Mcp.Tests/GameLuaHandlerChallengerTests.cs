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
/// Empirical Challenger Stress Test Suite for Milestone 2 Phase 2 Modernization.
/// Adversarially probes telemetry deserialization, key=value fallbacks, numeric coercions,
/// alias normalization, interleaved stream noise, event extraction, and lifecycle guard errors.
/// </summary>
public sealed class GameLuaHandlerChallengerTests
{
    private static (GameLuaHandler Handler, Mock<IGameIpcBridge> MockIpc, Mock<IUiControlService> MockUi) CreateHandler(
        string? ipcResponse = null)
    {
        var mockIpc = new Mock<IGameIpcBridge>();
        mockIpc.Setup(i => i.ExecuteCommandAsync(It.IsAny<string>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(ipcResponse ?? "OK");
        mockIpc.SetupGet(i => i.IsConnected).Returns(true);

        var mockUi = new Mock<IUiControlService>();
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

        return (handler, mockIpc, mockUi);
    }

    #region Category 1: Compact JSON Telemetry Deserialization

    [Fact]
    public void TryExtractTelemetry_CompactJson_AllStandardFields_ParsesCorrectly()
    {
        string payload = """
        [NEXUS_TELEMETRY] {"hero":"Hero_Devourer","level":16,"hp":2450,"maxHp":2450,"mana":820,"maxMana":820,"pos":{"x":1250.25,"y":-3420.5,"z":64.0},"gold":3850,"gameTime":1845,"cooldowns":[0,12.5,0,75.0]}
        """;

        bool success = GameLuaHandler.TryExtractTelemetry(payload, out JsonElement el);

        success.Should().BeTrue();
        el.ValueKind.Should().Be(JsonValueKind.Object);
        el.GetProperty("hero").GetString().Should().Be("Hero_Devourer");
        el.GetProperty("level").GetInt32().Should().Be(16);
        el.GetProperty("hp").GetInt32().Should().Be(2450);
        el.GetProperty("maxHp").GetInt32().Should().Be(2450);
        el.GetProperty("mana").GetInt32().Should().Be(820);
        el.GetProperty("maxMana").GetInt32().Should().Be(820);
        el.GetProperty("gold").GetInt32().Should().Be(3850);
        el.GetProperty("gameTime").GetInt32().Should().Be(1845);

        el.GetProperty("pos").GetProperty("x").GetDouble().Should().BeApproximately(1250.25, 0.01);
        el.GetProperty("pos").GetProperty("y").GetDouble().Should().BeApproximately(-3420.5, 0.01);
        el.GetProperty("pos").GetProperty("z").GetDouble().Should().BeApproximately(64.0, 0.01);

        el.GetProperty("cooldowns")[0].GetDouble().Should().Be(0);
        el.GetProperty("cooldowns")[1].GetDouble().Should().BeApproximately(12.5, 0.01);
        el.GetProperty("cooldowns")[3].GetDouble().Should().BeApproximately(75.0, 0.01);
    }

    [Fact]
    public void TryExtractTelemetry_CompactJson_ZeroWhitespace_ParsesCorrectly()
    {
        string payload = "[NEXUS_TELEMETRY]{\"hero\":\"Hero_FlintBeastwood\",\"level\":25,\"hp\":1800,\"gold\":99999}";

        bool success = GameLuaHandler.TryExtractTelemetry(payload, out JsonElement el);

        success.Should().BeTrue();
        el.GetProperty("hero").GetString().Should().Be("Hero_FlintBeastwood");
        el.GetProperty("level").GetInt32().Should().Be(25);
        el.GetProperty("hp").GetInt32().Should().Be(1800);
        el.GetProperty("gold").GetInt32().Should().Be(99999);
    }

    [Fact]
    public void TryExtractTelemetry_CompactJson_EmptyObject_ReturnsValidEmptyElement()
    {
        string payload = "[NEXUS_TELEMETRY] {}";

        bool success = GameLuaHandler.TryExtractTelemetry(payload, out JsonElement el);

        success.Should().BeTrue();
        el.ValueKind.Should().Be(JsonValueKind.Object);
        el.EnumerateObject().Count().Should().Be(0);
    }

    [Fact]
    public void TryExtractTelemetry_CompactJson_EscapedCharactersInStrings_ParsesSafely()
    {
        string payload = "[NEXUS_TELEMETRY] {\"hero\":\"Hero_Valkyrie \\\"The Frost Queen\\\"\",\"status\":\"active\\/alive\"}";

        bool success = GameLuaHandler.TryExtractTelemetry(payload, out JsonElement el);

        success.Should().BeTrue();
        el.GetProperty("hero").GetString().Should().Be("Hero_Valkyrie \"The Frost Queen\"");
        el.GetProperty("status").GetString().Should().Be("active/alive");
    }

    #endregion

    #region Category 2: Semicolon Key=Value Fallback Telemetry

    [Fact]
    public void TryExtractTelemetry_KeyValueFallback_StandardTokens_ParsesAllFields()
    {
        string payload = "[NEXUS_TELEMETRY] hero=Hero_Witchslayer;level=11;hp=1120;max_hp=1120;mana=500;max_mana=500;gold=2450;game_time=920";

        bool success = GameLuaHandler.TryExtractTelemetry(payload, out JsonElement el);

        success.Should().BeTrue();
        el.GetProperty("hero").GetString().Should().Be("Hero_Witchslayer");
        el.GetProperty("level").GetInt32().Should().Be(11);
        el.GetProperty("hp").GetInt32().Should().Be(1120);
        el.GetProperty("mana").GetInt32().Should().Be(500);
        el.GetProperty("gold").GetInt32().Should().Be(2450);
        el.GetProperty("game_time").GetInt32().Should().Be(920);
    }

    [Fact]
    public void TryExtractTelemetry_KeyValueFallback_PaddedWhitespaceAroundTokens_ParsesCleanly()
    {
        string payload = "[NEXUS_TELEMETRY]   hero = Hero_Legionnaire  ;   level = 8 ;   hp = 1350  ;  gold = 600   ";

        bool success = GameLuaHandler.TryExtractTelemetry(payload, out JsonElement el);

        success.Should().BeTrue();
        el.GetProperty("hero").GetString().Should().Be("Hero_Legionnaire");
        el.GetProperty("level").GetInt32().Should().Be(8);
        el.GetProperty("hp").GetInt32().Should().Be(1350);
        el.GetProperty("gold").GetInt32().Should().Be(600);
    }

    [Fact]
    public void TryExtractTelemetry_KeyValueFallback_MultipleAndTrailingSemicolons_HandlesGracefully()
    {
        string payload = "[NEXUS_TELEMETRY] ;;hero=Hero_Behemoth;;;level=4;;gold=300;;;";

        bool success = GameLuaHandler.TryExtractTelemetry(payload, out JsonElement el);

        success.Should().BeTrue();
        el.GetProperty("hero").GetString().Should().Be("Hero_Behemoth");
        el.GetProperty("level").GetInt32().Should().Be(4);
        el.GetProperty("gold").GetInt32().Should().Be(300);
    }

    [Fact]
    public void TryExtractTelemetry_KeyValueFallback_EmptyValues_DoesNotCrash()
    {
        string payload = "[NEXUS_TELEMETRY] hero=Hero_Magmus;level=;gold=100;empty_prop=";

        bool success = GameLuaHandler.TryExtractTelemetry(payload, out JsonElement el);

        success.Should().BeTrue();
        el.GetProperty("hero").GetString().Should().Be("Hero_Magmus");
        el.GetProperty("level").GetString().Should().BeEmpty();
        el.GetProperty("gold").GetInt32().Should().Be(100);
        el.GetProperty("empty_prop").GetString().Should().BeEmpty();
    }

    [Fact]
    public void TryExtractTelemetry_KeyValueFallback_TokensWithoutEquals_SkipsGracefully()
    {
        string payload = "[NEXUS_TELEMETRY] hero=Hero_Magmus;invalidTokenWithoutEqual;level=10";

        bool success = GameLuaHandler.TryExtractTelemetry(payload, out JsonElement el);

        success.Should().BeTrue();
        el.GetProperty("hero").GetString().Should().Be("Hero_Magmus");
        el.GetProperty("level").GetInt32().Should().Be(10);
        el.TryGetProperty("invalidTokenWithoutEqual", out _).Should().BeFalse();
    }

    #endregion

    #region Category 3: Numeric Type Coercions in Key=Value Payloads

    [Fact]
    public void TryExtractTelemetry_NumericCoercions_IntegerWithinInt32_CoercesToInt()
    {
        string payload = "[NEXUS_TELEMETRY] hp=1500;mana=600;level=15;gold=4500";

        bool success = GameLuaHandler.TryExtractTelemetry(payload, out JsonElement el);

        success.Should().BeTrue();
        el.GetProperty("hp").GetInt32().Should().Be(1500);
        el.GetProperty("mana").GetInt32().Should().Be(600);
        el.GetProperty("level").GetInt32().Should().Be(15);
        el.GetProperty("gold").GetInt32().Should().Be(4500);
    }

    [Fact]
    public void TryExtractTelemetry_NumericCoercions_NegativeValues_CoercesToNegativeInt()
    {
        string payload = "[NEXUS_TELEMETRY] gold=-250;pos_x=-1500;delta=-42";

        bool success = GameLuaHandler.TryExtractTelemetry(payload, out JsonElement el);

        success.Should().BeTrue();
        el.GetProperty("gold").GetInt32().Should().Be(-250);
        el.GetProperty("pos_x").GetInt32().Should().Be(-1500);
        el.GetProperty("delta").GetInt32().Should().Be(-42);
    }

    [Fact]
    public void TryExtractTelemetry_NumericCoercions_ValuesExceedingInt32_CoercesToLong()
    {
        // 5,000,000,000 exceeds Int32.MaxValue (2,147,483,647)
        string payload = "[NEXUS_TELEMETRY] gold=5000000000;match_guid=987654321012345";

        bool success = GameLuaHandler.TryExtractTelemetry(payload, out JsonElement el);

        success.Should().BeTrue();
        el.GetProperty("gold").GetInt64().Should().Be(5000000000L);
        el.GetProperty("match_guid").GetInt64().Should().Be(987654321012345L);
    }

    [Fact]
    public void TryExtractTelemetry_NumericCoercions_FloatingPointDecimals_CoercesToDouble()
    {
        string payload = "[NEXUS_TELEMETRY] hp=1250.75;mana=420.5;game_time=98.25;cooldown=4.5";

        bool success = GameLuaHandler.TryExtractTelemetry(payload, out JsonElement el);

        success.Should().BeTrue();
        el.GetProperty("hp").GetDouble().Should().BeApproximately(1250.75, 0.001);
        el.GetProperty("mana").GetDouble().Should().BeApproximately(420.5, 0.001);
        el.GetProperty("game_time").GetDouble().Should().BeApproximately(98.25, 0.001);
        el.GetProperty("cooldown").GetDouble().Should().BeApproximately(4.5, 0.001);
    }

    [Fact]
    public void TryExtractTelemetry_NumericCoercions_ScientificNotation_CoercesToDouble()
    {
        string payload = "[NEXUS_TELEMETRY] game_time=1.5e3;distance=2.5e-2";

        bool success = GameLuaHandler.TryExtractTelemetry(payload, out JsonElement el);

        success.Should().BeTrue();
        el.GetProperty("game_time").GetDouble().Should().BeApproximately(1500.0, 0.01);
        el.GetProperty("distance").GetDouble().Should().BeApproximately(0.025, 0.001);
    }

    [Fact]
    public void TryExtractTelemetry_NumericCoercions_Booleans_CoercesToTypedBoolean()
    {
        string payload = "[NEXUS_TELEMETRY] alive=true;stunned=false;dead=False;invulnerable=True";

        bool success = GameLuaHandler.TryExtractTelemetry(payload, out JsonElement el);

        success.Should().BeTrue();
        el.GetProperty("alive").GetBoolean().Should().BeTrue();
        el.GetProperty("stunned").GetBoolean().Should().BeFalse();
        el.GetProperty("dead").GetBoolean().Should().BeFalse();
        el.GetProperty("invulnerable").GetBoolean().Should().BeTrue();
    }

    [Fact]
    public void TryExtractTelemetry_NumericCoercions_AlphaString_FallsBackToString()
    {
        string payload = "[NEXUS_TELEMETRY] hero=Hero_Soulstealer;state=channeling;target=Enemy_01";

        bool success = GameLuaHandler.TryExtractTelemetry(payload, out JsonElement el);

        success.Should().BeTrue();
        el.GetProperty("hero").GetString().Should().Be("Hero_Soulstealer");
        el.GetProperty("state").GetString().Should().Be("channeling");
        el.GetProperty("target").GetString().Should().Be("Enemy_01");
    }

    #endregion

    #region Category 4: Alias Normalization

    [Fact]
    public void TryExtractTelemetry_AliasNormalization_MaxHp_NormalizesToMaxHp()
    {
        string payload = "[NEXUS_TELEMETRY] hero=Hero_Pyromancer;hp=750;max_hp=850";

        bool success = GameLuaHandler.TryExtractTelemetry(payload, out JsonElement el);

        success.Should().BeTrue();
        el.GetProperty("max_hp").GetInt32().Should().Be(850);
        el.GetProperty("maxHp").GetInt32().Should().Be(850);
    }

    [Fact]
    public void TryExtractTelemetry_AliasNormalization_MaxMana_NormalizesToMaxMana()
    {
        string payload = "[NEXUS_TELEMETRY] hero=Hero_Pyromancer;mana=350;max_mana=500";

        bool success = GameLuaHandler.TryExtractTelemetry(payload, out JsonElement el);

        success.Should().BeTrue();
        el.GetProperty("max_mana").GetInt32().Should().Be(500);
        el.GetProperty("maxMana").GetInt32().Should().Be(500);
    }

    [Fact]
    public void TryExtractTelemetry_AliasNormalization_GameTime_NormalizesToGameTime()
    {
        string payload = "[NEXUS_TELEMETRY] hero=Hero_Pyromancer;game_time=1450";

        bool success = GameLuaHandler.TryExtractTelemetry(payload, out JsonElement el);

        success.Should().BeTrue();
        el.GetProperty("game_time").GetInt32().Should().Be(1450);
        el.GetProperty("gameTime").GetInt32().Should().Be(1450);
    }

    [Fact]
    public void TryExtractTelemetry_AliasNormalization_CaseInsensitiveAliases_NormalizesCleanly()
    {
        string payload = "[NEXUS_TELEMETRY] MAX_HP=1950;MAX_MANA=850;GAME_TIME=720";

        bool success = GameLuaHandler.TryExtractTelemetry(payload, out JsonElement el);

        success.Should().BeTrue();
        el.GetProperty("maxHp").GetInt32().Should().Be(1950);
        el.GetProperty("maxMana").GetInt32().Should().Be(850);
        el.GetProperty("gameTime").GetInt32().Should().Be(720);
    }

    [Fact]
    public void TryExtractTelemetry_AliasNormalization_PreserveExistingCamelCase_DoesNotOverwrite()
    {
        // When both maxHp and max_hp are supplied, existing maxHp must be preserved
        string payload = "[NEXUS_TELEMETRY] maxHp=2000;max_hp=1500";

        bool success = GameLuaHandler.TryExtractTelemetry(payload, out JsonElement el);

        success.Should().BeTrue();
        el.GetProperty("maxHp").GetInt32().Should().Be(2000);
    }

    #endregion

    #region Category 5: Malformed, Truncated, or Interleaved Console Logs

    [Fact]
    public void TryExtractTelemetry_Malformed_UnclosedJson_DoesNotThrowAndHandlesGracefully()
    {
        string payload = "[NEXUS_TELEMETRY] {\"hero\":\"Hero_Pyromancer\",\"hp\":500";

        bool success = GameLuaHandler.TryExtractTelemetry(payload, out _);

        // Does not throw unhandled exception
        success.Should().BeFalse();
    }

    [Fact]
    public void TryExtractTelemetry_Malformed_SyntaxErrorInJson_FallsBackSafely()
    {
        string payload = "[NEXUS_TELEMETRY] {hero: invalid_json, level: 10}";

        bool success = GameLuaHandler.TryExtractTelemetry(payload, out _);

        // Does not throw unhandled exception
        success.Should().BeFalse();
    }

    [Fact]
    public void TryExtractTelemetry_Interleaved_MultipleEmissions_ExtractsFreshestLastEmission()
    {
        string stream = """
        [Engine] Client loaded
        [NEXUS_TELEMETRY] {"hero":"Hero_Pyromancer","level":1,"hp":600}
        [Engine] Hero Pyromancer leveled up to 2!
        [NEXUS_TELEMETRY] {"hero":"Hero_Pyromancer","level":2,"hp":720}
        [Engine] Host tick 124
        """;

        bool success = GameLuaHandler.TryExtractTelemetry(stream, out JsonElement el);

        success.Should().BeTrue();
        el.GetProperty("level").GetInt32().Should().Be(2);
        el.GetProperty("hp").GetInt32().Should().Be(720);
    }

    [Fact]
    public void TryExtractTelemetry_Interleaved_TrailingEngineLogs_StripsTrailingLinesCleanly()
    {
        string stream = "[NEXUS_TELEMETRY] {\"hero\":\"Hero_Pyromancer\",\"level\":5,\"hp\":950}\r\n[Engine] Ping: 32ms\r\n[Engine] FPS: 120";

        bool success = GameLuaHandler.TryExtractTelemetry(stream, out JsonElement el);

        success.Should().BeTrue();
        el.GetProperty("hero").GetString().Should().Be("Hero_Pyromancer");
        el.GetProperty("level").GetInt32().Should().Be(5);
        el.GetProperty("hp").GetInt32().Should().Be(950);
    }

    [Fact]
    public void TryExtractTelemetry_Interleaved_K2ColorCodes_StripsColorCodesAndExtractsPayload()
    {
        string stream = "^c[NEXUS_TELEMETRY]^w ^yhero=Hero_Devourer;level=10;hp=2000^w";

        bool success = GameLuaHandler.TryExtractTelemetry(stream, out JsonElement el);

        success.Should().BeTrue();
        el.GetProperty("hero").GetString().Should().Be("Hero_Devourer");
        el.GetProperty("level").GetInt32().Should().Be(10);
        el.GetProperty("hp").GetInt32().Should().Be(2000);
    }

    [Fact]
    public void TryExtractTelemetry_Prefix_CaseInsensitive_ExtractsSuccessfully()
    {
        string stream = "[nexus_telemetry] hero=Hero_Witchslayer;hp=550";

        bool success = GameLuaHandler.TryExtractTelemetry(stream, out JsonElement el);

        success.Should().BeTrue();
        el.GetProperty("hero").GetString().Should().Be("Hero_Witchslayer");
        el.GetProperty("hp").GetInt32().Should().Be(550);
    }

    [Fact]
    public void TryExtractTelemetry_MissingPrefix_ReturnsFalse()
    {
        string stream = "Hero_Pyromancer level 5 hp 800 mana 400";

        bool success = GameLuaHandler.TryExtractTelemetry(stream, out _);

        success.Should().BeFalse();
    }

    [Fact]
    public void TryExtractTelemetry_EmptyPayloadAfterPrefix_ReturnsFalse()
    {
        string stream = "[NEXUS_TELEMETRY]   \r\n";

        bool success = GameLuaHandler.TryExtractTelemetry(stream, out _);

        success.Should().BeFalse();
    }

    #endregion

    #region Category 6: Event Extraction Stress Testing

    [Fact]
    public void TryExtractEvent_JsonPayload_ExtractsNameAndTypedPayload()
    {
        string stream = "[NEXUS_EVENT] LOGIN_SUCCESS|{\"player\":\"guccidonut\",\"loggedIn\":true,\"account_id\":12345}";

        bool success = GameLuaHandler.TryExtractEvent(stream, out string? eventName, out JsonElement payload);

        success.Should().BeTrue();
        eventName.Should().Be("LOGIN_SUCCESS");
        payload.GetProperty("player").GetString().Should().Be("guccidonut");
        payload.GetProperty("loggedIn").GetBoolean().Should().BeTrue();
        payload.GetProperty("account_id").GetInt32().Should().Be(12345);
    }

    [Fact]
    public void TryExtractEvent_KeyValuePayload_ExtractsNameAndConvertsKeyValue()
    {
        string stream = "[NEXUS_EVENT] HERO_KILLED|killer=Hero_Pyromancer;victim=Hero_Witchslayer;bounty=350;first_blood=true";

        bool success = GameLuaHandler.TryExtractEvent(stream, out string? eventName, out JsonElement payload);

        success.Should().BeTrue();
        eventName.Should().Be("HERO_KILLED");
        payload.GetProperty("killer").GetString().Should().Be("Hero_Pyromancer");
        payload.GetProperty("victim").GetString().Should().Be("Hero_Witchslayer");
        payload.GetProperty("bounty").GetInt32().Should().Be(350);
        payload.GetProperty("first_blood").GetBoolean().Should().BeTrue();
    }

    [Fact]
    public void TryExtractEvent_RawStringPayload_WrapsInRawObject()
    {
        string stream = "[NEXUS_EVENT] MAP_LOADED|caldavar";

        bool success = GameLuaHandler.TryExtractEvent(stream, out string? eventName, out JsonElement payload);

        success.Should().BeTrue();
        eventName.Should().Be("MAP_LOADED");
        payload.GetProperty("raw").GetString().Should().Be("caldavar");
    }

    [Fact]
    public void TryExtractEvent_PipesInsideJsonPayload_PreservesJsonStructure()
    {
        string stream = "[NEXUS_EVENT] CHAT_MESSAGE|{\"sender\":\"guccidonut\",\"text\":\"gg | well played\"}";

        bool success = GameLuaHandler.TryExtractEvent(stream, out string? eventName, out JsonElement payload);

        success.Should().BeTrue();
        eventName.Should().Be("CHAT_MESSAGE");
        payload.GetProperty("sender").GetString().Should().Be("guccidonut");
        payload.GetProperty("text").GetString().Should().Be("gg | well played");
    }

    [Fact]
    public void TryExtractEvent_MultipleEventsInStream_ExtractsLastEvent()
    {
        string stream = """
        [NEXUS_EVENT] PRACTICE_MATCH_START|{"map":"caldavar"}
        [Engine] Game loading...
        [NEXUS_EVENT] PRACTICE_MATCH_HERO_SELECTED|{"hero":"Hero_Pyromancer","team":1}
        """;

        bool success = GameLuaHandler.TryExtractEvent(stream, out string? eventName, out JsonElement payload);

        success.Should().BeTrue();
        eventName.Should().Be("PRACTICE_MATCH_HERO_SELECTED");
        payload.GetProperty("hero").GetString().Should().Be("Hero_Pyromancer");
        payload.GetProperty("team").GetInt32().Should().Be(1);
    }

    [Fact]
    public void TryExtractEvent_ColorCodesInStream_StripsAndExtractsEvent()
    {
        string stream = "^g[NEXUS_EVENT]^w LEVEL_UP|{\"hero\":\"Hero_Pyromancer\",\"level\":6}";

        bool success = GameLuaHandler.TryExtractEvent(stream, out string? eventName, out JsonElement payload);

        success.Should().BeTrue();
        eventName.Should().Be("LEVEL_UP");
        payload.GetProperty("hero").GetString().Should().Be("Hero_Pyromancer");
        payload.GetProperty("level").GetInt32().Should().Be(6);
    }

    [Fact]
    public void TryExtractEvent_MissingPipe_ReturnsFalse()
    {
        string stream = "[NEXUS_EVENT] INVALID_EVENT_WITHOUT_PIPE";

        bool success = GameLuaHandler.TryExtractEvent(stream, out _, out _);

        success.Should().BeFalse();
    }

    #endregion

    #region Category 7: Structured Error Handling & Lifecycle Phase Guards

    [Fact]
    public void TryDetectStructuredError_InvalidGamePhaseEvent_ExtractsStructuredFields()
    {
        string eventPayloadJson = "{\"errorCode\":\"INVALID_GAME_PHASE\",\"currentPhase\":1,\"message\":\"Snapshot can only be applied during active match (phase 3)\"}";
        using var doc = JsonDocument.Parse(eventPayloadJson);

        bool detected = GameLuaHandler.TryDetectStructuredError(
            text: null,
            eventName: "ERROR",
            eventPayload: doc.RootElement,
            out string? errorCode,
            out string? errorMessage,
            out int? currentPhase);

        detected.Should().BeTrue();
        errorCode.Should().Be("INVALID_GAME_PHASE");
        errorMessage.Should().Be("Snapshot can only be applied during active match (phase 3)");
        currentPhase.Should().Be(1);
    }

    [Fact]
    public void TryDetectStructuredError_RawGuardAssertionText_ExtractsInvalidGamePhase()
    {
        string rawLog = "[NEXUS] ^r[VerifyPhase] Guard assertion: current phase 0 not in permitted phases.";

        bool detected = GameLuaHandler.TryDetectStructuredError(
            text: rawLog,
            eventName: null,
            eventPayload: default,
            out string? errorCode,
            out string? errorMessage,
            out _);

        detected.Should().BeTrue();
        errorCode.Should().Be("INVALID_GAME_PHASE");
        errorMessage.Should().Contain("Current game phase is not permitted");
    }

    [Fact]
    public void TryDetectStructuredError_InvalidSnapshotStructure_ExtractsInvalidSnapshotCode()
    {
        string eventPayloadJson = "{\"errorCode\":\"INVALID_SNAPSHOT\",\"message\":\"Snapshot payload must be a table\"}";
        using var doc = JsonDocument.Parse(eventPayloadJson);

        bool detected = GameLuaHandler.TryDetectStructuredError(
            text: null,
            eventName: "ERROR",
            eventPayload: doc.RootElement,
            out string? errorCode,
            out string? errorMessage,
            out _);

        detected.Should().BeTrue();
        errorCode.Should().Be("INVALID_SNAPSHOT");
        errorMessage.Should().Be("Snapshot payload must be a table");
    }

    [Fact]
    public void TryDetectStructuredError_ErrorEventWithoutCode_FallsBackToExecutionError()
    {
        using var doc = JsonDocument.Parse("{\"raw\":\"native engine exception\"}");

        bool detected = GameLuaHandler.TryDetectStructuredError(
            text: null,
            eventName: "ERROR",
            eventPayload: doc.RootElement,
            out string? errorCode,
            out _,
            out _);

        detected.Should().BeTrue();
        errorCode.Should().Be("EXECUTION_ERROR");
    }

    #endregion

    #region Category 8: End-to-End GameLuaHandler.HandleAsync Stress Testing

    [Fact]
    public async Task HandleAsync_CollectTelemetry_WithKeyValuePayload_ReturnsTypedTelemetryObject()
    {
        string ipcOutput = "[NEXUS_TELEMETRY] hero=Hero_Witchslayer;level=16;hp=1350;max_hp=1350;mana=620;max_mana=620;gold=3100;game_time=1250";
        var (handler, _, _) = CreateHandler(ipcResponse: ipcOutput);

        using JsonDocument req = JsonDocument.Parse("{\"action\": \"collect_telemetry\"}");
        ToolCallResult result = await handler.HandleAsync(req.RootElement);

        result.IsError.Should().BeFalse();
        string jsonText = ((TextContentItem)result.Content[0]).Text;
        using JsonDocument resDoc = JsonDocument.Parse(jsonText);
        var root = resDoc.RootElement;

        root.GetProperty("status").GetString().Should().Be("success");
        root.TryGetProperty("telemetry", out JsonElement tel).Should().BeTrue();
        tel.GetProperty("hero").GetString().Should().Be("Hero_Witchslayer");
        tel.GetProperty("level").GetInt32().Should().Be(16);
        tel.GetProperty("hp").GetInt32().Should().Be(1350);
        tel.GetProperty("maxHp").GetInt32().Should().Be(1350);
        tel.GetProperty("mana").GetInt32().Should().Be(620);
        tel.GetProperty("maxMana").GetInt32().Should().Be(620);
        tel.GetProperty("gold").GetInt32().Should().Be(3100);
        tel.GetProperty("gameTime").GetInt32().Should().Be(1250);
    }

    [Fact]
    public async Task HandleAsync_CollectTelemetry_MissingTelemetryEmission_ReturnsGracefulWarning()
    {
        string ipcOutput = "Client executed command, but hero entity is not loaded.";
        var (handler, _, _) = CreateHandler(ipcResponse: ipcOutput);

        using JsonDocument req = JsonDocument.Parse("{\"action\": \"collect_telemetry\"}");
        ToolCallResult result = await handler.HandleAsync(req.RootElement);

        result.IsError.Should().BeFalse();
        string jsonText = ((TextContentItem)result.Content[0]).Text;
        using JsonDocument resDoc = JsonDocument.Parse(jsonText);
        var root = resDoc.RootElement;

        root.GetProperty("status").GetString().Should().Be("success");
        root.TryGetProperty("warning", out JsonElement warn).Should().BeTrue();
        warn.GetString().Should().Contain("no [NEXUS_TELEMETRY] emission was found");
    }

    [Fact]
    public async Task HandleAsync_ApplySnapshot_InvalidGamePhase_ReturnsStructuredErrorResult()
    {
        string ipcOutput = "[NEXUS_EVENT] ERROR|{\"errorCode\":\"INVALID_GAME_PHASE\",\"currentPhase\":2,\"message\":\"Snapshot can only be applied during active match (phase 3)\"}";
        var (handler, _, _) = CreateHandler(ipcResponse: ipcOutput);

        using JsonDocument req = JsonDocument.Parse("{\"action\": \"apply_snapshot\", \"snapshot\": {\"level\": 15, \"gold\": 4000}}");
        ToolCallResult result = await handler.HandleAsync(req.RootElement);

        result.IsError.Should().BeTrue();
        string jsonText = ((TextContentItem)result.Content[0]).Text;
        using JsonDocument resDoc = JsonDocument.Parse(jsonText);
        var root = resDoc.RootElement;

        root.GetProperty("status").GetString().Should().Be("error");
        root.GetProperty("errorCode").GetString().Should().Be("INVALID_GAME_PHASE");
        root.GetProperty("currentPhase").GetInt32().Should().Be(2);
    }

    [Fact]
    public async Task HandleAsync_ConcurrentBursts_ThreadSafeUnderLoad()
    {
        var (handler, _, _) = CreateHandler(
            ipcResponse: "[NEXUS_TELEMETRY] {\"hero\":\"Hero_Pyromancer\",\"level\":1,\"hp\":600,\"maxHp\":600}");

        var tasks = Enumerable.Range(0, 50).Select(async i =>
        {
            using JsonDocument req = JsonDocument.Parse("{\"action\": \"collect_telemetry\"}");
            ToolCallResult res = await handler.HandleAsync(req.RootElement);
            res.IsError.Should().BeFalse();
        });

        await Task.WhenAll(tasks);
    }

    #endregion

    [Fact]
    public void TryExtractTelemetry_KeyValueFallback_DuplicateKeys_LastKeyWins()
    {
        string payload = "[NEXUS_TELEMETRY] hp=500;hp=600;hp=750;hero=Hero_Devourer";

        bool success = GameLuaHandler.TryExtractTelemetry(payload, out JsonElement el);

        success.Should().BeTrue();
        el.GetProperty("hp").GetInt32().Should().Be(750);
        el.GetProperty("hero").GetString().Should().Be("Hero_Devourer");
    }

    [Fact]
    public void TryExtractTelemetry_KeyValueFallback_SpecialCharactersInValues_PreservesStrings()
    {
        string payload = "[NEXUS_TELEMETRY] model=/heroes/flint/model.mdf;anim=run_1-fast;tag=@nexus_v2";

        bool success = GameLuaHandler.TryExtractTelemetry(payload, out JsonElement el);

        success.Should().BeTrue();
        el.GetProperty("model").GetString().Should().Be("/heroes/flint/model.mdf");
        el.GetProperty("anim").GetString().Should().Be("run_1-fast");
        el.GetProperty("tag").GetString().Should().Be("@nexus_v2");
    }

    [Fact]
    public void TryExtractTelemetry_NumericCoercions_ExtremeFloatPrecision_PreservesDoublePrecision()
    {
        string payload = "[NEXUS_TELEMETRY] hp=1250.123456;mana=0.000001";

        bool success = GameLuaHandler.TryExtractTelemetry(payload, out JsonElement el);

        success.Should().BeTrue();
        el.GetProperty("hp").GetDouble().Should().BeApproximately(1250.123456, 0.000001);
        el.GetProperty("mana").GetDouble().Should().BeApproximately(0.000001, 0.0000001);
    }

    [Fact]
    public void FormatJsonElementAsLuaTable_NonIdentifierKeys_EscapedWithBrackets()
    {
        using var doc = JsonDocument.Parse("{\"custom-stat\": 42, \"stat 2\": \"val\"}");
        string luaTable = GameLuaHandler.FormatJsonElementAsLuaTable(doc.RootElement);

        luaTable.Should().Contain("[\"custom-stat\"] = 42");
        luaTable.Should().Contain("[\"stat 2\"] = 'val'");
    }

    [Fact]
    public async Task HandleAsync_CollectTelemetry_ErrorFromClient_PreservesErrorResult()
    {
        var mockIpc = new Mock<IGameIpcBridge>();
        mockIpc.Setup(i => i.ExecuteCommandAsync(It.IsAny<string>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new TimeoutException("IPC timed out"));
        mockIpc.SetupGet(i => i.IsConnected).Returns(false);

        var mockUi = new Mock<IUiControlService>();
        mockUi.Setup(u => u.ExecuteInGameConsoleCommandAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new UiConsoleCommandResult { Success = false, ErrorMessage = "Client not running" });

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

        using var req = JsonDocument.Parse("{\"action\": \"collect_telemetry\"}");
        var res = await handler.HandleAsync(req.RootElement);

        res.IsError.Should().BeTrue();
    }

    #region Category 9: Fast Inner-Loop Execution & Zero Desktop GUI Windows

    [Fact]
    public async Task ChallengerTestSuite_WhenInvoked_AllTestsRunFastUnderManualGatingThreshold()
    {
        var (handler, _, mockUi) = CreateHandler(
            ipcResponse: "[NEXUS_TELEMETRY] {\"hero\":\"Hero_Pyromancer\",\"level\":1,\"hp\":600,\"maxHp\":600}");

        Stopwatch sw = Stopwatch.StartNew();

        for (int i = 0; i < 20; i++)
        {
            using JsonDocument req = JsonDocument.Parse("{\"action\": \"collect_telemetry\"}");
            var res = await handler.HandleAsync(req.RootElement);
            res.IsError.Should().BeFalse();
        }

        sw.Stop();

        // 20 executions must finish in < 2000ms under parallel assembly load
        sw.ElapsedMilliseconds.Should().BeLessThan(2000);

        // Zero physical GUI actuations
        mockUi.Invocations.Count.Should().Be(0);
    }

    #endregion
}

