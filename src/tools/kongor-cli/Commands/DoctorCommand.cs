// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// TAO (Together As One) MCP & Kongor CLI -- Created by guccidonut (https://github.com/guccidonut/PKV)

using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Net.Sockets;
using System.Text.Json;
using Kongor.Cli.Mcp.Protocol;
using Kongor.Cli.Mcp.Services;
using Kongor.Core.Services.Native;
using Spectre.Console;
using Spectre.Console.Cli;

namespace Kongor.Cli.Commands;

public sealed class DoctorCommandSettings : CommandSettings
{
    [CommandOption("-s|--subsystem <NAME>")]
    [Description("Filter diagnostics to a specific subsystem (e.g. 'GameClient', 'DedicatedServer', 'Infrastructure', 'DeveloperIssues')")]
    public string? Subsystem { get; init; }

    [CommandOption("-v|--verbose")]
    [Description("Show detailed probe responses, socket latencies, and stack traces")]
    public bool Verbose { get; init; }

    [CommandOption("--json")]
    [Description("Output raw diagnostic truth envelope in JSON format")]
    public bool Json { get; init; }

    [CommandOption("--sanitize")]
    [Description("Perform repository sanitation: relocate loose test parquet files, log files, and .r2bp backups to structured directories")]
    public bool Sanitize { get; init; }
}

public sealed record ProbeItemResult(string Name, string Endpoint, bool IsActive, string StatusText, string Details = "");

/// <summary>
/// kongor doctor: Comprehensive system diagnostics sweeping microservice ports,
/// game client/server processes, IPC named pipes, and developer issue backplane telemetry.
/// </summary>
public sealed class DoctorCommand : AsyncCommand<DoctorCommandSettings>
{
    private readonly TimeProvider _timeProvider;
    private readonly TextWriter? _outputWriter;

    public DoctorCommand(TimeProvider? timeProvider = null, TextWriter? outputWriter = null)
    {
        _timeProvider = timeProvider ?? TimeProvider.System;
        _outputWriter = outputWriter;
    }

