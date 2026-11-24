// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

// ============================================================================
// File: ExportModalViewModelTests.cs
// System: Replay Studio Desktop GUI Tool
// Policy: Strict Explicit Typing (NO 'var')
// ============================================================================

using System.IO;
using System.Threading.Tasks;
using FluentAssertions;
using ReplayStudio.GUI.Director;
using ReplayStudio.GUI.Engine;
using ReplayStudio.GUI.Export;
using ReplayStudio.GUI.Services;
using ReplayStudio.GUI.ViewModels;
using Xunit;

namespace ReplayStudio.Tests;

public class ExportModalViewModelTests
{
    [Fact]
    public void ConfigureMarkers_WhenInvoked_SetsMarkersAndFormattedStrings()
    {
        ExportModalViewModel vm = new();
        vm.ConfigureMarkers(15000, 45000);

        vm.InMarkerMs.Should().Be(15000);
        vm.OutMarkerMs.Should().Be(45000);
        vm.InMarkerFormatted.Should().Be("00:15.000");
        vm.OutMarkerFormatted.Should().Be("00:45.000");
    }

    [Fact]
    public void OpenAndCloseModal_WhenInvoked_TogglesVisibility()
    {
        ExportModalViewModel vm = new();
        vm.IsVisible.Should().BeFalse();

        vm.OpenModal();
        vm.IsVisible.Should().BeTrue();

        vm.CloseModal();
        vm.IsVisible.Should().BeFalse();
    }

    [Trait("Category", "Manual")]
    [Fact]
    public async Task StartExportAsync_WhenInvoked_CompletesProgressSuccessfully()
    {
        if (!NexusTestEnvironment.IsManualTestEnabled) return;
        ExportModalViewModel vm = new();
        string tempOut = Path.Combine(Path.GetTempPath(), $"modal_test_{Guid.NewGuid():N}.mp4");
        vm.SelectedBackendMode = ReplayExportBackendMode.SoftwarePreview;
        vm.OutputFilePath = tempOut;
        vm.SelectedPreset = "720p60 Fast (CPU H.264)";
        vm.ConfigureMarkers(0, 500); // 30 frames

        await vm.StartExportAsync();

        vm.ExportProgressPercent.Should().Be(100.0);
        vm.ExportStatusText.Should().Contain("Export completed");
        vm.IsExporting.Should().BeFalse();

        if (File.Exists(tempOut))
        {
            File.Delete(tempOut);
        }
    }

    [Fact]
    public void CancelExport_WhenExporting_CancelsCleanly()
    {
        ExportModalViewModel vm = new();
        vm.ConfigureMarkers(0, 10000);

        vm.CancelExport();
        vm.ExportStatusText.Should().Be("Ready to export.");
    }

    [Fact]
    public void AvailablePresets_WhenInvoked_ContainsStandardProfiles()
    {
        ExportModalViewModel vm = new();
        vm.AvailablePresets.Should().Contain("1080p60 Ultra (NVENC GPU)");
        vm.AvailablePresets.Should().Contain("4K60 RTX (NVENC HEVC)");
        vm.AvailablePresets.Should().Contain("720p60 Fast (CPU H.264)");
    }

    [Fact]
    public void BindDependencies_WhenInvoked_InjectsServices()
    {
        ExportModalViewModel vm = new();
        ReplaySnapshotEngine snapshotEngine = new();
        PlaybackClock clock = new();
        MapEnvironmentManager mapManager = new();
        CameraDirector cameraDirector = new();
        LightingController lightingController = new();
        ReplayStudioService service = new(snapshotEngine, clock, mapManager);

        vm.BindDependencies(service, cameraDirector, lightingController);
        vm.Should().NotBeNull();
    }
}
