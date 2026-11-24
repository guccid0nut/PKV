// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

using ASPIRE.Common.IPC;

namespace ASPIRE.Integration.Tests.IPC;

public sealed class WillowmakerIpcAdversarialStressTests
{
    [Trait("Category", "Manual")]
    [Fact]
    public async Task Adversarial_MassiveMultiThreadedBurst_ZeroCrossTalk_ZeroCorruption()
    {
        if (!NexusTestEnvironment.IsManualTestEnabled) return;
        // Arrange
        string testPipeName = $"test-ipc-burst-adv-{Guid.NewGuid():N}";
        await using WillowmakerIpcServer server = new(testPipeName);

        server.OnMessageReceived += msg => msg.OpCode switch
        {
            IpcOpCode.ExecuteCommand => Task.FromResult<WillowmakerIpcMessage?>(
                WillowmakerIpcMessage.CreateString(IpcOpCode.CommandResponse, $"ECHO_{msg.PayloadString}")),
            IpcOpCode.GetEngineState => Task.FromResult<WillowmakerIpcMessage?>(
                WillowmakerIpcMessage.CreateString(IpcOpCode.EngineStateResponse, $"STATE_{msg.PayloadString}")),
            _ => Task.FromResult<WillowmakerIpcMessage?>(null)
        };

        await server.StartAsync();

        await using WillowmakerIpcClient client = new(testPipeName);
        await client.ConnectAsync(5000);
        Assert.True(client.IsConnected);

        const int workerCount = 32;
        const int requestsPerWorker = 20;
        ConcurrentBag<string> errors = [];

        // Act - 32 parallel workers firing 20 requests each across mixed OpCodes
        Task[] workerTasks =
        [
            .. Enumerable.Range(0, workerCount).Select(workerId => Task.Run(async () =>
            {
                for (int r = 0; r < requestsPerWorker; r++)
                    try
                    {
                        string reqPayload = $"W{workerId}_R{r}_{Guid.NewGuid():N}";

                        if (r % 3 == 0)
                        {
                            long latency = await client.PingAsync();
                            if (latency < 0) errors.Add($"Worker {workerId} Ping returned invalid latency {latency}");
                        }
                        else if (r % 3 == 1)
                        {
                            WillowmakerIpcMessage cmd =
                                WillowmakerIpcMessage.CreateString(IpcOpCode.ExecuteCommand, reqPayload);
                            WillowmakerIpcMessage? resp = await client.SendRequestAsync(cmd, 10000);
                            if (resp == null || resp.OpCode != IpcOpCode.CommandResponse ||
                                resp.PayloadString != $"ECHO_{reqPayload}")
                                errors.Add(
                                    $"Worker {workerId} ExecuteCommand mismatch! Expected ECHO_{reqPayload}, got '{resp?.PayloadString}' (OpCode: {resp?.OpCode})");
                        }
                        else
                        {
                            WillowmakerIpcMessage query =
                                WillowmakerIpcMessage.CreateString(IpcOpCode.GetEngineState, reqPayload);
                            WillowmakerIpcMessage? resp = await client.SendRequestAsync(query, 10000);
                            if (resp == null || resp.OpCode != IpcOpCode.EngineStateResponse ||
                                resp.PayloadString != $"STATE_{reqPayload}")
                                errors.Add(
                                    $"Worker {workerId} GetEngineState mismatch! Expected STATE_{reqPayload}, got '{resp?.PayloadString}' (OpCode: {resp?.OpCode})");
                        }
                    }
                    catch (Exception ex)
                    {
                        errors.Add($"Worker {workerId} Request {r} threw exception: {ex}");
                    }
            }))
        ];

        await Task.WhenAll(workerTasks);

        // Assert - 100% correlation with zero cross-talk across all 640 requests
        Assert.Empty(errors);
    }

