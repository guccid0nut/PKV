// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// TAO (Together As One) MCP & Kongor CLI -- Created by guccidonut (https://github.com/guccidonut/PKV)

using System.Text;
using System.Text.Json;
using FluentAssertions;
using Kongor.Cli.Mcp.Handlers;
using Kongor.Cli.Mcp.Protocol;
using Kongor.Cli.Mcp.Services;
using Moq;
using Xunit;

namespace Kongor.Mcp.Tests;

public sealed class NexusMailboxIpcTests : IDisposable
{
    private readonly string _testMailboxDir;

    public NexusMailboxIpcTests()
    {
        _testMailboxDir = Path.Combine(Path.GetTempPath(), "nexus_mailbox_test_" + Guid.NewGuid().ToString("N"));
        if (!Directory.Exists(_testMailboxDir))
        {
            Directory.CreateDirectory(_testMailboxDir);
        }
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_testMailboxDir))
            {
                Directory.Delete(_testMailboxDir, recursive: true);
            }
        }
        catch { }
    }

    private static async Task SimulateEngineResponseAsync(
        string mailboxDir,
        Func<JsonElement, (bool success, object? result, string? error)> handler,
        CancellationToken ct)
    {
        string reqFile = Path.Combine(mailboxDir, "req_current.lua");
        while (!ct.IsCancellationRequested)
        {
            if (File.Exists(reqFile))
            {
                try
                {
                    using var fs = new FileStream(reqFile, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                    using var reader = new StreamReader(fs, Encoding.UTF8);
                    string lua = await reader.ReadToEndAsync(ct);
                    if (string.IsNullOrWhiteSpace(lua))
                    {
                        await Task.Delay(5, ct);
                        continue;
                    }

                    int startIdx = lua.IndexOf("[====[", StringComparison.Ordinal);
                    int endIdx = lua.IndexOf("]====]", StringComparison.Ordinal);
                    string? json = null;
                    if (startIdx >= 0 && endIdx > startIdx)
                    {
                        json = lua.Substring(startIdx + "[====[".Length, endIdx - startIdx - "[====[".Length);
                    }
                    if (string.IsNullOrWhiteSpace(json))
                    {
                        await Task.Delay(5, ct);
                        continue;
                    }

                    using var doc = JsonDocument.Parse(json);
                    int id = doc.RootElement.GetProperty("id").GetInt32();
                    string type = doc.RootElement.GetProperty("type").GetString()!;

                    var (success, result, error) = handler(doc.RootElement);

                    string resJson = JsonSerializer.Serialize(new
                    {
                        id,
                        type,
                        success,
                        result,
                        error,
                        timestamp = TimeProvider.System.GetUtcNow().ToUnixTimeSeconds()
                    });

                    string resFile = Path.Combine(mailboxDir, $"res_{id}.json");
                    string tmpFile = resFile + ".tmp";
                    await File.WriteAllTextAsync(tmpFile, resJson, ct);
                    File.Move(tmpFile, resFile, overwrite: true);

                    try { File.Delete(reqFile); } catch { }
                    break;
                }
                catch (Exception ex) when (ex is IOException or JsonException)
                {
                    await Task.Delay(5, ct);
                }
            }
            await Task.Delay(5, ct);
        }
    }

    [Fact]
    public async Task NexusMailboxIpcBridge_ExecuteCommandAsync_WritesRequestAndParsesResponse()
    {
        await using var bridge = new NexusMailboxIpcBridge(_testMailboxDir);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var simTask = SimulateEngineResponseAsync(_testMailboxDir, root =>
        {
            string cmd = root.GetProperty("command").GetString()!;
            return (true, $"Executed: {cmd}", null);
        }, cts.Token);

        IpcMessage? receivedIpcMessage = null;
        bridge.OnMessageReceived += msg => receivedIpcMessage = msg;

        string output = await bridge.ExecuteCommandAsync("echo mailbox_hello", timeoutMs: 10000);
        await simTask;

        output.Should().Be("Executed: echo mailbox_hello");
        receivedIpcMessage.Should().NotBeNull();
        receivedIpcMessage!.OpCode.Should().Be(IpcOpCode.CommandResponse);
        receivedIpcMessage.PayloadString.Should().Contain("Executed: echo mailbox_hello");
    }

    [Fact]
    public async Task NexusMailboxIpcBridge_ExecuteCommandAsync_WhenEngineReturnsError_ReturnsErrorString()
    {
        await using var bridge = new NexusMailboxIpcBridge(_testMailboxDir);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var simTask = SimulateEngineResponseAsync(_testMailboxDir, _ =>
            (false, null, "Unknown cvar or nil function"), cts.Token);

        string output = await bridge.ExecuteCommandAsync("bad_cmd", timeoutMs: 10000);
        await simTask;

        output.Should().StartWith("[ERROR]");
        output.Should().Contain("Unknown cvar or nil function");
    }

    [Fact]
    public async Task NexusMailboxIpcBridge_ExecuteCommandAsync_WhenTimeout_ThrowsTimeoutException()
    {
        await using var bridge = new NexusMailboxIpcBridge(_testMailboxDir);

        Func<Task> act = async () => await bridge.ExecuteCommandAsync("echo timeout_test", timeoutMs: 150);
        await act.Should().ThrowAsync<TimeoutException>()
            .WithMessage("*timed out*");
    }

    [Fact]
    public async Task NexusMailboxIpcBridge_DispatchUiEventAsync_WritesUiRequestAndReturnsSuccess()
    {
        await using var bridge = new NexusMailboxIpcBridge(_testMailboxDir);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var simTask = SimulateEngineResponseAsync(_testMailboxDir, root =>
        {
            root.GetProperty("type").GetString().Should().Be("ui");
            root.GetProperty("widget").GetString().Should().Be("sysbar_quickStart");
            root.GetProperty("interface").GetString().Should().Be("main");
            return (true, new { widget = "sysbar_quickStart", eventType = "click" }, null);
        }, cts.Token);

        string output = await bridge.DispatchUiEventAsync("main", "sysbar_quickStart", "click", timeoutMs: 10000);
        await simTask;

        output.Should().Contain("sysbar_quickStart");
        output.Should().Contain("Nexus Mailbox IPC");
    }

    [Fact]
    public async Task NexusMailboxIpcBridge_PingAsync_ReturnsRoundtripMicroseconds()
    {
        await using var bridge = new NexusMailboxIpcBridge(_testMailboxDir);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var simTask = SimulateEngineResponseAsync(_testMailboxDir, root =>
        {
            root.GetProperty("type").GetString().Should().Be("ping");
            return (true, new { phase = 3 }, null);
        }, cts.Token);

        long roundtripUs = await bridge.PingAsync(timeoutMs: 10000);
        await simTask;

        roundtripUs.Should().BeGreaterThanOrEqualTo(0);
    }

    [Fact]
    public async Task CompositeGameIpcBridge_WhenPrimaryConnected_RoutesCommandToPrimary()
    {
        var mockPrimary = new Mock<IGameIpcBridge>();
        mockPrimary.SetupGet(p => p.IsConnected).Returns(true);
        mockPrimary.Setup(p => p.ExecuteCommandAsync("echo test_primary", 3000, It.IsAny<CancellationToken>()))
            .ReturnsAsync("primary_response");

        var mockSecondary = new Mock<IGameIpcBridge>();
        mockSecondary.SetupGet(s => s.IsConnected).Returns(false);

        await using var composite = new CompositeGameIpcBridge(mockPrimary.Object, mockSecondary.Object);

        string response = await composite.ExecuteCommandAsync("echo test_primary", 3000);

        response.Should().Be("primary_response");
        mockPrimary.Verify(p => p.ExecuteCommandAsync("echo test_primary", 3000, It.IsAny<CancellationToken>()), Times.Once);
        mockSecondary.Verify(s => s.ExecuteCommandAsync(It.IsAny<string>(), It.IsAny<int>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task CompositeGameIpcBridge_WhenPrimaryDisconnected_RoutesCommandToSecondary()
    {
        var mockPrimary = new Mock<IGameIpcBridge>();
        mockPrimary.SetupGet(p => p.IsConnected).Returns(false);

        var mockSecondary = new Mock<IGameIpcBridge>();
        mockSecondary.SetupGet(s => s.IsConnected).Returns(true);
        mockSecondary.Setup(s => s.ExecuteCommandAsync("echo test_secondary", 3000, It.IsAny<CancellationToken>()))
            .ReturnsAsync("secondary_response");

        await using var composite = new CompositeGameIpcBridge(mockPrimary.Object, mockSecondary.Object);

        string response = await composite.ExecuteCommandAsync("echo test_secondary", 3000);

        response.Should().Be("secondary_response");
        mockSecondary.Verify(s => s.ExecuteCommandAsync("echo test_secondary", 3000, It.IsAny<CancellationToken>()), Times.Once);
        mockPrimary.Verify(p => p.ExecuteCommandAsync(It.IsAny<string>(), It.IsAny<int>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task CompositeGameIpcBridge_DispatchUiEventAsync_AlwaysRoutesToSecondaryMailbox()
    {
        var mockPrimary = new Mock<IGameIpcBridge>();
        var mockSecondary = new Mock<IGameIpcBridge>();
        mockSecondary.SetupGet(s => s.IsConnected).Returns(true);
        mockSecondary.Setup(s => s.DispatchUiEventAsync("main", "btn_play", "click", null, 3000, It.IsAny<CancellationToken>()))
            .ReturnsAsync("dispatched_via_secondary");

        await using var composite = new CompositeGameIpcBridge(mockPrimary.Object, mockSecondary.Object);

        string response = await composite.DispatchUiEventAsync("main", "btn_play", "click", null, 3000);

        response.Should().Be("dispatched_via_secondary");
        mockSecondary.Verify(s => s.DispatchUiEventAsync("main", "btn_play", "click", null, 3000, It.IsAny<CancellationToken>()), Times.Once);
        mockPrimary.Verify(p => p.DispatchUiEventAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<int?>(), It.IsAny<int>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task GameClientManageHandler_WhenKeystrokeSuppressed_DoesNotStealFocusOrSendKeystrokes()
    {
        var mockIpc = new Mock<IGameIpcBridge>();
        mockIpc.SetupGet(i => i.IsConnected).Returns(false);
        mockIpc.Setup(i => i.ConnectAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new TimeoutException("Pipe timeout"));

        var mockUi = new Mock<IUiControlService>();
        var remediation = new ClientCommandRemediationService(timeProvider: TimeProvider.System);

        var handler = new GameClientManageHandler(
            ipcBridge: mockIpc.Object,
            uiControlService: mockUi.Object,
            remediationService: remediation,
            timeProvider: TimeProvider.System,
            logger: _ => { });

        // allowKeystrokeFallback: false
        var result = await handler.ExecuteClientCommandAsync("echo test_suppress", 3000, autoLaunch: false, allowKeystrokeFallback: false);

        result.IsError.Should().BeTrue();
        string json = ((TextContentItem)result.Content[0]).Text;
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        root.GetProperty("status").GetString().Should().Be("error");
        root.GetProperty("channel").GetString().Should().Be("none");
        root.GetProperty("remediationCategory").GetString().Should().Be("KeystrokeFallbackSuppressed");
        root.GetProperty("errorMessage").GetString().Should().Contain("suppressed to prevent desktop focus theft");

        // UI control service MUST NEVER be called
        mockUi.Verify(u => u.ExecuteInGameConsoleCommandAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
        mockUi.Verify(u => u.ClickAsync(It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<int?>(), It.IsAny<int?>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task GameLuaHandler_WhenKeystrokeSuppressed_DoesNotActuateInGameConsole()
    {
        var mockIpc = new Mock<IGameIpcBridge>();
        mockIpc.SetupGet(i => i.IsConnected).Returns(false);

        var mockUi = new Mock<IUiControlService>();
        var remediation = new ClientCommandRemediationService(timeProvider: TimeProvider.System);

        var handler = new GameLuaHandler(
            clientManageHandler: null,
            serverManageHandler: null,
            gameIpcBridge: mockIpc.Object,
            uiControlService: mockUi.Object,
            remediationService: remediation,
            timeProvider: TimeProvider.System,
            logger: _ => { });

        var args = JsonElement.Parse("{\"action\": \"code\", \"code\": \"Echo('test')\", \"allow_keystroke_fallback\": false}");
        var result = await handler.HandleAsync(args);

        result.IsError.Should().BeTrue();
        string json = ((TextContentItem)result.Content[0]).Text;
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        root.GetProperty("status").GetString().Should().Be("error");
        root.GetProperty("remediationCategory").GetString().Should().Be("KeystrokeFallbackSuppressed");

        mockUi.Verify(u => u.ExecuteInGameConsoleCommandAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task UiFlowHandler_WhenKeystrokeSuppressed_DoesNotActuateInGameConsole()
    {
        var mockIpc = new Mock<IGameIpcBridge>();
        mockIpc.SetupGet(i => i.IsConnected).Returns(false);

        var mockUi = new Mock<IUiControlService>();
        var remediation = new ClientCommandRemediationService(timeProvider: TimeProvider.System);

        var handler = new UiFlowHandler(
            uiControlService: mockUi.Object,
            remediationService: remediation,
            gameIpcBridge: mockIpc.Object,
            timeProvider: TimeProvider.System,
            logger: _ => { });

        var args = JsonElement.Parse("{\"action\": \"open_store\", \"allow_keystroke_fallback\": false}");
        var result = await handler.HandleAsync(args);

        result.IsError.Should().BeTrue();
        string json = ((TextContentItem)result.Content[0]).Text;
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        root.GetProperty("status").GetString().Should().Be("error");
        root.GetProperty("remediationCategory").GetString().Should().Be("KeystrokeFallbackSuppressed");

        mockUi.Verify(u => u.ExecuteInGameConsoleCommandAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task NexusMailboxIpcBridge_DetectsHeartbeatFile_AndMarksConnected()
    {
        await using var bridge = new NexusMailboxIpcBridge(_testMailboxDir);

        // Before heartbeat: not connected (isolated test directory with no client)
        bridge.IsConnected.Should().BeFalse();

        // Engine writes heartbeat.json
        string heartbeatFile = Path.Combine(_testMailboxDir, "heartbeat.json");
        await File.WriteAllTextAsync(heartbeatFile, "{\"status\":\"alive\",\"timestamp\":12345,\"ticks\":42}");

        // Now bridge detects recent mailbox activity!
        bridge.IsConnected.Should().BeTrue();
    }

    [Fact]
    public async Task NexusMailboxIpcBridge_ExecuteCommandAsync_WhenResponseInConsoleLog_ParsesAndSucceeds()
    {
        await using var bridge = new NexusMailboxIpcBridge(_testMailboxDir);

        string consoleLog = Path.Combine(_testMailboxDir, "console.log");
        await File.WriteAllTextAsync(consoleLog, "[12:00:00] Initial engine log line\n");

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));

        // Simulate engine reading req_current.lua and appending [NEXUS_MAILBOX_RES] to console.log
        var simTask = Task.Run(async () =>
        {
            string reqLua = Path.Combine(_testMailboxDir, "req_current.lua");
            while (!cts.IsCancellationRequested)
            {
                if (File.Exists(reqLua))
                {
                    try
                    {
                        using var fs = new FileStream(reqLua, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                        using var reader = new StreamReader(fs, Encoding.UTF8);
                        string lua = await reader.ReadToEndAsync(cts.Token);
                        if (lua.Contains("ProcessFromJson") && lua.TrimEnd().EndsWith('}'))
                        {
                            var idMatch = System.Text.RegularExpressions.Regex.Match(lua, @"""id""\s*:\s*(\d+)");
                            int reqId = idMatch.Success ? int.Parse(idMatch.Groups[1].Value) : 1;
                            // Append response in standard chunk format to console.log
                            string line = $"[12:00:01] [NEXUS_MAILBOX_RES] {reqId} 1/1 {{\"id\":{reqId},\"type\":\"command\",\"success\":true,\"result\":\"Echoed from console.log!\",\"timestamp\":12345}}\n";
                            using var logFs = new FileStream(consoleLog, FileMode.Append, FileAccess.Write, FileShare.ReadWrite);
                            using var logWriter = new StreamWriter(logFs, new UTF8Encoding(false));
                            await logWriter.WriteAsync(line);
                            await logWriter.FlushAsync(cts.Token);
                            break;
                        }
                    }
                    catch (IOException)
                    {
                        await Task.Delay(5, cts.Token);
                    }
                }
                await Task.Delay(5, cts.Token);
            }
        }, cts.Token);

        string output = await bridge.ExecuteCommandAsync("echo test_stream", timeoutMs: 10000);
        await simTask;

        output.Should().Be("Echoed from console.log!");
    }

    [Fact]
    public async Task NexusMailboxIpcBridge_ExecuteCommandAsync_WhenMultiChunkResponseInConsoleLog_ReassemblesAndSucceeds()
    {
        await using var bridge = new NexusMailboxIpcBridge(_testMailboxDir);

        string consoleLog = Path.Combine(_testMailboxDir, "console.log");
        await File.WriteAllTextAsync(consoleLog, "[12:00:00] Initial engine log line\n");

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));

        // Simulate engine reading req_current.lua and appending multi-chunk response to console.log
        var simTask = Task.Run(async () =>
        {
            string reqLua = Path.Combine(_testMailboxDir, "req_current.lua");
            while (!cts.IsCancellationRequested)
            {
                if (File.Exists(reqLua))
                {
                    try
                    {
                        using var fs = new FileStream(reqLua, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                        using var reader = new StreamReader(fs, Encoding.UTF8);
                        string lua = await reader.ReadToEndAsync(cts.Token);
                        if (lua.Contains("ProcessFromJson") && lua.TrimEnd().EndsWith('}'))
                        {
                            var idMatch = System.Text.RegularExpressions.Regex.Match(lua, @"""id""\s*:\s*(\d+)");
                            int reqId = idMatch.Success ? int.Parse(idMatch.Groups[1].Value) : 1;
                            // Append 3 separate chunks for this response
                            string chunk1 = $"[12:00:01] [NEXUS_MAILBOX_RES] {reqId} 1/3 {{\"id\":{reqId},\"type\":\"command\",\n";
                            string chunk2 = $"[12:00:01] [NEXUS_MAILBOX_RES] {reqId} 2/3 \"success\":true,\"result\":\"MultiChunkSuccess\",\n";
                            string chunk3 = $"[12:00:01] [NEXUS_MAILBOX_RES] {reqId} 3/3 \"timestamp\":12345}}\n";
                            foreach (string chunk in new[] { chunk1, chunk2, chunk3 })
                            {
                                using var logFs = new FileStream(consoleLog, FileMode.Append, FileAccess.Write, FileShare.ReadWrite);
                                using var logWriter = new StreamWriter(logFs, new UTF8Encoding(false));
                                await logWriter.WriteAsync(chunk);
                                await logWriter.FlushAsync(cts.Token);
                                await Task.Delay(10, cts.Token);
                            }
                            break;
                        }
                    }
                    catch (IOException)
                    {
                        await Task.Delay(5, cts.Token);
                    }
                }
                await Task.Delay(5, cts.Token);
            }
        }, cts.Token);

        string output = await bridge.ExecuteCommandAsync("echo test_multi_chunk", timeoutMs: 10000);
        await simTask;

        output.Should().Be("MultiChunkSuccess");
    }

    [Trait("Category", "Manual")]
    [Fact]
    public async Task NexusMailboxIpcBridge_ExecuteCommandAsync_WhenResponseInUnicodeConsoleLog_ParsesAndSucceeds()
    {
        if (!NexusTestEnvironment.IsManualTestEnabled) return;

        await using var bridge = new NexusMailboxIpcBridge(_testMailboxDir);

        string consoleLog = Path.Combine(_testMailboxDir, "console.log");
        // K2 writes console.log with UTF-16 LE BOM
        await File.WriteAllTextAsync(consoleLog, "[12:00:00] Initial engine log line in Unicode\r\n", Encoding.Unicode);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(8));

        // Simulate engine appending [NEXUS_MAILBOX_RES] in UTF-16 LE
        var simTask = Task.Run(async () =>
        {
            string reqLua = Path.Combine(_testMailboxDir, "req_current.lua");
            while (!cts.IsCancellationRequested)
            {
                if (File.Exists(reqLua))
                {
                    try
                    {
                        using var fs = new FileStream(reqLua, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                        using var reader = new StreamReader(fs, Encoding.UTF8);
                        string lua = await reader.ReadToEndAsync(cts.Token);
                        if (lua.Contains("ProcessFromJson"))
                        {
                            var idMatch = System.Text.RegularExpressions.Regex.Match(lua, @"""id""\s*:\s*(\d+)");
                            int reqId = idMatch.Success ? int.Parse(idMatch.Groups[1].Value) : 1;
                            // Append response in UTF-16 LE (Unicode) as K2 Echo() does
                            string line = $"[12:00:01] [NEXUS_MAILBOX_RES] {reqId} 1/1 {{\"id\":{reqId},\"type\":\"command\",\"success\":true,\"result\":\"Echoed from Unicode console.log!\",\"timestamp\":12345}}\r\n";
                            await File.AppendAllTextAsync(consoleLog, line, Encoding.Unicode, cts.Token);
                            break;
                        }
                    }
                    catch (IOException)
                    {
                        await Task.Delay(5, cts.Token);
                    }
                }
                await Task.Delay(5, cts.Token);
            }
        }, cts.Token);

        string output = await bridge.ExecuteCommandAsync("echo test_unicode_stream", timeoutMs: 6000);
        await simTask;

        output.Should().Be("Echoed from Unicode console.log!");
    }

    [Trait("Category", "Manual")]
    [Fact]
    public async Task NexusMailboxIpcBridge_LiveGameClient_PingAndCommand_SucceedsEndToEnd()
    {
        if (!NexusTestEnvironment.IsManualTestEnabled) return;
        if (System.Diagnostics.Process.GetProcessesByName("hon_x64").Length == 0) return;

        await using var bridge = new NexusMailboxIpcBridge();
        bridge.ForceConnected = true;
        await bridge.ConnectAsync(timeoutMs: 3000);

        bridge.IsConnected.Should().BeTrue();

        // 1. Live Ping
        long pingUs = await bridge.PingAsync(timeoutMs: 3000);
        pingUs.Should().BeGreaterThan(0);

        // 2. Live Command
        string output = await bridge.ExecuteCommandAsync("Echo live_mailbox_verified", timeoutMs: 3000);
        output.Should().NotBeNullOrWhiteSpace();

        // 3. Live Lua
        string luaRes = await bridge.ExecuteLuaAsync("return 'LUA_OK: ' .. tostring(Nexus ~= nil)", timeoutMs: 3000);
        luaRes.Should().Contain("LUA_OK: true");
    }
}


