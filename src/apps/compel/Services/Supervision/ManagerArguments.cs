// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2022-2026 [K]ONGOR and Contributors
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV) -- Upstream lineage: https://github.com/Project-KONGOR-Open-Source

namespace COMPEL.Services.Supervision;

/// <summary>
///     Builds the command-line arguments passed to the Heroes Of Newerth manager, faithfully reproducing the configuration
///     flags the legacy COMPEL set via its "-execute" string.
/// </summary>
internal static class ManagerArguments
{
    /// <summary>
    ///     Builds the argument vector for launching the manager: "-manager", "-noconfig", "-execute", "\"Set ...;Set ...\"",
    ///     "-masterserver", "host:port".
    ///     The "-execute" payload carries its own literal double quotes because Heroes Of Newerth re-tokenises its command
    ///     line and requires them; the caller passes the vector verbatim (via <see cref="ProcessStartInfo.ArgumentList" /> on
    ///     Linux, or joined into <see cref="ProcessStartInfo.Arguments" /> on Windows) so those quotes survive to the process
    ///     on both platforms.
    /// </summary>
    public static string[] Build(MatchServerManagerOptions options, PortPlan ports, string serverAddress,
        string masterServerHostAndPort, string? installationDirectory = null)
    {
        int processorCount = Environment.ProcessorCount;

        // The Order Of These Settings Is Immaterial: Each "Set" Command Is Applied Independently By The Manager.
        Dictionary<string, string> settings = new()
        {
            // Append ':' So Game Server Instances Can Be Mapped To An Account Name (e.g. KONGOR:1, KONGOR:2). The Manager Appends An Incremental Index To This Value.
            ["man_masterLogin"] = options.UserName + ":",
            ["man_masterPassword"] = options.Password,
            ["man_numSlaveAccounts"] = options.Instances.ToString(),
            ["man_startServerPort"] = ports.LocalGameStart.ToString(),
            ["man_endServerPort"] = ports.LocalGameEnd.ToString(),

            // Historically Misnamed: This Is Effectively "man_voiceStartPort". No Proxy Is Involved In The Manager's Voice Port Allocation.
            ["man_voiceProxyStartPort"] = ports.LocalVoiceStart.ToString(),
            ["man_voiceProxyEndPort"] = ports.LocalVoiceEnd.ToString(),

            ["man_maxServers"] = processorCount.ToString(),
            ["man_enableProxy"] = options.UseProxy ? "true" : "false",
            ["man_broadcastSlaves"] = "true",
            ["man_autoServersPerCPU"] = "1",
            ["man_allowCPUs"] = string.Join(',', CpuTopologyResolver.GetOptimizedProcessorOrder()),

            // Shorten The Manager's Re-Authentication Retry Interval From Its Five-Minute Default. The Manager Re-Authenticates With The Master Server Only While It Is Disconnected From The Chat Server, So Lowering This Lets It Recover A Fresh Registration And Reconnect Within About Half A Minute After A Dropped Connection Instead Of Waiting Out The Default Cycle, At No Steady-State Cost. The Value Is In Milliseconds.
            ["man_reauthFrequency"] = "30000",

            // Enables On-Demand Replay Uploads.
            ["man_uploadToS3OnDemand"] = "1",

            // Disables Partial Replay Uploads.
            ["man_uploadToCDNOnDemand"] = "0",

            // Any Server Configuration Options Other Than The Following Are Ignored By The Manager.
            ["svr_name"] = ServerNameWithWhitespaceWorkaround(options.ServerNamePrefix),
            ["svr_location"] = options.Location,
            ["svr_ip"] = serverAddress,

            // Setting Affinity To "-1" Is Required So The Manager Can Assign Affinity To Its Child Processes.
            ["host_affinity"] = "-1",

            // The Port On Which COMPEL Answers Master-Server Pings. The Manager Ignores This Value; COMPEL Binds It Itself.
            ["svr_port"] = ports.PingPort.ToString(),

            // Performance & Low-Latency Optimizations: Eliminate Synchronous Console/Disk Flush Bottlenecks & Suppress In-Game Diagnostic Spam
            ["con_developer"] = "0",
            ["con_showDev"] = "false",
            ["con_showNet"] = "false",
            ["con_verbose"] = "false",
            ["log_consoleLevel"] = "0",
            ["con_flush"] = "false",
            ["log_flush"] = "false",
            ["sys_flushLogs"] = "false",
            ["con_buffered"] = "true",
            ["http_printdebuginfo"] = "false",
            ["php_printdebuginfo"] = "false",
            ["svr_heartbeatInterval"] = "999999999",
            ["svr_diagnosticsInterval"] = "999999999",

            // Zero-Lag 128-Tick High-Frequency Networking & Timing Engine
            ["net_maxPacketRate"] = "128",
            ["cl_packetEpsilon"] = "0.001",
            ["net_smoothInterpolation"] = "1",
            ["sys_sleepTime"] = "0",
            ["mem_maxHeapSize"] = "4096",
            ["svr_maxFPS"] = "1000",
            ["host_maxFPS"] = "1000",
            ["svr_chatRateLimit"] = "0",

            // Canonical 20 Hz (50ms) K2 Simulation Tickrate & Anti-Stall Frame Headroom
            ["svr_gameFPS"] = Math.Clamp(options.ServerTickRate, 20, 60).ToString(),
            ["svr_maxFramesPerHostFrame"] = "5",

            // Cryptographic Seed Randomization: Guarantees unique PRNG stream across every instance
            ["svr_seed"] = RandomNumberGenerator.GetInt32(1, int.MaxValue).ToString(),
            ["rand_seed"] = RandomNumberGenerator.GetInt32(1, int.MaxValue).ToString()
        };

        string execute = string.Join(';', settings.Select(setting => $"Set {setting.Key} {setting.Value}"));

        return
        [
            "-manager",
            "-noconfig",
            "-execute", '"' + execute + '"',
            "-masterserver", masterServerHostAndPort,

            // Directs The Manager To Write User Artefacts Beneath The "server" Subdirectory Rather Than The Game Client Default
            "-mod", "base;game;server"
        ];
    }

    /// <summary>
    ///     Appends two workaround tokens to the server name. The manager's "-execute"/"Set" handling drops the last
    ///     whitespace-delimited token, and the manager drops one again when it spawns each dedicated instance, so two trailing
    ///     tokens are needed for a multi-word server name to survive intact.
    /// </summary>
    public static string ServerNameWithWhitespaceWorkaround(string desiredServerName)
    {
        const string workaroundParameter = "0";

        return $"{desiredServerName} {workaroundParameter} {workaroundParameter}";
    }
}
