// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// TAO (Together As One) MCP & Kongor CLI -- Created by guccidonut (https://github.com/guccidonut/PKV)

using System;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using Kongor.Core.Services.Native;
using Spectre.Console;
using Spectre.Console.Cli;
using WILLOWMAKER.Core.Services;

namespace Kongor.Cli.Commands;

public sealed class PatchCommandSettings : CommandSettings
{
    [CommandArgument(0, "[action]")]
    [Description("Action to perform: 'status', 'apply', 'rollback', 'redirect-iat', 'upgrade-zlib', or 'rollback-zlib' (default: 'status')")]
    public string? Action { get; init; }

    [CommandOption("-t|--tier <TIER>")]
    [Description("Target rollback tier: 'previous' (.bak, default) or 'old' (.bak.old, historical baseline)")]
    public string? Tier { get; init; }

    [CommandOption("-d|--dir|--game-dir <PATH>")]
    [Description("Path to game client directory (defaults to auto-discovered games/client)")]
    public string? GameDirectory { get; init; }

    [CommandOption("-b|--binary <NAME>")]
    [Description("Target binary name for IAT redirection (default: 'k2_x64.dll')")]
    public string TargetBinary { get; init; } = "k2_x64.dll";

    [CommandOption("--old-dll <NAME>")]
    [Description("Original imported DLL name to redirect (e.g. 'libssl-1_1-x64.dll')")]
    public string? OldDll { get; init; }

    [CommandOption("--new-dll <NAME>")]
    [Description("Replacement imported DLL name (e.g. 'kongor_ssl_x64.dll')")]
    public string? NewDll { get; init; }

    [CommandOption("--skip-process-check")]
    [Description("Bypass running process lock detection (for testing or automation)")]
    public bool SkipProcessCheck { get; init; }
}

public sealed class PatchCommand : Command<PatchCommandSettings>
{
    public override int Execute(CommandContext context, PatchCommandSettings settings)
    {
        string action = (settings.Action ?? "status").Trim().ToLowerInvariant();
        string repoRoot = Program.FindRepoRoot();
        string gameDir = ResolveGameDirectory(repoRoot, settings.GameDirectory);

        AnsiConsole.Write(new Rule("[bold cyan]Native C++ Engine Patching & Import Address Table (IAT) Governance[/]").LeftJustified());

        switch (action)
        {
            case "status":
                return ExecuteStatus(gameDir);

            case "apply":
                return ExecuteApply(gameDir, settings.SkipProcessCheck);

            case "rollback":
                return ExecuteRollback(gameDir, settings.Tier ?? "previous", settings.SkipProcessCheck);

            case "redirect-iat":
            case "iat":
            case "redirect":
                return ExecuteRedirectIat(gameDir, settings);

            case "upgrade-zlib":
            case "zlib-upgrade":
                return ExecuteUpgradeZlib(gameDir, settings.SkipProcessCheck);

            case "rollback-zlib":
            case "zlib-rollback":
                return ExecuteRollbackZlib(gameDir, settings.Tier ?? "previous", settings.SkipProcessCheck);

            default:
                AnsiConsole.MarkupLine($"[bold red]Error:[/] Unknown action '[yellow]{action}[/]'. Valid actions: status, apply, rollback, redirect-iat, upgrade-zlib, rollback-zlib.");
                return 1;
        }
    }

