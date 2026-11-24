// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// TAO (Together As One) MCP & Kongor CLI -- Created by guccidonut (https://github.com/guccidonut/PKV)

using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
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
/// Tier 5 White-Box Adversarial Hardening Test Suite for Milestones 1, 2, and 3:
/// - Milestone 1: HotReloadService & HotReloadHandler (sliding debounce bursts, unwatch/dispose, concurrent triggers, disconnected IPC)
/// - Milestone 2: MatchmakingSimulateService & MatchmakingSimulateHandler (tiered port resolution order, socket cancellation/timeout, binary wire framing edge cases, socket leak prevention)
/// - Milestone 3: GameSnapshotHandler & ReplayTickMath (Phase 3 lifecycle safety guardrail across phases 0,1,2,4,5,-1, replay tick math precision, store concurrency, non-existent ID handling)
/// </summary>
public sealed class HotReloadAndSnapshotAdversarialTests
{
    private static JsonElement ParseResult(ToolCallResult result)
    {
        if (result.Content.Count > 0 && result.Content[0] is TextContentItem textItem)
        {
            using JsonDocument doc = JsonDocument.Parse(textItem.Text);
            return doc.RootElement.Clone();
        }
        return default;
    }

    private static JsonElement MakeArgs(object obj)
    {
        string json = JsonSerializer.Serialize(obj);
        using JsonDocument doc = JsonDocument.Parse(json);
        return doc.RootElement.Clone();
    }

    #region Milestone 1: HotReloadService & HotReloadHandler Adversarial Tests

