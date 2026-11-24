// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// TAO (Together As One) MCP & Kongor CLI -- Created by guccidonut (https://github.com/guccidonut/PKV)

using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using Kongor.Cli.Mcp.Protocol;
using Kongor.Cli.Mcp.Services;

namespace Kongor.Cli.Mcp.Handlers;

public sealed partial class GameLuaHandler
{
    [GeneratedRegex(@"\^[0-9a-zA-Z]", RegexOptions.Compiled)]
    private static partial Regex K2ColorCodeRegex();

    public static string StripColorCodes(string? input) =>
        string.IsNullOrEmpty(input) ? string.Empty : K2ColorCodeRegex().Replace(input, string.Empty);

    public static bool TryExtractTaoResponse(
        string? text,
        out JsonElement data,
        out string? status,
        out string? error)
    {
        data = default;
        status = null;
        error = null;
        if (string.IsNullOrWhiteSpace(text)) return false;

        string cleanText = StripColorCodes(text);
        const string prefix = "[TAO_RESPONSE]";
        int idx = cleanText.LastIndexOf(prefix, StringComparison.OrdinalIgnoreCase);
        string payload;

        if (idx >= 0)
        {
            payload = cleanText.Substring(idx + prefix.Length).Trim();
            int newlineIdx = payload.IndexOfAny(['\r', '\n']);
            if (newlineIdx >= 0)
            {
                payload = payload.Substring(0, newlineIdx).Trim();
            }
        }
        else
        {
            payload = cleanText.Trim();
        }

        if (string.IsNullOrWhiteSpace(payload)) return false;

        if (payload.StartsWith('{') && payload.EndsWith('}'))
        {
            try
            {
                using var doc = JsonDocument.Parse(payload);
                var root = doc.RootElement;
                bool hasStatus = root.TryGetProperty("status", out var sEl);
                bool hasData = root.TryGetProperty("data", out var dEl);
                bool hasError = root.TryGetProperty("error", out var eEl);

                if (hasStatus && (hasData || hasError || root.TryGetProperty("root", out _) || root.TryGetProperty("shieldedCount", out _)))
                {
                    status = sEl.GetString();
                    if (hasData)
                    {
                        data = dEl.Clone();
                    }
                    else if (!hasError)
                    {
                        data = root.Clone();
                    }
                    if (hasError)
                    {
                        error = eEl.GetString();
                    }
                    return true;
                }
            }
            catch
            {
                // Fall back
            }
        }

        return false;
    }

    public static bool TryExtractTelemetry(string? text, out JsonElement telemetry)
    {
        telemetry = default;
        if (string.IsNullOrWhiteSpace(text)) return false;

        string cleanText = StripColorCodes(text);
        const string prefix = "[NEXUS_TELEMETRY]";
        int idx = cleanText.LastIndexOf(prefix, StringComparison.OrdinalIgnoreCase);
        if (idx < 0) return false;

        string payload = cleanText.Substring(idx + prefix.Length).Trim();
        int newlineIdx = payload.IndexOfAny(['\r', '\n']);
        if (newlineIdx >= 0)
        {
            payload = payload.Substring(0, newlineIdx).Trim();
        }

        if (string.IsNullOrWhiteSpace(payload)) return false;

        // 1. Try JSON parsing
        if (payload.StartsWith('{') && payload.EndsWith('}'))
        {
            try
            {
                using var doc = JsonDocument.Parse(payload);
                telemetry = doc.RootElement.Clone();
                return true;
            }
            catch
            {
                // Fall back to KV parsing below
            }
        }

        // 2. Semicolon-delimited key=value tokens fallback
        var dict = ParseKeyValuePairs(payload);
        if (dict.Count > 0)
        {
            if (dict.TryGetValue("max_hp", out var maxHp) && !dict.ContainsKey("maxHp"))
                dict["maxHp"] = maxHp;
            if (dict.TryGetValue("max_mana", out var maxMana) && !dict.ContainsKey("maxMana"))
                dict["maxMana"] = maxMana;
            if (dict.TryGetValue("game_time", out var gt) && !dict.ContainsKey("gameTime"))
                dict["gameTime"] = gt;

            telemetry = JsonSerializer.SerializeToElement(dict);
            return true;
        }

        return false;
    }

    public static bool TryExtractEvent(string? text, [NotNullWhen(true)] out string? eventName, out JsonElement eventPayload)
    {
        eventName = null;
        eventPayload = default;
        if (string.IsNullOrWhiteSpace(text)) return false;

        string cleanText = StripColorCodes(text);
        const string prefix = "[NEXUS_EVENT]";
        int idx = cleanText.LastIndexOf(prefix, StringComparison.OrdinalIgnoreCase);
        if (idx < 0) return false;

        string payload = cleanText.Substring(idx + prefix.Length).Trim();
        int newlineIdx = payload.IndexOfAny(['\r', '\n']);
        if (newlineIdx >= 0)
        {
            payload = payload.Substring(0, newlineIdx).Trim();
        }

        int pipeIdx = payload.IndexOf('|');
        if (pipeIdx <= 0) return false;

        eventName = payload.Substring(0, pipeIdx).Trim();
        string dataStr = payload.Substring(pipeIdx + 1).Trim();

        if (dataStr.StartsWith('{') && dataStr.EndsWith('}'))
        {
            try
            {
                using var doc = JsonDocument.Parse(dataStr);
                eventPayload = doc.RootElement.Clone();
                return true;
            }
            catch
            {
                // Fall back to KV parsing below
            }
        }

        var kvDict = ParseKeyValuePairs(dataStr);
        if (kvDict.Count > 0)
        {
            eventPayload = JsonSerializer.SerializeToElement(kvDict);
            return true;
        }

        eventPayload = JsonSerializer.SerializeToElement(new { raw = dataStr });
        return true;
    }

