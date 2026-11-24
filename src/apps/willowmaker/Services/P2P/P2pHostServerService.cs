// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Kongor.Core.Vfs;

namespace WILLOWMAKER.Services.P2P;

public sealed record P2pHostLaunchResult(
    bool Succeeded,
    int BoundPort,
    int ProcessId,
    string HostConnectAddress,
    string DiagnosticMessage);

/// <summary>
/// Manages the lightweight headless K2_SERVER companion process for multi-player P2P Direct Play hosting.
/// K2_CLIENT compiles out multi-client lobby slots and rejects remote peers with disconnect_unknown;
/// spawning an isolated K2_SERVER worker (with zero-copy NTFS junctions/hardlinks to client .s2z archives)
/// allows up to 10 players to join over LAN/VPN/WAN without running MasterServer or COMPEL.
/// </summary>
public static class P2pHostServerService
{
    private const long ClientK2DllExactBytes = 19811840L;
    private const uint JobObjectLimitKillOnJobClose = 0x00002000u;
    private const int JobObjectExtendedLimitInformationClass = 9;

    private static readonly object SyncRoot = new();
    private static Process? _activeHostProcess;
    private static int _activeHostPort;
    private static IntPtr _jobObjectHandle = IntPtr.Zero;
    private static bool _exitHookRegistered;

    public static int ActiveHostPort
    {
        get
        {
            lock (SyncRoot)
            {
                if (_activeHostProcess is not null)
                {
                    try
                    {
                        if (!_activeHostProcess.HasExited)
                        {
                            return _activeHostPort;
                        }
                    }
                    catch (InvalidOperationException)
                    {
                        // Process handle already closed
                    }
                }

                return 0;
            }
        }
    }

    public static string? ResolveCompanionServerDirectory(string clientDirectory, string? targetPlatformOverride = null)
    {
        if (string.IsNullOrWhiteSpace(clientDirectory))
        {
            return null;
        }

        string fullClientDir = Path.GetFullPath(clientDirectory)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        DirectoryInfo? gamesParent = Directory.GetParent(fullClientDir);
        if (gamesParent is null)
        {
            return null;
        }

        List<string> searchParents = [gamesParent.FullName];
        string leafName = Path.GetFileName(fullClientDir);
        if ((leafName.Equals("linux-x64", StringComparison.OrdinalIgnoreCase) ||
             leafName.Equals("macos-x64", StringComparison.OrdinalIgnoreCase) ||
             leafName.Equals("windows-x64", StringComparison.OrdinalIgnoreCase)) &&
            gamesParent.Parent is not null)
        {
            searchParents.Add(gamesParent.Parent.FullName);
        }

        bool isWindowsTarget = targetPlatformOverride is null
            ? OperatingSystem.IsWindows()
            : targetPlatformOverride.Equals("windows", StringComparison.OrdinalIgnoreCase);

        foreach (string parentDir in searchParents)
        {
            string[] candidates =
            [
                Path.Combine(parentDir, "server_p2p"),
                Path.Combine(parentDir, "server")
            ];

            foreach (string candidate in candidates)
            {
                string? exePath = ResolveCompanionServerExecutable(candidate, targetPlatformOverride);
                string k2Path = Path.Combine(candidate, "k2_x64.dll");
                if (exePath is not null && (!isWindowsTarget || File.Exists(k2Path)))
                {
                    return candidate;
                }
            }
        }

        return null;
    }

    public static string? ResolveCompanionServerExecutable(string serverDirectory, string? targetPlatformOverride = null)
    {
        if (string.IsNullOrWhiteSpace(serverDirectory) || !Directory.Exists(serverDirectory))
        {
            return null;
        }

        string platform = string.IsNullOrWhiteSpace(targetPlatformOverride)
            ? (OperatingSystem.IsWindows() ? "windows" : OperatingSystem.IsMacOS() ? "macos" : "linux")
            : targetPlatformOverride.Trim().ToLowerInvariant();

        string[] relativeCandidates = platform switch
        {
            "windows" => ["hon_x64.exe", Path.Combine("windows-x64", "hon_x64.exe")],
            "macos" => [Path.Combine("macos-x64", "HoN64"), "HoN64", "hon-x86_64-server"],
            _ => [Path.Combine("linux-x64", "hon-x86_64-server"), "hon-x86_64-server"]
        };

        foreach (string rel in relativeCandidates)
        {
            string full = Path.Combine(serverDirectory, rel);
            if (File.Exists(full))
            {
                return full;
            }
        }

        return null;
    }

