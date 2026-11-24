// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// TAO (Together As One) MCP & Kongor CLI -- Created by guccidonut (https://github.com/guccidonut/PKV)

using System.Diagnostics;
using System.Text.Json;

namespace Kongor.Cli.Mcp.Protocol;

public sealed partial class McpServer
{
    /// <summary>
    /// Determines whether a tool invocation is permitted when the server is running in <c>--read-only</c> mode.
    /// Allows all tools registered under <see cref="McpToolCategory.ReadOnly" />, plus read-only inspection actions
    /// (e.g., <c>status</c>, <c>list</c>, <c>inspect</c>, <c>verify</c>, <c>health</c>) on management tools.
    /// </summary>
    public bool IsToolAllowedInReadOnlyMode(string toolName, JsonElement argumentsElement)
    {
        if (Kongor.Cli.Mcp.Handlers.CallToolHandler.IsBridgeName(toolName))
        {
            bool isBridgedCall = Kongor.Cli.Mcp.Handlers.CallToolHandler.TryExtractBridgedInvocation(
                argumentsElement,
                out string? innerTool,
                out JsonElement innerArgs,
                out string effectiveAction,
                out string? parseErr);

            if (effectiveAction is "list" or "search" or "describe")
            {
                return true;
            }

            if (parseErr != null || !isBridgedCall || string.IsNullOrWhiteSpace(innerTool) ||
                Kongor.Cli.Mcp.Handlers.CallToolHandler.IsBridgeName(innerTool))
            {
                return false;
            }

            return IsToolAllowedInReadOnlyMode(innerTool, innerArgs);
        }

        McpToolCategory category = _toolRegistry.GetToolCategory(toolName);
        if (category == McpToolCategory.ReadOnly)
        {
            return true;
        }

        if (argumentsElement.ValueKind == JsonValueKind.Object &&
            argumentsElement.TryGetProperty("action", out JsonElement actionEl) &&
            actionEl.ValueKind == JsonValueKind.String)
        {
            string? action = actionEl.GetString()?.Trim();
            if (!string.IsNullOrEmpty(action))
            {
                return action.Equals("status", StringComparison.OrdinalIgnoreCase) ||
                       action.Equals("list", StringComparison.OrdinalIgnoreCase) ||
                       action.Equals("inspect", StringComparison.OrdinalIgnoreCase) ||
                       action.Equals("verify", StringComparison.OrdinalIgnoreCase) ||
                       action.Equals("health", StringComparison.OrdinalIgnoreCase) ||
                       action.Equals("get", StringComparison.OrdinalIgnoreCase) ||
                       action.Equals("read", StringComparison.OrdinalIgnoreCase) ||
                       action.Equals("query", StringComparison.OrdinalIgnoreCase) ||
                       action.Equals("diff", StringComparison.OrdinalIgnoreCase);
            }
        }

        return false;
    }

    internal ResourceListResult GetResourcesListResult()
    {
        JsonRpcResponse resp = HandleResourcesList(new JsonRpcRequest { Id = new JsonRpcId(1), Method = "resources/list" });
        return resp.Result as ResourceListResult ?? new ResourceListResult();
    }

    internal ResourceTemplateListResult GetResourceTemplatesListResult()
    {
        JsonRpcResponse resp = HandleResourceTemplatesList(new JsonRpcRequest { Id = new JsonRpcId(1), Method = "resources/templates/list" });
        return resp.Result as ResourceTemplateListResult ?? new ResourceTemplateListResult();
    }

    internal async Task<ReadResourceResult> ReadResourceByUriAsync(string uri, CancellationToken cancellationToken = default)
    {
        using JsonDocument doc = JsonDocument.Parse(JsonRpcSerializer.Serialize(new { uri }));
        JsonRpcRequest req = new()
        {
            Id = new JsonRpcId(1),
            Method = "resources/read",
            Params = doc.RootElement.Clone()
        };

        JsonRpcResponse resp = await HandleResourcesReadAsync(req, cancellationToken).ConfigureAwait(false);
        if (resp.Error != null)
        {
            throw new InvalidOperationException(resp.Error.Message);
        }

        if (resp.Result is ReadResourceResult rrr)
        {
            return rrr;
        }

        throw new InvalidOperationException($"Unexpected resource read result for URI '{uri}'.");
    }

