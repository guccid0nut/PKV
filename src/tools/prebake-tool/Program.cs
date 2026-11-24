// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

using System.ComponentModel;
using System.Diagnostics;
using System.Text;
using ASPIRE.Common.Prebaking;
using ASPIRE.Common.Prebaking.Generators;
using ASPIRE.Common.Prebaking.Serialization;
using ASPIRE.Common.Telemetry;
using ASPIRE.PrebakeTool;
using Kongor.Modding.Vfs;
using Microsoft.Extensions.Logging;
using NEXUS.Contracts.EntityCache;
using Spectre.Console;
using Spectre.Console.Cli;
using ValidationResult = ASPIRE.Common.Prebaking.Serialization.ValidationResult;

namespace Kongor.PrebakeTool;

public class PrebakeSettings : CommandSettings
{
    [CommandOption("-g|--game-dir <DIR>")]
    [Description("Path to HoN/Kongor game client directory (default: games/client)")]
    public string? GameDir { get; init; }

    [CommandOption("-s|--shared-dir <DIR>")]
    [Description("Optional path to HoN/Kongor shared entities directory (falls back to client .s2z archives if omitted)")]
    public string? SharedDir { get; init; }

    [CommandOption("-o|--output-dir <DIR>")]
    [Description("Target directory for pre-baked override assets (default: games/client/override)")]
    [DefaultValue("games/client/override")]
    public string OutputDir { get; init; } = "games/client/override";

    [CommandOption("--validate")]
    [Description("Run verification and integrity checks after pre-baking (default: true)")]
    [DefaultValue(true)]
    public bool RunValidation { get; init; } = true;

    [CommandOption("-v|--verbose")]
    [Description("Enable verbose diagnostic logging")]
    [DefaultValue(false)]
    public bool Verbose { get; init; }

    [CommandOption("--log-file <PATH>")]
    [Description("Path to output structured NDJSON diagnostic telemetry log")]
    public string? LogFile { get; init; }

    [CommandOption("-c|--compression-level <LEVEL>")]
    [Description("Zstandard compression level (1-22, default: 3)")]
    [DefaultValue(3)]
    public int CompressionLevel { get; init; } = 3;

    [CommandOption("-m|--target-mode <MODE>")]
    [Description("Target deployment mode: 'vfs', 's2z', or 'both' (default: both)")]
    [DefaultValue("both")]
    public string TargetMode { get; init; } = "both";

    [CommandOption("--dry-run")]
    [Description("Simulate pre-baking execution without writing files to disk")]
    [DefaultValue(false)]
    public bool DryRun { get; init; }

    public string GetEffectiveGameDir() => !string.IsNullOrWhiteSpace(GameDir) ? GameDir : Program.ResolveDefaultGameDir();
    public string? GetEffectiveSharedDir() => !string.IsNullOrWhiteSpace(SharedDir) ? SharedDir : Program.ResolveDefaultSharedDir();
}

public sealed class AllCommand : AsyncCommand<PrebakeSettings>
{
    public override async Task<int> ExecuteAsync(CommandContext context, PrebakeSettings settings)
    {
        await Program.HandleAllAsync(
            settings.GetEffectiveGameDir(),
            settings.GetEffectiveSharedDir() ?? string.Empty,
            settings.OutputDir,
            settings.RunValidation,
            settings.Verbose,
            settings.LogFile,
            settings.CompressionLevel,
            settings.TargetMode,
            settings.DryRun);
        return 0;
    }
}

public sealed class EntitiesCommand : AsyncCommand<PrebakeSettings>
{
    public override async Task<int> ExecuteAsync(CommandContext context, PrebakeSettings settings)
    {
        await Program.HandleEntitiesAsync(
            settings.GetEffectiveGameDir(),
            settings.GetEffectiveSharedDir(),
            settings.OutputDir,
            settings.CompressionLevel,
            settings.Verbose);
        return 0;
    }
}

public sealed class LuaCommand : AsyncCommand<PrebakeSettings>
{
    public override async Task<int> ExecuteAsync(CommandContext context, PrebakeSettings settings)
    {
        await Program.HandleLuaAsync(
            settings.GetEffectiveGameDir(),
            settings.GetEffectiveSharedDir(),
            settings.OutputDir,
            settings.Verbose);
        return 0;
    }
}

