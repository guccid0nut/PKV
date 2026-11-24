// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

using IpcAuthPayload = NEXUS.Contracts.Ipc.IpcAuthPayload;
using IpcAuthResponseDto = NEXUS.Contracts.Ipc.IpcAuthResponseDto;
using WILLOWMAKER.Core.Services;
using WILLOWMAKER.Core.Services.IPC;
using Xunit;

namespace WILLOWMAKER.Tests;

public sealed class WillowmakerIpcServiceTests
{
    [Fact]
    public void Message_Serialization_Roundtrip_PreservesOpCodeAndPayload()
    {
        // Arrange
        WillowmakerIpcMessage original = WillowmakerIpcMessage.CreateString(
            IpcOpCode.ExecuteCommand,
            "Set host_maxFPS 1000");

        // Act
        byte[] serialized = WillowmakerIpcMessage.Serialize(original);
        bool success =
            WillowmakerIpcMessage.TryDeserialize(serialized, out WillowmakerIpcMessage? deserialized,
                out int bytesRead);

        // Assert
        Assert.True(success);
        Assert.NotNull(deserialized);
        Assert.Equal(IpcOpCode.ExecuteCommand, deserialized.OpCode);
        Assert.Equal("Set host_maxFPS 1000", deserialized.PayloadString);
        Assert.Equal(serialized.Length, bytesRead);
    }

    [Fact]
    public void Message_JsonDto_Roundtrip_PreservesTelemetryProperties()
    {
        // Arrange
        EngineStateDto originalState = new()
        {
            CurrentFps = 1000.0,
            MemoryWorkingSetBytes = 1024 * 1024 * 768,
            ActiveMatchId = 12345,
            CurrentMap = "caldavar",
            PlayerName = "TestPlayer",
            ConnectedPlayers = 10,
            UptimeSeconds = 120
        };

        // Act
        WillowmakerIpcMessage message = WillowmakerIpcMessage.CreateJson(IpcOpCode.EngineStateResponse, originalState);
        byte[] serialized = WillowmakerIpcMessage.Serialize(message);
        bool success =
            WillowmakerIpcMessage.TryDeserialize(serialized, out WillowmakerIpcMessage? deserialized,
                out int bytesRead);

        // Assert
        Assert.True(success);
        Assert.NotNull(deserialized);
        Assert.Equal(IpcOpCode.EngineStateResponse, deserialized.OpCode);

        EngineStateDto? parsedState = deserialized.ReadJson<EngineStateDto>();
        Assert.NotNull(parsedState);
        Assert.Equal(originalState.CurrentFps, parsedState.CurrentFps);
        Assert.Equal(originalState.MemoryWorkingSetBytes, parsedState.MemoryWorkingSetBytes);
        Assert.Equal(originalState.ActiveMatchId, parsedState.ActiveMatchId);
        Assert.Equal(originalState.CurrentMap, parsedState.CurrentMap);
        Assert.Equal(originalState.PlayerName, parsedState.PlayerName);
        Assert.Equal(originalState.ConnectedPlayers, parsedState.ConnectedPlayers);
        Assert.Equal(originalState.UptimeSeconds, parsedState.UptimeSeconds);
        Assert.Equal(serialized.Length, bytesRead);
    }

    [Fact]
    public async Task IpcService_ServerStartupAndShutdown_LifecycleWorks()
    {
        // Arrange
        string testPipe = $"test-willowmaker-lifecycle-{Guid.NewGuid():N}";
        await using WillowmakerIpcService service = new(testPipe);

        // Act
        await service.StartServerAsync();

        // Assert
        Assert.True(service.IsRunning);
        Assert.Equal(testPipe, service.PipeName);

        // Stop
        await service.StopServerAsync();
        Assert.False(service.IsRunning);
    }

    [Fact]
    public async Task IpcService_ClientConnectAndPing_ReturnsPong()
    {
        // Arrange
        string testPipe = $"test-willowmaker-ping-{Guid.NewGuid():N}";
        await using WillowmakerIpcService service = new(testPipe);
        await service.StartServerAsync();

        await using WillowmakerIpcClient client = new(testPipe);
        await client.ConnectAsync();

        // Act
        long latencyMicros = await client.PingAsync();

        // Assert
        Assert.True(client.IsConnected);
        Assert.True(latencyMicros >= 0);
        Assert.True(latencyMicros < 5_000_000);
    }

