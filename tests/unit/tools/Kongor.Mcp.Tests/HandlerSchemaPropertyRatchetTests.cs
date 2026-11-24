// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// TAO (Together As One) MCP & Kongor CLI -- Created by guccidonut (https://github.com/guccidonut/PKV)

using System.IO;
using System.Text.RegularExpressions;
using FluentAssertions;
using Kongor.Cli.Mcp.Protocol;
using Xunit;

namespace Kongor.Mcp.Tests;

/// <summary>
/// Permanent Horizon 2.6 ratchet test ensuring every top-level JSON argument key read by an MCP
/// handler is declared in its ToolDefinition.InputSchema.Properties, ParameterAliases, AcceptedHandlerKeys,
/// ResolveCanonicalPropertyName, or GlobalMetaArguments (MCP-293).
/// </summary>
public sealed class HandlerSchemaPropertyRatchetTests
{
    [Fact]
    public void EveryRegisteredToolHandler_TopLevelArgumentReads_AreCoveredBySchemaOrAliasMap()
    {
        // protects: Strict unknown_argument preflight validation from breaking legitimate handler parameters (MCP-293)
        // fails_when: A handler reads arguments.TryGetProperty("foo") without adding "foo" to its ToolDefinition schema or alias map
        // seam: McpToolRegistry.GetToolRegistrations() + McpToolRegistry.IsRecognizedToolArgument() + Handler C# AST/source scan
        DirectoryInfo? repoDir = new(AppContext.BaseDirectory);
        while (repoDir != null && !File.Exists(Path.Combine(repoDir.FullName, "ProjectKongor.slnx")))
        {
            repoDir = repoDir.Parent;
        }
        repoDir.Should().NotBeNull();

        string handlersDir = Path.Combine(repoDir!.FullName, "src", "tools", "kongor-cli", "Mcp", "Handlers");
        string[] allHandlerFiles = Directory.GetFiles(handlersDir, "*.cs", SearchOption.AllDirectories);

        var registry = new McpToolRegistry(registerDefaults: true);
        var gaps = new List<string>();

        foreach (var (toolName, reg) in registry.GetToolRegistrations().OrderBy(kv => kv.Key, StringComparer.Ordinal))
        {
            Type? declaringType = reg.Handler.Method.DeclaringType;
            if (declaringType == null) continue;

            string typeName = declaringType.Name;
            string entryMethodName = reg.Handler.Method.Name;

            string[] matchingFiles = allHandlerFiles
                .Where(f =>
                {
                    string fn = Path.GetFileNameWithoutExtension(f);
                    return string.Equals(fn, typeName, StringComparison.Ordinal) ||
                           fn.StartsWith(typeName + ".", StringComparison.Ordinal);
                })
                .ToArray();

            if (matchingFiles.Length == 0)
            {
                continue;
            }

            string combinedSource = string.Join("\n", matchingFiles.Select(File.ReadAllText));
            HashSet<string> extractedKeys = ExtractTopLevelHandlerKeys(combinedSource, entryMethodName);

            foreach (string key in extractedKeys.OrderBy(k => k, StringComparer.Ordinal))
            {
                if (!McpToolRegistry.IsRecognizedToolArgument(reg.Definition, key))
                {
                    gaps.Add($"{toolName} ({typeName}.{entryMethodName}) -> '{key}'");
                }
            }
        }

        gaps.Should().BeEmpty(
            "Every top-level JSON property read by a tool handler must be declared in InputSchema.Properties, ParameterAliases, AcceptedHandlerKeys, ResolveCanonicalPropertyName, or GlobalMetaArguments:\n" +
            string.Join("\n", gaps));
    }

    private static HashSet<string> ExtractTopLevelHandlerKeys(string source, string entryMethodName)
    {
        var keys = new HashSet<string>(StringComparer.Ordinal);
        var methodsToVisit = new Queue<string>();
        var visitedMethods = new HashSet<string>(StringComparer.Ordinal);

        methodsToVisit.Enqueue(entryMethodName);

        while (methodsToVisit.Count > 0)
        {
            string methodName = methodsToVisit.Dequeue();
            if (!visitedMethods.Add(methodName))
            {
                continue;
            }

            foreach (var (paramName, body) in FindJsonElementMethods(source, methodName))
            {
                ExtractKeysForParam(body, paramName, keys);

                // Follow private/internal helper calls in the same class that pass paramName as first argument: Helper(arguments, ...)
                var callRegex = new Regex(@"\b([A-Za-z_][A-Za-z0-9_]*)\s*\(\s*" + Regex.Escape(paramName) + @"\b");
                foreach (Match cm in callRegex.Matches(body))
                {
                    string callee = cm.Groups[1].Value;
                    if (callee is not ("GetStringArg" or "TryReadString" or "GetIntArg" or "GetBoolArg" or "GetDoubleArg" or "Serialize" or "Equals"))
                    {
                        methodsToVisit.Enqueue(callee);
                    }
                }
            }
        }

        return keys;
    }

    private static List<(string ParamName, string Body)> FindJsonElementMethods(string source, string methodName)
    {
        var results = new List<(string, string)>();
        var sigRegex = new Regex(
            @"\b" + Regex.Escape(methodName) + @"\s*\(\s*(?:this\s+)?JsonElement\s+([A-Za-z_][A-Za-z0-9_]*)[^)]*\)");

        foreach (Match m in sigRegex.Matches(source))
        {
            string paramName = m.Groups[1].Value;
            int openBrace = source.IndexOf('{', m.Index + m.Length);
            if (openBrace < 0) continue;

            int depth = 1;
            int pos = openBrace + 1;
            while (pos < source.Length && depth > 0)
            {
                char c = source[pos];
                if (c == '{') depth++;
                else if (c == '}') depth--;
                pos++;
            }

            if (depth == 0)
            {
                results.Add((paramName, source.Substring(openBrace, pos - openBrace)));
            }
        }

        return results;
    }

    private static void ExtractKeysForParam(string body, string paramName, HashSet<string> keys)
    {
        string escaped = Regex.Escape(paramName);
        var tryGetRegex = new Regex(@"\b" + escaped + @"\s*\.\s*TryGetProperty\s*\(\s*""([A-Za-z0-9_]+)""");
        foreach (Match m in tryGetRegex.Matches(body))
        {
            keys.Add(m.Groups[1].Value);
        }

        var helperRegex = new Regex(@"\b(?:GetStringArg|TryReadString|GetIntArg|GetBoolArg|GetDoubleArg)\s*\(\s*" + escaped + @"\s*,([^)]+)\)");
        foreach (Match m in helperRegex.Matches(body))
        {
            foreach (Match qm in Regex.Matches(m.Groups[1].Value, @"""([A-Za-z0-9_]+)"""))
            {
                keys.Add(qm.Groups[1].Value);
            }
        }
    }
}