    [Fact]
    public async Task HotReload_DebounceSlidingWindow_RapidBurstResetsTimer_OnlyExecutesOnceAfterQuietPeriod()
    {
        Mock<IGameIpcBridge> mockIpc = new();
        mockIpc.SetupGet(i => i.IsConnected).Returns(true);
        mockIpc.Setup(i => i.ExecuteCommandAsync(It.IsAny<string>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync("OK");

        using HotReloadService service = new(mockIpc.Object);
        TaskCompletionSource<HotReloadResultDto> reloadTcs = new();
        service.OnReloadCompleted += res => reloadTcs.TrySetResult(res);

        // 400ms sliding debounce window
        service.StartWatching(customPaths: ["games/shared/ui/"], debounceMs: 400);

        // Fire 4 rapid events back-to-back (each event resets the timer so it never triggers during the burst)
        for (int i = 0; i < 4; i++)
        {
            service.NotifyFileChanged($"games/shared/ui/rapid_burst_{i}.interface");
        }

        // Now let the 400ms window elapse quietly
        HotReloadResultDto result = await reloadTcs.Task.WaitAsync(TimeSpan.FromSeconds(10.0));

        result.Success.Should().BeTrue();
        service.TotalReloads.Should().Be(1, "Exactly one reload should be executed after debounce quiescence");
        service.TotalFileEvents.Should().Be(4, "All 4 file change events must be captured");
        service.LastChangedFiles.Should().HaveCount(4, "All 4 unique files must be in the batch");
        mockIpc.Verify(i => i.ExecuteCommandAsync("ReloadInterfaces", It.IsAny<int>(), It.IsAny<CancellationToken>()), Times.Once);
        mockIpc.Verify(i => i.ExecuteCommandAsync("Script dofile('/ui/scripts/nexus_bridge.lua')", It.IsAny<int>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(-100)]
    public void HotReload_DebounceMs_NonPositiveValues_ClampedToDefault150Ms(int invalidDebounce)
    {
        Mock<IGameIpcBridge> mockIpc = new();
        using HotReloadService service = new(mockIpc.Object);

        service.StartWatching(customPaths: ["games/shared/ui/"], debounceMs: invalidDebounce);

        service.DebounceMs.Should().Be(150, "Non-positive debounceMs must clamp to default 150ms");
    }

    [Fact]
    public void HotReload_CustomPaths_NullEmptyAndWhitespace_FallsBackToStandardUiAndScriptPaths()
    {
        Mock<IGameIpcBridge> mockIpc = new();
        using HotReloadService service = new(mockIpc.Object);

        // Pass list with only empty and whitespace paths
        service.StartWatching(customPaths: ["", "   ", "\t"], debounceMs: 150);

        service.WatchedPaths.Should().HaveCount(2);
        service.WatchedPaths.Should().Contain(p => p.EndsWith("ui", StringComparison.OrdinalIgnoreCase));
        service.WatchedPaths.Should().Contain(p => p.EndsWith("scripts", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void HotReload_UnwatchAndDispose_IdempotentCalls_ReleasesWatchersWithoutException()
    {
        Mock<IGameIpcBridge> mockIpc = new();
        HotReloadService service = new(mockIpc.Object);

        service.StartWatching(customPaths: ["games/shared/ui/"], debounceMs: 150);
        service.IsWatching.Should().BeTrue();

        // Calling StopWatching multiple times must be idempotent
        service.StopWatching();
        service.IsWatching.Should().BeFalse();
        service.StopWatching();
        service.IsWatching.Should().BeFalse();

        // Calling Dispose multiple times must be safe
        service.Dispose();
        service.Dispose();

        service.IsWatching.Should().BeFalse();
    }

    [Fact]
    public async Task HotReload_DisposeAsync_DisposesCleanly()
    {
        Mock<IGameIpcBridge> mockIpc = new();
        HotReloadService service = new(mockIpc.Object);

        service.StartWatching(customPaths: ["games/shared/ui/"], debounceMs: 150);
        await service.StopWatchingAsync();
        await service.DisposeAsync();

        service.IsWatching.Should().BeFalse();
    }

    [Theory]
    [InlineData(".hotreload_state")]
    [InlineData("~temporary_file.xml")]
    [InlineData("temp_edit.tmp")]
    [InlineData("backup_ui.bak")]
    [InlineData("editor_swap.swp")]
    [InlineData("engine_traffic.log")]
    [InlineData("mcp_events.ndjson")]
    [InlineData(".git/HEAD")]
    public void HotReload_NotifyFileChanged_IgnoredExtensions_FilteredOutWithoutTriggeringTimer(string ignoredPath)
    {
        Mock<IGameIpcBridge> mockIpc = new();
        using HotReloadService service = new(mockIpc.Object);

        service.StartWatching(customPaths: ["games/shared/ui/"], debounceMs: 150);
        int initialEvents = service.TotalFileEvents;

        service.NotifyFileChanged(ignoredPath);

        service.TotalFileEvents.Should().Be(initialEvents, $"Ignored file '{ignoredPath}' should not be counted as a file event");
    }

    [Fact]
    public void HotReload_CleanUtf8Bom_StripsBomFromDiskWithoutCorruptingContent()
    {
        Mock<IGameIpcBridge> mockIpc = new();
        using HotReloadService service = new(mockIpc.Object);

        string tempFile = Path.Combine(Path.GetTempPath(), $"kongor_test_bom_{Guid.NewGuid():N}.interface");
        try
        {
            // Write 3 BOM bytes followed by ASCII XML
            byte[] bom = [0xEF, 0xBB, 0xBF];
            byte[] content = "<interface name=\"main\" />"u8.ToArray();
            byte[] fileBytes = [.. bom, .. content];
            File.WriteAllBytes(tempFile, fileBytes);

            // NotifyFileChanged triggers CleanUtf8Bom for .interface
            service.NotifyFileChanged(tempFile);

            byte[] updatedBytes = File.ReadAllBytes(tempFile);
            updatedBytes.Should().Equal(content, "BOM header (0xEF, 0xBB, 0xBF) must be cleanly removed");
        }
        finally
        {
            if (File.Exists(tempFile)) File.Delete(tempFile);
        }
    }

    [Fact]
    public void HotReload_CleanUtf8Bom_ShortFileUnder3Bytes_DoesNotThrowOrCorrupt()
    {
        Mock<IGameIpcBridge> mockIpc = new();
        using HotReloadService service = new(mockIpc.Object);

        string tempFile = Path.Combine(Path.GetTempPath(), $"kongor_test_short_{Guid.NewGuid():N}.xml");
        try
        {
            File.WriteAllBytes(tempFile, [0x3C, 0x3E]); // 2 bytes: "<>"
            service.NotifyFileChanged(tempFile);

            byte[] updatedBytes = File.ReadAllBytes(tempFile);
            updatedBytes.Should().Equal([0x3C, 0x3E]);
        }
        finally
        {
            if (File.Exists(tempFile)) File.Delete(tempFile);
        }
    }

    [Fact]
    public async Task HotReload_ConcurrentTriggerReloadAsync_20ParallelCalls_SerializedCleanlyWithoutRace()
    {
        Mock<IGameIpcBridge> mockIpc = new();
        mockIpc.SetupGet(i => i.IsConnected).Returns(true);
        mockIpc.Setup(i => i.ExecuteCommandAsync(It.IsAny<string>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync("OK");

        using HotReloadService service = new(mockIpc.Object);

        const int callerCount = 20;
        Task<HotReloadResultDto>[] tasks = new Task<HotReloadResultDto>[callerCount];

        for (int i = 0; i < callerCount; i++)
        {
            int index = i;
            tasks[i] = Task.Run(() => service.TriggerReloadAsync([$"games/shared/ui/panel_{index}.interface"]));
        }

        HotReloadResultDto[] results = await Task.WhenAll(tasks);

        results.Should().HaveCount(callerCount);
        results.Should().OnlyContain(r => r.Success);
        service.TotalReloads.Should().Be(callerCount, "All 20 serial executions should complete and increment total reloads");
    }

    [Fact]
    public async Task HotReload_TriggerReload_IpcDisconnected_AttemptsReconnectBeforeExecution()
    {
        Mock<IGameIpcBridge> mockIpc = new();
        bool connected = false;

        mockIpc.SetupGet(i => i.IsConnected).Returns(() => connected);
        mockIpc.Setup(i => i.ConnectAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .Callback(() => connected = true)
            .Returns(Task.CompletedTask);
        mockIpc.Setup(i => i.ExecuteCommandAsync(It.IsAny<string>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync("OK");

        using HotReloadService service = new(mockIpc.Object);

        var result = await service.TriggerReloadAsync(["games/shared/ui/main.interface"]);

        result.Success.Should().BeTrue();
        mockIpc.Verify(i => i.ConnectAsync(2000, It.IsAny<CancellationToken>()), Times.Once,
            "Service must attempt to connect the IPC bridge when offline");
        mockIpc.Verify(i => i.ExecuteCommandAsync("ReloadInterfaces", It.IsAny<int>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task HotReload_TriggerReload_IpcFails_ReturnsFailureAndDoesNotIncrementTotalReloads()
    {
        Mock<IGameIpcBridge> mockIpc = new();
        mockIpc.SetupGet(i => i.IsConnected).Returns(true);
        mockIpc.Setup(i => i.ExecuteCommandAsync(It.IsAny<string>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new IOException("Named pipe disconnected unexpectedly."));

        using HotReloadService service = new(mockIpc.Object);

        var result = await service.TriggerReloadAsync(["games/shared/ui/broken.interface"]);

        result.Success.Should().BeFalse();
        result.Error.Should().Contain("Named pipe disconnected unexpectedly.");
        service.TotalReloads.Should().Be(0, "Failed reload must not increment TotalReloads counter");
        service.LastError.Should().Contain("Named pipe disconnected");
    }

    [Theory]
    [InlineData("destroy")]
    [InlineData("restart")]
    [InlineData("unknown_action")]
    public async Task HotReload_HotReloadHandler_InvalidAction_ReturnsMalformedArgumentsRemediation(string action)
    {
        Mock<IHotReloadService> mockService = new();
        HotReloadHandler handler = new(hotReloadService: mockService.Object);

        var result = await handler.HandleAsync(MakeArgs(new { action }));

        result.IsError.Should().BeTrue();
        JsonElement root = ParseResult(result);
        root.GetProperty("status").GetString().Should().Be("error");
        root.GetProperty("remediationCategory").GetString().Should().Be("MalformedCommandArguments");
        root.GetProperty("error").GetString().Should().Contain("Unsupported action");
    }

    [Fact]
    public async Task HotReload_HotReloadHandler_EmptyOrOmittedAction_DefaultsToStatus()
    {
        Mock<IHotReloadService> mockService = new();
        mockService.Setup(s => s.GetStatus()).Returns(new HotReloadStatusDto
        {
            Status = "success",
            IsWatching = true,
            WatchedPaths = ["games/shared/ui/"]
        });

        HotReloadHandler handler = new(hotReloadService: mockService.Object);

        var result1 = await handler.HandleAsync(MakeArgs(new { action = "" }));
        result1.IsError.Should().BeFalse();
        ParseResult(result1).GetProperty("action").GetString().Should().Be("status");

        var result2 = await handler.HandleAsync(MakeArgs(new { }));
        result2.IsError.Should().BeFalse();
        ParseResult(result2).GetProperty("action").GetString().Should().Be("status");
    }

    [Theory]
    [InlineData("start", "watch")]
    [InlineData("stop", "unwatch")]
    [InlineData("reload", "trigger")]
    public async Task HotReload_HotReloadHandler_ActionAliases_NormalizedToCanonicalAction(string inputAction, string expectedAction)
    {
        Mock<IHotReloadService> mockService = new();
        mockService.Setup(s => s.GetStatus()).Returns(new HotReloadStatusDto { Status = "success", WatchedPaths = [] });
        mockService.Setup(s => s.TriggerReloadAsync(It.IsAny<IEnumerable<string>?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new HotReloadResultDto { Success = true, ChangedFiles = ["test"] });

        HotReloadHandler handler = new(hotReloadService: mockService.Object);

        var result = await handler.HandleAsync(MakeArgs(new { action = inputAction }));

        result.IsError.Should().BeFalse();
        JsonElement root = ParseResult(result);
        root.GetProperty("action").GetString().Should().Be(expectedAction);
    }

    [Fact]
    public async Task HotReload_HotReloadHandler_TriggerFailure_ReturnsErrorWithImprovedCommand()
    {
        Mock<IHotReloadService> mockService = new();
        mockService.Setup(s => s.GetStatus()).Returns(new HotReloadStatusDto { Status = "error", WatchedPaths = [] });
        mockService.Setup(s => s.TriggerReloadAsync(It.IsAny<IEnumerable<string>?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new HotReloadResultDto
            {
                Success = false,
                Error = "IPC connection refused to willowmaker-ipc",
                DispatchedCommands = ["ReloadInterfaces"]
            });

        HotReloadHandler handler = new(hotReloadService: mockService.Object);

        var result = await handler.HandleAsync(MakeArgs(new { action = "trigger" }));

        result.IsError.Should().BeTrue();
        JsonElement root = ParseResult(result);
        root.GetProperty("status").GetString().Should().Be("error");
        root.GetProperty("error").GetString().Should().Contain("IPC connection refused");
        root.TryGetProperty("remediationHint", out _).Should().BeTrue();
    }

    #endregion

    #region Milestone 2: MatchmakingSimulateService & MatchmakingSimulateHandler Adversarial Tests

    [Fact]
    public void GameLua_TieredDiscovery_ExplicitPort_PreemptsAllAspireAndDefaultConfigurations()
    {
        MatchmakingSimulateService service = new();
        // Even when an Aspire key is configured, explicit port 12345 must take absolute precedence
        Dictionary<string, string?> mockEnv = new()
        {
            ["services__chat-server__tcp__0"] = "11040",
            ["CHAT_SERVER_PORT_CLIENT"] = "11041"
        };

        var (port, tier) = service.ResolveChatServerPort(12345, key => mockEnv.GetValueOrDefault(key));

        port.Should().Be(12345);
        tier.Should().Be("explicit");
    }

    [Fact]
    public void GameLua_TieredDiscovery_AspireResolutionOrder_EvaluatesPriorityKeysInExactOrder()
    {
        MatchmakingSimulateService service = new();

        // 1. services__chat-server__tcp__0 beats services__chat-server__client__0
        Dictionary<string, string?> env1 = new()
        {
            ["services__chat-server__tcp__0"] = "11050",
            ["services__chat-server__client__0"] = "11051",
            ["CHAT_SERVER_PORT_CLIENT"] = "11052"
        };
        service.ResolveChatServerPort(null, k => env1.GetValueOrDefault(k)).Should().Be((11050, "aspire"));

        // 2. services__chat-server__client__0 beats CHAT_SERVER_PORT_CLIENT
        Dictionary<string, string?> env2 = new()
        {
            ["services__chat-server__client__0"] = "11060",
            ["CHAT_SERVER_PORT_CLIENT"] = "11061"
        };
        service.ResolveChatServerPort(null, k => env2.GetValueOrDefault(k)).Should().Be((11060, "aspire"));

        // 3. ChatServer:ClientPort works as last fallback Aspire key
        Dictionary<string, string?> env3 = new()
        {
            ["ChatServer:ClientPort"] = "11070"
        };
        service.ResolveChatServerPort(null, k => env3.GetValueOrDefault(k)).Should().Be((11070, "aspire"));
    }

    [Theory]
    [InlineData("tcp://127.0.0.1:11080", 11080)]
    [InlineData("http://localhost:11081", 11081)]
    [InlineData("https://chat.kongor.net:11082/path", 11082)]
    public void GameLua_TieredDiscovery_AspireUriString_ExtractsPortCleanly(string uriString, int expectedPort)
    {
        MatchmakingSimulateService service = new();
        Dictionary<string, string?> env = new()
        {
            ["services__chat-server__tcp__0"] = uriString
        };

        var (port, tier) = service.ResolveChatServerPort(null, k => env.GetValueOrDefault(k));

        port.Should().Be(expectedPort);
        tier.Should().Be("aspire");
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-5)]
    [InlineData(65536)]
    [InlineData(99999)]
    public void GameLua_TieredDiscovery_InvalidExplicitPort_FallsThroughToDefaultWhenNoEnv(int invalidPort)
    {
        MatchmakingSimulateService service = new();

        var (port, tier) = service.ResolveChatServerPort(invalidPort, _ => null);

        port.Should().Be(11031);
        tier.Should().Be("default");
    }

    [Fact]
    public void GameLua_TieredDiscovery_CorruptedEnvValues_FallsThroughToDefault()
    {
        MatchmakingSimulateService service = new();
        Dictionary<string, string?> env = new()
        {
            ["services__chat-server__tcp__0"] = "not_a_number",
            ["CHAT_SERVER_PORT_CLIENT"] = "-500",
            ["ChatServer:ClientPort"] = "70000"
        };

        var (port, tier) = service.ResolveChatServerPort(null, k => env.GetValueOrDefault(k));

        port.Should().Be(11031);
        tier.Should().Be("default");
    }

    [Fact]
    public async Task GameLua_ReadPacketAsync_TruncatedLengthHeaderUnder2Bytes_ThrowsEndOfStreamException()
    {
        byte[] singleByte = [0x05];

        // Test using a connected pair of loopback sockets
        using Socket listener = new(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        listener.Bind(new IPEndPoint(IPAddress.Loopback, 0));
        listener.Listen(1);

        Task<Socket> acceptTask = listener.AcceptAsync();
        using Socket client = new(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        await client.ConnectAsync(listener.LocalEndPoint!);
        using Socket server = await acceptTask;

        using NetworkStream clientStream = new(client, ownsSocket: false);
        using NetworkStream serverStream = new(server, ownsSocket: false);

        // Send 1 byte then immediately close
        await clientStream.WriteAsync(singleByte);
        client.Shutdown(SocketShutdown.Both);
        client.Close();

        Func<Task> act = async () => await MatchmakingSimulateService.ReadPacketAsync(serverStream, CancellationToken.None);
        await act.Should().ThrowAsync<EndOfStreamException>("Reading length header from truncated stream must throw EndOfStreamException");
    }

    [Fact]
    public async Task GameLua_ReadPacketAsync_PayloadLengthHeaderLessThan2_ThrowsInvalidOperationException()
    {
        using Socket listener = new(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        listener.Bind(new IPEndPoint(IPAddress.Loopback, 0));
        listener.Listen(1);

        Task<Socket> acceptTask = listener.AcceptAsync();
        using Socket client = new(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        await client.ConnectAsync(listener.LocalEndPoint!);
        using Socket server = await acceptTask;

        using NetworkStream clientStream = new(client, ownsSocket: false);
        using NetworkStream serverStream = new(server, ownsSocket: false);

        // Header claims payload length is 1 (invalid, minimum is 2 bytes for opcode)
        byte[] malformedPacket = [0x01, 0x00, 0xAA];
        await clientStream.WriteAsync(malformedPacket);

        Func<Task> act = async () => await MatchmakingSimulateService.ReadPacketAsync(serverStream, CancellationToken.None);
        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*Malformed packet with invalid payload length 1*");
    }

    [Fact]
    public async Task GameLua_ReadPacketAsync_TruncatedPayloadData_ThrowsEndOfStreamException()
    {
        using Socket listener = new(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        listener.Bind(new IPEndPoint(IPAddress.Loopback, 0));
        listener.Listen(1);

        Task<Socket> acceptTask = listener.AcceptAsync();
        using Socket client = new(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        await client.ConnectAsync(listener.LocalEndPoint!);
        using Socket server = await acceptTask;

        using NetworkStream clientStream = new(client, ownsSocket: false);
        using NetworkStream serverStream = new(server, ownsSocket: false);

        // Header claims 10 bytes, but we only send 4 bytes then close
        byte[] truncatedPacket = [0x0A, 0x00, 0x00, 0x0C];
        await clientStream.WriteAsync(truncatedPacket);
        client.Shutdown(SocketShutdown.Both);
        client.Close();

        Func<Task> act = async () => await MatchmakingSimulateService.ReadPacketAsync(serverStream, CancellationToken.None);
        await act.Should().ThrowAsync<EndOfStreamException>("Incomplete payload read must throw EndOfStreamException");
    }

    [Fact]
    public async Task GameLua_ReadPacketAsync_ValidOpcodeWithZeroLengthPayload_ParsesCleanly()
    {
        using Socket listener = new(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        listener.Bind(new IPEndPoint(IPAddress.Loopback, 0));
        listener.Listen(1);

        Task<Socket> acceptTask = listener.AcceptAsync();
        using Socket client = new(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        await client.ConnectAsync(listener.LocalEndPoint!);
        using Socket server = await acceptTask;

        using NetworkStream clientStream = new(client, ownsSocket: false);
        using NetworkStream serverStream = new(server, ownsSocket: false);

        // Opcode only: length = 2, opcode = 0x1C00 (NET_CHAT_CL_ACCEPT)
        byte[] packet = MatchmakingSimulateService.EncodePacket(0x1C00);
        await clientStream.WriteAsync(packet);

        var (opcode, payload) = await MatchmakingSimulateService.ReadPacketAsync(serverStream, CancellationToken.None);

        opcode.Should().Be(0x1C00);
        payload.Should().BeEmpty();
    }

    [Fact]
    public void GameLua_ComputeChatAuthHash_MatchesKnownSha1Formula()
    {
        int accountId = 90001;
        string ip = "127.0.0.1";
        string cookie = "cookie_test";
        string salt = "test_salt";

        string computed = MatchmakingSimulateService.ComputeChatAuthHash(accountId, ip, cookie, salt);

        string raw = $"{accountId}{ip}{cookie}{salt}";
        string expected = Convert.ToHexStringLower(SHA1.HashData(Encoding.UTF8.GetBytes(raw)));

        computed.Should().Be(expected);
        computed.Length.Should().Be(40);
    }

    [Fact]
    public async Task GameLua_MatchmakingSimulateHandler_InvalidPortValidation_ReturnsMalformedCommandArguments()
    {
        Mock<IMatchmakingSimulateService> mockService = new();
        MatchmakingSimulateHandler handler = new(simulationService: mockService.Object);

        var result = await handler.HandleAsync(MakeArgs(new { serverPort = 70000 }));

        result.IsError.Should().BeTrue();
        JsonElement root = ParseResult(result);
        root.GetProperty("status").GetString().Should().Be("error");
        root.GetProperty("remediationCategory").GetString().Should().Be("MalformedCommandArguments");
        root.GetProperty("error").GetString().Should().Contain("70000");
    }

    [Fact]
    public async Task GameLua_MatchmakingSimulateHandler_NegativeTimeout_ReturnsMalformedCommandArguments()
    {
        Mock<IMatchmakingSimulateService> mockService = new();
        MatchmakingSimulateHandler handler = new(simulationService: mockService.Object);

        var result = await handler.HandleAsync(MakeArgs(new { timeoutSeconds = -1 }));

        result.IsError.Should().BeTrue();
        JsonElement root = ParseResult(result);
        root.GetProperty("status").GetString().Should().Be("error");
        root.GetProperty("remediationCategory").GetString().Should().Be("MalformedCommandArguments");
        root.GetProperty("error").GetString().Should().Contain("timeoutSeconds");
    }

    [Trait("Category", "Manual")]
    [Fact]
    public async Task GameLua_MatchmakingSimulateHandler_ServerOffline_ReturnsChatServerOfflineRemediation()
    {
        if (!NexusTestEnvironment.IsManualTestEnabled) return;

        // Use an unallocated local port that is guaranteed closed
        int closedPort;
        using (Socket temp = new(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp))
        {
            temp.Bind(new IPEndPoint(IPAddress.Loopback, 0));
            closedPort = ((IPEndPoint)temp.LocalEndPoint!).Port;
        }

        MatchmakingSimulateService service = new();
        MatchmakingSimulateHandler handler = new(simulationService: service);

        var result = await handler.HandleAsync(MakeArgs(new
        {
            serverHost = "127.0.0.1",
            serverPort = closedPort,
            clientCount = 1,
            timeoutSeconds = 2
        }));

        result.IsError.Should().BeTrue();
        JsonElement root = ParseResult(result);
        root.GetProperty("status").GetString().Should().Be("error");
        root.GetProperty("remediationCategory").GetString().Should().Be("ChatServerOffline");
        root.GetProperty("remediationHint").GetString().Should().Contain("ChatServer is not listening");
    }

    #endregion

    #region Milestone 3: GameSnapshotHandler & ReplayTickMath Adversarial Tests

    [Theory]
    [InlineData(0)]   // Loading Screen
    [InlineData(1)]   // Team Selection
    [InlineData(2)]   // Hero Picking
    [InlineData(4)]   // Match Ending / Post-Game
    [InlineData(5)]   // Spectator / Replay Exit
    [InlineData(-1)]  // Corrupt / Unset phase
    [InlineData(99)]  // Unknown future phase
    public async Task SnapshotRestore_WhenPhaseNonActive_RejectsAcrossAllPhases(int forbiddenPhase)
    {
        Mock<IGameIpcBridge> mockIpc = new();
        mockIpc.SetupGet(i => i.IsConnected).Returns(true);

        // IPC returns the forbidden phase
        mockIpc.Setup(i => i.ExecuteCommandAsync(It.Is<string>(s => s.Contains("GetCurrentPhase")), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(forbiddenPhase.ToString());

        GameSnapshotHandler handler = new(gameIpcBridge: mockIpc.Object);

        // Pre-populate a snapshot
        handler.SnapshotStore["snap_test"] = new GameSnapshotDto
        {
            Id = "snap_test",
            Gold = 2500,
            Level = 11
        };

        var result = await handler.HandleAsync(MakeArgs(new
        {
            action = "restore",
            id = "snap_test"
        }));

        result.IsError.Should().BeTrue();
        JsonElement root = ParseResult(result);
        root.GetProperty("status").GetString().Should().Be("error");
        root.GetProperty("errorCode").GetString().Should().Be("INVALID_GAME_PHASE");
        root.GetProperty("currentPhase").GetInt32().Should().Be(forbiddenPhase);

        // CRITICAL ADVERSARIAL ASSERTION: Zero mutation commands dispatched to engine
        mockIpc.Verify(i => i.ExecuteCommandAsync(It.Is<string>(s => s.Contains("ApplySnapshot")), It.IsAny<int>(), It.IsAny<CancellationToken>()), Times.Never,
            $"ApplySnapshot must NEVER be dispatched when currentPhase={forbiddenPhase}");
    }

    [Fact]
    public async Task M3_Adversarial_PhaseGuardrail_ActivePhase3_AllowsMutationAndExecutesApplySnapshot()
    {
        Mock<IGameIpcBridge> mockIpc = new();
        mockIpc.SetupGet(i => i.IsConnected).Returns(true);
        mockIpc.Setup(i => i.ExecuteCommandAsync(It.Is<string>(s => s.Contains("GetCurrentPhase")), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync("3");
        mockIpc.Setup(i => i.ExecuteCommandAsync(It.Is<string>(s => s.Contains("ApplySnapshot")), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync("OK");

        GameSnapshotHandler handler = new(gameIpcBridge: mockIpc.Object);
        handler.SnapshotStore["snap_combat"] = new GameSnapshotDto
        {
            Id = "snap_combat",
            Gold = 4500,
            Level = 16
        };

        var result = await handler.HandleAsync(MakeArgs(new
        {
            action = "restore",
            id = "snap_combat"
        }));

        result.IsError.Should().BeFalse();
        JsonElement root = ParseResult(result);
        root.GetProperty("status").GetString().Should().Be("success");
        root.GetProperty("gamePhase").GetInt32().Should().Be(3);

        mockIpc.Verify(i => i.ExecuteCommandAsync(It.Is<string>(s => s.Contains("ApplySnapshot")), It.IsAny<int>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public void SnapshotStore_ReplayTickMath_ResolveTick_PrioritizesExplicitTickOverSecondsOverMsOverTimestamp()
    {
        // When all 4 properties exist, 'tick' has highest precedence
        var allArgs = MakeArgs(new
        {
            tick = 500L,
            timeSeconds = 10.0,
            timeMs = 3000L,
            timestamp = "00:05.000"
        });
        ReplayTickMath.ResolveTick(allArgs).Should().Be(500L);

        // When tick is omitted, timeSeconds has second precedence (10.0s * 20 = 200 ticks)
        var secArgs = MakeArgs(new
        {
            timeSeconds = 10.0,
            timeMs = 3000L,
            timestamp = "00:05.000"
        });
        ReplayTickMath.ResolveTick(secArgs).Should().Be(200L);

        // When tick and seconds are omitted, timeMs has third precedence (3000ms / 50 = 60 ticks)
        var msArgs = MakeArgs(new
        {
            timeMs = 3000L,
            timestamp = "00:05.000"
        });
        ReplayTickMath.ResolveTick(msArgs).Should().Be(60L);

        // When only timestamp exists, parses string (00:05.000 = 5.0s * 20 = 100 ticks)
        var tsArgs = MakeArgs(new
        {
            timestamp = "00:05.000"
        });
        ReplayTickMath.ResolveTick(tsArgs).Should().Be(100L);
    }

    [Theory]
    [InlineData(-10.0, 0L)]
    [InlineData(double.NaN, 0L)]
    [InlineData(double.PositiveInfinity, 0L)]
    [InlineData(double.NegativeInfinity, 0L)]
    public void SnapshotStore_ReplayTickMath_FromSeconds_InvalidAndNegativeValues_ClampedToZero(double input, long expected)
    {
        ReplayTickMath.FromSeconds(input).Should().Be(expected);
    }

    [Theory]
    [InlineData(-100L, 0L)]
    [InlineData(0L, 0L)]
    [InlineData(24L, 0L)]   // 24 / 50 = 0.48 -> 0
    [InlineData(25L, 1L)]   // 25 / 50 = 0.50 -> 1 (rounds away from zero)
    [InlineData(74L, 1L)]   // 74 / 50 = 1.48 -> 1
    [InlineData(75L, 2L)]   // 75 / 50 = 1.50 -> 2
    public void ReplayTickMath_FromMilliseconds_RoundsFractionalTicksAccurately(long ms, long expectedTicks)
    {
        ReplayTickMath.FromMilliseconds(ms).Should().Be(expectedTicks);
    }

    [Theory]
    [InlineData("not_a_timestamp", 0L)]
    [InlineData("", 0L)]
    [InlineData("   ", 0L)]
    [InlineData("-01:30", 600L)] // negative minute clamped to 0, 30s = 600 ticks
    [InlineData("01:00", 1200L)] // 1 minute = 1200 ticks
    [InlineData("01:00:00", 72000L)] // 1 hour = 72000 ticks
    public void ReplayTickMath_FromFormattedTimestamp_HandlesMalformedAndEdgeCases(string ts, long expectedTicks)
    {
        ReplayTickMath.FromFormattedTimestamp(ts).Should().Be(expectedTicks);
    }

    [Fact]
    public async Task SnapshotStore_Delete_NonExistentId_ReturnsSnapshotNotFound()
    {
        GameSnapshotHandler handler = new();

        var result = await handler.HandleAsync(MakeArgs(new
        {
            action = "delete",
            id = "ghost_snapshot_id"
        }));

        result.IsError.Should().BeTrue();
        JsonElement root = ParseResult(result);
        root.GetProperty("status").GetString().Should().Be("error");
        root.GetProperty("errorCode").GetString().Should().Be("SNAPSHOT_NOT_FOUND");
    }

    [Fact]
    public async Task SnapshotStore_Delete_MissingId_ReturnsError()
    {
        GameSnapshotHandler handler = new();

        var result = await handler.HandleAsync(MakeArgs(new
        {
            action = "delete"
        }));

        result.IsError.Should().BeTrue();
        result.Content[0].As<TextContentItem>().Text.Should().Contain("Missing required parameter 'id'");
    }

    [Fact]
    public async Task SnapshotStore_Restore_NonExistentIdInPhase3_ReturnsSnapshotNotFound()
    {
        Mock<IGameIpcBridge> mockIpc = new();
        mockIpc.SetupGet(i => i.IsConnected).Returns(true);
        mockIpc.Setup(i => i.ExecuteCommandAsync(It.Is<string>(s => s.Contains("GetCurrentPhase")), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync("3");

        GameSnapshotHandler handler = new(gameIpcBridge: mockIpc.Object);

        var result = await handler.HandleAsync(MakeArgs(new
        {
            action = "restore",
            id = "non_existent_snap_999"
        }));

        result.IsError.Should().BeTrue();
        JsonElement root = ParseResult(result);
        root.GetProperty("status").GetString().Should().Be("error");
        root.GetProperty("errorCode").GetString().Should().Be("SNAPSHOT_NOT_FOUND");
    }

    [Fact]
    public async Task SnapshotStore_SnapshotStore_HighConcurrency30ParallelTasks_NoDictionaryCorruptionOrDeadlock()
    {
        GameSnapshotHandler handler = new();

        const int taskCount = 30;
        Task[] tasks = new Task[taskCount];

        for (int i = 0; i < taskCount; i++)
        {
            int index = i;
            tasks[i] = Task.Run(async () =>
            {
                string id = $"concurrent_snap_{index}";

                // 1. Save
                await handler.HandleAsync(MakeArgs(new
                {
                    action = "save",
                    id,
                    level = (index % 25) + 1,
                    gold = index * 100
                }));

                // 2. List
                await handler.HandleAsync(MakeArgs(new { action = "list" }));

                // 3. Delete half of them
                if (index % 2 == 0)
                {
                    await handler.HandleAsync(MakeArgs(new { action = "delete", id }));
                }
            });
        }

        await Task.WhenAll(tasks);

        // Store count must accurately match the surviving half (15 snapshots)
        handler.SnapshotStore.Count.Should().Be(15);
    }

    [Fact]
    public void SnapshotStore_GameSnapshotDto_ClampingRules_LevelGoldCoordinatesItemSlots()
    {
        GameSnapshotDto dto = new();

        // Level clamped [1, 25]
        dto.Level = 0;
        dto.Level.Should().Be(1);
        dto.Level = 99;
        dto.Level.Should().Be(25);

        // Gold clamped >= 0
        dto.Gold = -500;
        dto.Gold.Should().Be(0);

        // Coordinates clamped [-32768, 32768]
        dto.X = 50000.0;
        dto.X.Should().Be(32768.0);
        dto.Y = -40000.0;
        dto.Y.Should().Be(-32768.0);

        // Items: slot clamped [0, 11], charges >= 1, inStash set when slot >= 6
        ItemDto item = new("Item_RunesOfTheBlight", slot: 15, charges: 0);
        item.Slot.Should().Be(11);
        item.Charges.Should().Be(1);
        item.InStash.Should().BeTrue("Slots >= 6 represent stash");
    }

    [Fact]
    public void SnapshotStore_GameSnapshotDto_ToLuaSnapshotTable_EscapesSingleQuotesInItemNames()
    {
        GameSnapshotDto dto = new()
        {
            Gold = 1000,
            Level = 5,
            X = 100.5,
            Y = 200.5,
            Z = 50.0
        };
        dto.Player.Items.Add(new ItemDto("Item_King's_Armor", 0));

        string lua = dto.ToLuaSnapshotTable();

        lua.Should().Contain(@"'Item_King\'s_Armor'", "Single quotes inside item names must be backslash-escaped for Lua table syntax");
        lua.Should().Contain("gold = 1000");
        lua.Should().Contain("level = 5");
    }

    [Fact]
    public async Task SnapshotStore_SeekReplay_DispatchesSequentialReplaySetFrameAndSeekWithExactTick()
    {
        List<string> dispatched = [];
        Mock<IGameIpcBridge> mockIpc = new();
        mockIpc.SetupGet(i => i.IsConnected).Returns(true);
        mockIpc.Setup(i => i.ExecuteCommandAsync(It.IsAny<string>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .Callback<string, int, CancellationToken>((cmd, _, _) => dispatched.Add(cmd))
            .ReturnsAsync("OK");

        GameSnapshotHandler handler = new(gameIpcBridge: mockIpc.Object);

        var result = await handler.HandleAsync(MakeArgs(new
        {
            action = "seek",
            timeSeconds = 12.5 // 12.5s * 20 ticks/sec = 250 ticks
        }));

        result.IsError.Should().BeFalse();
        JsonElement root = ParseResult(result);
        root.GetProperty("status").GetString().Should().Be("success");
        root.GetProperty("tick").GetInt64().Should().Be(250L);
        root.GetProperty("timeSeconds").GetDouble().Should().Be(12.5);

        dispatched.Should().Equal("ReplaySetFrame 250", "Seek 250");
    }

    [Fact]
    public async Task SnapshotStore_Restore_IpcFailure_ReturnsIpcDispatchFailedError()
    {
        Mock<IGameIpcBridge> mockIpc = new();
        mockIpc.SetupGet(i => i.IsConnected).Returns(true);
        mockIpc.Setup(i => i.ExecuteCommandAsync(It.Is<string>(s => s.Contains("GetCurrentPhase")), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync("3");
        mockIpc.Setup(i => i.ExecuteCommandAsync(It.Is<string>(s => s.Contains("ApplySnapshot")), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new TimeoutException("IPC call timed out waiting for engine response"));

        GameSnapshotHandler handler = new(gameIpcBridge: mockIpc.Object);
        handler.SnapshotStore["snap_fail"] = new GameSnapshotDto { Id = "snap_fail" };

        var result = await handler.HandleAsync(MakeArgs(new
        {
            action = "restore",
            id = "snap_fail"
        }));

        result.IsError.Should().BeTrue();
        JsonElement root = ParseResult(result);
        root.GetProperty("status").GetString().Should().Be("error");
        root.GetProperty("errorCode").GetString().Should().Be("IPC_DISPATCH_FAILED");
        root.GetProperty("message").GetString().Should().Contain("Failed to restore snapshot over IPC");
    }

    [Fact]
    public async Task SnapshotStore_Seek_IpcFailure_ReturnsIpcDispatchFailedError()
    {
        Mock<IGameIpcBridge> mockIpc = new();
        mockIpc.SetupGet(i => i.IsConnected).Returns(true);
        mockIpc.Setup(i => i.ExecuteCommandAsync(It.IsAny<string>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new IOException("Pipe write fault"));

        GameSnapshotHandler handler = new(gameIpcBridge: mockIpc.Object);

        var result = await handler.HandleAsync(MakeArgs(new
        {
            action = "seek",
            tick = 100L
        }));

        result.IsError.Should().BeTrue();
        JsonElement root = ParseResult(result);
        root.GetProperty("status").GetString().Should().Be("error");
        root.GetProperty("errorCode").GetString().Should().Be("IPC_DISPATCH_FAILED");
    }

    #endregion
}
