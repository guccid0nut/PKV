// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

using System.Net;
using System.Net.Sockets;
using NEXUS.ControlPanel.GUI.Services;
using NEXUS.ControlPanel.GUI.Services.Models;

namespace ASPIRE.Tests.NEXUS.ControlPanel.GUI;

public sealed class PortScannerServiceTests
{
    [Fact]
    public async Task PortScannerService_ScanRequiredPortsAsync_ReturnsAllElevenRequiredPorts()
    {
        // Arrange
        PortScannerService service = new();

        // Act
        List<PortStatus> statuses = await service.ScanRequiredPortsAsync();

        // Assert
        Assert.NotNull(statuses);
        Assert.Equal(18, statuses.Count);
        Assert.Contains(statuses, p => p.Port == 5550);
        Assert.Contains(statuses, p => p.Port == 5551);
        Assert.Contains(statuses, p => p.Port == 55555);
        Assert.Contains(statuses, p => p.Port == 11031);
        Assert.Contains(statuses, p => p.Port == 11032);
        Assert.Contains(statuses, p => p.Port == 11033);
        Assert.Contains(statuses, p => p.Port == 5201);
        Assert.Contains(statuses, p => p.Port == 5557);
        Assert.Contains(statuses, p => p.Port == 1433);
        Assert.Contains(statuses, p => p.Port == 6379);
        Assert.Contains(statuses, p => p.Port == 11234);
        Assert.Contains(statuses, p => p.Port == 11235);
        Assert.Contains(statuses, p => p.Port == 11334);
        Assert.Contains(statuses, p => p.Port == 11335);
        Assert.Contains(statuses, p => p.Port == 11434);
        Assert.Contains(statuses, p => p.Port == 11435);
        Assert.Contains(statuses, p => p.Port == 11399);
        Assert.Contains(statuses, p => p.Port == 11255);
    }

    [Fact]
    public void IsPortAvailable_WhenPortBound_ReturnsFalse()
    {
        // Arrange
        PortScannerService service = new();
        TcpListener listener = new(IPAddress.Loopback, 0);
        listener.Start();
        int dynamicPort = ((IPEndPoint)listener.LocalEndpoint).Port;

        try
        {
            // Act
            bool isAvailable = service.IsPortAvailable(dynamicPort);

            // Assert
            Assert.False(isAvailable);
        }
        finally
        {
            listener.Stop();
        }

        // Act after release
        bool availableAfterStop = service.IsPortAvailable(dynamicPort);
        Assert.True(availableAfterStop);
    }
}
