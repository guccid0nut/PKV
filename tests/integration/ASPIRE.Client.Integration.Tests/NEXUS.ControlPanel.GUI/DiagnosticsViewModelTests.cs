// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

using Moq;
using NEXUS.ControlPanel.GUI.Services;
using NEXUS.ControlPanel.GUI.Services.Models;
using NEXUS.ControlPanel.GUI.ViewModels;

namespace ASPIRE.Tests.NEXUS.ControlPanel.GUI;

public sealed class DiagnosticsViewModelTests
{
    [Fact]
    public async Task DiagnosticsViewModel_RunDiagnostics_ChecksDockerAndPyroscopeHealth()
    {
        // Arrange
        Mock<IPortScannerService> mockPortScanner = new();
        Mock<IDockerHealthService> mockDockerHealth = new();
        Mock<IDatabaseHealthService> mockDatabaseHealth = new();

        mockDockerHealth.Setup(d => d.CheckDockerStatusAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new DockerStatus
            {
                IsInstalled = true,
                IsRunning = true,
                StatusMessage = "Docker Engine is running and ready."
            });

        mockDatabaseHealth.Setup(d => d.CheckPyroscopeHealthAsync(4040, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new DatabaseStatus
            {
                DatabaseType = "Pyroscope Continuous Profiling",
                Port = 4040,
                IsConnected = true,
                StatusMessage = "Pyroscope is active on port 4040 (2ms)."
            });

        mockPortScanner.Setup(p => p.ScanRequiredPortsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(
            [
                new PortStatus { Port = 4040, ServiceName = "Pyroscope Continuous Profiler", IsAvailable = false }
            ]);

        DiagnosticsViewModel vm = new(mockPortScanner.Object, mockDockerHealth.Object, mockDatabaseHealth.Object);

        // Act
        await vm.RunDiagnosticsAsync();

        // Assert
        Assert.True(vm.IsDockerHealthy);
        Assert.Equal("Docker Engine is running and ready.", vm.DockerStatusMessage);
        Assert.True(vm.IsPyroscopeHealthy);
        Assert.Equal("Pyroscope is active on port 4040 (2ms).", vm.PyroscopeStatusMessage);
        Assert.Equal("#10B981", vm.PyroscopeStatusColor);
        Assert.NotEmpty(vm.PortStatuses);
        Assert.Contains(vm.PortStatuses, p => p.Port == 4040);
    }

    [Fact]
    public async Task RunDiagnostics_WithPyroscopeOffline_SetsOfflineStatus()
    {
        // Arrange
        Mock<IPortScannerService> mockPortScanner = new();
        Mock<IDockerHealthService> mockDockerHealth = new();
        Mock<IDatabaseHealthService> mockDatabaseHealth = new();

        mockDockerHealth.Setup(d => d.CheckDockerStatusAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new DockerStatus
            {
                IsInstalled = true,
                IsRunning = false,
                StatusMessage = "Docker Engine is not running."
            });

        mockDatabaseHealth.Setup(d => d.CheckPyroscopeHealthAsync(4040, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new DatabaseStatus
            {
                DatabaseType = "Pyroscope Continuous Profiling",
                Port = 4040,
                IsConnected = false,
                StatusMessage = "Pyroscope is offline."
            });

        mockPortScanner.Setup(p => p.ScanRequiredPortsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync([]);

        DiagnosticsViewModel vm = new(mockPortScanner.Object, mockDockerHealth.Object, mockDatabaseHealth.Object);

        // Act
        await vm.RunDiagnosticsAsync();

        // Assert
        Assert.False(vm.IsDockerHealthy);
        Assert.False(vm.IsPyroscopeHealthy);
        Assert.Equal("Pyroscope is offline.", vm.PyroscopeStatusMessage);
        Assert.Equal("#EF4444", vm.PyroscopeStatusColor);
    }
}
