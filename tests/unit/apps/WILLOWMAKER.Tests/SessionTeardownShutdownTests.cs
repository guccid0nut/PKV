// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using Kongor.Core.Services.Voice;
using WILLOWMAKER.Core.ViewModels;
using Xunit;

namespace WILLOWMAKER.Tests;

/// <summary>
/// Value-Weighted Regression Suite for Issue #6 (WILLOWMAKER Smooth Non-Blocking Shutdown):
/// - protects: Launcher exit responsiveness when hon_x64.exe closes cleanly (ExitCode 0) and UI-thread freedom during voice/session teardown.
/// - fails_when: VoiceClientService.Dispose/StopAsync captures SynchronizationContext or awaits ReceiveFromAsync before socket close, when a session teardown step stalls indefinitely, or when ExitCode 0 waits on the 2.5s minidump poll loop.
/// - seam: Real UDP VoiceClientService loop + real MainViewModel teardown/termination pipeline under a non-pumping SynchronizationContext.
/// </summary>
public sealed class SessionTeardownShutdownTests
{
    private sealed class NonPumpingUiSynchronizationContext : SynchronizationContext
    {
        public ConcurrentQueue<(SendOrPostCallback Callback, object? State)> PostedCallbacks { get; } = new();

        public override void Post(SendOrPostCallback d, object? state)
        {
            // Simulate a UI thread currently blocked inside synchronous teardown (never pumping queued continuations)
            PostedCallbacks.Enqueue((d, state));
        }

        public override void Send(SendOrPostCallback d, object? state)
        {
            throw new InvalidOperationException("Synchronous Send onto blocked UI SynchronizationContext would deadlock.");
        }
    }

    [Fact]
    public async Task VoiceClientService_Dispose_DoesNotDeadlock_UnderBlockedUiSynchronizationContext()
    {
        Task worker = Task.Run(async () =>
        {
            SynchronizationContext? previous = SynchronizationContext.Current;
            NonPumpingUiSynchronizationContext blockedUiCtx = new();
            SynchronizationContext.SetSynchronizationContext(blockedUiCtx);
            try
            {
                using VoiceClientService voice = new(localAccountId: 42u);
                voice.Start(localPort: 0);

                // Give background ReceiveFromAsync / PeriodicTimer loops a brief moment to enter kernel/async wait
                await Task.Delay(30).ConfigureAwait(false);

                SynchronizationContext.SetSynchronizationContext(blockedUiCtx);
                long start = Stopwatch.GetTimestamp();
                voice.Dispose();
                double elapsedMs = Stopwatch.GetElapsedTime(start).TotalMilliseconds;

                elapsedMs.Should().BeLessThan(400.0, "synchronous Dispose must never block on async continuations");
            }
            finally
            {
                SynchronizationContext.SetSynchronizationContext(previous);
            }
        });

        await worker.WaitAsync(TimeSpan.FromSeconds(2));
    }

    [Fact]
    public async Task VoiceClientService_DisposeAsync_CompletesWithoutPumpingUiSynchronizationContext()
    {
        Task worker = Task.Run(async () =>
        {
            SynchronizationContext? previous = SynchronizationContext.Current;
            NonPumpingUiSynchronizationContext blockedUiCtx = new();
            SynchronizationContext.SetSynchronizationContext(blockedUiCtx);
            try
            {
                VoiceClientService voice = new(localAccountId: 77u);
                voice.Start(localPort: 0);
                await Task.Delay(30).ConfigureAwait(false);

                SynchronizationContext.SetSynchronizationContext(blockedUiCtx);
                await voice.DisposeAsync().ConfigureAwait(false);
            }
            finally
            {
                SynchronizationContext.SetSynchronizationContext(previous);
            }
        });

        await worker.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task RunBoundedTeardownStepAsync_TimesOutCleanly_WhenStepStallsIndefinitely()
    {
        TaskCompletionSource<bool> neverCompletes = new(TaskCreationOptions.RunContinuationsAsynchronously);
        ConcurrentQueue<string> logs = new();

        long start = Stopwatch.GetTimestamp();
        await MainViewModel.RunBoundedTeardownStepAsync(
            stepName: "StalledSignalRHub",
            asyncAction: () => neverCompletes.Task,
            timeout: TimeSpan.FromMilliseconds(80),
            log: msg => logs.Enqueue(msg));
        double elapsedMs = Stopwatch.GetElapsedTime(start).TotalMilliseconds;

        elapsedMs.Should().BeLessThan(600.0, "bounded teardown must abandon hung steps after timeout");
        logs.Should().Contain(line => line.Contains("timed out", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task HandleGameProcessTerminationAsync_CleanExitZero_SkipsCrashPollDelay_AndInvokesCleanShutdown()
    {
        string tempClientDir = Path.Combine(Path.GetTempPath(), "willowmaker_shutdown_test_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempClientDir);
        try
        {
            // Warm up MainViewModel / HandleGameProcessTerminationAsync JIT before timing clean ExitCode 0 fast-path
            MainViewModel warmupVm = new();
            warmupVm.ExitProcessOverride = _ => { };
            await warmupVm.HandleGameProcessTerminationAsync(0u, DateTimeOffset.UtcNow, tempClientDir);
            warmupVm.DisposeApplicationBackgroundServices();

            MainViewModel vm = new();
            int? capturedExitCode = null;
            vm.ExitProcessOverride = code => capturedExitCode = code;

            long start = Stopwatch.GetTimestamp();
            await vm.HandleGameProcessTerminationAsync(
                exitCode: 0u,
                launchStartUtc: DateTimeOffset.UtcNow,
                clientRoot: tempClientDir);
            double elapsedMs = Stopwatch.GetElapsedTime(start).TotalMilliseconds;

            vm.DisposeApplicationBackgroundServices();

            capturedExitCode.Should().Be(0);
            elapsedMs.Should().BeLessThan(2000.0, "clean ExitCode 0 must not wait for the 2500ms minidump flush loop");
        }
        finally
        {
            try
            {
                Directory.Delete(tempClientDir, recursive: true);
            }
            catch
            {
                // Best-effort temp cleanup
            }
        }
    }
}