public sealed class BundleCommand : AsyncCommand<PrebakeSettings>
{
    public override async Task<int> ExecuteAsync(CommandContext context, PrebakeSettings settings)
    {
        await Program.HandleBundleAsync(
            settings.GetEffectiveGameDir(),
            settings.GetEffectiveSharedDir(),
            settings.OutputDir,
            settings.Verbose);
        return 0;
    }
}

public sealed class ValidateCommand : AsyncCommand<PrebakeSettings>
{
    public override async Task<int> ExecuteAsync(CommandContext context, PrebakeSettings settings)
    {
        await Program.HandleValidateAsync(
            settings.GetEffectiveGameDir(),
            settings.OutputDir,
            settings.Verbose);
        return 0;
    }
}

public sealed class CleanCommand : AsyncCommand<PrebakeSettings>
{
    public override async Task<int> ExecuteAsync(CommandContext context, PrebakeSettings settings)
    {
        await Program.HandleCleanAsync(
            settings.GetEffectiveGameDir(),
            settings.Verbose);
        return 0;
    }
}

public sealed class BootstrapCommand : AsyncCommand<PrebakeSettings>
{
    public override async Task<int> ExecuteAsync(CommandContext context, PrebakeSettings settings)
    {
        string gameDir = settings.GetEffectiveGameDir();
        string repoRoot = Path.GetDirectoryName(Path.GetDirectoryName(gameDir)) ?? Directory.GetCurrentDirectory();
        await AssetBootstrapService.BootstrapAllAssetsAsync(repoRoot);
        AnsiConsole.MarkupLine("[bold green]✔ Bootstrapped all physical prebaked assets and mod packages.[/]");
        return 0;
    }
}

public class StripServerSettings : CommandSettings
{
    [CommandOption("-s|--source <DIR>")]
    [Description("Source server distribution directory (default: games/server)")]
    [DefaultValue("games/server")]
    public string SourceDir { get; init; } = "games/server";

    [CommandOption("-t|--target <DIR>")]
    [Description("Target headless output directory (default: games/server-headless)")]
    [DefaultValue("games/server-headless")]
    public string TargetDir { get; init; } = "games/server-headless";
}

public sealed class StripServerCommand : AsyncCommand<StripServerSettings>
{
    public override async Task<int> ExecuteAsync(CommandContext context, StripServerSettings settings)
    {
        AnsiConsole.MarkupLine("[bold cyan]Executing Headless Dedicated Server Asset Stripping...[/]");
        HeadlessStrippingResult result = await HeadlessAssetStripperService.StripServerDistributionAsync(
            settings.SourceDir,
            settings.TargetDir);

        AnsiConsole.MarkupLine($"[bold green]✔ Headless stripping completed successfully in {result.ElapsedMilliseconds} ms![/]");
        AnsiConsole.MarkupLine($"Files Scanned: [cyan]{result.TotalFilesScanned}[/] | Preserved: [green]{result.FilesPreserved}[/] | Stripped: [yellow]{result.FilesStripped}[/]");
        AnsiConsole.MarkupLine($"Size Reduced from [yellow]{result.OriginalBytes / (1024.0 * 1024.0):F1} MB[/] to [green]{result.StrippedBytes / (1024.0 * 1024.0):F1} MB[/] ([bold green]{result.ReductionPercentage:F1}% savings[/])");

        return 0;
    }
}