    private static int ExecuteStatus(string gameDir)
    {
        if (!Directory.Exists(gameDir))
        {
            AnsiConsole.MarkupLine($"[bold red]Error:[/] Game directory not found at '[yellow]{Markup.Escape(gameDir)}[/]'.");
            return 1;
        }

        EnginePatchStatusReport report = EnginePatchService.GetEnginePatchStatus(gameDir);

        var table = new Table()
            .Border(TableBorder.Rounded)
            .Title($"[bold white]Engine Binary Patch & IAT Status — {Markup.Escape(Path.GetFileName(gameDir))}[/]")
            .AddColumn("[bold]Binary[/]")
            .AddColumn("[bold]Size[/]")
            .AddColumn("[bold]SHA-256[/]")
            .AddColumn("[bold]FPS Unclamp[/]")
            .AddColumn("[bold]UserDir[/]")
            .AddColumn("[bold]Crash Guards[/]")
            .AddColumn("[bold]Backups[/]")
            .AddColumn("[bold]Imported DLLs[/]");

        foreach (EnginePatchStatusItem item in report.Binaries)
        {
            string fpsStatus = item.IsFpsUnclamped ? "[bold green]1000 FPS[/]" : "[grey]250 FPS[/]";
            string userDirStatus = item.IsUserDirRedirected ? "[bold green]userdata/[/]" : "[grey]Documents[/]";
            string crashStatus = item.AppliedCrashFixes.Count > 0
                ? $"[bold green]{item.AppliedCrashFixes.Count} applied[/]"
                : (item.PendingCrashFixes.Count > 0 ? $"[bold yellow]{item.PendingCrashFixes.Count} pending[/]" : "[grey]N/A[/]");

            string bakStatus = (item.HasBak ? "[green].bak[/] " : "[grey]- [/]") +
                               (item.HasBakOld ? "[cyan].bak.old[/]" : "[grey]-[/]");

            string sha = string.IsNullOrEmpty(item.Sha256Hash)
                ? "[grey]-[/]"
                : $"[grey]{item.Sha256Hash[..8]}...[/]";

            string importedSummary = item.ImportedDllNames.Count > 0
                ? $"[cyan]{item.ImportedDllNames.Count} modules[/]"
                : "[grey]None[/]";

            table.AddRow(
                $"[white]{Markup.Escape(item.RelativePath)}[/]",
                item.Exists ? $"{item.SizeBytes:N0} B" : "[grey]Missing[/]",
                sha,
                fpsStatus,
                userDirStatus,
                crashStatus,
                bakStatus,
                importedSummary
            );
        }

        AnsiConsole.Write(table);

        if (report.NativeLibraries != null && report.NativeLibraries.Count > 0)
        {
            var nativeTable = new Table()
                .Border(TableBorder.Rounded)
                .Title("[bold white]Native Shared Libraries & Security Status (Phase 1 Modernization)[/]")
                .AddColumn("[bold]Binary / Target[/]")
                .AddColumn("[bold]Size[/]")
                .AddColumn("[bold]Version[/]")
                .AddColumn("[bold]Security & CVE Status[/]")
                .AddColumn("[bold]C-ABI Gate[/]")
                .AddColumn("[bold]Backups[/]");

            foreach (var nat in report.NativeLibraries)
            {
                string abiStatus = nat.IsAbiVerified
                    ? "[bold green]✓ Verified[/]"
                    : (nat.Exists ? "[bold red]✗ Failed[/]" : "[grey]-[/]");

                string secFormatted = nat.SecurityStatus.StartsWith("SECURE", StringComparison.OrdinalIgnoreCase)
                    ? $"[bold green]{Markup.Escape(nat.SecurityStatus)}[/]"
                    : (nat.SecurityStatus.StartsWith("VULNERABLE", StringComparison.OrdinalIgnoreCase)
                        ? $"[bold red]{Markup.Escape(nat.SecurityStatus)}[/]"
                        : $"[yellow]{Markup.Escape(nat.SecurityStatus)}[/]");

                string bakStatus = (nat.HasBak ? "[green].bak[/] " : "[grey]- [/]") +
                                   (nat.HasBakOld ? "[cyan].bak.old[/]" : "[grey]-[/]");

                nativeTable.AddRow(
                    $"[white]{Markup.Escape(nat.RelativePath)}[/]",
                    nat.Exists ? $"{nat.SizeBytes:N0} B" : "[grey]Missing[/]",
                    $"[cyan]{Markup.Escape(nat.FileVersion)}[/]",
                    secFormatted,
                    abiStatus,
                    bakStatus
                );
            }

            AnsiConsole.WriteLine();
            AnsiConsole.Write(nativeTable);
        }

        // Crash Fix Details
        AnsiConsole.MarkupLine("\n[bold cyan]Registered Machine-Code Crash Mitigations:[/]");
        foreach (BinaryPatchDefinition fix in EnginePatchService.RegisteredCrashFixes)
        {
            AnsiConsole.MarkupLine($"  • [bold white]{Markup.Escape(fix.Name)}[/] ([yellow]{fix.Id}[/]) → [grey]{Markup.Escape(fix.Description)}[/]");
        }

        if (NativeBinaryManager.IsGameOrServerRunning(out string runningProcs))
        {
            AnsiConsole.MarkupLine($"\n[bold yellow]⚠️  Running Game Processes Detected:[/] [white]{Markup.Escape(runningProcs)}[/]");
        }
        else
        {
            AnsiConsole.MarkupLine("\n[bold green]✓  Process Safety:[/] Zero game client or server locks active.");
        }

        return 0;
    }

