// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2022-2026 [K]ONGOR and Contributors
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV) -- Upstream lineage: https://github.com/Project-KONGOR-Open-Source

using System.IO;
using System.Security.Cryptography;
using COMPEL.Configuration;

namespace COMPEL.Services.Supervision;

/// <summary>
/// Builds the command-line argument vector for launching an individual dedicated game server worker
/// (hon_x64.exe in -dedicated mode), with direct stdin redirection, per-port Named Pipe IPC,
/// 128-tick high-frequency performance cvars, and Master Server sub-account credentials.
/// </summary>
public static class DedicatedWorkerArguments
{
    public static string[] Build(
        DedicatedServerWorker worker,
        MatchServerManagerOptions options,
        PortPlan ports,
        string serverAddress,
        string masterServerHostAndPort,
        string? installationDirectory = null,
        bool enableMasterServer = true)
    {
        string cleanMasterServer = (masterServerHostAndPort ?? string.Empty)
            .Replace("http://", "", StringComparison.OrdinalIgnoreCase)
            .Replace("https://", "", StringComparison.OrdinalIgnoreCase)
            .TrimEnd('/');

        bool useMasterServer = enableMasterServer &&
            !string.IsNullOrWhiteSpace(cleanMasterServer) &&
            !string.IsNullOrWhiteSpace(options.UserName);

        List<string> args =
        [
            "-dedicated",
            "-noconfig",
            "-port", worker.Port.ToString(),
            "-voiceport", worker.VoicePort.ToString()
        ];


        if (!string.IsNullOrWhiteSpace(cleanMasterServer))
        {
            args.Add("-masterserver");
            args.Add(cleanMasterServer);
            args.Add("-webserver");
            args.Add(cleanMasterServer);
            args.Add("-messageserver");
            args.Add(cleanMasterServer);
        }

        args.Add("-pipe");
        args.Add(worker.PipeName);

        // VFS Mod Mounts Chain: K2 CFileManager::SetModStack evaluates m_vModStack in reverse (back() = highest priority).
        // Keep "override" above "game" (matching ClientModMountSpec.CanonicalMountTiers) so override/*.entity shadows
        // archived game/resources*.s2z identically to the client, while keeping "server" at back() so ~: userdata
        // artefacts (replays, logs) continue writing under the "server" profile directory.
        if (!string.IsNullOrWhiteSpace(installationDirectory) && Directory.Exists(installationDirectory))
        {
            List<string> modResources = ["base", "game"];

            if (Directory.Exists(Path.Combine(installationDirectory, "ClientPackageFiles")))
                modResources.Add("ClientPackageFiles");

            if (Directory.Exists(Path.Combine(installationDirectory, "..", "shared")))
                modResources.Add("../shared");

            string cfgPath = Path.Combine(installationDirectory, "KONGOR", "configuration");
            if (Directory.Exists(cfgPath)) modResources.Add("KONGOR/configuration");

            string updPath = Path.Combine(installationDirectory, "KONGOR", "updates");
            if (Directory.Exists(updPath)) modResources.Add("KONGOR/updates");

            string extPath = Path.Combine(installationDirectory, "KONGOR", "extensions");
            if (Directory.Exists(extPath)) modResources.Add("KONGOR/extensions");

            modResources.Add("override");
            modResources.Add("server");

            args.Add("-mod");
            args.Add(string.Join(";", modResources));
        }
        else
        {
            args.Add("-mod");
            args.Add("base;game;override;server");
        }

        // Calculate primary CPU core affinity to synchronize K2 host_affinity CVar with OS process affinity
        nint workerAffinityMask = CpuTopologyResolver.CalculateAffinityMask(
            worker.WorkerSlotIndex,
            options.Instances,
            CpuAffinityMode.PCoresOnly);
        int primaryCore = CpuTopologyResolver.GetPrimaryCoreFromMask(workerAffinityMask);

        Dictionary<string, string> settings = new()
        {
            ["svr_port"] = worker.Port.ToString(),
            ["svr_voicePort"] = worker.VoicePort.ToString(),
            ["svr_voicePortStart"] = worker.VoicePort.ToString(),
            ["svr_voicePortEnd"] = worker.VoicePort.ToString(),
            ["svr_name"] = CleanServerName(options.QueueArcadeMode && !worker.ServerName.Contains("QUEUE ARCADE", StringComparison.OrdinalIgnoreCase)
                ? (string.IsNullOrWhiteSpace(worker.ServerName) ? $"QUEUE ARCADE #{worker.InstanceIndex}" : $"QUEUE ARCADE {worker.ServerName}")
                : worker.ServerName),
            ["svr_location"] = options.Location,
            ["svr_ip"] = serverAddress,
            ["svr_broadcast"] = "true",
            ["host_affinity"] = primaryCore.ToString(),
            ["svr_slave"] = worker.InstanceIndex.ToString(),

            // Zero-Lag 128-Tick High-Frequency Networking & Timing Engine
            ["net_maxPacketRate"] = "128",
            ["net_maxPacketSize"] = "1350",
            ["net_maxBPS"] = "50000000",
            ["cl_packetEpsilon"] = "0.001",
            ["net_smoothInterpolation"] = "1",
            ["sys_sleepTime"] = "0",
            ["mem_maxHeapSize"] = "4096",
            ["svr_maxFPS"] = "1000",
            ["host_maxFPS"] = "1000",
            ["svr_chatRateLimit"] = "0",
            ["net_enableWatermark"] = "false",
            ["svr_clientConnectingTimeout"] = "15000",
            ["svr_clientConnectedTimeout"] = "120000",
            ["cl_connectionTimeout"] = "120000",

            // Canonical 20 Hz (50ms) K2 Simulation Tickrate & Anti-Stall Frame Headroom
            ["svr_gameFPS"] = Math.Clamp(options.ServerTickRate, 20, 60).ToString(),
            ["svr_maxFramesPerHostFrame"] = "5",

            // Live Console Output & Diagnostics: Unbuffered immediate flush to stdout pipe and console window
            ["con_developer"] = "1",
            ["con_showDev"] = "true",
            ["con_showNet"] = "true",
            ["con_verbose"] = "true",
            ["log_consoleLevel"] = "3",
            ["con_flush"] = "true",
            ["log_flush"] = "true",
            ["sys_flushLogs"] = "true",
            ["con_buffered"] = "false",
            ["sys_showConsole"] = "false",
            ["sys_dumpExecs"] = "true",
            ["http_printdebuginfo"] = "true",
            ["php_printdebuginfo"] = "true",
            ["svr_heartbeatInterval"] = "15000",
            ["svr_diagnosticsInterval"] = "999999999",

            // Cryptographic Seed Randomization per dedicated instance
            ["svr_seed"] = RandomNumberGenerator.GetInt32(1, int.MaxValue).ToString(),
            ["rand_seed"] = RandomNumberGenerator.GetInt32(1, int.MaxValue).ToString(),

            // Master Server Request Timeouts & Offline Match Headroom
            ["svr_matchIDRequestTotalTimeout"] = "4000",
            ["svr_matchIDRequired"] = "false",
            ["svr_authRequestTotalTimeout"] = "2000",
            ["svr_authRequestAttempts"] = "2",

            // Console Logging & Diagnostics
            ["con_log"] = "1",
            ["host_writeConsole"] = "true"
        };


        if (!string.IsNullOrWhiteSpace(cleanMasterServer))
        {
            settings["svr_masterServerAddress"] = cleanMasterServer;
            settings["svr_webServerAddress"] = cleanMasterServer;
        }

        if (options.QueueArcadeMode)
        {
            // Zero AFK kicks, zero vote-kick griefing, zero replay I/O, and zero Ranked stat pollution
            settings["sv_afkTimeout"] = "0";
            settings["sv_afkWarningTime"] = "0";
            settings["svr_recordReplay"] = "false";
            settings["g_voteKickCooldownTime"] = "99999999";
            settings["svr_submitMatchStatItems"] = "false";
            settings["svr_submitMatchStatAbilities"] = "false";
            settings["svr_submitMatchStatFrags"] = "false";
        }

        if (useMasterServer)
        {
            settings["svr_login"] = $"{options.UserName}:{worker.InstanceIndex}";
            settings["svr_password"] = options.Password;
        }

        // File logging & distinct slave index flags (isolates Slave-<index>_*.log handles across concurrent workers)
        args.Add("-slave");
        args.Add(worker.InstanceIndex.ToString());
        args.Add("-writeconsole");
        args.Add("-logfile");
        args.Add($"server_{worker.Port}.log");

        string execute = string.Join(';', settings
            .Select(s => $"Set {s.Key} {EscapeK2ExecuteToken(s.Value)}"));
        args.Add("-execute");
        args.Add(execute);

        foreach (KeyValuePair<string, string> setting in settings)
        {
            args.Add("+Set");
            args.Add(setting.Key);
            args.Add(setting.Value);
        }

        return [.. args];
    }

