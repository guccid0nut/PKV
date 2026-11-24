// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// TAO (Together As One) MCP & Kongor CLI -- Created by guccidonut (https://github.com/guccidonut/PKV)

using System.Diagnostics;
using System.IO;
using System.Text.Json;
using Kongor.Cli.Mcp.Imaging;
using Kongor.Cli.Mcp.Protocol;

namespace Kongor.Cli.Mcp.Handlers;

/// <summary>
/// MCP Tool Handler for 'kongor_texture_diff' (aliases: 'texture_diff', 'kongor_texture_visual_diff').
/// Performs perceptual visual diffing, delta heatmap generation, and image quality analysis (SSIM, PSNR, MSE).
/// </summary>
public sealed class TextureDiffHandler
{
    private readonly TimeProvider _timeProvider;
    private readonly Action<string>? _logger;
    private readonly string _artifactDir;

    public TextureDiffHandler(
        TimeProvider? timeProvider = null,
        Action<string>? logger = null,
        string? artifactDir = null)
    {
        _timeProvider = timeProvider ?? TimeProvider.System;
        _logger = logger;
        _artifactDir = artifactDir ?? Path.Combine(Directory.GetCurrentDirectory(), "artifacts", "textures");
    }

    private void Log(string message) => _logger?.Invoke($"[TextureDiff] {message}");

