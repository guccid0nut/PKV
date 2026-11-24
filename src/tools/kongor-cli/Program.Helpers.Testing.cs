// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// TAO (Together As One) MCP & Kongor CLI -- Created by guccidonut (https://github.com/guccidonut/PKV)

using System.Diagnostics;
using System.Text.RegularExpressions;
using Kongor.Cli.Infrastructure;
using Kongor.Core.Configuration;
using Spectre.Console;

namespace Kongor.Cli;

public static partial class Program
{
    /// <summary>
    /// Unified test harness with 100% parity with legacy <c>kongor.ps1 test</c>:
    /// supports <c>sln-filters/Kongor.UnitTests.slnf</c> (<c>-maxcpucount:4 -nodereuse:false</c>, Workstation GC),
    /// <c>scripts/Invoke-KongorTestLock.ps1</c>, <c>GITHUB_ACTIONS</c> <c>Category!=Manual&amp;Category!=LocalOnly</c>,
    /// <c>tests/coverage.runsettings</c> + ReportGenerator HTML/Markdown summaries, <c>test soak</c>, and the zero-test guard.
    /// </summary>
    internal static int RunTests(
        string target,
        bool noBuild,
        bool coverage,
        string? filter,
        string? verbosity,
        bool all = false,
        bool headless = false)
    {
        string repo = CliRuntimeContext.RequireSourceWorkspace().Root;
        string lower = (target ?? "all").Trim().ToLowerInvariant();
        if (lower is "--all")
        {
            lower = "all";
            all = true;
        }

        if (lower is "soak" or "endurance")
        {
            AnsiConsole.MarkupLine("[bold cyan]⏱️  Executing Endurance & Memory Leak Soak Test (scripts/qa/run-soak-test.ps1)...[/]");
            return RunPowerShellScript("scripts/qa/run-soak-test.ps1", []);
        }

        AnsiConsole.MarkupLine(
            $"[bold cyan]\U0001F9EA Executing ProjectKongor Test Harness (Target: [yellow]{Markup.Escape(lower)}[/], NoBuild: [yellow]{noBuild}[/], Coverage: [yellow]{coverage}[/])...[/]");

        string testsRoot = Path.Combine(repo, "tests");
        string unitSlnf = Path.Combine(repo, "sln-filters", "Kongor.UnitTests.slnf");
        bool useUnitSlnf = lower == "unit" && File.Exists(unitSlnf);
        List<string> projectTargets = [];

        if (useUnitSlnf)
        {
            projectTargets.Add(unitSlnf);
        }
        else if (lower is "all" || string.IsNullOrWhiteSpace(target))
        {
            projectTargets.Add(Path.Combine(repo, "ProjectKongor.slnx"));
        }
        else if (lower is "unit" or "integration" or "performance" or "perf" or "load" or "gui" or "adversarial" or "e2e")
        {
            string subFolder = lower == "perf" ? "performance" : lower;
            string subPath = Path.Combine(testsRoot, subFolder);
            if (Directory.Exists(subPath))
            {
                projectTargets.AddRange(Directory.GetFiles(subPath, "*.csproj", SearchOption.AllDirectories));
            }
        }
        else
        {
            string[] matches = Directory.Exists(testsRoot)
                ? Directory.GetFiles(testsRoot, $"*{target}*.csproj", SearchOption.AllDirectories)
                : [];
            if (matches.Length > 0)
            {
                projectTargets.AddRange(matches);
            }
            else
            {
                projectTargets.Add(target!);
            }
        }

        if (projectTargets.Count == 0)
        {
            AnsiConsole.MarkupLine($"[yellow]No test projects found matching target '{Markup.Escape(target ?? string.Empty)}'.[/]");
            return 0;
        }

        string? effectiveFilter = filter;
        if (string.IsNullOrWhiteSpace(effectiveFilter) && !all)
        {
            bool isGitHubActions = string.Equals(
                Environment.GetEnvironmentVariable("GITHUB_ACTIONS"),
                "true",
                StringComparison.OrdinalIgnoreCase);
            effectiveFilter = isGitHubActions
                ? "Category!=Manual&Category!=LocalOnly"
                : "Category!=Manual";
        }

        string effectiveVerbosity = !string.IsNullOrWhiteSpace(verbosity)
            ? verbosity
            : (useUnitSlnf ? "minimal" : "quiet");

        string runSettingsPath = Path.Combine(repo, "tests", "coverage.runsettings");
        string coverageResultsDir = Path.Combine(repo, "TestResults", "coverage");
        string testLockScript = Path.Combine(repo, "scripts", "Invoke-KongorTestLock.ps1");

        Stopwatch sw = Stopwatch.StartNew();
        int totalPassed = 0;
        int totalFailed = 0;
        int totalSkipped = 0;
        int totalCount = 0;
        int projectCount = 0;
        int overallExitCode = 0;

        foreach (string projTarget in projectTargets)
        {
            List<string> dotnetArgs = ["test", $"\"{projTarget}\"", "--nologo"];
            if (useUnitSlnf)
            {
                dotnetArgs.Add("-maxcpucount:4");
                dotnetArgs.Add("-nodereuse:false");
            }
            if (noBuild)
            {
                dotnetArgs.Add("--no-build");
            }
            if (coverage)
            {
                if (File.Exists(runSettingsPath))
                {
                    dotnetArgs.Add($"--settings \"{runSettingsPath}\"");
                }
                dotnetArgs.Add("--collect:\"XPlat Code Coverage\"");
                dotnetArgs.Add($"--results-directory \"{coverageResultsDir}\"");
            }
            if (!string.IsNullOrWhiteSpace(effectiveFilter))
            {
                dotnetArgs.Add($"--filter \"{effectiveFilter}\"");
            }
            dotnetArgs.Add($"-v {effectiveVerbosity}");

            string joinedDotnetArgs = string.Join(" ", dotnetArgs);
            ProcessStartInfo psi;
            if (File.Exists(testLockScript))
            {
                string shellExe = OperatingSystem.IsWindows() ? "powershell.exe" : "pwsh";
                psi = new ProcessStartInfo
                {
                    FileName = shellExe,
                    Arguments = $"-NoProfile -ExecutionPolicy Bypass -File \"{testLockScript}\" -Owner \"cli-test-{Environment.ProcessId}\" -- dotnet {joinedDotnetArgs}",
                    WorkingDirectory = repo,
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    StandardOutputEncoding = System.Text.Encoding.UTF8,
                    StandardErrorEncoding = System.Text.Encoding.UTF8
                };
            }
            else
            {
                psi = new ProcessStartInfo
                {
                    FileName = "dotnet",
                    Arguments = joinedDotnetArgs,
                    WorkingDirectory = repo,
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    StandardOutputEncoding = System.Text.Encoding.UTF8,
                    StandardErrorEncoding = System.Text.Encoding.UTF8
                };
            }

            psi.Environment[KongorWorkspaceResolver.EnvKongorRoot] = repo;
            psi.Environment[KongorWorkspaceResolver.EnvRepoRoot] = repo;
            psi.Environment[KongorWorkspaceResolver.EnvCallerCwd] = CliRuntimeContext.InvocationCwd;
            if (useUnitSlnf)
            {
                psi.Environment["DOTNET_gcServer"] = "0";
            }

            try
            {
                using Process proc = new() { StartInfo = psi };
                proc.OutputDataReceived += (_, e) =>
                {
                    if (e.Data == null) return;
                    Console.WriteLine(e.Data);
                    Match m = TestSummaryRegex().Match(e.Data);
                    if (m.Success)
                    {
                        Interlocked.Add(ref totalFailed, int.Parse(m.Groups[1].Value));
                        Interlocked.Add(ref totalPassed, int.Parse(m.Groups[2].Value));
                        Interlocked.Add(ref totalSkipped, int.Parse(m.Groups[3].Value));
                        Interlocked.Add(ref totalCount, int.Parse(m.Groups[4].Value));
                        Interlocked.Increment(ref projectCount);
                    }
                };
                proc.ErrorDataReceived += (_, e) =>
                {
                    if (e.Data != null) Console.Error.WriteLine(e.Data);
                };

                proc.Start();
                proc.BeginOutputReadLine();
                proc.BeginErrorReadLine();
                proc.WaitForExit();
                if (proc.ExitCode != 0)
                {
                    overallExitCode = proc.ExitCode;
                }
            }
            catch (Exception ex)
            {
                AnsiConsole.MarkupLine($"[bold red]Failed to execute dotnet test on {Markup.Escape(projTarget)}:[/] {Markup.Escape(ex.Message)}");
                overallExitCode = -1;
            }
        }

        sw.Stop();

        if (totalFailed == 0 && totalPassed > 0 && overallExitCode == 0)
        {
            AnsiConsole.WriteLine();
            for (int i = 0; i < 3; i++)
            {
                AnsiConsole.MarkupLine(WinnerBannerMarkup);
            }
            AnsiConsole.WriteLine();
            string projSummary = projectCount > 0 ? $" across {projectCount} projects" : "";
            AnsiConsole.MarkupLine(
                $"[bold rgb(57,255,20)]  \u2728 Total Tests: [white]{totalPassed:N0}[/] passed ([grey]{totalFailed} failed, {totalSkipped} skipped{projSummary}[/]) | Total Duration: [white]{sw.Elapsed.TotalSeconds:F2}s[/][/]");
        }
        else
        {
            AnsiConsole.WriteLine();
            AnsiConsole.MarkupLine(
                $"[bold red]  \u274C Test Failures Detected: [white]{totalFailed:N0}[/] failed, [white]{totalPassed:N0}[/] passed out of [white]{totalCount:N0}[/] tests | Total Duration: [white]{sw.Elapsed.TotalSeconds:F2}s[/][/]");

            // Zero-test guard (matches kongor.ps1 parity): 0 tests executed must never silently succeed
            if (totalPassed == 0 && overallExitCode == 0)
            {
                AnsiConsole.MarkupLine(
                    "[bold red]✗ Zero-test guard:[/] 0 tests executed across target projects. " +
                    "Check --filter or run [yellow]kongor test <target> --build[/].");
                overallExitCode = 1;
            }
        }

        if (coverage)
        {
            AnsiConsole.MarkupLine("[bold cyan]📊 Generating Code Coverage Report via ReportGenerator...[/]");
            _ = RunProcess("dotnet", "tool restore", repo);
            string coverageGlob = Path.Combine(repo, "TestResults", "coverage", "**", "coverage.cobertura.xml");
            string coverageReportDir = Path.Combine(repo, "TestResults", "coverage-report");
            _ = RunProcess(
                "dotnet",
                $"tool run reportgenerator -reports:\"{coverageGlob}\" -targetdir:\"{coverageReportDir}\" -sourcedirs:\"{repo}\" -reporttypes:\"Html;MarkdownSummaryGithub\"",
                repo);
            string indexPath = Path.Combine(coverageReportDir, "index.html");
            if (File.Exists(indexPath))
            {
                AnsiConsole.MarkupLine($"[bold green]✓ Code Coverage Report generated:[/] [white]{Markup.Escape(indexPath)}[/]");
                bool isCi = string.Equals(Environment.GetEnvironmentVariable("GITHUB_ACTIONS"), "true", StringComparison.OrdinalIgnoreCase);
                if (!isCi && !headless)
                {
                    try { Process.Start(new ProcessStartInfo(indexPath) { UseShellExecute = true }); }
                    catch { /* headless shell without browser handler */ }
                }
            }
        }

        return overallExitCode;
    }
}