public static class Program
{
    public static async Task<int> Main(string[] args)
    {
        if (args.Length == 1 && (args[0].Equals("--version", StringComparison.OrdinalIgnoreCase) ||
                                 args[0].Equals("-v", StringComparison.OrdinalIgnoreCase) ||
                                 args[0].Equals("version", StringComparison.OrdinalIgnoreCase)))
        {
            AnsiConsole.MarkupLine($"[bold cyan]prebake-tool[/] version [green]{NEXUS.Contracts.Domain.NexusVersionInfo.PlatformVersion}[/]");
            return 0;
        }

        CommandApp<AllCommand> app = new();
        app.Configure(config =>
        {
            config.SetApplicationName("prebake-tool");
            config.SetApplicationVersion(NEXUS.Contracts.Domain.NexusVersionInfo.PlatformVersion);

            config.AddCommand<AllCommand>("all")
                .WithDescription("Execute full pre-baking pipeline (parse, serialize, lua cache, s2z bundle, stage)");

            config.AddCommand<EntitiesCommand>("entities")
                .WithDescription("Parse game entity XMLs and serialize to entity_cache.bin (MemoryPack + Zstd)");

            config.AddCommand<LuaCommand>("lua")
                .WithDescription("Generate hero_definitions_cache.lua for instant client UI resolution");

            config.AddCommand<BundleCommand>("bundle")
                .WithDescription("Package pre-baked assets into resources0_prebaked.s2z");

            config.AddCommand<ValidateCommand>("validate")
                .WithDescription("Validate existing pre-baked cache binaries, checksums, and Lua syntax");

            config.AddCommand<CleanCommand>("clean")
                .WithDescription("Clean override directory and restore baseline client state");

            config.AddCommand<BootstrapCommand>("bootstrap")
                .WithDescription("Bootstrap all prebaked assets, mod packages, and icon textures");

            config.AddCommand<StripServerCommand>("strip-server")
                .WithDescription("Strip client media (textures, audio, video) to create a ~200MB headless server distribution");
        });

        return await app.RunAsync(args);
    }

