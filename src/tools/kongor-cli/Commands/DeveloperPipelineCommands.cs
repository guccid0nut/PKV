// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// TAO (Together As One) MCP & Kongor CLI -- Created by guccidonut (https://github.com/guccidonut/PKV)

using System.ComponentModel;
using System.Diagnostics;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using Kongor.Cli.Infrastructure;
using Spectre.Console;
using Spectre.Console.Cli;
using static Kongor.Cli.Program;

namespace Kongor.Cli.Commands;

[KongorLayoutRequirement(KongorLayoutSupport.SourceWorkspaceOnly)]
public sealed class GateCommand : Command<RawArgsSettings>
{
    public override int Execute(CommandContext context, RawArgsSettings settings)
    {
        string[] raw = CommandArgsHelper.GetEffectiveArgs(context, settings);
        AnsiConsole.MarkupLine("[bold cyan]🛡️  Executing Sub-5-Second Inner-Loop Quality Gate (scripts/gate_check.ps1)...[/]");
        return RunPowerShellScript("scripts/gate_check.ps1", raw);
    }
}

[KongorLayoutRequirement(KongorLayoutSupport.SourceWorkspaceOnly)]
public sealed class OracleCommand : Command<RawArgsSettings>
{
    public override int Execute(CommandContext context, RawArgsSettings settings)
    {
        string[] raw = CommandArgsHelper.GetEffectiveArgs(context, settings);
        AnsiConsole.MarkupLine("[bold cyan]⚖️  Executing Netcode Oracle Verification (scripts/verify_netcode_oracle.ps1)...[/]");
        return RunPowerShellScript("scripts/verify_netcode_oracle.ps1", raw);
    }
}

[KongorLayoutRequirement(KongorLayoutSupport.SourceWorkspaceOnly)]
public sealed class PracticeCommand : Command<RawArgsSettings>
{
    public override int Execute(CommandContext context, RawArgsSettings settings)
    {
        string[] raw = CommandArgsHelper.GetEffectiveArgs(context, settings);
        AnsiConsole.MarkupLine("[bold cyan]🎮 Launching Caldavar Practice Mode (scripts/launch_screen_practice.ps1)...[/]");
        return RunPowerShellScript("scripts/launch_screen_practice.ps1", raw);
    }
}

[KongorLayoutRequirement(KongorLayoutSupport.SourceWorkspaceOnly)]
public sealed class InjectCommand : Command<RawArgsSettings>
{
    public override int Execute(CommandContext context, RawArgsSettings settings)
    {
        string[] raw = CommandArgsHelper.GetEffectiveArgs(context, settings);
        string repo = FindRepoRoot();
        string[] normalized = CliPathResolver.NormalizeForwardedPathArgs(raw, repo);
        AnsiConsole.MarkupLine("[bold cyan]📦 Executing S2Z Archive Override Injection (scripts/vfs/inject_override_to_s2z.ps1)...[/]");
        return RunPowerShellScript("scripts/vfs/inject_override_to_s2z.ps1", normalized);
    }
}

[KongorLayoutRequirement(KongorLayoutSupport.SourceWorkspaceOnly)]
public sealed class DecompileCommand : Command<RawArgsSettings>
{
    public override int Execute(CommandContext context, RawArgsSettings settings)
    {
        string[] raw = CommandArgsHelper.GetEffectiveArgs(context, settings);
        string repo = FindRepoRoot();
        string[] normalized = CliPathResolver.NormalizeForwardedPathArgs(raw, repo);
        AnsiConsole.MarkupLine("[bold cyan]🔬 Executing Automated Lua Bytecode Decompilation (scripts/re/decompile_bytecode.ps1)...[/]");
        return RunPowerShellScript("scripts/re/decompile_bytecode.ps1", normalized);
    }
}

[KongorLayoutRequirement(KongorLayoutSupport.SourceWorkspaceOnly)]
public sealed class SanitizeCommand : Command<RawArgsSettings>
{
    public override int Execute(CommandContext context, RawArgsSettings settings)
    {
        string[] raw = CommandArgsHelper.GetEffectiveArgs(context, settings);
        AnsiConsole.MarkupLine("[bold cyan]🧹 Executing Workspace Sanitization & Quarantine (scripts/dev/sanitize_workspace.ps1)...[/]");
        return RunPowerShellScript("scripts/dev/sanitize_workspace.ps1", raw);
    }
}

