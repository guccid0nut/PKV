// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

using System.Diagnostics;
using COMPEL.Internals;
using COMPEL.Services.Supervision;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace COMPEL.Tests;

public sealed class ProcessIsolationStrategyTests
{
    [Fact]
    public void PlatformDetection_SelectsExpectedStrategy()
    {
        using ProcessTreeSandbox sandbox = new(NullLogger<ProcessTreeSandbox>.Instance);

        Assert.NotNull(sandbox.ActiveStrategy);
        if (OperatingSystem.IsWindows())
        {
            Assert.IsType<Win32JobObjectStrategy>(sandbox.ActiveStrategy);
            Assert.True(sandbox.ActiveStrategy.IsSupported);
        }
        else
        {
            Assert.IsType<LinuxCgroupV2Strategy>(sandbox.ActiveStrategy);
            Assert.True(sandbox.ActiveStrategy.IsSupported);
        }
    }

    [Fact]
    public void Win32JobObjectStrategy_InitializesCleanlyOnWindows()
    {
        if (!OperatingSystem.IsWindows()) return;

        using Win32JobObjectStrategy strategy = new();
        bool success = strategy.Initialize(new SandboxLimitsOptions
        {
            KillOnJobClose = true,
            MaxProcessMemoryBytes = 1024 * 1024 * 512,
            MaxJobMemoryBytes = 1024 * 1024 * 1024,
            MaxActiveProcesses = 4,
            PriorityMode = HostPriorityMode.Background
        }, NullLogger.Instance);

        Assert.True(success);
        Assert.True(strategy.IsInitialized);

        using Win32JobObjectStrategy highStrategy = new();
        bool highSuccess = highStrategy.Initialize(new SandboxLimitsOptions
        {
            KillOnJobClose = true,
            PriorityMode = HostPriorityMode.HighPriority
        }, NullLogger.Instance);

        Assert.True(highSuccess);
        Assert.True(highStrategy.IsInitialized);
    }

    [Fact]
    public void LinuxCgroupV2Strategy_HandlesPriorityModesCleanly()
    {
        using LinuxCgroupV2Strategy bgStrategy = new();
        bgStrategy.Initialize(new SandboxLimitsOptions
        {
            PriorityMode = HostPriorityMode.Background
        }, NullLogger.Instance);

        using LinuxCgroupV2Strategy highStrategy = new();
        highStrategy.Initialize(new SandboxLimitsOptions
        {
            PriorityMode = HostPriorityMode.HighPriority
        }, NullLogger.Instance);

        if (!OperatingSystem.IsWindows())
        {
            Assert.True(bgStrategy.IsInitialized);
            Assert.True(highStrategy.IsInitialized);
        }
    }

    [Fact]
    public void ProcessTreeSandbox_DelegatesToProvidedStrategy()
    {
        TestProcessIsolationStrategy mockStrategy = new();
        SandboxLimitsOptions options = new() { KillOnJobClose = true };

        using (ProcessTreeSandbox sandbox = new(options, mockStrategy, NullLogger.Instance))
        {
            Assert.True(mockStrategy.InitializeCalled);
            Assert.True(sandbox.IsInitialized);

            using Process current = Process.GetCurrentProcess();
            bool assigned = sandbox.AssignProcess(current);
            Assert.True(assigned);
            Assert.True(mockStrategy.AssignProcessCalled);

            JobObjectSandboxMetrics? metrics = sandbox.GetSandboxMetrics();
            Assert.NotNull(metrics);
            Assert.True(mockStrategy.GetMetricsCalled);
        }

        Assert.True(mockStrategy.TerminateCalled);
        Assert.True(mockStrategy.DisposeCalled);
    }

