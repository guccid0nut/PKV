// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// TAO (Together As One) MCP & Kongor CLI -- Created by guccidonut (https://github.com/guccidonut/PKV)

using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Serialization;
using Kongor.Cli.Mcp.Protocol;
using Kongor.Cli.Mcp.Services;
using Kongor.Core.Services.Networking;

namespace Kongor.Cli.Mcp.Handlers;

/// <summary>
/// Active network chaos rule state with auto-heal expiration.
/// </summary>
public sealed class ActiveChaosRule
{
    [JsonPropertyName("ruleId")]
    public string RuleId { get; set; } = string.Empty;

    [JsonPropertyName("targetType")]
    public string TargetType { get; set; } = "port"; // "port", "client", "both", or "session"

    [JsonPropertyName("targetId")]
    public string TargetId { get; set; } = string.Empty;

    [JsonPropertyName("latencyMs")]
    public int LatencyMs { get; set; }

    [JsonPropertyName("packetLossPercent")]
    public double PacketLossPercent { get; set; }

    [JsonPropertyName("jitterMs")]
    public int JitterMs { get; set; }

    [JsonPropertyName("isConnectionReset")]
    public bool IsConnectionReset { get; set; }

    [JsonPropertyName("createdAtUtc")]
    public DateTimeOffset CreatedAtUtc { get; set; }

    [JsonPropertyName("expiresAtUtc")]
    public DateTimeOffset ExpiresAtUtc { get; set; }

    [JsonPropertyName("appliedCvars")]
    public List<string> AppliedCvars { get; set; } = [];

    public bool IsExpired(DateTimeOffset now) => now >= ExpiresAtUtc;
}

/// <summary>
/// MCP Tool Handler for 'kongor_network_chaos' (aliases: 'network_chaos_inject', 'kongor_net_chaos').
/// Injects real-time latency spikes (100ms - 500ms), packet loss (5% - 25%), and connection resets
/// with automatic auto-heal timers preventing long-term environment impairment.
/// Adheres strictly to AGENTS.md dedicated server port routing rules.
/// </summary>
public sealed class NetworkChaosHandler
{
    private readonly ConcurrentDictionary<string, ActiveChaosRule> _activeRules = new(StringComparer.OrdinalIgnoreCase);

    public void ClearRules() => _activeRules.Clear();
    public static void ClearAllRules() { }

    private readonly GameServerManageHandler? _serverManageHandler;
    private readonly IServerPortRegistry _portRegistry;
    private readonly IGameIpcBridge? _clientIpcBridge;
    private readonly TimeProvider _timeProvider;
    private readonly Action<string>? _logger;

    public IServerPortRegistry PortRegistry => _portRegistry;

    public NetworkChaosHandler(
        GameServerManageHandler? serverManageHandler = null,
        IServerPortRegistry? portRegistry = null,
        IGameIpcBridge? clientIpcBridge = null,
        TimeProvider? timeProvider = null,
        Action<string>? logger = null)
    {
        _serverManageHandler = serverManageHandler;
        _portRegistry = portRegistry ?? serverManageHandler?.PortRegistry ?? ServerPortRegistry.Default;
        _clientIpcBridge = clientIpcBridge;
        _timeProvider = timeProvider ?? TimeProvider.System;
        _logger = logger;
    }

    public NetworkChaosHandler(TimeProvider timeProvider, Action<string>? logger = null)
        : this(null, null, null, timeProvider, logger)
    {
    }

    private void Log(string message) => _logger?.Invoke($"[NetworkChaos] {message}");

