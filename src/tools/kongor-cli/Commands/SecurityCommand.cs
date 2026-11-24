// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// TAO (Together As One) MCP & Kongor CLI -- Created by guccidonut (https://github.com/guccidonut/PKV)

// ============================================================================
// File: SecurityCommand.cs
// System: Kongor CLI - Native C++ Modernization & Engine Hardening
// Subsystem: Phase 4 Modern TLS 1.3 ABI Proxy Shim & libcurl 8.8+ Upgrade
// ============================================================================

using System;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Kongor.Core.Services.Native;
using Spectre.Console;
using Spectre.Console.Cli;

namespace Kongor.Cli.Commands;

public sealed class SecurityCommandSettings : CommandSettings
{
    [CommandArgument(0, "[action]")]
    [Description("Action to perform: 'status', 'upgrade', 'rollback', or 'verify-handshake' (default: 'status')")]
    public string? Action { get; init; }

    [CommandOption("-t|--tier <TIER>")]
    [Description("Target rollback tier: 'previous' (.bak, default) or 'old' (.bak.old, historical baseline)")]
    public string? Tier { get; init; }

    [CommandOption("-d|--dir|--game-dir <PATH>")]
    [Description("Path to game directory or repository root (defaults to auto-discovered games/client)")]
    public string? GameDirectory { get; init; }

    [CommandOption("-u|--url <URL>")]
    [Description("Target HTTPS endpoint for TLS 1.3 handshake verification (default: 'https://masterserver.naeu.heroesofnewerth.com')")]
    public string TargetUrl { get; init; } = "https://masterserver.naeu.heroesofnewerth.com";

    [CommandOption("-f|--fleet <FLEET>")]
    [Description("Target fleet scope: 'client', 'server', or 'all' (default: auto-detected)")]
    public string? Fleet { get; init; }

    [CommandOption("--skip-process-check")]
    [Description("Bypass running process lock detection (for testing or automation)")]
    public bool SkipProcessCheck { get; init; }

    [CommandOption("--skip-export-check")]
    [Description("Bypass export symbol parity check")]
    public bool SkipExportCheck { get; init; }

    [CommandOption("--redirect-iat")]
    [Description("Automatically redirect PE Import Address Table in k2_x64.dll to modern proxy shims (kongor_ssl_x64.dll / kongor_crypto_x64.dll)")]
    public bool RedirectIat { get; init; }
}

public sealed class SecurityCommand : AsyncCommand<SecurityCommandSettings>
{
    public override async Task<int> ExecuteAsync(CommandContext context, SecurityCommandSettings settings)
    {
        string action = (settings.Action ?? "status").Trim().ToLowerInvariant();
        string repoRoot = Program.FindRepoRoot();
        string targetDir = ResolveTargetDirectory(repoRoot, settings.GameDirectory, settings.Fleet);

        TargetFleet fleet = settings.Fleet?.ToLowerInvariant() switch
        {
            "server" => TargetFleet.Server,
            "client" => TargetFleet.Client,
            "all" => TargetFleet.All,
            _ => TargetFleet.All
        };

        AnsiConsole.Write(new Rule("[bold cyan]Native Network Security & Modern TLS 1.3 Governance[/]").LeftJustified());

        switch (action)
        {
            case "status":
                return ExecuteStatus(targetDir, fleet);

            case "upgrade":
            case "apply":
                return ExecuteUpgrade(targetDir, settings.SkipProcessCheck, settings.SkipExportCheck, settings.RedirectIat, fleet);

            case "rollback":
            case "revert":
                return ExecuteRollback(targetDir, settings.Tier ?? "previous", settings.SkipProcessCheck, fleet);

            case "verify":
            case "verify-handshake":
            case "handshake":
            case "tls":
                return await ExecuteVerifyHandshakeAsync(settings.TargetUrl).ConfigureAwait(false);

            default:
                AnsiConsole.MarkupLine($"[bold red]Error:[/] Unknown action '[yellow]{action}[/]'. Valid actions: status, upgrade, rollback, verify-handshake.");
                return 1;
        }
    }