    public static async Task HandleAllAsync(
        string gameDir,
        string sharedDir,
        string outputDir,
        bool validate,
        bool verbose,
        string? logFile,
        int compressionLevel,
        string targetMode,
        bool dryRun)
    {
        Stopwatch totalSw = Stopwatch.StartNew();

        AnsiConsole.Write(new FigletText("Kongor Prebake").Color(Color.Gold1));
        AnsiConsole.MarkupLine(
            $"[bold green]Executing Pre-Baking Pipeline[/] | GameDir: [cyan]{gameDir}[/] | Output: [cyan]{outputDir}[/]");

        if (dryRun) AnsiConsole.MarkupLine("[yellow bold]*** DRY RUN MODE ENABLED - No files will be modified ***[/]");

        using ILoggerFactory loggerFactory = LoggerFactory.Create(builder =>
        {
            if (verbose) builder.AddConsole().SetMinimumLevel(LogLevel.Debug);
        });
        ILogger logger = loggerFactory.CreateLogger("Kongor.PrebakeTool");

        if (string.IsNullOrWhiteSpace(sharedDir) || !Directory.Exists(sharedDir))
        {
            AnsiConsole.MarkupLine("[cyan]ℹ[/] No shared directory specified or found on disk; harvesting entities directly from client .s2z archives.");
            sharedDir = null!;
        }

        // Step 1: Parse entities from game XMLs & shared entity tree
        EntityHarvestEngine harvestEngine = new();
        EntityCacheContainer container = new();

        await AnsiConsole.Status().StartAsync("Harvesting & Parsing Game Entity XMLs...", async ctx =>
        {
            ctx.Spinner(Spinner.Known.Dots);
            container = await harvestEngine.HarvestAllAsync(gameDir, sharedDir);
            AnsiConsole.MarkupLine(
                $"[green]✔[/] Harvested [bold]{container.Heroes.Count}[/] heroes, [bold]{container.AltAvatars.Count}[/] avatars, [bold]{container.Abilities.Count}[/] abilities, [bold]{container.CosmeticUpgrades.Count}[/] cosmetics.");
        });

        // Step 2: Generate Lua Cache
        string luaContent = string.Empty;
        await AnsiConsole.Status().StartAsync("Generating hero_definitions_cache.lua...", async ctx =>
        {
            ctx.Spinner(Spinner.Known.Dots);
            luaContent = LuaCacheGenerator.GenerateLuaCache(container);
            AnsiConsole.MarkupLine(
                $"[green]✔[/] Generated Lua Cache manifest ([bold]{luaContent.Length / 1024.0:F1} KB[/]).");
            await Task.CompletedTask;
        });

        // Step 3: Serialize MemoryPack + Zstd
        byte[] binaryData = [];
        await AnsiConsole.Status().StartAsync($"Serializing MemoryPack binary with Zstd (level {compressionLevel})...",
            async ctx =>
            {
                ctx.Spinner(Spinner.Known.Dots);
                binaryData = EntityCacheSerializer.Serialize(container, compressionLevel);
                AnsiConsole.MarkupLine(
                    $"[green]✔[/] Serialized binary cache ([bold]{binaryData.Length / 1024.0:F1} KB[/], Checksum: [bold]0x{container.ChecksumXxHash3:X16}[/]).");
                await Task.CompletedTask;
            });

        // Step 3b: Serialize FlatBuffers Binary (schemas/hero_descriptors.fbs)
        byte[] flatBufferData = [];
        await AnsiConsole.Status().StartAsync("Generating FlatBuffers binary (hero_descriptors.bin)...", async ctx =>
        {
            ctx.Spinner(Spinner.Known.Dots);
            flatBufferData = HeroFlatBufferGenerator.Generate(container);
            AnsiConsole.MarkupLine(
                $"[green]✔[/] Generated FlatBuffers binary ([bold]{flatBufferData.Length / 1024.0:F1} KB[/], Identifier: [bold]KNFB[/]).");
            await Task.CompletedTask;
        });

        // Step 4: Staging / Bundling
        byte[] luaBytes;
        try
        {
            luaBytes = await LuaBytecodeCompilerService.CompileStringToBytecodeAsync(luaContent, "hero_definitions_cache.lua");
        }
        catch
        {
            luaBytes = Encoding.UTF8.GetBytes(luaContent);
        }

        Dictionary<string, byte[]> filesToDeploy = new(StringComparer.OrdinalIgnoreCase)
        {
            ["ui/scripts/newui/hero_definitions_cache.lua"] = luaBytes,
            ["prebaked_assets/entity_cache.bin"] = binaryData,
            ["prebaked_assets/hero_descriptors.bin"] = flatBufferData
        };

        if (!dryRun)
        {
            if (targetMode is "vfs" or "both")
                await AnsiConsole.Status().StartAsync("Staging loose files to VFS override/...", async ctx =>
                {
                    ctx.Spinner(Spinner.Known.Dots);
                    await VfsOverrideStagingService.StageLooseFilesAsync(gameDir, filesToDeploy, logger);
                    AnsiConsole.MarkupLine("[green]✔[/] Staged loose files to VFS override.");
                });

            if (targetMode is "s2z" or "both")
            {
                string s2zTarget = Path.Combine(gameDir, "game", "resources0_prebaked.s2z");
                await AnsiConsole.Status().StartAsync("Packaging resources0_prebaked.s2z...", async ctx =>
                {
                    ctx.Spinner(Spinner.Known.Dots);
                    await S2zBundlerService.BundleToS2zAsync(s2zTarget, filesToDeploy, logger: logger);
                    AnsiConsole.MarkupLine($"[green]✔[/] Packaged [bold]{s2zTarget}[/].");
                });
            }
        }

        // Step 5: Validation
        if (validate)
            await AnsiConsole.Status().StartAsync("Validating pre-baked cache integrity...", async ctx =>
            {
                ctx.Spinner(Spinner.Known.Dots);
                EntityCacheContainer deserialized = EntityCacheDeserializer.Deserialize(binaryData);
                if (deserialized.Heroes.Count != container.Heroes.Count)
                    throw new InvalidDataException(
                        "Validation failed: Deserialized hero count does not match original!");
                HeroFlatBufferLoader fbLoader = HeroFlatBufferLoader.Load(flatBufferData);
                if (fbLoader.HeroCount != container.Heroes.Count)
                    throw new InvalidDataException("Validation failed: FlatBuffer hero count does not match original!");
                AnsiConsole.MarkupLine("[green]✔[/] Binary cache & FlatBuffers integrity verified 100%.");
                await Task.CompletedTask;
            });

        totalSw.Stop();

        // Print Summary Table
        Table table = new Table().Border(TableBorder.Rounded);
        table.AddColumn("[bold]Metric[/]");
        table.AddColumn("[bold]Value[/]");
        table.AddRow("Total Heroes", container.Heroes.Count.ToString());
        table.AddRow("Total Alt Avatars", container.AltAvatars.Count.ToString());
        table.AddRow("Total Abilities", container.Abilities.Count.ToString());
        table.AddRow("Total States", container.States.Count.ToString());
        table.AddRow("Total Projectiles", container.Projectiles.Count.ToString());
        table.AddRow("Total Items", container.Items.Count.ToString());
        table.AddRow("Total Cosmetics", container.CosmeticUpgrades.Count.ToString());
        table.AddRow("Preload Resources", container.AllPreloadResources.Count.ToString());
        table.AddRow("MemoryPack Cache Size", $"{binaryData.Length / 1024.0:F2} KB");
        table.AddRow("FlatBuffers Cache Size", $"{flatBufferData.Length / 1024.0:F2} KB");
        table.AddRow("Lua Cache Size", $"{luaContent.Length / 1024.0:F2} KB");
        table.AddRow("XxHash3 Checksum", $"0x{container.ChecksumXxHash3:X16}");
        table.AddRow("Total Pipeline Time", $"{totalSw.ElapsedMilliseconds} ms");
        AnsiConsole.Write(table);

        // Telemetry NDJSON write
        if (!string.IsNullOrWhiteSpace(logFile))
            EntityCacheTelemetryLogger.LogPipelineExecuted(
                gameDir,
                sharedDir,
                outputDir,
                container.Heroes.Count,
                container.AltAvatars.Count,
                container.Abilities.Count,
                container.States.Count,
                container.Projectiles.Count,
                container.Items.Count,
                container.CosmeticUpgrades.Count,
                container.AllPreloadResources.Count,
                binaryData.Length,
                luaContent.Length,
                $"0x{container.ChecksumXxHash3:X16}",
                totalSw.ElapsedMilliseconds,
                targetMode,
                dryRun,
                logFile);

        AnsiConsole.MarkupLine("[bold green]✔ Pre-Baking Completed Successfully![/]");
    }

