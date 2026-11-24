// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// TAO (Together As One) MCP & Kongor CLI -- Created by guccidonut (https://github.com/guccidonut/PKV)

using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Spectre.Console;
using Spectre.Console.Cli;
using static Kongor.Cli.Program;

namespace Kongor.Cli.Commands;

public sealed class PackageCommandSettings : CommandSettings
{
    [CommandArgument(0, "[target]")]
    [Description("Target component to package ('willowmaker', 'compel', 'services', or 'all' (default))")]
    [DefaultValue("all")]
    public string Target { get; init; } = "all";

    [CommandOption("-o|--os|--target-os")]
    [Description("Target operating system ('current' (default), 'windows', 'linux', 'osx', 'all')")]
    [DefaultValue("current")]
    public string TargetOs { get; init; } = "current";

    [CommandOption("-r|--rid")]
    [Description("Specific .NET Runtime Identifier (e.g. 'win-x64', 'linux-x64', 'osx-arm64', 'osx-x64')")]
    public string? Rid { get; init; }

    [CommandOption("-c|--configuration")]
    [Description("Build configuration ('Release' (default), 'Debug')")]
    [DefaultValue("Release")]
    public string Configuration { get; init; } = "Release";

    [CommandOption("--out-dir")]
    [Description("Base output directory for packaged distributions (default: 'dist')")]
    [DefaultValue("dist")]
    public string OutDir { get; init; } = "dist";

    [CommandOption("--aot")]
    [Description("Enable Native AOT compilation (-p:PublishAot=true) where supported")]
    public bool PublishAot { get; init; }

    [CommandOption("--clean")]
    [Description("Clean target distribution directory before packaging")]
    public bool Clean { get; init; }

    [CommandOption("--prebake")]
    [Description("Run entity and Lua asset prebaker before packaging client")]
    public bool Prebake { get; init; }
}

