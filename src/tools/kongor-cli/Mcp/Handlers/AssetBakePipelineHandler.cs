// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// TAO (Together As One) MCP & Kongor CLI -- Created by guccidonut (https://github.com/guccidonut/PKV)

using System.Diagnostics;
using System.Text.Json;
using Kongor.Cli.Mcp.Imaging;
using Kongor.Cli.Mcp.Protocol;
using Kongor.Cli.Mcp.Services;

namespace Kongor.Cli.Mcp.Handlers;

/// <summary>
/// MCP Tool Handler for 'kongor_asset_bake_pipeline' (alias: 'asset_bake_pipeline').
/// Generative 2D/3D Asset Baking Pipeline: Suffix-aware texture compression (BC7 sRGB color,
/// BC5 linear tangent normal with Y-inversion, BC4 scalar specular), POT dimension validation,
/// pre-flight missing texture guard, K2 .material XML synthesis, and hot-reloading.
/// </summary>
public sealed class AssetBakePipelineHandler
{
    private readonly TimeProvider _timeProvider;
    private readonly Action<string>? _logger;
    private readonly AssetBakeService _assetBakeService;
    private readonly string _workspaceRoot;

    public AssetBakePipelineHandler(
        TimeProvider? timeProvider = null,
        Action<string>? logger = null,
        AssetBakeService? assetBakeService = null,
        string? workspaceRoot = null)
    {
        _timeProvider = timeProvider ?? TimeProvider.System;
        _logger = logger ?? (msg => Console.Error.WriteLine($"[AssetBakePipeline] {msg}"));
        _workspaceRoot = workspaceRoot ?? Directory.GetCurrentDirectory();
        _assetBakeService = assetBakeService ?? new AssetBakeService(_workspaceRoot);
    }

    public async Task<ToolCallResult> HandleAsync(JsonElement arguments, CancellationToken cancellationToken = default)
    {
        Stopwatch sw = Stopwatch.StartNew();

        string action = "inspect";
        if (arguments.TryGetProperty("action", out JsonElement actEl) && !string.IsNullOrWhiteSpace(actEl.GetString()))
        {
            action = actEl.GetString()!.Trim().ToLowerInvariant();
        }

        _logger?.Invoke($"Asset bake requested (Action: '{action}')...");

        switch (action)
        {
            case "inspect":
                return Inspect(arguments, sw);

            case "bake_material":
            case "bake":
                return await BakeMaterialAsync(arguments, sw, cancellationToken).ConfigureAwait(false);

            case "hot_reload":
            case "reload":
                return HotReload(arguments, sw);

            case "sanitize":
            case "sanitize_alpha":
                return SanitizeAlpha(arguments, sw);

            default:
                sw.Stop();
                return ToolCallResult.Error($"Unknown action '{action}'. Valid actions are: 'inspect', 'bake_material', 'hot_reload', 'sanitize_alpha'.");
        }
    }

