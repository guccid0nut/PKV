// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

// ============================================================================
// File: WindowFrameCapturerZeroExposureTests.cs
// System: NEXUS Replay Video Renderer Test Suite
// Policy: Strict Explicit Typing (NO 'var'), xUnit Framework
// ============================================================================

using FluentAssertions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using ReplayRenderer.Service.Ipc;
using Xunit;

namespace ReplayRenderer.Tests;

public sealed class WindowFrameCapturerZeroExposureTests
{
    [Fact]
    public void Constructor_ByDefault_EnforcesZeroWindowExposure()
    {
        WindowFrameCapturer capturer = new(
            NullLogger<WindowFrameCapturer>.Instance,
            processId: 4242,
            targetWidth: 640,
            targetHeight: 360,
            targetFps: 60);

        capturer.AllowForegroundActivation.Should().BeFalse();
        capturer.ForegroundActivationAttempted.Should().BeFalse();
        capturer.ZeroWindowExposureEnforced.Should().BeTrue();
    }

    [Fact]
    public async Task StartAsync_DefaultZeroWindowExposure_UsesPrintWindowRenderFullContentAndNeverActivatesForeground()
    {
        // protects: Zero-window-exposure invariant during offscreen replay capture.
        // fails_when: WindowFrameCapturer calls ShowWindow/HWND_TOPMOST/SetForegroundWindow or desktop screen capture by default.
        // seam: WindowFrameCapturer.CaptureLoopAsync -> IWin32WindowCaptureBackend.

        RecordingCaptureBackend backend = new(
            windowBounds: new WindowBounds(-32000, -32000, -32000 + 640, -32000 + 360),
            printWindowSucceeds: true,
            windowDcSucceeds: true,
            desktopSucceeds: true);

        await using WindowFrameCapturer capturer = new(
            NullLogger<WindowFrameCapturer>.Instance,
            processId: 12345,
            targetWidth: 640,
            targetHeight: 360,
            targetFps: 60,
            allowForegroundActivation: false,
            backend: backend);

        TaskCompletionSource<int> receivedFramesTcs = new(TaskCreationOptions.RunContinuationsAsynchronously);
        int framesDelivered = 0;

        using CancellationTokenSource cts = new(TimeSpan.FromSeconds(5));
        await capturer.StartAsync(frame =>
        {
            frame.Length.Should().Be(640 * 360 * 4);
            int count = Interlocked.Increment(ref framesDelivered);
            if (count >= 3)
            {
                receivedFramesTcs.TrySetResult(count);
                cts.Cancel();
            }

            return Task.CompletedTask;
        }, cts.Token);

        int finalCount = await receivedFramesTcs.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await capturer.DisposeAsync();

        finalCount.Should().BeGreaterOrEqualTo(3);
        capturer.AllowForegroundActivation.Should().BeFalse();
        capturer.ForegroundActivationAttempted.Should().BeFalse();
        capturer.ZeroWindowExposureEnforced.Should().BeTrue();
        backend.ActivateForegroundCallCount.Should().Be(0, "zero-window-exposure must never call ShowWindow/HWND_TOPMOST/SetForegroundWindow");
        backend.PrintWindowCallCount.Should().BeGreaterOrEqualTo(3);
        backend.ObservedPrintWindowFlags.Should().AllBeEquivalentTo(WindowFrameCapturer.PwRenderFullContent);
        backend.WindowDcCallCount.Should().Be(0);
        backend.DesktopCaptureCallCount.Should().Be(0);
        capturer.PrintWindowSuccessCount.Should().BeGreaterOrEqualTo(3);
        capturer.DesktopFallbackCount.Should().Be(0);
    }

    [Fact]
    public async Task StartAsync_WhenPrintWindowFailsInZeroExposureMode_FallsBackToWindowDcAndNeverScrapesDesktop()
    {
        RecordingCaptureBackend backend = new(
            windowBounds: new WindowBounds(100, 100, 740, 460),
            printWindowSucceeds: false,
            windowDcSucceeds: false,
            desktopSucceeds: true);

        await using WindowFrameCapturer capturer = new(
            NullLogger<WindowFrameCapturer>.Instance,
            processId: 23456,
            targetWidth: 640,
            targetHeight: 360,
            targetFps: 60,
            allowForegroundActivation: false,
            backend: backend);

        TaskCompletionSource<int> firstFrameTcs = new(TaskCreationOptions.RunContinuationsAsynchronously);
        using CancellationTokenSource cts = new(TimeSpan.FromSeconds(5));
        await capturer.StartAsync(_ =>
        {
            firstFrameTcs.TrySetResult(1);
            cts.Cancel();
            return Task.CompletedTask;
        }, cts.Token);

        await firstFrameTcs.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await capturer.DisposeAsync();

        backend.ActivateForegroundCallCount.Should().Be(0);
        backend.PrintWindowCallCount.Should().BeGreaterOrEqualTo(1);
        backend.WindowDcCallCount.Should().BeGreaterOrEqualTo(1);
        backend.DesktopCaptureCallCount.Should().Be(0, "desktop screen capture fallback must stay disabled when AllowForegroundActivation=false");
        capturer.ZeroWindowExposureEnforced.Should().BeTrue();
    }