    /// <summary>
    /// Links shared read-only client VFS tiers and .s2z archives into server_p2p/ using unprivileged
    /// NTFS junctions and hardlinks (0 MB archive duplication, .zip-extraction safe) while keeping
    /// server_p2p's K2_SERVER binaries (k2_x64.dll, game_x64.dll, game_shared_x64.dll) strictly isolated.
    /// </summary>
    public static void EnsureSharedClientAssetsLinked(string serverP2pDir, string clientDir)
    {
        SharedClientAssetLinker.EnsureLinked(serverP2pDir, clientDir, msg => Trace.WriteLine($"[P2pHostServer] {msg}"));
    }

    public static void StopActiveHostServer()
    {
        lock (SyncRoot)
        {
            if (_activeHostProcess is null)
            {
                _activeHostPort = 0;
                return;
            }

            try
            {
                if (!_activeHostProcess.HasExited)
                {
                    Trace.WriteLine($"[P2pHostServer] Stopping previous P2P K2_SERVER worker PID {_activeHostProcess.Id} (UDP {_activeHostPort})...");
                    _activeHostProcess.Kill(entireProcessTree: true);
                    _activeHostProcess.WaitForExit(1500);
                }
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[P2pHostServer] Failed stopping P2P K2_SERVER worker: {ex.Message}");
            }
            finally
            {
                _activeHostProcess.Dispose();
                _activeHostProcess = null;
                _activeHostPort = 0;
            }
        }
    }

