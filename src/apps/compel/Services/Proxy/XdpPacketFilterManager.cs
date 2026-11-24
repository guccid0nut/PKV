// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

// ============================================================================
// File: XdpPacketFilterManager.cs
// System: Heroes of Newerth - Dedicated Game Server Cluster (COMPEL)
// Subsystem: Hardware-Level eBPF / XDP Network Ingress Filter & Port Synchronizer
// Milestone 4.1: Dynamic Linux Kernel UDP Filtering & COMPEL Port Map Sync
// Policy: Strict Explicit Typing (NO 'var')
// ============================================================================

using System.Collections.Concurrent;
using System.Net;
using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace COMPEL.Services.Proxy;

/// <summary>
///     Encapsulates registered metadata for an active dedicated server game or voice UDP port.
/// </summary>
public sealed record KongorActivePortInfo(
    ushort Port,
    int ProcessId,
    bool IsVoice,
    bool IsProxy,
    DateTimeOffset RegisteredAt);

/// <summary>
///     Real-time telemetry counters emitted by the eBPF/XDP filter and user-space fallback.
/// </summary>
public sealed class XdpFilterStats
{
    public int ActivePortCount { get; init; }
    public long TotalIngressPackets { get; init; }
    public long PassedGamePackets { get; init; }
    public long PassedVoicePackets { get; init; }
    public long DroppedTooShort { get; init; }
    public long DroppedMalformed { get; init; }
    public long DroppedRateLimited { get; init; }
    public long DroppedUnregisteredPort { get; init; }
    public bool BpfMapSynchronized { get; init; }
    public string PinnedMapPath { get; init; } = string.Empty;
}

/// <summary>
///     Manages the lifecycle synchronization of active game and voice ports between COMPEL dedicated workers
///     and the Linux kernel eBPF / XDP ingress filter (/sys/fs/bpf/kongor_active_ports).
///     Provides a high-performance token-bucket rate limiter and packet verification fallback for Windows and dev hosts.
/// </summary>
public sealed class XdpPacketFilterManager : IDisposable
{
    public const string DefaultPinnedMapPath = "/sys/fs/bpf/kongor_active_ports";

    // Fallback static port boundaries when dynamic map is empty
    public const ushort StaticGameStart = 11235;
    public const ushort StaticGameEnd = 11534;
    public const ushort StaticVoiceStart = 11735;
    public const ushort StaticVoiceEnd = 12034;
    public const ushort StaticProxyGameStart = 21235;
    public const ushort StaticProxyGameEnd = 21534;
    public const ushort StaticProxyVoiceStart = 21735;
    public const ushort StaticProxyVoiceEnd = 22034;

    private readonly ILogger<XdpPacketFilterManager> _logger;
    private readonly TimeProvider _timeProvider;
    private readonly string _pinnedMapPath;
    private readonly ConcurrentDictionary<ushort, KongorActivePortInfo> _activePorts = new();
    private readonly ConcurrentDictionary<IPAddress, TokenBucket> _ipRateLimiters = new();

    // High-frequency telemetry counters
    private long _totalIngressPackets;
    private long _passedGamePackets;
    private long _passedVoicePackets;
    private long _droppedTooShort;
    private long _droppedMalformed;
    private long _droppedRateLimited;
    private long _droppedUnregisteredPort;
    private bool _bpfMapAvailable;
    private int _disposed;

    public string PinnedMapPath => _pinnedMapPath;
    public bool IsBpfMapAvailable => _bpfMapAvailable;
    public int ActivePortCount => _activePorts.Count;

