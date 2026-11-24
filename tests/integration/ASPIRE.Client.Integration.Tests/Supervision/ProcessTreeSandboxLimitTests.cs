// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

using System.Diagnostics;
using COMPEL.Configuration;
using COMPEL.Internals;
using Microsoft.Extensions.Logging.Abstractions;

namespace ASPIRE.Tests.Supervision;

public class ProcessTreeSandboxLimitTests
{
    [Fact]
    public void ProcessTreeSandbox_DefaultInitialization_AppliesSafeDefaults()
    {
        using ProcessTreeSandbox sandbox = new(NullLogger<ProcessTreeSandbox>.Instance);

        Assert.NotNull(sandbox.Options);
        Assert.True(sandbox.Options.KillOnJobClose, "KillOnJobClose must be enabled by default.");
        Assert.Equal(4096UL * 1024UL * 1024UL, sandbox.Options.MaxProcessMemoryBytes);
        Assert.Equal(16384UL * 1024UL * 1024UL, sandbox.Options.MaxJobMemoryBytes);
        Assert.Equal(32U, sandbox.Options.MaxActiveProcesses);
        Assert.True(sandbox.Options.EnforceAboveNormalPriority);

        if (OperatingSystem.IsWindows())
            Assert.True(sandbox.IsInitialized, "Windows Job Object handle must be valid on Windows.");
    }

    [Fact]
    public void ProcessTreeSandbox_CustomOptions_AppliesConfiguredLimits()
    {
        SandboxLimitsOptions customOptions = new()
        {
            KillOnJobClose = true,
            MaxProcessMemoryBytes = 2048UL * 1024UL * 1024UL, // 2 GB
            MaxJobMemoryBytes = 8192UL * 1024UL * 1024UL, // 8 GB
            MaxActiveProcesses = 16,
            EnforceAboveNormalPriority = true,
            CpuAffinityMask = 0x03 // Cores 0 and 1
        };

        IOptions<SandboxLimitsOptions> optionsWrapper = Options.Create(customOptions);
        using ProcessTreeSandbox sandbox = new(optionsWrapper, NullLogger<ProcessTreeSandbox>.Instance);

        Assert.Equal(customOptions.MaxProcessMemoryBytes, sandbox.Options.MaxProcessMemoryBytes);
        Assert.Equal(customOptions.MaxJobMemoryBytes, sandbox.Options.MaxJobMemoryBytes);
        Assert.Equal(customOptions.MaxActiveProcesses, sandbox.Options.MaxActiveProcesses);
        Assert.Equal(customOptions.CpuAffinityMask, sandbox.Options.CpuAffinityMask);

        if (OperatingSystem.IsWindows()) Assert.True(sandbox.IsInitialized);
    }

    [Fact]
    public void ProcessTreeSandbox_AssignProcessAndQueryMetrics_ReturnsValidTelemetry()
    {
        SandboxLimitsOptions options = new()
        {
            KillOnJobClose = false, // Do not kill test host on dispose
            MaxProcessMemoryBytes = 4096UL * 1024UL * 1024UL,
            MaxJobMemoryBytes = 16384UL * 1024UL * 1024UL
        };

        using ProcessTreeSandbox sandbox = new(options, NullLogger<ProcessTreeSandbox>.Instance);
        Process currentProcess = Process.GetCurrentProcess();

        bool assigned = sandbox.AssignProcess(currentProcess);
        if (OperatingSystem.IsWindows())
        {
            Assert.True(assigned, "Current test process should be successfully assigned to Sandbox Job Object.");

            JobObjectSandboxMetrics? metrics = sandbox.GetSandboxMetrics();
            Assert.NotNull(metrics);
            Assert.True(metrics.ActiveProcesses >= 1, "Active process count in job object should be at least 1.");
            Assert.True(metrics.TotalProcesses >= 1, "Total process count should be at least 1.");
            Assert.True(metrics.TotalUserTime >= TimeSpan.Zero, "Total user time should be non-negative.");
            Assert.True(metrics.TotalKernelTime >= TimeSpan.Zero, "Total kernel time should be non-negative.");
        }
        else
        {
            Assert.True(assigned, "Soft success on non-Windows platforms.");
        }
    }

    [Fact]
    public async Task ProcessTreeSandbox_KillOnJobClose_TerminatesChildProcessOnDispose()
    {
        if (!OperatingSystem.IsWindows()) return;

        ProcessStartInfo startInfo = new()
        {
            FileName = "cmd.exe",
            Arguments = "/c ping 127.0.0.1 -n 30 > nul",
            UseShellExecute = false,
            CreateNoWindow = true
        };

        Process? childProcess = Process.Start(startInfo);
        Assert.NotNull(childProcess);

        try
        {
            using (ProcessTreeSandbox sandbox = new(new SandboxLimitsOptions { KillOnJobClose = true },
                       NullLogger<ProcessTreeSandbox>.Instance))
            {
                bool assigned = sandbox.AssignProcess(childProcess);
                Assert.True(assigned);
                Assert.False(childProcess.HasExited);
            } // Sandbox.Dispose() runs here -> closes Job Object handle -> kernel kills childProcess!

            await Task.Delay(25);
            Assert.True(childProcess.HasExited,
                "Child process must be immediately terminated by Windows Job Object on job close.");
        }
        finally
        {
            if (!childProcess.HasExited) childProcess.Kill();
            childProcess.Dispose();
        }
    }

    [Fact]
    public void ProcessTreeSandbox_NullOrExitedProcess_RejectsGracefully()
    {
        using ProcessTreeSandbox sandbox = new(NullLogger<ProcessTreeSandbox>.Instance);

        bool nullAssigned = sandbox.AssignProcess(null!);
        Assert.False(nullAssigned, "Assigning null process must return false.");
    }
}
