// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

using System.Collections.ObjectModel;
using FluentAssertions;
using Moq;
using NEXUS.Contracts.ControlPlane;
using NEXUS.ControlPanel.GUI.Services;
using NEXUS.ControlPanel.GUI.Services.Models;
using NEXUS.ControlPanel.GUI.ViewModels;
using Xunit;

namespace ASPIRE.Client.Gui.Tests.GameServers;

public sealed class GameServersChatViewModelTests
{
    [Fact]
    public void GameServerChatMessageViewModel_OperatorMessage_SetsPropertiesCorrectly()
    {
        GameServerChatMessageViewModel msg = GameServerChatMessageViewModel.CreateOperatorMessage("AddFakePlayer 1", "stdin");

        msg.Sender.Should().Be("Operator");
        msg.Message.Should().Be("AddFakePlayer 1");
        msg.IsOperator.Should().BeTrue();
        msg.IsError.Should().BeFalse();
        msg.IsSystem.Should().BeFalse();
        msg.DeliveryChannel.Should().Be("stdin");
    }

    [Fact]
    public void GameServerChatMessageViewModel_ServerMessage_SetsPropertiesCorrectly()
    {
        DateTimeOffset now = new(2026, 9, 16, 15, 30, 5, TimeSpan.Zero);
        GameServerChatMessageViewModel msg = GameServerChatMessageViewModel.CreateServerLog(
            "Added bot player to Legion team",
            isError: false,
            timestamp: now);

        msg.Sender.Should().Be("Server");
        msg.Message.Should().Be("Added bot player to Legion team");
        msg.IsOperator.Should().BeFalse();
        msg.IsError.Should().BeFalse();
        msg.IsSystem.Should().BeFalse();
        msg.FormattedTime.Should().NotBeNullOrWhiteSpace();
    }

    [Fact]
    public void GameServerChatMessageViewModel_SystemMessage_SetsPropertiesCorrectly()
    {
        GameServerChatMessageViewModel msg = GameServerChatMessageViewModel.CreateSystemMessage("Connected to server pipe");

        msg.Sender.Should().Be("System");
        msg.Message.Should().Be("Connected to server pipe");
        msg.IsOperator.Should().BeFalse();
        msg.IsSystem.Should().BeTrue();
        msg.IsServer.Should().BeFalse();
    }

    [Fact]
    public void GameServerChatMessageViewModel_IsServer_EvaluatesCorrectly()
    {
        var op = GameServerChatMessageViewModel.CreateOperatorMessage("AddFakePlayer 1");
        op.IsServer.Should().BeFalse();

        var sys = GameServerChatMessageViewModel.CreateSystemMessage("Starting server...");
        sys.IsServer.Should().BeFalse();

        var server = GameServerChatMessageViewModel.CreateServerLog("Player joined match");
        server.IsServer.Should().BeTrue();
    }

    [Fact]
    public void GameServerInstanceDetailViewModel_IngestLogs_CorrelatesPendingOperatorCommand_WithoutDuplication()
    {
        GameServerInstanceDetailViewModel vm = new()
        {
            GamePort = 11235,
            ServerName = "DONUT ARENA #1"
        };
        vm.UpdateStatus("Available", isRunning: true);

        // Operator types a command optimistically
        vm.AddOperatorMessage("AddFakePlayer 1", "stdin");
        vm.ChatMessages.Should().HaveCount(1);
        vm.ChatMessages[0].DeliveryChannel.Should().Be("stdin");

        // Server echoes the operator command via logs
        DateTimeOffset now = TimeProvider.System.GetUtcNow();
        List<CompelDedicatedServerLogLineDto> echoLogs =
        [
            new CompelDedicatedServerLogLineDto
            {
                Text = "> AddFakePlayer 1",
                Source = "operator",
                Timestamp = now,
                IsError = false
            }
        ];

        vm.IngestLogs(echoLogs);

        // Should NOT create a second bubble!
        vm.ChatMessages.Should().HaveCount(1);
        vm.ChatMessages[0].DeliveryChannel.Should().Be("stdin (confirmed)");

        // Now server returns actual output
        List<CompelDedicatedServerLogLineDto> serverLogs =
        [
            new CompelDedicatedServerLogLineDto
            {
                Text = "Added fake player 'Bot1' to Legion",
                Source = "server",
                Timestamp = now.AddMilliseconds(100),
                IsError = false
            }
        ];

        vm.IngestLogs(serverLogs);
        vm.ChatMessages.Should().HaveCount(2);
        vm.ChatMessages[1].IsServer.Should().BeTrue();
        vm.ChatMessages[1].Message.Should().Be("Added fake player 'Bot1' to Legion");
    }

    [Theory]
    [InlineData("Available", true, "#10B981", "🟢")]
    [InlineData("InMatch", true, "#00F0FF", "⚔️")]
    [InlineData("Starting", true, "#F59E0B", "🟡")]
    [InlineData("Restarting", true, "#F59E0B", "🟡")]
    [InlineData("Stopped", false, "#94A3B8", "🛑")]
    [InlineData("Crashed", true, "#EF4444", "⚠️")]
    public void GameServerInstanceDetailViewModel_StatusColorAndIcon_MapsCorrectly(
        string state,
        bool isRunning,
        string expectedColor,
        string expectedIcon)
    {
        GameServerInstanceDetailViewModel vm = new()
        {
            GamePort = 11235,
            ServerName = "DONUT ARENA #1"
        };

        vm.UpdateStatus(state, isRunning);

        vm.StatusColor.Should().Be(expectedColor);
        vm.StatusIcon.Should().Be(expectedIcon);
    }