    [Fact]
    public async Task StartAsync_WhenExplicitlyOptedIntoForegroundActivation_ActivatesWindowOnce()
    {
        RecordingCaptureBackend backend = new(
            windowBounds: new WindowBounds(0, 0, 640, 360),
            printWindowSucceeds: false,
            windowDcSucceeds: false,
            desktopSucceeds: true);

        await using WindowFrameCapturer capturer = new(
            NullLogger<WindowFrameCapturer>.Instance,
            processId: 34567,
            targetWidth: 640,
            targetHeight: 360,
            targetFps: 60,
            allowForegroundActivation: true,
            backend: backend);

        TaskCompletionSource<int> firstFrameTcs = new(TaskCreationOptions.RunContinuationsAsynchronously);
        using CancellationTokenSource cts = new(TimeSpan.FromSeconds(5));
        await capturer.StartAsync(_ =>
        {
            firstFrameTcs.TrySetResult(1);
            cts.Cancel();
            return Task.CompletedTask;
        }, cts.Token);

        await firstFrameTcs.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await capturer.DisposeAsync();

        capturer.AllowForegroundActivation.Should().BeTrue();
        capturer.ForegroundActivationAttempted.Should().BeTrue();
        capturer.ZeroWindowExposureEnforced.Should().BeFalse();
        backend.ActivateForegroundCallCount.Should().Be(1);
        backend.DesktopCaptureCallCount.Should().BeGreaterOrEqualTo(1);
        capturer.DesktopFallbackCount.Should().BeGreaterOrEqualTo(1);
    }

    private sealed class RecordingCaptureBackend(
        WindowBounds windowBounds,
        bool printWindowSucceeds,
        bool windowDcSucceeds,
        bool desktopSucceeds) : IWin32WindowCaptureBackend
    {
        private readonly WindowBounds _windowBounds = windowBounds;
        private readonly bool _printWindowSucceeds = printWindowSucceeds;
        private readonly bool _windowDcSucceeds = windowDcSucceeds;
        private readonly bool _desktopSucceeds = desktopSucceeds;
        private readonly List<uint> _printWindowFlags = [];

        public int ActivateForegroundCallCount { get; private set; }
        public int PrintWindowCallCount { get; private set; }
        public int WindowDcCallCount { get; private set; }
        public int DesktopCaptureCallCount { get; private set; }
        public IReadOnlyList<uint> ObservedPrintWindowFlags => _printWindowFlags;

        public IntPtr FindMainWindow(int processId, ILogger logger) => new(0x123456);

        public bool TryGetWindowDimensions(IntPtr hwnd, out int width, out int height)
        {
            width = _windowBounds.Width;
            height = _windowBounds.Height;
            return true;
        }

        public bool IsWindowValid(IntPtr hwnd) => hwnd != IntPtr.Zero;

        public bool TryGetWindowBounds(IntPtr hwnd, out WindowBounds bounds)
        {
            bounds = _windowBounds;
            return true;
        }

        public void ActivateForegroundWindow(IntPtr hwnd, ILogger logger)
        {
            ActivateForegroundCallCount++;
        }

        public IWindowCaptureSurface CreateCaptureSurface(int targetWidth, int targetHeight)
        {
            return new RecordingCaptureSurface(this, targetWidth * targetHeight * 4);
        }

        private sealed class RecordingCaptureSurface(RecordingCaptureBackend owner, int frameBytes) : IWindowCaptureSurface
        {
            private readonly RecordingCaptureBackend _owner = owner;
            private readonly int _frameBytes = frameBytes;

            public bool TryCapturePrintWindow(IntPtr hwnd, uint flags, int windowWidth, int windowHeight)
            {
                _owner.PrintWindowCallCount++;
                _owner._printWindowFlags.Add(flags);
                return _owner._printWindowSucceeds;
            }

            public bool TryCaptureWindowDc(IntPtr hwnd, int windowWidth, int windowHeight)
            {
                _owner.WindowDcCallCount++;
                return _owner._windowDcSucceeds;
            }

            public bool TryCaptureDesktopScreen(WindowBounds bounds, int windowWidth, int windowHeight)
            {
                _owner.DesktopCaptureCallCount++;
                return _owner._desktopSucceeds;
            }

            public void CopyPixelsTo(byte[] destination)
            {
                int length = Math.Min(destination.Length, _frameBytes);
                Array.Fill<byte>(destination, 0x7F, 0, length);
            }

            public void Dispose()
            {
            }
        }
    }
}
