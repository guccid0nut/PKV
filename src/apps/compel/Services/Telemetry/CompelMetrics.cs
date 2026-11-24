// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

using System.Diagnostics.Metrics;

namespace COMPEL.Services.Telemetry;

/// <summary>
///     High-performance diagnostic metrics for COMPEL match server supervisor, UDP proxy,
///     and telemetry relay. Exposes metrics compatible with OpenTelemetry, Prometheus, and dotnet-counters.
/// </summary>
public static class CompelMetrics
{
    public const string MeterName = "COMPEL.ServerEngine";
    public const string MeterVersion = "5.0.2";

    private static readonly Meter s_meter = new(MeterName, MeterVersion);

    // UDP Proxy Counters
    public static readonly Counter<long> UdpDatagramsReceived = s_meter.CreateCounter<long>(
        "compel.proxy.udp.datagrams_received",
        "{datagrams}",
        "Total UDP datagrams received on public front ports");

    public static readonly Counter<long> UdpDatagramsRelayed = s_meter.CreateCounter<long>(
        "compel.proxy.udp.datagrams_relayed",
        "{datagrams}",
        "Total UDP datagrams successfully relayed between clients and match servers");

    public static readonly Counter<long> UdpBytesTransferred = s_meter.CreateCounter<long>(
        "compel.proxy.udp.bytes_transferred",
        "By",
        "Total UDP payload bytes forwarded through the proxy");

    public static readonly Counter<long> UdpInboundQueueDropped = s_meter.CreateCounter<long>(
        "compel.proxy.udp.inbound_queue_dropped",
        "{datagrams}",
        "Total inbound client datagrams dropped due to per-session ring buffer saturation under burst flood");

    public static readonly Counter<long> UdpBatchedDatagramsDrained = s_meter.CreateCounter<long>(
        "compel.proxy.udp.batched_datagrams_drained",
        "{datagrams}",
        "Total UDP datagrams drained synchronously in non-blocking batches without task context switching");

    // Telemetry Relay Counters
    public static readonly Counter<long> TelemetryTicksDispatched = s_meter.CreateCounter<long>(
        "compel.telemetry.ticks_dispatched",
        "{ticks}",
        "Total live match telemetry tick payloads sent to SignalR / HTTP");

    public static readonly Counter<long> TelemetryLogLinesProcessed = s_meter.CreateCounter<long>(
        "compel.telemetry.stdout_lines_processed",
        "{lines}",
        "Total stdout lines parsed from game server processes");

    public static readonly Counter<long> TelemetryEventsCaptured = s_meter.CreateCounter<long>(
        "compel.telemetry.events_captured",
        "{events}",
        "Total in-game combat and state events parsed from stdout");

    public static readonly Counter<long> StreamLinesDropped = s_meter.CreateCounter<long>(
        "compel.telemetry.stdout_lines_dropped",
        "{lines}",
        "Total stdout/stderr lines dropped due to channel buffer backpressure under severe log flood");

    public static readonly Counter<long> TelemetryTicksDropped = s_meter.CreateCounter<long>(
        "compel.telemetry.ticks_dropped",
        "{ticks}",
        "Total live match telemetry tick payloads dropped due to channel buffer capacity");

    // Zstandard Netcode Delta Compression Metrics
    public static readonly Counter<long> ZstdDeltaBytesUncompressed = s_meter.CreateCounter<long>(
        "compel.netcode.zstd.bytes_uncompressed",
        "By",
        "Total uncompressed entity delta bytes before RFC 8878 Zstd compression");

    public static readonly Counter<long> ZstdDeltaBytesCompressed = s_meter.CreateCounter<long>(
        "compel.netcode.zstd.bytes_compressed",
        "By",
        "Total compressed entity delta bytes transmitted across network");

    public static readonly Counter<long> ZstdDeltaDatagramsCompressed = s_meter.CreateCounter<long>(
        "compel.netcode.zstd.datagrams_compressed",
        "{datagrams}",
        "Total UDP entity delta datagrams compressed using hon_netcode dictionary");

    public static readonly Counter<long> ZstdUdpFragmentationPrevented = s_meter.CreateCounter<long>(
        "compel.netcode.zstd.fragmentation_prevented",
        "{packets}",
        "Total UDP snapshot bursts kept below Path MTU threshold to eliminate IP fragmentation");