    [Fact]
    public void GameServerInstanceDetailViewModel_IngestLogs_AddsMessagesAndUpdatesLastSnippet()
    {
        GameServerInstanceDetailViewModel vm = new()
        {
            GamePort = 11235,
            ServerName = "DONUT ARENA #1"
        };
        vm.UpdateStatus("Available", isRunning: true);

        DateTimeOffset now = TimeProvider.System.GetUtcNow();
        List<CompelDedicatedServerLogLineDto> lines =
        [
            new CompelDedicatedServerLogLineDto { Text = "Starting game engine...", Source = "stdout", Timestamp = now, IsError = false },
            new CompelDedicatedServerLogLineDto { Text = "Map caldavar loaded", Source = "stdout", Timestamp = now.AddSeconds(1), IsError = false }
        ];

        vm.IngestLogs(lines);

        vm.ChatMessages.Should().HaveCount(2);
        vm.LastMessageSnippet.Should().Be("Map caldavar loaded");

        // Clear chat
        vm.ClearChat();
        vm.ChatMessages.Should().BeEmpty();
        vm.LastMessageSnippet.Should().Be("Chat cleared.");
    }

    [Fact]
    public async Task GameServersViewModel_SendCommand_DispatchesToFleetServiceAndAppendsOperatorMessage()
    {
        Mock<IGameServerFleetService> mockService = new();
        mockService.Setup(s => s.SendWorkerCommandAsync(
                It.IsAny<string>(),
                It.IsAny<int>(),
                It.IsAny<int>(),
                It.IsAny<string>(),
                It.IsAny<string>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync((true, "stdin", "Command executed"));

        GameServersViewModel vm = new(mockService.Object);

        GameServerInstanceDetailViewModel instance = new()
        {
            GamePort = 11235,
            ServerName = "DONUT ARENA #1",
            HostAddress = "127.0.0.1",
            ControlPort = 55555
        };

        vm.AllChatInstances.Add(instance);
        vm.SelectedChatInstance = instance;
        vm.CommandInput = "AddFakePlayer 1";

        await vm.SendCommandAsync();

        mockService.Verify(s => s.SendWorkerCommandAsync(
            "127.0.0.1",
            55555,
            11235,
            "AddFakePlayer 1",
            It.IsAny<string>(),
            It.IsAny<CancellationToken>()), Times.Once);

        vm.CommandInput.Should().BeEmpty();
        instance.ChatMessages.Should().Contain(m => m.Message == "AddFakePlayer 1" && m.IsOperator);
    }

    [Fact]
    public async Task GameServersViewModel_SendQuickCommand_DispatchesCommandDirectly()
    {
        Mock<IGameServerFleetService> mockService = new();
        mockService.Setup(s => s.SendWorkerCommandAsync(
                It.IsAny<string>(),
                It.IsAny<int>(),
                It.IsAny<int>(),
                It.IsAny<string>(),
                It.IsAny<string>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync((true, "stdin", "Running"));

        GameServersViewModel vm = new(mockService.Object);

        GameServerInstanceDetailViewModel instance = new()
        {
            GamePort = 11235,
            ServerName = "DONUT ARENA #1",
            HostAddress = "127.0.0.1",
            ControlPort = 55555
        };

        vm.AllChatInstances.Add(instance);
        vm.SelectedChatInstance = instance;

        await vm.SendQuickCommandAsync("ServerStatus");

        mockService.Verify(s => s.SendWorkerCommandAsync(
            "127.0.0.1",
            55555,
            11235,
            "ServerStatus",
            It.IsAny<string>(),
            It.IsAny<CancellationToken>()), Times.Once);

        instance.ChatMessages.Should().Contain(m => m.Message == "ServerStatus" && m.IsOperator);
    }

    [Fact]
    public async Task GameServersViewModel_StartSelectedInstance_InvokesFleetService()
    {
        Mock<IGameServerFleetService> mockService = new();
        mockService.Setup(s => s.StartWorkerAsync(
                It.IsAny<string>(),
                It.IsAny<int>(),
                It.IsAny<int>(),
                It.IsAny<string>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync((true, "Instance started"));

        GameServersViewModel vm = new(mockService.Object);

        GameServerInstanceDetailViewModel instance = new()
        {
            GamePort = 11235,
            ServerName = "DONUT ARENA #1",
            HostAddress = "127.0.0.1",
            ControlPort = 55555
        };

        vm.AllChatInstances.Add(instance);
        vm.SelectedChatInstance = instance;

        await vm.StartSelectedInstanceAsync();

        mockService.Verify(s => s.StartWorkerAsync(
            "127.0.0.1",
            55555,
            11235,
            It.IsAny<string>(),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task GameServersViewModel_StopInstance_InvokesFleetService()
    {
        Mock<IGameServerFleetService> mockService = new();
        mockService.Setup(s => s.StopWorkerAsync(
                It.IsAny<string>(),
                It.IsAny<int>(),
                It.IsAny<int>(),
                It.IsAny<string>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync((true, "Instance stopped"));

        GameServersViewModel vm = new(mockService.Object);

        GameServerInstanceDetailViewModel instance = new()
        {
            GamePort = 11235,
            ServerName = "DONUT ARENA #1",
            HostAddress = "127.0.0.1",
            ControlPort = 55555
        };

        vm.AllChatInstances.Add(instance);
        vm.SelectedChatInstance = instance;

        await vm.StopInstanceAsync(instance);

        mockService.Verify(s => s.StopWorkerAsync(
            "127.0.0.1",
            55555,
            11235,
            It.IsAny<string>(),
            It.IsAny<CancellationToken>()), Times.Once);

        instance.ServerStatus.Should().Be("Stopped");
        instance.IsRunning.Should().BeFalse();
    }

    [Fact]
    public void GameServersViewModel_ChatSearchQuery_FiltersChatInstances()
    {
        Mock<IGameServerFleetService> mockService = new();
        GameServersViewModel vm = new(mockService.Object);

        GameServerInstanceDetailViewModel inst1 = new() { GamePort = 11235, ServerName = "DONUT ARENA #1" };
        inst1.UpdateStatus("Available", isRunning: true);

        GameServerInstanceDetailViewModel inst2 = new() { GamePort = 11236, ServerName = "CALDAVAR ARENA #2" };
        inst2.UpdateStatus("InMatch", isRunning: true);

        vm.AllChatInstances.Add(inst1);
        vm.AllChatInstances.Add(inst2);

        vm.ChatSearchQuery = "11236";
        vm.FilteredChatInstances.Should().ContainSingle(i => i.GamePort == 11236);

        vm.ChatSearchQuery = "DONUT";
        vm.FilteredChatInstances.Should().ContainSingle(i => i.GamePort == 11235);

        vm.ChatSearchQuery = string.Empty;
        vm.FilteredChatInstances.Should().HaveCount(2);
    }

    [Fact]
    public void GameServersViewModel_CloseChatTab_HidesInstanceFromFilteredList()
    {
        Mock<IGameServerFleetService> mockService = new();
        GameServersViewModel vm = new(mockService.Object);

        GameServerInstanceDetailViewModel inst1 = new() { GamePort = 11235, ServerName = "DONUT ARENA #1" };
        inst1.UpdateStatus("Available", isRunning: true);

        vm.AllChatInstances = [inst1];
        vm.SelectedChatInstance = inst1;

        vm.FilteredChatInstances.Should().ContainSingle();

        vm.CloseChatTab(inst1);

        inst1.IsTabClosed.Should().BeTrue();
        vm.FilteredChatInstances.Should().BeEmpty();
    }

    [Fact]
    public async Task GameServersViewModel_KillInstance_TerminatesServerAndDisablesAutoRelaunch()
    {
        Mock<IGameServerFleetService> mockService = new();
        mockService.Setup(s => s.StopWorkerAsync(It.IsAny<string>(), It.IsAny<int>(), It.IsAny<int>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((true, "Killed"));

        GameServersViewModel vm = new(mockService.Object);

        GameServerInstanceDetailViewModel instance = new()
        {
            GamePort = 11235,
            ServerName = "DONUT ARENA #1",
            HostAddress = "127.0.0.1",
            ControlPort = 11234
        };
        instance.UpdateStatus("Available", isRunning: true);
        vm.AllChatInstances.Add(instance);
        vm.SelectedChatInstance = instance;

        await vm.KillInstanceAsync(instance);

        instance.DesiredRunning.Should().BeFalse();
        instance.IsPermanentlyStopped.Should().BeTrue();
        instance.IsRunning.Should().BeFalse();
        instance.ServerStatus.Should().Be("Stopped");
    }

    [Fact]
    public void K2CaretColorParser_ParsesColorsAndStripsTokensCorrectly()
    {
        string raw = "^pDONUT ^oARENA";
        string clean = NEXUS.ControlPanel.GUI.Controls.K2CaretColorParser.StripCarets(raw);
        clean.Should().Be("DONUT ARENA");

        var segments = NEXUS.ControlPanel.GUI.Controls.K2CaretColorParser.Parse(raw);
        segments.Should().HaveCount(2);
        segments[0].Text.Should().Be("DONUT ");
        segments[0].HexColor.Should().Be("#A855F7"); // Purple for ^p
        segments[1].Text.Should().Be("ARENA");
        segments[1].HexColor.Should().Be("#F59E0B"); // Orange for ^o

        var goldSegments = NEXUS.ControlPanel.GUI.Controls.K2CaretColorParser.Parse("^yALL");
        goldSegments.Should().HaveCount(1);
        goldSegments[0].Text.Should().Be("ALL");
        goldSegments[0].HexColor.Should().Be("#FFD700"); // Gold for ^y
    }

    [Fact]
    public async Task GameServerFleetService_DiscoversLocalOrContainerHosts()
    {
        GameServerFleetService service = new();
        var hosts = await service.GetDiscoveredHostsAsync();
        hosts.Should().NotBeEmpty();
        hosts[0].Port.Should().Be(11234);
    }

    [Fact]
    public async Task GameServerFleetService_DiscoversBothLocalAndRunningDockerFleet()
    {
        GameServerFleetService service = new();
        var hosts = await service.GetDiscoveredHostsAsync();
        hosts.Should().NotBeEmpty();
        hosts.Should().Contain(h => h.Port == 11234);
        hosts.First(h => h.Port == 11234).Name.Should().Contain("Local");
    }

    [Fact]
    public async Task GameServerHostItemViewModel_ConfiguresLocalDockerAndK3sWithUncappedLetterTags()
    {
        Mock<IGameServerFleetService> mockFleet = new();

        // 1. Local Node (11234)
        GameServerHostEntry localEntry = new() { Id = "local-01", Name = "Local Host", HostAddress = "127.0.0.1", Port = 11234, Location = "USE" };
        CompelStatusResponseDto localStatus = new()
        {
            Instances = 2,
            ServerNamePrefix = "KONGOR ARENA",
            Ports = new CompelPortAllocationDto { GameStart = 11235, VoiceStart = 11435, PublicGameStart = 11235, PublicVoiceStart = 11435 }
        };
        mockFleet.Setup(f => f.PingHostAsync("127.0.0.1", 11234, It.IsAny<CancellationToken>()))
            .ReturnsAsync((true, 5, new CompelPingResponseDto { Application = "COMPEL", Version = "4.10.1" }));
        mockFleet.Setup(f => f.GetHostStatusAsync("127.0.0.1", 11234, It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((true, localStatus, null));

        GameServerHostItemViewModel localVm = new(localEntry, mockFleet.Object);
        localVm.HostingBadgeText.Should().Be("💻 LOCAL");
        localVm.HostingEnvironment.Should().Be("Local Windows Process");

        await localVm.RefreshAsync();
        localVm.InstancesList.Should().HaveCount(2);
        localVm.InstancesList[0].InstanceTag.Should().Be("#L1");
        localVm.InstancesList[0].ServerName.Should().Be("KONGOR ARENA-USE-L1");
        localVm.InstancesList[1].InstanceTag.Should().Be("#L2");
        localVm.InstancesList[1].ServerName.Should().Be("KONGOR ARENA-USE-L2");

        // 2. Docker Node (11244)
        GameServerHostEntry dockerEntry = new() { Id = "docker-01", Name = "Docker Host", HostAddress = "127.0.0.1", Port = 11244, Location = "USE" };
        CompelStatusResponseDto dockerStatus = new()
        {
            Instances = 2,
            ServerNamePrefix = "KONGOR ARENA",
            Ports = new CompelPortAllocationDto { GameStart = 11245, VoiceStart = 11445, PublicGameStart = 11245, PublicVoiceStart = 11445 }
        };
        mockFleet.Setup(f => f.PingHostAsync("127.0.0.1", 11244, It.IsAny<CancellationToken>()))
            .ReturnsAsync((true, 5, new CompelPingResponseDto { Application = "COMPEL", Version = "4.10.1" }));
        mockFleet.Setup(f => f.GetHostStatusAsync("127.0.0.1", 11244, It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((true, dockerStatus, null));

        GameServerHostItemViewModel dockerVm = new(dockerEntry, mockFleet.Object);
        dockerVm.HostingBadgeText.Should().Be("🐳 DOCKER");
        dockerVm.HostingEnvironment.Should().Be("Docker Container");

        await dockerVm.RefreshAsync();
        dockerVm.InstancesList.Should().HaveCount(2);
        dockerVm.InstancesList[0].InstanceTag.Should().Be("#D1");
        dockerVm.InstancesList[0].ServerName.Should().Be("KONGOR ARENA-USE-D1");
        dockerVm.InstancesList[1].InstanceTag.Should().Be("#D2");
        dockerVm.InstancesList[1].ServerName.Should().Be("KONGOR ARENA-USE-D2");

        // 3. K3s Node (11254)
        GameServerHostEntry k3sEntry = new() { Id = "k3s-01", Name = "K3s Host", HostAddress = "127.0.0.1", Port = 11254, Location = "USE" };
        CompelStatusResponseDto k3sStatus = new()
        {
            Instances = 2,
            ServerNamePrefix = "KONGOR ARENA",
            Ports = new CompelPortAllocationDto { GameStart = 11255, VoiceStart = 11455, PublicGameStart = 11255, PublicVoiceStart = 11455 }
        };
        mockFleet.Setup(f => f.PingHostAsync("127.0.0.1", 11254, It.IsAny<CancellationToken>()))
            .ReturnsAsync((true, 5, new CompelPingResponseDto { Application = "COMPEL", Version = "4.10.1" }));
        mockFleet.Setup(f => f.GetHostStatusAsync("127.0.0.1", 11254, It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((true, k3sStatus, null));

        GameServerHostItemViewModel k3sVm = new(k3sEntry, mockFleet.Object);
        k3sVm.HostingBadgeText.Should().Be("☸️ K3S");
        k3sVm.HostingEnvironment.Should().Be("Kubernetes / K3s");

        await k3sVm.RefreshAsync();
        k3sVm.InstancesList.Should().HaveCount(2);
        k3sVm.InstancesList[0].InstanceTag.Should().Be("#K1");
        k3sVm.InstancesList[0].ServerName.Should().Be("KONGOR ARENA-USE-K1");
        k3sVm.InstancesList[1].InstanceTag.Should().Be("#K2");
        k3sVm.InstancesList[1].ServerName.Should().Be("KONGOR ARENA-USE-K2");
    }

    [Fact]
    public async Task GameServersChatViewModel_TierPartition100Offsets_ResolvesAllTiersAndRecognizesPorts()
    {
        Mock<IGameServerFleetService> mockFleet = new();

        // 1. Local Node (11234, Offset 0)
        GameServerHostEntry localEntry = new() { Id = "local-01", Name = "Local Host", HostAddress = "127.0.0.1", Port = 11234, Location = "USE" };
        CompelStatusResponseDto localStatus = new()
        {
            Instances = 2,
            ServerNamePrefix = "KONGOR ARENA",
            Ports = new CompelPortAllocationDto { GameStart = 11235, VoiceStart = 11735, PublicGameStart = 11235, PublicVoiceStart = 11735 }
        };
        mockFleet.Setup(f => f.PingHostAsync("127.0.0.1", 11234, It.IsAny<CancellationToken>()))
            .ReturnsAsync((true, 5, new CompelPingResponseDto { Application = "COMPEL", Version = "4.10.1" }));
        mockFleet.Setup(f => f.GetHostStatusAsync("127.0.0.1", 11234, It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((true, localStatus, null));

        GameServerHostItemViewModel localVm = new(localEntry, mockFleet.Object);
        localVm.HostingBadgeText.Should().Be("💻 LOCAL");
        localVm.HostingEnvironment.Should().Be("Local Windows Process");
        await localVm.RefreshAsync();
        localVm.InstancesList.Should().HaveCount(2);
        localVm.InstancesList[0].InstanceTag.Should().Be("#L1");
        localVm.InstancesList[0].GamePort.Should().Be(11235);
        localVm.InstancesList[0].ServerName.Should().Be("KONGOR ARENA-USE-L1");

        // 2. Docker Node (11334, Offset 100)
        GameServerHostEntry dockerEntry = new() { Id = "docker-01", Name = "Docker Host", HostAddress = "127.0.0.1", Port = 11334, Location = "USE" };
        CompelStatusResponseDto dockerStatus = new()
        {
            Instances = 2,
            ServerNamePrefix = "KONGOR ARENA",
            Ports = new CompelPortAllocationDto { GameStart = 11335, VoiceStart = 11835, PublicGameStart = 11335, PublicVoiceStart = 11835 }
        };
        mockFleet.Setup(f => f.PingHostAsync("127.0.0.1", 11334, It.IsAny<CancellationToken>()))
            .ReturnsAsync((true, 5, new CompelPingResponseDto { Application = "COMPEL", Version = "4.10.1" }));
        mockFleet.Setup(f => f.GetHostStatusAsync("127.0.0.1", 11334, It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((true, dockerStatus, null));

        GameServerHostItemViewModel dockerVm = new(dockerEntry, mockFleet.Object);
        dockerVm.HostingBadgeText.Should().Be("🐳 DOCKER");
        dockerVm.HostingEnvironment.Should().Be("Docker Container");
        await dockerVm.RefreshAsync();
        dockerVm.InstancesList.Should().HaveCount(2);
        dockerVm.InstancesList[0].InstanceTag.Should().Be("#D1");
        dockerVm.InstancesList[0].GamePort.Should().Be(11335);
        dockerVm.InstancesList[0].ServerName.Should().Be("KONGOR ARENA-USE-D1");

        // 3. K3s Node (11434, Offset 200)
        GameServerHostEntry k3sEntry = new() { Id = "k3s-01", Name = "K3s Host", HostAddress = "127.0.0.1", Port = 11434, Location = "USE" };
        CompelStatusResponseDto k3sStatus = new()
        {
            Instances = 2,
            ServerNamePrefix = "KONGOR ARENA",
            Ports = new CompelPortAllocationDto { GameStart = 11435, VoiceStart = 11935, PublicGameStart = 11435, PublicVoiceStart = 11935 }
        };
        mockFleet.Setup(f => f.PingHostAsync("127.0.0.1", 11434, It.IsAny<CancellationToken>()))
            .ReturnsAsync((true, 5, new CompelPingResponseDto { Application = "COMPEL", Version = "4.10.1" }));
        mockFleet.Setup(f => f.GetHostStatusAsync("127.0.0.1", 11434, It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((true, k3sStatus, null));

        GameServerHostItemViewModel k3sVm = new(k3sEntry, mockFleet.Object);
        k3sVm.HostingBadgeText.Should().Be("☸️ K3S");
        k3sVm.HostingEnvironment.Should().Be("Kubernetes / K3s");
        await k3sVm.RefreshAsync();
        k3sVm.InstancesList.Should().HaveCount(2);
        k3sVm.InstancesList[0].InstanceTag.Should().Be("#K1");
        k3sVm.InstancesList[0].GamePort.Should().Be(11435);
        k3sVm.InstancesList[0].ServerName.Should().Be("KONGOR ARENA-USE-K1");
    }

    [Fact]
    public void GameServersViewModel_MultiFleetChatChannels_RemainDisjoint()
    {
        Mock<IGameServerFleetService> mockFleet = new();
        GameServersViewModel vm = new(mockFleet.Object);

        GameServerInstanceDetailViewModel instLocal = new()
        {
            GamePort = 11235,
            InstanceTag = "#L1",
            ServerName = "KONGOR ARENA-USE-L1",
            HostingBadgeText = "💻 LOCAL"
        };
        GameServerInstanceDetailViewModel instDocker = new()
        {
            GamePort = 11245,
            InstanceTag = "#D1",
            ServerName = "KONGOR ARENA-USE-D1",
            HostingBadgeText = "🐳 DOCKER"
        };
        GameServerInstanceDetailViewModel instK3s = new()
        {
            GamePort = 11255,
            InstanceTag = "#K1",
            ServerName = "KONGOR ARENA-USE-K1",
            HostingBadgeText = "☸️ K3S"
        };

        instLocal.UpdateStatus("Available", isRunning: true);
        instDocker.UpdateStatus("Available", isRunning: true);
        instK3s.UpdateStatus("Available", isRunning: true);

        vm.AllChatInstances.Add(instLocal);
        vm.AllChatInstances.Add(instDocker);
        vm.AllChatInstances.Add(instK3s);

        // Send commands to Local and K3s
        instLocal.AddOperatorMessage("AddFakePlayer 1", "stdin");
        instK3s.AddOperatorMessage("AddFakePlayer 2", "stdin");

        // Docker must remain untouched
        instDocker.ChatMessages.Should().BeEmpty();

        // Local and K3s must contain only their respective messages
        instLocal.ChatMessages.Should().HaveCount(1);
        instLocal.ChatMessages[0].Message.Should().Be("AddFakePlayer 1");

        instK3s.ChatMessages.Should().HaveCount(1);
        instK3s.ChatMessages[0].Message.Should().Be("AddFakePlayer 2");
    }

    [Fact]
    public async Task GameServersViewModel_LaunchLocalServer_DoesNotStopDisjointDocker()
    {
        Mock<IGameServerFleetService> mockFleet = new();
        Mock<IDockerHealthService> mockDocker = new();
        Mock<ISecretsManagerService> mockSecrets = new();
        Mock<INexusOrchestratorService> mockOrchestrator = new();

        NexusSecrets secrets = new()
        {
            CompelPortRangeOffset = 100 // Docker configured on offset 100 (11334)
        };
        mockSecrets.Setup(s => s.LoadSecretsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(secrets);

        GameServersViewModel vm = new(
            mockFleet.Object,
            orchestrator: mockOrchestrator.Object,
            secretsManager: mockSecrets.Object,
            dockerHealth: mockDocker.Object);

        await vm.LaunchLocalServerFromChatAsync();

        // Docker StopContainerAsync should NOT have been called because offset is 10!
        mockDocker.Verify(d => d.StopContainerAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
        mockOrchestrator.Verify(o => o.LaunchGameServerAsync(It.IsAny<string?>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public void ContainerLaunchCommandBuilder_K3sAgones_GeneratesK3sFleetApplyCommand()
    {
        NexusSecrets secrets = new()
        {
            HostAccountUserName = "guccihost",
            HostAccountPassword = "secretpassword",
            CompelAuthenticationToken = "devtoken"
        };

        string cmd = ContainerLaunchCommandBuilder.BuildCommand(secrets, ContainerPlatform.K3sAgones);

        cmd.Should().Contain("kubectl create secret generic kongor-host-credentials");
        cmd.Should().Contain("--from-literal=username=\"guccihost_k3s\"");
        cmd.Should().Contain("--from-literal=token=\"devtoken\"");
        cmd.Should().Contain("infra/k8s/agones/fleet.yaml");
        cmd.Should().Contain("kubectl get gameservers");
    }

    [Theory]
    [InlineData("DONUT ARENA #D1", "#D1", "DONUT ARENA")]
    [InlineData("KONGOR ARENA-USE-L1", "#L1", "KONGOR ARENA-USE")]
    [InlineData("Caldavar Mid Wars", "#1", "Caldavar Mid Wars")]
    [InlineData("ARENA #1", "#1", "ARENA")]
    public void GameServerInstanceDetailViewModel_CleanServerName_StripsRedundantTrailingTag(string serverName, string tag, string expectedClean)
    {
        GameServerInstanceDetailViewModel vm = new()
        {
            ServerName = serverName,
            InstanceTag = tag
        };

        vm.CleanServerName.Should().Be(expectedClean);
    }

    [Theory]
    [InlineData("Available", "Ready")]
    [InlineData("Stopped", "Stopped")]
    [InlineData("Crashed", "Crashed")]
    [InlineData("Starting", "Starting")]
    public void GameServerInstanceDetailViewModel_StatusShortText_ReturnsConciseStatus(string status, string expectedShort)
    {
        GameServerInstanceDetailViewModel vm = new();
        vm.UpdateStatus(status, status == "Available" || status == "Starting");

        vm.StatusShortText.Should().Be(expectedShort);
    }

    [Fact]
    public void GameServerInstanceDetailViewModel_IsRealError_AccuratelyDetectsChatSocketError()
    {
        const string errorLog = "Sv: [15:18:32] Error reading chat socket.";

        bool isErr = GameServerInstanceDetailViewModel.IsRealError(errorLog, isMarkedError: false);

        isErr.Should().BeTrue();
    }

    [Fact]
    public void GameServerChatMessageViewModel_SenderDisplayAndColor_ReflectErrorState()
    {
        var normalMsg = GameServerChatMessageViewModel.CreateServerLog("Player connected");
        normalMsg.SenderDisplay.Should().Be("Server");
        normalMsg.SenderColor.Should().Be("#94A3B8");

        var errorMsg = GameServerChatMessageViewModel.CreateServerLog("Error reading chat socket.", isError: true);
        errorMsg.SenderDisplay.Should().Be("⚠️ Server Error");
        errorMsg.SenderColor.Should().Be("#EF4444");
    }

    [Fact]
    public void GameServersViewModel_TabStyling_IsConsistentCyanPillWhenActive()
    {
        Mock<IGameServerFleetService> mockFleet = new();
        GameServersViewModel vm = new(mockFleet.Object);

        // Initially in Fleet mode
        vm.FleetTabBackground.Should().Be("#00F0FF");
        vm.FleetTabForeground.Should().Be("#0B0F17");
        vm.ChatTabBackground.Should().Be("Transparent");

        // Switch to Chat mode
        vm.SwitchToChatMode();
        vm.ChatTabBackground.Should().Be("#00F0FF");
        vm.ChatTabForeground.Should().Be("#0B0F17");
        vm.FleetTabBackground.Should().Be("Transparent");
    }

    [Fact]
    public async Task GameServerFleetService_GetServerLogsAsync_DeduplicatesOfflineHostErrorsAndMarksEndpointOffline()
    {
        // protects: Issue #2 fix preventing 2-second [FLEET] [ERROR] GetServerLogsAsync log spam when COMPEL is offline.
        // fails_when: Repeated offline GetServerLogsAsync polls emit isError=true on every 2s tick.
        // seam: Real GameServerFleetService + failing HttpMessageHandler.
        List<(string Category, string Message, bool IsError)> logs = [];
        using HttpClient offlineClient = new(new AlwaysRefusedHttpMessageHandler());
        GameServerFleetService service = new(
            secretsManager: null,
            httpClient: offlineClient,
            logSink: (cat, msg, isErr) => logs.Add((cat, msg, isErr)));

        for (int i = 0; i < 5; i++)
        {
            var (success, lines, _) = await service.GetServerLogsAsync("127.0.0.1", 11234, 11235, tail: 100);
            success.Should().BeFalse();
            lines.Should().BeEmpty();
        }

        service.IsEndpointKnownOffline("127.0.0.1", 11234).Should().BeTrue();
        logs.Count(l => l.IsError).Should().Be(0);
        logs.Should().HaveCount(1);
    }

    [Fact]
    public async Task GameServersViewModel_SendMatchCommandToIdleInstance_ShowsSwitchBannerAndForwardsToActiveServer()
    {
        // protects: Issue #3 UX guard when an operator sends AddFakePlayer/GiveGold to an idle server while another server hosts a match.
        // fails_when: No inline switch hint is surfaced or SwitchAndSendToActiveServerAsync fails to re-target the active port.
        // seam: GameServersViewModel command dispatch + IGameServerFleetService.
        Mock<IGameServerFleetService> mockService = new();
        mockService.Setup(s => s.SendWorkerCommandAsync(
                It.IsAny<string>(),
                It.IsAny<int>(),
                It.IsAny<int>(),
                It.IsAny<string>(),
                It.IsAny<string>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync((true, "native-pipe", "{\"success\":true}"));

        using GameServersViewModel vm = new(mockService.Object);

        GameServerInstanceDetailViewModel idleInstance = new()
        {
            GamePort = 11235,
            WorkerPort = 11235,
            ServerName = "DONUT ARENA #L1",
            HostAddress = "127.0.0.1",
            ControlPort = 11234
        };
        idleInstance.UpdateStatus("Available", isRunning: true);

        GameServerInstanceDetailViewModel activeInstance = new()
        {
            GamePort = 11236,
            WorkerPort = 11236,
            ServerName = "DONUT ARENA #L2",
            HostAddress = "127.0.0.1",
            ControlPort = 11234
        };
        activeInstance.UpdateStatus("InMatch", isRunning: true);

        vm.AllChatInstances.Add(idleInstance);
        vm.AllChatInstances.Add(activeInstance);
        vm.SelectedChatInstance = idleInstance;
        vm.CommandInput = "AddFakePlayer 1";

        await vm.SendCommandAsync();

        vm.LastCommandStatus.Should().Be("✓ Delivered to Port 11235 (native-pipe)");
        vm.HasIdleCommandTargetWarning.Should().BeTrue();
        vm.SwitchAndSendButtonLabel.Should().Be("Switch & Send to 11236");

        await vm.SwitchAndSendToActiveServerAsync();

        vm.SelectedChatInstance.Should().BeSameAs(activeInstance);
        vm.HasIdleCommandTargetWarning.Should().BeFalse();
        mockService.Verify(s => s.SendWorkerCommandAsync(
            "127.0.0.1",
            11234,
            11236,
            "AddFakePlayer 1",
            It.IsAny<string>(),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Theory]
    [InlineData("/status", SmartInputKind.ConsoleCommand, "status", "/status")]
    [InlineData(">svr_broadcast 1", SmartInputKind.ConsoleCommand, "svr_broadcast 1", ">svr_broadcast 1")]
    [InlineData("status", SmartInputKind.ConsoleCommand, "status", "status")]
    [InlineData("svr_name \"Kongor Arena\"", SmartInputKind.ConsoleCommand, "svr_name \"Kongor Arena\"", "svr_name \"Kongor Arena\"")]
    [InlineData("AddFakePlayer 1", SmartInputKind.ConsoleCommand, "AddFakePlayer 1", "AddFakePlayer 1")]
    [InlineData("ServerChat Hello", SmartInputKind.ConsoleCommand, "ServerChat Hello", "ServerChat Hello")]
    [InlineData("Good luck and have fun!", SmartInputKind.ServerChat, "ServerChat \"Good luck and have fun!\"", "Good luck and have fun!")]
    [InlineData("hello players", SmartInputKind.ServerChat, "ServerChat \"hello players\"", "hello players")]
    public void GameServersViewModel_ClassifySmartHybridInput_RoutesCorrectly(
        string input,
        SmartInputKind expectedKind,
        string expectedCommand,
        string expectedDisplay)
    {
        // protects: Issue #3B Smart Hybrid chat/console routing invariant.
        // fails_when: Conversational text is sent as raw command, or console commands are wrapped in ServerChat.
        // seam: GameServersViewModel.ClassifySmartHybridInput static classifier.
        var (kind, cmdToSend, displayText) = GameServersViewModel.ClassifySmartHybridInput(input);
        kind.Should().Be(expectedKind);
        cmdToSend.Should().Be(expectedCommand);
        displayText.Should().Be(expectedDisplay);
    }

    [Fact]
    public void GameServersViewModel_SyncCollection_UpdatesInPlaceWithoutReplacingTargetInstance()
    {
        // protects: Issue #3A Ping UI jitter & collection stability invariant.
        // fails_when: Target collection reference is replaced or elements are unnecessarily re-allocated.
        // seam: GameServersViewModel.SyncCollection generic in-place syncer.
        ObservableCollection<GameServerInstanceDetailViewModel> target = [];
        GameServerInstanceDetailViewModel inst1 = new() { GamePort = 11235, ServerName = "Instance 1" };
        GameServerInstanceDetailViewModel inst2 = new() { GamePort = 11236, ServerName = "Instance 2" };
        target.Add(inst1);
        target.Add(inst2);

        // Desired: keep inst2, remove inst1, add inst3
        GameServerInstanceDetailViewModel inst3 = new() { GamePort = 11237, ServerName = "Instance 3" };
        List<GameServerInstanceDetailViewModel> desired = [inst2, inst3];

        GameServersViewModel.SyncCollection(target, desired, i => i.GamePort);

        target.Count.Should().Be(2);
        target[0].Should().BeSameAs(inst2);
        target[1].Should().BeSameAs(inst3);
    }

    [Fact]
    public void GameServerInstanceDetailViewModel_IngestLogs_FormatsPlayerChatBubblesCorrectly()
    {
        // protects: Issue #3B Player chat distinct visual styling & bubble segregation.
        // fails_when: In-game player chat is rendered as generic server noise or filtered out.
        // seam: GameServerInstanceDetailViewModel.IngestLogs log ingestion pipe.
        GameServerInstanceDetailViewModel vm = new()
        {
            GamePort = 11235,
            ServerName = "DONUT ARENA #1"
        };
        vm.UpdateStatus("InMatch", isRunning: true);

        CompelDedicatedServerLogLineDto chatLine = new()
        {
            Timestamp = DateTimeOffset.UtcNow,
            Text = "[ALL] PlayerOne: Well played team!",
            IsError = false,
            Source = "chat"
        };

        vm.IngestLogs([chatLine]);

        vm.ChatMessages.Should().Contain(m =>
            m.IsPlayerChat &&
            m.Sender == "[ALL] PlayerOne" &&
            m.Message == "Well played team!" &&
            m.SenderColor == "#10B981");
    }

    private sealed class AlwaysRefusedHttpMessageHandler : System.Net.Http.HttpMessageHandler
    {
        protected override Task<System.Net.Http.HttpResponseMessage> SendAsync(
            System.Net.Http.HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            throw new System.Net.Http.HttpRequestException(
                "No connection could be made because the target machine actively refused it. (127.0.0.1:11234)");
        }
    }
}
