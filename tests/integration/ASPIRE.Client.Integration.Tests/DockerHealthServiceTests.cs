// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

using System.Diagnostics;
using Docker.DotNet;
using Docker.DotNet.Models;
using FluentAssertions;
using Moq;
using NEXUS.ControlPanel.GUI.Services;
using NEXUS.ControlPanel.GUI.Services.Models;
using Xunit;

namespace ASPIRE.Client.Integration.Tests;

public sealed class DockerHealthServiceTests
{
    [Fact]
    public void BuildCreateContainerParameters_ProducesExpectedConfig()
    {
        // Arrange
        var secrets = new NexusSecrets
        {
            HostAccountUserName = "testuser",
            HostAccountPassword = "secretpassword",
            CompelAuthenticationToken = "authtoken123",
            CompelPriorityMode = "Normal",
            CompelInstances = 3,
            CompelLocation = "EUW",
            CompelServerNamePrefix = "TEST SERVER",
            CompelPortRangeOffset = 200
        };

        // Act
        var parameters = ContainerLaunchCommandBuilder.BuildCreateContainerParameters(
            secrets,
            priorityMode: "Normal",
            instances: 3,
            serverPath: "C:\\hon\\server",
            artefactsPath: "C:\\hon\\artefacts",
            isWindowsHost: true);

        // Assert
        parameters.Should().NotBeNull();
        parameters.Name.Should().Be("ranked-compel-server");
        parameters.Image.Should().Be(ContainerLaunchCommandBuilder.DefaultDockerImage);

        // Verify Environment variables
        parameters.Env.Should().Contain("COMPEL_PRIORITY_MODE=Normal");
        parameters.Env.Should().Contain("COMPEL_INSTANCES=3");
        parameters.Env.Should().Contain("COMPEL_LOCATION=EUW");
        parameters.Env.Should().Contain("HOST_USERNAME=testuser");
        parameters.Env.Should().Contain("HOST_PASSWORD=secretpassword");
        parameters.Env.Should().Contain("COMPEL_AUTHENTICATION_TOKEN=authtoken123");
        parameters.Env.Should().Contain("MASTER_SERVER_ADDRESS=http://host.docker.internal:55555");
        parameters.Env.Should().Contain("COMPEL_GATEWAY=127.0.0.1");

        // Verify HostConfig
        parameters.HostConfig.Should().NotBeNull();
        parameters.HostConfig.RestartPolicy.Name.Should().Be(RestartPolicyKind.UnlessStopped);
        parameters.HostConfig.CapAdd.Should().Contain("SYS_NICE");
        parameters.HostConfig.Binds.Should().Contain("C:/hon/server:/app/server:ro");
        parameters.HostConfig.Binds.Should().Contain("C:/hon/artefacts:/app/artefacts:rw");
        parameters.HostConfig.Binds.Should().Contain("kongor-ranked-runtime:/app/run-server:rw");
        parameters.HostConfig.PortBindings.Should().NotBeEmpty();
    }

    [Fact]
    public async Task IsImageBuiltAsync_WhenFound_ReturnsTrue()
    {
        // Arrange
        var mockClient = new Mock<IDockerClient>();
        var mockImages = new Mock<IImageOperations>();
        mockClient.Setup(c => c.Images).Returns(mockImages.Object);

        mockImages
            .Setup(i => i.InspectImageAsync("projectkongor/compel-dedicated:latest", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ImageInspectResponse { ID = "sha256:abc123456789def0" });

        using var service = new DockerHealthService(mockClient.Object);

        // Act
        bool result = await service.IsImageBuiltAsync("projectkongor/compel-dedicated:latest");

        // Assert
        result.Should().BeTrue();
    }

    [Fact]
    public async Task IsImageBuiltAsync_WhenNotFound_ReturnsFalse()
    {
        // Arrange
        var mockClient = new Mock<IDockerClient>();
        var mockImages = new Mock<IImageOperations>();
        mockClient.Setup(c => c.Images).Returns(mockImages.Object);

        mockImages
            .Setup(i => i.InspectImageAsync("nonexistent:latest", It.IsAny<CancellationToken>()))
            .ThrowsAsync(new DockerImageNotFoundException(System.Net.HttpStatusCode.NotFound, "Image not found"));

        using var service = new DockerHealthService(mockClient.Object);

        // Act
        bool result = await service.IsImageBuiltAsync("nonexistent:latest");

        // Assert
        result.Should().BeFalse();
    }

    [Fact]
    public async Task StopContainerAsync_WhenContainerExists_StopsSuccessfully()
    {
        // Arrange
        var mockClient = new Mock<IDockerClient>();
        var mockContainers = new Mock<IContainerOperations>();
        mockClient.Setup(c => c.Containers).Returns(mockContainers.Object);

        mockContainers
            .Setup(c => c.StopContainerAsync("compel-dedicated-server", It.IsAny<ContainerStopParameters>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);

        using var service = new DockerHealthService(mockClient.Object);

        // Act
        var (success, output) = await service.StopContainerAsync("compel-dedicated-server");

        // Assert
        success.Should().BeTrue();
        output.Should().Contain("stopped");
    }

    [Fact]
    public async Task StopContainerAsync_WhenContainerNotFound_ReturnsTrueWithGracefulMessage()
    {
        // Arrange
        var mockClient = new Mock<IDockerClient>();
        var mockContainers = new Mock<IContainerOperations>();
        mockClient.Setup(c => c.Containers).Returns(mockContainers.Object);

        mockContainers
            .Setup(c => c.StopContainerAsync("compel-dedicated-server", It.IsAny<ContainerStopParameters>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new DockerContainerNotFoundException(System.Net.HttpStatusCode.NotFound, "Not found"));

        using var service = new DockerHealthService(mockClient.Object);

        // Act
        var (success, output) = await service.StopContainerAsync("compel-dedicated-server");

        // Assert
        success.Should().BeTrue();
        output.Should().Contain("not found");
    }

    [Fact]
    public async Task LaunchContainerAsync_WithTypedParameters_CreatesAndStartsContainer()
    {
        // Arrange
        var mockClient = new Mock<IDockerClient>();
        var mockContainers = new Mock<IContainerOperations>();
        mockClient.Setup(c => c.Containers).Returns(mockContainers.Object);

        mockContainers
            .Setup(c => c.RemoveContainerAsync("compel-dedicated-server", It.IsAny<ContainerRemoveParameters>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        mockContainers
            .Setup(c => c.CreateContainerAsync(It.IsAny<CreateContainerParameters>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new CreateContainerResponse { ID = "1234567890abcdef" });

        mockContainers
            .Setup(c => c.StartContainerAsync("1234567890abcdef", It.IsAny<ContainerStartParameters>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);

        using var service = new DockerHealthService(mockClient.Object);
        var parameters = new CreateContainerParameters
        {
            Name = "compel-dedicated-server",
            Image = "projectkongor/compel-dedicated:latest"
        };

        // Act
        var (success, output) = await service.LaunchContainerAsync(parameters);

        // Assert
        success.Should().BeTrue();
        output.Should().Contain("1234567890ab");
        mockContainers.Verify(c => c.StartContainerAsync("1234567890abcdef", It.IsAny<ContainerStartParameters>(), It.IsAny<CancellationToken>()), Times.Once);
    }
}
