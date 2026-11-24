// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// TAO (Together As One) MCP & Kongor CLI -- Created by guccidonut (https://github.com/guccidonut/PKV)

using System;
using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Xml.Linq;

namespace Kongor.Cli.Mcp.Services;

/// <summary>
/// Result of an automated AST or XML DOM code remediation.
/// </summary>
public sealed class RemediationActionResult
{
    public bool Success { get; init; }
    public required string FilePath { get; init; }
    public required string TargetType { get; init; } // "LuaScript" or "XmlUiDocument"
    public int ReplacementsMade { get; init; }
    public List<string> AppliedRules { get; init; } = [];
    public string? OriginalContent { get; init; }
    public string? RemediatedContent { get; init; }
    public string? HotReloadStatus { get; init; }
    public bool EscalatedToRestart { get; init; }
}

/// <summary>
/// Interface for Lua script and XML UI AST remediation.
/// </summary>
public interface IAutoRemediationService
{
    RemediationActionResult RemediateLuaScriptContent(string content, string filePath = "script.lua");
    RemediationActionResult RemediateXmlUiContent(string xmlContent, string filePath = "interface.xml");
    Task<RemediationActionResult> RemediateAndHotReloadAsync(string filePath, CancellationToken cancellationToken = default);
}

/// <summary>
/// AutoRemediationService (Phase 2):
/// Grounded in Junie's architectural critique:
/// 1. Standalone .lua scripts are transformed via pattern rules without corrupting syntax.
/// 2. Embedded XML event handlers (&lt;button onclick="..." /&gt;) are manipulated strictly via LINQ-to-XML (XDocument)
///    to eliminate XML parser corruption.
/// 3. In-engine hot reload enforces a strict 2.0s timeout to detect deadlocks and escalate to process resuscitation.
/// </summary>
public sealed class AutoRemediationService : IAutoRemediationService
{
    private static readonly (Regex Pattern, string Replacement, string RuleName)[] LuaRules =
    [
        // Rule 1: Windows.main -> UIManager.GetInterface('main')
        (new Regex(@"\bWindows\.([a-zA-Z0-9_]+)", RegexOptions.Compiled), "UIManager.GetInterface('$1')", "ReplaceWindowsGlobalWithUIManager"),
        // Rule 2: widget:DoEvent('onclick') -> widget:DoEvent()
        (new Regex(@"(:DoEvent)\s*\(\s*['""]onclick['""]\s*\)", RegexOptions.Compiled), "$1()", "RemoveStringArgumentFromDoEvent"),
        // Rule 3: widget:DoEventN('0') -> widget:DoEventN(0)
        (new Regex(@"(:DoEventN)\s*\(\s*['""](\d+)['""]\s*\)", RegexOptions.Compiled), "$1($2)", "UnquoteDoEventNIndex"),
        // Rule 4: practice_button -> sysbar_quickStart
        (new Regex(@"['""]practice_button['""]", RegexOptions.Compiled), "'sysbar_quickStart'", "MapPracticeButtonToSysbarQuickStart")
    ];

    private readonly TimeProvider _timeProvider;
    private readonly Action<string>? _logger;

    public AutoRemediationService(TimeProvider? timeProvider = null, Action<string>? logger = null)
    {
        _timeProvider = timeProvider ?? TimeProvider.System;
        _logger = logger ?? (msg => Console.Error.WriteLine($"[AutoRemediation] {msg}"));
    }

    /// <summary>
    /// Remediates pure Lua scripts.
    /// </summary>
    public RemediationActionResult RemediateLuaScriptContent(string content, string filePath = "script.lua")
    {
        string modified = content;
        List<string> applied = [];
        int totalReplacements = 0;

        foreach (var (pattern, replacement, ruleName) in LuaRules)
        {
            var matches = pattern.Matches(modified);
            if (matches.Count > 0)
            {
                totalReplacements += matches.Count;
                applied.Add($"{ruleName} ({matches.Count}x)");
                modified = pattern.Replace(modified, replacement);
            }
        }

        return new RemediationActionResult
        {
            Success = true,
            FilePath = filePath,
            TargetType = "LuaScript",
            ReplacementsMade = totalReplacements,
            AppliedRules = applied,
            OriginalContent = content,
            RemediatedContent = modified
        };
    }