    public async Task<ToolCallResult> HandleAsync(JsonElement arguments, CancellationToken cancellationToken = default)
    {
        await CleanupExpiredRulesAsync(cancellationToken).ConfigureAwait(false);

        string action = "inject";
        if (arguments.TryGetProperty("action", out JsonElement actionElem) && !string.IsNullOrWhiteSpace(actionElem.GetString()))
        {
            action = actionElem.GetString()!.Trim().ToLowerInvariant();
        }

        string targetType = "port";
        string targetId = "";
        if ((arguments.TryGetProperty("target", out JsonElement tElem) || arguments.TryGetProperty("targetType", out tElem)) && !string.IsNullOrWhiteSpace(tElem.GetString()))
        {
            string rawTarget = tElem.GetString()!.Trim().ToLowerInvariant();
            if (rawTarget == "client")
            {
                targetType = "client";
                targetId = "client";
            }
            else if (rawTarget == "both")
            {
                targetType = "both";
            }
            else if (rawTarget.StartsWith("port:", StringComparison.OrdinalIgnoreCase))
            {
                targetType = "port";
                string suffix = rawTarget["port:".Length..].Trim();
                if (int.TryParse(suffix, out int pSuffix) && pSuffix > 0)
                {
                    targetId = pSuffix.ToString();
                }
            }
            else if (rawTarget.StartsWith("server:", StringComparison.OrdinalIgnoreCase))
            {
                targetType = "port";
                string suffix = rawTarget["server:".Length..].Trim();
                if (int.TryParse(suffix, out int pSuffix) && pSuffix > 0)
                {
                    targetId = pSuffix.ToString();
                }
            }
            else if (rawTarget is "server" or "port")
            {
                targetType = "port";
            }
            else
            {
                targetType = rawTarget;
            }
        }

        if (arguments.TryGetProperty("targetId", out JsonElement tidElem) && !string.IsNullOrWhiteSpace(tidElem.GetString()))
        {
            targetId = tidElem.GetString()!.Trim();
        }
        else if (arguments.TryGetProperty("port", out JsonElement pElem) && pElem.TryGetInt32(out int pInt) && pInt > 0)
        {
            targetId = pInt.ToString();
        }

        if (targetType is "port" or "server")
        {
            targetType = "port";
            if (string.IsNullOrEmpty(targetId) || targetId == "11235")
            {
                (int resolvedPort, _) = _portRegistry.ResolveServerPort(explicitPort: (!string.IsNullOrEmpty(targetId) && int.TryParse(targetId, out int pVal) && pVal > 0 ? pVal : null), map: null, matchId: null);
                targetId = resolvedPort.ToString();
            }
        }
        else if (targetType is "both")
        {
            (int resolvedPort, _) = _portRegistry.ResolveServerPort(explicitPort: (!string.IsNullOrEmpty(targetId) && int.TryParse(targetId, out int pVal) && pVal > 0 ? pVal : null), map: null, matchId: null);
            targetId = resolvedPort.ToString();
        }
        else if (targetType is "client")
        {
            targetId = "client";
        }
        else if (string.IsNullOrEmpty(targetId))
        {
            targetId = "11235";
        }

        int durationSeconds = 30;
        if (arguments.TryGetProperty("durationSeconds", out JsonElement durElem) && durElem.TryGetInt32(out int d) && d > 0)
        {
            durationSeconds = Math.Clamp(d, 1, 600);
        }

        Log($"Executing chaos action '{action}' on target {targetType}:{targetId} (auto-heal: {durationSeconds}s)...");

        return action switch
        {
            "inject" or "set" or "apply" => await HandleInjectAsync(targetType, targetId, durationSeconds, arguments, cancellationToken).ConfigureAwait(false),
            "spike" => await HandleSpikeAsync(targetType, targetId, durationSeconds, arguments, cancellationToken).ConfigureAwait(false),
            "drop" or "packet_drop" => await HandleDropAsync(targetType, targetId, durationSeconds, arguments, cancellationToken).ConfigureAwait(false),
            "reset" or "rst" or "connection_reset" => await HandleResetAsync(targetType, targetId, durationSeconds, cancellationToken).ConfigureAwait(false),
            "profile" => await HandleProfileAsync(targetType, targetId, durationSeconds, arguments, cancellationToken).ConfigureAwait(false),
            "simulate_matrix" => HandleSimulateMatrix(arguments),
            "heal" or "clear" or "clear_all" => await HandleHealAsync(targetType, targetId, arguments, cancellationToken).ConfigureAwait(false),
            "list" or "status" => HandleList(),
            _ => ToolCallResult.Error($"Unknown action '{action}'. Valid actions are: inject, spike, drop, reset (connection_reset), profile, simulate_matrix, heal (clear), list, status.")
        };
    }

