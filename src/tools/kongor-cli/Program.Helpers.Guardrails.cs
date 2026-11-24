// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// TAO (Together As One) MCP & Kongor CLI -- Created by guccidonut (https://github.com/guccidonut/PKV)

using System.Diagnostics;
using System.Text.Json;
using Kongor.Cli.Infrastructure;
using Kongor.Core.Configuration;
using Spectre.Console;

namespace Kongor.Cli;

public static partial class Program
{
    internal static async Task<int> StopDotNetProcessesAsync(
        bool globalScope = false,
        bool includeMcp = false,
        bool sessionOnly = false)
    {
        bool hasWorkspace = CliRuntimeContext.TryResolveWorkspace(out KongorWorkspaceResult? ws);
        if (sessionOnly)
        {
            return StopSessionTrackedProcesses(ws?.Root);
        }

        string scopeLabel = globalScope
            ? "ALL ProjectKongor workspaces (sparing unrelated dotnet processes)"
            : hasWorkspace && ws is not null
                ? $"workspace '{ws.Root}'"
                : "ProjectKongor processes (no workspace configured; using Kongor-only ownership filter)";
        AnsiConsole.MarkupLine($"[bold red]🛑 Stopping Kongor processes for {Markup.Escape(scopeLabel)}...[/]");

        int currentPid = Environment.ProcessId;
        int? parentShimPid = int.TryParse(Environment.GetEnvironmentVariable("KONGOR_SHIM_PID"), out int parsedShim)
            ? parsedShim
            : null;
        KongorUserConfig userConfig = KongorUserConfigStore.Load();

        IReadOnlyList<ProcessSnapshot> snapshots = KongorProcessOwnershipFilter.CaptureLiveProcessSnapshots(
            msg => Console.Error.WriteLine(msg));

        StopFilterDecision decision = KongorProcessOwnershipFilter.SelectTargets(
            snapshots,
            new StopProcessFilterOptions
            {
                CurrentProcessId = currentPid,
                ParentShimProcessId = parentShimPid,
                ActiveWorkspaceRoot = ws?.Root,
                RegisteredWorkspaceRoots = [.. userConfig.Workspaces.Select(w => w.Path)],
                GlobalScope = globalScope,
                IncludeMcp = includeMcp
            });

        int count = 0;
        foreach (StopProcessTarget target in decision.Targets)
        {
            try
            {
                using Process proc = Process.GetProcessById(target.Process.ProcessId);
                AnsiConsole.MarkupLine(
                    $"[yellow]  Terminating {Markup.Escape(target.Process.ProcessName)} (PID: {target.Process.ProcessId})[/] [grey][[{Markup.Escape(target.Reason)}]][/]");
                proc.Kill(entireProcessTree: true);
                count++;
            }
            catch (ArgumentException)
            {
                // Process already exited before termination.
            }
            catch (Exception ex)
            {
                AnsiConsole.MarkupLine(
                    $"[grey]  Skipped PID {target.Process.ProcessId} ({Markup.Escape(target.Process.ProcessName)}): {Markup.Escape(ex.Message)}[/]");
            }
        }

        await Task.CompletedTask;

        if (decision.Skipped.Count > 0)
        {
            int unrelatedDotnet = decision.Skipped.Count(s => s.Reason.Contains("unrelated dotnet", StringComparison.OrdinalIgnoreCase));
            int accessDenied = decision.Skipped.Count(s => s.Reason.Contains("access-denied", StringComparison.OrdinalIgnoreCase));
            int mcpSpared = decision.Skipped.Count(s => s.Reason.Contains("MCP", StringComparison.OrdinalIgnoreCase));
            int otherWs = decision.Skipped.Count(s => s.Reason.Contains("workspace", StringComparison.OrdinalIgnoreCase));

            List<string> summaryParts = [];
            if (unrelatedDotnet > 0) summaryParts.Add($"{unrelatedDotnet} unrelated dotnet");
            if (mcpSpared > 0) summaryParts.Add($"{mcpSpared} active MCP");
            if (otherWs > 0) summaryParts.Add($"{otherWs} other-workspace");
            if (accessDenied > 0) summaryParts.Add($"{accessDenied} elevated/access-denied");

            if (summaryParts.Count > 0)
            {
                AnsiConsole.MarkupLine($"[grey]  Spared: {Markup.Escape(string.Join(", ", summaryParts))} process(es).[/]");
            }
        }

        if (count > 0)
            AnsiConsole.MarkupLine($"[bold green]✓ Successfully stopped {count} ProjectKongor process(es).[/]");
        else
            AnsiConsole.MarkupLine("[grey]No active ProjectKongor processes found in scope.[/]");
        return 0;
    }