    /// <summary>
    /// Escapes a CVar value for safe embedding inside K2's <c>-execute "..."</c> payload without
    /// emitting inner double quotes (<c>0x22</c>).
    /// <para>
    /// Verified via Ghidra decompilation of <c>k2_x64.dll</c>:
    /// <list type="bullet">
    /// <item><c>CHost::Init</c> (<c>0x1808b9990</c>) scans naively from the opening <c>"</c> after <c>-execute</c> to the very next <c>0x22</c>, truncating the entire remaining <c>-execute</c> command list on Linux if any inner quote is present.</item>
    /// <item><c>CConsole::Execute</c> (<c>0x180843880</c>) implements <c>case L'\\': bVar2 = true;</c>, copying the next character literally without splitting whitespace or triggering <c>$cvar$</c>, <c>#cvar#</c>, <c>[expr]</c>, <c>{expr}</c>, or <c>;</c> evaluation.</item>
    /// </list>
    /// </para>
    /// </summary>
    public static string EscapeK2ExecuteToken(string? value)
    {
        if (string.IsNullOrEmpty(value))
            return string.Empty;

        System.Text.StringBuilder sb = new(value.Length + 8);
        foreach (char c in value)
        {
            if (c is '\\' or ' ' or '\t' or '$' or '#' or '[' or ']' or '{' or '}' or ';' or '"')
            {
                sb.Append('\\');
            }

            sb.Append(c);
        }

        return sb.ToString();
    }

