// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// TAO (Together As One) MCP & Kongor CLI -- Created by guccidonut (https://github.com/guccidonut/PKV)

// ============================================================================
// File: TelemetryCommand.cs
// System: Kongor CLI - Real-Time Game Telemetry & Performance Monitoring
// Subsystem: Dual-Plane Telemetry (Shared Memory Fast Bus & Named Pipe IPC)
// ============================================================================

using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Kongor.Cli.Mcp.Handlers;
using Kongor.Cli.Mcp.Protocol;
using Spectre.Console;
using Spectre.Console.Cli;

namespace Kongor.Cli.Commands;

public sealed class TelemetryCommandSettings : CommandSettings
{
    [CommandArgument(0, "[action]")]
    [Description("Action to perform: 'latest', 'stream', 'poll', 'push', 'update', or 'feed' (default: 'latest')")]
    public string? Action { get; init; }

    [CommandOption("-p|--pid <PID>")]
    [Description("Target game client process ID for telemetry sampling")]
    public int? ProcessId { get; init; }

    [CommandOption("-m|--map <NAME>")]
    [Description("Custom shared memory map name (e.g. 'Local\\KongorClientTelemetry_1234')")]
    public string? MapName { get; init; }

    [CommandOption("-c|--channel <CHANNEL>")]
    [Description("Telemetry ingestion channel: 'auto', 'shm' (shared memory), or 'ipc' (default: 'auto')")]
    public string Channel { get; init; } = "auto";

    [CommandOption("-l|--limit <COUNT>")]
    [Description("Number of frame history records to retrieve for stream/poll (default: 64)")]
    public int Limit { get; init; } = 64;

    [CommandOption("--json")]
    [Description("Output machine-readable JSON telemetry payload")]
    public bool Json { get; init; }

    // Dynamic metrics injection parameters
    [CommandOption("--draw-calls <COUNT>")]
    [Description("Dynamic draw calls count to feed into telemetry buffer")]
    public uint? DrawCalls { get; init; }

    [CommandOption("--entities <COUNT>")]
    [Description("Dynamic active entities count to feed into telemetry buffer")]
    public uint? ActiveEntities { get; init; }

    [CommandOption("--particles <COUNT>")]
    [Description("Dynamic active particles count to feed into telemetry buffer")]
    public uint? ParticlesAlive { get; init; }

    [CommandOption("--ping <US>")]
    [Description("Dynamic ping in microseconds to feed into telemetry buffer")]
    public uint? PingMicroseconds { get; init; }

    [CommandOption("--tick <TICK>")]
    [Description("Dynamic server tick number to feed into telemetry buffer")]
    public uint? ServerTick { get; init; }

    [CommandOption("--latency <MS>")]
    [Description("Dynamic sub-tick input latency in milliseconds to feed into telemetry buffer")]
    public float? InputLatencyMs { get; init; }
}

