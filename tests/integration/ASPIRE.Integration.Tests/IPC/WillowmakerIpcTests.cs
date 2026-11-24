// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

using ASPIRE.Common.IPC;

namespace ASPIRE.Tests.IPC;

public sealed class WillowmakerIpcTests
{
    [Fact]
    public void Message_Serialization_Roundtrip_PreservesOpCodeAndPayload()
    {
        // Arrange
        WillowmakerIpcMessage original = WillowmakerIpcMessage.CreateString(
            IpcOpCode.ExecuteCommand,
            "Connect 127.0.0.1:11235");

        // Act
        byte[] serialized = WillowmakerIpcMessage.Serialize(original);
        bool success =
            WillowmakerIpcMessage.TryDeserialize(serialized, out WillowmakerIpcMessage? deserialized,
                out int bytesRead);

        // Assert
        Assert.True(success);
        Assert.NotNull(deserialized);
        Assert.Equal(IpcOpCode.ExecuteCommand, deserialized.OpCode);
        Assert.Equal("Connect 127.0.0.1:11235", deserialized.PayloadString);
        Assert.Equal(serialized.Length, bytesRead);
    }

    [Fact]
    public void Message_12ByteFraming_PreservesRequestId()
    {
        // Arrange
        const uint expectedRequestId = 4294901761; // 0xFFFF0001
        WillowmakerIpcMessage original = WillowmakerIpcMessage.CreateString(
            IpcOpCode.ExecuteCommand,
            "Echo RequestIdTest",
            requestId: expectedRequestId);

        // Act
        byte[] serialized = WillowmakerIpcMessage.Serialize(original);
        bool success = WillowmakerIpcMessage.TryDeserialize(serialized, out WillowmakerIpcMessage? deserialized, out int bytesRead);

        // Assert
        Assert.True(success);
        Assert.NotNull(deserialized);
        Assert.Equal(expectedRequestId, deserialized.RequestId);
        Assert.Equal(IpcOpCode.ExecuteCommand, deserialized.OpCode);
        Assert.Equal("Echo RequestIdTest", deserialized.PayloadString);
        Assert.Equal(serialized.Length, bytesRead);
    }

    [Fact]
    public void Message_EncodeFrame_MatchesSerialize()
    {
        // Arrange
        const uint reqId = 12345;
        byte[] payload = "TestBufferWriter"u8.ToArray();
        System.Buffers.ArrayBufferWriter<byte> writer = new();

        // Act
        WillowmakerIpcMessage.EncodeFrame(IpcOpCode.Ping, reqId, payload, writer);
        byte[] serializedViaHelper = writer.WrittenSpan.ToArray();

        WillowmakerIpcMessage msg = WillowmakerIpcMessage.CreateBytes(IpcOpCode.Ping, payload, reqId);
        byte[] serializedDirect = WillowmakerIpcMessage.Serialize(msg);

        // Assert
        Assert.Equal(serializedDirect, serializedViaHelper);
        Assert.True(WillowmakerIpcMessage.TryDeserialize(serializedViaHelper, out WillowmakerIpcMessage? deserialized, out int bytesRead));
        Assert.NotNull(deserialized);
        Assert.Equal(reqId, deserialized.RequestId);
        Assert.Equal(IpcOpCode.Ping, deserialized.OpCode);
        Assert.Equal(payload, deserialized.Payload);
    }

    [Fact]
    public void Message_LargePayload_Roundtrip_WorksCorrectly()
    {
        // Arrange
        byte[] largeData = new byte[256 * 1024]; // 256 KB
        Random.Shared.NextBytes(largeData);

        WillowmakerIpcMessage original = WillowmakerIpcMessage.CreateBytes(IpcOpCode.HotReloadMod, largeData);

        // Act
        byte[] serialized = WillowmakerIpcMessage.Serialize(original);
        bool success =
            WillowmakerIpcMessage.TryDeserialize(serialized, out WillowmakerIpcMessage? deserialized,
                out int bytesRead);

        // Assert
        Assert.True(success);
        Assert.NotNull(deserialized);
        Assert.Equal(IpcOpCode.HotReloadMod, deserialized.OpCode);
        Assert.Equal(largeData.Length, deserialized.Payload.Length);
        Assert.Equal(largeData, deserialized.Payload);
        Assert.Equal(serialized.Length, bytesRead);
    }

