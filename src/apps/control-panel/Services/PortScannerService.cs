// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

using System.Net;
using System.Net.Sockets;
using Kongor.Core.Network;
using NEXUS.ControlPanel.GUI.Services.Models;

namespace NEXUS.ControlPanel.GUI.Services;

public sealed class PortScannerService : IPortScannerService
{
    public Task<FirewallUdpAuditReport?> AuditWindowsFirewallUdpCoverageAsync(
        int portRangeOffset = 0,
        int instances = 2,
        string? lanIp = null,
        CancellationToken cancellationToken = default) =>
        WindowsFirewallUdpCoverageAuditor.RunAuditAsync(portRangeOffset, instances, lanIp, cancellationToken);
    private static readonly (int Port, string Name)[] RequiredPorts =
    [
        (5550, "Aspire Telemetry Dashboard (HTTPS)"),
        (5551, "Aspire OTLP gRPC Endpoint"),
        (55555, "KONGOR Master Server HTTP (SRP/API)"),
        (11031, "TRANSMUTANSTEIN Chat Client TCP"),
        (11032, "TRANSMUTANSTEIN Match Server TCP"),
        (11033, "TRANSMUTANSTEIN Server Manager TCP"),
        (5201, "ZORGATH Web Portal API (HTTPS)"),
        (5557, "DAWNBRINGER Web Portal UI (HTTPS)"),
        (1433, "SQL Server 2022 Database (TDS)"),
        (6379, "Redis Distributed Cache (RESP)"),
        (11234, "COMPEL Local Control Plane (Tier L HTTP)"),
        (11235, "HoN Local Dedicated Server 1 (Tier L UDP)"),
        (11334, "COMPEL Docker Control Plane (Tier D HTTP)"),
        (11335, "HoN Docker Dedicated Server 1 (Tier D UDP)"),
        (11434, "COMPEL K3s Control Plane (Tier K HTTP)"),
        (11435, "HoN K3s Dedicated Server 1 (Tier K UDP)"),
        (11399, "HoN In-Process Practice / Bot Match (UDP)"),
        (11255, "HoN Game Server Node (UDP/TCP)")
    ];

    public Task<List<PortStatus>> ScanRequiredPortsAsync(CancellationToken cancellationToken = default)
    {
        List<PortStatus> statuses = [];

        foreach ((int port, string name) in RequiredPorts)
        {
            if (cancellationToken.IsCancellationRequested) break;

            bool available = IsPortAvailable(port);
            statuses.Add(new PortStatus
            {
                Port = port,
                ServiceName = name,
                Protocol = "TCP/UDP",
                IsAvailable = available
            });
        }

        return Task.FromResult(statuses);
    }

    public bool IsPortAvailable(int port)
    {
        TcpListener? listener = null;
        try
        {
            listener = new TcpListener(IPAddress.Loopback, port);
            listener.Start();
            return true;
        }
        catch (SocketException)
        {
            return false;
        }
        finally
        {
            listener?.Stop();
        }
    }
}
