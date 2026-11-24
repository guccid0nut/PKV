// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

using System;
using System.IO;
using System.Text.Json;
using System.Threading.Tasks;
using FluentAssertions;
using Moq;
using NEXUS.ControlPanel.GUI.Services;
using NEXUS.ControlPanel.GUI.Services.Models;
using NEXUS.ControlPanel.GUI.ViewModels;
using Xunit;

namespace ASPIRE.Client.Gui.Tests.Standalone;

public sealed class DashboardHostingModeTests : IDisposable
{
    private readonly string _tempRoot;
    private readonly string _probeDir;

    public DashboardHostingModeTests()
    {
        _tempRoot = Path.Combine(Path.GetTempPath(), $"kongor_test_dashboard_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempRoot);

        File.WriteAllText(Path.Combine(_tempRoot, "playtest_policy.json"), "{}");
        Directory.CreateDirectory(Path.Combine(_tempRoot, "games", "client"));

        _probeDir = Path.Combine(_tempRoot, "control-panel-win-x64");
        Directory.CreateDirectory(_probeDir);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_tempRoot))
            {
                Directory.Delete(_tempRoot, recursive: true);
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[TestCleanup] Exception deleting tempRoot: {ex.Message}");
        }
    }

    private async Task<(DashboardViewModel vm, SecretsManagerService secretsManager)> CreateViewModelAsync(bool bundled = true)
    {
        var mockOrchestrator = new Mock<INexusOrchestratorService>();
        mockOrchestrator.SetupGet(o => o.CurrentClusterState).Returns(ServerState.Stopped);

        var mockDocker = new Mock<IDockerHealthService>();
        mockDocker.Setup(d => d.CheckDockerStatusAsync())
            .ReturnsAsync(new DockerStatus { IsRunning = false, StatusMessage = "Not running" });

        var mockDb = new Mock<IDatabaseHealthService>();
        mockDb.Setup(d => d.CheckDatabaseHealthAsync(It.IsAny<ISecretsManagerService?>()))
            .ReturnsAsync(new DatabaseStatus { IsConnected = false, StatusMessage = "Offline" });
        mockDb.Setup(d => d.CheckRedisHealthAsync())
            .ReturnsAsync(new DatabaseStatus { IsConnected = false, StatusMessage = "Offline" });
        mockDb.Setup(d => d.CheckPyroscopeHealthAsync())
            .ReturnsAsync(new DatabaseStatus { IsConnected = false, StatusMessage = "Offline" });

        string? probe = bundled ? _probeDir : null;
        var secretsManager = new SecretsManagerService(bundleProbeDirectory: probe);

        var vm = new DashboardViewModel(
            mockOrchestrator.Object,
            mockDocker.Object,
            mockDb.Object,
            secretsManager,
            bundleProbeDirectory: probe);

        await vm.InitialHydrationTask;
        return (vm, secretsManager);
    }

    [Fact]
    public async Task Test1_SelectingPublicInternet_WhenWanWarningNotAcknowledged_OpensDialog_AndCancelReverts()
    {
        var (vm, secretsManager) = await CreateViewModelAsync(bundled: true);

        // 1. Initial mode should be "Local only" and warning unacknowledged
        vm.SelectedHostingMode.Should().Be("Local only");
        vm.WanWarningAcknowledged.Should().BeFalse();
        vm.IsWanWarningDialogOpen.Should().BeFalse();

        // 2. Select "Public Internet"
        vm.SelectedHostingMode = "Public Internet";

        // Assert: Dialog is open and "Wan" was NOT saved to secrets manager
        vm.IsWanWarningDialogOpen.Should().BeTrue();
        NexusSecrets secretsDuringDialog = await secretsManager.LoadSecretsAsync();
        secretsDuringDialog.HostingMode.Should().Be("Local");

        // 3. Click CancelWanWarning()
        vm.CancelWanWarning();

        // Assert: Dialog closed, reverted to previous mode ("Local only"), secrets unchanged
        vm.IsWanWarningDialogOpen.Should().BeFalse();
        vm.SelectedHostingMode.Should().Be("Local only");
        NexusSecrets secretsAfterCancel = await secretsManager.LoadSecretsAsync();
        secretsAfterCancel.HostingMode.Should().Be("Local");
    }

    [Fact]
    public async Task Test2_ConfirmingWanWarning_PersistsWanMode_AndSubsequentSelectionDoesNotReopenDialog()
    {
        var (vm, secretsManager) = await CreateViewModelAsync(bundled: true);

        vm.SelectedHostingMode = "Public Internet";
        vm.IsWanWarningDialogOpen.Should().BeTrue();

        // Act: Click ConfirmWanWarningAsync()
        await vm.ConfirmWanWarningAsync();

        // Assert: Dialog closed, warning acknowledged, mode is Public Internet
        vm.IsWanWarningDialogOpen.Should().BeFalse();
        vm.WanWarningAcknowledged.Should().BeTrue();
        vm.SelectedHostingMode.Should().Be("Public Internet");

        // Assert: Persisted to nexus_secrets.json
        NexusSecrets persisted = await secretsManager.LoadSecretsAsync();
        persisted.HostingMode.Should().Be("Wan");
        persisted.WanWarningAcknowledged.Should().BeTrue();

        // Act: Switch to "Local only" then back to "Public Internet"
        vm.SelectedHostingMode = "Local only";
        vm.IsWanWarningDialogOpen.Should().BeFalse();

        vm.SelectedHostingMode = "Public Internet";

        // Assert: Warning dialog does NOT re-open
        vm.IsWanWarningDialogOpen.Should().BeFalse();
        vm.SelectedHostingMode.Should().Be("Public Internet");
    }

    [Fact]
    public async Task Test3_BuildFriendJoinInstructions_IncludesBuildManifestId_Ports_AndWanPortForwarding()
    {
        // 1. Write build_manifest.json to bundle root
        string manifestPath = Path.Combine(_tempRoot, "build_manifest.json");
        var manifestContent = new { buildId = "v3.1-unified-export-999" };
        File.WriteAllText(manifestPath, JsonSerializer.Serialize(manifestContent));

        var (vm, _) = await CreateViewModelAsync(bundled: true);

        // 2. Select Public Internet and confirm warning
        vm.SelectedHostingMode = "Public Internet";
        await vm.ConfirmWanWarningAsync();
        vm.WanHost = "play.kongor.net";

        // Act
        string instructions = vm.BuildFriendJoinInstructions();

        // Assert
        instructions.Should().Contain("v3.1-unified-export-999");
        instructions.Should().Contain("play.kongor.net:55555");
        instructions.Should().Contain("http://play.kongor.net:5266/Account/Register");
        instructions.Should().Contain("single-use invite code");
        instructions.Should().Contain("userdata/secrets/invite_codes.txt");
        instructions.Should().Contain("TCP 55555, 11031, 5266; UDP 11234 (ping), 11235-11236 (game), 11735-11736 (voice)");
    }

    [Fact]
    public async Task Test4_BundledLayout_LabelsAndPorts_MatchSpecification()
    {
        var (bundledVm, _) = await CreateViewModelAsync(bundled: true);

        bundledVm.IsBundledLayout.Should().BeTrue();
        bundledVm.ClusterSubtitleText.Should().Be("Standalone Self-Hosting Stack — Local PostgreSQL 17, Garnet Cache, Master/Chat/Portal & COMPEL Fleet.");
        bundledVm.WebPortalButtonLabel.Should().Be("🌐 Web Portal (5266)");
        bundledVm.WebPortalApiButtonLabel.Should().Be("🔑 Web Portal API Swagger (5200)");

        var master = bundledVm.Microservices.Should().ContainSingle(s => s.Key == "master-server").Subject;
        master.PortInfo.Should().Be("HTTP: 55555");

        var chat = bundledVm.Microservices.Should().ContainSingle(s => s.Key == "chat-server").Subject;
        chat.PortInfo.Should().Be("TCP: 11031 / 11032 / HTTP: 5554");

        var api = bundledVm.Microservices.Should().ContainSingle(s => s.Key == "web-portal-api").Subject;
        api.PortInfo.Should().Be("HTTP: 127.0.0.1:5200 (Loopback)");

        var ui = bundledVm.Microservices.Should().ContainSingle(s => s.Key == "web-portal-ui").Subject;
        ui.PortInfo.Should().Be("HTTP: 5266");
    }

    [Fact]
    public async Task Test5_CopyFriendJoinInstructionsAsync_UpdatesPreviewAndFeedback()
    {
        var (vm, _) = await CreateViewModelAsync(bundled: true);

        await vm.CopyFriendJoinInstructionsAsync();

        vm.CopyFeedbackMessage.Should().NotBeNullOrWhiteSpace();
        vm.HasCopyFeedbackMessage.Should().BeTrue();
        vm.FriendJoinInstructionsPreview.Should().Contain("Project Kongor — Friend Join Instructions");
    }
}
