// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

using Moq;
using NEXUS.ControlPanel.GUI.Services;
using NEXUS.ControlPanel.GUI.Services.Models;
using NEXUS.ControlPanel.GUI.ViewModels;

namespace ASPIRE.Tests.NEXUS.ControlPanel.GUI;

public sealed class ServiceItemViewModelTests
{
    [Fact]
    public void ServiceItemViewModel_WhenExecuted_UpdatesStateAndColorsCorrectly()
    {
        // Arrange
        Mock<INexusOrchestratorService> mockOrchestrator = new();
        mockOrchestrator.Setup(o => o.GetServiceState("master-server")).Returns(ServerState.Stopped);

        ServiceItemViewModel item = new(
            mockOrchestrator.Object,
            "master-server",
            "👑 KONGOR Master Server",
            "👑",
            "HTTP: 55555 / 80",
            "SRP Auth, Store, Vault, Stats",
            "#F59E0B");

        // Assert Initial Stopped State
        Assert.Equal(ServerState.Stopped, item.State);
        Assert.Equal("STOPPED", item.StatusText);
        Assert.True(item.CanStart);
        Assert.False(item.CanStop);
        Assert.False(item.CanRestart);

        // Act 1: Transition to Starting
        item.UpdateState(ServerState.Starting);
        Assert.Equal(ServerState.Starting, item.State);
        Assert.Equal("STARTING...", item.StatusText);
        Assert.Equal("#F59E0B", item.StatusColor);
        Assert.False(item.CanStart);
        Assert.True(item.CanStop);
        Assert.False(item.CanRestart);

        // Act 2: Transition to Running
        item.UpdateState(ServerState.Running);
        Assert.Equal(ServerState.Running, item.State);
        Assert.Equal("RUNNING", item.StatusText);
        Assert.Equal("#10B981", item.StatusColor);
        Assert.False(item.CanStart);
        Assert.True(item.CanStop);
        Assert.True(item.CanRestart);

        // Act 3: Transition to Faulted
        item.UpdateState(ServerState.Faulted);
        Assert.Equal(ServerState.Faulted, item.State);
        Assert.Equal("FAULTED", item.StatusText);
        Assert.Equal("#EF4444", item.StatusColor);
        Assert.True(item.CanStart);
        Assert.False(item.CanStop);
        Assert.False(item.CanRestart);
    }
}
