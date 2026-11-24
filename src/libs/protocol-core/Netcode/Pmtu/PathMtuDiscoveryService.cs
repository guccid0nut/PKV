// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

// ============================================================================
// File: PathMtuDiscoveryService.cs
// System: Kongor Core Netcode - RFC 4821 Packetization Layer Path MTU Discovery
// Policy: Strict Explicit Typing (NO 'var')
// ============================================================================

using System.Buffers.Binary;

namespace ASPIRE.Common.Netcode;

/// <summary>
///     Lifecycle states for the RFC 4821 Path MTU Discovery state machine.
/// </summary>
public enum PmtuState : byte
{
    Disabled = 0,
    Searching = 1,
    Validating = 2,
    Confirmed = 3,
    BlackHole = 4
}

/// <summary>
///     High-performance zero-allocation implementation of RFC 4821 Packetization Layer
///     Path MTU Discovery (PLPMTUD) tailored for competitive UDP game transport.
///     Probes intermediate network links across standard MTU plateau steps without
///     relying on ICMP 'Fragmentation Needed' feedback (RFC 2923 black-hole prevention).
/// </summary>
public sealed class PathMtuDiscoveryService
{
    public const ushort BaseMtuPayload = 1200; // Guaranteed minimum IPv4/IPv6 UDP payload floor
    public const ushort MaxMtuPayload = 1472;  // Standard 1500-byte MTU Ethernet UDP payload ceiling

    public const uint ProbeMagic = 0x55544D50; // "PMTU" in Little Endian

    // RFC 4821 Table 1 Standard MTU Plateaus (UDP Payload Sizing):
    // 1200: Safe baseline for IPv6 tunneling and cellular CGNAT/464XLAT
    // 1280: Standard IPv6 Minimum Link MTU (1280 - 40 - 8 = 1232 payload, rounded to 1280 IP)
    // 1360: WireGuard, IPSec ESP, and GTP-U mobile cellular tunnels
    // 1420: OpenVPN, GRE, and Cloudflare WARP tunnels
    // 1472: Standard IEEE 802.3 Ethernet (1500 IP - 20 IPv4 - 8 UDP = 1472 payload)
    public static readonly ushort[] StandardPlateaus = [1200, 1280, 1360, 1420, 1472];

    private readonly TimeProvider _timeProvider;
    private readonly TimeSpan _searchProbeInterval = TimeSpan.FromMilliseconds(500);
    private readonly TimeSpan _probeValidationTimeout = TimeSpan.FromMilliseconds(1500);
    private readonly TimeSpan _confirmedReprobeInterval = TimeSpan.FromSeconds(60);
    private readonly Lock _sync = new();

    private PmtuState _state = PmtuState.Searching;
    private int _plateauIndex;
    private ushort _currentMtu = BaseMtuPayload;
    private ushort _confirmedMtu = BaseMtuPayload;

    private uint _activeProbeSequence;
    private ushort _activeProbeSize;
    private long _lastProbeDispatchedTicks;
    private long _lastConfirmedTicks;
    private int _consecutiveProbeFailures;

    public PmtuState State
    {
        get
        {
            lock (_sync)
            {
                return _state;
            }
        }
    }

    public ushort CurrentMtu
    {
        get
        {
            lock (_sync)
            {
                return _currentMtu;
            }
        }
    }

    public ushort ConfirmedMtu
    {
        get
        {
            lock (_sync)
            {
                return _confirmedMtu;
            }
        }
    }

    public uint ActiveProbeSequence
    {
        get
        {
            lock (_sync)
            {
                return _activeProbeSequence;
            }
        }
    }

    public ushort ActiveProbeSize
    {
        get
        {
            lock (_sync)
            {
                return _activeProbeSize;
            }
        }
    }

    public int PlateauIndex
    {
        get
        {
            lock (_sync)
            {
                return _plateauIndex;
            }
        }
    }

    public PathMtuDiscoveryService(TimeProvider? timeProvider = null, ushort initialMtu = BaseMtuPayload)
    {
        _timeProvider = timeProvider ?? TimeProvider.System;
        _currentMtu = Math.Clamp(initialMtu, BaseMtuPayload, MaxMtuPayload);
        _confirmedMtu = _currentMtu;
        _plateauIndex = GetPlateauIndexForSize(_confirmedMtu);
        _lastConfirmedTicks = _timeProvider.GetTimestamp();
    }

    /// <summary>
    ///     Evaluates whether a new PLPMTUD probe packet should be emitted.
    ///     Returns true and sets probeSizeBytes to the target payload size if probing is due.
    /// </summary>
    public bool ShouldProbe(out ushort probeSizeBytes)
    {
        lock (_sync)
        {
            probeSizeBytes = 0;
            if (_state == PmtuState.Disabled)
            {
                return false;
            }

            long nowTicks = _timeProvider.GetTimestamp();

            // 1. If currently validating an in-flight probe, check for timeout
            if (_state == PmtuState.Validating)
            {
                if (_timeProvider.GetElapsedTime(_lastProbeDispatchedTicks, nowTicks) >= _probeValidationTimeout)
                {
                    OnProbeTimeoutUnsafe(nowTicks);
                }
                return false;
            }

            // 2. If at maximum confirmed plateau, periodically re-probe to detect path expansions
            if (_state == PmtuState.Confirmed)
            {
                if (_plateauIndex >= StandardPlateaus.Length - 1)
                {
                    return false;
                }

                if (_timeProvider.GetElapsedTime(_lastConfirmedTicks, nowTicks) < _confirmedReprobeInterval)
                {
                    return false;
                }

                _state = PmtuState.Searching;
            }

            // 3. In Searching or BlackHole state, verify spacing between probe attempts
            if (_timeProvider.GetElapsedTime(_lastProbeDispatchedTicks, nowTicks) < _searchProbeInterval)
            {
                return false;
            }

            int nextIndex = _plateauIndex + 1;
            if (nextIndex < StandardPlateaus.Length)
            {
                probeSizeBytes = StandardPlateaus[nextIndex];
                return true;
            }

            _state = PmtuState.Confirmed;
            return false;
        }
    }

