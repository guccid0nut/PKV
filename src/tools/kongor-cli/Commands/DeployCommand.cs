// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// TAO (Together As One) MCP & Kongor CLI -- Created by guccidonut (https://github.com/guccidonut/PKV)

using System.ComponentModel;
using System.Diagnostics;
using Spectre.Console;
using Spectre.Console.Cli;

namespace Kongor.Cli.Commands;

public sealed class DeployCommandSettings : CommandSettings
{
    [CommandArgument(0, "[action]")]
    [Description("Deployment action ('status' (default), 'servers', 'cluster')")]
    [DefaultValue("status")]
    public string Action { get; init; } = "status";

    [CommandOption("-s|--scale <COUNT>")]
    [Description("Number of dedicated server pods to scale or deploy")]
    [DefaultValue(2)]
    public int Scale { get; init; } = 2;
}

public sealed class DeployCommand : AsyncCommand<DeployCommandSettings>
{
    public override async Task<int> ExecuteAsync(CommandContext context, DeployCommandSettings settings)
    {
        string action = settings.Action.ToLowerInvariant();
        string repoRoot = ResolveRepoRoot();

        switch (action)
        {
            case "status" or "info":
            {
                AnsiConsole.Write(new Rule("[bold cyan]☸️ Project Kongor Kubernetes (K3s) & Container Deployment[/]").LeftJustified());

                // Check kubectl if installed
                bool kubectlAvailable = await CheckCommandAvailableAsync("kubectl");
                bool dockerAvailable = await CheckCommandAvailableAsync("docker");

                Table table = new();
                table.AddColumn("[bold]Tool / Subsystem[/]");
                table.AddColumn("[bold]Availability[/]");
                table.AddColumn("[bold]Status[/]");

                table.AddRow(
                    "Kubernetes CLI (`kubectl`)",
                    kubectlAvailable ? "[green]Installed[/]" : "[yellow]Not Found[/]",
                    kubectlAvailable ? "[cyan]Ready for cluster commands[/]" : "[grey]Optional for local dev[/]"
                );

                table.AddRow(
                    "Docker Engine (`docker`)",
                    dockerAvailable ? "[green]Installed[/]" : "[yellow]Not Found[/]",
                    dockerAvailable ? "[cyan]Container runtime active[/]" : "[grey]Optional for local dev[/]"
                );

                string k3sYaml = Path.Combine(repoRoot, "infra", "k8s", "compel-k3s.yaml");
                table.AddRow(
                    "Compel K3s Manifest",
                    File.Exists(k3sYaml) ? "[green]Present[/]" : "[red]Missing[/]",
                    File.Exists(k3sYaml) ? $"[grey]{Markup.Escape(k3sYaml)}[/]" : "-"
                );

                string deployScript = Path.Combine(repoRoot, "infra", "k8s", "deploy_k3s_servers.ps1");
                table.AddRow(
                    "K3s Deployment Script",
                    File.Exists(deployScript) ? "[green]Present[/]" : "[red]Missing[/]",
                    File.Exists(deployScript) ? $"[grey]{Markup.Escape(deployScript)}[/]" : "-"
                );

                AnsiConsole.Write(table);

                if (kubectlAvailable)
                {
                    AnsiConsole.WriteLine();
                    AnsiConsole.MarkupLine("[bold cyan]Fetching active Kubernetes pods...[/]");
                    await RunCommandAsync("kubectl", "get pods -o wide", repoRoot);
                }

                return 0;
            }

            case "servers" or "fleet":
            {
                string deployScript = Path.Combine(repoRoot, "infra", "k8s", "deploy_k3s_servers.ps1");
                if (!File.Exists(deployScript))
                {
                    AnsiConsole.MarkupLine($"[red]Error: Deployment script not found at '{Markup.Escape(deployScript)}'[/]");
                    return 1;
                }

                AnsiConsole.MarkupLine($"[bold cyan]🚀 Executing K3s Dedicated Server Deployment Pipeline (Scale: {settings.Scale})...[/]");
                int exitCode = await RunCommandAsync("powershell", $"-ExecutionPolicy Bypass -File \"{deployScript}\" -Scale {settings.Scale}", repoRoot);
                return exitCode;
            }

            case "cluster":
            {
                AnsiConsole.MarkupLine("[bold cyan]☸️ Cluster Configuration Manifests: [[Auto-Detected]][/]");
                AnsiConsole.MarkupLine($"• Manifest: [grey]{Markup.Escape(Path.Combine(repoRoot, "infra", "k8s", "compel-k3s.yaml"))}[/]");
                AnsiConsole.MarkupLine($"• Helm Values: [grey]{Markup.Escape(Path.Combine(repoRoot, "infra", "k8s", "agones", "values-k3s.yaml"))}[/]");
                return 0;
            }

            default:
                AnsiConsole.MarkupLine($"[red]Unknown action '{Markup.Escape(action)}'. Supported: status, servers, cluster.[/]");
                return 1;
        }
    }

    private static async Task<bool> CheckCommandAvailableAsync(string command)
    {
        try
        {
            using Process proc = new();
            proc.StartInfo = new ProcessStartInfo
            {
                FileName = command,
                Arguments = "--version",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            proc.Start();
            await proc.WaitForExitAsync();
            return proc.ExitCode == 0;
        }
        catch
        {
            return false;
        }
    }

    private static async Task<int> RunCommandAsync(string command, string arguments, string workingDir)
    {
        try
        {
            using Process proc = new();
            proc.StartInfo = new ProcessStartInfo
            {
                FileName = command,
                Arguments = arguments,
                WorkingDirectory = workingDir,
                RedirectStandardOutput = false,
                RedirectStandardError = false,
                UseShellExecute = false
            };
            proc.Start();
            await proc.WaitForExitAsync();
            return proc.ExitCode;
        }
        catch (Exception ex)
        {
            AnsiConsole.MarkupLine($"[red]Failed to execute {command}: {Markup.Escape(ex.Message)}[/]");
            return 1;
        }
    }

    private static string ResolveRepoRoot() => Program.FindRepoRoot();
}
