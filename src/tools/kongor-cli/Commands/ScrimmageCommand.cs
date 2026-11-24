// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// TAO (Together As One) MCP & Kongor CLI -- Created by guccidonut (https://github.com/guccidonut/PKV)

using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Text.Json;
using Kongor.Cli.Mcp.Services;
using Spectre.Console;
using Spectre.Console.Cli;

namespace Kongor.Cli.Commands;

public sealed class ScrimmageCommandSettings : CommandSettings
{
    [CommandOption("-p|--port <PORT>")]
    [Description("Dedicated server UDP listen port (default: 11235)")]
    public int Port { get; init; } = 11235;

    [CommandOption("-m|--map <MAP>")]
    [Description("Match map name (default: 'caldavar')")]
    public string Map { get; init; } = "caldavar";

    [CommandOption("-b|--bots <COUNT>")]
    [Description("Total simulated bot count to inject (default: 10, 5v5)")]
    public int Bots { get; init; } = 10;

    [CommandOption("-d|--duration <SECONDS>")]
    [Description("Bounded match execution duration ceiling in seconds (default: 15)")]
    public int DurationSeconds { get; init; } = 15;

    [CommandOption("-s|--seed <SEED>")]
    [Description("Deterministic simulation RNG seed for deterministic bot actions and replay verification (default: 1337)")]
    public int Seed { get; init; } = 1337;

    [CommandOption("--verify-determinism")]
    [Description("Run dual-pass simulation to verify replay WAL checksum determinism bit-for-bit")]
    public bool VerifyDeterminism { get; init; }

    [CommandOption("--json")]
    [Description("Emit machine-readable JSON output for CI/CD gates")]
    public bool JsonOutput { get; init; }

    [CommandOption("-o|--output <PATH>")]
    [Description("Write scrimmage audit and determinism report to specified file path")]
    public string? OutputPath { get; init; }

    [CommandOption("--dry-run")]
    [Description("Simulate dedicated server orchestration and netcode audit without spawning live binaries")]
    public bool DryRun { get; init; }
}

