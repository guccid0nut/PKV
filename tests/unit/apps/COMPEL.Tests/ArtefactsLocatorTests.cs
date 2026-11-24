// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2022-2026 [K]ONGOR and Contributors
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV) -- Upstream lineage: https://github.com/Project-KONGOR-Open-Source

namespace COMPEL.Tests;

/// <summary>
///     Verifies where the artefacts locator resolves the child process's profile and artefacts directories on each
///     platform.
/// </summary>
public sealed class ArtefactsLocatorTests
{
    private static ArtefactsLocator Locator(string runtimeArtefactsPath)
    {
        return new ArtefactsLocator(Options.Create(new MatchServerManagerOptions
            { RuntimeArtefactsPath = runtimeArtefactsPath }));
    }

    [Fact]
    public void On_Windows_The_Default_Alias_Places_Artefacts_Beneath_The_User_Profile()
    {
        if (OperatingSystem.IsWindows() is false)
            return;

        ArtefactsLocator locator = Locator("DEFAULT");

        string profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

        Assert.Equal(profile, locator.ProfileDirectory);
        Assert.Equal(Path.Combine(profile, "Documents", "Heroes of Newerth x64"), locator.ArtefactsDirectory);
        Assert.True(locator.RuntimeArtefactsPathApplies);
    }

    [Fact]
    public void On_Windows_A_Literal_Path_Is_Used_As_The_Profile_Directory()
    {
        if (OperatingSystem.IsWindows() is false)
            return;

        ArtefactsLocator locator = Locator(@"D:\HONServer");

        Assert.Equal(@"D:\HONServer", locator.ProfileDirectory);
    }

    [Fact]
    public void On_Linux_The_Artefacts_Directory_Is_The_Fixed_Server_Location_Regardless_Of_Configuration()
    {
        if (OperatingSystem.IsLinux() is false)
            return;

        ArtefactsLocator locator = Locator("DEFAULT");

        Assert.Equal("/opt/hon/config", locator.ArtefactsDirectory);
        Assert.False(locator.RuntimeArtefactsPathApplies);
    }
}
