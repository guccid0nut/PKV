// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2022-2026 [K]ONGOR and Contributors
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV) -- Upstream lineage: https://github.com/Project-KONGOR-Open-Source

namespace COMPEL.Configuration;

/// <summary>
///     The host-supplied configuration that governs how COMPEL launches and supervises the Heroes Of Newerth match server
///     manager.
///     These are the cross-platform equivalents of the keys that the legacy COMPEL stored in its "COMPEL.JSON" file.
/// </summary>
/// <summary>
///     Governs whether supervised K2 dedicated servers enforce <c>CArchive::ExamineChecksums</c>.
/// </summary>
public enum ExamineChecksumsPolicy
{
    /// <summary>
    ///     Enforce archive checksums on Ranked/TMM workers; allow mods on Queue Arcade or <c>AllowCustomMods=true</c> workers.
    /// </summary>
    RankedOnly = 0,

    /// <summary>
    ///     Always enforce <c>CArchive::ExamineChecksums</c> (<c>KONGOR_EXAMINE_ARCHIVE_CHECKSUMS=1</c>) on every worker.
    /// </summary>
    Always = 1,

    /// <summary>
    ///     Allow mods (<c>KONGOR_EXAMINE_ARCHIVE_CHECKSUMS=0</c>) on every worker in this fleet.
    /// </summary>
    Disabled = 2
}

public sealed class MatchServerManagerOptions
{
    /// <summary>
    ///     The name of the Project KONGOR user which will host the game servers.
    /// </summary>
    public string UserName { get; set; } = string.Empty;

    /// <summary>
    ///     The password of the Project KONGOR user which will host the game servers.
    /// </summary>
    public string Password { get; set; } = string.Empty;

    /// <summary>
    ///     The number of server instances to spawn. Must be between one and the port range window (100).
    /// </summary>
    public int Instances { get; set; } = 1;

    /// <summary>
    ///     The target number of idle/warm match server instances to keep actively running and ready for matches.
    ///     When instances are completed or idle, COMPEL ensures up to this target remain running while excess
    ///     standby workers are put to sleep/stopped to save system resources. Defaults to 1.
    /// </summary>
    public int WarmInstancesTarget { get; set; } = 0;

    /// <summary>
    ///     The entry point for game servers and the server manager: "localhost" for local development, "PUBLIC" to auto-detect
    ///     the public IP address, a LAN or public IP address, or a host name to resolve.
    /// </summary>
    public string Gateway { get; set; } = "masterserver.newbs.io";

    /// <summary>
    ///     The server region. In order for the server to be TMM-compatible, only the values "USW", "USE", "EU", "AU", "BR",
    ///     "RU", "SEA", and "NEWERTH" are valid.
    /// </summary>
    public string Location { get; set; } = "USE";

    /// <summary>
    ///     The base name of the game server instances. The server manager appends the one-based index of each instance to this
    ///     base name.
    /// </summary>
    public string ServerNamePrefix { get; set; } = "KONGOR ARENA";

    /// <summary>
    ///     Optional fleet tier identifier ("L" for Local, "D" for Docker, "K" for K3s / Agones).
    ///     When not set, COMPEL auto-detects based on the runtime environment (Agones, Container, or Native).
    /// </summary>
    public string? FleetTier { get; set; }

    /// <summary>
    ///     Whether to run the proxy in front of the game servers. The proxy exposes public ports offset by
    ///     <see cref="PortPlan.ProxyPublicOffset" />, forwards them to the local server ports, and authenticates clients with
    ///     the challenge protocol they require on that port range.
    /// </summary>
    public bool UseProxy { get; set; } = false;

    /// <summary>
    ///     Whether the UDP proxy issues legacy 18-byte 0x40 watermark challenge packets and requires 40-byte watermark headers
    ///     on client datagrams. Defaults to false because both the Project Kongor client (startup.cfg) and dedicated workers
    ///     (DedicatedWorkerArguments) run with net_enableWatermark = false.
    /// </summary>
    public bool EnableLegacyWatermarkChallenge { get; set; } = false;

    /// <summary>
    ///     Whether to enable BBR UDP congestion control and token-bucket pacing on proxy relays.
    ///     Default is false (opt-in safety kill-switch).
    /// </summary>
    public bool UseBbrPacing { get; set; } = false;

    /// <summary>
    ///     Whether to parse and interpolate microsecond sub-tick player input commands.
    ///     Default is false (opt-in safety kill-switch).
    /// </summary>
    public bool UseSubTickNetcode { get; set; } = false;

    /// <summary>
    ///     The offset from the start of the valid game and voice port ranges at which the ports used at runtime begin.
    /// </summary>
    public int PortRangeOffset { get; set; }

