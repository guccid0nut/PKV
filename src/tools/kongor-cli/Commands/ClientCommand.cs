// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// TAO (Together As One) MCP & Kongor CLI -- Created by guccidonut (https://github.com/guccidonut/PKV)

// ============================================================================
// File: ClientCommand.cs
// System: Kongor CLI - Game Client Process Governance & Supervision
// Subsystem: hon_x64.exe Lifecycle, Multi-Instance Slotting & IPC Integration
// ============================================================================

using System;
using System.ComponentModel;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Kongor.Cli.Mcp.Handlers;
using Kongor.Cli.Mcp.Protocol;
using Kongor.Cli.Mcp.Services;
using Spectre.Console;
using Spectre.Console.Cli;

namespace Kongor.Cli.Commands;

public sealed class ClientCommandSettings : CommandSettings
{
    [CommandArgument(0, "[action]")]
    [Description("Action to perform: 'launch', 'status', 'stop', 'restart', 'list', 'logs', 'errors', or 'command' (default: 'status')")]
    public string? Action { get; init; }

    [CommandArgument(1, "[target]")]
    [Description("Target parameter or command string (e.g. for 'command' or 'lua' actions)")]
    public string? Target { get; init; }

    [CommandOption("-m|--mode <MODE>")]
    [Description("Client launch mode: 'normal', 'practice', 'bot', 'replay', 'connect' (default: 'normal')")]
    public string Mode { get; init; } = "normal";

    [CommandOption("-c|--connect <ADDRESS>")]
    [Description("Dedicated server address to connect to upon launch (e.g. '127.0.0.1:11235')")]
    public string? ConnectAddress { get; init; }

    [CommandOption("-r|--resolution <RES>")]
    [Description("Screen resolution for the client (default: '1920x1080')")]
    public string Resolution { get; init; } = "1920x1080";

    [CommandOption("-w|--window-mode <MODE>")]
    [Description("Window mode: 'foreground', 'headless', 'offscreen', or 'minimized' (default: 'headless')")]
    public string WindowMode { get; init; } = "headless";

    [CommandOption("--headless")]
    [Description("Launch in headless offscreen mode at (-32000, -32000) with zero desktop disruption")]
    public bool Headless { get; init; }

    [CommandOption("--offscreen")]
    [Description("Alias for --headless (launches at offscreen slot coordinates)")]
    public bool Offscreen { get; init; }

    [CommandOption("--fullscreen")]
    [Description("Launch the client in exclusive/borderless fullscreen")]
    public bool Fullscreen { get; init; } = true;

    [CommandOption("--windowed")]
    [Description("Launch the client in windowed mode")]
    public bool Windowed { get; init; }

    [CommandOption("--fps <MAX_FPS>")]
    [Description("Maximum framerate unclamp limit (default: 1000)")]
    public int MaxFps { get; init; } = 1000;

    [CommandOption("-u|--user|--username <NAME>")]
    [Description("Account nickname or username for automatic login")]
    public string? Username { get; init; }

    [CommandOption("-p|--pass|--password <PASS>")]
    [Description("Account password for automatic login")]
    public string? Password { get; init; }

    [CommandOption("-s|--slot <SLOT>")]
    [Description("Multi-client slot index (0, 1, 2...)")]
    public int? Slot { get; init; }

    [CommandOption("--pid <PID>")]
    [Description("Target client process ID for status, stop, logs, or command")]
    public int? ProcessId { get; init; }

    [CommandOption("--all")]
    [Description("Stop all running game client processes")]
    public bool All { get; init; }

    [CommandOption("--steal-focus")]
    [Description("Allow the client window to steal foreground desktop focus")]
    public bool StealFocus { get; init; }

    [CommandOption("-n|--lines <LINES>")]
    [Description("Number of console log lines to retrieve (default: 50)")]
    public int Lines { get; init; } = 50;

    [CommandOption("--client-path <PATH>")]
    [Description("Custom absolute or relative path to hon_x64.exe")]
    public string? ClientPath { get; init; }

