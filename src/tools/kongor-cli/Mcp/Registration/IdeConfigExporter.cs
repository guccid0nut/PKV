// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// TAO (Together As One) MCP & Kongor CLI -- Created by guccidonut (https://github.com/guccidonut/PKV)

using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Kongor.Cli.Mcp.Registration;

internal sealed record IdeExportResult(
    string Ide,
    bool Success,
    string TargetFilePath,
    string? ErrorMessage = null);

/// <summary>
/// Non-destructive multi-IDE MCP configuration exporter supporting Antigravity, Cursor,
/// VS Code, Zed, Claude Desktop/Code, and OpenAI Codex (TOML).
/// Preserves all existing sibling servers and user settings on every upsert.
/// Never writes uncommitted files into the repository worktree unless explicitly requested via workspaceDir.
/// </summary>
internal static class IdeConfigExporter
{
    public static readonly IReadOnlyList<string> SupportedIdes =
    [
        "antigravity",
        "cursor",
        "vscode",
        "zed",
        "claude",
        "codex"
    ];

    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);

    public static IReadOnlyList<IdeExportResult> Export(
        string ideSelector,
        string commandPath,
        IReadOnlyList<string> args,
        IReadOnlyDictionary<string, string>? env = null,
        string? bundleRoot = null,
        string? userHomeOverride = null,
        string? workspaceDir = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(ideSelector);
        ArgumentException.ThrowIfNullOrWhiteSpace(commandPath);
        ArgumentNullException.ThrowIfNull(args);

        string userHome = !string.IsNullOrWhiteSpace(userHomeOverride)
            ? userHomeOverride
            : Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

        Dictionary<string, string> mergedEnv = new(StringComparer.Ordinal);
        if (!string.IsNullOrWhiteSpace(bundleRoot))
        {
            mergedEnv["KONGOR_BUNDLE_ROOT"] = bundleRoot;
        }
        if (env != null)
        {
            foreach (KeyValuePair<string, string> kv in env)
            {
                if (!string.IsNullOrWhiteSpace(kv.Key) && kv.Value != null)
                {
                    mergedEnv[kv.Key] = kv.Value;
                }
            }
        }

        string[] requested = ideSelector.Trim().Equals("all", StringComparison.OrdinalIgnoreCase)
            ? [.. SupportedIdes]
            : ideSelector.Split([',', ';', ' '], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        List<IdeExportResult> results = new(requested.Length);
        foreach (string rawIde in requested)
        {
            string normalized = NormalizeIdeName(rawIde);
            results.Add(ExportSingle(normalized, commandPath, args, mergedEnv, userHome, userHomeOverride != null, workspaceDir));
        }

        return results;
    }

    public static string NormalizeIdeName(string raw) =>
        raw.Trim().ToLowerInvariant() switch
        {
            "antigravity" or "agy" or "gemini" => "antigravity",
            "cursor" => "cursor",
            "vscode" or "code" or "vs-code" or "copilot" => "vscode",
            "zed" => "zed",
            "claude" or "claude-desktop" or "claude-code" => "claude",
            "codex" or "openai" => "codex",
            _ => raw.Trim().ToLowerInvariant()
        };

    private static IdeExportResult ExportSingle(
        string ide,
        string commandPath,
        IReadOnlyList<string> args,
        IReadOnlyDictionary<string, string> env,
        string userHome,
        bool isSandboxedHome,
        string? workspaceDir)
    {
        try
        {
            string targetPath = ResolveTargetConfigPath(ide, userHome, isSandboxedHome, workspaceDir);
            string? parent = Path.GetDirectoryName(targetPath);
            if (!string.IsNullOrWhiteSpace(parent))
            {
                Directory.CreateDirectory(parent);
            }

            switch (ide)
            {
                case "antigravity":
                case "cursor":
                case "claude":
                    UpsertStandardMcpServersJson(targetPath, commandPath, args, env);
                    return new IdeExportResult(ide, true, targetPath);

                case "vscode":
                    UpsertVsCodeServersJson(targetPath, commandPath, args, env);
                    return new IdeExportResult(ide, true, targetPath);

                case "zed":
                    UpsertZedSettingsJson(targetPath, commandPath, args, env);
                    return new IdeExportResult(ide, true, targetPath);

                case "codex":
                    UpsertCodexToml(targetPath, commandPath, args, env);
                    return new IdeExportResult(ide, true, targetPath);

                default:
                    return new IdeExportResult(
                        ide,
                        false,
                        string.Empty,
                        $"Unsupported IDE '{ide}'. Supported targets: {string.Join(", ", SupportedIdes)}, or 'all'.");
            }
        }
        catch (Exception ex)
        {
            return new IdeExportResult(ide, false, string.Empty, ex.Message);
        }
    }

    public static string ResolveTargetConfigPath(
        string ide,
        string userHome,
        bool isSandboxedHome = false,
        string? workspaceDir = null)
    {
        return ide switch
        {
            "antigravity" => Path.Combine(userHome, ".gemini", "antigravity", "mcp_config.json"),
            "cursor" => !string.IsNullOrWhiteSpace(workspaceDir)
                ? Path.Combine(workspaceDir, ".cursor", "mcp.json")
                : Path.Combine(userHome, ".cursor", "mcp.json"),
            "vscode" => !string.IsNullOrWhiteSpace(workspaceDir)
                ? Path.Combine(workspaceDir, ".vscode", "mcp.json")
                : Path.Combine(userHome, ".vscode", "mcp.json"),
            "zed" => !isSandboxedHome && OperatingSystem.IsWindows()
                ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Zed", "settings.json")
                : Path.Combine(userHome, ".config", "zed", "settings.json"),
            "claude" => !isSandboxedHome && OperatingSystem.IsWindows()
                ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Claude", "claude_desktop_config.json")
                : !isSandboxedHome && OperatingSystem.IsMacOS()
                    ? Path.Combine(userHome, "Library", "Application Support", "Claude", "claude_desktop_config.json")
                    : Path.Combine(userHome, ".config", "Claude", "claude_desktop_config.json"),
            "codex" => Path.Combine(userHome, ".codex", "config.toml"),
            _ => throw new ArgumentException($"Unsupported IDE '{ide}'.", nameof(ide))
        };
    }

    private static JsonObject ReadExistingJsonObjectOrThrow(string filePath)
    {
        if (!File.Exists(filePath))
        {
            return [];
        }

        string existing = File.ReadAllText(filePath, Encoding.UTF8);
        if (string.IsNullOrWhiteSpace(existing))
        {
            return [];
        }

        JsonDocumentOptions docOpts = new()
        {
            CommentHandling = JsonCommentHandling.Skip,
            AllowTrailingCommas = true
        };

        JsonNode? parsed = JsonNode.Parse(existing, documentOptions: docOpts);
        if (parsed is not JsonObject obj)
        {
            throw new InvalidOperationException($"Existing config '{filePath}' does not have a JSON object root; refusing to clobber.");
        }

        return obj;
    }

    private static void UpsertStandardMcpServersJson(
        string filePath,
        string commandPath,
        IReadOnlyList<string> args,
        IReadOnlyDictionary<string, string> env)
    {
        JsonObject root = ReadExistingJsonObjectOrThrow(filePath);
        if (root["mcpServers"] is not JsonObject servers)
        {
            servers = [];
            root["mcpServers"] = servers;
        }

        JsonObject node = new()
        {
            ["command"] = commandPath,
            ["args"] = ToJsonArray(args)
        };
        if (env.Count > 0)
        {
            node["env"] = ToJsonObject(env);
        }

        servers["kongor"] = node;
        WriteIndentedJson(filePath, root);
    }

    private static void UpsertVsCodeServersJson(
        string filePath,
        string commandPath,
        IReadOnlyList<string> args,
        IReadOnlyDictionary<string, string> env)
    {
        JsonObject root = ReadExistingJsonObjectOrThrow(filePath);
        if (root["servers"] is not JsonObject servers)
        {
            servers = [];
            root["servers"] = servers;
        }

        JsonObject node = new()
        {
            ["type"] = "stdio",
            ["command"] = commandPath,
            ["args"] = ToJsonArray(args)
        };
        if (env.Count > 0)
        {
            node["env"] = ToJsonObject(env);
        }

        servers["kongor"] = node;
        WriteIndentedJson(filePath, root);
    }

    private static void UpsertZedSettingsJson(
        string filePath,
        string commandPath,
        IReadOnlyList<string> args,
        IReadOnlyDictionary<string, string> env)
    {
        JsonObject root = ReadExistingJsonObjectOrThrow(filePath);
        if (root["context_servers"] is not JsonObject contextServers)
        {
            contextServers = [];
            root["context_servers"] = contextServers;
        }

        JsonObject node = new()
        {
            ["source"] = "custom",
            ["command"] = commandPath,
            ["args"] = ToJsonArray(args)
        };
        if (env.Count > 0)
        {
            node["env"] = ToJsonObject(env);
        }

        contextServers["kongor"] = node;
        WriteIndentedJson(filePath, root);
    }

    private static void UpsertCodexToml(
        string filePath,
        string commandPath,
        IReadOnlyList<string> args,
        IReadOnlyDictionary<string, string> env)
    {
        string existing = File.Exists(filePath) ? File.ReadAllText(filePath, Encoding.UTF8) : string.Empty;
        string stripped = StripKongorTomlSections(existing);

        StringBuilder sb = new();
        if (!string.IsNullOrWhiteSpace(stripped))
        {
            sb.Append(stripped.TrimEnd());
            sb.Append("\n\n");
        }

        sb.Append("[mcp_servers.kongor]\n");
        sb.Append($"command = {EscapeTomlBasicString(commandPath)}\n");
        sb.Append($"args = [{string.Join(", ", args.Select(EscapeTomlBasicString))}]\n");

        if (env.Count > 0)
        {
            sb.Append("\n[mcp_servers.kongor.env]\n");
            foreach (KeyValuePair<string, string> kv in env.OrderBy(k => k.Key, StringComparer.Ordinal))
            {
                string keyLiteral = Regex.IsMatch(kv.Key, "^[A-Za-z0-9_-]+$")
                    ? kv.Key
                    : EscapeTomlBasicString(kv.Key);
                sb.Append($"{keyLiteral} = {EscapeTomlBasicString(kv.Value)}\n");
            }
        }

        File.WriteAllText(filePath, sb.ToString(), Utf8NoBom);
    }

    private static string StripKongorTomlSections(string toml)
    {
        if (string.IsNullOrWhiteSpace(toml))
        {
            return string.Empty;
        }

        string[] lines = toml.Replace("\r\n", "\n").Split('\n');
        List<string> kept = new(lines.Length);
        bool inKongorSection = false;

        foreach (string line in lines)
        {
            string trimmed = line.Trim();
            if (trimmed.StartsWith('[') && trimmed.EndsWith(']') && !trimmed.StartsWith("[[", StringComparison.Ordinal))
            {
                string header = trimmed[1..^1].Trim();
                inKongorSection = header.Equals("mcp_servers.kongor", StringComparison.OrdinalIgnoreCase) ||
                                  header.StartsWith("mcp_servers.kongor.", StringComparison.OrdinalIgnoreCase);
            }

            if (!inKongorSection)
            {
                kept.Add(line);
            }
        }

        return string.Join("\n", kept);
    }

    private static string EscapeTomlBasicString(string value)
    {
        StringBuilder sb = new(value.Length + 4);
        sb.Append('"');
        foreach (char c in value)
        {
            switch (c)
            {
                case '\\': sb.Append(@"\\"); break;
                case '"': sb.Append("\\\""); break;
                case '\n': sb.Append(@"\n"); break;
                case '\r': sb.Append(@"\r"); break;
                case '\t': sb.Append(@"\t"); break;
                default: sb.Append(c); break;
            }
        }
        sb.Append('"');
        return sb.ToString();
    }

    private static JsonArray ToJsonArray(IReadOnlyList<string> items)
    {
        JsonArray arr = [];
        foreach (string item in items) arr.Add(item);
        return arr;
    }

    private static JsonObject ToJsonObject(IReadOnlyDictionary<string, string> dict)
    {
        JsonObject obj = [];
        foreach (KeyValuePair<string, string> kv in dict)
        {
            obj[kv.Key] = kv.Value;
        }
        return obj;
    }

    private static void WriteIndentedJson(string filePath, JsonObject root)
    {
        string json = root.ToJsonString(new JsonSerializerOptions { WriteIndented = true }).Replace("\r\n", "\n") + "\n";
        File.WriteAllText(filePath, json, Utf8NoBom);
    }
}