    [Trait("Category", "Manual")]
    [Fact]
    public async Task Adversarial_DeepPipelining_RapidFireQueueDrain()
    {
        if (!NexusTestEnvironment.IsManualTestEnabled) return;
        // Arrange
        string testPipeName = $"test-ipc-pipeline-adv-{Guid.NewGuid():N}";
        await using WillowmakerIpcServer server = new(testPipeName);

        server.OnMessageReceived += msg =>
        {
            if (msg.OpCode == IpcOpCode.ExecuteCommand)
                return Task.FromResult<WillowmakerIpcMessage?>(
                    WillowmakerIpcMessage.CreateString(IpcOpCode.CommandResponse, $"ACK_{msg.PayloadString}"));
            return Task.FromResult<WillowmakerIpcMessage?>(null);
        };

        await server.StartAsync();

        await using WillowmakerIpcClient client = new(testPipeName);
        await client.ConnectAsync(5000);

        const int pipelineDepth = 200;
        Task<WillowmakerIpcMessage?>[] pipelinedTasks = new Task<WillowmakerIpcMessage?>[pipelineDepth];

        // Act - Rapidly fire 200 in-flight requests into the pipeline without awaiting individual responses
        for (int i = 0; i < pipelineDepth; i++)
        {
            WillowmakerIpcMessage req = WillowmakerIpcMessage.CreateString(IpcOpCode.ExecuteCommand, $"SEQ_{i:D5}");
            pipelinedTasks[i] = client.SendRequestAsync(req, 10000);
        }

        WillowmakerIpcMessage?[] responses = await Task.WhenAll(pipelinedTasks);

        // Assert - Verify all 200 responses arrived intact and in exact FIFO matching order
        Assert.Equal(pipelineDepth, responses.Length);
        for (int i = 0; i < pipelineDepth; i++)
        {
            Assert.NotNull(responses[i]);
            Assert.Equal(IpcOpCode.CommandResponse, responses[i]!.OpCode);
            Assert.Equal($"ACK_SEQ_{i:D5}", responses[i]!.PayloadString);
        }
    }

    [Trait("Category", "Manual")]
    [Fact]
    public async Task Adversarial_OrphanPoisoning_MultipleTimeouts_ZeroSubsequentPoisoning()
    {
        if (!NexusTestEnvironment.IsManualTestEnabled) return;
        // Arrange
        string testPipeName = $"test-ipc-orphan-adv-{Guid.NewGuid():N}";
        await using WillowmakerIpcServer server = new(testPipeName);

        server.OnMessageReceived += async msg =>
        {
            if (msg.PayloadString.StartsWith("SLOW_"))
            {
                // Delay slow requests by 500ms so client times out
                await Task.Delay(500);
                return WillowmakerIpcMessage.CreateString(IpcOpCode.CommandResponse, $"SLOW_RESP_{msg.PayloadString}");
            }

            if (msg.PayloadString.StartsWith("FAST_"))
            {
                await Task.Delay(5);
                return WillowmakerIpcMessage.CreateString(IpcOpCode.CommandResponse, $"FAST_RESP_{msg.PayloadString}");
            }

            return null;
        };

        await server.StartAsync();

        await using WillowmakerIpcClient client = new(testPipeName);
        await client.ConnectAsync(5000);

        // Step 1: Send 5 slow requests with 20ms timeout to force client-side timeout exceptions
        const int slowCount = 5;
        Task<WillowmakerIpcMessage?>[] slowTasks = new Task<WillowmakerIpcMessage?>[slowCount];
        for (int i = 0; i < slowCount; i++)
        {
            WillowmakerIpcMessage slowReq = WillowmakerIpcMessage.CreateString(IpcOpCode.ExecuteCommand, $"SLOW_{i}");
            slowTasks[i] = client.SendRequestAsync(slowReq, 20);
        }

        for (int i = 0; i < slowCount; i++) await Assert.ThrowsAsync<TimeoutException>(async () => await slowTasks[i]);

        // Step 2: While server is completing the delayed slow requests, fire 20 fast requests with 5000ms timeout
        const int fastCount = 20;
        Task<WillowmakerIpcMessage?>[] fastTasks = new Task<WillowmakerIpcMessage?>[fastCount];
        for (int i = 0; i < fastCount; i++)
        {
            WillowmakerIpcMessage fastReq = WillowmakerIpcMessage.CreateString(IpcOpCode.ExecuteCommand, $"FAST_{i}");
            fastTasks[i] = client.SendRequestAsync(fastReq);
        }

        WillowmakerIpcMessage?[] fastResponses = await Task.WhenAll(fastTasks);

        // Assert: Confirm that every single fast request received its exact matching FAST response,
        // and NONE of the 5 orphan slow responses corrupted or poisoned the fast queue.
        for (int i = 0; i < fastCount; i++)
        {
            Assert.NotNull(fastResponses[i]);
            Assert.Equal(IpcOpCode.CommandResponse, fastResponses[i]!.OpCode);
            Assert.Equal($"FAST_RESP_FAST_{i}", fastResponses[i]!.PayloadString);
        }
    }

