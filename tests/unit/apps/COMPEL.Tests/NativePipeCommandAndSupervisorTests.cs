// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

// ============================================================================
// File: NativePipeCommandAndSupervisorTests.cs
// System: COMPEL Tests - NativeGuard Binary Pipe ACK, Intentional Exit & Full-Path Module Verification
// Policy: Strict Explicit Typing (NO 'var')
// ============================================================================

using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Text;
using COMPEL.Internals;
using COMPEL.Services.Supervision;
using FluentAssertions;
using Kongor.Core.Services.Native;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace COMPEL.Tests;

public sealed class NativePipeCommandAndSupervisorTests
{
    [Fact]
    public async Task ExecuteCommandOnPortAsync_WhenNativeGuardPipeAcksOpCode0004_RoutesViaNativePipeWithVerifiedPayload()
    {
        // protects: MatchServerManagerSupervisor.ExecuteCommandOnPortAsync + DedicatedServerWorker.WriteCommandAsync
        //           12-byte 0x4D 0x57 OpCode 0x0003 framing and OpCode 0x0004 JSON ACK verification.
        // fails_when: Commands route only to dead redirected stdin or corrupt the 12-byte NativeGuard binary header.
        // seam: Real OS NamedPipeServerStream <-> MatchServerManagerSupervisor.ExecuteCommandOnPortAsync.
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        string pipeName = $"kongor-test-native-{Guid.NewGuid():N}";
        string receivedCommand = string.Empty;
        ushort receivedOpCode = 0;

        using CancellationTokenSource cts = new(TimeSpan.FromSeconds(5));
        Task serverTask = Task.Run(async () =>
        {
            await using NamedPipeServerStream server = new(
                pipeName,
                PipeDirection.InOut,
                1,
                PipeTransmissionMode.Byte,
                PipeOptions.Asynchronous);

            await server.WaitForConnectionAsync(cts.Token).ConfigureAwait(false);

            byte[] reqHeader = new byte[12];
            int headerRead = 0;
            while (headerRead < 12)
            {
                int r = await server.ReadAsync(reqHeader.AsMemory(headerRead, 12 - headerRead), cts.Token).ConfigureAwait(false);
                if (r <= 0) break;
                headerRead += r;
            }

            reqHeader[0].Should().Be(0x4D);
            reqHeader[1].Should().Be(0x57);
            receivedOpCode = BitConverter.ToUInt16(reqHeader.AsSpan(2, 2));
            int payloadLen = BitConverter.ToInt32(reqHeader.AsSpan(4, 4));

            byte[] payloadBuf = new byte[payloadLen];
            int bodyRead = 0;
            while (bodyRead < payloadLen)
            {
                int r = await server.ReadAsync(payloadBuf.AsMemory(bodyRead, payloadLen - bodyRead), cts.Token).ConfigureAwait(false);
                if (r <= 0) break;
                bodyRead += r;
            }

            receivedCommand = Encoding.UTF8.GetString(payloadBuf, 0, bodyRead);

            byte[] ackJson = Encoding.UTF8.GetBytes("{\"success\":true,\"queued\":\"AddFakePlayer 1\"}");
            byte[] respFrame = new byte[12 + ackJson.Length];
            respFrame[0] = 0x4D;
            respFrame[1] = 0x57;
            BitConverter.TryWriteBytes(respFrame.AsSpan(2, 2), (ushort)0x0004);
            BitConverter.TryWriteBytes(respFrame.AsSpan(4, 4), ackJson.Length);
            BitConverter.TryWriteBytes(respFrame.AsSpan(8, 4), 1u);
            ackJson.CopyTo(respFrame.AsSpan(12));

            await server.WriteAsync(respFrame, cts.Token).ConfigureAwait(false);
            await server.FlushAsync(cts.Token).ConfigureAwait(false);
        }, cts.Token);

        MatchServerManagerOptions opts = new() { Instances = 1, AutoStart = false };
        using MatchServerManagerSupervisor supervisor = new(
            Options.Create(opts),
            NullLogger<MatchServerManagerSupervisor>.Instance);

        DedicatedServerWorker worker = new(1, 11235, 11435, 21235, 21435, "TestWorker_11235")
        {
            IsRunningOverride = true,
            CurrentState = "InMatch",
            NativePipeNameOverride = pipeName
        };
        supervisor.RegisterWorkerForTesting(worker);

        (bool success, string channel, string message) =
            await supervisor.ExecuteCommandOnPortAsync(11235, "AddFakePlayer 1", cts.Token);

        await serverTask;

        success.Should().BeTrue();
        channel.Should().Be("native-pipe");
        message.Should().Contain("\"success\":true");
        receivedOpCode.Should().Be(0x0003);
        receivedCommand.Should().Be("AddFakePlayer 1");
    }