    /// <summary>
    ///     The base directory beneath which the match server manager and servers write their runtime artefacts (replays and
    ///     logs). This value is either the alias "DEFAULT" or a fully qualified path used as the base profile directory.
    ///     "DEFAULT" places the artefacts beneath the host account's profile (its "Documents/Heroes of Newerth x64" tree),
    ///     which is where Heroes Of Newerth writes everything else.
    ///     This applies on Windows only; the Linux server build writes to a fixed location and ignores this setting.
    /// </summary>
    public string RuntimeArtefactsPath { get; set; } = "DEFAULT";

    /// <summary>
    ///     The SignalR Live Matches Hub endpoint URL on Dawnbringer / NEXUS Portal for real-time in-memory telemetry
    ///     streaming.
    /// </summary>
    public string LiveHubUrl { get; set; } = "https://localhost:5557/hubs/live-matches";

    /// <summary>
    ///     The master server address passed to the match server manager.
    ///     Defaults to "masterserver.newbs.io" or the configured dotnet user secret / environment variable.
    /// </summary>
    public string MasterServerAddress { get; set; } = "masterserver.newbs.io";

    /// <summary>
    ///     Whether to enable RFC 8878 Zstandard Netcode Dictionary compression on outbound entity delta snapshots
    ///     exceeding the MTU threshold to eliminate IP fragmentation and reduce bandwidth.
    /// </summary>
    public bool EnableZstdNetcodeCompression { get; set; } = false;

    /// <summary>
    ///     Whether to run the background disk log watcher service. Default is true on Windows because K2 dedicated
    ///     server binaries bypass stdout redirection and write runtime logs to disk.
    /// </summary>
    public bool EnableDiskLogWatcher { get; set; } = OperatingSystem.IsWindows();

    /// <summary>
    ///     Whether to allow untrusted/self-signed SSL/TLS certificates when connecting to the SignalR Live Matches Hub.
    ///     Default is false for strict production security.
    /// </summary>
    public bool AllowUntrustedCertificates { get; set; } = false;

    /// <summary>
    ///     Whether to immediately start dedicated server instances on startup.
    ///     When false, COMPEL starts in standby mode listening on its control plane without launching workers
    ///     until an explicit start request is received via the control plane or GUI.
    /// </summary>
    public bool AutoStart { get; set; } = true;

    /// <summary>
    ///     Whether to enable the Phase 8D zero-downtime crash resilience auto-reboot loop.
    ///     When enabled, worker crash exits trigger an immediate &lt;500ms recovery path.
    /// </summary>
    public bool EnableCrashResilienceLoop { get; set; } = true;

    /// <summary>
    ///     Target duration in milliseconds for the fast auto-reboot path (default: 500ms).
    /// </summary>
    public int FastRebootTargetMs { get; set; } = 500;

    /// <summary>
    ///     Whether to automatically synthesize and apply ABI-safe rel32 detours to faulting RVAs.
    /// </summary>
    public bool AutoApplyDetourPatches { get; set; } = true;

    /// <summary>
    ///     Maximum consecutive rapid crashes (within 30 seconds) allowed before throttling
    ///     the worker back to the standard 5-second restart backoff.
    /// </summary>
    public int MaxConsecutiveFastReboots { get; set; } = 3;

    /// <summary>
    ///     Number of matches a dedicated worker process should host before being cleanly recycled to eliminate heap fragmentation.
    ///     Defaults to 1 (clean restart after each match for pristine state). Set to 0 to disable automatic match-count recycling.
    /// </summary>
    public int RecycleEveryNMatches { get; set; } = 1;

    /// <summary>
    ///     Grace period (seconds) to wait for reconnection after the connected client count of a worker with an
    ///     active match drops to zero, before the match is declared abandoned and the worker recycled.
    ///     Each new zero-client event supersedes any pending grace timer for the same worker.
    /// </summary>
    public int DisconnectGraceSecondsInMatch { get; set; } = 30;

    /// <summary>
    ///     Grace period (seconds) to wait after the connected client count of a worker without an active match
    ///     (lobby / pre-match) drops to zero, before the worker is recycled to restore a pristine listening socket.
    /// </summary>
    public int DisconnectGraceSecondsPreMatch { get; set; } = 5;

    /// <summary>
    ///     Maximum age (seconds) an arranged or player-hosted lobby with zero connected players (0 -> 0 zero-join)
    ///     may remain open before COMPEL automatically recycles the worker back to an idle listening state.
    ///     Set to 0 to disable automatic zero-join empty-lobby reaping.
    /// </summary>
    public int EmptyLobbyReapSeconds { get; set; } = 60;

    /// <summary>
    ///     Whether to use SHA-256 password hashing instead of legacy MD5 when authenticating with the Master Server.
    ///     Defaults to false for legacy compatibility.
    /// </summary>
    public bool UseSha256Authentication { get; set; } = false;

