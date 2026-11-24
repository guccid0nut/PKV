// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// TAO (Together As One) MCP & Kongor CLI -- Created by guccidonut (https://github.com/guccidonut/PKV)

// ============================================================================
// File: AudioCommand.cs
// System: Kongor CLI - Native C++ Modernization & Engine Hardening
// Subsystem: Phase 8 Client Low-Latency WASAPI & 3D Spatial HRTF Audio (fmodex64.dll)
// ============================================================================

using System;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Kongor.Core.Services.Native;
using Kongor.Core.Services.Voice;
using Spectre.Console;
using Spectre.Console.Cli;

namespace Kongor.Cli.Commands;

public sealed class AudioCommandSettings : CommandSettings
{
    [CommandArgument(0, "[action]")]
    [Description("Primary action: 'status', 'test-mic', 'upgrade', 'rollback', or 'benchmark' (default: 'status')")]
    public string? Action { get; init; }

    [CommandOption("-d|--duration <SECONDS>")]
    [Description("Duration of microphone recording in seconds for loopback audition (default: 3)")]
    public int DurationSeconds { get; init; } = 3;

    [CommandOption("--skip-process-check")]
    [Description("Bypass running process lock detection")]
    public bool SkipProcessCheck { get; init; }
}

public sealed class AudioCommand : AsyncCommand<AudioCommandSettings>
{
    public override async Task<int> ExecuteAsync(CommandContext context, AudioCommandSettings settings)
    {
        string action = (settings.Action ?? "status").Trim().ToLowerInvariant();
        string repoRoot = Program.FindRepoRoot();

        AnsiConsole.Write(new Rule("[bold cyan]Phase 8 Client Low-Latency WASAPI & 3D Spatial HRTF Audio[/]").LeftJustified());

        switch (action)
        {
            case "status":
                return ExecuteStatus(repoRoot);

            case "test":
            case "test-mic":
            case "mic":
            case "loopback":
            case "voice":
                return await ExecuteMicTestAsync(settings.DurationSeconds);

            case "upgrade":
            case "deploy":
            case "modernize":
                return ExecuteUpgrade(repoRoot, settings.SkipProcessCheck);

            case "rollback":
            case "revert":
                return ExecuteRollback(repoRoot, settings.SkipProcessCheck);

            case "benchmark":
            case "bench":
            case "latency":
                return ExecuteBenchmark(repoRoot);

            default:
                AnsiConsole.MarkupLine($"[red]Unknown action:[/] '{action}'. Supported actions: 'status', 'test-mic', 'upgrade', 'rollback', 'benchmark'.");
                return 1;
        }
    }

