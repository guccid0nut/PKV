// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

using System.Diagnostics;
using System.Runtime.InteropServices;

namespace NEXUS.ControlPanel.GUI.Services.Standalone;

public sealed class ChildProcessJob : IDisposable
{
    private const int JobObjectExtendedLimitInformation = 9;
    private const uint JobObjectLimitKillOnJobClose = 0x00002000;

    private readonly object _lock = new();
    private readonly List<(string Name, Process Process)> _tracked = [];
    private nint _jobHandle = nint.Zero;
    private bool _disposed;

    public ChildProcessJob()
    {
        if (OperatingSystem.IsWindows())
        {
            try
            {
                _jobHandle = CreateJobObject(nint.Zero, null);
                if (_jobHandle != nint.Zero)
                {
                    JOBOBJECT_EXTENDED_LIMIT_INFORMATION info = new();
                    info.BasicLimitInformation.LimitFlags = JobObjectLimitKillOnJobClose;
                    int length = Marshal.SizeOf<JOBOBJECT_EXTENDED_LIMIT_INFORMATION>();
                    bool success = SetInformationJobObject(
                        _jobHandle,
                        JobObjectExtendedLimitInformation,
                        ref info,
                        (uint)length);

                    if (!success)
                    {
                        int error = Marshal.GetLastWin32Error();
                        Console.Error.WriteLine($"[NexusStandalone] SetInformationJobObject failed with error {error}.");
                    }
                }
                else
                {
                    int error = Marshal.GetLastWin32Error();
                    Console.Error.WriteLine($"[NexusStandalone] CreateJobObject failed with error {error}.");
                }
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[NexusStandalone] Exception initializing Win32 Job Object: {ex.Message}");
            }
        }
    }

    public void Attach(string name, Process process)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(process);