    [Trait("Category", "Manual")]
    [Fact]
    public async Task Adversarial_RepeatedServerCrashAndRapidAutoReconnect_RecoveryIntegrity()
    {
        if (!NexusTestEnvironment.IsManualTestEnabled) return;
        // Arrange
        string testPipeName = $"test-ipc-multirecon-adv-{Guid.NewGuid():N}";
        WillowmakerIpcClientOptions options = new()
        {
            AutoReconnect = true,
            InitialBackoffMs = 30,
            MaxBackoffMs = 150,
            DefaultTimeoutMs = 3000
        };

        WillowmakerIpcServer currentServer = new(testPipeName);
        currentServer.OnMessageReceived += msg => Task.FromResult<WillowmakerIpcMessage?>(
            WillowmakerIpcMessage.CreateString(IpcOpCode.CommandResponse, $"INIT_{msg.PayloadString}"));
        await currentServer.StartAsync();

        await using WillowmakerIpcClient client = new(testPipeName, options: options);
        await client.ConnectAsync();
        Assert.True(client.IsConnected);

        // Verify initial transaction
        WillowmakerIpcMessage initResp = (await client.SendRequestAsync(
            WillowmakerIpcMessage.CreateString(IpcOpCode.ExecuteCommand, "TEST_INIT"), 3000))!;
        Assert.Equal("INIT_TEST_INIT", initResp.PayloadString);

        // Act - Perform 3 consecutive cycles of server crash and restart
        for (int cycle = 1; cycle <= 3; cycle++)
        {
            int currentCycle = cycle;
            TaskCompletionSource reconnectedTcs = new(TaskCreationOptions.RunContinuationsAsynchronously);

            void OnReconnectedHandler()
            {
                reconnectedTcs.TrySetResult();
            }

            client.OnReconnected += OnReconnectedHandler;

            try
            {
                // Crash server
                await currentServer.StopAsync();
                await currentServer.DisposeAsync();

                // Spin up new server on same pipe with cycle-specific response logic
                await Task.Delay(60);
                currentServer = new WillowmakerIpcServer(testPipeName);
                currentServer.OnMessageReceived += msg => Task.FromResult<WillowmakerIpcMessage?>(
                    WillowmakerIpcMessage.CreateString(IpcOpCode.CommandResponse,
                        $"CYCLE_{currentCycle}_{msg.PayloadString}"));
                await currentServer.StartAsync();

                // Await client auto-reconnection
                await reconnectedTcs.Task.WaitAsync(TimeSpan.FromSeconds(6));
                Assert.True(client.IsConnected);

                // Send request on new connection
                WillowmakerIpcMessage cycleReq =
                    WillowmakerIpcMessage.CreateString(IpcOpCode.ExecuteCommand, $"PING_{currentCycle}");
                WillowmakerIpcMessage? cycleResp = await client.SendRequestAsync(cycleReq, 3000);

                Assert.NotNull(cycleResp);
                Assert.Equal($"CYCLE_{currentCycle}_PING_{currentCycle}", cycleResp.PayloadString);
            }
            finally
            {
                client.OnReconnected -= OnReconnectedHandler;
            }
        }

        await currentServer.StopAsync();
        await currentServer.DisposeAsync();
    }