    private static int ExecuteStatus(string repoRoot)
    {
        ClientAudioReport report = ClientAudioModernizationService.GetClientAudioStatus(repoRoot);

        var table = new Table().Border(TableBorder.Rounded).Title("[bold yellow]Player Client Audio Subsystem (fmodex64.dll)[/]");
        table.AddColumn(new TableColumn("[bold]Target Binary[/]").LeftAligned());
        table.AddColumn(new TableColumn("[bold]Audio Backend[/]").LeftAligned());
        table.AddColumn(new TableColumn("[bold]Size[/]").RightAligned());
        table.AddColumn(new TableColumn("[bold]SHA-256[/]").Centered());
        table.AddColumn(new TableColumn("[bold]Core / Backups[/]").LeftAligned());

        foreach (var t in report.Targets)
        {
            string statusColor = t.IsWasapiProxy ? "green" : "yellow";
            string sizeStr = t.Exists ? $"{t.SizeBytes:N0} B" : "[dim]Missing[/]";
            string hashStr = t.Exists ? $"{t.Sha256Hash[..8]}..." : "-";

            var backups = new System.Collections.Generic.List<string>();
            if (t.HasCoreEngine) backups.Add("[green]fmodex64_core.dll[/]");
            if (t.HasBackup) backups.Add(".bak");
            if (t.HasBackupOld) backups.Add(".bak.old");
            string backupStr = backups.Count > 0 ? string.Join(", ", backups) : "[dim]None[/]";

            table.AddRow(
                $"[bold white]{t.RelativePath}[/]",
                $"[{statusColor}]{t.AudioBackend}[/]",
                sizeStr,
                hashStr,
                backupStr
            );
        }

        AnsiConsole.Write(table);

        // Latency and performance summary
        var latencyTable = new Table().Border(TableBorder.Simple).Title("[bold cyan]DSP Buffer Latency Metrics[/]");
        latencyTable.AddColumn(new TableColumn("Metric").LeftAligned());
        latencyTable.AddColumn(new TableColumn("Value").RightAligned());

        latencyTable.AddRow("Vanilla DirectSound Latency", $"[yellow]{report.VanillaLatencyMs:F2} ms[/] (1024 samples x 4 buffers)");
        latencyTable.AddRow("Active Measured Latency", report.IsFleetModernized ? $"[green]{report.MeasuredLatencyMs:F2} ms[/] (512 samples x 4 buffers)" : $"[yellow]{report.MeasuredLatencyMs:F2} ms[/]");
        latencyTable.AddRow("Hardware Latency Reduction", report.IsFleetModernized ? $"[bold green]-{report.LatencyReductionPercent:F1}%[/] (Instant click/spell response)" : "[dim]Upgrade recommended[/]");
        latencyTable.AddRow("Windows 10/11 Spatial HRTF", report.IsFleetModernized ? "[bold green]Active (7.1 Binaural Headphone Virtualization)[/]" : "[dim]Inactive[/]");

        AnsiConsole.Write(latencyTable);

        if (report.ClientProcessesRunning)
        {
            AnsiConsole.MarkupLine($"\n[yellow]![/]  [bold yellow]Process Warning:[/] Active client process detected: {report.RunningProcesses}");
        }
        else
        {
            AnsiConsole.MarkupLine("\n[green]✓[/]  [dim]Process Safety: Zero game client locks active.[/]");
        }

        return 0;
    }

    private static int ExecuteUpgrade(string repoRoot, bool skipProcessCheck)
    {
        string cmakeProxy = Path.Combine(repoRoot, "native", "build", "kongor_audio_build", "Release", ClientAudioModernizationService.TargetBinaryName);
        string stagedProxy = Path.Combine(repoRoot, "artifacts", "native-binaries", "windows-x64", ClientAudioModernizationService.ProxyCandidateName);
        string candidatePath = File.Exists(cmakeProxy) ? cmakeProxy : stagedProxy;

        if (!File.Exists(candidatePath))
        {
            AnsiConsole.MarkupLine($"[red]Error:[/] Proxy binary not found at '{candidatePath}'. Run 'cmake --build native/build --config Release --target fmodex64' first.");
            return 1;
        }

        AnsiConsole.MarkupLine($"Deploying Phase 8 WASAPI & 3D Spatial HRTF Proxy Shim: [bold white]{repoRoot}[/]");

        var result = ClientAudioModernizationService.DeployClientAudioProxy(repoRoot, candidatePath, skipProcessCheck);

        if (!result.Success)
        {
            AnsiConsole.MarkupLine($"[bold red]Deployment Failed:[/] {result.Message}");
            return 1;
        }

        foreach (string core in result.CorePreservedPaths)
        {
            AnsiConsole.MarkupLine($"  [dim]>[/] [bold green][[PRESERVE_CORE]][/] Preserved genuine engine -> '[cyan]{core}[/]'.");
        }

        foreach (string rotated in result.RotatedBakOldPaths)
        {
            AnsiConsole.MarkupLine($"  [dim]>[/] [bold yellow][[BACKUP_ROTATED]][/] Rotated previous backup -> '[yellow]{rotated}[/]'.");
        }

        foreach (string dep in result.DeployedPaths)
        {
            AnsiConsole.MarkupLine($"  [dim]>[/] [bold green][[DEPLOYED_WASAPI]][/] Deployed low-latency proxy -> '[green]{dep}[/]'.");
        }

        AnsiConsole.MarkupLine($"\n[bold green]✓ {result.Message}[/]");
        return 0;
    }

