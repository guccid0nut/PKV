// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

using ASPIRE.Common.IPC;
using FluentAssertions;

namespace ASPIRE.Integration.Tests.IPC;

/// <summary>
///     Empirical adversarial tests challenging stream disposal paths, cancellation forwarding,
///     and timeout handling across Willowmaker IPC client, server, and service.
/// </summary>
public sealed class WillowmakerStreamDisposalAndCancellationTests
{
    [Fact]
    public async Task IpcClient_PreCanceledToken_AbortsImmediately_AndPreservesSubsequentQueueOrder()
    {
        string pipeName = $"test-ipc-precancel-{Guid.NewGuid():N}";
        await using WillowmakerIpcServer server = new(pipeName);

        server.OnMessageReceived += msg =>
        {
            if (msg.OpCode == IpcOpCode.ExecuteCommand)
                return Task.FromResult<WillowmakerIpcMessage?>(
                    WillowmakerIpcMessage.CreateString(IpcOpCode.CommandResponse, $"ACK_{msg.PayloadString}"));
            return Task.FromResult<WillowmakerIpcMessage?>(null);
        };

        await server.StartAsync();

        await using WillowmakerIpcClient client = new(pipeName);
        await client.ConnectAsync();
        client.IsConnected.Should().BeTrue();

        // 1. Pre-canceled token request
        using CancellationTokenSource canceledCts = new();
        canceledCts.Cancel();

        WillowmakerIpcMessage cancelReq = WillowmakerIpcMessage.CreateString(IpcOpCode.ExecuteCommand, "CANCEL_ME");
        Func<Task> cancelAct = async () => await client.SendRequestAsync(cancelReq, 5000, canceledCts.Token);

        await cancelAct.Should().ThrowAsync<OperationCanceledException>();

        // 2. Immediate subsequent valid request must succeed cleanly without queue misalignment
        WillowmakerIpcMessage validReq = WillowmakerIpcMessage.CreateString(IpcOpCode.ExecuteCommand, "VALID_REQUEST");
        WillowmakerIpcMessage? validResp = await client.SendRequestAsync(validReq);

        validResp.Should().NotBeNull();
        validResp!.OpCode.Should().Be(IpcOpCode.CommandResponse);
        validResp.PayloadString.Should().Be("ACK_VALID_REQUEST");
    }

    [Fact]
    public async Task IpcClient_MidFlightCancellation_TriggersOperationCanceled_AndDoesNotPoisonQueue()
    {
        string pipeName = $"test-ipc-midflight-cancel-{Guid.NewGuid():N}";
        await using WillowmakerIpcServer server = new(pipeName);

        server.OnMessageReceived += async msg =>
        {
            if (msg.PayloadString == "SLOW_REQ")
            {
                await Task.Delay(100);
                return WillowmakerIpcMessage.CreateString(IpcOpCode.CommandResponse, "SLOW_RESP");
            }

            if (msg.PayloadString == "FAST_REQ")
            {
                await Task.Delay(1);
                return WillowmakerIpcMessage.CreateString(IpcOpCode.CommandResponse, "FAST_RESP");
            }

            return null;
        };

        await server.StartAsync();

        await using WillowmakerIpcClient client = new(pipeName);
        await client.ConnectAsync();

        // 1. Launch slow request with cancellation token that fires after 10ms
        using CancellationTokenSource midFlightCts = new(10);
        WillowmakerIpcMessage slowReq = WillowmakerIpcMessage.CreateString(IpcOpCode.ExecuteCommand, "SLOW_REQ");

        Func<Task> slowAct = async () => await client.SendRequestAsync(slowReq, 5000, midFlightCts.Token);
        await slowAct.Should().ThrowAsync<OperationCanceledException>();

        // 2. Launch fast request immediately
        WillowmakerIpcMessage fastReq = WillowmakerIpcMessage.CreateString(IpcOpCode.ExecuteCommand, "FAST_REQ");
        WillowmakerIpcMessage? fastResp = await client.SendRequestAsync(fastReq);

        // Assert: Fast request gets FAST_RESP (not corrupted by orphan SLOW_RESP)
        fastResp.Should().NotBeNull();
        fastResp!.PayloadString.Should().Be("FAST_RESP");
    }

