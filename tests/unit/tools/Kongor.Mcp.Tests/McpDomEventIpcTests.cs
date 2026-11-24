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

public sealed class McpDomEventIpcTests
{
    [Fact]
    public async Task UiControlHandler_DispatchEvent_CallsIpcBridge()
    {
        Mock<IGameIpcBridge> mockIpc = new();
        mockIpc.Setup(ipc => ipc.DispatchUiEventAsync("main", "btn_play", "click", null, 3000, It.IsAny<CancellationToken>()))
            .ReturnsAsync("Event dispatched to btn_play:click");

        UiControlHandler handler = new(gameIpcBridge: mockIpc.Object);
        JsonElement args = JsonElement.Parse(@"{ ""action"": ""dispatch_event"", ""widget"": ""btn_play"", ""interface"": ""main"", ""event"": ""click"" }");

        ToolCallResult result = await handler.HandleAsync(args);

        result.IsError.Should().BeFalse();
        string jsonText = (result.Content[0] as TextContentItem)!.Text;
        using JsonDocument doc = JsonDocument.Parse(jsonText);
        JsonElement root = doc.RootElement;
        root.GetProperty("status").GetString().Should().Be("success");
        root.GetProperty("widget").GetString().Should().Be("btn_play");
        root.GetProperty("eventType").GetString().Should().Be("click");
        root.GetProperty("channel").GetString().Should().Be("engine_internal_ipc");

        mockIpc.Verify(ipc => ipc.DispatchUiEventAsync("main", "btn_play", "click", null, 3000, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task UiControlHandler_DispatchEvent_RemediatesPracticeButton()
    {
        Mock<IGameIpcBridge> mockIpc = new();
        mockIpc.Setup(ipc => ipc.DispatchUiEventAsync("main", "sysbar_quickStart", "click", null, 3000, It.IsAny<CancellationToken>()))
            .ReturnsAsync("Event dispatched to sysbar_quickStart:click");

        UiControlHandler handler = new(gameIpcBridge: mockIpc.Object);
        JsonElement args = JsonElement.Parse(@"{ ""action"": ""dispatch_event"", ""widget"": ""practice_button"", ""event"": ""click"" }");

        ToolCallResult result = await handler.HandleAsync(args);

        result.IsError.Should().BeFalse();
        string jsonText = (result.Content[0] as TextContentItem)!.Text;
        using JsonDocument doc = JsonDocument.Parse(jsonText);
        JsonElement root = doc.RootElement;
        root.GetProperty("status").GetString().Should().Be("success");
        root.GetProperty("widget").GetString().Should().Be("sysbar_quickStart");
        root.GetProperty("selfHealed").GetBoolean().Should().BeTrue();
        root.GetProperty("remediationHint").GetString().Should().Contain("sysbar_quickStart");

        mockIpc.Verify(ipc => ipc.DispatchUiEventAsync("main", "sysbar_quickStart", "click", null, 3000, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task UiControlHandler_ClickOnClientWidget_WhenIpcConnected_RoutesToDomDispatch()
    {
        Mock<IGameIpcBridge> mockIpc = new();
        mockIpc.SetupGet(ipc => ipc.IsConnected).Returns(true);
        mockIpc.Setup(ipc => ipc.DispatchUiEventAsync("main", "sysbar_quickStart", "click", null, 3000, It.IsAny<CancellationToken>()))
            .ReturnsAsync("Event dispatched to sysbar_quickStart:click");

        UiControlHandler handler = new(gameIpcBridge: mockIpc.Object);
        JsonElement args = JsonElement.Parse(@"{ ""action"": ""click"", ""target"": ""hon_x64"", ""element_name"": ""sysbar_quickStart"" }");

        ToolCallResult result = await handler.HandleAsync(args);

        result.IsError.Should().BeFalse();
        string jsonText = (result.Content[0] as TextContentItem)!.Text;
        using JsonDocument doc = JsonDocument.Parse(jsonText);
        JsonElement root = doc.RootElement;
        root.GetProperty("status").GetString().Should().Be("success");
        root.GetProperty("widget").GetString().Should().Be("sysbar_quickStart");
        root.GetProperty("channel").GetString().Should().Be("engine_internal_ipc");

        mockIpc.Verify(ipc => ipc.DispatchUiEventAsync("main", "sysbar_quickStart", "click", null, 3000, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task UiControlHandler_DispatchEvent_MissingWidget_ReturnsError()
    {
        Mock<IGameIpcBridge> mockIpc = new();
        UiControlHandler handler = new(gameIpcBridge: mockIpc.Object);
        JsonElement args = JsonElement.Parse(@"{ ""action"": ""dispatch_event"" }");

        ToolCallResult result = await handler.HandleAsync(args);

        result.IsError.Should().BeTrue();
        (result.Content[0] as TextContentItem)!.Text.Should().Contain("Missing required parameter: 'widget'");
    }

    [Fact]
    public async Task UiControlHandler_DispatchEvent_NoIpc_ReturnsError()
    {
        UiControlHandler handler = new(gameIpcBridge: null);
        JsonElement args = JsonElement.Parse(@"{ ""action"": ""dispatch_event"", ""widget"": ""btn_play"" }");

        ToolCallResult result = await handler.HandleAsync(args);

        result.IsError.Should().BeTrue();
        (result.Content[0] as TextContentItem)!.Text.Should().Contain("IPC connection to game client is not available");
    }
}