    [Fact]
    public async Task IpcService_LogStream_ForwardsClientLogsToEvent()
    {
        // Arrange
        string testPipe = $"test-willowmaker-logs-{Guid.NewGuid():N}";
        await using WillowmakerIpcService service = new(testPipe);

        TaskCompletionSource<string> logReceivedTcs = new(TaskCreationOptions.RunContinuationsAsynchronously);
        service.OnLogStreamReceived += line => logReceivedTcs.TrySetResult(line);

        await service.StartServerAsync();

        await using WillowmakerIpcClient client = new(testPipe);
        await client.ConnectAsync();

        WillowmakerIpcMessage logMsg = WillowmakerIpcMessage.CreateString(
            IpcOpCode.LogStreamEvent,
            "[K2 Engine] Initialized Vulkan 1.3 Driver (Volk direct ICD binding active).");

        // Act
        await client.SendMessageAsync(logMsg);
        string receivedLog = await logReceivedTcs.Task.WaitAsync(TimeSpan.FromSeconds(3));

        // Assert
        Assert.Equal("[K2 Engine] Initialized Vulkan 1.3 Driver (Volk direct ICD binding active).", receivedLog);
    }

    [Fact]
    public async Task IpcService_BroadcastHotReload_DispatchesManifestToClients()
    {
        // Arrange
        string testPipe = $"test-willowmaker-hotreload-{Guid.NewGuid():N}";
        await using WillowmakerIpcService service = new(testPipe);
        await service.StartServerAsync();

        await using WillowmakerIpcClient client = new(testPipe);

        TaskCompletionSource<HotReloadManifest?> manifestReceivedTcs =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        client.OnMessageReceived += msg =>
        {
            if (msg.OpCode == IpcOpCode.HotReloadMod)
                manifestReceivedTcs.TrySetResult(msg.ReadJson<HotReloadManifest>());
        };

        await client.ConnectAsync();

        while (service.ConnectedClientsCount == 0) await Task.Delay(10);

        HotReloadManifest manifest = new()
        {
            FilePath = "override/ui/game_shop.lua",
            VfsPath = "/ui/game_shop.lua",
            Action = "modified",
            IsValid = true
        };

        // Act
        await service.BroadcastHotReloadAsync(manifest);

        HotReloadManifest? receivedManifest = await manifestReceivedTcs.Task.WaitAsync(TimeSpan.FromSeconds(3));

        // Assert
        Assert.NotNull(receivedManifest);
        Assert.Equal("/ui/game_shop.lua", receivedManifest.VfsPath);
        Assert.True(receivedManifest.IsValid);
    }

    [Fact]
    public async Task IpcService_ExecuteRemoteCvar_SendsCommandAndAwaitsResponse()
    {
        // Arrange
        string testPipe = $"test-willowmaker-cvar-{Guid.NewGuid():N}";
        await using WillowmakerIpcService service = new(testPipe);
        await service.StartServerAsync();

        await using WillowmakerIpcClient client = new(testPipe);

        client.OnMessageReceived += msg =>
        {
            if (msg.OpCode == IpcOpCode.ExecuteCommand)
            {
                WillowmakerIpcMessage responseMsg = WillowmakerIpcMessage.CreateString(
                    IpcOpCode.CommandResponse,
                    $"Cvar '{msg.PayloadString}' set to 1000");
                _ = client.SendMessageAsync(responseMsg);
            }
        };

        await client.ConnectAsync();

        while (service.ConnectedClientsCount == 0) await Task.Delay(10);

        // Act
        string result = await service.ExecuteRemoteCvarAsync("Set host_maxFPS 1000", 3000);

        // Assert
        Assert.Equal("Cvar 'Set host_maxFPS 1000' set to 1000", result);
    }

    [Fact]
    public async Task Correlation_ConcurrentRequests_ResponsesRoutedToCorrectCallers()
    {
        // Arrange
        string testPipe = $"test-willowmaker-corr-{Guid.NewGuid():N}";
        await using WillowmakerIpcService service = new(testPipe);
        await service.StartServerAsync();

        await using WillowmakerIpcClient client = new(testPipe);
        await client.ConnectAsync();

        while (service.ConnectedClientsCount == 0) await Task.Delay(10);

        const int requestCount = 5;
        Task<long>[] pingTasks = new Task<long>[requestCount];

        // Act
        for (int i = 0; i < requestCount; i++) pingTasks[i] = client.PingAsync();

        long[] pingResults = await Task.WhenAll(pingTasks);

        // Assert
        for (int i = 0; i < requestCount; i++) Assert.True(pingResults[i] >= 0);
    }

