// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// TAO (Together As One) MCP & Kongor CLI -- Created by guccidonut (https://github.com/guccidonut/PKV)

using System.Diagnostics;
using System.Text.Json;
using Kongor.Core.Configuration;

namespace Kongor.Cli.Infrastructure;

internal sealed record ProcessSnapshot(
    int ProcessId,
    string ProcessName,
    string? ExecutablePath,
    string? CommandLine,
    bool AccessDenied = false);

internal sealed record StopProcessFilterOptions
{
    public required int CurrentProcessId { get; init; }
    public int? ParentShimProcessId { get; init; }
    public string? ActiveWorkspaceRoot { get; init; }
    public IReadOnlyList<string> RegisteredWorkspaceRoots { get; init; } = [];
    public bool GlobalScope { get; init; }
    public bool IncludeMcp { get; init; }
}

internal sealed record StopProcessTarget(ProcessSnapshot Process, string Reason);
internal sealed record SkippedProcessNotice(ProcessSnapshot Process, string Reason);

internal sealed record StopFilterDecision(
    IReadOnlyList<StopProcessTarget> Targets,
    IReadOnlyList<SkippedProcessNotice> Skipped);

/// <summary>
/// Deep module that strictly filters running processes to Project Kongor-owned processes,
/// preventing <c>kongor stop</c> from terminating unrelated <c>dotnet</c> processes, IDEs,
/// other worktrees (unless <c>--global</c> is specified), or active MCP stdio servers.
/// </summary>
internal static class KongorProcessOwnershipFilter
{
    private static readonly string[] DedicatedKongorProcessPrefixes =
    [
        "KONGOR.MasterServer",
        "TRANSMUTANSTEIN.ChatServer",
        "ZORGATH.WebPortal.API",
        "DAWNBRINGER.WebPortal.UI",
        "KONGOR.EdgeGateway",
        "ASPIRE.ApplicationHost",
        "NEXUS.ControlPanel.GUI",
        "WILLOWMAKER",
        "COMPEL",
        "ReplayRenderer.Service",
        "ModCli",
        "prebake-tool",
        "EndpointAuditor",
        "LuaVerifier",
        "hon_x64",
        "hon-x86_64",
        "HoN64",
        "kongor-render"
    ];

    private static readonly string[] SharedHostProcessNames =
    [
        "dotnet",
        "testhost",
        "testhost.x86",
        "vstest.console",
        "VBCSCompiler",
        "dcp",
        "dcpctrl",
        "dcp-host",
        "powershell",
        "pwsh"
    ];

    private static readonly string[] KongorCommandLineMarkers =
    [
        "KONGOR.MasterServer",
        "TRANSMUTANSTEIN.ChatServer",
        "ZORGATH.WebPortal.API",
        "DAWNBRINGER.WebPortal.UI",
        "KONGOR.EdgeGateway",
        "ASPIRE.ApplicationHost",
        "NEXUS.ControlPanel",
        "WILLOWMAKER",
        "COMPEL",
        "ReplayRenderer.Service",
        "Kongor.Core",
        "Kongor.Cli",
        "Kongor.Modding",
        "ProjectKongor",
        "MERRICK.DatabaseContext",
        "scratch\\run_",
        "scratch/run_",
        "run_master_server",
        "run_chat_server"
    ];

    private static readonly string[] ShadowRuntimeMarkers =
    [
        "kongor_app_runtime",
        "ProjectKongor\\runtime-cache",
        "ProjectKongor/runtime-cache"
    ];

