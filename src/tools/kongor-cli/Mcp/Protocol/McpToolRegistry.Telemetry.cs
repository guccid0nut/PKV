// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// TAO (Together As One) MCP & Kongor CLI -- Created by guccidonut (https://github.com/guccidonut/PKV)

using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Kongor.Cli.Mcp.Protocol;

public sealed partial class McpToolRegistry
{
    private static void EnrichToolDefinitionWithPitfallRule(ToolDefinition definition)
    {
        if (string.IsNullOrWhiteSpace(definition.Description) ||
            definition.Description.Contains("[MCP-", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        McpIssueKbEntry? topRule = FindTopPitfallRuleForTool(definition.Name);
        if (topRule is null || string.IsNullOrWhiteSpace(topRule.PreventionRule))
        {
            return;
        }

        string conciseRule = topRule.PreventionRule.Replace('\r', ' ').Replace('\n', ' ').Trim();
        if (conciseRule.Length > 120)
        {
            conciseRule = conciseRule[..117] + "...";
        }

        definition.Description = $"{definition.Description.TrimEnd()} [{topRule.Id}: {conciseRule}]";
    }

    internal static McpIssueKbEntry? FindTopPitfallRuleForTool(string toolName, string? errorText = null)
    {
        IReadOnlyList<McpIssueKbEntry> entries = s_mcpIssueKbEntries.Value;
        if (entries.Count == 0)
        {
            return null;
        }

        List<McpIssueKbEntry> toolMatches = new();
        foreach (McpIssueKbEntry entry in entries)
        {
            if (string.Equals(entry.Tool, toolName, StringComparison.OrdinalIgnoreCase) ||
                entry.Tool.Contains(toolName, StringComparison.OrdinalIgnoreCase))
            {
                toolMatches.Add(entry);
            }
        }

        if (toolMatches.Count == 0)
        {
            return null;
        }

        if (!string.IsNullOrWhiteSpace(errorText))
        {
            McpIssueKbEntry? bestErrorMatch = toolMatches
                .OrderByDescending(e => ScoreEntryAgainstError(e, errorText))
                .ThenByDescending(e => IsOpenStatus(e.Status) ? 1 : 0)
                .ThenByDescending(e => e.Occurrences)
                .FirstOrDefault();

            if (bestErrorMatch != null)
            {
                return bestErrorMatch;
            }
        }

        return toolMatches
            .OrderByDescending(e => IsOpenStatus(e.Status) ? 1 : 0)
            .ThenByDescending(e => e.Occurrences)
            .ThenByDescending(e => e.Id, StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault();
    }

    private static bool IsOpenStatus(string status) =>
        string.Equals(status, "open", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(status, "investigating", StringComparison.OrdinalIgnoreCase);

    private static readonly HashSet<string> s_errorEnvelopeStopwords = new(StringComparer.OrdinalIgnoreCase)
    {
        "kongor", "status", "error", "errors", "response", "result", "failed", "failure",
        "formerly", "when", "with", "from", "true", "false", "null", "code", "action",
        "json", "text", "data", "type", "name", "global", "local", "table", "value", "string", "index", "attempt", "scope"
    };

    private static HashSet<string> BuildExcludedTokensForTool(string toolName)
    {
        HashSet<string> excluded = new(s_errorEnvelopeStopwords, StringComparer.OrdinalIgnoreCase);
        if (!string.IsNullOrWhiteSpace(toolName))
        {
            foreach (string part in toolName.Split(['_', '-', ' ', '/'], StringSplitOptions.RemoveEmptyEntries))
            {
                excluded.Add(part);
            }
        }
        return excluded;
    }

    internal static McpIssueKbEntry? MatchIssueForError(string toolName, string diagnosticText, string errorClass)
    {
        IReadOnlyList<McpIssueKbEntry> entries = s_mcpIssueKbEntries.Value;
        if (entries.Count == 0 || string.IsNullOrWhiteSpace(diagnosticText))
        {
            return null;
        }

        List<McpIssueKbEntry> toolMatches = new();
        foreach (McpIssueKbEntry entry in entries)
        {
            if (string.Equals(entry.Tool, toolName, StringComparison.OrdinalIgnoreCase) ||
                entry.Tool.Contains(toolName, StringComparison.OrdinalIgnoreCase))
            {
                toolMatches.Add(entry);
            }
        }

        if (toolMatches.Count == 0)
        {
            return null;
        }

        HashSet<string> excludedTokens = BuildExcludedTokensForTool(toolName);
        McpIssueKbEntry? bestEntry = null;
        int bestScore = 0;

        foreach (McpIssueKbEntry entry in toolMatches)
        {
            int score = ScoreEntryAgainstError(entry, diagnosticText, errorClass, excludedTokens);
            if (score < 2)
            {
                continue;
            }

            if (bestEntry == null ||
                score > bestScore ||
                (score == bestScore && entry.Occurrences > bestEntry.Occurrences) ||
                (score == bestScore && entry.Occurrences == bestEntry.Occurrences && IsOpenStatus(entry.Status) && !IsOpenStatus(bestEntry.Status)))
            {
                bestEntry = entry;
                bestScore = score;
            }
        }

        return bestScore >= 2 ? bestEntry : null;
    }

    internal static string ClassifyErrorClass(
        string targetName,
        bool isError,
        string diagnosticText,
        string? errorCode,
        bool preflightFailed)
    {
        if (!isError)
        {
            return "none";
        }

        if (string.Equals(errorCode, "rate_limit_exceeded", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(errorCode, "rateLimited", StringComparison.OrdinalIgnoreCase) ||
            diagnosticText.Contains("Rate limit exceeded", StringComparison.OrdinalIgnoreCase))
        {
            return "rate_limited";
        }

        if (diagnosticText.StartsWith("Unknown tool '", StringComparison.OrdinalIgnoreCase) ||
            diagnosticText.Contains("Unknown tool '", StringComparison.OrdinalIgnoreCase))
        {
            return "unknown_tool";
        }

        if (preflightFailed ||
            diagnosticText.Contains("Preflight check failed", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(errorCode, "clientOffline", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(errorCode, "missing_connection_string", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(errorCode, "invalid_connection_string", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(errorCode, "database_unreachable", StringComparison.OrdinalIgnoreCase))
        {
            return "precondition";
        }

        if (string.Equals(errorCode, "luaEvalError", StringComparison.OrdinalIgnoreCase))
        {
            return "caller_lua_error";
        }

        bool isLuaEvalTool =
            string.Equals(targetName, "kongor_client_eval_lua", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(targetName, "kongor_game_lua", StringComparison.OrdinalIgnoreCase);

        if (isLuaEvalTool &&
            diagnosticText.Contains("[ERROR]", StringComparison.OrdinalIgnoreCase) &&
            !diagnosticText.Contains("Timed out", StringComparison.OrdinalIgnoreCase) &&
            !diagnosticText.Contains("timeout", StringComparison.OrdinalIgnoreCase) &&
            !diagnosticText.Contains("offline", StringComparison.OrdinalIgnoreCase) &&
            !diagnosticText.Contains("disconnected", StringComparison.OrdinalIgnoreCase))
        {
            return "caller_lua_error";
        }

        if (string.Equals(errorCode, "mainThreadDrainTimeout", StringComparison.OrdinalIgnoreCase) ||
            diagnosticText.Contains("Timed out waiting for K2 main thread", StringComparison.OrdinalIgnoreCase) ||
            diagnosticText.Contains("mainThreadBusy", StringComparison.OrdinalIgnoreCase))
        {
            return "engine_busy";
        }

        if (string.Equals(errorCode, "ipcPipeTimeout", StringComparison.OrdinalIgnoreCase) ||
            diagnosticText.Contains("TimeoutException", StringComparison.OrdinalIgnoreCase) ||
            diagnosticText.Contains("timed out", StringComparison.OrdinalIgnoreCase))
        {
            return "transport_timeout";
        }

        return "tool_fault";
    }

    private static int ScoreEntryAgainstError(McpIssueKbEntry entry, string errorText) =>
        ScoreEntryAgainstError(entry, errorText, "tool_fault", BuildExcludedTokensForTool(entry.Tool));

    private static int ScoreEntryAgainstError(
        McpIssueKbEntry entry,
        string diagnosticText,
        string errorClass,
        HashSet<string> excludedTokens)
    {
        if (string.Equals(errorClass, "unknown_tool", StringComparison.OrdinalIgnoreCase))
        {
            // Never attribute a caller's unknown tool name typo to an MCP issue entry (MCP-294).
            return 0;
        }

        if (string.Equals(errorClass, "rate_limited", StringComparison.OrdinalIgnoreCase))
        {
            return string.Equals(entry.Category, "rate-limit", StringComparison.OrdinalIgnoreCase) ? 6 : 0;
        }

        bool isCallerLuaError = string.Equals(errorClass, "caller_lua_error", StringComparison.OrdinalIgnoreCase);
        if (isCallerLuaError &&
            !string.Equals(entry.Category, "lua-eval", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(entry.Category, "format-string-escape", StringComparison.OrdinalIgnoreCase))
        {
            // Never attribute a caller Lua syntax/runtime bug to transport/pipe/offline KB issues (e.g. MCP-136).
            return 0;
        }

        int score = 0;
        if (!string.IsNullOrWhiteSpace(entry.Category) &&
            diagnosticText.Contains(entry.Category, StringComparison.OrdinalIgnoreCase))
        {
            score += 4;
        }
        else if (string.Equals(errorClass, "precondition", StringComparison.OrdinalIgnoreCase) &&
                 (string.Equals(entry.Category, "client-offline", StringComparison.OrdinalIgnoreCase) ||
                  string.Equals(entry.Category, "ipc-timeout", StringComparison.OrdinalIgnoreCase) ||
                  string.Equals(entry.Category, "window-resolution", StringComparison.OrdinalIgnoreCase)))
        {
            score += 3;
        }
        else if ((string.Equals(errorClass, "transport_timeout", StringComparison.OrdinalIgnoreCase) ||
                  string.Equals(errorClass, "engine_busy", StringComparison.OrdinalIgnoreCase)) &&
                 string.Equals(entry.Category, "ipc-timeout", StringComparison.OrdinalIgnoreCase))
        {
            score += 3;
        }

        char[] separators = [' ', '(', ')', '-', ':', '/', '\'', '"', ',', '`', '.', ';', '[', ']'];
        string[] titleWords = entry.Title.Split(separators, StringSplitOptions.RemoveEmptyEntries);
        foreach (string word in titleWords)
        {
            if (word.Length >= 4 &&
                !excludedTokens.Contains(word) &&
                diagnosticText.Contains(word, StringComparison.OrdinalIgnoreCase))
            {
                score += 2;
            }
        }

        if (!isCallerLuaError && !string.IsNullOrWhiteSpace(entry.PreventionRule))
        {
            int ruleBonus = 0;
            string[] ruleWords = entry.PreventionRule.Split(separators, StringSplitOptions.RemoveEmptyEntries);
            foreach (string word in ruleWords)
            {
                if (word.Length >= 5 &&
                    !excludedTokens.Contains(word) &&
                    diagnosticText.Contains(word, StringComparison.OrdinalIgnoreCase))
                {
                    ruleBonus++;
                    if (ruleBonus >= 4)
                    {
                        break;
                    }
                }
            }
            score += ruleBonus;
        }

        return score;
    }

    internal static string? EnrichErrorWithMcpIssue(string targetName, ToolCallResult errorResult, out string errorClass)
    {
        errorClass = "tool_fault";
        if (!errorResult.IsError || errorResult.Content.Count == 0 || errorResult.Content[0] is not TextContentItem textItem)
        {
            return null;
        }

        string currentText = textItem.Text ?? "";
        string diagnosticText = currentText;
        string? errorCode = null;
        bool preflightFailed = false;
        string trimmed = currentText.TrimStart();

        if (trimmed.StartsWith('{'))
        {
            try
            {
                using JsonDocument inspectDoc = JsonDocument.Parse(trimmed);
                JsonElement root = inspectDoc.RootElement;
                if (root.TryGetProperty("errorCode", out JsonElement ecEl) && ecEl.ValueKind == JsonValueKind.String)
                {
                    errorCode = ecEl.GetString();
                }
                if (root.TryGetProperty("preflightFailed", out JsonElement pfEl) && pfEl.ValueKind == JsonValueKind.True)
                {
                    preflightFailed = true;
                }

                List<string> parts = new();
                foreach (string propName in new[] { "error", "errorMessage", "message", "response", "reason", "remediation", "destination" })
                {
                    if (root.TryGetProperty(propName, out JsonElement pEl) && pEl.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(pEl.GetString()))
                    {
                        parts.Add(pEl.GetString()!);
                    }
                }
                if (!string.IsNullOrWhiteSpace(errorCode))
                {
                    parts.Add(errorCode!);
                }
                if (parts.Count > 0)
                {
                    diagnosticText = string.Join(" | ", parts);
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[McpToolRegistry] Error inspection parse fallback: {ex.Message}");
            }
        }

        errorClass = ClassifyErrorClass(targetName, errorResult.IsError, diagnosticText, errorCode, preflightFailed);
        McpIssueKbEntry? entry = MatchIssueForError(targetName, diagnosticText, errorClass);
        string? issueId = entry?.Id;
        string preventionRule = !string.IsNullOrWhiteSpace(entry?.PreventionRule)
            ? entry!.PreventionRule
            : (string.Equals(errorClass, "caller_lua_error", StringComparison.OrdinalIgnoreCase)
                ? "Inspect the Lua error message returned by K2; transport succeeded on named_pipe, so no MCP restart is needed."
                : $"Verify '{targetName}' preconditions and required parameters via kongor_health_check.");
        bool isClientOfflineError = preflightFailed || string.Equals(errorClass, "client_offline", StringComparison.OrdinalIgnoreCase);
        bool isRateLimitedError = string.Equals(errorClass, "rate_limited", StringComparison.OrdinalIgnoreCase);
        bool isUnknownToolError = string.Equals(errorClass, "unknown_tool", StringComparison.OrdinalIgnoreCase);
        bool isCallerLuaErr = string.Equals(errorClass, "caller_lua_error", StringComparison.OrdinalIgnoreCase);
        object remediationCall = isClientOfflineError
            ? new
            {
                tool = "kongor_game_client_manage",
                arguments = new { action = "start", window_mode = "offscreen", masterServer = "127.0.0.1:55555" }
            }
            : isRateLimitedError
                ? new
                {
                    tool = "kongor_rate_limit_status",
                    arguments = new { }
                }
                : isUnknownToolError
                    ? new
                    {
                        tool = "kongor_call_tool",
                        arguments = new { action = "search", query = targetName }
                    }
                    : isCallerLuaErr
                        ? new
                        {
                            tool = "kongor_errors_since",
                            arguments = new { severity = "error" }
                        }
                        : new
                        {
                            tool = "kongor_health_check",
                            arguments = new { detail = "concise" }
                        };
        string remediationCommand = isClientOfflineError
            ? "kongor_game_client_manage(action='start', window_mode='offscreen', masterServer='127.0.0.1:55555')"
            : isRateLimitedError
                ? "kongor_rate_limit_status()"
                : isUnknownToolError
                    ? $"kongor_call_tool(action='search', query='{targetName}')"
                    : isCallerLuaErr
                        ? "Inspect the Lua syntax or runtime error in the response; no health check is needed."
                        : "kongor_health_check(detail='concise')";

        if (trimmed.StartsWith('{'))
        {
            try
            {
                if (JsonNode.Parse(currentText) is JsonObject obj)
                {
                    if (!obj.ContainsKey("mcpIssueId"))
                    {
                        obj["mcpIssueId"] = issueId;
                    }
                    if (!obj.ContainsKey("errorClass"))
                    {
                        obj["errorClass"] = errorClass;
                    }
                    if (!obj.ContainsKey("preventionRule"))
                    {
                        obj["preventionRule"] = preventionRule;
                    }
                    if (!obj.ContainsKey("remediationCommand"))
                    {
                        obj["remediationCommand"] = remediationCommand;
                    }
                    if (!obj.ContainsKey("remediation_call"))
                    {
                        obj["remediation_call"] = JsonSerializer.SerializeToNode(remediationCall);
                    }
                    string jsonText = obj.ToJsonString(new JsonSerializerOptions
                    {
                        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
                    });
                    // Defend against host Go runtime format-string mangling (%s -> %!s(MISSING)) in fmt.Errorf
                    jsonText = jsonText.Replace("%", "\\u0025");
                    errorResult.Content[0] = new TextContentItem
                    {
                        Text = jsonText
                    };
                    return issueId;
                }
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[McpToolRegistry] JSON error envelope enrichment fallback: {ex.Message}");
            }
        }

        if (!currentText.Contains("mcpIssueId", StringComparison.OrdinalIgnoreCase) &&
            !currentText.Contains("[MCP-", StringComparison.OrdinalIgnoreCase))
        {
            string issueTag = issueId != null ? $"[mcpIssueId: {issueId}] " : "";
            string rawCombined = $"{currentText}\n{issueTag}[errorClass: {errorClass}] Prevention: {preventionRule} | Remediation: {remediationCommand}";
            errorResult.Content[0] = new TextContentItem
            {
                Text = rawCombined.Replace("%", "%%")
            };
        }

        return issueId;
    }

    private static readonly ConcurrentDictionary<string, int> s_falseGreenCounts = new(StringComparer.OrdinalIgnoreCase);
    private static readonly object s_falseGreenLogLock = new();

    public static void RecordFalseGreen(string toolName, string reason, string? explicitLogPath = null)
    {
        if (string.IsNullOrWhiteSpace(toolName)) return;
        s_falseGreenCounts.AddOrUpdate(toolName, 1, (_, count) => count + 1);

        string? logPath = explicitLogPath
            ?? Environment.GetEnvironmentVariable("KONGOR_MCP_OUTCOME_LOG_PATH")
            ?? Environment.GetEnvironmentVariable("KONGOR_MCP_OUTCOME_LOG");
        if (string.IsNullOrWhiteSpace(logPath)) return;

        try
        {
            var record = new
            {
                timestampUtc = TimeProvider.System.GetUtcNow().ToString("o"),
                sessionId = "false_green_detector",
                tool = toolName,
                requestedName = toolName,
                firstCall = false,
                isFirstCallInSession = false,
                succeeded = false,
                isError = true,
                verdict = "false_green",
                falseGreen = true,
                falseGreenReason = reason,
                elapsedMs = 0.0
            };
            string line = JsonSerializer.Serialize(record) + Environment.NewLine;
            lock (s_falseGreenLogLock)
            {
                string? dir = Path.GetDirectoryName(logPath);
                if (!string.IsNullOrWhiteSpace(dir) && !Directory.Exists(dir))
                {
                    Directory.CreateDirectory(dir);
                }
                File.AppendAllText(logPath, line);
            }
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[McpToolRegistry] False-green JSONL write failed: {ex.Message}");
        }
    }

    public static int GetFalseGreenCount(string toolName)
        => s_falseGreenCounts.TryGetValue(toolName, out int count) ? count : 0;

    public static void ResetFalseGreenCounts() => s_falseGreenCounts.Clear();

    private void RecordToolOutcome(
        string canonicalTool,
        string requestedName,
        ToolCallResult result,
        double elapsedMs,
        string? mcpIssueId,
        string errorClass,
        JsonElement arguments = default)
    {
        bool isCallerError = string.Equals(errorClass, "caller_lua_error", StringComparison.OrdinalIgnoreCase);
        ToolCallOutcomeStats stats = _outcomeStats.GetOrAdd(canonicalTool, _ => new ToolCallOutcomeStats());
        Interlocked.Increment(ref stats.TotalCalls);
        if (result.IsError)
        {
            Interlocked.Increment(ref stats.TotalErrors);
        }

        bool isFirstCall = false;
        if (!isCallerError && Interlocked.CompareExchange(ref stats.FirstCallRecorded, 1, 0) == 0)
        {
            stats.FirstCallSucceeded = !result.IsError;
            isFirstCall = true;
        }

        string? logPath = OutcomeLogFilePath;
        if (!string.IsNullOrWhiteSpace(logPath))
        {
            try
            {
                string verdict = result.IsError ? "fail" : "pass";
                string? channel = null;
                string? fallbackReason = null;
                if (result.Content.Count > 0 && result.Content[0] is TextContentItem txt && !string.IsNullOrWhiteSpace(txt.Text))
                {
                    string trimmed = txt.Text.TrimStart();
                    if (trimmed.StartsWith('{'))
                    {
                        try
                        {
                            using JsonDocument resDoc = JsonDocument.Parse(trimmed);
                            if (resDoc.RootElement.TryGetProperty("verdict", out JsonElement vEl) &&
                                vEl.ValueKind == JsonValueKind.String &&
                                !string.IsNullOrWhiteSpace(vEl.GetString()))
                            {
                                verdict = vEl.GetString()!;
                            }
                            if (resDoc.RootElement.TryGetProperty("channel", out JsonElement chEl) &&
                                chEl.ValueKind == JsonValueKind.String &&
                                !string.IsNullOrWhiteSpace(chEl.GetString()))
                            {
                                channel = chEl.GetString();
                            }
                            if (resDoc.RootElement.TryGetProperty("fallbackReason", out JsonElement fbEl) &&
                                fbEl.ValueKind == JsonValueKind.String &&
                                !string.IsNullOrWhiteSpace(fbEl.GetString()))
                            {
                                fallbackReason = fbEl.GetString();
                            }
                        }
                        catch (Exception ex)
                        {
                            Debug.WriteLine($"[McpToolRegistry] Verdict parse skipped: {ex.Message}");
                        }
                    }
                }

                bool escapeHatchUsed = false;
                string? escapeHatchReason = null;
                string? requestedChannel = null;
                if (arguments.ValueKind == JsonValueKind.Object)
                {
                    if ((arguments.TryGetProperty("allowDiagnosticWarnings", out JsonElement adw) && adw.ValueKind == JsonValueKind.True) ||
                        (arguments.TryGetProperty("skipLint", out JsonElement sl) && sl.ValueKind == JsonValueKind.True) ||
                        (arguments.TryGetProperty("skipBuildSync", out JsonElement sbs) && sbs.ValueKind == JsonValueKind.True))
                    {
                        escapeHatchUsed = true;
                    }
                    if (arguments.TryGetProperty("reason", out JsonElement rEl) && rEl.ValueKind == JsonValueKind.String)
                    {
                        escapeHatchReason = rEl.GetString();
                    }
                    if (arguments.TryGetProperty("channel", out JsonElement reqChEl) && reqChEl.ValueKind == JsonValueKind.String)
                    {
                        requestedChannel = reqChEl.GetString();
                    }
                }

                bool unrequestedMailboxFallback =
                    string.Equals(channel, "mailbox", StringComparison.OrdinalIgnoreCase) &&
                    !string.Equals(requestedChannel, "mailbox", StringComparison.OrdinalIgnoreCase);

                string origin = !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("KONGOR_MCP_ORIGIN"))
                    ? Environment.GetEnvironmentVariable("KONGOR_MCP_ORIGIN")!
                    : (Handlers.GameServerManageHandler.IsTestExecution ? "test" : "live");

                var record = new
                {
                    timestampUtc = _timeProvider.GetUtcNow().ToString("o"),
                    origin,
                    sessionId = _sessionId,
                    tool = canonicalTool,
                    targetTool = canonicalTool,
                    requestedName,
                    firstCall = isFirstCall,
                    isFirstCallInSession = isFirstCall,
                    succeeded = !result.IsError,
                    isError = result.IsError,
                    verdict,
                    channel,
                    fallbackReason,
                    unrequestedMailboxFallback,
                    falseGreen = false,
                    escapeHatchUsed,
                    escapeHatchReason,
                    firstCallSucceeded = stats.FirstCallSucceeded,
                    elapsedMs = Math.Round(elapsedMs, 2),
                    errorClass,
                    mcpIssueId
                };
                string line = JsonSerializer.Serialize(record) + Environment.NewLine;
                lock (_outcomeLogLock)
                {
                    string? dir = Path.GetDirectoryName(logPath);
                    if (!string.IsNullOrWhiteSpace(dir) && !Directory.Exists(dir))
                    {
                        Directory.CreateDirectory(dir);
                    }
                    File.AppendAllText(logPath, line);
                }
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[McpToolRegistry] Outcome JSONL write failed: {ex.Message}");
            }
        }
    }
}
