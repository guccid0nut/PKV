// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Controls;
using Moq;
using NEXUS.ControlPanel.GUI.Services;
using NEXUS.ControlPanel.GUI.Services.Models;
using NEXUS.ControlPanel.GUI.ViewModels;
using WILLOWMAKER.Core.Models;
using WILLOWMAKER.Core.Services;
using Xunit;
using WmMainViewModel = WILLOWMAKER.Core.ViewModels.MainViewModel;

namespace NEXUS.ControlPanel.Tests;

public sealed class UnifiedCockpitTests
{
    [Fact]
    public async Task OpenOrSyncWillowmaker_WhenWillowmakerListening_SyncsLocalAndWanPairsOverNamedPipe()
    {
        // protects: End-to-end Unified Cockpit Named Pipe IPC bridge between NEXUS.ControlPanel and WILLOWMAKER.
        // fails_when: Control Panel fails to detect running WILLOWMAKER, spawns a duplicate launcher, or syncs mismatched Master/CDN endpoints.
        // seam: DashboardViewModel.ResolveActiveCockpitEndpointPair + CockpitAppBridgeService <-> NamedPipe <-> WILLOWMAKER MainViewModel.

        string pipeName = $"cp-wm-live-{Guid.NewGuid():N}";
        WmMainViewModel willowVm = new();
        List<ComboBoxItem> comboItems =
        [
            new ComboBoxItem { Content = EndpointPairRegistry.MasterServerLocalOfflineAuto },
            new ComboBoxItem { Content = EndpointPairRegistry.MasterServerOfflineMode },
            new ComboBoxItem { Content = EndpointPairRegistry.MasterServerLocalhost55555 },
            new ComboBoxItem { Content = EndpointPairRegistry.MasterServerNewbsIo },
            new ComboBoxItem { Content = EndpointPairRegistry.MasterServerKongorNet },
            new ComboBoxItem { Content = EndpointPairRegistry.MasterServerCustomAddress }
        ];
        willowVm.RegisterMasterServerComboItems(comboItems);
        willowVm.MasterServerAddress = comboItems[1]; // Offline initially

        int broughtToFrontCount = 0;
        willowVm.RequestBringToFront = () =>
        {
            broughtToFrontCount++;
            return true;
        };

        Mock<INexusOrchestratorService> orchestratorMock = new();
        orchestratorMock.SetupGet(o => o.CurrentClusterState).Returns(ServerState.Running);
        orchestratorMock
            .Setup(o => o.GetQueueArcadeStateAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync((true, false, "Enabled"));

        Mock<IDockerHealthService> dockerMock = new();
        dockerMock
            .Setup(d => d.CheckDockerStatusAsync())
            .ReturnsAsync(new DockerStatus { IsRunning = false, StatusMessage = "Containerless" });

        Mock<IDatabaseHealthService> dbMock = new();
        dbMock.Setup(d => d.CheckDatabaseHealthAsync(It.IsAny<ISecretsManagerService?>()))
            .ReturnsAsync(new DatabaseStatus { IsConnected = true, DatabaseType = "PostgreSQL", StatusMessage = "Online" });
        dbMock.Setup(d => d.CheckRedisHealthAsync())
            .ReturnsAsync(new DatabaseStatus { IsConnected = true, StatusMessage = "Online" });
        dbMock.Setup(d => d.CheckPyroscopeHealthAsync())
            .ReturnsAsync(new DatabaseStatus { IsConnected = false, StatusMessage = "Offline" });

        CockpitAppBridgeService bridge = new(
            orchestratorMock.Object,
            processLauncherOverride: _ => throw new InvalidOperationException("Must not cold-launch when pipe is active!"));

        using DashboardViewModel dashboard = new(
            orchestratorMock.Object,
            dockerMock.Object,
            dbMock.Object,
            cockpitBridge: bridge);

        try
        {
            Assert.True(await willowVm.EnsureCockpitIpcServerAsync(pipeName, persistAcrossSessions: true));

            // 1. Local cluster active -> resolves localhost:55555 + localhost:55555/cdn
            (string localMaster, string localCdn) = dashboard.ResolveActiveCockpitEndpointPair();
            Assert.Equal("localhost:55555", localMaster);
            Assert.Equal("localhost:55555/cdn", localCdn);

            CockpitLaunchOrSyncResult localSync = await bridge.OpenOrSyncWillowmakerAsync(
                localMaster,
                localCdn,
                pipeNameOverride: pipeName);

            Assert.True(localSync.Succeeded);
            Assert.True(localSync.WasAlreadyRunning);
            Assert.True(localSync.SyncedViaIpc);
            Assert.False(localSync.LaunchedNewProcess);
            Assert.Equal("localhost:55555", localSync.AppliedMasterServer);
            Assert.Equal("localhost:55555/cdn", localSync.AppliedCdnEndpoint);
            Assert.Equal(1, broughtToFrontCount);

            WillowmakerCockpitStatus probe = await bridge.ProbeWillowmakerIpcAsync(pipeNameOverride: pipeName);
            Assert.True(probe.IsConnected);
            Assert.Equal("localhost:55555", probe.MasterServerEndpoint);
            Assert.Equal("localhost:55555/cdn", probe.CdnEndpoint);

            // 2. Public Internet (WAN) mode -> resolves configured WAN host:55555 + host:55555/cdn
            dashboard.SelectedHostingMode = "Public Internet";
            await dashboard.ConfirmWanWarningAsync();
            dashboard.WanHost = "play.kongor-test.org";

            (string wanMaster, string wanCdn) = dashboard.ResolveActiveCockpitEndpointPair();
            Assert.Equal("play.kongor-test.org:55555", wanMaster);
            Assert.Equal("play.kongor-test.org:55555/cdn", wanCdn);

            CockpitLaunchOrSyncResult wanSync = await bridge.OpenOrSyncWillowmakerAsync(
                wanMaster,
                wanCdn,
                discordClientId: "109876543210987654",
                pipeNameOverride: pipeName);

            Assert.True(wanSync.Succeeded);
            Assert.True(wanSync.SyncedViaIpc);
            Assert.Equal("play.kongor-test.org:55555", wanSync.AppliedMasterServer);
            Assert.Equal("play.kongor-test.org:55555/cdn", wanSync.AppliedCdnEndpoint);
            Assert.Equal("play.kongor-test.org:55555", willowVm.CustomMasterServerAddress);
            Assert.Equal("109876543210987654", willowVm.CockpitDiscordAppId);
            Assert.Equal(2, broughtToFrontCount);
        }
        finally
        {
            await willowVm.StopCockpitIpcAsync();
            willowVm.DisposeApplicationBackgroundServices();
        }
    }

    [Fact]
    public async Task ColdLaunch_WillowmakerAndCompelGui_ResolvesAcrossStandaloneAndDevLayouts_WithDotNet11Env()
    {
        // protects: Cross-layout binary discovery (Standalone Bundle vs Dev Checkout) and .NET 11 environment propagation on cold launch.
        // fails_when: Standalone tools-*/COMPEL.GUI or root WILLOWMAKER binaries are missed, or DOTNET_ROOT / endpoint env vars are omitted.
        // seam: CockpitAppBridgeService.ResolveWillowmakerBinary, ResolveCompelGuiBinary, OpenOrSyncWillowmakerAsync, and OpenCompelGuiAsync.

        string tempRoot = Path.Combine(Path.GetTempPath(), $"cp-layouts-{Guid.NewGuid():N}");
        try
        {
            // 1. Standalone Windows + Linux + macOS bundle layouts
            string winBundle = Path.Combine(tempRoot, "win-bundle");
            Directory.CreateDirectory(Path.Combine(winBundle, "tools-win-x64"));
            string winWm = Path.Combine(winBundle, "WILLOWMAKER.exe");
            string winCompel = Path.Combine(winBundle, "tools-win-x64", "COMPEL.GUI.exe");
            File.WriteAllBytes(winWm, [0x4D, 0x5A]);
            File.WriteAllBytes(winCompel, [0x4D, 0x5A]);

            string linuxBundle = Path.Combine(tempRoot, "linux-bundle");
            Directory.CreateDirectory(Path.Combine(linuxBundle, "tools-linux-x64"));
            string linuxWm = Path.Combine(linuxBundle, "WILLOWMAKER");
            string linuxCompel = Path.Combine(linuxBundle, "tools-linux-x64", "COMPEL.GUI");
            File.WriteAllBytes(linuxWm, [0x7F, (byte)'E', (byte)'L', (byte)'F']);
            File.WriteAllBytes(linuxCompel, [0x7F, (byte)'E', (byte)'L', (byte)'F']);

            string macBundle = Path.Combine(tempRoot, "mac-bundle");
            Directory.CreateDirectory(Path.Combine(macBundle, "tools-macos-x64"));
            string macWm = Path.Combine(macBundle, "WILLOWMAKER");
            string macCompel = Path.Combine(macBundle, "tools-macos-x64", "COMPEL.GUI");
            File.WriteAllBytes(macWm, [0xCF, 0xFA, 0xED, 0xFE]);
            File.WriteAllBytes(macCompel, [0xCF, 0xFA, 0xED, 0xFE]);

            // 2. Simulated Dev Checkout layout
            string devRepo = Path.Combine(tempRoot, "dev-repo");
            Directory.CreateDirectory(devRepo);
            File.WriteAllText(Path.Combine(devRepo, "ProjectKongor.slnx"), "<Solution />");
            string devWmDir = Path.Combine(devRepo, "src", "apps", "willowmaker", "bin", "Release", "net11.0");
            string devCompelDir = Path.Combine(devRepo, "src", "apps", "compel-gui", "bin", "Release", "net11.0");
            Directory.CreateDirectory(devWmDir);
            Directory.CreateDirectory(devCompelDir);
            string devWmExe = Path.Combine(devWmDir, OperatingSystem.IsWindows() ? "WILLOWMAKER.exe" : "WILLOWMAKER");
            string devCompelExe = Path.Combine(devCompelDir, OperatingSystem.IsWindows() ? "COMPEL.GUI.exe" : "COMPEL.GUI");
            File.WriteAllBytes(devWmExe, [0x4D, 0x5A]);
            File.WriteAllBytes(devCompelExe, [0x4D, 0x5A]);

            ProcessStartInfo? capturedPsi = null;
            CockpitAppBridgeService bridge = new(processLauncherOverride: psi =>
            {
                capturedPsi = psi;
                return 42420;
            });

            Assert.Equal(winWm, bridge.ResolveWillowmakerBinary(winBundle, "windows").ExecutablePath);
            Assert.Equal(winCompel, bridge.ResolveCompelGuiBinary(winBundle, "windows").ExecutablePath);
            Assert.Equal(linuxWm, bridge.ResolveWillowmakerBinary(linuxBundle, "linux").ExecutablePath);
            Assert.Equal(linuxCompel, bridge.ResolveCompelGuiBinary(linuxBundle, "linux").ExecutablePath);
            Assert.Equal(macWm, bridge.ResolveWillowmakerBinary(macBundle, "macos").ExecutablePath);
            Assert.Equal(macCompel, bridge.ResolveCompelGuiBinary(macBundle, "macos").ExecutablePath);

            CockpitBinaryResolution devWmRes = bridge.ResolveWillowmakerBinary(devRepo);
            Assert.True(devWmRes.Found);
            Assert.Equal("DevCheckoutRelease", devWmRes.LayoutKind);
            Assert.Equal(devWmExe, devWmRes.ExecutablePath);

            CockpitBinaryResolution devCompelRes = bridge.ResolveCompelGuiBinary(devRepo);
            Assert.True(devCompelRes.Found);
            Assert.Equal("DevCheckoutRelease", devCompelRes.LayoutKind);
            Assert.Equal(devCompelExe, devCompelRes.ExecutablePath);

            // Cold launch WILLOWMAKER when pipe is absent
            string unusedPipe = $"cp-absent-{Guid.NewGuid():N}";
            CockpitLaunchOrSyncResult launchWm = await bridge.OpenOrSyncWillowmakerAsync(
                "localhost:55555",
                "localhost:55555/cdn",
                rootOverride: devRepo,
                pipeNameOverride: unusedPipe);

            Assert.True(launchWm.Succeeded);
            Assert.True(launchWm.LaunchedNewProcess);
            Assert.Equal(42420, launchWm.ProcessId);
            Assert.NotNull(capturedPsi);
            Assert.Equal("localhost:55555", capturedPsi!.Environment["KONGOR_COCKPIT_MASTER_SERVER"]);
            Assert.Equal("localhost:55555/cdn", capturedPsi.Environment["KONGOR_COCKPIT_CDN_ENDPOINT"]);

            // Launch COMPEL.GUI
            CompelGuiLaunchResult launchCompel = await bridge.OpenCompelGuiAsync(rootOverride: devRepo);
            Assert.True(launchCompel.Succeeded);
            Assert.Equal(42420, launchCompel.ProcessId);
            Assert.Equal("http://127.0.0.1:11234", capturedPsi.Environment["COMPEL_CONTROL_PLANE_URL"]);
        }
        finally
        {
            if (Directory.Exists(tempRoot))
            {
                Directory.Delete(tempRoot, recursive: true);
            }
        }
    }

    [Fact]
    public async Task DiagnosticsProbes_NativeClientPipeAndCoreClusterEndpoints_VerifyLiveHandshakeAndTcpReadiness()
    {
        // protects: Live Diagnostics probes for Game Client Native Pipe (0x574D Ping/Pong), MasterServer /health, and ChatServer TCP.
        // fails_when: Frame endianness/magic mismatches TAO native pipe protocol or HTTP/TCP readiness probes misreport live sockets.
        // seam: CockpitAppBridgeService.ProbeNativeClientPipeAsync + ProbeCoreClusterEndpointsAsync over real OS pipe & TCP sockets.

        string nativePipeName = $"tao-native-{Guid.NewGuid():N}";
        using CancellationTokenSource serverCts = new(TimeSpan.FromSeconds(5));

        Task pipeServerTask = Task.Run(async () =>
        {
            await using NamedPipeServerStream server = new(
                nativePipeName,
                PipeDirection.InOut,
                1,
                PipeTransmissionMode.Byte,
                PipeOptions.Asynchronous);
            await server.WaitForConnectionAsync(serverCts.Token);

            byte[] reqHeader = new byte[12];
            int read = await server.ReadAsync(reqHeader, serverCts.Token);
            Assert.Equal(12, read);
            ushort magic = BinaryPrimitives.ReadUInt16LittleEndian(reqHeader.AsSpan(0, 2));
            ushort opCode = BinaryPrimitives.ReadUInt16LittleEndian(reqHeader.AsSpan(2, 2));
            uint reqId = BinaryPrimitives.ReadUInt32LittleEndian(reqHeader.AsSpan(8, 4));
            Assert.Equal((ushort)0x574D, magic);
            Assert.Equal((ushort)0x0001, opCode); // Ping

            byte[] pongHeader = new byte[12];
            BinaryPrimitives.WriteUInt16LittleEndian(pongHeader.AsSpan(0, 2), 0x574D);
            BinaryPrimitives.WriteUInt16LittleEndian(pongHeader.AsSpan(2, 2), 0x0002); // Pong
            BinaryPrimitives.WriteInt32LittleEndian(pongHeader.AsSpan(4, 4), 0);
            BinaryPrimitives.WriteUInt32LittleEndian(pongHeader.AsSpan(8, 4), reqId);
            await server.WriteAsync(pongHeader, serverCts.Token);
            await server.FlushAsync(serverCts.Token);
        }, serverCts.Token);

        TcpListener chatListener = new(IPAddress.Loopback, 0);
        chatListener.Start();
        int ephemeralChatPort = ((IPEndPoint)chatListener.LocalEndpoint).Port;
        Task<TcpClient> acceptTask = chatListener.AcceptTcpClientAsync(serverCts.Token).AsTask();

        try
        {
            CockpitAppBridgeService bridge = new();
            IpcPipeHealthStatus nativeHealth = await bridge.ProbeNativeClientPipeAsync(
                pipeNameOverride: nativePipeName,
                timeoutMs: 1500,
                cancellationToken: serverCts.Token);
            await pipeServerTask;

            Assert.True(nativeHealth.IsConnected);
            Assert.Equal("#10B981", nativeHealth.StatusColor);
            Assert.Contains("Connected", nativeHealth.StatusMessage);

            using StubHealthHttpHandler healthHandler = new(HttpStatusCode.OK);
            CoreClusterReadinessStatus clusterHealth = await bridge.ProbeCoreClusterEndpointsAsync(
                masterPort: 55555,
                chatPort: ephemeralChatPort,
                httpHandlerOverride: healthHandler,
                cancellationToken: serverCts.Token);
            using TcpClient accepted = await acceptTask;

            Assert.True(clusterHealth.IsMasterHealthOk);
            Assert.Equal(200, clusterHealth.MasterStatusCode);
            Assert.True(clusterHealth.IsChatTcpReady);
            Assert.Equal("#10B981", clusterHealth.ChatStatusColor);
        }
        finally
        {
            chatListener.Stop();
        }
    }

    [Fact]
    public void ExportReadiness_SelectExportStagingRootAndStageParser_AutoSelects20GbDriveAndParsesDryRunOutput()
    {
        // protects: Pre-export dry-run drive auto-selection (when C: has < 20 GB free) and stage-by-stage pass/fail parsing.
        // fails_when: Export dry-run targets C:\KongorPlaytestStaging with < 20 GB free and fails Stage 1 disk budget, or misparses stage failures.
        // seam: CockpitAppBridgeService.SelectExportStagingRoot and ParseExportDryRunStages.

        CockpitAppBridgeService bridge = new();
        long gib = 1024L * 1024L * 1024L;

        // Case 1: C: has 11.9 GiB (< 20 GiB), D: has 70.3 GiB (>= 20 GiB) -> picks D:\KongorPlaytestStaging
        (string pickedRoot, double pickedFreeGb) = bridge.SelectExportStagingRoot(
            @"C:\Workspaces\ProjectKongor",
            drivesOverride:
            [
                (@"C:\", (long)(11.92 * gib)),
                (@"D:\", (long)(70.29 * gib))
            ]);
        Assert.Equal(Path.Combine(@"D:\", "KongorPlaytestStaging"), pickedRoot);
        Assert.True(pickedFreeGb >= 70.0);

        // Case 2: C: has 45 GiB (>= 20 GiB) -> prefers C:\KongorPlaytestStaging
        (string cPicked, _) = bridge.SelectExportStagingRoot(
            @"C:\Workspaces\ProjectKongor",
            drivesOverride:
            [
                (@"C:\", 45L * gib),
                (@"D:\", 70L * gib)
            ]);
        Assert.Equal(Path.Combine(@"C:\", "KongorPlaytestStaging"), cPicked);

        // Case 3: Stage-by-stage parser on canonical export_playtest_bundle.ps1 -DryRun output
        string samplePassOutput = """
            [STAGE 1/9] Pre-Flight, Disk Space & Source Workspace Isolation Guard
            Running Stage 1 PE Import/Export Dev-Profiler Leak Firewall across client & server binaries...
            Target drive 'D' free space: 70.29 GB (minimum required: 20 GB).
            [DRY-RUN] Verified P2P companion K2_SERVER binary: k2_x64.dll (3605504 bytes != client 19811840 bytes)
            [DRY-RUN] Verified DXVK 3.0.2 shader cache pair: hon_x64.dxvk-cache
            [DRY-RUN] Verified Linux client & server binaries, Native Guard, and 12 runtime libraries.
            [DRY-RUN] Verified macOS client binaries (HoN64 + 18 root libs + HoN).
            [DRY-RUN] Verified 24 allowlisted root K2 runtime files
            [DRY-RUN] Planned root launcher: D:\KongorPlaytestStaging\build\WILLOWMAKER.exe
            [DRY-RUN] Pre-flight succeeded with 0 filesystem writes. Ready for full export!
            """;

        IReadOnlyList<ExportDryRunStageItem> stages = CockpitAppBridgeService.ParseExportDryRunStages(
            samplePassOutput,
            exitCode: 0,
            selectedStagingRoot: pickedRoot,
            selectedFreeGb: pickedFreeGb);

        Assert.Equal(7, stages.Count);
        Assert.All(stages, s => Assert.True(s.Passed, $"Expected stage '{s.StageName}' to pass, got: {s.Detail}"));
    }

    [Fact]
    public async Task ProbeConnectedCockpitPipes_HidesOffline_DisambiguatesMcp_PassiveClient_AndGroupsDeduplicatedServers()
    {
        // protects: Connected-only IPC pipe visibility, R1 passive native client detection, R2 MCP vs WILLOWMAKER disambiguation,
        //           R6 pre-rebuild Docker fallback, and R10 per-port server pipe deduplication into one grouped Dashboard pill.
        // fails_when: Offline pipes render Standby badges, MCP ping-only server is mislabeled WILLOWMAKER, duplicate ports inflate server counts,
        //             or passive mode attempts to connect to kongor-native-client.
        // seam: CockpitAppBridgeService.ProbeConnectedCockpitPipesAsync.

        CockpitAppBridgeService bridge = new();
        string absentWmPipe = $"cp-wm-absent-{Guid.NewGuid():N}";
        string absentClientPipe = $"cp-tao-absent-{Guid.NewGuid():N}";

        // 1. All offline -> 0 badges returned (disconnected pipes strictly hidden)
        IReadOnlyList<CockpitPipeBadgeItem> emptyBadges = await bridge.ProbeConnectedCockpitPipesAsync(
            knownServerInstances: [],
            passiveNativeClientProbe: true,
            willowmakerPipeOverride: absentWmPipe,
            nativeClientPipeOverride: absentClientPipe,
            discoveredPipeNamesOverride: []);
        Assert.Empty(emptyBadges);

        // 2. Spin up a Ping-only pipe host (simulating kongor-cli McpIpcServer that rejects OpGetLauncherStatus / answers OpPing -> OpPong)
        string mcpPipeName = $"cp-mcp-pingonly-{Guid.NewGuid():N}";
        using CancellationTokenSource serverCts = new(TimeSpan.FromSeconds(5));
        Task mcpServerTask = Task.Run(async () =>
        {
            await using NamedPipeServerStream server = new(
                mcpPipeName,
                PipeDirection.InOut,
                1,
                PipeTransmissionMode.Byte,
                PipeOptions.Asynchronous);
            await server.WaitForConnectionAsync(serverCts.Token);

            // Frame 1: OpGetLauncherStatus (0x0034) -> reply with unsupported opcode (0xFFFF) so probe falls back to OpPing
            byte[] header1 = new byte[12];
            Assert.Equal(12, await server.ReadAsync(header1, serverCts.Token));
            uint req1 = BinaryPrimitives.ReadUInt32LittleEndian(header1.AsSpan(8, 4));
            byte[] unsupportedResp = new byte[12];
            BinaryPrimitives.WriteUInt16LittleEndian(unsupportedResp.AsSpan(0, 2), 0x574D);
            BinaryPrimitives.WriteUInt16LittleEndian(unsupportedResp.AsSpan(2, 2), 0xFFFF);
            BinaryPrimitives.WriteInt32LittleEndian(unsupportedResp.AsSpan(4, 4), 0);
            BinaryPrimitives.WriteUInt32LittleEndian(unsupportedResp.AsSpan(8, 4), req1);
            await server.WriteAsync(unsupportedResp, serverCts.Token);
            await server.FlushAsync(serverCts.Token);

            // Frame 2: OpPing (0x0001) -> OpPong (0x0002)
            byte[] header2 = new byte[12];
            Assert.Equal(12, await server.ReadAsync(header2, serverCts.Token));
            ushort op2 = BinaryPrimitives.ReadUInt16LittleEndian(header2.AsSpan(2, 2));
            uint req2 = BinaryPrimitives.ReadUInt32LittleEndian(header2.AsSpan(8, 4));
            Assert.Equal((ushort)0x0001, op2);

            byte[] pongResp = new byte[12];
            BinaryPrimitives.WriteUInt16LittleEndian(pongResp.AsSpan(0, 2), 0x574D);
            BinaryPrimitives.WriteUInt16LittleEndian(pongResp.AsSpan(2, 2), 0x0002);
            BinaryPrimitives.WriteInt32LittleEndian(pongResp.AsSpan(4, 4), 0);
            BinaryPrimitives.WriteUInt32LittleEndian(pongResp.AsSpan(8, 4), req2);
            await server.WriteAsync(pongResp, serverCts.Token);
            await server.FlushAsync(serverCts.Token);
        }, serverCts.Token);

        List<CockpitServerPipeHint> serverHints =
        [
            new("#L1", "LOCAL ARENA", 11235, 11235, IsRunning: true, IsContainer: false, ReportedIpcConnected: true, ReportedIpcPath: @"\\.\pipe\hon-server-native-11235", ReportedIpcTransport: "NamedPipe"),
            new("#D1", "DONUT ARENA", 11335, 11335, IsRunning: true, IsContainer: true, ReportedIpcConnected: true, ReportedIpcPath: "/tmp/hon-server-native-11335.sock", ReportedIpcTransport: "UnixSocket"),
            new("#Q1", "QUEUE ARCADE", 11485, 11485, IsRunning: true, IsContainer: true, ReportedIpcConnected: null, ReportedIpcPath: null, ReportedIpcTransport: null),
            new("#Q2", "STOPPED ARCADE", 11486, 11486, IsRunning: false, IsContainer: true, ReportedIpcConnected: false, ReportedIpcPath: null, ReportedIpcTransport: null)
        ];

        // Passive list includes duplicate hon-server-11235 + hon-server-native-11235 (R10), native client pipe (R1), and replay pipe
        IReadOnlyList<CockpitPipeBadgeItem> badges = await bridge.ProbeConnectedCockpitPipesAsync(
            knownServerInstances: serverHints,
            passiveNativeClientProbe: true,
            willowmakerPipeOverride: mcpPipeName,
            nativeClientPipeOverride: absentClientPipe,
            discoveredPipeNamesOverride:
            [
                absentClientPipe,
                "hon-server-11235",
                "hon-server-native-11235",
                "hon_frame_pipe_job42"
            ],
            cancellationToken: serverCts.Token);
        await mcpServerTask;

        // R2: Ping-only host labeled MCP IPC BRIDGE (not WILLOWMAKER)
        CockpitPipeBadgeItem mcpBadge = Assert.Single(badges, b => b.Key == "mcp-ipc-bridge");
        Assert.Contains("MCP IPC BRIDGE", mcpBadge.Label);
        Assert.True(mcpBadge.IsConnected);

        // R1: Passive native client badge appears without connecting
        CockpitPipeBadgeItem clientBadge = Assert.Single(badges, b => b.Key == "game-client-tao");
        Assert.Contains("GAME CLIENT IPC", clientBadge.Label);
        Assert.Contains("Passive OS pipe detection", clientBadge.ToolTipText);

        // Grouped server pill: 3 unique connected ports (#L1 deduplicated, #D1 UnixSocket, #Q1 R6 fallback; #Q2 stopped excluded)
        CockpitPipeBadgeItem serverBadge = Assert.Single(badges, b => b.Key == "server-pipes");
        Assert.Contains("3 Connected", serverBadge.StatusMessage);
        Assert.Contains("#L1", serverBadge.StatusMessage);
        Assert.Contains("#D1", serverBadge.StatusMessage);
        Assert.Contains("#Q1", serverBadge.StatusMessage);
        Assert.DoesNotContain("#Q2", serverBadge.StatusMessage);
        Assert.Contains("hon-server-native-11235", serverBadge.ToolTipText);
        Assert.Contains("/tmp/hon-server-native-11335.sock", serverBadge.ToolTipText);

        // Replay renderer pipe detected
        CockpitPipeBadgeItem replayBadge = Assert.Single(badges, b => b.Key == "replay-renderer-ipc");
        Assert.Contains("1 stream", replayBadge.StatusMessage);
    }

    [Fact]
    public async Task PopOutChat_PreservesLiveStreamOnFleetSwitch_EnforcesSingleChatSurface_AndDocksCleanly()
    {
        // protects: R3 (SwitchToFleetMode must not kill SSE stream while popped out), R9 (only one chat control visible at a time),
        //           and Dashboard -> GameServers pop-out wiring replacing external COMPEL.GUI launch.
        // fails_when: Switching main window back to Fleet Overview stops popped-out chat stream, or both docked & popped-out controls render simultaneously.
        // seam: GameServersViewModel.PopOutChat / DockChat / SwitchToFleetMode / NotifyPopOutWindowClosed + DashboardViewModel.OpenCompelGuiAsync.

        Mock<IGameServerFleetService> fleetMock = new();
        fleetMock.Setup(f => f.GetDiscoveredHostsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync([]);
        fleetMock.Setup(f => f.IsEndpointKnownOffline(It.IsAny<string>(), It.IsAny<int>()))
            .Returns(false);

        using GameServersViewModel gsVm = new(fleetMock.Object);
        int popOutCalls = 0;
        int dockCalls = 0;
        gsVm.ConfigureChatPopOutHost(
            onRequestPopOutChat: () => popOutCalls++,
            onRequestDockChat: () => dockCalls++);

        // Switch to docked chat mode
        gsVm.SwitchToChatMode();
        Assert.True(gsVm.IsChatModeActive);
        Assert.False(gsVm.IsChatPoppedOut);
        Assert.True(gsVm.IsDockedChatVisible);
        Assert.False(gsVm.IsPopOutPlaceholderVisible);
        Assert.True(gsVm.IsChatSurfaceActive);

        // Pop out chat window -> docked copy hides (R9), placeholder shows, stream stays active
        gsVm.PopOutChat();
        Assert.Equal(1, popOutCalls);
        Assert.True(gsVm.IsChatPoppedOut);
        Assert.False(gsVm.IsDockedChatVisible);
        Assert.True(gsVm.IsPopOutPlaceholderVisible);
        Assert.True(gsVm.IsChatSurfaceActive);

        // R3: Switching main NEXUS view back to Fleet Overview keeps IsChatSurfaceActive == true
        gsVm.SwitchToFleetMode();
        Assert.False(gsVm.IsChatModeActive);
        Assert.True(gsVm.IsChatPoppedOut);
        Assert.True(gsVm.IsChatSurfaceActive);
        Assert.False(gsVm.IsDockedChatVisible);

        // Dashboard Server Chat button invokes pop-out host (not external COMPEL.GUI process)
        Mock<INexusOrchestratorService> orchestratorMock = new();
        orchestratorMock.SetupGet(o => o.CurrentClusterState).Returns(ServerState.Stopped);
        orchestratorMock.Setup(o => o.GetQueueArcadeStateAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync((true, false, "Enabled"));
        Mock<IDockerHealthService> dockerMock = new();
        dockerMock.Setup(d => d.CheckDockerStatusAsync())
            .ReturnsAsync(new DockerStatus { IsRunning = false, StatusMessage = "Offline" });
        Mock<IDatabaseHealthService> dbMock = new();
        dbMock.Setup(d => d.CheckDatabaseHealthAsync(It.IsAny<ISecretsManagerService?>()))
            .ReturnsAsync(new DatabaseStatus { IsConnected = false, StatusMessage = "Offline" });
        dbMock.Setup(d => d.CheckRedisHealthAsync())
            .ReturnsAsync(new DatabaseStatus { IsConnected = false, StatusMessage = "Offline" });
        dbMock.Setup(d => d.CheckPyroscopeHealthAsync())
            .ReturnsAsync(new DatabaseStatus { IsConnected = false, StatusMessage = "Offline" });

        using DashboardViewModel dashVm = new(
            orchestratorMock.Object,
            dockerMock.Object,
            dbMock.Object,
            onOpenServerChatPopOut: () => gsVm.PopOutChat(),
            getServerPipeHints: () => gsVm.GetServerPipeHints());

        await dashVm.OpenCompelGuiAsync();
        Assert.Equal(2, popOutCalls);

        // Dock back restores single docked control
        gsVm.DockChat();
        Assert.Equal(1, dockCalls);
        Assert.False(gsVm.IsChatPoppedOut);
        Assert.True(gsVm.IsChatModeActive);
        Assert.True(gsVm.IsDockedChatVisible);
        Assert.False(gsVm.IsPopOutPlaceholderVisible);

        // Closing OS window notifies VM cleanly
        gsVm.PopOutChat();
        Assert.True(gsVm.IsChatPoppedOut);
        gsVm.NotifyPopOutWindowClosed();
        Assert.False(gsVm.IsChatPoppedOut);
    }

    [Fact]
    public void DiagnosticsAndInstancePipeState_HidesOfflineByDefault_AndSupportsZeroRebuildFallback()
    {
        // protects: Diagnostics connected-only default visibility toggle and GameServerInstanceDetailViewModel additive pipe state (R5/R6).
        // fails_when: Disconnected pipes clutter Diagnostics by default, or cached Docker instances with null IpcConnected fail to render badge.
        // seam: DiagnosticsViewModel.ShowDisconnectedPipes + GameServerInstanceDetailViewModel.UpdateIpcPipeState.

        Mock<IPortScannerService> portMock = new();
        portMock.Setup(p => p.ScanRequiredPortsAsync()).ReturnsAsync([]);
        Mock<IDockerHealthService> dockerMock = new();
        dockerMock.Setup(d => d.CheckDockerStatusAsync()).ReturnsAsync(new DockerStatus { IsRunning = false, StatusMessage = "Offline" });
        Mock<ICockpitAppBridgeService> bridgeMock = new();
        bridgeMock.Setup(b => b.ProbeNativeClientPipeAsync(It.IsAny<string?>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new IpcPipeHealthStatus("TAO", false, @"\\.\pipe\kongor-native-client", 0, "Standby", "#94A3B8"));
        bridgeMock.Setup(b => b.ProbeWillowmakerIpcAsync(It.IsAny<string?>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new WillowmakerCockpitStatus(false, 0, "", "", false, 0, @"\\.\pipe\willowmaker-ipc", "Standby", "#94A3B8", "None"));
        bridgeMock.Setup(b => b.ProbeCoreClusterEndpointsAsync(It.IsAny<int>(), It.IsAny<int>(), It.IsAny<HttpMessageHandler?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new CoreClusterReadinessStatus(false, 0, 0, "Offline", "#EF4444", false, 0, "Offline", "#EF4444"));
        bridgeMock.Setup(b => b.ProbeConnectedCockpitPipesAsync(
                It.IsAny<IReadOnlyList<CockpitServerPipeHint>?>(),
                It.IsAny<bool>(),
                It.IsAny<string?>(),
                It.IsAny<string?>(),
                It.IsAny<IReadOnlyList<string>?>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync([]);

        DiagnosticsViewModel diagVm = new(portMock.Object, dockerMock.Object, cockpitBridge: bridgeMock.Object);
        Assert.False(diagVm.ShowDisconnectedPipes);
        Assert.False(diagVm.ShowNativeClientPipeCard);
        Assert.False(diagVm.ShowWillowmakerPipeCard);
        Assert.False(diagVm.ShowServerPipesCard);
        Assert.True(diagVm.HasNoVisibleIpcPipesMessage);

        diagVm.ShowDisconnectedPipes = true;
        Assert.True(diagVm.ShowNativeClientPipeCard);
        Assert.True(diagVm.ShowWillowmakerPipeCard);
        Assert.True(diagVm.ShowServerPipesCard);
        Assert.False(diagVm.HasNoVisibleIpcPipesMessage);

        // Per-server Chat Manager badge states
        GameServerInstanceDetailViewModel inst = new()
        {
            GamePort = 11335,
            WorkerPort = 11335,
            IsContainer = true,
            IsRunning = true
        };
        inst.UpdateIpcPipeState(null, null, null); // R6 fallback
        Assert.True(inst.IsPipeConnected);
        Assert.Contains("Container IPC", inst.ConnectedPipeBadgeText);

        inst.UpdateIpcPipeState(true, "/tmp/hon-server-native-11335.sock", "UnixSocket");
        Assert.True(inst.IsPipeConnected);
        Assert.Contains("UnixSocket", inst.ConnectedPipeBadgeText);
        Assert.Contains("/tmp/hon-server-native-11335.sock", inst.ConnectedPipeToolTip);

        inst.UpdateIpcPipeState(false, null, null);
        Assert.False(inst.IsPipeConnected);
        Assert.Equal(string.Empty, inst.ConnectedPipeBadgeText);
    }

    private sealed class StubHealthHttpHandler(HttpStatusCode statusCode) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            return Task.FromResult(new HttpResponseMessage(statusCode)
            {
                Content = new StringContent("Healthy")
            });
        }
    }
}