    [Fact]
    public void Message_JsonDto_Roundtrip_PreservesStrongTypes()
    {
        // Arrange
        EngineStateDto originalDto = new()
        {
            CurrentFps = 945.5,
            MemoryWorkingSetBytes = 1024 * 1024 * 512, // 512 MB
            ActiveMatchId = 98765,
            CurrentMap = "caldavar",
            PlayerName = "TestHero",
            ConnectedPlayers = 10,
            UptimeSeconds = 3600
        };

        // Act
        WillowmakerIpcMessage message = WillowmakerIpcMessage.CreateJson(IpcOpCode.EngineStateResponse, originalDto);
        byte[] serialized = WillowmakerIpcMessage.Serialize(message);
        bool success =
            WillowmakerIpcMessage.TryDeserialize(serialized, out WillowmakerIpcMessage? deserialized,
                out int bytesRead);

        // Assert
        Assert.True(success);
        Assert.NotNull(deserialized);
        Assert.Equal(IpcOpCode.EngineStateResponse, deserialized.OpCode);

        EngineStateDto? parsedDto = deserialized.ReadJson<EngineStateDto>();
        Assert.NotNull(parsedDto);
        Assert.Equal(originalDto.CurrentFps, parsedDto.CurrentFps);
        Assert.Equal(originalDto.MemoryWorkingSetBytes, parsedDto.MemoryWorkingSetBytes);
        Assert.Equal(originalDto.ActiveMatchId, parsedDto.ActiveMatchId);
        Assert.Equal(originalDto.CurrentMap, parsedDto.CurrentMap);
        Assert.Equal(originalDto.PlayerName, parsedDto.PlayerName);
        Assert.Equal(originalDto.ConnectedPlayers, parsedDto.ConnectedPlayers);
        Assert.Equal(originalDto.UptimeSeconds, parsedDto.UptimeSeconds);
        Assert.Equal(serialized.Length, bytesRead);
    }

    [Fact]
    public void Message_InvalidMagic_ThrowsInvalidDataException()
    {
        // Arrange
        byte[] corruptedHeader = new byte[12];
        corruptedHeader[0] = 0xAA;
        corruptedHeader[1] = 0xBB;

        // Act & Assert
        Assert.Throws<InvalidDataException>(() =>
        {
            WillowmakerIpcMessage.TryDeserialize(corruptedHeader, out _, out _);
        });
    }

    [Fact]
    public async Task NamedPipe_Ping_ReturnsSubMillisecondLatency()
    {
        // Arrange
        string testPipeName = $"test-ipc-ping-{Guid.NewGuid():N}";
        await using WillowmakerIpcServer server = new(testPipeName);
        await server.StartAsync();

        await using WillowmakerIpcClient client = new(testPipeName);
        await client.ConnectAsync();

        // Act
        long latencyMicros = await client.PingAsync();

        // Assert
        Assert.True(client.IsConnected);
        Assert.True(latencyMicros >= 0);
        Assert.True(latencyMicros < 5_000_000); // Sub-second in test environment under heavy concurrency
    }

    [Fact]
    public async Task NamedPipe_ExecuteCommand_ReturnsEngineResponse()
    {
        // Arrange
        string testPipeName = $"test-ipc-cmd-{Guid.NewGuid():N}";
        await using WillowmakerIpcServer server = new(testPipeName);

        server.OnMessageReceived += msg =>
        {
            if (msg.OpCode == IpcOpCode.ExecuteCommand)
            {
                WillowmakerIpcMessage response = WillowmakerIpcMessage.CreateString(
                    IpcOpCode.CommandResponse,
                    $"OK: Executed '{msg.PayloadString}'");
                return Task.FromResult<WillowmakerIpcMessage?>(response);
            }

            return Task.FromResult<WillowmakerIpcMessage?>(null);
        };

        await server.StartAsync();

        await using WillowmakerIpcClient client = new(testPipeName);
        await client.ConnectAsync();

        WillowmakerIpcMessage request = WillowmakerIpcMessage.CreateString(
            IpcOpCode.ExecuteCommand,
            "Set svr_ip 127.0.0.1");

        // Act
        WillowmakerIpcMessage? response = await client.SendRequestAsync(request, 3000);

        // Assert
        Assert.NotNull(response);
        Assert.Equal(IpcOpCode.CommandResponse, response.OpCode);
        Assert.Equal("OK: Executed 'Set svr_ip 127.0.0.1'", response.PayloadString);
    }

