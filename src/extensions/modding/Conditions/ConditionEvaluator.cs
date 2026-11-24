// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

using System.Collections.Concurrent;
using Kongor.Modding.Core.Models;

namespace Kongor.Modding.Conditions;

/// <summary>
///     Evaluator for HoN Boolean condition expressions with AST caching.
/// </summary>
public static class ConditionEvaluator
{
    private static readonly ConcurrentDictionary<string, ConditionNode> _astCache = new(StringComparer.Ordinal);

    public static bool Evaluate(string? expression, Func<string, string?> versionResolver)
    {
        if (string.IsNullOrWhiteSpace(expression)) return true;

        string trimmed = expression.Trim();
        ConditionNode ast = _astCache.GetOrAdd(trimmed, ConditionParser.Parse);
        return ast.Evaluate(versionResolver);
    }

    public static bool Evaluate(string? expression, IReadOnlyDictionary<string, string> activeMods)
    {
        return Evaluate(expression, name =>
        {
            if (activeMods.TryGetValue(name, out string? ver)) return ver;
            return null;
        });
    }

    public static bool Evaluate(string? expression, IEnumerable<ModPackage> activeMods)
    {
        Dictionary<string, string> dict = new(StringComparer.OrdinalIgnoreCase);
        foreach (ModPackage mod in activeMods) dict[mod.Manifest.Name] = mod.Manifest.Version;
        return Evaluate(expression, dict);
    }

    public static bool Evaluate(string? expression, IReadOnlySet<string> activeModNames)
    {
        return Evaluate(expression, name => { return activeModNames.Contains(name) ? "1.0.0" : null; });
    }
}