    public static StopFilterDecision SelectTargets(
        IReadOnlyList<ProcessSnapshot> snapshots,
        StopProcessFilterOptions options)
    {
        List<StopProcessTarget> targets = [];
        List<SkippedProcessNotice> skipped = [];

        string? normalizedActiveRoot = KongorUserConfigStore.TryNormalizePath(options.ActiveWorkspaceRoot);
        List<string> otherRegisteredRoots = options.RegisteredWorkspaceRoots
            .Select(KongorUserConfigStore.TryNormalizePath)
            .Where(p => !string.IsNullOrWhiteSpace(p) &&
                        (normalizedActiveRoot == null ||
                         !string.Equals(p, normalizedActiveRoot, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal)))
            .Cast<string>()
            .ToList();

        foreach (ProcessSnapshot proc in snapshots)
        {
            if (proc.ProcessId <= 0 ||
                proc.ProcessId == options.CurrentProcessId ||
                (options.ParentShimProcessId.HasValue && proc.ProcessId == options.ParentShimProcessId.Value))
            {
                continue;
            }

            if (proc.AccessDenied)
            {
                skipped.Add(new SkippedProcessNotice(proc, "access-denied (elevated or protected process)"));
                continue;
            }

            if (!options.IncludeMcp && IsMcpServerProcess(proc))
            {
                skipped.Add(new SkippedProcessNotice(proc, "spared active Kongor MCP server (pass --include-mcp to stop)"));
                continue;
            }

            bool isDedicatedName = MatchesDedicatedKongorProcess(proc.ProcessName);
            bool isSharedHost = MatchesSharedHostProcess(proc.ProcessName);

            if (!isDedicatedName && !isSharedHost)
            {
                continue;
            }

            string combinedIdentity = $"{proc.ExecutablePath ?? string.Empty} {proc.CommandLine ?? string.Empty}";
            bool referencesActiveRoot = !string.IsNullOrWhiteSpace(normalizedActiveRoot) &&
                combinedIdentity.Contains(normalizedActiveRoot, StringComparison.OrdinalIgnoreCase);
            bool referencesOtherRegisteredRoot = otherRegisteredRoots.Any(r =>
                combinedIdentity.Contains(r, StringComparison.OrdinalIgnoreCase));
            bool referencesShadowRuntime = ShadowRuntimeMarkers.Any(m =>
                combinedIdentity.Contains(m, StringComparison.OrdinalIgnoreCase));
            bool referencesKongorMarker = KongorCommandLineMarkers.Any(m =>
                combinedIdentity.Contains(m, StringComparison.OrdinalIgnoreCase));

            if (isDedicatedName)
            {
                if (!options.GlobalScope && !string.IsNullOrWhiteSpace(normalizedActiveRoot))
                {
                    // If the binary path clearly belongs to another Kongor checkout/bundle, respect workspace scoping.
                    if (referencesOtherRegisteredRoot && !referencesActiveRoot)
                    {
                        skipped.Add(new SkippedProcessNotice(proc, "belongs to another Kongor workspace (pass --global to stop all workspaces)"));
                        continue;
                    }

                    // If we have an executable path and it is neither inside active root nor shadow cache, check whether it looks like another workspace.
                    if (!string.IsNullOrWhiteSpace(proc.ExecutablePath) &&
                        !referencesActiveRoot &&
                        !referencesShadowRuntime &&
                        !KongorWorkspaceResolver.IsPathInsideDirectory(proc.ExecutablePath, normalizedActiveRoot))
                    {
                        skipped.Add(new SkippedProcessNotice(proc, $"outside active workspace '{normalizedActiveRoot}' (pass --global to stop)"));
                        continue;
                    }
                }

                string reason = referencesActiveRoot
                    ? "dedicated Kongor binary in active workspace"
                    : referencesShadowRuntime
                        ? "dedicated Kongor shadow-runtime binary"
                        : "dedicated Kongor binary";
                targets.Add(new StopProcessTarget(proc, reason));
                continue;
            }

            // Shared host processes (dotnet, testhost, vstest.console, powershell, dcp*):
            // NEVER kill unless command line / path explicitly proves ProjectKongor ownership!
            if (string.IsNullOrWhiteSpace(proc.CommandLine) && string.IsNullOrWhiteSpace(proc.ExecutablePath))
            {
                skipped.Add(new SkippedProcessNotice(proc, "shared host with unreadable command line (spared for safety)"));
                continue;
            }

            bool isKongorOwnedHost = referencesActiveRoot || referencesShadowRuntime || referencesKongorMarker ||
                (options.GlobalScope && referencesOtherRegisteredRoot);

            if (!isKongorOwnedHost)
            {
                if (proc.ProcessName.Equals("dotnet", StringComparison.OrdinalIgnoreCase))
                {
                    skipped.Add(new SkippedProcessNotice(proc, "unrelated dotnet host (spared)"));
                }
                continue;
            }

            // For PowerShell supervisors, require an explicit supervisor/test script marker so we don't kill the user's own terminal shell!
            if (proc.ProcessName.Equals("powershell", StringComparison.OrdinalIgnoreCase) ||
                proc.ProcessName.Equals("pwsh", StringComparison.OrdinalIgnoreCase))
            {
                bool isSupervisorScript = !string.IsNullOrWhiteSpace(proc.CommandLine) &&
                    (proc.CommandLine.Contains("scratch\\run_", StringComparison.OrdinalIgnoreCase) ||
                     proc.CommandLine.Contains("scratch/run_", StringComparison.OrdinalIgnoreCase) ||
                     proc.CommandLine.Contains("run_master_server", StringComparison.OrdinalIgnoreCase) ||
                     proc.CommandLine.Contains("run_chat_server", StringComparison.OrdinalIgnoreCase));
                if (!isSupervisorScript)
                {
                    continue;
                }
            }

            if (!options.GlobalScope && !string.IsNullOrWhiteSpace(normalizedActiveRoot) &&
                referencesOtherRegisteredRoot && !referencesActiveRoot)
            {
                skipped.Add(new SkippedProcessNotice(proc, "Kongor dotnet host in another workspace (pass --global to stop)"));
                continue;
            }

            targets.Add(new StopProcessTarget(proc,
                referencesActiveRoot ? "Kongor host in active workspace" : "Kongor-owned host process"));
        }

        return new StopFilterDecision(targets, skipped);
    }

