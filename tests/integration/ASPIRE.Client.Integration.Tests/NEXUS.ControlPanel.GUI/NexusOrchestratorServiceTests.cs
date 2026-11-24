// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

using System.Diagnostics;
using NEXUS.ControlPanel.GUI.Services;
using NEXUS.ControlPanel.GUI.Services.Models;

namespace ASPIRE.Tests.NEXUS.ControlPanel.GUI;

public sealed class NexusOrchestratorServiceTests : IDisposable
{
    private readonly string _tempTestDir;

    public NexusOrchestratorServiceTests()
    {
        _tempTestDir = Path.Combine(Path.GetTempPath(), "NexusFastLaunchTests_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempTestDir);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_tempTestDir)) Directory.Delete(_tempTestDir, true);
        }
        catch
        {
            // Ignore cleanup failures in test teardown
        }
    }

    [Fact]
    public void CreateFastProcessStartInfo_ResolvesCompiledExecutableDirectly_WhenExeExists()
    {
        // Arrange
        string projectDir = Path.Combine(_tempTestDir, "ASPIRE.ApplicationHost");
        string binDir = Path.Combine(projectDir, "bin", "Debug", "net10.0");
        Directory.CreateDirectory(binDir);
        string exePath = Path.Combine(binDir, "ASPIRE.ApplicationHost.exe");
        File.WriteAllText(exePath, string.Empty);
        File.WriteAllText(Path.Combine(binDir, "ASPIRE.ApplicationHost.runtimeconfig.json"), "{}");

        // Act
        ProcessStartInfo psi = NexusOrchestratorService.CreateFastProcessStartInfo(
            _tempTestDir,
            "ASPIRE.ApplicationHost",
            "ASPIRE.ApplicationHost Development",
            "--launch-game-servers");

        // Assert
        Assert.Equal(exePath, psi.FileName);
        Assert.Equal("--launch-game-servers", psi.Arguments);
        Assert.Equal(projectDir, psi.WorkingDirectory);
        Assert.False(psi.UseShellExecute);
        Assert.True(psi.CreateNoWindow);
        Assert.Equal("Development", psi.EnvironmentVariables["ASPNETCORE_ENVIRONMENT"]);
        Assert.Equal("https://localhost:5550", psi.EnvironmentVariables["ASPNETCORE_URLS"]);
        Assert.Equal("http://localhost:5551", psi.EnvironmentVariables["DOTNET_DASHBOARD_OTLP_ENDPOINT_URL"]);
        Assert.Equal("http://localhost:5552", psi.EnvironmentVariables["DOTNET_RESOURCE_SERVICE_ENDPOINT_URL"]);
        Assert.Equal("true", psi.EnvironmentVariables["DOTNET_DASHBOARD_UNSECURED_ALLOW_ANONYMOUS"]);
    }

    [Fact]
    public void CreateFastProcessStartInfo_ResolvesCompiledDllDirectly_WhenDllExists()
    {
        // Arrange
        string projectDir = Path.Combine(_tempTestDir, "KONGOR.MasterServer");
        string binDir = Path.Combine(projectDir, "bin", "Debug", "net10.0");
        Directory.CreateDirectory(binDir);
        string dllPath = Path.Combine(binDir, "KONGOR.MasterServer.dll");
        File.WriteAllText(dllPath, string.Empty);
        File.WriteAllText(Path.Combine(binDir, "KONGOR.MasterServer.runtimeconfig.json"), "{}");

        // Act
        ProcessStartInfo psi = NexusOrchestratorService.CreateFastProcessStartInfo(
            _tempTestDir,
            "KONGOR.MasterServer",
            "KONGOR.MasterServer Development");

        // Assert
        Assert.Equal("dotnet", psi.FileName);
        Assert.Equal($"\"{dllPath}\"", psi.Arguments);
        Assert.Equal(projectDir, psi.WorkingDirectory);
        Assert.Equal("Development", psi.EnvironmentVariables["ASPNETCORE_ENVIRONMENT"]);
    }

