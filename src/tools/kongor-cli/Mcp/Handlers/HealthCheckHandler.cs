// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// TAO (Together As One) MCP & Kongor CLI -- Created by guccidonut (https://github.com/guccidonut/PKV)

// ============================================================================
// File: HealthCheckHandler.cs
// System: Kongor MCP Server - Comprehensive System & Engine Health Probe
// Purpose: Implements 'kongor_health_check' (alias 'health_check') to verify
//          client processes, mailbox latency, TAO staleness, and error status.
// ============================================================================

using System;
using System.Diagnostics;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Kongor.Cli.Mcp.Protocol;
using Kongor.Cli.Mcp.Services;

namespace Kongor.Cli.Mcp.Handlers;

/// <summary>
/// MCP tool handler for 'kongor_health_check' (aliases: 'health_check', 'kongor_health').
/// Evaluates complete end-to-end health across:
/// 1. Client Process State (PID, memory RSS, alive)
/// 2. Mailbox IPC Connectivity (round-trip ping latency)
/// 3. TAO Layer Integrity (tao.lua hash comparison, staleness check)
/// 4. Engine & Log Error Baseline (errors and minidumps since checkpoint)
/// </summary>
public sealed class HealthCheckHandler
{
    private readonly ITaoClient? _taoClient;
    private readonly TaoCapabilitiesHandler? _taoCapabilitiesHandler;
    private readonly ErrorsSinceHandler _errorsSinceHandler;
    private readonly IClientProcessRegistry? _processRegistry;
    private readonly IGameIpcBridge? _gameIpcBridge;
    private readonly TimeProvider _timeProvider;
    private readonly Action<string>? _logger;
    private readonly bool _allowAmbientProcessFallback;

    public HealthCheckHandler(
        ITaoClient? taoClient = null,
        TaoCapabilitiesHandler? taoCapabilitiesHandler = null,
        ErrorsSinceHandler? errorsSinceHandler = null,
        IClientProcessRegistry? processRegistry = null,
        TimeProvider? timeProvider = null,
        Action<string>? logger = null,
        IGameIpcBridge? gameIpcBridge = null,
        bool? allowAmbientProcessFallback = null)
    {
        _timeProvider = timeProvider ?? TimeProvider.System;
        _logger = logger;
        _taoClient = taoClient;
        _taoCapabilitiesHandler = taoCapabilitiesHandler;
        _errorsSinceHandler = errorsSinceHandler ?? new ErrorsSinceHandler(timeProvider: _timeProvider, logger: _logger);
        _processRegistry = processRegistry;
        _gameIpcBridge = gameIpcBridge;
        _allowAmbientProcessFallback = allowAmbientProcessFallback ?? !GameServerManageHandler.IsTestExecution;
    }

