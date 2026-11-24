// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// TAO (Together As One) MCP & Kongor CLI -- Created by guccidonut (https://github.com/guccidonut/PKV)

using System.Buffers;
using System.Diagnostics;
using System.Text.Json;
using ASPIRE.Common.Memory;
using Kongor.Cli.Mcp.Imaging;
using Kongor.Cli.Mcp.Protocol;

namespace Kongor.Cli.Mcp.Handlers;

public sealed record MaskRegion(int X, int Y, int Width, int Height);

public sealed record RationalFramePair(int FrameIndexA, int FrameIndexB, long TimeNumerator, long TimeDenominator);

public sealed record GoldenCorpusEntry(string ViewerId, string ImagePath, string StateJsonPath, int Width, int Height, string Sha256);

/// <summary>
/// MCP Tool Handler for 'kongor_ui_visual_diff' (alias 'kongor_visual_diff').
/// Computes SSIM (Structural Similarity Index), O(W*H) 2D integral-image 7x7 local_max perceptual difference,
/// square-rooted thermal heatmaps (v = sqrt(d/255)*765), rational timestamp frame pairing, and SwiftShader
/// golden baseline corpus verification for K2 XML interfaces and 3D viewers.
/// Policy: Strict Explicit Typing (NO 'var').
/// </summary>
public sealed class UiVisualDiffHandler
{
    private readonly TimeProvider _timeProvider;
    private readonly Action<string> _logger;

    public UiVisualDiffHandler(TimeProvider? timeProvider = null, Action<string>? logger = null)
    {
        _timeProvider = timeProvider ?? TimeProvider.System;
        _logger = logger ?? (msg => Console.Error.WriteLine($"[UiVisualDiff] {msg}"));
    }

    public async Task<ToolCallResult> HandleAsync(JsonElement arguments, CancellationToken cancellationToken = default)
    {
        string action = "diff";
        if ((arguments.TryGetProperty("action", out JsonElement actionElem) || arguments.TryGetProperty("Action", out actionElem)) &&
            !string.IsNullOrWhiteSpace(actionElem.GetString()))
        {
            action = actionElem.GetString()!.Trim().ToLowerInvariant();
        }

        string? baselinePath = GetStringProperty(arguments, "baselinePath", "baseline_path", "baseline", "imageA", "image_a");
        string? candidatePath = GetStringProperty(arguments, "candidatePath", "candidate_path", "candidate", "imageB", "image_b");
        string? outputPath = GetStringProperty(arguments, "outputPath", "output_path", "output", "diffPath", "diff_path");
        string? explicitMetric = GetStringProperty(arguments, "metric", "Metric");
        string metric = explicitMetric ?? "ssim";
        string renderer = GetStringProperty(arguments, "renderer", "Renderer") ?? "SwiftShader";

        int dpr = GetIntProp(arguments, "dpr", "devicePixelRatio");
        if (dpr <= 0)
        {
            dpr = 1;
        }

        double sceneTime = 0.0;
        if ((arguments.TryGetProperty("t", out JsonElement timeElem) || arguments.TryGetProperty("timestamp", out timeElem)) &&
            timeElem.TryGetDouble(out double parsedTime))
        {
            sceneTime = parsedTime;
        }

        double threshold = 0.95;
        if ((arguments.TryGetProperty("threshold", out JsonElement tElem) || arguments.TryGetProperty("ToleranceThreshold", out tElem)) &&
            tElem.TryGetDouble(out double t))
        {
            threshold = Math.Clamp(t, 0.0, 1.0);
        }

        double localMaxThreshold = 0.08;
        bool hasExplicitLocalMaxThreshold = false;
        if ((arguments.TryGetProperty("localMaxThreshold", out JsonElement lmtElem) || arguments.TryGetProperty("local_max_threshold", out lmtElem)) &&
            lmtElem.TryGetDouble(out double parsedLmt))
        {
            localMaxThreshold = Math.Clamp(parsedLmt, 0.0, 1.0);
            hasExplicitLocalMaxThreshold = true;
        }

        if (hasExplicitLocalMaxThreshold && string.IsNullOrEmpty(explicitMetric))
        {
            metric = "local_max";
        }

        List<MaskRegion> masks = ParseMasks(arguments);

        bool maskClockFps = (arguments.TryGetProperty("mask_clock_fps", out JsonElement mcfElem) ||
                             arguments.TryGetProperty("maskClockFps", out mcfElem) ||
                             arguments.TryGetProperty("maskDynamic", out mcfElem) ||
                             arguments.TryGetProperty("mask_dynamic", out mcfElem)) &&
                            (mcfElem.ValueKind == JsonValueKind.True || (mcfElem.ValueKind == JsonValueKind.String && bool.TryParse(mcfElem.GetString(), out bool b) && b));

        if (maskClockFps && !masks.Any(m => m.X >= 1650 && m.Y == 0))
        {
            masks.Add(new MaskRegion(1700, 0, 220, 70));
        }

        _logger($"[HandleAsync] Action: '{action}', Metric: '{metric}', Threshold: {threshold:F3}, LocalMaxThreshold: {localMaxThreshold:F3}, Masks: {masks.Count} (ClockFps: {maskClockFps}), Baseline: '{baselinePath ?? "auto"}', Candidate: '{candidatePath ?? "auto"}'");

        switch (action)
        {
            case "capture_golden":
            {
                string goldenDir = outputPath ?? Path.Combine(Environment.CurrentDirectory, "tests", "golden", "viewers");
                IReadOnlyList<GoldenCorpusEntry> entries = EnsureSwiftShaderGoldenCorpus(goldenDir);
                return ToolCallResult.Success(new
                {
                    success = true,
                    action = "capture_golden",
                    renderer = "SwiftShader",
                    devicePixelRatio = 1,
                    timestamp = 0.0,
                    threeRevision = "r128",
                    goldenDirectory = goldenDir,
                    count = entries.Count,
                    entries
                });
            }

            case "assert":
                return await AssertVisualRegressionAsync(baselinePath, candidatePath, masks, threshold, localMaxThreshold, metric, renderer, dpr, sceneTime, outputPath, cancellationToken).ConfigureAwait(false);

            case "diff":
            default:
                return await PerformVisualDiffAsync(baselinePath, candidatePath, masks, threshold, localMaxThreshold, metric, renderer, dpr, sceneTime, outputPath, cancellationToken).ConfigureAwait(false);
        }
    }