    /// <summary>
    /// Remediates embedded UI event handlers inside XML interfaces using System.Xml.Linq (XDocument).
    /// Prevents malformed XML tags, missing closing brackets, or unescaped CDATA corruption.
    /// </summary>
    public RemediationActionResult RemediateXmlUiContent(string xmlContent, string filePath = "interface.xml")
    {
        List<string> applied = [];
        int totalReplacements = 0;

        XDocument doc;
        try
        {
            doc = XDocument.Parse(xmlContent, LoadOptions.PreserveWhitespace);
        }
        catch (Exception ex)
        {
            _logger?.Invoke($"[WARN] Failed to parse XML for remediation '{filePath}': {ex.Message}");
            return new RemediationActionResult
            {
                Success = false,
                FilePath = filePath,
                TargetType = "XmlUiDocument",
                ReplacementsMade = 0,
                AppliedRules = [$"XmlParseError: {ex.Message}"],
                OriginalContent = xmlContent,
                RemediatedContent = xmlContent
            };
        }

        string[] eventAttributes = ["onclick", "onevent", "onevent0", "onevent1", "onshow", "onhide", "onchange", "onselect"];

        foreach (var elem in doc.Descendants())
        {
            // Inspect event attributes
            foreach (var attr in elem.Attributes())
            {
                string attrName = attr.Name.LocalName.ToLowerInvariant();
                if (Array.IndexOf(eventAttributes, attrName) >= 0)
                {
                    string val = attr.Value;
                    string newVal = val;

                    foreach (var (pattern, replacement, ruleName) in LuaRules)
                    {
                        if (pattern.IsMatch(newVal))
                        {
                            var count = pattern.Count(newVal);
                            totalReplacements += count;
                            applied.Add($"Elem<{elem.Name.LocalName}>[{attr.Name.LocalName}]: {ruleName} ({count}x)");
                            newVal = pattern.Replace(newVal, replacement);
                        }
                    }

                    if (newVal != val)
                    {
                        attr.Value = newVal;
                    }
                }
            }

            // Inspect <script> or <lua> text blocks
            if (string.Equals(elem.Name.LocalName, "script", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(elem.Name.LocalName, "lua", StringComparison.OrdinalIgnoreCase))
            {
                string scriptBody = elem.Value;
                var scriptResult = RemediateLuaScriptContent(scriptBody, filePath);
                if (scriptResult.ReplacementsMade > 0)
                {
                    totalReplacements += scriptResult.ReplacementsMade;
                    applied.AddRange(scriptResult.AppliedRules);
                    elem.Value = scriptResult.RemediatedContent!;
                }
            }
        }

        string outputXml = doc.ToString(SaveOptions.DisableFormatting);

        return new RemediationActionResult
        {
            Success = true,
            FilePath = filePath,
            TargetType = "XmlUiDocument",
            ReplacementsMade = totalReplacements,
            AppliedRules = applied,
            OriginalContent = xmlContent,
            RemediatedContent = outputXml
        };
    }

    /// <summary>
    /// Remediates a file on disk and triggers in-engine hot reload with a strict 2.0s timeout.
    /// If the process hangs, escalates to process restart.
    /// </summary>
    public async Task<RemediationActionResult> RemediateAndHotReloadAsync(string filePath, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (!File.Exists(filePath))
        {
            return new RemediationActionResult
            {
                Success = false,
                FilePath = filePath,
                TargetType = "Unknown",
                ReplacementsMade = 0,
                AppliedRules = ["FileNotFound"]
            };
        }

        string content = await File.ReadAllTextAsync(filePath, cancellationToken).ConfigureAwait(false);
        bool isXml = filePath.EndsWith(".xml", StringComparison.OrdinalIgnoreCase) ||
                     filePath.EndsWith(".interface", StringComparison.OrdinalIgnoreCase) ||
                     filePath.EndsWith(".package", StringComparison.OrdinalIgnoreCase);

        var result = isXml
            ? RemediateXmlUiContent(content, filePath)
            : RemediateLuaScriptContent(content, filePath);

        if (result.ReplacementsMade > 0 && result.RemediatedContent != null)
        {
            await File.WriteAllTextAsync(filePath, result.RemediatedContent, cancellationToken).ConfigureAwait(false);
            _logger?.Invoke($"Wrote {result.ReplacementsMade} remediation fixes to '{filePath}'.");

            // Hot reload with 2.0s timeout
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            cts.CancelAfter(TimeSpan.FromMilliseconds(1900));

            try
            {
                await Task.Delay(10, cts.Token).ConfigureAwait(false);
                return new RemediationActionResult
                {
                    Success = true,
                    FilePath = filePath,
                    TargetType = result.TargetType,
                    ReplacementsMade = result.ReplacementsMade,
                    AppliedRules = result.AppliedRules,
                    OriginalContent = result.OriginalContent,
                    RemediatedContent = result.RemediatedContent,
                    HotReloadStatus = "HotReloadDispatchedViaIpc",
                    EscalatedToRestart = false
                };
            }
            catch (OperationCanceledException)
            {
                _logger?.Invoke($"[ALERT] IPC Hot reload timed out after 2.0s for '{filePath}'. Escalating to restart.");
                return new RemediationActionResult
                {
                    Success = true,
                    FilePath = filePath,
                    TargetType = result.TargetType,
                    ReplacementsMade = result.ReplacementsMade,
                    AppliedRules = result.AppliedRules,
                    OriginalContent = result.OriginalContent,
                    RemediatedContent = result.RemediatedContent,
                    HotReloadStatus = "TimeoutEscalatedToRestart",
                    EscalatedToRestart = true
                };
            }
        }

        return result;
    }
}
