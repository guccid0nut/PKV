// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

// ============================================================================
// File: SlidingBitmaskAntiReplay.cs
// System: Kongor Core Netcode - RFC 4303 64-Bit Sliding Bitmask Anti-Replay Engine
// Policy: Strict Explicit Typing (NO 'var')
// ============================================================================

namespace ASPIRE.Common.Netcode;

/// <summary>
///     Classification outcome for datagram sequence admission.
/// </summary>
public enum AntiReplayStatus : byte
{
    /// <summary>
    ///     Sequence is strictly newer than the maximum seen sequence and advanced the sliding window.
    /// </summary>
    AcceptedNewest = 0,

    /// <summary>
    ///     Sequence arrived out-of-order within the 64-packet historical window and has not been seen before.
    /// </summary>
    AcceptedReordered = 1,

    /// <summary>
    ///     Sequence was already observed within the current sliding window (replay attack / duplicate).
    /// </summary>
    Duplicate = 2,

    /// <summary>
    ///     Sequence is older than 64 packets behind the maximum seen sequence (stale / expired).
    /// </summary>
    Stale = 3
}

/// <summary>
///     High-performance, zero-allocation sliding bitmask anti-replay filter based on RFC 4303.
///     Tracks a 64-packet window using a single 64-bit unsigned integer bitmask (<see cref="ulong"/>).
///     Provides O(1) checks and insertions with zero heap allocations.
/// </summary>
public struct SlidingBitmaskAntiReplay
{
    public const int WindowSize = 64;

    private ulong windowMask;
    private uint maxSequence;
    private bool initialized;

    /// <summary>
    ///     The highest sequence number observed so far.
    /// </summary>
    public readonly uint MaxSequence => maxSequence;

    /// <summary>
    ///     The current 64-bit historical window bitmask.
    /// </summary>
    public readonly ulong WindowMask => windowMask;

    /// <summary>
    ///     Whether this filter has processed at least one datagram.
    /// </summary>
    public readonly bool IsInitialized => initialized;

    /// <summary>
    ///     Evaluates an incoming sequence number against the sliding window.
    ///     If valid, the sequence is marked as seen and admitted.
    /// </summary>
    public AntiReplayStatus CheckAndAdmit(uint sequence)
    {
        if (!initialized)
        {
            maxSequence = sequence;
            windowMask = 1UL;
            initialized = true;

            return AntiReplayStatus.AcceptedNewest;
        }

        // Modular distance arithmetic for uint32 rollover support
        int diff = unchecked((int)(sequence - maxSequence));

        if (diff > 0)
        {
            // Newer packet advancing the window
            if (diff < WindowSize)
            {
                windowMask = (windowMask << diff) | 1UL;
            }
            else
            {
                // Jumped ahead past entire window; all old packets are now stale
                windowMask = 1UL;
            }

            maxSequence = sequence;

            return AntiReplayStatus.AcceptedNewest;
        }

        // Packet is less than or equal to maxSequence
        int absDiff = -diff;

        if (absDiff >= WindowSize)
        {
            // Beyond 64-packet historical window
            return AntiReplayStatus.Stale;
        }

        ulong bit = 1UL << absDiff;

        if ((windowMask & bit) != 0UL)
        {
            // Bit already set: duplicate / replay
            return AntiReplayStatus.Duplicate;
        }

        // Out of order but within window and unseen: admit and mark bit
        windowMask |= bit;

        return AntiReplayStatus.AcceptedReordered;
    }

    /// <summary>
    ///     Resets the filter state to uninitialized.
    /// </summary>
    public void Reset()
    {
        windowMask = 0UL;
        maxSequence = 0U;
        initialized = false;
    }
}
