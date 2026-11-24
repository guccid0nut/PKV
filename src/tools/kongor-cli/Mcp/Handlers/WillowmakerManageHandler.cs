// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// TAO (Together As One) MCP & Kongor CLI -- Created by guccidonut (https://github.com/guccidonut/PKV)

using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Text.Json;
using Kongor.Cli.Mcp.Protocol;

namespace Kongor.Cli.Mcp.Handlers;

/// <summary>
/// MCP Tool Handler for 'kongor_willowmaker_manage' (alias: 'kongor_willowmaker_manager').
/// Controls launching, stopping, and status querying of the desktop launcher (WILLOWMAKER.exe).
/// Explicitly resolves games/client/WILLOWMAKER.exe or src/apps/willowmaker/bin/Release/net11.0/WILLOWMAKER.exe.
/// Manages launcher process lifecycle and window state.
/// </summary>
public sealed class WillowmakerManageHandler
{
    private readonly Lock _lock = new();
    private Process? _trackedProcess;
    private string? _lastExecutable;
    private string? _lastWorkingDirectory;
    private readonly TimeProvider _timeProvider;
    private readonly Action<string> _logger;

    public WillowmakerManageHandler(TimeProvider? timeProvider = null, Action<string>? logger = null)
    {
        _timeProvider = timeProvider ?? TimeProvider.System;
        _logger = logger ?? (msg => Console.Error.WriteLine($"[WillowmakerManage] {msg}"));
    }

    public async Task<ToolCallResult> HandleAsync(JsonElement arguments, CancellationToken cancellationToken = default)
    {
        string action = "status";
        if (arguments.TryGetProperty("action", out JsonElement actionElem) && !string.IsNullOrWhiteSpace(actionElem.GetString()))
        {
            action = actionElem.GetString()!.Trim().ToLowerInvariant();
        }

        string? customPath = arguments.TryGetProperty("customPath", out JsonElement customElem) && !string.IsNullOrWhiteSpace(customElem.GetString())
            ? customElem.GetString()!.Trim()
            : null;

        string? workingDirectory = arguments.TryGetProperty("workingDirectory", out JsonElement wdElem) && !string.IsNullOrWhiteSpace(wdElem.GetString())
            ? wdElem.GetString()!.Trim()
            : null;

        _logger($"Received action '{action}' (CustomPath: '{customPath ?? "none"}', WorkingDirectory: '{workingDirectory ?? "none"}').");

        try
        {
            switch (action)
            {
                case "launch":
                {
                    return await LaunchAsync(customPath, workingDirectory, cancellationToken).ConfigureAwait(false);
                }

                case "stop":
                {
                    return Stop();
                }

                case "status":
                default:
                {
                    return GetStatus();
                }
            }
        }
        catch (Exception ex)
        {
            _logger($"[ERROR] Exception executing willowmaker manage action '{action}': {ex}");
            return ToolCallResult.Error($"Failed executing willowmaker manage action '{action}': {ex.Message}");
        }
    }

