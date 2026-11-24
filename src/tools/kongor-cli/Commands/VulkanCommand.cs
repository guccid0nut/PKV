// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// TAO (Together As One) MCP & Kongor CLI -- Created by guccidonut (https://github.com/guccidonut/PKV)

// ============================================================================
// File: VulkanCommand.cs
// System: Kongor.Cli - Vulkan 1.3 & DXVK Translation Governance Command
// Policy: Strict Explicit Typing (NO 'var')
// ============================================================================

using System;
using System.ComponentModel;
using System.IO;
using System.Text.Json;
using System.Threading.Tasks;
using Kongor.Cli.Mcp.Services;
using Spectre.Console;
using Spectre.Console.Cli;
using WILLOWMAKER.Core.Services;

namespace Kongor.Cli.Commands;

/// <summary>
///     Settings for 'kongor vulkan' CLI command.
/// </summary>
public sealed class VulkanCommandSettings : CommandSettings
{
    [CommandArgument(0, "[action]")]
    [Description("Action to perform: 'status', 'upgrade', 'purge-cache', 'profile', or 'rollback-config' (default: 'status')")]
    public string? Action { get; init; }

    [CommandArgument(1, "[profile]")]
    [Description("Target profile for 'profile' action: 'competitive' (default), 'balanced', or 'ultra'")]
    public string? Profile { get; init; }

    [CommandOption("-t|--target <TARGET>")]
    [Description("Target architecture: 'client' (default) or 'server'")]
    public string? Target { get; init; }

    [CommandOption("-d|--dir|--game-dir <PATH>")]
    [Description("Path to target game client or dedicated server directory (optional)")]
    public string? GameDirectory { get; init; }

    [CommandOption("-p|--preset <PRESET>")]
    [Description("Ray tracing / DXVK preset: 'competitive' (default), 'balanced', or 'ultra'")]
    public string? Preset { get; init; }

    [CommandOption("--tier <TIER>")]
    [Description("Rollback tier for 'rollback-config': 'previous' (default) or 'baseline'")]
    public string? Tier { get; init; }

    [CommandOption("--json")]
    [Description("Output results as formatted JSON (for scripts and automation)")]
    public bool Json { get; init; }
}

/// <summary>
///     Spectre.Console CLI command for inspecting, verifying, upgrading, and purging
///     Vulkan 1.3 / DXVK translation layers for game clients and dedicated servers.
/// </summary>
public sealed class VulkanCommand : AsyncCommand<VulkanCommandSettings>
{
    public override async Task<int> ExecuteAsync(CommandContext context, VulkanCommandSettings settings)
    {
        string action = (settings.Action ?? "status").Trim().ToLowerInvariant();
        string repoRoot = Program.FindRepoRoot();
        bool isServer = (settings.Target ?? string.Empty).Equals("server", StringComparison.OrdinalIgnoreCase);
        string targetDir = ResolveTargetDirectory(repoRoot, settings.GameDirectory, isServer);

        if (!settings.Json)
        {
            AnsiConsole.Write(new Rule("[bold cyan]Vulkan 1.3 & DXVK Translation Subsystem Governance[/]").LeftJustified());
        }

        switch (action)
        {
            case "status":
            case "check":
            case "inspect":
                return ExecuteStatus(targetDir, isServer, settings.Json);

            case "upgrade":
            case "apply":
            case "install":
                return await ExecuteUpgradeAsync(targetDir, settings.Preset, isServer, settings.Json);

            case "profile":
            case "set-profile":
                return ExecuteProfile(targetDir, settings.Profile ?? settings.Preset ?? "competitive", settings.Json);

            case "rollback-config":
            case "rollback_config":
            case "rollback":
                return ExecuteRollbackConfig(targetDir, settings.Tier ?? "previous", settings.Json);

            case "purge-cache":
            case "purge_cache":
            case "purge":
            case "clear-cache":
                return await ExecutePurgeCacheAsync(repoRoot, settings.Json);

            default:
                if (settings.Json)
                {
                    Console.Out.WriteLine(JsonSerializer.Serialize(new { status = "error", message = $"Unknown action '{action}'. Valid actions: status, upgrade, profile, rollback-config, purge-cache." }));
                }
                else
                {
                    AnsiConsole.MarkupLine($"[bold red]Error:[/] Unknown action '[yellow]{action}[/]'. Valid actions: status, upgrade, profile, rollback-config, purge-cache.");
                }
                return 1;
        }
    }