    public static bool TryDetectStructuredError(
        string? text,
        string? eventName,
        JsonElement eventPayload,
        [NotNullWhen(true)] out string? errorCode,
        out string? errorMessage,
        out int? currentPhase)
    {
        errorCode = null;
        errorMessage = null;
        currentPhase = null;

        // 1. Check event payload object (e.g. [NEXUS_EVENT] ERROR|{"errorCode":"INVALID_GAME_PHASE","currentPhase":0,...})
        if (eventPayload.ValueKind == JsonValueKind.Object)
        {
            if (eventPayload.TryGetProperty("errorCode", out var ecEl) ||
                eventPayload.TryGetProperty("code", out ecEl))
            {
                errorCode = ecEl.GetString();
            }

            if (eventPayload.TryGetProperty("message", out var msgEl))
            {
                errorMessage = msgEl.GetString();
            }

            if (eventPayload.TryGetProperty("currentPhase", out var cpEl) && cpEl.TryGetInt32(out int cp))
            {
                currentPhase = cp;
            }
            else if (eventPayload.TryGetProperty("current_phase", out var cp2El) && cp2El.TryGetInt32(out int cp2))
            {
                currentPhase = cp2;
            }
        }

        // 2. Check if raw text is JSON containing errorCode (e.g. {"success": false, "errorCode": "INVALID_GAME_PHASE", "currentPhase": 1})
        if (string.IsNullOrEmpty(errorCode) && !string.IsNullOrWhiteSpace(text))
        {
            string trimmed = text.Trim();
            if (trimmed.StartsWith('{') && trimmed.EndsWith('}'))
            {
                try
                {
                    using var rawDoc = JsonDocument.Parse(trimmed);
                    var root = rawDoc.RootElement;
                    if (root.TryGetProperty("errorCode", out var rawEc) || root.TryGetProperty("code", out rawEc))
                    {
                        errorCode = rawEc.GetString();
                    }
                    if (root.TryGetProperty("message", out var rawMsg))
                    {
                        errorMessage = rawMsg.GetString();
                    }
                    if (root.TryGetProperty("currentPhase", out var rawCp) && rawCp.TryGetInt32(out int cp))
                    {
                        currentPhase = cp;
                    }
                    else if (root.TryGetProperty("current_phase", out var rawCp2) && rawCp2.TryGetInt32(out int cp2))
                    {
                        currentPhase = cp2;
                    }
                }
                catch { }
            }
        }

        // 3. Check event name
        if (eventName != null && eventName.Equals("ERROR", StringComparison.OrdinalIgnoreCase) && string.IsNullOrEmpty(errorCode))
        {
            errorCode = "EXECUTION_ERROR";
        }

        // 4. Check raw text patterns
        if (string.IsNullOrEmpty(errorCode) && !string.IsNullOrWhiteSpace(text))
        {
            if (text.Contains("INVALID_GAME_PHASE", StringComparison.OrdinalIgnoreCase) ||
                text.Contains("[VerifyPhase] Guard assertion", StringComparison.OrdinalIgnoreCase))
            {
                errorCode = "INVALID_GAME_PHASE";
                errorMessage = "Operation rejected: Current game phase is not permitted.";
            }
            else if (text.Contains("INVALID_SNAPSHOT", StringComparison.OrdinalIgnoreCase))
            {
                errorCode = "INVALID_SNAPSHOT";
                errorMessage = "Operation rejected: Invalid snapshot structure.";
            }
            else if (text.StartsWith("[ERROR]", StringComparison.OrdinalIgnoreCase) ||
                     text.Contains("[LUA_ERR]", StringComparison.OrdinalIgnoreCase) ||
                     text.Contains("Lua syntax error:", StringComparison.OrdinalIgnoreCase) ||
                     text.Contains("TAO.Dispatch error:", StringComparison.OrdinalIgnoreCase))
            {
                errorCode = "LUA_RUNTIME_ERROR";
                errorMessage = text.Trim();
            }
        }

        return !string.IsNullOrEmpty(errorCode);
    }

