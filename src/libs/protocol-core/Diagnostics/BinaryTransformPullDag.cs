// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

using System;
using System.Collections.Generic;
using Kongor.Core.Services.Native;

namespace Kongor.Core.Diagnostics;

/// <summary>
/// Phase 13 (#164): Lazy Pull-Based Binary Transformation DAG with Active-Stack DFS Cycle Guard
/// (<c>m_processedInputs</c>) &amp; Non-Destructive <see cref="VirtualByteOverlay"/> Sink
/// (<c>lib/libimhex/source/data_processor/node.cpp:25-206</c> port).
/// Distinguishes true directed cycles (back-edges in the active DFS recursion stack) from valid
/// diamond DAG shared ancestors.
/// </summary>
public sealed class BinaryTransformPullDag
{
    private sealed class DagNode
    {
        public readonly int Id;
        public readonly string Name;
        public readonly List<int> InputNodeIds;
        public readonly Func<IReadOnlyList<byte[]>, byte[]> Transform;

        public DagNode(int id, string name, List<int> inputNodeIds, Func<IReadOnlyList<byte[]>, byte[]> transform)
        {
            Id = id;
            Name = name;
            InputNodeIds = inputNodeIds;
            Transform = transform;
        }
    }

    private readonly Dictionary<int, DagNode> _nodes = new Dictionary<int, DagNode>();
    private int _nextNodeId = 1;

    public int NodeCount => _nodes.Count;

    public int AddSourceNode(string name, byte[] data)
    {
        ArgumentNullException.ThrowIfNull(name);
        ArgumentNullException.ThrowIfNull(data);
        byte[] copy = (byte[])data.Clone();
        return AddCustomNode(name, Array.Empty<int>(), _ => copy);
    }

    public int AddSliceNode(string name, int inputNodeId, int offset, int length)
    {
        return AddCustomNode(name, new int[] { inputNodeId }, inputs =>
        {
            byte[] src = inputs[0];
            if (offset < 0 || length < 0 || offset + length > src.Length)
            {
                throw new ArgumentOutOfRangeException(nameof(offset), $"Slice [{offset}, {offset + length}) exceeds input length {src.Length} in node '{name}'.");
            }

            byte[] sliced = new byte[length];
            Buffer.BlockCopy(src, offset, sliced, 0, length);
            return sliced;
        });
    }

    public int AddXorNode(string name, int inputNodeId, byte[] xorKey)
    {
        ArgumentNullException.ThrowIfNull(xorKey);
        if (xorKey.Length == 0)
        {
            throw new ArgumentException("XOR key cannot be empty.", nameof(xorKey));
        }

        byte[] keyCopy = (byte[])xorKey.Clone();
        return AddCustomNode(name, new int[] { inputNodeId }, inputs =>
        {
            byte[] src = inputs[0];
            byte[] dst = new byte[src.Length];
            for (int i = 0; i < src.Length; i++)
            {
                dst[i] = (byte)(src[i] ^ keyCopy[i % keyCopy.Length]);
            }

            return dst;
        });
    }

    public int AddBoundedInflateNode(
        string name,
        int inputNodeId,
        bool isGzip = false,
        int maxOutputBytes = BoundedDecompressionPaginator.DefaultMaxDecompressedBytes)
    {
        return AddCustomNode(name, new int[] { inputNodeId }, inputs =>
        {
            BoundedDecompressionResult res = BoundedDecompressionPaginator.DecompressBounded(inputs[0], isGzip, maxOutputBytes);
            return res.OutputBytes;
        });
    }

    public int AddCustomNode(
        string name,
        IReadOnlyList<int> inputNodeIds,
        Func<IReadOnlyList<byte[]>, byte[]> transform)
    {
        ArgumentNullException.ThrowIfNull(name);
        ArgumentNullException.ThrowIfNull(inputNodeIds);
        ArgumentNullException.ThrowIfNull(transform);

        int id = _nextNodeId++;
        List<int> inputsCopy = new List<int>(inputNodeIds.Count);
        for (int i = 0; i < inputNodeIds.Count; i++)
        {
            inputsCopy.Add(inputNodeIds[i]);
        }

        _nodes[id] = new DagNode(id, name, inputsCopy, transform);
        return id;
    }