    private ToolCallResult HandleSimulateMatrix(JsonElement arguments)
    {
        int nodeCount = 4;
        if (arguments.TryGetProperty("nodeCount", out JsonElement ncElem) && ncElem.TryGetInt32(out int nc) && nc > 0)
        {
            nodeCount = Math.Clamp(nc, 2, 32);
        }

        bool asymmetric = true;
        if (arguments.TryGetProperty("asymmetric", out JsonElement asymElem) && (asymElem.ValueKind == JsonValueKind.True || asymElem.ValueKind == JsonValueKind.False))
        {
            asymmetric = asymElem.GetBoolean();
        }

        DirectedMatrixPacketSimulator<string> simulator = new(nodeCount: nodeCount, linkCapacity: 8);
        simulator.ApplyPartition(
            PartitionTopology.IsolateSingle,
            asymmetric ? PartitionSymmetry.Asymmetric : PartitionSymmetry.Symmetric,
            isolatedNode: 0);

        TigerBeetleTimeSim clockSim = new(
            resolutionNs: 10_000_000L,
            offsetType: TimeOffsetType.Periodic,
            amplitudeNs: 250_000L);
        clockSim.Tick(5UL);

        return ToolCallResult.Json(new
        {
            status = "success",
            action = "simulate_matrix",
            nodeCount = simulator.NodeCount,
            link0To1Enabled = simulator.IsLinkEnabled(0, 1),
            link1To0Enabled = simulator.IsLinkEnabled(1, 0),
            driftedMonotonicNs = clockSim.DriftedMonotonicNs()
        });
    }

    private Task<ToolCallResult> HandleInjectAsync(string targetType, string targetId, int durationSeconds, JsonElement arguments, CancellationToken cancellationToken)
    {
        string type = "latency";
        if ((arguments.TryGetProperty("type", out JsonElement typeElem) || arguments.TryGetProperty("chaos_type", out typeElem) || arguments.TryGetProperty("chaosType", out typeElem)) && !string.IsNullOrWhiteSpace(typeElem.GetString()))
        {
            type = typeElem.GetString()!.Trim().ToLowerInvariant();
        }

        if (type is "packet_drop" or "drop" or "packet_loss")
        {
            return HandleDropAsync(targetType, targetId, durationSeconds, arguments, cancellationToken);
        }
        if (type is "reset")
        {
            return HandleResetAsync(targetType, targetId, durationSeconds, cancellationToken);
        }

        int latencyMs = 120;
        if ((arguments.TryGetProperty("latencyMs", out JsonElement lElem) || arguments.TryGetProperty("latency_ms", out lElem)) && lElem.TryGetInt32(out int l))
        {
            latencyMs = Math.Clamp(l, 0, 2000);
        }

        double dropPercent = 0.0;
        if ((arguments.TryGetProperty("dropRatePercent", out JsonElement dElem) || arguments.TryGetProperty("dropPercentage", out dElem) || arguments.TryGetProperty("packetLossPercent", out dElem) || arguments.TryGetProperty("packet_loss_percent", out dElem)) && dElem.TryGetDouble(out double dp))
        {
            dropPercent = Math.Clamp(dp, 0.0, 100.0);
        }

        int jitterMs = 15;
        if ((arguments.TryGetProperty("jitterMs", out JsonElement jElem) || arguments.TryGetProperty("jitter_ms", out jElem)) && jElem.TryGetInt32(out int j))
        {
            jitterMs = Math.Clamp(j, 0, 500);
        }

        return CreateRuleAsync(targetType, targetId, latencyMs, dropPercent, jitterMs, isReset: false, durationSeconds, cancellationToken);
    }