    private static int ExecuteStatus(string targetDir, bool isServer, bool json)
    {
        if (!Directory.Exists(targetDir))
        {
            if (json)
            {
                Console.Out.WriteLine(JsonSerializer.Serialize(new { status = "error", message = $"Target directory not found at '{targetDir}'." }));
            }
            else
            {
                AnsiConsole.MarkupLine($"[bold red]Error:[/] Target directory not found at '[yellow]{Markup.Escape(targetDir)}[/]'.");
            }
            return 1;
        }

        bool effectiveServer = isServer || VulkanDriverUpgradeService.IsServerDirectory(targetDir);
        string targetType = effectiveServer ? "Dedicated Server" : "Game Client";

        ClientGraphicsDriverReport report = VulkanDriverUpgradeService.InspectClient(targetDir, effectiveServer);

        if (json)
        {
            Console.Out.WriteLine(JsonSerializer.Serialize(new
            {
                status = "success",
                action = "status",
                target = effectiveServer ? "server" : "client",
                directory = targetDir,
                report
            }, new JsonSerializerOptions { WriteIndented = true }));
            return 0;
        }

        Table table = new Table()
            .Border(TableBorder.Rounded)
            .Title($"[bold white]Vulkan 1.3 / DXVK Diagnostic Report — {targetType} ({Markup.Escape(Path.GetFileName(targetDir))})[/]")
            .AddColumn("[bold]Component / Subsystem[/]")
            .AddColumn("[bold]Status[/]")
            .AddColumn("[bold]Details[/]");

        // 1. System Vulkan Loader
        table.AddRow(
            "[white]System Vulkan Loader[/]",
            report.HasSystemVulkanLoader ? "[bold green]Detected[/]" : "[bold red]Missing[/]",
            report.HasSystemVulkanLoader ? "vulkan-1.dll functional in System32 / GPU driver" : "Install GPU vendor driver or LunarG VulkanRT");

        // 2. DXVK d3d9.dll
        table.AddRow(
            "[white]DXVK D3D9 Translation[/]",
            report.HasDxvkD3D9 ? "[bold green]Installed[/]" : "[bold yellow]Missing / Legacy[/]",
            report.HasDxvkD3D9 ? "DXVK v3.0.2 (~7.2 MB) Direct3D 9 to Vulkan 1.3 translator" : "Native D3D9 or missing binary");

        // 3. PE DotLocal Redirection
        table.AddRow(
            "[white]DotLocal Redirection[/]",
            report.HasDotLocalRedirection ? "[bold green]Active[/]" : "[bold yellow]Not Configured[/]",
            report.HasDotLocalRedirection ? "hon_x64.exe.local marker forces Win32 DXVK priority" : "Missing hon_x64.exe.local marker");

        // 4. vid_d3d9_x64.dll Patch
        table.AddRow(
            "[white]Display Mode Patch[/]",
            report.IsVidDriverPatched ? "[bold green]Patched[/]" : "[bold yellow]Unpatched[/]",
            report.IsVidDriverPatched ? "vid_d3d9_x64.dll display mode failure bypass active" : "Display mode failure crash possible on alt-tab");

        // 5. k2_x64.dll Patch
        table.AddRow(
            "[white]High-FPS Engine Patch[/]",
            report.IsK2EnginePatched ? "[bold green]Patched[/]" : "[bold yellow]Unpatched[/]",
            report.IsK2EnginePatched ? "k2_x64.dll 1000 FPS clamp & frame unthrottled" : "Default 250 FPS clamp active");

        // 6. dxvk.conf Configuration
        table.AddRow(
            "[white]DXVK Configuration[/]",
            report.HasDxvkConfig ? "[bold green]Present[/]" : "[bold yellow]Missing[/]",
            report.HasDxvkConfig ? "dxvk.conf present (DirectFlip presentation & texture clarity)" : "Missing dxvk.conf");

        // 7. Base Shader Cache
        table.AddRow(
            "[white]DXVK Shader Cache[/]",
            report.HasBaseShaderCacheSeeded ? "[bold green]Seeded[/]" : "[bold yellow]Empty / Missing[/]",
            report.HasBaseShaderCacheSeeded ? "Pre-compiled pipeline state cache seeded (%LOCALAPPDATA%/dxvk)" : "Shaders will compile on first draw");

        // 8. Capture Layer
        if (effectiveServer)
        {
            table.AddRow(
                "[white]Replay Capture Layer[/]",
                "[grey]N/A (Server)[/]",
                "Dedicated server does not require Vulkan replay capture layer");
        }
        else
        {
            table.AddRow(
                "[white]Replay Capture Layer[/]",
                report.HasCaptureLayerDeployed ? "[bold green]Deployed[/]" : "[bold yellow]Not Deployed[/]",
                report.HasCaptureLayerDeployed ? "VkLayer_hon_replay_capture deployed for video renderer" : "Capture layer not deployed in client folder");
        }

        AnsiConsole.Write(table);

        if (report.RequiresUpgrade)
        {
            AnsiConsole.MarkupLine($"\n[bold yellow]Notice:[/] {Markup.Escape(report.StatusSummary)}");
            AnsiConsole.MarkupLine($"Run [bold cyan]kongor vulkan upgrade{(effectiveServer ? " --target server" : "")}[/] to automatically configure Vulkan 1.3 / DXVK.");
        }
        else
        {
            AnsiConsole.MarkupLine($"\n[bold green]✓ Subsystem Ready:[/] {Markup.Escape(report.StatusSummary)}");
        }

        return 0;
    }

