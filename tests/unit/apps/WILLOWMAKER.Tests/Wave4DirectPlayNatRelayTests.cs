// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

// ============================================================================
// File: Wave4DirectPlayNatRelayTests.cs
// System: WILLOWMAKER Unit Tests - Wave 4 DirectPlay STUN NAT & Relay Planning
// Policy: Strict Explicit Typing (NO 'var') - Sub-2s Automated Unit Tests
// ============================================================================

using System;
using System.Collections.Generic;
using System.Net;
using System.Threading.Tasks;
using FluentAssertions;
using Kongor.Core.Network;
using WILLOWMAKER.Core.ViewModels;
using WILLOWMAKER.Services.P2P;
using Xunit;

namespace WILLOWMAKER.Tests;

public sealed class Wave4DirectPlayNatRelayTests
{
    [Fact]
    public async Task DirectPlayViewModel_RunNetworkDiagnostics_SymmetricCgnatSelectsRatholeFallbackAndEmitsTelemetry()
    {
        // protects: WILLOWMAKER DirectPlay integration with StunNatClassifier + DirectPlayRelayCoordinator under CGNAT.
        // fails_when: Symmetric/CGNAT fails to set IsRelayRequired, omits rathole TOML snippet, or omits [P2P-NAT]/[P2P-RELAY] logs.
        // seam: DirectPlayViewModel.RunNetworkDiagnosticsAsync with injected Firewall, UPnP, STUN, and Relay options.
        List<string> logs = [];
        DirectPlayViewModel vm = new DirectPlayViewModel(_ => Task.CompletedTask, logSink: logs.Add)
        {
            HostPort = "11550",
            DetectedLocalIp = "192.168.1.42",
            FirewallAuditorOverride = (_, _, _, _) => Task.FromResult<FirewallUdpAuditReport?>(null),
            UpnpPortMapperOverride = (port, _, _) => Task.FromResult(new UpnpPortMappingResult(
                Succeeded: false,
                Protocol: "UDP",
                ExternalPort: port,
                InternalPort: port,
                GatewayAddress: null,
                ExternalIpAddress: null,
                ServiceType: null,
                DiagnosticMessage: "No UPnP IGD router responded on LAN.")),
            StunNatClassifierOverride = (_, _) => Task.FromResult(new StunProbeResult(
                NatBehaviorClassification.SymmetricOrCgnat,
                new IPEndPoint(IPAddress.Parse("192.168.1.42"), 11550),
                new IPEndPoint(IPAddress.Parse("100.64.5.10"), 51200),
                new IPEndPoint(IPAddress.Parse("100.64.5.10"), 51201),
                UsedXorMappedAddress: true,
                IsCgnatAddress: true,
                PrimaryRttMs: 12.4,
                SecondaryRttMs: 14.8,
                RejectedSpoofedPackets: 0,
                DiagnosticMessage: "RFC 6598 CGNAT detected")),
            RelayOptionsOverride = new DirectPlayRelayOptions(
                RatholeServerHost: "relay.community.example",
                RatholeControlPort: 2333,
                RatholePublicUdpPort: 20550)
        };

        await vm.RunNetworkDiagnosticsAsync();

        vm.IsUpnpMapped.Should().BeFalse();
        vm.NatClassification.Should().Be(NatBehaviorClassification.SymmetricOrCgnat);
        vm.NatStatusText.Should().Contain("CGNAT");
        vm.IsRelayRequired.Should().BeTrue();
        vm.RecommendedTransportStrategy.Should().Be(DirectPlayTransportStrategy.RatholeUdpTunnel);
        vm.RelayStatusText.Should().Contain("Relay Required");
        vm.RelayConfigSnippet.Should().Contain("remote_addr = \"relay.community.example:2333\"");
        vm.RelayConfigSnippet.Should().Contain("local_addr = \"127.0.0.1:11550\"");
        vm.RelayConfigSnippet.Should().Contain("${KONGOR_RATHOLE_TOKEN}");
        vm.LastTransportPlan.Should().NotBeNull();
        vm.LastTransportPlan!.RecommendedInviteAddress.Should().Be("relay.community.example:20550");
        logs.Should().Contain(l => l.Contains("[P2P-NAT]", StringComparison.Ordinal));
        logs.Should().Contain(l => l.Contains("[P2P-RELAY]", StringComparison.Ordinal));
    }

