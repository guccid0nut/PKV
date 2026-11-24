// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

using NEXUS.ControlPanel.GUI.Services;
using NEXUS.ControlPanel.GUI.Services.Models;

namespace ASPIRE.Tests.NEXUS.ControlPanel.GUI;

public sealed class ContainerLaunchCommandBuilderTests
{
    [Fact]
    public void BuildCommand_Docker_WithMaskedSecrets_MasksSensitiveValues()
    {
        // Arrange
        NexusSecrets secrets = new()
        {
            HostAccountUserName = "testoperator",
            HostAccountPassword = "SecretPassword123!",
            CompelAuthenticationToken = "compel-token-xyz-789",
            CompelPriorityMode = "HighPriority",
            CompelInstances = 4
        };

        // Act
        string command = ContainerLaunchCommandBuilder.BuildCommand(
            secrets,
            ContainerPlatform.Docker,
            maskSecrets: true,
            isWindowsHost: true);

        // Assert
        Assert.Contains("docker run -d", command);
        Assert.Contains("--name ranked-compel-server", command);
        Assert.Contains("-v kongor-ranked-runtime:/app/run-server:rw", command);
        Assert.Contains("--cap-add=SYS_NICE", command);
        Assert.Contains("-e COMPEL_PRIORITY_MODE=HighPriority", command);
        Assert.Contains("-e COMPEL_INSTANCES=4", command);
        Assert.Contains("-e HOST_USERNAME=\"testoperator\"", command);
        Assert.Contains("-e HOST_PASSWORD=\"••••••••••••\"", command);
        Assert.Contains("-e COMPEL_AUTHENTICATION_TOKEN=\"••••••••••••\"", command);
        Assert.DoesNotContain("SecretPassword123!", command);
        Assert.DoesNotContain("compel-token-xyz-789", command);
        Assert.Contains("${PWD}/games/server:/app/server:ro", command);
    }

    [Fact]
    public void BuildCommand_Docker_WithUnmaskedSecrets_IncludesActualValues()
    {
        // Arrange
        NexusSecrets secrets = new()
        {
            HostAccountUserName = "prodhost",
            HostAccountPassword = "RealProductionPassword456!",
            CompelAuthenticationToken = "real-auth-token-abc-123",
            CompelPriorityMode = "Background",
            CompelInstances = 2
        };

        // Act
        string command = ContainerLaunchCommandBuilder.BuildCommand(
            secrets,
            ContainerPlatform.Docker,
            maskSecrets: false,
            isWindowsHost: true);

        // Assert
        Assert.Contains("-e HOST_USERNAME=\"prodhost\"", command);
        Assert.Contains("-e HOST_PASSWORD=\"RealProductionPassword456!\"", command);
        Assert.Contains("-e COMPEL_AUTHENTICATION_TOKEN=\"real-auth-token-abc-123\"", command);
        Assert.Contains("-e COMPEL_PRIORITY_MODE=Background", command);
        Assert.Contains("-e COMPEL_INSTANCES=2", command);
        Assert.DoesNotContain("••••••••••••", command);
    }

    [Fact]
    public void BuildCommand_Minikube_GeneratesImageLoadAndKubectlRun()
    {
        // Arrange
        NexusSecrets secrets = new()
        {
            HostAccountUserName = "minikubeuser",
            HostAccountPassword = "minikubepassword",
            CompelAuthenticationToken = "minikube-token",
            CompelPriorityMode = "Adaptive",
            CompelInstances = 3
        };

        // Act
        string command = ContainerLaunchCommandBuilder.BuildCommand(
            secrets,
            ContainerPlatform.Minikube,
            maskSecrets: false);

        // Assert
        Assert.Contains("minikube image load projectkongor/compel-dedicated:latest", command);
        Assert.Contains("kubectl run compel-minikube", command);
        Assert.Contains("--image=projectkongor/compel-dedicated:latest", command);
        Assert.Contains("--image-pull-policy=Never", command);
        Assert.Contains("--env=\"COMPEL_PRIORITY_MODE=Adaptive\"", command);
        Assert.Contains("--env=\"COMPEL_INSTANCES=3\"", command);
        Assert.Contains("--env=\"HOST_USERNAME=minikubeuser\"", command);
        Assert.Contains("--env=\"HOST_PASSWORD=minikubepassword\"", command);
        Assert.Contains("--env=\"COMPEL_AUTHENTICATION_TOKEN=minikube-token\"", command);
    }