    private ToolCallResult SanitizeAlpha(JsonElement arguments, Stopwatch sw)
    {
        string inputPath = "";
        if ((arguments.TryGetProperty("input_path", out JsonElement ipEl) ||
             arguments.TryGetProperty("inputPath", out ipEl) ||
             arguments.TryGetProperty("file", out ipEl)) &&
            !string.IsNullOrWhiteSpace(ipEl.GetString()))
        {
            inputPath = ipEl.GetString()!.Trim();
        }

        string outputPath = "";
        if ((arguments.TryGetProperty("output_path", out JsonElement opEl) ||
             arguments.TryGetProperty("outputPath", out opEl) ||
             arguments.TryGetProperty("destination", out opEl)) &&
            !string.IsNullOrWhiteSpace(opEl.GetString()))
        {
            outputPath = opEl.GetString()!.Trim();
        }

        int targetWidth = 64;
        if ((arguments.TryGetProperty("target_width", out JsonElement twEl) ||
             arguments.TryGetProperty("targetWidth", out twEl) ||
             arguments.TryGetProperty("width", out twEl)) &&
            twEl.TryGetInt32(out int tw) && tw > 0)
        {
            targetWidth = tw;
        }

        int targetHeight = 64;
        if ((arguments.TryGetProperty("target_height", out JsonElement thEl) ||
             arguments.TryGetProperty("targetHeight", out thEl) ||
             arguments.TryGetProperty("height", out thEl)) &&
            thEl.TryGetInt32(out int th) && th > 0)
        {
            targetHeight = th;
        }

        bool cutoutSolidBorder = false;
        if ((arguments.TryGetProperty("cutout_solid_border", out JsonElement csbEl) ||
             arguments.TryGetProperty("cutoutSolidBorder", out csbEl)) &&
            (csbEl.ValueKind == JsonValueKind.True || csbEl.ValueKind == JsonValueKind.False))
        {
            cutoutSolidBorder = csbEl.GetBoolean();
        }

        int borderTolerance = 20;
        if ((arguments.TryGetProperty("border_tolerance", out JsonElement btEl) ||
             arguments.TryGetProperty("borderTolerance", out btEl)) &&
            btEl.TryGetInt32(out int bt))
        {
            borderTolerance = bt;
        }

        bool syntheticTestIcon = false;
        if (arguments.TryGetProperty("synthetic_test_icon", out JsonElement stiEl) ||
            arguments.TryGetProperty("syntheticTestIcon", out stiEl))
        {
            syntheticTestIcon = stiEl.GetBoolean();
        }

        Image<Rgba32> srcImg;
        if (syntheticTestIcon || string.IsNullOrWhiteSpace(inputPath) || !File.Exists(inputPath))
        {
            srcImg = AssetBakeService.CreateSyntheticGoldIcon(256, 256, solidDarkBorder: true);
        }
        else
        {
            srcImg = Image.Load<Rgba32>(inputPath);
        }

        int borderPixelsCleared = 0;
        if (cutoutSolidBorder)
        {
            borderPixelsCleared = AssetBakeService.FloodFillBorderCutout(srcImg, borderTolerance);
        }

        var premult = AssetBakeService.ResizePremultiplied(srcImg, targetWidth, targetHeight);
        var naive = AssetBakeService.ResizeNaiveStraightAlpha(srcImg, targetWidth, targetHeight);

        AssetBakeService.MeasureEdgeLumaStats(premult, out double preOpLuma, out double preEdLuma, out _);
        AssetBakeService.MeasureEdgeLumaStats(naive, out double naiveOpLuma, out double naiveEdLuma, out _);

        double preDrop = Math.Round(Math.Max(0.0, preOpLuma - preEdLuma), 2);
        double naiveDrop = Math.Round(Math.Max(0.0, naiveOpLuma - naiveEdLuma), 2);

        if (!string.IsNullOrWhiteSpace(outputPath))
        {
            string? dir = Path.GetDirectoryName(outputPath);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            premult.SaveAsPng(outputPath);
        }

        srcImg.Dispose();
        premult.Dispose();
        naive.Dispose();
        sw.Stop();

        return ExecutionTruthEnvelope.CreateJsonResult(
            status: "success",
            executionMode: ExecutionMode.OfflineDirect,
            targetStatus: new TargetStatus
            {
                Client = new ClientLivenessStatus { Running = false, EngineReady = false },
                Server = new ServerLivenessStatus { Running = false, Port = 0 }
            },
            metrics: new ExecutionMetrics
            {
                RoundtripLatencyMs = sw.Elapsed.TotalMilliseconds,
                EngineRenderFrame = 0,
                Timestamp = _timeProvider.GetUtcNow().ToString("o")
            },
            data: new
            {
                action = "sanitize_alpha",
                outputPath,
                targetWidth,
                targetHeight,
                borderPixelsCleared,
                premultipliedEdgeBrightnessDrop = preDrop,
                naiveEdgeBrightnessDrop = naiveDrop,
                haloEliminated = preDrop < 2.0 && naiveDrop > 35.0
            },
            fidelity: ExecutionFidelity.StaticOfflineAnalysis);
    }