    public async Task<ToolCallResult> HandleAsync(JsonElement arguments, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Stopwatch sw = Stopwatch.StartNew();

        string? checkpointToken = null;
        if (arguments.ValueKind == JsonValueKind.Object &&
            arguments.TryGetProperty("checkpoint", out JsonElement cpEl) &&
            cpEl.ValueKind == JsonValueKind.String &&
            !string.IsNullOrWhiteSpace(cpEl.GetString()))
        {
            checkpointToken = cpEl.GetString()!.Trim();
        }

        bool probeMailbox = true;
        if (arguments.ValueKind == JsonValueKind.Object &&
            arguments.TryGetProperty("probeMailbox", out JsonElement pmEl) &&
            (pmEl.ValueKind == JsonValueKind.True || pmEl.ValueKind == JsonValueKind.False))
        {
            probeMailbox = pmEl.GetBoolean();
        }

        bool probeCapabilities = true;
        if (arguments.ValueKind == JsonValueKind.Object &&
            arguments.TryGetProperty("probeCapabilities", out JsonElement pcEl) &&
            (pcEl.ValueKind == JsonValueKind.True || pcEl.ValueKind == JsonValueKind.False))
        {
            probeCapabilities = pcEl.GetBoolean();
        }

        bool fullDetail = false;
        if (arguments.ValueKind == JsonValueKind.Object)
        {
            if (arguments.TryGetProperty("detail", out JsonElement detEl) &&
                detEl.ValueKind == JsonValueKind.String &&
                string.Equals(detEl.GetString(), "full", StringComparison.OrdinalIgnoreCase))
            {
                fullDetail = true;
            }
            else if (arguments.TryGetProperty("verbose", out JsonElement verbEl) &&
                     verbEl.ValueKind == JsonValueKind.True)
            {
                fullDetail = true;
            }
        }

        DateTimeOffset now = _timeProvider.GetUtcNow();

        // 1. Client Process Assessment
        var clientStatus = ProbeClientProcess();

        // 2. Mailbox IPC Assessment
        var mailboxStatus = await ProbeMailboxAsync(probeMailbox, clientStatus.Running, cancellationToken).ConfigureAwait(false);

        // 3. TAO Layer & Hash Staleness Assessment
        var taoStatus = await ProbeTaoCapabilitiesAsync(probeCapabilities, cancellationToken).ConfigureAwait(false);

        // 4. Log & Minidump Errors Assessment
        var errorReport = _errorsSinceHandler.ScanLogs(
            checkpointToken != null && ErrorsSinceHandler.TryParseCheckpoint(checkpointToken, out var cutoff) ? cutoff : now.AddSeconds(-60),
            severityFilter: "error",
            limit: fullDetail ? 50 : 10);

        sw.Stop();
        double elapsedMs = Math.Round(sw.Elapsed.TotalMilliseconds, 2);

        // Compute overall health:
        // Healthy requires: Client is running, Mailbox is connected (or not requested), TAO is not stale, and no errors
        bool isClientOk = clientStatus.Running;
        bool isMailboxOk = !probeMailbox || mailboxStatus.Connected;
        bool isTaoOk = !probeCapabilities || (taoStatus.IsStale != true);
        bool isErrorsOk = errorReport.IsClean;

        bool overallHealthy = isClientOk && isMailboxOk && isTaoOk && isErrorsOk;

        string healthStatus;
        if (!clientStatus.Running && !mailboxStatus.Connected)
        {
            healthStatus = "offline";
        }
        else if (overallHealthy)
        {
            healthStatus = "healthy";
        }
        else
        {
            healthStatus = "degraded";
        }

        string newCheckpoint = ErrorsSinceHandler.CreateCheckpointToken(now);

        _logger?.Invoke($"[HealthCheckHandler] Overall: {healthStatus.ToUpperInvariant()}, Client: {clientStatus.Running}, Mailbox: {mailboxStatus.Connected} ({mailboxStatus.PingLatencyMs}ms), TAO Stale: {taoStatus.IsStale}, Errors: {errorReport.Errors.Count} in {elapsedMs}ms");

        int maxErrors = fullDetail ? 10 : 2;
        int maxDumps = fullDetail ? 5 : 1;

        if (!fullDetail)
        {
            var concisePayload = new
            {
                status = healthStatus,
                healthy = overallHealthy,
                pipeConnected = mailboxStatus.Connected,
                detail = "concise",
                timestampUtc = now.ToString("o"),
                checkpoint = newCheckpoint,
                elapsedMs,
                checks = new
                {
                    client = new
                    {
                        running = clientStatus.Running,
                        processId = clientStatus.ProcessId,
                        slotIndex = clientStatus.SlotIndex
                    },
                    mailbox = new
                    {
                        connected = mailboxStatus.Connected,
                        pingLatencyMs = mailboxStatus.PingLatencyMs,
                        phase = mailboxStatus.Phase,
                        fps = mailboxStatus.Fps,
                        heapMemoryKb = mailboxStatus.HeapMemoryKb,
                        activeInterface = mailboxStatus.ActiveInterface
                    },
                    tao = new
                    {
                        probed = taoStatus.Probed,
                        isStale = taoStatus.IsStale
                    },
                    errors = new
                    {
                        clean = errorReport.IsClean,
                        errorCount = errorReport.Errors.Count,
                        warningCount = errorReport.WarningCount,
                        minidumpCount = errorReport.RecentMinidumps.Count
                    }
                }
            };
            return ToolCallResult.Text(JsonSerializer.Serialize(concisePayload));
        }

        var payload = new
        {
            status = healthStatus,
            healthy = overallHealthy,
            pipeConnected = mailboxStatus.Connected,
            detail = "full",
            timestampUtc = now.ToString("o"),
            checkpoint = newCheckpoint,
            elapsedMs,
            checks = new
            {
                client = clientStatus,
                mailbox = mailboxStatus,
                tao = taoStatus,
                errors = new
                {
                    clean = errorReport.IsClean,
                    errorCount = errorReport.Errors.Count,
                    warningCount = errorReport.WarningCount,
                    minidumpCount = errorReport.RecentMinidumps.Count,
                    recentErrors = errorReport.Errors.Take(maxErrors).ToList(),
                    recentMinidumps = errorReport.RecentMinidumps.Take(maxDumps).ToList()
                }
            }
        };

        return ToolCallResult.Json(payload);
    }