    /// <summary>
    ///     Registers the dispatch of an MTU probe datagram of a specific size and sequence number.
    /// </summary>
    public void OnProbeDispatched(uint sequence, ushort probeSize)
    {
        lock (_sync)
        {
            _activeProbeSequence = sequence;
            _activeProbeSize = probeSize;
            _lastProbeDispatchedTicks = _timeProvider.GetTimestamp();
            _state = PmtuState.Validating;
        }
    }

    /// <summary>
    ///     Processes an incoming ACK from the client. If the ACK sequence matches the active probe,
    ///     the probed plateau is confirmed, and the operational MTU is raised.
    /// </summary>
    public bool OnProbeAcknowledged(uint sequence)
    {
        lock (_sync)
        {
            if (_state != PmtuState.Validating || _activeProbeSequence == 0 || sequence != _activeProbeSequence)
            {
                return false;
            }

            long nowTicks = _timeProvider.GetTimestamp();
            _confirmedMtu = _activeProbeSize;
            _currentMtu = _activeProbeSize;
            _plateauIndex = GetPlateauIndexForSize(_confirmedMtu);
            _lastConfirmedTicks = nowTicks;
            _consecutiveProbeFailures = 0;
            _activeProbeSequence = 0;
            _activeProbeSize = 0;

            if (_plateauIndex >= StandardPlateaus.Length - 1)
            {
                _state = PmtuState.Confirmed;
            }
            else
            {
                _state = PmtuState.Searching;
            }

            return true;
        }
    }

    /// <summary>
    ///     Handles an explicitly reported lost probe sequence from inflight loss tracking.
    /// </summary>
    public void OnProbeLost(uint sequence)
    {
        lock (_sync)
        {
            if (_state == PmtuState.Validating && _activeProbeSequence == sequence && sequence != 0)
            {
                OnProbeTimeoutUnsafe(_timeProvider.GetTimestamp());
            }
        }
    }

    /// <summary>
    ///     Black-hole detection heuristic (RFC 4821 Section 7.5).
    ///     If consecutive data packet losses occur at the current MTU, falls back to safe base MTU immediately.
    /// </summary>
    public void ReportConsecutiveLoss(int consecutiveLosses)
    {
        lock (_sync)
        {
            if (consecutiveLosses >= 3 && _currentMtu > BaseMtuPayload)
            {
                _currentMtu = BaseMtuPayload;
                _confirmedMtu = BaseMtuPayload;
                _plateauIndex = 0;
                _state = PmtuState.BlackHole;
                _activeProbeSequence = 0;
                _activeProbeSize = 0;
                _lastProbeDispatchedTicks = _timeProvider.GetTimestamp();
            }
        }
    }

    private void OnProbeTimeoutUnsafe(long nowTicks)
    {
        _consecutiveProbeFailures++;
        _activeProbeSequence = 0;
        _activeProbeSize = 0;
        _currentMtu = _confirmedMtu;
        _lastProbeDispatchedTicks = nowTicks;

        if (_consecutiveProbeFailures >= 2)
        {
            _state = PmtuState.Confirmed;
            _lastConfirmedTicks = nowTicks;
            _consecutiveProbeFailures = 0;
        }
        else
        {
            _state = PmtuState.Searching;
        }
    }

    private static int GetPlateauIndexForSize(ushort size)
    {
        for (int i = StandardPlateaus.Length - 1; i >= 0; i--)
        {
            if (size >= StandardPlateaus[i])
            {
                return i;
            }
        }
        return 0;
    }

    /// <summary>
    ///     Writes an RFC 4821 probe payload containing the magic signature, probe sequence,
    ///     and zero-padding to reach the exact target payload length.
    /// </summary>
    public static bool TryWriteProbePayload(Span<byte> destination, uint probeSequence, ushort targetLength)
    {
        if (destination.Length < targetLength || targetLength < 8)
        {
            return false;
        }

        BinaryPrimitives.WriteUInt32LittleEndian(destination[0..4], ProbeMagic);
        BinaryPrimitives.WriteUInt32LittleEndian(destination[4..8], probeSequence);
        destination[8..targetLength].Clear();
        return true;
    }

    /// <summary>
    ///     Validates whether a given payload is an RFC 4821 PLPMTUD probe packet.
    /// </summary>
    public static bool IsProbePayload(ReadOnlySpan<byte> payload, out uint probeSequence)
    {
        probeSequence = 0;
        if (payload.Length < 8)
        {
            return false;
        }

        uint magic = BinaryPrimitives.ReadUInt32LittleEndian(payload[0..4]);
        if (magic != ProbeMagic)
        {
            return false;
        }

        probeSequence = BinaryPrimitives.ReadUInt32LittleEndian(payload[4..8]);
        return true;
    }
}