    private static int StopSessionTrackedProcesses(string? workspaceRoot)
    {
        if (string.IsNullOrWhiteSpace(workspaceRoot))
        {
            AnsiConsole.MarkupLine("[yellow]No workspace resolved; cannot locate userdata/session_pids.json.[/]");
            return 1;
        }

        string registryPath = Path.Combine(workspaceRoot, "userdata", "session_pids.json");
        AnsiConsole.MarkupLine($"[bold cyan]🛑 Stopping session-scoped processes from '{Markup.Escape(registryPath)}'...[/]");
        if (!File.Exists(registryPath))
        {
            AnsiConsole.MarkupLine("[grey]No active session PID registry found.[/]");
            return 0;
        }

        int stopped = 0;
        try
        {
            using JsonDocument doc = JsonDocument.Parse(File.ReadAllText(registryPath));
            List<int> pids = [];
            if (doc.RootElement.ValueKind == JsonValueKind.Array)
            {
                foreach (JsonElement el in doc.RootElement.EnumerateArray())
                {
                    if (el.ValueKind == JsonValueKind.Number) pids.Add(el.GetInt32());
                    else if (el.ValueKind == JsonValueKind.Object && el.TryGetProperty("pid", out JsonElement pEl) && pEl.ValueKind == JsonValueKind.Number)
                        pids.Add(pEl.GetInt32());
                }
            }
            else if (doc.RootElement.ValueKind == JsonValueKind.Object &&
                     doc.RootElement.TryGetProperty("pids", out JsonElement pidsEl) &&
                     pidsEl.ValueKind == JsonValueKind.Array)
            {
                foreach (JsonElement el in pidsEl.EnumerateArray())
                {
                    if (el.ValueKind == JsonValueKind.Number) pids.Add(el.GetInt32());
                    else if (el.ValueKind == JsonValueKind.Object && el.TryGetProperty("pid", out JsonElement pEl) && pEl.ValueKind == JsonValueKind.Number)
                        pids.Add(pEl.GetInt32());
                }
            }

            foreach (int pid in pids.Distinct())
            {
                if (pid <= 0 || pid == Environment.ProcessId) continue;
                try
                {
                    using Process p = Process.GetProcessById(pid);
                    AnsiConsole.MarkupLine($"[yellow]  Terminating session process {Markup.Escape(p.ProcessName)} (PID: {pid})...[/]");
                    p.Kill(entireProcessTree: true);
                    stopped++;
                }
                catch (ArgumentException)
                {
                }
                catch (Exception ex)
                {
                    AnsiConsole.MarkupLine($"[grey]  Could not terminate session PID {pid}: {Markup.Escape(ex.Message)}[/]");
                }
            }

            File.Delete(registryPath);
        }
        catch (Exception ex)
        {
            AnsiConsole.MarkupLine($"[yellow]Warning: Failed processing '{Markup.Escape(registryPath)}': {Markup.Escape(ex.Message)}[/]");
        }

        AnsiConsole.MarkupLine(stopped > 0
            ? $"[bold green]✓ Successfully stopped {stopped} session process(es).[/]"
            : "[grey]No live session processes found.[/]");
        return 0;
    }