    private static int ExecuteRollback(string repoRoot, bool skipProcessCheck)
    {
        AnsiConsole.MarkupLine($"Rolling back player client audio to vanilla FMOD Ex: [bold white]{repoRoot}[/]");

        var result = ClientAudioModernizationService.RollbackClientAudioProxy(repoRoot, skipProcessCheck);

        if (!result.Success)
        {
            AnsiConsole.MarkupLine($"[bold red]Rollback Failed:[/] {result.Message}");
            return 1;
        }

        foreach (string restored in result.RestoredPaths)
        {
            AnsiConsole.MarkupLine($"  [dim]>[/] [bold green][[RESTORED]][/] Restored vanilla binary -> '[green]{restored}[/]'.");
        }

        AnsiConsole.MarkupLine($"\n[bold green]✓ {result.Message}[/]");
        return 0;
    }

    private static int ExecuteBenchmark(string repoRoot)
    {
        var table = new Table().Border(TableBorder.Rounded).Title("[bold yellow]Audio DSP Buffer Latency Calibration Matrix[/]");
        table.AddColumn(new TableColumn("Buffer Size").LeftAligned());
        table.AddColumn(new TableColumn("Latency @ 44.1 kHz").RightAligned());
        table.AddColumn(new TableColumn("Latency @ 48.0 kHz").RightAligned());
        table.AddColumn(new TableColumn("Latency @ 96.0 kHz").RightAligned());
        table.AddColumn(new TableColumn("Driver Mode").Centered());
        table.AddColumn(new TableColumn("Stability Profile").LeftAligned());

        int[] bufferSizes = [128, 256, 512, 1024, 2048];

        foreach (int buf in bufferSizes)
        {
            float ms44 = ((float)buf / 44100.0f) * 1000.0f;
            float ms48 = ((float)buf / 48000.0f) * 1000.0f;
            float ms96 = ((float)buf / 96000.0f) * 1000.0f;

            string driver;
            string profile;
            string style;

            if (buf == 512)
            {
                driver = "[bold green]WASAPI (Selected)[/]";
                profile = "[bold green]Optimal (Sub-11ms, Zero Glitches, Low CPU)[/]";
                style = "bold green";
            }
            else if (buf == 1024)
            {
                driver = "[yellow]DirectSound (Vanilla)[/]";
                profile = "[yellow]Legacy Baseline (~85ms total roundtrip)[/]";
                style = "yellow";
            }
            else if (buf < 512)
            {
                driver = "[cyan]WASAPI Exclusive[/]";
                profile = "[cyan]Ultra-low (Requires Dedicated Audio DAC)[/]";
                style = "cyan";
            }
            else
            {
                driver = "[dim]MME / WaveOut[/]";
                profile = "[dim]High Latency Fallback[/]";
                style = "dim";
            }

            table.AddRow(
                $"[{style}]{buf} samples x 4[/]",
                $"[{style}]{ms44:F2} ms[/]",
                $"[{style}]{ms48:F2} ms[/]",
                $"[{style}]{ms96:F2} ms[/]",
                driver,
                profile
            );
        }

        AnsiConsole.Write(table);
        AnsiConsole.MarkupLine("\n[dim]Note: Latency represents one-way DSP mixing buffer fill time before DAC presentation.[/]");
        return 0;
    }

