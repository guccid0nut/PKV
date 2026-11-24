// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

// ============================================================================
// File: WatchdogMonitoringTests.cs
// System: NEXUS Replay Video Renderer Test Suite
// Policy: Strict Explicit Typing (NO 'var'), xUnit Framework
// ============================================================================

using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using ReplayRenderer.Service.Models;
using ReplayRenderer.Service.Monitoring;
using Xunit;
using Xunit.Abstractions;

namespace ReplayRenderer.Tests;

public sealed class WatchdogMonitoringTests(ITestOutputHelper output)
{
    private readonly ILoggerFactory _loggerFactory = NullLoggerFactory.Instance;
    private readonly ITestOutputHelper _output = output;

    [Fact]
    public void RenderJobProgress_PercentageAndFormatting_CalculatesCorrectly()
    {
        _output.WriteLine("[Test] Testing RenderJobProgress percentage calculations and ToString()...");

        RenderJobProgress progressNullEst = new()
        {
            JobId = "job_alpha",
            State = RenderJobState.Capturing,
            TotalFramesRendered = 300,
            CurrentFps = 60.0,
            SpeedMultiplier = 1.0,
            ElapsedTime = TimeSpan.FromSeconds(5),
            EstimatedTotalFrames = null
        };

        Assert.Null(progressNullEst.PercentComplete);
        string summary1 = progressNullEst.ToString();
        _output.WriteLine($"[Test] Summary (null est): {summary1}");
        Assert.Contains("job_alpha", summary1);
        Assert.Contains("Frames=300", summary1);

        RenderJobProgress progressHalf = new()
        {
            JobId = "job_beta",
            State = RenderJobState.Encoding,
            TotalFramesRendered = 500,
            CurrentFps = 120.0,
            SpeedMultiplier = 2.0,
            ElapsedTime = TimeSpan.FromSeconds(10),
            EstimatedTotalFrames = 1000
        };

        Assert.NotNull(progressHalf.PercentComplete);
        Assert.Equal(50.0, progressHalf.PercentComplete!.Value);
        string summary2 = progressHalf.ToString();
        _output.WriteLine($"[Test] Summary (50%): {summary2}");
        Assert.Contains("50.0%", summary2);

        // Clamp test: frames exceed estimated total
        RenderJobProgress progressOverflow = new()
        {
            JobId = "job_gamma",
            TotalFramesRendered = 2500,
            EstimatedTotalFrames = 2000
        };
        Assert.Equal(100.0, progressOverflow.PercentComplete!.Value);
    }

    [Fact]
    public async Task RenderProgressMonitor_FrameNotification_TracksCumulativeFrames()
    {
        _output.WriteLine("[Test] Testing RenderProgressMonitor frame notification tracking...");
        ILogger<RenderProgressMonitor> logger = _loggerFactory.CreateLogger<RenderProgressMonitor>();

        await using RenderProgressMonitor monitor = new(
            logger,
            "frame_count_test",
            1000,
            TimeSpan.FromSeconds(10));

        monitor.Start();

        for (int i = 0; i < 50; i++) monitor.NotifyFrameRendered();

        Assert.Equal(50, monitor.TotalFramesRendered);
        _output.WriteLine($"[Test] Cumulative frames tracked: {monitor.TotalFramesRendered}");

        for (int i = 0; i < 50; i++) monitor.NotifyFrameRendered();

        Assert.Equal(100, monitor.TotalFramesRendered);
        _output.WriteLine($"[Test] Updated cumulative frames tracked: {monitor.TotalFramesRendered}");
    }

    [Fact]
    public async Task RenderProgressMonitor_StallWatchdog_TriggersOnTimeout()
    {
        _output.WriteLine("[Test] Testing RenderProgressMonitor stall watchdog detection...");
        ILogger<RenderProgressMonitor> logger = _loggerFactory.CreateLogger<RenderProgressMonitor>();

        // Set a short stall threshold for test: 300ms
        TimeSpan shortStall = TimeSpan.FromMilliseconds(300);

        await using RenderProgressMonitor monitor = new(
            logger,
            "stall_test_job",
            500,
            shortStall);

        TaskCompletionSource<bool> stallTcs = new(TaskCreationOptions.RunContinuationsAsynchronously);

        monitor.OnStallDetected += () =>
        {
            _output.WriteLine("[Test Watchdog Event] OnStallDetected fired successfully!");
            stallTcs.TrySetResult(true);
        };

        monitor.Start();

        // Feed an initial frame to activate the stream
        monitor.NotifyFrameRendered();
        _output.WriteLine("[Test] Initial frame notified. Halting feed to trigger stall...");

        // Wait up to 5 seconds for watchdog to trigger (timer ticks at 1 second intervals)
        using CancellationTokenSource cts = new(TimeSpan.FromSeconds(6));
        bool stallDetected = await stallTcs.Task.WaitAsync(cts.Token);

        Assert.True(stallDetected);
        _output.WriteLine("[Test] Watchdog successfully detected frame stream stall and triggered callback.");
    }

    [Fact]
    public void TelemetryLogger_LogMethods_ExecuteWithoutException()
    {
        _output.WriteLine("[Test] Testing TelemetryLogger banner, diagnostics, and summary logging...");
        ILogger<TelemetryLogger> logger = _loggerFactory.CreateLogger<TelemetryLogger>();

        TelemetryLogger telemetry = new(logger);

        RenderJobConfiguration config = new()
        {
            ReplayFilePath = "test.honreplay",
            OutputFilePath = "test.mp4",
            GameDirectory = "C:\\Games\\HoN",
            VulkanLayerPath = "C:\\Vulkan",
            RenderWidth = 1920,
            RenderHeight = 1080,
            TargetFramerate = 60,
            Codec = VideoCodec.H264Nvenc,
            AudioSampleRate = 48000,
            UseSharedMemoryIpc = false,
            Headless = true
        };

        telemetry.LogJobBanner(config, "job_test_diag_01");
        telemetry.LogSystemDiagnostics();
        telemetry.LogJobSummary(config, 1800, TimeSpan.FromSeconds(30), 0, 1920L * 1080L * 4L * 1800L);

        _output.WriteLine("[Test] All telemetry log methods executed cleanly.");
    }
}
