// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

// ============================================================================
// File: ReplayConverterViewModelTests.cs
// System: WILLOWMAKER Test Suite
// Policy: Strict Explicit Typing (NO 'var')
// ============================================================================

using FluentAssertions;
using WILLOWMAKER.Core.ViewModels;
using Xunit;

namespace WILLOWMAKER.Tests;

public class ReplayConverterViewModelTests
{
    [Fact]
    public void OpenInReplayStudioCommand_WithReplayPath_LogsLaunchIntent()
    {
        ReplayConverterViewModel vm = new()
        {
            ReplayFilePath = "C:\\Replays\\game_final.honreplay"
        };

        vm.OpenInReplayStudioCommand.Execute(null);

        vm.LogText.Should().Contain("[LAUNCH]");
        vm.LogText.Should().Contain("game_final.honreplay");
        vm.StatusText.Should().Contain("Replay Studio");
    }

    [Fact]
    public void ReplayConverterViewModel_PresetsAndPovOptions_ArePopulatedWithStandardDefaults()
    {
        ReplayConverterViewModel vm = new();

        vm.QualityPresets.Should().Contain("1080p60 Ultra (NVENC GPU)");
        vm.QualityPresets.Should().Contain("4K60 Ultra RTX (NVENC GPU)");
        vm.QualityPresets.Should().Contain("720p60 Fast (CPU H.264)");

        vm.PovCameraOptions.Should().Contain("Free Spectator Camera");
        vm.PovCameraOptions.Should().Contain("Player 1 (Hero Index 0)");
    }

    [Fact]
    public void ReplayConverterViewModel_FilteredLogText_FiltersCorrectlyByQuery()
    {
        ReplayConverterViewModel vm = new()
        {
            LogText = "[INFO] Initializing engine\n[WARN] Missing texture\n[INFO] Loading mesh\n"
        };

        vm.SearchQuery = "WARN";
        vm.FilteredLogText.Should().Contain("[WARN] Missing texture");
        vm.FilteredLogText.Should().NotContain("[INFO] Initializing engine");

        vm.SearchQuery = "";
        vm.FilteredLogText.Should().Be(vm.LogText);
    }
}