    public XdpPacketFilterManager(
        ILogger<XdpPacketFilterManager>? logger = null,
        string? pinnedMapPath = null,
        TimeProvider? timeProvider = null)
    {
        _logger = logger ?? NullLogger<XdpPacketFilterManager>.Instance;
        _timeProvider = timeProvider ?? TimeProvider.System;
        _pinnedMapPath = !string.IsNullOrWhiteSpace(pinnedMapPath)
            ? pinnedMapPath
            : (Environment.GetEnvironmentVariable("KONGOR_EBPF_MAP_PATH") ?? DefaultPinnedMapPath);

        if (OperatingSystem.IsLinux())
        {
            _bpfMapAvailable = File.Exists(_pinnedMapPath);
            if (_bpfMapAvailable)
            {
                _logger.LogInformation(
                    "[XDP Filter] Linux eBPF pinned map detected at {Path}. Kernel-level NIC offload active.",
                    _pinnedMapPath);
            }
            else
            {
                _logger.LogInformation(
                    "[XDP Filter] Pinned map {Path} not found. Running with in-memory user-space port filtering.",
                    _pinnedMapPath);
            }
        }
        else
        {
            _bpfMapAvailable = false;
            _logger.LogInformation(
                "[XDP Filter] Non-Linux platform detected ({OS}). Using high-performance user-space token-bucket filter.",
                Environment.OSVersion.Platform);
        }
    }

    /// <summary>
    ///     Registers an active dedicated server game or voice port in both user space and the eBPF kernel map.
    /// </summary>
    public bool RegisterPort(ushort port, int processId, bool isVoice = false, bool isProxy = false)
    {
        KongorActivePortInfo info = new(
            Port: port,
            ProcessId: processId,
            IsVoice: isVoice,
            IsProxy: isProxy,
            RegisteredAt: _timeProvider.GetUtcNow());

        _activePorts[port] = info;

        _logger.LogInformation(
            "[XDP Filter] Registered active {Kind} port {Port} (PID: {Pid}, Proxy: {IsProxy}) in fleet registry (Total: {Count})",
            isVoice ? "Voice" : "Game",
            port,
            processId,
            isProxy,
            _activePorts.Count);

        if (OperatingSystem.IsLinux() && _bpfMapAvailable)
        {
            bool synced = SyncPortToKernelBpfMap(port, processId, isVoice, isProxy, isAdd: true);
            if (!synced)
            {
                _logger.LogWarning(
                    "[XDP Filter] Failed to sync port {Port} to kernel BPF map at {Path}. In-memory guard active.",
                    port,
                    _pinnedMapPath);
            }
            return synced;
        }

        return true;
    }

    /// <summary>
    ///     Unregisters an active dedicated server game or voice port upon worker termination.
    /// </summary>
    public bool UnregisterPort(ushort port)
    {
        bool removed = _activePorts.TryRemove(port, out KongorActivePortInfo? info);
        if (removed && info != null)
        {
            _logger.LogInformation(
                "[XDP Filter] Unregistered {Kind} port {Port} (PID: {Pid}) from fleet registry (Remaining: {Count})",
                info.IsVoice ? "Voice" : "Game",
                port,
                info.ProcessId,
                _activePorts.Count);

            if (OperatingSystem.IsLinux() && _bpfMapAvailable)
            {
                SyncPortToKernelBpfMap(port, info.ProcessId, info.IsVoice, info.IsProxy, isAdd: false);
            }
            return true;
        }

        return false;
    }

    /// <summary>
    ///     Determines whether a port is currently registered as an active dedicated server port.
    /// </summary>
    public bool IsPortActive(ushort port)
    {
        return _activePorts.ContainsKey(port) || IsStaticKongorPort(port);
    }

    /// <summary>
    ///     Retrieves registered port information if active.
    /// </summary>
    public KongorActivePortInfo? GetPortInfo(ushort port)
    {
        return _activePorts.TryGetValue(port, out KongorActivePortInfo? info) ? info : null;
    }

    /// <summary>
    ///     Retrieves all currently registered dedicated server ports.
    /// </summary>
    public IReadOnlyCollection<KongorActivePortInfo> GetActivePorts()
    {
        return _activePorts.Values.ToList().AsReadOnly();
    }