[KongorLayoutRequirement(KongorLayoutSupport.SourceWorkspaceOnly)]
public sealed class SyncLuaCommand : Command<RawArgsSettings>
{
    public override int Execute(CommandContext context, RawArgsSettings settings)
    {
        string[] raw = CommandArgsHelper.GetEffectiveArgs(context, settings);
        AnsiConsole.MarkupLine("[bold cyan]🔄 Synchronizing Override Lua to Shared Tree (scripts/vfs/sync_override_to_shared.ps1)...[/]");
        return RunPowerShellScript("scripts/vfs/sync_override_to_shared.ps1", raw);
    }
}

[KongorLayoutRequirement(KongorLayoutSupport.SourceWorkspaceOnly)]
internal sealed class SbomCommandSettings : CommandSettings
{
    [CommandOption("-o|--output <DIR>")]
    [Description("Optional output directory for sbom.json (defaults to <workspace>/artifacts)")]
    public string? OutputDir { get; init; }
}

[KongorLayoutRequirement(KongorLayoutSupport.SourceWorkspaceOnly)]
internal sealed class SbomCommand : Command<SbomCommandSettings>
{
    public override int Execute(CommandContext context, SbomCommandSettings settings)
    {
        string repo = CliRuntimeContext.RequireSourceWorkspace().Root;
        string outputDir = !string.IsNullOrWhiteSpace(settings.OutputDir)
            ? CliPathResolver.ResolveOutputPath(settings.OutputDir, repo, "sbom", isExplicitExportCommand: true)
            : Path.Combine(repo, "artifacts");
        Directory.CreateDirectory(outputDir);

        AnsiConsole.MarkupLine("[bold cyan]📋 Generating CycloneDX Software Bill of Materials (SBOM)...[/]");
        _ = RunProcess("dotnet", "tool restore", repo);

        string sbomPath = Path.Combine(outputDir, "sbom.json");
        string cdxPath = Path.Combine(outputDir, "sbom-cyclonedx.json");
        int exitCode = RunProcess(
            "dotnet",
            $"tool run dotnet-CycloneDX -- ProjectKongor.slnx -o \"{outputDir}\" -fn sbom.json -F Json --disable-package-restore",
            repo);

        if (exitCode != 0 || !File.Exists(sbomPath))
        {
            AnsiConsole.MarkupLine($"[bold red]✗ Failed to generate CycloneDX SBOM (exit {exitCode}).[/]");
            return exitCode == 0 ? 1 : exitCode;
        }

        File.Copy(sbomPath, cdxPath, overwrite: true);
        int componentCount = 0;
        try
        {
            using JsonDocument doc = JsonDocument.Parse(File.ReadAllText(sbomPath));
            if (doc.RootElement.TryGetProperty("components", out JsonElement components) &&
                components.ValueKind == JsonValueKind.Array)
            {
                componentCount = components.GetArrayLength();
            }
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[kongor] [SBOM] Could not count JSON components: {ex.Message}");
        }

        AnsiConsole.MarkupLine($"[bold green]✓ Successfully generated CycloneDX SBOM ({componentCount} components cataloged).[/]");
        AnsiConsole.MarkupLine($"[grey]  Primary Output :[/] [white]{Markup.Escape(sbomPath)}[/]");
        AnsiConsole.MarkupLine($"[grey]  CycloneDX Copy :[/] [white]{Markup.Escape(cdxPath)}[/]");
        return 0;
    }
}

[KongorLayoutRequirement(KongorLayoutSupport.SourceWorkspaceOnly)]
public sealed class ScanCommand : Command<RawArgsSettings>
{
    public override int Execute(CommandContext context, RawArgsSettings settings)
    {
        string[] raw = CommandArgsHelper.GetEffectiveArgs(context, settings);
        AnsiConsole.MarkupLine("[bold cyan]🔍 Running NuGet Dependency Vulnerability Scanner (scripts/dev/scan-dependencies.ps1)...[/]");
        return RunPowerShellScript("scripts/dev/scan-dependencies.ps1", raw);
    }
}

[KongorLayoutRequirement(KongorLayoutSupport.SourceWorkspaceOnly)]
public sealed class SoakCommand : Command<RawArgsSettings>
{
    public override int Execute(CommandContext context, RawArgsSettings settings)
    {
        string[] raw = CommandArgsHelper.GetEffectiveArgs(context, settings);
        AnsiConsole.MarkupLine("[bold cyan]⏱️  Executing Endurance & Memory Leak Soak Test (scripts/qa/run-soak-test.ps1)...[/]");
        return RunPowerShellScript("scripts/qa/run-soak-test.ps1", raw);
    }
}