    private static int ExecuteApply(string gameDir, bool skipProcessCheck)
    {
        if (!skipProcessCheck && NativeBinaryManager.IsGameOrServerRunning(out string runningProcs))
        {
            AnsiConsole.MarkupLine($"[bold red]Error:[/] Cannot patch binaries while game processes are active ([yellow]{Markup.Escape(runningProcs)}[/]). Terminate them first or pass [yellow]--skip-process-check[/].");
            return 1;
        }

        AnsiConsole.MarkupLine($"[bold green]Applying engine tuning, machine-code crash guards, and IAT verifications to:[/] [white]{Markup.Escape(gameDir)}[/]\n");

        bool success = EnginePatchService.EnsureEnginePatched(gameDir, msg =>
        {
            AnsiConsole.MarkupLine($"  [grey]>[/] {Markup.Escape(msg)}");
        });

        if (success)
        {
            AnsiConsole.MarkupLine("\n[bold green]✓ Engine binaries successfully patched and hardened with two-tier backup protection.[/]");
            return 0;
        }

        AnsiConsole.MarkupLine("\n[bold yellow]⚠️ Some binaries could not be patched (see log output above).[/]");
        return 1;
    }

    private static int ExecuteRollback(string gameDir, string tier, bool skipProcessCheck)
    {
        if (!skipProcessCheck && NativeBinaryManager.IsGameOrServerRunning(out string runningProcs))
        {
            AnsiConsole.MarkupLine($"[bold red]Error:[/] Cannot rollback binaries while game processes are active ([yellow]{Markup.Escape(runningProcs)}[/]). Terminate them first or pass [yellow]--skip-process-check[/].");
            return 1;
        }

        AnsiConsole.MarkupLine($"[bold yellow]Rolling back engine binaries to tier '{tier}' in:[/] [white]{Markup.Escape(gameDir)}[/]\n");

        RollbackResult result = EnginePatchService.RollbackEnginePatches(gameDir, tier, msg =>
        {
            AnsiConsole.MarkupLine($"  [grey]>[/] {Markup.Escape(msg)}");
        });

        if (result.Success)
        {
            AnsiConsole.MarkupLine($"\n[bold green]✓ {Markup.Escape(result.Message)}[/]");
            return 0;
        }

        AnsiConsole.MarkupLine($"\n[bold red]✗ {Markup.Escape(result.Message)}[/]");
        return 1;
    }