    /// <summary>
    /// Connects (or appends) <paramref name="sourceNodeId"/> as an input of <paramref name="targetNodeId"/>,
    /// enabling dynamic graph rewiring (including cycle testing).
    /// </summary>
    public void AddInputEdge(int targetNodeId, int sourceNodeId)
    {
        if (!_nodes.TryGetValue(targetNodeId, out DagNode? target))
        {
            throw new KeyNotFoundException($"Target node ID {targetNodeId} does not exist.");
        }

        if (!_nodes.ContainsKey(sourceNodeId))
        {
            throw new KeyNotFoundException($"Source node ID {sourceNodeId} does not exist.");
        }

        target.InputNodeIds.Add(sourceNodeId);
    }

    /// <summary>
    /// Lazily pulls and evaluates <paramref name="sinkNodeId"/> and its upstream dependencies.
    /// Uses an active-DFS recursion set (<c>m_processedInputs</c> in <c>node.cpp:179-206</c>) that inserts
    /// a node before evaluating its inputs and removes it upon completion—catching directed cycles in O(1)
    /// while permitting diamond DAG shared ancestors.
    /// </summary>
    public byte[] EvaluateNode(int sinkNodeId, out int totalUniqueNodesEvaluated)
    {
        HashSet<int> activeDfsStack = new HashSet<int>();
        Dictionary<int, byte[]> memoCache = new Dictionary<int, byte[]>();

        byte[] output = EvaluateNodeRecursive(sinkNodeId, activeDfsStack, memoCache);
        totalUniqueNodesEvaluated = memoCache.Count;
        return output;
    }

    public byte[] EvaluateNode(int sinkNodeId)
    {
        return EvaluateNode(sinkNodeId, out _);
    }

    /// <summary>
    /// Evaluates <paramref name="sinkNodeId"/> and stages its output non-destructively into a
    /// <see cref="VirtualByteOverlay"/> over <paramref name="baseBuffer"/> at <paramref name="patchOffset"/>
    /// (<c>node.cpp:160-177 setOverlayData</c>).
    /// </summary>
    public VirtualByteOverlay EvaluateToVirtualOverlay(
        int sinkNodeId,
        ReadOnlyMemory<byte> baseBuffer,
        long patchOffset)
    {
        byte[] transformed = EvaluateNode(sinkNodeId);
        VirtualByteOverlay overlay = new VirtualByteOverlay(baseBuffer);
        overlay.ApplyOverlay(patchOffset, transformed);
        return overlay;
    }

    private byte[] EvaluateNodeRecursive(
        int nodeId,
        HashSet<int> activeDfsStack,
        Dictionary<int, byte[]> memoCache)
    {
        if (!_nodes.TryGetValue(nodeId, out DagNode? node))
        {
            throw new KeyNotFoundException($"DAG node ID {nodeId} was not found.");
        }

        // Active-stack DFS cycle check (node.cpp:179-206):
        // If nodeId is currently on the active recursion stack, we hit a directed cycle!
        if (!activeDfsStack.Add(nodeId))
        {
            throw new InvalidOperationException($"Directed cycle detected in BinaryTransformPullDag at node #{nodeId} ('{node.Name}').");
        }

        try
        {
            // Diamond DAG memoization: if already evaluated along another branch, reuse the result
            if (memoCache.TryGetValue(nodeId, out byte[]? cached))
            {
                return cached;
            }

            List<byte[]> inputBuffers = new List<byte[]>(node.InputNodeIds.Count);
            for (int i = 0; i < node.InputNodeIds.Count; i++)
            {
                int predId = node.InputNodeIds[i];
                byte[] predOutput = EvaluateNodeRecursive(predId, activeDfsStack, memoCache);
                inputBuffers.Add(predOutput);
            }

            byte[] result = node.Transform(inputBuffers);
            memoCache[nodeId] = result;
            return result;
        }
        finally
        {
            // Post-order removal from active DFS stack so shared diamond ancestors do not false-positive as cycles
            activeDfsStack.Remove(nodeId);
        }
    }
}
