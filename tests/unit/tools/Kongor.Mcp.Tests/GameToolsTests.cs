// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// TAO (Together As One) MCP & Kongor CLI -- Created by guccidonut (https://github.com/guccidonut/PKV)

using System.Buffers.Binary;
using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using Kongor.Cli.Mcp.Handlers;
using Kongor.Cli.Mcp.Protocol;
using Kongor.Cli.Mcp.Services;
using Moq;
using Xunit;

namespace Kongor.Mcp.Tests;

public sealed class GameToolsTests
{
    #region 1. Launch Parameter Composition & Directory Setup Tests

    [Fact]
    public void BuildLaunchArguments_NormalMode_ComposesCanonicalArguments()
    {
        GameLaunchOptions options = new()
        {
            Mode = "normal",
            Resolution = "1920x1080",
            Fullscreen = false,
            MasterServer = "127.0.0.1:55555"
        };

        string tempDir = Path.Combine(Path.GetTempPath(), "KongorTest_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);

        try
        {
            IReadOnlyList<string> args = GameLauncherService.BuildLaunchArguments(options, tempDir);

            args.Should().Contain("-masterserver 127.0.0.1:55555");
            args.Should().Contain("-webserver 127.0.0.1:55555");
            args.Should().Contain("-messageserver 127.0.0.1:55555");
            args.Should().Contain("-pipe willowmaker-ipc");
            args.Should().Contain("-writeconsole");
            args.Should().Contain("+Set host_maxFPS 1000");
            args.Should().Contain("+Set cl_maxFPS 1000");
            args.Should().Contain("+Set vid_maxFPS 1000");
            args.Should().Contain("+Set vid_resolution 1920x1080");
            args.Should().Contain("+Set vid_width 1920");
            args.Should().Contain("+Set vid_height 1080");
            args.Should().Contain("+Set vid_fullscreen false");
            args.Should().Contain("+Set vid_borderless true");
            args.Should().Contain("+Set releaseStage_dev true");
            args.Should().Contain("+Set ui_dev true");

            string modArg = args.First(a => a.StartsWith("-mod ", StringComparison.Ordinal));
            modArg.Should().Be(ClientModMountBuilder.GetCanonicalModString());
        }
        finally
        {
            if (Directory.Exists(tempDir)) Directory.Delete(tempDir, true);
        }
    }

    [Fact]
    public void BuildLaunchArguments_PracticeMode_AddsPracticeArgument()
    {
        GameLaunchOptions options = new()
        {
            Mode = "practice",
            Resolution = "2560x1440",
            Fullscreen = true
        };

        string tempDir = Path.Combine(Path.GetTempPath(), "KongorTest_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);

        try
        {
            IReadOnlyList<string> args = GameLauncherService.BuildLaunchArguments(options, tempDir);

            args.Should().Contain(a => a.StartsWith("+StartGame practice"));
            args.Should().Contain("+Set vid_resolution 2560x1440");
            args.Should().Contain("+Set vid_width 2560");
            args.Should().Contain("+Set vid_height 1440");
            args.Should().Contain("+Set vid_fullscreen true");
            args.Should().Contain("+Set vid_borderless false");
        }
        finally
        {
            if (Directory.Exists(tempDir)) Directory.Delete(tempDir, true);
        }
    }

    [Fact]
    public void BuildLaunchArguments_BotMatchMode_AddsBotMatchArguments()
    {
        GameLaunchOptions options = new()
        {
            Mode = "bot_match",
            ConnectAddress = "127.0.0.1:11235"
        };

        string tempDir = Path.Combine(Path.GetTempPath(), "KongorTest_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);

        try
        {
            IReadOnlyList<string> args = GameLauncherService.BuildLaunchArguments(options, tempDir);

            args.Should().Contain("+Set game_botMatch true");
            args.Should().Contain("+Set svr_botMatch true");
            args.Should().Contain(a => a.StartsWith("+StartGame practice") && a.Contains("bots:true"));
            args.Should().Contain("+Connect 127.0.0.1:11235");
        }
        finally
        {
            if (Directory.Exists(tempDir)) Directory.Delete(tempDir, true);
        }
    }

    [Fact]
    public void BuildLaunchArguments_EditorMode_ConfiguresEditorModChain()
    {
        GameLaunchOptions options = new()
        {
            Mode = "editor"
        };

        string tempDir = Path.Combine(Path.GetTempPath(), "KongorTest_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);

        try
        {
            IReadOnlyList<string> args = GameLauncherService.BuildLaunchArguments(options, tempDir);

            string modArg = args.First(a => a.StartsWith("-mod ", StringComparison.Ordinal));
            modArg.Should().Contain("editor");
        }
        finally
        {
            if (Directory.Exists(tempDir)) Directory.Delete(tempDir, true);
        }
    }

    [Theory]
    [InlineData("localhost:55555", "127.0.0.1:55555")]
    [InlineData("http://localhost:5200/", "127.0.0.1:5200")]
    [InlineData("https://masterserver.newbs.io", "masterserver.newbs.io")]
    [InlineData("masterserver.newbs.io", "masterserver.newbs.io")]
    [InlineData("192.168.1.100:11031", "192.168.1.100:11031")]
    [InlineData("", "127.0.0.1:55555")]
    [InlineData(null, "127.0.0.1:55555")]
    public void NormalizeMasterServerAddress_VariousInputs_ResolvesProperly(string? input, string expected)
    {
        string actual = GameLauncherService.NormalizeMasterServerAddress(input);
        actual.Should().Be(expected);
    }

    [Fact]
    public void EnsureUserDirectoryInitialized_OnConstruction_CreatesRequiredSubdirectoriesAndCleanCache()
    {
        string tempDir = Path.Combine(Path.GetTempPath(), "KongorUserDirTest_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);

        try
        {
            GameLauncherService.EnsureUserDirectoryInitialized(tempDir);

            Directory.Exists(Path.Combine(tempDir, "userdata", "game", "logs")).Should().BeTrue();
            Directory.Exists(Path.Combine(tempDir, "userdata", "game", "replays")).Should().BeTrue();
            Directory.Exists(Path.Combine(tempDir, "userdata", "game", "shaders")).Should().BeTrue();
            Directory.Exists(Path.Combine(tempDir, "userdata", "editor")).Should().BeTrue();
            Directory.Exists(Path.Combine(tempDir, "userdataKONGOR", "extensions")).Should().BeTrue();
            Directory.Exists(Path.Combine(tempDir, "userdataKONGOR", "configuration")).Should().BeTrue();
            Directory.Exists(Path.Combine(tempDir, "userdataKONGOR", "updates")).Should().BeTrue();
            Directory.Exists(Path.Combine(tempDir, "KONGOR", "configuration")).Should().BeTrue();

            string markerPath = Path.Combine(tempDir, "userdata", "game", "shaders", ".cache_version_v3_dxvk_clean");
            File.Exists(markerPath).Should().BeTrue();
            File.ReadAllText(markerPath).Should().Contain("v3_clean_shader_cache");
        }
        finally
        {
            if (Directory.Exists(tempDir)) Directory.Delete(tempDir, true);
        }
    }

    [Fact]
    public void WriteAutoexecConfig_WhenInvoked_WritesExpectedContent()
    {
        string tempDir = Path.Combine(Path.GetTempPath(), "KongorCfgTest_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);

        try
        {
            GameLauncherService.WriteAutoexecConfig(tempDir);

            string cfgPath1 = Path.Combine(tempDir, "userdataKONGOR", "configuration", "autoexec.cfg");
            string cfgPath2 = Path.Combine(tempDir, "KONGOR", "configuration", "autoexec.cfg");

            File.Exists(cfgPath1).Should().BeTrue();
            File.Exists(cfgPath2).Should().BeTrue();

            string content = File.ReadAllText(cfgPath1);
            content.Should().Contain("SetSave \"host_affinity\" \"-1\"");
            content.Should().Contain("SetSave \"vid_maxFPS\" \"1000\"");
            content.Should().Contain("SetSave \"cl_maxFPS\" \"1000\"");
            content.Should().Contain("SetSave \"host_maxFPS\" \"1000\"");
            content.Should().Contain("SetSave \"vid_refreshRate\" \"240\"");
            content.Should().Contain("SetSave \"net_maxPacketRate\" \"128\"");
            content.Should().Contain("SetSave \"sound_mixRate\" \"48000\"");
        }
        finally
        {
            if (Directory.Exists(tempDir)) Directory.Delete(tempDir, true);
        }
    }

    #endregion

    #region 2. IPC Message Binary Framing & Serialization Tests

    [Fact]
    public void IpcMessage_HeaderConstants_MatchWillowmakerProtocol()
    {
        IpcMessage.HeaderMagic.Should().Be(0x574D); // 'W', 'M'
        IpcMessage.HeaderSize.Should().Be(12);
        IpcMessage.MaxPayloadSize.Should().Be(16 * 1024 * 1024);
    }

    [Fact]
    public void IpcMessage_SerializeString_CreatesValid12ByteFramedBuffer()
    {
        string text = "Set vid_maxFPS 1000";
        byte[] expectedPayload = Encoding.UTF8.GetBytes(text);

        IpcMessage msg = IpcMessage.CreateString(IpcOpCode.ExecuteCommand, text);
        byte[] raw = IpcMessage.Serialize(msg);

        raw.Length.Should().Be(12 + expectedPayload.Length);

        // Verify Magic (LE)
        ushort magic = BinaryPrimitives.ReadUInt16LittleEndian(raw.AsSpan(0, 2));
        magic.Should().Be(0x574D);

        // Verify OpCode (LE)
        ushort opCode = BinaryPrimitives.ReadUInt16LittleEndian(raw.AsSpan(2, 2));
        opCode.Should().Be((ushort)IpcOpCode.ExecuteCommand);

        // Verify PayloadLength (LE)
        int length = BinaryPrimitives.ReadInt32LittleEndian(raw.AsSpan(4, 4));
        length.Should().Be(expectedPayload.Length);

        // Verify RequestId (LE)
        uint reqId = BinaryPrimitives.ReadUInt32LittleEndian(raw.AsSpan(8, 4));
        reqId.Should().Be(0);

        // Verify Payload
        string deserializedText = Encoding.UTF8.GetString(raw.AsSpan(12));
        deserializedText.Should().Be(text);
    }

    [Fact]
    public void IpcMessage_SerializeJson_SerializesEngineStateDtoCorrectly()
    {
        EngineStateDto state = new()
        {
            CurrentFps = 480.5,
            MemoryWorkingSetBytes = 1024 * 1024 * 512,
            ActiveMatchId = 42,
            CurrentMap = "forests_of_caldavar",
            PlayerName = "KongorHero",
            ConnectedPlayers = 10,
            UptimeSeconds = 3600
        };

        IpcMessage msg = IpcMessage.CreateJson(IpcOpCode.EngineStateResponse, state);
        byte[] raw = IpcMessage.Serialize(msg);

        bool parsed = IpcMessage.TryDeserialize(raw, out IpcMessage? deserialized, out int bytesConsumed);
        parsed.Should().BeTrue();
        bytesConsumed.Should().Be(raw.Length);
        deserialized.Should().NotBeNull();
        deserialized!.OpCode.Should().Be(IpcOpCode.EngineStateResponse);

        EngineStateDto? readState = deserialized.ReadJson<EngineStateDto>();
        readState.Should().NotBeNull();
        readState!.CurrentFps.Should().Be(480.5);
        readState.MemoryWorkingSetBytes.Should().Be(1024 * 1024 * 512);
        readState.MemoryWorkingSetMb.Should().Be(512.0);
        readState.ActiveMatchId.Should().Be(42);
        readState.CurrentMap.Should().Be("forests_of_caldavar");
        readState.PlayerName.Should().Be("KongorHero");
        readState.ConnectedPlayers.Should().Be(10);
        readState.UptimeSeconds.Should().Be(3600);
    }

    [Fact]
    public void IpcMessage_TryDeserialize_IncompleteHeaderOrPayload_ReturnsFalse()
    {
        // 1. Less than 8 bytes
        byte[] partialHeader = [0x4D, 0x57, 0x03, 0x00];
        IpcMessage.TryDeserialize(partialHeader, out IpcMessage? msg1, out int consumed1).Should().BeFalse();
        msg1.Should().BeNull();
        consumed1.Should().Be(0);

        // 2. Complete header but payload truncated
        IpcMessage full = IpcMessage.CreateString(IpcOpCode.CommandResponse, "Long response output text");
        byte[] fullBytes = IpcMessage.Serialize(full);
        byte[] truncatedBytes = fullBytes[..12]; // Truncated midway through payload

        IpcMessage.TryDeserialize(truncatedBytes, out IpcMessage? msg2, out int consumed2).Should().BeFalse();
        msg2.Should().BeNull();
        consumed2.Should().Be(0);
    }

    [Fact]
    public void IpcMessage_TryDeserialize_CorruptMagic_ThrowsInvalidDataException()
    {
        byte[] corruptBuffer = [0x12, 0x34, 0x01, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00];
        Action act = () => IpcMessage.TryDeserialize(corruptBuffer, out _, out _);
        act.Should().Throw<InvalidDataException>().WithMessage("*Invalid IPC magic header*");
    }

    #endregion

    #region 3. GameIpcBridge Named Pipe Client & Server Integration Tests

    [Fact]
    public async Task GameIpcBridge_PingPong_RoundTripsThroughRealNamedPipe()
    {
        string pipeName = "kongor-test-pipe-" + Guid.NewGuid().ToString("N");

        using CancellationTokenSource cts = new(TimeSpan.FromSeconds(10));

        await using NamedPipeServerStream server = new(
            pipeName,
            PipeDirection.InOut,
            1,
            PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous | PipeOptions.WriteThrough);

        Task waitForConnectionTask = server.WaitForConnectionAsync(cts.Token);

        // Start local Named Pipe Server to simulate game client IPC endpoint
        Task serverTask = Task.Run(async () =>
        {
            await waitForConnectionTask;

            byte[] buffer = new byte[1024];
            int read = await server.ReadAsync(buffer, cts.Token);
            bool success = IpcMessage.TryDeserialize(buffer.AsSpan(0, read), out IpcMessage? req, out _);

            if (success && req?.OpCode == IpcOpCode.Ping)
            {
                IpcMessage pong = IpcMessage.CreateString(IpcOpCode.Pong, "PONG");
                byte[] pongBytes = IpcMessage.Serialize(pong);
                await server.WriteAsync(pongBytes, cts.Token);
                await server.FlushAsync(cts.Token);
                if (OperatingSystem.IsWindows()) server.WaitForPipeDrain();
                await Task.Delay(50, cts.Token);
            }
        }, cts.Token);

        // Connect client
        await using GameIpcBridge client = new(pipeName: pipeName);
        await client.ConnectAsync(timeoutMs: 3000, cts.Token);
        client.IsConnected.Should().BeTrue();

        long latency = await client.PingAsync(timeoutMs: 3000, cts.Token);
        latency.Should().BeGreaterThanOrEqualTo(0);

        await serverTask;
    }

    [Fact]
    public async Task GameIpcBridge_ExecuteCommand_ReturnsEngineOutput()
    {
        string pipeName = "kongor-test-pipe-" + Guid.NewGuid().ToString("N");
        using CancellationTokenSource cts = new(TimeSpan.FromSeconds(10));

        await using NamedPipeServerStream server = new(
            pipeName,
            PipeDirection.InOut,
            1,
            PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous | PipeOptions.WriteThrough);

        Task waitForConnectionTask = server.WaitForConnectionAsync(cts.Token);

        Task serverTask = Task.Run(async () =>
        {
            await waitForConnectionTask;

            byte[] buffer = new byte[1024];
            int read = await server.ReadAsync(buffer, cts.Token);
            bool success = IpcMessage.TryDeserialize(buffer.AsSpan(0, read), out IpcMessage? req, out _);

            if (success && req?.OpCode == IpcOpCode.ExecuteCommand)
            {
                string command = req.PayloadString;
                string replyText = $"Executed: {command} -> vid_maxFPS is now '1000'";
                IpcMessage reply = IpcMessage.CreateString(IpcOpCode.CommandResponse, replyText);
                byte[] replyBytes = IpcMessage.Serialize(reply);
                await server.WriteAsync(replyBytes, cts.Token);
                await server.FlushAsync(cts.Token);
                if (OperatingSystem.IsWindows()) server.WaitForPipeDrain();
                await Task.Delay(50, cts.Token);
            }
        }, cts.Token);

        await using GameIpcBridge client = new(pipeName: pipeName);
        await client.ConnectAsync(timeoutMs: 3000, cts.Token);

        string output = await client.ExecuteCommandAsync("Set vid_maxFPS 1000", timeoutMs: 3000, cts.Token);
        output.Should().Contain("Executed: Set vid_maxFPS 1000");

        await serverTask;
    }

    [Fact]
    public async Task GameIpcBridge_GetEngineState_DeserializesEngineTelemetry()
    {
        string pipeName = "kongor-test-pipe-" + Guid.NewGuid().ToString("N");
        using CancellationTokenSource cts = new(TimeSpan.FromSeconds(10));

        EngineStateDto simulatedState = new()
        {
            CurrentFps = 520.0,
            MemoryWorkingSetBytes = 1428594688L,
            ActiveMatchId = 1088,
            CurrentMap = "forests_of_caldavar",
            PlayerName = "TestPlayer",
            ConnectedPlayers = 10,
            UptimeSeconds = 7200
        };

        await using NamedPipeServerStream server = new(
            pipeName,
            PipeDirection.InOut,
            1,
            PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous | PipeOptions.WriteThrough);

        Task waitForConnectionTask = server.WaitForConnectionAsync(cts.Token);

        Task serverTask = Task.Run(async () =>
        {
            await waitForConnectionTask;

            byte[] buffer = new byte[1024];
            int read = await server.ReadAsync(buffer, cts.Token);
            bool success = IpcMessage.TryDeserialize(buffer.AsSpan(0, read), out IpcMessage? req, out _);

            if (success && req?.OpCode == IpcOpCode.GetEngineState)
            {
                IpcMessage reply = IpcMessage.CreateJson(IpcOpCode.EngineStateResponse, simulatedState);
                byte[] replyBytes = IpcMessage.Serialize(reply);
                await server.WriteAsync(replyBytes, cts.Token);
                await server.FlushAsync(cts.Token);
                if (OperatingSystem.IsWindows()) server.WaitForPipeDrain();
                await Task.Delay(50, cts.Token);
            }
        }, cts.Token);

        await using GameIpcBridge client = new(pipeName: pipeName);
        await client.ConnectAsync(timeoutMs: 3000, cts.Token);

        EngineStateDto? state = await client.GetEngineStateAsync(timeoutMs: 3000, cts.Token);
        state.Should().NotBeNull();
        state!.CurrentFps.Should().Be(520.0);
        state.MemoryWorkingSetBytes.Should().Be(1428594688L);
        state.MemoryWorkingSetMb.Should().BeApproximately(1362.41, 0.1);
        state.ActiveMatchId.Should().Be(1088);
        state.CurrentMap.Should().Be("forests_of_caldavar");
        state.PlayerName.Should().Be("TestPlayer");
        state.ConnectedPlayers.Should().Be(10);
        state.UptimeSeconds.Should().Be(7200);

        await serverTask;
    }

    [Fact]
    public async Task GameIpcBridge_CommandTimeout_ThrowsTimeoutException()
    {
        string pipeName = "kongor-test-pipe-" + Guid.NewGuid().ToString("N");
        using CancellationTokenSource cts = new(TimeSpan.FromSeconds(10));

        await using NamedPipeServerStream server = new(
            pipeName,
            PipeDirection.InOut,
            1,
            PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous | PipeOptions.WriteThrough);

        Task waitForConnectionTask = server.WaitForConnectionAsync(cts.Token);

        Task serverTask = Task.Run(async () =>
        {
            await waitForConnectionTask;

            // Read message but intentionally never reply
            byte[] buffer = new byte[1024];
            int bytesRead = await server.ReadAsync(buffer, cts.Token);
            _ = bytesRead;

            // Hold open until test finishes
            await Task.Delay(2000, cts.Token).ConfigureAwait(false);
        }, cts.Token);

        await using GameIpcBridge client = new(pipeName: pipeName);
        await client.ConnectAsync(timeoutMs: 3000, cts.Token);

        Func<Task> act = async () => await client.ExecuteCommandAsync("Set host_maxFPS 1000", timeoutMs: 200, cts.Token);
        await act.Should().ThrowAsync<TimeoutException>().WithMessage("*timed out*");

        cts.Cancel();
        try { await serverTask; } catch { }
    }

    #endregion

    #region 4. MCP Handlers & Registry Dispatch Tests

    [Fact]
    public async Task GameManageHandler_LaunchMode_ReturnsToolCallResultWithDetails()
    {
        Mock<IGameLauncherService> mockLauncher = new();
        mockLauncher.Setup(l => l.LaunchAsync(It.IsAny<GameLaunchOptions>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new GameLaunchResult
            {
                Success = true,
                Status = "success",
                Action = "launch",
                Running = true,
                ProcessId = 12345,
                MainWindowHandle = "0x001B0E24",
                Executable = "C:\\Games\\HoN\\hon_x64.exe",
                WorkingDirectory = "C:\\Games\\HoN",
                Arguments = ["-masterserver 127.0.0.1:55555", "+Set vid_resolution 1920x1080"]
            });

        GameManageHandler handler = new(mockLauncher.Object);

        JsonElement args = JsonElement.Parse("{\"action\": \"launch\", \"mode\": \"practice\", \"resolution\": \"1920x1080\"}");
        ToolCallResult result = await handler.HandleAsync(args);

        result.IsError.Should().BeFalse();
        string jsonText = ((TextContentItem)result.Content[0]).Text;
        jsonText.Should().Contain("\"action\": \"launch\"");
        jsonText.Should().Contain("\"running\": true");
        jsonText.Should().Contain("\"processId\": 12345");
        jsonText.Should().Contain("0x001B0E24");
    }

    [Fact]
    public async Task GameManageHandler_StopAction_ReturnsStoppedProcessesInfo()
    {
        Mock<IGameLauncherService> mockLauncher = new();
        mockLauncher.Setup(l => l.Stop(null, true))
            .Returns(new GameStopResult
            {
                Status = "success",
                Action = "stop",
                Running = false,
                TerminatedProcessesCount = 2,
                TerminatedProcessIds = [12345, 12346],
                Message = "Stopped 2 processes"
            });

        GameManageHandler handler = new(mockLauncher.Object);

        JsonElement args = JsonElement.Parse("{\"action\": \"stop\"}");
        ToolCallResult result = await handler.HandleAsync(args);

        result.IsError.Should().BeFalse();
        string jsonText = ((TextContentItem)result.Content[0]).Text;
        jsonText.Should().Contain("\"action\": \"stop\"");
        jsonText.Should().Contain("\"running\": false");
        jsonText.Should().Contain("\"terminatedProcessesCount\": 2");
    }

    [Fact]
    public async Task GameCommandHandler_MissingCommand_ReturnsError()
    {
        GameCommandHandler handler = new();
        JsonElement args = JsonElement.Parse("{}");

        ToolCallResult result = await handler.HandleAsync(args);
        result.IsError.Should().BeTrue();
        ((TextContentItem)result.Content[0]).Text.Should().Contain("Missing required parameter 'command'");
    }

    [Fact]
    public async Task GameCommandHandler_ValidCommand_ExecutesAndReturnsOutput()
    {
        Mock<IGameIpcBridge> mockIpc = new();
        mockIpc.SetupGet(i => i.IsConnected).Returns(true);
        mockIpc.Setup(i => i.ExecuteCommandAsync("Set vid_maxFPS 1000", It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync("vid_maxFPS is now '1000'");

        GameCommandHandler handler = new(mockIpc.Object);

        JsonElement args = JsonElement.Parse("{\"command\": \"Set vid_maxFPS 1000\", \"timeoutMs\": 1500}");
        ToolCallResult result = await handler.HandleAsync(args);

        result.IsError.Should().BeFalse();
        string jsonText = ((TextContentItem)result.Content[0]).Text;
        jsonText.Should().Contain("\"success\": true");
        jsonText.Should().Contain("vid_maxFPS is now");
        jsonText.Should().Contain("1000");
        jsonText.Should().Contain("executionTimeMs");
    }

    [Fact]
    public async Task GameTelemetryHandler_ConnectedState_ReturnsFormattedTelemetryDto()
    {
        Mock<IGameIpcBridge> mockIpc = new();
        mockIpc.Setup(i => i.GetEngineStateAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new EngineStateDto
            {
                CurrentFps = 500.0,
                MemoryWorkingSetBytes = 1024 * 1024 * 1024,
                ActiveMatchId = 101,
                CurrentMap = "forests_of_caldavar",
                PlayerName = "KongorPro",
                ConnectedPlayers = 10,
                UptimeSeconds = 1800
            });

        GameTelemetryHandler handler = new(mockIpc.Object);

        JsonElement args = JsonElement.Parse("{\"channel\": \"pipe\", \"timeoutMs\": 2000}");
        ToolCallResult result = await handler.HandleAsync(args);

        result.IsError.Should().BeFalse();
        string jsonText = ((TextContentItem)result.Content[0]).Text;
        jsonText.Should().Contain("\"success\": true");
        jsonText.Should().Contain("\"currentFps\": 500");
        jsonText.Should().Contain("\"memoryWorkingSetMb\": 1024");
        jsonText.Should().Contain("\"currentMap\": \"forests_of_caldavar\"");
        jsonText.Should().Contain("\"playerName\": \"KongorPro\"");
        jsonText.Should().Contain("\"connectedPlayers\": 10");
    }

    [Fact]
    public async Task McpToolRegistry_WhenInvoked_DispatchesAllGameToolsSeamlessly()
    {
        Mock<IGameLauncherService> mockLauncher = new();
        mockLauncher.Setup(l => l.LaunchAsync(It.IsAny<GameLaunchOptions>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new GameLaunchResult
            {
                Success = true,
                Status = "success",
                Action = "launch",
                Running = true,
                ProcessId = 9999
            });

        Mock<IGameIpcBridge> mockIpc = new();
        mockIpc.Setup(i => i.ExecuteCommandAsync(It.IsAny<string>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync("Command completed");
        mockIpc.Setup(i => i.GetEngineStateAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new EngineStateDto { CurrentFps = 360.0 });

        McpToolRegistry registry = new(
            registerDefaults: true,
            gameLauncherService: mockLauncher.Object,
            gameIpcBridge: mockIpc.Object);

        // 1. kongor_game_manage
        var res1 = await registry.ExecuteToolAsync("kongor_game_manage", JsonElement.Parse("{\"action\":\"launch\",\"mode\":\"practice\"}"));
        res1.IsError.Should().BeFalse();
        ((TextContentItem)res1.Content[0]).Text.Should().Contain("9999");

        // 2. kongor_game_command
        var res2 = await registry.ExecuteToolAsync("kongor_game_command", JsonElement.Parse("{\"command\":\"ping\"}"));
        res2.IsError.Should().BeFalse();
        ((TextContentItem)res2.Content[0]).Text.Should().Contain("Command completed");

        // 3. kongor_game_telemetry
        var res3 = await registry.ExecuteToolAsync("kongor_game_telemetry", JsonElement.Parse("{\"channel\":\"pipe\"}"));
        res3.IsError.Should().BeFalse();
        ((TextContentItem)res3.Content[0]).Text.Should().Contain("360");
    }

    #endregion

    #region 6. Lua Scripting & Engine Control Tests

    [Fact]
    public async Task GameLuaHandler_DirectCode_FormatsScriptPrefix()
    {
        GameLuaHandler handler = new();
        using JsonDocument doc = JsonDocument.Parse("""{"code": "println('test')"}""");

        ToolCallResult result = await handler.HandleAsync(doc.RootElement);
        result.Content.Should().NotBeEmpty();
        string text = ((TextContentItem)result.Content[0]).Text;
        text.Should().Contain("Script println('test')");
    }

    [Fact]
    public async Task GameLuaHandler_Trigger_FormatsTriggerCommand()
    {
        GameLuaHandler handler = new();
        using JsonDocument doc = JsonDocument.Parse("""{"trigger": "main_login"}""");

        ToolCallResult result = await handler.HandleAsync(doc.RootElement);
        result.Content.Should().NotBeEmpty();
        string text = ((TextContentItem)result.Content[0]).Text;
        text.Should().Contain("Trigger main_login");
    }

    [Fact]
    public async Task GameLuaHandler_InvokeWidget_FormatsCallWidgetEvent()
    {
        GameLuaHandler handler = new();
        using JsonDocument doc = JsonDocument.Parse("""{"invoke_widget": "play_button", "event": "onclick"}""");

        ToolCallResult result = await handler.HandleAsync(doc.RootElement);
        result.Content.Should().NotBeEmpty();
        string text = ((TextContentItem)result.Content[0]).Text;
        text.Should().Contain("Call play_button onclick");
    }

    [Fact]
    public async Task GameLuaHandler_MissingParameters_ReturnsError()
    {
        GameLuaHandler handler = new();
        using JsonDocument doc = JsonDocument.Parse("{}");

        ToolCallResult result = await handler.HandleAsync(doc.RootElement);
        result.IsError.Should().BeTrue();
        string text = ((TextContentItem)result.Content[0]).Text;
        text.Should().Contain("Missing required parameter");
    }

    [Fact]
    public async Task HandleClientCommand_NoClient_AutoLaunchFalse_ReturnsOfflineResponsePromptly()
    {
        McpToolRegistry registry = new(registerDefaults: true);
        var args = JsonSerializer.Deserialize<JsonElement>("{\"command\":\"echo LiveMcpSuccess\",\"autoLaunch\":false,\"timeoutMs\":500}");
        using CancellationTokenSource cts = new(5000);
        var result = await registry.ExecuteToolAsync("kongor_game_command", args, cts.Token);
        result.Should().NotBeNull();
        result.Content.Should().NotBeEmpty();
    }

    #endregion
}
