// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Kongor.Core.Diagnostics;

/// <summary>
/// Phase 13 (#163): Keyed + Call-Site Deduplicated <c>DoLaterOnce</c> Main-Thread Coalescer &amp; Win32
/// <c>SetThreadDescription</c> Helper (<c>task_manager.cpp:25-47, 506-542</c> port).
/// Fixes upstream ImHex's loop-call-site collapse hazard by providing both a <c>[CallerFilePath]</c>
/// location key overload and an explicit per-item string key overload.
/// </summary>
internal sealed class DeferredOnceDispatcher
{
    private readonly object _syncLock = new object();
    private readonly Dictionary<string, Action> _pendingByKey = new Dictionary<string, Action>(StringComparer.Ordinal);
    private readonly List<string> _insertionOrder = new List<string>();
    private long _coalescedDuplicatesCount;
    private long _executedCallbacksCount;

    public int PendingCount
    {
        get
        {
            lock (_syncLock)
            {
                return _pendingByKey.Count;
            }
        }
    }

    public long CoalescedDuplicatesCount
    {
        get
        {
            lock (_syncLock)
            {
                return _coalescedDuplicatesCount;
            }
        }
    }

    public long ExecutedCallbacksCount
    {
        get
        {
            lock (_syncLock)
            {
                return _executedCallbacksCount;
            }
        }
    }

    /// <summary>
    /// Enqueues <paramref name="callback"/> to run at most once on the next <see cref="RunDeferredCalls"/> drain,
    /// deduplicating repeated calls from the same source <c>(file, member, line)</c> location (<c>task_manager.cpp:506-520</c>).
    /// Returns true if newly enqueued, or false if coalesced into an existing pending entry.
    /// </summary>
    public bool DoLaterOnce(
        Action callback,
        [CallerFilePath] string file = "",
        [CallerMemberName] string member = "",
        [CallerLineNumber] int line = 0)
    {
        ArgumentNullException.ThrowIfNull(callback);
        string locationKey = $"{file}:{member}:{line}";
        return DoLaterOnce(locationKey, callback);
    }

    /// <summary>
    /// Enqueues <paramref name="callback"/> keyed by an explicit <paramref name="explicitKey"/> (e.g., entity or file path),
    /// allowing callers inside a <c>foreach</c> loop to deduplicate per logical target without collapsing distinct
    /// loop items that share the same source line number.
    /// Returns true if newly enqueued, or false if coalesced into an existing pending entry.
    /// </summary>
    public bool DoLaterOnce(string explicitKey, Action callback)
    {
        if (string.IsNullOrWhiteSpace(explicitKey))
        {
            throw new ArgumentException("Explicit deduplication key cannot be null or whitespace.", nameof(explicitKey));
        }

        ArgumentNullException.ThrowIfNull(callback);

        lock (_syncLock)
        {
            if (_pendingByKey.ContainsKey(explicitKey))
            {
                _pendingByKey[explicitKey] = callback;
                _coalescedDuplicatesCount++;
                return false;
            }

            _pendingByKey[explicitKey] = callback;
            _insertionOrder.Add(explicitKey);
            return true;
        }
    }

    /// <summary>
    /// Atomically snapshots and drains all pending deferred callbacks in deterministic insertion order
    /// (<c>task_manager.cpp:525-542</c>). Callbacks that enqueue new <c>DoLaterOnce</c> work during execution
    /// are scheduled for the next tick rather than causing re-entrant loops.
    /// </summary>
    public int RunDeferredCalls()
    {
        List<Action> toExecute;
        lock (_syncLock)
        {
            if (_insertionOrder.Count == 0)
            {
                return 0;
            }

            toExecute = new List<Action>(_insertionOrder.Count);
            for (int i = 0; i < _insertionOrder.Count; i++)
            {
                string key = _insertionOrder[i];
                if (_pendingByKey.TryGetValue(key, out Action? fn))
                {
                    toExecute.Add(fn);
                }
            }

            _pendingByKey.Clear();
            _insertionOrder.Clear();
            _executedCallbacksCount += toExecute.Count;
        }

        for (int i = 0; i < toExecute.Count; i++)
        {
            toExecute[i]();
        }

        return toExecute.Count;
    }

    /// <summary>
    /// Sets the native Win32 thread description for the calling thread (<c>task_manager.cpp:25-47</c>)
    /// so it appears by name in Windows minidumps (<c>.dmp</c>) and WinDbg (<c>~*k</c>).
    /// </summary>
    public static bool TrySetCurrentThreadDescription(string threadName)
    {
        if (string.IsNullOrWhiteSpace(threadName) || !OperatingSystem.IsWindows())
        {
            return false;
        }

        try
        {
            int hr = SetThreadDescription(GetCurrentThread(), threadName);
            return hr >= 0;
        }
        catch
        {
            return false;
        }
    }

    [DllImport("kernel32.dll", ExactSpelling = true)]
    private static extern IntPtr GetCurrentThread();

    [DllImport("kernel32.dll", ExactSpelling = true, CharSet = CharSet.Unicode)]
    private static extern int SetThreadDescription(IntPtr hThread, [MarshalAs(UnmanagedType.LPWStr)] string lpThreadDescription);
}