    [Fact]
    public void ProcessTreeSandbox_DelegatesWithPinningAndPort()
    {
        TestProcessIsolationStrategy mockStrategy = new();
        SandboxLimitsOptions options = new() { KillOnJobClose = true };

        using ProcessTreeSandbox sandbox = new(options, mockStrategy, NullLogger.Instance);
        using Process current = Process.GetCurrentProcess();

        WorkerCpuPinning pinning = new(PrimaryPhysicalCore: 2, NumaNode: 0, AffinityMask: 4L, CpusSetString: "2", MemsSetString: "0");
        bool assigned = sandbox.AssignProcess(current, 11235, pinning);

        Assert.True(assigned);
        Assert.True(mockStrategy.AssignProcessCalled);
        Assert.Equal(11235, mockStrategy.LastPort);
        Assert.Same(pinning, mockStrategy.LastPinning);
    }

    [Fact]
    public void LinuxCgroupV2Strategy_AssignProcess_WithPinning_ExecutesSafely()
    {
        using LinuxCgroupV2Strategy strategy = new();
        strategy.Initialize(new SandboxLimitsOptions { PriorityMode = HostPriorityMode.HighPriority, KillOnJobClose = false }, NullLogger.Instance);

        using Process current = Process.GetCurrentProcess();
        WorkerCpuPinning pinning = new(PrimaryPhysicalCore: 0, NumaNode: 0, AffinityMask: 1L, CpusSetString: "0", MemsSetString: "0");
        bool assigned = strategy.AssignProcess(current, 11235, pinning);

        Assert.True(assigned);
    }

    [Fact]
    public void Win32JobObjectStrategy_AssignProcess_WithPinning_ExecutesSafely()
    {
        if (!OperatingSystem.IsWindows()) return;

        using Win32JobObjectStrategy strategy = new();
        strategy.Initialize(new SandboxLimitsOptions { PriorityMode = HostPriorityMode.HighPriority, KillOnJobClose = false }, NullLogger.Instance);

        using Process current = Process.GetCurrentProcess();
        nint originalAffinity = current.ProcessorAffinity;
        try
        {
            WorkerCpuPinning pinning = CpuTopologyResolver.GetDedicatedWorkerPinning(0, 1, HostPriorityMode.HighPriority);
            bool assigned = strategy.AssignProcess(current, 11235, pinning);
            Assert.True(assigned);
        }
        finally
        {
            try { current.ProcessorAffinity = originalAffinity; } catch { }
        }
    }

    [Fact]
    public void PrecisionTimerService_EnablesAndDisablesCleanly()
    {
        using PrecisionTimerService timer = new(NullLogger<PrecisionTimerService>.Instance);
        Assert.False(timer.IsActive);

        timer.Enable();
        Assert.True(timer.IsActive);

        timer.Disable();
        Assert.False(timer.IsActive);
    }

    private sealed class TestProcessIsolationStrategy : IProcessIsolationStrategy
    {
        public bool InitializeCalled { get; private set; }
        public bool AssignProcessCalled { get; private set; }
        public int LastPort { get; private set; }
        public WorkerCpuPinning? LastPinning { get; private set; }
        public bool GetMetricsCalled { get; private set; }
        public bool TerminateCalled { get; private set; }
        public bool DisposeCalled { get; private set; }

        public bool IsSupported => true;
        public bool IsInitialized => InitializeCalled;

        public bool Initialize(SandboxLimitsOptions options, Microsoft.Extensions.Logging.ILogger? logger)
        {
            InitializeCalled = true;
            return true;
        }

        public bool AssignProcess(Process process)
        {
            AssignProcessCalled = true;
            return true;
        }

        public bool AssignProcess(Process process, int port, WorkerCpuPinning? pinning = null)
        {
            AssignProcessCalled = true;
            LastPort = port;
            LastPinning = pinning;
            return true;
        }

        public bool AssignProcessHandle(nint hProcess, int processId = 0)
        {
            return true;
        }

        public JobObjectSandboxMetrics? GetSandboxMetrics(IReadOnlyDictionary<int, Process> trackedProcesses)
        {
            GetMetricsCalled = true;
            return new JobObjectSandboxMetrics(
                TimeSpan.FromSeconds(1),
                TimeSpan.FromSeconds(1),
                1, 1, 0, 100, 1024, 2048);
        }

        public void TerminateAll(IReadOnlyDictionary<int, Process> trackedProcesses)
        {
            TerminateCalled = true;
        }

        public void Dispose()
        {
            DisposeCalled = true;
        }
    }
}