    private static int ExecuteRedirectIat(string gameDir, PatchCommandSettings settings)
    {
        if (string.IsNullOrWhiteSpace(settings.OldDll) || string.IsNullOrWhiteSpace(settings.NewDll))
        {
            AnsiConsole.MarkupLine("[bold red]Error:[/] Both [yellow]--old-dll[/] and [yellow]--new-dll[/] arguments are required for IAT redirection.");
            AnsiConsole.MarkupLine("Example: [white]kongor patch redirect-iat --binary k2_x64.dll --old-dll libssl-1_1-x64.dll --new-dll kongor_ssl_x64.dll[/]");
            return 1;
        }

        string targetBinary = settings.TargetBinary;
        string targetFullPath = Path.Combine(gameDir, targetBinary);

        if (!File.Exists(targetFullPath))
        {
            // Check windows-x64 subdirectory
            string subPath = Path.Combine(gameDir, "windows-x64", targetBinary);
            if (File.Exists(subPath))
            {
                targetFullPath = subPath;
            }
            else
            {
                AnsiConsole.MarkupLine($"[bold red]Error:[/] Target binary '{targetBinary}' not found in '{gameDir}'.");
                return 1;
            }
        }

        AnsiConsole.MarkupLine($"[bold cyan]Redirecting IAT import in:[/] [white]{Markup.Escape(Path.GetFileName(targetFullPath))}[/]");
        AnsiConsole.MarkupLine($"  [grey]From:[/] [yellow]{Markup.Escape(settings.OldDll)}[/]");
        AnsiConsole.MarkupLine($"  [grey]To:  [/] [green]{Markup.Escape(settings.NewDll)}[/]\n");

        try
        {
            PeRedirectionResult result = PeImportAddressTableService.RedirectImportedDll(
                targetFullPath,
                settings.OldDll,
                settings.NewDll,
                createBackup: true,
                skipProcessCheck: settings.SkipProcessCheck);

            if (result.Success)
            {
                AnsiConsole.MarkupLine($"[bold green]✓ {Markup.Escape(result.Message)}[/]");
                if (result.UsedCavitySlack)
                {
                    AnsiConsole.MarkupLine("  [grey]Note: Redirection allocated from section alignment slack cavity.[/]");
                }
                if (result.BackupPath != null)
                {
                    AnsiConsole.MarkupLine($"  [grey]Backup saved to: '{Markup.Escape(result.BackupPath)}'[/]");
                }
                return 0;
            }

            AnsiConsole.MarkupLine($"[bold red]✗ Redirection failed: {Markup.Escape(result.Message)}[/]");
            return 1;
        }
        catch (Exception ex)
        {
            AnsiConsole.MarkupLine($"[bold red]Exception:[/] {Markup.Escape(ex.Message)}");
            return 1;
        }
    }

    private static int ExecuteUpgradeZlib(string gameDir, bool skipProcessCheck)
    {
        string? candidate = NativeZlibBridge.TryDiscoverZlibPath();
        if (candidate == null || !File.Exists(candidate))
        {
            AnsiConsole.MarkupLine("[bold red]Error:[/] Candidate zlib 1.3.1 binary not found in repository artifacts.");
            return 1;
        }

        AnsiConsole.MarkupLine($"[bold cyan]Upgrading native zlib across targets in:[/] [white]{Markup.Escape(gameDir)}[/]");
        AnsiConsole.MarkupLine($"  [grey]Candidate:[/] [green]{Markup.Escape(candidate)}[/]\n");

        try
        {
            var result = NativeBinaryManager.UpgradeZlib(gameDir, candidate, skipProcessCheck: skipProcessCheck);
            if (result.Success)
            {
                AnsiConsole.MarkupLine($"[bold green]✓ {Markup.Escape(result.Message)}[/]");
                return 0;
            }

            AnsiConsole.MarkupLine($"[bold red]✗ Upgrade failed: {Markup.Escape(result.Message)}[/]");
            return 1;
        }
        catch (Exception ex)
        {
            AnsiConsole.MarkupLine($"[bold red]Exception during upgrade:[/] {Markup.Escape(ex.Message)}");
            return 1;
        }
    }

    private static int ExecuteRollbackZlib(string gameDir, string tier, bool skipProcessCheck)
    {
        AnsiConsole.MarkupLine($"[bold yellow]Rolling back native zlib to tier '{tier}' in:[/] [white]{Markup.Escape(gameDir)}[/]\n");
        try
        {
            var result = NativeBinaryManager.RollbackZlib(gameDir, targetTier: tier, skipProcessCheck: skipProcessCheck);
            if (result.Success)
            {
                AnsiConsole.MarkupLine($"[bold green]✓ {Markup.Escape(result.Message)}[/]");
                return 0;
            }

            AnsiConsole.MarkupLine($"[bold red]✗ Rollback failed: {Markup.Escape(result.Message)}[/]");
            return 1;
        }
        catch (Exception ex)
        {
            AnsiConsole.MarkupLine($"[bold red]Exception during rollback:[/] {Markup.Escape(ex.Message)}");
            return 1;
        }
    }

    private static string ResolveGameDirectory(string repoRoot, string? explicitDir)
    {
        if (!string.IsNullOrWhiteSpace(explicitDir))
        {
            return Path.GetFullPath(explicitDir);
        }

        string candidate1 = Path.Combine(repoRoot, "games", "client");
        if (Directory.Exists(candidate1)) return candidate1;

        string candidate2 = Path.Combine(repoRoot, "games", "client", "windows-x64");
        if (Directory.Exists(candidate2)) return candidate2;

        return repoRoot;
    }
}
