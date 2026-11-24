// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// TAO (Together As One) MCP & Kongor CLI -- Created by guccidonut (https://github.com/guccidonut/PKV)

using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Runtime.InteropServices;
using Kongor.Cli.Mcp.Handlers;

namespace Kongor.Cli.Mcp.Services;

/// <summary>
/// Domain service interface for client process lifecycle supervision, affinity, and execution.
/// </summary>
public interface IClientProcessService
{
    bool TryResolveExecutable(string? customPath, string? workingDir, [NotNullWhen(true)] out FileInfo? executable);
    Process? FindRunningProcess();
    bool IsRunning(int processId);
    bool TerminateProcess(int processId, bool force = true);
    Process? LaunchProcess(string executablePath, string arguments, string workingDirectory, bool headless, bool stealFocus, IDictionary<string, string>? environmentVariables = null);
    void ApplyProcessAffinity(Process process, long cpuAffinityMask);
}

/// <summary>
/// Service managing the execution lifecycle, process binding, and environment configuration of hon_x64.exe.
/// </summary>
public sealed class ClientProcessService : IClientProcessService
{
    private readonly IClientProcessSupervisor _supervisor;
    private readonly Action<string> _logger;

    public ClientProcessService(IClientProcessSupervisor? supervisor = null, Action<string>? logger = null)
    {
        _supervisor = supervisor ?? new ClientProcessSupervisor();
        _logger = logger ?? (msg => Console.Error.WriteLine($"[ClientProcessService] {msg}"));
    }

    public bool TryResolveExecutable(string? customPath, string? workingDir, [NotNullWhen(true)] out FileInfo? executable)
    {
        _logger($"Resolving client executable: customPath='{customPath ?? "none"}', workingDir='{workingDir ?? "none"}'");
        return _supervisor.TryResolveClientExecutable(customPath, workingDir, out executable);
    }

    public Process? FindRunningProcess()
    {
        return _supervisor.FindRunningClientProcess();
    }

    public bool IsRunning(int processId)
    {
        try
        {
            var p = Process.GetProcessById(processId);
            return !p.HasExited && _supervisor.IsClientProcess(p);
        }
        catch
        {
            return false;
        }
    }

    public bool TerminateProcess(int processId, bool force = true)
    {
        _logger($"Terminating client process PID {processId} (force={force})...");
        if (GameServerManageHandler.IsTestExecution)
        {
            _logger($"[TEST MODE] Suppressing TerminateProcess for PID {processId}.");
            return true;
        }

        try
        {
            var p = Process.GetProcessById(processId);
            if (p.HasExited) return true;

            if (_supervisor.IsUserClient(p))
            {
                _logger($"[SECURITY GUARD] Preserving PID {processId}: identified as user active gaming session.");
                return false;
            }

            _supervisor.StopOffscreenWindowWatchdog(processId);
            if (force)
            {
                p.Kill(true);
            }
            else
            {
                p.CloseMainWindow();
            }

            p.WaitForExit(2000);
            _logger($"Terminated process PID {processId}.");
            return true;
        }
        catch (Exception ex)
        {
            _logger($"[WARN] Could not terminate process PID {processId}: {ex.Message}");
            return false;
        }
    }

    public Process? LaunchProcess(
        string executablePath,
        string arguments,
        string workingDirectory,
        bool headless,
        bool stealFocus,
        IDictionary<string, string>? environmentVariables = null)
    {
        _logger($"Launching client process: '{executablePath}' {arguments} (headless={headless}, stealFocus={stealFocus})");
        return InteractiveProcessLauncher.StartInteractiveProcess(
            executablePath,
            arguments,
            workingDirectory,
            headless: headless,
            stealFocus: stealFocus,
            environmentVariables: environmentVariables);
    }

    public void ApplyProcessAffinity(Process process, long cpuAffinityMask)
    {
        if (OperatingSystem.IsWindows() && cpuAffinityMask > 0)
        {
            try
            {
                _logger($"Setting processor affinity for PID {process.Id} to mask 0x{cpuAffinityMask:X}");
                process.ProcessorAffinity = checked((IntPtr)cpuAffinityMask);
            }
            catch (Exception ex)
            {
                _logger($"[WARN] Failed to apply CPU affinity to PID {process.Id}: {ex.Message}");
            }
        }
    }
}
