// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

using System.IO;
using System.IO.Pipes;
using System.Text;
using COMPEL.Services.Supervision;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace COMPEL.Tests;

public sealed class DedicatedCommandRoutingTests
{
    [Fact]
    public void Worker_PipeName_ConformsToDedicatedServerConvention()
    {
        DedicatedServerWorker worker = new(1, 11235, 11435, 21235, 21435, "TestServer");
        worker.PipeName.Should().Be("hon-server-11235");
        worker.FullPipePath.Should().Be(@"\\.\pipe\hon-server-11235");
    }

    [Fact]
    public async Task WriteStdinAsync_WithValidWriter_WritesCommandSuccessfully()
    {
        DedicatedServerWorker worker = new(1, 11235, 11435, 21235, 21435, "TestServer")
        {
            IsRunningOverride = true
        };

        using MemoryStream ms = new();
        using StreamWriter writer = new(ms, Encoding.UTF8, leaveOpen: true);
        worker.StdinWriter = writer;

        bool result = await worker.WriteStdinAsync("AddFakePlayer 1");

        result.Should().BeTrue();
        string written = Encoding.UTF8.GetString(ms.ToArray());
        written.Should().Contain("AddFakePlayer 1");
    }

    [Fact]
    public async Task WriteStdinAsync_WhenOffline_ReturnsFalse()
    {
        DedicatedServerWorker worker = new(1, 11235, 11435, 21235, 21435, "TestServer")
        {
            IsRunningOverride = false
        };

        bool result = await worker.WriteStdinAsync("AddFakePlayer 1");
        result.Should().BeFalse();
    }

    [Fact]
    public async Task WritePipeAsync_WhenNoServerListening_TimesOutGracefully()
    {
        DedicatedServerWorker worker = new(1, 11999, 11998, 21999, 21998, "TestServer")
        {
            IsRunningOverride = true
        };

        bool result = await worker.WritePipeAsync("AddFakePlayer 1", timeoutMs: 50);
        result.Should().BeFalse();
    }

    [Fact]
    public async Task WritePipeAsync_WhenNamedPipeServerListening_TransmitsCommandCleanly()
    {
        if (!OperatingSystem.IsWindows()) return;

        // Use a high ephemeral port to prevent collisions
        int testPort = Random.Shared.Next(40000, 50000);
        DedicatedServerWorker worker = new(1, testPort, testPort + 200, testPort + 10000, testPort + 10200, "TestServer")
        {
            IsRunningOverride = true
        };

        string nativePipeName = $"hon-server-native-{testPort}";
        using CancellationTokenSource cts = new(TimeSpan.FromSeconds(10));
        string received = string.Empty;
        TaskCompletionSource serverReady = new(TaskCreationOptions.RunContinuationsAsynchronously);

        Task serverTask = Task.Run(async () =>
        {
            await using NamedPipeServerStream server = new(
                nativePipeName,
                PipeDirection.InOut,
                1,
                PipeTransmissionMode.Byte,
                PipeOptions.Asynchronous);

            Task waitTask = server.WaitForConnectionAsync(cts.Token);
            serverReady.SetResult();
            await waitTask;

            try
            {
                byte[] header = new byte[12];
                await server.ReadExactlyAsync(header, cts.Token);
                int payloadLen = BitConverter.ToInt32(header, 4);
                byte[] payload = new byte[payloadLen];
                await server.ReadExactlyAsync(payload, cts.Token);
                received = Encoding.UTF8.GetString(payload);
            }
            catch (IOException)
            {
                // Client disconnected after writing
            }
        }, cts.Token);

        // Wait deterministically for pipe server to be actively listening
        await serverReady.Task.WaitAsync(cts.Token);

        bool writeResult = await worker.WritePipeAsync("AddFakePlayer 2", timeoutMs: 5000, cancellationToken: cts.Token);
        await serverTask;

        writeResult.Should().BeTrue();
        received.Should().Be("AddFakePlayer 2");
    }

    [Fact]
    public async Task ExecuteCommandOnPortAsync_WhenStdinFails_TruthfullyReportsFailureWithoutPipeLoopback()
    {
        MatchServerManagerSupervisor supervisor = new(
            Options.Create(new MatchServerManagerOptions()),
            NullLogger<MatchServerManagerSupervisor>.Instance);

        DedicatedServerWorker worker = new(1, 11235, 11435, 21235, 21435, "TestServer")
        {
            IsRunningOverride = true,
            StdinWriter = null // Stdin stream unavailable
        };

        supervisor.RegisterWorkerForTesting(worker);

        (bool success, string channel, string message) = await supervisor.ExecuteCommandOnPortAsync(11235, "AddFakePlayer 1");

        success.Should().BeFalse("broken or null stdin must never falsely report delivery via pipe loopback");
        channel.Should().Be("failed");
        message.Should().Contain("unavailable or process is unresponsive");
    }

    [Fact]
    public async Task ExecuteCommandOnPortAsync_WhenWorkerOffline_ReturnsOfflineFailure()
    {
        MatchServerManagerSupervisor supervisor = new(
            Options.Create(new MatchServerManagerOptions()),
            NullLogger<MatchServerManagerSupervisor>.Instance);

        DedicatedServerWorker worker = new(1, 11235, 11435, 21235, 21435, "TestServer")
        {
            IsRunningOverride = false
        };

        supervisor.RegisterWorkerForTesting(worker);

        (bool success, string channel, string message) = await supervisor.ExecuteCommandOnPortAsync(11235, "status");

        success.Should().BeFalse();
        channel.Should().Be("none");
        message.Should().Contain("not running");
    }

    [Fact]
    public async Task ExecuteCommandOnPortAsync_WhenStdinSucceeds_ReturnsSuccess()
    {
        MatchServerManagerSupervisor supervisor = new(
            Options.Create(new MatchServerManagerOptions()),
            NullLogger<MatchServerManagerSupervisor>.Instance);

        DedicatedServerWorker worker = new(1, 11235, 11435, 21235, 21435, "TestServer")
        {
            IsRunningOverride = true
        };

        using MemoryStream ms = new();
        using StreamWriter writer = new(ms, Encoding.UTF8, leaveOpen: true);
        worker.StdinWriter = writer;

        supervisor.RegisterWorkerForTesting(worker);

        (bool success, string channel, string message) = await supervisor.ExecuteCommandOnPortAsync(11235, "AddFakePlayer 1");

        success.Should().BeTrue();
        channel.Should().Be("stdin");
        message.Should().Contain("Successfully piped");

        string written = Encoding.UTF8.GetString(ms.ToArray());
        written.Should().Contain("AddFakePlayer 1");
    }
}