    [Fact]
    public void NexusOrchestratorService_CreateFastProcessStartInfo_SetsPortalUiReverseProxyEnvironmentVariables()
    {
        // Arrange
        string projectDir = Path.Combine(_tempTestDir, "DAWNBRINGER.WebPortal.UI");
        string binDir = Path.Combine(projectDir, "bin", "Debug", "net10.0");
        Directory.CreateDirectory(binDir);
        string exePath = Path.Combine(binDir, "DAWNBRINGER.WebPortal.UI.exe");
        File.WriteAllText(exePath, string.Empty);
        File.WriteAllText(Path.Combine(binDir, "DAWNBRINGER.WebPortal.UI.runtimeconfig.json"), "{}");

        // Act
        ProcessStartInfo psi = NexusOrchestratorService.CreateFastProcessStartInfo(
            _tempTestDir,
            "DAWNBRINGER.WebPortal.UI",
            "DAWNBRINGER.WebPortal.UI Development");

        // Assert
        Assert.Equal(exePath, psi.FileName);
        Assert.Equal("http://localhost:5200", psi.EnvironmentVariables["services__web-portal-api__http__0"]);
        Assert.Equal("https://localhost:5201", psi.EnvironmentVariables["services__web-portal-api__https__0"]);
    }

    [Fact]
    public void CreateFastProcessStartInfo_FallsBackToDotnetRun_WhenNoBinariesFound()
    {
        // Arrange
        string projectDir = Path.Combine(_tempTestDir, "KONGOR.MasterServer");
        Directory.CreateDirectory(projectDir);
        string csprojPath = Path.Combine(projectDir, "KONGOR.MasterServer.csproj");
        File.WriteAllText(csprojPath, "<Project></Project>");

        // Act
        ProcessStartInfo psi = NexusOrchestratorService.CreateFastProcessStartInfo(
            _tempTestDir,
            "KONGOR.MasterServer",
            "KONGOR.MasterServer Development");

        // Assert
        Assert.Equal("dotnet", psi.FileName);
        Assert.Contains("run", psi.Arguments);
        Assert.Contains("KONGOR.MasterServer.csproj", psi.Arguments);
        Assert.Equal(_tempTestDir, psi.WorkingDirectory);
    }

    [Fact]
    public void CreateFastProcessStartInfo_FallsBackToDotnetRun_WithExtraArgsProperlySeparated()
    {
        // Arrange
        string projectDir = Path.Combine(_tempTestDir, "ASPIRE.ApplicationHost");
        Directory.CreateDirectory(projectDir);
        string csprojPath = Path.Combine(projectDir, "ASPIRE.ApplicationHost.csproj");
        File.WriteAllText(csprojPath, "<Project></Project>");

        // Act
        ProcessStartInfo psi = NexusOrchestratorService.CreateFastProcessStartInfo(
            _tempTestDir,
            "ASPIRE.ApplicationHost",
            "ASPIRE.ApplicationHost Development",
            "--no-containers");

        // Assert
        Assert.Equal("dotnet", psi.FileName);
        Assert.Contains("--launch-profile \"ASPIRE.ApplicationHost Development\"", psi.Arguments);
        Assert.Contains("-- --no-containers", psi.Arguments);
        Assert.DoesNotContain("Development--no-containers", psi.Arguments);
    }

    [Fact]
    public void NexusOrchestratorService_WhenExecuted_InitializesInStoppedState()
    {
        // Arrange & Act
        using NexusOrchestratorService orchestrator = new();

        // Assert
        Assert.Equal(ServerState.Stopped, orchestrator.CurrentClusterState);
        Assert.Equal(ServerState.Stopped, orchestrator.GetServiceState("master-server"));
        Assert.Equal(ServerState.Stopped, orchestrator.GetServiceState("chat-server"));
        Assert.Equal(ServerState.Stopped, orchestrator.GetServiceState("web-portal-api"));
        Assert.Equal(ServerState.Stopped, orchestrator.GetServiceState("web-portal-ui"));
    }

    [Fact]
    public void NexusOrchestratorService_SetAllMicroservicesState_UpdatesAllFourServices()
    {
        // Arrange
        using NexusOrchestratorService orchestrator = new();

        // Act
        orchestrator.SetAllMicroservicesState(ServerState.Running);

        // Assert
        Assert.Equal(ServerState.Running, orchestrator.GetServiceState("master-server"));
        Assert.Equal(ServerState.Running, orchestrator.GetServiceState("chat-server"));
        Assert.Equal(ServerState.Running, orchestrator.GetServiceState("web-portal-api"));
        Assert.Equal(ServerState.Running, orchestrator.GetServiceState("web-portal-ui"));
    }