    private async Task<ToolCallResult> LaunchAsync(string? customPath, string? customWorkingDirectory, CancellationToken cancellationToken)
    {
        // 1. Check if already running
        Process? existing = FindRunningProcess();
        if (existing != null)
        {
            string hwnd = $"0x{existing.MainWindowHandle.ToInt64():X8}";
            _logger($"WILLOWMAKER is already running with PID {existing.Id}, MainWindowHandle: {hwnd}.");
            return ToolCallResult.Json(new
            {
                status = "success",
                action = "launch",
                running = true,
                alreadyRunning = true,
                processId = existing.Id,
                mainWindowHandle = hwnd,
                executable = TryGetProcessFileName(existing) ?? _lastExecutable,
                workingDirectory = _lastWorkingDirectory,
                message = $"WILLOWMAKER launcher is already active with PID {existing.Id}.",
                timestamp = _timeProvider.GetUtcNow().ToString("o")
            });
        }

        // 2. Resolve WILLOWMAKER executable
        _logger("Resolving WILLOWMAKER launcher executable...");
        if (!TryResolveWillowmakerExecutable(customPath, customWorkingDirectory, out FileInfo? executable))
        {
            string err = $"Unable to resolve WILLOWMAKER executable. Checked 'games/client/WILLOWMAKER.exe', 'src/apps/willowmaker/bin/Release/net11.0/WILLOWMAKER.exe', and custom path '{customPath ?? "none"}'.";
            _logger($"[ERROR] {err}");
            return ToolCallResult.Json(new
            {
                status = "error",
                action = "launch",
                running = false,
                errorMessage = err,
                timestamp = _timeProvider.GetUtcNow().ToString("o")
            });
        }

        string repoRootForLaunch = FindRepoRoot(customWorkingDirectory ?? Environment.CurrentDirectory);
        string clientDirForLaunch = Path.Combine(repoRootForLaunch, "games", "client");
        string workDir = customWorkingDirectory ?? (Directory.Exists(clientDirForLaunch) ? clientDirForLaunch : executable.DirectoryName ?? Environment.CurrentDirectory);
        _logger($"Resolved WILLOWMAKER launcher: '{executable.FullName}', working directory: '{workDir}'.");

        ProcessStartInfo startInfo = new()
        {
            FileName = executable.FullName,
            WorkingDirectory = workDir,
            UseShellExecute = true,
            CreateNoWindow = false
        };

        Process process = new() { StartInfo = startInfo, EnableRaisingEvents = true };

        bool started;
        try
        {
            started = process.Start();
        }
        catch (Exception ex)
        {
            string err = $"Failed to start WILLOWMAKER process '{executable.FullName}': {ex.Message}";
            _logger($"[ERROR] {err}");
            return ToolCallResult.Json(new
            {
                status = "error",
                action = "launch",
                running = false,
                executable = executable.FullName,
                workingDirectory = workDir,
                errorMessage = err,
                timestamp = _timeProvider.GetUtcNow().ToString("o")
            });
        }

        if (!started)
        {
            string err = $"Process.Start returned false for '{executable.FullName}'.";
            _logger($"[ERROR] {err}");
            return ToolCallResult.Json(new
            {
                status = "error",
                action = "launch",
                running = false,
                executable = executable.FullName,
                workingDirectory = workDir,
                errorMessage = err,
                timestamp = _timeProvider.GetUtcNow().ToString("o")
            });
        }

        lock (_lock)
        {
            _trackedProcess = process;
            _lastExecutable = executable.FullName;
            _lastWorkingDirectory = workDir;
        }

        // Wait up to 3 seconds for MainWindowHandle
        int waitMs = 0;
        const int maxWait = 3000;
        while (!process.HasExited && process.MainWindowHandle == IntPtr.Zero && waitMs < maxWait)
        {
            await Task.Delay(100, cancellationToken).ConfigureAwait(false);
            waitMs += 100;
            try { process.Refresh(); } catch { }
        }

        int pid = 0;
        string hwndStr = "0x00000000";
        bool isRunning = false;

        try
        {
            if (!process.HasExited)
            {
                pid = process.Id;
                hwndStr = $"0x{process.MainWindowHandle.ToInt64():X8}";
                isRunning = true;
                _logger($"WILLOWMAKER launcher launched successfully! PID: {pid}, MainWindowHandle: {hwndStr}");
            }
            else
            {
                _logger($"WILLOWMAKER process exited prematurely with exit code: {process.ExitCode}");
            }
        }
        catch (Exception ex)
        {
            _logger($"[WARN] Could not retrieve process state: {ex.Message}");
        }

        return ToolCallResult.Json(new
        {
            status = isRunning ? "success" : "exited",
            action = "launch",
            running = isRunning,
            processId = pid,
            mainWindowHandle = hwndStr,
            executable = executable.FullName,
            workingDirectory = workDir,
            timestamp = _timeProvider.GetUtcNow().ToString("o")
        });
    }

    private ToolCallResult Stop()
    {
        List<int> killedIds = [];

        if (GameServerManageHandler.IsTestExecution)
        {
            _logger("[TEST MODE] Suppressing Willowmaker Stop process kills to preserve environment.");
            return ToolCallResult.Json(new
            {
                status = "success",
                action = "stop",
                running = false,
                terminatedProcessesCount = 0,
                terminatedProcessIds = killedIds,
                message = "Test mode: suppressed process termination.",
                timestamp = _timeProvider.GetUtcNow().ToString("o")
            });
        }

        // 1. Kill tracked process
        lock (_lock)
        {
            if (_trackedProcess != null)
            {
                try
                {
                    if (!_trackedProcess.HasExited)
                    {
                        int pid = _trackedProcess.Id;
                        _trackedProcess.Kill(true);
                        _trackedProcess.WaitForExit(2000);
                        killedIds.Add(pid);
                        _logger($"Terminated tracked WILLOWMAKER process PID {pid}.");
                    }
                }
                catch (Exception ex)
                {
                    _logger($"[WARN] Error terminating tracked WILLOWMAKER process: {ex.Message}");
                }
                finally
                {
                    _trackedProcess = null;
                }
            }
        }

        // 2. Discover and terminate any other WILLOWMAKER processes
        try
        {
            Process[] procs = Process.GetProcessesByName("WILLOWMAKER");
            foreach (Process p in procs)
            {
                try
                {
                    if (p.Id != Environment.ProcessId && !killedIds.Contains(p.Id))
                    {
                        int pid = p.Id;
                        p.Kill(true);
                        p.WaitForExit(1500);
                        killedIds.Add(pid);
                        _logger($"Terminated WILLOWMAKER process PID {pid}.");
                    }
                }
                catch (Exception killEx)
                {
                    _logger($"[WARN] Error terminating WILLOWMAKER process: {killEx.Message}");
                }
            }
        }
        catch (Exception enumEx)
        {
            _logger($"[WARN] Error enumerating WILLOWMAKER processes: {enumEx.Message}");
        }

        return ToolCallResult.Json(new
        {
            status = "success",
            action = "stop",
            running = false,
            terminatedProcessesCount = killedIds.Count,
            terminatedProcessIds = killedIds,
            message = killedIds.Count > 0
                ? $"Successfully stopped {killedIds.Count} WILLOWMAKER process(es) (PIDs: {string.Join(", ", killedIds)})."
                : "No active WILLOWMAKER launcher processes were running.",
            timestamp = _timeProvider.GetUtcNow().ToString("o")
        });
    }