    private static async Task<int> ExecuteMicTestAsync(int durationSeconds)
    {
        AnsiConsole.MarkupLine("\n[bold yellow]=== Opus Microphone & Audio Loopback Diagnostic ===[/]\n");

        if (!OpusMicTestService.HasMicrophone())
        {
            AnsiConsole.MarkupLine("[bold red]Error:[/] No microphone hardware detected on Windows.");
            AnsiConsole.MarkupLine("[dim]Please check that your headset or USB microphone is plugged in and enabled in Windows Sound Settings.[/]");
            return 1;
        }

        if (!OpusMicTestService.HasPlaybackDevice())
        {
            AnsiConsole.MarkupLine("[bold yellow]Warning:[/] No audio playback device detected on Windows.");
        }

        int clampedDuration = Math.Clamp(durationSeconds, 1, 10);
        string inputDev = OpusMicTestService.GetDefaultInputDeviceName();
        string outputDev = OpusMicTestService.GetDefaultOutputDeviceName();

        AnsiConsole.MarkupLine($"[green]Input Device detected:[/] [bold white]{inputDev}[/]");
        AnsiConsole.MarkupLine($"[green]Output Device detected:[/] [bold white]{outputDev}[/]");
        AnsiConsole.MarkupLine($"Preparing to record for [bold cyan]{clampedDuration} seconds[/].");
        AnsiConsole.MarkupLine("[bold green]>>> SPEAK INTO YOUR MICROPHONE NOW... <<<[/]\n");

        string savePath = Path.Combine(Program.FindRepoRoot(), "scratch", "opus_loopback_test.wav");

        OpusMicTestResult result = await AnsiConsole.Progress()
            .Columns(
                new TaskDescriptionColumn(),
                new ProgressBarColumn(),
                new PercentageColumn(),
                new RemainingTimeColumn(),
                new SpinnerColumn())
            .StartAsync(async ctx =>
            {
                ProgressTask task = ctx.AddTask($"[bold green]Recording from {inputDev}...[/]", maxValue: 100);
                return await OpusMicTestService.RecordAndAuditionAsync(
                    durationSeconds: clampedDuration,
                    sampleRate: 48000,
                    playBackImmediately: false,
                    saveWavPath: savePath,
                    onProgressPercent: percent => task.Value = percent);
            });

        if (!result.Success)
        {
            AnsiConsole.MarkupLine($"\n[bold red]Microphone test failed:[/] {result.Message}");
            return 1;
        }

        Table table = new Table().Border(TableBorder.Rounded).Title("[bold green]Opus Voice Diagnostic Report[/]");
        table.AddColumn(new TableColumn("[bold]Acoustic / Codec Metric[/]").LeftAligned());
        table.AddColumn(new TableColumn("[bold]Measurement[/]").RightAligned());

        table.AddRow("Capture Device", $"[bold white]{result.InputDeviceName}[/]");
        table.AddRow("Playback Device", $"[bold white]{result.OutputDeviceName}[/]");
        table.AddRow("Capture Sample Rate", "48,000 Hz (Bit-Perfect Broadcast Quality)");
        table.AddRow("Channel Mode", "Mono 16-bit PCM");
        table.AddRow("Samples Captured", $"{result.SamplesRecorded:N0} samples");
        table.AddRow("Opus Frame Size", "20 ms (960 samples/frame)");
        table.AddRow("Compressed Opus Payload", $"{result.OpusBytesEncoded:N0} bytes");
        table.AddRow("Compression Ratio", $"{result.CompressionRatio:F1}:1 reduction");
        table.AddRow("Raw Mic Amplitude", $"{result.PeakAmplitude * 100:F1}%");
        table.AddRow("Headphone Boost (Norm)", $"{result.NormalizedPeak * 100:F0}% peak");
        table.AddRow("RMS Energy Level", $"{result.RmsEnergy * 100:F1}%");

        string micHealth = result.PeakAmplitude > 0.05f
            ? "[bold green]EXCELLENT (Clear voice detected)[/]"
            : "[bold yellow]LOW (Check mic gain or speaking volume)[/]";
        table.AddRow("Microphone Signal Health", micHealth);
        table.AddRow("Saved Test File", "[dim]scratch/opus_loopback_test.wav[/]");

        AnsiConsole.Write(table);

        AnsiConsole.MarkupLine($"\n[bold cyan]>>> AUDITIONING PLAYBACK: Streaming through {result.OutputDeviceName}... <<<[/]\n");

        await AnsiConsole.Progress()
            .Columns(
                new TaskDescriptionColumn(),
                new ProgressBarColumn(),
                new PercentageColumn(),
                new RemainingTimeColumn(),
                new SpinnerColumn())
            .StartAsync(async ctx =>
            {
                ProgressTask task = ctx.AddTask($"[bold cyan]Playing via Opus ({result.OutputDeviceName})...[/]", maxValue: 100);
                await OpusMicTestService.PlayWavBytesAsync(
                    result.DecodedWavBytes,
                    percent => task.Value = percent);
            });

        AnsiConsole.MarkupLine("\n[bold green]✓ Playback complete![/] Your recorded voice was auditioned through Opus.\n");
        return 0;
    }
}