    [Trait("Category", "Manual")]
    [Fact]
    public void NexusOrchestratorService_AppendLog_MaintainsBoundedHistoryAndNotifiesListeners()
    {
        if (!NexusTestEnvironment.IsManualTestEnabled) return;
        // Arrange
        using NexusOrchestratorService orchestrator = new();
        List<LogMessage> receivedLogs = [];
        orchestrator.OnLogReceived += receivedLogs.Add;

        LogMessage testLog = new()
        {
            ServiceName = "TEST",
            Message = "Fast launch verification test log",
            Level = "INFO",
            IsError = false
        };

        // Act
        orchestrator.AppendLog(testLog);

        // Assert
        Assert.Single(receivedLogs);
        Assert.Equal("Fast launch verification test log", receivedLogs[0].Message);
        Assert.Contains(orchestrator.LogHistory, l => l.Message == "Fast launch verification test log");
    }

    [Fact]
    public async Task NexusOrchestratorService_StopAllAsync_CompletesGracefullyAndLeavesServiceStopped()
    {
        // Arrange
        using NexusOrchestratorService orchestrator = new();

        // Act
        await orchestrator.StopAllAsync();

        // Assert
        Assert.Equal(ServerState.Stopped, orchestrator.CurrentClusterState);
        Assert.Equal(ServerState.Stopped, orchestrator.GetServiceState("master-server"));
        Assert.Equal(ServerState.Stopped, orchestrator.GetServiceState("chat-server"));
        Assert.Equal(ServerState.Stopped, orchestrator.GetServiceState("web-portal-api"));
        Assert.Equal(ServerState.Stopped, orchestrator.GetServiceState("web-portal-ui"));
    }

    [Fact]
    public void EvaluateClusterState_WhenStartingAndProcessAliveButPortsNotReady_RetainsStartingState()
    {
        // Act: While the host process is running but ports haven't opened yet, state must stay Starting (not revert to Stopped)
        ServerState state = NexusOrchestratorService.EvaluateClusterState(
            currentState: ServerState.Starting,
            isClusterSufficientlyRunning: false,
            isAnyServiceRunning: false,
            isClusterProcAlive: true);

        // Assert
        Assert.Equal(ServerState.Starting, state);
    }

    [Fact]
    public void EvaluateClusterState_WhenStartingAndClusterSufficientlyRunning_TransitionsToRunning()
    {
        // Act: When ports become available, state transitions to Running
        ServerState state = NexusOrchestratorService.EvaluateClusterState(
            currentState: ServerState.Starting,
            isClusterSufficientlyRunning: true,
            isAnyServiceRunning: true,
            isClusterProcAlive: true);

        // Assert
        Assert.Equal(ServerState.Running, state);
    }

    [Fact]
    public void EvaluateClusterState_WhenStartingAndProcessExited_TransitionsToStopped()
    {
        // Act: If host process crashes or exits during startup, state transitions to Stopped
        ServerState state = NexusOrchestratorService.EvaluateClusterState(
            currentState: ServerState.Starting,
            isClusterSufficientlyRunning: false,
            isAnyServiceRunning: false,
            isClusterProcAlive: false);

        // Assert
        Assert.Equal(ServerState.Stopped, state);
    }

    [Fact]
    public void EvaluateClusterState_WhenRunningAndServicesAndProcessDead_TransitionsToStopped()
    {
        // Act: When all services and host process exit, state transitions to Stopped
        ServerState state = NexusOrchestratorService.EvaluateClusterState(
            currentState: ServerState.Running,
            isClusterSufficientlyRunning: false,
            isAnyServiceRunning: false,
            isClusterProcAlive: false);

        // Assert
        Assert.Equal(ServerState.Stopped, state);
    }