    private static int ExecuteStatus(string targetDir, TargetFleet fleet = TargetFleet.All)
    {
        if (!Directory.Exists(targetDir))
        {
            AnsiConsole.MarkupLine($"[bold red]Error:[/] Target directory not found at '[yellow]{Markup.Escape(targetDir)}[/]'.");
            return 1;
        }

        SecurityUpgradeReport report = NativeSecurityUpgradeService.GetSecurityStatus(targetDir, fleet);

        var table = new Table()
            .Border(TableBorder.Rounded)
            .Title($"[bold white]Native Security & TLS 1.3 Binary Matrix — {Markup.Escape(Path.GetFileName(targetDir))} ({fleet})[/]")
            .AddColumn("[bold]Binary[/]")
            .AddColumn("[bold]Version[/]")
            .AddColumn("[bold]Size[/]")
            .AddColumn("[bold]SHA-256[/]")
            .AddColumn("[bold]TLS 1.3[/]")
            .AddColumn("[bold]CVE Remediation Status[/]")
            .AddColumn("[bold]Backups[/]")
            .AddColumn("[bold]ABI Parity[/]");

        foreach (SecurityBinaryTarget item in report.Targets)
        {
            string tlsStatus = item.IsTls13Supported
                ? "[bold green]Supported[/]"
                : "[bold red]No (Legacy)[/]";

            string cveDisplay = item.CveRemediationStatus.StartsWith("Remediated", StringComparison.OrdinalIgnoreCase)
                ? $"[green]{Markup.Escape(item.CveRemediationStatus)}[/]"
                : $"[bold red]{Markup.Escape(item.CveRemediationStatus)}[/]";

            string bakStatus = item.HasBakOld
                ? "[bold green].bak + .bak.old[/]"
                : (item.HasBak ? "[green].bak[/]" : "[grey]-[/]");

            string sha = string.IsNullOrEmpty(item.Sha256Hash)
                ? "[grey]-[/]"
                : $"[grey]{item.Sha256Hash[..8]}...[/]";

            string parityStatus = item.IsParityValid
                ? "[bold green]100% Match[/]"
                : (item.MissingSymbols.Count > 0 ? $"[bold red]Missing {item.MissingSymbols.Count}[/]" : "[grey]Unverified[/]");

            table.AddRow(
                $"[white]{Markup.Escape(item.RelativePath)}[/]",
                item.Exists ? $"[cyan]{Markup.Escape(item.FileVersion)}[/]" : "[grey]Missing[/]",
                item.Exists ? $"{item.SizeBytes:N0} B" : "[grey]Missing[/]",
                sha,
                tlsStatus,
                cveDisplay,
                bakStatus,
                parityStatus
            );
        }

        AnsiConsole.Write(table);

        if (report.GameProcessesRunning)
        {
            AnsiConsole.MarkupLine($"\n[bold yellow]⚠️  Running Game Processes Detected:[/] [white]{Markup.Escape(report.RunningProcesses)}[/]");
        }
        else
        {
            AnsiConsole.MarkupLine("\n[bold green]✓  Process Safety:[/] Zero game client or server locks active.");
        }

        return 0;
    }

    private static int ExecuteUpgrade(string targetDir, bool skipProcessCheck, bool skipExportCheck, bool redirectIat = false, TargetFleet fleet = TargetFleet.All)
    {
        if (!skipProcessCheck && NativeBinaryManager.IsGameOrServerRunning(out string runningProcs))
        {
            AnsiConsole.MarkupLine($"[bold red]Error:[/] Cannot upgrade binaries while game processes are active ([yellow]{Markup.Escape(runningProcs)}[/]). Terminate them first or pass [yellow]--skip-process-check[/].");
            return 1;
        }

        AnsiConsole.MarkupLine($"[bold green]Deploying modern libcurl (8.8+ Schannel) and OpenSSL 1.1.1w security binaries to ({fleet}):[/] [white]{Markup.Escape(targetDir)}[/]\n");

        SecurityUpgradeResult result = NativeSecurityUpgradeService.UpgradeSecurityBinaries(
            targetDir,
            null,
            skipProcessCheck,
            skipExportCheck,
            redirectIat,
            fleet,
            msg => AnsiConsole.MarkupLine($"  [grey]>[/] {Markup.Escape(msg)}"));

        if (result.Success)
        {
            AnsiConsole.MarkupLine($"\n[bold green]✓ {Markup.Escape(result.Message)}[/]");
            AnsiConsole.MarkupLine($"  [cyan]Upgraded {result.UpgradedPaths.Count} target binaries with historical .bak.old preservation.[/]");
            if (result.RedirectedIatPaths != null && result.RedirectedIatPaths.Count > 0)
            {
                AnsiConsole.MarkupLine($"  [yellow]Redirected {result.RedirectedIatPaths.Count} PE IAT targets to modern proxy shims.[/]");
            }
            return 0;
        }

        AnsiConsole.MarkupLine($"\n[bold red]✗ {Markup.Escape(result.Message)}[/]");
        return 1;
    }