    /// <summary>
    /// Captures a single batched snapshot of host processes with executable paths and command lines.
    /// Runs ONE query on Windows (never spawning PowerShell per PID).
    /// </summary>
    public static IReadOnlyList<ProcessSnapshot> CaptureLiveProcessSnapshots(Action<string>? logger = null)
    {
        if (OperatingSystem.IsWindows())
        {
            return CaptureWindowsSnapshots(logger);
        }

        if (OperatingSystem.IsLinux())
        {
            return CaptureLinuxSnapshots(logger);
        }

        if (OperatingSystem.IsMacOS())
        {
            return CaptureMacOsSnapshots(logger);
        }

        return CaptureFallbackSnapshots(logger);
    }

    private static bool MatchesDedicatedKongorProcess(string processName)
    {
        string normalized = StripExeSuffix(processName);
        return DedicatedKongorProcessPrefixes.Any(p =>
            normalized.Equals(p, StringComparison.OrdinalIgnoreCase) ||
            normalized.StartsWith(p, StringComparison.OrdinalIgnoreCase));
    }

    private static bool MatchesSharedHostProcess(string processName)
    {
        string normalized = StripExeSuffix(processName);
        return SharedHostProcessNames.Any(p =>
            normalized.Equals(p, StringComparison.OrdinalIgnoreCase));
    }