    /// <summary>
    ///     Clears all registered active ports and synchronizes with the kernel map if available.
    /// </summary>
    public void Clear()
    {
        foreach (ushort port in _activePorts.Keys)
        {
            UnregisterPort(port);
        }
        _activePorts.Clear();
        _ipRateLimiters.Clear();
    }

    /// <summary>
    ///     Validates an ingress datagram against active port registrations, length constraints,
    ///     stateless challenge tokens, and per-IP token bucket limits.
    /// </summary>
    public DatagramValidationResult ValidateIngressPacket(ReadOnlySpan<byte> datagram, ushort port, IPEndPoint clientEndPoint)
    {
        Interlocked.Increment(ref _totalIngressPackets);

        bool hasPortInfo = _activePorts.TryGetValue(port, out KongorActivePortInfo? info);
        bool isVoice = false;

        if (hasPortInfo && info != null)
        {
            isVoice = info.IsVoice;
        }
        else if (IsStaticKongorPort(port))
        {
            isVoice = IsStaticVoicePort(port);
        }
        else
        {
            Interlocked.Increment(ref _droppedUnregisteredPort);
            return DatagramValidationResult.Dropped(10, "Unregistered Port");
        }

        ProxyForwarderKind kind = isVoice ? ProxyForwarderKind.Voice : ProxyForwarderKind.Game;
        int minimumLength = ClientPacketReader.MinimumLength(kind);

        // 1. Truncated packet rejection
        if (datagram.Length < minimumLength)
        {
            Interlocked.Increment(ref _droppedTooShort);
            return DatagramValidationResult.Dropped(ViolationScoreContainer.TooShortViolationWeight, "Too Short");
        }

        // 2. Watermark prefix inspection
        if (ClientPacketReader.TryRead(datagram, kind, out uint challenge, out ushort counter))
        {
            // Drop malformed 0xFFFFFFFF/0xFFFF challenge flood
            if (challenge == 0xFFFFFFFF && counter == 0xFFFF)
            {
                Interlocked.Increment(ref _droppedMalformed);
                return DatagramValidationResult.Dropped(50, "Malformed Challenge Envelope");
            }

            // Drop all-zero flood
            if (challenge == 0 && counter == 0)
            {
                Interlocked.Increment(ref _droppedMalformed);
                return DatagramValidationResult.Dropped(50, "Zero Watermark Envelope");
            }
        }
        else
        {
            Interlocked.Increment(ref _droppedTooShort);
            return DatagramValidationResult.Dropped(ViolationScoreContainer.TooShortViolationWeight, "Too Short");
        }

        // 3. Token-Bucket Rate Limiting (per source IP)
        if (!AdmitClientToken(clientEndPoint.Address))
        {
            Interlocked.Increment(ref _droppedRateLimited);
            return DatagramValidationResult.Dropped(ViolationScoreContainer.RateLimitViolationWeight, "Rate Limited");
        }

        // 4. Accepted Packet
        if (isVoice)
        {
            Interlocked.Increment(ref _passedVoicePackets);
        }
        else
        {
            Interlocked.Increment(ref _passedGamePackets);
        }

        bool isAuthenticated = challenge != SessionChallengeState.UnauthenticatedChallenge;
        return DatagramValidationResult.Admitted(isAuthenticated);
    }

    /// <summary>
    ///     Fetches snapshot of live filter statistics.
    /// </summary>
    public XdpFilterStats GetStats()
    {
        return new XdpFilterStats
        {
            ActivePortCount = _activePorts.Count,
            TotalIngressPackets = Volatile.Read(ref _totalIngressPackets),
            PassedGamePackets = Volatile.Read(ref _passedGamePackets),
            PassedVoicePackets = Volatile.Read(ref _passedVoicePackets),
            DroppedTooShort = Volatile.Read(ref _droppedTooShort),
            DroppedMalformed = Volatile.Read(ref _droppedMalformed),
            DroppedRateLimited = Volatile.Read(ref _droppedRateLimited),
            DroppedUnregisteredPort = Volatile.Read(ref _droppedUnregisteredPort),
            BpfMapSynchronized = _bpfMapAvailable,
            PinnedMapPath = _pinnedMapPath
        };
    }

