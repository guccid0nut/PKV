// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// TAO (Together As One) MCP & Kongor CLI -- Created by guccidonut (https://github.com/guccidonut/PKV)

using System.Diagnostics;
using FluentAssertions;
using Kongor.Cli.Mcp.Services;
using NEXUS.Contracts.Ipc;
using Xunit;
using IpcOpCode = NEXUS.Contracts.Ipc.IpcOpCode;
using WillowmakerIpcMessage = NEXUS.Contracts.Ipc.WillowmakerIpcMessage;

namespace Kongor.Mcp.Tests;

public sealed class McpIpcServerTests
{
    [Fact]
    public void Factory_CreateForClient_FormatsPipeName()
    {
        using McpIpcServer server = McpIpcServer.CreateForClient(54321);
        server.PipeName.Should().Be("kongor-ipc-client-54321");
    }

    [Fact]
    public void Factory_CreateForServer_FormatsPipeName()
    {
        using McpIpcServer server = McpIpcServer.CreateForServer(11235);
        server.PipeName.Should().Be("kongor-ipc-server-11235");
    }

    [Fact]
    public async Task Server_StartAndStop_UpdatesRunningState()
    {
        string uniquePipe = $"test-ipc-server-lifecycle-{Guid.NewGuid():N}";
        await using McpIpcServer server = new(uniquePipe);

        server.IsRunning.Should().BeFalse();
        await server.StartAsync();
        server.IsRunning.Should().BeTrue();

        await server.StopAsync();
        server.IsRunning.Should().BeFalse();
    }

    [Fact]
    public async Task SendRequestAsync_NoConnectedClients_ThrowsIOException()
    {
        string uniquePipe = $"test-ipc-server-noclient-{Guid.NewGuid():N}";
        await using McpIpcServer server = new(uniquePipe);
        await server.StartAsync();

        var request = WillowmakerIpcMessage.CreateString(IpcOpCode.Ping, "PING");

        var act = () => server.SendRequestAsync(request, timeoutMs: 500);
        await act.Should().ThrowAsync<IOException>()
            .WithMessage("*No connected clients*");
    }

    [Fact]
    public void FaultAllPendingRequests_WhenInvoked_TriggersGameProcessTerminatedException()
    {
        string uniquePipe = $"test-ipc-server-fault-{Guid.NewGuid():N}";
        using McpIpcServer server = new(uniquePipe);

        var request = WillowmakerIpcMessage.CreateString(IpcOpCode.ExecuteCommand, "echo test", requestId: 42);

        // Simulate a pending TCS via reflection or direct fault call
        GameProcessTerminatedException ex = new(processId: 9999, exitCode: -1073741819);
        server.FaultAllPendingRequests(ex);

        ex.ProcessId.Should().Be(9999);
        ex.ExitCode.Should().Be(-1073741819);
        ex.Message.Should().Contain("PID: 9999");
        ex.Message.Should().Contain("-1073741819");
    }

    [Fact]
    public async Task SendRequestAsync_WhenAttachedProcessAlreadyExited_ThrowsImmediately()
    {
        string uniquePipe = $"test-ipc-server-exited-{Guid.NewGuid():N}";
        await using McpIpcServer server = new(uniquePipe);

        // Start and immediately let a quick process exit
        Process proc = Process.Start(new ProcessStartInfo
        {
            FileName = "cmd.exe",
            Arguments = "/c exit 42",
            CreateNoWindow = true,
            UseShellExecute = false
        })!;
        await proc.WaitForExitAsync();

        server.AttachProcess(proc);

        var request = WillowmakerIpcMessage.CreateString(IpcOpCode.Ping, "PING");
        var act = () => server.SendRequestAsync(request, timeoutMs: 500);

        var thrown = await act.Should().ThrowAsync<GameProcessTerminatedException>();
        thrown.Which.ExitCode.Should().Be(42);
    }
}
