// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

using Moq;
using NEXUS.ControlPanel.GUI.Services;
using NEXUS.ControlPanel.GUI.Services.Models;
using NEXUS.ControlPanel.GUI.ViewModels;

namespace ASPIRE.Tests.NEXUS.ControlPanel.GUI;

public sealed class LiveLogsViewModelTests
{
    [Fact]
    public void LiveLogsViewModel_MaskSensitivePaths_CensorsSingleAndDoubleBackslashPaths()
    {
        // Arrange
        const string singleBackslash =
            @"Using launch settings from C:\Users\developer\workspace\nexus\ASPIRE.ApplicationHost\Properties\launchSettings.json...";
        const string doubleBackslashJson =
            @"{""Cmd"": ""C:\\Program Files\\dotnet\\dotnet.exe"", ""Args"": [""run"", ""--project"", ""C:\\Users\\developer\\workspace\\nexus\\KONGOR.MasterServer\\KONGOR.MasterServer.csproj""]}";
        const string aspireLogPath = @"Logs: C:\Users\developer\.aspire\logs\cli_20260820T014500_e1473cbf.log";

        // Act
        string maskedSingle = LogMessage.MaskSensitivePaths(singleBackslash);
        string maskedDouble = LogMessage.MaskSensitivePaths(doubleBackslashJson);
        string maskedAspire = LogMessage.MaskSensitivePaths(aspireLogPath);

        // Assert
        Assert.DoesNotContain(@"C:\Users\developer", maskedSingle, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("[PATH: ...\\launchSettings.json]", maskedSingle);

        Assert.DoesNotContain(@"C:\\Users\\developer", maskedDouble, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(@"[PATH: ...\\KONGOR.MasterServer.csproj]", maskedDouble);

        Assert.DoesNotContain(@"C:\Users\developer", maskedAspire, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("[PATH: ...\\cli_20260820T014500_e1473cbf.log]", maskedAspire);
    }

    [Fact]
    public void LogMessage_MaskedDisplayText_RespectsIsRevealedToggle()
    {
        // Arrange & Act 1: Censored by default
        LogMessage msg = new()
        {
            ServiceName = "ASPIRE HOST",
            Level = "INFO",
            Message = @"Using launch settings from C:\Users\developer\workspace\launchSettings.json"
        };

        Assert.Contains("[PATH: ...\\launchSettings.json]", msg.MaskedDisplayText);
        Assert.DoesNotContain(@"C:\Users\developer", msg.MaskedDisplayText);

        // Act 2: Explicitly revealed
        LogMessage revealedMsg = new()
        {
            ServiceName = "ASPIRE HOST",
            Level = "INFO",
            Message = @"Using launch settings from C:\Users\developer\workspace\launchSettings.json",
            IsRevealed = true
        };

        Assert.Contains(@"C:\Users\developer\workspace\launchSettings.json", revealedMsg.MaskedDisplayText);
    }

    [Fact]
    public void LiveLogsViewModel_GetLogsAsPlainText_AlwaysExportsRawFullUnmaskedPaths()
    {
        // Arrange
        Mock<INexusOrchestratorService> mockOrchestrator = new();
        mockOrchestrator.Setup(o => o.LogHistory).Returns(
        [
            new LogMessage
            {
                ServiceName = "ASPIRE HOST",
                Level = "INFO",
                Message = @"Logs: C:\Users\developer\.aspire\logs\cli_20260820.log"
            }
        ]);

        LiveLogsViewModel vm = new(mockOrchestrator.Object);

        // Act
        string plainText = vm.GetLogsAsPlainText();

        // Assert: Raw unmasked path is preserved for clipboard
        Assert.Contains(@"C:\Users\developer\.aspire\logs\cli_20260820.log", plainText);
    }

    [Fact]
    public void LiveLogsViewModel_SearchAndServiceFilter_FiltersLogsAccurately()
    {
        // Arrange
        Mock<INexusOrchestratorService> mockOrchestrator = new();
        mockOrchestrator.Setup(o => o.LogHistory).Returns(
        [
            new LogMessage
            {
                ServiceName = "KONGOR", Level = "INFO", Message = "KONGOR Master Server ready on port 55555"
            },
            new LogMessage
            {
                ServiceName = "CHAT SERVER", Level = "INFO", Message = "TRANSMUTANSTEIN TCP listening on 11031"
            },
            new LogMessage { ServiceName = "PORTAL API", Level = "ERROR", Message = "Failed to connect to database" },
            new LogMessage { ServiceName = "GAME CLIENT", Level = "INFO", Message = "Game Client initialized" }
        ]);

        LiveLogsViewModel vm = new(mockOrchestrator.Object)
        {
            // Act 1: Filter by Service
            SelectedServiceFilter = "KONGOR"
        };

        Assert.Single(vm.FilteredLogs);
        Assert.Equal("KONGOR", vm.FilteredLogs[0].ServiceName);

        // Act 2: Filter by GAME CLIENT
        vm.SelectedServiceFilter = "GAME CLIENT";
        Assert.Single(vm.FilteredLogs);
        Assert.Equal("GAME CLIENT", vm.FilteredLogs[0].ServiceName);

        // Act 3: Filter by Level
        vm.SelectedServiceFilter = "ALL SERVICES";
        vm.SelectedLevelFilter = "ERROR";
        Assert.Single(vm.FilteredLogs);
        Assert.Equal("PORTAL API", vm.FilteredLogs[0].ServiceName);

        // Act 4: Filter by Search Text
        vm.SelectedLevelFilter = "ALL LEVELS";
        vm.SearchText = "11031";
        Assert.Single(vm.FilteredLogs);
        Assert.Equal("CHAT SERVER", vm.FilteredLogs[0].ServiceName);
    }

    [Fact]
    public void LiveLogsViewModel_ToggleGlobalPathMasking_TogglesAllLogItems()
    {
        // Arrange
        Mock<INexusOrchestratorService> mockOrchestrator = new();
        mockOrchestrator.Setup(o => o.LogHistory).Returns(
        [
            new LogMessage { ServiceName = "ASPIRE HOST", Level = "INFO", Message = @"C:\Users\developer\file.txt" }
        ]);

        LiveLogsViewModel vm = new(mockOrchestrator.Object);
        Assert.True(vm.MaskPaths);
        Assert.False(vm.FilteredLogs[0].IsRevealed);

        // Act
        vm.ToggleGlobalPathMaskingCommand.Execute(null);

        // Assert
        Assert.False(vm.MaskPaths);
        Assert.True(vm.FilteredLogs[0].IsRevealed);
    }

    [Fact]
    public void LiveLogsViewModel_MaskSensitivePaths_CensorsUnixHomeAndUsersPaths()
    {
        // Arrange
        const string linuxHomePath = "Mounted shader cache at /home/developer/games/hon/cache/dxvk.bin";
        const string macUsersPath =
            "Loading VFS package from /Users/developer/Library/Application Support/HoN/base.s2z";

        // Act
        string maskedLinux = LogMessage.MaskSensitivePaths(linuxHomePath);
        string maskedMac = LogMessage.MaskSensitivePaths(macUsersPath);

        // Assert
        Assert.DoesNotContain("/home/developer", maskedLinux);
        Assert.Contains("[PATH: .../dxvk.bin]", maskedLinux);

        Assert.DoesNotContain("/Users/developer", maskedMac);
        Assert.Contains("[PATH: .../base.s2z]", maskedMac);
    }

    [Fact]
    public void LiveLogsViewModel_AvailableServiceFilters_ExcludesUnusedServices()
    {
        // Arrange
        Mock<INexusOrchestratorService> mockOrchestrator = new();
        mockOrchestrator.Setup(o => o.LogHistory).Returns([]);
        LiveLogsViewModel vm = new(mockOrchestrator.Object);

        // Assert: Excludes removed services
        Assert.DoesNotContain("PORTAL API", vm.AvailableServiceFilters);
        Assert.DoesNotContain("PORTAL UI", vm.AvailableServiceFilters);
        Assert.DoesNotContain("GAME SERVER", vm.AvailableServiceFilters);

        // Assert: Contains all active expected services
        Assert.Equal(
            ["ALL SERVICES", "KONGOR", "CHAT SERVER", "GAME CLIENT", "DATABASE", "REDIS CACHE", "DOCKER STACK", "ASPIRE HOST"],
            vm.AvailableServiceFilters);
    }

    [Fact]
    public void LiveLogsViewModel_AutoScroll_DefaultsToTrueAndCanBeToggled()
    {
        // Arrange
        Mock<INexusOrchestratorService> mockOrchestrator = new();
        mockOrchestrator.Setup(o => o.LogHistory).Returns([]);
        LiveLogsViewModel vm = new(mockOrchestrator.Object);

        // Assert 1: Defaults to true
        Assert.True(vm.AutoScroll);
        Assert.Equal("⬇ Auto-Scroll ON", vm.AutoScrollButtonText);

        // Act 1: Toggle off
        vm.ToggleAutoScrollCommand.Execute(null);

        // Assert 2: Toggled to false
        Assert.False(vm.AutoScroll);
        Assert.Equal("⏸ Auto-Scroll PAUSED", vm.AutoScrollButtonText);

        // Act 2: Toggle back on
        vm.ToggleAutoScrollCommand.Execute(null);

        // Assert 3: Toggled back to true
        Assert.True(vm.AutoScroll);
        Assert.Equal("⬇ Auto-Scroll ON", vm.AutoScrollButtonText);
    }
}
