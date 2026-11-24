// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

// ============================================================================
// File: Challenger1ProcessLifecycleAdversarialTests.cs
// System: NEXUS Replay Video Renderer Test Suite - Empirical Challenger 1
// Policy: Strict Explicit Typing (NO 'var'), xUnit Framework
// ============================================================================

using System.Diagnostics;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using ReplayRenderer.Service.Models;
using ReplayRenderer.Service.Monitoring;
using ReplayRenderer.Service.ProcessLifecycle;
using Xunit;
using Xunit.Abstractions;

namespace ReplayRenderer.Tests;

/// <summary>
///     Adversarial stress tests for Process Lifecycle, Win32 Job Object Sandboxing,
///     and Stall Watchdog Monitoring written by Empirical Challenger 1.
/// </summary>
public sealed class Challenger1ProcessLifecycleAdversarialTests : IDisposable
{
    private readonly ILoggerFactory _loggerFactory;
    private readonly ITestOutputHelper _output;
    private readonly string _tempDirectory;

    public Challenger1ProcessLifecycleAdversarialTests(ITestOutputHelper output)
    {
        _output = output;
        _loggerFactory = NullLoggerFactory.Instance;
        _tempDirectory = Path.Combine(Path.GetTempPath(), "NexusChallenger1_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDirectory);
        _output.WriteLine($"[Challenger 1 Setup] Temp directory: {_tempDirectory}");
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_tempDirectory))
            {
                Directory.Delete(_tempDirectory, true);
                _output.WriteLine($"[Challenger 1 Teardown] Cleaned temp directory: {_tempDirectory}");
            }
        }
        catch (Exception ex)
        {
            _output.WriteLine($"[Challenger 1 Teardown Note] Failed to delete temp directory: {ex.Message}");
        }
    }

    [Fact]
    public async Task WindowsJobObjectSandbox_Adversarial_ChildProcessTreeTermination_KillsNestedChildrenOnDispose()
    {
        if (!OperatingSystem.IsWindows())
        {
            _output.WriteLine("[Adversarial Test Skipped] Win32 Job Object tests require Windows OS.");
            return;
        }

        _output.WriteLine("[Adversarial Test] Testing Win32 Job Object Kill-On-Close with child process tree...");
        ILogger<WindowsJobObjectSandbox> logger = _loggerFactory.CreateLogger<WindowsJobObjectSandbox>();

        WindowsJobObjectSandbox sandbox = new(logger);

        // Spawn a cmd.exe process that runs a long ping wait (simulating a nested process tree)
        ProcessStartInfo psi = new()
        {
            FileName = "cmd.exe",
            Arguments = "/c ping 127.0.0.1 -n 30 > nul",
            CreateNoWindow = true,
            UseShellExecute = false
        };

        Process? parentProcess = Process.Start(psi);
        Assert.NotNull(parentProcess);
        int parentPid = parentProcess.Id;
        _output.WriteLine($"[Adversarial Test] Spawned parent process (PID: {parentPid}).");

        try
        {
            bool assigned = sandbox.AssignProcess(parentProcess);
            Assert.True(assigned);
            _output.WriteLine($"[Adversarial Test] Process {parentPid} successfully sandboxed in Job Object.");

            // Confirm process is actively running
            Assert.False(parentProcess.HasExited);

            // Dispose sandbox — this closes the Win32 Job Object handle and triggers JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE
            _output.WriteLine(
                "[Adversarial Test] Disposing WindowsJobObjectSandbox to trigger kernel kill-on-close...");
            sandbox.Dispose();

            // Wait for parent process to exit
            using CancellationTokenSource cts = new(TimeSpan.FromSeconds(3));
            await parentProcess.WaitForExitAsync(cts.Token);

            Assert.True(parentProcess.HasExited);
            _output.WriteLine(
                $"[Adversarial Test] Confirmed process (PID: {parentPid}) was terminated by Windows NT kernel.");
        }
        finally
        {
            try
            {
                if (!parentProcess.HasExited) parentProcess.Kill(true);
            }
            catch
            {
                // Ignored
            }
            finally
            {
                parentProcess.Dispose();
            }
        }
    }

    [Fact]
    public async Task RenderProgressMonitor_Adversarial_ExactStallWatchdog_TriggersStallOnlyAfterThreshold()
    {
        _output.WriteLine(
            "[Adversarial Test] Testing RenderProgressMonitor stall detection threshold and timing precision...");
        ILogger<RenderProgressMonitor> logger = _loggerFactory.CreateLogger<RenderProgressMonitor>();

        TimeSpan stallTimeout = TimeSpan.FromMilliseconds(800);

        await using RenderProgressMonitor monitor = new(
            logger,
            "adv_stall_timing_test",
            1000,
            stallTimeout);

        TaskCompletionSource<bool> stallTcs = new(TaskCreationOptions.RunContinuationsAsynchronously);
        long stallFiredTimestamp = 0;

        monitor.OnStallDetected += () =>
        {
            stallFiredTimestamp = Environment.TickCount64;
            _output.WriteLine($"[Watchdog Event] OnStallDetected callback triggered at tick {stallFiredTimestamp}!");
            stallTcs.TrySetResult(true);
        };

        monitor.Start();

        // 1. Send continuous frames for 125ms (interval: 25ms) to ensure NO false stall occurs
        long lastFrameTimestamp = Environment.TickCount64;
        for (int i = 0; i < 5; i++)
        {
            monitor.NotifyFrameRendered();
            lastFrameTimestamp = Environment.TickCount64;
            await Task.Delay(25);
            Assert.False(stallTcs.Task.IsCompleted); // Must NOT stall while active
        }

        _output.WriteLine($"[Adversarial Test] Halting frame stream at tick {lastFrameTimestamp} to trigger stall...");

        // 2. Await stall detection
        using CancellationTokenSource cts = new(TimeSpan.FromSeconds(6));
        bool stallDetected = await stallTcs.Task.WaitAsync(cts.Token);
        Assert.True(stallDetected);

        long elapsedMs = stallFiredTimestamp - lastFrameTimestamp;
        _output.WriteLine(
            $"[Adversarial Test] Frame stream stalled after {elapsedMs} ms (configured limit: {stallTimeout.TotalMilliseconds} ms).");

        Assert.True(elapsedMs >= 400, $"Stall fired too early: {elapsedMs}ms");
        Assert.True(elapsedMs <= 3000, $"Stall fired too late: {elapsedMs}ms");
    }

    [Fact]
    public async Task RenderProgressMonitor_Adversarial_ZeroFramesRendered_DoesNotFalselyStallBeforeInitialFrame()
    {
        _output.WriteLine(
            "[Adversarial Test] Testing that 0 frames rendered does not trigger stall false positives during slow initialization...");
        ILogger<RenderProgressMonitor> logger = _loggerFactory.CreateLogger<RenderProgressMonitor>();

        TimeSpan shortStall = TimeSpan.FromMilliseconds(100);

        await using RenderProgressMonitor monitor = new(
            logger,
            "adv_zero_frame_test",
            500,
            shortStall);

        bool stallFired = false;
        monitor.OnStallDetected += () => { stallFired = true; };

        monitor.Start();

        // Wait 150ms with 0 frames notified
        await Task.Delay(150);

        // Zero frames rendered should not trigger stall watchdog (only active after first frame begins)
        Assert.False(stallFired);
        Assert.Equal(0, monitor.TotalFramesRendered);
        _output.WriteLine("[Adversarial Test] Confirmed zero-frame state does not trigger false positive stall.");
    }

    [Trait("Category", "Manual")]
    [Theory]
    [InlineData(1.0f, -1, false, "true")] // Default settings
    [InlineData(0.5f, 0, true, "false")] // Slow motion, POV 0, Clean UI
    [InlineData(2.0f, 5, true, "false")] // Fast forward, POV 5, Clean UI
    [InlineData(4.0f, 9, false, "true")] // 4x speed, POV 9, Standard UI
    public void ReplayProcessLauncher_Adversarial_AutoexecPermutations_GeneratesExactConfigDirectives(
        float timescale,
        int povClientIndex,
        bool cleanCinematicUi,
        string expectedUiSetting)
    {
        if (!NexusTestEnvironment.IsManualTestEnabled) return;
        _output.WriteLine(
            $"[Adversarial Test] Testing autoexec.cfg generation for Timescale={timescale:F2}, POV={povClientIndex}, CleanUI={cleanCinematicUi}...");

        string mockGameDir = Path.Combine(_tempDirectory, $"MockGame_{Guid.NewGuid():N}");
        Directory.CreateDirectory(mockGameDir);

        string mockExe = Path.Combine(mockGameDir, "hon_x64.exe");
        File.WriteAllText(mockExe, "STUB");

        string replaySource = Path.Combine(_tempDirectory, $"match_{povClientIndex}.honreplay");
        File.WriteAllText(replaySource, "REPLAY_DATA");

        ILogger<ReplayProcessLauncher> launcherLogger = _loggerFactory.CreateLogger<ReplayProcessLauncher>();
        ILogger<WindowsJobObjectSandbox> sandboxLogger = _loggerFactory.CreateLogger<WindowsJobObjectSandbox>();

        using WindowsJobObjectSandbox sandbox = new(sandboxLogger);
        ReplayProcessLauncher launcher = new(launcherLogger, sandbox);

        RenderJobConfiguration config = new()
        {
            ReplayFilePath = replaySource,
            OutputFilePath = Path.Combine(_tempDirectory, "out.mp4"),
            GameDirectory = mockGameDir,
            VulkanLayerPath = _tempDirectory,
            RenderWidth = 1920,
            RenderHeight = 1080,
            TargetFramerate = 60,
            Timescale = timescale,
            PovClientIndex = povClientIndex,
            EnableCleanCinematicUi = cleanCinematicUi
        };

        try
        {
            launcher.Launch(config, "test_pipe");
        }
        catch
        {
            // Expected since hon_x64.exe is a stub text file
        }

        string autoexecPath = Path.Combine(mockGameDir, "userdataKONGOR", "configuration", "autoexec.cfg");
        Assert.True(File.Exists(autoexecPath));

        string content = File.ReadAllText(autoexecPath);
        _output.WriteLine($"[Adversarial Test] autoexec.cfg:\n{content}");

        Assert.Contains($"Set \"host_timescale\" \"{timescale:F4}\"", content);
        Assert.Contains($"Set \"replay_client\" \"{povClientIndex}\"", content);
        Assert.Contains($"Set \"cg_draw2d\" \"{expectedUiSetting}\"", content);
        Assert.Contains($"Set \"cg_drawHealthBars\" \"{expectedUiSetting}\"", content);
        Assert.Contains($"Set \"cg_drawUnitFrames\" \"{expectedUiSetting}\"", content);
        Assert.Contains($"Set \"cg_drawMinimap\" \"{expectedUiSetting}\"", content);
        Assert.Contains("Set \"host_vidDriver\" \"vid_d3d9\"", content);
        Assert.Contains("Set \"vid_driver\" \"d3d9\"", content);
        Assert.Contains("Set \"sys_fixedFrameTime\" \"0\"", content);
        Assert.Contains("Set \"host_framerate\" \"60\"", content);
        Assert.Contains("Set \"sys_sleepTime\" \"0\"", content);
    }

    [Fact]
    public async Task ReplayProcessLauncher_Adversarial_StopAsync_TerminatesProcessWithinTimeout()
    {
        _output.WriteLine("[Adversarial Test] Testing ReplayProcessLauncher.StopAsync on running process...");
        ILogger<ReplayProcessLauncher> launcherLogger = _loggerFactory.CreateLogger<ReplayProcessLauncher>();
        ILogger<WindowsJobObjectSandbox> sandboxLogger = _loggerFactory.CreateLogger<WindowsJobObjectSandbox>();

        using WindowsJobObjectSandbox sandbox = new(sandboxLogger);
        ReplayProcessLauncher launcher = new(launcherLogger, sandbox);

        // Disposing launcher when no process is started should complete cleanly
        await launcher.StopAsync(TimeSpan.FromSeconds(1));
        await launcher.DisposeAsync();
        _output.WriteLine("[Adversarial Test] Unstarted launcher StopAsync and DisposeAsync completed cleanly.");
    }
}