    private ClientProcessHealth ProbeClientProcess()
    {
        try
        {
            if (_processRegistry != null)
            {
                var primary = _processRegistry.GetPrimaryClient();
                if (primary != null && primary.IsAlive)
                {
                    try
                    {
                        var proc = Process.GetProcessById(primary.ProcessId);
                        return new ClientProcessHealth
                        {
                            Running = true,
                            ProcessId = primary.ProcessId,
                            WorkingSetMb = Math.Round(proc.WorkingSet64 / (1024.0 * 1024.0), 2),
                            SlotIndex = primary.SlotIndex,
                            AgentOwner = primary.AgentId ?? "ambient"
                        };
                    }
                    catch (Exception ex)
                    {
                        _logger?.Invoke($"[HealthCheckHandler] Primary client PID {primary.ProcessId} lookup failed: {ex.Message}");
                    }
                }
            }

            if (_allowAmbientProcessFallback)
            {
                var procs = Process.GetProcessesByName("hon_x64");
                var live = procs.FirstOrDefault(p => !p.HasExited);
                if (live != null)
                {
                    return new ClientProcessHealth
                    {
                        Running = true,
                        ProcessId = live.Id,
                        WorkingSetMb = Math.Round(live.WorkingSet64 / (1024.0 * 1024.0), 2),
                        SlotIndex = 0,
                        AgentOwner = "ambient"
                    };
                }
            }
        }
        catch (Exception ex)
        {
            _logger?.Invoke($"[HealthCheckHandler] Error probing client process: {ex.Message}");
        }

        return new ClientProcessHealth
        {
            Running = false,
            ProcessId = 0,
            WorkingSetMb = 0,
            SlotIndex = -1,
            AgentOwner = "none"
        };
    }