    private static Dictionary<string, object?> ParseKeyValuePairs(string payload)
    {
        var dict = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
        string[] pairs = payload.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        foreach (string pair in pairs)
        {
            int eqIdx = pair.IndexOf('=');
            if (eqIdx <= 0) continue;

            string key = pair.Substring(0, eqIdx).Trim();
            string val = pair.Substring(eqIdx + 1).Trim();

            if (int.TryParse(val, NumberStyles.Integer, CultureInfo.InvariantCulture, out int intVal))
            {
                dict[key] = intVal;
            }
            else if (long.TryParse(val, NumberStyles.Integer, CultureInfo.InvariantCulture, out long lVal))
            {
                dict[key] = lVal;
            }
            else if (double.TryParse(val, NumberStyles.Float, CultureInfo.InvariantCulture, out double dblVal))
            {
                dict[key] = dblVal;
            }
            else if (bool.TryParse(val, out bool bVal))
            {
                dict[key] = bVal;
            }
            else
            {
                dict[key] = val;
            }
        }
        return dict;
    }

    public static string FormatJsonElementAsLuaTable(JsonElement element)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                var props = new List<string>();
                foreach (var prop in element.EnumerateObject())
                {
                    if (prop.NameEquals("action") || prop.NameEquals("target") || prop.NameEquals("timeoutMs") || prop.NameEquals("autoLaunch"))
                        continue;
                    string keyFormatted = IsValidLuaIdentifier(prop.Name)
                        ? prop.Name
                        : $"[\"{EscapeLuaString(prop.Name)}\"]";
                    props.Add($"{keyFormatted} = {FormatJsonElementAsLuaTable(prop.Value)}");
                }
                return $"{{ {string.Join(", ", props)} }}";

            case JsonValueKind.Array:
                var items = new List<string>();
                foreach (var item in element.EnumerateArray())
                {
                    items.Add(FormatJsonElementAsLuaTable(item));
                }
                return $"{{ {string.Join(", ", items)} }}";

            case JsonValueKind.String:
                return $"'{EscapeLuaString(element.GetString() ?? string.Empty)}'";

            case JsonValueKind.Number:
                return element.GetRawText();

            case JsonValueKind.True:
                return "true";

            case JsonValueKind.False:
                return "false";

            case JsonValueKind.Null:
            default:
                return "nil";
        }
    }

    private static bool IsValidLuaIdentifier(string s)
    {
        if (string.IsNullOrEmpty(s)) return false;
        if (!char.IsLetter(s[0]) && s[0] != '_') return false;
        for (int i = 1; i < s.Length; i++)
        {
            if (!char.IsLetterOrDigit(s[i]) && s[i] != '_') return false;
        }
        return true;
    }

    private static string? GetStringParam(JsonElement root, JsonElement nested, string propName)
    {
        if (nested.ValueKind == JsonValueKind.Object &&
            nested.TryGetProperty(propName, out JsonElement el) &&
            !string.IsNullOrWhiteSpace(el.GetString()))
        {
            return el.GetString()!.Trim();
        }
        if (root.ValueKind == JsonValueKind.Object &&
            root.TryGetProperty(propName, out JsonElement rEl) &&
            !string.IsNullOrWhiteSpace(rEl.GetString()))
        {
            return rEl.GetString()!.Trim();
        }
        return null;
    }

    private static bool GetBoolParam(JsonElement root, JsonElement nested, string propName, bool defaultValue)
    {
        if (nested.ValueKind == JsonValueKind.Object && nested.TryGetProperty(propName, out JsonElement el))
        {
            if (el.ValueKind == JsonValueKind.True) return true;
            if (el.ValueKind == JsonValueKind.False) return false;
            if (el.ValueKind == JsonValueKind.String && bool.TryParse(el.GetString(), out bool b)) return b;
        }
        if (root.ValueKind == JsonValueKind.Object && root.TryGetProperty(propName, out JsonElement rEl))
        {
            if (rEl.ValueKind == JsonValueKind.True) return true;
            if (rEl.ValueKind == JsonValueKind.False) return false;
            if (rEl.ValueKind == JsonValueKind.String && bool.TryParse(rEl.GetString(), out bool b)) return b;
        }
        return defaultValue;
    }

    private static int GetIntParam(JsonElement root, JsonElement nested, string propName, int defaultValue)
    {
        if (nested.ValueKind == JsonValueKind.Object && nested.TryGetProperty(propName, out JsonElement el) && el.TryGetInt32(out int val))
        {
            return val;
        }
        if (root.ValueKind == JsonValueKind.Object && root.TryGetProperty(propName, out JsonElement rEl) && rEl.TryGetInt32(out int rVal))
        {
            return rVal;
        }
        return defaultValue;
    }

    private static string EscapeLuaString(string str)
    {
        if (string.IsNullOrEmpty(str)) return string.Empty;
        return str.Replace("\\", "\\\\").Replace("'", "\\'");
    }

    private static bool GetAutoLaunch(JsonElement arguments)
    {
        if (arguments.TryGetProperty("autoLaunch", out JsonElement el))
        {
            if (el.ValueKind == JsonValueKind.False) return false;
            if (el.ValueKind == JsonValueKind.True) return true;
            if (el.ValueKind == JsonValueKind.String && bool.TryParse(el.GetString(), out bool b)) return b;
        }
        return true;
    }
}
