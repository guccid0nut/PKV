// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

using Kongor.Modding.Core.Models;

namespace Kongor.Modding.Resolution;

/// <summary>
///     Directed graph representing mod execution order and dependency relationships.
///     A directed edge (U -> V) indicates mod U must be applied BEFORE mod V.
/// </summary>
public sealed class DependencyGraph
{
    private readonly Dictionary<string, HashSet<string>> _incoming = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, ModPackage> _nodes = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, HashSet<string>> _outgoing = new(StringComparer.OrdinalIgnoreCase);

    public IReadOnlyCollection<ModPackage> Nodes => _nodes.Values;
    public IReadOnlyDictionary<string, HashSet<string>> OutgoingEdges => _outgoing;
    public IReadOnlyDictionary<string, HashSet<string>> IncomingEdges => _incoming;

    public void AddNode(ModPackage mod)
    {
        string name = mod.Manifest.Name;
        _nodes[name] = mod;
        if (!_outgoing.ContainsKey(name)) _outgoing[name] = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (!_incoming.ContainsKey(name)) _incoming[name] = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>
    ///     Adds a directed dependency edge: 'beforeMod' must execute before 'afterMod'.
    /// </summary>
    public void AddEdge(string beforeMod, string afterMod)
    {
        if (!_nodes.ContainsKey(beforeMod) || !_nodes.ContainsKey(afterMod)) return;

        _outgoing[beforeMod].Add(afterMod);
        _incoming[afterMod].Add(beforeMod);
    }

    public bool TryGetNode(string name, out ModPackage? mod)
    {
        return _nodes.TryGetValue(name, out mod);
    }

    public static DependencyGraph Build(IEnumerable<ModPackage> mods)
    {
        DependencyGraph graph = new();
        List<ModPackage> modList = [.. mods];

        foreach (ModPackage m in modList) graph.AddNode(m);

        foreach (ModPackage m in modList)
        {
            ModManifest manifest = m.Manifest;
            string currentName = manifest.Name;

            // 1. Requirements: if m requires R, then R must apply BEFORE m (R -> m)
            foreach (ModRequirement req in manifest.Requirements)
                if (graph.TryGetNode(req.Name, out _))
                    graph.AddEdge(req.Name, currentName);

            // 2. ApplyAfter: if m has applyafter A, then A must apply BEFORE m (A -> m)
            foreach (ModOrderingRule after in manifest.ApplyAfter)
                if (graph.TryGetNode(after.Name, out _))
                    graph.AddEdge(after.Name, currentName);

            // 3. ApplyBefore: if m has applybefore B, then m must apply BEFORE B (m -> B)
            foreach (ModOrderingRule before in manifest.ApplyBefore)
                if (graph.TryGetNode(before.Name, out _))
                    graph.AddEdge(currentName, before.Name);
        }

        return graph;
    }
}