    internal static int RunClean(
        bool cleanAll,
        bool cleanLogs = false,
        bool assumeYes = false,
        Func<string, bool>? confirmOutOfTreeOverride = null)
    {
        KongorWorkspaceResult ws = CliRuntimeContext.RequireSourceWorkspace();
        string repo = ws.Root;

        // Defense-in-depth sentinel guard: refuse to delete anything unless both ProjectKongor.slnx and src/ exist
        if (!File.Exists(Path.Combine(repo, "ProjectKongor.slnx")) || !Directory.Exists(Path.Combine(repo, "src")))
        {
            AnsiConsole.MarkupLine(
                $"[bold red]✗ Refusing to clean '{Markup.Escape(repo)}': missing ProjectKongor.slnx or src/ directory.[/]\n" +
                "[yellow]Fix:[/] Run [bold]kongor workspace use \"<path-to-ProjectKongor>\"[/] to verify your workspace.");
            return 1;
        }

        // Out-of-tree confirmation guardrail: never silently purge build artifacts when invoked outside the workspace
        if (ws.IsOutOfTree && !assumeYes)
        {
            bool confirmed;
            if (confirmOutOfTreeOverride is not null)
            {
                confirmed = confirmOutOfTreeOverride(repo);
            }
            else if (Console.IsInputRedirected)
            {
                AnsiConsole.MarkupLine(
                    $"[bold red]✗ Refusing out-of-tree clean of '{Markup.Escape(repo)}' in non-interactive mode (CWD: '{Markup.Escape(ws.InvocationCwd)}').[/]\n" +
                    "[yellow]Fix:[/] Re-run with [bold]--yes[/] (or [bold]-y[/]) to confirm cleaning this workspace from outside it.");
                return 1;
            }
            else
            {
                AnsiConsole.MarkupLine(
                    $"[bold yellow]⚠ Out-of-tree clean target:[/] [white]{Markup.Escape(repo)}[/] [grey](resolved via {ws.Source})[/]");
                confirmed = AnsiConsole.Confirm("Clean build artifacts (src/, tests/, artifacts/) in this workspace?", defaultValue: false);
            }

            if (!confirmed)
            {
                AnsiConsole.MarkupLine("[grey]Clean aborted.[/]");
                return 1;
            }
        }

        AnsiConsole.MarkupLine($"[bold cyan]\U0001F9F9 Running ProjectKongor Deep Clean Pipeline in '{Markup.Escape(repo)}'...[/]");

        string[] targetDirNames = ["bin", "obj", "TestResults", "testresults", "publish"];
        int deletedDirs = 0;
        int deletedLogs = 0;
        int deletedDumps = 0;
        long freedBytes = 0;

        // Strictly scope recursive build artifact deletion to verified source/test subtrees (never .venv, games/, or root symlinks)
        string[] allowedSweepRoots =
        [
            Path.Combine(repo, "src"),
            Path.Combine(repo, "tests")
        ];

        foreach (string sweepRoot in allowedSweepRoots)
        {
            if (!Directory.Exists(sweepRoot)) continue;

            foreach (string dir in Directory.GetDirectories(sweepRoot, "*", SearchOption.AllDirectories))
            {
                string name = Path.GetFileName(dir);
                if (!targetDirNames.Contains(name, StringComparer.OrdinalIgnoreCase)) continue;
                if (dir.Contains(".venv", StringComparison.OrdinalIgnoreCase) ||
                    dir.Contains("node_modules", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                try
                {
                    try
                    {
                        freedBytes += Directory.GetFiles(dir, "*", SearchOption.AllDirectories)
                            .Sum(f => new FileInfo(f).Length);
                    }
                    catch
                    {
                    }

                    Directory.Delete(dir, true);
                    deletedDirs++;
                }
                catch (Exception ex)
                {
                    Console.Error.WriteLine($"[kongor-clean] Skipped locked directory '{dir}': {ex.Message}");
                }
            }
        }

        // Top-level repo TestResults and nupkg outputs
        string[] directBuildDirs =
        [
            Path.Combine(repo, "TestResults"),
            Path.Combine(repo, "src", "tools", "kongor-cli", "nupkg")
        ];
        foreach (string directDir in directBuildDirs)
        {
            if (!Directory.Exists(directDir)) continue;
            try
            {
                Directory.Delete(directDir, true);
                deletedDirs++;
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[kongor-clean] Skipped '{directDir}': {ex.Message}");
            }
        }

        // Clean logs and crash dumps if requested or if --all is specified
        if (cleanLogs || cleanAll)
        {
            string[] logDirs =
            [
                Path.Combine(repo, "logs"),
                Path.Combine(repo, "src", "services", "master-server", "logs"),
                Path.Combine(repo, "src", "services", "chat-server", "logs"),
                Path.Combine(repo, "src", "services", "web-api", "logs"),
                Path.Combine(repo, "src", "services", "web-ui", "logs"),
                Path.Combine(repo, "games", "client", "userdata", "game", "logs"),
                Path.Combine(repo, "games", "client", "userdata", "logs"),
                Path.Combine(repo, "games", "server", "userdata", "game", "logs"),
                Path.Combine(repo, "games", "server", "userdata", "logs"),
                Path.Combine(repo, "games", "server"),
                Path.Combine(repo, "games", "client")
            ];

            foreach (string lDir in logDirs)
            {
                if (!Directory.Exists(lDir)) continue;

                foreach (string lf in Directory.GetFiles(lDir, "*.log", SearchOption.TopDirectoryOnly))
                {
                    try
                    {
                        FileInfo fi = new(lf);
                        freedBytes += fi.Length;
                        File.Delete(lf);
                        deletedLogs++;
                    }
                    catch (Exception ex)
                    {
                        Console.Error.WriteLine($"[kongor-clean] Skipped locked log '{lf}': {ex.Message}");
                    }
                }

                foreach (string df in Directory.GetFiles(lDir, "*.dmp", SearchOption.TopDirectoryOnly))
                {
                    try
                    {
                        FileInfo fi = new(df);
                        freedBytes += fi.Length;
                        File.Delete(df);
                        deletedDumps++;
                    }
                    catch (Exception ex)
                    {
                        Console.Error.WriteLine($"[kongor-clean] Skipped locked dump '{df}': {ex.Message}");
                    }
                }
            }
        }

        if (cleanAll)
        {
            string[] optionalClean =
            [
                Path.Combine(repo, "src/tools/comfyui/video_output"),
                Path.Combine(repo, "docs/_site"),
                Path.Combine(repo, "docs/api")
            ];
            foreach (string opt in optionalClean)
            {
                if (!Directory.Exists(opt)) continue;
                try
                {
                    Directory.Delete(opt, true);
                    deletedDirs++;
                }
                catch (Exception ex)
                {
                    Console.Error.WriteLine($"[kongor-clean] Skipped optional clean '{opt}': {ex.Message}");
                }
            }
        }

        double mb = freedBytes / (1024.0 * 1024.0);
        AnsiConsole.MarkupLine(
            $"[bold green]\u2713 Cleaned {deletedDirs} directory targets, {deletedLogs} log files, and {deletedDumps} crash dumps (~{mb:F2} MB freed).[/]");
        return 0;
    }
}
