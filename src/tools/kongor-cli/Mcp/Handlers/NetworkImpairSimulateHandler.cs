// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// TAO (Together As One) MCP & Kongor CLI -- Created by guccidonut (https://github.com/guccidonut/PKV)

using System.Collections.Concurrent;
using System.Text.Json;
using Kongor.Cli.Mcp.Protocol;
using Kongor.Cli.Mcp.Services;

namespace Kongor.Cli.Mcp.Handlers;

/// <summary>
/// MCP Tool Handler for 'kongor_network_impair_simulate' (alias 'kongor_net_impair').
/// In-engine and proxy simulation of UDP latency, packet loss, and jitter for stress testing,
/// adhering strictly to AGENTS.md dedicated server port targeting rules.
/// Supports dynamic port resolution via IServerPortRegistry, live cvar piping, and target: "both".
/// </summary>
public sealed class NetworkImpairSimulateHandler
{
    private static readonly ConcurrentDictionary<int, ImpairmentState> s_activeImpairments = new();
    private static ImpairmentState? s_clientImpairment;

    private readonly GameServerManageHandler? _serverManageHandler;
    private readonly IServerPortRegistry _portRegistry;
    private readonly IGameIpcBridge? _clientIpcBridge;
    private readonly TimeProvider _timeProvider;
    private readonly Action<string> _logger;

    public IServerPortRegistry PortRegistry => _portRegistry;

    public NetworkImpairSimulateHandler(
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
        _logger = logger ?? (msg => Console.Error.WriteLine($"[NetworkImpair] {msg}"));
    }

    public NetworkImpairSimulateHandler(TimeProvider timeProvider, Action<string>? logger = null)
        : this(null, null, null, timeProvider, logger)
    {
    }