[KongorLayoutRequirement(KongorLayoutSupport.SourceWorkspaceOnly)]
public sealed class AuditAllCommand : Command<RawArgsSettings>
{
    public override int Execute(CommandContext context, RawArgsSettings settings)
    {
        string[] raw = CommandArgsHelper.GetEffectiveArgs(context, settings);
        AnsiConsole.MarkupLine("[bold cyan]🧪 Executing Automated Developer CLI Audit Suite (scripts/qa/run-all-cli-audits.ps1)...[/]");
        return RunPowerShellScript("scripts/qa/run-all-cli-audits.ps1", raw);
    }
}

[KongorLayoutRequirement(KongorLayoutSupport.SourceWorkspaceOnly)]
public sealed class TestE2ECommand : Command<RawArgsSettings>
{
    public override int Execute(CommandContext context, RawArgsSettings settings)
    {
        string[] raw = CommandArgsHelper.GetEffectiveArgs(context, settings);
        AnsiConsole.MarkupLine("[bold cyan]🚀 Executing 4-Tier E2E Verification Suite (tests/e2e/run_all.ps1)...[/]");
        return RunPowerShellScript("tests/e2e/run_all.ps1", raw);
    }
}

[KongorLayoutRequirement(KongorLayoutSupport.SourceWorkspaceOnly)]
public sealed class DocgenCommand : Command<RawArgsSettings>
{
    public override int Execute(CommandContext context, RawArgsSettings settings)
    {
        string[] raw = CommandArgsHelper.GetEffectiveArgs(context, settings);
        AnsiConsole.MarkupLine("[bold cyan]📊 Executing Documentation Architecture Chart Generator (tools/docgen/generate_charts.py)...[/]");
        return RunPythonScript("tools/docgen/generate_charts.py", raw);
    }
}

