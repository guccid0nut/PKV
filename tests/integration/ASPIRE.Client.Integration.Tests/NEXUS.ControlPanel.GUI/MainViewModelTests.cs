// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

using Moq;
using NEXUS.ControlPanel.GUI.Controls;
using NEXUS.ControlPanel.GUI.Services;
using NEXUS.ControlPanel.GUI.Services.Models;
using NEXUS.ControlPanel.GUI.ViewModels;
using Xunit;

namespace ASPIRE.Tests.NEXUS.ControlPanel.GUI;

public sealed class MainViewModelTests
{
    [Fact]
    public void MainViewModel_Navigation_IndexesZeroToFive_RouteToCorrectPages()
    {
        // Arrange
        Mock<INexusOrchestratorService> mockOrchestrator = new();
        mockOrchestrator.SetupGet(o => o.LogHistory).Returns(Array.Empty<LogMessage>());
        Mock<ISecretsManagerService> mockSecrets = new();
        Mock<IDockerHealthService> mockDockerHealth = new();
        Mock<IPortScannerService> mockPortScanner = new();

        MainViewModel vm = new(
            mockOrchestrator.Object,
            mockSecrets.Object,
            mockDockerHealth.Object,
            mockPortScanner.Object);

        // Act & Assert
        vm.SelectedNavigationIndex = 0;
        Assert.Same(vm.Dashboard, vm.CurrentPage);

        vm.SelectedNavigationIndex = 1;
        Assert.Same(vm.Users, vm.CurrentPage);

        vm.SelectedNavigationIndex = 2;
        Assert.Same(vm.SecretsConfig, vm.CurrentPage);

        vm.SelectedNavigationIndex = 3;
        Assert.Same(vm.LiveLogs, vm.CurrentPage);

        vm.SelectedNavigationIndex = 4;
        Assert.Same(vm.Diagnostics, vm.CurrentPage);

        vm.SelectedNavigationIndex = 5;
        Assert.Same(vm.GameServers, vm.CurrentPage);
    }

    [Fact]
    public void MainViewModel_Navigation_IndexSixOrUnknown_FallsBackToDashboard_BecauseReplayStudioIsRemoved()
    {
        // Arrange
        Mock<INexusOrchestratorService> mockOrchestrator = new();
        mockOrchestrator.SetupGet(o => o.LogHistory).Returns(Array.Empty<LogMessage>());
        Mock<ISecretsManagerService> mockSecrets = new();
        Mock<IDockerHealthService> mockDockerHealth = new();
        Mock<IPortScannerService> mockPortScanner = new();

        MainViewModel vm = new(
            mockOrchestrator.Object,
            mockSecrets.Object,
            mockDockerHealth.Object,
            mockPortScanner.Object);

        // Act: Navigate to Moderation Queue (index 6) and unknown index (index 7)
        vm.SelectedNavigationIndex = 6;
        Assert.Same(vm.Moderation, vm.CurrentPage);

        vm.SelectedNavigationIndex = 7;

        // Assert: Unknown indices safely fall back to Dashboard, never legacy Replay Studio
        Assert.Same(vm.Dashboard, vm.CurrentPage);
    }

    [Fact]
    public void Kongor3DViewportControl_HeadlessTestEnvironment_InitializesSafely()
    {
        // In automated test environments, Kongor3DViewportControl should initialize
        // into MinimalistCrest fallback mode without attempting to allocate Win32 HWND/WebView2
        Kongor3DViewportControl viewport = new();
        Assert.True(viewport.IsViewportReady);
        Assert.True(viewport.IsWebViewInitialized);
        Assert.Equal(ViewportDisplayMode.MinimalistCrest, viewport.DisplayMode);
    }
}