    [Fact]
    public void EvaluateClusterState_WhenRunningAndClusterProcessAlive_RetainsRunningState()
    {
        // Act: When cluster process is alive and services are responding, state remains Running
        ServerState state = NexusOrchestratorService.EvaluateClusterState(
            currentState: ServerState.Running,
            isClusterSufficientlyRunning: false,
            isAnyServiceRunning: true,
            isClusterProcAlive: true);

        // Assert
        Assert.Equal(ServerState.Running, state);
    }

    [Fact]
    public void CreateFastProcessStartInfo_AspireHost_PassesNoContainers_WhenRequested()
    {
        // Arrange
        string projectDir = Path.Combine(_tempTestDir, "ASPIRE.ApplicationHost");
        string binDir = Path.Combine(projectDir, "bin", "Debug", "net10.0");
        Directory.CreateDirectory(binDir);
        string exePath = Path.Combine(binDir, "ASPIRE.ApplicationHost.exe");
        File.WriteAllText(exePath, string.Empty);
        File.WriteAllText(Path.Combine(binDir, "ASPIRE.ApplicationHost.runtimeconfig.json"), "{}");

        // Act
        ProcessStartInfo psi = NexusOrchestratorService.CreateFastProcessStartInfo(
            _tempTestDir,
            "ASPIRE.ApplicationHost",
            "ASPIRE.ApplicationHost Development",
            "--no-containers");

        // Assert
        Assert.Equal(exePath, psi.FileName);
        Assert.Equal("--no-containers", psi.Arguments);
    }

    [Fact]
    public async Task LaunchGameServerAsync_InTestEnvironment_SuppressesProcessSpawnGracefully()
    {
        // Arrange
        using NexusOrchestratorService orchestrator = new();

        // Act & Assert (Should not throw and should suppress external process spawn)
        await orchestrator.LaunchGameServerAsync();
    }

    [Fact]
    public void BuildLocalCompelProcessStartInfo_QueueArcadeAndRanked_ScopesEnvToChildWithoutParentBleed()
    {
        // protects: Child-scoped ProcessStartInfo.Environment isolation so launching Queue Arcade NEVER mutates parent
        //           Control Panel Environment variables or contaminates a subsequent Ranked COMPEL launch.
        // fails_when: Environment.SetEnvironmentVariable bleeds COMPEL_QUEUE_ARCADE_MODE=true into Ranked launches.
        // seam: Real NexusOrchestratorService.BuildLocalCompelProcessStartInfo for Queue Arcade followed by Ranked.
        string? priorParentArcadeEnv = Environment.GetEnvironmentVariable("COMPEL_QUEUE_ARCADE_MODE");
        try
        {
            Environment.SetEnvironmentVariable("COMPEL_QUEUE_ARCADE_MODE", null);
            NexusSecrets secrets = new()
            {
                HostAccountUserName = "guccihost",
                HostAccountPassword = Guid.NewGuid().ToString("N"),
                CompelAuthenticationToken = Guid.NewGuid().ToString("N"),
                CompelServerNamePrefix = "DONUT ARENA",
                CompelLocation = "EU",
                QueueArcadeInstances = 2
            };

            string arcadeHost = ContainerLaunchCommandBuilder.ResolveQueueArcadeHostUsername(secrets.HostAccountUserName);
            ProcessStartInfo arcadePsi = NexusOrchestratorService.BuildLocalCompelProcessStartInfo(
                exeToRun: "COMPEL.exe",
                workingDir: _tempTestDir,
                secrets: secrets,
                portRangeOffset: 250,
                controlPlanePort: 11484,
                instances: 2,
                gateway: "127.0.0.1",
                masterServerEndpoint: "127.0.0.1:55555",
                queueArcadeMode: true,
                resolvedHostUsername: arcadeHost);

            Assert.Null(Environment.GetEnvironmentVariable("COMPEL_QUEUE_ARCADE_MODE"));
            Assert.False(arcadePsi.UseShellExecute);
            Assert.Equal("true", arcadePsi.Environment["COMPEL_QUEUE_ARCADE_MODE"]);
            Assert.Equal("1", arcadePsi.Environment["KONGOR_QUEUE_ARCADE"]);
            Assert.Equal("Q", arcadePsi.Environment["COMPEL_FLEET_TIER"]);
            Assert.Equal("250", arcadePsi.Environment["COMPEL_PORT_RANGE_OFFSET"]);
            Assert.Equal("11484", arcadePsi.Environment["COMPEL_CONTROL_PLANE_PORT"]);
            Assert.Equal("1", arcadePsi.Environment["COMPEL_WARM_INSTANCES_TARGET"]);
            Assert.Equal("guccihost_arc", arcadePsi.Environment["HOST_USERNAME"]);
            Assert.Equal("EU", arcadePsi.Environment["COMPEL_LOCATION"]);
            Assert.Equal("QUEUE ARCADE DONUT ARENA", arcadePsi.Environment["COMPEL_SERVER_NAME_PREFIX"]);
            Assert.Contains("--Compel:QueueArcadeMode=true", arcadePsi.Arguments);
            Assert.Contains("--Compel:WarmInstancesTarget=1", arcadePsi.Arguments);
            Assert.Contains("--Compel:Location=\"EU\"", arcadePsi.Arguments);

            ProcessStartInfo rankedPsi = NexusOrchestratorService.BuildLocalCompelProcessStartInfo(
                exeToRun: "COMPEL.exe",
                workingDir: _tempTestDir,
                secrets: secrets,
                portRangeOffset: 0,
                controlPlanePort: 11234,
                instances: 2,
                gateway: "127.0.0.1",
                masterServerEndpoint: "127.0.0.1:55555",
                queueArcadeMode: false,
                resolvedHostUsername: secrets.HostAccountUserName);

            Assert.Equal("false", rankedPsi.Environment["COMPEL_QUEUE_ARCADE_MODE"]);
            Assert.Equal("0", rankedPsi.Environment["KONGOR_QUEUE_ARCADE"]);
            Assert.Equal("L", rankedPsi.Environment["COMPEL_FLEET_TIER"]);
            Assert.Equal("0", rankedPsi.Environment["COMPEL_PORT_RANGE_OFFSET"]);
            Assert.Equal("11234", rankedPsi.Environment["COMPEL_CONTROL_PLANE_PORT"]);
            Assert.Equal("guccihost", rankedPsi.Environment["HOST_USERNAME"]);
            Assert.Equal("EU", rankedPsi.Environment["COMPEL_LOCATION"]);
            Assert.Contains("--Compel:QueueArcadeMode=false", rankedPsi.Arguments);
            Assert.Contains("--Compel:Location=\"EU\"", rankedPsi.Arguments);
        }
        finally
        {
            Environment.SetEnvironmentVariable("COMPEL_QUEUE_ARCADE_MODE", priorParentArcadeEnv);
        }
    }