    public static async Task HandleEntitiesAsync(string gameDir, string? sharedDir, string outputDir, int compLevel,
        bool verbose)
    {
        if (string.IsNullOrWhiteSpace(sharedDir) || !Directory.Exists(sharedDir)) sharedDir = null;
        AnsiConsole.MarkupLine("[bold cyan]Extracting & Serializing Entities to Binary Cache...[/]");
        EntityHarvestEngine harvestEngine = new();
        EntityCacheContainer container = await harvestEngine.HarvestAllAsync(gameDir, sharedDir);

        string targetFile = Path.Combine(outputDir, "prebaked_assets", "entity_cache.bin");
        await EntityCacheSerializer.SerializeToFileAsync(container, targetFile, compLevel);
        AnsiConsole.MarkupLine(
            $"[bold green]✔ Entity cache saved to {targetFile} (Checksum: 0x{container.ChecksumXxHash3:X16})[/]");

        string fbFile = Path.Combine(outputDir, "prebaked_assets", "hero_descriptors.bin");
        await HeroFlatBufferGenerator.GenerateToFileAsync(container, fbFile);
        AnsiConsole.MarkupLine($"[bold green]✔ FlatBuffers cache saved to {fbFile} (Identifier: KNFB)[/]");
    }

    public static async Task HandleLuaAsync(string gameDir, string? sharedDir, string outputDir, bool verbose)
    {
        if (string.IsNullOrWhiteSpace(sharedDir) || !Directory.Exists(sharedDir)) sharedDir = null;
        AnsiConsole.MarkupLine("[bold cyan]Generating hero_definitions_cache.lua...[/]");
        EntityHarvestEngine harvestEngine = new();
        EntityCacheContainer container = await harvestEngine.HarvestAllAsync(gameDir, sharedDir);

        string targetFile = Path.Combine(outputDir, "ui", "scripts", "newui", "hero_definitions_cache.lua");
        await LuaCacheGenerator.GenerateLuaCacheAsync(container, targetFile);
        AnsiConsole.MarkupLine($"[bold green]✔ Lua cache generated at {targetFile}[/]");

        // Pre-compile hero definitions into 64-bit Lua 5.1 bytecode
        var compRes = await LuaBytecodeCompilerService.CompileFileAsync(targetFile, targetFile);
        if (compRes.Success)
        {
            AnsiConsole.MarkupLine($"[bold green]✔ Pre-compiled hero_definitions_cache.lua into 64-bit bytecode ({compRes.SourceBytes} -> {compRes.BytecodeBytes} bytes)[/]");
        }

        // Pre-compile all UI scripts in candidate script directories
        string[] candidateDirs =
        [
            Path.Combine(outputDir, "ui", "scripts"),
            Path.Combine(gameDir, "override", "ui", "scripts")
        ];

        foreach (string scriptsDir in candidateDirs.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (Directory.Exists(scriptsDir))
            {
                AnsiConsole.MarkupLine($"[bold cyan]Pre-compiling Lua UI scripts in {scriptsDir}...[/]");
                var summary = await LuaBytecodeCompilerService.CompileDirectoryAsync(scriptsDir, scriptsDir, inPlace: true, excludeFileNames: ["tao.lua"]);
                AnsiConsole.MarkupLine(
                    $"[bold green]✔ Compiled {summary.SucceededFiles}/{summary.TotalFiles} scripts into 64-bit Lua 5.1 bytecode ({summary.TotalSourceBytes / 1024.0:F1} KB -> {summary.TotalBytecodeBytes / 1024.0:F1} KB in {summary.ElapsedMilliseconds} ms)[/]");
            }
        }
    }