    private static bool IsMcpServerProcess(ProcessSnapshot proc)
    {
        string cmd = proc.CommandLine ?? string.Empty;
        if (cmd.Contains("tao_mcp.py", StringComparison.OrdinalIgnoreCase) ||
            cmd.Contains("kongor_mcp_runtime", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        string name = StripExeSuffix(proc.ProcessName);
        if ((name.Equals("kongor", StringComparison.OrdinalIgnoreCase) ||
             name.Equals("dotnet", StringComparison.OrdinalIgnoreCase)) &&
            (cmd.Contains(" mcp", StringComparison.OrdinalIgnoreCase) ||
             cmd.EndsWith("mcp", StringComparison.OrdinalIgnoreCase)))
        {
            return true;
        }

        return false;
    }

    private static string StripExeSuffix(string name)
        => name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? name[..^4] : name;

    private static IReadOnlyList<ProcessSnapshot> CaptureWindowsSnapshots(Action<string>? logger)
    {
        List<ProcessSnapshot> snapshots = [];
        try
        {
            const string script =
                "$procs = Get-CimInstance Win32_Process -ErrorAction SilentlyContinue | " +
                "Select-Object ProcessId, Name, ExecutablePath, CommandLine; " +
                "$procs | ConvertTo-Json -Compress";

            ProcessStartInfo psi = new("powershell.exe")
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };
            psi.ArgumentList.Add("-NoProfile");
            psi.ArgumentList.Add("-NonInteractive");
            psi.ArgumentList.Add("-Command");
            psi.ArgumentList.Add(script);

            using Process? proc = Process.Start(psi);
            if (proc is null)
            {
                logger?.Invoke("[StopFilter] Failed to spawn PowerShell CIM snapshot query; falling back to Process.GetProcesses().");
                return CaptureFallbackSnapshots(logger);
            }

            string stdout = proc.StandardOutput.ReadToEnd();
            proc.WaitForExit(6000);

            if (!string.IsNullOrWhiteSpace(stdout))
            {
                using JsonDocument doc = JsonDocument.Parse(stdout);
                IEnumerable<JsonElement> elements = doc.RootElement.ValueKind == JsonValueKind.Array
                    ? doc.RootElement.EnumerateArray()
                    : [doc.RootElement];

                foreach (JsonElement el in elements)
                {
                    int pid = el.TryGetProperty("ProcessId", out JsonElement pidEl) && pidEl.ValueKind == JsonValueKind.Number
                        ? pidEl.GetInt32()
                        : 0;
                    string name = el.TryGetProperty("Name", out JsonElement nameEl) && nameEl.ValueKind == JsonValueKind.String
                        ? nameEl.GetString() ?? string.Empty
                        : string.Empty;
                    string? exe = el.TryGetProperty("ExecutablePath", out JsonElement exeEl) && exeEl.ValueKind == JsonValueKind.String
                        ? exeEl.GetString()
                        : null;
                    string? cmd = el.TryGetProperty("CommandLine", out JsonElement cmdEl) && cmdEl.ValueKind == JsonValueKind.String
                        ? cmdEl.GetString()
                        : null;

                    if (pid > 0 && !string.IsNullOrWhiteSpace(name))
                    {
                        snapshots.Add(new ProcessSnapshot(pid, StripExeSuffix(name), exe, cmd, AccessDenied: false));
                    }
                }

                return snapshots;
            }
        }
        catch (Exception ex)
        {
            logger?.Invoke($"[StopFilter] Warning: Batched Win32_Process CIM query failed ({ex.Message}); falling back to Process.GetProcesses().");
        }

        return CaptureFallbackSnapshots(logger);
    }

    private static IReadOnlyList<ProcessSnapshot> CaptureLinuxSnapshots(Action<string>? logger)
    {
        List<ProcessSnapshot> snapshots = [];
        foreach (Process proc in Process.GetProcesses())
        {
            try
            {
                int pid = proc.Id;
                string name = proc.ProcessName;
                string? cmd = null;
                string? exe = null;
                bool accessDenied = false;

                try
                {
                    string cmdPath = $"/proc/{pid}/cmdline";
                    if (File.Exists(cmdPath))
                    {
                        cmd = File.ReadAllText(cmdPath).Replace('\0', ' ').Trim();
                    }
                }
                catch (UnauthorizedAccessException)
                {
                    accessDenied = true;
                }
                catch (IOException)
                {
                }

                try
                {
                    exe = proc.MainModule?.FileName;
                }
                catch (Exception ex)
                {
                    logger?.Invoke($"[StopFilter] Notice: Could not read MainModule for PID {pid} ({name}): {ex.Message}");
                }

                snapshots.Add(new ProcessSnapshot(pid, name, exe, cmd, accessDenied));
            }
            catch (Exception ex)
            {
                logger?.Invoke($"[StopFilter] Notice: Skipping inaccessible process: {ex.Message}");
            }
            finally
            {
                proc.Dispose();
            }
        }

        return snapshots;
    }

    private static IReadOnlyList<ProcessSnapshot> CaptureMacOsSnapshots(Action<string>? logger)
    {
        try
        {
            ProcessStartInfo psi = new("/bin/ps")
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };
            psi.ArgumentList.Add("-ax");
            psi.ArgumentList.Add("-o");
            psi.ArgumentList.Add("pid=,comm=,args=");

            using Process? proc = Process.Start(psi);
            if (proc is not null)
            {
                string stdout = proc.StandardOutput.ReadToEnd();
                proc.WaitForExit(5000);
                if (!string.IsNullOrWhiteSpace(stdout))
                {
                    List<ProcessSnapshot> snapshots = [];
                    foreach (string rawLine in stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries))
                    {
                        string line = rawLine.Trim();
                        if (string.IsNullOrEmpty(line)) continue;

                        int firstSpace = line.IndexOf(' ');
                        if (firstSpace <= 0 || !int.TryParse(line[..firstSpace], out int pid) || pid <= 0) continue;

                        string rest = line[(firstSpace + 1)..].TrimStart();
                        int secondSpace = rest.IndexOf(' ');
                        string comm = secondSpace > 0 ? rest[..secondSpace] : rest;
                        string cmdLine = secondSpace > 0 ? rest[(secondSpace + 1)..].Trim() : comm;
                        string name = Path.GetFileName(comm);

                        snapshots.Add(new ProcessSnapshot(pid, StripExeSuffix(name), comm.StartsWith('/') ? comm : null, cmdLine, AccessDenied: false));
                    }

                    if (snapshots.Count > 0)
                    {
                        return snapshots;
                    }
                }
            }
        }
        catch (Exception ex)
        {
            logger?.Invoke($"[StopFilter] Warning: macOS /bin/ps snapshot query failed ({ex.Message}); falling back to Process.GetProcesses().");
        }

        return CaptureFallbackSnapshots(logger);
    }

    private static IReadOnlyList<ProcessSnapshot> CaptureFallbackSnapshots(Action<string>? logger)
    {
        List<ProcessSnapshot> snapshots = [];
        foreach (Process proc in Process.GetProcesses())
        {
            try
            {
                string? exe = null;
                bool accessDenied = false;
                try
                {
                    exe = proc.MainModule?.FileName;
                }
                catch (System.ComponentModel.Win32Exception)
                {
                    accessDenied = true;
                }
                catch (UnauthorizedAccessException)
                {
                    accessDenied = true;
                }
                catch (InvalidOperationException)
                {
                    // Process exited during inspection.
                    continue;
                }

                snapshots.Add(new ProcessSnapshot(proc.Id, proc.ProcessName, exe, CommandLine: null, AccessDenied: accessDenied));
            }
            catch (Exception ex)
            {
                logger?.Invoke($"[StopFilter] Notice: Could not inspect process: {ex.Message}");
            }
            finally
            {
                proc.Dispose();
            }
        }

        return snapshots;
    }
}
