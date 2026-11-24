// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2022-2026 [K]ONGOR and Contributors
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV) -- Upstream lineage: https://github.com/Project-KONGOR-Open-Source

using COMPEL.Services.Supervision;
using FluentAssertions;
using Xunit;

namespace COMPEL.Tests;

/// <summary>
///     Verifies the check that decides whether the manager can spawn server instances from a given installation directory.
/// </summary>
public sealed class HeroesOfNewerthExecutableTests
{
    [Fact]
    public void A_Directory_Whose_Path_Contains_A_Whitespace_Character_Supports_Instance_Launching()
    {
        HeroesOfNewerthExecutable.CanLaunchInstancesFrom(@"C:\Games\HoN Match Server").Should().BeTrue();
    }

    [Fact]
    public void A_Directory_Whose_Path_Contains_No_Whitespace_Character_Does_Not_Support_Instance_Launching_On_Windows()
    {
        bool supported = HeroesOfNewerthExecutable.CanLaunchInstancesFrom(@"C:\Games\windows-x64-native-aot");

        // The Restriction Is A Windows-Only Consequence Of How The Engine Parses The Command Line Its Manager Builds For Each Instance
        supported.Should().Be(OperatingSystem.IsWindows() is false);
    }

    [Fact]
    public void The_Whitespace_Character_May_Come_From_Any_Part_Of_The_Path()
    {
        HeroesOfNewerthExecutable.CanLaunchInstancesFrom(@"C:\Program Files\COMPEL").Should().BeTrue();
    }

    [Fact]
    public void EnsureWhitespacePathOnWindows_WhenSiblingHonServerExists_ResolvesWhitespaceSibling()
    {
        string tempDir = Path.Combine(Path.GetTempPath(), "KongorTest_" + Guid.NewGuid().ToString("N"));
        try
        {
            string serverDir = Path.Combine(tempDir, "server");
            string honServerDir = Path.Combine(tempDir, "hon server");
            Directory.CreateDirectory(serverDir);
            Directory.CreateDirectory(honServerDir);
            string exeName = HeroesOfNewerthExecutable.FileName;
            File.WriteAllText(Path.Combine(honServerDir, exeName), "");
            File.WriteAllText(Path.Combine(honServerDir, "k2_x64.dll"), "");

            string resolved = COMPEL.Services.ContentBroker.DistributionSynchronisationService.EnsureWhitespacePathOnWindows(serverDir);
            if (OperatingSystem.IsWindows())
            {
                resolved.Should().Be(honServerDir);
                HeroesOfNewerthExecutable.CanLaunchInstancesFrom(resolved).Should().BeTrue();
            }
        }
        finally
        {
            if (Directory.Exists(tempDir)) Directory.Delete(tempDir, true);
        }
    }
}
