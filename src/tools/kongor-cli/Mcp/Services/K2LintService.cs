// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// TAO (Together As One) MCP & Kongor CLI -- Created by guccidonut (https://github.com/guccidonut/PKV)

using System.Text;
using System.Text.RegularExpressions;
using System.Xml;

namespace Kongor.Cli.Mcp.Services;

/// <summary>
/// Structured issue returned by K2LintService.
/// </summary>
public sealed record K2LintIssue
{
    public string File { get; init; } = string.Empty;
    public int Line { get; init; }
    public int Column { get; init; }
    public string Severity { get; init; } = "error"; // "error", "warning"
    public string Code { get; init; } = string.Empty;
    public string Message { get; init; } = string.Empty;
    public string? Snippet { get; init; }
    public string? RemediationHint { get; init; }
}

/// <summary>
/// Comprehensive report returned by K2LintService.
/// </summary>
public sealed record K2LintReport
{
    public string Status { get; init; } = "success";
    public string TargetPath { get; init; } = string.Empty;
    public int FilesScanned { get; init; }
    public int ErrorCount { get; init; }
    public int WarningCount { get; init; }
    public int CleanFilesCount { get; init; }
    public IReadOnlyList<K2LintIssue> Issues { get; init; } = [];
    public IReadOnlyList<string> FixedBomFiles { get; init; } = [];
    public string Timestamp { get; init; } = string.Empty;
}

/// <summary>
/// Interface for K2 XML & Lua static analysis.
/// </summary>
public interface IK2LintService
{
    Task<K2LintReport> LintAsync(
        string targetPath,
        bool recursive = true,
        bool fixBom = false,
        string severityThreshold = "warning",
        CancellationToken ct = default);
}

/// <summary>
/// Fast offline static linter for K2 XML packages (.package, .interface) and Lua scripts (.lua).
/// Validates UTF-8 BOM, XML well-formedness, unescaped entities, template integrity,
/// Lua syntax grammar, and K2 engine Lua API compliance without requiring a graphical window.
/// </summary>
public sealed class K2LintService : IK2LintService
{
    private static readonly HashSet<string> KnownK2Tags = new(StringComparer.OrdinalIgnoreCase)
    {
        "package", "interface", "panel", "frame", "image", "label", "button",
        "textbox", "combobox", "template", "instance", "include", "script",
        "widget", "floater", "listbox", "slider", "checkbox", "tableweb",
        "web", "pie", "worldimage", "model", "effect", "trigger", "listener",
        "keyhook", "sound", "userinterface", "anchor", "lua", "childinterface",
        "if", "widgetstate", "modelpanel", "movie", "alias", "constants",
        "effectpanel", "animatedimage", "chatoutput", "chatinput", "textbuffer",
        "scrollbar", "dirlist", "filelist", "map", "minimap", "avatar", "hero",
        "item", "ability", "state", "modifier", "cinematic", "swatch", "graph"
    };

    private static readonly HashSet<string> LuaEventAttributes = new(StringComparer.OrdinalIgnoreCase)
    {
        "onlua", "oneventlua", "onclicklua", "ondoubleclicklua", "onrightclicklua",
        "onshowlua", "onhidelua", "onloadlua", "onchangelua", "onrefreshlua",
        "onfocuslua", "onlosefocuslua", "onmouseoverlua", "onmouseoutlua",
        "onenterlua", "onwakelua", "onsleeplua", "onframelua"
    };

    private static readonly HashSet<string> K2UiCmdEventAttributes = new(StringComparer.OrdinalIgnoreCase)
    {
        "onevent", "onevent0", "onevent1", "onevent2", "onevent3", "onevent4",
        "onclick", "ondoubleclick", "onrightclick", "onshow", "onhide", "onload",
        "onchange", "onrefresh", "onfocus", "onlosefocus", "watch", "onmouseover",
        "onmouseout", "onenter", "onwake", "onsleep"
    };

    private static readonly Regex LuaHeuristicInAttributeRegex = new(
        @"(?:\blocal\s+[a-zA-Z_]|\bfunction\s*\(|\bthen\b|\bUIManager\s*[\.\:]|\bWindows\s*[\.\:]|:GetWidget\s*\(|:DoEvent\s*\()",
        RegexOptions.Compiled);

    private static readonly byte[] Utf8Bom = [0xEF, 0xBB, 0xBF];
    private static readonly Regex UnescapedAmpRegex = new(@"&(?!(amp|lt|gt|quot|apos|#\d+|#x[0-9a-fA-F]+);)", RegexOptions.Compiled);
    private static readonly Regex WindowsGlobalRegex = new(@"\bWindows\b\s*(\.|\:)", RegexOptions.Compiled);
    private static readonly Regex DoEventStringArgRegex = new(@"\:DoEvent\s*\(\s*['""][^'""]*['""]\s*\)", RegexOptions.Compiled);
    private static readonly Regex LuaInvalidOpRegex = new(@"(?<!~)(!=)|(&&)|(\|\|)", RegexOptions.Compiled);
    private static readonly Regex FunctionArithmeticRegex = new(
        @"(?:\bHostTime\b\s*[\+\-\*\/%^]|[\+\-\*\/%^]\s*\bHostTime\b(?!\s*\()|\(\s*HostTime\s+or\s+[^\)]+\)\s*[\+\-\*\/%^])",
        RegexOptions.Compiled);
    private static readonly Regex LuaLiteralOrCommentRegex = new(
        @"(?<c1>--\[(?<eq1>=*)\[[\s\S]*?\]\k<eq1>\])|(?<c2>--[^\r\n]*)|(?<s1>\[(?<eq2>=*)\[[\s\S]*?\]\k<eq2>\])|(?<s2>""([^""\\]|\\.)*"")|(?<s3>'([^'\\]|\\.)*')",
        RegexOptions.Compiled);
    private static readonly Regex LuaBlockOpenRegex = new(@"\b(if|do|function)\b", RegexOptions.Compiled);
    private static readonly Regex LuaBlockCloseRegex = new(@"\b(end)\b", RegexOptions.Compiled);

