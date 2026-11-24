// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

using Kongor.Modding.Conditions;
using Kongor.Modding.Core.Models;

namespace Kongor.Modding.Resolution;

public sealed record SortResult
{
    public required bool Success { get; init; }
    public IReadOnlyList<ModPackage> ExecutionPlan { get; init; } = [];
    public IReadOnlyList<string> CycleReports { get; init; } = [];
    public IReadOnlyList<string> ValidationErrors { get; init; } = [];
}

/// <summary>
///     High-performance topological sorter using Kahn's algorithm and Tarjan's SCC cycle detector.
/// </summary>
public static class TopologicalSorter
{
    /// <summary>
    ///     Validates dependencies, incompatibilities, and produces a sorted execution plan.
    /// </summary>
    public static SortResult ResolveAndSort(IEnumerable<ModPackage> mods)
    {
        List<ModPackage> modList = [.. mods];
        DependencyGraph graph = DependencyGraph.Build(modList);
        List<string> validationErrors = [];

        Dictionary<string, ModPackage> modMap = new(StringComparer.OrdinalIgnoreCase);
        foreach (ModPackage m in modList) modMap[m.Manifest.Name] = m;

        // 1. Validate requirements and incompatibilities
        foreach (ModPackage mod in modList)
        {
            ModManifest manifest = mod.Manifest;

            // Check requirements
            foreach (ModRequirement req in manifest.Requirements)
                if (!modMap.TryGetValue(req.Name, out ModPackage? targetMod))
                {
                    validationErrors.Add($"Mod '{manifest.Name}' requires missing mod '{req.Name}'");
                }
                else if (!string.IsNullOrEmpty(req.VersionExpression))
                {
                    VersionRangeExpression verExpr = VersionRangeExpression.Parse(req.VersionExpression);
                    if (!verExpr.IsSatisfiedBy(targetMod.Manifest.Version))
                        validationErrors.Add(
                            $"Mod '{manifest.Name}' requires '{req.Name}' [{req.VersionExpression}], but installed version is '{targetMod.Manifest.Version}'");
                }

            // Check incompatibilities
            foreach (ModIncompatibility incomp in manifest.Incompatibilities)
                if (modMap.TryGetValue(incomp.Name, out ModPackage? targetMod))
                {
                    if (string.IsNullOrEmpty(incomp.VersionExpression))
                    {
                        validationErrors.Add($"Mod '{manifest.Name}' is incompatible with enabled mod '{incomp.Name}'");
                    }
                    else
                    {
                        VersionRangeExpression verExpr = VersionRangeExpression.Parse(incomp.VersionExpression);
                        if (verExpr.IsSatisfiedBy(targetMod.Manifest.Version))
                            validationErrors.Add(
                                $"Mod '{manifest.Name}' is incompatible with enabled mod '{incomp.Name}' [{incomp.VersionExpression}] (installed: '{targetMod.Manifest.Version}')");
                    }
                }
        }

        if (validationErrors.Count > 0)
            return new SortResult
            {
                Success = false,
                ExecutionPlan = [],
                CycleReports = [],
                ValidationErrors = validationErrors
            };

        // 2. Kahn's Topological Sort
        Dictionary<string, int> inDegree = new(StringComparer.OrdinalIgnoreCase);
        foreach (ModPackage node in graph.Nodes)
        {
            string name = node.Manifest.Name;
            inDegree[name] = graph.IncomingEdges.TryGetValue(name, out HashSet<string>? incoming) ? incoming.Count : 0;
        }

        // Use deterministic sorting for nodes with 0 in-degree (alphabetical by name)
        PriorityQueue<ModPackage, string> queue = new();
        foreach (ModPackage node in graph.Nodes)
        {
            string name = node.Manifest.Name;
            if (inDegree[name] == 0) queue.Enqueue(node, name);
        }

        List<ModPackage> executionPlan = [];

        while (queue.Count > 0)
        {
            ModPackage current = queue.Dequeue();
            executionPlan.Add(current);
            string currentName = current.Manifest.Name;

            if (graph.OutgoingEdges.TryGetValue(currentName, out HashSet<string>? neighbors))
                foreach (string neighborName in neighbors)
                {
                    inDegree[neighborName]--;
                    if (inDegree[neighborName] == 0 && graph.TryGetNode(neighborName, out ModPackage? neighborNode))
                        queue.Enqueue(neighborNode!, neighborName);
                }
        }

        // 3. Cycle Detection via Tarjan's SCC if not all nodes were visited
        if (executionPlan.Count < graph.Nodes.Count)
        {
            List<List<string>> cycles = DetectCyclesTarjan(graph);
            List<string> cycleReports =
                [.. cycles.Select(c => $"Cyclic dependency detected: {string.Join(" -> ", c)}")];

            return new SortResult
            {
                Success = false,
                ExecutionPlan = executionPlan,
                CycleReports = cycleReports,
                ValidationErrors = validationErrors
            };
        }

        return new SortResult
        {
            Success = true,
            ExecutionPlan = executionPlan,
            CycleReports = [],
            ValidationErrors = []
        };
    }

    /// <summary>
    ///     Tarjan's Strongly Connected Components algorithm to find cyclic dependencies.
    /// </summary>
    public static List<List<string>> DetectCyclesTarjan(DependencyGraph graph)
    {
        Dictionary<string, int> indices = new(StringComparer.OrdinalIgnoreCase);
        Dictionary<string, int> lowlink = new(StringComparer.OrdinalIgnoreCase);
        HashSet<string> onStack = new(StringComparer.OrdinalIgnoreCase);
        Stack<string> stack = new();
        List<List<string>> sccs = [];
        int index = 0;

        foreach (ModPackage node in graph.Nodes)
        {
            string name = node.Manifest.Name;
            if (!indices.ContainsKey(name))
                StrongConnect(name, ref index, indices, lowlink, onStack, stack, graph, sccs);
        }

        // Filter out single-node SCCs without self-loops
        return
        [
            .. sccs.Where(scc =>
                scc.Count > 1 || (scc.Count == 1 &&
                                  graph.OutgoingEdges.TryGetValue(scc[0], out HashSet<string>? outg) &&
                                  outg.Contains(scc[0])))
        ];
    }

    private static void StrongConnect(
        string v,
        ref int index,
        Dictionary<string, int> indices,
        Dictionary<string, int> lowlink,
        HashSet<string> onStack,
        Stack<string> stack,
        DependencyGraph graph,
        List<List<string>> sccs)
    {
        indices[v] = index;
        lowlink[v] = index;
        index++;
        stack.Push(v);
        onStack.Add(v);

        if (graph.OutgoingEdges.TryGetValue(v, out HashSet<string>? neighbors))
            foreach (string w in neighbors)
                if (!indices.TryGetValue(w, out int wIndex))
                {
                    StrongConnect(w, ref index, indices, lowlink, onStack, stack, graph, sccs);
                    lowlink[v] = Math.Min(lowlink[v], lowlink[w]);
                }
                else if (onStack.Contains(w))
                {
                    lowlink[v] = Math.Min(lowlink[v], wIndex);
                }

        if (lowlink[v] == indices[v])
        {
            List<string> scc = [];
            string w;
            do
            {
                w = stack.Pop();
                onStack.Remove(w);
                scc.Add(w);
            } while (!w.Equals(v, StringComparison.OrdinalIgnoreCase));

            sccs.Add(scc);
        }
    }
}