    [Fact]
    public void BuildCommand_Kubernetes_GeneratesSecretAndAgonesFleet()
    {
        // Arrange
        NexusSecrets secrets = new()
        {
            HostAccountUserName = "k8sadmin",
            HostAccountPassword = "k8spassword",
            CompelAuthenticationToken = "k8s-secret-token",
            CompelPriorityMode = "HighPriority"
        };

        // Act
        string command = ContainerLaunchCommandBuilder.BuildCommand(
            secrets,
            ContainerPlatform.Kubernetes,
            maskSecrets: false);

        // Assert
        Assert.Contains("kubectl create secret generic kongor-host-credentials", command);
        Assert.Contains("--from-literal=username=\"k8sadmin\"", command);
        Assert.Contains("--from-literal=password=\"k8spassword\"", command);
        Assert.Contains("--from-literal=token=\"k8s-secret-token\"", command);
        Assert.Contains("kubectl apply -f infra/k8s/agones/fleet.yaml", command);
    }

    [Fact]
    public void BuildCommand_Docker_VolumeMounts_FormatsWindowsVsLinuxCorrectly()
    {
        // Arrange
        NexusSecrets secrets = new();

        // Act
        string windowsCmd = ContainerLaunchCommandBuilder.BuildCommand(
            secrets,
            ContainerPlatform.Docker,
            maskSecrets: true,
            isWindowsHost: true);

        string linuxCmd = ContainerLaunchCommandBuilder.BuildCommand(
            secrets,
            ContainerPlatform.Docker,
            maskSecrets: true,
            isWindowsHost: false);

        // Assert
        Assert.Contains("\"${PWD}/games/server:/app/server:ro\"", windowsCmd);
        Assert.Contains("\"${PWD}/artefacts:/app/artefacts:rw\"", windowsCmd);

        Assert.Contains("/opt/hon/server:/app/server:ro", linuxCmd);
        Assert.Contains("./artefacts:/app/artefacts:rw", linuxCmd);
    }

    [Fact]
    public void BuildCommand_Docker_ConfiguredHost_PreservesHostUsernameAndAppliesDefaultDockerOffset()
    {
        // Arrange - custom operator host account and default offset 0
        NexusSecrets secrets = new()
        {
            HostAccountUserName = "custom_operator",
            CompelServerNamePrefix = "CUSTOM ARENA",
            CompelPortRangeOffset = 0
        };

        // Act
        string command = ContainerLaunchCommandBuilder.BuildCommand(
            secrets,
            ContainerPlatform.Docker,
            maskSecrets: true,
            isWindowsHost: true);

        // Assert: Preserves configured host username, defaults Docker offset to 100 and control port to 11334
        Assert.Contains("-e HOST_USERNAME=\"custom_operator\"", command);
        Assert.Contains("-e COMPEL_PORT_RANGE_OFFSET=100", command);
        Assert.Contains("-e COMPEL_CONTROL_PLANE_PORT=11334", command);
        Assert.Contains("-p 11334:11334", command);
        Assert.Contains("-p 11335-11336:11335-11336/udp", command);
        Assert.Contains("-p 11835-11836:11835-11836/udp", command);
        Assert.Contains("-p 21334-21336:21334-21336/udp", command);
        Assert.Contains("-p 21835-21836:21835-21836/udp", command);
        Assert.Contains("-e COMPEL_SERVER_NAME_PREFIX=\"CUSTOM ARENA\"", command);
    }

    [Fact]
    public void BuildCommand_Kubernetes_ConfiguredHost_PreservesHostUsername()
    {
        // Arrange - custom operator host account
        NexusSecrets secrets = new()
        {
            HostAccountUserName = "k8s_operator"
        };

        // Act
        string command = ContainerLaunchCommandBuilder.BuildCommand(
            secrets,
            ContainerPlatform.Kubernetes,
            maskSecrets: true);

        // Assert: Preserves configured host username and references turnkey K3s manifest
        Assert.Contains("--from-literal=username=\"k8s_operator\"", command);
        Assert.Contains("infra/k8s/compel-k3s.yaml", command);
    }