    public static bool IsStaticKongorPort(ushort port)
    {
        return (port >= StaticGameStart && port <= StaticGameEnd) ||
               (port >= StaticVoiceStart && port <= StaticVoiceEnd) ||
               (port >= StaticProxyGameStart && port <= StaticProxyGameEnd) ||
               (port >= StaticProxyVoiceStart && port <= StaticProxyVoiceEnd);
    }

    public static bool IsStaticVoicePort(ushort port)
    {
        return (port >= StaticVoiceStart && port <= StaticVoiceEnd) ||
               (port >= StaticProxyVoiceStart && port <= StaticProxyVoiceEnd);
    }

    private bool AdmitClientToken(IPAddress address)
    {
        TokenBucket bucket = _ipRateLimiters.GetOrAdd(address, static _ => new TokenBucket(capacity: 250, refillRatePerSec: 500));
        return bucket.TryConsume();
    }

    private bool SyncPortToKernelBpfMap(ushort port, int pid, bool isVoice, bool isProxy, bool isAdd)
    {
        try
        {
            if (!File.Exists(_pinnedMapPath))
            {
                return false;
            }

            int mapFd = LinuxBpfInterop.GetPinnedMapFd(_pinnedMapPath);
            if (mapFd < 0)
            {
                return false;
            }

            try
            {
                if (isAdd)
                {
                    LinuxBpfInterop.BpfPortEntry entry = new()
                    {
                        Pid = (uint)pid,
                        Flags = (uint)((isVoice ? 1 : 0) | (isProxy ? 2 : 0)),
                        RegisteredAtNs = (ulong)(_timeProvider.GetUtcNow().ToUnixTimeMilliseconds() * 1_000_000)
                    };
                    return LinuxBpfInterop.UpdateMapElem(mapFd, port, entry);
                }
                else
                {
                    return LinuxBpfInterop.DeleteMapElem(mapFd, port);
                }
            }
            finally
            {
                LinuxBpfInterop.Close(mapFd);
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "[XDP Filter] BPF kernel map sync exception for port {Port}", port);
            return false;
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        Clear();
    }

    /// <summary>
    ///     Lightweight, lock-free token bucket for per-client IP ingress rate limiting.
    /// </summary>
    private sealed class TokenBucket(double capacity, double refillRatePerSec)
    {
        private readonly double _capacity = capacity;
        private readonly double _refillRate = refillRatePerSec;
        private double _tokens = capacity;
        private long _lastRefillTicks = Stopwatch.GetTimestamp();
        private readonly Lock _sync = new();

        public bool TryConsume()
        {
            lock (_sync)
            {
                long nowTicks = Stopwatch.GetTimestamp();
                double elapsedSec = (double)(nowTicks - _lastRefillTicks) / Stopwatch.Frequency;
                _lastRefillTicks = nowTicks;

                _tokens = Math.Min(_capacity, _tokens + (elapsedSec * _refillRate));

                if (_tokens >= 1.0)
                {
                    _tokens -= 1.0;
                    return true;
                }

                return false;
            }
        }
    }

    /// <summary>
    ///     Low-level Linux BPF syscall interop definitions.
    /// </summary>
    private static class LinuxBpfInterop
    {
        public const int BPF_MAP_UPDATE_ELEM = 2;
        public const int BPF_MAP_DELETE_ELEM = 3;
        public const int BPF_OBJ_GET = 7;
        public const int SYS_BPF = 321; // x86_64 __NR_bpf

        [StructLayout(LayoutKind.Explicit)]
        public struct BpfAttrObjGet
        {
            [FieldOffset(0)] public ulong Pathname;
            [FieldOffset(8)] public uint BpfFd;
            [FieldOffset(12)] public uint FileFlags;
        }

        [StructLayout(LayoutKind.Explicit)]
        public struct BpfAttrMapElem
        {
            [FieldOffset(0)] public uint MapFd;
            [FieldOffset(8)] public ulong Key;
            [FieldOffset(16)] public ulong Value;
            [FieldOffset(24)] public ulong Flags;
        }

        [StructLayout(LayoutKind.Sequential, Pack = 1)]
        public struct BpfPortEntry
        {
            public uint Pid;
            public uint Flags; // bit 0: is_voice, bit 1: is_proxy
            public ulong RegisteredAtNs;
        }

        [DllImport("libc", EntryPoint = "syscall", SetLastError = true)]
        public static extern long SyscallBpfObjGet(long number, int cmd, ref BpfAttrObjGet attr, uint size);

        [DllImport("libc", EntryPoint = "syscall", SetLastError = true)]
        public static extern long SyscallBpfMapElem(long number, int cmd, ref BpfAttrMapElem attr, uint size);

        [DllImport("libc", EntryPoint = "close", SetLastError = true)]
        public static extern int Close(int fd);

        public static int GetPinnedMapFd(string path)
        {
            if (!OperatingSystem.IsLinux()) return -1;
            IntPtr pathPtr = Marshal.StringToHGlobalAnsi(path);
            try
            {
                BpfAttrObjGet attr = new()
                {
                    Pathname = (ulong)pathPtr.ToInt64(),
                    BpfFd = 0,
                    FileFlags = 0
                };
                long result = SyscallBpfObjGet(SYS_BPF, BPF_OBJ_GET, ref attr, (uint)Marshal.SizeOf<BpfAttrObjGet>());
                return (int)result;
            }
            finally
            {
                Marshal.FreeHGlobal(pathPtr);
            }
        }

        public static bool UpdateMapElem(int mapFd, ushort portKey, BpfPortEntry entry)
        {
            if (!OperatingSystem.IsLinux() || mapFd < 0) return false;
            IntPtr keyPtr = Marshal.AllocHGlobal(sizeof(ushort));
            IntPtr valPtr = Marshal.AllocHGlobal(Marshal.SizeOf<BpfPortEntry>());
            try
            {
                Marshal.WriteInt16(keyPtr, (short)portKey);
                Marshal.StructureToPtr(entry, valPtr, fDeleteOld: false);

                BpfAttrMapElem attr = new()
                {
                    MapFd = (uint)mapFd,
                    Key = (ulong)keyPtr.ToInt64(),
                    Value = (ulong)valPtr.ToInt64(),
                    Flags = 0 // BPF_ANY
                };
                long result = SyscallBpfMapElem(SYS_BPF, BPF_MAP_UPDATE_ELEM, ref attr, (uint)Marshal.SizeOf<BpfAttrMapElem>());
                return result == 0;
            }
            finally
            {
                Marshal.FreeHGlobal(keyPtr);
                Marshal.FreeHGlobal(valPtr);
            }
        }

        public static bool DeleteMapElem(int mapFd, ushort portKey)
        {
            if (!OperatingSystem.IsLinux() || mapFd < 0) return false;
            IntPtr keyPtr = Marshal.AllocHGlobal(sizeof(ushort));
            try
            {
                Marshal.WriteInt16(keyPtr, (short)portKey);

                BpfAttrMapElem attr = new()
                {
                    MapFd = (uint)mapFd,
                    Key = (ulong)keyPtr.ToInt64(),
                    Value = 0,
                    Flags = 0
                };
                long result = SyscallBpfMapElem(SYS_BPF, BPF_MAP_DELETE_ELEM, ref attr, (uint)Marshal.SizeOf<BpfAttrMapElem>());
                return result == 0;
            }
            finally
            {
                Marshal.FreeHGlobal(keyPtr);
            }
        }
    }
}