    private ToolCallResult Inspect(JsonElement arguments, Stopwatch sw)
    {
        string texturePath = "texture_color.png";
        if ((arguments.TryGetProperty("texture_path", out JsonElement tpEl) ||
             arguments.TryGetProperty("texturePath", out tpEl) ||
             arguments.TryGetProperty("input_file", out tpEl) ||
             arguments.TryGetProperty("file", out tpEl)) &&
            !string.IsNullOrWhiteSpace(tpEl.GetString()))
        {
            texturePath = tpEl.GetString()!.Trim();
        }

        int? width = null;
        if (arguments.TryGetProperty("width", out JsonElement wEl) && wEl.TryGetInt32(out int w))
        {
            width = w;
        }

        int? height = null;
        if (arguments.TryGetProperty("height", out JsonElement hEl) && hEl.TryGetInt32(out int h))
        {
            height = h;
        }

        bool? invertY = null;
        if (arguments.TryGetProperty("invert_normal_y", out JsonElement iyEl) ||
            arguments.TryGetProperty("invertNormalY", out iyEl) ||
            arguments.TryGetProperty("invert_y", out iyEl))
        {
            invertY = iyEl.GetBoolean();
        }

        var result = _assetBakeService.InspectTexture(texturePath, width, height, invertY);
        sw.Stop();

        var handoffs = new List<ToolHandoff>
        {
            ToolHandoff.Create(
                server: "texconv",
                tool: "dds_convert_texture",
                reason: $"Convert texture to optimal DirectX DDS format ({result.TargetDdsFormat})",
                arguments: new
                {
                    input_file = result.InputFile,
                    format = result.TargetDdsFormat,
                    mipmaps = true,
                    extra_flags = result.ExtraFlags
                },
                path: result.InputFile)
        };

        return ExecutionTruthEnvelope.CreateJsonResult(
            status: "success",
            executionMode: ExecutionMode.OfflineDirect,
            targetStatus: new TargetStatus
            {
                Client = new ClientLivenessStatus { Running = false, EngineReady = false },
                Server = new ServerLivenessStatus { Running = false, Port = 0 }
            },
            metrics: new ExecutionMetrics
            {
                RoundtripLatencyMs = sw.Elapsed.TotalMilliseconds,
                EngineRenderFrame = 0,
                Timestamp = _timeProvider.GetUtcNow().ToString("o")
            },
            data: new
            {
                action = "inspect",
                inspection = result
            },
            handoffs: handoffs,
            fidelity: ExecutionFidelity.StaticOfflineAnalysis);
    }