[KongorLayoutRequirement(KongorLayoutSupport.SourceWorkspaceOnly)]
public sealed class InstallHooksCommand : Command<EmptyCommandSettings>
{
    public override int Execute(CommandContext context, EmptyCommandSettings settings)
    {
        string repo = CliRuntimeContext.RequireSourceWorkspace().Root;
        string hooksDir = Path.Combine(repo, ".git", "hooks");
        if (!Directory.Exists(hooksDir))
        {
            AnsiConsole.MarkupLine($"[bold red]✗ No .git/hooks directory found at '{Markup.Escape(hooksDir)}'.[/]");
            return 1;
        }

        string prePushHook = Path.Combine(hooksDir, "pre-push");
        string hookContent =
            "#!/bin/sh\n" +
            "# Git Pre-Push Hook for Project Kongor\n" +
            "if command -v pwsh >/dev/null 2>&1; then\n" +
            "    pwsh -NoProfile -ExecutionPolicy Bypass -File \"./scripts/git-pre-push.ps1\" \"$1\" \"$2\"\n" +
            "else\n" +
            "    powershell.exe -NoProfile -ExecutionPolicy Bypass -File \"./scripts/git-pre-push.ps1\" \"$1\" \"$2\"\n" +
            "fi\n" +
            "exit $?\n";

        File.WriteAllText(prePushHook, hookContent, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        if (!OperatingSystem.IsWindows())
        {
            try
            {
                UnixFileMode mode = File.GetUnixFileMode(prePushHook);
                File.SetUnixFileMode(prePushHook, mode | UnixFileMode.UserExecute | UnixFileMode.GroupExecute | UnixFileMode.OtherExecute);
            }
            catch { }
        }
        AnsiConsole.MarkupLine($"[bold green]✓ Installed Project Kongor Git pre-push hook:[/] [white]{Markup.Escape(prePushHook)}[/]");
        return 0;
    }
}

[KongorLayoutRequirement(KongorLayoutSupport.SourceWorkspaceOnly)]
public sealed class MigrateCommand : Command<RawArgsSettings>
{
    public override int Execute(CommandContext context, RawArgsSettings settings)
    {
        string repo = CliRuntimeContext.RequireSourceWorkspace().Root;
        string[] raw = CommandArgsHelper.GetEffectiveArgs(context, settings);
        string dbProj = Path.Combine(repo, "src", "libs", "database-context");
        string webProj = Path.Combine(repo, "src", "services", "web-api");

        // Ensure local dotnet-ef tool is restored
        if (RunProcess("dotnet", "ef --version", repo) != 0)
        {
            AnsiConsole.MarkupLine("[yellow]▶ Restoring local dotnet-ef tool...[/]");
            _ = RunProcess("dotnet", "tool restore", repo);
        }

        string sub = raw.Length > 0 ? raw[0].Trim().ToLowerInvariant() : "list";
        string projFlags = $"--configuration Release --project \"{dbProj}\" --startup-project \"{webProj}\"";

        switch (sub)
        {
            case "preview":
            {
                AnsiConsole.MarkupLine("[bold cyan]🗄️  Generating idempotent EF Core SQL migration preview...[/]");
                int code = RunProcess("dotnet", $"ef migrations script --idempotent --no-build {projFlags}", repo);
                if (code != 0)
                {
                    code = RunProcess("dotnet", $"ef migrations script --idempotent {projFlags}", repo);
                }
                return code;
            }
            case "apply":
            {
                AnsiConsole.MarkupLine("[bold cyan]🗄️  Applying pending EF Core database migrations...[/]");
                return RunProcess("dotnet", $"ef database update {projFlags}", repo);
            }
            case "rollback":
            {
                if (raw.Length < 2 || string.IsNullOrWhiteSpace(raw[1]))
                {
                    AnsiConsole.MarkupLine("[bold red]✗ Target migration name is required (e.g. 'kongor migrate rollback <Name>').[/]");
                    return 1;
                }
                string target = raw[1];
                AnsiConsole.MarkupLine($"[yellow]🗄️  Rolling back database to migration '{Markup.Escape(target)}'...[/]");
                return RunProcess("dotnet", $"ef database update \"{target}\" {projFlags}", repo);
            }
            case "add":
            {
                if (raw.Length < 2 || string.IsNullOrWhiteSpace(raw[1]))
                {
                    AnsiConsole.MarkupLine("[bold red]✗ Migration name is required (e.g. 'kongor migrate add AddFeature').[/]");
                    return 1;
                }
                string name = raw[1];
                AnsiConsole.MarkupLine($"[bold cyan]🗄️  Scaffolding new EF Core migration '{Markup.Escape(name)}'...[/]");
                return RunProcess("dotnet", $"ef migrations add \"{name}\" {projFlags}", repo);
            }
            default:
            {
                AnsiConsole.MarkupLine("[bold cyan]🗄️  Listing EF Core database migrations...[/]");
                int code = RunProcess("dotnet", $"ef migrations list --no-build {projFlags}", repo);
                if (code != 0)
                {
                    code = RunProcess("dotnet", $"ef migrations list {projFlags}", repo);
                }
                return code;
            }
        }
    }
}

[KongorLayoutRequirement(KongorLayoutSupport.SourceWorkspaceOnly)]
public sealed class TailwindCommand : Command<RawArgsSettings>
{
    private static string ResolveTailwindDownloadUrl()
    {
        const string baseUrl = "https://github.com/tailwindlabs/tailwindcss/releases/download/v4.3.3";
        bool isArm64 = System.Runtime.InteropServices.RuntimeInformation.OSArchitecture == System.Runtime.InteropServices.Architecture.Arm64;
        if (OperatingSystem.IsWindows())
        {
            return isArm64 ? $"{baseUrl}/tailwindcss-windows-arm64.exe" : $"{baseUrl}/tailwindcss-windows-x64.exe";
        }
        if (OperatingSystem.IsMacOS())
        {
            return isArm64 ? $"{baseUrl}/tailwindcss-macos-arm64" : $"{baseUrl}/tailwindcss-macos-x64";
        }
        return isArm64 ? $"{baseUrl}/tailwindcss-linux-arm64" : $"{baseUrl}/tailwindcss-linux-x64";
    }