    public static async Task<P2pHostLaunchResult> StartOrRestartHostServerAsync(
        string clientDirectory,
        int hostPort,
        string hostNickname,
        Action<string>? logSink = null,
        CancellationToken cancellationToken = default)
    {
        void EmitLog(string message)
        {
            Trace.WriteLine(message);
            logSink?.Invoke(message);
        }

        StopActiveHostServer();

        string? serverDir = ResolveCompanionServerDirectory(clientDirectory);
        if (string.IsNullOrWhiteSpace(serverDir))
        {
            string notFoundMsg = $"Companion K2_SERVER directory not found next to '{clientDirectory}'. Falling back to solo local practice server.";
            EmitLog($"[P2pHostServer] {notFoundMsg}");
            return new P2pHostLaunchResult(
                false,
                hostPort,
                0,
                string.Empty,
                notFoundMsg);
        }

        string dirLeaf = Path.GetFileName(serverDir.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        if (dirLeaf.Equals("server_p2p", StringComparison.OrdinalIgnoreCase))
        {
            EnsureSharedClientAssetsLinked(serverDir, clientDirectory);
        }

        if (OperatingSystem.IsWindows())
        {
            string k2DllPath = Path.Combine(serverDir, "k2_x64.dll");
            if (File.Exists(k2DllPath))
            {
                FileInfo k2Info = new FileInfo(k2DllPath);
                if (k2Info.Length == ClientK2DllExactBytes)
                {
                    string mismatchMsg = $"Binary mismatch in '{k2DllPath}': found K2_CLIENT binary ({ClientK2DllExactBytes} bytes) instead of K2_SERVER. Restore genuine server binaries before P2P hosting.";
                    EmitLog($"[P2pHostServer] {mismatchMsg}");
                    return new P2pHostLaunchResult(
                        false,
                        hostPort,
                        0,
                        string.Empty,
                        mismatchMsg);
                }
            }
        }

        string loopbackEndpoint = P2pLoopbackAuthService.EnsureStarted();
        int voicePort = hostPort + P2pConnectionService.VoicePortOffset;
        string safeServerName = SanitizeToken(string.IsNullOrWhiteSpace(hostNickname) ? "P2P_Host" : $"{hostNickname}_P2P");
        string exePath = ResolveCompanionServerExecutable(serverDir)
                         ?? Path.Combine(serverDir, OperatingSystem.IsWindows() ? "hon_x64.exe" : "hon-x86_64-server");
        string arguments = BuildDedicatedHostArguments(serverDir, hostPort, voicePort, safeServerName, loopbackEndpoint);

        if (!OperatingSystem.IsWindows() && File.Exists(exePath))
        {
            try
            {
                UnixFileMode currentMode = File.GetUnixFileMode(exePath);
                UnixFileMode execBits = UnixFileMode.UserExecute | UnixFileMode.GroupExecute | UnixFileMode.OtherExecute;
                if ((currentMode & UnixFileMode.UserExecute) == 0)
                {
                    File.SetUnixFileMode(exePath, currentMode | execBits);
                    EmitLog($"[P2pHostServer][POSIX] Restored +x permission on '{exePath}'.");
                }
            }
            catch (Exception ex)
            {
                EmitLog($"[P2pHostServer][POSIX] Executable mode check notice on '{exePath}': {ex.Message}");
            }
        }

        ProcessStartInfo psi = new ProcessStartInfo
        {
            FileName = exePath,
            WorkingDirectory = serverDir,
            Arguments = arguments,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        if (!OperatingSystem.IsWindows())
        {
            string serverUserData = Path.Combine(serverDir, "userdata");
            Directory.CreateDirectory(serverUserData);
            psi.Environment["HOME"] = serverUserData;
            psi.Environment["LC_NUMERIC"] = "C";

            List<string> nativeLibDirs =
            [
                serverDir,
                Path.GetDirectoryName(exePath) ?? serverDir,
                Path.Combine(serverDir, "linux-x64"),
                Path.Combine(serverDir, "libs-runtime"),
                Path.Combine(serverDir, "libs-x86_64"),
                Path.Combine(serverDir, "compatibility")
            ];
            List<string> existingLibDirs = [];
            foreach (string libDir in nativeLibDirs)
            {
                if (Directory.Exists(libDir) && !existingLibDirs.Contains(libDir))
                {
                    existingLibDirs.Add(libDir);
                }
            }

            if (existingLibDirs.Count > 0)
            {
                string envKey = OperatingSystem.IsMacOS() ? "DYLD_LIBRARY_PATH" : "LD_LIBRARY_PATH";
                string existingVal = Environment.GetEnvironmentVariable(envKey) ?? string.Empty;
                string joined = string.Join(Path.PathSeparator, existingLibDirs);
                psi.Environment[envKey] = string.IsNullOrWhiteSpace(existingVal)
                    ? joined
                    : $"{joined}{Path.PathSeparator}{existingVal}";
                EmitLog($"[P2pHostServer][POSIX] Configured {envKey}='{psi.Environment[envKey]}' and HOME='{serverUserData}'.");
            }
        }

        Process? startedProcess;
        lock (SyncRoot)
        {
            EnsureExitHookRegisteredLocked();
            startedProcess = Process.Start(psi);
            if (startedProcess is null)
            {
                return new P2pHostLaunchResult(
                    false,
                    hostPort,
                    0,
                    string.Empty,
                    $"Process.Start returned null for '{exePath}'.");
            }

            TryAttachKillOnCloseJobObjectLocked(startedProcess);
            _activeHostProcess = startedProcess;
            _activeHostPort = hostPort;
        }

        Trace.WriteLine($"[P2pHostServer] Spawned headless K2_SERVER P2P worker PID {startedProcess.Id} on UDP {hostPort} (voice {voicePort}) from '{serverDir}'.");

        Stopwatch sw = Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < 25000 && !cancellationToken.IsCancellationRequested)
        {
            if (startedProcess.HasExited)
            {
                int exitCode = startedProcess.ExitCode;
                StopActiveHostServer();
                return new P2pHostLaunchResult(
                    false,
                    hostPort,
                    0,
                    string.Empty,
                    $"K2_SERVER P2P worker exited prematurely (exit code {exitCode}). Inspect '{Path.Combine(serverDir, "userdata", "logs")}' or run scripts/triage_client_logs.ps1.");
            }

            if (TryGetBoundUdpAddress(hostPort, out IPAddress? gameBindAddress) &&
                TryGetBoundUdpAddress(voicePort, out _))
            {
                await Task.Delay(500, cancellationToken).ConfigureAwait(false);
                if (startedProcess.HasExited)
                {
                    int postExit = startedProcess.ExitCode;
                    StopActiveHostServer();
                    return new P2pHostLaunchResult(
                        false,
                        hostPort,
                        0,
                        string.Empty,
                        $"K2_SERVER P2P worker exited during post-entity session handshake (exit code {postExit}). Inspect '{Path.Combine(serverDir, "userdata", "logs")}'.");
                }

                string connectIp = gameBindAddress is null ||
                                   gameBindAddress.Equals(IPAddress.Any) ||
                                   gameBindAddress.Equals(IPAddress.IPv6Any)
                    ? "127.0.0.1"
                    : gameBindAddress.ToString();
                string connectAddr = $"{connectIp}:{hostPort}";
                Trace.WriteLine($"[P2pHostServer] K2_SERVER P2P worker PID {startedProcess.Id} ready (game UDP {connectAddr}, voice UDP {voicePort} bound) in {sw.ElapsedMilliseconds} ms.");
                return new P2pHostLaunchResult(
                    true,
                    hostPort,
                    startedProcess.Id,
                    connectAddr,
                    $"K2_SERVER P2P host active on UDP {connectAddr} (PID {startedProcess.Id}).");
            }

            await Task.Delay(100, cancellationToken).ConfigureAwait(false);
        }

        string fallbackIp = TryGetBoundUdpAddress(hostPort, out IPAddress? partialBind) &&
                            partialBind is not null &&
                            !partialBind.Equals(IPAddress.Any) &&
                            !partialBind.Equals(IPAddress.IPv6Any)
            ? partialBind.ToString()
            : "127.0.0.1";
        string fallbackConnectAddr = $"{fallbackIp}:{hostPort}";
        return new P2pHostLaunchResult(
            !startedProcess.HasExited,
            hostPort,
            startedProcess.HasExited ? 0 : startedProcess.Id,
            fallbackConnectAddr,
            $"K2_SERVER P2P host warming up on UDP {fallbackConnectAddr} (PID {startedProcess.Id}).");
    }

    public static string BuildDedicatedHostArguments(
        string serverDir,
        int hostPort,
        int voicePort,
        string safeServerName,
        string loopbackAuthEndpoint = "127.0.0.1:19999")
    {
        string effectiveLoopback = string.IsNullOrWhiteSpace(loopbackAuthEndpoint)
            ? "127.0.0.1:19999"
            : loopbackAuthEndpoint.Trim();

        // K2 CFileManager::SetModStack evaluates m_vModStack in reverse (back() = highest priority);
        // mount "override" above "game" and KONGOR/* tiers to match ClientModMountSpec.CanonicalMountTiers,
        // with "server" at back() for ~: userdata isolation.
        List<string> modResources = ["base", "game"];
        if (!string.IsNullOrWhiteSpace(serverDir))
        {
            if (Directory.Exists(Path.Combine(serverDir, "ClientPackageFiles")))
            {
                modResources.Add("ClientPackageFiles");
            }

            string sharedRel = "../shared";
            string sharedAbs = Path.GetFullPath(Path.Combine(serverDir, sharedRel));
            if (Directory.Exists(sharedAbs))
            {
                modResources.Add(sharedRel);
            }
            else if (Directory.Exists(Path.Combine(serverDir, "shared")))
            {
                modResources.Add("shared");
            }

            if (Directory.Exists(Path.Combine(serverDir, "KONGOR", "configuration")))
            {
                modResources.Add("KONGOR/configuration");
            }

            if (Directory.Exists(Path.Combine(serverDir, "KONGOR", "updates")))
            {
                modResources.Add("KONGOR/updates");
            }

            if (Directory.Exists(Path.Combine(serverDir, "KONGOR", "extensions")))
            {
                modResources.Add("KONGOR/extensions");
            }
        }

        modResources.Add("override");
        modResources.Add("server");

        string modChain = string.Join(";", modResources);

        string executeScript =
            "Set svr_slave 1;" +
            $"Set svr_port {hostPort};" +
            $"Set svr_voicePort {voicePort};" +
            $"Set svr_voicePortStart {voicePort};" +
            $"Set svr_voicePortEnd {voicePort};" +
            "Set svr_ip 0.0.0.0;" +
            "Set svr_location LOCAL;" +
            "Set svr_broadcast true;" +
            $"Set svr_masterServerAddress {effectiveLoopback};" +
            $"Set svr_webServerAddress {effectiveLoopback};" +
            "Set svr_heartbeatInterval 15000;" +
            "Set svr_diagnosticsInterval 999999999;" +
            "Set svr_gameFPS 20;" +
            "Set svr_maxFramesPerHostFrame 5;" +
            "Set net_maxPacketRate 128;" +
            "Set net_maxPacketSize 1350;" +
            "Set net_maxBPS 50000000;" +
            "Set sys_sleepTime 0;" +
            "Set svr_maxFPS 1000;" +
            "Set host_maxFPS 1000;" +
            "Set svr_clientConnectingTimeout 15000;" +
            "Set svr_clientConnectedTimeout 120000;" +
            "Set cl_connectionTimeout 120000;" +
            "Set svr_login p2phost:1;" +
            "Set svr_password p2p;" +
            "Set svr_adminPassword kongorp2p;" +
            "Set svr_matchIDRequired false;" +
            "Set svr_matchIDRequestAttempts 3;" +
            "Set svr_matchIDRequestTotalTimeout 5000;" +
            "Set svr_authRequestAttempts 3;" +
            "Set svr_authRequestTotalTimeout 5000;" +
            "Set svr_authTimeout 86400000;" +
            "Set svr_requestSessionCookieTimeout 5000;" +
            "Set host_needTokenToCreateGame false;" +
            "Set sv_matchmakingDisconnectCheck false;" +
            "Set sv_confirmMatchIDForReconnect false;" +
            "Set sv_allowSolo true;" +
            "Set sv_allowEmpty true;" +
            "Set sv_overrideFreeHeroes true;" +
            "Set net_enableWatermark false;" +
            "Set man_enableProxy false;" +
            "Set con_developer 1;" +
            "Set con_showDev true;" +
            "Set con_showNet true;" +
            "Set con_verbose true;" +
            "Set log_consoleLevel 3;" +
            "Set con_flush true;" +
            "Set log_flush true;" +
            "Set sys_flushLogs true;" +
            "Set con_buffered false;" +
            "Set http_printdebuginfo true;" +
            "Set php_printdebuginfo true;" +
            "Set con_log 1;" +
            "Set host_writeConsole true;" +
            $"Set svr_name {safeServerName}";

        string plusSets =
            "+Set svr_slave 1 " +
            $"+Set svr_port {hostPort} " +
            $"+Set svr_voicePort {voicePort} " +
            $"+Set svr_voicePortStart {voicePort} " +
            $"+Set svr_voicePortEnd {voicePort} " +
            "+Set svr_ip 0.0.0.0 " +
            "+Set svr_location LOCAL " +
            "+Set svr_broadcast true " +
            $"+Set svr_masterServerAddress \"{effectiveLoopback}\" " +
            $"+Set svr_webServerAddress \"{effectiveLoopback}\" " +
            "+Set svr_heartbeatInterval 15000 " +
            "+Set svr_gameFPS 20 " +
            "+Set svr_maxFramesPerHostFrame 5 " +
            "+Set svr_login \"p2phost:1\" " +
            "+Set svr_password \"p2p\" " +
            "+Set svr_adminPassword \"kongorp2p\" " +
            "+Set svr_matchIDRequired false " +
            "+Set svr_matchIDRequestAttempts 3 " +
            "+Set svr_matchIDRequestTotalTimeout 5000 " +
            "+Set svr_authRequestAttempts 3 " +
            "+Set svr_authRequestTotalTimeout 5000 " +
            "+Set svr_authTimeout 86400000 " +
            "+Set svr_requestSessionCookieTimeout 5000 " +
            "+Set host_needTokenToCreateGame false " +
            "+Set sv_matchmakingDisconnectCheck false " +
            "+Set sv_confirmMatchIDForReconnect false " +
            "+Set sv_allowSolo true " +
            "+Set sv_allowEmpty true " +
            "+Set sv_overrideFreeHeroes true " +
            "+Set net_enableWatermark false " +
            "+Set con_flush true " +
            "+Set log_flush true " +
            "+Set sys_flushLogs true " +
            "+Set con_buffered false " +
            $"+Set svr_name {safeServerName}";

        return $"-dedicated -noconfig -port {hostPort} -voiceport {voicePort} -slave 1 -writeconsole -logfile server_{hostPort}.log -mod {modChain} -masterserver {effectiveLoopback} -webserver {effectiveLoopback} -messageserver {effectiveLoopback} -execute \"{executeScript}\" {plusSets}";
    }

    private static bool TryGetBoundUdpAddress(int port, out IPAddress? boundAddress)
    {
        boundAddress = null;
        try
        {
            IPEndPoint[] listeners = IPGlobalProperties.GetIPGlobalProperties().GetActiveUdpListeners();
            foreach (IPEndPoint ep in listeners)
            {
                if (ep.Port == port)
                {
                    if (ep.Address.Equals(IPAddress.Any) || ep.Address.Equals(IPAddress.IPv6Any))
                    {
                        boundAddress = ep.Address;
                        return true;
                    }

                    if (ep.AddressFamily == AddressFamily.InterNetwork)
                    {
                        boundAddress = ep.Address;
                        return true;
                    }

                    boundAddress ??= ep.Address;
                }
            }

            if (boundAddress is not null)
            {
                return true;
            }
        }
        catch (Exception ex)
        {
            Trace.WriteLine($"[P2pHostServer] UDP listener poll warning: {ex.Message}");
        }

        return false;
    }

    private static string SanitizeToken(string input)
    {
        char[] buffer = new char[Math.Min(input.Length, 28)];
        int idx = 0;
        foreach (char ch in input)
        {
            if (idx >= buffer.Length)
            {
                break;
            }

            if (char.IsLetterOrDigit(ch) || ch == '_' || ch == '-')
            {
                buffer[idx++] = ch;
            }
            else if (ch == ' ')
            {
                buffer[idx++] = '_';
            }
        }

        return idx > 0 ? new string(buffer, 0, idx) : "P2P_Host";
    }

    private static void EnsureExitHookRegisteredLocked()
    {
        if (_exitHookRegistered)
        {
            return;
        }

        _exitHookRegistered = true;
        AppDomain.CurrentDomain.ProcessExit += static (_, _) => StopActiveHostServer();
    }

    private static void TryAttachKillOnCloseJobObjectLocked(Process process)
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        try
        {
            if (_jobObjectHandle == IntPtr.Zero)
            {
                IntPtr job = CreateJobObjectW(IntPtr.Zero, null);
                if (job != IntPtr.Zero)
                {
                    JOBOBJECT_EXTENDED_LIMIT_INFORMATION info = default;
                    info.BasicLimitInformation.LimitFlags = JobObjectLimitKillOnJobClose;
                    int size = Marshal.SizeOf<JOBOBJECT_EXTENDED_LIMIT_INFORMATION>();
                    if (SetInformationJobObject(job, JobObjectExtendedLimitInformationClass, ref info, (uint)size))
                    {
                        _jobObjectHandle = job;
                    }
                    else
                    {
                        CloseHandle(job);
                    }
                }
            }

            if (_jobObjectHandle != IntPtr.Zero)
            {
                AssignProcessToJobObject(_jobObjectHandle, process.Handle);
            }
        }
        catch (Exception ex)
        {
            Trace.WriteLine($"[P2pHostServer] JobObject attach notice: {ex.Message}");
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct JOBOBJECT_BASIC_LIMIT_INFORMATION
    {
        public long PerProcessUserTimeLimit;
        public long PerJobUserTimeLimit;
        public uint LimitFlags;
        public UIntPtr MinimumWorkingSetSize;
        public UIntPtr MaximumWorkingSetSize;
        public uint ActiveProcessLimit;
        public UIntPtr Affinity;
        public uint PriorityClass;
        public uint SchedulingClass;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct IO_COUNTERS
    {
        public ulong ReadOperationCount;
        public ulong WriteOperationCount;
        public ulong OtherOperationCount;
        public ulong ReadTransferCount;
        public ulong WriteTransferCount;
        public ulong OtherTransferCount;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct JOBOBJECT_EXTENDED_LIMIT_INFORMATION
    {
        public JOBOBJECT_BASIC_LIMIT_INFORMATION BasicLimitInformation;
        public IO_COUNTERS IoInfo;
        public UIntPtr ProcessMemoryLimit;
        public UIntPtr JobMemoryLimit;
        public UIntPtr PeakProcessMemoryUsed;
        public UIntPtr PeakJobMemoryUsed;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CreateJobObjectW(IntPtr lpJobAttributes, string? lpName);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetInformationJobObject(
        IntPtr hJob,
        int infoType,
        ref JOBOBJECT_EXTENDED_LIMIT_INFORMATION lpJobObjectInfo,
        uint cbJobObjectInfoLength);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool AssignProcessToJobObject(IntPtr hJob, IntPtr hProcess);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr hObject);
}