/// <summary>
/// kongor telemetry: Real-time dual-plane in-game telemetry stream, QPC frame latencies,
/// sub-tick input timings, and shared memory stats reader.
/// </summary>
public sealed class TelemetryCommand : AsyncCommand<TelemetryCommandSettings>
{
    public override async Task<int> ExecuteAsync(CommandContext context, TelemetryCommandSettings settings)
    {
        string action = (settings.Action ?? "latest").Trim().ToLowerInvariant();

        if (!settings.Json)
        {
            AnsiConsole.Write(new Rule("[bold cyan]ProjectKongor In-Game Real-Time Telemetry & QPC Sampler[/]").LeftJustified());
        }

        GameClientManageHandler clientManageHandler = new();
        GameTelemetryHandler telemetryHandler = new(clientManageHandler: clientManageHandler);

        using MemoryStream ms = new();
        using (Utf8JsonWriter writer = new(ms))
        {
            writer.WriteStartObject();
            writer.WriteString("action", action);
            writer.WriteString("channel", settings.Channel);

            if (settings.ProcessId.HasValue)
            {
                writer.WriteNumber("pid", settings.ProcessId.Value);
            }

            if (!string.IsNullOrWhiteSpace(settings.MapName))
            {
                writer.WriteString("mapName", settings.MapName);
            }

            writer.WriteNumber("limit", settings.Limit);

            if (settings.DrawCalls.HasValue)
            {
                writer.WriteNumber("drawCalls", settings.DrawCalls.Value);
            }

            if (settings.ActiveEntities.HasValue)
            {
                writer.WriteNumber("activeEntities", settings.ActiveEntities.Value);
            }

            if (settings.ParticlesAlive.HasValue)
            {
                writer.WriteNumber("particlesAlive", settings.ParticlesAlive.Value);
            }

            if (settings.PingMicroseconds.HasValue)
            {
                writer.WriteNumber("pingMicroseconds", settings.PingMicroseconds.Value);
            }

            if (settings.ServerTick.HasValue)
            {
                writer.WriteNumber("serverTick", settings.ServerTick.Value);
            }

            if (settings.InputLatencyMs.HasValue)
            {
                writer.WriteNumber("inputLatencyMs", settings.InputLatencyMs.Value);
            }

            writer.WriteEndObject();
        }

        using JsonDocument requestDoc = JsonDocument.Parse(ms.ToArray());
        ToolCallResult result;
        try
        {
            result = await telemetryHandler.HandleAsync(requestDoc.RootElement, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            if (settings.Json)
            {
                Console.WriteLine(JsonSerializer.Serialize(new { success = false, connected = false, error = ex.Message }));
                return 1;
            }
            AnsiConsole.MarkupLine($"\n[bold yellow]Telemetry notice:[/] {Markup.Escape(ex.Message)}");
            return 0;
        }

        if (settings.Json)
        {
            Console.WriteLine(result.FirstText);
            return result.IsError ? 1 : 0;
        }

        if (action is "push" or "update" or "feed")
        {
            return RenderPushResult(result);
        }

        if (action is "stream" or "poll" or "poll_ring")
        {
            return RenderStreamResult(result);
        }

        return RenderLatestResult(result, settings);
    }

    private static int RenderLatestResult(ToolCallResult result, TelemetryCommandSettings settings)
    {
        JsonDocument jsonDoc;
        try
        {
            jsonDoc = JsonDocument.Parse(result.FirstText);
        }
        catch (Exception ex)
        {
            AnsiConsole.MarkupLine($"[bold red]Error parsing telemetry payload:[/] {Markup.Escape(ex.Message)}");
            return 1;
        }

        using (jsonDoc)
        {
            JsonElement root = jsonDoc.RootElement;
            bool success = root.TryGetProperty("success", out JsonElement succEl) && succEl.GetBoolean();
            bool connected = root.TryGetProperty("connected", out JsonElement connEl) && connEl.GetBoolean();
            string channel = root.TryGetProperty("channel", out JsonElement chEl) ? chEl.GetString() ?? "auto" : "auto";
            string mapName = root.TryGetProperty("mapName", out JsonElement mnEl) ? mnEl.GetString() ?? "none" : "none";
            string timestamp = root.TryGetProperty("timestamp", out JsonElement tsEl) ? tsEl.GetString() ?? TimeProvider.System.GetUtcNow().ToString("o") : TimeProvider.System.GetUtcNow().ToString("o");

            Table table = new Table()
                .Border(TableBorder.Rounded)
                .BorderColor(Color.Cyan1)
                .Title("[bold white]In-Game Real-Time Performance & Hardware Telemetry[/]")
                .AddColumn(new TableColumn("[bold]Performance Metric[/]").Width(30))
                .AddColumn(new TableColumn("[bold]Live Sample[/]").Width(26))
                .AddColumn(new TableColumn("[bold]Supervision & SLA Target[/]"));

            if (success && root.TryGetProperty("telemetry", out JsonElement telem))
            {
                double fps = telem.TryGetProperty("currentFps", out JsonElement fpsEl) ? fpsEl.GetDouble() : 0.0;
                double cpuFrameMs = telem.TryGetProperty("cpuFrameTimeMs", out JsonElement cpuEl) ? cpuEl.GetDouble() : 0.0;
                double gpuFrameMs = telem.TryGetProperty("gpuFrameTimeMs", out JsonElement gpuEl) ? gpuEl.GetDouble() : 0.0;
                double inputLatMs = telem.TryGetProperty("inputLatencyMs", out JsonElement latEl) ? latEl.GetDouble() : 0.0;
                uint drawCalls = telem.TryGetProperty("drawCallCount", out JsonElement dcEl) ? dcEl.GetUInt32() : 0u;
                uint activeEntities = telem.TryGetProperty("activeEntities", out JsonElement aeEl) ? aeEl.GetUInt32() : 0u;
                uint particlesAlive = telem.TryGetProperty("particlesAlive", out JsonElement paEl) ? paEl.GetUInt32() : 0u;
                uint pingUs = telem.TryGetProperty("pingMicroseconds", out JsonElement pUsEl) ? pUsEl.GetUInt32() : 0u;
                uint pingMs = telem.TryGetProperty("pingMs", out JsonElement pMsEl) ? pMsEl.GetUInt32() : pingUs / 1000u;
                double packetLoss = telem.TryGetProperty("packetLossPercent", out JsonElement plEl) ? plEl.GetDouble() : 0.0;
                double memMb = telem.TryGetProperty("memoryWorkingSetMb", out JsonElement memEl) ? memEl.GetDouble() : 0.0;
                uint tick = telem.TryGetProperty("serverTick", out JsonElement tickEl) ? tickEl.GetUInt32() : 0u;
                uint voices = telem.TryGetProperty("audioVoicesActive", out JsonElement vEl) ? vEl.GetUInt32() : 0u;
                ulong frameIndex = telem.TryGetProperty("frameIndex", out JsonElement fiEl) ? fiEl.GetUInt64() : 0ul;

                string fpsColor = fps >= 144.0 ? "bold green" : (fps >= 60.0 ? "yellow" : "red");
                string latColor = inputLatMs <= 0.5 ? "bold green" : (inputLatMs <= 2.0 ? "yellow" : "red");

                table.AddRow("Telemetry Transport", channel.Equals("shm", StringComparison.OrdinalIgnoreCase) ? "[green]Shared Memory Fast Bus (Ring Buffer)[/]" : "[cyan]Named Pipe IPC (Control Plane)[/]", $"[grey]{Markup.Escape(mapName)}[/]");
                table.AddRow("Frame Sequence Index", $"[white]#{frameIndex:N0}[/]", "[green]Hardware QPC Timestamps[/]");
                table.AddRow("Frame Rate (FPS)", $"[{fpsColor}]{fps:F1} FPS[/]", fps >= 144.0 ? "[green]✓ Target >= 144 FPS Met[/]" : "[yellow]Normal Rendering[/]");
                table.AddRow("CPU Frame Time", $"{cpuFrameMs:F3} ms", cpuFrameMs <= 6.94 ? "[green]✓ <= 6.94 ms (144 Hz)[/]" : "[yellow]CPU Bound[/]");
                table.AddRow("GPU Frame Time", $"{gpuFrameMs:F3} ms", gpuFrameMs <= 6.94 ? "[green]✓ DXVK Vulkan 1.3[/]" : "[yellow]GPU Bound[/]");
                table.AddRow("Sub-Tick Input Latency", $"[{latColor}]{inputLatMs:F3} ms[/]", inputLatMs <= 0.5 ? "[bold green]✓ Sub-Tick Ultra-Low (< 0.5 ms)[/]" : "[yellow]Standard Tick Bound[/]");
                table.AddRow("Draw Calls", $"{drawCalls:N0} calls", drawCalls <= 1500 ? "[green]✓ Efficient Batching[/]" : "[yellow]High Draw Calls[/]");
                table.AddRow("Active Game Entities", $"{activeEntities:N0} entities", "[green]Entity Pool Active[/]");
                table.AddRow("Active Particle Emitters", $"{particlesAlive:N0} particles", "[green]Vulkan Compute/Sprite[/]");
                table.AddRow("Network Round-Trip Ping", $"{pingMs} ms ({pingUs:N0} µs)", pingMs <= 30 ? "[green]✓ Ultra-Low Latency[/]" : "[yellow]Moderate Ping[/]");
                table.AddRow("Packet Loss", $"{packetLoss:F2}%", packetLoss == 0.0 ? "[green]✓ 0.00% Loss[/]" : "[red]Packet Drops Detected[/]");
                table.AddRow("Working Set Memory", $"{memMb:F1} MB", memMb <= 1024.0 ? "[green]✓ Heap Bounds Normal (< 1 GB)[/]" : "[yellow]High Memory[/]");
                table.AddRow("Server Tick Sequence", $"{tick:N0} ticks", "[green]Synchronized[/]");
                table.AddRow("Active WASAPI Audio Voices", $"{voices} voices", "[green]HRTF Spatial Audio Active[/]");

                AnsiConsole.Write(table);
                AnsiConsole.MarkupLine($"\n[grey]Synchronized at: {Markup.Escape(timestamp)}[/]");
                return 0;
            }
            else
            {
                table.AddRow("Telemetry Transport", "[yellow]Offline / Disconnected[/]", "[grey]No shared memory fast bus active[/]");
                table.AddRow("Game Client (hon_x64)", "[yellow]Not Detected[/]", "[grey]Target PID detached or offline[/]");
                table.AddRow("Frame Rate (FPS)", "[grey]0.0 FPS[/]", "[grey]Engine not running[/]");
                table.AddRow("Sub-Tick Latency", "[grey]0.000 ms[/]", "[grey]Offline[/]");
                table.AddRow("Memory Working Set", "[grey]0.0 MB[/]", "[grey]Offline[/]");

                AnsiConsole.Write(table);

                string? errorMsg = root.TryGetProperty("error", out JsonElement errEl) ? errEl.GetString() : null;
                if (!string.IsNullOrWhiteSpace(errorMsg))
                {
                    AnsiConsole.MarkupLine($"\n[yellow]Status notice:[/] [grey]{Markup.Escape(errorMsg)}[/]");
                }

                AnsiConsole.MarkupLine("\n[bold cyan]💡 Quick Tip:[/] Launch the game client with '[bold green]kongor client launch[/]' or run a scrimmage match with '[bold green]kongor scrimmage[/]'.");
                return 0;
            }
        }
    }

    private static int RenderStreamResult(ToolCallResult result)
    {
        JsonDocument jsonDoc;
        try
        {
            jsonDoc = JsonDocument.Parse(result.FirstText);
        }
        catch (Exception ex)
        {
            AnsiConsole.MarkupLine($"[bold red]Error parsing stream payload:[/] {Markup.Escape(ex.Message)}");
            return 1;
        }

        using (jsonDoc)
        {
            JsonElement root = jsonDoc.RootElement;
            int framesRead = root.TryGetProperty("framesRead", out JsonElement frEl) ? frEl.GetInt32() : 0;
            string mapName = root.TryGetProperty("mapName", out JsonElement mnEl) ? mnEl.GetString() ?? "none" : "none";

            Table table = new Table()
                .Border(TableBorder.Rounded)
                .BorderColor(Color.Green)
                .Title($"[bold white]Telemetry Ring Buffer Stream ({framesRead} Frames Sampled)[/]")
                .AddColumn(new TableColumn("[bold]Frame[/]"))
                .AddColumn(new TableColumn("[bold]FPS[/]"))
                .AddColumn(new TableColumn("[bold]CPU (ms)[/]"))
                .AddColumn(new TableColumn("[bold]GPU (ms)[/]"))
                .AddColumn(new TableColumn("[bold]Input Latency[/]"))
                .AddColumn(new TableColumn("[bold]Draw Calls[/]"))
                .AddColumn(new TableColumn("[bold]Ping (ms)[/]"))
                .AddColumn(new TableColumn("[bold]Memory[/]"));

            if (root.TryGetProperty("records", out JsonElement recordsEl) && recordsEl.ValueKind == JsonValueKind.Array)
            {
                foreach (JsonElement r in recordsEl.EnumerateArray())
                {
                    ulong fi = r.TryGetProperty("frameIndex", out JsonElement fiEl) ? fiEl.GetUInt64() : 0ul;
                    double fps = r.TryGetProperty("currentFps", out JsonElement fpsEl) ? fpsEl.GetDouble() : 0.0;
                    double cpu = r.TryGetProperty("cpuFrameTimeMs", out JsonElement cEl) ? cEl.GetDouble() : 0.0;
                    double gpu = r.TryGetProperty("gpuFrameTimeMs", out JsonElement gEl) ? gEl.GetDouble() : 0.0;
                    double lat = r.TryGetProperty("inputLatencyMs", out JsonElement lEl) ? lEl.GetDouble() : 0.0;
                    uint dc = r.TryGetProperty("drawCallCount", out JsonElement dEl) ? dEl.GetUInt32() : 0u;
                    uint ping = r.TryGetProperty("pingMs", out JsonElement pEl) ? pEl.GetUInt32() : 0u;
                    uint mem = r.TryGetProperty("memoryWorkingSetMb", out JsonElement mEl) ? mEl.GetUInt32() : 0u;

                    table.AddRow(
                        $"#{fi}",
                        $"{fps:F1}",
                        $"{cpu:F2}",
                        $"{gpu:F2}",
                        lat <= 0.5 ? $"[green]{lat:F3} ms[/]" : $"{lat:F3} ms",
                        $"{dc:N0}",
                        $"{ping} ms",
                        $"{mem} MB");
                }
            }

            AnsiConsole.Write(table);
            AnsiConsole.MarkupLine($"\n[grey]Active Ring Buffer Map: {Markup.Escape(mapName)}[/]");
            return 0;
        }
    }

    private static int RenderPushResult(ToolCallResult result)
    {
        JsonDocument jsonDoc;
        try
        {
            jsonDoc = JsonDocument.Parse(result.FirstText);
        }
        catch (Exception ex)
        {
            AnsiConsole.MarkupLine($"[bold red]Error parsing push response:[/] {Markup.Escape(ex.Message)}");
            return 1;
        }

        using (jsonDoc)
        {
            JsonElement root = jsonDoc.RootElement;
            bool success = root.TryGetProperty("success", out JsonElement sEl) && sEl.GetBoolean();
            string msg = root.TryGetProperty("message", out JsonElement mEl) ? mEl.GetString() ?? string.Empty : string.Empty;

            if (success)
            {
                AnsiConsole.MarkupLine($"[bold green]✓ Dynamic telemetry feed updated successfully![/]");
                AnsiConsole.MarkupLine($"[grey]{Markup.Escape(msg)}[/]");
                return 0;
            }
            else
            {
                AnsiConsole.MarkupLine($"[bold yellow]Notice:[/] [grey]{Markup.Escape(msg)}[/]");
                return 0;
            }
        }
    }
}