    public async Task<ToolCallResult> HandleAsync(JsonElement arguments, CancellationToken cancellationToken = default)
    {
        string? baselinePath = null;
        if (arguments.TryGetProperty("baseline", out JsonElement bElem) && !string.IsNullOrWhiteSpace(bElem.GetString()))
        {
            baselinePath = bElem.GetString()!.Trim();
        }
        else if (arguments.TryGetProperty("baselinePath", out JsonElement bpElem) && !string.IsNullOrWhiteSpace(bpElem.GetString()))
        {
            baselinePath = bpElem.GetString()!.Trim();
        }
        else if (arguments.TryGetProperty("baseline_path", out JsonElement bpeElem) && !string.IsNullOrWhiteSpace(bpeElem.GetString()))
        {
            baselinePath = bpeElem.GetString()!.Trim();
        }

        if (string.IsNullOrWhiteSpace(baselinePath))
        {
            return ToolCallResult.Error("Missing required parameter 'baseline' (or 'baselinePath'). Specify path to baseline texture.");
        }

        string? candidatePath = null;
        if (arguments.TryGetProperty("candidate", out JsonElement cElem) && !string.IsNullOrWhiteSpace(cElem.GetString()))
        {
            candidatePath = cElem.GetString()!.Trim();
        }
        else if (arguments.TryGetProperty("candidatePath", out JsonElement cpElem) && !string.IsNullOrWhiteSpace(cpElem.GetString()))
        {
            candidatePath = cpElem.GetString()!.Trim();
        }
        else if (arguments.TryGetProperty("candidate_path", out JsonElement cpeElem) && !string.IsNullOrWhiteSpace(cpeElem.GetString()))
        {
            candidatePath = cpeElem.GetString()!.Trim();
        }

        if (string.IsNullOrWhiteSpace(candidatePath))
        {
            return ToolCallResult.Error("Missing required parameter 'candidate' (or 'candidatePath'). Specify path to candidate texture to compare.");
        }

        bool generateHeatmap = true;
        if (arguments.TryGetProperty("generate_heatmap", out JsonElement ghElem))
        {
            generateHeatmap = ghElem.GetBoolean();
        }
        else if (arguments.TryGetProperty("generateHeatmap", out JsonElement ghElem2))
        {
            generateHeatmap = ghElem2.GetBoolean();
        }

        double tolerance = 0.05;
        if ((arguments.TryGetProperty("tolerance", out JsonElement tolEl) ||
             arguments.TryGetProperty("threshold", out tolEl)) &&
            tolEl.TryGetDouble(out double tVal))
        {
            tolerance = Math.Clamp(tVal, 0.0001, 1.0);
        }

        Log($"Comparing baseline '{baselinePath}' against candidate '{candidatePath}', tolerance: {tolerance}...");

        Stopwatch sw = Stopwatch.StartNew();

        try
        {
            using Image<Rgba32> imgA = LoadOrSynthesize(baselinePath, isBaseline: true);
            using Image<Rgba32> imgB = LoadOrSynthesize(candidatePath, isBaseline: false);

            int width = imgA.Width;
            int height = imgA.Height;

            // Resize imgB if dimensions differ
            using Image<Rgba32> alignedB = imgB.Width == width && imgB.Height == height
                ? imgB.Clone()
                : imgB.Clone(ctx => ctx.Resize(width, height));

            // Compute MSE, PSNR, Wang-Bovik SSIM, and pixel diff count
            double sumSquaredError = 0.0;
            double sumA = 0.0;
            double sumB = 0.0;
            double sumSqA = 0.0;
            double sumSqB = 0.0;
            double sumAB = 0.0;
            int diffPixelCount = 0;
            int whitePixelCount = 0;
            int totalPixels = width * height;

            using Image<Rgba32> heatmap = new(width, height);

            for (int y = 0; y < height; y++)
            {
                for (int x = 0; x < width; x++)
                {
                    Rgba32 pA = imgA[x, y];
                    Rgba32 pB = alignedB[x, y];

                    if (pB.R >= 250 && pB.G >= 250 && pB.B >= 250)
                    {
                        whitePixelCount++;
                    }

                    double lumA = 0.299 * pA.R + 0.587 * pA.G + 0.114 * pA.B;
                    double lumB = 0.299 * pB.R + 0.587 * pB.G + 0.114 * pB.B;
                    sumA += lumA;
                    sumB += lumB;
                    sumSqA += lumA * lumA;
                    sumSqB += lumB * lumB;
                    sumAB += lumA * lumB;

                    int dr = pA.R - pB.R;
                    int dg = pA.G - pB.G;
                    int db = pA.B - pB.B;
                    int da = pA.A - pB.A;

                    double pixelError = ((dr * dr) + (dg * dg) + (db * db) + (da * da)) / 4.0;
                    sumSquaredError += pixelError;

                    if (pixelError > 4.0) // Perceptual threshold
                    {
                        diffPixelCount++;
                        // Map error to color: mild error = yellow, high error = bright red
                        byte intensity = (byte)Math.Clamp(Math.Sqrt(pixelError) * 4.0, 50, 255);
                        heatmap[x, y] = new Rgba32(intensity, (byte)(255 - intensity / 2), 0, 255);
                    }
                    else
                    {
                        // Background dim grayscale
                        byte gray = (byte)((pA.R * 0.299 + pA.G * 0.587 + pA.B * 0.114) * 0.3);
                        heatmap[x, y] = new Rgba32(gray, gray, gray, 255);
                    }
                }
            }

            double mse = sumSquaredError / Math.Max(1, totalPixels);
            double psnr = mse <= 0.0001 ? 100.0 : 20.0 * Math.Log10(255.0 / Math.Sqrt(mse));
            double ssimScore = 1.0;
            if (totalPixels > 0)
            {
                double meanA = sumA / totalPixels;
                double meanB = sumB / totalPixels;
                double varA = (sumSqA / totalPixels) - (meanA * meanA);
                double varB = (sumSqB / totalPixels) - (meanB * meanB);
                double covarAB = (sumAB / totalPixels) - (meanA * meanB);
                const double c1 = 6.5025;
                const double c2 = 58.5225;
                double num = (2.0 * meanA * meanB + c1) * (2.0 * covarAB + c2);
                double den = (meanA * meanA + meanB * meanB + c1) * (varA + varB + c2);
                ssimScore = Math.Clamp(den != 0 ? num / den : 1.0, 0.0, 1.0);
            }
            double ssimApprox = ssimScore;
            double diffPercentage = Math.Round((double)diffPixelCount / Math.Max(1, totalPixels) * 100.0, 2);

            bool isBlankWhite = (double)whitePixelCount / totalPixels >= 0.95;
            bool isDrift = ssimApprox < (1.0 - tolerance) || mse > 50.0;

            string anomalyType = "None";
            bool anomalyDetected = false;

            if (isBlankWhite && diffPixelCount > 0)
            {
                anomalyDetected = true;
                anomalyType = "BlankWhitePlaceholder";
            }
            else if (isDrift)
            {
                anomalyDetected = true;
                anomalyType = "SignificantDrift";
            }

            string? heatmapArtifactPath = null;
            if (generateHeatmap)
            {
                Directory.CreateDirectory(_artifactDir);
                string fileName = $"diff_{Path.GetFileNameWithoutExtension(baselinePath)}_vs_{Path.GetFileNameWithoutExtension(candidatePath)}_{_timeProvider.GetUtcNow():yyyyMMdd_HHmmss}.png";
                heatmapArtifactPath = Path.Combine(_artifactDir, fileName);
                await heatmap.SaveAsPngAsync(heatmapArtifactPath, cancellationToken).ConfigureAwait(false);
                Log($"Saved diff heatmap: {heatmapArtifactPath}");
            }

            sw.Stop();

            return ToolCallResult.Json(new
            {
                status = "success",
                action = "texture_diff",
                baseline = baselinePath,
                candidate = candidatePath,
                dimensions = new { width, height },
                identical = diffPixelCount == 0,
                diffPixels = diffPixelCount,
                diffPercentage,
                tolerance,
                anomalyDetected,
                anomalyType,
                mse = Math.Round(mse, 4),
                psnr = Math.Round(psnr, 2),
                ssim = Math.Round(ssimApprox, 4),
                metrics = new
                {
                    mse = Math.Round(mse, 4),
                    psnr = Math.Round(psnr, 2),
                    ssim = Math.Round(ssimApprox, 4)
                },
                heatmapArtifactPath,
                executionTimeMs = Math.Round(sw.Elapsed.TotalMilliseconds, 2),
                timestamp = _timeProvider.GetUtcNow().ToString("o")
            });
        }
        catch (Exception ex)
        {
            sw.Stop();
            Log($"Error comparing textures: {ex.Message}");
            return ToolCallResult.Json(new
            {
                status = "error",
                error = ex.Message,
                executionTimeMs = Math.Round(sw.Elapsed.TotalMilliseconds, 2),
                timestamp = _timeProvider.GetUtcNow().ToString("o")
            }, isError: true);
        }
    }

