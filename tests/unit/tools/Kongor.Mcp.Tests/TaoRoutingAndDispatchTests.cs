// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// TAO (Together As One) MCP & Kongor CLI -- Created by guccidonut (https://github.com/guccidonut/PKV)

using System.Text.Json;
using FluentAssertions;
using Kongor.Cli.Mcp.Handlers;
using Kongor.Cli.Mcp.Protocol;
using Kongor.Cli.Mcp.Services;
using Moq;
using Xunit;

namespace Kongor.Mcp.Tests;

/// <summary>
/// Phase 1 TAO Modernization Unit Test Suite.
/// Validates unified target routing (target: "server:{port}") and TAO companion layer dispatch
/// (inspect_ui, navigate, protect_queues, tao_dispatch) across GameCommandHandler and GameLuaHandler.
/// </summary>
public sealed class TaoRoutingAndDispatchTests
{
    private static JsonElement ParseJson(string json) => JsonElement.Parse(json);

    private static (GameLuaHandler Handler, Mock<IGameIpcBridge> MockIpc) CreateLuaHandlerWithMocks(
        string? ipcResponse = null,
        Action<string>? onExecute = null,
        GameServerManageHandler? serverManageHandler = null)
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
            serverManageHandler: serverManageHandler,
            uiControlService: mockUi.Object,
            remediationService: remediationService,
            timeProvider: TimeProvider.System,
            logger: _ => { });

        return (handler, mockIpc);
    }

    #region 1. Unified Target Routing Tests

    [Fact]
    public async Task GameCommandHandler_RoutesToServerWithExplicitPortFormat_11236()
    {
        var serverManageHandler = new GameServerManageHandler();
        var commandHandler = new GameCommandHandler(serverManageHandler: serverManageHandler);

        JsonElement args = ParseJson("{\"command\": \"status\", \"target\": \"server:11236\"}");
        ToolCallResult result = await commandHandler.HandleAsync(args);

        string text = ((TextContentItem)result.Content[0]).Text;
        text.Should().Contain("\"port\": 11236");
        text.Should().Contain("status");
    }

    [Fact]
    public async Task GameCommandHandler_HandleServerCommandAsync_SupportsServerColonPort()
    {
        var serverManageHandler = new GameServerManageHandler();
        var commandHandler = new GameCommandHandler(serverManageHandler: serverManageHandler);

        JsonElement args = ParseJson("{\"command\": \"AddFakePlayer 1\", \"target\": \"server:11240\"}");
        ToolCallResult result = await commandHandler.HandleServerCommandAsync(args);

        string text = ((TextContentItem)result.Content[0]).Text;
        text.Should().Contain("\"port\": 11240");
        text.Should().Contain("AddFakePlayer 1");
    }

    [Fact]
    public async Task GameLuaHandler_RoutesToServerWithExplicitPortFormat_11237()
    {
        var serverManageHandler = new GameServerManageHandler();
        var (handler, _) = CreateLuaHandlerWithMocks(serverManageHandler: serverManageHandler);

        JsonElement args = ParseJson("{\"action\": \"code\", \"code\": \"Echo('server_test')\", \"target\": \"server:11237\"}");
        ToolCallResult result = await handler.HandleAsync(args);

        string text = ((TextContentItem)result.Content[0]).Text;
        text.Should().Contain("\"port\": 11237");
        text.Should().Contain("server_test");
    }

    #endregion

    #region 2. In-Engine TAO Action Formatting Tests

    [Fact]
    public async Task GameLuaHandler_InspectUiAction_FormatsCorrectTaoDispatchCommand()
    {
        string? sentCommand = null;
        var (handler, _) = CreateLuaHandlerWithMocks(
            ipcResponse: "[TAO_RESPONSE] {\"status\":\"success\",\"data\":{\"interface\":\"main\",\"found\":true,\"root\":{\"name\":\"root\"}}}",
            onExecute: cmd => sentCommand = cmd);

        JsonElement args = ParseJson("{\"action\": \"inspect_ui\", \"interface\": \"main\", \"widget\": \"store_container2\", \"maxDepth\": 5}");
        ToolCallResult result = await handler.HandleAsync(args);

        result.IsError.Should().BeFalse();
        sentCommand.Should().NotBeNull();
        sentCommand.Should().StartWith("Script ");
        sentCommand.Should().Contain("TAO.Dispatch('inspect_ui'");
        sentCommand.Should().Contain("main");
        sentCommand.Should().Contain("store_container2");

        string text = ((TextContentItem)result.Content[0]).Text;
        text.Should().Contain("\"status\": \"success\"");
        text.Should().Contain("\"taoStatus\": \"success\"");
        text.Should().Contain("\"interface\": \"main\"");
    }

    [Fact]
    public async Task GameLuaHandler_NavigateAction_FormatsCorrectTaoDispatchCommand()
    {
        string? sentCommand = null;
        var (handler, _) = CreateLuaHandlerWithMocks(
            ipcResponse: "[TAO_RESPONSE] {\"status\":\"success\",\"data\":{\"route\":\"store/altavatars\",\"activeTab\":\"altavatars\"}}",
            onExecute: cmd => sentCommand = cmd);

        JsonElement args = ParseJson("{\"action\": \"navigate\", \"route\": \"store/altavatars\", \"hero\": \"Hero_Gwynplaine\", \"avatar\": \"Hero_Gwynplaine.Alt\"}");
        ToolCallResult result = await handler.HandleAsync(args);

        result.IsError.Should().BeFalse();
        sentCommand.Should().NotBeNull();
        sentCommand.Should().StartWith("Script ");
        sentCommand.Should().Contain("TAO.Dispatch('navigate'");
        sentCommand.Should().Contain("store/altavatars");
        sentCommand.Should().Contain("Hero_Gwynplaine");

        string text = ((TextContentItem)result.Content[0]).Text;
        text.Should().Contain("\"status\": \"success\"");
        text.Should().Contain("\"route\": \"store/altavatars\"");
    }

    [Fact]
    public async Task GameLuaHandler_ProtectQueuesAction_FormatsCorrectTaoCommand()
    {
        string? sentCommand = null;
        var (handler, _) = CreateLuaHandlerWithMocks(
            ipcResponse: "[TAO_RESPONSE] {\"status\":\"success\",\"data\":{\"status\":\"protected\",\"shieldedCount\":1}}",
            onExecute: cmd => sentCommand = cmd);

        JsonElement args = ParseJson("{\"action\": \"protect_queues\"}");
        ToolCallResult result = await handler.HandleAsync(args);

        result.IsError.Should().BeFalse();
        sentCommand.Should().NotBeNull();
        sentCommand.Should().StartWith("Script ");
        sentCommand.Should().Contain("TAO.ProtectFrameQueues");

        string text = ((TextContentItem)result.Content[0]).Text;
        text.Should().Contain("\"status\": \"success\"");
        text.Should().Contain("\"shieldedCount\": 1");
    }

    [Fact]
    public async Task GameLuaHandler_TaoDispatch_CustomActionAndPayload_FormatsCorrectly()
    {
        string? sentCommand = null;
        var (handler, _) = CreateLuaHandlerWithMocks(
            ipcResponse: "[TAO_RESPONSE] {\"status\":\"success\",\"data\":{\"ack\":true}}",
            onExecute: cmd => sentCommand = cmd);

        JsonElement args = ParseJson("{\"action\": \"tao_dispatch\", \"subaction\": \"custom_eval\", \"payload\": {\"key\": \"value\"}}");
        ToolCallResult result = await handler.HandleAsync(args);

        result.IsError.Should().BeFalse();
        sentCommand.Should().NotBeNull();
        sentCommand.Should().StartWith("Script ");
        sentCommand.Should().Contain("TAO.Dispatch('custom_eval'");
        sentCommand.Should().Contain("key");
    }

    #endregion

    #region 3. Response Extraction & Deserialization Tests

    [Fact]
    public void TryExtractTaoResponse_SuccessPayload_ReturnsTrueAndData()
    {
        string raw = "[TAO_RESPONSE] {\"status\":\"success\",\"data\":{\"interface\":\"main\",\"found\":true}}";
        bool ok = GameLuaHandler.TryExtractTaoResponse(raw, out JsonElement data, out string? status, out string? error);

        ok.Should().BeTrue();
        status.Should().Be("success");
        error.Should().BeNull();
        data.GetProperty("interface").GetString().Should().Be("main");
        data.GetProperty("found").GetBoolean().Should().BeTrue();
    }

    [Fact]
    public void TryExtractTaoResponse_ErrorPayload_ReturnsTrueAndError()
    {
        string raw = "[TAO_RESPONSE] {\"status\":\"error\",\"error\":\"Interface not found: test\",\"traceback\":\"test traceback\"}";
        bool ok = GameLuaHandler.TryExtractTaoResponse(raw, out JsonElement data, out string? status, out string? error);

        ok.Should().BeTrue();
        status.Should().Be("error");
        error.Should().Be("Interface not found: test");
    }

    [Fact]
    public void TryExtractTaoResponse_WithColorCodes_StripsAndExtracts()
    {
        string raw = "^g[TAO_RESPONSE] {\"status\":\"success\",\"data\":{\"shieldedCount\":2}}";
        bool ok = GameLuaHandler.TryExtractTaoResponse(raw, out JsonElement data, out string? status, out string? error);

        ok.Should().BeTrue();
        status.Should().Be("success");
        data.GetProperty("shieldedCount").GetInt32().Should().Be(2);
    }

    [Fact]
    public void TryExtractTaoResponse_NonTaoText_ReturnsFalse()
    {
        string raw = "Random engine log message without TAO prefix";
        bool ok = GameLuaHandler.TryExtractTaoResponse(raw, out _, out _, out _);

        ok.Should().BeFalse();
    }

    #endregion
}