    private static readonly Regex EmmyLuaTagRegex = new(@"^\s*---@(?<tag>[a-zA-Z0-9_]+)(?:\s+(?<rest>.*))?$", RegexOptions.Compiled);
    private static readonly Regex EmmyLuaParamRegex = new(@"^(?<name>[a-zA-Z0-9_]+|\.\.\.)\??\s+(?<type>[^\s]+)(?:\s+(?<desc>.*))?$", RegexOptions.Compiled);
    private static readonly Regex EmmyLuaClassRegex = new(@"^(?<name>[a-zA-Z0-9_]+)(?:\s*:\s*(?<parent>[a-zA-Z0-9_]+))?", RegexOptions.Compiled);
    private static readonly Regex EmmyLuaClassLineRegex = new(@"^---@class\s+(?<name>[a-zA-Z0-9_]+)", RegexOptions.Compiled);
    private static readonly Regex LuaFunctionSignatureRegex = new(@"^\s*(?:local\s+)?function\s+(?:(?<receiver>[a-zA-Z0-9_]+)[:\.])?(?<name>[a-zA-Z0-9_]+)\s*\((?<params>[^\)]*)\)", RegexOptions.Compiled);
    private static readonly Regex LuaLocalDeclRegex = new(@"^\s*local\s+(?:function\s+)?(?<name>[a-zA-Z0-9_]+)", RegexOptions.Compiled);
    private static readonly Regex LuaGlobalTableAssignRegex = new(@"^(?<name>[A-Za-z0-9_]+)\s*=\s*\{", RegexOptions.Compiled);
    private static readonly Regex LuaGlobalCallOrIndexRegex = new(@"(?<![\.\:]\s*)\b(?<name>[A-Z][A-Za-z0-9_]*)\b(?=\s*[\.\:\(])", RegexOptions.Compiled);

    private static readonly HashSet<string> KnownEmmyLuaTags = new(StringComparer.OrdinalIgnoreCase)
    {
        "class", "param", "return", "field", "type", "alias", "generic", "overload",
        "meta", "see", "deprecated", "diagnostic", "module", "language", "source",
        "version", "author", "license", "nodiscard", "enum"
    };

    private static readonly Dictionary<string, string> EmmyLuaTagCorrections = new(StringComparer.OrdinalIgnoreCase)
    {
        ["parm"] = "param",
        ["params"] = "param",
        ["retun"] = "return",
        ["returns"] = "return",
        ["clas"] = "class",
        ["feild"] = "field",
        ["fild"] = "field",
        ["tyep"] = "type"
    };

    private static readonly Regex GetWidgetRegex = new(
        @"(?:UIManager\.GetInterface\(['""](?<iface>[^'""]+)['""]\)\s*:\s*GetWidget|(?<![:\.])GetWidget)\s*\(\s*['""](?<name>[^'""]+)['""]",
        RegexOptions.Compiled);

    private static readonly Dictionary<string, string> KnownWidgetTypoMap = new(StringComparer.Ordinal)
    {
        ["practice_button"] = "sysbar_quickStart",
        ["sysbar_quickstart"] = "sysbar_quickStart",
        ["sysbar_quick_start"] = "sysbar_quickStart",
        ["mainmenupanelswitcher"] = "MainMenuPanelSwitcher",
        ["main_menu_switcher"] = "MainMenuPanelSwitcher",
        ["prelogin"] = "prelogin_lower",
        ["store"] = "store_container2",
        ["store2"] = "store_container2",
        ["play_button"] = "btnPlayNow",
        ["btn_play_now"] = "btnPlayNow",
        ["chat_input"] = "game_chat_input",
        ["health_bar"] = "hero_health",
        ["mana_bar"] = "hero_mana",
        ["play_pause"] = "btn_play_pause",
        ["follow_camera"] = "btn_camera_follow",
        ["mmr_label"] = "ranked_mmr_label",
        ["hero_details"] = "learnatorium_hero_details"
    };

