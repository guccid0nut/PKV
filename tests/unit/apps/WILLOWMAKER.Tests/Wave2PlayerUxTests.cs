// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

using FluentAssertions;
using Kongor.Core.Network;
using WILLOWMAKER.Core.Services;
using WILLOWMAKER.Core.ViewModels;
using Xunit;

namespace WILLOWMAKER.Tests;

public sealed class Wave2PlayerUxTests
{
    [Fact]
    public void LocationGuard_RelocateToDedicatedFolder_CopiesLauncherFilesWithoutTouchingForeignFiles()
    {
        // protects: 1-click LocationGuard self-relocation safety (never modifies or deletes user's foreign files).
        // fails_when: foreign files are copied/deleted or WILLOWMAKER executable is omitted from relocation.
        // seam: LocationGuard.RelocateToDedicatedFolder against real temp directories.
        string tempRoot = Path.Combine(Path.GetTempPath(), $"kongor_relocate_test_{Guid.NewGuid():N}");
        string sourceDir = Path.Combine(tempRoot, "DesktopLike");
        string targetDir = Path.Combine(tempRoot, "SafeLauncher");
        Directory.CreateDirectory(sourceDir);

        try
        {
            string exeName = DeploymentManifest.ApplicationExecutableFileName;
            File.WriteAllText(Path.Combine(sourceDir, exeName), "fake-willowmaker-exe");
            File.WriteAllText(Path.Combine(sourceDir, "launcher_settings.json"), "{\"EnableCdnSync\":true}");
            File.WriteAllText(Path.Combine(sourceDir, "taxes_2026.pdf"), "important-user-document");

            LocationGuard.RelocationResult result = LocationGuard.RelocateToDedicatedFolder(
                sourceDir,
                targetDir,
                launchRelocated: false);

            result.Succeeded.Should().BeTrue();
            result.CopiedFiles.Should().Contain(exeName);
            result.CopiedFiles.Should().Contain("launcher_settings.json");
            result.CopiedFiles.Should().NotContain("taxes_2026.pdf");

            File.Exists(Path.Combine(targetDir, exeName)).Should().BeTrue();
            File.Exists(Path.Combine(targetDir, "launcher_settings.json")).Should().BeTrue();
            File.Exists(Path.Combine(targetDir, "taxes_2026.pdf")).Should().BeFalse();
            File.ReadAllText(Path.Combine(sourceDir, "taxes_2026.pdf")).Should().Be("important-user-document");
        }
        finally
        {
            if (Directory.Exists(tempRoot))
                Directory.Delete(tempRoot, recursive: true);
        }
    }

    [Fact]
    public async Task DirectPlayViewModel_RunNetworkDiagnostics_WiresUpnpAndWindowsFirewallAudit()
    {
        // protects: DirectPlay P2P UPnP auto-mapping + Windows Firewall UDP reachability reporting.
        // fails_when: Firewall warning banner/remediation command or UPnP external IP update is dropped.
        // seam: DirectPlayViewModel.RunNetworkDiagnosticsAsync with injected UPnP & Firewall delegates.
        DirectPlayViewModel vm = new(_ => Task.CompletedTask)
        {
            HostPort = "11550",
            DetectedLocalIp = "192.168.1.42",
            FirewallAuditorOverride = (offset, instances, lanIp, ct) =>
            {
                FirewallUdpAuditReport report = new(
                    ActiveProfile: "Public",
                    PortRangeOffset: offset,
                    Instances: instances,
                    RequiredUdpPorts: [11550, 12050],
                    UncoveredUdpPorts: [11550],
                    WarningBanner: "[WARN] Public profile blocks UDP 11550.",
                    RemediationPowerShellCommand: "New-NetFirewallRule -DisplayName 'ProjectKongor-COMPEL-UDP' -Direction Inbound -Action Allow -Protocol UDP -Profile Any -LocalPort 11550",
                    RouterPortForwardSummary: "UDP 11550 -> 192.168.1.42");
                return Task.FromResult<FirewallUdpAuditReport?>(report);
            },
            UpnpPortMapperOverride = (port, localIp, ct) =>
                Task.FromResult(new UpnpPortMappingResult(
                    Succeeded: true,
                    Protocol: "UDP",
                    ExternalPort: port,
                    InternalPort: port,
                    GatewayAddress: "192.168.1.1",
                    ExternalIpAddress: "198.51.100.25",
                    ServiceType: "urn:schemas-upnp-org:service:WANIPConnection:1",
                    DiagnosticMessage: "UPnP mapped UDP 11550 -> 192.168.1.42:11550 via gateway 192.168.1.1."))
        };

        await vm.RunNetworkDiagnosticsAsync();

        vm.IsUpnpMapped.Should().BeTrue();
        vm.UpnpStatusText.Should().Contain("UPnP mapped UDP 11550");
        vm.DetectedPublicIp.Should().Be("198.51.100.25");
        vm.InviteCode.Should().Be("198.51.100.25:11550");
        vm.HasFirewallWarning.Should().BeTrue();
        vm.FirewallAuditStatusText.Should().Contain("Public profile blocks UDP 11550");
        vm.FirewallRemediationCommand.Should().Contain("New-NetFirewallRule");
    }

    [Fact]
    public void MainViewModel_Wave2Toggles_DefaultAdvancedCollapsed_AndToggleDirectPlayAndModManager()
    {
        // protects: Decluttered default WILLOWMAKER player surface + reachable DirectPlay & ModManager overlays.
        // fails_when: Advanced drawer starts open or DirectPlay/ModManager toggle commands fail to mutate state.
        // seam: MainViewModel RelayCommands and default property values.
        MainViewModel vm = new();
        try
        {
            vm.IsAdvancedOptionsOpen.Should().BeFalse();
            vm.IsDirectPlayOpen.Should().BeFalse();
            vm.IsModManagerOpen.Should().BeFalse();

            vm.ToggleAdvancedOptionsCommand.Execute(null);
            vm.IsAdvancedOptionsOpen.Should().BeTrue();
            vm.AdvancedOptionsButtonText.Should().Contain("Hide");

            vm.ToggleModManagerCommand.Execute(null);
            vm.IsModManagerOpen.Should().BeTrue();
            vm.CloseModManagerCommand.Execute(null);
            vm.IsModManagerOpen.Should().BeFalse();

            vm.ToggleDirectPlayCommand.Execute(null);
            vm.IsDirectPlayOpen.Should().BeTrue();
            vm.CloseDirectPlayCommand.Execute(null);
            vm.IsDirectPlayOpen.Should().BeFalse();
        }
        finally
        {
            vm.DisposeApplicationBackgroundServices();
        }
    }
}
