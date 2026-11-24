// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// TAO (Together As One) MCP & Kongor CLI -- Created by guccidonut (https://github.com/guccidonut/PKV)

using System.Text.Json;
using System.Text.Json.Serialization;

namespace Kongor.Cli.Mcp.Protocol;

/// <summary>
/// Canonical execution modes for the TAO MCP Protocol Runtime.
/// Eliminates "Phantom Success" by explicitly distinguishing in-engine live execution
/// from synthetic fallback simulation or offline file operations.
/// </summary>
public static class ExecutionMode
{
    /// <summary>
    /// Command was dispatched directly into a live running K2 engine instance
    /// via IPC Named Pipe, in-engine Lua RPC, or dedicated server stdin.
    /// </summary>
    public const string LiveEngineRpc = "live_engine_rpc";

    /// <summary>
    /// Command executed in headless mock / simulated test harness mode.
    /// Used when the test environment suppresses desktop GUI process creation.
    /// </summary>
    public const string SyntheticFallback = "synthetic_fallback";

    /// <summary>
    /// Command operated purely on offline files, databases, or local disk artifacts.
    /// </summary>
    public const string OfflineDirect = "offline_direct";
}

/// <summary>
/// Authoritative liveness telemetry for the game client process (hon_x64.exe).
/// </summary>
public sealed class ClientLivenessStatus
{
    [JsonPropertyName("running")]
    public bool Running { get; set; }

    [JsonPropertyName("processId")]
    public int ProcessId { get; set; }

    [JsonPropertyName("engineReady")]
    public bool EngineReady { get; set; }

    [JsonPropertyName("ipcConnected")]
    public bool IpcConnected { get; set; }

    [JsonPropertyName("mainWindowHandle")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? MainWindowHandle { get; set; }

    [JsonPropertyName("windowMode")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? WindowMode { get; set; }
}

/// <summary>
/// Authoritative liveness telemetry for a dedicated game server worker process.
/// </summary>
public sealed class ServerLivenessStatus
{
    [JsonPropertyName("running")]
    public bool Running { get; set; }

    [JsonPropertyName("processId")]
    public int ProcessId { get; set; }

    [JsonPropertyName("port")]
    public int Port { get; set; }

    [JsonPropertyName("pipeConnected")]
    public bool PipeConnected { get; set; }

    [JsonPropertyName("map")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Map { get; set; }
}

/// <summary>
/// Dual-plane target status uniting client and server liveness into a single envelope.
/// </summary>
public sealed class TargetStatus
{
    [JsonPropertyName("client")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public ClientLivenessStatus? Client { get; set; }

    [JsonPropertyName("server")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public ServerLivenessStatus? Server { get; set; }
}

/// <summary>
/// Real-time execution performance metrics.
/// </summary>
public sealed class ExecutionMetrics
{
    [JsonPropertyName("roundtripLatencyMs")]
    public double RoundtripLatencyMs { get; set; }

    [JsonPropertyName("engineRenderFrame")]
    public long EngineRenderFrame { get; set; }

    [JsonPropertyName("timestamp")]
    public string Timestamp { get; set; } = string.Empty;
}

/// <summary>
/// Deterministic handoff recommendation for cross-server MCP workflow orchestration.
/// Bridges native Windows tools and containerized/POSIX workspace environments.
/// </summary>
public sealed class ToolHandoff
{
    [JsonPropertyName("server")]
    public string Server { get; set; } = string.Empty;

    [JsonPropertyName("tool")]
    public string Tool { get; set; } = string.Empty;

    [JsonPropertyName("reason")]
    public string Reason { get; set; } = string.Empty;

    [JsonPropertyName("arguments")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public object? Arguments { get; set; }

    [JsonPropertyName("target_workspace_path")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? TargetWorkspacePath { get; set; }

    [JsonPropertyName("target_native_path")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? TargetNativePath { get; set; }

    public static ToolHandoff Create(string server, string tool, string reason, object? arguments = null, string? path = null)
    {
        var handoff = new ToolHandoff
        {
            Server = server,
            Tool = tool,
            Reason = reason,
            Arguments = arguments
        };

        if (!string.IsNullOrEmpty(path))
        {
            try
            {
                handoff.TargetNativePath = Path.GetFullPath(path).Replace('/', '\\');
                handoff.TargetWorkspacePath = path.Replace('\\', '/');
            }
            catch
            {
                handoff.TargetNativePath = path.Replace('/', '\\');
                handoff.TargetWorkspacePath = path.Replace('\\', '/');
            }
        }

        return handoff;
    }
}

/// <summary>
/// Execution truth fidelity levels.
/// Eliminates "Phantom Success" by explicitly distinguishing in-engine live verification from
/// cached snapshots, offline direct file analysis, and simulated test doubles.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum ExecutionFidelity
{
    LiveVerified,
    DispatchedUnverified,
    CachedSnapshot,
    StaticOfflineAnalysis,
    SimulatedTestDouble
}

/// <summary>
/// Standardized Execution Truth Envelope returned by high-fidelity MCP tools.
/// </summary>
public static class ExecutionTruthEnvelope
{
    /// <summary>
    /// Creates a standardized JSON tool call result stamped with execution truth metadata.
    /// Automatically derives StaticOfflineAnalysis when executionMode == OfflineDirect and
    /// SimulatedTestDouble when executionMode == SyntheticFallback unless explicitly overridden (MCP-277, MCP-279).
    /// </summary>
    public static ToolCallResult CreateJsonResult(
        string status,
        string executionMode,
        TargetStatus? targetStatus,
        ExecutionMetrics? metrics,
        object? data = null,
        bool isError = false,
        IReadOnlyList<ToolHandoff>? handoffs = null,
        ExecutionFidelity? fidelity = null)
    {
        ExecutionFidelity effectiveFidelity = fidelity ?? (
            string.Equals(executionMode, ExecutionMode.OfflineDirect, StringComparison.OrdinalIgnoreCase)
                ? ExecutionFidelity.StaticOfflineAnalysis
                : string.Equals(executionMode, ExecutionMode.SyntheticFallback, StringComparison.OrdinalIgnoreCase)
                    ? ExecutionFidelity.SimulatedTestDouble
                    : ExecutionFidelity.LiveVerified);

        var envelope = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["status"] = status,
            ["ok"] = !isError,
            ["executionMode"] = executionMode,
            ["executionFidelity"] = effectiveFidelity.ToString(),
            ["targetStatus"] = targetStatus,
            ["metrics"] = metrics,
        };

        if (handoffs != null && handoffs.Count > 0)
        {
            envelope["handoffs"] = handoffs;
        }

        if (data != null)
        {
            // If data is an anonymous object or dictionary, merge its properties
            var json = JsonSerializer.Serialize(data);
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind == JsonValueKind.Object)
            {
                foreach (var prop in doc.RootElement.EnumerateObject())
                {
                    envelope[prop.Name] = prop.Value.Clone();
                }
            }
            else
            {
                envelope["data"] = data;
            }
        }

        return ToolCallResult.Json(envelope, isError);
    }
}