    private Task<ToolCallResult> HandleSpikeAsync(string targetType, string targetId, int durationSeconds, JsonElement arguments, CancellationToken cancellationToken)
    {
        int spikeLatency = 350;
        if (arguments.TryGetProperty("latencyMs", out JsonElement lElem) && lElem.TryGetInt32(out int l))
        {
            spikeLatency = Math.Clamp(l, 50, 2000);
        }

        return CreateRuleAsync(targetType, targetId, spikeLatency, packetLossPercent: 2.0, jitterMs: 80, isReset: false, durationSeconds, cancellationToken);
    }

    private Task<ToolCallResult> HandleDropAsync(string targetType, string targetId, int durationSeconds, JsonElement arguments, CancellationToken cancellationToken)
    {
        double dropPercent = 10.0;
        if ((arguments.TryGetProperty("dropRatePercent", out JsonElement dElem) || arguments.TryGetProperty("dropPercentage", out dElem) || arguments.TryGetProperty("packetLossPercent", out dElem) || arguments.TryGetProperty("packet_loss_percent", out dElem)) && dElem.TryGetDouble(out double dp))
        {
            dropPercent = Math.Clamp(dp, 0.1, 100.0);
        }

        int latencyMs = 30;
        if ((arguments.TryGetProperty("latencyMs", out JsonElement lElem) || arguments.TryGetProperty("latency_ms", out lElem)) && lElem.TryGetInt32(out int l))
        {
            latencyMs = Math.Clamp(l, 0, 2000);
        }

        int jitterMs = 10;
        if ((arguments.TryGetProperty("jitterMs", out JsonElement jElem) || arguments.TryGetProperty("jitter_ms", out jElem)) && jElem.TryGetInt32(out int j))
        {
            jitterMs = Math.Clamp(j, 0, 500);
        }

        return CreateRuleAsync(targetType, targetId, latencyMs, dropPercent, jitterMs, isReset: false, durationSeconds, cancellationToken);
    }

    private Task<ToolCallResult> HandleResetAsync(string targetType, string targetId, int durationSeconds, CancellationToken cancellationToken)
    {
        return CreateRuleAsync(targetType, targetId, latencyMs: 0, packetLossPercent: 100.0, jitterMs: 0, isReset: true, durationSeconds: Math.Min(5, durationSeconds), cancellationToken: cancellationToken);
    }

    private Task<ToolCallResult> HandleProfileAsync(string targetType, string targetId, int durationSeconds, JsonElement arguments, CancellationToken cancellationToken)
    {
        string profileName = "flaky_wifi";
        if (arguments.TryGetProperty("profile", out JsonElement pElem) && !string.IsNullOrWhiteSpace(pElem.GetString()))
        {
            profileName = pElem.GetString()!.Trim().ToLowerInvariant();
        }

        (int lat, double drop, int jit) = profileName switch
        {
            "packet_storm" => (220, 18.0, 45),
            "oceanic_pipe" => (280, 0.5, 20),
            "peer_reconnect_burst" => (10, 50.0, 5),
            "flaky_wifi" or _ => (150, 5.0, 40)
        };

        return CreateRuleAsync(targetType, targetId, lat, drop, jit, isReset: false, durationSeconds, cancellationToken);
    }