    /// <summary>
    ///     Canonical simulation tickrate in Hz for K2 dedicated game servers (20 Hz = 50ms tick).
    ///     Higher tickrates (e.g. 30 Hz / 60 Hz) cause integer truncation in CHostServer::Frame,
    ///     breaking IEntityState Phase-1 expiration and 20 Hz-hardcoded &lt;onframe&gt; scripts.
    /// </summary>
    public const int CanonicalSimulationTickRate = 20;

    private int _serverTickRate = CanonicalSimulationTickRate;

    /// <summary>
    ///     The simulation tickrate in Hz for dedicated game servers. Default is 20 (K2 canonical 50ms step; higher values cause 16ms/33ms truncation in CHostServer::Frame, breaking IEntityState Phase-1 expiration and 20 Hz &lt;onframe&gt; scripts). Values clamped between 20 and 60.
    ///     Default is 20 (canonical 20 Hz / 50ms simulation step). Values are clamped between 20 and 60.
    /// </summary>
    public int ServerTickRate
    {
        get => _serverTickRate;
        set => _serverTickRate = Math.Clamp(value, 20, 60);
    }

    /// <summary>
    ///     Governs the host CPU scheduling priority, core affinity targeting, and operating system yielding behavior.
    ///     Defaults to Background mode for zero desktop disruption.
    /// </summary>
    public HostPriorityMode PriorityMode { get; set; } = HostPriorityMode.Background;

    /// <summary>
    ///     Whether to automatically symbolize dedicated worker minidumps and crash logs via Ghidra MCP
    ///     and generate structured diagnostic issue reports upon abnormal worker termination.
    /// </summary>
    public bool EnableAutomatedCrashSymbolization { get; set; } = true;

    /// <summary>
    ///     Whether to automatically append synthesized crash diagnostic drafts to docs/ISSUES.md.
    /// </summary>
    public bool AutoAppendToIssuesLog { get; set; } = true;

    /// <summary>
    ///     Directory where symbolized crash markdown artifacts and minidumps are archived.
    /// </summary>
    public string CrashReportsDirectory { get; set; } = "logs/crashes";

    /// <summary>
    ///     Whether to enable real-time chunked binary Write-Ahead Logging (WAL) for in-flight match replays
    ///     to prevent data loss and corruption during abrupt host power cuts or node panics.
    /// </summary>
    public bool EnableReplayWal { get; set; } = true;

    /// <summary>
    ///     Directory where active match replay WAL journals (.wal) are written and staged.
    /// </summary>
    public string ReplayWalDirectory { get; set; } = "logs/wal";

    /// <summary>
    ///     Size of each sequential WAL chunk in bytes (default: 64 KB = 65,536 bytes).
    /// </summary>
    public int ReplayWalChunkSizeBytes { get; set; } = 64 * 1024;

    /// <summary>
    ///     Maximum duration in milliseconds to buffer unwritten replay events before forcing a WAL disk flush.
    /// </summary>
    public int ReplayWalFlushIntervalMs { get; set; } = 1000;

    /// <summary>
    ///     Enables Queue Mini-Game ("While You Wait" Arcade) mode on all supervised workers in this COMPEL fleet.
    ///     Disables AFK kicks, replay recording, stat submission, empty-lobby reaping, and 0-client disconnect abandonment
    ///     so queued matchmaking players can seamlessly drop in and be pulled out when Ranked matches pop.
    /// </summary>
    public bool QueueArcadeMode { get; set; } = false;

    /// <summary>
    ///     Target maximum duration in seconds for each Queue Arcade mini-game round before automatic map rotation (default: 600s = 10m).
    /// </summary>
    public int QueueArcadeRoundDurationSeconds { get; set; } = 600;

    /// <summary>
    ///     Explicit anti-cheat archive checksum enforcement policy passed to the native guard
    ///     (<c>KONGOR_EXAMINE_ARCHIVE_CHECKSUMS</c>) on supervised dedicated server workers.
    ///     Defaults to <see cref="ExamineChecksumsPolicy.RankedOnly"/> (enforces checksums on Ranked/TMM servers,
    ///     allows custom mods on Queue Arcade or explicitly custom/modded servers).
    /// </summary>
    public ExamineChecksumsPolicy ExamineChecksumsPolicy { get; set; } = ExamineChecksumsPolicy.RankedOnly;

    /// <summary>
    ///     Marks this COMPEL fleet as a custom/modded server pool. Under <see cref="ExamineChecksumsPolicy.RankedOnly"/>,
    ///     setting <c>AllowCustomMods = true</c> disables archive checksum enforcement (<c>KONGOR_EXAMINE_ARCHIVE_CHECKSUMS=0</c>)
    ///     so community maps and cosmetic/gameplay mods load cleanly without affecting Ranked fleets.
    /// </summary>
    public bool AllowCustomMods { get; set; } = false;
}