    internal void SubscribeResourceByUri(string uri)
    {
        using JsonDocument doc = JsonDocument.Parse(JsonRpcSerializer.Serialize(new { uri }));
        JsonRpcRequest req = new()
        {
            Id = new JsonRpcId(1),
            Method = "resources/subscribe",
            Params = doc.RootElement.Clone()
        };

        JsonRpcResponse resp = HandleResourcesSubscribe(req);
        if (resp.Error != null)
        {
            throw new InvalidOperationException(resp.Error.Message);
        }
    }

    internal void UnsubscribeResourceByUri(string uri)
    {
        using JsonDocument doc = JsonDocument.Parse(JsonRpcSerializer.Serialize(new { uri }));
        JsonRpcRequest req = new()
        {
            Id = new JsonRpcId(1),
            Method = "resources/unsubscribe",
            Params = doc.RootElement.Clone()
        };

        JsonRpcResponse resp = HandleResourcesUnsubscribe(req);
        if (resp.Error != null)
        {
            throw new InvalidOperationException(resp.Error.Message);
        }
    }

    internal PromptListResult GetPromptsListResult()
    {
        JsonRpcResponse resp = HandlePromptsList(new JsonRpcRequest { Id = new JsonRpcId(1), Method = "prompts/list" });
        return resp.Result as PromptListResult ?? new PromptListResult();
    }

    internal GetPromptResult GetPromptResultByName(string name, IDictionary<string, JsonElement>? arguments = null)
    {
        using JsonDocument doc = JsonDocument.Parse(JsonRpcSerializer.Serialize(new
        {
            name,
            arguments = arguments ?? new Dictionary<string, JsonElement>()
        }));

        JsonRpcRequest req = new()
        {
            Id = new JsonRpcId(1),
            Method = "prompts/get",
            Params = doc.RootElement.Clone()
        };

        JsonRpcResponse resp = HandlePromptsGet(req);
        if (resp.Error != null)
        {
            throw new InvalidOperationException(resp.Error.Message);
        }

        if (resp.Result is GetPromptResult gpr)
        {
            return gpr;
        }

        throw new InvalidOperationException($"Prompt '{name}' did not return a valid GetPromptResult.");
    }