public sealed class PackageCommand : AsyncCommand<PackageCommandSettings>
{
    public override async Task<int> ExecuteAsync(CommandContext context, PackageCommandSettings settings)
    {
        string targetNormalized = settings.Target.ToLowerInvariant();
        string repoRoot = ResolveRepoRoot();
        string baseOutDir = Path.IsPathRooted(settings.OutDir)
            ? settings.OutDir
            : Path.Combine(repoRoot, settings.OutDir);

        if (settings.Clean && Directory.Exists(baseOutDir))
        {
            AnsiConsole.MarkupLine($"[yellow]🧹 Cleaning output directory:[/] [grey]{Markup.Escape(baseOutDir)}[/]");
            try { Directory.Delete(baseOutDir, recursive: true); } catch { }
        }
        Directory.CreateDirectory(baseOutDir);

        List<string> targetRids = ResolveTargetRids(settings.TargetOs, settings.Rid);

        AnsiConsole.Write(new Rule("[bold cyan]📦 Project Kongor Cross-Platform Distribution Packager[/]").LeftJustified());
        AnsiConsole.MarkupLine($"[bold]Target Component:[/] [yellow]{Markup.Escape(targetNormalized)}[/]");
        AnsiConsole.MarkupLine($"[bold]Configuration:[/]    [green]{settings.Configuration}[/]");
        AnsiConsole.MarkupLine($"[bold]Target RIDs:[/]      [cyan]{string.Join(", ", targetRids)}[/]");
        AnsiConsole.MarkupLine($"[bold]Output Directory:[/] [white]{Markup.Escape(baseOutDir)}[/]");
        AnsiConsole.WriteLine();

        bool isWillowmaker = targetNormalized is "willowmaker" or "client" or "launcher" or "all";
        bool isCompel = targetNormalized is "compel" or "server" or "supervisor" or "all";
        bool isServices = targetNormalized is "services" or "nexus" or "microservices" or "cluster" or "all";

        if (!isWillowmaker && !isCompel && !isServices)
        {
            AnsiConsole.MarkupLine($"[red]Error: Unknown target component '{Markup.Escape(settings.Target)}'.[/]");
            AnsiConsole.MarkupLine("Supported targets: [yellow]willowmaker[/] (or client), [yellow]compel[/] (or server), [yellow]services[/], [yellow]all[/].");
            return 1;
        }

        int successCount = 0;
        int failureCount = 0;

        // 1. Prebake assets if requested or if packaging Willowmaker/all
        if (settings.Prebake && isWillowmaker)
        {
            AnsiConsole.MarkupLine("[bold purple]⚡ Executing Asset Prebaker Pipeline...[/]");
            int prebakeExit = await ExecutePrebakeAsync(repoRoot);
            if (prebakeExit != 0)
            {
                AnsiConsole.MarkupLine("[red]Warning: Prebake pipeline returned non-zero exit code. Continuing packaging...[/]");
            }
        }

        // 2. Package Willowmaker Launcher
        if (isWillowmaker)
        {
            string willowmakerProj = Path.Combine(repoRoot, "src", "apps", "willowmaker", "WILLOWMAKER.csproj");
            foreach (string rid in targetRids)
            {
                string targetDir = Path.Combine(baseOutDir, $"willowmaker-{rid}");
                bool ok = await PublishComponentAsync("WILLOWMAKER (Cross-Platform Launcher)", willowmakerProj, rid, settings.Configuration, targetDir, settings.PublishAot, repoRoot);
                if (ok)
                {
                    // If Windows packaging, stage native Vulkan layer alongside executable
                    if (rid.StartsWith("win", StringComparison.OrdinalIgnoreCase))
                    {
                        StageWindowsNativeBinaries(repoRoot, targetDir);
                    }
                    successCount++;
                }
                else
                {
                    failureCount++;
                }
            }
        }

        // 3. Package Compel Dedicated Server Supervisor
        if (isCompel)
        {
            string compelProj = Path.Combine(repoRoot, "src", "apps", "compel", "COMPEL.csproj");
            // Compel primarily runs on Windows and Linux (dedicated server hosts)
            List<string> serverRids = targetRids.Where(r => r.StartsWith("win", StringComparison.OrdinalIgnoreCase) || r.StartsWith("linux", StringComparison.OrdinalIgnoreCase)).ToList();
            if (serverRids.Count == 0) serverRids = [.. targetRids];

            foreach (string rid in serverRids)
            {
                string targetDir = Path.Combine(baseOutDir, $"compel-{rid}");
                bool ok = await PublishComponentAsync("COMPEL (Dedicated Server Supervisor)", compelProj, rid, settings.Configuration, targetDir, settings.PublishAot, repoRoot);
                if (ok) successCount++;
                else failureCount++;
            }
        }

        // 4. Package Microservices
        if (isServices)
        {
            string[] serviceProjects = [
                Path.Combine(repoRoot, "src", "services", "master-server", "KONGOR.MasterServer.csproj"),
                Path.Combine(repoRoot, "src", "services", "chat-server", "TRANSMUTANSTEIN.ChatServer.csproj"),
                Path.Combine(repoRoot, "src", "services", "web-api", "ZORGATH.WebPortal.API.csproj")
            ];

            foreach (string proj in serviceProjects)
            {
                string serviceName = Path.GetFileNameWithoutExtension(proj);
                foreach (string rid in targetRids)
                {
                    string targetDir = Path.Combine(baseOutDir, $"{serviceName.ToLowerInvariant()}-{rid}");
                    bool ok = await PublishComponentAsync(serviceName, proj, rid, settings.Configuration, targetDir, publishAot: false, repoRoot);
                    if (ok) successCount++;
                    else failureCount++;
                }
            }
        }

        AnsiConsole.WriteLine();
        if (failureCount == 0)
        {
            AnsiConsole.MarkupLine($"[bold green]✅ Cross-Platform Packaging Completed Successfully![/] [white]({successCount} bundles created in {Markup.Escape(baseOutDir)})[/]");
            return 0;
        }
        else
        {
            AnsiConsole.MarkupLine($"[bold yellow]⚠️ Packaging Completed with warnings:[/] [green]{successCount} succeeded[/], [red]{failureCount} failed[/].");
            return failureCount > 0 && successCount == 0 ? 1 : 0;
        }
    }

    private static List<string> ResolveTargetRids(string targetOs, string? explicitRid)
    {
        if (!string.IsNullOrWhiteSpace(explicitRid))
        {
            return [explicitRid.Trim()];
        }

        return targetOs.ToLowerInvariant() switch
        {
            "windows" or "win" => ["win-x64"],
            "linux" or "ubuntu" or "debian" => ["linux-x64"],
            "osx" or "macos" or "darwin" => ["osx-arm64", "osx-x64"],
            "all" or "cross" => ["win-x64", "linux-x64", "osx-arm64"],
            _ => [GetCurrentPlatformRid()]
        };
    }

