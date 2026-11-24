// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

// ============================================================================
// File: CompelNativeGuardSupervisionTests.cs
// System: COMPEL Unit Tests - Native C++ Modernization & Engine Hardening
// Subsystem: Phase 5 Runtime Supervision - Native Guard Supervision Integration
// ============================================================================

using System;
using System.Diagnostics;
using System.IO;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using ASPIRE.Common.Telemetry;
using COMPEL.Configuration;
using COMPEL.Internals;
using COMPEL.Services.Supervision;
using NEXUS.Contracts.Telemetry;
using Xunit;

namespace COMPEL.Tests;

public sealed class CompelNativeGuardSupervisionTests
{
    private static string TemporaryPath()
    {
        return Path.Combine(Path.GetTempPath(), $"compel-configuration-{Guid.NewGuid():N}.json");
    }

    [Fact]
    public void SandboxLimitsOptions_DefaultsEnableNativeGuardInjection_ToTrue()
    {
        SandboxLimitsOptions options = new();
        Assert.True(options.EnableNativeGuardInjection, "EnableNativeGuardInjection must default to true.");
    }

    [Fact]
    public void CompelConfiguration_LoadsNativeGuardInjection_WithDefaultTrue()
    {
        string path = TemporaryPath();

        try
        {
            CompelConfigurationLoader.CreateDefault(path);
            CompelConfigurationFile file = CompelConfigurationLoader.Load(path);

            Assert.NotNull(file.EnableNativeGuardInjection);
            Assert.True(file.EnableNativeGuardInjection.Value);
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [Fact]
    public void CompelNativeGuardSupervision_CompelConfiguration_CanExplicitlyDisableNativeGuard()
    {
        string path = TemporaryPath();

        try
        {
            File.WriteAllText(path, """{ "EnableNativeGuardInjection": { "Value": false } }""");
            CompelConfigurationFile file = CompelConfigurationLoader.Load(path);

            Assert.NotNull(file.EnableNativeGuardInjection);
            Assert.False(file.EnableNativeGuardInjection.Value);
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [Fact]
    public void ProcessTreeSandbox_WithNativeGuardEnabled_InitializesCleanly()
    {
        SandboxLimitsOptions options = new()
        {
            KillOnJobClose = false,
            EnableNativeGuardInjection = true
        };

        using ProcessTreeSandbox sandbox = new(options);
        Assert.Equal(options.EnableNativeGuardInjection, sandbox.Options.EnableNativeGuardInjection);

        if (OperatingSystem.IsWindows())
        {
            Assert.True(sandbox.IsInitialized);
            bool assigned = sandbox.AssignProcess(Process.GetCurrentProcess());
            Assert.True(assigned);
        }
    }

    [Fact]
    public void LinuxNativeGuard_KillSwitchAndSharedObjectResolution_BehavesDeterministically()
    {
        // protects: KONGOR_DISABLE_LINUX_NATIVE_GUARD=1 kill-switch and libk2_native_guard.so path discovery
        // fails_when: Kill-switch fails to recognize '1'/'true' or ResolveLinuxNativeGuardSoPath misses staged .so
        // seam: MatchServerManagerSupervisor static helpers against real temporary directory layout
        Assert.True(MatchServerManagerSupervisor.IsLinuxNativeGuardDisabledByEnvironment("1"));
        Assert.True(MatchServerManagerSupervisor.IsLinuxNativeGuardDisabledByEnvironment("true"));
        Assert.True(MatchServerManagerSupervisor.IsLinuxNativeGuardDisabledByEnvironment("TRUE"));
        Assert.True(MatchServerManagerSupervisor.IsLinuxNativeGuardDisabledByEnvironment("yes"));
        Assert.False(MatchServerManagerSupervisor.IsLinuxNativeGuardDisabledByEnvironment("0"));
        Assert.False(MatchServerManagerSupervisor.IsLinuxNativeGuardDisabledByEnvironment("false"));
        Assert.False(MatchServerManagerSupervisor.IsLinuxNativeGuardDisabledByEnvironment(""));

        string tempInstallDir = Path.Combine(Path.GetTempPath(), $"kongor-linux-ng-{Guid.NewGuid():N}");
        Directory.CreateDirectory(Path.Combine(tempInstallDir, "linux-x64"));
        Directory.CreateDirectory(Path.Combine(tempInstallDir, "compatibility"));

        try
        {
            Assert.Null(MatchServerManagerSupervisor.ResolveLinuxNativeGuardSoPath(tempInstallDir));

            string compatSo = Path.Combine(tempInstallDir, "compatibility", "libk2_native_guard.so");
            File.WriteAllBytes(compatSo, [0x7F, (byte)'E', (byte)'L', (byte)'F']);
            Assert.Equal(compatSo, MatchServerManagerSupervisor.ResolveLinuxNativeGuardSoPath(tempInstallDir));

            string primarySo = Path.Combine(tempInstallDir, "linux-x64", "libk2_native_guard.so");
            File.WriteAllBytes(primarySo, [0x7F, (byte)'E', (byte)'L', (byte)'F']);
            Assert.Equal(primarySo, MatchServerManagerSupervisor.ResolveLinuxNativeGuardSoPath(tempInstallDir));
        }
        finally
        {
            if (Directory.Exists(tempInstallDir))
            {
                Directory.Delete(tempInstallDir, recursive: true);
            }
        }
    }

    [Fact]
    public void PosixFileBackedTelemetryBuffer_V2SeqlockProducerAndConsumer_RoundTripsZeroTornFrames()
    {
        // protects: POSIX /dev/shm file-backed MemoryMappedTelemetryBuffer V2 192-byte Seqlock contract
        // fails_when: File-backed producer/consumer fails to open, corrupts V2 header offsets, or tears Seqlock reads
        // seam: Real FileStream + MemoryMappedFile.CreateFromFile backing in MemoryMappedTelemetryBuffer
        string shmFilePath = Path.Combine(Path.GetTempPath(), $"KongorServerTelemetry_{Guid.NewGuid():N}.shm");

        try
        {
            using MemoryMappedTelemetryBuffer producer = MemoryMappedTelemetryBuffer.CreateProducer(shmFilePath, capacity: 64, producerProcessId: 4242);
            producer.UpdateLiveEngineState(
                gamePhase: 3,
                activeMatchId: 9001,
                connectedPlayers: 10,
                luaMemoryKb: 16384,
                currentMap: "caldavar",
                playerName: "KongorSrv");

            FrameTelemetryRecord written = new()
            {
                FrameIndex = 1,
                TimestampQpc = 123456789L,
                CpuFrameTimeMs = 1.25f,
                GpuFrameTimeMs = 0.0f,
                InputLatencyMs = 0.4f,
                DrawCallCount = 0,
                ActiveEntities = 128,
                ParticlesAlive = 0,
                PingMicroseconds = 4500,
                PacketLossPercent = 0.0f,
                MemoryWorkingSetMb = 512,
                AudioVoicesActive = 0,
                ServerTick = 777,
                ReservedFlags = 0
            };
            Assert.True(producer.TryWrite(in written));

            Assert.True(MemoryMappedTelemetryBuffer.TryOpenConsumer(shmFilePath, out MemoryMappedTelemetryBuffer? consumer, out string? error), error);
            Assert.NotNull(consumer);
            using (consumer)
            {
                Assert.Equal(2u, consumer.Version);
                Assert.True(consumer.SupportsSeqlock);
                Assert.Equal(3u, consumer.GamePhase);
                Assert.Equal(9001u, consumer.ActiveMatchId);
                Assert.Equal(10u, consumer.ConnectedPlayers);
                Assert.Equal(16384u, consumer.LuaMemoryKb);
                Assert.Equal("caldavar", consumer.CurrentMap);
                Assert.Equal("KongorSrv", consumer.PlayerName);

                Assert.True(consumer.TryReadLatestSeqlock(out FrameTelemetryRecord latest));
                Assert.Equal(777u, latest.ServerTick);
                Assert.Equal(128u, latest.ActiveEntities);
                Assert.Equal(512u, latest.MemoryWorkingSetMb);
            }
        }
        finally
        {
            if (File.Exists(shmFilePath))
            {
                try { File.Delete(shmFilePath); } catch { }
            }
        }
    }

    [Fact]
    public async Task DedicatedServerWorker_UnixDomainSocketIpc_RoundTripsExecuteCommandAndEvalLuaSync()
    {
        // protects: Linux Unix domain socket (/tmp/hon-server-native-<port>.sock) [0x4D, 0x57] binary frame IPC
        // fails_when: DedicatedServerWorker.WritePipeAsync or EvalLuaNativeAsync fails to frame or parse 0x0003 / 0x000E
        // seam: Real OS AF_UNIX Socket (UnixDomainSocketEndPoint) server & client
        string sockPath = Path.Combine(Path.GetTempPath(), $"ksrv-{Guid.NewGuid():N}"[..20] + ".sock");
        if (File.Exists(sockPath)) File.Delete(sockPath);

        using Socket serverSock = new(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        serverSock.Bind(new UnixDomainSocketEndPoint(sockPath));
        serverSock.Listen(4);

        using CancellationTokenSource cts = new(TimeSpan.FromSeconds(5));
        Task serverTask = Task.Run(async () =>
        {
            for (int reqIndex = 0; reqIndex < 2; reqIndex++)
            {
                using Socket client = await serverSock.AcceptAsync(cts.Token);
                byte[] header = new byte[12];
                int read = 0;
                while (read < 12)
                {
                    int r = await client.ReceiveAsync(header.AsMemory(read, 12 - read), SocketFlags.None, cts.Token);
                    if (r <= 0) break;
                    read += r;
                }

                Assert.Equal(0x4D, header[0]);
                Assert.Equal(0x57, header[1]);
                ushort opCode = BitConverter.ToUInt16(header.AsSpan(2, 2));
                int payloadLen = BitConverter.ToInt32(header.AsSpan(4, 4));
                uint reqId = BitConverter.ToUInt32(header.AsSpan(8, 4));

                byte[] payloadBytes = new byte[payloadLen];
                int bodyRead = 0;
                while (bodyRead < payloadLen)
                {
                    int r = await client.ReceiveAsync(payloadBytes.AsMemory(bodyRead, payloadLen - bodyRead), SocketFlags.None, cts.Token);
                    if (r <= 0) break;
                    bodyRead += r;
                }
                string payloadStr = Encoding.UTF8.GetString(payloadBytes, 0, bodyRead);

                ushort respOp = opCode == 0x000E ? (ushort)0x000F : (ushort)0x0004;
                string respJson = opCode == 0x000E
                    ? $"{{\"success\":true,\"result\":\"eval_ok:{payloadStr}\"}}"
                    : $"{{\"success\":true,\"command\":\"{payloadStr}\"}}";
                byte[] respBytes = Encoding.UTF8.GetBytes(respJson);
                byte[] outFrame = new byte[12 + respBytes.Length];
                outFrame[0] = 0x4D;
                outFrame[1] = 0x57;
                BitConverter.TryWriteBytes(outFrame.AsSpan(2, 2), respOp);
                BitConverter.TryWriteBytes(outFrame.AsSpan(4, 4), respBytes.Length);
                BitConverter.TryWriteBytes(outFrame.AsSpan(8, 4), reqId);
                respBytes.CopyTo(outFrame.AsSpan(12));

                await client.SendAsync(outFrame, SocketFlags.None, cts.Token);
            }
        }, cts.Token);

        try
        {
            DedicatedServerWorker worker = new(1, 11335, 11435, 11335, 11435, "LinuxGuardWorker")
            {
                IsRunningOverride = true,
                UnixSocketPathOverride = sockPath
            };

            bool cmdDispatched = await worker.WritePipeAsync("Set svr_name TestLinuxGuard", timeoutMs: 2000, cts.Token);
            Assert.True(cmdDispatched);

            string? luaResp = await worker.EvalLuaNativeAsync("return 2 + 2", timeoutMs: 2000, cts.Token);
            Assert.NotNull(luaResp);
            Assert.Contains("eval_ok:return 2 + 2", luaResp);

            await serverTask;
        }
        finally
        {
            try { serverSock.Close(); } catch { }
            if (File.Exists(sockPath))
            {
                try { File.Delete(sockPath); } catch { }
            }
        }
    }
}