    [Fact]
    public void CollectQueueArcadeTargetPids_StopsOnlyArcadePortsAndSessionPids_NeverTouchesRankedServers()
    {
        // protects: StopQueueArcadeServersAsync surgical PID targeting so stopping Queue Arcade never kills live Ranked COMPEL or hon_x64.
        // fails_when: Ranked session PIDs ("GAME SERVER") or Ranked UDP ports (11234/11235) are included in Queue Arcade stop targets.
        // seam: Real NexusOrchestratorService.CollectQueueArcadeTargetPids against mixed Ranked + Arcade session & socket tables.
        using NexusOrchestratorService orchestrator = new();
        orchestrator.TrackSessionPidForTest(5100, "GAME SERVER");
        orchestrator.TrackSessionPidForTest(6200, "QUEUE ARCADE");

        Dictionary<int, int> udpOwners = new()
        {
            [11234] = 5100, // Ranked COMPEL ping
            [11235] = 5101, // Ranked #L1 hon_x64
            [11335] = 5201, // Docker Ranked #D1
            [11485] = 6201, // Queue Arcade #Q1 hon_x64
            [11486] = 6202, // Queue Arcade #Q2 hon_x64
            [11603] = 6200  // Queue Arcade Voice Relay
        };

        HashSet<int> targets = orchestrator.CollectQueueArcadeTargetPids(tcpControlOwnerPid: 6200, udpOwners);

        Assert.Contains(6200, targets);
        Assert.Contains(6201, targets);
        Assert.Contains(6202, targets);
        Assert.DoesNotContain(5100, targets);
        Assert.DoesNotContain(5101, targets);
        Assert.DoesNotContain(5201, targets);
        Assert.DoesNotContain(Environment.ProcessId, targets);
    }

