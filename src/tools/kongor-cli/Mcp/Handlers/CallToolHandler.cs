// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// TAO (Together As One) MCP & Kongor CLI -- Created by guccidonut (https://github.com/guccidonut/PKV)

using System.Diagnostics;
using System.Text.Json;
using Kongor.Cli.Mcp.Protocol;
using Kongor.Cli.Mcp.Registration;

namespace Kongor.Cli.Mcp.Handlers;

/// <summary>
/// Stateless cross-IDE meta-bridge and catalog discovery handler ('kongor_call_tool').
/// Enables IDEs and agents without dynamic 'notifications/tools/list_changed' support
/// (Cursor, Zed, JetBrains, Windsurf, Codex, VS Code) to discover, inspect, and invoke
/// any registered TAO MCP tool while preserving single-billing rate limits, preflight
/// checks, and per-tool telemetry in <see cref="McpToolRegistry.ExecuteToolAsync" />.
/// </summary>
internal sealed class CallToolHandler
{
    public const string CanonicalToolName = "kongor_call_tool";
    public const int MaxArgumentsJsonBytes = 2 * 1024 * 1024; // 2 MiB inbound argument guard (MCP-273)

    private static readonly AsyncLocal<int> s_recursionDepth = new();
    private static readonly HashSet<string> BridgeNames = new(StringComparer.OrdinalIgnoreCase)
    {
        CanonicalToolName,
        "call_tool",
        "kongor_bridge",
        "kongor_discover_tools"
    };

    private readonly McpToolRegistry _registry;
    private readonly TimeProvider _timeProvider;

