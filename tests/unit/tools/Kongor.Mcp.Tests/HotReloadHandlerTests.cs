// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// TAO (Together As One) MCP & Kongor CLI -- Created by guccidonut (https://github.com/guccidonut/PKV)

using System.Diagnostics;
using System.Text.Json;
using FluentAssertions;
using Kongor.Cli.Mcp.Handlers;
using Kongor.Cli.Mcp.Protocol;
using Kongor.Cli.Mcp.Services;
using Moq;
using Xunit;

namespace Kongor.Mcp.Tests;

public sealed class HotReloadHandlerTests
{
    private static (HotReloadHandler Handler, Mock<IHotReloadService> MockService, Mock<IGameIpcBridge> MockIpc) CreateHandlerWithMocks(
        bool isConnected = true,
        string ipcResponse = "OK: Reload complete")
    {
        Mock<IGameIpcBridge> mockIpc = new();
        mockIpc.SetupGet(i => i.IsConnected).Returns(isConnected);
        mockIpc.Setup(i => i.ExecuteCommandAsync(It.IsAny<string>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(ipcResponse);

        Mock<IHotReloadService> mockService = new();
        mockService.SetupGet(s => s.IsWatching).Returns(true);
        mockService.SetupGet(s => s.WatchedPaths).Returns(["games/shared/ui/", "games/shared/scripts/"]);
        mockService.SetupGet(s => s.DebounceMs).Returns(150);
        mockService.SetupGet(s => s.TotalReloads).Returns(1);
        mockService.SetupGet(s => s.LastReloadDurationMs).Returns(12.5);
        mockService.SetupGet(s => s.LastChangedFiles).Returns(["games/shared/ui/main.interface"]);

        mockService.Setup(s => s.GetStatus()).Returns(new HotReloadStatusDto
        {
            Status = "success",
            IsWatching = true,
            WatchedPaths = ["games/shared/ui/", "games/shared/scripts/"],
            DebounceMs = 150,
            TotalReloads = 1,
            TotalFileEvents = 1,
            LastReloadDurationMs = 12.5,
            LastChangedFiles = ["games/shared/ui/main.interface"]
        });

        ClientCommandRemediationService remediation = new(timeProvider: TimeProvider.System);
        HotReloadHandler handler = new(
            hotReloadService: mockService.Object,
            gameIpcBridge: mockIpc.Object,
            remediationService: remediation,
            timeProvider: TimeProvider.System,
            logger: _ => { });

        return (handler, mockService, mockIpc);
    }

    #region Category 1: HotReloadService Core State & IPC Command Sequencing

    [Fact]
    public void HotReloadService_InitialState_ReflectsDefaults()
    {
        Mock<IGameIpcBridge> mockIpc = new();
        using HotReloadService service = new(mockIpc.Object);

        service.IsWatching.Should().BeFalse();
        service.DebounceMs.Should().Be(150);
        service.TotalReloads.Should().Be(0);
        service.TotalFileEvents.Should().Be(0);
        service.LastReloadDurationMs.Should().Be(0.0);
        service.LastChangedFiles.Should().BeEmpty();
        service.LastError.Should().BeNull();
    }

    [Fact]
    public async Task HotReloadService_TriggerReloadAsync_DispatchesDualCommandsInExactSequence()
    {
        Mock<IGameIpcBridge> mockIpc = new();
        List<string> dispatched = [];

        mockIpc.SetupGet(x => x.IsConnected).Returns(true);
        mockIpc.Setup(x => x.ExecuteCommandAsync(It.IsAny<string>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .Callback<string, int, CancellationToken>((cmd, _, _) => dispatched.Add(cmd))
            .ReturnsAsync("OK");

        using HotReloadService service = new(mockIpc.Object);
        var result = await service.TriggerReloadAsync(["games/shared/ui/main.interface"]);

        result.Success.Should().BeTrue();
        result.DurationMs.Should().BeLessThan(2000.0);
        dispatched.Should().Equal("ReloadInterfaces", "Script dofile('/ui/scripts/nexus_bridge.lua')");
        service.TotalReloads.Should().Be(1);
        service.LastChangedFiles.Should().Contain("games/shared/ui/main.interface");
    }

    [Fact]
    public async Task HotReloadService_TriggerReloadAsync_IpcException_HandlesGracefullyWithoutThrowing()
    {
        Mock<IGameIpcBridge> mockIpc = new();
        mockIpc.SetupGet(x => x.IsConnected).Returns(true);
        mockIpc.Setup(x => x.ExecuteCommandAsync(It.IsAny<string>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new TimeoutException("IPC pipe response timed out."));

        using HotReloadService service = new(mockIpc.Object);
        var result = await service.TriggerReloadAsync(["test.lua"]);

        result.Success.Should().BeFalse();
        result.Error.Should().Contain("IPC pipe response timed out.");
        service.LastError.Should().Contain("IPC pipe response timed out.");
        service.TotalReloads.Should().Be(0);
    }

    #endregion

    #region Category 2: Debounce Windowing (150ms Sliding Window & Event Coalescing)

    [Fact]
    public async Task HotReloadService_RapidFileModifications_DebouncesToExactlyOneReload()
    {
        Mock<IGameIpcBridge> mockIpc = new();
        mockIpc.SetupGet(i => i.IsConnected).Returns(true);
        mockIpc.Setup(i => i.ExecuteCommandAsync(It.IsAny<string>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync("OK");

        using HotReloadService service = new(mockIpc.Object);
        TaskCompletionSource<HotReloadResultDto> reloadTcs = new();
        service.OnReloadCompleted += res => reloadTcs.TrySetResult(res);

        service.StartWatching(customPaths: ["games/shared/ui/"], debounceMs: 300);

        // Burst 5 rapid file modifications
        for (int i = 0; i < 5; i++)
        {
            service.NotifyFileChanged($"games/shared/ui/dialog_{i}.interface");
        }

        // Wait for debounce window to fire (300ms from last event)
        var result = await reloadTcs.Task.WaitAsync(TimeSpan.FromSeconds(5));

        result.Success.Should().BeTrue();
        // Exactly 1 dispatch for all 5 events!
        mockIpc.Verify(i => i.ExecuteCommandAsync("ReloadInterfaces", It.IsAny<int>(), It.IsAny<CancellationToken>()), Times.Once);
        service.TotalReloads.Should().Be(1);
        service.LastChangedFiles.Should().HaveCount(5);
    }

    [Fact]
    public async Task HotReloadService_EventsOutsideDebounceWindow_TriggersSeparateReloads()
    {
        Mock<IGameIpcBridge> mockIpc = new();
        mockIpc.SetupGet(i => i.IsConnected).Returns(true);
        mockIpc.Setup(i => i.ExecuteCommandAsync(It.IsAny<string>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync("OK");

        using HotReloadService service = new(mockIpc.Object);

        // Use 50ms debounce for fast dual-burst test
        service.StartWatching(customPaths: ["games/shared/ui/"], debounceMs: 50);

        TaskCompletionSource<bool> tcs1 = new();
        Action<HotReloadResultDto> handler1 = _ => tcs1.TrySetResult(true);
        service.OnReloadCompleted += handler1;

        // Burst 1
        service.NotifyFileChanged("games/shared/ui/menu1.xml");
        service.NotifyFileChanged("games/shared/ui/menu2.xml");
        await tcs1.Task.WaitAsync(TimeSpan.FromSeconds(2));

        service.OnReloadCompleted -= handler1;
        TaskCompletionSource<bool> tcs2 = new();
        service.OnReloadCompleted += _ => tcs2.TrySetResult(true);

        // Pause 75ms (greater than 50ms debounce window)
        await Task.Delay(75);

        // Burst 2
        service.NotifyFileChanged("games/shared/ui/menu3.xml");
        await tcs2.Task.WaitAsync(TimeSpan.FromSeconds(2));

        // Exactly 2 dispatches total
        mockIpc.Verify(i => i.ExecuteCommandAsync("ReloadInterfaces", It.IsAny<int>(), It.IsAny<CancellationToken>()), Times.Exactly(2));
        service.TotalReloads.Should().Be(2);
    }

    #endregion

    #region Category 3: HotReloadHandler MCP Actions & Parameter Parsing

    [Fact]
    public async Task HotReloadHandler_StatusAction_ReturnsValidJsonStructure()
    {
        var (handler, _, _) = CreateHandlerWithMocks();

        using var req = JsonDocument.Parse("""{ "action": "status" }""");
        ToolCallResult result = await handler.HandleAsync(req.RootElement);

        result.IsError.Should().BeFalse();
        string json = ((TextContentItem)result.Content[0]).Text;
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        root.GetProperty("status").GetString().Should().Be("success");
        root.GetProperty("isWatching").GetBoolean().Should().BeTrue();
        root.GetProperty("debounceMs").GetInt32().Should().Be(150);
        root.GetProperty("totalReloads").GetInt32().Should().Be(1);
        root.GetProperty("watchedPaths").GetArrayLength().Should().Be(2);
        root.GetProperty("lastChangedFiles").GetArrayLength().Should().Be(1);
    }

    [Fact]
    public async Task HotReloadHandler_WatchAndUnwatch_TogglesWatchingState()
    {
        Mock<IGameIpcBridge> mockIpc = new();
        string tempDir = Path.Combine(Path.GetTempPath(), "HotReloadTest_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);

        try
        {
            using HotReloadService service = new(mockIpc.Object);
            HotReloadHandler handler = new(service);

            string watchJson = JsonSerializer.Serialize(new
            {
                action = "watch",
                paths = new[] { tempDir },
                debounceMs = 120
            });

            ToolCallResult watchResult = await handler.HandleAsync(JsonElement.Parse(watchJson));
            watchResult.IsError.Should().BeFalse();
            service.IsWatching.Should().BeTrue();
            service.DebounceMs.Should().Be(120);

            ToolCallResult unwatchResult = await handler.HandleAsync(JsonElement.Parse("""{ "action": "unwatch" }"""));
            unwatchResult.IsError.Should().BeFalse();
            service.IsWatching.Should().BeFalse();
        }
        finally
        {
            if (Directory.Exists(tempDir))
            {
                Directory.Delete(tempDir, true);
            }
        }
    }

    [Fact]
    public async Task HotReloadHandler_TriggerAction_ExecutesReloadAndReturnsMetrics()
    {
        var (handler, mockService, _) = CreateHandlerWithMocks();
        mockService.Setup(s => s.TriggerReloadAsync(It.IsAny<IEnumerable<string>?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new HotReloadResultDto
            {
                Success = true,
                DurationMs = 15.2,
                ChangedFiles = ["games/shared/ui/main.interface"],
                DispatchedCommands = ["ReloadInterfaces", "Script dofile('/ui/scripts/nexus_bridge.lua')"],
                CommandResponses = ["OK", "OK"]
            });

        using var req = JsonDocument.Parse("""{ "action": "trigger" }""");
        ToolCallResult result = await handler.HandleAsync(req.RootElement);

        result.IsError.Should().BeFalse();
        string json = ((TextContentItem)result.Content[0]).Text;
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        root.GetProperty("status").GetString().Should().Be("success");
        root.GetProperty("lastReloadDurationMs").GetDouble().Should().BeApproximately(15.2, 0.1);
        root.GetProperty("dispatchedCommands").GetArrayLength().Should().Be(2);
        mockService.Verify(s => s.TriggerReloadAsync(It.IsAny<IEnumerable<string>?>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task HotReloadHandler_TriggerAction_WhenIpcFails_ReturnsRemediatedError()
    {
        Mock<IGameIpcBridge> mockIpc = new();
        mockIpc.SetupGet(i => i.IsConnected).Returns(false);
        mockIpc.Setup(i => i.ConnectAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new IOException("Pipe server not found"));

        using HotReloadService service = new(mockIpc.Object);
        HotReloadHandler handler = new(service);

        using var req = JsonDocument.Parse("""{ "action": "trigger" }""");
        ToolCallResult result = await handler.HandleAsync(req.RootElement);

        result.IsError.Should().BeTrue();
        string json = ((TextContentItem)result.Content[0]).Text;
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        root.GetProperty("status").GetString().Should().Be("error");
        root.GetProperty("error").GetString().Should().Contain("Pipe server not found");
        root.TryGetProperty("remediationHint", out _).Should().BeTrue();
    }

    [Fact]
    public async Task HotReloadHandler_InvalidAction_ReturnsErrorResultWithRemediation()
    {
        var (handler, _, _) = CreateHandlerWithMocks();

        using var req = JsonDocument.Parse("""{ "action": "explode" }""");
        ToolCallResult result = await handler.HandleAsync(req.RootElement);

        result.IsError.Should().BeTrue();
        string json = ((TextContentItem)result.Content[0]).Text;
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        root.GetProperty("status").GetString().Should().Be("error");
        root.GetProperty("error").GetString().Should().Contain("Unsupported action 'explode'");
        root.GetProperty("remediationCategory").GetString().Should().Be("MalformedCommandArguments");
    }

    #endregion

    #region Category 4: McpToolRegistry Integration & Tool Schema

    [Fact]
    public void McpToolRegistry_WhenInvoked_RegistersHotReloadToolAndAliases()
    {
        McpToolRegistry registry = new(registerDefaults: true);

        string[] toolAliases = ["kongor_hot_reload", "kongor_reload", "kongor_ui_reload"];
        foreach (string tool in toolAliases)
        {
            registry.HasTool(tool).Should().BeTrue($"Registry must contain '{tool}'");
            var def = registry.GetTool(tool);
            def.Should().NotBeNull();
            def!.Description.Should().NotBeNullOrWhiteSpace();
            def.InputSchema.Should().NotBeNull();
            def.InputSchema.Properties.Should().ContainKey("action");
            def.InputSchema.Properties.Should().ContainKey("paths");
            def.InputSchema.Properties.Should().ContainKey("debounceMs");
        }
    }

    [Fact]
    public async Task McpToolRegistry_ExecuteHotReloadTool_DispatchesViaRegistryAndAlias()
    {
        Mock<IGameIpcBridge> mockIpc = new();
        mockIpc.SetupGet(x => x.IsConnected).Returns(true);
        mockIpc.Setup(x => x.ExecuteCommandAsync(It.IsAny<string>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync("OK");

        using HotReloadService service = new(mockIpc.Object);
        HotReloadHandler handler = new(service);

        McpToolRegistry registry = new(
            registerDefaults: true,
            hotReloadService: service,
            hotReloadHandler: handler);

        // Test canonical execution
        using var req1 = JsonDocument.Parse("""{ "action": "status" }""");
        ToolCallResult res1 = await registry.ExecuteToolAsync("kongor_hot_reload", req1.RootElement);
        res1.IsError.Should().BeFalse();
        string json1 = ((TextContentItem)res1.Content[0]).Text;
        using var doc1 = JsonDocument.Parse(json1);
        doc1.RootElement.GetProperty("status").GetString().Should().Be("success");

        // Test alias execution
        using var req2 = JsonDocument.Parse("""{ "action": "status" }""");
        ToolCallResult res2 = await registry.ExecuteToolAsync("kongor_reload", req2.RootElement);
        res2.IsError.Should().BeFalse();
        string json2 = ((TextContentItem)res2.Content[0]).Text;
        using var doc2 = JsonDocument.Parse(json2);
        doc2.RootElement.GetProperty("status").GetString().Should().Be("success");
    }

    #endregion

    #region Category 5: Fast Inner-Loop Execution & Zero Desktop GUI Windows

    [Fact]
    public async Task HotReloadHandler_WhenInvoked_AllTestsRunFastUnderManualGatingThreshold()
    {
        var (handler, _, _) = CreateHandlerWithMocks();
        Stopwatch sw = Stopwatch.StartNew();

        for (int i = 0; i < 25; i++)
        {
            using var req = JsonDocument.Parse("""{ "action": "status" }""");
            var res = await handler.HandleAsync(req.RootElement);
            res.IsError.Should().BeFalse();
        }

        sw.Stop();
        // 25 status queries must execute in < 2000ms under parallel assembly load
        sw.ElapsedMilliseconds.Should().BeLessThan(2000);
    }

    #endregion
}
