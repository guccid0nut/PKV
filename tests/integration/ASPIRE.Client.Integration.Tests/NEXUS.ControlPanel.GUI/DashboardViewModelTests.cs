// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

using Moq;
using NEXUS.ControlPanel.GUI.Services;
using NEXUS.ControlPanel.GUI.Services.Models;
using NEXUS.ControlPanel.GUI.ViewModels;

namespace ASPIRE.Tests.NEXUS.ControlPanel.GUI;

public sealed class DashboardViewModelTests
{
    [Fact]
    public void DashboardViewModel_WhenExecuted_InitializesAllMicroservicesAndInfrastructure()
    {
        // Arrange
        Mock<INexusOrchestratorService> mockOrchestrator = new();
        Mock<IDockerHealthService> mockDockerHealth = new();
        Mock<IDatabaseHealthService> mockDatabaseHealth = new();

        mockOrchestrator.Setup(o => o.CurrentClusterState).Returns(ServerState.Stopped);
        mockOrchestrator.Setup(o => o.GetServiceState(It.IsAny<string>())).Returns(ServerState.Stopped);
        mockDockerHealth.Setup(d => d.CheckDockerStatusAsync(CancellationToken.None)).ReturnsAsync(new DockerStatus
        {
            IsInstalled = true,
            IsRunning = true,
            StatusMessage = "Docker Engine is running and ready."
        });
        mockDatabaseHealth.Setup(d =>
                d.CheckDatabaseHealthAsync(It.IsAny<ISecretsManagerService?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(
                new DatabaseStatus
                {
                    DatabaseType = "Supabase Cloud (PostgreSQL)",
                    Port = 5432,
                    IsConnected = true,
                    StatusMessage = "Supabase Cloud DB is active & connected (12ms)."
                });
        mockDatabaseHealth.Setup(d => d.CheckSqlServerHealthAsync(1433, CancellationToken.None)).ReturnsAsync(
            new DatabaseStatus
            {
                DatabaseType = "SQL Server 2022",
                Port = 1433,
                IsConnected = true,
                StatusMessage = "SQL Server is active on port 1433."
            });
        mockDatabaseHealth.Setup(d => d.CheckRedisHealthAsync(CancellationToken.None)).ReturnsAsync(new DatabaseStatus
        {
            DatabaseType = "Redis Distributed Cache",
            Port = 21631,
            IsConnected = true,
            StatusMessage = "Redis Cache is active on port 21631."
        });

        // Act
        DashboardViewModel vm = new(mockOrchestrator.Object, mockDockerHealth.Object, mockDatabaseHealth.Object);

        // Assert: 4 core microservices initialized (KONGOR, TRANSMUTANSTEIN, ZORGATH, DAWNBRINGER)
        Assert.Equal(4, vm.Microservices.Count);
        Assert.Contains(vm.Microservices, m => m.Key == "master-server");
        Assert.Contains(vm.Microservices, m => m.Key == "chat-server");
        Assert.Contains(vm.Microservices, m => m.Key == "web-portal-api");
        Assert.Contains(vm.Microservices, m => m.Key == "web-portal-ui");
        Assert.Equal(ServerState.Stopped, vm.ClusterState);
        Assert.True(vm.CanStartCluster);
        Assert.False(vm.CanStopCluster);
    }

    [Fact]
    public async Task DashboardViewModel_LaunchGameClient_InvokesOrchestratorService()
    {
        // Arrange
        Mock<INexusOrchestratorService> mockOrchestrator = new();
        Mock<IDockerHealthService> mockDockerHealth = new();
        Mock<IDatabaseHealthService> mockDatabaseHealth = new();

        DashboardViewModel vm = new(mockOrchestrator.Object, mockDockerHealth.Object, mockDatabaseHealth.Object);

        // Act
        await vm.LaunchGameClientAsync();

        // Assert
        mockOrchestrator.Verify(o => o.LaunchGameClientAsync(It.IsAny<string?>(), CancellationToken.None), Times.Once);
    }

    [Fact]
    public async Task DashboardViewModel_LaunchGameServers_InvokesOrchestratorService()
    {
        // Arrange
        Mock<INexusOrchestratorService> mockOrchestrator = new();
        Mock<IDockerHealthService> mockDockerHealth = new();
        Mock<IDatabaseHealthService> mockDatabaseHealth = new();

        mockOrchestrator.SetupGet(o => o.CurrentClusterState).Returns(ServerState.Running);

        DashboardViewModel vm = new(mockOrchestrator.Object, mockDockerHealth.Object, mockDatabaseHealth.Object);

        // Act
        await vm.LaunchGameServersAsync();

        // Assert
        mockOrchestrator.Verify(o => o.LaunchGameServerAsync(It.IsAny<string?>(), CancellationToken.None), Times.Once);
    }

    [Fact]
    public void DashboardViewModel_LaunchDockerGameServers_InvokesDockerLaunchCallback()
    {
        // Arrange
        Mock<INexusOrchestratorService> mockOrchestrator = new();
        Mock<IDockerHealthService> mockDockerHealth = new();
        Mock<IDatabaseHealthService> mockDatabaseHealth = new();
        bool dockerLaunched = false;

        DashboardViewModel vm = new(
            mockOrchestrator.Object,
            mockDockerHealth.Object,
            mockDatabaseHealth.Object,
            onLaunchDockerServer: () => dockerLaunched = true);

        // Act
        vm.LaunchDockerGameServersCommand.Execute(null);

        // Assert
        Assert.True(dockerLaunched);
        Assert.Contains("Docker", vm.ClusterStatusText);
    }

    [Fact]
    public void DashboardViewModel_LaunchK3sAgonesGameServers_InvokesK3sLaunchCallback()
    {
        // Arrange
        Mock<INexusOrchestratorService> mockOrchestrator = new();
        Mock<IDockerHealthService> mockDockerHealth = new();
        Mock<IDatabaseHealthService> mockDatabaseHealth = new();
        bool k3sLaunched = false;

        DashboardViewModel vm = new(
            mockOrchestrator.Object,
            mockDockerHealth.Object,
            mockDatabaseHealth.Object,
            onLaunchK3sAgonesServer: () => k3sLaunched = true);

        // Act
        vm.LaunchK3sAgonesGameServersCommand.Execute(null);

        // Assert
        Assert.True(k3sLaunched);
        Assert.Contains("K3s", vm.ClusterStatusText);
    }

    [Fact]
    public async Task DashboardViewModel_RefreshInfrastructureHealth_UpdatesAllStatuses()
    {
        // Arrange
        Mock<INexusOrchestratorService> mockOrchestrator = new();
        Mock<IDockerHealthService> mockDockerHealth = new();
        Mock<IDatabaseHealthService> mockDatabaseHealth = new();

        mockDockerHealth.Setup(d => d.CheckDockerStatusAsync(CancellationToken.None)).ReturnsAsync(new DockerStatus
        {
            IsInstalled = true,
            IsRunning = true,
            StatusMessage = "Docker Engine is active."
        });
        mockDatabaseHealth.Setup(d =>
                d.CheckDatabaseHealthAsync(It.IsAny<ISecretsManagerService?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(
                new DatabaseStatus
                {
                    DatabaseType = "Supabase Cloud (PostgreSQL)",
                    Port = 5432,
                    IsConnected = true,
                    StatusMessage = "Supabase Cloud DB ready."
                });
        mockDatabaseHealth.Setup(d => d.CheckRedisHealthAsync(CancellationToken.None)).ReturnsAsync(new DatabaseStatus
        {
            DatabaseType = "Redis Distributed Cache",
            Port = 21631,
            IsConnected = true,
            StatusMessage = "Redis Cache ready."
        });
        mockDatabaseHealth.Setup(d => d.CheckPyroscopeHealthAsync(4040, CancellationToken.None)).ReturnsAsync(
            new DatabaseStatus
            {
                DatabaseType = "Pyroscope Continuous Profiling",
                Port = 4040,
                IsConnected = true,
                StatusMessage = "Pyroscope is active on port 4040 (2ms)."
            });

        DashboardViewModel vm = new(mockOrchestrator.Object, mockDockerHealth.Object, mockDatabaseHealth.Object);

        // Act
        await vm.RefreshInfrastructureHealthAsync();

        // Assert
        Assert.True(vm.IsDockerOnline);
        Assert.True(vm.IsDatabaseOnline);
        Assert.True(vm.IsRedisOnline);
        Assert.True(vm.IsPyroscopeOnline);
        Assert.Equal("⚡ Supabase:", vm.DatabaseLabel);
        Assert.Equal("#10B981", vm.DockerStatusColor);
        Assert.Equal("#10B981", vm.DatabaseStatusColor);
        Assert.Equal("#10B981", vm.RedisStatusColor);
        Assert.Equal("#10B981", vm.PyroscopeStatusColor);
    }

    [Fact]
    public async Task RefreshInfrastructureHealth_WithSqlServer_SetsSqlServerLabel()
    {
        // Arrange
        Mock<INexusOrchestratorService> mockOrchestrator = new();
        Mock<IDockerHealthService> mockDockerHealth = new();
        Mock<IDatabaseHealthService> mockDatabaseHealth = new();

        mockDockerHealth.Setup(d => d.CheckDockerStatusAsync(CancellationToken.None)).ReturnsAsync(new DockerStatus
        {
            IsInstalled = true,
            IsRunning = true,
            StatusMessage = "Docker Engine is active."
        });
        mockDatabaseHealth.Setup(d =>
                d.CheckDatabaseHealthAsync(It.IsAny<ISecretsManagerService?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(
                new DatabaseStatus
                {
                    DatabaseType = "SQL Server 2022",
                    Port = 1433,
                    IsConnected = true,
                    StatusMessage = "SQL Server is active on port 1433."
                });
        mockDatabaseHealth.Setup(d => d.CheckRedisHealthAsync(CancellationToken.None)).ReturnsAsync(new DatabaseStatus
        {
            DatabaseType = "Redis Distributed Cache",
            Port = 21631,
            IsConnected = true,
            StatusMessage = "Redis Cache ready."
        });

        DashboardViewModel vm = new(mockOrchestrator.Object, mockDockerHealth.Object, mockDatabaseHealth.Object);

        // Act
        await vm.RefreshInfrastructureHealthAsync();

        // Assert
        Assert.Equal("🗄️ SQL Server:", vm.DatabaseLabel);
        Assert.True(vm.IsDatabaseOnline);
    }

    [Fact]
    public async Task RefreshSecretsLinks_WithConfiguredSecrets_PopulatesDashboardUrlsAndFlags()
    {
        // Arrange
        Mock<INexusOrchestratorService> mockOrchestrator = new();
        Mock<IDockerHealthService> mockDockerHealth = new();
        Mock<IDatabaseHealthService> mockDatabaseHealth = new();
        Mock<ISecretsManagerService> mockSecretsManager = new();

        NexusSecrets secrets = new()
        {
            SupabaseUrl = "https://abcdefghijklmnop.supabase.co",
            GrafanaOtlpEndpoint = "https://otlp-gateway-prod-us-east-0.grafana.net/otlp",
            PyroscopeServerAddress = "https://profiles-prod-us-east-0.grafana.net",
            DiscordClientId = "1003760104199422052",
            TwitchClientId = "twitch_app_client_123"
        };

        mockSecretsManager.Setup(s => s.LoadSecretsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(secrets);

        DashboardViewModel vm = new(mockOrchestrator.Object, mockDockerHealth.Object, mockDatabaseHealth.Object,
            mockSecretsManager.Object);

        // Act
        await vm.RefreshSecretsLinksAsync();

        // Assert
        Assert.True(vm.HasSupabaseConfigured);
        Assert.Equal("https://supabase.com/dashboard/project/abcdefghijklmnop", vm.SupabaseDashboardUrl);

        Assert.True(vm.HasGrafanaConfigured);
        Assert.Equal("https://grafana.com", vm.GrafanaDashboardUrl);

        Assert.True(vm.HasPyroscopeConfigured);
        Assert.Equal("https://grafana.com", vm.PyroscopeDashboardUrl);

        Assert.True(vm.HasDiscordConfigured);
        Assert.Equal("https://discord.com/developers/applications/1003760104199422052", vm.DiscordDevPortalUrl);

        Assert.True(vm.HasTwitchConfigured);
        Assert.Equal("https://dev.twitch.tv/console/apps/twitch_app_client_123", vm.TwitchDevConsoleUrl);
    }

    [Fact]
    public async Task RefreshSecretsLinks_WithEmptySecrets_FlagsRemainFalse()
    {
        // Arrange
        Mock<INexusOrchestratorService> mockOrchestrator = new();
        Mock<IDockerHealthService> mockDockerHealth = new();
        Mock<IDatabaseHealthService> mockDatabaseHealth = new();
        Mock<ISecretsManagerService> mockSecretsManager = new();

        NexusSecrets secrets = new(); // All empty

        mockSecretsManager.Setup(s => s.LoadSecretsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(secrets);

        DashboardViewModel vm = new(mockOrchestrator.Object, mockDockerHealth.Object, mockDatabaseHealth.Object,
            mockSecretsManager.Object);

        // Act
        await vm.RefreshSecretsLinksAsync();

        // Assert
        Assert.False(vm.HasSupabaseConfigured);
        Assert.False(vm.HasGrafanaConfigured);
        Assert.False(vm.HasPyroscopeConfigured);
        Assert.False(vm.HasDiscordConfigured);
        Assert.False(vm.HasTwitchConfigured);
    }

    [Fact]
    public async Task DashboardViewModel_QueueArcadeCommands_LaunchToggleAndStopCleanly()
    {
        // protects: Dashboard '🕹️ Queue Arcade ▾' menu commands (Local launch, Live Pause/Resume toggle with Redis offline warning, and isolated Stop).
        // fails_when: Queue Arcade menu items fail to invoke orchestrator methods or fail to update toggle header/warning state.
        // seam: Real DashboardViewModel commands bound to INexusOrchestratorService.
        Mock<INexusOrchestratorService> mockOrchestrator = new();
        Mock<IDockerHealthService> mockDockerHealth = new();
        Mock<IDatabaseHealthService> mockDatabaseHealth = new();
        mockOrchestrator.SetupGet(o => o.CurrentClusterState).Returns(ServerState.Running);
        mockOrchestrator
            .Setup(o => o.GetQueueArcadeStateAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync((true, true, "Enabled (Live)"));
        mockOrchestrator
            .Setup(o => o.SetQueueArcadeEnabledAsync(false, It.IsAny<CancellationToken>()))
            .ReturnsAsync((false, false, "Saved to Vault, but Redis cache is offline"));

        bool navigatedToServers = false;
        DashboardViewModel vm = new(
            mockOrchestrator.Object,
            mockDockerHealth.Object,
            mockDatabaseHealth.Object,
            onNavigateToGameServers: () => navigatedToServers = true);

        Assert.Contains("Enabled", vm.QueueArcadeToggleMenuHeader);

        await vm.LaunchLocalQueueArcadeAsync();
        mockOrchestrator.Verify(o => o.LaunchQueueArcadeServerAsync(null, CancellationToken.None), Times.Once);
        Assert.True(navigatedToServers);

        await vm.ToggleQueueArcadeEnabledAsync();
        Assert.False(vm.IsQueueArcadeEnabled);
        Assert.Contains("Paused", vm.QueueArcadeToggleMenuHeader);
        Assert.Equal("#F59E0B", vm.ClusterStatusColor);

        await vm.StopQueueArcadeServersAsync();
        mockOrchestrator.Verify(o => o.StopQueueArcadeServersAsync(CancellationToken.None), Times.Once);
    }
}
