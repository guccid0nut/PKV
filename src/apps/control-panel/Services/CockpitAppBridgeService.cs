// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

using System;
using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Linq;
using System.Net.Http;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Kongor.Core.Configuration;
using NEXUS.ControlPanel.GUI.Services.Models;

namespace NEXUS.ControlPanel.GUI.Services;

public sealed partial class CockpitAppBridgeService : ICockpitAppBridgeService
{
    public const string DefaultWillowmakerPipeName = "willowmaker-ipc";
    public const string DefaultNativeClientPipeName = "kongor-native-client";
    public const long MinimumExportStagingFreeBytes = 20L * 1024L * 1024L * 1024L;

    private const ushort IpcFrameMagic = 0x574D; // 'WM'
    private const int IpcFrameHeaderBytes = 12;
    private const int MaxIpcPayloadBytes = 256 * 1024;

    private const ushort OpPing = 0x0001;
    private const ushort OpPong = 0x0002;
    private const ushort OpSyncEndpoints = 0x0030;
    private const ushort OpSyncEndpointsResponse = 0x0031;
    private const ushort OpBringToFront = 0x0032;
    private const ushort OpBringToFrontResponse = 0x0033;
    private const ushort OpGetLauncherStatus = 0x0034;
    private const ushort OpLauncherStatusResponse = 0x0035;

    private static uint _nextRequestId;

    private readonly INexusOrchestratorService? _orchestrator;
    private readonly Func<ProcessStartInfo, int?>? _processLauncherOverride;
    private readonly ConcurrentDictionary<string, string> _lastLoggedPipeStates = new(StringComparer.OrdinalIgnoreCase);

    public CockpitAppBridgeService(
        INexusOrchestratorService? orchestrator = null,
        Func<ProcessStartInfo, int?>? processLauncherOverride = null)
    {
        _orchestrator = orchestrator;
        _processLauncherOverride = processLauncherOverride;
    }

    public static string FormatPipeDisplayPath(string pipeName)
    {
        string clean = string.IsNullOrWhiteSpace(pipeName) ? DefaultWillowmakerPipeName : pipeName.Trim();
        return OperatingSystem.IsWindows()
            ? $@"\\.\pipe\{clean}"
            : Path.Combine(Path.GetTempPath(), $"CoreFxPipe_{clean}");
    }

    public CockpitBinaryResolution ResolveWillowmakerBinary(string? rootOverride = null, string? targetPlatformOverride = null)
    {
        string platform = ResolveTargetPlatform(targetPlatformOverride);
        string? bundleRoot = ResolveBundleRoot(rootOverride);
        if (!string.IsNullOrWhiteSpace(bundleRoot) && Directory.Exists(bundleRoot))
        {
            string[] standaloneRelCandidates = platform switch
            {
                "windows" =>
                [
                    "WILLOWMAKER.exe",
                    Path.Combine("tools-win-x64", "WILLOWMAKER.exe")
                ],
                "macos" =>
                [
                    "WILLOWMAKER",
                    Path.Combine("WILLOWMAKER.app", "Contents", "MacOS", "WILLOWMAKER"),
                    Path.Combine("tools-macos-x64", "WILLOWMAKER"),
                    Path.Combine("tools-osx-universal", "WILLOWMAKER")
                ],
                _ =>
                [
                    "WILLOWMAKER",
                    Path.Combine("tools-linux-x64", "WILLOWMAKER")
                ]
            };

            foreach (string rel in standaloneRelCandidates)
            {
                string full = Path.Combine(bundleRoot, rel);
                if (File.Exists(full))
                {
                    string workDir = ResolvePreferredWillowmakerWorkDir(bundleRoot, Path.GetDirectoryName(full) ?? bundleRoot);
                    return new CockpitBinaryResolution(
                        true,
                        full,
                        workDir,
                        LaunchViaDotnetHost: false,
                        LayoutKind: "StandaloneBundle",
                        DiagnosticMessage: $"Resolved standalone WILLOWMAKER binary at '{full}'.");
                }
            }
        }

        string? repoRoot = FindRepositoryRoot(rootOverride);
        if (!string.IsNullOrWhiteSpace(repoRoot))
        {
            string appBinRoot = Path.Combine(repoRoot, "src", "apps", "willowmaker", "bin");
            CockpitBinaryResolution? devHit = ResolveDevAppBinary(
                appBinRoot,
                "WILLOWMAKER",
                platform,
                preferredWorkDir: ResolvePreferredWillowmakerWorkDir(repoRoot, repoRoot));
            if (devHit is not null)
            {
                return devHit;
            }
        }

        return new CockpitBinaryResolution(
            false,
            null,
            null,
            LaunchViaDotnetHost: false,
            LayoutKind: "NotFound",
            DiagnosticMessage: "WILLOWMAKER binary not found. Build it with: dotnet build -c Release src/apps/willowmaker/WILLOWMAKER.csproj");
    }

    public CockpitBinaryResolution ResolveCompelGuiBinary(string? rootOverride = null, string? targetPlatformOverride = null)
    {
        string platform = ResolveTargetPlatform(targetPlatformOverride);
        string? bundleRoot = ResolveBundleRoot(rootOverride);
        if (!string.IsNullOrWhiteSpace(bundleRoot) && Directory.Exists(bundleRoot))
        {
            string exeLeaf = platform == "windows" ? "COMPEL.GUI.exe" : "COMPEL.GUI";
            List<string> standaloneCandidates = platform switch
            {
                "windows" =>
                [
                    Path.Combine(bundleRoot, "tools-win-x64", "COMPEL.GUI.exe"),
                    Path.Combine(bundleRoot, "COMPEL.GUI.exe")
                ],
                "macos" =>
                [
                    Path.Combine(bundleRoot, "tools-macos-x64", "COMPEL.GUI"),
                    Path.Combine(bundleRoot, "tools-osx-universal", "COMPEL.GUI"),
                    Path.Combine(bundleRoot, "COMPEL.GUI.app", "Contents", "MacOS", "COMPEL.GUI"),
                    Path.Combine(bundleRoot, "COMPEL.GUI")
                ],
                _ =>
                [
                    Path.Combine(bundleRoot, "tools-linux-x64", "COMPEL.GUI"),
                    Path.Combine(bundleRoot, "COMPEL.GUI")
                ]
            };

            try
            {
                foreach (string toolsDir in Directory.EnumerateDirectories(bundleRoot, "tools-*", SearchOption.TopDirectoryOnly))
                {
                    string dynamicCandidate = Path.Combine(toolsDir, exeLeaf);
                    if (!standaloneCandidates.Contains(dynamicCandidate))
                    {
                        standaloneCandidates.Add(dynamicCandidate);
                    }
                }
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[CockpitBridge] tools-* scan notice under '{bundleRoot}': {ex.Message}");
            }

            foreach (string full in standaloneCandidates)
            {
                if (File.Exists(full))
                {
                    return new CockpitBinaryResolution(
                        true,
                        full,
                        Path.GetDirectoryName(full) ?? bundleRoot,
                        LaunchViaDotnetHost: false,
                        LayoutKind: "StandaloneBundle",
                        DiagnosticMessage: $"Resolved standalone COMPEL.GUI binary at '{full}'.");
                }
            }
        }

        string? repoRoot = FindRepositoryRoot(rootOverride);
        if (!string.IsNullOrWhiteSpace(repoRoot))
        {
            string appBinRoot = Path.Combine(repoRoot, "src", "apps", "compel-gui", "bin");
            CockpitBinaryResolution? devHit = ResolveDevAppBinary(
                appBinRoot,
                "COMPEL.GUI",
                platform,
                preferredWorkDir: null);
            if (devHit is not null)
            {
                return devHit;
            }
        }

        return new CockpitBinaryResolution(
            false,
            null,
            null,
            LaunchViaDotnetHost: false,
            LayoutKind: "NotFound",
            DiagnosticMessage: "COMPEL.GUI binary not found. Build it with: dotnet build -c Release src/apps/compel-gui/COMPEL.GUI.csproj");
    }

