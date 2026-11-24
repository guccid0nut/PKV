// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// TAO (Together As One) MCP & Kongor CLI -- Created by guccidonut (https://github.com/guccidonut/PKV)

using System.Diagnostics;
using System.Text.Json;

namespace Kongor.Cli.Mcp.Protocol;

public sealed partial class McpToolRegistry
{
    private async Task<ToolCallResult?> TryEvaluatePreflightAsync(string targetName, JsonElement arguments, CancellationToken ct)
    {
        bool skipPreflight = false;
        bool enforcePreflight = false;
        bool autoHeal = false;

        if (arguments.ValueKind == JsonValueKind.Object)
        {
            if ((arguments.TryGetProperty("skip_preflight", out JsonElement sp1) && sp1.ValueKind == JsonValueKind.True) ||
                (arguments.TryGetProperty("skipPreflight", out JsonElement sp2) && sp2.ValueKind == JsonValueKind.True) ||
                (arguments.TryGetProperty("dryRun", out JsonElement dr1) && dr1.ValueKind == JsonValueKind.True) ||
                (arguments.TryGetProperty("dry_run", out JsonElement dr2) && dr2.ValueKind == JsonValueKind.True))
            {
                skipPreflight = true;
            }

            if ((arguments.TryGetProperty("enforce_preflight", out JsonElement ep1) && ep1.ValueKind == JsonValueKind.True) ||
                (arguments.TryGetProperty("enforcePreflight", out JsonElement ep2) && ep2.ValueKind == JsonValueKind.True))
            {
                enforcePreflight = true;
            }

            if ((arguments.TryGetProperty("auto_heal", out JsonElement ah1) && ah1.ValueKind == JsonValueKind.True) ||
                (arguments.TryGetProperty("autoHeal", out JsonElement ah2) && ah2.ValueKind == JsonValueKind.True))
            {
                autoHeal = true;
            }
        }

        if (skipPreflight)
        {
            return null;
        }

        bool isClientEvalLua = string.Equals(targetName, "kongor_client_eval_lua", StringComparison.OrdinalIgnoreCase);
        bool isCaptureWindow = string.Equals(targetName, "kongor_capture_window", StringComparison.OrdinalIgnoreCase);
        bool isAssertVisualLive =
            string.Equals(targetName, "kongor_assert_visual", StringComparison.OrdinalIgnoreCase) &&
            !HasAnyNonEmptyProperty(arguments, "candidate", "candidate_path", "candidate_fixture");
        bool isK2UiInspectLive =
            string.Equals(targetName, "kongor_k2_ui_inspect", StringComparison.OrdinalIgnoreCase) &&
            IsExplicitStringPropertyValue(arguments, "mode", "live");
        bool isClientNavigateWithoutAutoBoot =
            string.Equals(targetName, "kongor_client_navigate", StringComparison.OrdinalIgnoreCase) &&
            IsExplicitFalseBooleanProperty(arguments, "autoBoot", "auto_boot");
        bool isLiveClientTool = isClientEvalLua ||
            isCaptureWindow ||
            isAssertVisualLive ||
            isK2UiInspectLive ||
            isClientNavigateWithoutAutoBoot ||
            string.Equals(targetName, "kongor_transient_capture", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(targetName, "kongor_client_capture_preview", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(targetName, "kongor_game_lua", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(targetName, "kongor_game_command", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(targetName, "kongor_ui_control", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(targetName, "kongor_assert_ui", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(targetName, "kongor_ui_flow", StringComparison.OrdinalIgnoreCase);
        // Note: kongor_wait_for is intentionally excluded from isLiveClientTool so boot polling works while hon_x64.exe starts,
        // kongor_k2_ui_inspect only enforces live-client preflight when mode=='live' (MCP-274),
        // and kongor_client_navigate only enforces live-client preflight when autoBoot==false (MCP-274).

        if (!isClientEvalLua && !enforcePreflight && !autoHeal && _hasInjectedMockCollaborators)
        {
            return null;
        }

        if (isLiveClientTool && !enforcePreflight && arguments.ValueKind == JsonValueKind.Object)
        {
            if (string.Equals(targetName, "kongor_game_command", StringComparison.OrdinalIgnoreCase))
            {
                bool hasCmd = (arguments.TryGetProperty("command", out JsonElement cmdEl) && cmdEl.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(cmdEl.GetString())) ||
                              (arguments.TryGetProperty("cmd", out JsonElement cmdAliasEl) && cmdAliasEl.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(cmdAliasEl.GetString())) ||
                              (arguments.TryGetProperty("action", out JsonElement actCmdEl) && actCmdEl.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(actCmdEl.GetString()));
                if (!hasCmd)
                {
                    return null;
                }
            }

            if (arguments.TryGetProperty("port", out JsonElement portEl) &&
                ((portEl.ValueKind == JsonValueKind.Number && portEl.TryGetInt32(out int p) && p > 0) ||
                 (portEl.ValueKind == JsonValueKind.String && int.TryParse(portEl.GetString(), out p) && p > 0)))
            {
                return null;
            }

            if (isCaptureWindow && TryGetExplicitPid(arguments, out int explicitPid) && !IsHonClientProcessId(explicitPid))
            {
                return null;
            }

            if (arguments.TryGetProperty("target", out JsonElement targetEl) &&
                targetEl.ValueKind == JsonValueKind.String &&
                !string.IsNullOrWhiteSpace(targetEl.GetString()))
            {
                string t = targetEl.GetString()!.Trim();
                bool isK2ClientTarget =
                    string.Equals(t, "hon_x64", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(t, "hon_x64.exe", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(t, "game_client", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(t, "client", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(t, "k2", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(t, "game", StringComparison.OrdinalIgnoreCase) ||
                    (t.StartsWith("pid:", StringComparison.OrdinalIgnoreCase) &&
                     (!isCaptureWindow || (TryParsePidSuffix(t, out int targetPid) && IsHonClientProcessId(targetPid))));
                if (!isK2ClientTarget)
                {
                    return null;
                }
            }

            if (arguments.TryGetProperty("action", out JsonElement actionEl) &&
                actionEl.ValueKind == JsonValueKind.String &&
                !string.IsNullOrWhiteSpace(actionEl.GetString()))
            {
                string act = actionEl.GetString()!.Trim();
                if (act is "errors" or "clear_errors" or "status")
                {
                    return null;
                }

                if (string.Equals(targetName, "kongor_ui_control", StringComparison.OrdinalIgnoreCase) &&
                    act is not ("click" or "double_click" or "right_click" or "move" or "type" or "key_tap" or "key_press" or "key_down" or "key_up" or "hotkey" or "scroll" or "inspect" or "list_elements" or "focus" or "console" or "in_game_console"))
                {
                    return null;
                }
            }
        }

        if (isLiveClientTool)
        {
            bool clientOnline = await CheckClientOnlineCachedAsync(ct).ConfigureAwait(false);
            if (!clientOnline && autoHeal)
            {
                try
                {
                    using JsonDocument startDoc = JsonDocument.Parse("{\"action\":\"start\",\"window_mode\":\"offscreen\",\"masterServer\":\"127.0.0.1:55555\"}");
                    await _gameClientManageHandler.HandleAsync(startDoc.RootElement, ct).ConfigureAwait(false);
                    _lastPreflightCheckTicks = 0;
                    clientOnline = await CheckClientOnlineCachedAsync(ct).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    Console.Error.WriteLine($"[McpToolRegistry] Auto-heal client start failed: {ex.Message}");
                }
            }

            if (!clientOnline)
            {
                return ToolCallResult.Json(new
                {
                    status = "error",
                    preflightFailed = true,
                    tool = targetName,
                    error = $"Preflight check failed for '{targetName}': K2 game client (hon_x64.exe) and native IPC pipe (\\\\.\\pipe\\kongor-native-client) are offline.",
                    remediation = "Start the game client via kongor_game_client_manage(action='start') or pass auto_heal=true.",
                    remediationCommand = "kongor_game_client_manage(action='start')",
                    remediation_call = new
                    {
                        tool = "kongor_game_client_manage",
                        arguments = new
                        {
                            action = "start",
                            window_mode = "offscreen"
                        }
                    }
                }, isError: true);
            }
        }

        return null;
    }

    private static bool HasAnyNonEmptyProperty(JsonElement arguments, params string[] propertyNames)
    {
        if (arguments.ValueKind != JsonValueKind.Object) return false;

        foreach (string name in propertyNames)
        {
            if (arguments.TryGetProperty(name, out JsonElement prop) &&
                prop.ValueKind == JsonValueKind.String &&
                !string.IsNullOrWhiteSpace(prop.GetString()))
            {
                return true;
            }
        }

        return false;
    }

    private static bool IsExplicitStringPropertyValue(JsonElement arguments, string propertyName, string expectedValue)
    {
        if (arguments.ValueKind != JsonValueKind.Object) return false;
        return arguments.TryGetProperty(propertyName, out JsonElement prop) &&
               prop.ValueKind == JsonValueKind.String &&
               string.Equals(prop.GetString()?.Trim(), expectedValue, StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsExplicitFalseBooleanProperty(JsonElement arguments, params string[] propertyNames)
    {
        if (arguments.ValueKind != JsonValueKind.Object) return false;
        foreach (string name in propertyNames)
        {
            if (arguments.TryGetProperty(name, out JsonElement prop))
            {
                if (prop.ValueKind == JsonValueKind.False) return true;
                if (prop.ValueKind == JsonValueKind.String &&
                    string.Equals(prop.GetString()?.Trim(), "false", StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }
        }
        return false;
    }

    private static bool TryGetExplicitPid(JsonElement arguments, out int pid)
    {
        pid = 0;
        if (arguments.ValueKind != JsonValueKind.Object) return false;

        foreach (string key in new[] { "pid", "process_id", "processId" })
        {
            if (arguments.TryGetProperty(key, out JsonElement el))
            {
                if (el.ValueKind == JsonValueKind.Number && el.TryGetInt32(out int numPid) && numPid > 0)
                {
                    pid = numPid;
                    return true;
                }

                if (el.ValueKind == JsonValueKind.String && int.TryParse(el.GetString(), out int strPid) && strPid > 0)
                {
                    pid = strPid;
                    return true;
                }
            }
        }

        if (arguments.TryGetProperty("target", out JsonElement targetEl) &&
            targetEl.ValueKind == JsonValueKind.String &&
            TryParsePidSuffix(targetEl.GetString(), out int suffixPid))
        {
            pid = suffixPid;
            return true;
        }

        return false;
    }

    private static bool TryParsePidSuffix(string? target, out int pid)
    {
        pid = 0;
        if (string.IsNullOrWhiteSpace(target)) return false;

        string trimmed = target.Trim();
        if (!trimmed.StartsWith("pid:", StringComparison.OrdinalIgnoreCase)) return false;

        return int.TryParse(trimmed[4..].Trim(), out pid) && pid > 0;
    }

    private static bool IsHonClientProcessId(int pid)
    {
        if (pid <= 0) return false;
        try
        {
            using Process proc = Process.GetProcessById(pid);
            return string.Equals(proc.ProcessName, "hon_x64", StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    private static long s_lastPayloadPruneTicks;

    public static readonly IReadOnlySet<string> GlobalMetaArguments = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "allowSimulation",
        "allow_simulation",
        "detail",
        "verbose",
        "uncapped",
        "enforceResponseCap",
        "enforce_response_cap",
        "skipPreflight",
        "skip_preflight",
        "enforcePreflight",
        "enforce_preflight",
        "preflightGuard",
        "preflight_guard",
        "dryRun",
        "dry_run",
        "agentId",
        "agent_id",
        "agentOwner",
        "agent_owner",
        "agent",
        "confirmRemote",
        "confirm_remote",
        "allowWindowExposure",
        "allow_window_exposure",
        "stealFocus",
        "steal_focus",
        "selfHeal",
        "self_heal",
        "forceRetry",
        "force_retry",
        "_meta",
        "progressToken",
        "progress_token"
    };

    internal IReadOnlyDictionary<string, ToolRegistration> GetToolRegistrations() => _tools;

    internal static bool IsRecognizedToolArgument(ToolDefinition def, string key)
    {
        if (string.IsNullOrWhiteSpace(key)) return false;
        IReadOnlyDictionary<string, ToolPropertySchema> schemaProps = def.InputSchema.Properties;
        if (schemaProps.ContainsKey(key)) return true;
        if (GlobalMetaArguments.Contains(key)) return true;
        if (def.AcceptedHandlerKeys != null && def.AcceptedHandlerKeys.Contains(key)) return true;
        if (IsAcceptedByHandlerParameterMap(def.Name, key)) return true;
        if (def.ParameterAliases != null && def.ParameterAliases.ContainsKey(key)) return true;
        return ResolveCanonicalPropertyName(key, schemaProps, def.ParameterAliases) != null;
    }

    private static bool NormalizeAndValidateToolArguments(
        string targetName,
        ToolDefinition def,
        JsonElement arguments,
        out JsonElement normalizedArguments,
        out ToolCallResult? conflictError)
    {
        normalizedArguments = arguments;
        conflictError = null;

        if (arguments.ValueKind != JsonValueKind.Object)
        {
            return true;
        }

        IReadOnlyDictionary<string, ToolPropertySchema> schemaProps = def.InputSchema.Properties;
        Dictionary<string, JsonElement> rawProps = new(StringComparer.Ordinal);
        foreach (JsonProperty p in arguments.EnumerateObject())
        {
            rawProps[p.Name] = p.Value;
        }

        bool modified = false;
        foreach (KeyValuePair<string, JsonElement> kv in rawProps.ToArray())
        {
            if (schemaProps.ContainsKey(kv.Key) ||
                GlobalMetaArguments.Contains(kv.Key) ||
                IsAcceptedByHandlerParameterMap(targetName, kv.Key) ||
                (def.AcceptedHandlerKeys != null && def.AcceptedHandlerKeys.Contains(kv.Key)))
            {
                continue;
            }

            string? mappedCanonical = ResolveCanonicalPropertyName(kv.Key, schemaProps, def.ParameterAliases);
            if (mappedCanonical == null)
            {
                IEnumerable<string> candidateKeys = AcceptedHandlerKeysByTool.TryGetValue(targetName, out IReadOnlySet<string>? extraKeys)
                    ? schemaProps.Keys.Concat(extraKeys)
                    : schemaProps.Keys;
                string? suggestion = FindClosestParameter(kv.Key, candidateKeys);
                string[] validParams = schemaProps.Keys.OrderBy(k => k, StringComparer.Ordinal).ToArray();
                string didYouMeanClause = suggestion != null ? $" Did you mean '{suggestion}'?" : string.Empty;
                string validList = validParams.Length > 0 ? string.Join(", ", validParams) : "(none)";
                string unknownMsg =
                    $"Unknown argument '{kv.Key}' supplied to tool '{targetName}' (MCP-293).{didYouMeanClause} Valid parameters for '{targetName}': [{validList}].";

                conflictError = ToolCallResult.Json(new
                {
                    status = "error",
                    ok = false,
                    tool = targetName,
                    errorCode = "unknown_argument",
                    unknownArgument = kv.Key,
                    didYouMean = suggestion,
                    validParameters = validParams,
                    error = unknownMsg,
                    message = unknownMsg,
                    remediation_call = new
                    {
                        tool = Handlers.CallToolHandler.CanonicalToolName,
                        arguments = new
                        {
                            action = "describe",
                            tool_name = targetName
                        }
                    }
                }, isError: true);
                return false;
            }

            if (rawProps.TryGetValue(mappedCanonical, out JsonElement existingVal))
            {
                if (!JsonElementsAreEquivalent(existingVal, kv.Value))
                {
                    string msg =
                        $"Conflicting argument values on '{targetName}': alias '{kv.Key}' ('{kv.Value}') and canonical parameter '{mappedCanonical}' ('{existingVal}') were both supplied with different values. Supply only '{mappedCanonical}'.";
                    conflictError = ToolCallResult.Json(new
                    {
                        status = "error",
                        ok = false,
                        tool = targetName,
                        errorCode = "conflicting_arguments",
                        alias = kv.Key,
                        canonicalParameter = mappedCanonical,
                        error = msg,
                        message = msg
                    }, isError: true);
                    return false;
                }
            }
            else
            {
                rawProps[mappedCanonical] = kv.Value;
                modified = true;
            }
        }

        if (modified)
        {
            byte[] utf8 = JsonSerializer.SerializeToUtf8Bytes(rawProps);
            using JsonDocument doc = JsonDocument.Parse(utf8);
            normalizedArguments = doc.RootElement.Clone();
        }

        return true;
    }

    internal static string? ResolveCanonicalPropertyName(
        string suppliedKey,
        IReadOnlyDictionary<string, ToolPropertySchema> schemaProps,
        IReadOnlyDictionary<string, string>? perToolAliases = null)
    {
        if (perToolAliases != null &&
            perToolAliases.TryGetValue(suppliedKey, out string? explicitTarget) &&
            schemaProps.ContainsKey(explicitTarget))
        {
            return explicitTarget;
        }

        string camel = ToCamelCaseKey(suppliedKey);
        if (schemaProps.ContainsKey(camel)) return camel;

        string snake = ToSnakeCaseKey(suppliedKey);
        if (schemaProps.ContainsKey(snake)) return snake;

        foreach (string schemaKey in schemaProps.Keys)
        {
            if (string.Equals(schemaKey, suppliedKey, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(schemaKey.Replace("_", "", StringComparison.Ordinal), suppliedKey.Replace("_", "", StringComparison.Ordinal), StringComparison.OrdinalIgnoreCase))
            {
                return schemaKey;
            }
        }

        string lower = suppliedKey.ToLowerInvariant();
        if (lower is "file_path" or "filepath" or "target_path" or "targetpath" or "interface_path" or "interfacepath" or "resource_path" or "resourcepath" or "vfs_path" or "vfspath" or "asset_path" or "assetpath")
        {
            if (schemaProps.ContainsKey("path")) return "path";
            if (schemaProps.ContainsKey("virtualPath")) return "virtualPath";
            if (schemaProps.ContainsKey("virtual_path")) return "virtual_path";
            if (schemaProps.ContainsKey("resource")) return "resource";
            if (schemaProps.ContainsKey("assetPath")) return "assetPath";
        }
        if (lower is "path" or "file_path" or "filepath" or "file")
        {
            if (!schemaProps.ContainsKey("path"))
            {
                if (schemaProps.ContainsKey("virtualPath")) return "virtualPath";
                if (schemaProps.ContainsKey("virtual_path")) return "virtual_path";
                if (schemaProps.ContainsKey("replay_path")) return "replay_path";
                if (schemaProps.ContainsKey("replayPath")) return "replayPath";
                if (schemaProps.ContainsKey("dump_path")) return "dump_path";
                if (schemaProps.ContainsKey("dumpPath")) return "dumpPath";
                if (schemaProps.ContainsKey("custom_output_path")) return "custom_output_path";
                if (schemaProps.ContainsKey("output_path")) return "output_path";
                if (schemaProps.ContainsKey("resource")) return "resource";
                if (schemaProps.ContainsKey("mdfPath")) return "mdfPath";
                if (schemaProps.ContainsKey("materialPath")) return "materialPath";
            }
        }
        if (lower is "replay_path" or "replaypath" or "replay_file")
        {
            if (schemaProps.ContainsKey("replay_path")) return "replay_path";
            if (schemaProps.ContainsKey("replayPath")) return "replayPath";
            if (schemaProps.ContainsKey("path")) return "path";
        }
        if (lower is "dump_path" or "dumppath")
        {
            if (schemaProps.ContainsKey("dumpPath")) return "dumpPath";
            if (schemaProps.ContainsKey("dump_path")) return "dump_path";
            if (schemaProps.ContainsKey("path")) return "path";
        }
        if (lower is "max_depth" or "maxdepth")
        {
            if (schemaProps.ContainsKey("depth")) return "depth";
            if (schemaProps.ContainsKey("max_depth")) return "max_depth";
            if (schemaProps.ContainsKey("maxDepth")) return "maxDepth";
        }
        if (lower is "depth")
        {
            if (schemaProps.ContainsKey("max_depth")) return "max_depth";
            if (schemaProps.ContainsKey("maxDepth")) return "maxDepth";
        }
        if (lower is "interface_name" or "interfacename")
        {
            if (schemaProps.ContainsKey("interface")) return "interface";
        }
        if (lower is "widget_name" or "widgetname")
        {
            if (schemaProps.ContainsKey("widget")) return "widget";
        }
        if (lower is "widget")
        {
            if (schemaProps.ContainsKey("widget_name")) return "widget_name";
        }
        if (lower is "mode" or "method")
        {
            if (schemaProps.ContainsKey("capture_method") && !schemaProps.ContainsKey("mode")) return "capture_method";
        }
        if (lower is "command" && !schemaProps.ContainsKey("command") && schemaProps.ContainsKey("action"))
        {
            return "action";
        }
        if (lower is "pid" or "process_id" or "processid" or "target_pid" or "targetprocessid")
        {
            if (schemaProps.ContainsKey("processId")) return "processId";
            if (schemaProps.ContainsKey("process_id")) return "process_id";
            if (schemaProps.ContainsKey("pid")) return "pid";
        }
        if (lower == "target" && !schemaProps.ContainsKey("target") && schemaProps.ContainsKey("targets"))
        {
            return "targets";
        }
        if (lower is "game_dir" or "gamedir" or "base_dir" or "basedir" or "game_directory" or "gamedirectory" or "dir")
        {
            if (schemaProps.ContainsKey("game_dir")) return "game_dir";
            if (schemaProps.ContainsKey("workingDirectory")) return "workingDirectory";
            if (schemaProps.ContainsKey("working_directory")) return "working_directory";
            if (schemaProps.ContainsKey("directory")) return "directory";
            if (schemaProps.ContainsKey("path")) return "path";
        }
        if (lower is "focus")
        {
            if (schemaProps.ContainsKey("steal_focus")) return "steal_focus";
            if (schemaProps.ContainsKey("stealFocus")) return "stealFocus";
        }
        if (lower is "killall" or "kill_all")
        {
            if (schemaProps.ContainsKey("all")) return "all";
        }

        return null;
    }

    private static string? FindClosestParameter(string input, IEnumerable<string> candidates)
    {
        if (string.IsNullOrWhiteSpace(input)) return null;
        string lowerInput = input.ToLowerInvariant();
        string? best = null;
        int bestDist = int.MaxValue;

        foreach (string cand in candidates)
        {
            string lowerCand = cand.ToLowerInvariant();
            int dist = ComputeLevenshteinDistance(lowerInput, lowerCand);
            int underscoreIdx = lowerCand.IndexOf('_');
            if (underscoreIdx > 1)
            {
                int prefixDist = ComputeLevenshteinDistance(lowerInput, lowerCand[..underscoreIdx]);
                if (prefixDist < dist)
                {
                    dist = prefixDist;
                }
            }
            if (dist < bestDist)
            {
                bestDist = dist;
                best = cand;
            }
        }

        int maxAllowed = Math.Max(2, Math.Min(3, lowerInput.Length / 2 + 1));
        return bestDist <= maxAllowed ? best : null;
    }

    private static int ComputeLevenshteinDistance(string s, string t)
    {
        int n = s.Length;
        int m = t.Length;
        if (n == 0) return m;
        if (m == 0) return n;

        int[] prev = new int[m + 1];
        int[] curr = new int[m + 1];
        for (int j = 0; j <= m; j++) prev[j] = j;

        for (int i = 1; i <= n; i++)
        {
            curr[0] = i;
            for (int j = 1; j <= m; j++)
            {
                int cost = s[i - 1] == t[j - 1] ? 0 : 1;
                curr[j] = Math.Min(Math.Min(curr[j - 1] + 1, prev[j] + 1), prev[j - 1] + cost);
            }
            (prev, curr) = (curr, prev);
        }

        return prev[m];
    }

    private static string ToCamelCaseKey(string key)
    {
        if (!key.Contains('_')) return key;
        string[] parts = key.Split('_', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0) return key;
        System.Text.StringBuilder sb = new(parts[0].ToLowerInvariant());
        for (int i = 1; i < parts.Length; i++)
        {
            string p = parts[i];
            if (p.Length > 0)
            {
                sb.Append(char.ToUpperInvariant(p[0]));
                if (p.Length > 1) sb.Append(p.AsSpan(1).ToString().ToLowerInvariant());
            }
        }
        return sb.ToString();
    }

    private static string ToSnakeCaseKey(string key)
    {
        if (string.IsNullOrEmpty(key)) return key;
        System.Text.StringBuilder sb = new();
        for (int i = 0; i < key.Length; i++)
        {
            char c = key[i];
            if (char.IsUpper(c))
            {
                if (i > 0 && key[i - 1] != '_') sb.Append('_');
                sb.Append(char.ToLowerInvariant(c));
            }
            else
            {
                sb.Append(c);
            }
        }
        return sb.ToString();
    }

    private static bool JsonElementsAreEquivalent(JsonElement a, JsonElement b)
    {
        if (a.ValueKind != b.ValueKind) return false;
        return a.ValueKind switch
        {
            JsonValueKind.String => string.Equals(a.GetString(), b.GetString(), StringComparison.Ordinal),
            JsonValueKind.Number => string.Equals(a.GetRawText(), b.GetRawText(), StringComparison.Ordinal),
            JsonValueKind.True or JsonValueKind.False => a.GetBoolean() == b.GetBoolean(),
            JsonValueKind.Null => true,
            _ => string.Equals(a.GetRawText(), b.GetRawText(), StringComparison.Ordinal)
        };
    }

    private static ToolCallResult EnforceExecutionFidelityGate(string targetName, JsonElement arguments, ToolCallResult result)
    {
        if (result.IsError)
        {
            return result;
        }

        string text = result.FirstText;
        if (string.IsNullOrWhiteSpace(text))
        {
            return result;
        }

        bool isSimulatedTestDouble = false;
        try
        {
            using JsonDocument doc = JsonDocument.Parse(text);
            if (doc.RootElement.ValueKind == JsonValueKind.Object &&
                doc.RootElement.TryGetProperty("executionFidelity", out JsonElement efEl) &&
                efEl.ValueKind == JsonValueKind.String &&
                string.Equals(efEl.GetString(), ExecutionFidelity.SimulatedTestDouble.ToString(), StringComparison.OrdinalIgnoreCase))
            {
                isSimulatedTestDouble = true;
            }
        }
        catch (JsonException)
        {
            return result;
        }

        if (!isSimulatedTestDouble || IsSimulationAllowedForCall(arguments))
        {
            return result;
        }

        string msg =
            $"Simulated test double output rejected: Tool '{targetName}' returned simulated test double data (ExecutionFidelity.SimulatedTestDouble), but live or real offline verification is required (MCP-283). " +
            "To permit simulated or synthetic offline data for headless testing, pass argument 'allowSimulation: true' or set environment variable KONGOR_MCP_ALLOW_LEGACY_MOCKS=1.";

        return ToolCallResult.Json(new
        {
            status = "error",
            ok = false,
            tool = targetName,
            errorCode = "simulation_not_allowed",
            executionMode = ExecutionMode.SyntheticFallback,
            executionFidelity = ExecutionFidelity.SimulatedTestDouble.ToString(),
            error = msg,
            message = msg
        }, isError: true);
    }

    internal static bool IsSimulationAllowedForCall(JsonElement arguments)
    {
        if (Environment.GetEnvironmentVariable("KONGOR_MCP_ALLOW_LEGACY_MOCKS") == "1")
        {
            return true;
        }

        if (arguments.ValueKind == JsonValueKind.Object &&
            (arguments.TryGetProperty("allowSimulation", out JsonElement allowEl) ||
             arguments.TryGetProperty("allow_simulation", out allowEl)))
        {
            if (allowEl.ValueKind == JsonValueKind.True)
            {
                return true;
            }

            if (allowEl.ValueKind == JsonValueKind.String && bool.TryParse(allowEl.GetString(), out bool b) && b)
            {
                return true;
            }
        }

        return false;
    }

    private ToolCallResult ApplyDefaultResponseCapIfNeeded(string targetName, JsonElement arguments, ToolCallResult result)
    {
        const int DefaultMaxResponseBytes = 8192;
        string text = result.FirstText;
        if (string.IsNullOrEmpty(text) || System.Text.Encoding.UTF8.GetByteCount(text) <= DefaultMaxResponseBytes)
        {
            return result;
        }

        bool isFullRequested = false;
        bool explicitCapRequested = false;
        if (arguments.ValueKind == JsonValueKind.Object)
        {
            if (arguments.TryGetProperty("detail", out JsonElement detEl) &&
                string.Equals(detEl.GetString(), "full", StringComparison.OrdinalIgnoreCase))
            {
                isFullRequested = true;
            }
            if ((arguments.TryGetProperty("verbose", out JsonElement verbEl) ||
                 arguments.TryGetProperty("uncapped", out verbEl)) &&
                verbEl.ValueKind == JsonValueKind.True)
            {
                isFullRequested = true;
            }
            if ((arguments.TryGetProperty("enforceResponseCap", out JsonElement capEl) ||
                 arguments.TryGetProperty("enforce_response_cap", out capEl)) &&
                capEl.ValueKind == JsonValueKind.True)
            {
                explicitCapRequested = true;
            }
        }

        if (isFullRequested)
        {
            return result;
        }

        // Never truncate kongor_call_tool catalog list/describe unless explicitly requested
        if (string.Equals(targetName, Handlers.CallToolHandler.CanonicalToolName, StringComparison.OrdinalIgnoreCase) && !explicitCapRequested)
        {
            return result;
        }

        try
        {
            using JsonDocument doc = JsonDocument.Parse(text);
            if (doc.RootElement.ValueKind != JsonValueKind.Object)
            {
                return result;
            }

            string overflowDir = ResolvePayloadOverflowDirectory();
            Directory.CreateDirectory(overflowDir);
            PrunePayloadOverflowDirectoryIfNeeded(overflowDir, _timeProvider.GetUtcNow());

            string uniqueSuffix = Guid.NewGuid().ToString("N")[..8];
            string fullOutputPath = Path.Combine(overflowDir, $"{targetName}_{_timeProvider.GetUtcNow():yyyyMMdd_HHmmss_fff}_{uniqueSuffix}.json");
            File.WriteAllText(fullOutputPath, text);
            int originalBytes = System.Text.Encoding.UTF8.GetByteCount(text);

            Dictionary<string, object?> compact = CompactJsonObject(doc.RootElement, depth: 0, maxArrayItems: 5, maxStringChars: 400);
            compact["truncated"] = true;
            compact["originalBytes"] = originalBytes;
            compact["fullOutputPath"] = fullOutputPath;
            compact["hint"] = "Response exceeded the 8 KB default budget; full payload saved to fullOutputPath. Pass detail: 'full' to inline the complete response.";

            string serialized = JsonRpcSerializer.SerializeIndented(compact);
            if (System.Text.Encoding.UTF8.GetByteCount(serialized) > DefaultMaxResponseBytes)
            {
                compact = CompactJsonObject(doc.RootElement, depth: 0, maxArrayItems: 2, maxStringChars: 140);
                compact["truncated"] = true;
                compact["originalBytes"] = originalBytes;
                compact["fullOutputPath"] = fullOutputPath;
                compact["hint"] = "Response exceeded the 8 KB default budget; full payload saved to fullOutputPath. Pass detail: 'full' to inline the complete response.";
                serialized = JsonRpcSerializer.SerializeIndented(compact);
            }

            if (System.Text.Encoding.UTF8.GetByteCount(serialized) > DefaultMaxResponseBytes)
            {
                var minimal = new Dictionary<string, object?>(StringComparer.Ordinal);
                foreach (string essentialKey in new[] { "status", "ok", "tool", "action", "errorCode", "executionFidelity", "error", "message", "remediation_call" })
                {
                    if (doc.RootElement.TryGetProperty(essentialKey, out JsonElement essEl))
                    {
                        minimal[essentialKey] = CompactJsonValue(essEl, essentialKey, depth: 1, maxArrayItems: 2, maxStringChars: 300);
                    }
                }
                minimal["truncated"] = true;
                minimal["originalBytes"] = originalBytes;
                minimal["fullOutputPath"] = fullOutputPath;
                minimal["hint"] = "Response exceeded the 8 KB default budget; full payload saved to fullOutputPath. Pass detail: 'full' to inline the complete response.";
                serialized = JsonRpcSerializer.SerializeIndented(minimal);
            }

            List<ContentItem> preservedNonText = result.Content.Where(c => c is not TextContentItem).ToList();
            List<ContentItem> newContent = [new TextContentItem(serialized), .. preservedNonText];
            return new ToolCallResult
            {
                Content = newContent,
                IsError = result.IsError
            };
        }
        catch (Exception capEx)
        {
            Console.Error.WriteLine($"[McpToolRegistry] Warning: response cap spillover failed for '{targetName}': {capEx.Message}");
            return result;
        }
    }

    private static Dictionary<string, object?> CompactJsonObject(JsonElement obj, int depth, int maxArrayItems, int maxStringChars)
    {
        Dictionary<string, object?> compact = new(StringComparer.Ordinal);
        foreach (JsonProperty prop in obj.EnumerateObject())
        {
            if (prop.Value.ValueKind == JsonValueKind.Array)
            {
                compact[$"{prop.Name}TotalCount"] = prop.Value.GetArrayLength();
            }

            compact[prop.Name] = CompactJsonValue(prop.Value, prop.Name, depth, maxArrayItems, maxStringChars);
        }

        return compact;
    }

    private static object? CompactJsonValue(JsonElement val, string? propName, int depth, int maxArrayItems, int maxStringChars)
    {
        if (propName is "remediation_call")
        {
            return val.Clone();
        }

        switch (val.ValueKind)
        {
            case JsonValueKind.String:
            {
                string s = val.GetString() ?? string.Empty;
                if (propName != null && propName.Contains("base64", StringComparison.OrdinalIgnoreCase) && s.Length > 128)
                {
                    return $"<omitted {s.Length} chars of base64 data; see artifactPath or fullOutputPath>";
                }

                int cap = propName is "error" or "message" ? Math.Max(maxStringChars, 1000) : maxStringChars;
                return s.Length > cap ? string.Concat(s.AsSpan(0, cap), "... [truncated]") : s;
            }

            case JsonValueKind.Array:
            {
                return val.EnumerateArray()
                    .Take(maxArrayItems)
                    .Select(item => CompactJsonValue(item, null, depth + 1, Math.Max(2, maxArrayItems - 2), Math.Max(120, maxStringChars / 2)))
                    .ToArray();
            }

            case JsonValueKind.Object:
            {
                if (depth >= 3)
                {
                    var leaf = new Dictionary<string, object?>(StringComparer.Ordinal);
                    int count = 0;
                    foreach (JsonProperty p in val.EnumerateObject())
                    {
                        if (p.Value.ValueKind is JsonValueKind.String or JsonValueKind.Number or JsonValueKind.True or JsonValueKind.False or JsonValueKind.Null && count < 8)
                        {
                            leaf[p.Name] = CompactJsonValue(p.Value, p.Name, depth + 1, 1, 120);
                            count++;
                        }
                    }
                    leaf["_nestedTruncated"] = true;
                    return leaf;
                }

                return CompactJsonObject(val, depth + 1, Math.Max(2, maxArrayItems - 1), Math.Max(160, maxStringChars / 2));
            }

            default:
                return val.Clone();
        }
    }

    private static string ResolvePayloadOverflowDirectory()
    {
        string? customDir = Environment.GetEnvironmentVariable("KONGOR_MCP_PAYLOAD_DIR");
        if (!string.IsNullOrWhiteSpace(customDir))
        {
            return customDir;
        }

        string? bundleRoot = Environment.GetEnvironmentVariable("KONGOR_BUNDLE_ROOT");
        if (!string.IsNullOrWhiteSpace(bundleRoot) && Directory.Exists(bundleRoot))
        {
            return Path.Combine(bundleRoot, "artifacts", "mcp_payloads");
        }

        foreach (string start in new[] { AppContext.BaseDirectory, Directory.GetCurrentDirectory() })
        {
            if (string.IsNullOrWhiteSpace(start)) continue;
            DirectoryInfo? cur = new(start);
            while (cur != null)
            {
                if (File.Exists(Path.Combine(cur.FullName, "ProjectKongor.slnx")) ||
                    Directory.Exists(Path.Combine(cur.FullName, ".git")))
                {
                    return Path.Combine(cur.FullName, "artifacts", "mcp_payloads");
                }
                cur = cur.Parent;
            }
        }

        return Path.Combine(Path.GetTempPath(), "kongor_mcp_payloads");
    }

    private static void PrunePayloadOverflowDirectoryIfNeeded(string dir, DateTimeOffset nowUtc)
    {
        long nowTicks = nowUtc.UtcTicks;
        long prevTicks = Interlocked.Read(ref s_lastPayloadPruneTicks);
        if (nowTicks - prevTicks < TimeSpan.FromSeconds(60).Ticks)
        {
            return;
        }

        Interlocked.Exchange(ref s_lastPayloadPruneTicks, nowTicks);
        try
        {
            var files = new DirectoryInfo(dir)
                .EnumerateFiles("*.json")
                .OrderByDescending(f => f.LastWriteTimeUtc)
                .ToList();

            DateTime cutoffUtc = nowUtc.UtcDateTime.AddHours(-24);
            for (int i = 0; i < files.Count; i++)
            {
                if (i >= 500 || files[i].LastWriteTimeUtc < cutoffUtc)
                {
                    try
                    {
                        files[i].Delete();
                    }
                    catch (Exception delEx)
                    {
                        Console.Error.WriteLine($"[McpToolRegistry] Warning: failed pruning old MCP payload '{files[i].FullName}': {delEx.Message}");
                    }
                }
            }
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[McpToolRegistry] Warning: payload overflow prune skipped for '{dir}': {ex.Message}");
        }
    }
}