    public override int Execute(CommandContext context, RawArgsSettings settings)
    {
        string repo = CliRuntimeContext.RequireSourceWorkspace().Root;
        string[] raw = CommandArgsHelper.GetEffectiveArgs(context, settings);
        string action = raw.Length > 0 ? raw[0].Trim().ToLowerInvariant() : "build";

        string toolsDir = Path.Combine(repo, "src", "services", "web-ui", "tools");
        string tailwindExe = Path.Combine(toolsDir, OperatingSystem.IsWindows() ? "tailwindcss.exe" : "tailwindcss");
        string inputCss = Path.Combine(repo, "src", "services", "web-ui", "Styles", "input.css");
        string outputCss = Path.Combine(repo, "src", "services", "web-ui", "wwwroot", "css", "tailwind.css");

        if (action == "setup" || !File.Exists(tailwindExe))
        {
            AnsiConsole.MarkupLine("[bold cyan]🎨 Ensuring Tailwind CSS v4 standalone CLI executable is installed...[/]");
            Directory.CreateDirectory(toolsDir);
            string curlExe = OperatingSystem.IsWindows() ? "curl.exe" : "curl";
            int dlCode = RunProcess(curlExe, $"-L -o \"{tailwindExe}\" \"{ResolveTailwindDownloadUrl()}\"", repo);
            if (dlCode != 0 || !File.Exists(tailwindExe))
            {
                AnsiConsole.MarkupLine("[bold red]✗ Failed to download Tailwind CSS v4 standalone CLI.[/]");
                return dlCode == 0 ? 1 : dlCode;
            }
            if (!OperatingSystem.IsWindows())
            {
                try
                {
                    UnixFileMode mode = File.GetUnixFileMode(tailwindExe);
                    File.SetUnixFileMode(tailwindExe, mode | UnixFileMode.UserExecute | UnixFileMode.GroupExecute | UnixFileMode.OtherExecute);
                }
                catch { }
            }

            AnsiConsole.MarkupLine($"[bold green]✓ Tailwind CSS v4 CLI ready at:[/] [white]{Markup.Escape(tailwindExe)}[/]");
            if (action == "setup")
            {
                return 0;
            }
        }

        switch (action)
        {
            case "build":
            {
                AnsiConsole.MarkupLine("[bold cyan]🎨 Building minified Tailwind CSS v4 production bundle...[/]");
                int code = RunProcess(tailwindExe, $"-i \"{inputCss}\" -o \"{outputCss}\" --minify", repo);
                if (code == 0 && File.Exists(outputCss))
                {
                    double sizeKb = new FileInfo(outputCss).Length / 1024.0;
                    AnsiConsole.MarkupLine($"[bold green]✓ Tailwind CSS v4 build completed:[/] [white]{Markup.Escape(outputCss)}[/] [grey]({sizeKb:F1} KB)[/]");
                }
                return code;
            }
            case "watch":
            case "dev":
            {
                AnsiConsole.MarkupLine($"[bold cyan]🎨 Starting Tailwind CSS v4 watch mode ({Markup.Escape(action)})...[/]");
                return RunProcess(tailwindExe, $"-i \"{inputCss}\" -o \"{outputCss}\" --watch", repo);
            }
            default:
            {
                AnsiConsole.MarkupLine("[yellow]Usage: kongor tailwind [build|watch|dev|setup][/]");
                return 1;
            }
        }
    }
}

public sealed class HealthCommandSettings : CommandSettings
{
    [CommandOption("--json")]
    [Description("Output health probe results as machine-readable JSON")]
    public bool Json { get; init; }

    [CommandOption("--strict")]
    [Description("Exit with code 1 if any probed microservice is degraded or unreachable")]
    public bool Strict { get; init; }
}

public sealed class HealthCommand : AsyncCommand<HealthCommandSettings>
{
    private static readonly HttpClient LocalHealthClient = new(new SocketsHttpHandler
    {
        PooledConnectionLifetime = TimeSpan.FromMinutes(2),
        SslOptions = new System.Net.Security.SslClientAuthenticationOptions
        {
            // Local loopback diagnostic health probe only (matches kongor.ps1 health command)
            RemoteCertificateValidationCallback = (_, _, _, _) => true
        }
    })
    {
        Timeout = TimeSpan.FromSeconds(5)
    };

    private sealed record ServiceProbe(string Name, string PrimaryUrl, string? FallbackUrl = null);

    private static readonly ServiceProbe[] Probes =
    [
        new("KONGOR Master Server", "http://127.0.0.1:55555/health"),
        new("ZORGATH Web API", "http://127.0.0.1:5200/health"),
        new("DAWNBRINGER Web UI", "http://127.0.0.1:5266/health"),
        new("TRANSMUTANSTEIN Chat", "http://127.0.0.1:5554/health", "https://127.0.0.1:5554/health")
    ];