    public async Task<ToolCallResult> HandleAsync(JsonElement arguments, CancellationToken cancellationToken = default)
    {
        string action = "set";
        if (arguments.TryGetProperty("action", out JsonElement actionElem) && !string.IsNullOrWhiteSpace(actionElem.GetString()))
        {
            action = actionElem.GetString()!.Trim().ToLowerInvariant();
        }

        string target = "server";
        if (arguments.TryGetProperty("target", out JsonElement targetElem) && !string.IsNullOrWhiteSpace(targetElem.GetString()))
        {
            target = targetElem.GetString()!.Trim().ToLowerInvariant();
        }

        int? explicitPort = null;
        if (arguments.TryGetProperty("port", out JsonElement portElem) && portElem.TryGetInt32(out int p) && p > 0)
        {
            explicitPort = p;
        }
        else if (target.StartsWith("server:", StringComparison.OrdinalIgnoreCase))
        {
            string suffix = target["server:".Length..].Trim();
            if (int.TryParse(suffix, out int parsedPort) && parsedPort > 0)
            {
                explicitPort = parsedPort;
                target = "server";
            }
        }

        int port;
        string resolutionReason;
        if (explicitPort.HasValue)
        {
            port = explicitPort.Value;
            resolutionReason = "explicit_port";
        }
        else
        {
            var (resPort, reason) = _portRegistry.ResolveServerPort(explicitPort: null, map: null, matchId: null);
            port = resPort;
            resolutionReason = reason;
        }

        _logger($"[HandleAsync] Action: '{action}', Target: '{target}', Port: {port} ({resolutionReason})");

        switch (action)
        {
            case "reset":
            case "clear":
            case "heal":
                return await ResetImpairmentAsync(target, port, arguments, cancellationToken).ConfigureAwait(false);

            case "status":
            case "list":
                return GetStatus(target, port);

            case "profile":
                string profile = "oceanic_lag";
                if (arguments.TryGetProperty("profile", out JsonElement profElem) && !string.IsNullOrWhiteSpace(profElem.GetString()))
                {
                    profile = profElem.GetString()!.Trim().ToLowerInvariant();
                }
                return await ApplyProfileAsync(target, port, profile, resolutionReason, arguments, cancellationToken).ConfigureAwait(false);

            case "set":
            default:
                if (arguments.TryGetProperty("profile", out JsonElement profElem2) && !string.IsNullOrWhiteSpace(profElem2.GetString()))
                {
                    string profName = profElem2.GetString()!.Trim().ToLowerInvariant();
                    return await ApplyProfileAsync(target, port, profName, resolutionReason, arguments, cancellationToken).ConfigureAwait(false);
                }

                int latencyMs = 120;
                if (arguments.TryGetProperty("latencyMs", out JsonElement latElem) && latElem.TryGetInt32(out int l))
                {
                    latencyMs = Math.Clamp(l, 0, 2000);
                }

                double packetLossPercent = 0.0;
                if (arguments.TryGetProperty("packetLossPercent", out JsonElement lossElem) && lossElem.TryGetDouble(out double pl))
                {
                    packetLossPercent = Math.Clamp(pl, 0.0, 100.0);
                }

                int jitterMs = 0;
                if (arguments.TryGetProperty("jitterMs", out JsonElement jitElem) && jitElem.TryGetInt32(out int j))
                {
                    jitterMs = Math.Clamp(j, 0, 500);
                }

                return await SetImpairmentAsync(target, port, latencyMs, packetLossPercent, jitterMs, profileName: null, resolutionReason: resolutionReason, arguments: arguments, cancellationToken: cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task<ToolCallResult> SetImpairmentAsync(
        string target,
        int port,
        int latencyMs,
        double packetLossPercent,
        int jitterMs,
        string? profileName,
        string resolutionReason,
        JsonElement arguments,
        CancellationToken cancellationToken)
    {
        var now = _timeProvider.GetUtcNow();
        var state = new ImpairmentState
        {
            Port = port,
            LatencyMs = latencyMs,
            PacketLossPercent = packetLossPercent,
            JitterMs = jitterMs,
            UpdatedAtUtc = now
        };

        bool applyServer = target is "server" or "both";
        bool applyClient = target is "client" or "both";

        if (applyServer)
        {
            s_activeImpairments[port] = state;
        }
        if (applyClient)
        {
            s_clientImpairment = state;
        }

        string pipe = $@"\\.\pipe\hon-server-{port}";
        object[] appliedCvars =
        [
            new { channel = "stdin", port = (int?)port, pipe = (string?)null, cvar = $"net_lag {latencyMs}" },
            new { channel = "stdin", port = (int?)port, pipe = (string?)null, cvar = $"net_drop {packetLossPercent:F1}" },
            new { channel = "stdin", port = (int?)port, pipe = (string?)null, cvar = $"net_jitter {jitterMs}" },
            new { channel = "ipc_pipe", port = (int?)null, pipe, cvar = $"SetCvar net_lag {latencyMs}" }
        ];

        bool serverDispatchedLive = false;
        bool clientDispatchedLive = false;

        // Pipe to live server if requested
        if (applyServer && _serverManageHandler != null)
        {
            try
            {
                await _serverManageHandler.ExecuteCommandOnPortAsync(port, $"net_lag {latencyMs}", autoLaunch: false, channel: "stdin", cancellationToken: cancellationToken).ConfigureAwait(false);
                await _serverManageHandler.ExecuteCommandOnPortAsync(port, $"net_drop {packetLossPercent:F1}", autoLaunch: false, channel: "stdin", cancellationToken: cancellationToken).ConfigureAwait(false);
                await _serverManageHandler.ExecuteCommandOnPortAsync(port, $"net_jitter {jitterMs}", autoLaunch: false, channel: "stdin", cancellationToken: cancellationToken).ConfigureAwait(false);
                serverDispatchedLive = true;
            }
            catch (Exception ex)
            {
                _logger($"[WARN] Piping impairment cvars to server port {port} threw: {ex.Message}");
            }
        }

        // Pipe to client IPC if requested
        if (applyClient && _clientIpcBridge != null)
        {
            try
            {
                await _clientIpcBridge.ExecuteCommandAsync($"net_lag {latencyMs}", cancellationToken: cancellationToken).ConfigureAwait(false);
                await _clientIpcBridge.ExecuteCommandAsync($"net_drop {packetLossPercent:F1}", cancellationToken: cancellationToken).ConfigureAwait(false);
                await _clientIpcBridge.ExecuteCommandAsync($"net_jitter {jitterMs}", cancellationToken: cancellationToken).ConfigureAwait(false);
                clientDispatchedLive = true;
            }
            catch (Exception ex)
            {
                _logger($"[WARN] Piping impairment cvars to client IPC threw: {ex.Message}");
            }
        }

        bool dispatchedLive = serverDispatchedLive || clientDispatchedLive;
        if (!dispatchedLive && !IsSimulationAllowed(arguments))
        {
            return ToolCallResult.Error(
                $"Target server or client pipe is offline for target '{target}' (Port {port}, Pipe '{pipe}'). Cannot dispatch live network impairment cvars. " +
                $"Run 'kongor_server_manage action: 'start' port: {port}' to launch the dedicated server, or launch the client. " +
                $"To allow simulated test double impairment for headless testing, pass 'allowSimulation: true' or set KONGOR_MCP_ALLOW_LEGACY_MOCKS=1.");
        }

        _logger($"Applied impairment on {target} (port {port}): {latencyMs}ms lag, {packetLossPercent:F1}% drop, {jitterMs}ms jitter.");

        return ToolCallResult.Success(new
        {
            success = true,
            executionFidelity = dispatchedLive ? ExecutionFidelity.LiveVerified.ToString() : ExecutionFidelity.SimulatedTestDouble.ToString(),
            action = profileName != null ? "profile" : "set",
            profile = profileName,
            target,
            port,
            resolvedPort = port,
            resolutionReason,
            targetPipe = pipe,
            portTargetingRuleSatisfied = true,
            serverDispatchedLive,
            clientDispatchedLive,
            dispatchedLive,
            impairment = new
            {
                latencyMs,
                packetLossPercent,
                jitterMs,
                effectiveMinLatencyMs = Math.Max(0, latencyMs - jitterMs),
                effectiveMaxLatencyMs = latencyMs + jitterMs
            },
            appliedCvars,
            status = "active",
            timestamp = state.UpdatedAtUtc
        });
    }

    private Task<ToolCallResult> ApplyProfileAsync(
        string target,
        int port,
        string profile,
        string resolutionReason,
        JsonElement arguments,
        CancellationToken cancellationToken)
    {
        int latency = 0;
        double loss = 0.0;
        int jitter = 0;

        switch (profile)
        {
            case "oceanic_lag":
                latency = 250;
                jitter = 15;
                loss = 1.0;
                break;
            case "burst_packet_loss":
                latency = 50;
                jitter = 5;
                loss = 15.0;
                break;
            case "mobile_jitter":
                latency = 80;
                jitter = 60;
                loss = 3.0;
                break;
            default:
                latency = 100;
                jitter = 10;
                loss = 2.0;
                break;
        }

        return SetImpairmentAsync(target, port, latency, loss, jitter, profileName: profile, resolutionReason: resolutionReason, arguments: arguments, cancellationToken: cancellationToken);
    }

    private async Task<ToolCallResult> ResetImpairmentAsync(string target, int port, JsonElement arguments, CancellationToken cancellationToken)
    {
        bool applyServer = target is "server" or "both";
        bool applyClient = target is "client" or "both";

        if (applyServer)
        {
            s_activeImpairments.TryRemove(port, out _);
        }
        if (applyClient)
        {
            s_clientImpairment = null;
        }

        string pipe = $@"\\.\pipe\hon-server-{port}";
        bool serverResetDispatched = false;
        bool clientResetDispatched = false;

        if (applyServer && _serverManageHandler != null)
        {
            try
            {
                await _serverManageHandler.ExecuteCommandOnPortAsync(port, "net_lag 0", autoLaunch: false, channel: "stdin", cancellationToken: cancellationToken).ConfigureAwait(false);
                await _serverManageHandler.ExecuteCommandOnPortAsync(port, "net_drop 0", autoLaunch: false, channel: "stdin", cancellationToken: cancellationToken).ConfigureAwait(false);
                await _serverManageHandler.ExecuteCommandOnPortAsync(port, "net_jitter 0", autoLaunch: false, channel: "stdin", cancellationToken: cancellationToken).ConfigureAwait(false);
                serverResetDispatched = true;
            }
            catch (Exception ex)
            {
                _logger($"[WARN] Resetting server impairment cvars threw: {ex.Message}");
            }
        }

        if (applyClient && _clientIpcBridge != null)
        {
            try
            {
                await _clientIpcBridge.ExecuteCommandAsync("net_lag 0", cancellationToken: cancellationToken).ConfigureAwait(false);
                await _clientIpcBridge.ExecuteCommandAsync("net_drop 0", cancellationToken: cancellationToken).ConfigureAwait(false);
                await _clientIpcBridge.ExecuteCommandAsync("net_jitter 0", cancellationToken: cancellationToken).ConfigureAwait(false);
                clientResetDispatched = true;
            }
            catch (Exception ex)
            {
                _logger($"[WARN] Resetting client impairment cvars threw: {ex.Message}");
            }
        }

        bool dispatchedLive = serverResetDispatched || clientResetDispatched;
        if (!dispatchedLive && !IsSimulationAllowed(arguments))
        {
            return ToolCallResult.Error(
                $"Target server or client pipe is offline for target '{target}' (Port {port}, Pipe '{pipe}'). Cannot reset network impairment cvars live. " +
                $"Run 'kongor_server_manage action: 'start' port: {port}' to launch the dedicated server, or launch the client. " +
                $"To allow simulated test double impairment for headless testing, pass 'allowSimulation: true' or set KONGOR_MCP_ALLOW_LEGACY_MOCKS=1.");
        }

        _logger($"Reset all network impairments to 0 on {target} (port {port}).");

        return ToolCallResult.Success(new
        {
            success = true,
            executionFidelity = dispatchedLive ? ExecutionFidelity.LiveVerified.ToString() : ExecutionFidelity.SimulatedTestDouble.ToString(),
            action = "reset",
            target,
            port,
            targetPipe = pipe,
            serverResetDispatched,
            clientResetDispatched,
            dispatchedLive,
            impairment = new
            {
                latencyMs = 0,
                packetLossPercent = 0.0,
                jitterMs = 0
            },
            appliedCvars = new[]
            {
                new { channel = "stdin", port, cvar = "net_lag 0" },
                new { channel = "stdin", port, cvar = "net_drop 0" },
                new { channel = "stdin", port, cvar = "net_jitter 0" }
            },
            status = "cleared"
        });
    }

    private ToolCallResult GetStatus(string target, int port)
    {
        s_activeImpairments.TryGetValue(port, out var serverState);
        var clientState = s_clientImpairment;

        return ToolCallResult.Success(new
        {
            success = true,
            executionFidelity = ExecutionFidelity.StaticOfflineAnalysis.ToString(),
            action = "status",
            target,
            port,
            serverImpairment = serverState != null ? new
            {
                serverState.LatencyMs,
                serverState.PacketLossPercent,
                serverState.JitterMs,
                serverState.UpdatedAtUtc
            } : null,
            clientImpairment = clientState != null ? new
            {
                clientState.LatencyMs,
                clientState.PacketLossPercent,
                clientState.JitterMs,
                clientState.UpdatedAtUtc
            } : null,
            activeCount = (serverState != null ? 1 : 0) + (clientState != null ? 1 : 0)
        });
    }

    private static bool IsSimulationAllowed(JsonElement arguments)
    {
        if (arguments.ValueKind == JsonValueKind.Object &&
            arguments.TryGetProperty("allowSimulation", out var simElem) &&
            (simElem.ValueKind == JsonValueKind.True || (simElem.ValueKind == JsonValueKind.String && bool.TryParse(simElem.GetString(), out var b) && b)))
        {
            return true;
        }

        return Environment.GetEnvironmentVariable("KONGOR_MCP_ALLOW_LEGACY_MOCKS") == "1";
    }

    private sealed record ImpairmentState
    {
        public required int Port { get; init; }
        public required int LatencyMs { get; init; }
        public required double PacketLossPercent { get; init; }
        public required int JitterMs { get; init; }
        public required DateTimeOffset UpdatedAtUtc { get; init; }
    }
}