    private async Task<MailboxHealth> ProbeMailboxAsync(bool probe, bool clientRunning, CancellationToken ct)
    {
        if (!probe || (_taoClient == null && _gameIpcBridge == null))
        {
            return new MailboxHealth { Connected = false, PingLatencyMs = 0, Version = "not_probed" };
        }

        // Fast-path when using the real CompositeGameIpcBridge and neither hon_x64 nor the native named pipe is active.
        if (_gameIpcBridge is CompositeGameIpcBridge compositeBridge &&
            !clientRunning &&
            !compositeBridge.IsConnected &&
            !compositeBridge.IsNativePipePresent())
        {
            _logger?.Invoke("[HealthCheckHandler] Fast-path offline mailbox probe: no running hon_x64 client and native pipe is absent.");
            return new MailboxHealth
            {
                Connected = false,
                PingLatencyMs = 0,
                Version = "offline"
            };
        }

        bool nativePipeConnected = false;
        double nativePipeMs = 0;
        string nativePipeName = "native-pipe";

        if (_gameIpcBridge != null)
        {
            try
            {
                Stopwatch swPipe = Stopwatch.StartNew();
                if (!_gameIpcBridge.IsConnected)
                {
                    await _gameIpcBridge.ConnectAsync(clientRunning ? 250 : 80, ct).ConfigureAwait(false);
                }
                swPipe.Stop();
                nativePipeMs = Math.Round(swPipe.Elapsed.TotalMilliseconds, 2);
                nativePipeConnected = _gameIpcBridge.IsConnected;
                if (!string.IsNullOrWhiteSpace(_gameIpcBridge.PipeName))
                {
                    nativePipeName = _gameIpcBridge.PipeName;
                }
            }
            catch (Exception ex)
            {
                _logger?.Invoke($"[HealthCheckHandler] Native pipe probe failed: {ex.Message}");
            }
        }

        if (_taoClient == null || (_gameIpcBridge != null && nativePipeConnected && !_taoClient.IsConnected))
        {
            return new MailboxHealth
            {
                Connected = nativePipeConnected,
                PingLatencyMs = nativePipeConnected ? nativePipeMs : 0,
                IpcRoundTripMs = nativePipeConnected ? nativePipeMs : 0,
                ColdStartMs = nativePipeMs,
                Version = nativePipeConnected ? nativePipeName : "offline",
                ActiveInterface = "main"
            };
        }

        // If _gameIpcBridge was supplied and native pipe is disconnected, avoid a 21s multi-tier TaoClient stall.
        if (_gameIpcBridge != null && !nativePipeConnected)
        {
            return new MailboxHealth
            {
                Connected = false,
                PingLatencyMs = 0,
                ColdStartMs = nativePipeMs,
                Version = "offline"
            };
        }

        try
        {
            using var timeoutCts = _gameIpcBridge != null
                ? CancellationTokenSource.CreateLinkedTokenSource(ct)
                : null;
            if (timeoutCts != null)
            {
                timeoutCts.CancelAfter(TimeSpan.FromMilliseconds(750));
            }
            CancellationToken probeToken = timeoutCts?.Token ?? ct;
            int rpcTimeoutMs = _gameIpcBridge != null ? 400 : 1500;

            Stopwatch swPing = Stopwatch.StartNew();
            var pingResp = await _taoClient.DispatchAsync("ping", new { }, rpcTimeoutMs, probeToken).ConfigureAwait(false);
            swPing.Stop();
            double pingMs = Math.Round(swPing.Elapsed.TotalMilliseconds, 2);

            TaoResponse<JsonElement>? resp = null;
            if (_gameIpcBridge == null || (pingResp != null && pingResp.Success))
            {
                resp = await _taoClient.DispatchAsync("health_check", new { }, rpcTimeoutMs, probeToken).ConfigureAwait(false);
            }
            if (resp == null || !resp.Success)
            {
                resp = pingResp;
            }

            bool ok = nativePipeConnected || (pingResp != null && pingResp.Success) || (resp != null && resp.Success && !string.Equals(resp.Status, "error", StringComparison.OrdinalIgnoreCase));
            int phase = 0;
            double fps = 0;
            double heapKb = 0;
            string actIface = "main";
            int qDepth = 0;

            if (resp is { Data.ValueKind: JsonValueKind.Object } validResp)
            {
                JsonElement data = validResp.Data;
                if (data.TryGetProperty("phase", out var pEl) && pEl.TryGetInt32(out int pVal)) phase = pVal;
                if (data.TryGetProperty("fps", out var fEl) && fEl.TryGetDouble(out double fVal)) fps = Math.Round(fVal, 1);
                if (data.TryGetProperty("heapMemoryKb", out var hEl) && hEl.TryGetDouble(out double hVal)) heapKb = Math.Round(hVal, 1);
                if (data.TryGetProperty("activeInterface", out var aEl)) actIface = aEl.GetString() ?? "main";
                if (data.TryGetProperty("queueDepth", out var qEl) && qEl.TryGetInt32(out int qVal)) qDepth = qVal;
            }

            double rawIpcMs = pingResp != null && pingResp.ElapsedMs > 0
                ? Math.Round(pingResp.ElapsedMs, 2)
                : (nativePipeConnected && nativePipeMs > 0 ? nativePipeMs : pingMs);

            return new MailboxHealth
            {
                Connected = ok,
                PingLatencyMs = rawIpcMs,
                IpcRoundTripMs = rawIpcMs,
                ColdStartMs = pingMs,
                Version = resp is { Data.ValueKind: JsonValueKind.Object } vr && vr.Data.TryGetProperty("version", out var v)
                    ? v.GetString() ?? "unknown"
                    : (nativePipeConnected ? nativePipeName : "3.0.0"),
                Phase = phase,
                Fps = fps,
                HeapMemoryKb = heapKb,
                ActiveInterface = actIface,
                QueueDepth = qDepth
            };
        }
        catch (Exception ex)
        {
            _logger?.Invoke($"[HealthCheckHandler] Mailbox health probe failed: {ex.Message}");
            if (nativePipeConnected)
            {
                return new MailboxHealth
                {
                    Connected = true,
                    PingLatencyMs = nativePipeMs,
                    IpcRoundTripMs = nativePipeMs,
                    ColdStartMs = nativePipeMs,
                    Version = nativePipeName,
                    ActiveInterface = "main"
                };
            }
            return new MailboxHealth { Connected = false, PingLatencyMs = 0, Version = "offline" };
        }
    }

