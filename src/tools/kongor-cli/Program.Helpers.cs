// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// TAO (Together As One) MCP & Kongor CLI -- Created by guccidonut (https://github.com/guccidonut/PKV)

using System.Diagnostics;
using System.Net.Sockets;
using System.Text.Json;
using System.Text.RegularExpressions;
using Kongor.Cli.Infrastructure;
using Kongor.Cli.Mcp.Protocol;
using Kongor.Cli.Mcp.Registration;
using Kongor.Cli.Mcp.Logging;
using Kongor.Cli.Mcp.Services;
using Kongor.Core.Configuration;
using Spectre.Console;

namespace Kongor.Cli;

public static partial class Program
{
    internal const string WinnerBannerMarkup = "[bold rgb(57,255,20)]  \U0001F3C6 100% Passed - A WINNER IS YOU[/]";

    internal static readonly ProjectTarget[] AllProjects =
    [
        // Core Libraries & Extensions
        new("Kongor.Core", "src/libs/protocol-core/Kongor.Core.csproj", "libs"),
        new("MERRICK.DatabaseContext", "src/libs/database-context/MERRICK.DatabaseContext.csproj", "libs"),
        new("NEXUS.Contracts", "src/libs/contracts/NEXUS.Contracts.csproj", "libs"),
        new("ASPIRE.ServiceDefaults", "src/libs/service-defaults/ASPIRE.ServiceDefaults.csproj", "libs"),
        new("ASPIRE.SourceGenerator", "src/libs/source-generators/ASPIRE.SourceGenerator.csproj", "libs"),
        new("NEXUS.CdnBroker", "src/extensions/cdn-broker/NEXUS.CdnBroker.csproj", "libs"),
        new("Kongor.Modding", "src/extensions/modding/Kongor.Modding.csproj", "libs"),
        new("NEXUS.VulkanInterop", "src/extensions/vulkan-interop/NEXUS.VulkanInterop.csproj", "libs"),

        // Primary Applications
        new("WILLOWMAKER (Launcher)", "src/apps/willowmaker/WILLOWMAKER.csproj", "apps"),
        new("COMPEL (Server Host)", "src/apps/compel/COMPEL.csproj", "apps"),
        new("NEXUS Control Panel", "src/apps/control-panel/NEXUS.ControlPanel.GUI.csproj", "apps"),

        // Microservices
        new("ASPIRE AppHost", "src/services/aspire-host/ASPIRE.ApplicationHost.csproj", "services"),
        new("KONGOR Master Server", "src/services/master-server/KONGOR.MasterServer.csproj", "services"),
        new("TRANSMUTANSTEIN Chat Server", "src/services/chat-server/TRANSMUTANSTEIN.ChatServer.csproj", "services"),
        new("ZORGATH Web API", "src/services/web-api/ZORGATH.WebPortal.API.csproj", "services"),
        new("DAWNBRINGER Web UI", "src/services/web-ui/DAWNBRINGER.WebPortal.UI.csproj", "services"),
        new("Replay Renderer Service", "src/extensions/replay-renderer/ReplayRenderer.Service.csproj", "services"),

        // Tools & CLIs
        new("Kongor.Cli", "src/tools/kongor-cli/Kongor.Cli.csproj", "tools"),
        new("ModCli", "src/tools/mod-cli/ModCli.csproj", "tools"),
        new("PrebakeTool", "src/tools/prebake-tool/prebake-tool.csproj", "tools"),
        new("EndpointAuditor", "src/tools/endpoint-auditor/EndpointAuditor.csproj", "tools"),
        new("LuaVerifier", "src/tools/lua-verifier/LuaVerifier.csproj", "tools"),

        // Test Suites
        new("ASPIRE.Integration.Tests", "tests/integration/ASPIRE.Integration.Tests/ASPIRE.Integration.Tests.csproj",
            "tests"),
        new("ASPIRE.Prebaking.Tests", "tests/integration/ASPIRE.Prebaking.Tests/ASPIRE.Prebaking.Tests.csproj",
            "tests"),
        new("Grpc.Challenger.Tests", "tests/integration/Grpc.Challenger.Tests/Grpc.Challenger.Tests.csproj", "tests"),
        new("ASPIRE.Tests.Performance", "tests/performance/ASPIRE.Tests.Performance/ASPIRE.Tests.Performance.csproj",
            "tests"),
        new("COMPEL.Tests", "tests/unit/apps/COMPEL.Tests/COMPEL.Tests.csproj", "tests"),
        new("WILLOWMAKER.Tests", "tests/unit/apps/WILLOWMAKER.Tests/WILLOWMAKER.Tests.csproj", "tests"),
        new("Kongor.Modding.Tests", "tests/unit/extensions/Kongor.Modding.Tests/Kongor.Modding.Tests.csproj", "tests"),
        new("ReplayRenderer.Tests", "tests/unit/extensions/ReplayRenderer.Tests/ReplayRenderer.Tests.csproj", "tests"),
        new("Kongor.Core.Tests", "tests/unit/libs/Kongor.Core.Tests/Kongor.Core.Tests.csproj", "tests"),
        new("NEXUS.Contracts.Tests", "tests/unit/libs/NEXUS.Contracts.Tests/NEXUS.Contracts.Tests.csproj", "tests"),
        new("KONGOR.MasterServer.Tests",
            "tests/unit/services/KONGOR.MasterServer.Tests/KONGOR.MasterServer.Tests.csproj", "tests"),
        new("TRANSMUTANSTEIN.ChatServer.Tests",
            "tests/unit/services/TRANSMUTANSTEIN.ChatServer.Tests/TRANSMUTANSTEIN.ChatServer.Tests.csproj", "tests"),
        new("ZORGATH.WebPortal.API.Tests",
            "tests/unit/services/ZORGATH.WebPortal.API.Tests/ZORGATH.WebPortal.API.Tests.csproj", "tests")
    ];

    internal static string FindRepoRoot()
        => CliRuntimeContext.RequireAnyWorkspace().Root;

    internal static bool TryFindRepoRoot(out string? repoRoot)
    {
        if (CliRuntimeContext.TryResolveWorkspace(out KongorWorkspaceResult? ws) && ws is not null)
        {
            repoRoot = ws.Root;
            return true;
        }

        repoRoot = null;
        return false;
    }

    internal static int RunProcess(string executable, string arguments, string? workingDir = null)
    {
        string effectiveWorkDir = workingDir ?? FindRepoRoot();
        ProcessStartInfo psi = new()
        {
            FileName = executable,
            Arguments = arguments,
            WorkingDirectory = effectiveWorkDir,
            UseShellExecute = false,
            RedirectStandardOutput = false,
            RedirectStandardError = false
        };

        if (CliRuntimeContext.TryResolveWorkspace(out KongorWorkspaceResult? ws) && ws is not null)
        {
            psi.Environment[KongorWorkspaceResolver.EnvKongorRoot] = ws.Root;
            if (ws.IsBundledInstall)
            {
                psi.Environment[KongorWorkspaceResolver.EnvBundleRoot] = ws.Root;
            }
            else
            {
                psi.Environment[KongorWorkspaceResolver.EnvRepoRoot] = ws.Root;
            }
        }
        psi.Environment[KongorWorkspaceResolver.EnvCallerCwd] = CliRuntimeContext.InvocationCwd;

        try
        {
            using Process? proc = Process.Start(psi);
            if (proc == null) return -1;
            proc.WaitForExit();
            return proc.ExitCode;
        }
        catch (Exception ex)
        {
            AnsiConsole.MarkupLine($"[bold red]Failed to execute {Markup.Escape(executable)}:[/] {Markup.Escape(ex.Message)}");
            return -1;
        }
    }

