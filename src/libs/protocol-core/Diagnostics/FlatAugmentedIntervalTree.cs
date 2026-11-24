// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

using System;
using System.Collections.Generic;

namespace Kongor.Core.Diagnostics;

/// <summary>
/// Represents an inclusive address/byte interval <c>[Start, End]</c> associated with a payload <typeparamref name="TValue"/>.
/// </summary>
public readonly record struct IntervalEntry<TValue>(
    ulong Start,
    ulong End,
    TValue Value);

/// <summary>
/// Phase 13 (#155): Cache-Oblivious Flat-Vector Augmented Interval Tree (<c>wolv::container::IntervalTree</c> port).
/// Uses lazy O(1) insertion into a contiguous list, iterative bottom-up balanced tree construction with subtree
/// <c>MaxEnd</c> augmentation, and explicit-stack O(log N + K) overlap queries with zero recursion.
/// </summary>
public sealed class FlatAugmentedIntervalTree<TValue>
{
    private struct TreeNode
    {
        public ulong Start;
        public ulong End;
        public ulong MaxEnd;
        public int DataIndex;
        public int LeftChild;
        public int RightChild;
    }

    private readonly struct BuildRange
    {
        public readonly int NodeIndex;
        public readonly int Low;
        public readonly int High;
        public readonly bool ChildrenVisited;

        public BuildRange(int nodeIndex, int low, int high, bool childrenVisited)
        {
            NodeIndex = nodeIndex;
            Low = low;
            High = high;
            ChildrenVisited = childrenVisited;
        }
    }

    private readonly List<IntervalEntry<TValue>> _data = new List<IntervalEntry<TValue>>();
    private TreeNode[] _nodes = Array.Empty<TreeNode>();
    private int _rootIndex = -1;
    private bool _dirty;

    /// <summary>
    /// Gets the number of intervals stored in the tree.
    /// </summary>
    public int Count => _data.Count;

    /// <summary>
    /// Gets whether the underlying flat tree structure needs recomputation before the next query.
    /// </summary>
    public bool IsDirty => _dirty;

    /// <summary>
    /// Clears all intervals from the tree.
    /// </summary>
    public void Clear()
    {
        _data.Clear();
        _nodes = Array.Empty<TreeNode>();
        _rootIndex = -1;
        _dirty = false;
    }

    /// <summary>
    /// Inserts an inclusive interval <c>[start, end]</c> in amortized O(1) time and marks the tree dirty for lazy rebuild.
    /// If <paramref name="end"/> is less than <paramref name="start"/>, the endpoints are normalized.
    /// </summary>
    public void Insert(ulong start, ulong end, TValue value)
    {
        ulong normStart = start <= end ? start : end;
        ulong normEnd = start <= end ? end : start;
        _data.Add(new IntervalEntry<TValue>(normStart, normEnd, value));
        _dirty = true;
    }

    /// <summary>
    /// Ensures the flat augmented interval tree is sorted and built using an iterative explicit stack (zero recursion).
    /// Modeled on <c>wolv::container::IntervalTree::computeTree</c> (<c>interval_tree.hpp:244-279</c>).
    /// </summary>
    public void ComputeTree()
    {
        if (!_dirty)
        {
            return;
        }

        int count = _data.Count;
        if (count == 0)
        {
            _nodes = Array.Empty<TreeNode>();
            _rootIndex = -1;
            _dirty = false;
            return;
        }

        _data.Sort(static (IntervalEntry<TValue> a, IntervalEntry<TValue> b) =>
        {
            int startCmp = a.Start.CompareTo(b.Start);
            return startCmp != 0 ? startCmp : a.End.CompareTo(b.End);
        });

        if (_nodes.Length != count)
        {
            _nodes = new TreeNode[count];
        }

        // First pass: assign each node to the midpoint of its [Low, High] slice
        // and link LeftChild / RightChild deterministically without recursion.
        _rootIndex = BuildSubtreeIterative(0, count - 1);
        _dirty = false;
    }

    private int BuildSubtreeIterative(int fullLow, int fullHigh)
    {
        int totalCount = _data.Count;
        // Node index in _nodes is identical to the sorted data index `mid = low + (high - low) / 2`!
        // Because every [low, high] partition chooses a unique midpoint `mid`, we can index _nodes directly by `mid`.
        Stack<BuildRange> stack = new Stack<BuildRange>(64);
        int rootMid = fullLow + ((fullHigh - fullLow) / 2);
        stack.Push(new BuildRange(rootMid, fullLow, fullHigh, false));

        while (stack.Count > 0)
        {
            BuildRange current = stack.Pop();
            int mid = current.NodeIndex;
            int low = current.Low;
            int high = current.High;

            int leftMid = -1;
            if (low <= mid - 1)
            {
                leftMid = low + ((mid - 1 - low) / 2);
            }

            int rightMid = -1;
            if (mid + 1 <= high)
            {
                rightMid = (mid + 1) + ((high - (mid + 1)) / 2);
            }

            if (!current.ChildrenVisited)
            {
                IntervalEntry<TValue> entry = _data[mid];
                _nodes[mid] = new TreeNode
                {
                    Start = entry.Start,
                    End = entry.End,
                    MaxEnd = entry.End,
                    DataIndex = mid,
                    LeftChild = leftMid,
                    RightChild = rightMid
                };

                stack.Push(new BuildRange(mid, low, high, true));
                if (rightMid >= 0 && rightMid < totalCount)
                {
                    stack.Push(new BuildRange(rightMid, mid + 1, high, false));
                }

                if (leftMid >= 0 && leftMid < totalCount)
                {
                    stack.Push(new BuildRange(leftMid, low, mid - 1, false));
                }
            }
            else
            {
                ulong maxEnd = _nodes[mid].End;
                if (leftMid >= 0 && _nodes[leftMid].MaxEnd > maxEnd)
                {
                    maxEnd = _nodes[leftMid].MaxEnd;
                }

                if (rightMid >= 0 && _nodes[rightMid].MaxEnd > maxEnd)
                {
                    maxEnd = _nodes[rightMid].MaxEnd;
                }

                _nodes[mid].MaxEnd = maxEnd;
            }
        }

        return rootMid;
    }