    /// <summary>
    /// Returns the clean server name trimmed of leading/trailing whitespace.
    /// Direct dedicated workers do not drop tokens and do not require trailing padding tokens.
    /// </summary>
    public static string ServerNameWithWhitespaceWorkaround(string desiredServerName)
    {
        const string workaroundParameter = "0";
        return $"{desiredServerName} {workaroundParameter} {workaroundParameter}";
    }

    /// <summary>
    /// Returns the clean server name trimmed of leading/trailing whitespace.
    /// </summary>
    /// <summary>
    /// Resolves the explicit <c>CArchive::ExamineChecksums</c> enforcement environment contract
    /// (<c>KONGOR_EXAMINE_ARCHIVE_CHECKSUMS</c>) honored by both Windows (<c>version.dll</c>) and Linux
    /// (<c>libk2_native_guard.so</c>) dedicated server workers.
    /// </summary>
    public static (bool EnforceChecksums, string EnvValue, string Reason) ResolveExamineChecksumsPolicy(
        MatchServerManagerOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        return options.ExamineChecksumsPolicy switch
        {
            ExamineChecksumsPolicy.Always => (true, "1", "Policy=Always (EnforceChecksums)"),
            ExamineChecksumsPolicy.Disabled => (false, "0", "Policy=Disabled (AllowMods)"),
            ExamineChecksumsPolicy.RankedOnly when options.QueueArcadeMode => (false, "0", "Policy=RankedOnly + QueueArcadeMode=true (AllowMods)"),
            ExamineChecksumsPolicy.RankedOnly when options.AllowCustomMods => (false, "0", "Policy=RankedOnly + AllowCustomMods=true (AllowMods)"),
            _ => (true, "1", "Policy=RankedOnly + Ranked/TMM Worker (EnforceChecksums)")
        };
    }

    public static string CleanServerName(string? desiredServerName)
    {
        return string.IsNullOrWhiteSpace(desiredServerName) ? "KONGOR DEDICATED" : desiredServerName.Trim();
    }
}