    public static async Task HandleBundleAsync(string gameDir, string? sharedDir, string outputDir, bool verbose)
    {
        if (string.IsNullOrWhiteSpace(sharedDir) || !Directory.Exists(sharedDir)) sharedDir = null;
        AnsiConsole.MarkupLine("[bold cyan]Packaging Pre-Baked S2Z Archive...[/]");
        EntityHarvestEngine harvestEngine = new();
        EntityCacheContainer container = await harvestEngine.HarvestAllAsync(gameDir, sharedDir);

        string luaContent = LuaCacheGenerator.GenerateLuaCache(container);
        byte[] luaBytes;
        try
        {
            luaBytes = await LuaBytecodeCompilerService.CompileStringToBytecodeAsync(luaContent, "hero_definitions_cache.lua");
        }
        catch
        {
            luaBytes = Encoding.UTF8.GetBytes(luaContent);
        }

        byte[] binaryData = EntityCacheSerializer.Serialize(container);
        byte[] flatBufferData = HeroFlatBufferGenerator.Generate(container);

        Dictionary<string, byte[]> files = new(StringComparer.OrdinalIgnoreCase)
        {
            ["ui/scripts/newui/hero_definitions_cache.lua"] = luaBytes,
            ["prebaked_assets/entity_cache.bin"] = binaryData,
            ["prebaked_assets/hero_descriptors.bin"] = flatBufferData
        };

        string s2zTarget = Path.Combine(gameDir, "game", "resources0_prebaked.s2z");
        BundlingResult res = await S2zBundlerService.BundleToS2zAsync(s2zTarget, files);
        AnsiConsole.MarkupLine(
            $"[bold green]✔ Packaged S2Z archive at {res.ArchivePath} ({res.TotalBytes} bytes in {res.ElapsedMilliseconds} ms)[/]");
    }