    private static async Task<int> ExecuteUpgradeAsync(string targetDir, string? presetName, bool isServer, bool json)
    {
        if (!Directory.Exists(targetDir))
        {
            if (json)
            {
                Console.Out.WriteLine(JsonSerializer.Serialize(new { status = "error", message = $"Target directory not found at '{targetDir}'." }));
            }
            else
            {
                AnsiConsole.MarkupLine($"[bold red]Error:[/] Target directory not found at '[yellow]{Markup.Escape(targetDir)}[/]'.");
            }
            return 1;
        }

        bool effectiveServer = isServer || VulkanDriverUpgradeService.IsServerDirectory(targetDir);
        RayTracingPreset preset = ParsePreset(presetName);

        if (!json)
        {
            AnsiConsole.MarkupLine($"[bold cyan]Upgrading {(effectiveServer ? "Dedicated Server" : "Game Client")} to Vulkan 1.3 / DXVK...[/]");
            AnsiConsole.MarkupLine($"Target: [white]{Markup.Escape(targetDir)}[/]");
            AnsiConsole.MarkupLine($"Preset: [white]{preset}[/]");
        }

        VulkanUpgradeResult result = await VulkanDriverUpgradeService.UpgradeToVulkanAsync(
            targetDir,
            preset,
            json ? null : msg => AnsiConsole.MarkupLine($"  [grey]{Markup.Escape(msg)}[/]"),
            effectiveServer);

        if (json)
        {
            Console.Out.WriteLine(JsonSerializer.Serialize(new
            {
                status = result.Success ? "success" : "warning",
                action = "upgrade",
                target = effectiveServer ? "server" : "client",
                directory = targetDir,
                preset = preset.ToString(),
                result
            }, new JsonSerializerOptions { WriteIndented = true }));
            return result.Success ? 0 : 1;
        }

        AnsiConsole.WriteLine();
        if (result.Success)
        {
            AnsiConsole.MarkupLine($"[bold green]✓ {Markup.Escape(result.Message)}[/]");
        }
        else
        {
            AnsiConsole.MarkupLine($"[bold yellow]! {Markup.Escape(result.Message)}[/]");
        }

        AnsiConsole.WriteLine();
        ExecuteStatus(targetDir, effectiveServer, json: false);
        return result.Success ? 0 : 1;
    }

    private static async Task<int> ExecutePurgeCacheAsync(string repoRoot, bool json)
    {
        if (!json)
        {
            AnsiConsole.MarkupLine("[bold cyan]Purging DXVK state cache and pipeline cache files...[/]");
        }

        EngineCacheService cacheService = new();
        CachePurgeResult result = await cacheService.PurgeCacheAsync(
            ["dxvk"],
            hotReload: false,
            cleanBoms: false,
            repoRoot: repoRoot);

        if (json)
        {
            Console.Out.WriteLine(JsonSerializer.Serialize(new
            {
                status = result.Success ? "success" : "warning",
                action = "purge_cache",
                result
            }, new JsonSerializerOptions { WriteIndented = true }));
            return result.Success ? 0 : 1;
        }

        if (result.Success)
        {
            AnsiConsole.MarkupLine("[bold green]✓ DXVK cache purged successfully.[/]");
            AnsiConsole.MarkupLine($"Deleted [bold white]{result.FilesDeleted}[/] cache entries ({result.MbReclaimed:F2} MB reclaimed).");
            return 0;
        }

        string errorMsg = result.Errors.Count > 0 ? string.Join(", ", result.Errors) : "Purge encountered warnings.";
        AnsiConsole.MarkupLine($"[bold yellow]! Cache purge finished with notice: {Markup.Escape(errorMsg)}[/]");
        return 0;
    }