    [Fact]
    public async Task IpcClient_DisconnectAndReconnect_DisposesOldStream_AndAllowsNewTraffic()
    {
        string pipeName = $"test-ipc-recon-stream-{Guid.NewGuid():N}";
        await using WillowmakerIpcServer server = new(pipeName);
        await server.StartAsync();

        await using WillowmakerIpcClient client = new(pipeName);
        await client.ConnectAsync();
        client.IsConnected.Should().BeTrue();

        long latency1 = await client.PingAsync();
        latency1.Should().BeGreaterThanOrEqualTo(0);

        // Disconnect manually
        await client.DisconnectAsync();
        client.IsConnected.Should().BeFalse();

        // Sending while disconnected throws InvalidOperationException
        WillowmakerIpcMessage ping = WillowmakerIpcMessage.CreateString(IpcOpCode.Ping, "PING");
        Func<Task> sendAct = async () => await client.SendMessageAsync(ping);
        await sendAct.Should().ThrowAsync<InvalidOperationException>();

        // Reconnect and verify stream is operational again
        await client.ConnectAsync();
        client.IsConnected.Should().BeTrue();

        long latency2 = await client.PingAsync();
        latency2.Should().BeGreaterThanOrEqualTo(0);
    }

    [Fact]
    public async Task IpcServer_StopAsync_DisposesAllClientStreams_AndCleansUpResources()
    {
        string pipeName = $"test-ipc-srv-stop-{Guid.NewGuid():N}";
        WillowmakerIpcServer server = new(pipeName);
        await server.StartAsync();

        await using WillowmakerIpcClient client1 = new(pipeName);
        await using WillowmakerIpcClient client2 = new(pipeName);

        await client1.ConnectAsync();
        await client2.ConnectAsync();

        for (int i = 0; i < 200 && server.ConnectedClientsCount < 2; i++) await Task.Delay(5);

        server.ConnectedClientsCount.Should().Be(2);
        server.IsRunning.Should().BeTrue();

        await server.StopAsync();

        server.ConnectedClientsCount.Should().Be(0);
        server.IsRunning.Should().BeFalse();

        await server.DisposeAsync();
    }

    [Trait("Category", "Manual")]
    [Fact]
    public async Task IpcClient_DisposeAsync_IsIdempotentAndSafe()
    {
        if (!NexusTestEnvironment.IsManualTestEnabled) return;
        string pipeName = $"test-ipc-idempotent-{Guid.NewGuid():N}";
        await using WillowmakerIpcServer server = new(pipeName);
        await server.StartAsync();

        WillowmakerIpcClient client = new(pipeName);
        await client.ConnectAsync();

        await client.DisposeAsync();
        client.IsConnected.Should().BeFalse();

        // Second dispose should be a no-op and not throw
        Func<Task> secondDispose = async () => await client.DisposeAsync();
        await secondDispose.Should().NotThrowAsync();
    }

    [Fact]
    public async Task IpcClient_RapidConcurrentBursts_WithInterspersedCancellations_MaintainsStability()
    {
        string pipeName = $"test-ipc-stress-cancels-{Guid.NewGuid():N}";
        await using WillowmakerIpcServer server = new(pipeName);

        server.OnMessageReceived += async msg =>
        {
            if (msg.PayloadString.StartsWith("DELAY_")) await Task.Delay(10);
            return WillowmakerIpcMessage.CreateString(IpcOpCode.CommandResponse, $"ECHO_{msg.PayloadString}");
        };

        await server.StartAsync();

        await using WillowmakerIpcClient client = new(pipeName);
        await client.ConnectAsync();

        const int totalRequests = 40;
        Task[] tasks = new Task[totalRequests];

        for (int i = 0; i < totalRequests; i++)
        {
            int id = i;
            if (id % 4 == 0)
            {
                // Pre-canceled
                CancellationToken token = new(true);
                tasks[id] = Task.Run(async () =>
                {
                    try
                    {
                        await client.SendRequestAsync(
                            WillowmakerIpcMessage.CreateString(IpcOpCode.ExecuteCommand, $"PRECANCEL_{id}"),
                            cancellationToken: token);
                    }
                    catch (OperationCanceledException)
                    {
                    }
                });
            }
            else if (id % 4 == 1)
            {
                // Timeout
                tasks[id] = Task.Run(async () =>
                {
                    try
                    {
                        await client.SendRequestAsync(
                            WillowmakerIpcMessage.CreateString(IpcOpCode.ExecuteCommand, $"DELAY_{id}"), 1);
                    }
                    catch (TimeoutException)
                    {
                    }
                });
            }
            else
            {
                // Normal
                tasks[id] = Task.Run(async () =>
                {
                    WillowmakerIpcMessage req =
                        WillowmakerIpcMessage.CreateString(IpcOpCode.ExecuteCommand, $"NORMAL_{id}");
                    WillowmakerIpcMessage? resp = await client.SendRequestAsync(req);
                    resp.Should().NotBeNull();
                    resp!.PayloadString.Should().Be($"ECHO_NORMAL_{id}");
                });
            }
        }

        await Task.WhenAll(tasks);
    }
}