    private static int ExecuteRollback(string targetDir, string tier, bool skipProcessCheck, TargetFleet fleet = TargetFleet.All)
    {
        if (!skipProcessCheck && NativeBinaryManager.IsGameOrServerRunning(out string runningProcs))
        {
            AnsiConsole.MarkupLine($"[bold red]Error:[/] Cannot rollback binaries while game processes are active ([yellow]{Markup.Escape(runningProcs)}[/]). Terminate them first or pass [yellow]--skip-process-check[/].");
            return 1;
        }

        AnsiConsole.MarkupLine($"[bold yellow]Rolling back security binaries to tier '{tier}' in ({fleet}):[/] [white]{Markup.Escape(targetDir)}[/]\n");

        RollbackResult result = NativeSecurityUpgradeService.RollbackSecurityBinaries(
            targetDir,
            tier,
            skipProcessCheck,
            fleet,
            msg => AnsiConsole.MarkupLine($"  [grey]>[/] {Markup.Escape(msg)}"));

        if (result.Success)
        {
            AnsiConsole.MarkupLine($"\n[bold green]✓ {Markup.Escape(result.Message)}[/]");
            return 0;
        }

        AnsiConsole.MarkupLine($"\n[bold red]✗ {Markup.Escape(result.Message)}[/]");
        return 1;
    }

    private static async Task<int> ExecuteVerifyHandshakeAsync(string targetUrl)
    {
        AnsiConsole.MarkupLine($"[bold cyan]Initiating cryptographic TLS 1.3 handshake verification against:[/] [white]{Markup.Escape(targetUrl)}[/]...\n");

        TlsHandshakeResult result = await NativeSecurityUpgradeService.VerifyTls13HandshakeAsync(targetUrl).ConfigureAwait(false);

        if (result.Success)
        {
            AnsiConsole.MarkupLine($"[bold green]✓ {Markup.Escape(result.Message)}[/]");
            AnsiConsole.MarkupLine($"  [cyan]Negotiated Protocol:[/] [white]{Markup.Escape(result.NegotiatedProtocol)}[/]");
            AnsiConsole.MarkupLine($"  [cyan]Cipher Suite:[/]        [white]{Markup.Escape(result.CipherSuite)}[/]");
            AnsiConsole.MarkupLine($"  [cyan]Roundtrip Latency:[/]   [green]{result.RoundtripMs} ms[/]");
            return 0;
        }

        AnsiConsole.MarkupLine($"[bold yellow]⚠️ {Markup.Escape(result.Message)}[/]");
        AnsiConsole.MarkupLine($"  [grey]Roundtrip Time:[/] {result.RoundtripMs} ms");
        return 1;
    }

    private static string ResolveTargetDirectory(string repoRoot, string? explicitDir, string? fleet = null)
    {
        if (!string.IsNullOrWhiteSpace(explicitDir))
        {
            return Path.GetFullPath(explicitDir);
        }

        if (string.Equals(fleet, "server", StringComparison.OrdinalIgnoreCase))
        {
            string serverDir = Path.Combine(repoRoot, "games", "server");
            if (Directory.Exists(serverDir)) return serverDir;
        }

        if (string.Equals(fleet, "all", StringComparison.OrdinalIgnoreCase))
        {
            return repoRoot;
        }

        string candidate1 = Path.Combine(repoRoot, "games", "client");
        if (Directory.Exists(candidate1)) return candidate1;

        string candidate2 = Path.Combine(repoRoot, "games", "client", "windows-x64");
        if (Directory.Exists(candidate2)) return candidate2;

        return repoRoot;
    }
}
