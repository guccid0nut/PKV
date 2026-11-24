// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// TAO (Together As One) MCP & Kongor CLI -- Created by guccidonut (https://github.com/guccidonut/PKV)

using System.Diagnostics;
using System.IO.Pipes;
using System.Net.Sockets;
using System.Text.Json;
using Kongor.Cli.Mcp.Handlers;
using Kongor.Cli.Mcp.Protocol;

namespace Kongor.Cli.Mcp.Services;

/// <summary>
/// Authoritative models and probing utilities for Execution Truth.
/// Guarantees that MCP responses report actual in-engine liveness rather than optimistic
/// or simulated successes ("Zero Phantom Success").
/// </summary>
public static class ExecutionTruth
{
    public const string ModeLiveEngine = "live_engine";
    public const string ModeOfflineSimulation = "offline_simulation";
    public const string ModeDryRunPlan = "dry_run_plan";

    public const string StatusLiveDispatched = "live_dispatched";
    public const string StatusTargetOffline = "target_offline";
    public const string StatusSuccess = "success";
    public const string StatusError = "error";

    public sealed class ClientLivenessStatus
    {
        public bool Running { get; set; }
        public int? Pid { get; set; }
        public bool EngineReady { get; set; }
        public bool IpcConnected { get; set; }
    }

    public sealed class ServerLivenessStatus
    {
        public bool Running { get; set; }
        public int? Pid { get; set; }
        public int Port { get; set; } = 11235;
        public bool PipeConnected { get; set; }
        public bool EngineReady { get; set; }
    }

    public sealed class TargetLivenessStatus
    {
        public ClientLivenessStatus Client { get; set; } = new();
        public ServerLivenessStatus Server { get; set; } = new();
    }

    public sealed class RemediationAdvice
    {
        public string Hint { get; set; } = string.Empty;
        public string SuggestedAction { get; set; } = string.Empty;
        public object SuggestedParams { get; set; } = new { };
    }

    /// <summary>
    /// Probes client liveness: checks process existence, window handle, IPC connectivity,
    /// and engine readiness (distinguishing process booting from ready engine).
    /// </summary>
    public static async Task<ClientLivenessStatus> ProbeClientLivenessAsync(
        GameClientManageHandler? clientHandler,
        IGameIpcBridge? ipcBridge,
        CancellationToken cancellationToken = default)
    {
        ClientLivenessStatus status = new();

        // In test environments, honor mock/configured state
        if (GameServerManageHandler.IsTestExecution)
        {
            bool isConnected = ipcBridge?.IsConnected == true;
            status.Running = isConnected;
            status.Pid = isConnected ? Environment.ProcessId : null;
            status.IpcConnected = isConnected;
            status.EngineReady = isConnected;
            return status;
        }

        Process? proc = clientHandler?.GetRunningClientProcess();
        if (proc != null)
        {
            try
            {
                proc.Refresh();
                if (!proc.HasExited)
                {
                    status.Running = true;
                    status.Pid = proc.Id;
                    status.IpcConnected = ipcBridge?.IsConnected == true || EngineTelemetryDiagnostics.CheckIpcConnectivity();

                    // Engine readiness: proc responding + window handle created or active IPC
                    bool hasHwnd = proc.MainWindowHandle != IntPtr.Zero;
                    bool responding = proc.Responding;
                    status.EngineReady = (status.IpcConnected || hasHwnd) && responding;
                }
            }
            catch
            {
                // Process inspection failed
            }
        }

        return status;
    }

