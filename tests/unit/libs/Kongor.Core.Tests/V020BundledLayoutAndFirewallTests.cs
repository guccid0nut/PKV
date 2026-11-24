// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

using System;
using System.Collections.Generic;
using System.IO;
using FluentAssertions;
using Kongor.Core.Configuration;
using Kongor.Core.Network;
using Kongor.Core.Services.Native;
using Xunit;

namespace Kongor.Core.Tests;

public sealed class V020BundledLayoutAndFirewallTests
{
    [Fact]
    public void BundledLayoutResolver_RootLauncherVsRidSubdir_ResolvesCleanlyWithoutStdoutPollution()
    {
        // protects: Standalone bundle root discovery (root WILLOWMAKER vs launcher-<rid> fallback) and MCP stdio stdout purity.
        // fails_when: LauncherExe ignores root WILLOWMAKER binary or BundledLayoutResolver writes diagnostic banners to Console.Out.
        // seam: Real filesystem directory tree + BundledLayoutResolver.TryResolve + Console.Out capture.
        string tempRoot = Path.Combine(Path.GetTempPath(), "kongor_bundle_" + Guid.NewGuid().ToString("N"));
        string toolsDir = Path.Combine(tempRoot, "tools-linux-x64");
        string launcherRidDir = Path.Combine(tempRoot, "launcher-linux-x64");
        Directory.CreateDirectory(Path.Combine(tempRoot, "games", "client"));
        Directory.CreateDirectory(toolsDir);
        Directory.CreateDirectory(launcherRidDir);
        File.WriteAllText(Path.Combine(tempRoot, BundledLayoutResolver.PolicyFileName), "{\"isPlaytest\":true}");

        try
        {
            TextWriter originalOut = Console.Out;
            using StringWriter capturedStdout = new();
            try
            {
                Console.SetOut(capturedStdout);
                bool resolved = BundledLayoutResolver.TryResolve(
                    out BundledLayout? layout,
                    startDirectory: toolsDir,
                    ridOverride: "linux-x64",
                    isWindowsOverride: false);

                resolved.Should().BeTrue();
                layout.Should().NotBeNull();

                // When only launcher-linux-x64/WILLOWMAKER exists, resolve there
                string ridBinary = Path.Combine(launcherRidDir, "WILLOWMAKER");
                File.WriteAllText(ridBinary, "elf");
                layout!.LauncherExe.Should().Be(ridBinary);

                // When root WILLOWMAKER also exists, root takes precedence
                string rootBinary = Path.Combine(tempRoot, "WILLOWMAKER");
                File.WriteAllText(rootBinary, "elf-root");
                layout.LauncherExe.Should().Be(rootBinary);
            }
            finally
            {
                Console.SetOut(originalOut);
            }

            capturedStdout.ToString().Should().BeEmpty("BundledLayoutResolver must log exclusively to Console.Error so MCP stdio framing is never corrupted");
        }
        finally
        {
            Directory.Delete(tempRoot, recursive: true);
        }
    }

    [Fact]
    public void WindowsFirewallUdpCoverageAuditor_OffsetPortsAndNetshParsing_DetectsUncoveredAndCoveredRanges()
    {
        // protects: Self-hosted COMPEL WAN/LAN reachability preflight across ping, game, and voice UDP ports with port offsets.
        // fails_when: Port offset is ignored or missing UDP inbound rules fail to emit actionable New-NetFirewallRule remediation.
        // seam: Real WindowsFirewallUdpCoverageAuditor.ComputeRequiredUdpPorts + EvaluateNetshOutput.
        IReadOnlyList<int> offsetPorts = WindowsFirewallUdpCoverageAuditor.ComputeRequiredUdpPorts(portRangeOffset: 100, instances: 2);
        offsetPorts.Should().BeEquivalentTo([11334, 11335, 11336, 11600, 11835, 11836]);

        string uncoveredNetsh = """
                                Rule Name:                            Unrelated TCP Rule
                                ----------------------------------------------------------------------
                                Enabled:                              Yes
                                Direction:                            In
                                Profiles:                             Domain,Private,Public
                                Protocol:                             TCP
                                LocalPort:                            55555
                                Action:                               Allow
                                """;

        FirewallUdpAuditReport missingReport = WindowsFirewallUdpCoverageAuditor.EvaluateNetshOutput(
            uncoveredNetsh,
            activeProfile: "Public",
            portRangeOffset: 100,
            instances: 2,
            lanIp: "192.168.1.50");

        missingReport.IsFullyCovered.Should().BeFalse();
        missingReport.UncoveredUdpPorts.Should().Contain([11334, 11335, 11600, 11835]);
        missingReport.WarningBanner.Should().Contain("[WAN-REACHABILITY]");
        missingReport.RemediationPowerShellCommand.Should().Contain("11334-11336,11600,11835-11836");
        missingReport.RouterPortForwardSummary.Should().Be("UDP 11334-11336, 11600, 11835-11836 -> 192.168.1.50");

        string coveredNetsh = """
                              Rule Name:                            ProjectKongor-COMPEL-UDP
                              ----------------------------------------------------------------------
                              Enabled:                              Yes
                              Direction:                            In
                              Profiles:                             Domain,Private,Public
                              Protocol:                             UDP
                              LocalPort:                            11334-11336,11600,11835-11836
                              Action:                               Allow
                              """;

        FirewallUdpAuditReport coveredReport = WindowsFirewallUdpCoverageAuditor.EvaluateNetshOutput(
            coveredNetsh,
            activeProfile: "Public",
            portRangeOffset: 100,
            instances: 2,
            lanIp: "192.168.1.50");

        coveredReport.IsFullyCovered.Should().BeTrue();
        coveredReport.UncoveredUdpPorts.Should().BeEmpty();
        coveredReport.WarningBanner.Should().BeNull();
    }

    [Fact]
    public void ClientAudioModernizationService_IsClientSafeTarget_RejectsServerPaths()
    {
        // protects: Dedicated server fmodex64.dll from ever being overwritten by client WASAPI/HRTF proxy deployment.
        // fails_when: Server or dedicated directories pass IsClientSafeTarget.
        // seam: Real ClientAudioModernizationService.IsClientSafeTarget guard.
        ClientAudioModernizationService.IsClientSafeTarget(@"C:\ProjectKongor\games\client\fmodex64.dll").Should().BeTrue();
        ClientAudioModernizationService.IsClientSafeTarget(@"C:\ProjectKongor\games\server\fmodex64.dll").Should().BeFalse();
        ClientAudioModernizationService.IsClientSafeTarget("/opt/kongor/dedicated/fmodex64.dll").Should().BeFalse();
    }
}