    internal static string FormatProcessArgs(IEnumerable<string>? rawArgs)
    {
        if (rawArgs == null) return string.Empty;
        return string.Join(" ", rawArgs.Select(a =>
        {
            if (string.IsNullOrEmpty(a)) return "\"\"";
            if (a.Contains(' ') && (!a.StartsWith('"') || !a.EndsWith('"')))
            {
                return $"\"{a.Replace("\"", "\\\"")}\"";
            }
            return a;
        }));
    }

    internal static int RunPythonScript(string relativeScriptPath, string[] rawArgs)
    {
        string repo = FindRepoRoot();
        string fullScriptPath = Path.Combine(repo, relativeScriptPath);
        string subArgs = FormatProcessArgs(rawArgs);
        string args = string.IsNullOrWhiteSpace(subArgs)
            ? $"\"{fullScriptPath}\""
            : $"\"{fullScriptPath}\" {subArgs}";

        return RunProcess("python", args, repo);
    }

    internal static int RunPowerShellScript(string relativeScriptPath, string[] rawArgs)
    {
        string repo = FindRepoRoot();
        string fullScriptPath = Path.Combine(repo, relativeScriptPath);
        string subArgs = FormatProcessArgs(rawArgs);
        string scriptArgs = $"-NoProfile -ExecutionPolicy Bypass -File \"{fullScriptPath}\" {subArgs}".Trim();

        string pwsh = OperatingSystem.IsWindows() ? "powershell.exe" : "pwsh";
        return RunProcess(pwsh, scriptArgs, repo);
    }

    internal static int LaunchGameClient(string[] rawArgs)
    {
        KongorWorkspaceResult ws = CliRuntimeContext.RequireAnyWorkspace();
        if (ws.IsBundledInstall && ws.BundledLayout is { } layout)
        {
            AnsiConsole.MarkupLine($"[bold green][[BundledLayout]] Launching bundled WILLOWMAKER: {Markup.Escape(layout.LauncherExe)}[/]");
            return RunProcess(layout.LauncherExe, FormatProcessArgs(rawArgs), Path.GetDirectoryName(layout.LauncherExe) ?? layout.Root);
        }

        AnsiConsole.MarkupLine("[bold green]🚀 Launching WILLOWMAKER (Fast Shadow-Copy Runtime)...[/]");
        return FastAppLauncher.LaunchFromSourceWorkspace(
            new AppLaunchSpec(
                DisplayName: "WILLOWMAKER",
                CacheKey: "willowmaker",
                RelativeProjectPath: "src/apps/willowmaker/WILLOWMAKER.csproj",
                BinaryBaseName: "WILLOWMAKER",
                UseShadowCopy: true,
                RelativeWorkingDirectory: "games/client"),
            rawArgs,
            ws,
            RunProcess,
            RunDotnetWithLock);
    }

    internal static int LaunchGameServer(string[] rawArgs)
    {
        KongorWorkspaceResult ws = CliRuntimeContext.RequireAnyWorkspace();
        if (ws.IsBundledInstall && ws.BundledLayout is { } layout)
        {
            AnsiConsole.MarkupLine($"[bold green][[BundledLayout]] Launching bundled COMPEL: {Markup.Escape(layout.CompelExe)}[/]");
            return RunProcess(layout.CompelExe, FormatProcessArgs(rawArgs), Path.GetDirectoryName(layout.CompelExe) ?? layout.Root);
        }

        AnsiConsole.MarkupLine("[bold green]🛡️  Launching COMPEL (Fast Shadow-Copy Runtime)...[/]");
        return FastAppLauncher.LaunchFromSourceWorkspace(
            new AppLaunchSpec(
                DisplayName: "COMPEL",
                CacheKey: "compel",
                RelativeProjectPath: "src/apps/compel/COMPEL.csproj",
                BinaryBaseName: "COMPEL",
                UseShadowCopy: true,
                RelativeWorkingDirectory: "games/server"),
            rawArgs,
            ws,
            RunProcess,
            RunDotnetWithLock);
    }

    internal static int ForwardToProject(string relativeProjectPath, string[] rawArgs)
    {
        KongorWorkspaceResult ws = CliRuntimeContext.RequireSourceWorkspace();
        AppLaunchSpec? fastSpec = TryMatchFastToolSpec(relativeProjectPath);
        if (fastSpec is not null)
        {
            return FastAppLauncher.LaunchFromSourceWorkspace(fastSpec, rawArgs, ws, RunProcess, RunDotnetWithLock);
        }

        string repo = ws.Root;
        string subArgs = FormatProcessArgs(rawArgs);
        string fullArgs = string.IsNullOrWhiteSpace(subArgs)
            ? $"run --project {relativeProjectPath}"
            : $"run --project {relativeProjectPath} -- {subArgs}";

        return RunProcess("dotnet", fullArgs, repo);
    }

    private static AppLaunchSpec? TryMatchFastToolSpec(string relativeProjectPath)
    {
        string normalized = relativeProjectPath.Replace('\\', '/');
        if (normalized.EndsWith("NEXUS.ControlPanel.GUI.csproj", StringComparison.OrdinalIgnoreCase))
            return new("NEXUS Control Panel", "control-panel", normalized, "NEXUS.ControlPanel.GUI", UseShadowCopy: true);
        if (normalized.EndsWith("ModCli.csproj", StringComparison.OrdinalIgnoreCase))
            return new("ModCli", "mod-cli", normalized, "mod-cli", UseShadowCopy: false);
        if (normalized.EndsWith("prebake-tool.csproj", StringComparison.OrdinalIgnoreCase))
            return new("PrebakeTool", "prebake-tool", normalized, "prebake-tool", UseShadowCopy: false);
        if (normalized.EndsWith("EndpointAuditor.csproj", StringComparison.OrdinalIgnoreCase))
            return new("EndpointAuditor", "endpoint-auditor", normalized, "EndpointAuditor", UseShadowCopy: false);
        if (normalized.EndsWith("LuaVerifier.csproj", StringComparison.OrdinalIgnoreCase))
            return new("LuaVerifier", "lua-verifier", normalized, "LuaVerifier", UseShadowCopy: false);
        return null;
    }
    internal static async Task ShowEcosystemStatusAsync()
    {
        PrintBanner();
        AnsiConsole.MarkupLine("[bold cyan]\U0001F98D ProjectKongor Ecosystem Runtime Status & Health Topology[/]\n");

        bool hasWorkspace = CliRuntimeContext.TryResolveWorkspace(out KongorWorkspaceResult? ws);
        string repo = ws?.Root ?? "(not configured — run 'kongor workspace use <path>')";
        bool isBundled = ws?.IsBundledInstall ?? false;

        // 1. SERVICES & ENDPOINTS TABLE
        Table serviceTable = new();
        serviceTable.Border(TableBorder.Rounded);
        serviceTable.Title("[bold white]\U0001F310 Core Services & Endpoint Health[/]");
        serviceTable.AddColumn("[bold white]Service Component[/]");
        serviceTable.AddColumn("[bold white]Default Endpoint[/]");
        serviceTable.AddColumn("[bold white]Protocol / Purpose[/]");
        serviceTable.AddColumn("[bold white]Port Status[/]");
        serviceTable.AddColumn("[bold white]Active Process[/]");

        Process[] allProcs = Process.GetProcesses();
        Kongor.Cli.Mcp.Services.DockerComposeStackSnapshot dockerStack =
            await Kongor.Cli.Mcp.Services.DockerStackProbe.ProbeAsync(hasWorkspace ? ws!.Root : null, includeFleetHttpStatus: true);

        CheckServiceStatus(serviceTable, allProcs, "KONGOR Master Server", 55555, "HTTP / XML / PHP API",
            "KONGOR.MasterServer", dockerService: dockerStack.TryGetRunningService("master-server"));
        CheckServiceStatus(serviceTable, allProcs, "TRANSMUTANSTEIN Chat", 11031, "TCP Binary Chat",
            "TRANSMUTANSTEIN.ChatServer", dockerService: dockerStack.TryGetRunningService("chat-server"));
        CheckServiceStatus(serviceTable, allProcs, "TRANSMUTANSTEIN Match", 11032, "TCP Match Coordinator",
            "TRANSMUTANSTEIN.ChatServer", dockerService: dockerStack.TryGetRunningService("chat-server"));
        CheckServiceStatus(serviceTable, allProcs, "ZORGATH Web Portal API", 5200, "REST / OpenAPI / Swagger",
            "ZORGATH.WebPortal.API", dockerService: dockerStack.TryGetRunningService("web-api"));
        CheckServiceStatus(serviceTable, allProcs, "DAWNBRINGER Web Portal UI", 5266, "Razor MVC / SignalR",
            "DAWNBRINGER.WebPortal.UI", dockerService: dockerStack.TryGetRunningService("web-ui"));
        if (isBundled)
        {
            CheckServiceStatus(serviceTable, allProcs, "PostgreSQL 17 (MERRICK)", 25432, "PostgreSQL Standalone DB",
                "postgres|docker", 5432, dockerStack.TryGetRunningService("postgres"));
            CheckServiceStatus(serviceTable, allProcs, "Garnet / Redis Cache", 28398, "Distributed Cache",
                "garnet|redis|docker", 28298, dockerStack.TryGetRunningService("garnet"));
        }
        else
        {
            CheckServiceStatus(serviceTable, allProcs, "ASPIRE Cloud Cluster", 5550, "Aspire Orchestrator UI",
                "ASPIRE.ApplicationHost", dockerService: dockerStack.TryGetRunningService("aspire-dashboard"));
            CheckServiceStatus(serviceTable, allProcs, "Distributed Cache (Redis)", 28298, "Redis Distributed Cache",
                "redis|memurai|docker", 6379, dockerStack.TryGetRunningService("garnet"));
            CheckServiceStatus(serviceTable, allProcs, "Pyroscope Profiler", 4040, "Continuous Profiler", "pyroscope|docker");
        }
        CheckCompelSupervisorStatus(serviceTable, allProcs, dockerStack);
        CheckServiceStatus(serviceTable, allProcs, "WILLOWMAKER Launcher", 0, "Desktop Launcher GUI", "WILLOWMAKER");
        CheckGameClientStatus(serviceTable, allProcs);
        CheckDedicatedServersStatus(serviceTable, allProcs, dockerStack);
        CheckServiceStatus(serviceTable, allProcs, "NEXUS Control Panel", 0, "Desktop Admin GUI",
            "NEXUS.ControlPanel.GUI");
        if (!isBundled)
        {
            CheckServiceStatus(serviceTable, allProcs, "ComfyUI AI Server", 8188, "Generative AI Engine", "python");
            CheckServiceStatus(serviceTable, allProcs, "DocFX Documentation", 55590, "Documentation Hub", "python");
            CheckServiceStatus(serviceTable, allProcs, "Blender 3D", 0, "3D Pipeline / Hero Rigs", "blender");
        }

        AnsiConsole.Write(serviceTable);
        AnsiConsole.WriteLine();

        // 2. METADATA & TOPOLOGY
        Table metaTable = new();
        metaTable.Border(TableBorder.Rounded);
        metaTable.Title("[bold green]\U0001F4E6 Platform Topology & Configuration[/]");
        metaTable.AddColumn("[bold white]Property[/]");
        metaTable.AddColumn("[bold white]Value / Details[/]");

        metaTable.AddRow("[cyan]Platform SemVer[/]", $"[bold white]v{NEXUS.Contracts.Domain.NexusVersionInfo.PlatformVersion}[/]");
        metaTable.AddRow("[cyan]Engine Protocol[/]", $"[bold white]v{NEXUS.Contracts.Domain.NexusVersionInfo.EngineProtocolVersion}[/] (Min Compatible: [yellow]{NEXUS.Contracts.Domain.NexusVersionInfo.MinimumCompatibleEngineProtocolVersion}[/])");
        metaTable.AddRow("[cyan]Centralized User Secrets Vault[/]",
            "[green]00005072-6f6a-6563-7420-4b4f4e474f52[/] [grey](src/services/aspire-host/ASPIRE.ApplicationHost.csproj)[/]");
        metaTable.AddRow("[cyan]Repository Root[/]", $"[grey]{Markup.Escape(repo)}[/]");

        AnsiConsole.Write(metaTable);
        AnsiConsole.WriteLine();

        // 3. VFS MODDING & OVERRIDE DIAGNOSTICS
        Kongor.Cli.Mcp.Services.VfsStatusResult vfs = Kongor.Cli.Mcp.Services.VfsDiagnostics.GetVfsStatus();
        Table vfsTable = new();
        vfsTable.Border(TableBorder.Rounded);
        vfsTable.Title("[bold magenta]📁 K2 VFS Mounts & Override Diagnostics[/]");
        vfsTable.AddColumn("[bold white]Property[/]");
        vfsTable.AddColumn("[bold white]Diagnostic Value[/]");

        vfsTable.AddRow("[cyan]Active Search Order[/]", string.Join(" -> ", vfs.SearchOrder));
        vfsTable.AddRow("[cyan]Override Priority[/]", vfs.OverrideMountedFirst ? "[bold green]Active (Mounted First)[/]" : "[bold red]Inactive[/]");
        vfsTable.AddRow("[cyan]Active Overrides[/]", $"[bold white]{vfs.OverrideFilesCount}[/] files in games/client/override/");
        vfsTable.AddRow("[cyan]Shadowed Base Assets[/]", vfs.ShadowedFilesCount > 0 ? $"[bold yellow]{vfs.ShadowedFilesCount}[/] base files shadowed by overrides" : "[green]None (Zero conflicts)[/]");

        AnsiConsole.Write(vfsTable);
        AnsiConsole.WriteLine();

        AnsiConsole.MarkupLine(
            "[grey]Quick Actions:[/] [yellow]kongor start[/] (Launch all services) | [yellow]kongor stop all[/] (Stop all processes) | [yellow]kongor doctor[/] (Port checks)\n");
    }

    internal static void CheckServiceStatus(Table table, Process[] procs, string name, int port, string purpose,
        string procMatch, int altPort = 0, Kongor.Cli.Mcp.Services.DockerComposeServiceState? dockerService = null)
    {
        bool isOpen = false;
        if (port > 0)
        {
            isOpen = ProbePort(port);
            if (!isOpen && altPort > 0) isOpen = ProbePort(altPort);
        }

        string portStr = port > 0 ? $"127.0.0.1:{port}" : "[grey]N/A (GUI / Client)[/]";
        string status = port > 0
            ? isOpen ? "[bold green]\u25CF ONLINE[/]" : "[grey]\u25CB OFFLINE[/]"
            : "[grey]\u2014[/]";

        // Find active process
        string[] matches = procMatch.Split('|', StringSplitOptions.RemoveEmptyEntries);
        Process? matched = procs.FirstOrDefault(p =>
        {
            try
            {
                return matches.Any(m => p.ProcessName.Contains(m.Trim(), StringComparison.OrdinalIgnoreCase));
            }
            catch
            {
                return false;
            }
        });

        string procStatus;
        if (matched != null)
            try
            {
                long memMb = matched.WorkingSet64 / (1024 * 1024);
                procStatus = $"[bold green]Running[/] [grey](PID: {matched.Id}, {memMb} MB)[/]";
            }
            catch
            {
                procStatus = $"[bold green]Running[/] [grey](PID: {matched.Id})[/]";
            }
        else
            procStatus = "[grey]Stopped[/]";

        if (matched == null && dockerService is { IsRunning: true })
        {
            procStatus =
                $"[bold green]Running[/] [cyan](Docker: {Markup.Escape(dockerService.ContainerName)}, {Markup.Escape(dockerService.DisplaySummary)})[/]";
        }

        table.AddRow($"[bold cyan]{name}[/]", portStr, purpose, status, procStatus);
    }

    internal static void CheckCompelSupervisorStatus(
        Table table,
        Process[] procs,
        Kongor.Cli.Mcp.Services.DockerComposeStackSnapshot dockerStack)
    {
        var matchSvc = dockerStack.TryGetRunningService("compel-dedicated");
        var arcadeSvc = dockerStack.TryGetRunningService("compel-queue-arcade");

        if (matchSvc == null && arcadeSvc == null)
        {
            CheckServiceStatus(table, procs, "COMPEL Server Manager", 11234, "Win32 Sandbox Supervisor", "COMPEL");
            return;
        }

        List<string> endpoints = [];
        List<string> summaries = [];
        if (matchSvc != null)
        {
            endpoints.Add("127.0.0.1:11334 (Tier D)");
            summaries.Add($"Match: {Markup.Escape(matchSvc.DisplaySummary)}");
        }
        if (arcadeSvc != null)
        {
            endpoints.Add("127.0.0.1:11484 (Tier Q)");
            summaries.Add($"Arcade: {Markup.Escape(arcadeSvc.DisplaySummary)}");
        }

        bool anyOpen = (matchSvc != null && ProbePort(11334)) || (arcadeSvc != null && ProbePort(11484));
        string status = anyOpen ? "[bold green]\u25CF ONLINE[/]" : "[yellow]\u25CF STARTING[/]";
        string procStatus = $"[bold green]Running[/] [cyan](Docker: {string.Join(" | ", summaries)})[/]";
        table.AddRow("[bold cyan]COMPEL Server Manager[/]", string.Join(", ", endpoints), "Containerized Fleet Supervisors", status, procStatus);
    }

    internal static void CheckGameClientStatus(Table table, Process[] procs)
    {
        Process? matched = null;
        foreach (Process p in procs)
        {
            try
            {
                if (p.ProcessName.Equals("hon_x64", StringComparison.OrdinalIgnoreCase) ||
                    p.ProcessName.Equals("hon-x86_64", StringComparison.OrdinalIgnoreCase) ||
                    p.ProcessName.Equals("HoN64", StringComparison.OrdinalIgnoreCase))
                {
                    string? cmd = Kongor.Cli.Mcp.Handlers.GameClientManageHandler.TryGetProcessCommandLine(p.Id);
                    if (cmd == null || (!cmd.Contains("-dedicated", StringComparison.OrdinalIgnoreCase) && !cmd.Contains("--type=", StringComparison.OrdinalIgnoreCase)))
                    {
                        matched = p;
                        break;
                    }
                }
            }
            catch { }
        }

        string procStatus;
        if (matched != null)
        {
            try
            {
                long memMb = matched.WorkingSet64 / (1024 * 1024);
                procStatus = $"[bold green]Running[/] [grey](PID: {matched.Id}, {memMb} MB, IPC: \\\\.\\pipe\\willowmaker-ipc)[/]";
            }
            catch
            {
                procStatus = $"[bold green]Running[/] [grey](PID: {matched.Id})[/]";
            }
        }
        else
        {
            procStatus = "[grey]Stopped[/]";
        }

        table.AddRow("[bold cyan]K2 Player Client[/]", "[grey]N/A (GUI / Client)[/]", "Player Client (1000 FPS)", "[grey]—[/]", procStatus);
    }

    internal static void CheckDedicatedServersStatus(
        Table table,
        Process[] procs,
        Kongor.Cli.Mcp.Services.DockerComposeStackSnapshot? dockerStack = null)
    {
        List<(int Pid, int Port, long MemMb)> dedicated = [];
        foreach (Process p in procs)
        {
            try
            {
                if (p.ProcessName.Equals("hon_x64", StringComparison.OrdinalIgnoreCase) ||
                    p.ProcessName.Equals("hon-x86_64", StringComparison.OrdinalIgnoreCase) ||
                    p.ProcessName.Equals("HoN64", StringComparison.OrdinalIgnoreCase))
                {
                    string? cmd = Kongor.Cli.Mcp.Handlers.GameClientManageHandler.TryGetProcessCommandLine(p.Id);
                    if (cmd != null && cmd.Contains("-dedicated", StringComparison.OrdinalIgnoreCase))
                    {
                        int port = Kongor.Cli.Mcp.Handlers.GameServerManageHandler.ExtractPortFromCommandLine(cmd) ?? 11235;
                        long mem = 0;
                        try { mem = p.WorkingSet64 / (1024 * 1024); } catch { }
                        dedicated.Add((p.Id, port, mem));
                    }
                }
            }
            catch { }
        }

        string portStr = dedicated.Count > 0
            ? string.Join(", ", dedicated.Select(d => $"127.0.0.1:{d.Port}"))
            : "[grey]127.0.0.1:11235[/]";

        string status = dedicated.Count > 0
            ? "[bold green]● ONLINE[/]"
            : "[grey]○ OFFLINE[/]";

        string procStatus = dedicated.Count > 0
            ? $"[bold green]Running[/] [grey]({dedicated.Count} worker(s): {string.Join(", ", dedicated.Select(d => $"PID {d.Pid}, {d.MemMb} MB"))})[/]"
            : "[grey]Stopped[/]";

        int dockerMatchWorkers = dockerStack?.MatchFleet?.ActiveWorkerCount ?? 0;
        int dockerArcadeWorkers = dockerStack?.ArcadeFleet?.ActiveWorkerCount ?? 0;
        if (dedicated.Count == 0 && (dockerMatchWorkers + dockerArcadeWorkers) > 0)
        {
            string dockerPorts = "UDP 11335+ (Match), 11485+ (Arcade)";
            string dockerProc =
                $"[bold green]Running[/] [cyan](Docker Linux: {dockerMatchWorkers} Match Tier D, {dockerArcadeWorkers} Queue Arcade Tier Q)[/]";
            table.AddRow("[bold cyan]Dedicated Game Servers[/]", dockerPorts, "Dedicated Server Fleet", "[bold green]● ONLINE[/]", dockerProc);
            return;
        }

        table.AddRow("[bold cyan]Dedicated Game Servers[/]", portStr, "Dedicated Server Fleet", status, procStatus);
    }

    internal static bool ProbePort(int port)
    {
        try
        {
            using TcpClient client = new();
            IAsyncResult result = client.BeginConnect("127.0.0.1", port, null, null);
            bool success = result.AsyncWaitHandle.WaitOne(TimeSpan.FromMilliseconds(100));
            if (success && client.Connected)
            {
                client.EndConnect(result);
                return true;
            }
        }
        catch
        {
        }

        return false;
    }

    internal static void PrintBanner()
    {
        const string gorillaArt = """
                                                                   %%%%%
                                                               %%##*****#%
                                                             %#*+++++=+*%
                                                            %#*++=++++==+*%
                                                            %#++==++++++++*##%
                                                          %#+====+++++++++++++#%%
                                                         %#+=====+++++=======+++#%
                                                        %#*+++==+++++++++++++++++*#%%%%%%%%%%%%
                                                          %#*++++++++++++++++++++++++**###*****#%%
                                                          %%#**++++++******++++++++++++++++++++**#%
                                                            %%%#*+++**************+++++++++++++++*#%
                                                               %%***********************+++++++++++*#
                                                               %%******************************++++*#
                                                               %%**********+++******************+++*#
                                                               %%*********++++++*****************++*#
                                                               %%**********+++++++++*************++*#
                                                               %%**********+++++++++++++++++*****++*#
                                                               %%*****#**##**++++++++++++++*********#
                                                               %%**#*##*##%%##****+++++***####******#
                                                               %%**####*##%  %%%%#####%%%%%%###*****#
                                                               %%#########%       %%%%    %%###*****#
                                                               %%#########%               %###****#
                                                               %%%########%%              %#####*##
                                                                 %%######**#%            %%######*##
                                                                   %%%%####%%            %%%%#####%
                                  """;

        const string titleBanner = """
                                     ____                     _                 _       _  __   ___    _   _    ____    ___    ____  
                                    |  _ \   _ __    ___     (_)   ___    ___  | |_    | |/ /  / _ \  | \ | |  / ___|  / _ \  |  _ \ 
                                    | |_) | | '__|  / _ \    | |  / _ \  / __| | __|   | ' /  | | | | |  \| | | |  _  | | | | | |_) |
                                    |  __/  | |    | (_) |   | | |  __/ | (__  | |_    | . \  | |_| | | |\  | | |_| | | |_| | |  _ < 
                                    |_|     |_|     \___/   _/ |  \___|  \___|  \__|   |_|\_\  \___/  |_| \_|  \____|  \___/  |_| \_\
                                                           |__/                                                                      
                                   """;

        AnsiConsole.MarkupLine($"[bold purple]{Markup.Escape(gorillaArt)}[/]");
        AnsiConsole.MarkupLine($"[bold cyan]{Markup.Escape(titleBanner)}[/]");
        AnsiConsole.MarkupLine(
            "[bold green]The Next-Gen Master Server, Dedicated Server Manager & Launcher Ecosystem[/]");
        AnsiConsole.MarkupLine(
            "[grey]Type [yellow]kongor --help[/] or [yellow]kongor <command> --help[/] for details.[/]\n");
    }

    internal static void RunDoctor()
    {
        PrintBanner();
        AnsiConsole.MarkupLine("[bold white]\U0001F50D Running ProjectKongor Doctor Diagnostics...[/]\n");

        bool isBundled = Kongor.Core.Configuration.BundledLayoutResolver.TryResolve(out _);
        Table table = new();
        table.Border(TableBorder.Rounded);
        table.AddColumn("[bold white]Service / Port[/]");
        table.AddColumn("[bold white]Expected Endpoint[/]");
        table.AddColumn("[bold white]Port Status[/]");

        CheckPort(table, "KONGOR Master Server", 55555);
        CheckPort(table, "TRANSMUTANSTEIN Chat", 11031);
        CheckPort(table, "TRANSMUTANSTEIN Match", 11032);
        CheckPort(table, "ZORGATH Web API", 5200);
        CheckPort(table, "DAWNBRINGER Web UI", 5266);
        CheckPort(table, "COMPEL Control Plane", 11234);
        CheckPort(table, "COMPEL Match Slave 1", isBundled ? 11235 : 21235);
        if (!isBundled) CheckPort(table, "Aspire Dashboard", 5550);
        CheckPort(table, isBundled ? "Garnet Cache" : "Redis Cache", isBundled ? 28398 : 28298);
        CheckPort(table, "PostgreSQL DB", isBundled ? 25432 : 5432);

        AnsiConsole.Write(table);
        AnsiConsole.MarkupLine("\n[bold green]\u2713 Doctor diagnostics completed.[/]\n");
    }

    internal static void CheckPort(Table table, string name, int port)
    {
        bool isOpen = false;
        try
        {
            using TcpClient client = new();
            IAsyncResult result = client.BeginConnect("127.0.0.1", port, null, null);
            bool success = result.AsyncWaitHandle.WaitOne(TimeSpan.FromMilliseconds(100));
            if (success && client.Connected)
            {
                client.EndConnect(result);
                isOpen = true;
            }
        }
        catch
        {
            isOpen = false;
        }

        string status = isOpen ? "[bold green]ONLINE (Port Active)[/]" : "[grey]OFFLINE (Port Available)[/]";
        table.AddRow($"[bold cyan]{name}[/]", $"127.0.0.1:{port}", status);
    }

    internal static void PrintStatusDashboard()
    {
        PrintBanner();

        bool hasWs = CliRuntimeContext.TryResolveWorkspace(out KongorWorkspaceResult? ws);
        bool isBundled = hasWs && ws is { IsBundledInstall: true };
        string repo = hasWs && ws is not null
            ? ws.Root
            : "(not configured — run 'kongor workspace use <path>')";

        // 1. PRIMARY APPLICATIONS & SERVICES TABLE
        Table primaryTable = new();
        primaryTable.Border(TableBorder.Rounded);
        primaryTable.Title("[bold cyan]\U0001F680 PRIMARY APPLICATIONS & SERVICES[/]");
        primaryTable.AddColumn("[bold white]Primary Command[/]");
        primaryTable.AddColumn("[bold white]Component / Target[/]");
        primaryTable.AddColumn("[bold white]Description[/]");

        primaryTable.AddRow("[yellow]kongor play[/]", "[bold cyan]WILLOWMAKER[/]",
            "Game Client Launcher (1000 FPS / Vulkan / CDN Sync)");
        primaryTable.AddRow("[yellow]kongor host[/]", "[bold cyan]COMPEL[/]",
            "Dedicated Server Manager (Sandbox / 1.0ms Timer)");
        primaryTable.AddRow("[yellow]kongor gui[/]", "[bold cyan]NEXUS Control Panel[/]",
            "Desktop Admin GUI (Avalonia 11, 3D Hero Viewport)");
        primaryTable.AddRow("[yellow]kongor start[/] / [yellow]aspire[/]", "[bold cyan]ASPIRE Cluster[/]",
            "9-Microservice Local Cloud Cluster (localhost:5550)");
        primaryTable.AddRow("[yellow]kongor stop [[target]][/]", "[bold cyan]Process Supervisor[/]",
            "Stop running services ('stop all', 'stop docker', 'stop comfy', 'stop blender')");
        primaryTable.AddRow("[yellow]kongor docker [[up|down|restart|cleanup|check]][/]", "[bold cyan]Docker Stack[/]",
            "Turnkey Containerized Cluster + Match & Queue Arcade Fleets (+ --client)");
        primaryTable.AddRow("[yellow]kongor blender [[file]][/]", "[bold cyan]Blender 3D[/]",
            "Launch Blender 3D (v5.2+ / v4.2 LTS) or open .blend scene");
        primaryTable.AddRow("[yellow]kongor comfy [[install|stop]][/]", "[bold cyan]ComfyUI Generative AI[/]",
            "Launch ComfyUI server (port 8188) & MCP agent tools");
        primaryTable.AddRow("[yellow]kongor model export <hero>[/]", "[bold cyan]Model Exporter[/]",
            "Batch export HoN models, armatures & clips to glTF/GLB");
        primaryTable.AddRow("[yellow]kongor replay highlight <rep>[/]", "[bold cyan]Highlight Clipper[/]",
            "Auto-detect & render game-winning teamfight MP4");
        primaryTable.AddRow("[yellow]kongor replay render <rep>[/]", "[bold cyan]Replay Renderer[/]",
            "Render 60FPS in-game player POV video");
        primaryTable.AddRow("[yellow]kongor docs[/]", "[bold cyan]DocFX Hub[/]",
            "Interactive Developer Documentation & API Hub (:55590)");
        primaryTable.AddRow("[yellow]kongor ask <question>[/] / [yellow]?[/]", "[bold cyan]KONGOR AI[/]",
            "Natural language RAG codebase & architecture search");
        primaryTable.AddRow("[yellow]kongor doctor[/]", "[bold cyan]Diagnostics[/]",
            "System environment, port availability & health checks");
        primaryTable.AddRow("[yellow]kongor status[/]", "[bold cyan]Ecosystem Status[/]",
            "Display ecosystem topology, active ports & service health");

        AnsiConsole.Write(primaryTable);
        AnsiConsole.WriteLine();

        if (!isBundled)
        {
        // 2. DEVELOPER TOOLS & ENGINEERING PIPELINE TABLE
        Table devTable = new();
        devTable.Border(TableBorder.Rounded);
        devTable.Title("[bold green]\U0001F6E0\uFE0F DEVELOPER TOOLS & ENGINEERING PIPELINE[/]");
        devTable.AddColumn("[bold white]Developer Command[/]");
        devTable.AddColumn("[bold white]Target / Area[/]");
        devTable.AddColumn("[bold white]Description[/]");

        devTable.AddRow("[yellow]kongor build [[-c Release]][/]", "[bold green]Build Pipeline[/]",
            "Multi-target build across all 34 projects (Debug + Release)");
        devTable.AddRow("[yellow]kongor test [[unit|int|all]][/]", "[bold green]Test Harness[/]",
            "Execute TUnit/xUnit test suites (default: --no-build)");
        devTable.AddRow("[yellow]kongor clean [[--all]][/]", "[bold green]Deep Clean[/]",
            "Deep-clean bin/, obj/, TestResults/, and build caches");
        devTable.AddRow("[yellow]kongor secret [[list|set]][/]", "[bold green]User Secrets[/]",
            "Manage centralized secrets vault (00005072-6f6a-6563...)");
        devTable.AddRow("[yellow]kongor logs [[service]] [[-t]][/]", "[bold green]Logs Inspector[/]",
            "Inspect and live-stream microservice / server logs");
        devTable.AddRow("[yellow]kongor refresh[/]", "[bold green]CLI Self-Update[/]",
            "Re-compile, package & reinstall global tool with latest edits");
        devTable.AddRow("[yellow]kongor sync-version [[ver]][/]", "[bold green]Release Sync[/]",
            $"Sync SemVer {NEXUS.Contracts.Domain.NexusVersionInfo.PlatformVersion}, CalVer & Protocol across ecosystem");
        devTable.AddRow("[yellow]kongor master-server[/]", "[bold green]Backend Standalone[/]",
            "Run KONGOR Master Server standalone (port 55555)");
        devTable.AddRow("[yellow]kongor chat-server[/]", "[bold green]Backend Standalone[/]",
            "Run TRANSMUTANSTEIN TCP Chat Server standalone (11031-33)");
        devTable.AddRow("[yellow]kongor web-api[/]", "[bold green]Backend Standalone[/]",
            "Run ZORGATH Web Portal REST API standalone");
        devTable.AddRow("[yellow]kongor web-ui[/]", "[bold green]Backend Standalone[/]",
            "Run DAWNBRINGER Web Portal MVC UI standalone");
        devTable.AddRow("[yellow]kongor where[/] / [yellow]workspace[/]", "[bold green]Workspace Registry[/]",
            "Show or switch active ProjectKongor checkout/bundle from anywhere");
        devTable.AddRow("[yellow]kongor mod [[list|install]][/]", "[bold green]Mod CLI[/]",
            "Mod package manager & DAG conflict resolver (tools/mod-cli)");
        devTable.AddRow("[yellow]kongor prebake [[--help]][/]", "[bold green]Prebake Tool[/]",
            "Entity XML harvesting & MemoryPack+Zstd binary prebaker");
        devTable.AddRow("[yellow]kongor audit-endpoints[/]", "[bold green]Endpoint Auditor[/]",
            "Extract Lua/XML SubmitForm routes & reconcile API");
        devTable.AddRow("[yellow]kongor lua-verify[/]", "[bold green]Lua Sandbox[/]",
            "Headless NLua sandbox & K2 UI widget tester");
        devTable.AddRow("[yellow]kongor k2-test[/]", "[bold green]K2 Simulator[/]",
            "Headless K2 engine & cvar simulator");
        devTable.AddRow("[yellow]kongor tools test-e2e[/]", "[bold green]E2E Test Suite[/]",
            "Execute verification test harness");

        devTable.AddRow("[yellow]kongor completion powershell[/]", "[bold green]Tab Completion[/]",
            "Emit or install instant (<2ms) static shell tab-completions");
        AnsiConsole.Write(devTable);
        }
        else
        {
            AnsiConsole.MarkupLine("[grey]Layout Mode:[/] [yellow]BundledInstall[/] [grey](Engineering/source commands hidden; run 'kongor where' or 'kongor completion')[/]");
        }

        AnsiConsole.MarkupLine($"\n[grey]Repository Root:[/] [cyan]{Markup.Escape(repo)}[/]");
        AnsiConsole.MarkupLine("[grey]For command-specific help, run:[/] [yellow]kongor <command> --help[/]");
        AnsiConsole.MarkupLine(
            "[grey]Or use unified tools syntax:[/] [yellow]kongor tools <tool_name> [[args...]][/]\n");
    }

    [GeneratedRegex(@"<VersionPrefix>([^<]+)</VersionPrefix>")]
    internal static partial Regex VersionPrefixRegex();

    [GeneratedRegex(@"<VersionSuffix>([^<]+)</VersionSuffix>")]
    internal static partial Regex VersionSuffixRegex();

    [GeneratedRegex(@"<InformationalVersion>([^<]+)</InformationalVersion>")]
    internal static partial Regex InformationalVersionRegex();

    [GeneratedRegex(@"public const string BuildDate = ""([^""]+)"";")]
    internal static partial Regex BuildDateRegex();

    [GeneratedRegex(@"public const string EngineProtocolVersion = ""([^""]+)"";")]
    internal static partial Regex EngineProtocolRegex();

    [GeneratedRegex(@"public const string MinimumCompatibleEngineProtocolVersion = ""([^""]+)"";")]
    internal static partial Regex MinEngineProtocolRegex();

    [GeneratedRegex(@"Failed:\s*(\d+),\s*Passed:\s*(\d+),\s*Skipped:\s*(\d+),\s*Total:\s*(\d+)")]
    internal static partial Regex TestSummaryRegex();

    internal static void PrintVersionInfo()
    {
        PrintBanner();

        bool hasRepo = TryFindRepoRoot(out string? repo);
        string buildPropsPath = hasRepo && !string.IsNullOrWhiteSpace(repo)
            ? Path.Combine(repo, "Directory.Build.props")
            : string.Empty;
        string nexusVersionPath = hasRepo && !string.IsNullOrWhiteSpace(repo)
            ? Path.Combine(repo, "src", "libs", "contracts", "Domain", "NexusVersionInfo.cs")
            : string.Empty;

        string semVer = NEXUS.Contracts.Domain.NexusVersionInfo.PlatformVersion;
        string calVer = NEXUS.Contracts.Domain.NexusVersionInfo.BuildDate;
        string engineProto = "5.0.0";
        string minProto = "4.10.1";
        string infoVersion = NEXUS.Contracts.Domain.NexusVersionInfo.InformationalHeaderVersion;

        if (File.Exists(buildPropsPath))
            try
            {
                string text = File.ReadAllText(buildPropsPath);
                Match mPrefix = VersionPrefixRegex().Match(text);
                if (mPrefix.Success) semVer = mPrefix.Groups[1].Value;

                Match mSuffix = VersionSuffixRegex().Match(text);
                if (mSuffix.Success && !string.IsNullOrWhiteSpace(mSuffix.Groups[1].Value)) calVer = mSuffix.Groups[1].Value;

                Match mInfo = InformationalVersionRegex().Match(text);
                if (mInfo.Success) infoVersion = mInfo.Groups[1].Value;
            }
            catch
            {
            }

        if (File.Exists(nexusVersionPath))
            try
            {
                string text = File.ReadAllText(nexusVersionPath);
                Match mBuildDate = BuildDateRegex().Match(text);
                if (mBuildDate.Success && !string.IsNullOrWhiteSpace(mBuildDate.Groups[1].Value)) calVer = mBuildDate.Groups[1].Value;

                Match mProto = EngineProtocolRegex().Match(text);
                if (mProto.Success) engineProto = mProto.Groups[1].Value;

                Match mMinProto = MinEngineProtocolRegex().Match(text);
                if (mMinProto.Success) minProto = mMinProto.Groups[1].Value;
            }
            catch
            {
            }

        Table table = new();
        table.Border(TableBorder.Rounded);
        table.Title("[bold cyan]\U0001F98D ProjectKongor Ecosystem Version Information[/]");
        table.AddColumn("[bold white]Property[/]");
        table.AddColumn("[bold white]Value[/]");
        table.AddColumn("[bold white]Methodology / Description[/]");

        table.AddRow("[bold cyan]Platform Version[/]", $"[bold green]{semVer}[/]",
            "SemVer 2.0 (Solution-wide platform era)");
        table.AddRow("[bold cyan]Build Date[/]", $"[bold green]{calVer}[/]",
            "CalVer (Deterministic calendar build stamp)");
        table.AddRow("[bold cyan]Engine Protocol[/]", $"[bold green]v{engineProto}[/]",
            "K2 Game Engine Protocol (Vulkan 1.3 / 1000 FPS)");
        table.AddRow("[bold cyan]Min Compatible Proto[/]", $"[bold green]>={minProto}[/]",
            "Backwards Compatibility (HoN 4.10.1 clients & mods)");
        table.AddRow("[bold cyan]Informational Version[/]", $"[bold green]{Markup.Escape(infoVersion)}[/]",
            "Assembly Metadata / HTTP Header / gRPC Contract");
        table.AddRow("[bold cyan]Author & License[/]", "[bold green]guccidonut (Project KONGOR V) — AGPL-3.0-or-later | MPL-2.0[/]",
            "See LICENSE, native/LICENSE, NOTICE & CITATION.cff");
        table.AddRow("[bold cyan]Corresponding Source[/]", "[bold green]https://github.com/guccidonut/PKV[/]",
            "Canonical Public Cleanroom Source Repository");

        AnsiConsole.Write(table);
        AnsiConsole.MarkupLine($"\n[grey]Repository Root:[/] [cyan]{Markup.Escape(repo ?? "(not configured — run 'kongor workspace use <path>')")}[/]");
        AnsiConsole.MarkupLine(
            "[grey]To synchronize or bump the ecosystem version, run:[/] [yellow]kongor sync-version <new_version>[/]\n");
    }

    internal static int ExecuteBuild(string target, string config, bool clean, bool restore, string verbosity)
    {
        string repo = FindRepoRoot();
        string targetLower = target.ToLowerInvariant().Trim();

        if (targetLower is "native" or "c++" or "cmake")
        {
            List<string> psArgs = ["-Configuration", config, "-Target", "native", "-Verbosity", verbosity];
            if (clean) psArgs.Add("-Clean");
            if (restore) psArgs.Add("-Restore");
            return RunPowerShellScript("scripts/build.ps1", [.. psArgs]);
        }

        string[] configs = config.Equals("All", StringComparison.OrdinalIgnoreCase) ||
                           config.Equals("Both", StringComparison.OrdinalIgnoreCase)
            ? ["Debug", "Release"]
            : [config];

        Stopwatch sw = Stopwatch.StartNew();

        // 1. Entire Solution (Default / Fast Parallel Build)
        if (targetLower == "all" || string.IsNullOrWhiteSpace(targetLower) || targetLower == "solution" ||
            targetLower == "sln")
        {
            AnsiConsole.MarkupLine(
                "[bold cyan]================================================================================[/]");
            AnsiConsole.MarkupLine(
                "[bold cyan]                PROJECT KONGOR ECOSYSTEM BUILD PIPELINE                        [/]");
            AnsiConsole.MarkupLine(
                "[bold cyan]================================================================================[/]");
            AnsiConsole.MarkupLine("[grey]Target Scope:[/]   [bold white]Entire Solution (ProjectKongor.slnx)[/]");
            AnsiConsole.MarkupLine($"[grey]Configurations:[/] [bold white]{string.Join(", ", configs)}[/]");
            AnsiConsole.MarkupLine($"[grey]Verbosity:[/]      [bold white]{verbosity}[/]");
            AnsiConsole.WriteLine();

            foreach (string cfg in configs)
            {
                AnsiConsole.MarkupLine($"[bold yellow]\U0001F528 BUILDING SOLUTION ({Markup.Escape(cfg)})...[/]");

                if (clean)
                {
                    AnsiConsole.MarkupLine($"[grey]  Cleaning {Markup.Escape(cfg)} build outputs and releasing build servers...[/]");
                    RunProcess("dotnet", "build-server shutdown", repo);
                    RunProcess("dotnet", $"clean -c {cfg} -v {verbosity}", repo);
                }

                string restoreFlag = restore ? "--force" : "";
                string buildArgs =
                    $"build -c {cfg} -v {verbosity} --nologo -nodeReuse:false {restoreFlag}"
                        .Trim();

                string testLockScript = Path.Combine(repo, "scripts", "Invoke-KongorTestLock.ps1");
                int code = RunDotnetWithLock(testLockScript, buildArgs, repo);
                if (code != 0)
                {
                    AnsiConsole.MarkupLine(
                        $"\n[bold red]\u274C Solution build failed for configuration: {Markup.Escape(cfg)} (Exit Code: {code})[/]");
                    return code;
                }
            }

            sw.Stop();
            AnsiConsole.WriteLine();
            AnsiConsole.MarkupLine(
                "[bold cyan]================================================================================[/]");
            AnsiConsole.MarkupLine(
                "[bold cyan]                       BUILD SUMMARY REPORT                                     [/]");
            AnsiConsole.MarkupLine(
                "[bold cyan]================================================================================[/]");
            AnsiConsole.MarkupLine($"Total Elapsed Time:   [bold white]{sw.Elapsed.TotalSeconds:F2} seconds[/]");
            AnsiConsole.MarkupLine(
                "[bold green]\u2728 ALL TARGETS COMPILED CLEANLY ACROSS ALL SPECIFIED CONFIGURATIONS! \u2728[/]");
            return 0;
        }

        // 2. Specific project/category target
        List<ProjectTarget> selected = [];
        if (((string[])["apps", "services", "libs", "tools", "tests"]).Contains(targetLower))
            selected.AddRange(AllProjects.Where(p => p.Category == targetLower));
        else
            selected.AddRange(AllProjects.Where(p =>
                p.Name.Contains(targetLower, StringComparison.OrdinalIgnoreCase) ||
                p.Path.Contains(targetLower, StringComparison.OrdinalIgnoreCase)));

        if (selected.Count == 0)
        {
            AnsiConsole.MarkupLine($"[bold red]\u274C No projects matched target '{Markup.Escape(target)}'.[/]");
            AnsiConsole.MarkupLine(
                "[yellow]Available targets: all, apps, services, libs, tools, tests, willowmaker, compel, aspire, master, chat, web, mod, cli, etc.[/]");
            return 1;
        }

        AnsiConsole.MarkupLine(
            "[bold cyan]================================================================================[/]");
        AnsiConsole.MarkupLine(
            "[bold cyan]                PROJECT KONGOR ECOSYSTEM BUILD PIPELINE                        [/]");
        AnsiConsole.MarkupLine(
            "[bold cyan]================================================================================[/]");
        AnsiConsole.MarkupLine(
            $"[grey]Target Scope:[/]   [bold white]{Markup.Escape(target)}[/] ({selected.Count} projects)");
        AnsiConsole.MarkupLine($"[grey]Configurations:[/] [bold white]{string.Join(", ", configs)}[/]");
        AnsiConsole.MarkupLine($"[grey]Verbosity:[/]      [bold white]{verbosity}[/]");
        AnsiConsole.WriteLine();

        int successCount = 0;
        List<(string Name, string Config, string Path)> failures = [];

        foreach (string cfg in configs)
        {
            AnsiConsole.MarkupLine($"[bold yellow]\U0001F528 BUILDING CONFIGURATION: {Markup.Escape(cfg)}[/]");

            if (clean)
            {
                AnsiConsole.MarkupLine($"[grey]  Cleaning {Markup.Escape(cfg)} build outputs...[/]");
                foreach (ProjectTarget proj in selected)
                {
                    string fullPath = Path.Combine(repo, proj.Path);
                    if (File.Exists(fullPath))
                        RunProcess("dotnet", $"clean \"{fullPath}\" -c {cfg} -v {verbosity}", repo);
                }
            }

            int index = 0;
            foreach (ProjectTarget proj in selected)
            {
                index++;
                string fullPath = Path.Combine(repo, proj.Path);
                if (!File.Exists(fullPath))
                {
                    AnsiConsole.MarkupLine(
                        $"[grey][[{index}/{selected.Count}]] Skipping missing project: {Markup.Escape(proj.Path)}[/]");
                    continue;
                }

                AnsiConsole.Markup(
                    $"[cyan][[{index}/{selected.Count}]][/] Compiling [bold]{Markup.Escape(proj.Name)}[/] ({Markup.Escape(cfg)})... ");

                Stopwatch projSw = Stopwatch.StartNew();
                string restoreFlag = restore ? "--force" : "";
                string buildArgs = $"build \"{fullPath}\" -c {cfg} -v {verbosity} --nologo {restoreFlag}".Trim();

                string testLockScript = Path.Combine(repo, "scripts", "Invoke-KongorTestLock.ps1");
                int code = RunDotnetWithLock(testLockScript, buildArgs, repo);
                projSw.Stop();
                string dur = projSw.Elapsed.TotalSeconds.ToString("F1");

                if (code == 0)
                {
                    successCount++;
                    AnsiConsole.MarkupLine($"[bold green]\u2713 OK[/] [grey]({dur}s)[/]");
                }
                else
                {
                    failures.Add((proj.Name, cfg, proj.Path));
                    AnsiConsole.MarkupLine($"[bold red]\u2717 FAILED[/] [grey]({dur}s)[/]");
                }
            }
        }

        sw.Stop();
        AnsiConsole.WriteLine();
        AnsiConsole.MarkupLine(
            "[bold cyan]================================================================================[/]");
        AnsiConsole.MarkupLine(
            "[bold cyan]                       BUILD SUMMARY REPORT                                     [/]");
        AnsiConsole.MarkupLine(
            "[bold cyan]================================================================================[/]");
        AnsiConsole.MarkupLine($"Total Elapsed Time:   [bold white]{sw.Elapsed.TotalSeconds:F2} seconds[/]");
        AnsiConsole.MarkupLine($"Successful Builds:    [bold green]{successCount}[/]");
        AnsiConsole.MarkupLine(
            $"Failed Builds:        [bold {(failures.Count == 0 ? "green" : "red")}]{failures.Count}[/]");

        if (failures.Count > 0)
        {
            AnsiConsole.WriteLine();
            AnsiConsole.MarkupLine("[bold red]\u274C The following projects failed to build:[/]");
            foreach ((string Name, string Config, string Path) f in failures)
                AnsiConsole.MarkupLine(
                    $"  \u2022 [[{Markup.Escape(f.Config)}]] [bold]{Markup.Escape(f.Name)}[/] ({Markup.Escape(f.Path)})");
            return 1;
        }

        AnsiConsole.WriteLine();
        AnsiConsole.MarkupLine("[bold green]\u2728 ALL TARGETS COMPILED CLEANLY ACROSS ALL SPECIFIED CONFIGURATIONS! \u2728[/]");
        return 0;
    }

    internal static int StopComfyUI()
    {
        AnsiConsole.MarkupLine("[bold magenta]\U0001F6D1 Stopping ComfyUI Generative AI Server & MCP Server...[/]");
        return RunPythonScript("src/tools/comfyui/launch_comfyui.py", ["--stop"]);
    }

    internal static int StopBlenderProcesses()
    {
        AnsiConsole.MarkupLine("[bold cyan]\U0001F6D1 Stopping all running Blender instances...[/]");
        int count = 0;
        try
        {
            Process[] procs = Process.GetProcesses();
            foreach (Process p in procs)
                try
                {
                    if (p.ProcessName.Equals("blender", StringComparison.OrdinalIgnoreCase) ||
                        p.ProcessName.StartsWith("blender", StringComparison.OrdinalIgnoreCase))
                    {
                        AnsiConsole.MarkupLine(
                            $"[yellow]  Killing Blender process PID {p.Id} ({Markup.Escape(p.ProcessName)})...[/]");
                        p.Kill(true);
                        count++;
                    }
                }
                catch (Exception ex)
                {
                    AnsiConsole.MarkupLine($"[grey]  Could not terminate PID {p.Id}: {Markup.Escape(ex.Message)}[/]");
                }
        }
        catch (Exception ex)
        {
            AnsiConsole.MarkupLine($"[red]Error querying processes: {Markup.Escape(ex.Message)}[/]");
        }

        if (count > 0)
            AnsiConsole.MarkupLine($"[bold green]\u2713 Successfully stopped {count} Blender process(es).[/]");
        else
            AnsiConsole.MarkupLine("[grey]No active Blender processes found.[/]");
        return 0;
    }

    // StopDotNetProcessesAsync moved to Program.Helpers.Guardrails.cs























    // RunTests moved to Program.Helpers.Testing.cs (Phase 4 full parity with kongor.ps1)

    // RunClean moved to Program.Helpers.Guardrails.cs




































    internal static int ManageSecrets(string[] raw)
    {
        string repo = FindRepoRoot();
        const string vaultId = "00005072-6f6a-6563-7420-4b4f4e474f52";
        string secretProj = Path.Combine(repo, "src/services/aspire-host/ASPIRE.ApplicationHost.csproj");

        if (raw.Length == 0 || raw[0].Equals("list", StringComparison.OrdinalIgnoreCase))
        {
            AnsiConsole.MarkupLine($"[bold cyan]\U0001F512 Listing User Secrets for Vault [yellow]{vaultId}[/]...[/]");
            return RunProcess("dotnet", $"user-secrets list --project \"{secretProj}\" --id {vaultId}", repo);
        }

        string sub = raw[0].ToLowerInvariant();
        if (sub is "init")
        {
            AnsiConsole.MarkupLine($"[bold cyan]\U0001F512 Initializing User Secrets Vault [yellow]{vaultId}[/]...[/]");
            return RunProcess("dotnet", $"user-secrets init --project \"{secretProj}\" --id {vaultId}", repo);
        }

        if (sub is "set" && raw.Length >= 3)
        {
            string key = raw[1];
            string value = string.Join(" ", raw.Skip(2));
            AnsiConsole.MarkupLine(
                $"[bold cyan]\U0001F512 Setting Secret [green]{Markup.Escape(key)}[/] in Vault [yellow]{vaultId}[/]...[/]");
            return RunProcess("dotnet",
                $"user-secrets set \"{key}\" \"{value}\" --project \"{secretProj}\" --id {vaultId}", repo);
        }

        if (sub is "get" && raw.Length >= 2)
        {
            string key = raw[1];
            return RunProcess("dotnet", $"user-secrets get \"{key}\" --project \"{secretProj}\" --id {vaultId}", repo);
        }

        if (sub is "remove" or "delete" && raw.Length >= 2)
        {
            string key = raw[1];
            return RunProcess("dotnet", $"user-secrets remove \"{key}\" --project \"{secretProj}\" --id {vaultId}",
                repo);
        }

        if (sub is "clear")
            return RunProcess("dotnet", $"user-secrets clear --project \"{secretProj}\" --id {vaultId}", repo);

        // Default forward
        return RunProcess("dotnet", $"user-secrets {string.Join(" ", raw)} --project \"{secretProj}\" --id {vaultId}",
            repo);
    }

    internal static int ShowLogs(string service, bool tail, int lines)
    {
        string repo = FindRepoRoot();
        string lower = service.ToLowerInvariant();
        List<string> candidateDirs =
        [
            Path.Combine(repo, "src/services/master-server/logs"),
            Path.Combine(repo, "src/services/chat-server/logs"),
            Path.Combine(repo, "games/server/windows-x64"),
            Path.Combine(repo, "scratch"),
            Path.Combine(repo, "userdata/logs")
        ];

        List<FileInfo> logFiles = [];
        foreach (string dir in candidateDirs)
            if (Directory.Exists(dir))
                try
                {
                    IEnumerable<FileInfo> files = Directory.GetFiles(dir, "*.log", SearchOption.AllDirectories)
                        .Select(f => new FileInfo(f))
                        .Where(fi => fi.Length > 0);
                    logFiles.AddRange(files);
                }
                catch
                {
                }

        if (lower is not "all" and not "")
            logFiles =
            [
                .. logFiles.Where(fi => fi.Name.Contains(lower, StringComparison.OrdinalIgnoreCase) ||
                                        fi.FullName.Contains(lower, StringComparison.OrdinalIgnoreCase))
            ];

        if (logFiles.Count == 0)
        {
            AnsiConsole.MarkupLine($"[yellow]No log files found matching '{Markup.Escape(service)}'.[/]");
            return 0;
        }

        FileInfo latest = logFiles.OrderByDescending(f => f.LastWriteTimeUtc).First();
        AnsiConsole.MarkupLine(
            $"[bold cyan]\U0001F4DC Inspecting Log File:[/] [green]{Markup.Escape(latest.FullName)}[/] [grey]({latest.Length / 1024.0:F1} KB, Modified: {latest.LastWriteTime:yyyy-MM-dd HH:mm:ss})[/]");

        try
        {
            using FileStream fs = new(latest.FullName, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using StreamReader sr = new(fs);
            List<string> allLines = [];
            while (sr.ReadLine() is { } line) allLines.Add(line);

            int start = Math.Max(0, allLines.Count - lines);
            for (int i = start; i < allLines.Count; i++) AnsiConsole.WriteLine(allLines[i]);

            if (tail)
            {
                AnsiConsole.MarkupLine("\n[grey]--- Live Streaming Log Output (Press Ctrl+C to stop) ---[/]");
                long lastPos = fs.Position;
                while (true)
                {
#pragma warning disable RS0030 // Justification: Synchronous CLI tail polling loop
                    Task.Delay(500).GetAwaiter().GetResult();
#pragma warning restore RS0030
                    if (fs.Length > lastPos)
                    {
                        fs.Seek(lastPos, SeekOrigin.Begin);
                        while (sr.ReadLine() is { } newLine) AnsiConsole.WriteLine(newLine);
                        lastPos = fs.Position;
                    }
                }
            }
        }
        catch (Exception ex)
        {
            AnsiConsole.MarkupLine($"[red]Error reading log file: {Markup.Escape(ex.Message)}[/]");
            return 1;
        }

        return 0;
    }

    internal static int RefreshCli(bool noBuild = false)
    {
        KongorWorkspaceResult ws = CliRuntimeContext.RequireAnyWorkspace();
        if (ws.IsBundledInstall && ws.BundledLayout is { } layout)
        {
            return InstallBundledCli(layout);
        }

        string repo = ws.Root;
        AnsiConsole.MarkupLine($"[bold cyan]\U0001F504 Refreshing ProjectKongor Global CLI from '{Markup.Escape(repo)}'...[/]");

        string cliProj = Path.Combine(repo, "src", "tools", "kongor-cli", "Kongor.Cli.csproj");
        string shimProj = Path.Combine(repo, "src", "tools", "kongor-shim", "Kongor.Shim.csproj");
        string testLockScript = Path.Combine(repo, "scripts", "Invoke-KongorTestLock.ps1");

        string? shimBinDir = File.Exists(shimProj)
            ? Path.Combine(repo, "src", "tools", "kongor-shim", "bin", "Release", "net11.0")
            : null;

        if (!noBuild)
        {
            AnsiConsole.MarkupLine("[yellow]\u25B6 Building Kongor.Cli and Kongor.Shim (Release)...[/]");
            int cliBuildCode = RunDotnetWithLock(testLockScript, $"build \"{cliProj}\" -c Release --nologo", repo);
            if (cliBuildCode != 0)
            {
                AnsiConsole.MarkupLine($"[red]\u2717 Failed to build Kongor.Cli (exit {cliBuildCode}).[/]");
                return cliBuildCode;
            }

            if (File.Exists(shimProj))
            {
                int shimBuildCode = RunDotnetWithLock(testLockScript, $"build \"{shimProj}\" -c Release --nologo", repo);
                if (shimBuildCode != 0)
                {
                    AnsiConsole.MarkupLine($"[red]\u2717 Failed to build Kongor.Shim (exit {shimBuildCode}).[/]");
                    return shimBuildCode;
                }
            }
        }

        string cliBinDir = Path.Combine(repo, "src", "tools", "kongor-cli", "bin", "Release", "net11.0");
        AnsiConsole.MarkupLine("[yellow]\u25B6 Staging Host-RID filtered CLI slot & updating ~/.kongor/bin shim synchronously...[/]");

        CliInstallResult installResult = CliSelfInstaller.InstallFromSourceDirectory(
            cliBinSourceDir: cliBinDir,
            shimBinSourceDir: shimBinDir,
            workspaceRoot: repo,
            workspaceKind: KongorWorkspaceKind.SourceWorkspace,
            versionStamp: NEXUS.Contracts.Domain.NexusVersionInfo.PlatformVersion,
            logger: msg => Console.Error.WriteLine(msg));

        if (!installResult.Success)
        {
            AnsiConsole.MarkupLine($"[bold red]\u2717 Failed to install global Kongor CLI:[/] {Markup.Escape(installResult.ErrorMessage ?? "Unknown error")}");
            return 1;
        }

        foreach (string warn in installResult.Warnings)
        {
            AnsiConsole.MarkupLine($"[yellow]  \u26A0 {Markup.Escape(warn)}[/]");
        }

        ShellCompletionGenerator.TryRegenerateAll(AllPrimaryCommands);

        AnsiConsole.MarkupLine(
            $"[bold green]\u2713 Synchronously updated global 'kongor' CLI![/] " +
            $"[grey]({installResult.CopiedFilesCount} files, {installResult.CopiedBytes / (1024.0 * 1024.0):F1} MB, pruned {installResult.PrunedOldSlots} old slot(s))[/]");
        AnsiConsole.MarkupLine($"[grey]  Shim      :[/] [white]{Markup.Escape(installResult.ShimPath)}[/]");
        AnsiConsole.MarkupLine($"[grey]  Slot      :[/] [white]{Markup.Escape(installResult.ActiveSlotPath)}[/]");
        AnsiConsole.MarkupLine($"[grey]  Workspace :[/] [cyan]{Markup.Escape(repo)}[/] [grey](saved in ~/.kongor/config.json)[/]");
        return 0;
    }

    private static int RunDotnetWithLock(string testLockScript, string dotnetArgs, string repo)
    {
        if (File.Exists(testLockScript))
        {
            string shellExe = OperatingSystem.IsWindows() ? "powershell.exe" : "pwsh";
            return RunProcess(
                shellExe,
                $"-NoProfile -ExecutionPolicy Bypass -File \"{testLockScript}\" -Owner \"cli-refresh-{Environment.ProcessId}\" -- dotnet {dotnetArgs}",
                repo);
        }

        return RunProcess("dotnet", dotnetArgs, repo);
    }

    internal static async Task<int> StopDockerStackAsync()
    {
        string repo = FindRepoRoot();
        AnsiConsole.MarkupLine("[yellow]\u25B6 Stopping ProjectKongor Docker Compose stack ('kongor-stack')...[/]");
        var (exitCode, _) = await Kongor.Cli.Mcp.Services.DockerStackProbe.RunStackScriptAsync(
            "down",
            repoRoot: repo,
            onLine: (line, isErr) =>
            {
                if (isErr) AnsiConsole.MarkupLine($"[red]{Markup.Escape(line)}[/]");
                else AnsiConsole.MarkupLine($"[grey]{Markup.Escape(line)}[/]");
            });
        return exitCode;
    }

    public static int InstallBundledCli(
        Kongor.Core.Configuration.BundledLayout layout,
        string? userHomeOverride = null,
        bool? isWindowsOverride = null)
    {
        bool isWindows = isWindowsOverride ?? OperatingSystem.IsWindows();
        string userHome = !string.IsNullOrWhiteSpace(userHomeOverride)
            ? userHomeOverride
            : Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

        AnsiConsole.MarkupLine(
            $"[bold cyan][[BundledLayout]] Installing standalone 'kongor' CLI shim from {Markup.Escape(layout.Root)}...[/]");

        try
        {
            if (isWindows)
            {
                string localAppData = !string.IsNullOrWhiteSpace(userHomeOverride)
                    ? Path.Combine(userHomeOverride, "AppData", "Local")
                    : Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
                string binDir = Path.Combine(localAppData, "ProjectKongor", "bin");
                Directory.CreateDirectory(binDir);

                string shimPath = Path.Combine(binDir, "kongor.cmd");
                string shimContent =
                    "@echo off\r\n" +
                    $"set \"KONGOR_BUNDLE_ROOT={layout.Root}\"\r\n" +
                    $"\"{layout.KongorCliExe}\" %*\r\n";
                File.WriteAllText(shimPath, shimContent, new System.Text.UTF8Encoding(false));

                if (string.IsNullOrWhiteSpace(userHomeOverride) && OperatingSystem.IsWindows())
                {
                    string currentUserPath = Environment.GetEnvironmentVariable("Path", EnvironmentVariableTarget.User) ?? string.Empty;
                    bool alreadyOnPath = currentUserPath
                        .Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                        .Any(entry => string.Equals(
                            Path.TrimEndingDirectorySeparator(entry),
                            Path.TrimEndingDirectorySeparator(binDir),
                            StringComparison.OrdinalIgnoreCase));
                    if (!alreadyOnPath)
                    {
                        string updatedPath = string.IsNullOrWhiteSpace(currentUserPath)
                            ? binDir
                            : $"{currentUserPath.TrimEnd(';')};{binDir}";
                        Environment.SetEnvironmentVariable("Path", updatedPath, EnvironmentVariableTarget.User);
                    }
                }

                AnsiConsole.MarkupLine($"[bold green]\u2713 [[BundledLayout]] Installed 'kongor' shim to:[/] [white]{Markup.Escape(shimPath)}[/]");
            }
            else
            {
                EnsureBundledPosixExecutablesMarked(layout);

                string localBinDir = Path.Combine(userHome, ".local", "bin");
                Directory.CreateDirectory(localBinDir);

                string escapedRoot = layout.Root.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("$", "\\$").Replace("`", "\\`");
                string escapedCli = layout.KongorCliExe.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("$", "\\$").Replace("`", "\\`");
                string shimPath = Path.Combine(localBinDir, "kongor");
                string tempShimPath = shimPath + ".tmp";
                string posixShim =
                    "#!/usr/bin/env sh\n" +
                    "# Auto-generated by Project Kongor Standalone CLI Installer\n" +
                    $"export KONGOR_BUNDLE_ROOT=\"{escapedRoot}\"\n" +
                    $"exec \"{escapedCli}\" \"$@\"\n";

                File.WriteAllText(tempShimPath, posixShim, new System.Text.UTF8Encoding(false));
                TrySetPosixExecutableMode(tempShimPath);
                File.Move(tempShimPath, shimPath, overwrite: true);
                TrySetPosixExecutableMode(shimPath);

                EnsurePosixShellPathProfiles(userHome);

                AnsiConsole.MarkupLine($"[bold green]\u2713 [[BundledLayout]] Installed 'kongor' shim to:[/] [white]{Markup.Escape(shimPath)}[/]");
                string activePath = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
                bool inCurrentPath = activePath
                    .Split(':', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                    .Any(p => string.Equals(Path.TrimEndingDirectorySeparator(p), Path.TrimEndingDirectorySeparator(localBinDir), StringComparison.Ordinal));
                if (!inCurrentPath)
                {
                    AnsiConsole.MarkupLine("[grey]  Run in current shell to activate immediately:[/] [yellow]export PATH=\"$HOME/.local/bin:$PATH\"[/]");
                }
            }

            KongorUserConfigStore.RegisterOrUpdateWorkspace(
                layout.Root,
                KongorWorkspaceKind.BundledInstall,
                setActive: true,
                userHomeOverride: userHomeOverride,
                diagnosticLogger: msg => Console.Error.WriteLine(msg));
            ShellCompletionGenerator.TryRegenerateAll(AllPrimaryCommands, userHomeOverride);

            AntigravityRegistrar.WriteBundleMcpConfigFiles(layout);
            string? targetSchemaDir = !string.IsNullOrWhiteSpace(userHomeOverride)
                ? Path.Combine(userHomeOverride, ".gemini", "antigravity", "mcp", "kongor")
                : null;
            McpToolRegistry registry = new(registerDefaults: true, bundleProbeDirectory: layout.Root);
            AntigravityRegistrar.Register(targetSchemaDir, registry);
            if (!string.IsNullOrWhiteSpace(userHomeOverride))
            {
                AntigravityRegistrar.UpsertAgentMcpServerConfigs(layout.KongorCliExe, ["mcp"], layout.Root, userHomeOverride);
            }

            AnsiConsole.MarkupLine($"[bold green]\u2713 [[BundledLayout]] Synced TAO MCP config (.mcp.json) at:[/] [white]{Markup.Escape(Path.Combine(layout.Root, ".mcp.json"))}[/]");
            return 0;
        }
        catch (Exception ex)
        {
            AnsiConsole.MarkupLine($"[bold red]\u2717 [[BundledLayout]] Failed to install standalone CLI shim:[/] {Markup.Escape(ex.Message)}");
            return 1;
        }
    }

    private static void EnsureBundledPosixExecutablesMarked(Kongor.Core.Configuration.BundledLayout layout)
    {
        string serverP2pDir = Path.Combine(layout.Root, "games", "server_p2p");
        string[] candidateExecutables =
        [
            layout.KongorCliExe,
            layout.LauncherExe,
            layout.ControlPanelExe,
            layout.CompelExe,
            layout.CompelGuiExe,
            Path.Combine(layout.Root, "WILLOWMAKER"),
            Path.Combine(layout.Root, "macos-self-sign.command"),
            Path.Combine(layout.ClientDir, "linux-x64", "hon-x86_64"),
            Path.Combine(layout.ClientDir, "linux-x64", "hon.sh"),
            Path.Combine(layout.ClientDir, "macos-x64", "HoN64"),
            Path.Combine(layout.DedicatedServerDir, "linux-x64", "hon-x86_64-server"),
            Path.Combine(layout.DedicatedServerDir, "linux-x64", "hon-x86_64-server-wrapper.sh"),
            Path.Combine(serverP2pDir, "linux-x64", "hon-x86_64-server"),
            Path.Combine(serverP2pDir, "linux-x64", "hon-x86_64-server-wrapper.sh")
        ];

        foreach (string exePath in candidateExecutables)
        {
            if (File.Exists(exePath))
            {
                TrySetPosixExecutableMode(exePath);
            }
        }
    }

    private static void TrySetPosixExecutableMode(string path)
    {
        if (OperatingSystem.IsWindows()) return;
        try
        {
            UnixFileMode mode = File.GetUnixFileMode(path);
            mode |= UnixFileMode.UserRead | UnixFileMode.UserExecute | UnixFileMode.GroupExecute | UnixFileMode.OtherExecute;
            File.SetUnixFileMode(path, mode);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[BundledCli] chmod +x skipped for '{path}': {ex.Message}");
        }
    }

    private static void EnsurePosixShellPathProfiles(string userHome)
    {
        const string beginMarker = "# >>> project-kongor cli >>>";
        const string endMarker = "# <<< project-kongor cli <<<";
        const string exportBlock =
            "\n# >>> project-kongor cli >>>\n" +
            "case \":$PATH:\" in\n" +
            "  *\":$HOME/.local/bin:\"*) ;;\n" +
            "  *) export PATH=\"$HOME/.local/bin:$PATH\" ;;\n" +
            "esac\n" +
            "# <<< project-kongor cli <<<\n";

        string[] rcCandidates =
        [
            Path.Combine(userHome, ".bashrc"),
            Path.Combine(userHome, ".zshrc"),
            Path.Combine(userHome, ".profile")
        ];

        bool updatedAny = false;
        foreach (string rcPath in rcCandidates)
        {
            if (!File.Exists(rcPath)) continue;
            string existing = File.ReadAllText(rcPath);
            if (!existing.Contains(beginMarker, StringComparison.Ordinal) &&
                !existing.Contains(".local/bin", StringComparison.Ordinal))
            {
                File.WriteAllText(rcPath, existing.TrimEnd('\r', '\n') + exportBlock, new System.Text.UTF8Encoding(false));
            }
            updatedAny = true;
        }

        if (!updatedAny)
        {
            string profilePath = Path.Combine(userHome, ".profile");
            File.WriteAllText(profilePath, exportBlock.TrimStart('\n'), new System.Text.UTF8Encoding(false));
        }

        string fishDir = Path.Combine(userHome, ".config", "fish");
        if (Directory.Exists(fishDir))
        {
            string fishConfDir = Path.Combine(fishDir, "conf.d");
            Directory.CreateDirectory(fishConfDir);
            string fishFile = Path.Combine(fishConfDir, "kongor.fish");
            if (!File.Exists(fishFile))
            {
                File.WriteAllText(
                    fishFile,
                    $"{beginMarker}\nfish_add_path -g \"$HOME/.local/bin\"\n{endMarker}\n",
                    new System.Text.UTF8Encoding(false));
            }
        }
    }

    internal sealed record ProjectTarget(string Name, string Path, string Category);
}