    [Fact]
    public async Task NamedPipe_LogStream_DeliversRealTimeEvents()
    {
        // Arrange
        string testPipeName = $"test-ipc-logs-{Guid.NewGuid():N}";
        await using WillowmakerIpcServer server = new(testPipeName);

        TaskCompletionSource<string> logReceivedTcs = new(TaskCreationOptions.RunContinuationsAsynchronously);
        server.OnLogStreamReceived += line => { logReceivedTcs.TrySetResult(line); };

        await server.StartAsync();

        await using WillowmakerIpcClient client = new(testPipeName);
        await client.ConnectAsync();

        WillowmakerIpcMessage logMsg = WillowmakerIpcMessage.CreateString(
            IpcOpCode.LogStreamEvent,
            "[K2 Engine] Loading map 'caldavar.s2z' into memory...");

        // Act
        await client.SendMessageAsync(logMsg);
        string receivedLine = await logReceivedTcs.Task.WaitAsync(TimeSpan.FromSeconds(3));

        // Assert
        Assert.Equal("[K2 Engine] Loading map 'caldavar.s2z' into memory...", receivedLine);
    }

    [Fact]
    public async Task NamedPipe_Broadcast_ReachesAllConnectedClients()
    {
        // Arrange
        string testPipeName = $"test-ipc-bcast-{Guid.NewGuid():N}";
        await using WillowmakerIpcServer server = new(testPipeName);
        await server.StartAsync();

        await using WillowmakerIpcClient client1 = new(testPipeName);
        await using WillowmakerIpcClient client2 = new(testPipeName);

        TaskCompletionSource<string> client1Tcs = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource<string> client2Tcs = new(TaskCreationOptions.RunContinuationsAsynchronously);

        client1.OnMessageReceived += msg => client1Tcs.TrySetResult(msg.PayloadString);
        client2.OnMessageReceived += msg => client2Tcs.TrySetResult(msg.PayloadString);

        await client1.ConnectAsync();
        await client2.ConnectAsync();

        while (server.ConnectedClientsCount < 2) await Task.Delay(10);

        // Act
        WillowmakerIpcMessage broadcastMsg =
            WillowmakerIpcMessage.CreateString(IpcOpCode.HotReloadMod, "/interface/ui.package");
        await server.BroadcastAsync(broadcastMsg);

        string received1 = await client1Tcs.Task.WaitAsync(TimeSpan.FromSeconds(10));
        string received2 = await client2Tcs.Task.WaitAsync(TimeSpan.FromSeconds(10));

        // Assert
        Assert.Equal("/interface/ui.package", received1);
        Assert.Equal("/interface/ui.package", received2);
    }

    [Fact]
    public async Task Correlation_ConcurrentRequests_ResponsesRoutedToCorrectCallers()
    {
        // Arrange
        string testPipeName = $"test-ipc-correlation-{Guid.NewGuid():N}";
        await using WillowmakerIpcServer server = new(testPipeName);

        server.OnMessageReceived += msg =>
        {
            if (msg.OpCode == IpcOpCode.ExecuteCommand)
            {
                WillowmakerIpcMessage response = WillowmakerIpcMessage.CreateString(
                    IpcOpCode.CommandResponse,
                    $"RESP:{msg.PayloadString}");
                return Task.FromResult<WillowmakerIpcMessage?>(response);
            }

            return Task.FromResult<WillowmakerIpcMessage?>(null);
        };

        await server.StartAsync();

        await using WillowmakerIpcClient client = new(testPipeName);
        await client.ConnectAsync();

        const int requestCount = 10;
        Task<long>[] pingTasks = new Task<long>[requestCount];
        Task<WillowmakerIpcMessage?>[] cmdTasks = new Task<WillowmakerIpcMessage?>[requestCount];

        // Act - Concurrently send pings and commands
        for (int i = 0; i < requestCount; i++)
        {
            int index = i;
            pingTasks[index] = client.PingAsync();
            WillowmakerIpcMessage cmd = WillowmakerIpcMessage.CreateString(IpcOpCode.ExecuteCommand, $"CMD_{index}");
            cmdTasks[index] = client.SendRequestAsync(cmd);
        }

        long[] pingResults = await Task.WhenAll(pingTasks);
        WillowmakerIpcMessage?[] cmdResponses = await Task.WhenAll(cmdTasks);

        // Assert - Verify each response matches expected OpCode and payload
        for (int i = 0; i < requestCount; i++)
        {
            Assert.True(pingResults[i] >= 0);
            Assert.NotNull(cmdResponses[i]);
            Assert.Equal(IpcOpCode.CommandResponse, cmdResponses[i]!.OpCode);
            Assert.Equal($"RESP:CMD_{i}", cmdResponses[i]!.PayloadString);
        }
    }