    [Fact]
    public async Task AutoReconnect_ServerRestart_ClientAutomaticallyReconnects()
    {
        // Arrange
        string testPipe = $"test-willowmaker-recon-{Guid.NewGuid():N}";
        WillowmakerIpcService service = new(testPipe);
        await service.StartServerAsync();

        WillowmakerIpcClientOptions options = new()
        {
            AutoReconnect = true,
            InitialBackoffMs = 50,
            MaxBackoffMs = 200,
            DefaultTimeoutMs = 2000
        };

        TaskCompletionSource reconnectedTcs = new(TaskCreationOptions.RunContinuationsAsynchronously);

        await using WillowmakerIpcClient client = new(testPipe, options: options);
        client.OnReconnected += () => reconnectedTcs.TrySetResult();

        await client.ConnectAsync();
        Assert.True(client.IsConnected);

        // Act - Stop server
        await service.StopServerAsync();
        await service.DisposeAsync();

        // Spin up new server instance
        await Task.Delay(100);
        WillowmakerIpcService newService = new(testPipe);
        await newService.StartServerAsync();

        try
        {
            await reconnectedTcs.Task.WaitAsync(TimeSpan.FromSeconds(5));

            // Assert
            Assert.True(client.IsConnected);
            long latency = await client.PingAsync();
            Assert.True(latency >= 0);
        }
        finally
        {
            await newService.StopServerAsync();
            await newService.DisposeAsync();
        }
    }

    [Fact]
    public void IpcMessage_LegacyHeader_DeserializesCorrectly()
    {
        // 8-byte legacy header: [Magic: 0x574D (2B)][OpCode: (2B)][Length: (4B)][Payload]
        ushort magic = 0x574D;
        ushort opCode = (ushort)IpcOpCode.ExecuteCommand;
        byte[] payload = System.Text.Encoding.UTF8.GetBytes("TestLegacyPayload");
        int length = payload.Length;

        byte[] buffer = new byte[WillowmakerIpcMessage.LegacyHeaderSize + length];
        BitConverter.GetBytes(magic).CopyTo(buffer, 0);
        BitConverter.GetBytes(opCode).CopyTo(buffer, 2);
        BitConverter.GetBytes(length).CopyTo(buffer, 4);
        payload.CopyTo(buffer, 8);

        bool success = WillowmakerIpcMessage.TryDeserializeAny(buffer, out WillowmakerIpcMessage? message, out int bytesRead);

        Assert.True(success);
        Assert.NotNull(message);
        Assert.Equal(IpcOpCode.ExecuteCommand, message.OpCode);
        Assert.Equal("TestLegacyPayload", message.PayloadString);
        Assert.Equal(buffer.Length, bytesRead);
    }

    [Fact]
    public void IpcMessage_TextLine_DeserializesAsExecuteCommand()
    {
        string commandLine = "Set host_maxFPS 240\n";
        byte[] buffer = System.Text.Encoding.UTF8.GetBytes(commandLine);

        bool success = WillowmakerIpcMessage.TryDeserializeAny(buffer, out WillowmakerIpcMessage? message, out int bytesRead);

        Assert.True(success);
        Assert.NotNull(message);
        Assert.Equal(IpcOpCode.ExecuteCommand, message.OpCode);
        Assert.Equal("Set host_maxFPS 240", message.PayloadString);
        Assert.Equal(buffer.Length, bytesRead);
    }

    [Fact]
    public void IpcService_BufferOverflow_RejectsOversizedAccumulatedStream()
    {
        byte[] oversized = new byte[WillowmakerIpcMessage.MaxPayloadSize + 10];
        Array.Fill(oversized, (byte)'a');

        Assert.Throws<InvalidDataException>(() =>
        {
            WillowmakerIpcMessage.TryDeserializeLine(oversized, out _, out _);
        });
    }

    [Fact]
    public async Task IpcService_ClientDisconnect_FaultsPendingRequestsImmediately()
    {
        string testPipe = $"test-willowmaker-disconnect-{Guid.NewGuid():N}";
        await using WillowmakerIpcService service = new(testPipe);
        await service.StartServerAsync();

        WillowmakerIpcClient client = new(testPipe);
        await client.ConnectAsync();

        while (service.ConnectedClientsCount == 0) await Task.Delay(10);

        TaskCompletionSource clientMsgReceived = new(TaskCreationOptions.RunContinuationsAsynchronously);
        client.OnMessageReceived += msg =>
        {
            if (msg.OpCode == IpcOpCode.ExecuteCommand)
                clientMsgReceived.TrySetResult();
        };

        Task<string> serverReqTask = Task.Run(() => service.ExecuteRemoteCvarAsync("Set test 1", 5000));
        await clientMsgReceived.Task.WaitAsync(TimeSpan.FromSeconds(3));

        await client.DisposeAsync();

        Exception ex = await Assert.ThrowsAnyAsync<Exception>(() => serverReqTask);
        Assert.True(ex is IOException or ObjectDisposedException, $"Expected IOException or ObjectDisposedException on pipe disconnect, got {ex.GetType().FullName}");
    }

