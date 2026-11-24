// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

// ============================================================================
// File: DedicatedServerCrashResilienceTests.cs
// System: COMPEL Unit Tests - Phase 8D Auto-Reboot & Detour Resilience
// Subsystem: Dedicated Server Crash Resilience & Zombie Socket Tests
// ============================================================================

using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading.Tasks;
using ASPIRE.Common.Netcode;
using COMPEL.Configuration;
using COMPEL.Endpoints;
using COMPEL.Services.Proxy;
using COMPEL.Services.Supervision;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace COMPEL.Tests;

public sealed class DedicatedServerCrashResilienceTests
{
    private static IOptions<MatchServerManagerOptions> CreateDefaultOptions(
        bool enableResilience = true,
        int maxFastReboots = 3,
        int fastRebootTargetMs = 500)
    {
        return Options.Create(new MatchServerManagerOptions
        {
            EnableCrashResilienceLoop = enableResilience,
            MaxConsecutiveFastReboots = maxFastReboots,
            FastRebootTargetMs = fastRebootTargetMs,
            AutoApplyDetourPatches = true
        });
    }

    [Theory]
    [InlineData(-1073741819, "STATUS_ACCESS_VIOLATION", true)]
    [InlineData(-1073741676, "STATUS_INTEGER_DIVIDE_BY_ZERO", true)]
    [InlineData(-1073741571, "STATUS_STACK_OVERFLOW", true)]
    [InlineData(-1073741515, "STATUS_DLL_NOT_FOUND", true)]
    [InlineData(-536870911, "STATUS_FATAL_ENGINE_EXCEPTION", true)]
    [InlineData(134, "SIGABRT", true)]
    [InlineData(137, "SIGKILL_OOM", true)]
    [InlineData(139, "SIGSEGV", true)]
    [InlineData(143, "SIGTERM", true)]
    [InlineData(0, "Success", false)]
    public void DetectsCrashExitCode_AndClassifiesFaultType_Accurately(
        int exitCode,
        string expectedName,
        bool expectedCrash)
    {
        bool isCrash = DedicatedServerCrashResilienceService.IsCrashExitCode(exitCode);
        string name = DedicatedServerCrashResilienceService.ClassifyExitCode(exitCode);

        isCrash.Should().Be(expectedCrash);
        name.Should().Be(expectedName);
    }

    [Fact]
    public void ExtractFaultingCallsite_FromWorkerLogs_IdentifiesModuleAndRva()
    {
        List<DedicatedServerLogLine> logs =
        [
            new(TimeProvider.System.GetUtcNow(), "[DedicatedServer:11235] Initializing engine Host_Init...", false, "server"),
            new(TimeProvider.System.GetUtcNow(), "[DedicatedServer:11235] Exception 0xC0000005 encountered in worker process", true, "server"),
            new(TimeProvider.System.GetUtcNow(), "Crash at k2_x64.dll+0x3A20 (CHostClient::ProcessSnapshotDelta+0x18)", true, "server")
        ];

        var (module, rva, exceptionCode) = DedicatedServerCrashResilienceService.ExtractFaultingCallsite(logs);

        module.Should().Be("k2_x64.dll");
        rva.Should().Be("0x3A20");
        exceptionCode.Should().Be("0xC0000005");
    }

    [Fact]
    public void ExtractFaultingCallsite_FromLinuxWorkerLogs_IdentifiesSoModuleAndRva()
    {
        List<DedicatedServerLogLine> logs =
        [
            new(TimeProvider.System.GetUtcNow(), "[DedicatedServer:11235] Server running on Linux x86_64", false, "server"),
            new(TimeProvider.System.GetUtcNow(), "Signal 11 received. Crash in libgame_shared.so+0x54AB", true, "server")
        ];

        var (module, rva, _) = DedicatedServerCrashResilienceService.ExtractFaultingCallsite(logs);

        module.Should().Be("libgame_shared.so");
        rva.Should().Be("0x54AB");
    }