    [Fact]
    public async Task WriteCommandAsync_WhenNativeGuardPipeReturnsErrorJsonAndNoStdin_ReportsFailureTruthfully()
    {
        // protects: Honest failure reporting when NativeGuard returns OpCode 0x0004 with {"success":false}.
        // fails_when: WriteCommandAsync returns true without inspecting the NativeGuard JSON ACK payload.
        // seam: Real OS NamedPipeServerStream returning OpCode 0x0004 rejection frame.
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        string pipeName = $"kongor-test-native-err-{Guid.NewGuid():N}";
        using CancellationTokenSource cts = new(TimeSpan.FromSeconds(5));
        Task serverTask = Task.Run(async () =>
        {
            await using NamedPipeServerStream server = new(
                pipeName,
                PipeDirection.InOut,
                1,
                PipeTransmissionMode.Byte,
                PipeOptions.Asynchronous);

            await server.WaitForConnectionAsync(cts.Token).ConfigureAwait(false);

            byte[] reqHeader = new byte[12];
            int headerRead = 0;
            while (headerRead < 12)
            {
                int r = await server.ReadAsync(reqHeader.AsMemory(headerRead, 12 - headerRead), cts.Token).ConfigureAwait(false);
                if (r <= 0) break;
                headerRead += r;
            }

            int payloadLen = BitConverter.ToInt32(reqHeader.AsSpan(4, 4));
            byte[] payloadBuf = new byte[payloadLen];
            int bodyRead = 0;
            while (bodyRead < payloadLen)
            {
                int r = await server.ReadAsync(payloadBuf.AsMemory(bodyRead, payloadLen - bodyRead), cts.Token).ConfigureAwait(false);
                if (r <= 0) break;
                bodyRead += r;
            }

            byte[] errJson = Encoding.UTF8.GetBytes("{\"success\":false,\"error\":\"ring_full\"}");
            byte[] respFrame = new byte[12 + errJson.Length];
            respFrame[0] = 0x4D;
            respFrame[1] = 0x57;
            BitConverter.TryWriteBytes(respFrame.AsSpan(2, 2), (ushort)0x0004);
            BitConverter.TryWriteBytes(respFrame.AsSpan(4, 4), errJson.Length);
            BitConverter.TryWriteBytes(respFrame.AsSpan(8, 4), 1u);
            errJson.CopyTo(respFrame.AsSpan(12));

            await server.WriteAsync(respFrame, cts.Token).ConfigureAwait(false);
            await server.FlushAsync(cts.Token).ConfigureAwait(false);
        }, cts.Token);

        DedicatedServerWorker worker = new(1, 11235, 11435, 21235, 21435, "TestWorker_Err")
        {
            IsRunningOverride = true,
            CurrentState = "InMatch",
            NativePipeNameOverride = pipeName
        };

        (bool success, string channel, string detail) = await worker.WriteCommandAsync("AddFakePlayer 1", 1000, cts.Token);
        await serverTask;

        success.Should().BeFalse();
        channel.Should().Be("native-pipe");
        detail.Should().Contain("ring_full");
    }

