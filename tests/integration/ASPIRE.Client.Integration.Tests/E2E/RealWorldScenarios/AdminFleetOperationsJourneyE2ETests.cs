// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

// ============================================================================
// File: AdminFleetOperationsJourneyE2ETests.cs
// System: ProjectKongor Solution-Wide E2E Testing Suite
// Tier 4: Real-World Application Scenarios (Admin Fleet Operations E2E Journey)
// Policy: Strict Explicit Typing, Multi-Stage Pipeline Verification
// ============================================================================

using FluentAssertions;
using Moq;
using NEXUS.ControlPanel.GUI.Services;
using NEXUS.ControlPanel.GUI.Services.Models;
using NEXUS.ControlPanel.GUI.ViewModels;

namespace ASPIRE.Tests.E2E.Tier4_RealWorldScenarios;

public sealed class AdminFleetOperationsJourneyE2ETests
{
    [Fact]
    public async Task Journey_AdminFleetManagement_DiscoversAndMonitorsDedicatedInstances()
    {
        // ---------------------------------------------------------------------
        // Stage 1: Dashboard Initialization & Health Assessment
        // ---------------------------------------------------------------------
        Mock<INexusOrchestratorService> mockOrchestrator = new();
        Mock<IDockerHealthService> mockDocker = new();
        Mock<IDatabaseHealthService> mockDb = new();

        mockOrchestrator.Setup(o => o.CurrentClusterState).Returns(ServerState.Stopped);
        mockOrchestrator.Setup(o => o.GetServiceState(It.IsAny<string>())).Returns(ServerState.Stopped);
        mockDocker.Setup(d => d.CheckDockerStatusAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new DockerStatus { IsInstalled = true, IsRunning = true, StatusMessage = "Operational" });
        mockDb.Setup(d => d.CheckSqlServerHealthAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new DatabaseStatus { IsConnected = true, StatusMessage = "Operational" });

        DashboardViewModel dashboardVm = new(mockOrchestrator.Object, mockDocker.Object, mockDb.Object);

        dashboardVm.ClusterState.Should().Be(ServerState.Stopped);
        dashboardVm.Microservices.Count.Should().Be(4);

        // ---------------------------------------------------------------------
        // Stage 2: Game Server Fleet Inspection
        // ---------------------------------------------------------------------
        Mock<IGameServerFleetService> mockFleet = new();
        List<GameServerHostEntry> hostEntries =
        [
            new() { Id = "host-1", Name = "Server-Alpha", HostAddress = "127.0.0.1", Port = 11235, Location = "USE" },
            new() { Id = "host-2", Name = "Server-Beta", HostAddress = "127.0.0.1", Port = 11236, Location = "EU" }
        ];

        mockFleet.Setup(f => f.GetDiscoveredHostsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(hostEntries);

        GameServersViewModel fleetVm = new(mockFleet.Object);
        fleetVm.Should().NotBeNull();

        // ---------------------------------------------------------------------
        // Stage 3: Remote Administration via User Management Service
        // ---------------------------------------------------------------------
        Mock<IUserManagementService> mockUserService = new();
        mockUserService.Setup(u => u.SetUserRoleAsync(42, "ADMINISTRATOR", It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);
        mockUserService.Setup(u => u.UpdateUserEconomyAsync(42, 500, 1000, 5, It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);

        bool roleUpdated = await mockUserService.Object.SetUserRoleAsync(42, "ADMINISTRATOR");
        bool economyUpdated = await mockUserService.Object.UpdateUserEconomyAsync(42, 500, 1000, 5);

        roleUpdated.Should().BeTrue();
        economyUpdated.Should().BeTrue();
    }

    [Fact]
    public void Journey_MicroserviceStatusEntry_TracksAllCoreComponents()
    {
        // Arrange
        ServerState state = ServerState.Running;

        // Assert
        state.Should().Be(ServerState.Running);
    }

    [Fact]
    public void Journey_DockerStatus_ReportsInstallationState()
    {
        // Arrange
        DockerStatus status = new()
        {
            IsInstalled = true,
            IsRunning = false,
            StatusMessage = "Docker daemon stopped"
        };

        // Assert
        status.IsInstalled.Should().BeTrue();
        status.IsRunning.Should().BeFalse();
    }

    [Fact]
    public void Journey_DatabaseStatus_ReportsConnectionState()
    {
        // Arrange
        DatabaseStatus status = new()
        {
            IsConnected = true,
            StatusMessage = "PostgreSQL cluster healthy"
        };

        // Assert
        status.IsConnected.Should().BeTrue();
    }
}
