// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// TAO (Together As One) MCP & Kongor CLI -- Created by guccidonut (https://github.com/guccidonut/PKV)

using System.Collections.Concurrent;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.IO.Compression;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace Kongor.Cli.Mcp.Services;

/// <summary>
/// Structured node representing a K2 engine UI widget.
/// </summary>
public sealed record K2WidgetNode
{
    public string Name { get; init; } = string.Empty;
    public string Type { get; init; } = string.Empty;
    public bool Visible { get; init; } = true;
    public string? Texture { get; init; }
    public string? Model { get; init; }
    public string? X { get; init; }
    public string? Y { get; init; }
    public string? Width { get; init; }
    public string? Height { get; init; }
    public string? Align { get; init; }
    public string? Valign { get; init; }
    public string? Color { get; init; }
    public bool Noclick { get; init; }
    public bool IsWhiteFallback { get; init; }
    public bool IsMissingTexture { get; init; }
    public string? ResolvedTextureDiskPath { get; init; }
    public List<K2WidgetNode> Children { get; init; } = [];
    public Dictionary<string, string> Properties { get; init; } = new(StringComparer.OrdinalIgnoreCase);
}

/// <summary>
/// Warning flagged during K2 UI inspection.
/// </summary>
public sealed record K2UiWarning
{
    public string WidgetName { get; init; } = string.Empty;
    public string WarningType { get; init; } = string.Empty; // "MissingTexture", "WhiteFallback", "MissingModel", "MissingTemplate"
    public string Message { get; init; } = string.Empty;
    public string? Path { get; init; }
}

/// <summary>
/// Introspection result returned by K2UiInspectService.
/// </summary>
public sealed record K2UiInspectResult
{
    public bool Success { get; init; }
    public string Mode { get; init; } = "static"; // "live", "static"
    public string SourcePath { get; init; } = string.Empty;
    public int TotalWidgets { get; init; }
    public int VisibleWidgets { get; init; }
    public int MissingTexturesCount { get; init; }
    public int WhiteFallbacksCount { get; init; }
    public IReadOnlyList<K2UiWarning> Warnings { get; init; } = [];
    public K2WidgetNode? Root { get; init; }
    public IReadOnlyList<K2WidgetNode> Widgets { get; init; } = [];
    public string? ErrorMessage { get; init; }
    public string Timestamp { get; init; } = string.Empty;
}

/// <summary>
/// Interface for K2 UI Introspection Service.
/// </summary>
public interface IK2UiInspectService
{
    Task<K2UiInspectResult> InspectUiAsync(
        string? targetPath = null,
        string? widgetName = null,
        int depth = 10,
        string mode = "auto",
        CancellationToken ct = default);

    Task<K2UiInspectResult> InspectLiveUiAsync(
        string? widgetName = null,
        int depth = 10,
        CancellationToken ct = default);

    K2UiInspectResult InspectStaticUi(
        string? interfaceOrPackagePath = null,
        string? widgetName = null,
        int depth = 10,
        string? gameDirectory = null);
}

