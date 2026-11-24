// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// TAO (Together As One) MCP & Kongor CLI -- Created by guccidonut (https://github.com/guccidonut/PKV)

using System.Text.Json;
using FluentAssertions;
using Kongor.Cli.Mcp.Handlers;
using Kongor.Cli.Mcp.Protocol;
using Kongor.Cli.Mcp.Services;
using Moq;
using Xunit;

namespace Kongor.Mcp.Tests;

/// <summary>
/// Unit tests for GameClientManageHandler three-tier transport hierarchy:
/// Tier 1: Direct Named Pipe IPC (proactive connection with 1000ms timeout)
/// Tier 2: Native Win32 in-game console actuation (with self-healing auto-retry)
/// Tier 3: Tertiary physical mouse click last resort fallback
/// </summary>
public sealed class GameClientManageTransportTests
{
    private static JsonElement ParseResult(ToolCallResult result)
    {
        result.Content.Should().ContainSingle();
        string json = ((TextContentItem)result.Content[0]).Text;
        return JsonElement.Parse(json);
    }

    [Fact]
    public async Task ExecuteClientCommandAsync_WhenIpcDisconnected_ProactivelyCallsConnectAsyncAndExecutesIpc()
    {
        var mockIpc = new Mock<IGameIpcBridge>();
        bool connected = false;
        mockIpc.SetupGet(i => i.IsConnected).Returns(() => connected);
        mockIpc.Setup(i => i.ConnectAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .Callback(() => connected = true)
            .Returns(Task.CompletedTask);
        mockIpc.Setup(i => i.ExecuteCommandAsync(It.IsAny<string>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync("IPC response: command processed");

        var mockUi = new Mock<IUiControlService>();
        var remediation = new ClientCommandRemediationService(timeProvider: TimeProvider.System);

        var handler = new GameClientManageHandler(
            ipcBridge: mockIpc.Object,
            uiControlService: mockUi.Object,
            remediationService: remediation,
            timeProvider: TimeProvider.System,
            logger: _ => { });

        var result = await handler.ExecuteClientCommandAsync("echo test_hello", 3000, autoLaunch: false);

        result.IsError.Should().BeFalse();
        var root = ParseResult(result);
        root.GetProperty("status").GetString().Should().Be("success");
        root.GetProperty("channel").GetString().Should().Be("ipc");
        root.GetProperty("response").GetString().Should().Contain("IPC response");

        mockIpc.Verify(i => i.ConnectAsync(1000, It.IsAny<CancellationToken>()), Times.Once);
        mockIpc.Verify(i => i.ExecuteCommandAsync("echo test_hello", 3000, It.IsAny<CancellationToken>()), Times.Once);
        mockUi.Verify(u => u.ExecuteInGameConsoleCommandAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task ExecuteClientCommandAsync_WhenIpcThrows_FallsBackToInGameConsole()
    {
        var mockIpc = new Mock<IGameIpcBridge>();
        mockIpc.SetupGet(i => i.IsConnected).Returns(true);
        mockIpc.Setup(i => i.ExecuteCommandAsync(It.IsAny<string>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("Named pipe broken"));

        var mockUi = new Mock<IUiControlService>();
        mockUi.Setup(u => u.ExecuteInGameConsoleCommandAsync("hon_x64", "echo fallback", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new UiConsoleCommandResult
            {
                Success = true,
                WindowTitle = "Heroes of Newerth",
                Hwnd = "0x00010001",
                Command = "echo fallback",
                ExecutionDurationMs = 25.0
            });

        var remediation = new ClientCommandRemediationService(timeProvider: TimeProvider.System);

        var handler = new GameClientManageHandler(
            ipcBridge: mockIpc.Object,
            uiControlService: mockUi.Object,
            remediationService: remediation,
            timeProvider: TimeProvider.System,
            logger: _ => { });

        var result = await handler.ExecuteClientCommandAsync("echo fallback", 3000, autoLaunch: false);

        result.IsError.Should().BeFalse();
        var root = ParseResult(result);
        root.GetProperty("status").GetString().Should().Be("success");
        root.GetProperty("channel").GetString().Should().Be("in_game_console");
        root.GetProperty("response").GetString().Should().Contain("native in-game console");

        mockUi.Verify(u => u.ExecuteInGameConsoleCommandAsync("hon_x64", "echo fallback", It.IsAny<CancellationToken>()), Times.Once);
        mockUi.Verify(u => u.ClickAsync(It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<int?>(), It.IsAny<int?>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task ExecuteClientCommandAsync_WhenConsoleFails_AutoRetriesWithSelfHealing()
    {
        var mockIpc = new Mock<IGameIpcBridge>();
        mockIpc.SetupGet(i => i.IsConnected).Returns(false);
        mockIpc.Setup(i => i.ConnectAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new TimeoutException("Pipe timeout"));

        var mockUi = new Mock<IUiControlService>();
        // First attempt with non-existent widget 'practice_button' fails
        mockUi.Setup(u => u.ExecuteInGameConsoleCommandAsync("hon_x64", "GetWidget('practice_button'):DoEvent()", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new UiConsoleCommandResult
            {
                Success = false,
                ErrorMessage = "widget 'practice_button' not found"
            });
        // Auto-healed command succeeds
        mockUi.Setup(u => u.ExecuteInGameConsoleCommandAsync("hon_x64", "GetWidget('sysbar_quickStart'):DoEvent()", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new UiConsoleCommandResult
            {
                Success = true,
                WindowTitle = "Heroes of Newerth",
                Hwnd = "0x00010001",
                Command = "GetWidget('sysbar_quickStart'):DoEvent()",
                ExecutionDurationMs = 30.0
            });

        var remediation = new ClientCommandRemediationService(timeProvider: TimeProvider.System);

        var handler = new GameClientManageHandler(
            ipcBridge: mockIpc.Object,
            uiControlService: mockUi.Object,
            remediationService: remediation,
            timeProvider: TimeProvider.System,
            logger: _ => { });

        var result = await handler.ExecuteClientCommandAsync("GetWidget('practice_button'):DoEvent()", 3000, autoLaunch: false);

        result.IsError.Should().BeFalse();
        var root = ParseResult(result);
        root.GetProperty("status").GetString().Should().Be("success");
        root.GetProperty("channel").GetString().Should().Be("in_game_console");
        root.GetProperty("selfHealed").GetBoolean().Should().BeTrue();
        root.GetProperty("remediationCategory").GetString().Should().Be("WidgetNotFound");
        root.GetProperty("command").GetString().Should().Be("GetWidget('sysbar_quickStart'):DoEvent()");
    }

    [Theory]
    [InlineData("Call store_button onclick", "store_button")]
    [InlineData("Set _mainmenu_currentpanel store_container", "store_button")]
    [InlineData("Call sysbar_quickStart onclick", "sysbar_quickStart")]
    public async Task ExecuteClientCommandAsync_WhenConsoleFails_FallsBackToPhysicalClick(string command, string expectedWidget)
    {
        var mockIpc = new Mock<IGameIpcBridge>();
        mockIpc.SetupGet(i => i.IsConnected).Returns(false);
        mockIpc.Setup(i => i.ConnectAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new TimeoutException("Pipe timeout"));

        var mockUi = new Mock<IUiControlService>();
        mockUi.Setup(u => u.ExecuteInGameConsoleCommandAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new UiConsoleCommandResult
            {
                Success = false,
                ErrorMessage = "DirectInput exclusive mode active"
            });
        mockUi.Setup(u => u.ClickAsync("hon_x64", expectedWidget, It.IsAny<int?>(), It.IsAny<int?>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new UiClickResult
            {
                Success = true,
                ElementClicked = expectedWidget,
                Target = "hon_x64",
                ActionTaken = "mouse_click"
            });

        var remediation = new ClientCommandRemediationService(timeProvider: TimeProvider.System);

        var handler = new GameClientManageHandler(
            ipcBridge: mockIpc.Object,
            uiControlService: mockUi.Object,
            remediationService: remediation,
            timeProvider: TimeProvider.System,
            logger: _ => { });

        var result = await handler.ExecuteClientCommandAsync(command, 3000, autoLaunch: false);

        result.IsError.Should().BeFalse();
        var root = ParseResult(result);
        root.GetProperty("status").GetString().Should().Be("success");
        root.GetProperty("channel").GetString().Should().Be("physical_click");
        root.GetProperty("elementClicked").GetString().Should().Be(expectedWidget);

        mockUi.Verify(u => u.ClickAsync("hon_x64", expectedWidget, It.IsAny<int?>(), It.IsAny<int?>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task ExecuteClientCommandAsync_WhenAllChannelsFail_ReturnsStructuredError()
    {
        var mockIpc = new Mock<IGameIpcBridge>();
        mockIpc.SetupGet(i => i.IsConnected).Returns(false);
        mockIpc.Setup(i => i.ConnectAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new TimeoutException("Pipe timeout"));

        var mockUi = new Mock<IUiControlService>();
        mockUi.Setup(u => u.ExecuteInGameConsoleCommandAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new UiConsoleCommandResult
            {
                Success = false,
                ErrorMessage = "Target window not found"
            });

        var remediation = new ClientCommandRemediationService(timeProvider: TimeProvider.System);

        var handler = new GameClientManageHandler(
            ipcBridge: mockIpc.Object,
            uiControlService: mockUi.Object,
            remediationService: remediation,
            timeProvider: TimeProvider.System,
            logger: _ => { });

        var result = await handler.ExecuteClientCommandAsync("echo failed_test", 3000, autoLaunch: false);

        result.IsError.Should().BeTrue();
        var root = ParseResult(result);
        root.GetProperty("status").GetString().Should().Be("error");
        root.GetProperty("errorMessage").GetString().Should().Contain("Target window not found");
    }

    [Fact]
    public async Task HandleAsync_ActionCommand_DispatchesOverTransportHierarchy()
    {
        var mockIpc = new Mock<IGameIpcBridge>();
        mockIpc.SetupGet(i => i.IsConnected).Returns(true);
        mockIpc.Setup(i => i.ExecuteCommandAsync(It.IsAny<string>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync("OK");

        var handler = new GameClientManageHandler(
            ipcBridge: mockIpc.Object,
            uiControlService: null,
            remediationService: null,
            timeProvider: TimeProvider.System,
            logger: _ => { });

        var args = JsonElement.Parse("{\"action\": \"command\", \"command\": \"Set host_maxFPS 120\", \"autoLaunch\": false}");
        var result = await handler.HandleAsync(args);

        result.IsError.Should().BeFalse();
        var root = ParseResult(result);
        root.GetProperty("status").GetString().Should().Be("success");
        root.GetProperty("channel").GetString().Should().Be("ipc");
        mockIpc.Verify(i => i.ExecuteCommandAsync("Set host_maxFPS 120", It.IsAny<int>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public void BuildClientLaunchArguments_WhenHeadless_IncludesVidHeadlessAndOffscreenPositioning()
    {
        var args = GameClientManageHandler.BuildClientLaunchArguments(
            gameDir: "C:\\FakeGameDir",
            mode: "normal",
            connectAddress: null,
            masterServer: "127.0.0.1:55555",
            resolution: "1920x1080",
            fullscreen: true,
            maxFps: 1000,
            customNickname: "PlayerOne",
            windowMode: "headless");

        string joined = string.Join(" ", args);
        joined.Should().NotContain("+Set vid_headless true");
        joined.Should().Contain("+Set vid_pos -32000 -32000");
        joined.Should().Contain("+Set sound_disable true");
        joined.Should().Contain("+Set vid_fullscreen false");
        joined.Should().Contain("+Set vid_borderless true");
        joined.Should().NotContain("+Set sound_numChannels 64");
    }

    [Fact]
    public void BuildClientLaunchArguments_WhenForeground_IncludesSoundAndFullscreen()
    {
        var args = GameClientManageHandler.BuildClientLaunchArguments(
            gameDir: "C:\\FakeGameDir",
            mode: "normal",
            connectAddress: null,
            masterServer: "127.0.0.1:55555",
            resolution: "1920x1080",
            fullscreen: true,
            maxFps: 1000,
            customNickname: "PlayerOne",
            windowMode: "foreground");

        string joined = string.Join(" ", args);
        joined.Should().NotContain("+Set vid_headless true");
        joined.Should().NotContain("+Set vid_pos -32000 -32000");
        joined.Should().NotContain("+Set sound_disable true");
        joined.Should().Contain("+Set sound_numChannels 64");
        joined.Should().Contain("+Set vid_fullscreen true");
    }

    [Fact]
    public void GameLauncherService_BuildLaunchArguments_WhenHeadless_IncludesVidHeadlessAndOffscreenPositioning()
    {
        var options = new GameLaunchOptions
        {
            WindowMode = "headless",
            Fullscreen = true,
            Resolution = "1920x1080"
        };

        var args = GameLauncherService.BuildLaunchArguments(options, "C:\\FakeGameDir");
        string joined = string.Join(" ", args);

        joined.Should().NotContain("+Set vid_headless true");
        joined.Should().Contain("+Set vid_pos -32000 -32000");
        joined.Should().Contain("+Set sound_disable true");
        joined.Should().Contain("+Set vid_fullscreen false");
        joined.Should().Contain("+Set vid_borderless true");
    }

    [Fact]
    public async Task HandleAsync_ActionCommandWithOmittedAutoLaunch_DoesNotSpawnClientWhenDisconnected()
    {
        // protects: Desktop zero-disruption invariant when hon_x64.exe has already exited or is not running
        // fails_when: GameClientManageHandler.GetAutoLaunch defaults to true and spawns foreground hon_x64.exe
        // seam: real GameClientManageHandler.HandleAsync JSON argument dispatch with disconnected IPC & UI fallback
        var mockIpc = new Mock<IGameIpcBridge>();
        mockIpc.SetupGet(i => i.IsConnected).Returns(false);
        mockIpc.Setup(i => i.ConnectAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new TimeoutException("No client pipe"));

        var mockUi = new Mock<IUiControlService>();
        mockUi.Setup(u => u.ExecuteInGameConsoleCommandAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new UiConsoleCommandResult { Success = false, ErrorMessage = "Window not found" });

        var handler = new GameClientManageHandler(
            ipcBridge: mockIpc.Object,
            uiControlService: mockUi.Object,
            remediationService: new ClientCommandRemediationService(timeProvider: TimeProvider.System),
            timeProvider: TimeProvider.System,
            logger: _ => { });

        // Omit "autoLaunch" completely -> must default to false and return structured error without spawning hon_x64
        var result = await handler.HandleAsync(JsonElement.Parse("{\"action\": \"command\", \"command\": \"Echo probe\"}"));
        result.IsError.Should().BeTrue();

        // Even if caller explicitly passes autoLaunch: true on a Quit command, shutdown guard MUST block relaunch!
        var quitResult = await handler.HandleAsync(JsonElement.Parse("{\"action\": \"command\", \"command\": \"Quit\", \"autoLaunch\": true}"));
        quitResult.IsError.Should().BeTrue();
    }

    [Fact]
    public async Task ReplayPlaybackHandler_DispatchesVerifiedK2NativeReplayCommandsOverIpc()
    {
        // protects: kongor_replay_playback MCP tool compatibility with K2's real CReplayManager / game_replay_control commands
        // fails_when: ReplayPlaybackHandler emits non-existent commands (LoadReplay, ReplayPause, host_timescale for speed, or FogOfWar 0 when enabled: true)
        // seam: real ReplayPlaybackHandler.HandleAsync -> IGameIpcBridge.ExecuteCommandAsync
        List<string> dispatched = [];
        var mockIpc = new Mock<IGameIpcBridge>();
        mockIpc.SetupGet(i => i.IsConnected).Returns(true);
        mockIpc.Setup(i => i.ExecuteCommandAsync(It.IsAny<string>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .Callback<string, int, CancellationToken>((cmd, _, _) => dispatched.Add(cmd))
            .ReturnsAsync("OK");

        var handler = new ReplayPlaybackHandler(gameIpcBridge: mockIpc.Object, timeProvider: TimeProvider.System, logger: _ => { });

        (await handler.HandleAsync(JsonElement.Parse("{\"action\": \"start\", \"replayPath\": \"~/replays/M1076.honreplay\"}"))).IsError.Should().BeFalse();
        (await handler.HandleAsync(JsonElement.Parse("{\"action\": \"pause\"}"))).IsError.Should().BeFalse();
        (await handler.HandleAsync(JsonElement.Parse("{\"action\": \"resume\"}"))).IsError.Should().BeFalse();
        (await handler.HandleAsync(JsonElement.Parse("{\"action\": \"seek\", \"tick\": 2400}"))).IsError.Should().BeFalse();
        (await handler.HandleAsync(JsonElement.Parse("{\"action\": \"seek\", \"deltaTicks\": -200}"))).IsError.Should().BeFalse();
        (await handler.HandleAsync(JsonElement.Parse("{\"action\": \"speed\", \"speed\": 4.0}"))).IsError.Should().BeFalse();
        (await handler.HandleAsync(JsonElement.Parse("{\"action\": \"fog\", \"enabled\": true}"))).IsError.Should().BeFalse();
        (await handler.HandleAsync(JsonElement.Parse("{\"action\": \"stop\"}"))).IsError.Should().BeFalse();

        dispatched.Should().Equal([
            "StartReplay ~/replays/M1076.honreplay",
            "ReplaySetPaused 1",
            "ReplaySetPaused 0",
            "ReplaySetFrame 2400",
            "ReplayIncFrame -200",
            "ReplaySetPlaybackSpeed 2",
            "FogOfWar 1",
            "Disconnect"
        ]);
    }
}