    public CallToolHandler(McpToolRegistry registry, TimeProvider? timeProvider = null)
    {
        _registry = registry ?? throw new ArgumentNullException(nameof(registry));
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public static bool IsBridgeName(string? name) =>
        !string.IsNullOrWhiteSpace(name) && BridgeNames.Contains(name.Trim());

    /// <summary>
    /// Determines whether an incoming invocation of <c>kongor_call_tool</c> is an active
    /// bridged <c>call</c> that will delegate to <see cref="McpToolRegistry.ExecuteToolAsync" />
    /// (and therefore should not be double-billed at the outer bridge layer).
    /// </summary>
    public static bool TryExtractBridgedInvocation(
        JsonElement arguments,
        out string? targetToolName,
        out JsonElement innerArguments,
        out string effectiveAction,
        out string? parseError)
    {
        targetToolName = null;
        innerArguments = default;
        effectiveAction = "call";
        parseError = null;

        if (arguments.ValueKind != JsonValueKind.Object)
        {
            effectiveAction = "list";
            return false;
        }

        string? rawToolName = TryReadString(arguments, "tool_name", "toolName", "name", "tool");
        string? rawAction = TryReadString(arguments, "action", "mode");
        string? rawQuery = TryReadString(arguments, "query", "search", "category", "filter");

        if (!string.IsNullOrWhiteSpace(rawAction))
        {
            effectiveAction = rawAction.Trim().ToLowerInvariant() switch
            {
                "list" or "ls" or "catalog" => "list",
                "search" or "find" => "search",
                "describe" or "schema" or "inspect" or "help" => "describe",
                "call" or "invoke" or "execute" or "run" => "call",
                _ => rawAction.Trim().ToLowerInvariant()
            };
        }
        else if (string.IsNullOrWhiteSpace(rawToolName))
        {
            effectiveAction = !string.IsNullOrWhiteSpace(rawQuery) ? "search" : "list";
        }

        targetToolName = rawToolName?.Trim();

        if (!string.Equals(effectiveAction, "call", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (string.IsNullOrWhiteSpace(targetToolName))
        {
            parseError =
                "Missing 'tool_name' for kongor_call_tool(action='call'). " +
                "Pass {\"action\":\"list\"} or {\"action\":\"search\",\"query\":\"<keyword>\"} to discover tools, " +
                "{\"action\":\"describe\",\"tool_name\":\"<name>\"} to inspect exact parameters, or " +
                "{\"action\":\"call\",\"tool_name\":\"<name>\",\"arguments_json\":\"{...}\"} to execute.";
            return false;
        }

        if (!TryParseInnerArguments(arguments, out innerArguments, out parseError))
        {
            return false;
        }

        return true;
    }

    private static bool TryParseInnerArguments(JsonElement outerArgs, out JsonElement innerArgs, out string? error)
    {
        error = null;

        // 1. Preferred universal scalar property: arguments_json (string)
        if (outerArgs.TryGetProperty("arguments_json", out JsonElement argsJsonEl) ||
            outerArgs.TryGetProperty("argumentsJson", out argsJsonEl) ||
            outerArgs.TryGetProperty("args_json", out argsJsonEl))
        {
            if (argsJsonEl.ValueKind == JsonValueKind.Null)
            {
                using JsonDocument emptyDoc = JsonDocument.Parse("{}");
                innerArgs = emptyDoc.RootElement.Clone();
                return true;
            }

            if (argsJsonEl.ValueKind == JsonValueKind.Object)
            {
                innerArgs = argsJsonEl.Clone();
                return true;
            }

            if (argsJsonEl.ValueKind == JsonValueKind.String)
            {
                string raw = argsJsonEl.GetString() ?? "{}";
                if (System.Text.Encoding.UTF8.GetByteCount(raw) > MaxArgumentsJsonBytes)
                {
                    innerArgs = default;
                    error =
                        $"MCP-273 Frame Guard: 'arguments_json' exceeds {MaxArgumentsJsonBytes / (1024 * 1024)} MiB limit. " +
                        "Pass file paths on disk instead of inline base64 payloads.";
                    return false;
                }

                if (string.IsNullOrWhiteSpace(raw))
                {
                    raw = "{}";
                }

                try
                {
                    using JsonDocument parsed = JsonDocument.Parse(raw);
                    if (parsed.RootElement.ValueKind != JsonValueKind.Object)
                    {
                        innerArgs = default;
                        error = "Invalid 'arguments_json': root JSON value must be a JSON object (e.g. \"{\\\"action\\\":\\\"status\\\"}\").";
                        return false;
                    }

                    innerArgs = parsed.RootElement.Clone();
                    return true;
                }
                catch (JsonException jex)
                {
                    innerArgs = default;
                    error = $"Malformed JSON in 'arguments_json': {jex.Message}. Provide a valid JSON object string such as \"{{}}\".";
                    return false;
                }
            }
        }

        // 2. Postel's Law fallback: raw 'arguments' or 'args' passed as JSON object or JSON string
        if (outerArgs.TryGetProperty("arguments", out JsonElement rawArgsEl) ||
            outerArgs.TryGetProperty("args", out rawArgsEl))
        {
            if (rawArgsEl.ValueKind == JsonValueKind.Object)
            {
                string rawText = rawArgsEl.GetRawText();
                if (System.Text.Encoding.UTF8.GetByteCount(rawText) > MaxArgumentsJsonBytes)
                {
                    innerArgs = default;
                    error =
                        $"MCP-273 Frame Guard: 'arguments' payload exceeds {MaxArgumentsJsonBytes / (1024 * 1024)} MiB limit. " +
                        "Pass file paths on disk instead of inline base64 payloads.";
                    return false;
                }

                innerArgs = rawArgsEl.Clone();
                return true;
            }

            if (rawArgsEl.ValueKind == JsonValueKind.String)
            {
                string str = rawArgsEl.GetString() ?? "{}";
                if (string.IsNullOrWhiteSpace(str)) str = "{}";
                try
                {
                    using JsonDocument parsed = JsonDocument.Parse(str);
                    if (parsed.RootElement.ValueKind != JsonValueKind.Object)
                    {
                        innerArgs = default;
                        error = "Invalid 'arguments': root JSON value must be a JSON object.";
                        return false;
                    }

                    innerArgs = parsed.RootElement.Clone();
                    return true;
                }
                catch (JsonException jex)
                {
                    innerArgs = default;
                    error = $"Malformed JSON in 'arguments': {jex.Message}";
                    return false;
                }
            }
        }

        using JsonDocument defaultDoc = JsonDocument.Parse("{}");
        innerArgs = defaultDoc.RootElement.Clone();
        return true;
    }

    public async Task<ToolCallResult> HandleAsync(JsonElement arguments, CancellationToken cancellationToken = default)
    {
        Stopwatch sw = Stopwatch.StartNew();
        bool isBridgedCall = TryExtractBridgedInvocation(
            arguments,
            out string? targetToolName,
            out JsonElement innerArgs,
            out string effectiveAction,
            out string? parseError);

        if (parseError != null)
        {
            return ToolCallResult.Error(parseError);
        }

        if (effectiveAction is "list" or "search")
        {
            string? query = arguments.ValueKind == JsonValueKind.Object
                ? TryReadString(arguments, "query", "search", "category", "filter", "domain")
                : null;
            return BuildCatalogDirectoryResult(query, sw);
        }

        if (effectiveAction == "describe")
        {
            return BuildToolDescribeResult(targetToolName, sw);
        }

        if (!isBridgedCall || string.IsNullOrWhiteSpace(targetToolName))
        {
            return ToolCallResult.Error(
                $"Unsupported action '{effectiveAction}' for {CanonicalToolName}. " +
                "Supported actions: 'call', 'list', 'search', 'describe'.");
        }

        // Guard 1: Block direct or aliased self-recursion and nested depth > 1
        if (IsBridgeName(targetToolName) || s_recursionDepth.Value >= 1)
        {
            return ToolCallResult.Error(
                $"Recursion blocked: '{CanonicalToolName}' cannot invoke '{targetToolName}' recursively. " +
                "Specify the target canonical tool name directly in 'tool_name'.");
        }

        // Guard 2: Resolve canonical name and enforce BundledMode / Unsafe boundaries explicitly
        ToolDefinition? resolvedDef = _registry.GetTool(targetToolName);
        if (resolvedDef == null)
        {
            return ToolCallResult.Error(
                $"Unknown tool '{targetToolName}'. Run kongor_call_tool with {{\"action\":\"search\",\"query\":\"<keyword>\"}} to find the exact tool name.");
        }

        if (_registry.IsBundledMode)
        {
            McpToolCategory cat = _registry.GetToolCategory(resolvedDef.Name);
            bool allowedInBundle = (cat == McpToolCategory.ReadOnly || McpToolRegistry.BundledSafeTools.Contains(resolvedDef.Name))
                && !McpToolRegistry.BundledUnsafeTools.Contains(resolvedDef.Name);
            if (!allowedInBundle)
            {
                return ToolCallResult.Error(
                    $"Tool '{resolvedDef.Name}' is disabled in standalone bundled mode for safety.");
            }
        }

        s_recursionDepth.Value++;
        try
        {
            return await _registry.ExecuteToolAsync(targetToolName, innerArgs, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            s_recursionDepth.Value--;
        }
    }

    private ToolCallResult BuildCatalogDirectoryResult(string? query, Stopwatch sw)
    {
        IReadOnlyList<ToolDefinition> allTools = _registry.ListTools(toolsets: "all");
        IReadOnlyDictionary<string, string> aliasMap = _registry.ListAliases();
        ILookup<string, string> aliasesByCanonical = aliasMap.ToLookup(
            kv => kv.Value,
            kv => kv.Key,
            StringComparer.OrdinalIgnoreCase);

        string trimmedQuery = query?.Trim() ?? string.Empty;

        var matched = allTools
            .Where(t => !IsBridgeName(t.Name))
            .Select(t =>
            {
                string category = _registry.GetToolCategory(t.Name).ToString();
                string cleanDesc = AntigravityRegistrar.StripMcpPitfallProse(t.Description);
                string summary = FirstSentence(cleanDesc, 160);
                string[] aliases = aliasesByCanonical[t.Name].OrderBy(a => a, StringComparer.Ordinal).ToArray();
                string[] requiredParams = (t.InputSchema.Required ?? []).ToArray();
                string[] optionalParams = t.InputSchema.Properties.Keys
                    .Where(k => !requiredParams.Contains(k, StringComparer.Ordinal))
                    .Take(8)
                    .ToArray();

                return new
                {
                    name = t.Name,
                    isCore = McpToolRegistry.CoreToolsetAllowlist.Contains(t.Name),
                    category,
                    aliases,
                    summary,
                    requiredParams,
                    optionalParams
                };
            })
            .Where(item =>
                string.IsNullOrEmpty(trimmedQuery) ||
                item.name.Contains(trimmedQuery, StringComparison.OrdinalIgnoreCase) ||
                item.category.Contains(trimmedQuery, StringComparison.OrdinalIgnoreCase) ||
                item.summary.Contains(trimmedQuery, StringComparison.OrdinalIgnoreCase) ||
                item.aliases.Any(a => a.Contains(trimmedQuery, StringComparison.OrdinalIgnoreCase)))
            .OrderBy(item => item.name, StringComparer.Ordinal)
            .ToArray();

        sw.Stop();
        var payload = new
        {
            query = string.IsNullOrEmpty(trimmedQuery) ? null : trimmedQuery,
            totalMatched = matched.Length,
            totalRegisteredTools = allTools.Count,
            usageHint = "Inspect exact parameters with {\"action\":\"describe\",\"tool_name\":\"<name>\"} or invoke directly with {\"action\":\"call\",\"tool_name\":\"<name>\",\"arguments_json\":\"{...}\"}.",
            tools = matched
        };

        return ExecutionTruthEnvelope.CreateJsonResult(
            status: "success",
            executionMode: ExecutionMode.OfflineDirect,
            targetStatus: null,
            metrics: new ExecutionMetrics
            {
                RoundtripLatencyMs = sw.Elapsed.TotalMilliseconds,
                Timestamp = _timeProvider.GetUtcNow().ToString("o")
            },
            data: payload,
            isError: false,
            fidelity: ExecutionFidelity.StaticOfflineAnalysis);
    }

    private ToolCallResult BuildToolDescribeResult(string? toolName, Stopwatch sw)
    {
        if (string.IsNullOrWhiteSpace(toolName))
        {
            return ToolCallResult.Error(
                "Missing 'tool_name' for action='describe'. Pass {\"action\":\"describe\",\"tool_name\":\"<name>\"}.");
        }

        ToolDefinition? def = _registry.GetTool(toolName.Trim());
        if (def == null)
        {
            return ToolCallResult.Error(
                $"Unknown tool '{toolName}'. Run {{\"action\":\"search\",\"query\":\"<keyword>\"}} to find valid tool names.");
        }

        IReadOnlyDictionary<string, string> aliases = _registry.ListAliases();
        string[] toolAliases = aliases
            .Where(kv => string.Equals(kv.Value, def.Name, StringComparison.OrdinalIgnoreCase))
            .Select(kv => kv.Key)
            .OrderBy(a => a, StringComparer.Ordinal)
            .ToArray();

        Dictionary<string, object> exampleArgs = BuildRunnableExampleArguments(def, explicitAction: null);
        Dictionary<string, string>? examplesByAction = null;
        if (def.InputSchema.Properties.TryGetValue("action", out ToolPropertySchema? actSchema) &&
            actSchema.Enum is { Count: > 0 })
        {
            examplesByAction = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (string actVal in actSchema.Enum.Take(10))
            {
                Dictionary<string, object> perActArgs = BuildRunnableExampleArguments(def, actVal);
                examplesByAction[actVal] = JsonSerializer.Serialize(perActArgs);
            }
        }

        sw.Stop();
        var payload = new
        {
            name = def.Name,
            category = _registry.GetToolCategory(def.Name).ToString(),
            aliases = toolAliases,
            annotations = def.Annotations,
            description = def.Description,
            inputSchema = def.InputSchema,
            exampleBridgeCall = new
            {
                action = "call",
                tool_name = def.Name,
                arguments_json = JsonSerializer.Serialize(exampleArgs)
            },
            examplesByAction
        };

        return ExecutionTruthEnvelope.CreateJsonResult(
            status: "success",
            executionMode: ExecutionMode.OfflineDirect,
            targetStatus: null,
            metrics: new ExecutionMetrics
            {
                RoundtripLatencyMs = sw.Elapsed.TotalMilliseconds,
                Timestamp = _timeProvider.GetUtcNow().ToString("o")
            },
            data: payload,
            isError: false,
            fidelity: ExecutionFidelity.StaticOfflineAnalysis);
    }

    private static Dictionary<string, object> BuildRunnableExampleArguments(ToolDefinition def, string? explicitAction)
    {
        Dictionary<string, object> exampleArgs = new(StringComparer.Ordinal);
        if (!string.IsNullOrWhiteSpace(explicitAction))
        {
            exampleArgs["action"] = explicitAction;
        }

        foreach (string req in def.InputSchema.Required)
        {
            if (exampleArgs.ContainsKey(req)) continue;
            if (def.InputSchema.Properties.TryGetValue(req, out ToolPropertySchema? p))
            {
                exampleArgs[req] = BuildConcreteExampleValue(req, p);
            }
        }

        if (exampleArgs.Count == 0)
        {
            if (def.InputSchema.Properties.TryGetValue("action", out ToolPropertySchema? actProp))
            {
                exampleArgs["action"] = BuildConcreteExampleValue("action", actProp);
            }
            else
            {
                foreach (string primaryKey in new[] { "path", "virtual_path", "virtualPath", "query", "code", "command", "hero", "target", "detail" })
                {
                    if (def.InputSchema.Properties.TryGetValue(primaryKey, out ToolPropertySchema? prop))
                    {
                        exampleArgs[primaryKey] = BuildConcreteExampleValue(primaryKey, prop);
                        break;
                    }
                }
            }
        }

        return exampleArgs;
    }

    private static object BuildConcreteExampleValue(string propName, ToolPropertySchema p)
    {
        if (p.Default != null)
        {
            return p.Default;
        }

        if (p.Enum is { Count: > 0 })
        {
            return p.Enum[0];
        }

        if (p.Examples is { Count: > 0 } && !string.IsNullOrWhiteSpace(p.Examples[0]))
        {
            return p.Examples[0];
        }

        string lower = propName.ToLowerInvariant();
        if (lower is "path" or "file_path" or "filepath" or "targetpath" or "target_path" or "interface_path")
        {
            return "games/client/override/ui/fe2/main.interface";
        }
        if (lower is "virtualpath" or "virtual_path")
        {
            return "ui/fe2/main.interface";
        }
        if (lower is "query" or "search" or "filter" or "pattern")
        {
            return "store";
        }
        if (lower is "code" or "script" or "expression")
        {
            return "return UIGamePhase and UIGamePhase() or 0";
        }
        if (lower is "command" or "cmd")
        {
            return "Echo OK";
        }
        if (lower is "fixture" or "baseline" or "baselinepath" or "baseline_path" or "expected_path")
        {
            return "hud_baseline.png";
        }
        if (lower is "actual_path" or "candidate_path" or "candidatepath")
        {
            return "hud_candidate.png";
        }
        if (lower is "target" or "process")
        {
            return "hon_x64";
        }
        if (lower is "hero" or "hero_name")
        {
            return "Hero_Pyromancer";
        }
        if (lower is "widget" or "widget_name")
        {
            return "main_top_bar";
        }
        if (lower is "interface" or "interface_name")
        {
            return "main";
        }
        if (lower is "sql")
        {
            return "SELECT COUNT(*) AS total FROM match_combat_events";
        }
        if (lower.Contains("port", StringComparison.Ordinal))
        {
            return 11235;
        }

        return (p.Type?.ToLowerInvariant()) switch
        {
            "integer" => 1,
            "number" => 1.0,
            "boolean" => false,
            "array" => Array.Empty<string>(),
            "object" => new Dictionary<string, object>(),
            _ => "status"
        };
    }

    private static string? TryReadString(JsonElement obj, params string[] keys)
    {
        foreach (string key in keys)
        {
            if (obj.TryGetProperty(key, out JsonElement el) && el.ValueKind == JsonValueKind.String)
            {
                string? val = el.GetString();
                if (!string.IsNullOrWhiteSpace(val))
                {
                    return val;
                }
            }
        }
        return null;
    }

    private static string FirstSentence(string text, int maxLen)
    {
        if (string.IsNullOrWhiteSpace(text)) return string.Empty;
        int dot = text.IndexOf(". ", StringComparison.Ordinal);
        string slice = dot > 0 ? text[..(dot + 1)] : text;
        return slice.Length <= maxLen ? slice : slice[..(maxLen - 3)] + "...";
    }
}