    /// <summary>
    /// Executes a tool through the full 6-layer Kongor enterprise middleware pipeline
    /// (Read-Only Gate, OpenTelemetry Activity, Conversation Audit Log, Progress Context, Agent Context, and Timeout Guard).
    /// Used by <see cref="KongorOfficialMcpAdapter" /> so HTTP/SSE and sdk-stdio transports share 100% parity with stdio.
    /// </summary>
    internal async Task<ToolCallResult> ExecuteToolWithMiddlewareAsync(
        string toolName,
        JsonElement argumentsElement,
        string? requestId = null,
        IMcpProgressReporter? progressReporter = null,
        string? agentId = null,
        IMcpSamplingService? samplingServiceOverride = null,
        CancellationToken cancellationToken = default)
    {
        Stopwatch sw = Stopwatch.StartNew();
        string reqIdStr = requestId ?? Guid.NewGuid().ToString("N")[..8];

        using Activity? activity = s_activitySource.StartActivity($"MCP {toolName}", ActivityKind.Server);
        if (activity != null)
        {
            activity.SetTag("mcp.tool", toolName);
            activity.SetTag("mcp.id", reqIdStr);
            if (!string.IsNullOrEmpty(agentId))
            {
                activity.SetTag("mcp.agentId", agentId);
            }
        }

        string? actTraceId = activity != null && activity.TraceId != default ? activity.TraceId.ToHexString() : null;
        string? actSpanId = activity != null && activity.SpanId != default ? activity.SpanId.ToHexString() : null;
        string rawCallJson = JsonRpcSerializer.Serialize(new
        {
            jsonrpc = "2.0",
            id = reqIdStr,
            method = "tools/call",
            @params = new { name = toolName, arguments = argumentsElement }
        });

        _conversationLogger?.LogInbound(rawCallJson, "tools/call", reqIdStr, toolName, argumentsElement, actTraceId, actSpanId);

        if (_readOnlyMode && !IsToolAllowedInReadOnlyMode(toolName, argumentsElement))
        {
            LogDiagnostic($"[MCP Server] Blocked mutating tool '{toolName}' in read-only mode.");
            ToolCallResult blockedResult = ToolCallResult.Error(
                $"Tool '{toolName}' is blocked because the MCP server is running in '--read-only' mode. Launch without '--read-only' to allow mutating operations.");
            _conversationLogger?.LogOutbound(JsonRpcSerializer.Serialize(blockedResult), reqIdStr, true, sw.Elapsed.TotalMilliseconds, actTraceId, actSpanId);
            return blockedResult;
        }

        IMcpProgressReporter? prevProgressReporter = McpProgressContext.Current;
        IMcpSamplingService? prevSamplingService = McpSamplingContext.Current;
        string? prevAgentId = McpAgentContext.CurrentAgentId;

        McpProgressContext.Current = progressReporter;
        McpSamplingContext.Current = samplingServiceOverride ?? this;
        McpAgentContext.CurrentAgentId = agentId;

        int toolTimeoutSeconds = 60;
        if (argumentsElement.ValueKind == JsonValueKind.Object)
        {
            if (argumentsElement.TryGetProperty("timeoutMs", out JsonElement tMsEl) && tMsEl.TryGetInt32(out int tMs) && tMs > 0)
            {
                toolTimeoutSeconds = Math.Max(toolTimeoutSeconds, (tMs / 1000) + 15);
            }
            if (argumentsElement.TryGetProperty("durationSeconds", out JsonElement dsEl) && dsEl.TryGetInt32(out int ds) && ds > 0)
            {
                toolTimeoutSeconds = Math.Max(toolTimeoutSeconds, ds + 15);
            }
            if (argumentsElement.TryGetProperty("duration_seconds", out JsonElement dsEl2) && dsEl2.TryGetInt32(out int ds2) && ds2 > 0)
            {
                toolTimeoutSeconds = Math.Max(toolTimeoutSeconds, ds2 + 15);
            }
            if (argumentsElement.TryGetProperty("duration", out JsonElement durEl) && durEl.TryGetInt32(out int dur) && dur > 0)
            {
                toolTimeoutSeconds = Math.Max(toolTimeoutSeconds, dur + 15);
            }
        }

        ToolCallResult toolResult;
        try
        {
            using CancellationTokenSource timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutCts.CancelAfter(TimeSpan.FromSeconds(toolTimeoutSeconds));

            toolResult = await _toolRegistry.ExecuteToolAsync(toolName, argumentsElement, timeoutCts.Token).ConfigureAwait(false);
            activity?.SetTag("mcp.isError", toolResult.IsError);
            cancellationToken.ThrowIfCancellationRequested();
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            LogDiagnostic($"[MCP Server] Tool '{toolName}' timed out after {toolTimeoutSeconds} seconds.");
            toolResult = ToolCallResult.Error($"Tool '{toolName}' timed out after {toolTimeoutSeconds} seconds. Engine or UI did not respond in time.");
            activity?.SetTag("mcp.timeout", true);
        }
        catch (OperationCanceledException)
        {
            LogDiagnostic($"[MCP Server] Tool '{toolName}' was cancelled by client.");
            throw;
        }
        catch (Exception ex)
        {
            LogDiagnostic($"[MCP Server] Tool '{toolName}' encountered an unhandled error: {ex.Message}");
            toolResult = ToolCallResult.Error($"Tool '{toolName}' failed: {ex.Message}");
            activity?.SetTag("mcp.exception", ex.Message);
        }
        finally
        {
            McpProgressContext.Current = prevProgressReporter;
            McpSamplingContext.Current = prevSamplingService;
            McpAgentContext.CurrentAgentId = prevAgentId;
        }

        string responseJson = JsonRpcSerializer.Serialize(toolResult);
        _conversationLogger?.LogOutbound(responseJson, reqIdStr, toolResult.IsError == true, sw.Elapsed.TotalMilliseconds, actTraceId, actSpanId);
        return toolResult;
    }
}