    [Fact]
    public async Task DirectPlayViewModel_RunNetworkDiagnostics_CgnatWithCompelRelayConfigured_SelectsCompelUdpProxy()
    {
        // protects: Reuse of COMPEL UDP proxy relay in DirectPlay when configured for CGNAT hosts.
        // fails_when: DirectPlayViewModel ignores CompelProxyHost or fails to expose CompelUdpProxyRelay strategy.
        // seam: DirectPlayViewModel.RunNetworkDiagnosticsAsync -> P2pConnectionService.PlanDirectPlayTransport.
        DirectPlayViewModel vm = new DirectPlayViewModel(_ => Task.CompletedTask)
        {
            HostPort = "11550",
            DetectedLocalIp = "192.168.1.42",
            FirewallAuditorOverride = (_, _, _, _) => Task.FromResult<FirewallUdpAuditReport?>(null),
            UpnpPortMapperOverride = (port, _, _) => Task.FromResult(new UpnpPortMappingResult(
                false, "UDP", port, port, null, null, null, "UPnP unavailable")),
            StunNatClassifierOverride = (_, _) => Task.FromResult(new StunProbeResult(
                NatBehaviorClassification.SymmetricOrCgnat,
                new IPEndPoint(IPAddress.Parse("192.168.1.42"), 11550),
                new IPEndPoint(IPAddress.Parse("203.0.113.80"), 60101),
                new IPEndPoint(IPAddress.Parse("203.0.113.80"), 60102),
                UsedXorMappedAddress: true,
                IsCgnatAddress: false,
                PrimaryRttMs: 18.0,
                SecondaryRttMs: 19.0,
                RejectedSpoofedPackets: 0,
                DiagnosticMessage: "Symmetric NAT")),
            RelayOptionsOverride = new DirectPlayRelayOptions(
                CompelProxyHost: "compel-use.kongor.example",
                CompelProxyPort: 20100)
        };

        await vm.RunNetworkDiagnosticsAsync();

        vm.RecommendedTransportStrategy.Should().Be(DirectPlayTransportStrategy.CompelUdpProxyRelay);
        vm.IsRelayRequired.Should().BeTrue();
        vm.RelayConfigSnippet.Should().Be("compel-use.kongor.example:20100");
        vm.LastTransportPlan!.CompelRelayEndPoint.Should().Be("compel-use.kongor.example:20100");
    }

    [Fact]
    public async Task DirectPlayViewModel_RunNetworkDiagnostics_ConeNatPopulatesExternalIpAndSelectsHolePunch()
    {
        // protects: Cone NAT (Endpoint-Independent Mapping) STUN hole-punch path & external IP population when UPnP fails.
        // fails_when: Cone NAT incorrectly demands relay or fails to populate unresolved DetectedPublicIp from STUN.
        // seam: DirectPlayViewModel.RunNetworkDiagnosticsAsync.
        DirectPlayViewModel vm = new DirectPlayViewModel(_ => Task.CompletedTask)
        {
            HostPort = "11550",
            DetectedLocalIp = "192.168.1.42",
            DetectedPublicIp = "Unavailable (Use LAN / VPN IP)",
            FirewallAuditorOverride = (_, _, _, _) => Task.FromResult<FirewallUdpAuditReport?>(null),
            UpnpPortMapperOverride = (port, _, _) => Task.FromResult(new UpnpPortMappingResult(
                false, "UDP", port, port, null, null, null, "UPnP disabled")),
            StunNatClassifierOverride = (_, _) => Task.FromResult(new StunProbeResult(
                NatBehaviorClassification.ConeNat,
                new IPEndPoint(IPAddress.Parse("192.168.1.42"), 11550),
                new IPEndPoint(IPAddress.Parse("198.51.100.99"), 11550),
                new IPEndPoint(IPAddress.Parse("198.51.100.99"), 11550),
                UsedXorMappedAddress: true,
                IsCgnatAddress: false,
                PrimaryRttMs: 9.5,
                SecondaryRttMs: 11.1,
                RejectedSpoofedPackets: 0,
                DiagnosticMessage: "Cone NAT EIM"))
        };

        await vm.RunNetworkDiagnosticsAsync();

        vm.NatClassification.Should().Be(NatBehaviorClassification.ConeNat);
        vm.RecommendedTransportStrategy.Should().Be(DirectPlayTransportStrategy.StunHolePunch);
        vm.IsRelayRequired.Should().BeFalse();
        vm.DetectedPublicIp.Should().Be("198.51.100.99");
        vm.InviteCode.Should().Be("198.51.100.99:11550");
        P2pConnectionService.FormatNatClassificationLabel(vm.NatClassification).Should().Contain("Cone NAT");
    }
}