    [Fact]
    public async Task IpcService_ConcurrentMultiplexedRequests_RequestIdCorrelation()
    {
        string testPipe = $"test-willowmaker-multiplex-{Guid.NewGuid():N}";
        await using WillowmakerIpcService service = new(testPipe);
        await service.StartServerAsync();

        await using WillowmakerIpcClient client = new(testPipe);

        List<WillowmakerIpcMessage> receivedRequests = [];
        client.OnMessageReceived += msg =>
        {
            if (msg.OpCode == IpcOpCode.ExecuteCommand)
            {
                lock (receivedRequests)
                {
                    receivedRequests.Add(msg);
                }
            }
        };

        await client.ConnectAsync();
        while (service.ConnectedClientsCount == 0) await Task.Delay(10);

        Task<string> req1 = Task.Run(() => service.ExecuteRemoteCvarAsync("cmd1", 3000));
        Task<string> req2 = Task.Run(() => service.ExecuteRemoteCvarAsync("cmd2", 3000));

        while (true)
        {
            lock (receivedRequests)
            {
                if (receivedRequests.Count == 2) break;
            }
            await Task.Delay(10);
        }

        WillowmakerIpcMessage reqMsg1, reqMsg2;
        lock (receivedRequests)
        {
            reqMsg1 = receivedRequests.First(r => r.PayloadString == "cmd1");
            reqMsg2 = receivedRequests.First(r => r.PayloadString == "cmd2");
        }

        // Reply to req2 first, then req1 out-of-order
        await client.SendMessageAsync(WillowmakerIpcMessage.CreateString(IpcOpCode.CommandResponse, "resp2", reqMsg2.RequestId));
        await client.SendMessageAsync(WillowmakerIpcMessage.CreateString(IpcOpCode.CommandResponse, "resp1", reqMsg1.RequestId));

        string res1 = await req1;
        string res2 = await req2;

        Assert.Equal("resp1", res1);
        Assert.Equal("resp2", res2);
    }

    [Fact]
    public async Task IpcClient_AutoReconnect_ReauthenticatesSession()
    {
        string testPipe = $"test-willowmaker-auth-recon-{Guid.NewGuid():N}";
        string secretKey = "super-secret-auth-key";

        WillowmakerIpcService service = new(testPipe, authKey: secretKey, sandboxMode: NEXUS.Contracts.Ipc.IpcSandboxMode.Enforced);
        await service.StartServerAsync();

        WillowmakerIpcClientOptions options = new()
        {
            AutoReconnect = true,
            InitialBackoffMs = 50,
            MaxBackoffMs = 200,
            DefaultTimeoutMs = 2000
        };

        TaskCompletionSource reconnectedTcs = new(TaskCreationOptions.RunContinuationsAsynchronously);

        await using WillowmakerIpcClient client = new(testPipe, options: options, authKey: secretKey);
        client.OnReconnected += () => reconnectedTcs.TrySetResult();

        await client.ConnectAsync();
        Assert.True(client.IsConnected);
        Assert.True(client.IsAuthenticated);

        // Restart server
        await service.StopServerAsync();
        await service.DisposeAsync();

        await Task.Delay(100);
        WillowmakerIpcService newService = new(testPipe, authKey: secretKey, sandboxMode: NEXUS.Contracts.Ipc.IpcSandboxMode.Enforced);
        await newService.StartServerAsync();

        try
        {
            await reconnectedTcs.Task.WaitAsync(TimeSpan.FromSeconds(5));

            Assert.True(client.IsConnected);
            Assert.True(client.IsAuthenticated);

            long latency = await client.PingAsync();
            Assert.True(latency >= 0);
        }
        finally
        {
            await newService.StopServerAsync();
            await newService.DisposeAsync();
        }
    }

