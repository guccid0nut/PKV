// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

using Kongor.Core.Network;
using NEXUS.ControlPanel.GUI.Services.Models;

namespace NEXUS.ControlPanel.GUI.Services;

public interface IPortScannerService
{
    Task<List<PortStatus>> ScanRequiredPortsAsync(CancellationToken cancellationToken = default);

    Task<FirewallUdpAuditReport?> AuditWindowsFirewallUdpCoverageAsync(
        int portRangeOffset = 0,
        int instances = 2,
        string? lanIp = null,
        CancellationToken cancellationToken = default) =>
        WindowsFirewallUdpCoverageAuditor.RunAuditAsync(portRangeOffset, instances, lanIp, cancellationToken);
}