    public async Task<WillowmakerCockpitStatus> ProbeWillowmakerIpcAsync(
        string? pipeNameOverride = null,
        int timeoutMs = 250,
        CancellationToken cancellationToken = default)
    {
        string pipeName = string.IsNullOrWhiteSpace(pipeNameOverride) ? DefaultWillowmakerPipeName : pipeNameOverride.Trim();
        string displayPath = FormatPipeDisplayPath(pipeName);
        string probeReason = "Idle";
        Stopwatch sw = Stopwatch.StartNew();

        try
        {
            using NamedPipeClientStream pipe = new(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
            using CancellationTokenSource connectCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            connectCts.CancelAfter(Math.Max(50, timeoutMs));
            await pipe.ConnectAsync(connectCts.Token).ConfigureAwait(false);

            uint reqId = Interlocked.Increment(ref _nextRequestId);
            await WriteIpcFrameAsync(pipe, OpGetLauncherStatus, reqId, ReadOnlyMemory<byte>.Empty, connectCts.Token).ConfigureAwait(false);
            (ushort opCode, byte[] payload) = await ReadIpcFrameAsync(pipe, connectCts.Token).ConfigureAwait(false);
            sw.Stop();
            double latency = Math.Round(sw.Elapsed.TotalMilliseconds, 2);

            if (opCode == OpLauncherStatusResponse && payload.Length > 0)
            {
                CockpitLauncherStatusWireDto? dto = JsonSerializer.Deserialize(
                    payload,
                    CockpitBridgeJsonContext.Default.CockpitLauncherStatusWireDto);
                if (dto is not null)
                {
                    string master = string.IsNullOrWhiteSpace(dto.MasterServerEndpoint) ? "unconfigured" : dto.MasterServerEndpoint;
                    string gameTag = dto.IsGameRunning ? " • HoN Active" : string.Empty;
                    return new WillowmakerCockpitStatus(
                        true,
                        dto.ProcessId,
                        master,
                        dto.CdnEndpoint ?? string.Empty,
                        dto.IsGameRunning,
                        latency,
                        displayPath,
                        $"Connected (PID {dto.ProcessId} • {master}{gameTag} • {latency:F1} ms)",
                        "#10B981",
                        HostKind: "Willowmaker");
                }
            }

            // Fallback to simple Ping if connected to kongor-cli McpIpcServer or older WILLOWMAKER build without OpGetLauncherStatus
            uint pingId = Interlocked.Increment(ref _nextRequestId);
            await WriteIpcFrameAsync(pipe, OpPing, pingId, ReadOnlyMemory<byte>.Empty, connectCts.Token).ConfigureAwait(false);
            (ushort pongOp, _) = await ReadIpcFrameAsync(pipe, connectCts.Token).ConfigureAwait(false);
            if (pongOp == OpPong)
            {
                return new WillowmakerCockpitStatus(
                    true,
                    0,
                    string.Empty,
                    string.Empty,
                    false,
                    latency,
                    displayPath,
                    $"Connected (MCP / Ping-only host • {latency:F1} ms)",
                    "#10B981",
                    HostKind: "McpBridge");
            }
        }
        catch (OperationCanceledException ex) when (!cancellationToken.IsCancellationRequested)
        {
            probeReason = ex.GetType().Name;
        }
        catch (Exception ex) when (ex is TimeoutException or IOException or SocketException)
        {
            probeReason = ex.GetType().Name;
        }
        catch (Exception ex)
        {
            probeReason = ex.GetType().Name;
            Console.Error.WriteLine($"[CockpitBridge] WILLOWMAKER IPC probe warning on '{displayPath}': {ex.Message}");
        }

        return new WillowmakerCockpitStatus(
            false,
            0,
            string.Empty,
            string.Empty,
            false,
            0,
            displayPath,
            $"Standby (Not Running • {displayPath} • {probeReason})",
            "#94A3B8",
            HostKind: "None");
    }

    public async Task<CockpitLaunchOrSyncResult> OpenOrSyncWillowmakerAsync(
        string masterServerEndpoint,
        string cdnEndpoint,
        string? rootOverride = null,
        string? pipeNameOverride = null,
        string? discordClientId = null,
        CancellationToken cancellationToken = default)
    {
        string cleanMaster = StripHttpScheme(string.IsNullOrWhiteSpace(masterServerEndpoint) ? "localhost:55555" : masterServerEndpoint);
        string cleanCdn = StripHttpScheme(string.IsNullOrWhiteSpace(cdnEndpoint) ? $"{cleanMaster}/cdn" : cdnEndpoint);
        string cleanDiscordClientId = discordClientId?.Trim() ?? string.Empty;
        string pipeName = string.IsNullOrWhiteSpace(pipeNameOverride) ? DefaultWillowmakerPipeName : pipeNameOverride.Trim();

        // 1. Live IPC Fast-Path: if WILLOWMAKER is already running, focus it & sync Master + CDN endpoints + Discord Client ID
        CockpitEndpointSyncWireResponse? liveSync = await TrySendEndpointSyncOverPipeAsync(
            pipeName,
            cleanMaster,
            cleanCdn,
            cleanDiscordClientId,
            bringToFront: true,
            timeoutMs: 400,
            cancellationToken).ConfigureAwait(false);

        if (liveSync is not null)
        {
            string appliedMaster = string.IsNullOrWhiteSpace(liveSync.AppliedMasterServer) ? cleanMaster : liveSync.AppliedMasterServer;
            string appliedCdn = string.IsNullOrWhiteSpace(liveSync.AppliedCdnEndpoint) ? cleanCdn : liveSync.AppliedCdnEndpoint;
            string msg = liveSync.Success
                ? $"Focused WILLOWMAKER (PID {liveSync.ProcessId}) & synced Master '{appliedMaster}' + CDN '{appliedCdn}'."
                : $"WILLOWMAKER IPC responded with warning: {liveSync.Message}";

            EmitOrchestratorLog("WILLOWMAKER IPC", msg, isError: !liveSync.Success);
            return new CockpitLaunchOrSyncResult(
                Succeeded: liveSync.Success,
                WasAlreadyRunning: true,
                SyncedViaIpc: liveSync.Success,
                LaunchedNewProcess: false,
                ProcessId: liveSync.ProcessId,
                AppliedMasterServer: appliedMaster,
                AppliedCdnEndpoint: appliedCdn,
                ExecutablePath: null,
                StatusMessage: msg);
        }

        // 2. Cold-Launch Path: resolve WILLOWMAKER across standalone bundle or dev checkout and spawn with .NET 11 env
        CockpitBinaryResolution resolved = ResolveWillowmakerBinary(rootOverride);
        if (!resolved.Found || string.IsNullOrWhiteSpace(resolved.ExecutablePath))
        {
            EmitOrchestratorLog("WILLOWMAKER", resolved.DiagnosticMessage, isError: true);
            return new CockpitLaunchOrSyncResult(
                Succeeded: false,
                WasAlreadyRunning: false,
                SyncedViaIpc: false,
                LaunchedNewProcess: false,
                ProcessId: 0,
                AppliedMasterServer: cleanMaster,
                AppliedCdnEndpoint: cleanCdn,
                ExecutablePath: null,
                StatusMessage: resolved.DiagnosticMessage);
        }

        ProcessStartInfo psi = BuildDotNetAppProcessStartInfo(resolved);
        psi.Environment["KONGOR_COCKPIT_MASTER_SERVER"] = cleanMaster;
        psi.Environment["KONGOR_COCKPIT_CDN_ENDPOINT"] = cleanCdn;
        psi.Environment["KONGOR_MASTER_SERVER"] = cleanMaster;
        psi.Environment["KONGOR_CDN_URL"] = $"http://{cleanCdn}";
        if (!string.IsNullOrWhiteSpace(cleanDiscordClientId))
        {
            psi.Environment["KONGOR_DISCORD_APP_ID"] = cleanDiscordClientId;
            psi.Environment["DISCORD_CLIENT_ID"] = cleanDiscordClientId;
        }

        try
        {
            int pid = LaunchConfiguredProcess(psi, resolved.ExecutablePath);
            EmitOrchestratorLog(
                "WILLOWMAKER",
                $"Launched WILLOWMAKER ({resolved.LayoutKind}, PID {pid}) from '{resolved.ExecutablePath}' with Master='{cleanMaster}' + CDN='{cleanCdn}'.",
                isError: false);

            // Belt-and-suspenders: if running live (not unit-test override), poll pipe briefly to confirm live sync
            bool postLaunchSynced = false;
            if (_processLauncherOverride is null && !IsRunningUnderTestRunner() && pid > 0)
            {
                for (int attempt = 0; attempt < 8 && !cancellationToken.IsCancellationRequested; attempt++)
                {
                    await Task.Delay(150, cancellationToken).ConfigureAwait(false);
                    CockpitEndpointSyncWireResponse? postSync = await TrySendEndpointSyncOverPipeAsync(
                        pipeName,
                        cleanMaster,
                        cleanCdn,
                        cleanDiscordClientId,
                        bringToFront: true,
                        timeoutMs: 200,
                        cancellationToken).ConfigureAwait(false);
                    if (postSync is { Success: true })
                    {
                        postLaunchSynced = true;
                        EmitOrchestratorLog(
                            "WILLOWMAKER IPC",
                            $"Post-launch IPC handshake confirmed for WILLOWMAKER PID {postSync.ProcessId} ({postSync.AppliedMasterServer}).",
                            isError: false);
                        break;
                    }
                }
            }

            return new CockpitLaunchOrSyncResult(
                Succeeded: true,
                WasAlreadyRunning: false,
                SyncedViaIpc: postLaunchSynced,
                LaunchedNewProcess: true,
                ProcessId: pid,
                AppliedMasterServer: cleanMaster,
                AppliedCdnEndpoint: cleanCdn,
                ExecutablePath: resolved.ExecutablePath,
                StatusMessage: $"Launched WILLOWMAKER ({resolved.LayoutKind}, PID {pid}) -> {cleanMaster}");
        }
        catch (Exception ex)
        {
            string err = $"Failed launching WILLOWMAKER from '{resolved.ExecutablePath}': {ex.Message}";
            EmitOrchestratorLog("WILLOWMAKER", err, isError: true);
            return new CockpitLaunchOrSyncResult(
                Succeeded: false,
                WasAlreadyRunning: false,
                SyncedViaIpc: false,
                LaunchedNewProcess: false,
                ProcessId: 0,
                AppliedMasterServer: cleanMaster,
                AppliedCdnEndpoint: cleanCdn,
                ExecutablePath: resolved.ExecutablePath,
                StatusMessage: err);
        }
    }

    public Task<CompelGuiLaunchResult> OpenCompelGuiAsync(
        string? rootOverride = null,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        CockpitBinaryResolution resolved = ResolveCompelGuiBinary(rootOverride);
        if (!resolved.Found || string.IsNullOrWhiteSpace(resolved.ExecutablePath))
        {
            EmitOrchestratorLog("COMPEL.GUI", resolved.DiagnosticMessage, isError: true);
            return Task.FromResult(new CompelGuiLaunchResult(
                Succeeded: false,
                ProcessId: 0,
                ExecutablePath: null,
                LayoutKind: resolved.LayoutKind,
                StatusMessage: resolved.DiagnosticMessage));
        }

        ProcessStartInfo psi = BuildDotNetAppProcessStartInfo(resolved);
        psi.Environment["COMPEL_CONTROL_PLANE_URL"] = "http://127.0.0.1:11234";

        try
        {
            int pid = LaunchConfiguredProcess(psi, resolved.ExecutablePath);
            string msg = $"Launched COMPEL.GUI ({resolved.LayoutKind}, PID {pid}) from '{resolved.ExecutablePath}'.";
            EmitOrchestratorLog("COMPEL.GUI", msg, isError: false);
            return Task.FromResult(new CompelGuiLaunchResult(
                Succeeded: true,
                ProcessId: pid,
                ExecutablePath: resolved.ExecutablePath,
                LayoutKind: resolved.LayoutKind,
                StatusMessage: msg));
        }
        catch (Exception ex)
        {
            string err = $"Failed launching COMPEL.GUI from '{resolved.ExecutablePath}': {ex.Message}";
            EmitOrchestratorLog("COMPEL.GUI", err, isError: true);
            return Task.FromResult(new CompelGuiLaunchResult(
                Succeeded: false,
                ProcessId: 0,
                ExecutablePath: resolved.ExecutablePath,
                LayoutKind: resolved.LayoutKind,
                StatusMessage: err));
        }
    }

    public async Task<IpcPipeHealthStatus> ProbeNativeClientPipeAsync(
        string? pipeNameOverride = null,
        int timeoutMs = 200,
        CancellationToken cancellationToken = default)
    {
        string pipeName = string.IsNullOrWhiteSpace(pipeNameOverride) ? DefaultNativeClientPipeName : pipeNameOverride.Trim();
        string displayPath = OperatingSystem.IsWindows()
            ? $@"\\.\pipe\{pipeName}"
            : $"/tmp/{pipeName}.sock";
        string probeReason = "Idle";

        Stopwatch sw = Stopwatch.StartNew();

        // 1. Probe NamedPipeClientStream (works on Windows \\.\pipe\<name> and .NET POSIX CoreFxPipe_<name>)
        try
        {
            using NamedPipeClientStream pipe = new(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
            using CancellationTokenSource connectCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            connectCts.CancelAfter(Math.Max(50, timeoutMs));
            await pipe.ConnectAsync(connectCts.Token).ConfigureAwait(false);

            uint reqId = Interlocked.Increment(ref _nextRequestId);
            await WriteIpcFrameAsync(pipe, OpPing, reqId, ReadOnlyMemory<byte>.Empty, connectCts.Token).ConfigureAwait(false);
            (ushort opCode, _) = await ReadIpcFrameAsync(pipe, connectCts.Token).ConfigureAwait(false);
            sw.Stop();
            double latency = Math.Round(sw.Elapsed.TotalMilliseconds, 2);

            if (opCode == OpPong)
            {
                return new IpcPipeHealthStatus(
                    "Game Client Native Pipe (TAO)",
                    true,
                    displayPath,
                    latency,
                    $"Connected — TAO / K2 Native IPC active ({latency:F1} ms • {displayPath})",
                    "#10B981");
            }
        }
        catch (OperationCanceledException ex) when (!cancellationToken.IsCancellationRequested)
        {
            probeReason = ex.GetType().Name;
        }
        catch (Exception ex) when (ex is TimeoutException or IOException or SocketException)
        {
            probeReason = ex.GetType().Name;
        }
        catch (Exception ex)
        {
            probeReason = ex.GetType().Name;
            Console.Error.WriteLine($"[CockpitBridge] Native client pipe probe notice on '{displayPath}': {ex.Message}");
        }

        // 2. POSIX raw UnixDomainSocket fallback (/tmp/kongor-native-client.sock)
        if (!OperatingSystem.IsWindows() && pipeNameOverride is null)
        {
            string unixSockPath = $"/tmp/{pipeName}.sock";
            if (File.Exists(unixSockPath))
            {
                try
                {
                    sw.Restart();
                    using Socket sock = new(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
                    using CancellationTokenSource sockCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                    sockCts.CancelAfter(Math.Max(50, timeoutMs));
                    await sock.ConnectAsync(new UnixDomainSocketEndPoint(unixSockPath), sockCts.Token).ConfigureAwait(false);
                    await using NetworkStream netStream = new(sock, ownsSocket: false);

                    uint reqId = Interlocked.Increment(ref _nextRequestId);
                    await WriteIpcFrameAsync(netStream, OpPing, reqId, ReadOnlyMemory<byte>.Empty, sockCts.Token).ConfigureAwait(false);
                    (ushort opCode, _) = await ReadIpcFrameAsync(netStream, sockCts.Token).ConfigureAwait(false);
                    sw.Stop();
                    double latency = Math.Round(sw.Elapsed.TotalMilliseconds, 2);
                    if (opCode == OpPong)
                    {
                        return new IpcPipeHealthStatus(
                            "Game Client Native Pipe (TAO)",
                            true,
                            unixSockPath,
                            latency,
                            $"Connected — POSIX Unix Socket active ({latency:F1} ms • {unixSockPath})",
                            "#10B981");
                    }
                }
                catch (Exception ex)
                {
                    Console.Error.WriteLine($"[CockpitBridge] POSIX Unix socket probe notice on '{unixSockPath}': {ex.Message}");
                }
            }
        }

        return new IpcPipeHealthStatus(
            "Game Client Native Pipe (TAO)",
            false,
            displayPath,
            0,
            $"Standby — Game client not running ({displayPath} • {probeReason})",
            "#94A3B8");
    }

    /// <summary>
    /// Discovers all currently connected Project Kongor IPC pipes across WILLOWMAKER / MCP, Game Client (TAO),
    /// Dedicated Game Servers (grouped &amp; deduplicated by port across local Named Pipes and Docker/K3s COMPEL workers),
    /// and Replay Renderer. Returns ONLY connected badges so offline pipes remain hidden.
    /// </summary>
    public async Task<IReadOnlyList<CockpitPipeBadgeItem>> ProbeConnectedCockpitPipesAsync(
        IReadOnlyList<CockpitServerPipeHint>? knownServerInstances = null,
        bool passiveNativeClientProbe = true,
        string? willowmakerPipeOverride = null,
        string? nativeClientPipeOverride = null,
        IReadOnlyList<string>? discoveredPipeNamesOverride = null,
        CancellationToken cancellationToken = default)
    {
        List<CockpitPipeBadgeItem> badges = [];

        // 1. Fast, non-intrusive OS pipe directory snapshot (with try/catch + File.Exists fallback for R4)
        (HashSet<string> osPipes, bool osEnumerationComplete) = discoveredPipeNamesOverride is not null
            ? (new HashSet<string>(discoveredPipeNamesOverride.Select(NormalizePipeLeafName), StringComparer.OrdinalIgnoreCase), true)
            : EnumerateActiveOsPipeNamesSafe();

        string wmPipeName = string.IsNullOrWhiteSpace(willowmakerPipeOverride)
            ? DefaultWillowmakerPipeName
            : willowmakerPipeOverride.Trim();
        string nativePipeName = string.IsNullOrWhiteSpace(nativeClientPipeOverride)
            ? DefaultNativeClientPipeName
            : nativeClientPipeOverride.Trim();

        // 2. WILLOWMAKER / MCP IPC probe (skip slow 250ms connect timeout when OS enumeration confirms pipe absent)
        bool shouldProbeWillowmaker =
            !string.IsNullOrWhiteSpace(willowmakerPipeOverride) ||
            !osEnumerationComplete ||
            osPipes.Contains(wmPipeName);

        Task<WillowmakerCockpitStatus> wmTask = shouldProbeWillowmaker
            ? ProbeWillowmakerIpcAsync(willowmakerPipeOverride, timeoutMs: 220, cancellationToken: cancellationToken)
            : Task.FromResult(new WillowmakerCockpitStatus(
                false,
                0,
                string.Empty,
                string.Empty,
                false,
                0,
                FormatPipeDisplayPath(wmPipeName),
                $"Standby (Not Running • {FormatPipeDisplayPath(wmPipeName)})",
                "#94A3B8",
                HostKind: "None"));

        // 3. Game Client Native Pipe (TAO): R1 passive existence check on Dashboard 8s loop to prevent ERROR_PIPE_BUSY
        Task<IpcPipeHealthStatus?> nativeTask;
        if (passiveNativeClientProbe)
        {
            bool nativeExists = osPipes.Contains(nativePipeName) || CheckNativeClientPipeExistsPassive(nativePipeName);
            string nativeDisplayPath = OperatingSystem.IsWindows()
                ? $@"\\.\pipe\{nativePipeName}"
                : $"/tmp/{nativePipeName}.sock";
            nativeTask = Task.FromResult<IpcPipeHealthStatus?>(nativeExists
                ? new IpcPipeHealthStatus(
                    "Game Client Native Pipe (TAO)",
                    true,
                    nativeDisplayPath,
                    0,
                    "Connected (HoN Native IPC)",
                    "#10B981")
                : null);
        }
        else
        {
            nativeTask = ProbeNativeClientPipeOrNullAsync(nativeClientPipeOverride, 200, cancellationToken);
        }

        await Task.WhenAll(wmTask, nativeTask).ConfigureAwait(false);
        WillowmakerCockpitStatus wmStatus = await wmTask.ConfigureAwait(false);
        IpcPipeHealthStatus? nativeStatus = await nativeTask.ConfigureAwait(false);

        if (wmStatus.IsConnected)
        {
            bool isMcpBridge = string.Equals(wmStatus.HostKind, "McpBridge", StringComparison.OrdinalIgnoreCase);
            string wmLabel = isMcpBridge ? "🤖 MCP IPC BRIDGE:" : "🎯 WILLOWMAKER IPC:";
            string wmColor = isMcpBridge ? "#38BDF8" : "#A78BFA";
            string wmKey = isMcpBridge ? "mcp-ipc-bridge" : "willowmaker-ipc";
            badges.Add(new CockpitPipeBadgeItem(
                Key: wmKey,
                Label: wmLabel,
                LabelColor: wmColor,
                StatusMessage: wmStatus.StatusMessage,
                StatusColor: "#10B981",
                PipePath: wmStatus.PipePath,
                ToolTipText: $"{wmLabel.TrimEnd(':')} • {wmStatus.PipePath}\n{wmStatus.StatusMessage}",
                IsConnected: true,
                LatencyMs: wmStatus.LatencyMs));
            LogPipeStateTransition("willowmaker-ipc", $"{wmStatus.HostKind}:{wmStatus.ProcessId}:{wmStatus.MasterServerEndpoint}", $"{wmLabel.TrimEnd(':')} connected ({wmStatus.StatusMessage})");
        }
        else
        {
            LogPipeStateTransition("willowmaker-ipc", "offline", $"WILLOWMAKER IPC disconnected ({wmStatus.PipePath})");
        }

        if (nativeStatus is { IsConnected: true })
        {
            string tip = passiveNativeClientProbe
                ? $"Game Client Native Pipe (TAO) • {nativeStatus.PipePath}\nPassive OS pipe detection active (zero contention with MCP game-worker)"
                : $"Game Client Native Pipe (TAO) • {nativeStatus.PipePath}\n{nativeStatus.StatusMessage}";
            badges.Add(new CockpitPipeBadgeItem(
                Key: "game-client-tao",
                Label: "🎮 GAME CLIENT IPC:",
                LabelColor: "#38BDF8",
                StatusMessage: nativeStatus.StatusMessage,
                StatusColor: "#10B981",
                PipePath: nativeStatus.PipePath,
                ToolTipText: tip,
                IsConnected: true,
                LatencyMs: nativeStatus.LatencyMs));
            LogPipeStateTransition("game-client-tao", "connected", $"Game Client Native Pipe active ({nativeStatus.PipePath})");
        }
        else
        {
            LogPipeStateTransition("game-client-tao", "offline", $"Game Client Native Pipe inactive ({FormatPipeDisplayPath(nativePipeName)})");
        }

        // 4. Dedicated Game Server Pipes — Deduplicated by Port (R10) & Grouped into One Summary Pill (R6)
        CockpitPipeBadgeItem? serverGroupedBadge = BuildGroupedServerPipeBadge(osPipes, knownServerInstances);
        if (serverGroupedBadge is not null)
        {
            badges.Add(serverGroupedBadge);
            LogPipeStateTransition("server-pipes", serverGroupedBadge.StatusMessage, $"Dedicated Server Pipes active: {serverGroupedBadge.StatusMessage}");
        }
        else
        {
            LogPipeStateTransition("server-pipes", "offline", "Dedicated Server Pipes: 0 connected");
        }

        // 5. Replay Renderer Pipes (hon_frame_pipe_* / hon_audio_pipe_*)
        List<string> replayPipes = osPipes
            .Where(p => p.StartsWith("hon_frame_pipe_", StringComparison.OrdinalIgnoreCase) ||
                        p.StartsWith("hon_audio_pipe_", StringComparison.OrdinalIgnoreCase))
            .OrderBy(p => p, StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (replayPipes.Count > 0)
        {
            string formattedPaths = string.Join(Environment.NewLine, replayPipes.Select(FormatPipeDisplayPath));
            badges.Add(new CockpitPipeBadgeItem(
                Key: "replay-renderer-ipc",
                Label: "🎬 REPLAY RENDERER:",
                LabelColor: "#F472B6",
                StatusMessage: $"Connected ({replayPipes.Count} stream{(replayPipes.Count == 1 ? string.Empty : "s")})",
                StatusColor: "#10B981",
                PipePath: FormatPipeDisplayPath(replayPipes[0]),
                ToolTipText: $"Active Replay Studio Renderer Pipes:\n{formattedPaths}",
                IsConnected: true));
            LogPipeStateTransition("replay-renderer-ipc", $"connected:{replayPipes.Count}", $"Replay Renderer IPC active ({replayPipes.Count} stream(s))");
        }
        else
        {
            LogPipeStateTransition("replay-renderer-ipc", "offline", "Replay Renderer IPC inactive");
        }

        return badges;
    }

    private async Task<IpcPipeHealthStatus?> ProbeNativeClientPipeOrNullAsync(
        string? pipeNameOverride,
        int timeoutMs,
        CancellationToken cancellationToken)
    {
        IpcPipeHealthStatus result = await ProbeNativeClientPipeAsync(
            pipeNameOverride,
            timeoutMs,
            cancellationToken).ConfigureAwait(false);
        return result.IsConnected ? result : null;
    }

    private static CockpitPipeBadgeItem? BuildGroupedServerPipeBadge(
        HashSet<string> osPipes,
        IReadOnlyList<CockpitServerPipeHint>? knownServerInstances)
    {
        // Keyed by canonical game/worker port to deduplicate hon-server-{Port} vs hon-server-native-{Port} (R10)
        Dictionary<int, (string Tag, int DisplayPort, string PipePath, string Transport, bool IsNativePreferred)> byPort = [];

        foreach (string leaf in osPipes)
        {
            if (TryParseServerPipePort(leaf, "hon-server-native-", out int nativePort))
            {
                string fullPath = OperatingSystem.IsWindows()
                    ? $@"\\.\pipe\{leaf}"
                    : $"/tmp/{leaf}.sock";
                byPort[nativePort] = ($":{nativePort}", nativePort, fullPath, "NativePipe", true);
            }
            else if (TryParseServerPipePort(leaf, "hon-server-", out int cmdPort))
            {
                if (!byPort.ContainsKey(cmdPort))
                {
                    string fullPath = OperatingSystem.IsWindows()
                        ? $@"\\.\pipe\{leaf}"
                        : $"/tmp/{leaf}.sock";
                    byPort[cmdPort] = ($":{cmdPort}", cmdPort, fullPath, "CommandPipe", false);
                }
            }
        }

        if (knownServerInstances is not null)
        {
            foreach (CockpitServerPipeHint inst in knownServerInstances)
            {
                int canonicalPort = inst.WorkerPort > 0 ? inst.WorkerPort : inst.GamePort;
                int displayPort = inst.GamePort > 0 ? inst.GamePort : canonicalPort;
                if (canonicalPort <= 0)
                {
                    continue;
                }

                string cleanTag = string.IsNullOrWhiteSpace(inst.InstanceTag)
                    ? $":{displayPort}"
                    : inst.InstanceTag.Trim();

                if (byPort.TryGetValue(canonicalPort, out var existing))
                {
                    // Enrich OS-discovered local pipe with human-friendly instance tag (e.g. #L1)
                    byPort[canonicalPort] = (cleanTag, displayPort, existing.PipePath, existing.Transport, existing.IsNativePreferred);
                    continue;
                }

                if (!inst.IsRunning)
                {
                    continue;
                }

                if (inst.ReportedIpcConnected == false)
                {
                    // Explicitly reported disconnected by updated COMPEL worker
                    continue;
                }

                if (inst.ReportedIpcConnected == true && !string.IsNullOrWhiteSpace(inst.ReportedIpcPath))
                {
                    string transport = string.IsNullOrWhiteSpace(inst.ReportedIpcTransport)
                        ? (inst.IsContainer ? "UnixSocket" : "NamedPipe")
                        : inst.ReportedIpcTransport!;
                    byPort[canonicalPort] = (cleanTag, displayPort, inst.ReportedIpcPath!, transport, true);
                }
                else if (inst.ReportedIpcConnected is null)
                {
                    // R6 zero-rebuild fallback: cached Docker / COMPEL container running before IpcConnected field existed
                    string inferredPath = inst.IsContainer
                        ? $"/tmp/hon-server-native-{canonicalPort}.sock (Docker COMPEL)"
                        : $@"\\.\pipe\hon-server-{canonicalPort} (COMPEL)";
                    string transport = inst.IsContainer ? "Container IPC" : "COMPEL IPC";
                    byPort[canonicalPort] = (cleanTag, displayPort, inferredPath, transport, false);
                }
            }
        }

        if (byPort.Count == 0)
        {
            return null;
        }

        var ordered = byPort
            .OrderBy(kvp => kvp.Value.DisplayPort)
            .ToList();

        int count = ordered.Count;
        List<string> tagSummary = [.. ordered.Take(4).Select(x => x.Value.Tag)];
        string extraSuffix = count > 4 ? $" +{count - 4}" : string.Empty;
        string summary = $"{count} Connected ({string.Join(", ", tagSummary)}{extraSuffix})";

        StringBuilder tooltip = new();
        tooltip.AppendLine($"Connected Game Server IPC Pipes ({count}):");
        foreach (var entry in ordered)
        {
            tooltip.AppendLine($"• {entry.Value.Tag} (UDP {entry.Value.DisplayPort}) — {entry.Value.Transport} • {entry.Value.PipePath}");
        }

        return new CockpitPipeBadgeItem(
            Key: "server-pipes",
            Label: "⚔️ SERVER PIPES:",
            LabelColor: "#34D399",
            StatusMessage: summary,
            StatusColor: "#10B981",
            PipePath: ordered[0].Value.PipePath,
            ToolTipText: tooltip.ToString().TrimEnd(),
            IsConnected: true);
    }

    private static bool TryParseServerPipePort(string pipeLeaf, string prefix, out int port)
    {
        port = 0;
        if (!pipeLeaf.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        string tail = pipeLeaf[prefix.Length..];
        if (tail.EndsWith(".sock", StringComparison.OrdinalIgnoreCase))
        {
            tail = tail[..^5];
        }

        return int.TryParse(tail, out port) && port > 0;
    }

    private static string NormalizePipeLeafName(string raw)
    {
        string trimmed = (raw ?? string.Empty).Trim();
        if (trimmed.StartsWith(@"\\.\pipe\", StringComparison.OrdinalIgnoreCase))
        {
            trimmed = trimmed[9..];
        }
        else if (trimmed.StartsWith("/tmp/", StringComparison.Ordinal))
        {
            trimmed = Path.GetFileName(trimmed);
            if (trimmed.StartsWith("CoreFxPipe_", StringComparison.Ordinal))
            {
                trimmed = trimmed[11..];
            }
            if (trimmed.EndsWith(".sock", StringComparison.OrdinalIgnoreCase))
            {
                trimmed = trimmed[..^5];
            }
        }
        return trimmed;
    }

    private static (HashSet<string> Pipes, bool EnumerationComplete) EnumerateActiveOsPipeNamesSafe()
    {
        HashSet<string> found = new(StringComparer.OrdinalIgnoreCase);

        if (OperatingSystem.IsWindows())
        {
            try
            {
                foreach (string pipe in Directory.EnumerateFiles(@"\\.\pipe\"))
                {
                    found.Add(NormalizePipeLeafName(pipe));
                }
                return (found, true);
            }
            catch (Exception ex)
            {
                // R4 fallback: Directory.EnumerateFiles(@"\\.\pipe\") can throw ArgumentException on exotic Win32 pipe names
                Console.Error.WriteLine($"[CockpitBridge] Pipe directory scan fallback triggered ({ex.GetType().Name}: {ex.Message}); probing known pipe names via File.Exists.");
                string[] staticCandidates = [DefaultWillowmakerPipeName, DefaultNativeClientPipeName];
                foreach (string candidate in staticCandidates)
                {
                    try
                    {
                        if (File.Exists($@"\\.\pipe\{candidate}"))
                        {
                            found.Add(candidate);
                        }
                    }
                    catch (Exception probeEx)
                    {
                        Console.Error.WriteLine($"[CockpitBridge] File.Exists fallback notice for '{candidate}': {probeEx.Message}");
                    }
                }

                int[] portRangesStart = [11235, 11335, 11435, 11485];
                foreach (int startPort in portRangesStart)
                {
                    for (int p = startPort; p < startPort + 8; p++)
                    {
                        string nativeLeaf = $"hon-server-native-{p}";
                        string cmdLeaf = $"hon-server-{p}";
                        try
                        {
                            if (File.Exists($@"\\.\pipe\{nativeLeaf}")) found.Add(nativeLeaf);
                            if (File.Exists($@"\\.\pipe\{cmdLeaf}")) found.Add(cmdLeaf);
                        }
                        catch (Exception portEx)
                        {
                            Console.Error.WriteLine($"[CockpitBridge] Port pipe fallback notice for {p}: {portEx.Message}");
                        }
                    }
                }

                return (found, false);
            }
        }

        try
        {
            if (Directory.Exists("/tmp"))
            {
                foreach (string file in Directory.EnumerateFiles("/tmp", "hon-server-native-*.sock"))
                {
                    found.Add(NormalizePipeLeafName(file));
                }

                if (File.Exists($"/tmp/{DefaultNativeClientPipeName}.sock"))
                {
                    found.Add(DefaultNativeClientPipeName);
                }
            }

            string tempDir = Path.GetTempPath();
            if (Directory.Exists(tempDir))
            {
                foreach (string file in Directory.EnumerateFiles(tempDir, "CoreFxPipe_*"))
                {
                    found.Add(NormalizePipeLeafName(file));
                }
            }

            return (found, true);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[CockpitBridge] POSIX socket scan fallback notice: {ex.Message}");
            return (found, false);
        }
    }

    private static bool CheckNativeClientPipeExistsPassive(string pipeName)
    {
        try
        {
            if (OperatingSystem.IsWindows())
            {
                return File.Exists($@"\\.\pipe\{pipeName}");
            }

            return File.Exists($"/tmp/{pipeName}.sock") ||
                   File.Exists(Path.Combine(Path.GetTempPath(), $"CoreFxPipe_{pipeName}"));
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[CockpitBridge] Passive native pipe check notice for '{pipeName}': {ex.Message}");
            return false;
        }
    }

    private void LogPipeStateTransition(string key, string stateSignature, string detail)
    {
        if (!_lastLoggedPipeStates.TryGetValue(key, out string? previous))
        {
            _lastLoggedPipeStates[key] = stateSignature;
            if (!string.Equals(stateSignature, "offline", StringComparison.OrdinalIgnoreCase))
            {
                EmitOrchestratorLog("IPC PIPES", $"[IPC-STATE] {detail}", isError: false);
            }
            return;
        }

        if (!string.Equals(previous, stateSignature, StringComparison.Ordinal))
        {
            _lastLoggedPipeStates[key] = stateSignature;
            EmitOrchestratorLog("IPC PIPES", $"[IPC-TRANSITION] {detail} (prev={previous})", isError: false);
        }
    }

    public async Task<CoreClusterReadinessStatus> ProbeCoreClusterEndpointsAsync(
        int masterPort = 55555,
        int chatPort = 11031,
        HttpMessageHandler? httpHandlerOverride = null,
        CancellationToken cancellationToken = default)
    {
        bool masterOk = false;
        int masterCode = 0;
        double masterMs = 0;
        string masterMsg;
        string masterColor;

        Stopwatch masterSw = Stopwatch.StartNew();
        try
        {
            using HttpClient httpClient = httpHandlerOverride is not null
                ? new HttpClient(httpHandlerOverride, disposeHandler: false) { Timeout = TimeSpan.FromMilliseconds(1500) }
                : ASPIRE.Common.Http.SharedSocketsHttpClientFactory.Create(TimeSpan.FromMilliseconds(1500));

            using HttpResponseMessage resp = await httpClient
                .GetAsync($"http://127.0.0.1:{masterPort}/health", cancellationToken)
                .ConfigureAwait(false);
            masterSw.Stop();
            masterMs = Math.Round(masterSw.Elapsed.TotalMilliseconds, 1);
            masterCode = (int)resp.StatusCode;
            masterOk = resp.IsSuccessStatusCode;
            masterMsg = masterOk
                ? $"Healthy — HTTP {masterCode} on 127.0.0.1:{masterPort}/health ({masterMs:F1} ms)"
                : $"Degraded — HTTP {masterCode} on 127.0.0.1:{masterPort}/health ({masterMs:F1} ms)";
            masterColor = masterOk ? "#10B981" : "#F59E0B";
        }
        catch (Exception ex)
        {
            masterSw.Stop();
            masterMsg = $"Offline — 127.0.0.1:{masterPort}/health unreachable ({ex.GetType().Name})";
            masterColor = "#EF4444";
        }

        bool chatOk = false;
        double chatMs = 0;
        string chatMsg;
        string chatColor;

        Stopwatch chatSw = Stopwatch.StartNew();
        try
        {
            using TcpClient tcp = new();
            using CancellationTokenSource chatCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            chatCts.CancelAfter(500);
            await tcp.ConnectAsync("127.0.0.1", chatPort, chatCts.Token).ConfigureAwait(false);
            chatSw.Stop();
            chatMs = Math.Round(chatSw.Elapsed.TotalMilliseconds, 1);
            chatOk = tcp.Connected;
            chatMsg = $"Listening — TRANSMUTANSTEIN TCP 127.0.0.1:{chatPort} ready ({chatMs:F1} ms)";
            chatColor = "#10B981";
        }
        catch (Exception ex)
        {
            chatSw.Stop();
            chatMsg = $"Offline — TCP 127.0.0.1:{chatPort} not accepting connections ({ex.GetType().Name})";
            chatColor = "#EF4444";
        }

        return new CoreClusterReadinessStatus(
            masterOk,
            masterCode,
            masterMs,
            masterMsg,
            masterColor,
            chatOk,
            chatMs,
            chatMsg,
            chatColor);
    }

    public (string StagingRoot, double FreeGb) SelectExportStagingRoot(
        string repoRoot,
        IReadOnlyList<(string DriveRoot, long FreeBytes)>? drivesOverride = null)
    {
        if (!OperatingSystem.IsWindows() && drivesOverride is null)
        {
            string posixStaging = Path.Combine(Path.GetTempPath(), "KongorPlaytestStaging");
            return (posixStaging, 50.0);
        }

        List<(string DriveRoot, long FreeBytes)> drives = [];
        if (drivesOverride is not null)
        {
            drives.AddRange(drivesOverride);
        }
        else
        {
            foreach (DriveInfo d in DriveInfo.GetDrives())
            {
                try
                {
                    if (d.IsReady && d.DriveType == DriveType.Fixed)
                    {
                        drives.Add((d.RootDirectory.FullName, d.AvailableFreeSpace));
                    }
                }
                catch (Exception ex)
                {
                    Console.Error.WriteLine($"[CockpitBridge] Drive inspection notice for '{d.Name}': {ex.Message}");
                }
            }
        }

        // Prefer C:\KongorPlaytestStaging when C: has >= 20 GiB free; otherwise prefer D:\KongorPlaytestStaging
        // or the fixed drive with the largest free space >= 20 GiB outside repoRoot.
        (string DriveRoot, long FreeBytes)? cDrive = drives.FirstOrDefault(
            d => d.DriveRoot.StartsWith("C:", StringComparison.OrdinalIgnoreCase));
        if (cDrive is { FreeBytes: >= MinimumExportStagingFreeBytes } cOk && !string.IsNullOrWhiteSpace(cOk.DriveRoot))
        {
            string candidate = Path.Combine(cOk.DriveRoot, "KongorPlaytestStaging");
            if (!IsSubpathOf(candidate, repoRoot))
            {
                return (candidate, Math.Round(cOk.FreeBytes / (1024.0 * 1024.0 * 1024.0), 2));
            }
        }

        (string DriveRoot, long FreeBytes)? dDrive = drives.FirstOrDefault(
            d => d.DriveRoot.StartsWith("D:", StringComparison.OrdinalIgnoreCase));
        if (dDrive is { FreeBytes: >= MinimumExportStagingFreeBytes } dOk && !string.IsNullOrWhiteSpace(dOk.DriveRoot))
        {
            string candidate = Path.Combine(dOk.DriveRoot, "KongorPlaytestStaging");
            if (!IsSubpathOf(candidate, repoRoot))
            {
                return (candidate, Math.Round(dOk.FreeBytes / (1024.0 * 1024.0 * 1024.0), 2));
            }
        }

        var bestEligible = drives
            .Where(d => d.FreeBytes >= MinimumExportStagingFreeBytes && !IsSubpathOf(Path.Combine(d.DriveRoot, "KongorPlaytestStaging"), repoRoot))
            .OrderByDescending(d => d.FreeBytes)
            .FirstOrDefault();

        if (!string.IsNullOrWhiteSpace(bestEligible.DriveRoot))
        {
            return (
                Path.Combine(bestEligible.DriveRoot, "KongorPlaytestStaging"),
                Math.Round(bestEligible.FreeBytes / (1024.0 * 1024.0 * 1024.0), 2));
        }

        // Fallback to highest-free drive so the export script emits its exact disk-space diagnostic
        var fallbackDrive = drives.OrderByDescending(d => d.FreeBytes).FirstOrDefault();
        if (!string.IsNullOrWhiteSpace(fallbackDrive.DriveRoot))
        {
            return (
                Path.Combine(fallbackDrive.DriveRoot, "KongorPlaytestStaging"),
                Math.Round(fallbackDrive.FreeBytes / (1024.0 * 1024.0 * 1024.0), 2));
        }

        return (@"D:\KongorPlaytestStaging", 0.0);
    }

    public async Task<ExportDryRunVerificationResult> VerifyExportReadinessDryRunAsync(
        string? repoRootOverride = null,
        CancellationToken cancellationToken = default)
    {
        string? repoRoot = FindRepositoryRoot(repoRootOverride);
        if (string.IsNullOrWhiteSpace(repoRoot))
        {
            string missingRepoMsg = "Export dry-run is only available from a Project Kongor source checkout (scripts/release/export_playtest_bundle.ps1 not found).";
            return new ExportDryRunVerificationResult(
                false,
                -1,
                string.Empty,
                0,
                missingRepoMsg,
                [new ExportDryRunStageItem("Source Checkout Locator", false, missingRepoMsg, "FAIL", "#EF4444")],
                missingRepoMsg);
        }

        string scriptPath = Path.Combine(repoRoot, "scripts", "release", "export_playtest_bundle.ps1");
        if (!File.Exists(scriptPath))
        {
            string missingScriptMsg = $"Export script not found at '{scriptPath}'.";
            return new ExportDryRunVerificationResult(
                false,
                -1,
                string.Empty,
                0,
                missingScriptMsg,
                [new ExportDryRunStageItem("Export Script Check", false, missingScriptMsg, "FAIL", "#EF4444")],
                missingScriptMsg);
        }

        (string stagingRoot, double freeGb) = SelectExportStagingRoot(repoRoot);
        string shellExe = OperatingSystem.IsWindows() ? "powershell.exe" : "pwsh";
        string args = $"-NoProfile -ExecutionPolicy Bypass -File \"{scriptPath}\" -Platform all -DryRun -AllowDirtyWorktree -StagingRoot \"{stagingRoot}\"";

        EmitOrchestratorLog(
            "EXPORT DRY-RUN",
            $"Starting pre-export readiness dry-run: {shellExe} {args} (Selected drive free: {freeGb:F2} GB)",
            isError: false);

        ProcessStartInfo psi = new()
        {
            FileName = shellExe,
            Arguments = args,
            WorkingDirectory = repoRoot,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        NexusOrchestratorService.EnsureDotNetSdkEnvironment(psi);

        StringBuilder combinedOutput = new();
        try
        {
            using Process proc = new() { StartInfo = psi };
            if (!proc.Start())
            {
                string failStart = $"Failed to start {shellExe} for export dry-run.";
                return new ExportDryRunVerificationResult(
                    false,
                    -1,
                    stagingRoot,
                    freeGb,
                    failStart,
                    [new ExportDryRunStageItem("Process Launch", false, failStart, "FAIL", "#EF4444")],
                    failStart);
            }

            Task<string> stdOutTask = proc.StandardOutput.ReadToEndAsync(cancellationToken);
            Task<string> stdErrTask = proc.StandardError.ReadToEndAsync(cancellationToken);
            await Task.WhenAll(stdOutTask, stdErrTask, proc.WaitForExitAsync(cancellationToken)).ConfigureAwait(false);

            string stdOut = await stdOutTask.ConfigureAwait(false);
            string stdErr = await stdErrTask.ConfigureAwait(false);
            if (!string.IsNullOrWhiteSpace(stdOut))
            {
                combinedOutput.AppendLine(stdOut.TrimEnd());
            }
            if (!string.IsNullOrWhiteSpace(stdErr))
            {
                combinedOutput.AppendLine(stdErr.TrimEnd());
            }

            string raw = StripAnsiCodes(combinedOutput.ToString());
            int exitCode = proc.ExitCode;
            IReadOnlyList<ExportDryRunStageItem> stages = ParseExportDryRunStages(raw, exitCode, stagingRoot, freeGb);
            bool succeeded = exitCode == 0 && stages.All(s => s.Passed);

            string summary = succeeded
                ? $"PASS — All {stages.Count} pre-export stages verified (0 writes • Staging '{stagingRoot}' with {freeGb:F1} GB free)."
                : $"FAIL (Exit {exitCode}) — {stages.Count(s => !s.Passed)} stage(s) reported issues. Inspect stage breakdown below.";

            EmitOrchestratorLog("EXPORT DRY-RUN", summary, isError: !succeeded);
            return new ExportDryRunVerificationResult(
                succeeded,
                exitCode,
                stagingRoot,
                freeGb,
                summary,
                stages,
                raw);
        }
        catch (Exception ex)
        {
            string errMsg = $"Export dry-run execution error: {ex.Message}";
            EmitOrchestratorLog("EXPORT DRY-RUN", errMsg, isError: true);
            return new ExportDryRunVerificationResult(
                false,
                -1,
                stagingRoot,
                freeGb,
                errMsg,
                [new ExportDryRunStageItem("Dry-Run Execution", false, errMsg, "FAIL", "#EF4444")],
                combinedOutput.ToString());
        }
    }

    public static IReadOnlyList<ExportDryRunStageItem> ParseExportDryRunStages(
        string rawOutput,
        int exitCode,
        string selectedStagingRoot = "",
        double selectedFreeGb = 0)
    {
        string text = rawOutput ?? string.Empty;
        List<ExportDryRunStageItem> stages = [];

        bool stage1Reached = text.Contains("[STAGE 1/9]", StringComparison.OrdinalIgnoreCase);
        bool leakFirewallRan = text.Contains("Dev-Profiler Leak Firewall", StringComparison.OrdinalIgnoreCase);
        bool leakFailed = text.Contains("LEAK FIREWALL", StringComparison.OrdinalIgnoreCase) &&
                          text.Contains("[ERROR]", StringComparison.OrdinalIgnoreCase);
        stages.Add(MakeStage(
            "1. Source Isolation & Leak Firewall",
            stage1Reached && leakFirewallRan && !leakFailed,
            leakFirewallRan
                ? "PE Import/Export Dev-Profiler Leak Firewall & workspace isolation verified."
                : ExtractFirstErrorLine(text, "Stage 1 isolation guard did not complete.")));

        Match diskMatch = DiskSpaceLineRegex().Match(text);
        bool diskInsufficient = text.Contains("Insufficient disk space", StringComparison.OrdinalIgnoreCase);
        stages.Add(MakeStage(
            "2. Staging Disk Budget (>= 20 GB)",
            diskMatch.Success && !diskInsufficient,
            diskMatch.Success
                ? diskMatch.Value.Trim()
                : (diskInsufficient
                    ? ExtractFirstErrorLine(text, $"Insufficient disk space on '{selectedStagingRoot}' ({selectedFreeGb:F1} GB free).")
                    : $"Target '{selectedStagingRoot}' ({selectedFreeGb:F1} GB free).")));

        bool winP2pOk = text.Contains("Verified P2P companion K2_SERVER binary", StringComparison.OrdinalIgnoreCase);
        bool dxvkOk = text.Contains("Verified DXVK", StringComparison.OrdinalIgnoreCase);
        stages.Add(MakeStage(
            "3. Windows K2_SERVER P2P & DXVK Cache",
            winP2pOk && dxvkOk,
            winP2pOk && dxvkOk
                ? "Companion K2_SERVER binary + DXVK 3.0.2 shader cache pair verified."
                : ExtractFirstErrorLine(text, "Windows K2_SERVER or DXVK pre-flight check failed.")));

        bool linuxOk = text.Contains("Verified Linux client & server binaries", StringComparison.OrdinalIgnoreCase);
        stages.Add(MakeStage(
            "4. Linux Client & Server Binaries (linux-x64)",
            linuxOk,
            linuxOk
                ? "hon-x86_64, hon-x86_64-server, Native Guard & runtime libs verified."
                : ExtractFirstErrorLine(text, "Linux client/server pre-flight check did not pass.")));

        bool macOk = text.Contains("Verified macOS client binaries", StringComparison.OrdinalIgnoreCase);
        stages.Add(MakeStage(
            "5. macOS Client Binaries (macos-x64)",
            macOk,
            macOk
                ? "macOS HoN64 client & dylib layout verified."
                : ExtractFirstErrorLine(text, "macOS client pre-flight check did not pass.")));

        bool rootAllowlistOk = text.Contains("allowlisted root K2 runtime files", StringComparison.OrdinalIgnoreCase) &&
                               text.Contains("Planned root launcher:", StringComparison.OrdinalIgnoreCase);
        stages.Add(MakeStage(
            "6. Root Allowlist & Launcher Layout",
            rootAllowlistOk,
            rootAllowlistOk
                ? "Allowlisted K2 runtime files & root WILLOWMAKER launcher verified."
                : ExtractFirstErrorLine(text, "Root allowlist / launcher layout check did not pass.")));

        bool zeroWriteComplete = exitCode == 0 &&
                                 text.Contains("Pre-flight succeeded with 0 filesystem writes", StringComparison.OrdinalIgnoreCase);
        stages.Add(MakeStage(
            "7. Zero-Write Dry-Run Gate",
            zeroWriteComplete,
            zeroWriteComplete
                ? "Dry-run completed with exit code 0 and 0 filesystem writes."
                : ExtractFirstErrorLine(text, $"Exited with code {exitCode}.")));

        return stages;
    }

    private static ExportDryRunStageItem MakeStage(string name, bool passed, string detail)
    {
        return new ExportDryRunStageItem(
            name,
            passed,
            detail,
            passed ? "PASS" : "FAIL",
            passed ? "#10B981" : "#EF4444");
    }

    private static string ExtractFirstErrorLine(string text, string fallback)
    {
        foreach (string rawLine in text.Split('\n'))
        {
            string line = rawLine.Trim();
            if (line.Contains("[ERROR]", StringComparison.OrdinalIgnoreCase) ||
                line.Contains("Exception", StringComparison.OrdinalIgnoreCase) ||
                line.Contains("Missing required", StringComparison.OrdinalIgnoreCase) ||
                line.Contains("Insufficient disk space", StringComparison.OrdinalIgnoreCase))
            {
                return line;
            }
        }
        return fallback;
    }

    private static string StripAnsiCodes(string input)
    {
        return string.IsNullOrEmpty(input) ? string.Empty : AnsiEscapeRegex().Replace(input, string.Empty);
    }

    [GeneratedRegex(@"\x1B\[[0-9;]*[A-Za-z]")]
    private static partial Regex AnsiEscapeRegex();

    [GeneratedRegex(@"Target drive '[^']+' free space: [0-9.]+\s*GB \(minimum required: [0-9.]+\s*GB\)\.")]
    private static partial Regex DiskSpaceLineRegex();

    private async Task<CockpitEndpointSyncWireResponse?> TrySendEndpointSyncOverPipeAsync(
        string pipeName,
        string masterServerEndpoint,
        string cdnEndpoint,
        string discordClientId,
        bool bringToFront,
        int timeoutMs,
        CancellationToken cancellationToken)
    {
        try
        {
            using NamedPipeClientStream pipe = new(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
            using CancellationTokenSource cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            cts.CancelAfter(Math.Max(50, timeoutMs));
            await pipe.ConnectAsync(cts.Token).ConfigureAwait(false);

            CockpitEndpointSyncWireRequest req = new()
            {
                MasterServerEndpoint = masterServerEndpoint,
                CdnEndpoint = cdnEndpoint,
                DiscordClientId = discordClientId,
                BringToFront = bringToFront,
                Source = "NEXUS.ControlPanel"
            };
            byte[] payload = JsonSerializer.SerializeToUtf8Bytes(
                req,
                CockpitBridgeJsonContext.Default.CockpitEndpointSyncWireRequest);
            uint reqId = Interlocked.Increment(ref _nextRequestId);

            await WriteIpcFrameAsync(pipe, OpSyncEndpoints, reqId, payload, cts.Token).ConfigureAwait(false);
            (ushort opCode, byte[] respBytes) = await ReadIpcFrameAsync(pipe, cts.Token).ConfigureAwait(false);
            if (opCode == OpSyncEndpointsResponse && respBytes.Length > 0)
            {
                return JsonSerializer.Deserialize(
                    respBytes,
                    CockpitBridgeJsonContext.Default.CockpitEndpointSyncWireResponse);
            }
        }
        catch (Exception ex) when (ex is OperationCanceledException or TimeoutException or IOException or SocketException)
        {
            return null;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[CockpitBridge] Endpoint sync IPC error on '{pipeName}': {ex.Message}");
        }

        return null;
    }

    private static async Task WriteIpcFrameAsync(
        Stream stream,
        ushort opCode,
        uint requestId,
        ReadOnlyMemory<byte> payload,
        CancellationToken cancellationToken)
    {
        byte[] header = new byte[IpcFrameHeaderBytes];
        BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(0, 2), IpcFrameMagic);
        BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(2, 2), opCode);
        BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(4, 4), payload.Length);
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(8, 4), requestId);

        await stream.WriteAsync(header, cancellationToken).ConfigureAwait(false);
        if (!payload.IsEmpty)
        {
            await stream.WriteAsync(payload, cancellationToken).ConfigureAwait(false);
        }
        await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task<(ushort OpCode, byte[] Payload)> ReadIpcFrameAsync(
        Stream stream,
        CancellationToken cancellationToken)
    {
        byte[] header = new byte[IpcFrameHeaderBytes];
        await ReadExactBytesAsync(stream, header, cancellationToken).ConfigureAwait(false);

        ushort magic = BinaryPrimitives.ReadUInt16LittleEndian(header.AsSpan(0, 2));
        if (magic != IpcFrameMagic)
        {
            throw new InvalidDataException($"Invalid IPC frame magic 0x{magic:X4} (expected 0x{IpcFrameMagic:X4}).");
        }

        ushort opCode = BinaryPrimitives.ReadUInt16LittleEndian(header.AsSpan(2, 2));
        int payloadLen = BinaryPrimitives.ReadInt32LittleEndian(header.AsSpan(4, 4));
        if (payloadLen < 0 || payloadLen > MaxIpcPayloadBytes)
        {
            throw new InvalidDataException($"Invalid IPC payload length {payloadLen} bytes.");
        }

        if (payloadLen == 0)
        {
            return (opCode, []);
        }

        byte[] payload = new byte[payloadLen];
        await ReadExactBytesAsync(stream, payload, cancellationToken).ConfigureAwait(false);
        return (opCode, payload);
    }

    private static async Task ReadExactBytesAsync(Stream stream, Memory<byte> buffer, CancellationToken cancellationToken)
    {
        int total = 0;
        while (total < buffer.Length)
        {
            int read = await stream.ReadAsync(buffer[total..], cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                throw new EndOfStreamException("IPC stream closed before full frame arrived.");
            }
            total += read;
        }
    }

    private static ProcessStartInfo BuildDotNetAppProcessStartInfo(CockpitBinaryResolution resolved)
    {
        string exePath = resolved.ExecutablePath!;
        string workDir = !string.IsNullOrWhiteSpace(resolved.WorkingDirectory) && Directory.Exists(resolved.WorkingDirectory)
            ? resolved.WorkingDirectory!
            : (Path.GetDirectoryName(exePath) ?? AppContext.BaseDirectory);

        ProcessStartInfo psi;
        if (resolved.LaunchViaDotnetHost)
        {
            psi = new ProcessStartInfo
            {
                FileName = ResolveDotNetExecutablePath(),
                Arguments = $"\"{exePath}\"",
                WorkingDirectory = workDir,
                UseShellExecute = false,
                CreateNoWindow = false
            };
        }
        else
        {
            EnsurePosixExecutableBitIfNeeded(exePath);
            psi = new ProcessStartInfo
            {
                FileName = exePath,
                WorkingDirectory = workDir,
                UseShellExecute = false,
                CreateNoWindow = false
            };
        }

        NexusOrchestratorService.EnsureDotNetSdkEnvironment(psi);
        return psi;
    }

    private int LaunchConfiguredProcess(ProcessStartInfo psi, string targetDescription)
    {
        if (_processLauncherOverride is not null)
        {
            int? overridePid = _processLauncherOverride(psi);
            if (overridePid is null or <= 0)
            {
                throw new InvalidOperationException($"Process launcher returned null/non-positive PID for '{targetDescription}'.");
            }
            return overridePid.Value;
        }

        if (IsRunningUnderTestRunner())
        {
            throw new InvalidOperationException(
                $"Blocked unmocked desktop process spawn of '{targetDescription}' inside unit test runner. Pass processLauncherOverride in CockpitAppBridgeService.");
        }

        Process? started = Process.Start(psi);
        if (started is null)
        {
            throw new InvalidOperationException($"Process.Start returned null for '{targetDescription}'.");
        }
        return started.Id;
    }

    private static string ResolveDotNetExecutablePath()
    {
        string exeName = OperatingSystem.IsWindows() ? "dotnet.exe" : "dotnet";
        string? dotnetRoot = Environment.GetEnvironmentVariable("DOTNET_ROOT");
        if (!string.IsNullOrWhiteSpace(dotnetRoot))
        {
            string fromRoot = Path.Combine(dotnetRoot, exeName);
            if (File.Exists(fromRoot))
            {
                return fromRoot;
            }
        }

        if (OperatingSystem.IsWindows())
        {
            string localApp = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            if (!string.IsNullOrWhiteSpace(localApp))
            {
                string localCandidate = Path.Combine(localApp, "Microsoft", "dotnet", "dotnet.exe");
                if (File.Exists(localCandidate))
                {
                    return localCandidate;
                }
            }
        }

        return exeName;
    }

    private static void EnsurePosixExecutableBitIfNeeded(string filePath)
    {
        if (OperatingSystem.IsWindows() || !File.Exists(filePath))
        {
            return;
        }

        try
        {
            UnixFileMode mode = File.GetUnixFileMode(filePath);
            if ((mode & UnixFileMode.UserExecute) == 0)
            {
                File.SetUnixFileMode(
                    filePath,
                    mode | UnixFileMode.UserExecute | UnixFileMode.GroupExecute | UnixFileMode.OtherExecute);
            }
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[CockpitBridge] POSIX chmod notice on '{filePath}': {ex.Message}");
        }
    }

    private static CockpitBinaryResolution? ResolveDevAppBinary(
        string appBinRoot,
        string binaryBaseName,
        string platform,
        string? preferredWorkDir)
    {
        if (!Directory.Exists(appBinRoot))
        {
            return null;
        }

        string nativeFile = platform == "windows" ? $"{binaryBaseName}.exe" : binaryBaseName;
        string dllFile = $"{binaryBaseName}.dll";
        string[] configs = ["Release", "Debug"];
        string[] ridSubdirs = platform switch
        {
            "windows" => ["", "win-x64", "win-arm64"],
            "macos" => ["", "osx-arm64", "osx-x64", "macos-x64"],
            _ => ["", "linux-x64", "linux-arm64"]
        };

        // Pass 1: native apphost executable (preferred)
        foreach (string cfg in configs)
        {
            string tfmDir = Path.Combine(appBinRoot, cfg, "net11.0");
            if (!Directory.Exists(tfmDir))
            {
                continue;
            }

            foreach (string rid in ridSubdirs)
            {
                string dir = string.IsNullOrEmpty(rid) ? tfmDir : Path.Combine(tfmDir, rid);
                string exeCandidate = Path.Combine(dir, nativeFile);
                if (File.Exists(exeCandidate))
                {
                    return new CockpitBinaryResolution(
                        true,
                        exeCandidate,
                        preferredWorkDir ?? dir,
                        LaunchViaDotnetHost: false,
                        LayoutKind: $"DevCheckout{cfg}",
                        DiagnosticMessage: $"Resolved {cfg} {binaryBaseName} executable at '{exeCandidate}'.");
                }
            }
        }

        // Pass 2: managed entry assembly (.dll via dotnet host)
        foreach (string cfg in configs)
        {
            string tfmDir = Path.Combine(appBinRoot, cfg, "net11.0");
            if (!Directory.Exists(tfmDir))
            {
                continue;
            }

            foreach (string rid in ridSubdirs)
            {
                string dir = string.IsNullOrEmpty(rid) ? tfmDir : Path.Combine(tfmDir, rid);
                string dllCandidate = Path.Combine(dir, dllFile);
                if (File.Exists(dllCandidate))
                {
                    return new CockpitBinaryResolution(
                        true,
                        dllCandidate,
                        preferredWorkDir ?? dir,
                        LaunchViaDotnetHost: true,
                        LayoutKind: $"DevCheckout{cfg}",
                        DiagnosticMessage: $"Resolved {cfg} {binaryBaseName} assembly at '{dllCandidate}' (dotnet host).");
                }
            }
        }

        return null;
    }

    private static string ResolvePreferredWillowmakerWorkDir(string rootDir, string fallbackDir)
    {
        string gamesClient = Path.Combine(rootDir, "games", "client");
        if (Directory.Exists(gamesClient))
        {
            return gamesClient;
        }
        return Directory.Exists(rootDir) ? rootDir : fallbackDir;
    }

    private static string? ResolveBundleRoot(string? rootOverride)
    {
        if (!string.IsNullOrWhiteSpace(rootOverride))
        {
            if (BundledLayoutResolver.TryResolve(out BundledLayout? probedLayout, rootOverride) && probedLayout is not null)
            {
                return probedLayout.Root;
            }

            // Also support direct standalone folder layouts passed explicitly (e.g. unit test fixtures or custom unpack dirs)
            if (Directory.Exists(rootOverride) && !File.Exists(Path.Combine(rootOverride, "ProjectKongor.slnx")))
            {
                return Path.GetFullPath(rootOverride);
            }
        }

        if (BundledLayoutResolver.TryResolve(out BundledLayout? activeLayout) && activeLayout is not null)
        {
            return activeLayout.Root;
        }

        return null;
    }

    private static string? FindRepositoryRoot(string? startDirectoryOverride)
    {
        string[] seeds = !string.IsNullOrWhiteSpace(startDirectoryOverride)
            ? [startDirectoryOverride]
            : [AppContext.BaseDirectory, Environment.CurrentDirectory];

        foreach (string seed in seeds)
        {
            if (string.IsNullOrWhiteSpace(seed))
            {
                continue;
            }

            try
            {
                DirectoryInfo? dir = new(Path.GetFullPath(seed));
                for (int depth = 0; depth < 8 && dir is not null; depth++)
                {
                    if (File.Exists(Path.Combine(dir.FullName, "ProjectKongor.slnx")) ||
                        Directory.Exists(Path.Combine(dir.FullName, "src", "apps", "willowmaker")))
                    {
                        return dir.FullName;
                    }
                    dir = dir.Parent;
                }
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[CockpitBridge] Repo root walk notice from '{seed}': {ex.Message}");
            }
        }

        return null;
    }

    private static string ResolveTargetPlatform(string? targetPlatformOverride)
    {
        if (!string.IsNullOrWhiteSpace(targetPlatformOverride))
        {
            return targetPlatformOverride.Trim().ToLowerInvariant();
        }
        if (OperatingSystem.IsWindows()) return "windows";
        if (OperatingSystem.IsMacOS()) return "macos";
        return "linux";
    }

    private static bool IsSubpathOf(string candidatePath, string parentPath)
    {
        if (string.IsNullOrWhiteSpace(candidatePath) || string.IsNullOrWhiteSpace(parentPath))
        {
            return false;
        }

        string fullCandidate = Path.GetFullPath(candidatePath)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
        string fullParent = Path.GetFullPath(parentPath)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;

        return fullCandidate.StartsWith(
            fullParent,
            OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
    }

    private static string StripHttpScheme(string value)
    {
        string trimmed = (value ?? string.Empty).Trim();
        if (trimmed.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            trimmed = trimmed[8..];
        }
        else if (trimmed.StartsWith("http://", StringComparison.OrdinalIgnoreCase))
        {
            trimmed = trimmed[7..];
        }
        return trimmed.TrimEnd('/');
    }

    private static bool IsRunningUnderTestRunner()
    {
        return AppDomain.CurrentDomain.GetAssemblies().Any(a =>
        {
            string? name = a.GetName().Name;
            return name != null && (name.StartsWith("xunit", StringComparison.OrdinalIgnoreCase) ||
                                    name.StartsWith("testhost", StringComparison.OrdinalIgnoreCase));
        });
    }

    private void EmitOrchestratorLog(string serviceName, string message, bool isError)
    {
        Console.Error.WriteLine($"[{serviceName}] {message}");
        _orchestrator?.AppendLog(new LogMessage
        {
            ServiceName = serviceName,
            Message = message,
            Level = isError ? "ERROR" : "INFO",
            IsError = isError,
            Timestamp = TimeProvider.System.GetUtcNow()
        });
    }
}

internal sealed class CockpitEndpointSyncWireRequest
{
    [JsonPropertyName("masterServerEndpoint")]
    public string MasterServerEndpoint { get; set; } = string.Empty;

    [JsonPropertyName("cdnEndpoint")]
    public string CdnEndpoint { get; set; } = string.Empty;

    [JsonPropertyName("discordClientId")]
    public string DiscordClientId { get; set; } = string.Empty;

    [JsonPropertyName("bringToFront")]
    public bool BringToFront { get; set; } = true;

    [JsonPropertyName("source")]
    public string Source { get; set; } = "NEXUS.ControlPanel";
}

internal sealed class CockpitEndpointSyncWireResponse
{
    [JsonPropertyName("success")]
    public bool Success { get; set; }

    [JsonPropertyName("appliedMasterServer")]
    public string AppliedMasterServer { get; set; } = string.Empty;

    [JsonPropertyName("appliedCdnEndpoint")]
    public string AppliedCdnEndpoint { get; set; } = string.Empty;

    [JsonPropertyName("broughtToFront")]
    public bool BroughtToFront { get; set; }

    [JsonPropertyName("processId")]
    public int ProcessId { get; set; }

    [JsonPropertyName("message")]
    public string Message { get; set; } = string.Empty;
}

internal sealed class CockpitLauncherStatusWireDto
{
    [JsonPropertyName("processId")]
    public int ProcessId { get; set; }

    [JsonPropertyName("version")]
    public string Version { get; set; } = string.Empty;

    [JsonPropertyName("masterServerEndpoint")]
    public string MasterServerEndpoint { get; set; } = string.Empty;

    [JsonPropertyName("cdnEndpoint")]
    public string CdnEndpoint { get; set; } = string.Empty;

    [JsonPropertyName("isGameRunning")]
    public bool IsGameRunning { get; set; }

    [JsonPropertyName("platform")]
    public string Platform { get; set; } = string.Empty;
}

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    PropertyNameCaseInsensitive = true)]
[JsonSerializable(typeof(CockpitEndpointSyncWireRequest))]
[JsonSerializable(typeof(CockpitEndpointSyncWireResponse))]
[JsonSerializable(typeof(CockpitLauncherStatusWireDto))]
internal partial class CockpitBridgeJsonContext : JsonSerializerContext
{
}
