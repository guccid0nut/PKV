// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

using Docker.DotNet;
using Docker.DotNet.Models;
using FluentAssertions;
using Kongor.Cli.Mcp.Services;
using Moq;
using NEXUS.ControlPanel.GUI.Services;
using NEXUS.ControlPanel.GUI.Services.Models;
using NEXUS.ControlPanel.GUI.ViewModels;
using Xunit;

namespace NEXUS.ControlPanel.Tests;

public sealed class DockerRestartAndServiceControlTests
{
    private static DockerComposeStackSnapshot BuildActiveStackSnapshot() => new()
    {
        IsDockerDaemonAvailable = true,
        PerformancePreset = "Eco",
        Services = new Dictionary<string, DockerComposeServiceState>(StringComparer.OrdinalIgnoreCase)
        {
            ["master-server"] = new("master-server", "kongor-stack-master-server", "running", "healthy", "Up 2m (healthy)"),
            ["chat-server"] = new("chat-server", "kongor-stack-chat-server", "running", "healthy", "Up 2m (healthy)"),
            ["web-api"] = new("web-api", "kongor-stack-web-api", "running", "healthy", "Up 2m (healthy)"),
            ["web-ui"] = new("web-ui", "kongor-stack-web-ui", "running", "healthy", "Up 2m (healthy)"),
            ["garnet"] = new("garnet", "kongor-stack-garnet", "running", "healthy", "Up 2m (healthy)")
        }
    };