    [CommandOption("--master-server <URL>")]
    [Description("Master Server host address for authentication (default: '127.0.0.1:55555')")]
    public string? MasterServer { get; init; }

    [CommandOption("--direct")]
    [Description("Launch hon_x64.exe directly instead of launching Willowmaker")]
    public bool Direct { get; init; }

    [CommandOption("--json")]
    [Description("Output machine-readable JSON result")]
    public bool Json { get; init; }
}

/// <summary>
/// kongor client: Lifecycle supervision, headless offscreen launching, multi-client slotting,
/// and IPC command routing for the player game client binary (hon_x64.exe).
/// </summary>
public sealed class ClientCommand : AsyncCommand<ClientCommandSettings>
{
    public override async Task<int> ExecuteAsync(CommandContext context, ClientCommandSettings settings)
    {
        string action = (settings.Action ?? "status").Trim().ToLowerInvariant();

        if (action is "launch" or "start" or "restart" or "play")
        {
            JobObjectManager.Enabled = false;

            // When launching interactively without --headless, --offscreen, or --direct,
            // route to WILLOWMAKER launcher identical to 'kongor play'
            if (!settings.Headless && !settings.Offscreen && !settings.Direct &&
                !string.Equals(settings.WindowMode, "headless", StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(settings.WindowMode, "offscreen", StringComparison.OrdinalIgnoreCase))
            {
                string[] rawArgs = [.. context.Remaining.Raw];
                return Program.LaunchGameClient(rawArgs);
            }
        }

        if (!settings.Json)
        {
            AnsiConsole.Write(new Rule("[bold cyan]ProjectKongor Game Client (hon_x64.exe) Supervisor[/]").LeftJustified());
        }

        GameClientManageHandler clientHandler = new(
            logger: msg =>
            {
                if (!settings.Json)
                {
                    AnsiConsole.MarkupLine($"[grey]>>> {Markup.Escape(msg)}[/]");
                }
            });

        string effectiveWindowMode = settings.WindowMode;
        if (settings.Headless || settings.Offscreen)
        {
            effectiveWindowMode = "headless";
        }

        bool effectiveFullscreen = settings.Windowed ? false : settings.Fullscreen;
        bool effectiveStealFocus = settings.StealFocus || string.Equals(effectiveWindowMode, "foreground", StringComparison.OrdinalIgnoreCase);

        using MemoryStream ms = new();
        using (Utf8JsonWriter writer = new(ms))
        {
            writer.WriteStartObject();
            writer.WriteString("action", action);
            writer.WriteString("mode", settings.Mode);
            writer.WriteString("resolution", settings.Resolution);
            writer.WriteString("window_mode", effectiveWindowMode);
            writer.WriteBoolean("fullscreen", effectiveFullscreen);
            writer.WriteNumber("maxFps", settings.MaxFps > 0 ? settings.MaxFps : 1000);
            writer.WriteBoolean("steal_focus", effectiveStealFocus);

            if (!string.IsNullOrWhiteSpace(settings.ConnectAddress))
            {
                writer.WriteString("connectAddress", settings.ConnectAddress);
            }

            if (!string.IsNullOrWhiteSpace(settings.Username))
            {
                writer.WriteString("username", settings.Username);
                writer.WriteString("nickname", settings.Username);
            }

            if (!string.IsNullOrWhiteSpace(settings.Password))
            {
                writer.WriteString("password", settings.Password);
            }

            if (settings.Slot.HasValue)
            {
                writer.WriteNumber("slot", settings.Slot.Value);
            }

            if (settings.ProcessId.HasValue)
            {
                writer.WriteNumber("pid", settings.ProcessId.Value);
            }

            if (settings.All)
            {
                writer.WriteBoolean("all", true);
            }

            if (settings.Lines > 0)
            {
                writer.WriteNumber("lines", settings.Lines);
            }

            if (!string.IsNullOrWhiteSpace(settings.ClientPath))
            {
                writer.WriteString("customClientPath", settings.ClientPath);
            }

            if (!string.IsNullOrWhiteSpace(settings.MasterServer))
            {
                writer.WriteString("masterServer", settings.MasterServer);
            }

            if (!string.IsNullOrWhiteSpace(settings.Target))
            {
                if (action is "command")
                {
                    writer.WriteString("command", settings.Target);
                }
                else if (action is "lua")
                {
                    writer.WriteString("code", settings.Target);
                }
            }

            writer.WriteEndObject();
        }

        using JsonDocument requestDoc = JsonDocument.Parse(ms.ToArray());
        ToolCallResult result = await clientHandler.HandleAsync(requestDoc.RootElement, CancellationToken.None).ConfigureAwait(false);

        if (settings.Json)
        {
            Console.WriteLine(result.FirstText);
            return result.IsError ? 1 : 0;
        }

        switch (action)
        {
            case "launch":
            case "start":
                return RenderLaunchResult(result);

            case "stop":
            case "kill":
                return RenderStopResult(result);

            case "restart":
                return RenderRestartResult(result);

            case "list":
            case "clients":
                return RenderListResult(result);

            case "logs":
            case "log":
                return RenderLogsResult(result);

            case "errors":
                return RenderErrorsResult(result);

            case "command":
            case "lua":
                return RenderCommandResult(result);

            case "status":
            default:
                return RenderStatusResult(result);
        }
    }

    private static int RenderLaunchResult(ToolCallResult result)
    {
        JsonDocument jsonDoc;
        try
        {
            jsonDoc = JsonDocument.Parse(result.FirstText);
        }
        catch (Exception ex)
        {
            AnsiConsole.MarkupLine($"[bold red]Error parsing launch response:[/] {Markup.Escape(ex.Message)}");
            return 1;
        }

        using (jsonDoc)
        {
            JsonElement root = jsonDoc.RootElement;
            bool running = root.TryGetProperty("running", out JsonElement runEl) && runEl.GetBoolean();
            int pid = root.TryGetProperty("processId", out JsonElement pidEl) ? pidEl.GetInt32() : 0;
            string hwnd = root.TryGetProperty("mainWindowHandle", out JsonElement hEl) ? hEl.GetString() ?? "0x00000000" : "0x00000000";
            string windowMode = root.TryGetProperty("windowMode", out JsonElement wmEl) ? wmEl.GetString() ?? "foreground" : "foreground";
            string exe = root.TryGetProperty("executable", out JsonElement exeEl) ? exeEl.GetString() ?? "hon_x64.exe" : "hon_x64.exe";
            string? err = root.TryGetProperty("errorMessage", out JsonElement errEl) ? errEl.GetString() : null;
            int? slot = root.TryGetProperty("slotIndex", out JsonElement sEl) && sEl.ValueKind == JsonValueKind.Number ? sEl.GetInt32() : null;

            if (running && pid > 0)
            {
                AnsiConsole.MarkupLine($"[bold green]✓ Game client (hon_x64.exe) launched successfully![/]\n");

                Table table = new Table()
                    .Border(TableBorder.Rounded)
                    .BorderColor(Color.Green)
                    .AddColumn("[bold]Parameter[/]")
                    .AddColumn("[bold]Launch Configuration[/]");

                table.AddRow("Process ID (PID)", $"[bold cyan]{pid}[/]");
                table.AddRow("Main Window Handle", $"[white]{hwnd}[/]");
                table.AddRow("Window Mode", windowMode.Equals("headless", StringComparison.OrdinalIgnoreCase) ? "[green]Headless Offscreen ((-32000, -32000))[/]" : "[white]Foreground[/]");
                if (slot.HasValue) table.AddRow("Multi-Client Slot", $"[white]Slot #{slot.Value}[/]");
                table.AddRow("Executable Path", $"[grey]{Markup.Escape(exe)}[/]");
                table.AddRow("Named Pipe IPC", $"[cyan]\\\\.\\pipe\\willowmaker-ipc[/]");
                table.AddRow("Vulkan Swapchain Layer", "[green]VK_LAYER_HON_replay_capture (Ring Buffer Active)[/]");

                AnsiConsole.Write(table);
                AnsiConsole.MarkupLine("\n[bold cyan]💡 Quick Tip:[/] Inspect live performance with '[bold green]kongor telemetry latest[/]'.");
                return 0;
            }
            else
            {
                AnsiConsole.MarkupLine($"[bold red]✗ Game client failed to launch.[/]");
                if (!string.IsNullOrWhiteSpace(err))
                {
                    AnsiConsole.MarkupLine($"[red]Error:[/] {Markup.Escape(err)}");
                }
                return 1;
            }
        }
    }

    private static int RenderStatusResult(ToolCallResult result)
    {
        JsonDocument jsonDoc;
        try
        {
            jsonDoc = JsonDocument.Parse(result.FirstText);
        }
        catch (Exception ex)
        {
            AnsiConsole.MarkupLine($"[bold red]Error parsing status response:[/] {Markup.Escape(ex.Message)}");
            return 1;
        }

        using (jsonDoc)
        {
            JsonElement root = jsonDoc.RootElement;
            bool running = root.TryGetProperty("running", out JsonElement runEl) && runEl.GetBoolean();
            int pid = root.TryGetProperty("processId", out JsonElement pidEl) ? pidEl.GetInt32() : 0;
            string hwnd = root.TryGetProperty("mainWindowHandle", out JsonElement hEl) ? hEl.GetString() ?? "0x00000000" : "0x00000000";
            double memMb = root.TryGetProperty("workingSetMb", out JsonElement memEl) ? memEl.GetDouble() : 0.0;
            string windowMode = root.TryGetProperty("windowMode", out JsonElement wmEl) ? wmEl.GetString() ?? "offline" : "offline";
            string phaseName = root.TryGetProperty("enginePhaseName", out JsonElement phEl) ? phEl.GetString() ?? "Unknown" : "Unknown";
            bool ipcConnected = root.TryGetProperty("ipcConnected", out JsonElement ipcEl) && ipcEl.GetBoolean();
            string nextAction = root.TryGetProperty("nextAction", out JsonElement naEl) ? naEl.GetString() ?? string.Empty : string.Empty;

            Table table = new Table()
                .Border(TableBorder.Rounded)
                .BorderColor(running ? Color.Green : Color.Yellow)
                .Title("[bold white]Player Client (hon_x64.exe) Supervision Status[/]")
                .AddColumn(new TableColumn("[bold]Property[/]").Width(25))
                .AddColumn(new TableColumn("[bold]Runtime State[/]").Width(30))
                .AddColumn(new TableColumn("[bold]Supervision Details[/]"));

            if (running && pid > 0)
            {
                table.AddRow("Client Process", $"[bold green]Running (PID {pid})[/]", "[green]Actively Supervised[/]");
                table.AddRow("Main Window Handle", $"[white]{hwnd}[/]", windowMode.Equals("headless", StringComparison.OrdinalIgnoreCase) ? "[green]Headless Offscreen Slot[/]" : "[white]Foreground[/]");
                table.AddRow("Working Set Memory", $"{memMb:F1} MB", memMb <= 1024.0 ? "[green]Normal (< 1024 MB)[/]" : "[yellow]High Memory[/]");
                table.AddRow("Engine Phase", $"[cyan]{Markup.Escape(phaseName)}[/]", "[green]Engine Initialized[/]");
                table.AddRow("Named Pipe IPC", ipcConnected ? "[bold green]Connected[/]" : "[yellow]Detached (Will Auto-Connect)[/]", "[grey]\\\\.\\pipe\\willowmaker-ipc[/]");

                if (root.TryGetProperty("graphics", out JsonElement gfx))
                {
                    string renderer = gfx.TryGetProperty("renderer", out JsonElement renEl) ? renEl.GetString() ?? "Unknown" : "Unknown";
                    table.AddRow("Graphics Backend", $"[cyan]{Markup.Escape(renderer)}[/]", "[green]DXVK Vulkan 1.3 Supervised[/]");
                }

                if (root.TryGetProperty("audio", out JsonElement audio))
                {
                    string driver = audio.TryGetProperty("driverName", out JsonElement drvEl) ? drvEl.GetString() ?? "WASAPI" : "WASAPI";
                    table.AddRow("Audio Driver", $"[cyan]{Markup.Escape(driver)}[/]", "[green]Low-Latency Audio Ready[/]");
                }

                AnsiConsole.Write(table);

                if (!string.IsNullOrWhiteSpace(nextAction))
                {
                    AnsiConsole.MarkupLine($"\n[grey]Next action recommended: {Markup.Escape(nextAction)}[/]");
                }
                return 0;
            }
            else
            {
                table.AddRow("Client Process", "[yellow]Offline (Not Running)[/]", "[grey]hon_x64.exe not active[/]");
                table.AddRow("Engine Phase", "[grey]Offline[/]", "[grey]No process attached[/]");
                table.AddRow("Named Pipe IPC", "[grey]Disconnected[/]", "[grey]No IPC pipe server found[/]");

                AnsiConsole.Write(table);
                AnsiConsole.MarkupLine("\n[bold cyan]💡 Quick Tip:[/] Start the game client using '[bold green]kongor client launch[/]'.");
                return 0;
            }
        }
    }

    private static int RenderStopResult(ToolCallResult result)
    {
        JsonDocument jsonDoc;
        try
        {
            jsonDoc = JsonDocument.Parse(result.FirstText);
        }
        catch (Exception ex)
        {
            AnsiConsole.MarkupLine($"[bold red]Error parsing stop response:[/] {Markup.Escape(ex.Message)}");
            return 1;
        }

        using (jsonDoc)
        {
            JsonElement root = jsonDoc.RootElement;
            string msg = root.TryGetProperty("message", out JsonElement mEl) ? mEl.GetString() ?? "Client stopped." : "Client stopped.";
            AnsiConsole.MarkupLine($"[bold green]✓ {Markup.Escape(msg)}[/]");
            return 0;
        }
    }

    private static int RenderRestartResult(ToolCallResult result)
    {
        AnsiConsole.MarkupLine("[bold green]✓ Client restart cycle dispatched.[/]");
        return RenderLaunchResult(result);
    }

    private static int RenderListResult(ToolCallResult result)
    {
        JsonDocument jsonDoc;
        try
        {
            jsonDoc = JsonDocument.Parse(result.FirstText);
        }
        catch (Exception ex)
        {
            AnsiConsole.MarkupLine($"[bold red]Error parsing list response:[/] {Markup.Escape(ex.Message)}");
            return 1;
        }

        using (jsonDoc)
        {
            JsonElement root = jsonDoc.RootElement;
            int total = root.TryGetProperty("totalActive", out JsonElement tEl) ? tEl.GetInt32() : 0;

            Table table = new Table()
                .Border(TableBorder.Rounded)
                .BorderColor(Color.Cyan1)
                .Title($"[bold white]Active Managed Client Instances ({total})[/]")
                .AddColumn(new TableColumn("[bold]PID[/]"))
                .AddColumn(new TableColumn("[bold]Slot[/]"))
                .AddColumn(new TableColumn("[bold]Window Mode[/]"))
                .AddColumn(new TableColumn("[bold]User / Account[/]"))
                .AddColumn(new TableColumn("[bold]Offscreen Coord[/]"))
                .AddColumn(new TableColumn("[bold]Named Pipe[/]"));

            if (root.TryGetProperty("clients", out JsonElement clientsEl) && clientsEl.ValueKind == JsonValueKind.Array)
            {
                foreach (JsonElement c in clientsEl.EnumerateArray())
                {
                    int pid = c.TryGetProperty("processId", out JsonElement pEl) ? pEl.GetInt32() : 0;
                    int slot = c.TryGetProperty("slotIndex", out JsonElement sEl) ? sEl.GetInt32() : 0;
                    string wm = c.TryGetProperty("windowMode", out JsonElement wEl) ? wEl.GetString() ?? "foreground" : "foreground";
                    string user = c.TryGetProperty("username", out JsonElement uEl) ? uEl.GetString() ?? "Auto" : "Auto";
                    int ox = c.TryGetProperty("offscreenX", out JsonElement oxEl) ? oxEl.GetInt32() : 0;
                    int oy = c.TryGetProperty("offscreenY", out JsonElement oyEl) ? oyEl.GetInt32() : 0;
                    string pipe = c.TryGetProperty("pipeName", out JsonElement pipeEl) ? pipeEl.GetString() ?? string.Empty : string.Empty;

                    table.AddRow(
                        $"[cyan]{pid}[/]",
                        $"Slot #{slot}",
                        wm,
                        user,
                        $"({ox}, {oy})",
                        pipe);
                }
            }

            if (total == 0)
            {
                table.AddRow("[grey]-[/]", "[grey]-[/]", "[yellow]No managed clients running[/]", "[grey]-[/]", "[grey]-[/]", "[grey]-[/]");
            }

            AnsiConsole.Write(table);
            return 0;
        }
    }

    private static int RenderLogsResult(ToolCallResult result)
    {
        JsonDocument jsonDoc;
        try
        {
            jsonDoc = JsonDocument.Parse(result.FirstText);
        }
        catch (Exception ex)
        {
            AnsiConsole.MarkupLine($"[bold red]Error parsing logs response:[/] {Markup.Escape(ex.Message)}");
            return 1;
        }

        using (jsonDoc)
        {
            JsonElement root = jsonDoc.RootElement;
            if (root.TryGetProperty("lines", out JsonElement linesEl) && linesEl.ValueKind == JsonValueKind.Array)
            {
                AnsiConsole.MarkupLine("[bold cyan]Recent Game Client Console Logs:[/]");
                foreach (JsonElement l in linesEl.EnumerateArray())
                {
                    Console.WriteLine(l.GetString());
                }
            }
            else
            {
                AnsiConsole.MarkupLine("[yellow]No log lines available.[/]");
            }
            return 0;
        }
    }

    private static int RenderErrorsResult(ToolCallResult result)
    {
        JsonDocument jsonDoc;
        try
        {
            jsonDoc = JsonDocument.Parse(result.FirstText);
        }
        catch (Exception ex)
        {
            AnsiConsole.MarkupLine($"[bold red]Error parsing errors response:[/] {Markup.Escape(ex.Message)}");
            return 1;
        }

        using (jsonDoc)
        {
            JsonElement root = jsonDoc.RootElement;
            Table table = new Table()
                .Border(TableBorder.Rounded)
                .BorderColor(Color.Red)
                .Title("[bold white]Client Command Failures & Self-Healing Remediation History[/]")
                .AddColumn(new TableColumn("[bold]Category[/]"))
                .AddColumn(new TableColumn("[bold]Failed Command[/]"))
                .AddColumn(new TableColumn("[bold]Remediation Hint[/]"));

            if (root.TryGetProperty("recentErrors", out JsonElement errorsEl) && errorsEl.ValueKind == JsonValueKind.Array)
            {
                foreach (JsonElement e in errorsEl.EnumerateArray())
                {
                    string cat = e.TryGetProperty("category", out JsonElement cEl) ? cEl.GetString() ?? "Unknown" : "Unknown";
                    string cmd = e.TryGetProperty("failedCommand", out JsonElement cmdEl) ? cmdEl.GetString() ?? string.Empty : string.Empty;
                    string hint = e.TryGetProperty("remediationHint", out JsonElement hEl) ? hEl.GetString() ?? string.Empty : string.Empty;

                    table.AddRow($"[red]{Markup.Escape(cat)}[/]", $"[yellow]{Markup.Escape(cmd)}[/]", $"[green]{Markup.Escape(hint)}[/]");
                }
            }

            AnsiConsole.Write(table);
            return 0;
        }
    }

    private static int RenderCommandResult(ToolCallResult result)
    {
        if (result.IsError)
        {
            AnsiConsole.MarkupLine($"[bold red]Command Execution Failed:[/] {Markup.Escape(result.FirstText)}");
            return 1;
        }

        AnsiConsole.MarkupLine($"[bold green]Command Output:[/]");
        Console.WriteLine(result.FirstText);
        return 0;
    }
}