    private static string GetCurrentPlatformRid()
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            return RuntimeInformation.ProcessArchitecture == Architecture.Arm64 ? "win-arm64" : "win-x64";
        }
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
        {
            return RuntimeInformation.ProcessArchitecture == Architecture.Arm64 ? "linux-arm64" : "linux-x64";
        }
        if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
        {
            return RuntimeInformation.ProcessArchitecture == Architecture.Arm64 ? "osx-arm64" : "osx-x64";
        }
        return "win-x64";
    }

    private static async Task<bool> PublishComponentAsync(
        string displayName,
        string projectPath,
        string rid,
        string configuration,
        string outputDir,
        bool publishAot,
        string repoRoot)
    {
        string projectName = Path.GetFileName(projectPath);
        AnsiConsole.MarkupLine($"[bold cyan]📦 Publishing {Markup.Escape(displayName)}[/] [grey]({rid}, {configuration})[/]...");

        Directory.CreateDirectory(outputDir);

        // When publishing cross-platform Native AOT from Windows host without cross-compilers,
        // fall back safely to self-contained single-file publish if cross-AOT fails.
        bool isCrossCompile = !rid.StartsWith(GetCurrentPlatformRid().Split('-')[0], StringComparison.OrdinalIgnoreCase);
        bool tryAot = publishAot && !isCrossCompile;

        List<string> args = [
            "publish",
            projectPath,
            "-c", configuration,
            "-r", rid,
            "--self-contained", "true",
            $"-o", outputDir
        ];

        if (tryAot)
        {
            args.Add("-p:PublishAot=true");
        }
        else
        {
            args.Add("-p:PublishSingleFile=true");
            args.Add("-p:IncludeNativeLibrariesForSelfExtract=true");
        }

        int exitCode = await RunDotnetAsync(args, repoRoot);
        if (exitCode != 0 && tryAot)
        {
            AnsiConsole.MarkupLine($"[yellow]⚠️ Native AOT publish for {rid} requested fallback; retrying as self-contained single-file...[/]");
            args.Remove("-p:PublishAot=true");
            args.Add("-p:PublishSingleFile=true");
            args.Add("-p:IncludeNativeLibrariesForSelfExtract=true");
            exitCode = await RunDotnetAsync(args, repoRoot);
        }

        if (exitCode == 0)
        {
            AnsiConsole.MarkupLine($"  [green]✓ {Markup.Escape(displayName)} ({rid}) successfully staged to:[/] [grey]{Markup.Escape(outputDir)}[/]");
            return true;
        }
        else
        {
            AnsiConsole.MarkupLine($"  [red]✗ Failed to publish {Markup.Escape(displayName)} ({rid}) - exit code {exitCode}[/]");
            return false;
        }
    }

    private static void StageWindowsNativeBinaries(string repoRoot, string targetDir)
    {
        try
        {
            string vulkanLayerDll = Path.Combine(repoRoot, "games", "client", "VkLayer_hon_replay_capture.dll");
            string vulkanLayerJson = Path.Combine(repoRoot, "games", "client", "VkLayer_hon_replay_capture.json");

            if (File.Exists(vulkanLayerDll))
            {
                File.Copy(vulkanLayerDll, Path.Combine(targetDir, Path.GetFileName(vulkanLayerDll)), overwrite: true);
            }
            if (File.Exists(vulkanLayerJson))
            {
                File.Copy(vulkanLayerJson, Path.Combine(targetDir, Path.GetFileName(vulkanLayerJson)), overwrite: true);
            }
        }
        catch
        {
            // Non-fatal if native layer not present
        }
    }

    private static async Task<int> ExecutePrebakeAsync(string repoRoot)
    {
        string prebakeProj = Path.Combine(repoRoot, "src", "tools", "prebake-tool", "prebake-tool.csproj");
        return await RunDotnetAsync(["run", "--project", prebakeProj, "-c", "Release", "--", "all"], repoRoot);
    }

    private static async Task<int> RunDotnetAsync(List<string> args, string workingDir)
    {
        ProcessStartInfo psi = new("dotnet")
        {
            WorkingDirectory = workingDir,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        foreach (string a in args)
        {
            psi.ArgumentList.Add(a);
        }

        using Process proc = new() { StartInfo = psi };
        proc.Start();
        string stdout = await proc.StandardOutput.ReadToEndAsync();
        string stderr = await proc.StandardError.ReadToEndAsync();
        await proc.WaitForExitAsync();

        if (proc.ExitCode != 0)
        {
            if (!string.IsNullOrWhiteSpace(stderr))
            {
                AnsiConsole.MarkupLine($"[grey]{Markup.Escape(stderr.Trim())}[/]");
            }
            else if (!string.IsNullOrWhiteSpace(stdout))
            {
                AnsiConsole.MarkupLine($"[grey]{Markup.Escape(stdout.Trim())}[/]");
            }
        }

        return proc.ExitCode;
    }

    private static string ResolveRepoRoot() => Program.FindRepoRoot();
}