/// <summary>
/// Dual-mode K2 UI introspection service:
/// 1. Live Mode: Named Pipe IPC (\\.\pipe\willowmaker-ipc) console & Lua queries to running hon_x64.exe.
/// 2. Static Mode: Parses K2 .interface and .package XML files, expanding &lt;template&gt; and &lt;instance&gt; tags,
///    verifying texture paths against VFS mounts, and flagging missing textures or $white fallbacks.
/// 3. Auto Mode: Queries live engine if running, otherwise falls back to static package parser.
/// </summary>
public sealed class K2UiInspectService : IK2UiInspectService
{
    private static readonly Regex TextureHookRegex = new(@"(?:SetTexture|RegisterTexture)\s*\(\s*['""]([^'""]+)['""]", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex ModelHookRegex = new(@"(?:SetModel|RegisterModel)\s*\(\s*['""]([^'""]+)['""]", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex ParamPlaceholderRegex = new(@"\{([a-zA-Z0-9_\-\.]+)(?:=([^\{\}]*))?\}", RegexOptions.Compiled);
    private static readonly Regex UnescapedAmpersandRegex = new(@"&(?!(amp|lt|gt|quot|apos|#\d+|#x[0-9a-fA-F]+);)", RegexOptions.Compiled);

    private readonly IGameIpcBridge? _ipcBridge;
    private readonly ITaoClient? _taoClient;
    private readonly TimeProvider _timeProvider;
    private readonly Action<string> _logger;

    public K2UiInspectService(
        IGameIpcBridge? ipcBridge = null,
        TimeProvider? timeProvider = null,
        Action<string>? logger = null,
        ITaoClient? taoClient = null)
    {
        _ipcBridge = ipcBridge ?? new GameIpcBridge();
        _timeProvider = timeProvider ?? TimeProvider.System;
        _logger = logger ?? (msg => Console.Error.WriteLine($"[{_timeProvider.GetUtcNow():yyyy-MM-ddTHH:mm:ss.fffZ}] [K2UiInspectService] {msg}"));

        NexusMailboxIpcBridge? mailboxBridge = _ipcBridge as NexusMailboxIpcBridge
            ?? (_ipcBridge as CompositeGameIpcBridge)?.Tertiary as NexusMailboxIpcBridge;
        _taoClient = taoClient ?? (_ipcBridge != null ? new TaoClient(mailboxBridge: mailboxBridge, fallbackBridge: _ipcBridge) : null);
    }

    public async Task<K2UiInspectResult> InspectUiAsync(
        string? targetPath = null,
        string? widgetName = null,
        int depth = 10,
        string mode = "auto",
        CancellationToken ct = default)
    {
        string normalizedMode = string.IsNullOrWhiteSpace(mode) ? "auto" : mode.Trim().ToLowerInvariant();
        _logger($"InspectUiAsync called with targetPath='{targetPath ?? "default"}', widgetName='{widgetName ?? "all"}', depth={depth}, mode='{normalizedMode}'");

        if (normalizedMode == "live")
        {
            return await InspectLiveUiAsync(widgetName, depth, ct).ConfigureAwait(false);
        }

        if (normalizedMode == "static")
        {
            return InspectStaticUi(targetPath, widgetName, depth);
        }

        // Auto mode: Check if game client process is running and IPC responds
        if (IsGameClientRunning())
        {
            _logger("Game client process detected. Probing live UI query with 400ms timeout...");
            try
            {
                using var autoTimeoutCts = new CancellationTokenSource(400);
                using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct, autoTimeoutCts.Token);
                var liveResult = await InspectLiveUiAsync(widgetName, depth, linkedCts.Token).ConfigureAwait(false);
                if (liveResult.Success && liveResult.TotalWidgets > 0)
                {
                    _logger($"Live UI query succeeded with {liveResult.TotalWidgets} widgets discovered.");
                    return liveResult;
                }
                _logger($"Live UI query produced 0 widgets or warning ({liveResult.ErrorMessage}). Falling back to static parser.");
            }
            catch (Exception ex)
            {
                _logger($"Live UI query failed ({ex.Message}). Falling back to static parser.");
            }
        }
        else
        {
            _logger("Game client process not running. Using static K2 XML parser.");
        }

        return InspectStaticUi(targetPath, widgetName, depth);
    }

    #region Live Introspection (IPC & Lua)

    public async Task<K2UiInspectResult> InspectLiveUiAsync(
        string? widgetName = null,
        int depth = 10,
        CancellationToken ct = default)
    {
        _logger($"InspectLiveUiAsync executing. Widget: '{widgetName ?? "root"}', Depth: {depth}");

        if (!IsGameClientRunning())
        {
            string msg = "hon_x64 game client is not currently running. Cannot perform live UI introspection.";
            _logger($"[WARN] {msg}");
            return new K2UiInspectResult
            {
                Success = false,
                Mode = "live",
                ErrorMessage = msg,
                Timestamp = _timeProvider.GetUtcNow().ToString("o")
            };
        }

        try
        {
            if (_taoClient != null)
            {
                var inspectPayload = string.IsNullOrWhiteSpace(widgetName)
                    ? (object)new { maxDepth = Math.Clamp(depth, 1, 12) }
                    : (object)new { widget = widgetName.Trim(), maxDepth = Math.Clamp(depth, 1, 12) };

                var taoResp = await _taoClient.DispatchAsync("inspect_ui", inspectPayload, 3500, ct).ConfigureAwait(false);
                if (taoResp.Success && taoResp.Data.ValueKind != JsonValueKind.Undefined && taoResp.Data.TryGetProperty("root", out JsonElement taoRoot))
                {
                    List<K2WidgetNode> widgets = [];
                    List<K2UiWarning> warnings = [];
                    int total = 0, visibleCount = 0, whiteCount = 0;
                    var parsedRoot = ParseTaoJsonNode(taoRoot, widgets, warnings, ref total, ref visibleCount, ref whiteCount);
                    if (parsedRoot != null)
                    {
                        return new K2UiInspectResult
                        {
                            Success = true,
                            Mode = "live",
                            SourcePath = "live://hon_x64/ui",
                            TotalWidgets = total,
                            VisibleWidgets = visibleCount,
                            MissingTexturesCount = 0,
                            WhiteFallbacksCount = whiteCount,
                            Root = parsedRoot,
                            Widgets = widgets,
                            Warnings = warnings,
                            Timestamp = _timeProvider.GetUtcNow().ToString("o")
                        };
                    }
                }
            }

            if (_ipcBridge != null)
            {
                if (!_ipcBridge.IsConnected)
                {
                    _logger("Connecting to Named Pipe IPC \\\\.\\pipe\\willowmaker-ipc...");
                    await _ipcBridge.ConnectAsync(2000, ct).ConfigureAwait(false);
                }

                // Build Lua script snippet to inspect live widgets in K2 UI
                string luaQuery = BuildLiveUiLuaScript(widgetName, depth);
                _logger($"Dispatching Lua query over IPC (length: {luaQuery.Length} chars)...");

                string responseText = await _ipcBridge.ExecuteCommandAsync($"Script {luaQuery}", 3500, ct).ConfigureAwait(false);
                _logger($"Received raw IPC response ({responseText.Length} chars). Parsing widget data...");

                return ParseLiveUiResponse(responseText, widgetName);
            }

            return new K2UiInspectResult
            {
                Success = false,
                Mode = "live",
                ErrorMessage = "No active IPC bridge or TAO client connected to hon_x64.",
                Timestamp = _timeProvider.GetUtcNow().ToString("o")
            };
        }
        catch (Exception ex)
        {
            _logger($"[ERROR] Live UI inspection encountered exception: {ex.Message}");
            return new K2UiInspectResult
            {
                Success = false,
                Mode = "live",
                ErrorMessage = $"Live UI introspection failed: {ex.Message}",
                Timestamp = _timeProvider.GetUtcNow().ToString("o")
            };
        }
    }

    private static string BuildLiveUiLuaScript(string? widgetName, int depth)
    {
        string escapedWidget = string.IsNullOrWhiteSpace(widgetName) ? "" : widgetName.Trim().Replace("'", "\\'");
        int clampedDepth = Math.Clamp(depth, 1, 12);

        string taoInspectJson = string.IsNullOrWhiteSpace(escapedWidget)
            ? $"{{\"maxDepth\":{clampedDepth}}}"
            : $"{{\"widget\":\"{escapedWidget}\",\"maxDepth\":{clampedDepth}}}";

        return "if TAO and TAO.Dispatch then " +
               $" local res = TAO.Dispatch('inspect_ui', '{taoInspectJson.Replace("\"", "\\\"")}');" +
               " Echo('TAO_JSON|' .. tostring(res)); " +
               "else " +
               (string.IsNullOrWhiteSpace(escapedWidget)
                   ? "local ifaces={'main','game_lobby_v2','hosted_game','create','select_server'};" +
                     "local function dumpW(w,d)" +
                     $" if not w or d>{clampedDepth} then return end " +
                     " local t,m='','';" +
                     " pcall(function() t=w:GetTexture() or '' end);" +
                     " pcall(function() m=w:GetModel() or '' end);" +
                     " local typ='widget';" +
                     " pcall(function() typ=w:GetTypeName() or 'widget' end);" +
                     " local n,v,x,y,width,height='','true','0','0','0','0';" +
                     " pcall(function() n=w:GetName() or '' end);" +
                     " pcall(function() v=tostring(w:IsVisible()) end);" +
                     " pcall(function() x=tostring(w:GetAbsoluteX()) end);" +
                     " pcall(function() y=tostring(w:GetAbsoluteY()) end);" +
                     " pcall(function() width=tostring(w:GetWidth()) end);" +
                     " pcall(function() height=tostring(w:GetHeight()) end);" +
                     " Echo('UI_NODE|'..n..'|'..typ..'|'..v..'|'..x..'|'..y..'|'..width..'|'..height..'|'..tostring(t)..'|'..tostring(m));" +
                     " local nc=0; pcall(function() nc=w:GetNumChildren() or 0 end);" +
                     " if nc>0 then for i=0,math.min(nc-1,64) do local child=nil; pcall(function() child=w:GetChild(i) end); if child then dumpW(child,d+1) end end end " +
                     "end;" +
                     "local found=false;" +
                     "for _,iname in ipairs(ifaces) do " +
                     " local iface=UIManager.GetInterface(iname);" +
                     " if iface then " +
                     "  found=true; Echo('UI_DUMP_IFACE|'..iname);" +
                     "  dumpW(iface, 1);" +
                     " end " +
                     "end;" +
                     "if found then Echo('UI_DUMP_END'); else Echo('UI_DUMP_NO_ROOT'); end"
                   : "local ifaces={'main','game_lobby_v2','hosted_game','create','select_server'};" +
                     "local w=nil;" +
                     "for _,iname in ipairs(ifaces) do " +
                     " local iface=UIManager.GetInterface(iname);" +
                     $" if iface and iface.GetWidget then pcall(function() w=iface:GetWidget('{escapedWidget}') end); if w then break end end " +
                     "end;" +
                     "if w then " +
                     " local t,m='','';" +
                     " pcall(function() t=w:GetTexture() or '' end);" +
                     " pcall(function() m=w:GetModel() or '' end);" +
                     " local typ='widget';" +
                     " pcall(function() typ=w:GetTypeName() or 'widget' end);" +
                     " local n,v,x,y,width,height='','true','0','0','0','0';" +
                     " pcall(function() n=w:GetName() or '' end);" +
                     " pcall(function() v=tostring(w:IsVisible()) end);" +
                     " pcall(function() x=tostring(w:GetAbsoluteX()) end);" +
                     " pcall(function() y=tostring(w:GetAbsoluteY()) end);" +
                     " pcall(function() width=tostring(w:GetWidth()) end);" +
                     " pcall(function() height=tostring(w:GetHeight()) end);" +
                     " Echo('UI_DUMP_START|'..n..'|'..typ..'|'..v..'|'..x..'|'..y..'|'..width..'|'..height..'|'..tostring(t)..'|'..tostring(m)..'|UI_DUMP_END');" +
                     $"else Echo('UI_DUMP_NOT_FOUND|{escapedWidget}'); end") +
               " end";
    }

    private K2UiInspectResult ParseLiveUiResponse(string rawResponse, string? targetWidget)
    {
        List<K2WidgetNode> widgets = [];
        List<K2UiWarning> warnings = [];
        int total = 0;
        int visibleCount = 0;
        int missingCount = 0;
        int whiteCount = 0;

        // 1. Try parsing structured TAO JSON payload if present
        int taoJsonIdx = rawResponse.IndexOf("TAO_JSON|", StringComparison.Ordinal);
        if (taoJsonIdx >= 0)
        {
            string jsonText = rawResponse[(taoJsonIdx + 9)..].Trim();
            int lastBrace = jsonText.LastIndexOf('}');
            if (lastBrace > 0)
            {
                jsonText = jsonText.Substring(0, lastBrace + 1);
            }
            try
            {
                using var doc = JsonDocument.Parse(jsonText);
                var rootElem = doc.RootElement;
                if (rootElem.TryGetProperty("root", out JsonElement taoRoot))
                {
                    var parsedRoot = ParseTaoJsonNode(taoRoot, widgets, warnings, ref total, ref visibleCount, ref whiteCount);
                    if (parsedRoot != null)
                    {
                        return new K2UiInspectResult
                        {
                            Success = true,
                            Mode = "live",
                            SourcePath = "live://hon_x64/ui",
                            TotalWidgets = total,
                            VisibleWidgets = visibleCount,
                            WhiteFallbacksCount = whiteCount,
                            Warnings = warnings,
                            Root = parsedRoot,
                            Widgets = widgets,
                            Timestamp = _timeProvider.GetUtcNow().ToString("o")
                        };
                    }
                }
            }
            catch (Exception ex)
            {
                _logger($"Failed to parse TAO_JSON payload ({ex.Message}). Falling back to pipe parser.");
            }
        }

        string[] lines = rawResponse.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries);
        K2WidgetNode? root = null;

        foreach (string line in lines)
        {
            string trimmed = line.Trim();
            if (trimmed.StartsWith("UI_DUMP_START|") || trimmed.StartsWith("UI_NODE|"))
            {
                string[] parts = trimmed.Split('|');
                if (parts.Length >= 9)
                {
                    string name = parts[1];
                    string type = parts[2];
                    bool visible = bool.TryParse(parts[3], out bool v) ? v : parts[3] == "1";
                    string x = parts[4];
                    string y = parts[5];
                    string w = parts[6];
                    string h = parts[7];
                    string tex = parts.Length > 8 ? parts[8] : "";
                    string mdl = parts.Length > 9 ? parts[9] : "";

                    bool isWhite = string.Equals(tex, "$white", StringComparison.OrdinalIgnoreCase);
                    if (isWhite)
                    {
                        whiteCount++;
                        warnings.Add(new K2UiWarning
                        {
                            WidgetName = name,
                            WarningType = "WhiteFallback",
                            Message = $"Live widget '{name}' is rendering with fallback solid white texture ($white)."
                        });
                    }

                    var node = new K2WidgetNode
                    {
                        Name = name,
                        Type = type,
                        Visible = visible,
                        Texture = string.IsNullOrEmpty(tex) ? null : tex,
                        Model = string.IsNullOrEmpty(mdl) ? null : mdl,
                        X = x,
                        Y = y,
                        Width = w,
                        Height = h,
                        IsWhiteFallback = isWhite
                    };

                    root ??= node;
                    widgets.Add(node);
                    total++;
                    if (visible) visibleCount++;
                }
            }
            else if (trimmed.Contains("UI_DUMP_NOT_FOUND"))
            {
                warnings.Add(new K2UiWarning
                {
                    WidgetName = targetWidget ?? "unknown",
                    WarningType = "WidgetNotFound",
                    Message = $"Live widget '{targetWidget}' was not found in active K2 interface."
                });
            }
        }

        // If no parsed nodes but command executed successfully, construct an informational node
        if (widgets.Count == 0)
        {
            _logger($"[WARN] Could not parse structured UI nodes from live response: '{rawResponse}'. Creating summary node.");
            root = new K2WidgetNode
            {
                Name = targetWidget ?? "live_k2_root",
                Type = "interface",
                Visible = true,
                Properties = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                {
                    ["rawResponse"] = rawResponse
                }
            };
            widgets.Add(root);
            total = 1;
            visibleCount = 1;
        }

        return new K2UiInspectResult
        {
            Success = true,
            Mode = "live",
            SourcePath = "hon_x64 (Live Engine IPC)",
            TotalWidgets = total,
            VisibleWidgets = visibleCount,
            MissingTexturesCount = missingCount,
            WhiteFallbacksCount = whiteCount,
            Warnings = warnings,
            Root = root,
            Widgets = widgets,
            Timestamp = _timeProvider.GetUtcNow().ToString("o")
        };
    }

    private static K2WidgetNode? ParseTaoJsonNode(
        JsonElement elem,
        List<K2WidgetNode> allWidgets,
        List<K2UiWarning> warnings,
        ref int total,
        ref int visibleCount,
        ref int whiteCount)
    {
        if (elem.ValueKind != JsonValueKind.Object) return null;

        string name = elem.TryGetProperty("name", out var nEl) ? nEl.GetString() ?? "" : "";
        string type = elem.TryGetProperty("type", out var tEl) ? tEl.GetString() ?? "widget" : "widget";
        bool visible = elem.TryGetProperty("visible", out var vEl) && vEl.GetBoolean();
        string? tex = elem.TryGetProperty("texture", out var txEl) ? txEl.GetString() : null;
        string? mdl = elem.TryGetProperty("model", out var mEl) ? mEl.GetString() : null;

        string x = "0", y = "0", w = "0", h = "0";
        if (elem.TryGetProperty("bounds", out var bEl) && bEl.ValueKind == JsonValueKind.Object)
        {
            if (bEl.TryGetProperty("x", out var xEl)) x = xEl.ToString();
            if (bEl.TryGetProperty("y", out var yEl)) y = yEl.ToString();
            if (bEl.TryGetProperty("width", out var wEl)) w = wEl.ToString();
            if (bEl.TryGetProperty("height", out var hEl)) h = hEl.ToString();
        }

        bool isWhite = string.Equals(tex, "$white", StringComparison.OrdinalIgnoreCase);
        if (isWhite)
        {
            whiteCount++;
            warnings.Add(new K2UiWarning
            {
                WidgetName = name,
                WarningType = "WhiteFallback",
                Message = $"Live widget '{name}' is rendering with fallback solid white texture ($white)."
            });
        }

        var node = new K2WidgetNode
        {
            Name = name,
            Type = type,
            Visible = visible,
            Texture = tex,
            Model = mdl,
            X = x,
            Y = y,
            Width = w,
            Height = h,
            IsWhiteFallback = isWhite
        };

        allWidgets.Add(node);
        total++;
        if (visible) visibleCount++;

        if (elem.TryGetProperty("children", out var chEl) && chEl.ValueKind == JsonValueKind.Array)
        {
            foreach (var childElem in chEl.EnumerateArray())
            {
                var childNode = ParseTaoJsonNode(childElem, allWidgets, warnings, ref total, ref visibleCount, ref whiteCount);
                if (childNode != null)
                {
                    node.Children.Add(childNode);
                }
            }
        }

        return node;
    }

    #endregion

    #region Static XML Parsing & Template Expansion

    public K2UiInspectResult InspectStaticUi(
        string? interfaceOrPackagePath = null,
        string? widgetName = null,
        int depth = 10,
        string? gameDirectory = null)
    {
        _logger($"InspectStaticUi executing. Path: '{interfaceOrPackagePath ?? "default"}', Widget: '{widgetName ?? "all"}', Depth: {depth}");

        string repoRoot = FindRepoRoot(Environment.CurrentDirectory);
        string gameDir = gameDirectory ?? Path.Combine(repoRoot, "games", "client");

        if (!TryResolveInterfaceFile(interfaceOrPackagePath, repoRoot, out string? resolvedFilePath))
        {
            string err = $"Unable to locate K2 interface or package file '{interfaceOrPackagePath ?? "default"}'. Searched override, game, and shared UI folders.";
            _logger($"[ERROR] {err}");
            return new K2UiInspectResult
            {
                Success = false,
                Mode = "static",
                ErrorMessage = err,
                Timestamp = _timeProvider.GetUtcNow().ToString("o")
            };
        }

        _logger($"Resolved static UI file: '{resolvedFilePath}'");

        try
        {
            // 1. Collect all templates from the target file and any included packages
            Dictionary<string, XElement> templates = new(StringComparer.OrdinalIgnoreCase);
            HashSet<string> visitedFiles = new(StringComparer.OrdinalIgnoreCase);

            XDocument doc = LoadSanitizedXml(resolvedFilePath);
            visitedFiles.Add(resolvedFilePath);

            CollectTemplatesRecursive(doc.Root, Path.GetDirectoryName(resolvedFilePath)!, repoRoot, templates, visitedFiles);
            _logger($"Collected {templates.Count} templates across package includes.");

            // 2. Parse widget hierarchy starting from root
            List<K2UiWarning> warnings = [];
            K2WidgetNode rootNode = ParseElementHierarchy(
                doc.Root ?? new XElement("root"),
                templates,
                repoRoot,
                warnings,
                currentDepth: 0,
                maxDepth: depth);

            // If standalone package only contains templates, expand template contents into root node
            if (rootNode.Children.Count == 0 && templates.Count > 0)
            {
                foreach (var kvp in templates)
                {
                    _logger($"Auto-expanding standalone template '{kvp.Key}' into root node...");
                    foreach (XElement templateChild in kvp.Value.Elements())
                    {
                        ProcessChildElement(templateChild, rootNode, templates, repoRoot, warnings, 1, depth);
                    }
                }
            }

            // 3. Filter or flatten
            List<K2WidgetNode> flattened = [];
            FlattenWidgetTree(rootNode, flattened);

            K2WidgetNode selectedRoot = rootNode;
            if (!string.IsNullOrWhiteSpace(widgetName))
            {
                string cleanTarget = widgetName.Trim();
                K2WidgetNode? matched = flattened.FirstOrDefault(w =>
                    w.Name.Equals(cleanTarget, StringComparison.OrdinalIgnoreCase))
                    ?? flattened.FirstOrDefault(w =>
                    w.Name.Contains(cleanTarget, StringComparison.OrdinalIgnoreCase));

                if (matched != null)
                {
                    selectedRoot = matched;
                    _logger($"Matched specific widget target '{widgetName}' -> '{matched.Name}' ({matched.Type})");
                }
                else
                {
                    warnings.Add(new K2UiWarning
                    {
                        WidgetName = widgetName,
                        WarningType = "WidgetNotFound",
                        Message = $"Widget '{widgetName}' was not found in static file '{resolvedFilePath}'."
                    });
                }
            }

            int total = flattened.Count;
            int visibleCount = flattened.Count(w => w.Visible);
            int missingCount = flattened.Count(w => w.IsMissingTexture);
            int whiteCount = flattened.Count(w => w.IsWhiteFallback);

            _logger($"Static inspection completed. Total widgets: {total}, Visible: {visibleCount}, Missing textures: {missingCount}, White fallbacks: {whiteCount}, Warnings: {warnings.Count}");

            return new K2UiInspectResult
            {
                Success = true,
                Mode = "static",
                SourcePath = resolvedFilePath,
                TotalWidgets = total,
                VisibleWidgets = visibleCount,
                MissingTexturesCount = missingCount,
                WhiteFallbacksCount = whiteCount,
                Warnings = warnings,
                Root = selectedRoot,
                Widgets = flattened,
                Timestamp = _timeProvider.GetUtcNow().ToString("o")
            };
        }
        catch (Exception ex)
        {
            _logger($"[ERROR] Static XML inspection failed: {ex.Message}");
            return new K2UiInspectResult
            {
                Success = false,
                Mode = "static",
                SourcePath = resolvedFilePath,
                ErrorMessage = $"Failed to parse K2 XML file '{resolvedFilePath}': {ex.Message}",
                Timestamp = _timeProvider.GetUtcNow().ToString("o")
            };
        }
    }

    private void CollectTemplatesRecursive(
        XElement? root,
        string currentDir,
        string repoRoot,
        Dictionary<string, XElement> templates,
        HashSet<string> visitedFiles)
    {
        if (root == null) return;

        // 1. Gather all <template> definitions in this document
        foreach (XElement tmpl in root.DescendantsAndSelf("template"))
        {
            string? name = tmpl.Attribute("name")?.Value;
            if (!string.IsNullOrWhiteSpace(name) && !templates.ContainsKey(name))
            {
                templates[name] = tmpl;
            }
        }

        // 2. Discover and traverse <include file="..." /> directives
        foreach (XElement inc in root.DescendantsAndSelf("include"))
        {
            string? fileAttr = inc.Attribute("file")?.Value;
            if (string.IsNullOrWhiteSpace(fileAttr)) continue;

            if (TryResolveIncludedFile(fileAttr, currentDir, repoRoot, out string? incPath) &&
                visitedFiles.Add(incPath))
            {
                try
                {
                    XDocument incDoc = LoadSanitizedXml(incPath);
                    CollectTemplatesRecursive(incDoc.Root, Path.GetDirectoryName(incPath)!, repoRoot, templates, visitedFiles);
                }
                catch (Exception ex)
                {
                    _logger($"[WARN] Failed to parse included file '{incPath}': {ex.Message}");
                }
            }
        }
    }

    private K2WidgetNode ParseElementHierarchy(
        XElement element,
        Dictionary<string, XElement> templates,
        string repoRoot,
        List<K2UiWarning> warnings,
        int currentDepth,
        int maxDepth)
    {
        string tagName = element.Name.LocalName.ToLowerInvariant();
        string name = element.Attribute("name")?.Value ?? element.Attribute("id")?.Value ?? string.Empty;
        string? texture = element.Attribute("texture")?.Value ?? ExtractTextureFromHook(element);
        string? model = element.Attribute("model")?.Value ?? ExtractModelFromHook(element);

        // Visibility default is true unless visible="0" or visible="false"
        string? visAttr = element.Attribute("visible")?.Value;
        bool visible = true;
        if (!string.IsNullOrWhiteSpace(visAttr))
        {
            visible = !string.Equals(visAttr, "0", StringComparison.OrdinalIgnoreCase) &&
                      !string.Equals(visAttr, "false", StringComparison.OrdinalIgnoreCase);
        }

        bool isWhiteFallback = false;
        bool isMissingTexture = false;
        string? resolvedDiskTexture = null;

        // Texture verification
        if (!string.IsNullOrWhiteSpace(texture))
        {
            if (string.Equals(texture, "$white", StringComparison.OrdinalIgnoreCase))
            {
                isWhiteFallback = true;
                warnings.Add(new K2UiWarning
                {
                    WidgetName = string.IsNullOrEmpty(name) ? $"[{tagName}]" : name,
                    WarningType = "WhiteFallback",
                    Message = $"Widget '{name}' uses solid white fallback texture ($white).",
                    Path = texture
                });
            }
            else if (!string.Equals(texture, "$invis", StringComparison.OrdinalIgnoreCase) &&
                     !string.Equals(texture, "$black", StringComparison.OrdinalIgnoreCase))
            {
                if (TryResolveTextureFile(texture, repoRoot, out string? diskPath))
                {
                    resolvedDiskTexture = diskPath;
                }
                else
                {
                    isMissingTexture = true;
                    warnings.Add(new K2UiWarning
                    {
                        WidgetName = string.IsNullOrEmpty(name) ? $"[{tagName}]" : name,
                        WarningType = "MissingTexture",
                        Message = $"Widget '{name}' references texture '{texture}' which was not found in VFS mount paths.",
                        Path = texture
                    });
                }
            }
        }

        Dictionary<string, string> properties = new(StringComparer.OrdinalIgnoreCase);
        foreach (XAttribute attr in element.Attributes())
        {
            properties[attr.Name.LocalName] = attr.Value;
        }

        K2WidgetNode node = new()
        {
            Name = name,
            Type = tagName,
            Visible = visible,
            Texture = texture,
            Model = model,
            X = element.Attribute("x")?.Value,
            Y = element.Attribute("y")?.Value,
            Width = element.Attribute("width")?.Value,
            Height = element.Attribute("height")?.Value,
            Align = element.Attribute("align")?.Value,
            Valign = element.Attribute("valign")?.Value,
            Color = element.Attribute("color")?.Value,
            Noclick = string.Equals(element.Attribute("noclick")?.Value, "1", StringComparison.OrdinalIgnoreCase) ||
                      string.Equals(element.Attribute("noclick")?.Value, "true", StringComparison.OrdinalIgnoreCase),
            IsWhiteFallback = isWhiteFallback,
            IsMissingTexture = isMissingTexture,
            ResolvedTextureDiskPath = resolvedDiskTexture,
            Properties = properties
        };

        if (currentDepth >= maxDepth)
        {
            return node;
        }

        // Traverse child elements, handling <instance>, <if>, and standard child widgets
        foreach (XElement child in element.Elements())
        {
            string childTag = child.Name.LocalName.ToLowerInvariant();

            // Ignore non-widget meta tags
            if (childTag is "template" or "include" or "lua" or "trigger" or "alias" or "script" or "constants")
            {
                continue;
            }

            // Handle <if condition="..."> unwrap
            if (childTag == "if")
            {
                foreach (XElement ifChild in child.Elements())
                {
                    ProcessChildElement(ifChild, node, templates, repoRoot, warnings, currentDepth + 1, maxDepth);
                }
                continue;
            }

            // Handle <instance name="TemplateName" ... />
            if (childTag == "instance")
            {
                ExpandInstance(child, node, templates, repoRoot, warnings, currentDepth + 1, maxDepth);
                continue;
            }

            // Standard widget element
            ProcessChildElement(child, node, templates, repoRoot, warnings, currentDepth + 1, maxDepth);
        }

        return node;
    }

    private void ProcessChildElement(
        XElement element,
        K2WidgetNode parent,
        Dictionary<string, XElement> templates,
        string repoRoot,
        List<K2UiWarning> warnings,
        int currentDepth,
        int maxDepth)
    {
        string tag = element.Name.LocalName.ToLowerInvariant();
        if (tag is "template" or "include" or "lua" or "trigger" or "alias" or "script")
            return;

        if (tag == "instance")
        {
            ExpandInstance(element, parent, templates, repoRoot, warnings, currentDepth, maxDepth);
            return;
        }

        if (tag == "if")
        {
            foreach (XElement ifChild in element.Elements())
            {
                ProcessChildElement(ifChild, parent, templates, repoRoot, warnings, currentDepth, maxDepth);
            }
            return;
        }

        K2WidgetNode childNode = ParseElementHierarchy(element, templates, repoRoot, warnings, currentDepth, maxDepth);
        parent.Children.Add(childNode);
    }

    private void ExpandInstance(
        XElement instanceElement,
        K2WidgetNode parent,
        Dictionary<string, XElement> templates,
        string repoRoot,
        List<K2UiWarning> warnings,
        int currentDepth,
        int maxDepth)
    {
        string? tmplName = instanceElement.Attribute("name")?.Value ?? instanceElement.Attribute("template")?.Value;
        if (string.IsNullOrWhiteSpace(tmplName)) return;

        if (!templates.TryGetValue(tmplName, out XElement? templateDef))
        {
            warnings.Add(new K2UiWarning
            {
                WidgetName = tmplName,
                WarningType = "MissingTemplate",
                Message = $"<instance> requested template '{tmplName}', but template definition was not found."
            });
            return;
        }

        // Map parameter replacements from instance attributes
        Dictionary<string, string> paramMap = new(StringComparer.OrdinalIgnoreCase);
        foreach (XAttribute attr in instanceElement.Attributes())
        {
            paramMap[attr.Name.LocalName] = attr.Value;
        }

        // Deep clone template and substitute parameters
        foreach (XElement tmplChild in templateDef.Elements())
        {
            XElement substituted = SubstituteParametersInElement(tmplChild, paramMap);
            ProcessChildElement(substituted, parent, templates, repoRoot, warnings, currentDepth, maxDepth);
        }
    }

    private static XElement SubstituteParametersInElement(XElement original, Dictionary<string, string> paramMap)
    {
        XElement clone = new(original.Name);

        foreach (XAttribute attr in original.Attributes())
        {
            string val = SubstituteString(attr.Value, paramMap);
            clone.SetAttributeValue(attr.Name, val);
        }

        foreach (XNode node in original.Nodes())
        {
            if (node is XElement childElem)
            {
                clone.Add(SubstituteParametersInElement(childElem, paramMap));
            }
            else if (node is XText textNode)
            {
                clone.Add(new XText(SubstituteString(textNode.Value, paramMap)));
            }
        }

        return clone;
    }

    private static string SubstituteString(string input, Dictionary<string, string> paramMap)
    {
        if (string.IsNullOrEmpty(input) || !input.Contains('{')) return input;

        return ParamPlaceholderRegex.Replace(input, match =>
        {
            string key = match.Groups[1].Value;
            string? defaultVal = match.Groups[2].Success ? match.Groups[2].Value : null;

            if (paramMap.TryGetValue(key, out string? value))
            {
                return value;
            }

            return defaultVal ?? string.Empty;
        });
    }

    private static void FlattenWidgetTree(K2WidgetNode node, List<K2WidgetNode> list)
    {
        list.Add(node);
        foreach (K2WidgetNode child in node.Children)
        {
            FlattenWidgetTree(child, list);
        }
    }

    private static string? ExtractTextureFromHook(XElement element)
    {
        string? onload = element.Attribute("onload")?.Value ?? element.Attribute("ontrigger")?.Value ?? element.Attribute("ontriggerlua")?.Value;
        if (string.IsNullOrWhiteSpace(onload)) return null;

        Match m = TextureHookRegex.Match(onload);
        return m.Success ? m.Groups[1].Value : null;
    }

    private static string? ExtractModelFromHook(XElement element)
    {
        string? onload = element.Attribute("onload")?.Value ?? element.Attribute("ontrigger")?.Value ?? element.Attribute("ontriggerlua")?.Value;
        if (string.IsNullOrWhiteSpace(onload)) return null;

        Match m = ModelHookRegex.Match(onload);
        return m.Success ? m.Groups[1].Value : null;
    }

    private static XDocument LoadSanitizedXml(string filePath)
    {
        string raw = File.ReadAllText(filePath, Encoding.UTF8);

        // Strip UTF-8 BOM if present
        if (raw.StartsWith('\uFEFF'))
        {
            raw = raw[1..];
        }

        try
        {
            return XDocument.Parse(raw, LoadOptions.PreserveWhitespace);
        }
        catch
        {
            // Sanitize unescaped ampersands common in K2 scripting strings inside XML attributes
            string sanitized = UnescapedAmpersandRegex.Replace(raw, "&amp;");
            return XDocument.Parse(sanitized, LoadOptions.PreserveWhitespace);
        }
    }

    #endregion

    #region Path & VFS Resolvers

    public static bool TryResolveInterfaceFile(string? pathQuery, string repoRoot, [NotNullWhen(true)] out string? resolvedPath)
    {
        resolvedPath = null;

        // 1. Direct explicit disk path
        if (!string.IsNullOrWhiteSpace(pathQuery))
        {
            string expanded = Environment.ExpandEnvironmentVariables(pathQuery.Trim());
            if (File.Exists(expanded))
            {
                resolvedPath = Path.GetFullPath(expanded);
                return true;
            }
            if (File.Exists(Path.Combine(repoRoot, expanded)))
            {
                resolvedPath = Path.GetFullPath(Path.Combine(repoRoot, expanded));
                return true;
            }
        }

        // 2. Candidate default files (main_v2.interface, main.interface, etc.)
        List<string> searchNames = [];
        if (!string.IsNullOrWhiteSpace(pathQuery))
        {
            string clean = pathQuery.Trim().TrimStart('/', '\\');
            searchNames.Add(clean);
            string fn = Path.GetFileName(clean);
            if (!searchNames.Contains(fn, StringComparer.OrdinalIgnoreCase))
            {
                searchNames.Add(fn);
            }
            if (!clean.EndsWith(".interface", StringComparison.OrdinalIgnoreCase) && !clean.EndsWith(".package", StringComparison.OrdinalIgnoreCase))
            {
                searchNames.Add($"{clean}_v2.interface");
                searchNames.Add($"{clean}.interface");
                searchNames.Add($"{clean}_v2.package");
                searchNames.Add($"{clean}.package");
                searchNames.Add($"{fn}_v2.interface");
                searchNames.Add($"{fn}.interface");
                searchNames.Add($"{fn}_v2.package");
                searchNames.Add($"{fn}.package");
            }
        }
        else
        {
            searchNames.AddRange([
                "main_v2.interface",
                "main.interface",
                "game_apex.interface",
                "game.interface",
                "background_v2.package",
                "background.package"
            ]);
        }

        string[] candidateBases =
        [
            Path.Combine(repoRoot, "games", "client", "override", "ui", "fe2"),
            Path.Combine(repoRoot, "games", "client", "override", "ui"),
            Path.Combine(repoRoot, "games", "client", "game", "ui"),
            Path.Combine(repoRoot, "games", "client", "ClientPackageFiles", "ui", "fe2"),
            Path.Combine(repoRoot, "games", "client", "ClientPackageFiles", "ui"),
            Path.Combine(repoRoot, "games", "shared", "ui", "fe2"),
            Path.Combine(repoRoot, "games", "shared", "ui"),
            Path.Combine(repoRoot, "games", "client")
        ];

        // 1. Fast direct file check
        foreach (string name in searchNames)
        {
            foreach (string baseDir in candidateBases)
            {
                if (!Directory.Exists(baseDir)) continue;

                string direct = Path.Combine(baseDir, name);
                if (File.Exists(direct))
                {
                    resolvedPath = direct;
                    return true;
                }
            }
        }

        // 2. Targeted search within games/client/override and games/client/game
        string[] searchBases =
        [
            Path.Combine(repoRoot, "games", "client", "override", "ui"),
            Path.Combine(repoRoot, "games", "client", "game", "ui"),
            Path.Combine(repoRoot, "games", "shared", "ui")
        ];

        foreach (string name in searchNames)
        {
            string fileName = Path.GetFileName(name);
            foreach (string searchDir in searchBases)
            {
                if (!Directory.Exists(searchDir)) continue;
                try
                {
                    string[] matches = Directory.GetFiles(searchDir, fileName, SearchOption.AllDirectories);
                    if (matches.Length > 0)
                    {
                        resolvedPath = matches[0];
                        return true;
                    }
                }
                catch { }
            }
        }

        return false;
    }

    private static readonly ConcurrentDictionary<string, string?> IncludedFileCache = new(StringComparer.OrdinalIgnoreCase);
    private static readonly ConcurrentDictionary<string, string?> TextureFileCache = new(StringComparer.OrdinalIgnoreCase);

    public static bool TryResolveIncludedFile(string includeAttr, string currentDir, string repoRoot, [NotNullWhen(true)] out string? resolvedPath)
    {
        string cacheKey = $"{currentDir}::{includeAttr}";
        if (IncludedFileCache.TryGetValue(cacheKey, out resolvedPath) && resolvedPath != null)
        {
            return true;
        }

        resolvedPath = null;
        string clean = includeAttr.Trim().Replace('\\', '/').TrimStart('/');

        // 1. Relative to current file directory
        string rel = Path.Combine(currentDir, Path.GetFileName(clean));
        if (File.Exists(rel))
        {
            resolvedPath = rel;
            IncludedFileCache[cacheKey] = rel;
            return true;
        }

        // 2. Search VFS mount paths
        string[] vfsMounts =
        [
            Path.Combine(repoRoot, "games", "client", "override"),
            Path.Combine(repoRoot, "games", "client", "game"),
            Path.Combine(repoRoot, "games", "client", "ClientPackageFiles"),
            Path.Combine(repoRoot, "games", "shared"),
            Path.Combine(repoRoot, "games", "client")
        ];

        string fn = Path.GetFileName(clean);
        foreach (string mount in vfsMounts)
        {
            if (!Directory.Exists(mount)) continue;

            string candidate = Path.Combine(mount, clean);
            if (File.Exists(candidate))
            {
                resolvedPath = candidate;
                IncludedFileCache[cacheKey] = candidate;
                return true;
            }

            string candUi = Path.Combine(mount, "ui", clean);
            if (File.Exists(candUi))
            {
                resolvedPath = candUi;
                IncludedFileCache[cacheKey] = candUi;
                return true;
            }

            string candFe2 = Path.Combine(mount, "ui", "fe2", fn);
            if (File.Exists(candFe2))
            {
                resolvedPath = candFe2;
                IncludedFileCache[cacheKey] = candFe2;
                return true;
            }

            string candNewUi = Path.Combine(mount, "ui", "fe2", "newui", fn);
            if (File.Exists(candNewUi))
            {
                resolvedPath = candNewUi;
                IncludedFileCache[cacheKey] = candNewUi;
                return true;
            }

            string candUiFn = Path.Combine(mount, "ui", fn);
            if (File.Exists(candUiFn))
            {
                resolvedPath = candUiFn;
                IncludedFileCache[cacheKey] = candUiFn;
                return true;
            }
        }

        IncludedFileCache[cacheKey] = null;
        return false;
    }

    private static readonly ConcurrentDictionary<string, HashSet<string>> S2zEntriesCache = new(StringComparer.OrdinalIgnoreCase);

    private static HashSet<string> GetS2zEntries(string repoRoot)
    {
        return S2zEntriesCache.GetOrAdd(repoRoot, root =>
        {
            HashSet<string> set = new(StringComparer.OrdinalIgnoreCase);
            try
            {
                string[] searchRoots =
                [
                    Path.Combine(root, "games", "client"),
                    Path.Combine(root, "games", "shared")
                ];

                foreach (string sRoot in searchRoots)
                {
                    if (!Directory.Exists(sRoot)) continue;
                    foreach (string s2z in Directory.EnumerateFiles(sRoot, "*.s2z", SearchOption.AllDirectories))
                    {
                        try
                        {
                            using ZipArchive zip = ZipFile.OpenRead(s2z);
                            foreach (ZipArchiveEntry entry in zip.Entries)
                            {
                                string entryName = entry.FullName.Replace('\\', '/');
                                set.Add(entryName);
                                // Strip 8-digit mip/resolution folder e.g. 00000000/ or 00000001/
                                int slashIdx = entryName.IndexOf('/');
                                if (slashIdx == 8 && char.IsDigit(entryName[0]))
                                {
                                    set.Add(entryName[(slashIdx + 1)..]);
                                }
                            }
                        }
                        catch { }
                    }
                }
            }
            catch { }
            return set;
        });
    }

    public static bool TryResolveTextureFile(string texturePath, string repoRoot, [NotNullWhen(true)] out string? resolvedDiskPath)
    {
        string cacheKey = $"{repoRoot}::{texturePath}";
        if (TextureFileCache.TryGetValue(cacheKey, out resolvedDiskPath) && resolvedDiskPath != null)
        {
            return true;
        }

        resolvedDiskPath = null;
        string clean = texturePath.Trim().Replace('\\', '/').TrimStart('/');

        string[] vfsMounts =
        [
            Path.Combine(repoRoot, "games", "client", "override"),
            Path.Combine(repoRoot, "games", "client", "game"),
            Path.Combine(repoRoot, "games", "client", "ClientPackageFiles"),
            Path.Combine(repoRoot, "games", "shared"),
            Path.Combine(repoRoot, "games", "client", "KONGOR", "updates"),
            Path.Combine(repoRoot, "games", "client", "KONGOR", "extensions"),
            Path.Combine(repoRoot, "games", "client")
        ];

        string[] extensions = [Path.GetExtension(clean), ".tga", ".dds", ".png"];
        string fnNoExt = Path.GetFileNameWithoutExtension(clean);
        string[] frameSuffixes = ["", "_c", "_tl", "_t", "_tr", "_l", "_r", "_bl", "_b", "_br"];

        foreach (string mount in vfsMounts)
        {
            if (!Directory.Exists(mount)) continue;

            // Direct path check
            string direct = Path.Combine(mount, clean);
            if (File.Exists(direct))
            {
                resolvedDiskPath = direct;
                TextureFileCache[cacheKey] = direct;
                return true;
            }

            // Extension permutations
            string baseNoExt = Path.Combine(Path.GetDirectoryName(direct) ?? mount, fnNoExt);
            foreach (string ext in extensions)
            {
                if (string.IsNullOrEmpty(ext)) continue;
                foreach (string sfx in frameSuffixes)
                {
                    string candidate = baseNoExt + sfx + ext;
                    if (File.Exists(candidate))
                    {
                        resolvedDiskPath = candidate;
                        TextureFileCache[cacheKey] = candidate;
                        return true;
                    }
                }
            }

            // Targeted UI prefixes
            string[] prefixDirs =
            [
                Path.Combine(mount, "ui", clean),
                Path.Combine(mount, "ui", "fe2", fnNoExt),
                Path.Combine(mount, "ui", "fe2", "elements", fnNoExt),
                Path.Combine(mount, "ui", "fe2", "mainmenu", fnNoExt),
                Path.Combine(mount, "ui", "elements", fnNoExt),
                Path.Combine(mount, "ui", "icons", fnNoExt),
                Path.Combine(mount, "core", "textures", fnNoExt)
            ];

            foreach (string pfx in prefixDirs)
            {
                foreach (string ext in extensions)
                {
                    if (string.IsNullOrEmpty(ext)) continue;
                    foreach (string sfx in frameSuffixes)
                    {
                        string candidate = pfx + sfx + ext;
                        if (File.Exists(candidate))
                        {
                            resolvedDiskPath = candidate;
                            TextureFileCache[cacheKey] = candidate;
                            return true;
                        }
                    }
                }
            }
        }

        // Check inside mounted .s2z packages
        HashSet<string> s2zEntries = GetS2zEntries(repoRoot);
        if (s2zEntries.Count > 0)
        {
            string extExisting = Path.GetExtension(clean);
            string cleanNoExt = string.IsNullOrEmpty(extExisting) ? clean : clean[..^extExisting.Length];
            string[] s2zExts = [".dds", ".tga", ".png"];

            string[] s2zPrefixes = ["", "ui/", "ui/fe2/", "ui/common/", "ui/elements/", "ui/icons/", "textures/", "core/textures/"];

            foreach (string ext in s2zExts)
            {
                foreach (string sfx in frameSuffixes)
                {
                    string cand = cleanNoExt + sfx + ext;
                    foreach (string pfx in s2zPrefixes)
                    {
                        string testPath = string.IsNullOrEmpty(pfx) ? cand : (cand.StartsWith(pfx, StringComparison.OrdinalIgnoreCase) ? cand : pfx + cand);
                        if (s2zEntries.Contains(testPath))
                        {
                            resolvedDiskPath = $"[VFS:.s2z]/{testPath}";
                            TextureFileCache[cacheKey] = resolvedDiskPath;
                            return true;
                        }
                    }
                }
            }
        }

        TextureFileCache[cacheKey] = null;
        return false;
    }

    private static bool IsGameClientRunning()
    {
        try
        {
            string[] names = ["hon_x64", "hon"];
            foreach (string n in names)
            {
                Process[] procs = Process.GetProcessesByName(n);
                if (procs.Length > 0) return true;
            }
        }
        catch { }

        return false;
    }

    public static string FindRepoRoot(string? startingDir = null)
    {
        if (Kongor.Core.Configuration.KongorWorkspaceResolver.TryResolve(
                out var ws,
                new Kongor.Core.Configuration.WorkspaceResolveOptions
                {
                    InvocationCwd = !string.IsNullOrWhiteSpace(startingDir) ? startingDir : Directory.GetCurrentDirectory()
                }) && ws is not null)
        {
            return ws.Root;
        }

        if (!string.IsNullOrWhiteSpace(startingDir))
        {
            DirectoryInfo? current = new(startingDir);
            while (current != null)
            {
                if (Directory.Exists(Path.Combine(current.FullName, "games", "client", "override", "ui")))
                {
                    return current.FullName;
                }
                current = current.Parent;
            }
        }

        return Directory.GetCurrentDirectory();
    }

    #endregion
}