    private static (
        DashboardViewModel ViewModel,
        Mock<INexusOrchestratorService> OrchestratorMock) CreateDashboardViewModel(
        DockerStackScriptRunner stackRunner,
        ServerState initialClusterState = ServerState.Running)
    {
        Mock<INexusOrchestratorService> orchestratorMock = new(MockBehavior.Loose);
        orchestratorMock.SetupGet(o => o.CurrentClusterState).Returns(initialClusterState);
        orchestratorMock.SetupGet(o => o.LogHistory).Returns([]);
        orchestratorMock
            .Setup(o => o.GetQueueArcadeStateAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync((true, false, "Enabled"));

        Mock<IDockerHealthService> dockerMock = new(MockBehavior.Loose);
        dockerMock
            .Setup(d => d.CheckDockerStatusAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new DockerStatus { IsInstalled = true, IsRunning = true, StatusMessage = "Running" });

        Mock<IDatabaseHealthService> dbMock = new(MockBehavior.Loose);
        dbMock
            .Setup(d => d.CheckDatabaseHealthAsync(It.IsAny<ISecretsManagerService?>()))
            .ReturnsAsync(new DatabaseStatus { IsConnected = true, StatusMessage = "Connected" });
        dbMock
            .Setup(d => d.CheckRedisHealthAsync())
            .ReturnsAsync(new DatabaseStatus { IsConnected = true, StatusMessage = "Connected" });
        dbMock
            .Setup(d => d.CheckPyroscopeHealthAsync())
            .ReturnsAsync(new DatabaseStatus { IsConnected = false, StatusMessage = "Offline" });

        Mock<ISecretsManagerService> secretsMock = new(MockBehavior.Loose);
        secretsMock
            .Setup(s => s.LoadSecretsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new NexusSecrets());

        DashboardViewModel vm = new(
            orchestratorMock.Object,
            dockerMock.Object,
            dbMock.Object,
            secretsMock.Object,
            stackRunner: stackRunner);

        return (vm, orchestratorMock);
    }

    [Fact]
    public async Task RestartClusterAsync_WhenDockerStackActive_InvokesStackRestartAndTransitionsRunning()
    {
        // protects: Active kongor-stack Docker restart path in NexusOrchestratorService without tearing containers down or running rebuilds.
        // fails_when: RestartClusterAsync skips _stackRunner("restart", ...) or fails to transition CurrentClusterState to Running.
        // seam: NexusOrchestratorService injected _stackProbe and _stackRunner delegates.
        List<(string Action, IEnumerable<string>? ExtraArgs)> calls = [];

        using NexusOrchestratorService orchestrator = new(
            stackProbe: (_, _) => Task.FromResult(BuildActiveStackSnapshot()),
            stackRunner: (action, extraArgs, _, onLine, _) =>
            {
                calls.Add((action, extraArgs?.ToArray()));
                onLine?.Invoke("[DOCKER-STACK] [SUCCESS] Stack restart complete.", false);
                return Task.FromResult((0, "restarted"));
            });

        await orchestrator.RestartClusterAsync();

        calls.Should().ContainSingle();
        calls[0].Action.Should().Be("restart");
        calls[0].ExtraArgs.Should().BeEquivalentTo(["-Performance", "Eco"]);
        orchestrator.CurrentClusterState.Should().Be(ServerState.Running);
        orchestrator.LogHistory.Should().Contain(m => m.Message.Contains("Fast-restarting kongor-stack", StringComparison.OrdinalIgnoreCase));
        orchestrator.LogHistory.Should().Contain(m => m.Message.Contains("Stack restart complete", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task RestartClusterAsync_WhenStackLockBusyExit75_RestoresPriorStateAndLogsContention()
    {
        // protects: Singleton docker-stack lock contention (exit 75) handling during RestartClusterAsync.
        // fails_when: Exit 75 leaves cluster stuck in Starting, marks Faulted needlessly, or omits contention telemetry.
        // seam: NexusOrchestratorService injected _stackProbe + _stackRunner returning exit code 75.
        using NexusOrchestratorService orchestrator = new(
            stackProbe: (_, _) => Task.FromResult(BuildActiveStackSnapshot()),
            stackRunner: (_, _, _, onLine, _) =>
            {
                onLine?.Invoke("[DOCKER-STACK] [ERROR] Another docker stack operation is currently running.", true);
                return Task.FromResult((75, "busy"));
            });

        await orchestrator.RestartClusterAsync();

        orchestrator.CurrentClusterState.Should().Be(ServerState.Stopped);
        orchestrator.LogHistory.Should().Contain(m => m.Message.Contains("holds the stack lock", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task RestartClusterAsync_WhenStackRestartFails_SetsFaultedState()
    {
        // protects: Non-zero docker-stack restart failures transition cluster state to Faulted for Dashboard error surfacing.
        // fails_when: Non-zero exit (other than 75) leaves CurrentClusterState as Running or Starting.
        // seam: NexusOrchestratorService _stackProbe + _stackRunner error path.
        using NexusOrchestratorService orchestrator = new(
            stackProbe: (_, _) => Task.FromResult(BuildActiveStackSnapshot()),
            stackRunner: (_, _, _, onLine, _) =>
            {
                onLine?.Invoke("[DOCKER-STACK] [ERROR] Container health check timed out.", true);
                return Task.FromResult((1, "failed"));
            });

        await orchestrator.RestartClusterAsync();

        orchestrator.CurrentClusterState.Should().Be(ServerState.Faulted);
    }

    [Fact]
    public async Task PerServiceDockerLifecycle_StartStopRestart_RoutesToDockerClientAndUpdatesState()
    {
        // protects: Per-card Start/Stop/Restart microservice controls against kongor-stack-* containers via IDockerClient.
        // fails_when: StartServiceAsync/StopServiceAsync/RestartServiceAsync fails to call ContainerOperations or updates wrong ServerState.
        // seam: NexusOrchestratorService + mocked IDockerHealthService.Client (IDockerClient.Containers).
        Mock<IContainerOperations> containersMock = new(MockBehavior.Strict);
        containersMock
            .Setup(c => c.ListContainersAsync(It.IsAny<ContainersListParameters>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(
            [
                new ContainerListResponse
                {
                    ID = "cid-master",
                    Names = ["/kongor-stack-master-server"]
                }
            ]);
        containersMock
            .Setup(c => c.StopContainerAsync("kongor-stack-master-server", It.IsAny<ContainerStopParameters>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);
        containersMock
            .Setup(c => c.StartContainerAsync("kongor-stack-master-server", It.IsAny<ContainerStartParameters>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);
        containersMock
            .Setup(c => c.RestartContainerAsync("kongor-stack-master-server", It.IsAny<ContainerRestartParameters>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        Mock<IDockerClient> dockerClientMock = new(MockBehavior.Loose);
        dockerClientMock.SetupGet(c => c.Containers).Returns(containersMock.Object);

        Mock<IDockerHealthService> dockerHealthMock = new(MockBehavior.Loose);
        dockerHealthMock.SetupGet(d => d.Client).Returns(dockerClientMock.Object);

        using NexusOrchestratorService orchestrator = new(dockerHealth: dockerHealthMock.Object);

        await orchestrator.StopServiceAsync("master-server");
        orchestrator.GetServiceState("master-server").Should().Be(ServerState.Stopped);

        await orchestrator.StartServiceAsync("master-server");
        orchestrator.GetServiceState("master-server").Should().Be(ServerState.Running);

        await orchestrator.RestartServiceAsync("master-server");
        orchestrator.GetServiceState("master-server").Should().Be(ServerState.Running);

        containersMock.Verify(c => c.StopContainerAsync("kongor-stack-master-server", It.IsAny<ContainerStopParameters>(), It.IsAny<CancellationToken>()), Times.Once);
        containersMock.Verify(c => c.StartContainerAsync("kongor-stack-master-server", It.IsAny<ContainerStartParameters>(), It.IsAny<CancellationToken>()), Times.Once);
        containersMock.Verify(c => c.RestartContainerAsync("kongor-stack-master-server", It.IsAny<ContainerRestartParameters>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task DashboardViewModel_PresetCommandsForwardSkipAutoRebuild_AndSurfaceStaleWarningAndErrors()
    {
        // protects: DashboardViewModel -SkipAutoRebuild preset switches, busy-lock concurrency prevention, stale-image warning, and error surfacing.
        // fails_when: Preset launches rebuild unnecessarily, concurrent RestartClusterAsync overlaps Eco launch, or error/warn banners fail to update.
        // seam: DashboardViewModel injected DockerStackScriptRunner + INexusOrchestratorService mock.
        List<(string Action, string[]? Args)> recordedCalls = [];
        TaskCompletionSource<(int ExitCode, string Output)> restartHold = new(TaskCreationOptions.RunContinuationsAsynchronously);

        (DashboardViewModel vm, Mock<INexusOrchestratorService> orchestratorMock) = CreateDashboardViewModel(
            (action, extraArgs, _, onLine, _) =>
            {
                string[]? capturedArgs = extraArgs?.ToArray();
                recordedCalls.Add((action, capturedArgs));

                if (action == "up")
                {
                    onLine?.Invoke("[DOCKER-STACK] [WARN] Cached Docker images do not include latest C# changes.", true);
                    return Task.FromResult((0, "ok"));
                }

                onLine?.Invoke("[DOCKER-STACK] [ERROR] Simulated lock failure", true);
                return Task.FromResult((75, "busy"));
            });

        using (vm)
        {
            orchestratorMock
                .Setup(o => o.RestartClusterAsync(It.IsAny<bool>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
                .Returns(async () =>
                {
                    await restartHold.Task;
                    orchestratorMock.SetupGet(o => o.CurrentClusterState).Returns(ServerState.Running);
                });

            Task restartTask = vm.RestartClusterAsync();
            vm.IsDockerStackBusy.Should().BeTrue();
            vm.CanRestartCluster.Should().BeFalse();

            // Overlapping Eco launch while RestartClusterAsync holds IsDockerStackBusy must be blocked.
            await vm.LaunchDockerStackEcoAsync();
            recordedCalls.Should().BeEmpty("IsDockerStackBusy must prevent overlapping Docker stack runs");

            restartHold.SetResult((0, "done"));
            await restartTask;

            vm.IsDockerStackBusy.Should().BeFalse();
            vm.ClusterStatusText.Should().Be("CLUSTER / DOCKER STACK RESTARTED & ONLINE");

            // Normal Eco launch must pass -SkipAutoRebuild and propagate stale-image warning banner.
            await vm.LaunchDockerStackEcoAsync();
            recordedCalls.Should().ContainSingle(c =>
                c.Action == "up" &&
                c.Args != null &&
                c.Args.Contains("Eco") &&
                c.Args.Contains("-SkipAutoRebuild"));
            vm.HasStaleImageWarning.Should().BeTrue();
            vm.StaleImageWarningText.Should().Contain("Rebuild & Sync Stack");

            // Exit 75 contention must show actionable busy message rather than generic exit code failure.
            await vm.RestartDockerStackAsync();
            vm.ClusterStatusText.Should().Contain("Docker Stack busy");
        }
    }

    [Fact]
    public void ServiceItemViewModel_UpdateState_TogglesCanStartCanStopCanRestartAndRaisesNotifications()
    {
        // protects: ServiceItemViewModel button enablement properties (CanStart, CanStop, CanRestart) across lifecycle state transitions.
        // fails_when: Stopped/Faulted disables Start, Running disables Restart, or PropertyChanged is not raised for CanRestart.
        // seam: ServiceItemViewModel.UpdateState state machine.
        Mock<INexusOrchestratorService> orchestratorMock = new(MockBehavior.Loose);
        orchestratorMock.Setup(o => o.GetServiceState("master-server")).Returns(ServerState.Stopped);

        ServiceItemViewModel item = new(
            orchestratorMock.Object,
            "master-server",
            "KONGOR Master Server",
            "👑",
            "HTTP: 55555",
            "Auth & Store",
            "#F59E0B");

        List<string?> changedProperties = [];
        item.PropertyChanged += (_, args) => changedProperties.Add(args.PropertyName);

        item.CanStart.Should().BeTrue();
        item.CanStop.Should().BeFalse();
        item.CanRestart.Should().BeFalse();

        item.UpdateState(ServerState.Running);
        item.CanStart.Should().BeFalse();
        item.CanStop.Should().BeTrue();
        item.CanRestart.Should().BeTrue();
        changedProperties.Should().Contain(nameof(ServiceItemViewModel.CanRestart));

        item.UpdateState(ServerState.Faulted);
        item.CanStart.Should().BeTrue();
        item.CanStop.Should().BeFalse();
        item.CanRestart.Should().BeFalse();
    }
}