    [Fact]
    public async Task WriteCommandAsync_WhenNoNativePipeListening_FallsBackDirectlyToStdin()
    {
        // protects: Fallback to redirected stdin when no NativeGuard pipe server is listening for the worker port.
        // fails_when: WriteCommandAsync drops the command or throws when NamedPipeClientStream.ConnectAsync fails.
        // seam: DedicatedServerWorker.WriteCommandAsync + MemoryStream StdinWriter.
        DedicatedServerWorker worker = new(1, 59991, 60191, 61991, 62191, "StdinFallbackWorker")
        {
            IsRunningOverride = true,
            CurrentState = "Available",
            NativePipeNameOverride = $"kongor-missing-pipe-{Guid.NewGuid():N}"
        };
        using MemoryStream ms = new();
        using StreamWriter stdinWriter = new(ms, Encoding.UTF8, leaveOpen: true);
        worker.StdinWriter = stdinWriter;

        (bool success, string channel, _) = await worker.WriteCommandAsync("echo hermetic");

        success.Should().BeTrue();
        channel.Should().Be("stdin");
        Encoding.UTF8.GetString(ms.ToArray()).Should().Contain("echo hermetic");
    }

    [Fact]
    public void IsIntentionalWorkerExit_DistinguishesDrainsAndStopsFromUnexpectedCrashes()
    {
        // protects: MatchServerManagerSupervisor.OnWorkerExited crash-accounting and double-respawn guard.
        // fails_when: Clean drains/restarts/stops increment CrashCount or non-zero active crashes are ignored.
        // seam: MatchServerManagerSupervisor.IsIntentionalWorkerExit state-machine predicate.
        DedicatedServerWorker activeWorker = new(1, 11235, 11435, 21235, 21435, "ActiveWorker")
        {
            DesiredRunning = true,
            IsDraining = false,
            CurrentState = "InMatch"
        };

        MatchServerManagerSupervisor.IsIntentionalWorkerExit(activeWorker, -1073741819).Should().BeFalse();
        MatchServerManagerSupervisor.IsIntentionalWorkerExit(activeWorker, -1).Should().BeFalse();
        MatchServerManagerSupervisor.IsIntentionalWorkerExit(activeWorker, 0).Should().BeTrue();

        DedicatedServerWorker drainingWorker = new(2, 11236, 11436, 21236, 21436, "DrainingWorker")
        {
            DesiredRunning = true,
            IsDraining = true,
            CurrentState = "Restarting"
        };
        MatchServerManagerSupervisor.IsIntentionalWorkerExit(drainingWorker, -1).Should().BeTrue();

        DedicatedServerWorker restartingWorker = new(3, 11237, 11437, 21237, 21437, "RestartingWorker")
        {
            DesiredRunning = true,
            IsDraining = false,
            CurrentState = "Restarting"
        };
        MatchServerManagerSupervisor.IsIntentionalWorkerExit(restartingWorker, -1).Should().BeTrue();

        DedicatedServerWorker stoppedWorker = new(4, 11238, 11438, 21238, 21438, "StoppedWorker")
        {
            DesiredRunning = false,
            IsDraining = false,
            CurrentState = "Available"
        };
        MatchServerManagerSupervisor.IsIntentionalWorkerExit(stoppedWorker, -1).Should().BeTrue();
    }

    [Fact]
    public void GetRemoteModuleHandleByFullPath_MatchesExactModulePathAndRejectsSameBasenameAtDifferentPath()
    {
        // protects: NativeProcessInjectionService.InjectDll against C:\Windows\System32\version.dll basename collision.
        // fails_when: GetRemoteModuleHandleByFullPath matches by filename instead of full path.
        // seam: Win32 EnumProcessModulesEx + GetModuleFileNameExW on Process.GetCurrentProcess().
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        using Process current = Process.GetCurrentProcess();
        string systemKernel32 = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "kernel32.dll");
        string fakeLocalKernel32 = Path.Combine(AppContext.BaseDirectory, "kernel32.dll");

        nint realHandle = NativeProcessInjectionService.GetRemoteModuleHandleByFullPath(current.Handle, systemKernel32);
        nint fakeHandle = NativeProcessInjectionService.GetRemoteModuleHandleByFullPath(current.Handle, fakeLocalKernel32);

        realHandle.Should().NotBe(nint.Zero);
        fakeHandle.Should().Be(nint.Zero);
    }
}