        lock (_lock)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);

            if (OperatingSystem.IsWindows() && _jobHandle != nint.Zero)
            {
                try
                {
                    if (!process.HasExited)
                    {
                        bool assigned = AssignProcessToJobObject(_jobHandle, process.Handle);
                        if (!assigned)
                        {
                            int error = Marshal.GetLastWin32Error();
                            Console.Error.WriteLine($"[NexusStandalone] AssignProcessToJobObject failed for {name} (PID {process.Id}) with error {error}");
                        }
                        else
                        {
                            Console.WriteLine($"[NexusStandalone] Attached {name} (PID {process.Id}) to ChildProcessJob");
                        }
                    }
                }
                catch (Exception ex)
                {
                    Console.Error.WriteLine($"[NexusStandalone] Exception assigning {name} (PID {process.Id}) to Job Object: {ex.Message}");
                }
            }

            _tracked.Add((name, process));
        }
    }

    public async Task StopProcessAsync(string name, CancellationToken ct = default)
    {
        (string Name, Process Process)[] targets;
        lock (_lock)
        {
            targets = _tracked.Where(t => string.Equals(t.Name, name, StringComparison.OrdinalIgnoreCase)).ToArray();
        }

        foreach (var (procName, proc) in targets)
        {
            await TerminateProcessAsync(procName, proc, ct).ConfigureAwait(false);
            lock (_lock)
            {
                _tracked.Remove((procName, proc));
            }
        }
    }

    public async Task StopAllAsync(CancellationToken ct = default)
    {
        (string Name, Process Process)[] snapshot;
        lock (_lock)
        {
            snapshot = _tracked.ToArray();
            _tracked.Clear();
        }

        // Stops tracked processes in reverse order
        for (int i = snapshot.Length - 1; i >= 0; i--)
        {
            var (name, process) = snapshot[i];
            await TerminateProcessAsync(name, process, ct).ConfigureAwait(false);
        }
    }

    private static async Task TerminateProcessAsync(string name, Process process, CancellationToken ct)
    {
        try
        {
            if (process.HasExited)
            {
                Console.WriteLine($"[NexusStandalone] Process {name} (PID {process.Id}) already exited");
                return;
            }

            int pid = process.Id;
            if (OperatingSystem.IsWindows())
            {
                try
                {
                    process.Kill(entireProcessTree: true);
                    await process.WaitForExitAsync(ct).ConfigureAwait(false);
                    Console.WriteLine($"[NexusStandalone] Stopped {name} (PID {pid})");
                }
                catch (Exception ex)
                {
                    Console.Error.WriteLine($"[NexusStandalone] Kill failed for {name} (PID {pid}): {ex.Message}");
                }
            }
            else
            {
                // POSIX: try graceful SIGTERM, wait up to 10s, then SIGKILL
                try
                {
                    using Process? killer = Process.Start("/bin/kill", $"-TERM {pid}");
                    if (killer != null)
                    {
                        await killer.WaitForExitAsync(ct).ConfigureAwait(false);
                    }

                    using var timeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                    using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct, timeoutCts.Token);
                    try
                    {
                        await process.WaitForExitAsync(linkedCts.Token).ConfigureAwait(false);
                        Console.WriteLine($"[NexusStandalone] Gracefully stopped {name} (PID {pid}) via SIGTERM");
                        return;
                    }
                    catch (OperationCanceledException)
                    {
                        // Fall through to SIGKILL
                    }
                }
                catch (Exception ex)
                {
                    Console.Error.WriteLine($"[NexusStandalone] SIGTERM failed for {name} (PID {pid}): {ex.Message}");
                }

                try
                {
                    if (!process.HasExited)
                    {
                        process.Kill(entireProcessTree: true);
                        await process.WaitForExitAsync(ct).ConfigureAwait(false);
                        Console.WriteLine($"[NexusStandalone] Force killed {name} (PID {pid})");
                    }
                }
                catch (Exception ex)
                {
                    Console.Error.WriteLine($"[NexusStandalone] Force kill failed for {name} (PID {pid}): {ex.Message}");
                }
            }
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[NexusStandalone] Exception in TerminateProcessAsync for {name}: {ex.Message}");
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        (string Name, Process Process)[] remaining;
        lock (_lock)
        {
            remaining = _tracked.ToArray();
            _tracked.Clear();
        }

        for (int i = remaining.Length - 1; i >= 0; i--)
        {
            var (name, process) = remaining[i];
            try
            {
                if (!process.HasExited)
                {
                    process.Kill(entireProcessTree: true);
                    Console.WriteLine($"[NexusStandalone] Disposed and killed {name} (PID {process.Id})");
                }
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[NexusStandalone] Dispose kill failed for {name}: {ex.Message}");
            }
            finally
            {
                process.Dispose();
            }
        }

        if (OperatingSystem.IsWindows() && _jobHandle != nint.Zero)
        {
            try
            {
                if (!CloseHandle(_jobHandle))
                {
                    int win32Err = Marshal.GetLastWin32Error();
                    Console.Error.WriteLine($"[NexusStandalone] CloseHandle on Job Object returned false (Win32Error={win32Err})");
                }
                _jobHandle = nint.Zero;
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[NexusStandalone] Exception closing Job Object handle: {ex.Message}");
            }
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct IO_COUNTERS
    {
        public ulong ReadOperationCount;
        public ulong WriteOperationCount;
        public ulong OtherOperationCount;
        public ulong ReadTransferCount;
        public ulong WriteTransferCount;
        public ulong OtherTransferCount;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct JOBOBJECT_BASIC_LIMIT_INFORMATION
    {
        public long PerProcessUserTimeLimit;
        public long PerJobUserTimeLimit;
        public uint LimitFlags;
        public nuint MinimumWorkingSetSize;
        public nuint MaximumWorkingSetSize;
        public uint ActiveProcessLimit;
        public nuint Affinity;
        public uint PriorityClass;
        public uint SchedulingClass;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct JOBOBJECT_EXTENDED_LIMIT_INFORMATION
    {
        public JOBOBJECT_BASIC_LIMIT_INFORMATION BasicLimitInformation;
        public IO_COUNTERS IoInfo;
        public nuint ProcessMemoryLimit;
        public nuint JobMemoryLimit;
        public nuint PeakProcessMemoryLimit;
        public nuint PeakJobMemoryLimit;
    }

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern nint CreateJobObject(nint lpJobAttributes, string? lpName);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetInformationJobObject(
        nint hJob,
        int JobObjectInformationClass,
        ref JOBOBJECT_EXTENDED_LIMIT_INFORMATION lpJobObjectInfo,
        uint cbJobObjectInfoLength);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AssignProcessToJobObject(nint hJob, nint hProcess);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(nint hObject);
}