    private async Task<TaoHealth> ProbeTaoCapabilitiesAsync(bool probe, CancellationToken ct)
    {
        if (!probe || _taoCapabilitiesHandler == null || (_gameIpcBridge != null && _taoClient?.IsConnected != true))
        {
            return new TaoHealth { Probed = false, IsStale = false, HandlersCount = 0, Build = "not_probed" };
        }

        try
        {
            var res = await _taoCapabilitiesHandler.HandleAsync(default, ct).ConfigureAwait(false);
            if (res.Content.Count > 0 && res.Content[0] is TextContentItem txt)
            {
                using var doc = JsonDocument.Parse(txt.Text);
                var root = doc.RootElement;
                string build = root.TryGetProperty("engineBuild", out var bEl) ? bEl.GetString() ?? "" : "";
                bool isOffline = string.Equals(build, "offline", StringComparison.OrdinalIgnoreCase) || string.Equals(build, "unknown", StringComparison.OrdinalIgnoreCase);
                bool? isStale = isOffline ? null : (root.TryGetProperty("isStale", out var stEl) && stEl.GetBoolean());
                int count = root.TryGetProperty("handlers", out var hEl) && hEl.ValueKind == JsonValueKind.Array ? hEl.GetArrayLength() : 0;
                string diskPath = root.TryGetProperty("hashedDiskPath", out var pEl) ? pEl.GetString() ?? "" : "";

                return new TaoHealth
                {
                    Probed = true,
                    IsStale = isStale,
                    HandlersCount = count,
                    Build = build,
                    HashedDiskPath = diskPath
                };
            }
        }
        catch (Exception ex)
        {
            _logger?.Invoke($"[HealthCheckHandler] TAO capabilities probe failed: {ex.Message}");
        }

        return new TaoHealth { Probed = false, IsStale = null, HandlersCount = 0, Build = "unknown" };
    }
}

public sealed class ClientProcessHealth
{
    public bool Running { get; set; }
    public int ProcessId { get; set; }
    public double WorkingSetMb { get; set; }
    public int SlotIndex { get; set; }
    public string AgentOwner { get; set; } = "";
}

public sealed class MailboxHealth
{
    public bool Connected { get; set; }
    public double PingLatencyMs { get; set; }
    public double IpcRoundTripMs { get; set; }
    public double ColdStartMs { get; set; }
    public string Version { get; set; } = "";
    public int Phase { get; set; }
    public double Fps { get; set; }
    public double HeapMemoryKb { get; set; }
    public string ActiveInterface { get; set; } = "main";
    public int QueueDepth { get; set; }
}

public sealed class TaoHealth
{
    public bool Probed { get; set; }
    public bool? IsStale { get; set; }
    public int HandlersCount { get; set; }
    public string Build { get; set; } = "";
    public string HashedDiskPath { get; set; } = "";
}
