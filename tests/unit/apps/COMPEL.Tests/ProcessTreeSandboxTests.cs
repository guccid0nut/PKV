// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

namespace COMPEL.Tests;

/// <summary>
///     Verifies that the cross-platform ProcessTreeSandbox initializes cleanly,
///     accepts process assignments, and disposes without leaking OS handles.
/// </summary>
public sealed class ProcessTreeSandboxTests
{
    [Fact]
    public void ProcessTreeSandbox_Initializes_And_Disposes_Safely()
    {
        SandboxLimitsOptions options = new() { KillOnJobClose = false };
        using ProcessTreeSandbox sandbox = new(options);
        Process currentProcess = Process.GetCurrentProcess();

        bool assigned = sandbox.AssignProcess(currentProcess);
        if (OperatingSystem.IsWindows()) Assert.True(assigned);

        sandbox.Dispose();
    }

    [Fact]
    public async Task ProcessTreeSandbox_DisposedProcessAndUnassignProcess_NeverLeaksDeadPidsOrThrows()
    {
        SandboxLimitsOptions options = new() { KillOnJobClose = false };
        using ProcessTreeSandbox sandbox = new(options);

        ProcessStartInfo psi = new()
        {
            FileName = "dotnet",
            Arguments = "--version",
            CreateNoWindow = true,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };

        Process transientProc = Process.Start(psi)!;
        int transientPid = transientProc.Id;
        _ = sandbox.AssignProcess(transientProc);

        await transientProc.WaitForExitAsync();
        // Dispose the Process instance so subsequent .Id / .HasExited access throws InvalidOperationException
        transientProc.Dispose();

        JobObjectSandboxMetrics? metrics = sandbox.GetSandboxMetrics();
        _ = metrics;
        Assert.Equal(0, sandbox.TrackedProcessCount);
        Assert.False(sandbox.UnassignProcess(transientPid));

        using Process selfRef = Process.GetProcessById(Environment.ProcessId);
        if (sandbox.AssignProcess(selfRef))
        {
            Assert.Equal(1, sandbox.TrackedProcessCount);
            Assert.True(sandbox.UnassignProcess(selfRef.Id));
            Assert.Equal(0, sandbox.TrackedProcessCount);
        }
    }
}