    [Fact]
    public void IpcMessage_BackToBackLegacyFrames_DeserializesWithoutDesynchronization()
    {
        // Construct 2 back-to-back 8B legacy frames: Frame1 (18B) + Frame2 (18B) = 36B
        byte[] payload1 = System.Text.Encoding.UTF8.GetBytes("LegacyCmd1");
        byte[] payload2 = System.Text.Encoding.UTF8.GetBytes("LegacyCmd2");

        byte[] buffer = new byte[(WillowmakerIpcMessage.LegacyHeaderSize + payload1.Length) * 2];

        // Frame 1
        BitConverter.GetBytes(WillowmakerIpcMessage.HeaderMagic).CopyTo(buffer, 0);
        BitConverter.GetBytes((ushort)IpcOpCode.ExecuteCommand).CopyTo(buffer, 2);
        BitConverter.GetBytes(payload1.Length).CopyTo(buffer, 4);
        payload1.CopyTo(buffer, 8);

        // Frame 2
        int offset2 = WillowmakerIpcMessage.LegacyHeaderSize + payload1.Length;
        BitConverter.GetBytes(WillowmakerIpcMessage.HeaderMagic).CopyTo(buffer, offset2);
        BitConverter.GetBytes((ushort)IpcOpCode.ExecuteCommand).CopyTo(buffer, offset2 + 2);
        BitConverter.GetBytes(payload2.Length).CopyTo(buffer, offset2 + 4);
        payload2.CopyTo(buffer, offset2 + 8);

        // Read Frame 1
        bool success1 = WillowmakerIpcMessage.TryDeserializeAny(buffer, out WillowmakerIpcMessage? msg1, out int bytesRead1);
        Assert.True(success1);
        Assert.NotNull(msg1);
        Assert.Equal("LegacyCmd1", msg1.PayloadString);
        Assert.Equal(offset2, bytesRead1);

        // Read Frame 2 from remainder
        ReadOnlySpan<byte> remainder = buffer.AsSpan(bytesRead1);
        bool success2 = WillowmakerIpcMessage.TryDeserializeAny(remainder, out WillowmakerIpcMessage? msg2, out int bytesRead2);
        Assert.True(success2);
        Assert.NotNull(msg2);
        Assert.Equal("LegacyCmd2", msg2.PayloadString);
        Assert.Equal(offset2, bytesRead2);
    }

    [Fact]
    public async Task IpcService_MultipleClients_TargetClientDisconnectFaultsImmediately()
    {
        string testPipe = $"test-willowmaker-multiclient-{Guid.NewGuid():N}";
        await using WillowmakerIpcService service = new(testPipe);
        await service.StartServerAsync();

        await using WillowmakerIpcClient client1 = new(testPipe);
        await client1.ConnectAsync();

        await using WillowmakerIpcClient client2 = new(testPipe);
        await client2.ConnectAsync();

        while (service.ConnectedClientsCount < 2) await Task.Delay(10);

        WillowmakerIpcClient? targetedClient = null;
        TaskCompletionSource msgReceived = new(TaskCreationOptions.RunContinuationsAsynchronously);
        client1.OnMessageReceived += msg =>
        {
            if (msg.OpCode == IpcOpCode.ExecuteCommand)
            {
                targetedClient = client1;
                msgReceived.TrySetResult();
            }
        };
        client2.OnMessageReceived += msg =>
        {
            if (msg.OpCode == IpcOpCode.ExecuteCommand)
            {
                targetedClient = client2;
                msgReceived.TrySetResult();
            }
        };

        // Target client by executing remote cvar (server targets active client)
        Task<string> reqTask = Task.Run(() => service.ExecuteRemoteCvarAsync("Set test 1", 5000));
        await msgReceived.Task.WaitAsync(TimeSpan.FromSeconds(3));
        Assert.NotNull(targetedClient);

        // Disconnect whichever client was targeted; other client remains connected
        await targetedClient.DisposeAsync();

        // Pending request must fault immediately with IOException (or ObjectDisposedException if pipe closed during FlushAsync) despite other client being connected
        Exception ex = await Assert.ThrowsAnyAsync<Exception>(() => reqTask);
        Assert.True(ex is IOException or ObjectDisposedException, $"Expected IOException or ObjectDisposedException on pipe disconnect, got {ex.GetType().FullName}");
        Assert.True(service.ConnectedClientsCount >= 1);
    }