    [Fact]
    public void SynthesizesAbiSafeDetour_ForAccessViolationCallsite()
    {
        var options = CreateDefaultOptions();
        var service = new DedicatedServerCrashResilienceService(options);

        var worker = new DedicatedServerWorker(1, 11235, 11435, 11235, 11435, "KONGOR #1");
        worker.AddLogLine("Access violation 0xC0000005 at k2_x64.dll+0x3A20", isError: true);

        var decision = service.EvaluateCrash(worker, unchecked((int)0xC0000005), worker.GetRecentLogs(10));

        decision.IsCrash.Should().BeTrue();
        decision.CanFastReboot.Should().BeTrue();
        decision.FaultingModule.Should().Be("k2_x64.dll");
        decision.FaultingRva.Should().Be("0x3A20");
        decision.SynthesizedDetour.Should().NotBeNull();
        decision.SynthesizedDetour!.Strategy.Should().Be("LeafNullCheck");
        decision.SynthesizedDetour.PreservesNonVolatileRegisters.Should().BeTrue();
        decision.SynthesizedDetour.Maintains16ByteStackAlignment.Should().BeTrue();
        decision.SynthesizedDetour.DetourBytesHex.Should().NotBeNullOrWhiteSpace();
    }

    [Fact]
    public void EvaluateCrash_ThrottlesToBackoff_WhenConsecutiveCrashesExceedThreshold()
    {
        var options = CreateDefaultOptions(maxFastReboots: 3);
        var service = new DedicatedServerCrashResilienceService(options);

        var worker = new DedicatedServerWorker(1, 11235, 11435, 11235, 11435, "KONGOR #1");

        // Crash 1: Fast reboot allowed
        var d1 = service.EvaluateCrash(worker, unchecked((int)0xC0000005), worker.GetRecentLogs(10));
        d1.CanFastReboot.Should().BeTrue();
        worker.ConsecutiveCrashCount.Should().Be(1);

        // Crash 2: Fast reboot allowed
        var d2 = service.EvaluateCrash(worker, unchecked((int)0xC0000005), worker.GetRecentLogs(10));
        d2.CanFastReboot.Should().BeTrue();
        worker.ConsecutiveCrashCount.Should().Be(2);

        // Crash 3: Fast reboot allowed (limit reached)
        var d3 = service.EvaluateCrash(worker, unchecked((int)0xC0000005), worker.GetRecentLogs(10));
        d3.CanFastReboot.Should().BeTrue();
        worker.ConsecutiveCrashCount.Should().Be(3);

        // Crash 4: Throttle triggered, must drop back to standard backoff
        var d4 = service.EvaluateCrash(worker, unchecked((int)0xC0000005), worker.GetRecentLogs(10));
        d4.CanFastReboot.Should().BeFalse();
        worker.ConsecutiveCrashCount.Should().Be(4);
        d4.DiagnosticReason.Should().Contain("Throttling");
    }