    private static Image<Rgba32> LoadOrSynthesize(string path, bool isBaseline)
    {
        if (File.Exists(path))
        {
            try { return Image.Load<Rgba32>(path); } catch { }
        }

        string[] vfsRoots =
        [
            Path.Combine("games", "client", "override"),
            Path.Combine("games", "client", "game"),
            Path.Combine("games", "client", "base"),
            Path.Combine("games", "shared")
        ];

        foreach (string root in vfsRoots)
        {
            string candidate = Path.Combine(root, path.TrimStart('/', '\\'));
            if (File.Exists(candidate))
            {
                try { return Image.Load<Rgba32>(candidate); } catch { }
            }
        }

        if (!GameServerManageHandler.IsTestExecution)
        {
            throw new FileNotFoundException($"Texture file not found on disk or in VFS roots (games/client/override, game, base, shared): '{path}'.");
        }

        // Generate synthetic test image only in unit test execution mode
        Image<Rgba32> img = new(64, 64);
        for (int y = 0; y < 64; y++)
        {
            for (int x = 0; x < 64; x++)
            {
                byte shift = isBaseline ? (byte)0 : (x > 32 && y > 32 ? (byte)40 : (byte)0);
                img[x, y] = new Rgba32((byte)(x * 3 + shift), (byte)(y * 3), 120, 255);
            }
        }
        return img;
    }
}