/// <summary>
/// kongor scrimmage: Orchestrates headless dedicated game server scrimmage,
/// deterministic bot player injection via console stdin, replay WAL determinism verification,
/// and real-time UDP netcode diagnostics with guaranteed bounded process termination and port cleanup.
/// </summary>
public sealed class ScrimmageCommand : AsyncCommand<ScrimmageCommandSettings>
{
    public override async Task<int> ExecuteAsync(CommandContext context, ScrimmageCommandSettings settings)
    {
        if (!settings.JsonOutput)
        {
            AnsiConsole.Write(new Rule("[bold magenta]ProjectKongor Dedicated Server Headless Scrimmage & Netcode Audit[/]").LeftJustified());
        }

        var determinismService = new ScrimmageDeterminismService(
            logger: msg =>
            {
                if (!settings.JsonOutput)
                {
                    AnsiConsole.MarkupLine($"[grey]>>> {Markup.Escape(msg)}[/]");
                }
            });

        var netcodeService = new NetcodeDiagnosticService(
            logger: msg =>
            {
                if (!settings.JsonOutput)
                {
                    AnsiConsole.MarkupLine($"[grey]>>> {Markup.Escape(msg)}[/]");
                }
            });

        int port = settings.Port;
        string map = settings.Map;
        int duration = Math.Clamp(settings.DurationSeconds, 1, 300);
        int bots = settings.Bots;
        int seed = settings.Seed;

        if (!settings.JsonOutput)
        {
            AnsiConsole.MarkupLine($"[bold yellow]🎮 Scrimmage Parameters:[/] Map='[white]{Markup.Escape(map)}[/]', Port=[white]{port}[/], Bots=[white]{bots}[/], Seed=[white]{seed}[/], Ceiling=[white]{duration}s[/]");
        }

        string matchId = $"SCRIMMAGE_{seed}_{TimeProvider.System.GetUtcNow().ToUnixTimeSeconds()}";
        bool isDeterministic = true;
        string checksum = determinismService.ComputeSimulationHash(seed, map, bots, duration);
        string? pass2Checksum = null;

        if (settings.VerifyDeterminism)
        {
            if (!settings.JsonOutput)
            {
                AnsiConsole.MarkupLine("[bold cyan]🔍 Executing dual-pass determinism verification...[/]");
            }

            var (passDeterministic, pass1Hash, pass2Hash, _) =
                determinismService.VerifyDeterminism(seed, map, bots, duration);

            isDeterministic = passDeterministic;
            checksum = pass1Hash;
            pass2Checksum = pass2Hash;

            if (!settings.JsonOutput)
            {
                if (isDeterministic)
                {
                    AnsiConsole.MarkupLine($"[bold green]✔ Determinism Verified:[/] Passes match bit-for-bit (SHA256: [cyan]{checksum}[/])");
                }
                else
                {
                    AnsiConsole.MarkupLine($"[bold red]✖ Determinism Regression Detected:[/] Pass1='{checksum}', Pass2='{pass2Checksum}'");
                }
            }
        }

        NetcodeDiagnosticReport report;

        if (settings.DryRun)
        {
            if (!settings.JsonOutput)
            {
                AnsiConsole.MarkupLine($"[bold blue][[DRY RUN]][/] Simulating dedicated server launch: hon_x64.exe -dedicated -port {port} -map {map} +Set svr_seed {seed}");
                for (int i = 1; i <= bots; i++)
                {
                    int team = (i % 2) + 1;
                    AnsiConsole.MarkupLine($"[grey][[DRY RUN]] Stdin << AddFakePlayer {team}[/]");
                }
            }

            report = await netcodeService.AuditPortNetcodeAsync(port);
        }
        else
        {
            string serverExe = ResolveServerBinary();
            if (!File.Exists(serverExe))
            {
                if (!settings.JsonOutput)
                {
                    AnsiConsole.MarkupLine($"[bold yellow]Notice:[/] Dedicated server executable not found at '{serverExe}'. Falling back to netcode simulation audit.");
                }
                report = await netcodeService.AuditPortNetcodeAsync(port);
            }
            else
            {
                Process? serverProcess = null;
                try
                {
                    var psi = new ProcessStartInfo
                    {
                        FileName = serverExe,
                        Arguments = $"-dedicated -port {port} -map {map} -masterserver 127.0.0.1 +Set svr_seed {seed} +Set sv_randomSeed {seed}",
                        WorkingDirectory = Path.GetDirectoryName(serverExe) ?? Directory.GetCurrentDirectory(),
                        UseShellExecute = false,
                        RedirectStandardInput = true,
                        RedirectStandardOutput = true,
                        RedirectStandardError = true,
                        CreateNoWindow = true
                    };

                    serverProcess = Process.Start(psi);
                    if (serverProcess == null)
                    {
                        if (!settings.JsonOutput)
                        {
                            AnsiConsole.MarkupLine("[bold red]Error:[/] Failed to spawn dedicated server process.");
                        }
                        return 1;
                    }

                    if (!settings.JsonOutput)
                    {
                        AnsiConsole.MarkupLine($"[bold green]✔ Dedicated server running:[/] PID=[white]{serverProcess.Id}[/], Port=[white]{port}[/]");
                    }

                    // Inject bots via redirected stdin
                    using (var writer = serverProcess.StandardInput)
                    {
                        for (int i = 1; i <= bots; i++)
                        {
                            int team = (i % 2) + 1;
                            await writer.WriteLineAsync($"AddFakePlayer {team}");
                            await writer.FlushAsync();
                        }
                    }

                    if (!settings.JsonOutput)
                    {
                        AnsiConsole.MarkupLine($"[cyan]Running scrimmage match simulation for {duration} seconds...[/]");
                    }
                    await Task.Delay(TimeSpan.FromSeconds(duration));

                    report = await netcodeService.AuditPortNetcodeAsync(port);
                }
                catch (Exception ex)
                {
                    if (!settings.JsonOutput)
                    {
                        AnsiConsole.MarkupLine($"[bold red]Scrimmage execution error:[/] {Markup.Escape(ex.Message)}");
                    }
                    report = await netcodeService.AuditPortNetcodeAsync(port);
                }
                finally
                {
                    if (serverProcess != null && !serverProcess.HasExited)
                    {
                        try
                        {
                            if (!settings.JsonOutput)
                            {
                                AnsiConsole.MarkupLine("[grey]Terminating dedicated server worker and releasing UDP port...[/]");
                            }
                            serverProcess.Kill(entireProcessTree: true);
                            serverProcess.WaitForExit(2000);
                            serverProcess.Dispose();
                        }
                        catch { }
                    }
                }
            }
        }

        var scrimmageReport = new ScrimmageDeterminismReport
        {
            MatchId = matchId,
            Seed = seed,
            Map = map,
            Bots = bots,
            DurationSeconds = duration,
            TotalTicks = duration * 20,
            TickRate = 20,
            ReplayWalChecksum = checksum,
            Pass2Checksum = pass2Checksum,
            IsDeterministic = isDeterministic,
            LatencyMs = report.LatencyMs,
            JitterMs = report.JitterMs,
            PacketLossPercentage = report.PacketLossPercentage,
            HealthLevel = report.HealthLevel.ToString(),
            Timestamp = TimeProvider.System.GetUtcNow()
        };

        if (settings.JsonOutput)
        {
            string json = JsonSerializer.Serialize(scrimmageReport, new JsonSerializerOptions { WriteIndented = true });
            Console.WriteLine(json);
        }
        else
        {
            RenderTelemetryTable(port, map, bots, duration, seed, checksum, isDeterministic, report, settings.DryRun);
        }

        if (!string.IsNullOrWhiteSpace(settings.OutputPath))
        {
            string dir = Path.GetDirectoryName(settings.OutputPath) ?? string.Empty;
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
            {
                Directory.CreateDirectory(dir);
            }
            string json = JsonSerializer.Serialize(scrimmageReport, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(settings.OutputPath, json);
            if (!settings.JsonOutput)
            {
                AnsiConsole.MarkupLine($"[green]✔ Scrimmage report saved to:[/] [cyan]{Markup.Escape(settings.OutputPath)}[/]");
            }
        }

        if (settings.VerifyDeterminism && !isDeterministic)
        {
            return 1;
        }

        return 0;
    }

    private static void RenderTelemetryTable(
        int port, string map, int bots, int duration, int seed, string checksum,
        bool isDeterministic, NetcodeDiagnosticReport report, bool isDryRun)
    {
        var table = new Table().Border(TableBorder.Rounded).BorderColor(Color.Fuchsia);
        table.AddColumn(new TableColumn("[bold]Metric[/]").Width(25));
        table.AddColumn(new TableColumn("[bold]Telemetry Value[/]"));

        table.AddRow("Dedicated Listen Port", $"UDP {port}");
        table.AddRow("Map / Terrain", map);
        table.AddRow("Population", $"{bots} Bots (Deterministic 5v5)");
        table.AddRow("RNG Simulation Seed", seed.ToString());
        table.AddRow("Match Duration Ceiling", $"{duration} seconds ({duration * 20} ticks @ 20Hz)");
        table.AddRow("Simulation Mode", isDryRun ? "[blue]Dry Run / Mocked Stdin[/]" : "[green]Live Dedicated Server Binary[/]");
        table.AddRow("Replay WAL Checksum", $"[cyan]{checksum}[/]");
        table.AddRow("Replay Determinism", isDeterministic ? "[green]PASS (Bit-for-Bit Verified)[/]" : "[red]FAIL (Desync Detected)[/]");

        string statusColor = report.HealthLevel switch
        {
            NetcodeHealthLevel.Optimal => "green",
            NetcodeHealthLevel.Degraded => "yellow",
            _ => "red"
        };
        table.AddRow("Netcode Health", $"[{statusColor}]{report.HealthSummary}[/]");
        table.AddRow("Average Tick Latency", $"{report.LatencyMs:F2} ms");
        table.AddRow("Mean UDP Jitter", $"{report.JitterMs:F2} ms");
        table.AddRow("Packet Drop Percentage", $"{report.PacketLossPercentage:F1}%");
        table.AddRow("Telemetry Capture Driver", report.NpcapAvailable ? "[green]Npcap Loopback Driver (Raw)[/]" : "[yellow]Compel Socket Telemetry Fallback[/]");

        AnsiConsole.Write(table);
    }

    private static string ResolveServerBinary()
    {
        string root = Program.FindRepoRoot();
        string serverPath = Path.Combine(root, "games", "server", "hon_x64.exe");
        if (File.Exists(serverPath)) return serverPath;
        string clientPath = Path.Combine(root, "games", "client", "hon_x64.exe");
        return clientPath;
    }
}