    [Fact]
    public void IpcMessage_CreateAndReadMemoryPack_RoundtripsWithZeroLoss()
    {
        // 1. EngineStateDto
        EngineStateDto state = new()
        {
            CurrentFps = 144.5,
            MemoryWorkingSetBytes = 1024 * 1024 * 512,
            ActiveMatchId = 98765,
            CurrentMap = "caldavar",
            PlayerName = "LegionCommander",
            ConnectedPlayers = 10,
            UptimeSeconds = 3600
        };

        WillowmakerIpcMessage stateMsg = WillowmakerIpcMessage.CreateMemoryPack(IpcOpCode.EngineStateResponse, state, 42);
        byte[] serializedState = WillowmakerIpcMessage.Serialize(stateMsg);
        bool deserializedStateOk = WillowmakerIpcMessage.TryDeserialize(serializedState, out WillowmakerIpcMessage? parsedStateMsg, out _);

        Assert.True(deserializedStateOk);
        Assert.NotNull(parsedStateMsg);
        Assert.Equal(IpcOpCode.EngineStateResponse, parsedStateMsg.OpCode);
        Assert.Equal(42u, parsedStateMsg.RequestId);

        EngineStateDto? roundtripState = parsedStateMsg.ReadMemoryPack<EngineStateDto>();
        Assert.NotNull(roundtripState);
        Assert.Equal(state.CurrentFps, roundtripState.CurrentFps);
        Assert.Equal(state.MemoryWorkingSetBytes, roundtripState.MemoryWorkingSetBytes);
        Assert.Equal(state.ActiveMatchId, roundtripState.ActiveMatchId);
        Assert.Equal(state.CurrentMap, roundtripState.CurrentMap);
        Assert.Equal(state.PlayerName, roundtripState.PlayerName);
        Assert.Equal(state.ConnectedPlayers, roundtripState.ConnectedPlayers);
        Assert.Equal(state.UptimeSeconds, roundtripState.UptimeSeconds);

        // 2. HotReloadManifest
        HotReloadManifest manifest = new()
        {
            FilePath = "C:/Heroes/game/ui/hud.interface",
            VfsPath = "/ui/hud.interface",
            Action = "modified",
            Timestamp = 1700000000000,
            IsValid = true,
            ErrorMessage = null
        };

        WillowmakerIpcMessage manifestMsg = WillowmakerIpcMessage.CreateMemoryPack(IpcOpCode.HotReloadMod, manifest, 101);
        HotReloadManifest? roundtripManifest = manifestMsg.ReadMemoryPack<HotReloadManifest>();
        Assert.NotNull(roundtripManifest);
        Assert.Equal(manifest.FilePath, roundtripManifest.FilePath);
        Assert.Equal(manifest.VfsPath, roundtripManifest.VfsPath);
        Assert.Equal(manifest.Action, roundtripManifest.Action);
        Assert.Equal(manifest.Timestamp, roundtripManifest.Timestamp);
        Assert.True(roundtripManifest.IsValid);

        // 3. IpcAuthPayload
        IpcAuthPayload authPayload = new()
        {
            ClientName = "WillowmakerClient",
            Nonce = "abc123nonce",
            Signature = "sig-hex-dummy",
            Timestamp = 123456789
        };

        WillowmakerIpcMessage authMsg = WillowmakerIpcMessage.CreateMemoryPack(IpcOpCode.Authenticate, authPayload, 1);
        IpcAuthPayload? roundtripAuth = authMsg.ReadMemoryPack<IpcAuthPayload>();
        Assert.NotNull(roundtripAuth);
        Assert.Equal(authPayload.ClientName, roundtripAuth.ClientName);
        Assert.Equal(authPayload.Nonce, roundtripAuth.Nonce);
        Assert.Equal(authPayload.Signature, roundtripAuth.Signature);
        Assert.Equal(authPayload.Timestamp, roundtripAuth.Timestamp);

        // 4. IpcAuthResponseDto
        IpcAuthResponseDto authResponse = new()
        {
            Authenticated = true,
            Message = "Session established",
            SessionId = "sess-42-guid"
        };

        WillowmakerIpcMessage authRespMsg = WillowmakerIpcMessage.CreateMemoryPack(IpcOpCode.AuthResponse, authResponse, 1);
        IpcAuthResponseDto? roundtripAuthResp = authRespMsg.ReadMemoryPack<IpcAuthResponseDto>();
        Assert.NotNull(roundtripAuthResp);
        Assert.True(roundtripAuthResp.Authenticated);
        Assert.Equal(authResponse.Message, roundtripAuthResp.Message);
        Assert.Equal(authResponse.SessionId, roundtripAuthResp.SessionId);
    }
}
