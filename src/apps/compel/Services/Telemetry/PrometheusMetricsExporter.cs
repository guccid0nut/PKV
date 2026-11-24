// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

// ============================================================================
// File: PrometheusMetricsExporter.cs
// System: COMPEL - Observability & Telemetry Control Plane
// Policy: Strict Explicit Typing (NO 'var')
// ============================================================================

using System.Globalization;
using System.Text;
using COMPEL.Configuration;
using COMPEL.Services.Proxy;
using COMPEL.Services.Supervision;

namespace COMPEL.Services.Telemetry;

/// <summary>
/// Formats real-time COMPEL supervisor, UDP proxy, and worker telemetry into Prometheus exposition format
/// (text/plain; version=0.0.4; charset=utf-8) for seamless scraping by Prometheus, VictoriaMetrics, and Grafana Agent.
/// </summary>
public static class PrometheusMetricsExporter
{
    public static string ExportMetrics(
        MatchServerManagerSupervisor supervisor,
        UDPProxyService? proxy,
        MatchServerManagerOptions options)
    {
        StringBuilder sb = new(4096);

        // Header and Node Info
        sb.AppendLine("# HELP compel_up Whether the COMPEL supervisor node is operational");
        sb.AppendLine("# TYPE compel_up gauge");
        sb.AppendLine("compel_up 1");
        sb.AppendLine();

        string version = GeneratedVersionInformation.VersionString;
        string location = options.Location ?? "NEWERTH";
        string prefix = options.ServerNamePrefix ?? "KONGOR";

        sb.AppendLine("# HELP compel_info Node and build metadata");
        sb.AppendLine("# TYPE compel_info gauge");
        sb.Append("compel_info{version=\"").Append(version)
            .Append("\",location=\"").Append(location)
            .Append("\",prefix=\"").Append(prefix)
            .AppendLine("\"} 1");
        sb.AppendLine();

        // Supervisor Node State
        sb.AppendLine("# HELP compel_supervisor_desired_running Desired running state of fleet supervisor");
        sb.AppendLine("# TYPE compel_supervisor_desired_running gauge");
        sb.Append("compel_supervisor_desired_running ").AppendLine(supervisor.DesiredRunning ? "1" : "0");
        sb.AppendLine();

        sb.AppendLine("# HELP compel_supervisor_active_workers Active workers currently running");
        sb.AppendLine("# TYPE compel_supervisor_active_workers gauge");
        sb.Append("compel_supervisor_active_workers ").AppendLine(supervisor.ActiveWorkerCount.ToString(CultureInfo.InvariantCulture));
        sb.AppendLine();

        sb.AppendLine("# HELP compel_supervisor_total_workers Total configured dedicated workers");
        sb.AppendLine("# TYPE compel_supervisor_total_workers gauge");
        sb.Append("compel_supervisor_total_workers ").AppendLine(supervisor.Workers.Count.ToString(CultureInfo.InvariantCulture));
        sb.AppendLine();

        // UDP Proxy Observability
        if (proxy != null)
        {
            sb.AppendLine("# HELP compel_proxy_running Whether the COMPEL UDP proxy service is running");
            sb.AppendLine("# TYPE compel_proxy_running gauge");
            sb.Append("compel_proxy_running ").AppendLine(proxy.IsRunning ? "1" : "0");
            sb.AppendLine();

            sb.AppendLine("# HELP compel_proxy_under_attack Whether the proxy is currently detecting a UDP flood attack");
            sb.AppendLine("# TYPE compel_proxy_under_attack gauge");
            sb.Append("compel_proxy_under_attack ").AppendLine(proxy.IsUnderAttack ? "1" : "0");
            sb.AppendLine();

            sb.AppendLine("# HELP compel_proxy_dropped_datagrams_total Total datagrams dropped by proxy");
            sb.AppendLine("# TYPE compel_proxy_dropped_datagrams_total counter");
            sb.Append("compel_proxy_dropped_datagrams_total ").AppendLine(proxy.DroppedDatagramCount.ToString(CultureInfo.InvariantCulture));
            sb.AppendLine();

            sb.AppendLine("# HELP compel_proxy_failed_forwarders Total forwarders in failed state");
            sb.AppendLine("# TYPE compel_proxy_failed_forwarders gauge");
            sb.Append("compel_proxy_failed_forwarders ").AppendLine(proxy.FailedForwarderCount.ToString(CultureInfo.InvariantCulture));
            sb.AppendLine();
        }

        // Per-Worker Metrics
        if (supervisor.Workers.Count > 0)
        {
            // compel_worker_running
            sb.AppendLine("# HELP compel_worker_running Whether worker process is running");
            sb.AppendLine("# TYPE compel_worker_running gauge");
            foreach (DedicatedServerWorker w in supervisor.Workers)
            {
                AppendWorkerLabels(sb, "compel_worker_running", w);
                sb.Append(' ').AppendLine(w.IsRunning ? "1" : "0");
            }
            sb.AppendLine();

            // compel_worker_uptime_seconds
            sb.AppendLine("# HELP compel_worker_uptime_seconds Dedicated worker uptime in seconds");
            sb.AppendLine("# TYPE compel_worker_uptime_seconds gauge");
            foreach (DedicatedServerWorker w in supervisor.Workers)
            {
                AppendWorkerLabels(sb, "compel_worker_uptime_seconds", w);
                sb.Append(' ').AppendLine(w.UptimeSeconds.ToString("F1", CultureInfo.InvariantCulture));
            }
            sb.AppendLine();

            // compel_worker_working_set_bytes
            sb.AppendLine("# HELP compel_worker_working_set_bytes Worker working set memory in bytes");
            sb.AppendLine("# TYPE compel_worker_working_set_bytes gauge");
            foreach (DedicatedServerWorker w in supervisor.Workers)
            {
                AppendWorkerLabels(sb, "compel_worker_working_set_bytes", w);
                sb.Append(' ').AppendLine(w.WorkingSetBytes.ToString(CultureInfo.InvariantCulture));
            }
            sb.AppendLine();

            // compel_worker_crashes_total
            sb.AppendLine("# HELP compel_worker_crashes_total Cumulative crashes for dedicated worker");
            sb.AppendLine("# TYPE compel_worker_crashes_total counter");
            foreach (DedicatedServerWorker w in supervisor.Workers)
            {
                AppendWorkerLabels(sb, "compel_worker_crashes_total", w);
                sb.Append(' ').AppendLine(w.CrashCount.ToString(CultureInfo.InvariantCulture));
            }
            sb.AppendLine();

            // compel_worker_tickrate_hz
            sb.AppendLine("# HELP compel_worker_tickrate_hz Dedicated worker simulation tickrate in Hz");
            sb.AppendLine("# TYPE compel_worker_tickrate_hz gauge");
            foreach (DedicatedServerWorker w in supervisor.Workers)
            {
                AppendWorkerLabels(sb, "compel_worker_tickrate_hz", w);
                sb.Append(' ').AppendLine(w.TickrateTracker.CurrentTickrateHz.ToString("F2", CultureInfo.InvariantCulture));
            }
            sb.AppendLine();

            // compel_worker_tick_variance_ms
            sb.AppendLine("# HELP compel_worker_tick_variance_ms Dedicated worker tick interval standard deviation in ms");
            sb.AppendLine("# TYPE compel_worker_tick_variance_ms gauge");
            foreach (DedicatedServerWorker w in supervisor.Workers)
            {
                AppendWorkerLabels(sb, "compel_worker_tick_variance_ms", w);
                sb.Append(' ').AppendLine(w.TickrateTracker.TickVarianceMs.ToString("F3", CultureInfo.InvariantCulture));
            }
            sb.AppendLine();

            // compel_worker_connected_clients
            sb.AppendLine("# HELP compel_worker_connected_clients Number of connected clients");
            sb.AppendLine("# TYPE compel_worker_connected_clients gauge");
            foreach (DedicatedServerWorker w in supervisor.Workers)
            {
                AppendWorkerLabels(sb, "compel_worker_connected_clients", w);
                sb.Append(' ').AppendLine(w.ConnectedClients.ToString(CultureInfo.InvariantCulture));
            }
            sb.AppendLine();

            // compel_worker_matches_hosted_total
            sb.AppendLine("# HELP compel_worker_matches_hosted_total Total completed matches hosted by worker");
            sb.AppendLine("# TYPE compel_worker_matches_hosted_total counter");
            foreach (DedicatedServerWorker w in supervisor.Workers)
            {
                AppendWorkerLabels(sb, "compel_worker_matches_hosted_total", w);
                sb.Append(' ').AppendLine(w.TotalLifetimeMatchesHosted.ToString(CultureInfo.InvariantCulture));
            }
            sb.AppendLine();

            // compel_worker_is_draining
            sb.AppendLine("# HELP compel_worker_is_draining Whether worker is currently marked for draining");
            sb.AppendLine("# TYPE compel_worker_is_draining gauge");
            foreach (DedicatedServerWorker w in supervisor.Workers)
            {
                AppendWorkerLabels(sb, "compel_worker_is_draining", w);
                sb.Append(' ').AppendLine(w.IsDraining ? "1" : "0");
            }
            sb.AppendLine();
        }

        return sb.ToString();
    }

    private static void AppendWorkerLabels(StringBuilder sb, string metricName, DedicatedServerWorker worker)
    {
        sb.Append(metricName)
            .Append("{instance=\"").Append(worker.InstanceIndex.ToString(CultureInfo.InvariantCulture))
            .Append("\",port=\"").Append(worker.Port.ToString(CultureInfo.InvariantCulture))
            .Append("\",public_port=\"").Append(worker.PublicGamePort.ToString(CultureInfo.InvariantCulture))
            .Append("\"}");
    }
}