    private static readonly HashSet<string> KnownK2Globals = new(StringComparer.Ordinal)
    {
        // Engine Singletons & Systems
        "UIManager", "TAO", "Nexus", "Host", "Client", "Cmd", "Echo", "GetPlayerHero",
        "GetLocalPlayerIndex", "Cvar", "Sound", "LuaTrigger", "Trigger", "Action",
        "Chat", "Voice", "Video", "Input", "VFS", "Account", "MasterServer", "HTTP", "File",
        // K2 Interface Script Global Functions
        "GetWidget", "Set", "GetCvarBool", "GetCvarString", "GetCvarInt", "GetCvarNumber",
        "CreateBool", "CreateString", "CreateInt", "VarExists", "SetSave", "PlayMusic",
        "RegisterScript", "RegisterScript2", "SleepWidget", "HostTime", "GetTime",
        "GetSelectedHero", "GetViewHeroName", "GetMap", "AtoB", "StripClanTag",
        "GetDBEntry", "SetDBEntry", "PlaySound", "IsPreseason", "isNewUI",
        // Standard Lua 5.1 Globals
        "_G", "_VERSION", "assert", "collectgarbage", "dofile", "error", "getfenv",
        "getmetatable", "ipairs", "load", "loadfile", "loadstring", "module", "next",
        "pairs", "pcall", "print", "rawequal", "rawget", "rawset", "require", "select",
        "setfenv", "setmetatable", "tonumber", "tostring", "type", "unpack", "xpcall",
        "coroutine", "debug", "io", "math", "os", "package", "string", "table", "arg", "self",
        // Subsystem Global Tables & Utilities
        "HoN_Store", "HoN_Main", "Main", "Watch_System", "Player_Stats", "Player_Stats_V2",
        "HoN_Matchmaking", "PlayerHosted", "Match_Stats", "TestSuite", "GameChat", "HoN_Options",
        "tinsert", "tremove", "JSON", "RPC", "Login", "Phase",
        // Script Event Context Variables
        "interface", "widget", "source", "param", "param0", "param1", "param2", "param3",
        "param4", "param5", "param6", "param7", "param8", "param9", "event", "data", "sender", "recipient"
    };

    private readonly TimeProvider _timeProvider;