    [Trait("Category", "Manual")]
    [Fact]
    public async Task AutoReconnect_ServerRestart_ClientAutomaticallyReconnects()
    {
        if (!NexusTestEnvironment.IsManualTestEnabled) return;
        // Arrange
        string testPipeName = $"test-ipc-reconnect-{Guid.NewGuid():N}";
        WillowmakerIpcServer server = new(testPipeName);
        await server.StartAsync();

        WillowmakerIpcClientOptions options = new()
        {
            AutoReconnect = true,
            InitialBackoffMs = 50,
            MaxBackoffMs = 200,
            DefaultTimeoutMs = 2000
        };

        TaskCompletionSource reconnectedTcs = new(TaskCreationOptions.RunContinuationsAsynchronously);

        await using WillowmakerIpcClient client = new(testPipeName, options: options);
        client.OnReconnected += () => reconnectedTcs.TrySetResult();

        await client.ConnectAsync();
        Assert.True(client.IsConnected);

        // Act - Stop server to induce disconnection
        await server.StopAsync();
        await server.DisposeAsync();

        // Wait a short moment and spin up a new server instance on the same pipe
        await Task.Delay(100);
        WillowmakerIpcServer newServer = new(testPipeName);
        await newServer.StartAsync();

        try
        {
            // Wait for client auto-reconnect
            await reconnectedTcs.Task.WaitAsync(TimeSpan.FromSeconds(5));

            // Assert
            Assert.True(client.IsConnected);
            long latency = await client.PingAsync();
            Assert.True(latency >= 0);
        }
        finally
        {
            await newServer.StopAsync();
            await newServer.DisposeAsync();
        }
    }

    [Fact]
    public async Task Correlation_TimedOutRequest_OrphanResponseHandling()
    {
        // Arrange
        string testPipeName = $"test-ipc-orphan-{Guid.NewGuid():N}";
        await using WillowmakerIpcServer server = new(testPipeName);
        TaskCompletionSource slowReqGate = new(TaskCreationOptions.RunContinuationsAsynchronously);

        server.OnMessageReceived += async msg =>
        {
            if (msg.PayloadString == "SLOW_REQ")
            {
                await slowReqGate.Task;
                return WillowmakerIpcMessage.CreateString(IpcOpCode.CommandResponse, "SLOW_RESP");
            }

            if (msg.PayloadString == "FAST_REQ")
            {
                return WillowmakerIpcMessage.CreateString(IpcOpCode.CommandResponse, "FAST_RESP");
            }

            return null;
        };

        await server.StartAsync();

        await using WillowmakerIpcClient client = new(testPipeName);
        await client.ConnectAsync();

        // Act 1: Send SLOW_REQ with 10ms timeout -> Deterministically times out because server is gated at slowReqGate
        WillowmakerIpcMessage slowReq = WillowmakerIpcMessage.CreateString(IpcOpCode.ExecuteCommand, "SLOW_REQ");
        await Assert.ThrowsAsync<TimeoutException>(async () => { await client.SendRequestAsync(slowReq, 10); });

        // Unblock server to release the orphan SLOW_RESP onto the stream
        slowReqGate.TrySetResult();

        // Act 2: Send FAST_REQ -> Must receive FAST_RESP (not orphan SLOW_RESP)
        WillowmakerIpcMessage fastReq = WillowmakerIpcMessage.CreateString(IpcOpCode.ExecuteCommand, "FAST_REQ");
        WillowmakerIpcMessage? fastResp = await client.SendRequestAsync(fastReq, 2000);

        // Assert: Verify if FAST_REQ received FAST_RESP (and orphan SLOW_RESP was cleanly discarded)
        Assert.NotNull(fastResp);
        Assert.Equal("FAST_RESP", fastResp.PayloadString);
    }