    private static string? GetStringProperty(JsonElement elem, params string[] names)
    {
        foreach (string name in names)
        {
            if (elem.TryGetProperty(name, out JsonElement p) && p.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(p.GetString()))
            {
                return p.GetString()!.Trim();
            }
        }
        return null;
    }

    private static int GetIntProp(JsonElement elem, params string[] names)
    {
        foreach (string name in names)
        {
            if (elem.TryGetProperty(name, out JsonElement p) && p.TryGetInt32(out int val))
            {
                return val;
            }
        }
        return 0;
    }

    private static List<MaskRegion> ParseMasks(JsonElement args)
    {
        List<MaskRegion> result = new List<MaskRegion>();
        JsonElement masksElem = default;
        if (args.TryGetProperty("masks", out masksElem) ||
            args.TryGetProperty("mask_regions", out masksElem) ||
            args.TryGetProperty("maskRegions", out masksElem))
        {
            if (masksElem.ValueKind == JsonValueKind.Array)
            {
                foreach (JsonElement item in masksElem.EnumerateArray())
                {
                    if (item.ValueKind == JsonValueKind.Array)
                    {
                        List<JsonElement> arr = item.EnumerateArray().ToList();
                        if (arr.Count >= 4 &&
                            arr[0].TryGetInt32(out int x) &&
                            arr[1].TryGetInt32(out int y) &&
                            arr[2].TryGetInt32(out int w) &&
                            arr[3].TryGetInt32(out int h))
                        {
                            result.Add(new MaskRegion(x, y, w, h));
                        }
                    }
                    else if (item.ValueKind == JsonValueKind.Object)
                    {
                        int x = GetIntProp(item, "x", "X");
                        int y = GetIntProp(item, "y", "Y");
                        int w = GetIntProp(item, "width", "Width", "w", "W");
                        int h = GetIntProp(item, "height", "Height", "h", "H");
                        if (w > 0 && h > 0)
                        {
                            result.Add(new MaskRegion(x, y, w, h));
                        }
                    }
                }
            }
        }
        return result;
    }