    public static readonly Counter<long> PmtuSnapshotsClamped = s_meter.CreateCounter<long>(
        "compel.netcode.pmtu.snapshots_clamped",
        "{snapshots}",
        "Total outgoing server delta snapshots clamped to prevent exceeding active Path MTU ceiling");

    // Dedicated Server Simulation & Tickrate Observability
    public static readonly Histogram<double> ServerTickrateHz = s_meter.CreateHistogram<double>(
        "compel.server.tickrate_hz",
        "Hz",
        "Rolling dedicated server simulation tickrate in Hertz");

    public static readonly Histogram<double> ServerTickDurationMs = s_meter.CreateHistogram<double>(
        "compel.server.tick_duration_ms",
        "ms",
        "Dedicated server frame tick execution duration in milliseconds");

    public static readonly Histogram<double> ServerTickVarianceMs = s_meter.CreateHistogram<double>(
        "compel.server.tick_variance_ms",
        "ms",
        "Dedicated server tick interval standard deviation in milliseconds");

    public static readonly Counter<long> ServerSlowTicks = s_meter.CreateCounter<long>(
        "compel.server.slow_ticks",
        "{ticks}",
        "Total ticks exceeding 1.25x target duration (simulation lag frames)");

    // Netcode FEC, Jitter, and Anomaly Shield Metrics
    public static readonly Counter<long> FecPacketsRecovered = s_meter.CreateCounter<long>(
        "compel.netcode.fec.packets_recovered",
        "{packets}",
        "Total missing UDP datagrams successfully recovered via XOR FEC parity");

    public static readonly Counter<long> FecRecoveryFailed = s_meter.CreateCounter<long>(
        "compel.netcode.fec.recovery_failed",
        "{packets}",
        "Total packet loss bursts exceeding XOR FEC block recovery capacity");

    public static readonly Counter<long> FecParityPacketsGenerated = s_meter.CreateCounter<long>(
        "compel.netcode.fec.parity_packets_generated",
        "{packets}",
        "Total XOR parity datagrams synthesized and transmitted");

    public static readonly Histogram<double> JitterDelayMs = s_meter.CreateHistogram<double>(
        "compel.netcode.jitter.delay_ms",
        "ms",
        "Adaptive jitter buffer playout delay in milliseconds");

    public static readonly Counter<long> ShieldReplaysDropped = s_meter.CreateCounter<long>(
        "compel.netcode.shield.replays_dropped",
        "{packets}",
        "Total duplicate or stale UDP datagrams dropped by RFC 4303 64-bit sliding anti-replay");

    public static readonly Counter<long> ShieldBurstViolations = s_meter.CreateCounter<long>(
        "compel.netcode.shield.burst_violations",
        "{violations}",
        "Total micro-burst UDP rate limit violations detected and dropped");

    // Modern Envelope & AEAD Transport Security Metrics
    public static readonly Counter<long> ModernEnvelopesAdmitted = s_meter.CreateCounter<long>(
        "compel.netcode.modern.envelopes_admitted",
        "{envelopes}",
        "Total session-authenticated modern UDP envelopes admitted and unwrapped");

    public static readonly Counter<long> ModernEnvelopesTampered = s_meter.CreateCounter<long>(
        "compel.netcode.modern.envelopes_tampered",
        "{envelopes}",
        "Total modern UDP envelopes dropped due to Poly1305 MAC tag authentication failure");

    public static readonly Counter<long> ModernEnvelopesReplayed = s_meter.CreateCounter<long>(
        "compel.netcode.modern.envelopes_replayed",
        "{envelopes}",
        "Total modern UDP envelopes dropped due to sequence replay or stale sliding window");

    // Closed-Loop BBR Congestion Control & Inflight Metrics
    public static readonly Counter<long> BbrAcksProcessed = s_meter.CreateCounter<long>(
        "compel.netcode.bbr.acks_processed",
        "{acks}",
        "Total modern ACK envelopes processed for closed-loop BBR delivery rate and RTT estimation");

    public static readonly Histogram<long> BbrEstimatedBandwidthBps = s_meter.CreateHistogram<long>(
        "compel.netcode.bbr.estimated_bandwidth_bps",
        "By/s",
        "Rolling delivery rate bottleneck bandwidth estimate in bytes per second");