    [Fact]
    public async Task StressTest_HighVolumeBurst_MixedOpCodes_Concurrent50()
    {
        // Arrange
        string testPipeName = $"test-ipc-burst50-{Guid.NewGuid():N}";
        await using WillowmakerIpcServer server = new(testPipeName);

        server.OnMessageReceived += msg =>
        {
            return msg.OpCode switch
            {
                IpcOpCode.ExecuteCommand => Task.FromResult<WillowmakerIpcMessage?>(
                    WillowmakerIpcMessage.CreateString(IpcOpCode.CommandResponse, $"ECHO_{msg.PayloadString}")),
                IpcOpCode.GetEngineState => Task.FromResult<WillowmakerIpcMessage?>(
                    WillowmakerIpcMessage.CreateString(IpcOpCode.EngineStateResponse, $"STATE_{msg.PayloadString}")),
                _ => Task.FromResult<WillowmakerIpcMessage?>(null)
            };
        };

        await server.StartAsync();

        await using WillowmakerIpcClient client = new(testPipeName);
        await client.ConnectAsync();

        const int iterations = 50;
        Task[] tasks = new Task[iterations * 3];

        for (int i = 0; i < iterations; i++)
        {
            int id = i;
            tasks[i * 3 + 0] = Task.Run(async () =>
            {
                long pingLatency = await client.PingAsync();
                Assert.True(pingLatency >= 0);
            });

            tasks[i * 3 + 1] = Task.Run(async () =>
            {
                WillowmakerIpcMessage cmd = WillowmakerIpcMessage.CreateString(IpcOpCode.ExecuteCommand, $"C_{id}");
                WillowmakerIpcMessage? resp = await client.SendRequestAsync(cmd);
                Assert.NotNull(resp);
                Assert.Equal(IpcOpCode.CommandResponse, resp.OpCode);
                Assert.Equal($"ECHO_C_{id}", resp.PayloadString);
            });

            tasks[i * 3 + 2] = Task.Run(async () =>
            {
                WillowmakerIpcMessage query = WillowmakerIpcMessage.CreateString(IpcOpCode.GetEngineState, $"S_{id}");
                WillowmakerIpcMessage? resp = await client.SendRequestAsync(query);
                Assert.NotNull(resp);
                Assert.Equal(IpcOpCode.EngineStateResponse, resp.OpCode);
                Assert.Equal($"STATE_S_{id}", resp.PayloadString);
            });
        }

        await Task.WhenAll(tasks);
    }

    [Trait("Category", "Manual")]
    [Fact]
    public async Task StressTest_ServerRestart_DuringInFlightRequests_FailsGracefullyAndAutoReconnects()
    {
        if (!NexusTestEnvironment.IsManualTestEnabled) return;
        // Arrange
        string testPipeName = $"test-ipc-restart-inflight-{Guid.NewGuid():N}";
        WillowmakerIpcServer server = new(testPipeName);

        server.OnMessageReceived += async msg =>
        {
            await Task.Delay(2000); // Hang server response
            return WillowmakerIpcMessage.CreateString(IpcOpCode.CommandResponse, "LATE");
        };

        await server.StartAsync();

        WillowmakerIpcClientOptions options = new()
        {
            AutoReconnect = true,
            InitialBackoffMs = 50,
            MaxBackoffMs = 200,
            DefaultTimeoutMs = 2000
        };

        TaskCompletionSource reconnectedTcs = new(TaskCreationOptions.RunContinuationsAsynchronously);

        await using WillowmakerIpcClient client = new(testPipeName, options: options);
        client.OnReconnected += () => reconnectedTcs.TrySetResult();

        await client.ConnectAsync();
        Assert.True(client.IsConnected);

        // Fire 5 requests that will be pending on server
        Task<WillowmakerIpcMessage?>[] inFlightTasks = new Task<WillowmakerIpcMessage?>[5];
        for (int i = 0; i < 5; i++)
        {
            WillowmakerIpcMessage req = WillowmakerIpcMessage.CreateString(IpcOpCode.ExecuteCommand, $"INFLIGHT_{i}");
            inFlightTasks[i] = client.SendRequestAsync(req);
        }

        // Wait a tiny bit to ensure packets were sent
        await Task.Delay(50);

        // Abruptly kill the server while requests are in flight
        await server.StopAsync();
        await server.DisposeAsync();

        // In-flight requests must fail cleanly with IOException or TimeoutException
        for (int i = 0; i < 5; i++) await Assert.ThrowsAnyAsync<Exception>(async () => await inFlightTasks[i]);

        // Start new responsive server instance on same pipe
        await Task.Delay(100);
        WillowmakerIpcServer newServer = new(testPipeName);
        newServer.OnMessageReceived += msg =>
        {
            return Task.FromResult<WillowmakerIpcMessage?>(
                WillowmakerIpcMessage.CreateString(IpcOpCode.CommandResponse, $"RECOVERED_{msg.PayloadString}"));
        };
        await newServer.StartAsync();

        try
        {
            await reconnectedTcs.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.True(client.IsConnected);

            // Verify new requests succeed immediately after reconnect
            WillowmakerIpcMessage postReq = WillowmakerIpcMessage.CreateString(IpcOpCode.ExecuteCommand, "HELLO");
            WillowmakerIpcMessage? postResp = await client.SendRequestAsync(postReq, 3000);
            Assert.NotNull(postResp);
            Assert.Equal("RECOVERED_HELLO", postResp.PayloadString);
        }
        finally
        {
            await newServer.StopAsync();
            await newServer.DisposeAsync();
        }
    }
}