    private static int ExecuteProfile(string targetDir, string profileName, bool json)
    {
        if (!Directory.Exists(targetDir))
        {
            if (json)
            {
                Console.Out.WriteLine(JsonSerializer.Serialize(new { status = "error", message = $"Target directory not found at '{targetDir}'." }));
            }
            else
            {
                AnsiConsole.MarkupLine($"[bold red]Error:[/] Target directory not found at '[yellow]{Markup.Escape(targetDir)}[/]'.");
            }
            return 1;
        }

        Dxvk24Profile profile = profileName.Trim().ToLowerInvariant() switch
        {
            "ultra" or "ultrahdr" => Dxvk24Profile.UltraHDRVisual,
            "fsr" or "budgetfsr" or "upscaled" => Dxvk24Profile.BudgetFsrUpscaled,
            "balanced" => Dxvk24Profile.Standard,
            _ => Dxvk24Profile.CompetitiveLowLatency
        };

        RenderModernizationService renderService = new(Microsoft.Extensions.Logging.Abstractions.NullLogger<RenderModernizationService>.Instance);
        renderService.ApplyRenderModernization(targetDir, profile);

        DxvkStatusReport status = renderService.GetDxvkStatus(targetDir);

        if (json)
        {
            Console.Out.WriteLine(JsonSerializer.Serialize(new
            {
                status = "success",
                action = "profile",
                profile = profile.ToString(),
                directory = targetDir,
                dxvkStatus = status
            }, new JsonSerializerOptions { WriteIndented = true }));
            return 0;
        }

        AnsiConsole.MarkupLine($"[bold green]✓ Applied DXVK 3.0.2 Profile:[/] [bold white]{profile}[/] to [yellow]{Markup.Escape(targetDir)}[/]");
        AnsiConsole.MarkupLine($"[cyan]• Reflex Low-Latency:[/] [bold white]{(status.EnableReflex ? "ENABLED (dxvk.nvLowLatency=true)" : "DISABLED")}[/]");
        AnsiConsole.MarkupLine($"[cyan]• Frame Latency:[/] [bold white]maxFrameLatency={status.MaxFrameLatency}, latencyTolerance={status.LatencyTolerance}[/]");
        AnsiConsole.MarkupLine($"[cyan]• Rolling Backup:[/] [bold white]{(status.HasConfigBackup ? "Created dxvk.conf.bak" : "None")}[/]");
        return 0;
    }

    private static int ExecuteRollbackConfig(string targetDir, string tier, bool json)
    {
        if (!Directory.Exists(targetDir))
        {
            if (json)
            {
                Console.Out.WriteLine(JsonSerializer.Serialize(new { status = "error", message = $"Target directory not found at '{targetDir}'." }));
            }
            else
            {
                AnsiConsole.MarkupLine($"[bold red]Error:[/] Target directory not found at '[yellow]{Markup.Escape(targetDir)}[/]'.");
            }
            return 1;
        }

        RenderModernizationService renderService = new(Microsoft.Extensions.Logging.Abstractions.NullLogger<RenderModernizationService>.Instance);
        bool success = renderService.RollbackDxvkConfig(targetDir, tier);

        if (json)
        {
            Console.Out.WriteLine(JsonSerializer.Serialize(new
            {
                status = success ? "success" : "error",
                action = "rollback_config",
                tier,
                directory = targetDir
            }, new JsonSerializerOptions { WriteIndented = true }));
            return success ? 0 : 1;
        }

        if (success)
        {
            AnsiConsole.MarkupLine($"[bold green]✓ Successfully rolled back dxvk.conf[/] using tier '[bold white]{tier}[/]' in [yellow]{Markup.Escape(targetDir)}[/]");
            return 0;
        }

        AnsiConsole.MarkupLine($"[bold red]Error:[/] Rollback failed for tier '[yellow]{tier}[/]'. Backup file not found.");
        return 1;
    }

    private static string ResolveTargetDirectory(string repoRoot, string? explicitDir, bool isServer)
    {
        if (!string.IsNullOrWhiteSpace(explicitDir))
        {
            return Path.GetFullPath(explicitDir);
        }

        string subfolder = isServer ? "server" : "client";
        string candidate1 = Path.Combine(repoRoot, "games", subfolder);
        if (Directory.Exists(candidate1))
        {
            return candidate1;
        }

        string candidate2 = Path.Combine(repoRoot, "games", subfolder, "windows-x64");
        if (Directory.Exists(candidate2))
        {
            return candidate2;
        }

        return repoRoot;
    }

    private static RayTracingPreset ParsePreset(string? presetName)
    {
        if (string.IsNullOrWhiteSpace(presetName))
        {
            return RayTracingPreset.Competitive;
        }

        return presetName.Trim().ToLowerInvariant() switch
        {
            "ultra" => RayTracingPreset.Ultra,
            "balanced" => RayTracingPreset.Balanced,
            _ => RayTracingPreset.Competitive
        };
    }
}