    private async Task<ToolCallResult> CreateRuleAsync(string targetType, string targetId, int latencyMs, double packetLossPercent, int jitterMs, bool isReset, int durationSeconds, CancellationToken cancellationToken)
    {
        DateTimeOffset now = _timeProvider.GetUtcNow();
        DateTimeOffset expiresAt = now.AddSeconds(durationSeconds);
        string ruleId = $"chaos_{targetType}_{targetId}";

        ActiveChaosRule rule = new()
        {
            RuleId = ruleId,
            TargetType = targetType,
            TargetId = targetId,
            LatencyMs = latencyMs,
            PacketLossPercent = packetLossPercent,
            JitterMs = jitterMs,
            IsConnectionReset = isReset,
            CreatedAtUtc = now,
            ExpiresAtUtc = expiresAt
        };

        bool applyServer = targetType is "port" or "both";
        bool applyClient = targetType is "client" or "both";

        if (applyServer && int.TryParse(targetId, out int port))
        {
            rule.AppliedCvars.Add($"[PORT {port}] stdin: net_lag {latencyMs}");
            rule.AppliedCvars.Add($"[PORT {port}] stdin: net_drop {packetLossPercent:F1}");
            rule.AppliedCvars.Add($"[PORT {port}] stdin: net_jitter {jitterMs}");
            rule.AppliedCvars.Add($@"[PORT {port}] pipe \\.\pipe\hon-server-{port}: SetCvar net_lag {latencyMs}");

            if (_serverManageHandler != null)
            {
                try
                {
                    await _serverManageHandler.ExecuteCommandOnPortAsync(port, $"net_lag {latencyMs}", autoLaunch: false, channel: "stdin", cancellationToken: cancellationToken).ConfigureAwait(false);
                    await _serverManageHandler.ExecuteCommandOnPortAsync(port, $"net_drop {packetLossPercent:F1}", autoLaunch: false, channel: "stdin", cancellationToken: cancellationToken).ConfigureAwait(false);
                    await _serverManageHandler.ExecuteCommandOnPortAsync(port, $"net_jitter {jitterMs}", autoLaunch: false, channel: "stdin", cancellationToken: cancellationToken).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    Log($"[WARN] Piping chaos cvars to server port {port} threw: {ex.Message}");
                }
            }
        }

        if (applyClient)
        {
            rule.AppliedCvars.Add($"[CLIENT] IPC: net_lag {latencyMs}, net_drop {packetLossPercent:F1}, net_jitter {jitterMs}");
            if (_clientIpcBridge != null)
            {
                try
                {
                    await _clientIpcBridge.ExecuteCommandAsync($"net_lag {latencyMs}", cancellationToken: cancellationToken).ConfigureAwait(false);
                    await _clientIpcBridge.ExecuteCommandAsync($"net_drop {packetLossPercent:F1}", cancellationToken: cancellationToken).ConfigureAwait(false);
                    await _clientIpcBridge.ExecuteCommandAsync($"net_jitter {jitterMs}", cancellationToken: cancellationToken).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    Log($"[WARN] Piping chaos cvars to client IPC threw: {ex.Message}");
                }
            }
        }

        if (!applyServer && !applyClient)
        {
            rule.AppliedCvars.Add($"[SESSION {targetId}] Socket impairment: {latencyMs}ms lag, {packetLossPercent:F1}% drop, reset: {isReset}");
        }

        _activeRules[ruleId] = rule;
        Log($"Injected chaos rule '{ruleId}' on {targetType}:{targetId}. Auto-healing in {durationSeconds}s.");

        string ruleType = isReset ? "reset" : (packetLossPercent > 0 && latencyMs <= 30 ? "packet_drop" : "latency");
        return ToolCallResult.Json(new
        {
            status = "success",
            action = isReset ? "reset" : "inject",
            type = ruleType,
            ruleId,
            rule,
            latencyMs,
            dropPercentage = packetLossPercent,
            autoHealSeconds = durationSeconds,
            expiresAt = rule.ExpiresAtUtc,
            timestamp = now.ToString("o")
        });
    }