    public static async Task HandleValidateAsync(string gameDir, string outputDir, bool verbose)
    {
        AnsiConsole.MarkupLine("[bold cyan]Validating Pre-Baked Artifacts...[/]");
        string binPath = Path.Combine(outputDir, "prebaked_assets", "entity_cache.bin");
        if (!File.Exists(binPath)) binPath = Path.Combine(gameDir, "override", "prebaked_assets", "entity_cache.bin");

        ValidationResult valRes = EntityCacheDeserializer.ValidateFile(binPath);
        if (valRes.IsValid)
            AnsiConsole.MarkupLine(
                $"[bold green]✔ Binary Cache Valid:[/] {valRes.Message} (Entities: {valRes.Header.EntityCount}, Checksum: 0x{valRes.Header.XxHash3Checksum:X16})");
        else
            AnsiConsole.MarkupLine($"[bold red]✖ Binary Cache Invalid:[/] {valRes.Message}");

        string fbPath = Path.Combine(outputDir, "prebaked_assets", "hero_descriptors.bin");
        if (!File.Exists(fbPath)) fbPath = Path.Combine(gameDir, "override", "prebaked_assets", "hero_descriptors.bin");

        if (File.Exists(fbPath))
        {
            HeroFlatBufferLoader fbLoader = HeroFlatBufferLoader.LoadFromFile(fbPath);
            AnsiConsole.MarkupLine(
                $"[bold green]✔ FlatBuffers Cache Valid:[/] {fbPath} (Heroes: {fbLoader.HeroCount}, Items: {fbLoader.ItemCount}, Checksum: 0x{fbLoader.Manifest.ChecksumXxHash3:X16})");
        }
        else
        {
            AnsiConsole.MarkupLine($"[bold yellow]! FlatBuffers Cache Not Found:[/] {fbPath}");
        }

        string luaPath = Path.Combine(outputDir, "ui", "scripts", "newui", "hero_definitions_cache.lua");
        if (!File.Exists(luaPath))
            luaPath = Path.Combine(gameDir, "override", "ui", "scripts", "newui", "hero_definitions_cache.lua");

        if (File.Exists(luaPath))
        {
            FileInfo fi = new(luaPath);
            byte[] header = new byte[12];
            using (var fs = File.OpenRead(luaPath))
            {
                _ = fs.Read(header, 0, 12);
            }
            bool isBytecode = header.Length >= 5 && header[0] == 0x1B && header[1] == 0x4C && header[2] == 0x75 && header[3] == 0x61 && header[4] == 0x51;
            string formatStr = isBytecode ? "[green]64-bit Lua 5.1 Bytecode[/]" : "[yellow]Plain Text Lua[/]";
            AnsiConsole.MarkupLine($"[bold green]✔ Lua Cache Exists:[/] {luaPath} ({fi.Length} bytes, {formatStr})");
        }
        else
        {
            AnsiConsole.MarkupLine($"[bold yellow]! Lua Cache Not Found:[/] {luaPath}");
        }

        await Task.CompletedTask;
    }

    public static async Task HandleCleanAsync(string gameDir, bool verbose)
    {
        AnsiConsole.MarkupLine("[bold yellow]Cleaning VFS Override Directory...[/]");
        bool res = await RollbackJournal.ExecuteRollbackAsync(gameDir);
        if (res)
            AnsiConsole.MarkupLine(
                "[bold green]✔ Successfully cleaned override directory and restored client state.[/]");
        else
            AnsiConsole.MarkupLine("[bold red]✖ Failed to clean override directory.[/]");
    }

    public static string ResolveDefaultGameDir()
    {
        if (Kongor.Core.Configuration.KongorWorkspaceResolver.TryResolve(out var ws) && ws is not null)
        {
            string wsClient = Path.Combine(ws.Root, "games", "client");
            if (Directory.Exists(wsClient)) return wsClient;
        }

        if (Directory.Exists("games/client")) return Path.GetFullPath("games/client");
        if (Directory.Exists("../games/client")) return Path.GetFullPath("../games/client");
        if (Directory.Exists("../../games/client")) return Path.GetFullPath("../../games/client");
        return Path.GetFullPath("games/client");
    }

    public static string? ResolveDefaultSharedDir()
    {
        if (Kongor.Core.Configuration.KongorWorkspaceResolver.TryResolve(out var ws) && ws is not null)
        {
            string wsShared = Path.Combine(ws.Root, "games", "shared");
            if (Directory.Exists(wsShared)) return wsShared;
        }

        if (Directory.Exists("games/shared")) return Path.GetFullPath("games/shared");
        if (Directory.Exists("../games/shared")) return Path.GetFullPath("../games/shared");
        if (Directory.Exists("../../games/shared")) return Path.GetFullPath("../../games/shared");
        return null;
    }
}