    [Fact]
    public void BuildDockerRunArguments_ConfiguredHost_ProducesIsolatedOffsetAndConfiguredUser()
    {
        // Arrange
        NexusSecrets secrets = new()
        {
            HostAccountUserName = "docker_host_user"
        };

        // Act
        string rawArgs = ContainerLaunchCommandBuilder.BuildDockerRunArguments(
            secrets,
            priorityMode: "Background",
            instances: 2,
            serverVol: "\"/app/server:ro\"",
            artefactsVol: "\"/app/artefacts:rw\"",
            isWindowsHost: true);

        // Assert
        Assert.Contains("-e HOST_USERNAME=\"docker_host_user\"", rawArgs);
        Assert.Contains("-e COMPEL_FLEET_TIER=D", rawArgs);
        Assert.Contains("-e Compel__FleetTier=D", rawArgs);
        Assert.Contains("-e COMPEL_PORT_RANGE_OFFSET=100", rawArgs);
        Assert.Contains("-e COMPEL_CONTROL_PLANE_PORT=11334", rawArgs);
        Assert.Contains("-p 11334:11334", rawArgs);
        Assert.Contains("-p 21835-21836:21835-21836/udp", rawArgs);
    }

    [Fact]
    public void BuildCreateContainerParameters_WhenSecretsHaveK3sOrLocalOffset_ForcesDockerTierDOffset100()
    {
        // Arrange: Even if user secrets were previously saved with offset 0 (Local) or 200 (K3s),
        // Docker Tier D must stay isolated in 100-199 (default 100 / control port 11334).
        NexusSecrets secrets = new()
        {
            HostAccountUserName = "multi_fleet_host",
            CompelPortRangeOffset = 200,
            CompelControlPlanePort = 11434
        };

        // Act
        var createParams = ContainerLaunchCommandBuilder.BuildCreateContainerParameters(
            secrets,
            priorityMode: "Background",
            instances: 2,
            serverPath: "C:/repo/games/server",
            artefactsPath: "C:/repo/artefacts",
            isWindowsHost: true);

        // Assert
        Assert.Contains("COMPEL_FLEET_TIER=D", createParams.Env);
        Assert.Contains("Compel__FleetTier=D", createParams.Env);
        Assert.Contains("COMPEL_PORT_RANGE_OFFSET=100", createParams.Env);
        Assert.Contains("Compel__PortRangeOffset=100", createParams.Env);
        Assert.Contains("COMPEL_CONTROL_PLANE_PORT=11334", createParams.Env);
        Assert.Contains("Compel__ControlPlanePort=11334", createParams.Env);
        Assert.Contains("Compel__MasterServerAddress=host.docker.internal:55555", createParams.Env);
        Assert.True(createParams.ExposedPorts.ContainsKey("11334/tcp"));
        Assert.True(createParams.ExposedPorts.ContainsKey("11335/udp"));
        Assert.True(createParams.ExposedPorts.ContainsKey("11336/udp"));
    }

    [Theory]
    [InlineData("guccihost")]
    [InlineData("guccihost_local")]
    public void ResolveFleetHostUsername_DefaultLocalHost_IsolatesDockerAndK3sAccounts(string localHostName)
    {
        NexusSecrets secrets = new() { HostAccountUserName = localHostName };

        string dockerCmd = ContainerLaunchCommandBuilder.BuildCommand(secrets, ContainerPlatform.Docker, maskSecrets: true);
        string k3sCmd = ContainerLaunchCommandBuilder.BuildCommand(secrets, ContainerPlatform.K3sAgones, maskSecrets: true);

        Assert.Contains("-e HOST_USERNAME=\"guccihost_dock\"", dockerCmd);
        Assert.Contains("--from-literal=username=\"guccihost_k3s\"", k3sCmd);
    }