    public static readonly Histogram<double> BbrMinRttMs = s_meter.CreateHistogram<double>(
        "compel.netcode.bbr.min_rtt_ms",
        "ms",
        "Filtered physical minimum round-trip time in milliseconds");

    public static readonly Counter<long> BbrInflightCapped = s_meter.CreateCounter<long>(
        "compel.netcode.bbr.inflight_capped",
        "{events}",
        "Total packet transmissions delayed or throttled due to target congestion window saturation");

    // Dynamic Path MTU Discovery (PLPMTUD, RFC 4821) Metrics
    public static readonly Counter<long> PmtuProbesDispatched = s_meter.CreateCounter<long>(
        "compel.netcode.pmtu.probes_dispatched",
        "{probes}",
        "Total RFC 4821 PLPMTUD probe datagrams dispatched to validate MTU plateaus");

    public static readonly Counter<long> PmtuProbesConfirmed = s_meter.CreateCounter<long>(
        "compel.netcode.pmtu.probes_confirmed",
        "{probes}",
        "Total RFC 4821 PLPMTUD probes acknowledged, raising the effective Path MTU");

    public static readonly Counter<long> PmtuBlackHolesRecovered = s_meter.CreateCounter<long>(
        "compel.netcode.pmtu.black_holes_recovered",
        "{events}",
        "Total RFC 4821 black hole drops recovered by falling back to base 1200-byte MTU");

    public static readonly Histogram<int> PmtuCurrentMtuBytes = s_meter.CreateHistogram<int>(
        "compel.netcode.pmtu.current_mtu_bytes",
        "By",
        "Discovered Path MTU ceiling in bytes for active player sessions");

    // Adaptive Delta Rate-Limiting Metrics
    public static readonly Counter<long> RateLimitingShedEntities = s_meter.CreateCounter<long>(
        "compel.netcode.rate_limiting.shed_entities",
        "{entities}",
        "Total low-priority cosmetic and secondary summon entity deltas pruned to prevent bufferbloat");

    public static readonly Counter<long> RateLimitingDownsampledCreeps = s_meter.CreateCounter<long>(
        "compel.netcode.rate_limiting.downsampled_creeps",
        "{entities}",
        "Total creep and neutral entity deltas downsampled to half-rate during link congestion");

    public static readonly Counter<long> RateLimitingCriticalActivations = s_meter.CreateCounter<long>(
        "compel.netcode.rate_limiting.critical_activations",
        "{events}",
        "Total ticks where severe congestion triggered Hero VIP-only transmission mode");

    // Zero-Downtime Socket Rebinding & Reclamation Metrics [SR-08]
    public static readonly Counter<long> SocketReclaimsAttempted = s_meter.CreateCounter<long>(
        "compel.socket.reclaims.attempted",
        "{attempts}",
        "Total attempts to reclaim occupied UDP/TCP ports prior to worker spawn or proxy reload");

    public static readonly Counter<long> SocketReclaimsSuccessful = s_meter.CreateCounter<long>(
        "compel.socket.reclaims.successful",
        "{successes}",
        "Total successful port reclaims clearing lingering kernel socket states");

    public static readonly Counter<long> SocketRebindsFastPath = s_meter.CreateCounter<long>(
        "compel.socket.rebinds.fast_path",
        "{rebinds}",
        "Total sockets bound immediately on recycled ports via SO_REUSEADDR/SO_REUSEPORT without delay");

    public static readonly Counter<long> SocketZombiePidsTerminated = s_meter.CreateCounter<long>(
        "compel.socket.zombie_pids.terminated",
        "{pids}",
        "Total orphaned or zombie processes surgically terminated to free game and voice ports");

    // Fleet Match Recycling Observability
    public static readonly Counter<long> MatchesHostedTotal = s_meter.CreateCounter<long>(
        "compel.server.matches_hosted_total",
        "{matches}",
        "Total completed matches hosted across dedicated server workers");

    public static readonly Counter<long> WorkerRecyclesTotal = s_meter.CreateCounter<long>(
        "compel.server.worker_recycles_total",
        "{recycles}",
        "Total clean worker recycling operations executed");
}


