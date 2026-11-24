// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// TAO (Together As One) MCP & Kongor CLI -- Created by guccidonut (https://github.com/guccidonut/PKV)

using System.Diagnostics;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using Kongor.Cli.Mcp.Handlers;
using Kongor.Cli.Mcp.Protocol;
using Kongor.Cli.Mcp.Services;
using Moq;
using Xunit;

namespace Kongor.Mcp.Tests;

/// <summary>
/// Unit tests for Phase 1: Universal IPC & Ambient Process Auto-Adoption.
/// Verifies that ambient game clients are seamlessly auto-adopted into the registry,
/// agents are never locked out of running instances, and dual-channel IPC deserializes correctly.
/// </summary>
public sealed class AmbientProcessAutoAdoptionTests
{
    [Fact]
    public void ValidateAgentAccess_AllowsSingleClientSharing_AcrossDifferentAgents()
    {
        var registry = new ClientProcessRegistry();
        using var proc = Process.GetCurrentProcess();

        registry.Register(new ClientInstance
        {
            ProcessId = proc.Id,
            SlotIndex = 0,
            AgentId = "agent-launcher",
            Process = proc,
            LaunchedAt = TimeProvider.System.GetUtcNow()
        });

        // Caller is a different agent, but since there is only 1 managed client running, access is permitted
        bool allowed = registry.ValidateAgentAccess("agent-collaborator", proc.Id, out string? error);

        allowed.Should().BeTrue();
        error.Should().BeNull();
    }

    [Fact]
    public void ValidateAgentAccess_AllowsAmbientOrAutoAgent()
    {
        var registry = new ClientProcessRegistry();
        using var proc = Process.GetCurrentProcess();

        registry.Register(new ClientInstance
        {
            ProcessId = proc.Id,
            SlotIndex = 0,
            AgentId = "agent-owner",
            Process = proc,
            LaunchedAt = TimeProvider.System.GetUtcNow()
        });

        bool allowedAmbient = registry.ValidateAgentAccess("ambient", proc.Id, out string? errorAmbient);
        allowedAmbient.Should().BeTrue();
        errorAmbient.Should().BeNull();

        bool allowedAuto = registry.ValidateAgentAccess("auto", proc.Id, out string? errorAuto);
        allowedAuto.Should().BeTrue();
        errorAuto.Should().BeNull();
    }

    [Fact]
    public void GetClientByAgentId_ReturnsSingleManagedClient_WhenNoExactMatch()
    {
        var registry = new ClientProcessRegistry();
        using var proc = Process.GetCurrentProcess();

        registry.Register(new ClientInstance
        {
            ProcessId = proc.Id,
            SlotIndex = 0,
            AgentId = "agent-initial",
            Process = proc,
            LaunchedAt = TimeProvider.System.GetUtcNow()
        });

        var match = registry.GetClientByAgentId("agent-secondary");

        match.Should().NotBeNull();
        match!.ProcessId.Should().Be(proc.Id);
    }

    [Fact]
    public void GetClientByAgentId_ReturnsAmbientClient_WhenPresent()
    {
        var registry = new ClientProcessRegistry();
        using var proc = Process.GetCurrentProcess();

        registry.Register(new ClientInstance
        {
            ProcessId = proc.Id,
            SlotIndex = 0,
            AgentId = "ambient",
            Process = proc,
            LaunchedAt = TimeProvider.System.GetUtcNow()
        });

        var match = registry.GetClientByAgentId("agent-inspector");

        match.Should().NotBeNull();
        match!.ProcessId.Should().Be(proc.Id);
    }

    [Fact]
    public void GameIpcBridge_TryDeserializeLine_ParsesJsonIdAndCommandResponse()
    {
        string jsonLine = "{\"id\":77,\"success\":true,\"result\":\"Echo [TAO_OK]\"}\n";
        byte[] bytes = Encoding.UTF8.GetBytes(jsonLine);

        bool success = IpcMessage.TryDeserializeLine(bytes, out IpcMessage? message, out int bytesConsumed);

        success.Should().BeTrue();
        bytesConsumed.Should().Be(bytes.Length);
        message.Should().NotBeNull();
        message!.OpCode.Should().Be(IpcOpCode.CommandResponse);
        message.RequestId.Should().Be(77u);
        message.PayloadString.Should().Contain("Echo [TAO_OK]");
    }

    [Fact]
    public void GameIpcBridge_TryDeserializeLine_HandlesPlainTextLine()
    {
        string textLine = "Pong response from test server\r\n";
        byte[] bytes = Encoding.UTF8.GetBytes(textLine);

        bool success = IpcMessage.TryDeserializeLine(bytes, out IpcMessage? message, out int bytesConsumed);

        success.Should().BeTrue();
        message.Should().NotBeNull();
        message!.OpCode.Should().Be(IpcOpCode.CommandResponse);
        message.RequestId.Should().Be(0u);
        message.PayloadString.Should().Be("Pong response from test server");
    }

    [Fact]
    public async Task GameClientManage_ExecuteClientCommand_DispatchesSuccessfully_ForAmbientAgent()
    {
        var registry = new ClientProcessRegistry();
        using var proc = Process.GetCurrentProcess();

        registry.Register(new ClientInstance
        {
            ProcessId = proc.Id,
            SlotIndex = 0,
            AgentId = "agent-launch",
            Process = proc,
            LaunchedAt = TimeProvider.System.GetUtcNow()
        });

        var mockIpc = new Mock<IGameIpcBridge>();
        mockIpc.SetupGet(x => x.IsConnected).Returns(true);
        mockIpc.Setup(x => x.ExecuteCommandAsync(It.IsAny<string>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync("[TAO_TEST_SUCCESS]");

        var handler = new GameClientManageHandler(
            ipcBridge: mockIpc.Object,
            processRegistry: registry,
            timeProvider: TimeProvider.System,
            logger: _ => { });

        McpAgentContext.CurrentAgentId = "ambient-agent-42";

        var res = await handler.ExecuteClientCommandAsync("Echo [TAO_VERIFY]", CancellationToken.None);

        res.IsError.Should().BeFalse();
        string text = ((TextContentItem)res.Content[0]).Text;
        text.Should().Contain("[TAO_TEST_SUCCESS]");
    }
}
