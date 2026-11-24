// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

using WILLOWMAKER.Core.Services.Models;
using WILLOWMAKER.Core.ViewModels;
using Xunit;

namespace WILLOWMAKER.Tests;

public sealed class LiveLogsViewModelTests
{
    [Fact]
    public void LogMessage_PathMasking_CensorsPathsAndUserDir()
    {
        // Arrange
        string rawWindowsPath =
            @"Loading asset from C:\Users\Developer\Projects\ProjectKongor\override\ui\game_shop.lua into VFS";
        string rawForwardSlash = "Mounted resource at /Users/Developer/Projects/ProjectKongor/base/core.s2z";

        // Act
        string maskedWindows = WillowmakerLogMessage.MaskSensitivePaths(rawWindowsPath);
        string maskedForward = WillowmakerLogMessage.MaskSensitivePaths(rawForwardSlash);

        // Assert
        Assert.DoesNotContain(@"C:\Users\Developer\Projects\ProjectKongor\override\ui\", maskedWindows);
        Assert.Contains("game_shop.lua", maskedWindows);
        Assert.Contains("[PATH:", maskedWindows);

        Assert.DoesNotContain("/Users/Developer/Projects/ProjectKongor/", maskedForward);
        Assert.Contains("core.s2z", maskedForward);
    }

    [Fact]
    public void LiveLogsViewModel_ErrorBanner_DisplaysWinnerIsYouWhenZeroErrors()
    {
        // Arrange
        LiveLogsViewModel vm = new();

        // Act
        vm.AddLog("General", "System initialized successfully");
        vm.AddLog("IPC", "Named Pipe listener started on \\\\.\\pipe\\willowmaker-ipc");

        // Assert
        Assert.False(vm.HasErrors);
        Assert.Equal(0, vm.ErrorCount);
        Assert.Equal("➔ 100% ERROR FREE - A WINNER IS YOU!", vm.ErrorBannerText);
        Assert.Equal("#052E16", vm.ErrorBannerBackground);
        Assert.Equal("#10B981", vm.ErrorBannerBorder);
        Assert.Equal("#22C55E", vm.ErrorBannerForeground);
    }

    [Fact]
    public void LiveLogsViewModel_ErrorBanner_DisplaysErrorStateWhenErrorsOccur()
    {
        // Arrange
        LiveLogsViewModel vm = new();

        // Act
        vm.AddLog("General", "System initialized successfully");
        vm.AddLog("Executable", "Failed to start process: Access denied error");

        // Assert
        Assert.True(vm.HasErrors);
        Assert.Equal(1, vm.ErrorCount);
        Assert.Equal("➔ EACH ERROR IS ONE STEP CLOSER TO PERFECTION!", vm.ErrorBannerText);
        Assert.Equal("#450A0A", vm.ErrorBannerBackground);
        Assert.Equal("#EF4444", vm.ErrorBannerBorder);
        Assert.Equal("#F87171", vm.ErrorBannerForeground);
    }

    [Fact]
    public void LiveLogsViewModel_Filtering_BySearchTextAndCategoryAndLevel()
    {
        // Arrange
        LiveLogsViewModel vm = new();
        vm.AddLog("Executable", "Starting hon_x64.exe with arguments");
        vm.AddLog("IPC", "Client connected to IPC bridge");
        vm.AddLog("HotReload", "Modified file /ui/game_shop.lua");
        vm.AddLog("Executable", "Direct3D9 render error in swapchain initialization");

        // Act 1: Filter by Search Text
        vm.SearchText = "game_shop";
        Assert.Single(vm.FilteredLogs);
        Assert.Equal("HotReload", vm.FilteredLogs[0].Category);

        // Act 2: Filter by Category
        vm.SearchText = string.Empty;
        vm.SelectedCategoryFilter = "Executable";
        Assert.Equal(2, vm.FilteredLogs.Count);

        // Act 3: Filter by Level
        vm.SelectedCategoryFilter = "ALL CATEGORIES";
        vm.SelectedLevelFilter = "ERROR";
        Assert.Single(vm.FilteredLogs);
        Assert.Contains("error", vm.FilteredLogs[0].Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void LiveLogsViewModel_CategoryNormalization_MatchesPaddedUnderscoreCategories()
    {
        // Arrange
        LiveLogsViewModel vm = new();
        vm.AddLog("PARAMETERS_", "-masterserver masterserver.newbs.io");
        vm.AddLog("GUARD______", "Heroes Of Newerth Directory is Present");
        vm.AddLog("VERSION____", "Current Version: v2.1.4");
        vm.AddLog("EXECUTABLE_", "Game Launch Initiated");
        vm.AddLog("GAME_CLIENT", "Vulkan 1.3 Driver active");

        // Act 1: Filter by PARAMETERS
        vm.SelectedCategoryFilter = "PARAMETERS";
        Assert.Single(vm.FilteredLogs);
        Assert.Equal("PARAMETERS_", vm.FilteredLogs[0].Category);

        // Act 2: Filter by GAME CLIENT
        vm.SelectedCategoryFilter = "GAME CLIENT";
        Assert.Single(vm.FilteredLogs);
        Assert.Equal("GAME_CLIENT", vm.FilteredLogs[0].Category);

        // Act 3: Filter by GUARD
        vm.SelectedCategoryFilter = "GUARD";
        Assert.Single(vm.FilteredLogs);
        Assert.Equal("GUARD______", vm.FilteredLogs[0].Category);
    }

    [Fact]
    public void LiveLogsViewModel_PathMaskingToggle_UpdatesMaskedText()
    {
        // Arrange
        LiveLogsViewModel vm = new();
        vm.AddLog("VFS", @"Mounted override at C:\Users\Developer\Projects\ProjectKongor\override\ui\main.lua");

        // Act 1: Default is Masked (Censored)
        Assert.True(vm.MaskPaths);
        Assert.Equal("🔒 Paths Censored", vm.MaskPathsButtonText);
        WillowmakerLogMessage logItem = vm.FilteredLogs[0];
        Assert.Contains("[PATH:", logItem.MaskedDisplayText);
        Assert.DoesNotContain(@"C:\Users\Developer\Projects\", logItem.MaskedDisplayText);

        // Act 2: Toggle to Revealed
        vm.ToggleGlobalPathMasking();
        Assert.False(vm.MaskPaths);
        Assert.Equal("👁 Paths Revealed", vm.MaskPathsButtonText);
        Assert.Contains(@"C:\Users\Developer\Projects\", logItem.MaskedDisplayText);
    }

    [Fact]
    public void LiveLogsViewModel_ClearLogs_ResetsState()
    {
        // Arrange
        LiveLogsViewModel vm = new();
        vm.AddLog("Executable", "Failed to launch client: fatal error");
        Assert.Equal(1, vm.LogCount);
        Assert.True(vm.HasErrors);

        // Act
        vm.ClearLogs();

        // Assert
        Assert.Equal(0, vm.LogCount);
        Assert.Empty(vm.FilteredLogs);
        Assert.False(vm.HasErrors);
        Assert.Equal(0, vm.ErrorCount);
        Assert.Equal("➔ 100% ERROR FREE - A WINNER IS YOU!", vm.ErrorBannerText);
    }
}