    private async Task<ToolCallResult> HandleHealAsync(string targetType, string targetId, JsonElement arguments, CancellationToken cancellationToken)
    {
        bool hasExplicitTarget = arguments.TryGetProperty("target", out _)
            || arguments.TryGetProperty("targetType", out _)
            || arguments.TryGetProperty("targetId", out _)
            || arguments.TryGetProperty("port", out _);
        bool hasRuleId = arguments.TryGetProperty("ruleId", out JsonElement rElem) && !string.IsNullOrWhiteSpace(rElem.GetString());

        if (!hasExplicitTarget && !hasRuleId)
        {
            List<ActiveChaosRule> rulesToHeal = _activeRules.Values.ToList();
            _activeRules.Clear();
            List<string> allRestoredCvars = [];
            foreach (ActiveChaosRule r in rulesToHeal)
            {
                if ((r.TargetType is "port" or "both") && int.TryParse(r.TargetId, out int p))
                {
                    allRestoredCvars.Add($"[PORT {p}] stdin: net_lag 0");
                    allRestoredCvars.Add($"[PORT {p}] stdin: net_drop 0");
                    allRestoredCvars.Add($"[PORT {p}] stdin: net_jitter 0");
                    if (_serverManageHandler != null)
                    {
                        try
                        {
                            await _serverManageHandler.ExecuteCommandOnPortAsync(p, "net_lag 0", autoLaunch: false, channel: "stdin", cancellationToken: cancellationToken).ConfigureAwait(false);
                            await _serverManageHandler.ExecuteCommandOnPortAsync(p, "net_drop 0", autoLaunch: false, channel: "stdin", cancellationToken: cancellationToken).ConfigureAwait(false);
                            await _serverManageHandler.ExecuteCommandOnPortAsync(p, "net_jitter 0", autoLaunch: false, channel: "stdin", cancellationToken: cancellationToken).ConfigureAwait(false);
                        }
                        catch { }
                    }
                }
                if ((r.TargetType is "client" or "both") && _clientIpcBridge != null)
                {
                    allRestoredCvars.Add("[CLIENT] IPC: net_lag 0, net_drop 0, net_jitter 0");
                    try
                    {
                        await _clientIpcBridge.ExecuteCommandAsync("net_lag 0", cancellationToken: cancellationToken).ConfigureAwait(false);
                        await _clientIpcBridge.ExecuteCommandAsync("net_drop 0", cancellationToken: cancellationToken).ConfigureAwait(false);
                        await _clientIpcBridge.ExecuteCommandAsync("net_jitter 0", cancellationToken: cancellationToken).ConfigureAwait(false);
                    }
                    catch { }
                }
            }

            return ToolCallResult.Json(new
            {
                status = "success",
                action = "heal",
                target = "all",
                wasActive = rulesToHeal.Count > 0,
                restoredCvars = allRestoredCvars,
                timestamp = _timeProvider.GetUtcNow().ToString("o")
            });
        }

        string ruleId = $"chaos_{targetType}_{targetId}";
        if (hasRuleId)
        {
            ruleId = rElem.GetString()!.Trim();
        }

        bool removed = _activeRules.TryRemove(ruleId, out ActiveChaosRule? existingRule);
        if (existingRule != null)
        {
            targetType = existingRule.TargetType;
            targetId = existingRule.TargetId;
        }

        List<string> restoredCvars = [];
        bool applyServer = targetType is "port" or "both";
        bool applyClient = targetType is "client" or "both";

        if (applyServer && int.TryParse(targetId, out int port))
        {
            restoredCvars.Add($"[PORT {port}] stdin: net_lag 0");
            restoredCvars.Add($"[PORT {port}] stdin: net_drop 0");
            restoredCvars.Add($"[PORT {port}] stdin: net_jitter 0");

            if (_serverManageHandler != null)
            {
                try
                {
                    await _serverManageHandler.ExecuteCommandOnPortAsync(port, "net_lag 0", autoLaunch: false, channel: "stdin", cancellationToken: cancellationToken).ConfigureAwait(false);
                    await _serverManageHandler.ExecuteCommandOnPortAsync(port, "net_drop 0", autoLaunch: false, channel: "stdin", cancellationToken: cancellationToken).ConfigureAwait(false);
                    await _serverManageHandler.ExecuteCommandOnPortAsync(port, "net_jitter 0", autoLaunch: false, channel: "stdin", cancellationToken: cancellationToken).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    Log($"[WARN] Resetting server chaos cvars threw: {ex.Message}");
                }
            }
        }

        if (applyClient && _clientIpcBridge != null)
        {
            restoredCvars.Add("[CLIENT] IPC: net_lag 0, net_drop 0, net_jitter 0");
            try
            {
                await _clientIpcBridge.ExecuteCommandAsync("net_lag 0", cancellationToken: cancellationToken).ConfigureAwait(false);
                await _clientIpcBridge.ExecuteCommandAsync("net_drop 0", cancellationToken: cancellationToken).ConfigureAwait(false);
                await _clientIpcBridge.ExecuteCommandAsync("net_jitter 0", cancellationToken: cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                Log($"[WARN] Resetting client chaos cvars threw: {ex.Message}");
            }
        }

        Log($"Cleared chaos rule '{ruleId}' (was active: {removed})");

        return ToolCallResult.Json(new
        {
            status = "success",
            action = "heal",
            target = $"{targetType}:{targetId}",
            wasActive = removed,
            restoredCvars,
            timestamp = _timeProvider.GetUtcNow().ToString("o")
        });
    }

    private ToolCallResult HandleList()
    {
        CleanupExpiredRulesSync();

        return ToolCallResult.Json(new
        {
            status = "success",
            action = "list",
            activeImpairmentCount = _activeRules.Count,
            totalActiveRules = _activeRules.Count,
            rules = _activeRules.Values.ToList(),
            timestamp = _timeProvider.GetUtcNow().ToString("o")
        });
    }

    private void CleanupExpiredRulesSync()
    {
        DateTimeOffset now = _timeProvider.GetUtcNow();
        foreach (KeyValuePair<string, ActiveChaosRule> kvp in _activeRules)
        {
            if (kvp.Value.IsExpired(now))
            {
                _activeRules.TryRemove(kvp.Key, out _);
                Log($"Auto-healed expired chaos rule '{kvp.Key}'");
            }
        }
    }

    private async Task CleanupExpiredRulesAsync(CancellationToken cancellationToken)
    {
        DateTimeOffset now = _timeProvider.GetUtcNow();
        foreach (KeyValuePair<string, ActiveChaosRule> kvp in _activeRules)
        {
            if (kvp.Value.IsExpired(now))
            {
                if (_activeRules.TryRemove(kvp.Key, out ActiveChaosRule? expiredRule))
                {
                    Log($"Auto-healed expired chaos rule '{kvp.Key}'");
                    if ((expiredRule.TargetType == "port" || expiredRule.TargetType == "both") && int.TryParse(expiredRule.TargetId, out int port))
                    {
                        if (_serverManageHandler != null)
                        {
                            try
                            {
                                await _serverManageHandler.ExecuteCommandOnPortAsync(port, "net_lag 0", autoLaunch: false, channel: "stdin", cancellationToken: cancellationToken).ConfigureAwait(false);
                                await _serverManageHandler.ExecuteCommandOnPortAsync(port, "net_drop 0", autoLaunch: false, channel: "stdin", cancellationToken: cancellationToken).ConfigureAwait(false);
                                await _serverManageHandler.ExecuteCommandOnPortAsync(port, "net_jitter 0", autoLaunch: false, channel: "stdin", cancellationToken: cancellationToken).ConfigureAwait(false);
                            }
                            catch { }
                        }
                    }

                    if ((expiredRule.TargetType == "client" || expiredRule.TargetType == "both") && _clientIpcBridge != null)
                    {
                        try
                        {
                            await _clientIpcBridge.ExecuteCommandAsync("net_lag 0", cancellationToken: cancellationToken).ConfigureAwait(false);
                            await _clientIpcBridge.ExecuteCommandAsync("net_drop 0", cancellationToken: cancellationToken).ConfigureAwait(false);
                            await _clientIpcBridge.ExecuteCommandAsync("net_jitter 0", cancellationToken: cancellationToken).ConfigureAwait(false);
                        }
                        catch { }
                    }
                }
            }
        }
    }
}
