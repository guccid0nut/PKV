// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

using Moq;
using NEXUS.ControlPanel.GUI.Services;
using NEXUS.ControlPanel.GUI.Services.Models;
using NEXUS.ControlPanel.GUI.ViewModels;
using NEXUS.Contracts.ControlPlane;

namespace ASPIRE.Tests.NEXUS.ControlPanel.GUI;

public sealed class GameServersViewModelTests
{
    [Fact]
    public async Task GameServersViewModel_WhenExecuted_LoadsAndRefreshesHosts()
    {
        // Arrange
        Mock<IGameServerFleetService> mockFleet = new();
        List<GameServerHostEntry> entries =
        [
            new()
            {
                Id = "host-1",
                Name = "Alpha-USE",
                HostAddress = "127.0.0.1",
                Port = 8080,
                Location = "USE"
            },
            new()
            {
                Id = "host-2",
                Name = "Beta-EU",
                HostAddress = "192.168.1.50",
                Port = 8080,
                Location = "EU"
            }
        ];

        mockFleet.Setup(f => f.GetDiscoveredHostsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(entries);

        mockFleet.Setup(f => f.PingHostAsync(It.IsAny<string>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((true, 15, new CompelPingResponseDto { Application = "COMPEL", Version = "2.0.0" }));

        mockFleet.Setup(f => f.GetHostStatusAsync(It.IsAny<string>(), It.IsAny<int>(), It.IsAny<string?>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync((true, new CompelStatusResponseDto
            {
                Application = "COMPEL",
                Instances = 2,
                ManagerRunning = true,
                ProxyRunning = true,
                UptimeSeconds = 60
            }, null));

        // Act
        GameServersViewModel vm = new(mockFleet.Object);
        await vm.LoadAndRefreshFleetAsync();

        // Assert
        Assert.Equal(2, vm.TotalHosts);
        Assert.Equal(2, vm.OnlineHosts);
        Assert.Equal(4, vm.TotalActiveInstances);
        Assert.Equal(15, vm.AveragePingMs);
        Assert.Equal(2, vm.Hosts.Count);
    }

    [Fact]
    public async Task GameServersViewModel_StartAllAsync_BroadcastsStartToAllOnlineHosts()
    {
        // Arrange
        Mock<IGameServerFleetService> mockFleet = new();
        List<GameServerHostEntry> entries =
        [
            new() { Id = "host-1", Name = "Host-1", HostAddress = "127.0.0.1", Port = 8080 }
        ];

        mockFleet.Setup(f => f.GetDiscoveredHostsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(entries);

        mockFleet.Setup(f => f.PingHostAsync(It.IsAny<string>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((true, 10, new CompelPingResponseDto()));

        mockFleet.Setup(f => f.StartInstancesAsync(It.IsAny<string>(), It.IsAny<int>(), It.IsAny<string?>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync((true, "Start Requested"));

        GameServersViewModel vm = new(mockFleet.Object);
        await vm.LoadAndRefreshFleetAsync();

        // Act
        await vm.StartAllAsync();

        // Assert
        mockFleet.Verify(
            f => f.StartInstancesAsync("127.0.0.1", 8080, It.IsAny<string?>(), It.IsAny<CancellationToken>()),
            Times.AtLeastOnce);
    }

    [Fact]
    public async Task GameServerHostItemViewModel_StopAndStart_TransitionsStateCorrectly()
    {
        // Arrange
        Mock<IGameServerFleetService> mockFleet = new();
        GameServerHostEntry entry = new()
        {
            Id = "host-test",
            Name = "guccihost",
            HostAddress = "127.0.0.1",
            Port = 8080,
            Location = "USE"
        };

        mockFleet.Setup(f => f.PingHostAsync("127.0.0.1", 8080, It.IsAny<CancellationToken>()))
            .ReturnsAsync((true, 5, new CompelPingResponseDto { Application = "COMPEL", Version = "2.0.0" }));

        // Initially Running
        mockFleet
            .Setup(f => f.GetHostStatusAsync("127.0.0.1", 8080, It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((true, new CompelStatusResponseDto
            {
                Application = "COMPEL",
                Instances = 2,
                ManagerRunning = true,
                UptimeSeconds = 120
            }, null));

        mockFleet
            .Setup(f => f.StopInstancesAsync("127.0.0.1", 8080, It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((true, "Stop Requested"));

        mockFleet.Setup(f =>
                f.StartInstancesAsync("127.0.0.1", 8080, It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((true, "Start Requested"));

        GameServerHostItemViewModel itemVm = new(entry, mockFleet.Object);
        await itemVm.RefreshAsync();

        Assert.Equal("Running", itemVm.StatusText);
        Assert.Equal("#10B981", itemVm.StatusColor);
        Assert.Equal("Running (2 Slaves Active)", itemVm.ServerStateText);

        // Act: Stop instances
        mockFleet
            .Setup(f => f.GetHostStatusAsync("127.0.0.1", 8080, It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((true, new CompelStatusResponseDto
            {
                Application = "COMPEL",
                Instances = 2,
                ManagerRunning = false,
                UptimeSeconds = 130
            }, null));

        await itemVm.StopAsync();

        // Assert: Stopped by Operator
        Assert.Equal("Stopped by Operator", itemVm.StatusText);
        Assert.Equal("#F43F5E", itemVm.StatusColor);
        Assert.Equal("Stopped by Operator", itemVm.ServerStateText);
        Assert.Equal("#F43F5E", itemVm.ServerStateColor);

        // Act: Start instances again
        mockFleet
            .Setup(f => f.GetHostStatusAsync("127.0.0.1", 8080, It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((true, new CompelStatusResponseDto
            {
                Application = "COMPEL",
                Instances = 2,
                ManagerRunning = true,
                UptimeSeconds = 140
            }, null));

        await itemVm.StartAsync();

        // Assert: Running again
        Assert.Equal("Running", itemVm.StatusText);
        Assert.Equal("#10B981", itemVm.StatusColor);
        Assert.Equal("Running (2 Slaves Active)", itemVm.ServerStateText);
    }

    [Fact]
    public async Task GameServersViewModel_GameServerHostItemViewModel_PopulatesInstanceDetailsAndTogglesExpand()
    {
        // Arrange
        Mock<IGameServerFleetService> mockFleet = new();
        GameServerHostEntry entry = new()
        {
            Id = "host-instances",
            Name = "guccihost",
            HostAddress = "127.0.0.1",
            Port = 8080,
            Location = "USE"
        };

        mockFleet.Setup(f => f.PingHostAsync("127.0.0.1", 8080, It.IsAny<CancellationToken>()))
            .ReturnsAsync((true, 5, new CompelPingResponseDto { Application = "COMPEL", Version = "2.0.0" }));

        mockFleet
            .Setup(f => f.GetHostStatusAsync("127.0.0.1", 8080, It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((true, new CompelStatusResponseDto
            {
                Application = "COMPEL",
                ServerNamePrefix = "NEXUS-ARENA",
                Location = "USE",
                Instances = 2,
                UseProxy = false,
                Ports = new CompelPortAllocationDto
                {
                    GameStart = 11255,
                    GameEnd = 11256,
                    VoiceStart = 11257,
                    VoiceEnd = 11258,
                    PingPort = 11259
                },
                ManagerRunning = true,
                PingResponderBound = true,
                UptimeSeconds = 240
            }, null));

        GameServerHostItemViewModel itemVm = new(entry, mockFleet.Object);
        await itemVm.RefreshAsync();

        // Assert instance details
        Assert.Equal(2, itemVm.InstancesList.Count);
        Assert.Equal(1, itemVm.InstancesList[0].InstanceNumber);
        Assert.Equal("NEXUS-ARENA-USE-L1", itemVm.InstancesList[0].ServerName);
        Assert.Equal(11255, itemVm.InstancesList[0].GamePort);
        Assert.Equal(11257, itemVm.InstancesList[0].VoicePort);
        Assert.Equal("Available (Ready)", itemVm.InstancesList[0].StatusText);
        Assert.Equal("🟢", itemVm.InstancesList[0].StatusIcon);

        Assert.Equal(2, itemVm.InstancesList[1].InstanceNumber);
        Assert.Equal("NEXUS-ARENA-USE-L2", itemVm.InstancesList[1].ServerName);
        Assert.Equal(11256, itemVm.InstancesList[1].GamePort);
        Assert.Equal(11258, itemVm.InstancesList[1].VoicePort);
        Assert.Equal("127.0.0.1:11256", itemVm.InstancesList[1].Endpoint);

        Assert.Equal("Bound (11259)", itemVm.PingResponderStatus);
        Assert.Equal("Direct (Disabled)", itemVm.ProxyStatus);
        Assert.Equal("http://127.0.0.1:8080/status", itemVm.ControlPlaneUrl);

        // Test ToggleExpand
        Assert.False(itemVm.IsExpanded);
        Assert.Equal("▼ View Instances (2)", itemVm.ExpandButtonText);

        itemVm.ToggleExpand();
        Assert.True(itemVm.IsExpanded);
        Assert.Equal("▲ Hide Instances (2)", itemVm.ExpandButtonText);

        itemVm.ToggleExpand();
        Assert.False(itemVm.IsExpanded);
        Assert.Equal("▼ View Instances (2)", itemVm.ExpandButtonText);
    }

    [Fact]
    public void GameServerInstanceDetailViewModel_IngestLogs_ReplacesBoilerplateWithMilestones()
    {
        // Arrange
        GameServerInstanceDetailViewModel detailVm = new()
        {
            ServerName = "TEST-SERVER",
            GamePort = 11235
        };

        // Act: Ingest startup noise
        List<string> startupNoise =
        [
            "[CFileManager] Mounting resource archive resources999.s2z...",
            "Loading interface XML: ui/main.interface...",
            "libxml2 parser initialized successfully.",
            "SoundManager::Initialize directsound device ready",
            "CWorld::Init terrain quadtree initialized."
        ];
        detailVm.IngestLogs(startupNoise);

        // Assert: Milestones replaced the noise, no raw noise appears
        Assert.NotEmpty(detailVm.ChatMessages);
        Assert.DoesNotContain(detailVm.ChatMessages, m => m.Message.Contains("CFileManager"));
        Assert.DoesNotContain(detailVm.ChatMessages, m => m.Message.Contains("libxml2"));
        Assert.Contains(detailVm.ChatMessages, m => m.Message.Contains("Combobulating Legion & Hellbourne Leylines"));

        // Act: Ingest boot completion signal
        detailVm.IngestLogs(["Dedicated server initialized."]);
        Assert.Contains(detailVm.ChatMessages, m => m.Message.Contains("Dedicated Game Server initialized and ready for battle!"));

        // Act: Ingest post-boot logs (should pass through without suppression)
        detailVm.IngestLogs(["[Server] Player [GM]Gucci connected.", "[Chat] [GM]Gucci: mid or feed"]);
        Assert.Contains(detailVm.ChatMessages, m => m.Message.Contains("Player [GM]Gucci connected."));
        Assert.Contains(detailVm.ChatMessages, m => m.Message.Contains("[GM]Gucci: mid or feed"));
    }

    [Fact]
    public async Task GameServersViewModel_ChatHistoryPreservedAcrossFleetRefreshes()
    {
        // Arrange
        Mock<IGameServerFleetService> mockFleet = new();
        GameServerHostEntry entry = new()
        {
            Id = "host-chat-test",
            Name = "chat-host",
            HostAddress = "127.0.0.1",
            Port = 8080,
            Location = "USE"
        };

        mockFleet.Setup(f => f.GetDiscoveredHostsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync([entry]);

        mockFleet.Setup(f => f.PingHostAsync(It.IsAny<string>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((true, 10, new CompelPingResponseDto()));

        mockFleet.Setup(f => f.GetHostStatusAsync(It.IsAny<string>(), It.IsAny<int>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((true, new CompelStatusResponseDto
            {
                Application = "COMPEL",
                Instances = 1,
                ManagerRunning = true,
                Ports = new CompelPortAllocationDto { GameStart = 11235, GameEnd = 11235 }
            }, null));

        GameServersViewModel vm = new(mockFleet.Object);
        await vm.LoadAndRefreshFleetAsync();

        Assert.NotNull(vm.SelectedChatInstance);
        Assert.Equal(11235, vm.SelectedChatInstance.GamePort);

        // Add user chat messages to the selected instance
        vm.SelectedChatInstance.AddOperatorMessage("status", "sent");
        vm.SelectedChatInstance.IngestLogs(["Match #101 Caldavar running: 10 players."]);

        int messageCountBefore = vm.SelectedChatInstance.ChatMessages.Count;
        Assert.True(messageCountBefore >= 2);

        // Act: Refresh fleet quietly (as the background timer or orchestrator hook does)
        await vm.RefreshFleetQuietlyAsync();

        // Assert: SelectedChatInstance remains selected and messages are fully preserved
        Assert.NotNull(vm.SelectedChatInstance);
        Assert.Equal(11235, vm.SelectedChatInstance.GamePort);
        Assert.Equal(messageCountBefore, vm.SelectedChatInstance.ChatMessages.Count);
        Assert.Contains(vm.SelectedChatInstance.ChatMessages, m => m.Message == "status");
        Assert.Contains(vm.SelectedChatInstance.ChatMessages, m => m.Message.Contains("Match #101 Caldavar running"));
    }

    [Fact]
    public void GameServerInstanceDetailViewModel_IngestLogs_ExactScreenshotLogs_FilteredAndMilestonesDisplayed()
    {
        // Arrange
        GameServerInstanceDetailViewModel detailVm = new()
        {
            ServerName = "DONUT ARENA-USE-1",
            GamePort = 11235
        };

        // Act: Ingest exact logs seen in user screenshot
        List<string> screenshotLogs =
        [
            "[18:59:57] Executing \"/autoexec.cfg\"",
            "Error: [18:59:57] Failed to remove application binding with cpu core",
            "Sv: [19:00:08] Sending handshake to chat server.",
            "Sv: [19:00:08] Sent heartbeat",
            "Sv: [19:00:08] Connected to chat server.",
            "Sv: [19:00:08] Sending game server status update.",
            "Sv: [19:00:08] Project KONGOR Remote Configuration Was Injected Successfully",
            "Sv: [19:00:08] Sending game server status update.",
            "Sv: [19:00:08] CGameServer::StateStringChanged 1"
        ];
        detailVm.IngestLogs(screenshotLogs);

        // Assert: Raw boilerplate is filtered out
        Assert.DoesNotContain(detailVm.ChatMessages, m => m.Message.Contains("autoexec.cfg"));
        Assert.DoesNotContain(detailVm.ChatMessages, m => m.Message.Contains("Failed to remove application binding"));
        Assert.DoesNotContain(detailVm.ChatMessages, m => m.Message.Contains("Sending handshake"));
        Assert.DoesNotContain(detailVm.ChatMessages, m => m.Message.Contains("Sent heartbeat"));
        Assert.DoesNotContain(detailVm.ChatMessages, m => m.Message.Contains("Connected to chat server"));
        Assert.DoesNotContain(detailVm.ChatMessages, m => m.Message.Contains("Remote Configuration Was Injected"));
        Assert.DoesNotContain(detailVm.ChatMessages, m => m.Message.Contains("CGameServer::StateStringChanged"));

        // Assert: Humorous milestones are displayed
        Assert.Contains(detailVm.ChatMessages, m => m.Message.Contains("Combobulating Legion & Hellbourne Leylines"));
        Assert.Contains(detailVm.ChatMessages, m => m.Message.Contains("Sharpening Kongor's cleaver"));
        Assert.Contains(detailVm.ChatMessages, m => m.Message.Contains("Calibrating Caldavar mana streams"));
        Assert.Contains(detailVm.ChatMessages, m => m.Message.Contains("Dedicated Game Server initialized and ready for battle!"));

        // Assert: GetLogsAsPlainText preserves 100% of raw logs for Copy All button
        string rawText = detailVm.GetLogsAsPlainText();
        Assert.Contains("autoexec.cfg", rawText);
        Assert.Contains("Failed to remove application binding with cpu core", rawText);
        Assert.Contains("CGameServer::StateStringChanged 1", rawText);
    }

    [Fact]
    public void GameServerInstanceDetailViewModel_RealErrors_NeverTreatedAsNoise_AndSurfacedToChat()
    {
        // Arrange
        GameServerInstanceDetailViewModel detailVm = new()
        {
            ServerName = "TEST-SERVER-ERRORS",
            GamePort = 11235
        };

        List<string> criticalLogs =
        [
            "Error: [19:00:01] CFileManager::Error: file corrupted",
            "NetDriver Initialization Failed: WSAEADDRINUSE (Address already in use)",
            "SoundManager: failed to bind DirectSound audio output",
            "Assertion failed: (pClient != nullptr)",
            "FATAL: Out of memory during world mesh allocation"
        ];

        // Verify IsRealError & IsStartupNoise standalone classification
        foreach (string line in criticalLogs)
        {
            Assert.True(GameServerInstanceDetailViewModel.IsRealError(line), $"Expected '{line}' to be classified as real error");
            Assert.False(GameServerInstanceDetailViewModel.IsStartupNoise(line), $"Expected '{line}' to NOT be classified as noise");
        }

        // Act: Ingest critical logs during startup
        detailVm.IngestLogs(criticalLogs);

        // Assert: Errors are counted and surfaced
        Assert.True(detailVm.HasErrors);
        Assert.Equal(5, detailVm.ErrorCount);
        Assert.Equal("⚠️ 5 Errors", detailVm.ErrorDisplay);

        // Assert: Error bubbles are added to ChatMessages with IsError = true
        var errorMessages = detailVm.ChatMessages.Where(m => m.IsError).ToList();
        Assert.Equal(5, errorMessages.Count);
        Assert.Contains(detailVm.ChatMessages, m => m.Message.Contains("CFileManager::Error: file corrupted") && m.IsError);
        Assert.Contains(detailVm.ChatMessages, m => m.Message.Contains("WSAEADDRINUSE") && m.IsError);
        Assert.Contains(detailVm.ChatMessages, m => m.Message.Contains("failed to bind DirectSound") && m.IsError);
        Assert.Contains(detailVm.ChatMessages, m => m.Message.Contains("Assertion failed") && m.IsError);
        Assert.Contains(detailVm.ChatMessages, m => m.Message.Contains("Out of memory") && m.IsError);

        // Act: Complete boot
        detailVm.IngestLogs(["Sv: [19:00:09] Connected to chat server.", "Sv: [19:00:10] CGameServer::StateStringChanged 1"]);

        // Assert: Boot completion announces warning about the detected errors
        Assert.Contains(detailVm.ChatMessages, m => m.Message.Contains("Dedicated Game Server initialized with 5 warning(s)/error(s)"));
        Assert.Equal("Ready (5 err)", detailVm.LastMessageSnippet);

        // Act: Clear chat resets error tracking
        detailVm.ClearChat();
        Assert.False(detailVm.HasErrors);
        Assert.Equal(0, detailVm.ErrorCount);
        Assert.Equal(string.Empty, detailVm.ErrorDisplay);
    }

    [Fact]
    public void GameServerInstanceDetailViewModel_HarmlessWarnings_TreatedAsStartupNoise()
    {
        // Arrange
        GameServerInstanceDetailViewModel detailVm = new()
        {
            ServerName = "TEST-SERVER-HARMLESS",
            GamePort = 11235
        };

        List<string> harmlessLogs =
        [
            "Error: [18:59:57] Failed to remove application binding with cpu core 0",
            "Warning: [18:59:58] Cvar 'sv_name' is a read-only value",
            "Error: [19:07:06] CFileManager::GetFile() [game/resources2.s2z]/heroes/devourer/alt4/sin_set_bonus/ability_01/effects/bullet.effect - Invalid file type",
            "Error: [19:07:06] Invalid effect: /heroes/devourer/alt4/sin_set_bonus/ability_01/effects/bullet.effect"
        ];

        // Assert: standalone classification
        foreach (string line in harmlessLogs)
        {
            Assert.True(GameServerInstanceDetailViewModel.IsHarmlessStartupWarning(line), $"Expected harmless warning for: {line}");
            Assert.False(GameServerInstanceDetailViewModel.IsRealError(line), $"Expected not real error for: {line}");
            Assert.True(GameServerInstanceDetailViewModel.IsStartupNoise(line), $"Expected startup noise for: {line}");
        }

        // Act
        detailVm.IngestLogs(harmlessLogs);

        // Assert: HasErrors remains false and no error messages exist
        Assert.False(detailVm.HasErrors);
        Assert.Equal(0, detailVm.ErrorCount);
        Assert.Empty(detailVm.ErrorDisplay);
        Assert.DoesNotContain(detailVm.ChatMessages, m => m.IsError);
    }

    [Fact]
    public void GameServerInstanceDetailViewModel_IngestLogs_FullRealServerRestartLogStream_ProperlyFilteredAndMilestonesDisplayed()
    {
        // Arrange: Start with an instance that already completed boot previously (simulating server that was running)
        GameServerInstanceDetailViewModel detailVm = new()
        {
            ServerName = "DONUT ARENA #1",
            GamePort = 11235
        };
        detailVm.IngestLogs(["Dedicated server initialized."]);
        Assert.Contains(detailVm.ChatMessages, m => m.Message.Contains("Dedicated Game Server initialized and ready for battle!"));

        // Act: Ingest exact logs from server restart as observed in production
        List<string> fullRestartLogs =
        [
            "Server on Port 11235 stopped.",
            "Server on Port 11235 stopped.",
            "[19:07:06] NetDriver Initialization",
            "Resource: [19:07:06] Loading ^970Entity definition^*: /heroes/devourer/alt4/sin_set_bonus/ability_01/projectile.entity",
            "Resource: [19:07:06] Loading ^970Entity definition^*: /heroes/devourer/hd_devourer/ability_01/projectile.entity",
            "Resource: [19:07:06] Loading ^970Entity definition^*: /heroes/devourer/hero.entity",
            "Resource: [19:07:06] Loading ^970Entity definition^*: /heroes/devourer/state_devourer_ult_boost.entity",
            "Resource: [19:07:06] Loading ^970Entity definition^*: /heroes/diseasedrider/ability_01/ability.entity",
            "Resource: [19:07:06] Loading ^970Entity definition^*: /heroes/diseasedrider/ability_01/state.entity",
            "Resource: [19:07:06] Loading ^970Entity definition^*: /heroes/diseasedrider/ability_01/state_slow.entity",
            "Resource: [19:07:06] Loading ^970Entity definition^*: /heroes/diseasedrider/ability_02/ability.entity",
            "Resource: [19:07:06] Loading ^970Entity definition^*: /heroes/diseasedrider/ability_02/state.entity",
            "Resource: [19:07:06] Loading ^970Entity definition^*: /heroes/diseasedrider/ability_02/state_buff.entity",
            "Resource: [19:07:06] Finished loading ^970Entity definition^*: /heroes/devourer/hero.entity",
            "Error: [19:07:06] CFileManager::GetFile() [C:\\Users\\KongorDev\\Documents\\Heroes of Newerth x64\\game\\resources2.s2z]/heroes/devourer/alt4/sin_set_bonus/ability_01/effects/bullet.effect - Invalid file type",
            "Error: [19:07:06] Invalid effect: /heroes/devourer/alt4/sin_set_bonus/ability_01/effects/bullet.effect",
            "Net: [19:07:06] Host: api.kongor.net",
            "Net: [19:07:06] User-Agent: K2 Engine / hon_x64",
            "Net: [19:07:06] Accept: */*",
            "Net: [19:07:06] Content-Length: 42",
            "Net: [19:07:06] 187",
            "Net: [19:07:06] a:5:{s:8:\"response\";s:2:\"OK\";}",
            "Net: [19:07:06] Created CSocket (11235)",
            "VoiceServer: [19:07:06] Initializing VoiceServer on port 11236",
            "Sv: [19:07:06] CGameServer::StateStringChanged 2",
            "Sv: [19:07:06] CGameServer::StateStringChanged 1",
            "Sv: [19:07:06] Connected to chat server.",
            "Sv: [19:07:06] Project KONGOR Remote Configuration Was Injected Successfully"
        ];
        detailVm.IngestLogs(fullRestartLogs);

        // Assert: No errors recorded (harmless missing visual assets were properly filtered)
        Assert.False(detailVm.HasErrors);
        Assert.Equal(0, detailVm.ErrorCount);
        Assert.Empty(detailVm.ErrorDisplay);

        // Assert: Humorous milestones appeared
        Assert.Contains(detailVm.ChatMessages, m => m.Message.Contains("Combobulating Legion & Hellbourne Leylines"));
        Assert.Contains(detailVm.ChatMessages, m => m.Message.Contains("Sharpening Kongor's cleaver"));
        Assert.Contains(detailVm.ChatMessages, m => m.Message.Contains("Calibrating Caldavar mana streams"));
        Assert.Contains(detailVm.ChatMessages, m => m.Message.Contains("Stocking Kongor's stash with bananas"));
        Assert.Contains(detailVm.ChatMessages, m => m.Message.Contains("Dedicated Game Server initialized and ready for battle!"));

        // Assert: Raw noise was suppressed from chat
        Assert.DoesNotContain(detailVm.ChatMessages, m => m.Message.Contains("bullet.effect"));
        Assert.DoesNotContain(detailVm.ChatMessages, m => m.Message.Contains("api.kongor.net"));
        Assert.DoesNotContain(detailVm.ChatMessages, m => m.Message.Contains("Created CSocket"));
        Assert.DoesNotContain(detailVm.ChatMessages, m => m.Message.Contains("VoiceServer"));

        // Assert: 100% of the raw log history is retained for the Copy All button
        string rawText = detailVm.GetLogsAsPlainText();
        Assert.Contains("Server on Port 11235 stopped.", rawText);
        Assert.Contains("bullet.effect - Invalid file type", rawText);
        Assert.Contains("Host: api.kongor.net", rawText);
        Assert.Contains("StateStringChanged 2", rawText);
        Assert.Contains("Remote Configuration Was Injected Successfully", rawText);
    }

    [Fact]
    public async Task GameServersViewModel_WhenWorkerCrashes_ReflectsCrashedStateAndRedBadge()
    {
        // Arrange
        Mock<IGameServerFleetService> mockFleet = new();
        List<GameServerHostEntry> entries =
        [
            new()
            {
                Id = "host-1",
                Name = "DONUT ARENA",
                HostAddress = "127.0.0.1",
                Port = 11234,
                Location = "USE"
            }
        ];

        mockFleet.Setup(f => f.GetDiscoveredHostsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(entries);

        mockFleet.Setup(f => f.PingHostAsync(It.IsAny<string>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((true, 5, new CompelPingResponseDto { Application = "COMPEL", Version = "2.0.0" }));

        CompelStatusResponseDto statusDto = new()
        {
            Application = "COMPEL",
            ServerNamePrefix = "DONUT ARENA",
            Location = "USE",
            Instances = 2,
            ManagerRunning = true,
            ActiveWorkers = 1,
            Ports = new CompelPortAllocationDto
            {
                GameStart = 11235,
                GameEnd = 11236,
                VoiceStart = 11435,
                VoiceEnd = 11436
            },
            Workers =
            [
                new CompelWorkerStatusDto
                {
                    InstanceIndex = 0,
                    Port = 11235,
                    Running = true,
                    State = "Available"
                },
                new CompelWorkerStatusDto
                {
                    InstanceIndex = 1,
                    Port = 11236,
                    Running = false,
                    CrashCount = 1,
                    State = "Crashed"
                }
            ]
        };

        mockFleet.Setup(f => f.GetHostStatusAsync(It.IsAny<string>(), It.IsAny<int>(), It.IsAny<string?>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync((true, statusDto, null));

        // Act
        GameServersViewModel vm = new(mockFleet.Object);
        await vm.LoadAndRefreshFleetAsync();

        // Assert
        Assert.Single(vm.Hosts);
        var host = vm.Hosts[0];
        Assert.Equal(2, host.InstancesList.Count);

        // Instance 1: Available
        var inst1 = host.InstancesList.First(i => i.GamePort == 11235);
        Assert.True(inst1.IsRunning);
        Assert.Equal("Available", inst1.ServerStatus);
        Assert.Equal("🟢", inst1.StatusIcon);
        Assert.Equal("#10B981", inst1.StatusColor);

        // Instance 2: Crashed
        var inst2 = host.InstancesList.First(i => i.GamePort == 11236);
        Assert.False(inst2.IsRunning);
        Assert.Equal("Crashed", inst2.ServerStatus);
        Assert.Equal("⚠️", inst2.StatusIcon);
        Assert.Equal("Crashed (Faulted)", inst2.StatusText);
        Assert.Equal("#EF4444", inst2.StatusColor);
        Assert.Equal("#450A0A", inst2.StatusBadgeBackground);

        // Host reflects Degraded
        Assert.Contains("Degraded", host.StatusText);
        Assert.Equal(1, vm.TotalActiveInstances);
    }

    [Fact]
    public void GameServerInstanceDetailViewModel_IngestLogs_WhenBindFails_TransitionsToCrashed()
    {
        // Arrange
        GameServerInstanceDetailViewModel detailVm = new()
        {
            GamePort = 11236,
            ServerName = "DONUT-ARENA-USE-2",
            IsRunning = true
        };
        detailVm.UpdateStatus("Starting", isRunning: true);

        // Act: Ingest real log stream containing port bind failure and server shutdown
        List<string> bindFailureLogs =
        [
            "Net: [19:18:46] [SERVER_GAME] Attempting to open port: 11236",
            "Error: [19:18:46] CNetDriver::OpenPort() - bind() failed: Only one usage of each socket address (protocol/network address/port) is normally permitted.",
            "Error: [19:18:46] CHostServer::Init() - Couldn't open port 11236",
            "Error: [19:18:46] CHost::StartServer() - Failed to initialize server",
            "Sv: [19:18:46] Shutting down...",
            "Server on Port 11236 stopped."
        ];
        detailVm.IngestLogs(bindFailureLogs);

        // Assert: Instance transitioned to Crashed with red badge
        Assert.False(detailVm.IsRunning);
        Assert.Equal("Crashed", detailVm.ServerStatus);
        Assert.Equal("⚠️", detailVm.StatusIcon);
        Assert.Equal("Crashed (Faulted)", detailVm.StatusText);
        Assert.Equal("#EF4444", detailVm.StatusColor);
        Assert.Equal("#450A0A", detailVm.StatusBadgeBackground);
        Assert.True(detailVm.HasErrors);
        Assert.True(detailVm.ErrorCount >= 3);
    }

    [Fact]
    public async Task GameServersViewModel_ChatFilter_WhenCrashedSelected_FiltersToFaultedServers()
    {
        // Arrange
        Mock<IGameServerFleetService> mockFleet = new();
        List<GameServerHostEntry> entries =
        [
            new() { Id = "host-1", Name = "Cluster-1", HostAddress = "127.0.0.1", Port = 11234, Location = "USE" }
        ];

        mockFleet.Setup(f => f.GetDiscoveredHostsAsync(It.IsAny<CancellationToken>())).ReturnsAsync(entries);
        mockFleet.Setup(f => f.PingHostAsync(It.IsAny<string>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((true, 10, new CompelPingResponseDto()));

        CompelStatusResponseDto statusDto = new()
        {
            Application = "COMPEL",
            Instances = 3,
            ManagerRunning = true,
            ActiveWorkers = 2,
            Ports = new CompelPortAllocationDto { GameStart = 11235, GameEnd = 11237 },
            Workers =
            [
                new CompelWorkerStatusDto { InstanceIndex = 0, Port = 11235, Running = true, State = "Available" },
                new CompelWorkerStatusDto { InstanceIndex = 1, Port = 11236, Running = false, State = "Crashed" },
                new CompelWorkerStatusDto { InstanceIndex = 2, Port = 11237, Running = true, State = "Available" }
            ]
        };

        mockFleet.Setup(f => f.GetHostStatusAsync(It.IsAny<string>(), It.IsAny<int>(), It.IsAny<string?>(),
                It.IsAny<CancellationToken>())).ReturnsAsync((true, statusDto, null));

        GameServersViewModel vm = new(mockFleet.Object);
        await vm.LoadAndRefreshFleetAsync();

        // Act: Filter by CRASHED
        vm.SelectedChatStatusFilter = "CRASHED";

        // Assert: Only instance 2 (Port 11236) is visible
        Assert.Single(vm.FilteredChatInstances);
        Assert.Equal(11236, vm.FilteredChatInstances[0].GamePort);
        Assert.Equal("Crashed", vm.FilteredChatInstances[0].ServerStatus);
    }

    [Fact]
    public void PortPlan_BaseVoiceRelayPort_DoesNotOverlapGameOrVoiceServerWindows()
    {
        // Verify Voice Relay Port (11600) sits outside 300 game server ports (11235..11534)
        // and 300 dedicated voice server ports (11735..12034)
        int gameStart = COMPEL.Services.Supervision.PortPlan.BaseGamePort;
        int gameEnd = gameStart + COMPEL.Services.Supervision.PortPlan.PortRangeWindow - 1;
        int voiceStart = COMPEL.Services.Supervision.PortPlan.BaseVoicePort;
        int voiceEnd = voiceStart + COMPEL.Services.Supervision.PortPlan.PortRangeWindow - 1;
        int voiceRelayPort = COMPEL.Services.Supervision.PortPlan.BaseVoiceRelayPort;

        Assert.Equal(11600, voiceRelayPort);
        Assert.True(voiceRelayPort > gameEnd, "Voice relay must not collide with game server range");
        Assert.True(voiceRelayPort < voiceStart || voiceRelayPort > voiceEnd, "Voice relay must not collide with voice server range");
    }

    [Fact]
    public async Task GameServersViewModel_ToggleContainerLauncherAsync_TogglesDialogAndLoadsSecrets()
    {
        // Arrange
        Mock<IGameServerFleetService> mockFleet = new();
        Mock<ISecretsManagerService> mockSecrets = new();
        Mock<IDockerHealthService> mockDocker = new();

        NexusSecrets secrets = new()
        {
            HostAccountUserName = "testoperator",
            CompelPriorityMode = "HighPriority",
            ContainerRuntimeTarget = "Docker"
        };
        mockSecrets.Setup(s => s.LoadSecretsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(secrets);
        mockDocker.Setup(d => d.IsImageBuiltAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);

        GameServersViewModel vm = new(
            mockFleet.Object,
            orchestrator: null,
            secretsManager: mockSecrets.Object,
            dockerHealth: mockDocker.Object);

        // Act
        await vm.ToggleContainerLauncherAsync();

        // Assert
        Assert.True(vm.ShowContainerLauncherDialog);
        Assert.Equal("HighPriority", vm.CompelPriorityMode);
        Assert.Equal("Docker", vm.SelectedContainerPlatform);
        Assert.True(vm.IsDockerImageBuilt);
        Assert.Contains("HighPriority", vm.GeneratedContainerCommand);
    }

    [Fact]
    public void GameServersViewModel_NavigateToSecretsConfig_InvokesCallbackAndClosesDialog()
    {
        // Arrange
        Mock<IGameServerFleetService> mockFleet = new();
        bool callbackInvoked = false;

        GameServersViewModel vm = new(
            mockFleet.Object,
            orchestrator: null,
            secretsManager: null,
            dockerHealth: null,
            onNavigateToSecretsConfig: () => callbackInvoked = true);

        vm.ShowContainerLauncherDialog = true;

        // Act
        vm.NavigateToSecretsConfig();

        // Assert
        Assert.False(vm.ShowContainerLauncherDialog);
        Assert.True(callbackInvoked);
    }

    [Fact]
    public void GameServersViewModel_PlatformChange_UpdatesGeneratedCommand()
    {
        // Arrange
        Mock<IGameServerFleetService> mockFleet = new();
        GameServersViewModel vm = new(mockFleet.Object);

        // Act
        vm.SelectedContainerPlatform = "Minikube";

        // Assert
        Assert.Contains("minikube image load", vm.GeneratedContainerCommand);
        Assert.Contains("kubectl run compel-minikube", vm.GeneratedContainerCommand);
    }

    [Fact]
    public async Task GameServersViewModel_RevealContainerSecretsInCommand_TogglesSecretMaskingInCommand()
    {
        // Arrange
        Mock<IGameServerFleetService> mockFleet = new();
        Mock<ISecretsManagerService> mockSecrets = new();

        NexusSecrets secrets = new()
        {
            HostAccountUserName = "testoperator",
            HostAccountPassword = "secretpassword123",
            CompelAuthenticationToken = "supersecrettoken999",
            CompelPriorityMode = "HighPriority",
            ContainerRuntimeTarget = "Docker"
        };
        mockSecrets.Setup(s => s.LoadSecretsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(secrets);

        GameServersViewModel vm = new(
            mockFleet.Object,
            secretsManager: mockSecrets.Object);

        await vm.ToggleContainerLauncherAsync();

        // Default state: RevealContainerSecretsInCommand == false -> secrets masked with bullets
        Assert.False(vm.RevealContainerSecretsInCommand);
        Assert.Contains("••••••••••••", vm.GeneratedContainerCommand);
        Assert.DoesNotContain("secretpassword123", vm.GeneratedContainerCommand);
        Assert.DoesNotContain("supersecrettoken999", vm.GeneratedContainerCommand);

        // Act: Reveal secrets
        vm.RevealContainerSecretsInCommand = true;

        // Assert: secrets revealed
        Assert.True(vm.RevealContainerSecretsInCommand);
        Assert.Contains("secretpassword123", vm.GeneratedContainerCommand);
        Assert.Contains("supersecrettoken999", vm.GeneratedContainerCommand);

        // Act: Mask secrets again
        vm.RevealContainerSecretsInCommand = false;

        // Assert: secrets re-masked
        Assert.False(vm.RevealContainerSecretsInCommand);
        Assert.Contains("••••••••••••", vm.GeneratedContainerCommand);
        Assert.DoesNotContain("secretpassword123", vm.GeneratedContainerCommand);
        Assert.DoesNotContain("supersecrettoken999", vm.GeneratedContainerCommand);
    }

    [Fact]
    public void GameServersViewModel_ContainerPlatformsAndPriorityModes_ContainExpectedPresets()
    {
        // Arrange
        Mock<IGameServerFleetService> mockFleet = new();
        GameServersViewModel vm = new(mockFleet.Object);

        // Assert
        Assert.Contains("Docker", vm.ContainerPlatforms);
        Assert.Contains("Minikube", vm.ContainerPlatforms);
        Assert.Contains("Kubernetes", vm.ContainerPlatforms);

        Assert.Contains("Background", vm.CompelPriorityModes);
        Assert.Contains("HighPriority", vm.CompelPriorityModes);
        Assert.Contains("Adaptive", vm.CompelPriorityModes);
    }

    [Fact]
    public void GameServerInstanceDetailViewModel_IngestLogs_HistoricalStoppedLogReplay_DoesNotRevertLiveAvailableStatus()
    {
        // Arrange: Server boots up after a prior stop event
        GameServerInstanceDetailViewModel detailVm = new()
        {
            ServerName = "KONGOR ARENA #1",
            GamePort = 11235
        };

        DateTimeOffset t0 = TimeProvider.System.GetUtcNow().AddMinutes(-5);
        List<CompelDedicatedServerLogLineDto> initialBatch =
        [
            new() { Text = "Server on Port 11235 stopped.", Timestamp = t0, Source = "system" },
            new() { Text = "[14:56:52] K2 Engine start up...", Timestamp = t0.AddSeconds(1), Source = "server" },
            new() { Text = "[14:56:52] Executing \"/init.cfg\"", Timestamp = t0.AddSeconds(2), Source = "server" },
            new() { Text = "Sv: [14:56:52] Connected to chat server.", Timestamp = t0.AddSeconds(3), Source = "server" },
            new() { Text = "Sv: [14:56:52] Project KONGOR Remote Configuration Was Injected Successfully", Timestamp = t0.AddSeconds(4), Source = "server" }
        ];

        detailVm.IngestLogs(initialBatch);
        Assert.True(detailVm.IsRunning);
        Assert.Equal("Available", detailVm.ServerStatus);
        Assert.Equal("🟢", detailVm.StatusIcon);

        // Act: Operator dispatches ping, COMPEL executes command and returns the 100-tail buffer
        // containing historical stopped log followed by new ping confirmation
        detailVm.AddOperatorMessage("echo ping");

        DateTimeOffset t1 = TimeProvider.System.GetUtcNow();
        List<CompelDedicatedServerLogLineDto> polledTailWithPing =
        [
            new() { Text = "Server on Port 11235 stopped.", Timestamp = t0, Source = "system" },
            new() { Text = "[14:56:52] K2 Engine start up...", Timestamp = t0.AddSeconds(1), Source = "server" },
            new() { Text = "Sv: [14:56:52] Connected to chat server.", Timestamp = t0.AddSeconds(3), Source = "server" },
            new() { Text = "Sv: [14:56:52] Project KONGOR Remote Configuration Was Injected Successfully", Timestamp = t0.AddSeconds(4), Source = "server" },
            new() { Text = "> echo ping", Timestamp = t1, Source = "operator" },
            new() { Text = ">[15:05:48] >echo ping", Timestamp = t1.AddMilliseconds(10), Source = "server" },
            new() { Text = "[15:05:48] ping", Timestamp = t1.AddMilliseconds(20), Source = "server" }
        ];

        detailVm.IngestLogs(polledTailWithPing);

        // Assert: Instance MUST remain Available and Running (not falsely reverted to Stopped)
        Assert.True(detailVm.IsRunning);
        Assert.Equal("Available", detailVm.ServerStatus);
        Assert.Equal("🟢", detailVm.StatusIcon);
        Assert.Equal("Available (Ready)", detailVm.StatusText);
    }

    [Fact]
    public void GameServerInstanceDetailViewModel_IngestLogs_BenignHonPingShimStderr_DoesNotTriggerErrors()
    {
        // Arrange
        GameServerInstanceDetailViewModel detailVm = new()
        {
            ServerName = "KONGOR ARENA #1",
            GamePort = 11235
        };

        DateTimeOffset now = TimeProvider.System.GetUtcNow();
        List<CompelDedicatedServerLogLineDto> logs =
        [
            new() { Text = "[HON_PING_SHIM] Initialized configuration: server_name='DONUT ARENA #1', location='USE', version='5.0.0.0'", Timestamp = now, Source = "server", IsError = true },
            new() { Text = "[HON_PING_SHIM] libhon-ping-shim loaded into PID 1234 (real_recvfrom = 0x7f1234)", Timestamp = now.AddMilliseconds(1), Source = "server", IsError = true },
            new() { Text = "[HON_PING_SHIM] SUCCESS: Patched libk2 host promotion at 0x7f1234 (base 0x7f1000): client 0 auto-promoted to host!", Timestamp = now.AddMilliseconds(2), Source = "server", IsError = true },
            new() { Text = "[HON_PING_SHIM] PING received from 127.0.0.1:54321 (46 bytes, challenge 0xABCD)", Timestamp = now.AddMilliseconds(3), Source = "server", IsError = true },
            new() { Text = "[HON_PING_SHIM] PONG sent to 127.0.0.1:54321 (75 bytes, sendto result: 75)", Timestamp = now.AddMilliseconds(4), Source = "server", IsError = true },
            new() { Text = "Sv: [14:56:52] Project KONGOR Remote Configuration Was Injected Successfully", Timestamp = now.AddMilliseconds(5), Source = "server" }
        ];

        // Act
        detailVm.IngestLogs(logs);

        // Assert: No errors recorded and error count remains 0
        Assert.False(detailVm.HasErrors);
        Assert.Equal(0, detailVm.ErrorCount);
        Assert.Empty(detailVm.ErrorDisplay);
    }

    [Fact]
    public async Task GameServersViewModel_WhenAllHostsOffline_ProgressivelyBacksOffPolling()
    {
        // Arrange: A host that fails to ping (offline)
        Mock<IGameServerFleetService> mockFleet = new();
        GameServerHostEntry entry = new()
        {
            Id = "host-offline-test",
            Name = "Local Game Server (COMPEL)",
            HostAddress = "127.0.0.1",
            Port = 11234,
            Location = "USE"
        };

        mockFleet.Setup(f => f.GetDiscoveredHostsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync([entry]);
        mockFleet.Setup(f => f.PingHostAsync(It.IsAny<string>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((false, -1, null));

        GameServersViewModel vm = new(mockFleet.Object);
        await vm.LoadAndRefreshFleetAsync();

        // Initial offline interval after load
        Assert.Equal(TimeSpan.FromSeconds(5), vm.CurrentPollInterval);

        // Act & Assert: Successive auto-polls back off exponentially
        await vm.AutoPollFleetAsync();
        Assert.Equal(TimeSpan.FromSeconds(10), vm.CurrentPollInterval);

        await vm.AutoPollFleetAsync();
        Assert.Equal(TimeSpan.FromSeconds(20), vm.CurrentPollInterval);

        await vm.AutoPollFleetAsync();
        Assert.Equal(TimeSpan.FromSeconds(30), vm.CurrentPollInterval);

        await vm.AutoPollFleetAsync();
        Assert.Equal(TimeSpan.FromSeconds(60), vm.CurrentPollInterval);

        // Stays capped at 60s
        await vm.AutoPollFleetAsync();
        Assert.Equal(TimeSpan.FromSeconds(60), vm.CurrentPollInterval);
    }

    [Fact]
    public async Task GameServersViewModel_ResetPollBackoff_ImmediatelySnapsToFastPolling()
    {
        // Arrange: Start with backed off polling
        Mock<IGameServerFleetService> mockFleet = new();
        mockFleet.Setup(f => f.GetDiscoveredHostsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync([]);
        mockFleet.Setup(f => f.PingHostAsync(It.IsAny<string>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((false, -1, null));

        GameServersViewModel vm = new(mockFleet.Object);
        for (int i = 0; i < 4; i++)
        {
            await vm.AutoPollFleetAsync();
        }
        Assert.True(vm.CurrentPollInterval >= TimeSpan.FromSeconds(30));

        // Act: Reset backoff (e.g. user refreshed, or cluster started)
        vm.ResetPollBackoff();

        // Assert: Snapped back to 3 seconds
        Assert.Equal(TimeSpan.FromSeconds(3), vm.CurrentPollInterval);
    }
}