    [Fact]
    public void CreateFastProcessStartInfo_SelectsNewerDebugBinary_OverStaleReleaseBinary_AndPrefersReleaseOnTie()
    {
        // protects: Control Panel service launcher never silently picks an older stale bin/Release DLL over a freshly built bin/Debug DLL.
        // fails_when: FindBestBinary returns the first existing config ("Release") without comparing File.GetLastWriteTimeUtc.
        // seam: Real NexusOrchestratorService.CreateFastProcessStartInfo filesystem timestamp inspection on temp bin/Release & bin/Debug trees.
        string projectDir = Path.Combine(_tempTestDir, "TRANSMUTANSTEIN.ChatServer");
        string releaseDir = Path.Combine(projectDir, "bin", "Release", "net11.0");
        string debugDir = Path.Combine(projectDir, "bin", "Debug", "net11.0");
        Directory.CreateDirectory(releaseDir);
        Directory.CreateDirectory(debugDir);

        string releaseExe = Path.Combine(releaseDir, "TRANSMUTANSTEIN.ChatServer.exe");
        string releaseDll = Path.Combine(releaseDir, "TRANSMUTANSTEIN.ChatServer.dll");
        string debugExe = Path.Combine(debugDir, "TRANSMUTANSTEIN.ChatServer.exe");
        string debugDll = Path.Combine(debugDir, "TRANSMUTANSTEIN.ChatServer.dll");

        File.WriteAllText(releaseExe, string.Empty);
        File.WriteAllText(releaseDll, string.Empty);
        File.WriteAllText(Path.Combine(releaseDir, "TRANSMUTANSTEIN.ChatServer.runtimeconfig.json"), "{}");

        File.WriteAllText(debugExe, string.Empty);
        File.WriteAllText(debugDll, string.Empty);
        File.WriteAllText(Path.Combine(debugDir, "TRANSMUTANSTEIN.ChatServer.runtimeconfig.json"), "{}");

        DateTime staleTimeUtc = new(2026, 11, 7, 22, 35, 25, DateTimeKind.Utc);
        DateTime freshTimeUtc = new(2026, 11, 8, 16, 40, 00, DateTimeKind.Utc);

        File.SetLastWriteTimeUtc(releaseExe, staleTimeUtc);
        File.SetLastWriteTimeUtc(releaseDll, staleTimeUtc);
        File.SetLastWriteTimeUtc(debugExe, freshTimeUtc);
        File.SetLastWriteTimeUtc(debugDll, freshTimeUtc);

        ProcessStartInfo psiNewerDebug = NexusOrchestratorService.CreateFastProcessStartInfo(
            _tempTestDir,
            "TRANSMUTANSTEIN.ChatServer",
            "TRANSMUTANSTEIN.ChatServer Development");
        Assert.Equal(debugExe, psiNewerDebug.FileName);

        // When Release and Debug share identical timestamps, Release wins via priority tie-breaker
        File.SetLastWriteTimeUtc(releaseExe, freshTimeUtc);
        File.SetLastWriteTimeUtc(releaseDll, freshTimeUtc);

        ProcessStartInfo psiTie = NexusOrchestratorService.CreateFastProcessStartInfo(
            _tempTestDir,
            "TRANSMUTANSTEIN.ChatServer",
            "TRANSMUTANSTEIN.ChatServer Development");
        Assert.Equal(releaseExe, psiTie.FileName);
    }