    public K2LintService(TimeProvider? timeProvider = null)
    {
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public async Task<K2LintReport> LintAsync(
        string targetPath,
        bool recursive = true,
        bool fixBom = false,
        string severityThreshold = "warning",
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(targetPath);

        string resolvedPath = ResolvePath(targetPath);
        List<string> filesToScan = [];

        if (File.Exists(resolvedPath))
        {
            filesToScan.Add(resolvedPath);
        }
        else if (Directory.Exists(resolvedPath))
        {
            SearchOption opt = recursive ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly;
            filesToScan.AddRange(Directory.EnumerateFiles(resolvedPath, "*.package", opt));
            filesToScan.AddRange(Directory.EnumerateFiles(resolvedPath, "*.interface", opt));
            filesToScan.AddRange(Directory.EnumerateFiles(resolvedPath, "*.lua", opt));
        }
        else
        {
            // Support glob pattern in directory
            string dir = Path.GetDirectoryName(resolvedPath) ?? Directory.GetCurrentDirectory();
            string pattern = Path.GetFileName(resolvedPath);
            if (Directory.Exists(dir))
            {
                SearchOption opt = recursive ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly;
                filesToScan.AddRange(Directory.EnumerateFiles(dir, pattern, opt));
            }
            else
            {
                return new K2LintReport
                {
                    Status = "error",
                    TargetPath = targetPath,
                    Timestamp = _timeProvider.GetUtcNow().ToString("o"),
                    Issues =
                    [
                        new K2LintIssue
                        {
                            File = targetPath,
                            Severity = "error",
                            Code = "PATH_NOT_FOUND",
                            Message = $"Target path '{targetPath}' could not be resolved."
                        }
                    ]
                };
            }
        }

        List<K2LintIssue> issues = [];
        List<string> fixedBomFiles = [];
        int cleanFiles = 0;

        foreach (string file in filesToScan.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            ct.ThrowIfCancellationRequested();
            var fileIssues = await LintSingleFileAsync(file, fixBom, fixedBomFiles, ct).ConfigureAwait(false);
            if (fileIssues.Count == 0)
            {
                cleanFiles++;
            }
            else
            {
                issues.AddRange(fileIssues);
            }
        }

        bool onlyErrors = severityThreshold.Equals("error", StringComparison.OrdinalIgnoreCase);
        var filteredIssues = onlyErrors
            ? issues.Where(i => i.Severity.Equals("error", StringComparison.OrdinalIgnoreCase)).ToList()
            : issues;

        int errorCount = filteredIssues.Count(i => i.Severity.Equals("error", StringComparison.OrdinalIgnoreCase));
        int warningCount = filteredIssues.Count(i => i.Severity.Equals("warning", StringComparison.OrdinalIgnoreCase));

        return new K2LintReport
        {
            Status = errorCount == 0 ? "success" : "error",
            TargetPath = targetPath,
            FilesScanned = filesToScan.Count,
            ErrorCount = errorCount,
            WarningCount = warningCount,
            CleanFilesCount = cleanFiles,
            Issues = filteredIssues,
            FixedBomFiles = fixedBomFiles,
            Timestamp = _timeProvider.GetUtcNow().ToString("o")
        };
    }

    private async Task<List<K2LintIssue>> LintSingleFileAsync(
        string file,
        bool fixBom,
        List<string> fixedBomFiles,
        CancellationToken ct)
    {
        List<K2LintIssue> issues = [];
        byte[] bytes = await File.ReadAllBytesAsync(file, ct).ConfigureAwait(false);

        // 1. Check for UTF-8 Byte Order Mark (BOM)
        if (bytes.Length >= 3 && bytes[0] == Utf8Bom[0] && bytes[1] == Utf8Bom[1] && bytes[2] == Utf8Bom[2])
        {
            if (fixBom)
            {
                byte[] withoutBom = bytes[3..];
                await File.WriteAllBytesAsync(file, withoutBom, ct).ConfigureAwait(false);
                fixedBomFiles.Add(file);
                bytes = withoutBom;
            }
            else
            {
                issues.Add(new K2LintIssue
                {
                    File = file,
                    Line = 1,
                    Column = 1,
                    Severity = "warning",
                    Code = "K2_BOM_PRESENT",
                    Message = "UTF-8 Byte Order Mark (BOM) detected. K2 engine XML parsers crash or fail on leading BOM bytes.",
                    Snippet = "0xEF, 0xBB, 0xBF",
                    RemediationHint = "Save file as UTF-8 without BOM or rerun with fix_bom: true."
                });
            }
        }

        string ext = Path.GetExtension(file).ToLowerInvariant();
        string text = Encoding.UTF8.GetString(bytes);

        if (ext == ".lua")
        {
            LintLuaScript(file, text, baseLine: 1, issues);
            LintEmmyLuaAndGlobals(file, text, baseLine: 1, issues);
            if (Path.GetFileName(file).Equals("store.lua", StringComparison.OrdinalIgnoreCase))
            {
                ValidateStoreCatalogConsistency(file, text, issues);
            }
            return issues;
        }

        // XML linting for .package and .interface files
        LintK2Xml(file, text, issues);
        return issues;
    }

    private void LintK2Xml(string file, string text, List<K2LintIssue> issues)
    {
        string[] lines = text.Split('\n');

        // Fast pre-scan: unescaped & in XML attributes
        for (int i = 0; i < lines.Length; i++)
        {
            string line = lines[i];
            var match = UnescapedAmpRegex.Match(line);
            if (match.Success)
            {
                // Check if inside attribute quotes
                int ampIdx = match.Index;
                int quotesBefore = line[..ampIdx].Count(c => c == '"');
                if (quotesBefore % 2 == 1)
                {
                    issues.Add(new K2LintIssue
                    {
                        File = file,
                        Line = i + 1,
                        Column = ampIdx + 1,
                        Severity = "error",
                        Code = "XML_UNESCAPED_AMP",
                        Message = "Unescaped '&' found inside XML attribute. K2 parser requires '&amp;' or Lua logical 'and'.",
                        Snippet = line.Trim(),
                        RemediationHint = "Replace '&' with '&amp;' or use Lua keyword 'and'."
                    });
                }
            }
        }

        HashSet<string> definedTemplates = new(StringComparer.OrdinalIgnoreCase);
        List<(string Template, int Line, int Col)> instanceReferences = [];

        // Parse XML hierarchy with XmlReader for exact line/col tracking
        XmlReaderSettings settings = new()
        {
            DtdProcessing = DtdProcessing.Ignore,
            CheckCharacters = false,
            IgnoreComments = true,
            IgnoreWhitespace = true
        };

        try
        {
            using StringReader sr = new(text);
            using XmlReader reader = XmlReader.Create(sr, settings);
            string currentElementName = string.Empty;

            while (reader.Read())
            {
                IXmlLineInfo? lineInfo = reader as IXmlLineInfo;
                int line = lineInfo?.LineNumber ?? 1;
                int col = lineInfo?.LinePosition ?? 1;

                if (reader.NodeType == XmlNodeType.Element)
                {
                    string tagName = reader.Name;
                    currentElementName = tagName;
                    if (!KnownK2Tags.Contains(tagName))
                    {
                        issues.Add(new K2LintIssue
                        {
                            File = file,
                            Line = line,
                            Column = col,
                            Severity = "warning",
                            Code = "K2_UNKNOWN_TAG",
                            Message = $"Unrecognized K2 XML widget element '<{tagName}>'.",
                            Snippet = $"<{tagName}>",
                            RemediationHint = "Verify spelling against valid K2 tags (panel, frame, image, label, button, template, instance, script, etc.)."
                        });
                    }

                    // Track templates and instances
                    if (tagName.Equals("template", StringComparison.OrdinalIgnoreCase))
                    {
                        string? name = reader.GetAttribute("name");
                        if (!string.IsNullOrWhiteSpace(name))
                        {
                            definedTemplates.Add(name);
                        }
                    }
                    else if (tagName.Equals("instance", StringComparison.OrdinalIgnoreCase))
                    {
                        string? targetTemplate = reader.GetAttribute("template") ?? reader.GetAttribute("name");
                        if (!string.IsNullOrWhiteSpace(targetTemplate))
                        {
                            instanceReferences.Add((targetTemplate, line, col));
                        }
                        else
                        {
                            issues.Add(new K2LintIssue
                            {
                                File = file,
                                Line = line,
                                Column = col,
                                Severity = "error",
                                Code = "K2_MISSING_TEMPLATE_ATTR",
                                Message = "<instance> widget is missing required 'template' attribute.",
                                Snippet = "<instance ... />",
                                RemediationHint = "Provide a valid template attribute e.g. <instance template=\"my_button\" />."
                            });
                        }
                    }

                    // Lint Lua event attributes (skipping legacy K2 UICmd expressions unless they contain explicit Lua syntax)
                    if (reader.HasAttributes)
                    {
                        while (reader.MoveToNextAttribute())
                        {
                            string attrName = reader.Name;
                            string attrVal = reader.Value;
                            if (LuaEventAttributes.Contains(attrName) ||
                                attrName.EndsWith("lua", StringComparison.OrdinalIgnoreCase) ||
                                (K2UiCmdEventAttributes.Contains(attrName) && LuaHeuristicInAttributeRegex.IsMatch(attrVal)))
                            {
                                LintLuaScript(file, attrVal, baseLine: line, issues, isAttribute: true);
                            }
                        }
                        reader.MoveToElement();
                    }
                }
                else if (reader.NodeType == XmlNodeType.Text || reader.NodeType == XmlNodeType.CDATA)
                {
                    // Only lint <lua> elements, or <script> elements that actually contain Lua code (not K2 UICmd scripts)
                    if (currentElementName.Equals("lua", StringComparison.OrdinalIgnoreCase) ||
                        (currentElementName.Equals("script", StringComparison.OrdinalIgnoreCase) && LuaHeuristicInAttributeRegex.IsMatch(reader.Value)))
                    {
                        LintLuaScript(file, reader.Value, baseLine: line, issues);
                        LintEmmyLuaAndGlobals(file, reader.Value, baseLine: line, issues);
                    }
                }
            }
        }
        catch (XmlException xmlEx)
        {
            issues.Add(new K2LintIssue
            {
                File = file,
                Line = xmlEx.LineNumber,
                Column = xmlEx.LinePosition,
                Severity = "error",
                Code = "XML_SYNTAX_ERROR",
                Message = $"XML syntax error: {xmlEx.Message}",
                Snippet = xmlEx.LineNumber <= lines.Length ? lines[xmlEx.LineNumber - 1].Trim() : null,
                RemediationHint = "Check for unclosed tags, mismatched quotes, or invalid characters."
            });
        }
        catch (Exception ex)
        {
            issues.Add(new K2LintIssue
            {
                File = file,
                Line = 1,
                Column = 1,
                Severity = "error",
                Code = "XML_PARSE_FAILURE",
                Message = $"General XML parse failure: {ex.Message}"
            });
        }

        // Verify template existence for local references
        foreach (var (tmpl, line, col) in instanceReferences)
        {
            // If template not found locally and file defines templates, warn
            if (definedTemplates.Count > 0 && !definedTemplates.Contains(tmpl) && !tmpl.StartsWith("common_", StringComparison.OrdinalIgnoreCase))
            {
                issues.Add(new K2LintIssue
                {
                    File = file,
                    Line = line,
                    Column = col,
                    Severity = "warning",
                    Code = "K2_UNRESOLVED_TEMPLATE",
                    Message = $"Instance references template '{tmpl}' which is not defined in this package.",
                    Snippet = $"<instance template=\"{tmpl}\" />",
                    RemediationHint = $"Ensure template '{tmpl}' is imported via <include> or defined in common templates."
                });
            }
        }
    }

    private static void LintLuaScript(string file, string luaCode, int baseLine, List<K2LintIssue> issues, bool isAttribute = false)
    {
        if (string.IsNullOrWhiteSpace(luaCode)) return;

        string[] scriptLines = luaCode.Split('\n');

        // Check 1: Forbidden 'Windows' global reference
        for (int i = 0; i < scriptLines.Length; i++)
        {
            string line = scriptLines[i];
            string trimmed = line.Trim();
            if (trimmed.StartsWith("--", StringComparison.Ordinal)) continue;

            int currentLine = baseLine + (isAttribute ? 0 : i);

            var winMatch = WindowsGlobalRegex.Match(line);
            if (winMatch.Success)
            {
                issues.Add(new K2LintIssue
                {
                    File = file,
                    Line = currentLine,
                    Column = winMatch.Index + 1,
                    Severity = "error",
                    Code = "K2_FORBIDDEN_GLOBAL",
                    Message = "Global reference 'Windows' is not defined in K2 Lua. The global UI manager is 'UIManager'.",
                    Snippet = line.Trim(),
                    RemediationHint = "Replace 'Windows.main' or 'Windows[...]' with 'UIManager.GetInterface(\"main\")'."
                });
            }

            // Check 2: Invalid DoEvent('onclick') signature
            var doEvMatch = DoEventStringArgRegex.Match(line);
            if (doEvMatch.Success)
            {
                issues.Add(new K2LintIssue
                {
                    File = file,
                    Line = currentLine,
                    Column = doEvMatch.Index + 1,
                    Severity = "error",
                    Code = "K2_INVALID_DOEVENT_SIGNATURE",
                    Message = "widget:DoEvent() takes NO string parameters. Passing strings like 'onclick' causes K2 Lua runtime errors.",
                    Snippet = line.Trim(),
                    RemediationHint = "Use 'widget:DoEvent()' with no arguments, or 'widget:DoEventN(index)' with an integer index."
                });
            }

            string codeWithoutLiterals = LuaLiteralOrCommentRegex.Replace(line, m => new string(' ', m.Length));

            // Check 3: Invalid C-style logical/relational operators (!=, &&, ||)
            var opMatches = LuaInvalidOpRegex.Matches(codeWithoutLiterals);
            foreach (Match opMatch in opMatches)
            {
                string invalidOp = opMatch.Value;
                string replacement = invalidOp == "!=" ? "~=" : (invalidOp == "&&" ? "and" : "or");
                issues.Add(new K2LintIssue
                {
                    File = file,
                    Line = currentLine,
                    Column = opMatch.Index + 1,
                    Severity = "error",
                    Code = "LUA_INVALID_OPERATOR",
                    Message = $"Invalid operator '{invalidOp}' in Lua code. Lua uses '{replacement}'.",
                    Snippet = line.Trim(),
                    RemediationHint = $"Replace '{invalidOp}' with '{replacement}'."
                });
            }

            // Check 4: Unknown or mistyped widget names in GetWidget calls
            var widgetMatches = GetWidgetRegex.Matches(line);
            foreach (Match wMatch in widgetMatches)
            {
                string widgetName = wMatch.Groups["name"].Value;
                if (KnownWidgetTypoMap.TryGetValue(widgetName, out string? suggestion))
                {
                    issues.Add(new K2LintIssue
                    {
                        File = file,
                        Line = currentLine,
                        Column = wMatch.Index + 1,
                        Severity = "warning",
                        Code = "K2_UNKNOWN_WIDGET",
                        Message = $"Potentially invalid or mistyped widget reference '{widgetName}'.",
                        Snippet = line.Trim(),
                        RemediationHint = $"Replace '{widgetName}' with canonical widget '{suggestion}'."
                    });
                }
            }

            // Check 4b: Function arithmetic trap (Issue-242: e.g. 'HostTime + 3200' or '(HostTime or 0) + 3200')
            var fnArithMatch = FunctionArithmeticRegex.Match(codeWithoutLiterals);
            if (fnArithMatch.Success)
            {
                issues.Add(new K2LintIssue
                {
                    File = file,
                    Line = currentLine,
                    Column = fnArithMatch.Index + 1,
                    Severity = "error",
                    Code = "LUA_FUNCTION_ARITHMETIC",
                    Message = "Attempting arithmetic on function 'HostTime' without invocation. In K2 Lua this throws runtime error: attempt to perform arithmetic on a function value.",
                    Snippet = line.Trim(),
                    RemediationHint = "Invoke the function e.g. 'HostTime()' or verify parentheses."
                });
            }
        }

        // Check 5: Balanced keywords (if/end, do/end, function/end)
        // Strip comments, multi-line strings, and quoted literals first to eliminate false positives
        string stripped = LuaLiteralOrCommentRegex.Replace(luaCode, " ");
        int blockDepth = LuaBlockOpenRegex.Count(stripped) - LuaBlockCloseRegex.Count(stripped);

        if (blockDepth > 0)
        {
            issues.Add(new K2LintIssue
            {
                File = file,
                Line = baseLine,
                Column = 1,
                Severity = "error",
                Code = "LUA_UNBALANCED_BLOCK",
                Message = $"Lua syntax error: Missing 'end' statement (unclosed block depth: +{blockDepth}).",
                Snippet = luaCode.Length > 80 ? luaCode[..80] + "..." : luaCode,
                RemediationHint = "Ensure every 'if...then', 'for...do', 'while...do', and 'function' has a matching 'end'."
            });
        }
        else if (blockDepth < 0)
        {
            issues.Add(new K2LintIssue
            {
                File = file,
                Line = baseLine,
                Column = 1,
                Severity = "error",
                Code = "LUA_EXTRA_END",
                Message = $"Lua syntax error: Unexpected 'end' statement (block depth: {blockDepth}).",
                Snippet = luaCode.Length > 80 ? luaCode[..80] + "..." : luaCode,
                RemediationHint = "Check for extraneous or mismatched 'end' statements."
            });
        }
    }

    private static void LintEmmyLuaAndGlobals(string file, string luaCode, int baseLine, List<K2LintIssue> issues)
    {
        if (string.IsNullOrWhiteSpace(luaCode)) return;

        string[] lines = luaCode.Split('\n');
        List<(string ParamName, int Line, int Col)> pendingDocParams = [];
        HashSet<string> fileDefinedSymbols = new(StringComparer.Ordinal);

        // Pre-pass: scan for file-level declarations (classes, globals, top-level locals, functions)
        for (int i = 0; i < lines.Length; i++)
        {
            string rawLine = lines[i].Trim();
            var classMatch = EmmyLuaClassLineRegex.Match(rawLine);
            if (classMatch.Success)
            {
                fileDefinedSymbols.Add(classMatch.Groups["name"].Value);
            }

            var tableMatch = LuaGlobalTableAssignRegex.Match(rawLine);
            if (tableMatch.Success)
            {
                fileDefinedSymbols.Add(tableMatch.Groups["name"].Value);
            }

            var localMatch = LuaLocalDeclRegex.Match(rawLine);
            if (localMatch.Success)
            {
                fileDefinedSymbols.Add(localMatch.Groups["name"].Value);
            }

            var funcMatch = LuaFunctionSignatureRegex.Match(rawLine);
            if (funcMatch.Success)
            {
                string funcName = funcMatch.Groups["name"].Value;
                fileDefinedSymbols.Add(funcName);
                if (funcMatch.Groups["receiver"].Success)
                {
                    fileDefinedSymbols.Add(funcMatch.Groups["receiver"].Value);
                }
            }
        }

        // Main pass: EmmyLua tag checking and function signature cross-validation
        for (int i = 0; i < lines.Length; i++)
        {
            int currentLine = baseLine + i;
            string line = lines[i];
            string trimmed = line.Trim();

            // 1. EmmyLua tag syntax check
            var tagMatch = EmmyLuaTagRegex.Match(trimmed);
            if (tagMatch.Success)
            {
                string tag = tagMatch.Groups["tag"].Value;
                string rest = tagMatch.Groups["rest"].Value.Trim();
                int col = line.IndexOf("---@", StringComparison.Ordinal) + 1;

                if (!KnownEmmyLuaTags.Contains(tag))
                {
                    string hint = EmmyLuaTagCorrections.TryGetValue(tag, out string? corrected)
                        ? $"Did you mean '---@{corrected}'?"
                        : "Supported EmmyLua tags include: @class, @param, @return, @field, @type, @alias, @generic.";

                    issues.Add(new K2LintIssue
                    {
                        File = file,
                        Line = currentLine,
                        Column = col,
                        Severity = "warning",
                        Code = "EMMYLUA_UNKNOWN_TAG",
                        Message = $"Unrecognized EmmyLua annotation tag '---@{tag}'.",
                        Snippet = trimmed,
                        RemediationHint = hint
                    });
                }
                else if (tag.Equals("class", StringComparison.OrdinalIgnoreCase))
                {
                    var classMatch = EmmyLuaClassRegex.Match(rest);
                    if (!classMatch.Success || string.IsNullOrWhiteSpace(classMatch.Groups["name"].Value))
                    {
                        issues.Add(new K2LintIssue
                        {
                            File = file,
                            Line = currentLine,
                            Column = col,
                            Severity = "error",
                            Code = "EMMYLUA_MALFORMED_CLASS",
                            Message = "Malformed EmmyLua @class annotation. Expected: '---@class <ClassName> [: <ParentClass>]'.",
                            Snippet = trimmed,
                            RemediationHint = "Provide a valid class identifier e.g. '---@class CWidget'."
                        });
                    }
                }
                else if (tag.Equals("param", StringComparison.OrdinalIgnoreCase))
                {
                    var paramMatch = EmmyLuaParamRegex.Match(rest);
                    if (!paramMatch.Success)
                    {
                        issues.Add(new K2LintIssue
                        {
                            File = file,
                            Line = currentLine,
                            Column = col,
                            Severity = "error",
                            Code = "EMMYLUA_MALFORMED_PARAM",
                            Message = "Malformed EmmyLua @param annotation. Expected: '---@param <paramName> <paramType> [description]'.",
                            Snippet = trimmed,
                            RemediationHint = "Provide parameter name and type e.g. '---@param index integer'."
                        });
                    }
                    else
                    {
                        string pName = paramMatch.Groups["name"].Value;
                        pendingDocParams.Add((pName, currentLine, col));
                    }
                }
                else if (tag.Equals("return", StringComparison.OrdinalIgnoreCase))
                {
                    if (string.IsNullOrWhiteSpace(rest))
                    {
                        issues.Add(new K2LintIssue
                        {
                            File = file,
                            Line = currentLine,
                            Column = col,
                            Severity = "error",
                            Code = "EMMYLUA_MALFORMED_RETURN",
                            Message = "EmmyLua @return annotation is missing return type.",
                            Snippet = trimmed,
                            RemediationHint = "Specify return type e.g. '---@return nil' or '---@return string'."
                        });
                    }
                }
                continue;
            }

            // 2. Correlate documented params with function declaration
            if (pendingDocParams.Count > 0)
            {
                var funcMatch = LuaFunctionSignatureRegex.Match(trimmed);
                if (funcMatch.Success)
                {
                    string funcName = funcMatch.Groups["name"].Value;
                    string rawParams = funcMatch.Groups["params"].Value;
                    HashSet<string> declaredParams = new(
                        rawParams.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries),
                        StringComparer.Ordinal
                    );

                    foreach (var (pName, docLine, docCol) in pendingDocParams)
                    {
                        if (!declaredParams.Contains(pName) && !pName.Equals("...", StringComparison.Ordinal) && !declaredParams.Contains("..."))
                        {
                            issues.Add(new K2LintIssue
                            {
                                File = file,
                                Line = docLine,
                                Column = docCol,
                                Severity = "warning",
                                Code = "EMMYLUA_PARAM_MISMATCH",
                                Message = $"EmmyLua @param '{pName}' does not match any declared parameter in function '{funcName}({rawParams})'.",
                                Snippet = $"function {funcName}({rawParams})",
                                RemediationHint = $"Rename @param '{pName}' to match one of the declared function parameters ({rawParams}), or update the function signature."
                            });
                        }
                    }

                    pendingDocParams.Clear();
                }
                else if (!trimmed.StartsWith("--", StringComparison.Ordinal) && !string.IsNullOrWhiteSpace(trimmed))
                {
                    // Hit a non-comment, non-function line after @param
                    pendingDocParams.Clear();
                }
            }

            // 3. Undefined Global Checking
            // Skip comments and string literals
            if (trimmed.StartsWith("--", StringComparison.Ordinal)) continue;

            string codeWithoutLiterals = LuaLiteralOrCommentRegex.Replace(line, " ");
            var globalMatches = LuaGlobalCallOrIndexRegex.Matches(codeWithoutLiterals);
            foreach (Match gMatch in globalMatches)
            {
                string globalName = gMatch.Groups["name"].Value;
                if (!KnownK2Globals.Contains(globalName) && !fileDefinedSymbols.Contains(globalName))
                {
                    int col = gMatch.Index + 1;
                    if (globalName.Equals("Windows", StringComparison.OrdinalIgnoreCase))
                    {
                        // Already handled by WindowsGlobalRegex
                        continue;
                    }

                    issues.Add(new K2LintIssue
                    {
                        File = file,
                        Line = currentLine,
                        Column = col,
                        Severity = "warning",
                        Code = "K2_UNDEFINED_GLOBAL",
                        Message = $"Reference to undeclared global '{globalName}'. This symbol is neither in the K2 engine API nor declared in this file.",
                        Snippet = line.Trim(),
                        RemediationHint = $"Ensure '{globalName}' is defined or import it. Common K2 globals are UIManager, Cmd, Echo, TAO, Nexus, Host, Client."
                    });
                }
            }
        }
    }