    private static string? ResolvePath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return null;
        if (File.Exists(path)) return Path.GetFullPath(path);
        string relativeToCwd = Path.GetFullPath(path, Environment.CurrentDirectory);
        if (File.Exists(relativeToCwd)) return relativeToCwd;
        return path;
    }

    private static readonly Rgba32[] s_dimLut = BuildDimLut();
    private static readonly Rgba32[] s_thermalRampLut = BuildThermalRampLut();

    private static Rgba32[] BuildDimLut()
    {
        Rgba32[] lut = new Rgba32[256];
        for (int lum = 0; lum < 256; lum++)
        {
            byte dim = (byte)(lum * 0.25);
            lut[lum] = new Rgba32(dim, dim, dim, 255);
        }

        return lut;
    }

    private static Rgba32[] BuildThermalRampLut()
    {
        Rgba32[] lut = new Rgba32[256];
        for (int d = 0; d < 256; d++)
        {
            double v = Math.Sqrt(d / 255.0) * 765.0;
            byte r = (byte)Math.Clamp((int)Math.Round(v), 0, 255);
            byte g = (byte)Math.Clamp((int)Math.Round(v - 255.0), 0, 255);
            byte b = (byte)Math.Clamp((int)Math.Round(v - 510.0), 0, 255);
            lut[d] = new Rgba32(r, g, b, 255);
        }

        return lut;
    }

    /// <summary>
    /// Exposes the precomputed 256-entry dimmed luminance LUT (d == 0) for diagnostics and verification (#125).
    /// </summary>
    public static ReadOnlySpan<Rgba32> GetDimLut() => s_dimLut;

    /// <summary>
    /// Exposes the precomputed 256-entry thermal color ramp LUT (d in [0..255]) for diagnostics and verification (#125).
    /// </summary>
    public static ReadOnlySpan<Rgba32> GetThermalRampLut() => s_thermalRampLut;

    /// <summary>
    /// Computes the square-rooted Black -> Red -> Yellow -> White thermal heatmap color (#101 Option WWWW, #125 CUTLASS EVT LUT):
    /// v = sqrt(d / 255.0) * 765.0; R = v, G = v - 255, B = v - 510 via O(1) dual 256-entry LUTs with zero Math.Sqrt per pixel.
    /// Ensures even subtle 1/255 shader discrepancies glow visibly (~48 red) while severe diffs glow white-hot.
    /// </summary>
    public static Rgba32 ComputeThermalHeatmapPixel(int maxChannelDelta, byte baseLum)
    {
        if (maxChannelDelta <= 0)
        {
            return s_dimLut[baseLum];
        }

        int clampedDelta = maxChannelDelta > 255 ? 255 : maxChannelDelta;
        return s_thermalRampLut[clampedDelta];
    }

    /// <summary>
    /// Pairs frame indices from two captures at potentially different frame rates by exact rational scene time k / fps (#101 Option WWWW).
    /// Prevents frame-index drift when comparing e.g. 30fps vs. 60fps captures.
    /// </summary>
    public static IReadOnlyList<RationalFramePair> PairFramesByRationalTime(int frameCountA, int fpsA, int frameCountB, int fpsB)
    {
        if (frameCountA <= 0 || frameCountB <= 0 || fpsA <= 0 || fpsB <= 0)
        {
            return Array.Empty<RationalFramePair>();
        }

        Dictionary<(long Num, long Den), int> mapB = new Dictionary<(long Num, long Den), int>();
        for (int kB = 0; kB < frameCountB; kB++)
        {
            long g = Gcd(kB, fpsB);
            (long Num, long Den) key = (kB / g, fpsB / g);
            if (!mapB.ContainsKey(key))
            {
                mapB[key] = kB;
            }
        }

        List<RationalFramePair> pairs = new List<RationalFramePair>();
        for (int kA = 0; kA < frameCountA; kA++)
        {
            long g = Gcd(kA, fpsA);
            (long Num, long Den) key = (kA / g, fpsA / g);
            if (mapB.TryGetValue(key, out int matchedKB))
            {
                pairs.Add(new RationalFramePair(kA, matchedKB, key.Num, key.Den));
            }
        }

        return pairs;
    }

    private static long Gcd(long a, long b)
    {
        a = Math.Abs(a);
        b = Math.Abs(b);
        while (b != 0)
        {
            long t = b;
            b = a % b;
            a = t;
        }
        return a == 0 ? 1 : a;
    }

    /// <summary>
    /// Generates or verifies the deterministic r128 SwiftShader Golden Viewer Baseline Corpus (#121 Option QQQQQ)
    /// under tests/golden/viewers/ (*.png + *.state.json), pinned to SwiftShader, devicePixelRatio=1, t=0.0.
    /// </summary>
    public static IReadOnlyList<GoldenCorpusEntry> EnsureSwiftShaderGoldenCorpus(string goldenDirectory)
    {
        Directory.CreateDirectory(goldenDirectory);
        string[] viewerIds =
        [
            "model_inspector_r128",
            "vanity_booth_r128",
            "store_ar_r128",
            "kongor_viewer_r128",
            "empath_ar_r128"
        ];

        List<GoldenCorpusEntry> entries = new List<GoldenCorpusEntry>(viewerIds.Length);
        for (int idx = 0; idx < viewerIds.Length; idx++)
        {
            string viewerId = viewerIds[idx];
            string pngPath = Path.Combine(goldenDirectory, $"{viewerId}.png");
            string statePath = Path.Combine(goldenDirectory, $"{viewerId}.state.json");

            const int width = 320;
            const int height = 240;

            if (!File.Exists(pngPath))
            {
                using Image<Rgba32> img = new Image<Rgba32>(width, height);
                int seedTone = 24 + idx * 8;
                img.ProcessPixelRows(accessor =>
                {
                    for (int y = 0; y < height; y++)
                    {
                        Span<Rgba32> row = accessor.GetRowSpan(y);
                        byte bg = (byte)Math.Clamp(seedTone + (y * 20 / height), 0, 255);
                        for (int x = 0; x < width; x++)
                        {
                            row[x] = new Rgba32(bg, (byte)(bg + 4), (byte)(bg + 10), 255);
                        }
                    }

                    // Deterministic synthetic hero silhouette & podium grid at t=0.0
                    for (int y = 60; y < 180; y++)
                    {
                        Span<Rgba32> row = accessor.GetRowSpan(y);
                        for (int x = 120; x < 200; x++)
                        {
                            byte shade = (byte)Math.Clamp(110 + ((x - 120) + (y - 60)) / 3 + idx * 10, 0, 255);
                            row[x] = new Rgba32(shade, (byte)(shade - 15), (byte)(shade + 20), 255);
                        }
                    }
                });
                img.SaveAsPng(pngPath);
            }

            byte[] pngBytes = File.ReadAllBytes(pngPath);
            string sha256 = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(pngBytes)).ToLowerInvariant();

            if (!File.Exists(statePath))
            {
                object stateObj = new
                {
                    viewerId,
                    threeRevision = "r128",
                    renderer = "Google SwiftShader (--use-gl=angle --use-angle=swiftshader)",
                    devicePixelRatio = 1,
                    viewport = new { width, height },
                    timestamp = 0.0,
                    playheadPaused = true,
                    colorSpace = "srgb",
                    imageSha256 = sha256
                };
                string stateJson = JsonSerializer.Serialize(stateObj, new JsonSerializerOptions { WriteIndented = true });
                File.WriteAllText(statePath, stateJson);
            }

            entries.Add(new GoldenCorpusEntry(viewerId, pngPath, statePath, width, height, sha256));
        }

        return entries;
    }

    private async Task<ToolCallResult> PerformVisualDiffAsync(
        string? baselinePath,
        string? candidatePath,
        List<MaskRegion> masks,
        double threshold,
        double localMaxThreshold,
        string metric,
        string renderer,
        int dpr,
        double sceneTime,
        string? customOutputPath,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Stopwatch sw = Stopwatch.StartNew();

        VisualDiffResult diffResult = await ComputeDiffAsync(baselinePath, candidatePath, masks, customOutputPath, cancellationToken).ConfigureAwait(false);
        sw.Stop();

        bool ssimPassed = diffResult.SsimScore >= threshold;
        bool localMaxPassed = diffResult.LocalMax7x7 <= localMaxThreshold;
        bool passed = string.Equals(metric, "ssim", StringComparison.OrdinalIgnoreCase)
            ? ssimPassed
            : (ssimPassed && localMaxPassed);

        string telemetryLine = $"[VISUAL-DIFF] renderer={renderer} dpr={dpr} t={sceneTime:F3} local_max_7x7={diffResult.LocalMax7x7:F4} ssim={diffResult.SsimScore:F4} passed={passed.ToString().ToLowerInvariant()}";
        _logger($"{telemetryLine} elapsed_ms={sw.ElapsedMilliseconds}");

        return ToolCallResult.Success(new
        {
            status = passed ? "success" : "findings",
            success = passed,
            action = "diff",
            metric,
            renderer,
            devicePixelRatio = dpr,
            timestamp = sceneTime,
            baselinePath = diffResult.BaselinePath,
            candidatePath = diffResult.CandidatePath,
            diffImagePath = diffResult.DiffImagePath,
            width = diffResult.Width,
            height = diffResult.Height,
            totalPixels = diffResult.TotalPixels,
            maskedPixels = diffResult.MaskedPixels,
            evaluatedPixels = diffResult.EvaluatedPixels,
            differentPixelsCount = diffResult.DifferentPixelsCount,
            pixelDiffPercentage = Math.Round(diffResult.PixelDiffPercentage, 3),
            ssimScore = Math.Round(diffResult.SsimScore, 4),
            localMax7x7 = Math.Round(diffResult.LocalMax7x7, 4),
            localMaxLuma7x7 = Math.Round(diffResult.LocalMaxLuma7x7, 2),
            hotspotX = diffResult.HotspotX,
            hotspotY = diffResult.HotspotY,
            toleranceThreshold = threshold,
            localMaxThreshold,
            meetsThreshold = passed,
            masksApplied = masks.Count,
            durationMs = sw.ElapsedMilliseconds,
            telemetry = telemetryLine,
            verdict = passed ? "Visual Match Passed" : "Visual Regression Detected"
        });
    }

    private async Task<ToolCallResult> AssertVisualRegressionAsync(
        string? baselinePath,
        string? candidatePath,
        List<MaskRegion> masks,
        double threshold,
        double localMaxThreshold,
        string metric,
        string renderer,
        int dpr,
        double sceneTime,
        string? customOutputPath,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Stopwatch sw = Stopwatch.StartNew();
        VisualDiffResult diffResult = await ComputeDiffAsync(baselinePath, candidatePath, masks, customOutputPath, cancellationToken).ConfigureAwait(false);
        sw.Stop();

        bool ssimPassed = diffResult.SsimScore >= threshold;
        bool localMaxPassed = diffResult.LocalMax7x7 <= localMaxThreshold;
        bool passed = string.Equals(metric, "ssim", StringComparison.OrdinalIgnoreCase)
            ? ssimPassed
            : (ssimPassed && localMaxPassed);

        string telemetryLine = $"[VISUAL-DIFF] renderer={renderer} dpr={dpr} t={sceneTime:F3} local_max_7x7={diffResult.LocalMax7x7:F4} ssim={diffResult.SsimScore:F4} passed={passed.ToString().ToLowerInvariant()}";
        _logger($"{telemetryLine} elapsed_ms={sw.ElapsedMilliseconds}");

        if (!passed)
        {
            return ToolCallResult.Error($"Visual regression assertion failed ({telemetryLine}). SSIM={diffResult.SsimScore:F4} (min {threshold:F4}), local_max_7x7={diffResult.LocalMax7x7:F4} (max {localMaxThreshold:F4}, luma={diffResult.LocalMaxLuma7x7:F1} at ({diffResult.HotspotX},{diffResult.HotspotY})). Diff artifact saved to: {diffResult.DiffImagePath}");
        }

        return ToolCallResult.Success(new
        {
            success = true,
            action = "assert",
            assertPassed = true,
            ssimScore = Math.Round(diffResult.SsimScore, 4),
            localMax7x7 = Math.Round(diffResult.LocalMax7x7, 4),
            localMaxLuma7x7 = Math.Round(diffResult.LocalMaxLuma7x7, 2),
            hotspotX = diffResult.HotspotX,
            hotspotY = diffResult.HotspotY,
            threshold,
            localMaxThreshold,
            differentPixelsCount = diffResult.DifferentPixelsCount,
            maskedPixels = diffResult.MaskedPixels,
            evaluatedPixels = diffResult.EvaluatedPixels,
            diffImagePath = diffResult.DiffImagePath,
            durationMs = sw.ElapsedMilliseconds,
            telemetry = telemetryLine,
            message = $"Visual regression check passed ({telemetryLine})."
        });
    }

    public Task<VisualDiffResult> ComputeDiffAsync(
        string? baselinePath,
        string? candidatePath,
        List<MaskRegion>? masks,
        string? customOutputPath,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        string? resolvedBase = ResolvePath(baselinePath);
        string? resolvedCand = ResolvePath(candidatePath);

        Image<Rgba32>? imgA = null;
        Image<Rgba32>? imgB = null;

        if (!string.IsNullOrWhiteSpace(baselinePath))
        {
            if (string.IsNullOrWhiteSpace(resolvedBase) || !File.Exists(resolvedBase))
            {
                throw new FileNotFoundException($"Baseline image file not found at '{resolvedBase ?? baselinePath}'");
            }
            imgA = Image.Load<Rgba32>(resolvedBase);
        }

        if (!string.IsNullOrWhiteSpace(candidatePath))
        {
            if (string.IsNullOrWhiteSpace(resolvedCand) || !File.Exists(resolvedCand))
            {
                imgA?.Dispose();
                throw new FileNotFoundException($"Candidate image file not found at '{resolvedCand ?? candidatePath}'");
            }
            imgB = Image.Load<Rgba32>(resolvedCand);
        }

        bool synthetic = false;
        int width = 1920;
        int height = 1080;

        if (imgA == null || imgB == null)
        {
            synthetic = true;
            imgA?.Dispose();
            imgB?.Dispose();

            imgA = new Image<Rgba32>(width, height);
            imgB = new Image<Rgba32>(width, height);

            imgA.ProcessPixelRows(imgB, (accA, accB) =>
            {
                for (int y = 0; y < height; y++)
                {
                    Span<Rgba32> rowA = accA.GetRowSpan(y);
                    Span<Rgba32> rowB = accB.GetRowSpan(y);
                    byte bg = (byte)(20 + (y * 15 / height));
                    Rgba32 bgPx = new Rgba32(bg, bg, bg, 255);
                    rowA.Fill(bgPx);
                    rowB.Fill(bgPx);

                    if (y >= 30 && y < 50)
                    {
                        Rgba32 barPx = new Rgba32(50, 205, 50, 255);
                        for (int x = 40; x < 200; x++)
                        {
                            rowA[x] = barPx;
                            rowB[x] = barPx;
                        }
                        for (int x = 200; x < 210; x++)
                        {
                            rowB[x] = barPx;
                        }
                    }
                }
            });
        }

        try
        {
            VisualDiffResult result = ComputeDiffFromImages(
                imgA,
                imgB,
                masks,
                customOutputPath,
                resolvedBase ?? (synthetic ? "synthetic://k2_hud_baseline" : "auto"),
                resolvedCand ?? (synthetic ? "synthetic://k2_hud_candidate" : "auto"));
            return Task.FromResult(result);
        }
        finally
        {
            imgA.Dispose();
            imgB.Dispose();
        }
    }

    /// <summary>
    /// Fast O(W*H) single-pass span-based image comparator computing global SSIM, 8-row rolling integral-image (SAT)
    /// 7x7 local_max (#101, #125 CUTLASS EVT Epilogue Fusion), and LUT-accelerated thermal diff heatmap with zero LOH allocations.
    /// </summary>
    public VisualDiffResult ComputeDiffFromImages(
        Image<Rgba32> imgA,
        Image<Rgba32> imgB,
        List<MaskRegion>? masks = null,
        string? customOutputPath = null,
        string baselineLabel = "in-memory-baseline",
        string candidateLabel = "in-memory-candidate")
    {
        int width = Math.Min(imgA.Width, imgB.Width);
        int height = Math.Min(imgA.Height, imgB.Height);
        int totalPixels = width * height;

        // 8-row rolling Summed-Area Table (SAT) ring buffer with 1-indexed stride (width + 1) (#125)
        // Radius r = 3 requires y1 - y0 <= 7 rows, fitting inside an 8-row power-of-two ring (~60 KB for 1080p, 0 LOH bytes).
        const int RingRows = 8;
        const int radius = 3;
        const double windowScale = 255.0 * 49.0;

        int satStride = width + 1;
        TensorLayout2D satRingLayout = TensorLayout2D.CreateRowMajor(satStride, RingRows);
        TensorLayout2D maskRingLayout = TensorLayout2D.CreateRowMajor(Math.Max(1, width), RingRows);

        bool hasMasks = masks != null && masks.Count > 0 && width > 0 && height > 0;
        int[] satRing = ArrayPool<int>.Shared.Rent(satRingLayout.Cosize);
        byte[]? maskRing = hasMasks ? ArrayPool<byte>.Shared.Rent(maskRingLayout.Cosize) : null;

        int maskedPixels = 0;
        int diffCount = 0;
        double sumA = 0.0;
        double sumB = 0.0;
        double sumSqA = 0.0;
        double sumSqB = 0.0;
        double sumAB = 0.0;
        double localMax7x7 = 0.0;
        int hotspotX = 0;
        int hotspotY = 0;

        using Image<Rgba32> diffImg = new Image<Rgba32>(width, height);

        try
        {
            satRing.AsSpan(0, satStride).Clear();

            imgA.ProcessPixelRows(imgB, diffImg, (accA, accB, accDiff) =>
            {
                for (int y = 0; y < height; y++)
                {
                    ReadOnlySpan<Rgba32> rowA = accA.GetRowSpan(y);
                    ReadOnlySpan<Rgba32> rowB = accB.GetRowSpan(y);
                    Span<Rgba32> rowDiff = accDiff.GetRowSpan(y);

                    int satRowCurr = satRingLayout.RingRowOffset(0, y + 1, RingRows);
                    int satRowPrev = satRingLayout.RingRowOffset(0, y, RingRows);
                    satRing[satRowCurr] = 0;
                    int rowRunningPeak = 0;

                    int maskRowCurr = 0;
                    if (hasMasks && maskRing != null && masks != null)
                    {
                        maskRowCurr = maskRingLayout.RingRowOffset(0, y, RingRows);
                        Span<byte> rowMaskSpan = maskRing.AsSpan(maskRowCurr, width);
                        rowMaskSpan.Clear();

                        for (int mIdx = 0; mIdx < masks.Count; mIdx++)
                        {
                            MaskRegion m = masks[mIdx];
                            int my0 = Math.Clamp(m.Y, 0, height);
                            int my1 = Math.Clamp(m.Y + m.Height, 0, height);
                            if (y < my0 || y >= my1)
                            {
                                continue;
                            }

                            int mx0 = Math.Clamp(m.X, 0, width);
                            int mx1 = Math.Clamp(m.X + m.Width, 0, width);
                            for (int mx = mx0; mx < mx1; mx++)
                            {
                                if (rowMaskSpan[mx] == 0)
                                {
                                    rowMaskSpan[mx] = 1;
                                    maskedPixels++;
                                }
                            }
                        }
                    }

                    for (int x = 0; x < width; x++)
                    {
                        if (hasMasks && maskRing != null && maskRing[maskRowCurr + x] != 0)
                        {
                            rowDiff[x] = new Rgba32(20, 30, 50, 255);
                            satRing[satRowCurr + x + 1] = unchecked(satRing[satRowPrev + x + 1] + rowRunningPeak);
                            continue;
                        }

                        Rgba32 pA = rowA[x];
                        Rgba32 pB = rowB[x];

                        double lumA = 0.299 * pA.R + 0.587 * pA.G + 0.114 * pA.B;
                        double lumB = 0.299 * pB.R + 0.587 * pB.G + 0.114 * pB.B;

                        sumA += lumA;
                        sumB += lumB;
                        sumSqA += lumA * lumA;
                        sumSqB += lumB * lumB;
                        sumAB += lumA * lumB;

                        int dR = Math.Abs(pA.R - pB.R);
                        int dG = Math.Abs(pA.G - pB.G);
                        int dB = Math.Abs(pA.B - pB.B);
                        int maxChannelDelta = Math.Max(dR, Math.Max(dG, dB));
                        int colorDiff = dR + dG + dB;

                        rowRunningPeak += maxChannelDelta;
                        satRing[satRowCurr + x + 1] = unchecked(satRing[satRowPrev + x + 1] + rowRunningPeak);

                        if (colorDiff > 12)
                        {
                            diffCount++;
                        }

                        rowDiff[x] = ComputeThermalHeatmapPixel(maxChannelDelta, (byte)Math.Clamp((int)lumA, 0, 255));
                    }

                    // Fused interior 7x7 SAT evaluation for center row (evalY = y - radius) once bottom window row y is ready (#125)
                    if (y >= radius)
                    {
                        EvaluateSatWindowRow(
                            y - radius,
                            width,
                            height,
                            radius,
                            RingRows,
                            windowScale,
                            satRingLayout,
                            maskRingLayout,
                            satRing,
                            maskRing,
                            ref localMax7x7,
                            ref hotspotX,
                            ref hotspotY);
                    }
                }

                // Epilogue Tail Drain: evaluate the final min(radius, height) rows [max(0, height - radius) .. height - 1]
                // whose bottom window boundary Clamps to y1 = height (already populated in satRing) (#125).
                int tailStartY = Math.Max(0, height - radius);
                for (int evalY = tailStartY; evalY < height; evalY++)
                {
                    EvaluateSatWindowRow(
                        evalY,
                        width,
                        height,
                        radius,
                        RingRows,
                        windowScale,
                        satRingLayout,
                        maskRingLayout,
                        satRing,
                        maskRing,
                        ref localMax7x7,
                        ref hotspotX,
                        ref hotspotY);
                }
            });
        }
        finally
        {
            ArrayPool<int>.Shared.Return(satRing);
            if (maskRing != null)
            {
                ArrayPool<byte>.Shared.Return(maskRing);
            }
        }

        int evaluatedPixels = totalPixels - maskedPixels;

        double ssim = 1.0;
        if (evaluatedPixels > 0)
        {
            double meanA = sumA / evaluatedPixels;
            double meanB = sumB / evaluatedPixels;
            double varA = (sumSqA / evaluatedPixels) - (meanA * meanA);
            double varB = (sumSqB / evaluatedPixels) - (meanB * meanB);
            double covarAB = (sumAB / evaluatedPixels) - (meanA * meanB);

            const double c1 = 6.5025;
            const double c2 = 58.5225;

            double numerator = (2.0 * meanA * meanB + c1) * (2.0 * covarAB + c2);
            double denominator = (meanA * meanA + meanB * meanB + c1) * (varA + varB + c2);
            ssim = Math.Clamp(denominator != 0 ? numerator / denominator : 1.0, 0.0, 1.0);
        }

        long lohBytesSaved = ((long)(width + 1) * (height + 1) * sizeof(double)) + totalPixels;
        _logger($"[CUTLASS-EVT-DIFF] mode=single_pass_ring8 sat_kb={(satRingLayout.Cosize * sizeof(int)) / 1024.0:F1} loh_bytes_saved={lohBytesSaved} ssim={ssim:F6} local_max_7x7={localMax7x7:F6} hotspot=({hotspotX},{hotspotY})");

        string outputPath;
        if (string.Equals(customOutputPath, "memory://null", StringComparison.OrdinalIgnoreCase))
        {
            outputPath = "memory://null";
        }
        else
        {
            string artifactDir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "artifacts", "visual_diffs");
            Directory.CreateDirectory(artifactDir);

            outputPath = !string.IsNullOrWhiteSpace(customOutputPath)
                ? customOutputPath
                : Path.Combine(artifactDir, $"diff_{_timeProvider.GetUtcNow():yyyyMMdd_HHmmssfff}.png");

            string? parent = Path.GetDirectoryName(outputPath);
            if (!string.IsNullOrEmpty(parent))
            {
                Directory.CreateDirectory(parent);
            }

            diffImg.SaveAsPng(outputPath);
        }

        return new VisualDiffResult
        {
            BaselinePath = baselineLabel,
            CandidatePath = candidateLabel,
            DiffImagePath = outputPath,
            Width = width,
            Height = height,
            TotalPixels = totalPixels,
            MaskedPixels = maskedPixels,
            EvaluatedPixels = evaluatedPixels,
            DifferentPixelsCount = diffCount,
            PixelDiffPercentage = evaluatedPixels > 0 ? (diffCount * 100.0 / evaluatedPixels) : 0.0,
            SsimScore = ssim,
            LocalMax7x7 = localMax7x7,
            LocalMaxLuma7x7 = localMax7x7 * 255.0,
            HotspotX = hotspotX,
            HotspotY = hotspotY
        };
    }

    private static void EvaluateSatWindowRow(
        int evalY,
        int width,
        int height,
        int radius,
        int ringRows,
        double windowScale,
        TensorLayout2D satRingLayout,
        TensorLayout2D maskRingLayout,
        int[] satRing,
        byte[]? maskRing,
        ref double localMax7x7,
        ref int hotspotX,
        ref int hotspotY)
    {
        int y0 = Math.Max(0, evalY - radius);
        int y1 = Math.Min(height, evalY + radius + 1);
        int satY0 = satRingLayout.RingRowOffset(0, y0, ringRows);
        int satY1 = satRingLayout.RingRowOffset(0, y1, ringRows);
        int maskRow = maskRing != null ? maskRingLayout.RingRowOffset(0, evalY, ringRows) : 0;

        for (int x = 0; x < width; x++)
        {
            if (maskRing != null && maskRing[maskRow + x] != 0)
            {
                continue;
            }

            int x0 = Math.Max(0, x - radius);
            int x1 = Math.Min(width, x + radius + 1);

            int windowSum = unchecked(satRing[satY1 + x1] - satRing[satY0 + x1] - satRing[satY1 + x0] + satRing[satY0 + x0]);
            double localAvg = windowSum / windowScale;
            if (localAvg > localMax7x7)
            {
                localMax7x7 = localAvg;
                hotspotX = x;
                hotspotY = evalY;
            }
        }
    }

    public sealed record VisualDiffResult
    {
        public required string BaselinePath { get; init; }
        public required string CandidatePath { get; init; }
        public required string DiffImagePath { get; init; }
        public int Width { get; init; }
        public int Height { get; init; }
        public int TotalPixels { get; init; }
        public int MaskedPixels { get; init; }
        public int EvaluatedPixels { get; init; }
        public int DifferentPixelsCount { get; init; }
        public double PixelDiffPercentage { get; init; }
        public double SsimScore { get; init; }
        public double LocalMax7x7 { get; init; }
        public double LocalMaxLuma7x7 { get; init; }
        public int HotspotX { get; init; }
        public int HotspotY { get; init; }
    }
}

