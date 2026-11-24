// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// TAO (Together As One) MCP & Kongor CLI -- Created by guccidonut (https://github.com/guccidonut/PKV)

using System.Diagnostics;
using FluentAssertions;
using Kongor.Cli.Mcp.Protocol;
using Kongor.Cli.Mcp.Services;
using Moq;
using Xunit;

namespace Kongor.Mcp.Tests;

public sealed class McpServerIpcBridgeTests
{
    [Fact]
    public async Task Constructor_Defaults_InitializesServerAndPipeName()
    {
        await using McpServerIpcBridge bridge = new(autoStart: false);

        bridge.PipeName.Should().Be("willowmaker-ipc");
        bridge.ServerName.Should().Be(".");
        bridge.IsConnected.Should().BeFalse();
        bridge.IsAuthenticated.Should().BeFalse();
    }

    [Fact]
    public void McpToolRegistry_InitializesWithMcpServerIpcBridge_ByDefault()
    {
        McpToolRegistry registry = new(registerDefaults: true);
        registry.Should().NotBeNull();
        registry.ListTools().Should().NotBeEmpty();
    }

    [Fact]
    public async Task AttachProcess_WhenInvoked_DelegatesToUnderlyingServer()
    {
        Mock<IMcpIpcServer> mockServer = new();
        await using McpServerIpcBridge bridge = new(server: mockServer.Object, autoStart: false);

        using Process currentProc = Process.GetCurrentProcess();
        bridge.AttachProcess(currentProc);

        mockServer.Verify(s => s.AttachProcess(currentProc), Times.Once);
    }

    [Fact]
    public async Task ExecuteCommandAsync_WithInvalidInput_ThrowsOnNullOrEmptyCommand()
    {
        await using McpServerIpcBridge bridge = new(autoStart: false);

        var actNull = () => bridge.ExecuteCommandAsync(null!);
        await actNull.Should().ThrowAsync<ArgumentException>();

        var actEmpty = () => bridge.ExecuteCommandAsync("   ");
        await actEmpty.Should().ThrowAsync<ArgumentException>();
    }

    [Fact]
    public async Task DispatchUiEventAsync_WithInvalidInput_ThrowsOnNullOrEmptyWidget()
    {
        await using McpServerIpcBridge bridge = new(autoStart: false);

        var actNull = () => bridge.DispatchUiEventAsync("main", null!, "click");
        await actNull.Should().ThrowAsync<ArgumentException>();

        var actEmpty = () => bridge.DispatchUiEventAsync("main", "   ", "click");
        await actEmpty.Should().ThrowAsync<ArgumentException>();
    }

    [Fact]
    public async Task ConnectAsync_TimesOut_WhenNoClientConnects()
    {
        Mock<IMcpIpcServer> mockServer = new();
        mockServer.SetupGet(s => s.IsRunning).Returns(true);
        mockServer.SetupGet(s => s.ConnectedClientsCount).Returns(0);
        mockServer.SetupGet(s => s.PipeName).Returns("test-pipe-timeout");

        await using McpServerIpcBridge bridge = new(server: mockServer.Object, autoStart: false);

        var act = () => bridge.ConnectAsync(timeoutMs: 50);
        await act.Should().ThrowAsync<TimeoutException>();
    }

    [Fact]
    public async Task AuthenticateAsync_EmptyKey_ReturnsFalse()
    {
        await using McpServerIpcBridge bridge = new(autoStart: false);

        bool result = await bridge.AuthenticateAsync(authKey: "");
        result.Should().BeFalse();
        bridge.IsAuthenticated.Should().BeFalse();
    }
}