    /// <summary>
    /// Probes dedicated server liveness on the specified port.
    /// </summary>
    public static Task<ServerLivenessStatus> ProbeServerLivenessAsync(
        int port,
        GameServerManageHandler? serverHandler,
        CancellationToken cancellationToken = default)
    {
        ServerLivenessStatus status = new() { Port = port };

        if (GameServerManageHandler.IsTestExecution)
        {
            return Task.FromResult(status);
        }

        try
        {
            IReadOnlyList<GameServerWorkerInfo> workers = serverHandler?.DiscoverRunningDedicatedServers() ?? [];
            GameServerWorkerInfo? match = workers.FirstOrDefault(w => w.Port == port);

            if (match != null)
            {
                status.Running = true;
                status.Pid = match.ProcessId;
                status.EngineReady = match.Responding;
            }

            // Probe named pipe presence (both native in-process pipe and supervisor pipe)
            string nativePipeName = $"hon-server-native-{port}";
            string pipeName = $"hon-server-{port}";
            if (OperatingSystem.IsWindows())
            {
                try
                {
                    status.PipeConnected =
                        Directory.GetFiles(@"\\.\pipe\", nativePipeName).Length > 0 ||
                        Directory.GetFiles(@"\\.\pipe\", pipeName).Length > 0;
                }
                catch
                {
                    status.PipeConnected = false;
                }
            }
        }
        catch
        {
            // Probe failed
        }

        return Task.FromResult(status);
    }

    /// <summary>
    /// Probes both client and server status into a combined TargetLivenessStatus model.
    /// </summary>
    public static async Task<TargetLivenessStatus> ProbeAllTargetsAsync(
        int serverPort,
        GameClientManageHandler? clientHandler,
        GameServerManageHandler? serverHandler,
        IGameIpcBridge? ipcBridge,
        CancellationToken cancellationToken = default)
    {
        Task<ClientLivenessStatus> clientTask = ProbeClientLivenessAsync(clientHandler, ipcBridge, cancellationToken);
        Task<ServerLivenessStatus> serverTask = ProbeServerLivenessAsync(serverPort, serverHandler, cancellationToken);

        await Task.WhenAll(clientTask, serverTask).ConfigureAwait(false);

        return new TargetLivenessStatus
        {
            Client = await clientTask.ConfigureAwait(false),
            Server = await serverTask.ConfigureAwait(false)
        };
    }

    /// <summary>
    /// Creates a standardized 'target_offline' tool call result with remediation instructions.
    /// </summary>
    public static ToolCallResult CreateTargetOfflineResult(
        string target,
        string attemptedAction,
        TargetLivenessStatus targetStatus,
        string? customHint = null,
        object? additionalPayload = null)
    {
        bool isClient = target.Equals("client", StringComparison.OrdinalIgnoreCase);

        string hint = customHint ?? (isClient
            ? "Player client is offline. Pass 'autoLaunch: true' or invoke 'kongor_game_client_manage action: launch'."
            : $"Dedicated game server is offline on port {targetStatus.Server.Port}. Pass 'autoLaunch: true' or invoke 'kongor_game_server_manage action: launch'.");

        string suggestedAction = isClient ? "kongor_game_client_manage" : "kongor_game_server_manage";
        object suggestedParams = isClient
            ? new { action = "launch", windowMode = "headless" }
            : new { action = "launch", port = targetStatus.Server.Port };

        Dictionary<string, object?> dict = new Dictionary<string, object?>
        {
            ["status"] = StatusTargetOffline,
            ["executionMode"] = ModeOfflineSimulation,
            ["target"] = target,
            ["action"] = attemptedAction,
            ["targetStatus"] = new
            {
                client = new
                {
                    running = targetStatus.Client.Running,
                    pid = targetStatus.Client.Pid,
                    engineReady = targetStatus.Client.EngineReady,
                    ipcConnected = targetStatus.Client.IpcConnected
                },
                server = new
                {
                    running = targetStatus.Server.Running,
                    pid = targetStatus.Server.Pid,
                    port = targetStatus.Server.Port,
                    pipeConnected = targetStatus.Server.PipeConnected,
                    engineReady = targetStatus.Server.EngineReady
                }
            },
            ["remediation"] = new
            {
                hint,
                suggestedAction,
                suggestedParams
            }
        };

        if (additionalPayload != null)
        {
            using JsonDocument doc = JsonSerializer.SerializeToDocument(additionalPayload);
            foreach (JsonProperty prop in doc.RootElement.EnumerateObject())
            {
                if (!dict.ContainsKey(prop.Name))
                {
                    dict[prop.Name] = prop.Value.Clone();
                }
            }
        }

        return ToolCallResult.Json(dict);
    }

    // ------------------------------------------------------------------------------------------
    // #184: rea-volatile-dimension-conformance-trust-gate (Gem 1F)
    // ------------------------------------------------------------------------------------------

    public static readonly IReadOnlySet<string> VolatileDimensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "timing",
        "timestamp",
        "pid",
        "ppid",
        "duration_ms",
        "created_at",
        "updated_at"
    };

    public sealed record ConformanceFirstDivergence(
        string Dimension,
        string EvidenceId,
        string Message);

    public sealed record ConformanceTrustGateReport(
        bool Conforms,
        IReadOnlyList<string> IsolatedVolatileDimensions,
        IReadOnlyList<string> CheckedDeterministicDimensions,
        ConformanceFirstDivergence? FirstDivergence,
        string BaselineCanonicalSha256,
        string CandidateCanonicalSha256);

    /// <summary>
    /// Isolates VOLATILE_DIMENSIONS ('timing', 'timestamp', 'pid', 'ppid', 'duration_ms', 'created_at', 'updated_at')
    /// and emits a deterministic 'first_divergence' ('dimension', 'evidence_id', 'message') record during
    /// post-reload conformance verification.
    /// </summary>
    public static ConformanceTrustGateReport EvaluateConformanceTrustGate(
        IReadOnlyDictionary<string, string?> baselineState,
        IReadOnlyDictionary<string, string?> candidateState,
        string evidenceId = "ev_post_reload")
    {
        List<string> isolatedVolatile = new List<string>();
        List<string> checkedDeterministic = new List<string>();
        ConformanceFirstDivergence? firstDivergence = null;

        SortedSet<string> allKeys = new SortedSet<string>(StringComparer.Ordinal);
        if (baselineState != null)
        {
            foreach (string k in baselineState.Keys)
            {
                allKeys.Add(k);
            }
        }
        if (candidateState != null)
        {
            foreach (string k in candidateState.Keys)
            {
                allKeys.Add(k);
            }
        }

        SortedDictionary<string, object?> cleanBase = new SortedDictionary<string, object?>(StringComparer.Ordinal);
        SortedDictionary<string, object?> cleanCand = new SortedDictionary<string, object?>(StringComparer.Ordinal);

        foreach (string key in allKeys)
        {
            if (VolatileDimensions.Contains(key))
            {
                isolatedVolatile.Add(key);
                continue;
            }

            checkedDeterministic.Add(key);
            string? bVal = null;
            string? cVal = null;
            bool hasBase = baselineState != null && baselineState.TryGetValue(key, out bVal);
            bool hasCand = candidateState != null && candidateState.TryGetValue(key, out cVal);
            string normBase = hasBase ? (bVal ?? string.Empty) : "<missing>";
            string normCand = hasCand ? (cVal ?? string.Empty) : "<missing>";
            cleanBase[key] = normBase;
            cleanCand[key] = normCand;

            if (firstDivergence == null && !string.Equals(normBase, normCand, StringComparison.Ordinal))
            {
                firstDivergence = new ConformanceFirstDivergence(
                    Dimension: key,
                    EvidenceId: string.IsNullOrWhiteSpace(evidenceId) ? "ev_post_reload" : evidenceId,
                    Message: $"Deterministic dimension '{key}' diverged: baseline='{normBase}' vs candidate='{normCand}'.");
            }
        }

        JsonElement baseElem = JsonSerializer.SerializeToElement(cleanBase);
        JsonElement candElem = JsonSerializer.SerializeToElement(cleanCand);
        string baseSha = ReaEpistemicForensicsEngine.ComputeClosureDigestSha256(baseElem, excludeSelfDigestField: false);
        string candSha = ReaEpistemicForensicsEngine.ComputeClosureDigestSha256(candElem, excludeSelfDigestField: false);

        return new ConformanceTrustGateReport(
            Conforms: firstDivergence == null && string.Equals(baseSha, candSha, StringComparison.Ordinal),
            IsolatedVolatileDimensions: isolatedVolatile,
            CheckedDeterministicDimensions: checkedDeterministic,
            FirstDivergence: firstDivergence,
            BaselineCanonicalSha256: baseSha,
            CandidateCanonicalSha256: candSha);
    }
}