    public override async Task<int> ExecuteAsync(CommandContext context, HealthCommandSettings settings)
    {
        if (!settings.Json)
        {
            AnsiConsole.MarkupLine("[bold cyan]🩺 Querying ProjectKongor Microservice Health Endpoints (/health)...[/]\n");
        }

        List<object> jsonResults = [];
        bool allHealthy = true;

        foreach (ServiceProbe probe in Probes)
        {
            (string status, string? details, string endpointUsed, long elapsedMs) = await ProbeServiceAsync(probe);
            bool healthy = string.Equals(status, "Healthy", StringComparison.OrdinalIgnoreCase);
            if (!healthy)
            {
                allHealthy = false;
            }

            jsonResults.Add(new
            {
                service = probe.Name,
                endpoint = endpointUsed,
                status,
                elapsedMs,
                details
            });

            if (!settings.Json)
            {
                string color = status.Equals("Healthy", StringComparison.OrdinalIgnoreCase)
                    ? "green"
                    : status.Equals("Degraded", StringComparison.OrdinalIgnoreCase)
                        ? "yellow"
                        : status.Equals("Unreachable", StringComparison.OrdinalIgnoreCase)
                            ? "grey"
                            : "red";
                string suffix = string.IsNullOrWhiteSpace(details) ? string.Empty : $" [grey]({Markup.Escape(details)})[/]";
                AnsiConsole.MarkupLine($"  [bold cyan]{Markup.Escape(probe.Name)}[/]: [bold {color}]{Markup.Escape(status)}[/] [grey]{elapsedMs}ms[/]{suffix}");
            }
        }

        if (settings.Json)
        {
            Console.WriteLine(JsonSerializer.Serialize(new
            {
                allHealthy,
                services = jsonResults
            }, new JsonSerializerOptions { WriteIndented = true }));
        }
        else if (allHealthy)
        {
            AnsiConsole.MarkupLine("\n[bold green]✓ All checked ProjectKongor microservices are Healthy.[/]");
        }
        else
        {
            AnsiConsole.MarkupLine("\n[yellow]⚠ One or more microservices are unreachable or degraded.[/]");
        }

        return settings.Strict && !allHealthy ? 1 : 0;
    }

    private static async Task<(string Status, string? Details, string EndpointUsed, long ElapsedMs)> ProbeServiceAsync(ServiceProbe probe)
    {
        Stopwatch sw = Stopwatch.StartNew();
        string endpoint = probe.PrimaryUrl;
        try
        {
            string body = await FetchHealthBodyAsync(probe.PrimaryUrl);
            sw.Stop();
            return (ExtractHealthStatus(body), null, endpoint, sw.ElapsedMilliseconds);
        }
        catch (Exception primaryEx)
        {
            if (!string.IsNullOrWhiteSpace(probe.FallbackUrl))
            {
                endpoint = probe.FallbackUrl;
                try
                {
                    string body = await FetchHealthBodyAsync(probe.FallbackUrl);
                    sw.Stop();
                    return (ExtractHealthStatus(body), null, endpoint, sw.ElapsedMilliseconds);
                }
                catch (Exception fallbackEx)
                {
                    sw.Stop();
                    return ("Unreachable", fallbackEx.Message, endpoint, sw.ElapsedMilliseconds);
                }
            }

            sw.Stop();
            return ("Unreachable", primaryEx.Message, endpoint, sw.ElapsedMilliseconds);
        }
    }

    private static async Task<string> FetchHealthBodyAsync(string url)
    {
        using HttpRequestMessage req = new(HttpMethod.Get, url);
        using HttpResponseMessage resp = await LocalHealthClient.SendAsync(req);
        string body = await resp.Content.ReadAsStringAsync();
        if (!resp.IsSuccessStatusCode && string.IsNullOrWhiteSpace(body))
        {
            return $"HTTP {(int)resp.StatusCode}";
        }
        return body;
    }

    private static string ExtractHealthStatus(string rawBody)
    {
        string trimmed = rawBody.Trim();
        if (string.IsNullOrWhiteSpace(trimmed))
        {
            return "Healthy";
        }

        if (trimmed.StartsWith('{'))
        {
            try
            {
                using JsonDocument doc = JsonDocument.Parse(trimmed);
                if (doc.RootElement.TryGetProperty("status", out JsonElement statusEl) &&
                    statusEl.ValueKind == JsonValueKind.String)
                {
                    return statusEl.GetString() ?? "Healthy";
                }
            }
            catch
            {
                // Fall back to plain-text parsing
            }
        }

        return trimmed.Length <= 40 ? trimmed : "Healthy";
    }
}