    private static void ValidateStoreCatalogConsistency(string file, string luaText, List<K2LintIssue> issues)
    {
        string root = FindProjectRoot();
        string configPath = Path.Combine(root, "src", "services", "master-server", "Configuration", "Store", "StoreItemsConfiguration.json");
        if (!File.Exists(configPath)) return;

        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(File.ReadAllText(configPath));
            foreach (var item in doc.RootElement.EnumerateArray())
            {
                int id = item.TryGetProperty("ID", out var idProp) ? idProp.GetInt32() : 0;
                if (id <= 0) continue;

                bool isEnabled = !item.TryGetProperty("IsEnabled", out var enProp) || enProp.GetBoolean();
                if (!isEnabled) continue;

                string name = item.TryGetProperty("Name", out var nameProp) ? (nameProp.GetString() ?? "") : "";
                int type = item.TryGetProperty("Type", out var typeProp) ? typeProp.GetInt32() : 0;

                // Check Creeps (Type 15 or name containing "Creep")
                if (type == 15 || name.Contains("Creep", StringComparison.OrdinalIgnoreCase))
                {
                    string idStr = $"'{id}'";
                    string idStr2 = $"\"{id}\"";
                    string idNum = $"[{id}]";
                    if (!luaText.Contains(idStr) && !luaText.Contains(idStr2) && !luaText.Contains(idNum))
                    {
                        issues.Add(new K2LintIssue
                        {
                            File = file,
                            Line = 1,
                            Column = 1,
                            Severity = "error",
                            Code = "K2_STORE_CATALOG_DESYNC",
                            Message = $"Store catalog creep item ID {id} ('{name}') is present in StoreItemsConfiguration.json but missing from store.lua CreepsTable.",
                            Snippet = $"ID: {id}, Name: {name}",
                            RemediationHint = $"Register product ID {id} in HoN_Store.CreepsTable in store.lua (prevents Issue-245 infinite spinner)."
                        });
                    }
                }
                // Check Announcers (Type 4 or name containing "Announcer")
                else if (type == 4 || name.Contains("Announcer", StringComparison.OrdinalIgnoreCase))
                {
                    string idStr = $"'{id}'";
                    string idStr2 = $"\"{id}\"";
                    string idNum = $"[{id}]";
                    if (!luaText.Contains(idStr) && !luaText.Contains(idStr2) && !luaText.Contains(idNum))
                    {
                        issues.Add(new K2LintIssue
                        {
                            File = file,
                            Line = 1,
                            Column = 1,
                            Severity = "error",
                            Code = "K2_STORE_CATALOG_DESYNC",
                            Message = $"Store catalog announcer ID {id} ('{name}') is present in StoreItemsConfiguration.json but missing from store.lua VoiceAnnouncersTable or fallbackNames.",
                            Snippet = $"ID: {id}, Name: {name}",
                            RemediationHint = $"Register product ID {id} in HoN_Store.VoiceAnnouncersTable in store.lua (prevents Issue-240 silent audio preview)."
                        });
                    }
                }
            }
        }
        catch
        {
            // Ignore parse errors on missing/malformed catalog
        }
    }

    private static string ResolvePath(string path)
    {
        if (Path.IsPathRooted(path)) return path;

        string root = FindProjectRoot();
        string p1 = Path.Combine(root, path);
        if (File.Exists(p1) || Directory.Exists(p1)) return p1;

        string clientDir = Path.Combine(root, "games", "client");
        string p2 = Path.Combine(clientDir, path);
        if (File.Exists(p2) || Directory.Exists(p2)) return p2;

        return p1;
    }

    private static string FindProjectRoot()
    {
        if (Kongor.Core.Configuration.KongorWorkspaceResolver.TryResolve(out var ws) && ws is not null)
        {
            return ws.Root;
        }

        return Directory.GetCurrentDirectory();
    }
}