    /// <summary>
    /// Queries all intervals overlapping the inclusive range <c>[start, end]</c> in O(log N + K) time using an explicit stack.
    /// Modeled on <c>wolv::container::IntervalTree::findIndices</c> (<c>interval_tree.hpp:65-116</c>).
    /// </summary>
    public List<IntervalEntry<TValue>> QueryOverlapping(ulong start, ulong end)
    {
        List<IntervalEntry<TValue>> results = new List<IntervalEntry<TValue>>();
        QueryOverlapping(start, end, results);
        return results;
    }

    /// <summary>
    /// Populates <paramref name="results"/> with all intervals overlapping the inclusive range <c>[start, end]</c>.
    /// Results are ordered by ascending <c>(Start, End)</c>.
    /// </summary>
    public void QueryOverlapping(ulong start, ulong end, List<IntervalEntry<TValue>> results)
    {
        ArgumentNullException.ThrowIfNull(results);
        ComputeTree();

        if (_rootIndex < 0 || _nodes.Length == 0)
        {
            return;
        }

        ulong normStart = start <= end ? start : end;
        ulong normEnd = start <= end ? end : start;

        Stack<int> stack = new Stack<int>(64);
        stack.Push(_rootIndex);

        while (stack.Count > 0)
        {
            int nodeIdx = stack.Pop();
            ref readonly TreeNode node = ref _nodes[nodeIdx];

            // If the entire subtree's maximum end is before our query start, prune the entire subtree
            if (node.MaxEnd < normStart)
            {
                continue;
            }

            // Check overlap with current node: [node.Start, node.End] overlaps [normStart, normEnd]
            if (node.Start <= normEnd && node.End >= normStart)
            {
                results.Add(_data[node.DataIndex]);
            }

            // Right subtree can only contain overlapping intervals if node.Start <= normEnd
            // (since all nodes in right subtree have Start >= node.Start)
            if (node.RightChild >= 0 && node.Start <= normEnd)
            {
                stack.Push(node.RightChild);
            }

            // Left subtree can contain overlapping intervals if leftChild.MaxEnd >= normStart
            if (node.LeftChild >= 0 && _nodes[node.LeftChild].MaxEnd >= normStart)
            {
                stack.Push(node.LeftChild);
            }
        }

        results.Sort(static (IntervalEntry<TValue> a, IntervalEntry<TValue> b) =>
        {
            int cmp = a.Start.CompareTo(b.Start);
            return cmp != 0 ? cmp : a.End.CompareTo(b.End);
        });
    }

    /// <summary>
    /// Finds the first interval whose <c>Start</c> is strictly greater than <paramref name="address"/> in O(log N) time,
    /// allowing callers to jump across unmapped <c>MEM_FREE</c> virtual address holes (<c>interval_tree.hpp:177-206</c>).
    /// </summary>
    public IntervalEntry<TValue>? FindNextInterval(ulong address)
    {
        ComputeTree();
        if (_data.Count == 0)
        {
            return null;
        }

        int low = 0;
        int high = _data.Count - 1;
        int candidateIdx = -1;

        while (low <= high)
        {
            int mid = low + ((high - low) / 2);
            if (_data[mid].Start > address)
            {
                candidateIdx = mid;
                high = mid - 1;
            }
            else
            {
                low = mid + 1;
            }
        }

        return candidateIdx >= 0 ? _data[candidateIdx] : null;
    }

    /// <summary>
    /// Finds the last interval whose <c>Start</c> is strictly less than <paramref name="address"/> in O(log N) time
    /// (<c>interval_tree.hpp:208-238</c>).
    /// </summary>
    public IntervalEntry<TValue>? FindPrevInterval(ulong address)
    {
        ComputeTree();
        if (_data.Count == 0)
        {
            return null;
        }

        int low = 0;
        int high = _data.Count - 1;
        int candidateIdx = -1;

        while (low <= high)
        {
            int mid = low + ((high - low) / 2);
            if (_data[mid].Start < address)
            {
                candidateIdx = mid;
                low = mid + 1;
            }
            else
            {
                high = mid - 1;
            }
        }

        return candidateIdx >= 0 ? _data[candidateIdx] : null;
    }
}
