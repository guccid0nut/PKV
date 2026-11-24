// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

using Docker.DotNet;
using Docker.DotNet.Models;
using FluentAssertions;
using Moq;
using NEXUS.ControlPanel.GUI.Controls;
using NEXUS.ControlPanel.GUI.Services;
using NEXUS.ControlPanel.GUI.Services.Models;
using NEXUS.ControlPanel.GUI.ViewModels;
using Xunit;

namespace NEXUS.ControlPanel.Tests;

public sealed class WindowCloseConfirmationTests
{
    private static async Task<(
        MainViewModel ViewModel,
        Mock<INexusOrchestratorService> OrchestratorMock,
        Mock<ISecretsManagerService> SecretsMock)> CreateMainViewModelAsync(
        RuntimeExitInspectionResult exitInspection)
    {
        Mock<INexusOrchestratorService> orchestratorMock = new(MockBehavior.Loose);
        orchestratorMock.SetupGet(o => o.CurrentClusterState).Returns(ServerState.Stopped);
        orchestratorMock.SetupGet(o => o.LogHistory).Returns([]);
        orchestratorMock.SetupGet(o => o.AspireDashboardUrl).Returns("https://localhost:5550");
        orchestratorMock
            .Setup(o => o.GetQueueArcadeStateAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync((true, false, "Enabled"));
        orchestratorMock
            .Setup(o => o.InspectRuntimeForExitAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(exitInspection);

        Mock<ISecretsManagerService> secretsMock = new(MockBehavior.Loose);
        secretsMock
            .Setup(s => s.LoadSecretsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new NexusSecrets());
        secretsMock
            .Setup(s => s.GetDriftReportAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(SecretsDriftReport.None);
        secretsMock
            .Setup(s => s.SaveSecretsAsync(
                It.IsAny<NexusSecrets>(),
                It.IsAny<NexusSecrets?>(),
                It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        Mock<IDockerHealthService> dockerMock = new(MockBehavior.Loose);
        dockerMock
            .Setup(d => d.CheckDockerStatusAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new DockerStatus { IsInstalled = true, IsRunning = false, StatusMessage = "Offline" });

        Mock<IPortScannerService> portScannerMock = new(MockBehavior.Loose);
        portScannerMock
            .Setup(p => p.ScanRequiredPortsAsync())
            .ReturnsAsync([]);

        Mock<IDatabaseHealthService> dbMock = new(MockBehavior.Loose);
        dbMock
            .Setup(d => d.CheckDatabaseHealthAsync(It.IsAny<ISecretsManagerService?>()))
            .ReturnsAsync(new DatabaseStatus { IsConnected = false, StatusMessage = "Offline" });
        dbMock
            .Setup(d => d.CheckRedisHealthAsync())
            .ReturnsAsync(new DatabaseStatus { IsConnected = false, StatusMessage = "Offline" });
        dbMock
            .Setup(d => d.CheckPyroscopeHealthAsync())
            .ReturnsAsync(new DatabaseStatus { IsConnected = false, StatusMessage = "Offline" });

        Mock<IUserManagementService> userMock = new(MockBehavior.Loose);
        userMock
            .Setup(u => u.GetSummaryStatsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new UserSummaryStats());
        userMock
            .Setup(u => u.SearchUsersAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([]);

        Mock<IGameClientAssetService> assetMock = new(MockBehavior.Loose);
        assetMock
            .Setup(a => a.EnsureAssetsExtractedAsync(It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);

        Mock<ICockpitAppBridgeService> cockpitMock = new(MockBehavior.Loose);

        MainViewModel vm = new(
            orchestratorMock.Object,
            secretsMock.Object,
            dockerMock.Object,
            portScannerMock.Object,
            dbMock.Object,
            userMock.Object,
            assetMock.Object,
            cockpitMock.Object);

        await vm.Dashboard.InitialHydrationTask;
        await vm.SecretsConfig.LoadSecretsAsync();

        return (vm, orchestratorMock, secretsMock);
    }

    [Fact]
    public async Task RequestWindowClose_WhenAllServicesStopped_DetachesImmediatelyWithoutModalOrStopAll()
    {
        // protects: Closing the window X when Docker and local services are stopped exits immediately without an unnecessary modal or StopAll sweep.
        // fails_when: Idle close either pops a redundant confirmation dialog or calls StopAllAsync unnecessarily.
        // seam: MainViewModel.RequestWindowCloseAsync -> INexusOrchestratorService.InspectRuntimeForExitAsync -> OnRequestDetachExit.

        (MainViewModel vm, Mock<INexusOrchestratorService> orchestratorMock, _) =
            await CreateMainViewModelAsync(RuntimeExitInspectionResult.Idle);
        using (vm)
        {
            string? detachReason = null;
            string? fullShutdownReason = null;
            vm.OnRequestDetachExit += r => detachReason = r;
            vm.OnRequestFullShutdownExit += r => fullShutdownReason = r;

            await vm.RequestWindowCloseAsync();

            vm.IsExitConfirmationModalOpen.Should().BeFalse();
            detachReason.Should().Be("WindowCloseIdle");
            fullShutdownReason.Should().BeNull();
            orchestratorMock.Verify(o => o.StopAllAsync(It.IsAny<CancellationToken>()), Times.Never);
        }
    }

    [Fact]
    public async Task RequestWindowClose_WhenDockerStackRunning_Opens3ButtonModalDefaultingToKeepDocker_AndKeepDockerDetachesWithoutStopping()
    {
        // protects: Clicking window X while Docker stack runs prompts the 3-button dialog defaulting to "No (Keep Docker Running)" and never stops Docker on default choice.
        // fails_when: Modal fails to open, IsKeepDockerDefaultButton is false, or confirming Keep Docker invokes full shutdown / StopAllAsync.
        // seam: MainViewModel.RequestWindowCloseAsync -> IsKeepDockerDefaultButton -> ConfirmExitKeepDockerRunningCommand -> OnRequestDetachExit.

        RuntimeExitInspectionResult dockerRunning = new(
            HasRunningDockerServices: true,
            RunningDockerContainerCount: 8,
            RunningDockerContainerNames: ["kongor-stack-master-server-1", "kongor-stack-chat-server-1"],
            IsDockerStackActive: true,
            HasRunningLocalHostServices: false,
            SummaryMessage: "Docker stack active (8 container(s)).");

        (MainViewModel vm, Mock<INexusOrchestratorService> orchestratorMock, _) =
            await CreateMainViewModelAsync(dockerRunning);
        using (vm)
        {
            string? detachReason = null;
            string? fullShutdownReason = null;
            vm.OnRequestDetachExit += r => detachReason = r;
            vm.OnRequestFullShutdownExit += r => fullShutdownReason = r;

            await vm.RequestWindowCloseAsync();

            vm.IsExitConfirmationModalOpen.Should().BeTrue();
            vm.IsDockerExitMode.Should().BeTrue();
            vm.IsKeepDockerDefaultButton.Should().BeTrue();
            vm.ExitShutDownButtonText.Should().Be("⏹ Shut Down Docker & Exit");
            vm.ExitModalDescription.Should().Contain("8 active container(s)");

            vm.ConfirmExitKeepDockerRunningCommand.Execute(null);

            vm.IsExitConfirmationModalOpen.Should().BeFalse();
            detachReason.Should().Be("UserConfirmedKeepDockerRunning");
            fullShutdownReason.Should().BeNull();
            orchestratorMock.Verify(o => o.StopAllAsync(It.IsAny<CancellationToken>()), Times.Never);
        }
    }

    [Fact]
    public async Task RequestWindowClose_WhenDockerStackRunning_AndUserSelectsShutDownDockerAndExit_TriggersFullGracefulShutdown()
    {
        // protects: Explicit confirmation of "Shut Down Docker & Exit" in the close modal routes to full graceful shutdown.
        // fails_when: Selecting the shutdown option detaches instead of signaling full shutdown, or leaves the modal open.
        // seam: MainViewModel.ConfirmExitShutDownServicesCommand -> OnRequestFullShutdownExit.

        RuntimeExitInspectionResult dockerRunning = new(
            HasRunningDockerServices: true,
            RunningDockerContainerCount: 2,
            RunningDockerContainerNames: ["ranked-compel-server", "arcade-compel-server"],
            IsDockerStackActive: false,
            HasRunningLocalHostServices: false,
            SummaryMessage: "Docker container(s) active: ranked-compel-server, arcade-compel-server.");

        (MainViewModel vm, _, _) = await CreateMainViewModelAsync(dockerRunning);
        using (vm)
        {
            string? detachReason = null;
            string? fullShutdownReason = null;
            vm.OnRequestDetachExit += r => detachReason = r;
            vm.OnRequestFullShutdownExit += r => fullShutdownReason = r;

            await vm.RequestWindowCloseAsync();

            vm.IsExitConfirmationModalOpen.Should().BeTrue();
            vm.IsDockerExitMode.Should().BeTrue();
            vm.ExitModalDescription.Should().Contain("ranked-compel-server");

            vm.ConfirmExitShutDownServicesCommand.Execute(null);

            vm.IsExitConfirmationModalOpen.Should().BeFalse();
            fullShutdownReason.Should().Be("UserConfirmedShutDownDockerAndExit");
            detachReason.Should().BeNull();
        }
    }

    [Fact]
    public async Task RequestWindowClose_WhenDockerRunning_AndUserClicksCancel_KeepsWindowOpenWithoutExiting()
    {
        // protects: Clicking Cancel / pressing Esc keeps Control Panel open without touching Docker and restores Win32 3D viewport airspace.
        // fails_when: Cancelling fires an exit event or fails to toggle Kongor3DViewportControl.SetModalOverlayActive(true -> false).
        // seam: MainViewModel.CancelExitConfirmationCommand <-> Kongor3DViewportControl.OnGlobalModalOverlayChanged.

        RuntimeExitInspectionResult dockerRunning = new(
            HasRunningDockerServices: true,
            RunningDockerContainerCount: 8,
            RunningDockerContainerNames: ["kongor-stack-master-server-1"],
            IsDockerStackActive: true,
            HasRunningLocalHostServices: false,
            SummaryMessage: "Docker stack active.");

        (MainViewModel vm, Mock<INexusOrchestratorService> orchestratorMock, _) =
            await CreateMainViewModelAsync(dockerRunning);

        List<bool> modalStates = [];
        void Handler(bool active) => modalStates.Add(active);
        Kongor3DViewportControl.OnGlobalModalOverlayChanged += Handler;
        try
        {
            using (vm)
            {
                bool exited = false;
                vm.OnRequestDetachExit += _ => exited = true;
                vm.OnRequestFullShutdownExit += _ => exited = true;

                await vm.RequestWindowCloseAsync();
                vm.IsExitConfirmationModalOpen.Should().BeTrue();

                vm.CancelExitConfirmationCommand.Execute(null);

                vm.IsExitConfirmationModalOpen.Should().BeFalse();
                vm.IsKeepDockerDefaultButton.Should().BeFalse();
                exited.Should().BeFalse();
                orchestratorMock.Verify(o => o.StopAllAsync(It.IsAny<CancellationToken>()), Times.Never);
                modalStates.Should().ContainInOrder(true, false);
            }
        }
        finally
        {
            Kongor3DViewportControl.OnGlobalModalOverlayChanged -= Handler;
        }
    }

    [Fact]
    public async Task RequestWindowClose_WhenOnlyLocalClusterRunning_Opens2ButtonLocalClusterModal_WithKeepDockerHidden()
    {
        // protects: Closing X when only local host cluster services are active opens the 2-button local-cluster confirmation without offering "Keep Docker".
        // fails_when: Local cluster close either skips confirmation or mistakenly renders Docker-only controls.
        // seam: MainViewModel.RequestWindowCloseAsync -> IsDockerExitMode == false -> ExitShutDownButtonText.

        RuntimeExitInspectionResult localOnly = new(
            HasRunningDockerServices: false,
            RunningDockerContainerCount: 0,
            RunningDockerContainerNames: [],
            IsDockerStackActive: false,
            HasRunningLocalHostServices: true,
            SummaryMessage: "Local host cluster services are active.");

        (MainViewModel vm, _, _) = await CreateMainViewModelAsync(localOnly);
        using (vm)
        {
            string? fullShutdownReason = null;
            vm.OnRequestFullShutdownExit += r => fullShutdownReason = r;

            await vm.RequestWindowCloseAsync();

            vm.IsExitConfirmationModalOpen.Should().BeTrue();
            vm.IsDockerExitMode.Should().BeFalse();
            vm.IsKeepDockerDefaultButton.Should().BeFalse();
            vm.ExitShutDownButtonText.Should().Be("⏹ Stop Local Cluster & Exit");

            vm.ConfirmExitShutDownServicesCommand.Execute(null);

            vm.IsExitConfirmationModalOpen.Should().BeFalse();
            fullShutdownReason.Should().Be("UserConfirmedStopLocalClusterAndExit");
        }
    }

    [Fact]
    public async Task RequestWindowClose_WhenSecretsHaveUnsavedChanges_ChainsUnsavedGuardIntoDockerExitModal()
    {
        // protects: Closing X with unsaved Secrets & Config edits first offers Save/Discard, then automatically resumes the Docker close prompt.
        // fails_when: Unsaved changes are silently lost on close, or confirming Save exits without showing the active Docker confirmation modal.
        // seam: MainViewModel.RequestWindowCloseAsync -> IsUnsavedChangesModalOpen -> ConfirmSaveAndNavigateCommand -> IsExitConfirmationModalOpen.

        RuntimeExitInspectionResult dockerRunning = new(
            HasRunningDockerServices: true,
            RunningDockerContainerCount: 8,
            RunningDockerContainerNames: ["kongor-stack-master-server-1"],
            IsDockerStackActive: true,
            HasRunningLocalHostServices: false,
            SummaryMessage: "Docker stack active.");

        (MainViewModel vm, _, Mock<ISecretsManagerService> secretsMock) =
            await CreateMainViewModelAsync(dockerRunning);
        using (vm)
        {
            vm.SecretsConfig.HasChanges = true;

            await vm.RequestWindowCloseAsync();

            vm.IsUnsavedChangesModalOpen.Should().BeTrue();
            vm.IsPendingWindowCloseOnUnsavedModal.Should().BeTrue();
            vm.IsExitConfirmationModalOpen.Should().BeFalse();
            vm.UnsavedModalSaveButtonText.Should().Be("💾 Save & Continue");

            await vm.ConfirmSaveAndNavigateAsync();

            secretsMock.Verify(
                s => s.SaveSecretsAsync(It.IsAny<NexusSecrets>(), It.IsAny<NexusSecrets?>(), It.IsAny<CancellationToken>()),
                Times.Once);
            vm.IsUnsavedChangesModalOpen.Should().BeFalse();
            vm.IsPendingWindowCloseOnUnsavedModal.Should().BeFalse();
            vm.IsExitConfirmationModalOpen.Should().BeTrue();
            vm.IsDockerExitMode.Should().BeTrue();
        }
    }

    [Fact]
    public async Task StopAllServicesCommand_AlwaysTriggersFullAppExitBypassingCloseConfirmationModal()
    {
        // protects: Clicking Dashboard "Stop All" keeps immediate full shutdown semantics and bypasses the window-X confirmation modal.
        // fails_when: StopAllServicesCommand opens the X close modal or stops invoking DashboardViewModel.OnRequestAppExit.
        // seam: DashboardViewModel.StopAllServicesCommand -> DashboardViewModel.OnRequestAppExit.

        RuntimeExitInspectionResult dockerRunning = new(
            HasRunningDockerServices: true,
            RunningDockerContainerCount: 8,
            RunningDockerContainerNames: ["kongor-stack-master-server-1"],
            IsDockerStackActive: true,
            HasRunningLocalHostServices: false,
            SummaryMessage: "Docker stack active.");

        (MainViewModel vm, _, _) = await CreateMainViewModelAsync(dockerRunning);
        int exitSignalCount = 0;
        void ExitHandler() => exitSignalCount++;

        DashboardViewModel.OnRequestAppExit += ExitHandler;
        try
        {
            using (vm)
            {
                await vm.Dashboard.StopAllServicesCommand.ExecuteAsync(null);

                exitSignalCount.Should().Be(1);
                vm.IsExitConfirmationModalOpen.Should().BeFalse();
            }
        }
        finally
        {
            DashboardViewModel.OnRequestAppExit -= ExitHandler;
        }
    }

    [Fact]
    public async Task InspectRuntimeForExitAsync_ClassifiesManagedContainersAccurately_AndDetachPreservesSessionPids()
    {
        // protects: Only ProjectKongor stack and game-server containers count as active Docker runtime (ignoring Aspire cache/Pyroscope sidecars and unrelated containers), and DetachForBackgroundExit safely retires pollers idempotently without throwing or invoking StopAll.
        // fails_when: Helper containers like kongor-garnet-cache trigger false Docker close prompts, real stack containers are missed, or double Detach/Dispose throws.
        // seam: NexusOrchestratorService.IsManagedProjectKongorContainer & InspectRuntimeForExitAsync <-> IDockerHealthService/IDockerClient & DetachForBackgroundExit.

        List<ContainerListResponse> fakeContainers =
        [
            new ContainerListResponse
            {
                ID = "111111111111",
                Names = ["/kongor-stack-master-server-1"],
                Image = "kongor-stack-master-server:latest",
                Labels = new Dictionary<string, string> { ["com.docker.compose.project"] = "kongor-stack" }
            },
            new ContainerListResponse
            {
                ID = "222222222222",
                Names = ["/ranked-compel-server"],
                Image = "projectkongor/compel-dedicated:latest",
                Labels = new Dictionary<string, string>()
            },
            new ContainerListResponse
            {
                ID = "333333333333",
                Names = ["/kongor-garnet-cache"],
                Image = "ghcr.io/microsoft/garnet:latest",
                Labels = new Dictionary<string, string>()
            },
            new ContainerListResponse
            {
                ID = "444444444444",
                Names = ["/unrelated-postgres"],
                Image = "postgres:16",
                Labels = new Dictionary<string, string>()
            }
        ];

        Mock<IContainerOperations> containerOpsMock = new(MockBehavior.Strict);
        containerOpsMock
            .Setup(c => c.ListContainersAsync(It.IsAny<ContainersListParameters>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(fakeContainers);

        Mock<IDockerClient> dockerClientMock = new(MockBehavior.Loose);
        dockerClientMock.SetupGet(c => c.Containers).Returns(containerOpsMock.Object);

        Mock<IDockerHealthService> dockerHealthMock = new(MockBehavior.Loose);
        dockerHealthMock.SetupGet(d => d.Client).Returns(dockerClientMock.Object);

        using NexusOrchestratorService orchestrator = new(secretsManager: null, dockerHealth: dockerHealthMock.Object);

        RuntimeExitInspectionResult result = await orchestrator.InspectRuntimeForExitAsync();

        result.HasRunningDockerServices.Should().BeTrue();
        result.IsDockerStackActive.Should().BeTrue();
        result.RunningDockerContainerCount.Should().Be(2);
        result.RunningDockerContainerNames.Should().Equal("kongor-stack-master-server-1", "ranked-compel-server");
        result.HasRunningLocalHostServices.Should().BeFalse();

        // Pure classification edge cases
        NexusOrchestratorService.IsManagedProjectKongorContainer(
            new ContainerListResponse { Names = ["/kongor-garnet-cache"], Image = "ghcr.io/microsoft/garnet:latest" },
            out _,
            out _).Should().BeFalse();
        NexusOrchestratorService.IsManagedProjectKongorContainer(
            new ContainerListResponse { Names = ["/compel-queue-arcade-server"], Image = "projectkongor/compel-dedicated:latest" },
            out bool arcadeIsStack,
            out string arcadeName).Should().BeTrue();
        arcadeIsStack.Should().BeFalse();
        arcadeName.Should().Be("compel-queue-arcade-server");

        // Detach + duplicate Detach + Dispose must all succeed idempotently without stopping containers
        orchestrator.DetachForBackgroundExit();
        orchestrator.DetachForBackgroundExit();
        orchestrator.Dispose();
    }
}