    private ToolCallResult GetStatus()
    {
        Process? proc = null;
        lock (_lock)
        {
            if (_trackedProcess != null && !_trackedProcess.HasExited)
            {
                proc = _trackedProcess;
            }
        }

        if (proc == null)
        {
            proc = FindRunningProcess();
        }

        if (proc != null)
        {
            try
            {
                proc.Refresh();
                if (!proc.HasExited)
                {
                    string hwnd = $"0x{proc.MainWindowHandle.ToInt64():X8}";
                    long workingSet = proc.WorkingSet64;
                    double workingSetMb = Math.Round(workingSet / (1024.0 * 1024.0), 2);
                    string? exePath = TryGetProcessFileName(proc) ?? _lastExecutable;

                    _logger($"WILLOWMAKER is active: PID {proc.Id}, MainWindowHandle: {hwnd}, WorkingSet: {workingSetMb} MB");

                    return ToolCallResult.Json(new
                    {
                        status = "success",
                        action = "status",
                        running = true,
                        processId = proc.Id,
                        mainWindowHandle = hwnd,
                        executable = exePath,
                        workingDirectory = _lastWorkingDirectory,
                        workingSetBytes = workingSet,
                        workingSetMb,
                        responding = proc.Responding,
                        timestamp = _timeProvider.GetUtcNow().ToString("o")
                    });
                }
            }
            catch (Exception ex)
            {
                _logger($"[WARN] Error querying WILLOWMAKER status: {ex.Message}");
            }
        }

        _logger("WILLOWMAKER launcher is not running.");
        return ToolCallResult.Json(new
        {
            status = "success",
            action = "status",
            running = false,
            processId = 0,
            mainWindowHandle = "0x00000000",
            executable = _lastExecutable,
            workingDirectory = _lastWorkingDirectory,
            workingSetBytes = (long?)null,
            workingSetMb = (double?)null,
            responding = false,
            timestamp = _timeProvider.GetUtcNow().ToString("o")
        });
    }

    /// <summary>
    /// Explicitly resolves games/client/WILLOWMAKER.exe or src/apps/willowmaker/bin/Release/net11.0/WILLOWMAKER.exe.
    /// </summary>
    public static bool TryResolveWillowmakerExecutable(string? customPath, string? workingDir, [NotNullWhen(true)] out FileInfo? executable)
    {
        executable = null;

        // 1. Explicit Custom Path
        if (!string.IsNullOrWhiteSpace(customPath))
        {
            string expanded = Environment.ExpandEnvironmentVariables(customPath);
            FileInfo customFi = new(expanded);
            if (customFi.Exists)
            {
                executable = customFi;
                return true;
            }
            if (File.Exists(Path.GetFullPath(expanded)))
            {
                executable = new FileInfo(Path.GetFullPath(expanded));
                return true;
            }
        }

        string baseDir = workingDir ?? Environment.CurrentDirectory;
        string repoRoot = FindRepoRoot(baseDir);

        string[] candidatePaths =
        [
            Path.Combine(repoRoot, "src", "apps", "willowmaker", "bin", "Release", "net11.0", "WILLOWMAKER.exe"),
            Path.Combine(repoRoot, "src", "apps", "willowmaker", "bin", "Debug", "net11.0", "WILLOWMAKER.exe"),
            Path.Combine(repoRoot, "src", "apps", "willowmaker", "bin", "Release", "net10.0", "WILLOWMAKER.exe"),
            Path.Combine(repoRoot, "src", "apps", "willowmaker", "bin", "Debug", "net10.0", "WILLOWMAKER.exe"),
            Path.Combine(repoRoot, "games", "client", "WILLOWMAKER.exe"),
            Path.Combine(baseDir, "WILLOWMAKER.exe"),
            Path.Combine(AppContext.BaseDirectory, "WILLOWMAKER.exe")
        ];

        foreach (string path in candidatePaths)
        {
            if (File.Exists(path))
            {
                executable = new FileInfo(path);
                return true;
            }
        }

        return false;
    }

    private static Process? FindRunningProcess()
    {
        try
        {
            Process[] procs = Process.GetProcessesByName("WILLOWMAKER");
            Process? match = procs.FirstOrDefault(p => p.Id != Environment.ProcessId && !p.HasExited);
            if (match != null) return match;
        }
        catch { }

        return null;
    }

    private static string? TryGetProcessFileName(Process process)
    {
        try
        {
            return process.MainModule?.FileName;
        }
        catch
        {
            return null;
        }
    }

    private static string FindRepoRoot(string startingDir)
    {
        if (Kongor.Core.Configuration.KongorWorkspaceResolver.TryResolve(
                out var ws,
                new Kongor.Core.Configuration.WorkspaceResolveOptions { InvocationCwd = startingDir }) && ws is not null)
        {
            return ws.Root;
        }

        return startingDir;
    }
}
