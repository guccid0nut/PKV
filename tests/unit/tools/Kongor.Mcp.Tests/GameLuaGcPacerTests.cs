// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// TAO (Together As One) MCP & Kongor CLI -- Created by guccidonut (https://github.com/guccidonut/PKV)

// ============================================================================
// File: GameLuaGcPacerTests.cs
// System: Kongor MCP Unit Tests - [SR-05] Lua Generational Memory Sweeper
// ============================================================================

using System;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using Kongor.Cli.Mcp.Handlers;
using Kongor.Cli.Mcp.Protocol;
using Kongor.Cli.Mcp.Services;
using Moq;
using Xunit;

namespace Kongor.Mcp.Tests;

public sealed class GameLuaGcPacerTests
{
    private static (GameLuaHandler Handler, Mock<IGameIpcBridge> MockIpc) CreateHandlerWithMocks(
        string? ipcResponse = null,
        Action<string>? onExecute = null)
    {
        var mockIpc = new Mock<IGameIpcBridge>();
        var setup = mockIpc.Setup(i => i.ExecuteCommandAsync(It.IsAny<string>(), It.IsAny<int>(), It.IsAny<CancellationToken>()));
        if (onExecute != null)
        {
            setup.Callback<string, int, CancellationToken>((cmd, _, _) => onExecute(cmd));
        }
        setup.ReturnsAsync(ipcResponse ?? "OK");
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

        return (handler, mockIpc);
    }

    [Fact]
    public async Task GameLuaHandler_GcPace_FormatsStepCommandAndParsesMemoryTokens()
    {
        string executedCmd = string.Empty;
        string fakeResponse = "[K2] LUA_GC_PACE|48210.5|47980.2|75\nOK";

        var (handler, _) = CreateHandlerWithMocks(fakeResponse, cmd => executedCmd = cmd);

        var args = JsonElement.Parse("{\"action\": \"gc_pace\", \"step\": 75}");
        ToolCallResult result = await handler.HandleAsync(args);

        result.IsError.Should().BeFalse();
        executedCmd.Should().Contain("collectgarbage('step', 75)");
        executedCmd.Should().Contain("collectgarbage('count')");

        var textItem = result.Content[0] as TextContentItem;
        textItem.Should().NotBeNull();

        using var jsonDoc = JsonDocument.Parse(textItem!.Text);
        var root = jsonDoc.RootElement;
        root.GetProperty("status").GetString().Should().Be("success");
        root.GetProperty("action").GetString().Should().Be("gc_pace");
        root.GetProperty("memoryBeforeKb").GetDouble().Should().BeApproximately(48210.5, 0.1);
        root.GetProperty("memoryAfterKb").GetDouble().Should().BeApproximately(47980.2, 0.1);
        root.GetProperty("reclaimedKb").GetDouble().Should().BeApproximately(230.3, 0.1);
        root.GetProperty("stepSize").GetInt32().Should().Be(75);
        root.GetProperty("watermark").GetString().Should().Be("nominal");
    }

    [Fact]
    public async Task GameLuaHandler_GcStats_ReturnsMemoryMetricsAndWatermark()
    {
        string executedCmd = string.Empty;
        string fakeResponse = "LUA_GC_STATS|85240.0\nOK";

        var (handler, _) = CreateHandlerWithMocks(fakeResponse, cmd => executedCmd = cmd);

        var args = JsonElement.Parse("{\"action\": \"gc_stats\"}");
        ToolCallResult result = await handler.HandleAsync(args);

        result.IsError.Should().BeFalse();
        executedCmd.Should().Contain("collectgarbage('count')");

        var textItem = result.Content[0] as TextContentItem;
        textItem.Should().NotBeNull();

        using var jsonDoc = JsonDocument.Parse(textItem!.Text);
        var root = jsonDoc.RootElement;
        root.GetProperty("status").GetString().Should().Be("success");
        root.GetProperty("action").GetString().Should().Be("gc_stats");
        root.GetProperty("memoryKb").GetDouble().Should().BeApproximately(85240.0, 0.1);
        root.GetProperty("memoryMb").GetDouble().Should().BeApproximately(83.24, 0.1);
        root.GetProperty("watermark").GetString().Should().Be("elevated"); // > 65536 KB
    }

    [Fact]
    public async Task GameLuaHandler_GcCollect_ReturnsFullCycleSalvagedMemory()
    {
        string executedCmd = string.Empty;
        string fakeResponse = "LUA_GC_COLLECT|145000.0|62000.0\nOK";

        var (handler, _) = CreateHandlerWithMocks(fakeResponse, cmd => executedCmd = cmd);

        var args = JsonElement.Parse("{\"action\": \"gc_collect\"}");
        ToolCallResult result = await handler.HandleAsync(args);

        result.IsError.Should().BeFalse();
        executedCmd.Should().Contain("collectgarbage('collect')");

        var textItem = result.Content[0] as TextContentItem;
        textItem.Should().NotBeNull();

        using var jsonDoc = JsonDocument.Parse(textItem!.Text);
        var root = jsonDoc.RootElement;
        root.GetProperty("status").GetString().Should().Be("success");
        root.GetProperty("action").GetString().Should().Be("gc_collect");
        root.GetProperty("memoryBeforeKb").GetDouble().Should().BeApproximately(145000.0, 0.1);
        root.GetProperty("memoryAfterKb").GetDouble().Should().BeApproximately(62000.0, 0.1);
        root.GetProperty("reclaimedKb").GetDouble().Should().BeApproximately(83000.0, 0.1);
    }
}