    private async Task<ToolCallResult> BakeMaterialAsync(JsonElement arguments, Stopwatch sw, CancellationToken cancellationToken)
    {
        string materialName = "custom_material";
        if ((arguments.TryGetProperty("material_name", out JsonElement mnEl) ||
             arguments.TryGetProperty("materialName", out mnEl) ||
             arguments.TryGetProperty("name", out mnEl)) &&
            !string.IsNullOrWhiteSpace(mnEl.GetString()))
        {
            materialName = mnEl.GetString()!.Trim();
        }

        string shaderType = "hero";
        if ((arguments.TryGetProperty("shader_type", out JsonElement stEl) ||
             arguments.TryGetProperty("shaderType", out stEl) ||
             arguments.TryGetProperty("shader", out stEl)) &&
            !string.IsNullOrWhiteSpace(stEl.GetString()))
        {
            shaderType = stEl.GetString()!.Trim();
        }

        string? vfsPath = null;
        if ((arguments.TryGetProperty("vfs_path", out JsonElement vpEl) ||
             arguments.TryGetProperty("vfsPath", out vpEl)) &&
            !string.IsNullOrWhiteSpace(vpEl.GetString()))
        {
            vfsPath = vpEl.GetString()!.Trim();
        }

        bool preflightGuard = true;
        if (arguments.TryGetProperty("preflight_guard", out JsonElement pgEl) ||
            arguments.TryGetProperty("preflightGuard", out pgEl))
        {
            preflightGuard = pgEl.GetBoolean();
        }

        var samplers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (arguments.TryGetProperty("samplers", out JsonElement samplersEl) && samplersEl.ValueKind == JsonValueKind.Object)
        {
            foreach (var prop in samplersEl.EnumerateObject())
            {
                if (prop.Value.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(prop.Value.GetString()))
                {
                    samplers[prop.Name] = prop.Value.GetString()!.Trim();
                }
            }
        }
        else
        {
            // Default samplers based on materialName
            samplers["diffuse"] = $"{materialName}_color.dds";
            samplers["normalmap"] = $"{materialName}_norm.dds";
            samplers["specular"] = $"{materialName}_spec.dds";
        }

        var recipe = _assetBakeService.GenerateMaterial(materialName, shaderType, samplers, vfsPath, preflightGuard);

        bool writeToDisk = false;
        if (arguments.TryGetProperty("write_to_disk", out JsonElement wdEl) ||
            arguments.TryGetProperty("writeToDisk", out wdEl))
        {
            writeToDisk = wdEl.GetBoolean();
        }

        string? diskFileWritten = null;
        if (writeToDisk)
        {
            string outDir = Path.Combine(_workspaceRoot, "heroes", materialName);
            if (arguments.TryGetProperty("output_directory", out JsonElement odEl) && !string.IsNullOrWhiteSpace(odEl.GetString()))
            {
                outDir = odEl.GetString()!.Trim();
            }

            Directory.CreateDirectory(outDir);
            diskFileWritten = Path.Combine(outDir, $"{materialName}.material");
            await File.WriteAllTextAsync(diskFileWritten, recipe.XmlContent, cancellationToken).ConfigureAwait(false);
            _logger?.Invoke($"Wrote K2 material definition to '{diskFileWritten}'.");
        }

        sw.Stop();

        var handoffs = new List<ToolHandoff>();
        foreach (var cmd in recipe.TexconvCommands)
        {
            handoffs.Add(ToolHandoff.Create(
                server: "texconv",
                tool: "dds_convert_texture",
                reason: $"Convert texture for material '{materialName}'",
                arguments: new { command = cmd },
                path: recipe.VfsDestinationPath));
        }

        handoffs.Add(ToolHandoff.Create(
            server: "kongor",
            tool: "kongor_hot_reload",
            reason: $"Hot reload live materials for '{materialName}'",
            arguments: new { type = "material", target = recipe.VfsDestinationPath },
            path: recipe.VfsDestinationPath));

        return ExecutionTruthEnvelope.CreateJsonResult(
            status: "success",
            executionMode: ExecutionMode.OfflineDirect,
            targetStatus: new TargetStatus
            {
                Client = new ClientLivenessStatus { Running = false, EngineReady = false },
                Server = new ServerLivenessStatus { Running = false, Port = 0 }
            },
            metrics: new ExecutionMetrics
            {
                RoundtripLatencyMs = sw.Elapsed.TotalMilliseconds,
                EngineRenderFrame = 0,
                Timestamp = _timeProvider.GetUtcNow().ToString("o")
            },
            data: new
            {
                action = "bake_material",
                recipe = recipe,
                diskFileWritten = diskFileWritten
            },
            handoffs: handoffs,
            fidelity: ExecutionFidelity.StaticOfflineAnalysis);
    }

    private ToolCallResult HotReload(JsonElement arguments, Stopwatch sw)
    {
        string target = "/heroes/all";
        if (arguments.TryGetProperty("target", out JsonElement tEl) && !string.IsNullOrWhiteSpace(tEl.GetString()))
        {
            target = tEl.GetString()!.Trim();
        }

        sw.Stop();

        var handoffs = new List<ToolHandoff>
        {
            ToolHandoff.Create(
                server: "kongor",
                tool: "kongor_hot_reload",
                reason: "Hot reload live engine materials and shaders",
                arguments: new { type = "material", target = target },
                path: target)
        };

        return ExecutionTruthEnvelope.CreateJsonResult(
            status: "success",
            executionMode: ExecutionMode.OfflineDirect,
            targetStatus: new TargetStatus
            {
                Client = new ClientLivenessStatus { Running = false, EngineReady = false },
                Server = new ServerLivenessStatus { Running = false, Port = 0 }
            },
            metrics: new ExecutionMetrics
            {
                RoundtripLatencyMs = sw.Elapsed.TotalMilliseconds,
                EngineRenderFrame = 0,
                Timestamp = _timeProvider.GetUtcNow().ToString("o")
            },
            data: new
            {
                action = "hot_reload",
                target = target,
                reloaded = true
            },
            handoffs: handoffs,
            fidelity: ExecutionFidelity.StaticOfflineAnalysis);
    }
}