    [Theory]
    [InlineData("guccidonut", "guccihost_arc")]
    [InlineData("guccihost", "guccihost_arc")]
    [InlineData("guccihost_local", "guccihost_arc")]
    [InlineData("kongor_host", "kongor_host_arc")]
    [InlineData("alicehost", "alicehost_arc")]
    [InlineData("community_host", "community_h_arc")]
    [InlineData("community_h_arc", "community_h_arc")]
    public void ResolveQueueArcadeHostUsername_MapsPrimaryHostToDedicatedArcadeHostAccount(
        string configuredHost,
        string expectedArcadeHost)
    {
        // protects: Automatic derivation of isolated <host>_arc ServerHost accounts clamped to <= 15 chars (HoN/PostgreSQL VARCHAR(15)).
        // fails_when: Queue Arcade reuses the Ranked HostAccountUserName or produces > 15 char account names rejected by PostgreSQL 22001.
        // seam: ContainerLaunchCommandBuilder.ResolveQueueArcadeHostUsername + EnsureDistinctHostAccounts.
        string resolved = ContainerLaunchCommandBuilder.ResolveQueueArcadeHostUsername(configuredHost);
        Assert.Equal(expectedArcadeHost, resolved);
        Assert.True(resolved.Length <= 15, $"Derived Queue Arcade username '{resolved}' ({resolved.Length} chars) exceeds VARCHAR(15) limit.");

        if (!string.Equals(configuredHost, expectedArcadeHost, StringComparison.OrdinalIgnoreCase))
        {
            ContainerLaunchCommandBuilder.EnsureDistinctHostAccounts(configuredHost, resolved);
        }
    }

    [Fact]
    public void EnsureDistinctHostAccounts_WhenRankedAndArcadeShareSameAccount_ThrowsActionableError()
    {
        // protects: Hard pre-launch guard preventing Ranked and Queue Arcade COMPEL fleets from sharing a single ServerHost username.
        // fails_when: Identical host accounts launch side-by-side and kick each other's session cookies off MasterServer.
        // seam: ContainerLaunchCommandBuilder.EnsureDistinctHostAccounts.
        InvalidOperationException ex = Assert.Throws<InvalidOperationException>(() =>
            ContainerLaunchCommandBuilder.EnsureDistinctHostAccounts("guccihost_arc", "guccihost_arc"));
        Assert.Contains("Queue Arcade cannot share the same ServerHost account", ex.Message);
    }

    [Fact]
    public void BuildCreateContainerParameters_WhenQueueArcadeModeTrue_UsesContainerOffset250TierQAndArcadeAccount()
    {
        // protects: Docker Queue Arcade isolation (container 'compel-queue-arcade-server', offset 250, port 11484, Tier Q, WarmInstancesTarget=1).
        // fails_when: Launching Docker Queue Arcade overwrites 'compel-dedicated-server' or binds Ranked ports 11334/11234.
        // seam: ContainerLaunchCommandBuilder.BuildCreateContainerParameters(queueArcadeMode: true).
        NexusSecrets secrets = new()
        {
            HostAccountUserName = "guccidonut",
            QueueArcadeInstances = 2
        };

        Docker.DotNet.Models.CreateContainerParameters createParams =
            ContainerLaunchCommandBuilder.BuildCreateContainerParameters(
                secrets,
                priorityMode: "Background",
                instances: 2,
                serverPath: "C:/repo/games/server",
                artefactsPath: "C:/repo/artefacts",
                isWindowsHost: true,
                queueArcadeMode: true);

        Assert.Equal("arcade-compel-server", createParams.Name);
        Assert.Contains("kongor-arcade-runtime:/app/run-server:rw", createParams.HostConfig.Binds);
        Assert.Contains("HOST_USERNAME=guccihost_arc", createParams.Env);
        Assert.Contains("COMPEL_FLEET_TIER=Q", createParams.Env);
        Assert.Contains("COMPEL_PORT_RANGE_OFFSET=250", createParams.Env);
        Assert.Contains("COMPEL_CONTROL_PLANE_PORT=11484", createParams.Env);
        Assert.Contains("COMPEL_QUEUE_ARCADE_MODE=true", createParams.Env);
        Assert.Contains("Compel__WarmInstancesTarget=1", createParams.Env);
        Assert.True(createParams.ExposedPorts.ContainsKey("11484/tcp"));
        Assert.True(createParams.ExposedPorts.ContainsKey("11485/udp"));
        Assert.True(createParams.ExposedPorts.ContainsKey("11486/udp"));
    }
}