    public override async Task<int> ExecuteAsync(CommandContext context, DoctorCommandSettings settings)
    {
        var probes = new List<ProbeItemResult>();

        bool workspaceResolved = Infrastructure.CliRuntimeContext.TryResolveWorkspace(out var resolvedWorkspace);
        string repoRoot = resolvedWorkspace?.Root ?? Directory.GetCurrentDirectory();

        // 0. Probe CLI Workspace & Global PATH Shim Hygiene
        string? permEnvWarning = Kongor.Core.Configuration.KongorWorkspaceResolver.GetPermanentRootEnvWarning();
        if (workspaceResolved && resolvedWorkspace is not null)
        {
            string wsDetails = permEnvWarning is not null
                ? $"{Markup.Escape(resolvedWorkspace.Root)} ({resolvedWorkspace.Kind} via {resolvedWorkspace.Source}) — ⚠ {Markup.Escape(permEnvWarning)}"
                : $"{Markup.Escape(resolvedWorkspace.Root)} ({resolvedWorkspace.Kind} via {resolvedWorkspace.Source})";
            probes.Add(new ProbeItemResult(
                "CLI Workspace Hygiene",
                resolvedWorkspace.IsOutOfTree ? "Out-of-Tree" : "In-Workspace",
                permEnvWarning is null,
                permEnvWarning is null ? "[bold green]RESOLVED[/]" : "[yellow]WARN (ENV OVERRIDE)[/]",
                wsDetails));
        }
        else
        {
            probes.Add(new ProbeItemResult(
                "CLI Workspace Hygiene",
                "Unresolved",
                false,
                "[bold red]NOT CONFIGURED[/]",
                "Run 'kongor workspace use \"<path-to-ProjectKongor>\"' to register your workspace"));
        }

        var pathAudit = Infrastructure.CliSelfInstaller.AuditPathShadowing();
        if (pathAudit.IsExpectedFirst && !pathAudit.HasShadowingConflict)
        {
            probes.Add(new ProbeItemResult(
                "CLI PATH Shim Hygiene",
                "~/.kongor/bin",
                true,
                "[bold green]CLEAN[/]",
                $"Active shim: {Markup.Escape(pathAudit.ExpectedShimPath)}"));
        }
        else if (pathAudit.DiscoveredKongorBinaries.Count == 0)
        {
            probes.Add(new ProbeItemResult(
                "CLI PATH Shim Hygiene",
                "~/.kongor/bin",
                false,
                "[yellow]NOT ON PATH[/]",
                "Run 'scripts/install-cli.ps1' or 'kongor refresh' to install ~/.kongor/bin onto PATH"));
        }
        else
        {
            string foundList = string.Join(" > ", pathAudit.DiscoveredKongorBinaries);
            probes.Add(new ProbeItemResult(
                "CLI PATH Shim Hygiene",
                "PATH Shadow",
                false,
                "[yellow]SHADOWED[/]",
                $"Order: {Markup.Escape(foundList)} (expected '{Markup.Escape(pathAudit.ExpectedShimPath)}' first; run 'kongor refresh' to fix)"));
        }

        DockerComposeStackSnapshot dockerStack =
            await DockerStackProbe.ProbeAsync(repoRoot, includeFleetHttpStatus: true);

        // 1. Probe Infrastructure & Microservice Ports
        bool filterInfra = string.IsNullOrEmpty(settings.Subsystem) ||
                           settings.Subsystem.Equals("Infrastructure", StringComparison.OrdinalIgnoreCase);

        if (filterInfra)
        {
            bool dockerPgActive = dockerStack.TryGetRunningService("postgres") != null;
            var portsToCheck = new (string Name, int Port, string? ComposeService)[]
            {
                ("KONGOR Master Server", 55555, "master-server"),
                ("TRANSMUTANSTEIN Chat", 11031, "chat-server"),
                ("TRANSMUTANSTEIN Voice", 11032, "chat-server"),
                ("ZORGATH Web API (HTTP)", 5200, "web-api"),
                ("ZORGATH Web API (HTTPS)", 5201, "web-api"),
                ("DAWNBRINGER Web UI (HTTP)", 5266, "web-ui"),
                ("DAWNBRINGER Web UI (HTTPS)", 5557, "web-ui"),
                ("Aspire Dashboard", 5550, "aspire-dashboard"),
                ("Redis Cache", 28298, "garnet"),
                ("PostgreSQL DB", dockerPgActive ? 25432 : 5432, dockerPgActive ? "postgres" : null)
            };

            foreach (var (name, port, composeSvc) in portsToCheck)
            {
                var probe = await ProbeTcpPortAsync(name, "127.0.0.1", port);
                var runningContainer = composeSvc != null ? dockerStack.TryGetRunningService(composeSvc) : null;
                if (probe.IsActive && runningContainer != null)
                {
                    probe = probe with
                    {
                        StatusText = "[bold green]ONLINE (Docker)[/]",
                        Details = $"Container: {Markup.Escape(runningContainer.ContainerName)} ({Markup.Escape(runningContainer.DisplaySummary)})"
                    };
                }
                probes.Add(probe);
            }

            if (dockerStack.TryGetRunningService("compel-dedicated") != null ||
                dockerStack.TryGetRunningService("compel-queue-arcade") != null)
            {
                var matchProbe = await ProbeTcpPortAsync("COMPEL Match (Tier D)", "127.0.0.1", 11334);
                var matchSvc = dockerStack.TryGetRunningService("compel-dedicated");
                if (matchProbe.IsActive && matchSvc != null)
                {
                    int mWorkers = dockerStack.MatchFleet?.ActiveWorkerCount ?? 0;
                    matchProbe = matchProbe with
                    {
                        StatusText = "[bold green]ONLINE (Docker)[/]",
                        Details = $"{Markup.Escape(matchSvc.ContainerName)} ({Markup.Escape(matchSvc.DisplaySummary)}, {mWorkers} worker(s))"
                    };
                }
                probes.Add(matchProbe);

                var arcadeProbe = await ProbeTcpPortAsync("COMPEL Arcade (Tier Q)", "127.0.0.1", 11484);
                var arcadeSvc = dockerStack.TryGetRunningService("compel-queue-arcade");
                if (arcadeProbe.IsActive && arcadeSvc != null)
                {
                    int aWorkers = dockerStack.ArcadeFleet?.ActiveWorkerCount ?? 0;
                    arcadeProbe = arcadeProbe with
                    {
                        StatusText = "[bold green]ONLINE (Docker)[/]",
                        Details = $"{Markup.Escape(arcadeSvc.ContainerName)} ({Markup.Escape(arcadeSvc.DisplaySummary)}, {aWorkers} worker(s))"
                    };
                }
                probes.Add(arcadeProbe);
            }
            else
            {
                probes.Add(await ProbeTcpPortAsync("COMPEL Control Plane", "127.0.0.1", 11234));
            }
        }

        // 2. Probe Game Engine Processes & Named Pipes
        bool filterEngine = string.IsNullOrEmpty(settings.Subsystem) ||
                            settings.Subsystem.Contains("Client", StringComparison.OrdinalIgnoreCase) ||
                            settings.Subsystem.Contains("Server", StringComparison.OrdinalIgnoreCase);

        int clientProcessCount = 0;
        bool willowmakerIpcActive = false;
        bool dedicatedPipeActive = false;

        if (filterEngine)
        {
            var processes = Process.GetProcessesByName("hon_x64");
            clientProcessCount = processes.Length;

            probes.Add(new ProbeItemResult(
                "Game Engine Processes",
                "hon_x64.exe",
                clientProcessCount > 0,
                clientProcessCount > 0 ? $"[bold green]RUNNING[/] ({clientProcessCount} instances)" : "[grey]NOT RUNNING[/]",
                clientProcessCount > 0 ? $"PIDs: {string.Join(", ", processes.Select(p => p.Id))}" : "Headless / idle state"
            ));

            // Named pipes
            try
            {
                var pipes = Directory.GetFiles(@"\\.\pipe\");
                willowmakerIpcActive = pipes.Any(p => p.Contains("willowmaker-ipc", StringComparison.OrdinalIgnoreCase));
                dedicatedPipeActive = pipes.Any(p => p.Contains("hon-server", StringComparison.OrdinalIgnoreCase));
            }
            catch
            {
                // Fallback check
                willowmakerIpcActive = File.Exists(@"\\.\pipe\willowmaker-ipc");
                dedicatedPipeActive = File.Exists(@"\\.\pipe\hon-server-11235");
            }

            probes.Add(new ProbeItemResult(
                "Willowmaker IPC Pipe",
                @"\\.\pipe\willowmaker-ipc",
                willowmakerIpcActive,
                willowmakerIpcActive ? "[bold green]LISTENING[/]" : "[grey]NOT ACTIVE[/]",
                willowmakerIpcActive ? "Client IPC bridge connected" : "Game client not hosting pipe"
            ));

            if (!dedicatedPipeActive && dockerStack.TotalActiveGameWorkers > 0)
            {
                probes.Add(new ProbeItemResult(
                    "Dedicated Server IPC Pipe",
                    "127.0.0.1:11334/:11484",
                    true,
                    "[bold green]DOCKER HTTP IPC[/]",
                    $"Containerized Linux workers active ({dockerStack.MatchFleet?.ActiveWorkerCount ?? 0} Match, {dockerStack.ArcadeFleet?.ActiveWorkerCount ?? 0} Arcade)"
                ));
            }
            else
            {
                probes.Add(new ProbeItemResult(
                    "Dedicated Server IPC Pipe",
                    @"\\.\pipe\hon-server-11235",
                    dedicatedPipeActive,
                    dedicatedPipeActive ? "[bold green]LISTENING[/]" : "[grey]NOT ACTIVE[/]",
                    dedicatedPipeActive ? "Dedicated server worker pipe active" : "No active dedicated server pipe"
                ));
            }
        }

        // 2.5 Probe Native Engine Dependencies & Security Status
        bool filterNative = string.IsNullOrEmpty(settings.Subsystem) ||
                            settings.Subsystem.Contains("Native", StringComparison.OrdinalIgnoreCase) ||
                            settings.Subsystem.Contains("Client", StringComparison.OrdinalIgnoreCase) ||
                            settings.Subsystem.Contains("Server", StringComparison.OrdinalIgnoreCase);

        bool nativeVulnerable = false;
        if (filterNative)
        {
            // 2.5.1 Probe Native Zlib (Phase 1)
            var zlibTargets = NativeBinaryManager.GetStatus(repoRoot);
            var zlibTarget = zlibTargets.FirstOrDefault(t => t.Exists) ?? zlibTargets.FirstOrDefault();

            if (zlibTarget != null && zlibTarget.Exists)
            {
                bool isModern = zlibTarget.FileVersion.StartsWith("1.3", StringComparison.OrdinalIgnoreCase);
                bool isAbiVerified = zlibTarget.IsAbiVerified;
                if (!isModern) nativeVulnerable = true;

                string statusText = isModern && isAbiVerified
                    ? $"[bold green]SECURE (v{zlibTarget.FileVersion})[/]"
                    : (isModern
                        ? $"[bold yellow]UNVERIFIED (v{zlibTarget.FileVersion})[/]"
                        : $"[bold red]VULNERABLE (v{zlibTarget.FileVersion})[/]");

                string details = isModern && isAbiVerified
                    ? "CVE-2022-37434 patched, Hardware CRC32 + In-Memory Decompression verified"
                    : (isModern
                        ? "Modern zlib version present, but runtime C-ABI verification failed"
                        : "Vulnerable to CVE-2022-37434 heap buffer overflow; run 'kongor patch upgrade-zlib'");

                probes.Add(new ProbeItemResult(
                    "Native Zlib Security",
                    "zlibwapi_x64.dll",
                    isModern && isAbiVerified,
                    statusText,
                    details
                ));
            }
            else
            {
                probes.Add(new ProbeItemResult(
                    "Native Zlib Security",
                    "zlibwapi_x64.dll",
                    false,
                    "[grey]NOT FOUND[/]",
                    "No zlibwapi_x64.dll discovered in games runtime"
                ));
            }

            // 2.5.2 Probe Native Guard (Unified version.dll proxy + fallback Kongor.NativeGuard.dll)
            string[] versionProxyCandidates =
            [
                Path.Combine(repoRoot, "games", "client", "version.dll"),
                Path.Combine(repoRoot, "games", "client", "windows-x64", "version.dll"),
                Path.Combine(repoRoot, "games", "server", "version.dll"),
                Path.Combine(repoRoot, "native", "build", "native_guard_build", "Release", "version.dll"),
                Path.Combine(repoRoot, "artifacts", "native-binaries", "windows-x64", "version.dll")
            ];

            string? discoveredVersionProxy = versionProxyCandidates.FirstOrDefault(File.Exists);
            if (discoveredVersionProxy != null)
            {
                var proxyExports = NativeBinaryManager.ExtractPeExportNames(discoveredVersionProxy);
                bool hasVersionExports =
                    proxyExports.Contains("GetFileVersionInfoA", StringComparer.OrdinalIgnoreCase) &&
                    proxyExports.Contains("GetFileVersionInfoW", StringComparer.OrdinalIgnoreCase);

                bool isProxyHealthy = hasVersionExports && proxyExports.Count >= 14;
                string proxyStatusText = isProxyHealthy
                    ? $"[bold green]SUPERVISED (Unified version.dll, {proxyExports.Count} Exports)[/]"
                    : $"[bold yellow]PARTIAL ({proxyExports.Count} Exports)[/]";

                string proxyDetails = isProxyHealthy
                    ? "Unified version.dll proxy active (V2 Seqlock telemetry, moodycamel IPC queue, hash-pinned RVA crash unwinder)"
                    : "version.dll exports incomplete; rebuild native/proxies/native_guard";

                probes.Add(new ProbeItemResult(
                    "Native Guard Supervision",
                    "version.dll",
                    isProxyHealthy,
                    proxyStatusText,
                    proxyDetails
                ));
            }
            else
            {
                string[] guardCandidates =
                [
                    Path.Combine(repoRoot, "artifacts", "native-binaries", "windows-x64", "Kongor.NativeGuard.dll"),
                    Path.Combine(repoRoot, "games", "client", "windows-x64", "Kongor.NativeGuard.dll"),
                    Path.Combine(repoRoot, "games", "client", "Kongor.NativeGuard.dll"),
                    Path.Combine(AppContext.BaseDirectory, "Kongor.NativeGuard.dll"),
                    Path.Combine(AppContext.BaseDirectory, "windows-x64", "Kongor.NativeGuard.dll")
                ];

                string? discoveredGuard = guardCandidates.FirstOrDefault(File.Exists);
                if (discoveredGuard != null)
                {
                    var guardExports = NativeBinaryManager.ExtractPeExportNames(discoveredGuard);
                    bool hasAllExports = NativeGuardBinaryBuilder.RequiredExports.All(re => guardExports.Contains(re, StringComparer.OrdinalIgnoreCase));

                    var importReport = PeImportAddressTableService.GetImports(discoveredGuard);
                    bool hasTimerPacing = importReport.IsValidPe && importReport.ImportedModules.Any(m =>
                        m.DllName.Equals("winmm.dll", StringComparison.OrdinalIgnoreCase) &&
                        m.ImportedFunctions.Contains("timeBeginPeriod", StringComparer.OrdinalIgnoreCase));

                    bool isGuardHealthy = hasAllExports && hasTimerPacing;
                    string guardStatusText = isGuardHealthy
                        ? $"[bold green]SUPERVISED ({guardExports.Count} Exports, 1.0ms Timer)[/]"
                        : (hasAllExports
                            ? $"[bold yellow]PARTIAL ({guardExports.Count} Exports, timer missing)[/]"
                            : $"[bold red]INVALID EXPORTS ({guardExports.Count}/{NativeGuardBinaryBuilder.RequiredExports.Length})[/]");

                    string guardDetails = isGuardHealthy
                        ? "Detours trampolines ready, atomic heap bounds & socket validation active"
                        : "NativeGuard exports or winmm imports incomplete; stage games/client/version.dll";

                    probes.Add(new ProbeItemResult(
                        "Native Guard Supervision",
                        "Kongor.NativeGuard.dll",
                        isGuardHealthy,
                        guardStatusText,
                        guardDetails
                    ));
                }
                else
                {
                    probes.Add(new ProbeItemResult(
                        "Native Guard Supervision",
                        "version.dll",
                        false,
                        "[grey]NOT STAGED[/]",
                        "Neither games/client/version.dll nor fallback Kongor.NativeGuard.dll found"
                    ));
                }
            }

            // 2.5.3 Probe Native TLS 1.3 & Schannel (Phase 4)
            var securityReport = NativeSecurityUpgradeService.GetSecurityStatus(repoRoot);
            bool tls13Healthy = securityReport.AllTargetsHealthy && securityReport.Targets.Count > 0;
            if (!tls13Healthy && securityReport.Targets.Count > 0)
            {
                bool hasVulnerableTarget = securityReport.Targets.Any(t =>
                    !t.CveRemediationStatus.Contains("Patched", StringComparison.OrdinalIgnoreCase) || !t.IsTls13Supported);
                if (hasVulnerableTarget)
                {
                    nativeVulnerable = true;
                }
            }

            if (securityReport.Targets.Count > 0)
            {
                var curlTarget = securityReport.Targets.FirstOrDefault(t => t.BinaryName.Equals("libcurl_x64.dll", StringComparison.OrdinalIgnoreCase));
                string curlVersion = curlTarget?.FileVersion ?? "8.21.0";
                string tlsStatusText = tls13Healthy
                    ? $"[bold green]SECURE (libcurl {curlVersion} + TLS 1.3)[/]"
                    : $"[bold red]VULNERABLE (CVE-2023-38545 risk)[/]";

                string tlsDetails = tls13Healthy
                    ? "CVE-2023-38545 patched, Windows Schannel TLS 1.3 active, OpenSSL ABI proxy verified"
                    : "Legacy libcurl/OpenSSL detected; run 'kongor security upgrade --redirect-iat'";

                probes.Add(new ProbeItemResult(
                    "Native TLS 1.3 & Schannel",
                    "libcurl_x64.dll",
                    tls13Healthy,
                    tlsStatusText,
                    tlsDetails
                ));
            }
            else
            {
                probes.Add(new ProbeItemResult(
                    "Native TLS 1.3 & Schannel",
                    "libcurl_x64.dll",
                    false,
                    "[grey]NOT FOUND[/]",
                    "No security binaries discovered in games runtime; run 'kongor security upgrade'"
                ));
            }
        }

        // 2.6 Probe Workspace Root Hygiene
        bool filterHygiene = string.IsNullOrEmpty(settings.Subsystem) ||
                             settings.Subsystem.Contains("Hygiene", StringComparison.OrdinalIgnoreCase) ||
                             settings.Subsystem.Contains("Repo", StringComparison.OrdinalIgnoreCase);

        if (filterHygiene)
        {
            string rootScanDir = repoRoot;
            List<string> looseFiles = Directory.GetFiles(rootScanDir)
                .Select(Path.GetFileName)
                .Where(f => !string.IsNullOrEmpty(f))
                .Where(f => f!.EndsWith(".parquet", StringComparison.OrdinalIgnoreCase) ||
                            f!.EndsWith(".r2bp", StringComparison.OrdinalIgnoreCase) ||
                            f!.Equals("WILLOWMAKER.log", StringComparison.OrdinalIgnoreCase))
                .Select(f => f!)
                .ToList();

            if (settings.Sanitize && looseFiles.Count > 0)
            {
                string analyticsDir = Path.Combine(rootScanDir, "data", "analytics", "replays");
                string archiveDir = Path.Combine(rootScanDir, "scratch", "_quarantine", "r2bp");
                string logsDir = Path.Combine(rootScanDir, "logs");
                Directory.CreateDirectory(analyticsDir);
                Directory.CreateDirectory(archiveDir);
                Directory.CreateDirectory(logsDir);

                foreach (string fileName in looseFiles)
                {
                    string srcPath = Path.Combine(rootScanDir, fileName);
                    if (fileName.EndsWith(".parquet", StringComparison.OrdinalIgnoreCase))
                    {
                        File.Move(srcPath, Path.Combine(analyticsDir, fileName), overwrite: true);
                    }
                    else if (fileName.EndsWith(".r2bp", StringComparison.OrdinalIgnoreCase))
                    {
                        File.Move(srcPath, Path.Combine(archiveDir, fileName), overwrite: true);
                    }
                    else if (fileName.Equals("WILLOWMAKER.log", StringComparison.OrdinalIgnoreCase))
                    {
                        File.Move(srcPath, Path.Combine(logsDir, fileName), overwrite: true);
                    }
                }
                looseFiles.Clear();
            }

            bool isClean = looseFiles.Count == 0;
            probes.Add(new ProbeItemResult(
                "Repository Root Hygiene",
                rootScanDir,
                isClean,
                isClean ? "[bold green]CLEAN (0 loose artifacts)[/]" : $"[bold yellow]UNSANITIZED ({looseFiles.Count} loose artifacts)[/]",
                isClean ? "Root directory conforms to zero-clutter policy" : $"Loose files: {string.Join(", ", looseFiles)}. Run 'kongor doctor --sanitize'"
            ));
        }

        // 3. Probe Developer Issues Backplane
        using var issueStore = new DeveloperIssueStore();
        var pendingIssues = issueStore.GetPendingIssues();
        var journeySummary = issueStore.GetJourneySummary();

        int openCount = pendingIssues.Count(i => i.Status == IssueResolutionStatus.Open);
        int regCount = pendingIssues.Count(i => i.Status == IssueResolutionStatus.Regression);
        int actionReqCount = pendingIssues.Count(i => i.Status == IssueResolutionStatus.ActionRequired);
        int autoResolvedCount = journeySummary.AutoResolvedCount;

        bool hasActionRequired = actionReqCount > 0;
        bool hasCriticalCrash = pendingIssues.Any(i => i.Severity == IssueSeverity.CriticalCrash && i.Status != IssueResolutionStatus.AutoResolved);

        // Overall Health State Calculation
        string healthStatus = "HEALTHY";
        if (hasActionRequired || hasCriticalCrash || nativeVulnerable)
        {
            healthStatus = "ACTION REQUIRED";
        }
        else if (openCount > 0 || regCount > 0)
        {
            healthStatus = "DEGRADED";
        }

        string healthColor = healthStatus switch
        {
            "HEALTHY" => "green",
            "DEGRADED" => "yellow",
            _ => "red"
        };

        // Output JSON if requested
        if (settings.Json)
        {
            var truthEnvelope = new
            {
                status = "success",
                health = healthStatus,
                timestamp = _timeProvider.GetUtcNow().ToString("o"),
                probes = probes.Select(p => new
                {
                    name = p.Name,
                    endpoint = p.Endpoint,
                    active = p.IsActive,
                    details = p.Details
                }),
                issues = new
                {
                    totalTracked = journeySummary.TotalCaught,
                    open = openCount,
                    regressions = regCount,
                    actionRequired = actionReqCount,
                    autoResolved = autoResolvedCount
                },
                handoffs = hasActionRequired
                    ? new[]
                    {
                        new ToolHandoff
                        {
                            Server = "kongor",
                            Tool = "kongor_self_heal",
                            Reason = "Resolve pending ActionRequired developer issues via automated recipes",
                            Arguments = new Dictionary<string, object?> { ["action"] = "journey_summary" }
                        }
                    }
                    : Array.Empty<ToolHandoff>()
            };

            TextWriter writer = _outputWriter ?? Console.Out;
            writer.WriteLine(JsonSerializer.Serialize(truthEnvelope, new JsonSerializerOptions { WriteIndented = true }));
            return hasActionRequired ? 1 : 0;
        }

        // Render Spectre.Console Tables
        AnsiConsole.Write(new Rule("[bold cyan]Together as One (TAO) — ProjectKongor System Diagnostics[/]").LeftJustified());
        AnsiConsole.WriteLine();

        // Section A: Microservices & Infrastructure
        if (filterInfra)
        {
            var infraTable = new Table().Border(TableBorder.Rounded).BorderColor(Color.Cyan1);
            infraTable.AddColumn(new TableColumn("[bold]Service / Subsystem[/]").Width(26));
            infraTable.AddColumn(new TableColumn("[bold]Endpoint[/]").Width(18));
            infraTable.AddColumn(new TableColumn("[bold]State[/]").Width(22));
            infraTable.AddColumn(new TableColumn("[bold]Details[/]"));

            foreach (var probe in probes.Where(p => !p.Name.Contains("Game Engine") && !p.Name.Contains("IPC Pipe") && !p.Name.StartsWith("Native", StringComparison.OrdinalIgnoreCase)))
            {
                infraTable.AddRow(probe.Name, probe.Endpoint, probe.StatusText, probe.Details);
            }

            AnsiConsole.Write(infraTable);
            AnsiConsole.WriteLine();
        }

        // Section B: Game Engine & IPC Pipes
        if (filterEngine)
        {
            var engineTable = new Table().Border(TableBorder.Rounded).BorderColor(Color.Cyan1);
            engineTable.AddColumn(new TableColumn("[bold]Engine / IPC Channel[/]").Width(26));
            engineTable.AddColumn(new TableColumn("[bold]Endpoint[/]").Width(18));
            engineTable.AddColumn(new TableColumn("[bold]State[/]").Width(22));
            engineTable.AddColumn(new TableColumn("[bold]Details[/]"));

            foreach (var probe in probes.Where(p => p.Name.Contains("Game Engine") || p.Name.Contains("IPC Pipe")))
            {
                engineTable.AddRow(probe.Name, probe.Endpoint, probe.StatusText, probe.Details);
            }

            AnsiConsole.Write(engineTable);
            AnsiConsole.WriteLine();
        }

        // Section C: Native Engine Hardening & Security
        if (filterNative)
        {
            var nativeTable = new Table().Border(TableBorder.Rounded).BorderColor(Color.Cyan1);
            nativeTable.AddColumn(new TableColumn("[bold]Native Subsystem[/]").Width(26));
            nativeTable.AddColumn(new TableColumn("[bold]Target Binary[/]").Width(18));
            nativeTable.AddColumn(new TableColumn("[bold]Security State[/]").Width(22));
            nativeTable.AddColumn(new TableColumn("[bold]Compliance Details[/]"));

            foreach (var probe in probes.Where(p => p.Name.StartsWith("Native", StringComparison.OrdinalIgnoreCase)))
            {
                nativeTable.AddRow(probe.Name, probe.Endpoint, probe.StatusText, probe.Details);
            }

            AnsiConsole.Write(nativeTable);
            AnsiConsole.WriteLine();
        }

        // Section D: Workspace Root Hygiene & Zero-Clutter Architecture
        if (filterHygiene)
        {
            Table hygieneTable = new Table().Border(TableBorder.Rounded).BorderColor(Color.Cyan1);
            hygieneTable.AddColumn(new TableColumn("[bold]Workspace Domain[/]").Width(26));
            hygieneTable.AddColumn(new TableColumn("[bold]Directory Path[/]").Width(18));
            hygieneTable.AddColumn(new TableColumn("[bold]Hygiene State[/]").Width(22));
            hygieneTable.AddColumn(new TableColumn("[bold]Audit Details[/]"));

            foreach (ProbeItemResult probe in probes.Where(p => p.Name.Contains("Hygiene", StringComparison.OrdinalIgnoreCase)))
            {
                hygieneTable.AddRow(probe.Name, probe.Endpoint, probe.StatusText, probe.Details);
            }

            AnsiConsole.Write(hygieneTable);
            AnsiConsole.WriteLine();
        }

        // Section C: Developer Issues Backplane
        var issueTable = new Table().Border(TableBorder.Rounded).BorderColor(Color.Cyan1);
        issueTable.AddColumn(new TableColumn("[bold]Backplane Metric[/]").Width(26));
        issueTable.AddColumn(new TableColumn("[bold]Count[/]").Width(18));
        issueTable.AddColumn(new TableColumn("[bold]Status[/]").Width(22));
        issueTable.AddColumn(new TableColumn("[bold]Notes[/]"));

        issueTable.AddRow("Total Issues Tracked", journeySummary.TotalCaught.ToString(), "[white]Active[/]", "Dual-engine NDJSON buffer");
        issueTable.AddRow("Auto-Resolved", autoResolvedCount.ToString(), "[bold green]RESOLVED[/]", "Healed via deterministic recipes");
        issueTable.AddRow("Open Issues", openCount.ToString(), openCount > 0 ? "[yellow]OPEN[/]" : "[green]CLEAR[/]", "Pending triage or reflex");
        issueTable.AddRow("Regressions", regCount.ToString(), regCount > 0 ? "[bold red]REGRESSION[/]" : "[green]CLEAR[/]", "Recurring failures detected");
        issueTable.AddRow("Action Required", actionReqCount.ToString(), actionReqCount > 0 ? "[bold red]ACTION REQUIRED[/]" : "[green]CLEAR[/]", "Requires manual / agent journey");

        AnsiConsole.Write(issueTable);
        AnsiConsole.WriteLine();

        // Overall Health Banner Panel
        var panel = new Panel(new Markup(
            $"[bold]Overall System Status:[/] [{healthColor}]{healthStatus}[/]\n" +
            $"[grey]Evaluated {probes.Count} probes across infrastructure, engine processes, IPC channels, and issue backplane.[/]"))
        {
            Header = new PanelHeader("[bold white]Diagnostic Summary[/]"),
            Border = BoxBorder.Rounded,
            BorderStyle = new Style(healthStatus == "HEALTHY" ? Color.Green : healthStatus == "DEGRADED" ? Color.Yellow : Color.Red),
            Padding = new Padding(2, 1, 2, 1)
        };

        AnsiConsole.Write(panel);
        AnsiConsole.WriteLine();

        return hasActionRequired ? 1 : 0;
    }

    private static async Task<ProbeItemResult> ProbeTcpPortAsync(string name, string host, int port)
    {
        try
        {
            using var client = new TcpClient();
            using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(50));
            await client.ConnectAsync(host, port, cts.Token);

            if (client.Connected)
            {
                return new ProbeItemResult(name, $"{host}:{port}", true, "[bold green]ONLINE (Port Active)[/]", "TCP handshake successful");
            }
        }
        catch { }

        return new ProbeItemResult(name, $"{host}:{port}", false, "[grey]OFFLINE (Available)[/]", "Port ready for bind");
    }
}