    [Trait("Category", "Manual")]
    [Fact]
    public async Task Adversarial_WhenExecuted_ConcurrentBidirectionalEventsAndLargePayloads()
    {
        if (!NexusTestEnvironment.IsManualTestEnabled) return;
        // Arrange
        string testPipeName = $"test-ipc-large-bidi-adv-{Guid.NewGuid():N}";
        await using WillowmakerIpcServer server = new(testPipeName);

        ConcurrentBag<string> receivedServerLogs = [];
        server.OnLogStreamReceived += receivedServerLogs.Add;

        server.OnMessageReceived += msg =>
        {
            if (msg.OpCode == IpcOpCode.ExecuteCommand)
                return Task.FromResult<WillowmakerIpcMessage?>(
                    WillowmakerIpcMessage.CreateString(IpcOpCode.CommandResponse, $"CMD_OK_{msg.PayloadString}"));
            if (msg.OpCode == IpcOpCode.GetEngineState)
                // Echo back the large payload with EngineStateResponse
                return Task.FromResult<WillowmakerIpcMessage?>(
                    WillowmakerIpcMessage.CreateBytes(IpcOpCode.EngineStateResponse, msg.Payload));
            return Task.FromResult<WillowmakerIpcMessage?>(null);
        };

        await server.StartAsync();

        await using WillowmakerIpcClient client = new(testPipeName);
        ConcurrentBag<string> receivedClientBroadcasts = [];
        client.OnMessageReceived += msg =>
        {
            if (msg.OpCode == IpcOpCode.HotReloadMod) receivedClientBroadcasts.Add(msg.PayloadString);
        };

        await client.ConnectAsync(5000);

        // 2 MB random binary payload
        byte[] largePayload = new byte[2 * 1024 * 1024];
        Random.Shared.NextBytes(largePayload);

        // Act - Concurrently send large frame, multiple small commands, client logs, and server broadcasts
        Task largeFrameTask = Task.Run(async () =>
        {
            WillowmakerIpcMessage largeReq = WillowmakerIpcMessage.CreateBytes(IpcOpCode.GetEngineState, largePayload);
            WillowmakerIpcMessage? largeResp = await client.SendRequestAsync(largeReq, 10000);
            Assert.NotNull(largeResp);
            Assert.Equal(IpcOpCode.EngineStateResponse, largeResp.OpCode);
            Assert.Equal(largePayload.Length, largeResp.Payload.Length);
            Assert.True(largePayload.AsSpan().SequenceEqual(largeResp.Payload));
        });

        Task commandBurstTask = Task.Run(async () =>
        {
            for (int i = 0; i < 30; i++)
            {
                WillowmakerIpcMessage cmd =
                    WillowmakerIpcMessage.CreateString(IpcOpCode.ExecuteCommand, $"CONCURRENT_{i}");
                WillowmakerIpcMessage? resp = await client.SendRequestAsync(cmd, 10000);
                Assert.NotNull(resp);
                Assert.Equal($"CMD_OK_CONCURRENT_{i}", resp.PayloadString);
            }
        });

        Task logStreamTask = Task.Run(async () =>
        {
            for (int i = 0; i < 20; i++)
            {
                WillowmakerIpcMessage logMsg =
                    WillowmakerIpcMessage.CreateString(IpcOpCode.LogStreamEvent, $"LOG_ENTRY_{i}");
                await client.SendMessageAsync(logMsg);
            }
        });

        Task serverBroadcastTask = Task.Run(async () =>
        {
            for (int i = 0; i < 15; i++)
            {
                WillowmakerIpcMessage bcast =
                    WillowmakerIpcMessage.CreateString(IpcOpCode.HotReloadMod, $"BCAST_ENTRY_{i}");
                await server.BroadcastAsync(bcast);
            }
        });

        await Task.WhenAll(largeFrameTask, commandBurstTask, logStreamTask, serverBroadcastTask);

        // Wait a moment for async event dispatches to settle
        await Task.Delay(200);

        // Assert - Verify events arrived
        Assert.Equal(20, receivedServerLogs.Count);
        Assert.True(receivedClientBroadcasts.Count >= 15);
    }

    [Trait("Category", "Manual")]
    [Fact]
    public async Task Adversarial_RapidClientDispose_DuringHighVolumeInFlightRequests()
    {
        if (!NexusTestEnvironment.IsManualTestEnabled) return;
        // Arrange
        string testPipeName = $"test-ipc-dispose-adv-{Guid.NewGuid():N}";
        await using WillowmakerIpcServer server = new(testPipeName);

        server.OnMessageReceived += async msg =>
        {
            await Task.Delay(500); // Simulate slow processing
            return WillowmakerIpcMessage.CreateString(IpcOpCode.CommandResponse, "DELAYED");
        };

        await server.StartAsync();

        WillowmakerIpcClient client = new(testPipeName);
        await client.ConnectAsync();

        // Act - Launch 30 requests in flight and immediately dispose client
        Task<WillowmakerIpcMessage?>[] tasks = new Task<WillowmakerIpcMessage?>[30];
        for (int i = 0; i < 30; i++)
        {
            WillowmakerIpcMessage req = WillowmakerIpcMessage.CreateString(IpcOpCode.ExecuteCommand, $"REQ_{i}");
            tasks[i] = client.SendRequestAsync(req);
        }

        await Task.Delay(20);
        await client.DisposeAsync();

        // Assert - All in-flight requests should complete (throwing exception or cancelled) without hung tasks or deadlocks
        for (int i = 0; i < 30; i++) await Assert.ThrowsAnyAsync<Exception>(async () => await tasks[i]);
    }
}