    [Fact]
    public void ParseLogLevel_HonorsSerilogStructuredLevelTokens_AndDowngradesDedicatedServerStdoutErrorsToWarn()
    {
        // protects: Control Panel log viewer against false-positive red [ERROR] floods when COMPEL emits Serilog [INF]
        //           lines wrapping non-fatal K2 engine stdout asset warnings ("Error: CFileManager::GetFile"), while
        //           preserving real [ERR] and Fatal classifications.
        // fails_when: ParseLogLevel matches raw "error:" before checking Serilog's [INF]/[ERR]/[FTL] structured tag.
        // seam: Real NexusOrchestratorService.ParseLogLevel on exact COMPEL DedicatedServer & Lakehouse log lines.
        string k2MissingClipLine =
            "2026-11-09 14:02:56.647 -06:00 [INF] [DedicatedServer:11486] [Nov 9 14:02:56] Error: [14:02:56] CFileManager::GetFile() [/buildings/legion/attack_tower/effects/explosion_50.effect] - Invalid file type";
        string k2FatalLine =
            "2026-11-09 14:02:56.900 -06:00 [INF] [DedicatedServer:11486] [Nov 9 14:02:56] Fatal: Crash in server frame";
        string serilogErrLine =
            "2026-11-09 14:02:58.100 -06:00 [ERR] [LAKEHOUSE_UPLOADER] Upload failed with HTTP 403";
        string serilogInfoWithErrorWord =
            "2026-11-09 14:03:00.000 -06:00 [INF] [QueueArcade] Completed startup with 0 error: records";

        Assert.Equal("WARN", NexusOrchestratorService.ParseLogLevel(k2MissingClipLine));
        Assert.Equal("FATAL", NexusOrchestratorService.ParseLogLevel(k2FatalLine));
        Assert.Equal("ERROR", NexusOrchestratorService.ParseLogLevel(serilogErrLine));
        Assert.Equal("INFO", NexusOrchestratorService.ParseLogLevel(serilogInfoWithErrorWord));
    }

    [Fact]
    public void ParseLogLevel_StatefulContinuationInheritance_PreservesStructuredSeverityAcrossMultilineStackTraces()
    {
        // protects: Control Panel per-stream log parser from misclassifying unprefixed continuation lines (such as
        //           ZiggyCreatures.Caching.Fusion.SyntheticTimeoutException under a [WRN] Serilog header) as [ERROR],
        //           while still classifying genuinely unprefixed standalone exceptions and [ERR] stack traces as [ERROR].
        // fails_when: Continuation lines reset or ignore lastStructuredLevel, or stale level leaks past plain-text lines.
        // seam: Real NexusOrchestratorService.ParseLogLevel(line, ref lastStructuredLevel) across multi-line stream sequences.
        string? streamLevel = null;

        Assert.Equal(
            "WARN",
            NexusOrchestratorService.ParseLogLevel(
                "2026-11-11 23:13:02.049 -06:00 [WRN] [FusionCache] an error occurred while calling the factory for key='profile:simple_stats:guccidonut:ranked'",
                ref streamLevel));
        Assert.Equal("WARN", streamLevel);

        Assert.Equal(
            "WARN",
            NexusOrchestratorService.ParseLogLevel(
                "ZiggyCreatures.Caching.Fusion.SyntheticTimeoutException: The operation has timed out.",
                ref streamLevel));
        Assert.Equal(
            "WARN",
            NexusOrchestratorService.ParseLogLevel(
                "   at ZiggyCreatures.Caching.Fusion.Internals.FusionCacheInternalUtils.RunAsyncFactoryWithTimeout()",
                ref streamLevel));
        Assert.Equal(
            "WARN",
            NexusOrchestratorService.ParseLogLevel(
                "--- End of stack trace from previous location ---",
                ref streamLevel));

        // Normal unstructured stdout line resets continuation state
        Assert.Equal(
            "INFO",
            NexusOrchestratorService.ParseLogLevel("Server heartbeat tick completed.", ref streamLevel));
        Assert.Null(streamLevel);

        // Standalone unprefixed exception without preceding structured header still classifies as ERROR
        Assert.Equal(
            "ERROR",
            NexusOrchestratorService.ParseLogLevel(
                "System.InvalidOperationException: Unhandled worker crash",
                ref streamLevel));

        // Explicit [ERR] structured header propagates ERROR across its continuation frames
        Assert.Equal(
            "ERROR",
            NexusOrchestratorService.ParseLogLevel(
                "2026-11-11 23:14:00.000 -06:00 [ERR] Database connection faulted",
                ref streamLevel));
        Assert.Equal(
            "ERROR",
            NexusOrchestratorService.ParseLogLevel(
                "Npgsql.NpgsqlException: Failed to connect",
                ref streamLevel));
        Assert.Equal(
            "ERROR",
            NexusOrchestratorService.ParseLogLevel(
                "   at Npgsql.Internal.NpgsqlConnector.ConnectAsync()",
                ref streamLevel));
    }
}