    [Fact]
    public async Task ZombieSocketDetector_ValidatesPortAvailability_AndRecyclesPort()
    {
        // Select an isolated non-ephemeral UDP port outside the OS dynamic range (32768..65535)
        // so concurrent Bind(..., 0) sockets across parallel COMPEL tests cannot steal it mid-test.
        int port = TestNonEphemeralUdpPortAllocator.NextFreePort();

        // 1. Verify port is initially available
        ZombieSocketDetector.IsPortAvailable(port).Should().BeTrue();

        // 2. Bind port to simulate an active/zombie socket
        using Socket activeSocket = new(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        activeSocket.ExclusiveAddressUse = true;
        activeSocket.Bind(new IPEndPoint(IPAddress.Any, port));

        // Port should now report occupied
        ZombieSocketDetector.IsPortAvailable(port).Should().BeFalse();

        // 3. Release the socket and verify EnsurePortAvailableAsync detects availability
        activeSocket.Close();

        bool becameAvailable = await ZombieSocketDetector.EnsurePortAvailableAsync(port, maxWaitMs: 250);
        becameAvailable.Should().BeTrue();
    }

    [Fact]
    public void WorkerLifecycle_SupportsRecoveringState_DuringFastRebootTransition()
    {
        var worker = new DedicatedServerWorker(1, 11235, 11435, 11235, 11435, "KONGOR #1");
        worker.CrashCount = 1;
        worker.DesiredRunning = true;

        // Offline and crashed
        worker.CurrentState.Should().Be("Crashed");

        // Transitioning into Recovering
        worker.CurrentState = "Recovering";
        worker.CurrentState.Should().Be("Recovering");

        // Transitioning into Starting
        worker.CurrentState = "Starting";
        worker.CurrentState.Should().Be("Starting");
    }

    [Fact]
    public void ServerKeyframeSharedMemory_WriteAndRead_RoundTripsStateCorrectly()
    {
        int port = 19890;
        ServerKeyframeSharedMemory.ClearKeyframe(port);

        byte[] payload = [0x01, 0x02, 0x03, 0x04, 0xAA, 0xBB, 0xCC, 0xDD];
        DateTimeOffset timestamp = TimeProvider.System.GetUtcNow();
        uint crc = KongorServerKeyframe.ComputeCrc32(payload);

        KongorServerKeyframe keyframe = new()
        {
            Sequence = 42,
            ServerTick = 12050,
            MatchId = 9876543210UL,
            Timestamp = timestamp,
            StatePayload = payload,
            Crc32 = crc
        };

        bool written = ServerKeyframeSharedMemory.WriteKeyframe(port, keyframe, logger: NullLogger.Instance);
        written.Should().BeTrue();

        bool read = ServerKeyframeSharedMemory.TryReadKeyframe(port, out KongorServerKeyframe? recovered, logger: NullLogger.Instance);
        read.Should().BeTrue();
        recovered.Should().NotBeNull();
        recovered!.Sequence.Should().Be(42u);
        recovered.ServerTick.Should().Be(12050u);
        recovered.MatchId.Should().Be(9876543210UL);
        recovered.StatePayload.Should().Equal(payload);
        recovered.Crc32.Should().Be(crc);

        ServerKeyframeSharedMemory.ClearKeyframe(port);
    }

    [Fact]
    public void KongorServerKeyframe_ComputeCrc32_MatchesStandardIeeeVector()
    {
        // Standard IEEE 802.3 CRC32 vector for "123456789" is 0xCBF43926
        byte[] standardVector = Encoding.ASCII.GetBytes("123456789");
        uint crc = KongorServerKeyframe.ComputeCrc32(standardVector);
        crc.Should().Be(0xCBF43926);
    }

    [Fact]
    public void ServerKeyframeSharedMemory_CorruptPayload_RejectsKeyframeDueToCrcMismatch()
    {
        int port = 19893;
        ServerKeyframeSharedMemory.ClearKeyframe(port);

        byte[] payload = [0xAA, 0xBB, 0xCC, 0xDD];
        KongorServerKeyframe keyframe = new()
        {
            Sequence = 1,
            ServerTick = 100,
            MatchId = 555UL,
            Timestamp = TimeProvider.System.GetUtcNow(),
            StatePayload = payload,
            Crc32 = KongorServerKeyframe.ComputeCrc32(payload)
        };

        ServerKeyframeSharedMemory.WriteKeyframe(port, keyframe, logger: NullLogger.Instance);

        // Tamper with the payload byte in the backing file
        string fallbackPath = ServerKeyframeSharedMemory.GetFallbackFilePath(port);
        using (FileStream fs = new(fallbackPath, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite))
        {
            fs.Seek(ServerKeyframeHeader.HeaderSizeBytes, SeekOrigin.Begin);
            fs.WriteByte(0xFF); // Corrupt payload byte
        }

        bool read = ServerKeyframeSharedMemory.TryReadKeyframe(port, out KongorServerKeyframe? recovered, logger: NullLogger.Instance);
        read.Should().BeFalse();
        recovered.Should().BeNull();

        ServerKeyframeSharedMemory.ClearKeyframe(port);
    }

    [Fact]
    public void EvaluateCrash_WithSharedMemoryKeyframe_AttachesRecoveredKeyframe()
    {
        int port = 19892;
        ServerKeyframeSharedMemory.ClearKeyframe(port);

        byte[] payload = [0x55, 0x66, 0x77, 0x88];
        KongorServerKeyframe keyframe = new()
        {
            Sequence = 7,
            ServerTick = 8888,
            MatchId = 777777UL,
            Timestamp = TimeProvider.System.GetUtcNow(),
            StatePayload = payload,
            Crc32 = KongorServerKeyframe.ComputeCrc32(payload)
        };

        ServerKeyframeSharedMemory.WriteKeyframe(port, keyframe, logger: NullLogger.Instance);

        IOptions<MatchServerManagerOptions> options = CreateDefaultOptions();
        DedicatedServerCrashResilienceService service = new(options, NullLogger<DedicatedServerCrashResilienceService>.Instance);
        DedicatedServerWorker worker = new(1, port, port + 200, port, port + 200, "KONGOR #1");

        CrashRecoveryDecision decision = service.EvaluateCrash(worker, unchecked((int)0xC0000005), worker.GetRecentLogs(10));

        decision.IsCrash.Should().BeTrue();
        decision.CanFastReboot.Should().BeTrue();
        decision.HasKeyframeRecovery.Should().BeTrue();
        decision.RecoveredKeyframe.Should().NotBeNull();
        decision.RecoveredKeyframe!.ServerTick.Should().Be(8888u);
        decision.RecoveredKeyframe.MatchId.Should().Be(777777UL);
        decision.DiagnosticReason.Should().Contain("Keyframe Restorable @ Tick 8888, Match 777777");

        ServerKeyframeSharedMemory.ClearKeyframe(port);
    }

    [Fact]
    public async Task UDPForwarder_RetainSessions_FreezesEvictionDuringCrashRecovery()
    {
        int publicPort = FreeUdpPort();
        int localPort = FreeUdpPort();

        using Socket server = new(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        server.Bind(new IPEndPoint(IPAddress.Loopback, localPort));

        using UDPForwarder forwarder = new(
            publicPort,
            localPort,
            NullLogger.Instance,
            enableZstdCompression: false,
            enableFec: false);

        using CancellationTokenSource cts = new();
        _ = forwarder.Run(cts.Token);

        using Socket client = new(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        client.Bind(new IPEndPoint(IPAddress.Loopback, 0));

        byte[] datagram = new byte[ClientPacketReader.MinimumLength(ProxyForwarderKind.Game) + 4];
        BinaryPrimitives.WriteUInt32LittleEndian(
            datagram.AsSpan(ClientPacketReader.ChallengeOffset),
            SessionChallengeState.UnauthenticatedChallenge);
        BinaryPrimitives.WriteUInt16LittleEndian(
            datagram.AsSpan(ClientPacketReader.CounterOffset),
            0);

        await client.SendToAsync(datagram, SocketFlags.None, new IPEndPoint(IPAddress.Loopback, publicPort));

        // Allow datagram to be processed by forwarder
        await Task.Delay(150);

        forwarder.ActiveSessionCount.Should().Be(1);

        // 1. Retain sessions (simulating worker crash)
        forwarder.RetainSessions(true);
        forwarder.AreSessionsRetained.Should().BeTrue();

        // Evict idle sessions with immediate zero timeout - must NOT evict while retained
        forwarder.EvictIdleSessions(TimeSpan.Zero, TimeSpan.Zero);
        forwarder.ActiveSessionCount.Should().Be(1);

        // 2. Release session retention (simulating worker resurrected & state restored)
        forwarder.RetainSessions(false);
        forwarder.AreSessionsRetained.Should().BeFalse();

        // Evict idle sessions - now session should be evicted
        forwarder.EvictIdleSessions(TimeSpan.Zero, TimeSpan.Zero);
        forwarder.ActiveSessionCount.Should().Be(0);

        await cts.CancelAsync();
    }

    private static int FreeUdpPort()
    {
        using Socket socket = new(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        socket.Bind(new IPEndPoint(IPAddress.Loopback, 0));
        return ((IPEndPoint)socket.LocalEndPoint!).Port;
    }
}
