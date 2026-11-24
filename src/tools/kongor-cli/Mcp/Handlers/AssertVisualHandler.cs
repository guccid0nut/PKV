// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// TAO (Together As One) MCP & Kongor CLI -- Created by guccidonut (https://github.com/guccidonut/PKV)

using System.Diagnostics;
using System.IO;
using System.Text.Json;
using Kongor.Cli.Mcp.Imaging;
using Kongor.Cli.Mcp.Protocol;
using Kongor.Cli.Mcp.Services;

namespace Kongor.Cli.Mcp.Handlers;

/// <summary>
/// MCP Tool Handler for 'kongor_assert_visual' (aliases: 'assert_visual', 'verify_visual_golden').
/// Crops target UI widgets or viewport regions from GPU VRAM / window captures and performs
/// perceptual Structural Similarity Index (SSIM) and MSE assertion against golden fixtures.
/// </summary>
public sealed class AssertVisualHandler
{
    private readonly IWindowCapturer _windowCapturer;
    private readonly GameClientManageHandler _clientManageHandler;
    private readonly GameServerManageHandler _serverManageHandler;
    private readonly TimeProvider _timeProvider;
    private readonly Action<string>? _logger;
    private readonly string _fixtureDirectory;
    private readonly string _artifactDirectory;

    public AssertVisualHandler(
        IWindowCapturer windowCapturer,
        GameClientManageHandler clientManageHandler,
        GameServerManageHandler serverManageHandler,
        TimeProvider? timeProvider = null,
        Action<string>? logger = null,
        string? fixtureDirectory = null,
        string? artifactDirectory = null)
    {
        _windowCapturer = windowCapturer;
        _clientManageHandler = clientManageHandler;
        _serverManageHandler = serverManageHandler;
        _timeProvider = timeProvider ?? TimeProvider.System;
        _logger = logger ?? (msg => Console.Error.WriteLine($"[AssertVisual] {msg}"));
        _fixtureDirectory = fixtureDirectory ?? Path.Combine(Directory.GetCurrentDirectory(), "fixtures", "visual");
        _artifactDirectory = artifactDirectory ?? Path.Combine(Directory.GetCurrentDirectory(), "artifacts", "visual_diffs");
    }

    public async Task<ToolCallResult> HandleAsync(JsonElement arguments, CancellationToken cancellationToken = default)
    {
        Stopwatch sw = Stopwatch.StartNew();

        string? fixtureParam = null;
        if (arguments.TryGetProperty("fixture", out JsonElement fElem) && !string.IsNullOrWhiteSpace(fElem.GetString()))
        {
            fixtureParam = fElem.GetString()!.Trim();
        }
        else if (arguments.TryGetProperty("golden", out JsonElement gElem) && !string.IsNullOrWhiteSpace(gElem.GetString()))
        {
            fixtureParam = gElem.GetString()!.Trim();
        }

        if (string.IsNullOrWhiteSpace(fixtureParam))
        {
            return ToolCallResult.Error("Missing required parameter 'fixture'. Specify fixture name or file path (e.g. 'hud_health_bar.png').");
        }

        string? widget = null;
        if (arguments.TryGetProperty("widget", out JsonElement wgtEl) && !string.IsNullOrWhiteSpace(wgtEl.GetString()))
        {
            widget = wgtEl.GetString()!.Trim();
        }

        string? maskPathParam = null;
        if (arguments.TryGetProperty("mask_path", out JsonElement mpEl) ||
            arguments.TryGetProperty("maskPath", out mpEl) ||
            arguments.TryGetProperty("mask", out mpEl))
        {
            maskPathParam = mpEl.GetString()?.Trim();
        }

        Rectangle? ignoreViewportRect = null;
        if (arguments.TryGetProperty("ignore_viewport_rect", out JsonElement ivEl) ||
            arguments.TryGetProperty("ignoreViewportRect", out ivEl) ||
            arguments.TryGetProperty("ignore_viewport", out ivEl))
        {
            if (ivEl.ValueKind == JsonValueKind.Object)
            {
                int ix = ivEl.TryGetProperty("x", out JsonElement ixEl) && ixEl.TryGetInt32(out int ixv) ? Math.Max(0, ixv) : 0;
                int iy = ivEl.TryGetProperty("y", out JsonElement iyEl) && iyEl.TryGetInt32(out int iyv) ? Math.Max(0, iyv) : 0;
                int iw = (ivEl.TryGetProperty("width", out JsonElement iwEl) && iwEl.TryGetInt32(out int iwv)) ? iwv :
                         (ivEl.TryGetProperty("w", out iwEl) && iwEl.TryGetInt32(out iwv)) ? iwv : 0;
                int ih = (ivEl.TryGetProperty("height", out JsonElement ihEl) && ihEl.TryGetInt32(out int ihv)) ? ihv :
                         (ivEl.TryGetProperty("h", out ihEl) && ihEl.TryGetInt32(out ihv)) ? ihv : 0;
                if (iw > 0 && ih > 0)
                {
                    ignoreViewportRect = new Rectangle(ix, iy, iw, ih);
                }
            }
            else if (ivEl.ValueKind == JsonValueKind.Array && ivEl.GetArrayLength() >= 4)
            {
                int ix = Math.Max(0, ivEl[0].GetInt32());
                int iy = Math.Max(0, ivEl[1].GetInt32());
                int iw = ivEl[2].GetInt32();
                int ih = ivEl[3].GetInt32();
                if (iw > 0 && ih > 0)
                {
                    ignoreViewportRect = new Rectangle(ix, iy, iw, ih);
                }
            }
        }

        string? mode = null;
        if (arguments.TryGetProperty("mode", out JsonElement modeEl) && !string.IsNullOrWhiteSpace(modeEl.GetString()))
        {
            mode = modeEl.GetString()!.Trim();
        }

        bool isStaticUi = !string.IsNullOrWhiteSpace(widget) ||
                          ignoreViewportRect.HasValue ||
                          !string.IsNullOrWhiteSpace(maskPathParam) ||
                          string.Equals(mode, "ui", StringComparison.OrdinalIgnoreCase) ||
                          string.Equals(mode, "2d", StringComparison.OrdinalIgnoreCase) ||
                          string.Equals(mode, "static", StringComparison.OrdinalIgnoreCase);

        double requiredStandard = isStaticUi ? 0.99 : 0.92;

        double threshold = requiredStandard;
        if ((arguments.TryGetProperty("threshold", out JsonElement tEl) ||
             arguments.TryGetProperty("tolerance", out tEl) ||
             arguments.TryGetProperty("min_ssim", out tEl)) &&
            tEl.TryGetDouble(out double parsedThreshold))
        {
            threshold = Math.Clamp(parsedThreshold, 0.01, 1.0);
        }

        bool enforceThresholds = false;
        if (arguments.TryGetProperty("enforce_thresholds", out JsonElement ethEl) ||
            arguments.TryGetProperty("enforceThresholds", out ethEl) ||
            arguments.TryGetProperty("validate_threshold", out ethEl) ||
            arguments.TryGetProperty("validateThreshold", out ethEl) ||
            arguments.TryGetProperty("strict", out ethEl))
        {
            enforceThresholds = ethEl.ValueKind == JsonValueKind.True ||
                               (ethEl.ValueKind == JsonValueKind.String && bool.TryParse(ethEl.GetString(), out bool b) && b);
        }

        if (enforceThresholds && threshold < requiredStandard)
        {
            sw.Stop();
            return ExecutionTruthEnvelope.CreateJsonResult(
                status: "error",
                executionMode: ExecutionMode.OfflineDirect,
                targetStatus: BuildTargetStatus(),
                metrics: new ExecutionMetrics
                {
                    RoundtripLatencyMs = Math.Round(sw.Elapsed.TotalMilliseconds, 2),
                    EngineRenderFrame = 0,
                    Timestamp = _timeProvider.GetUtcNow().ToString("o")
                },
                data: new
                {
                    match = false,
                    error = $"Requested threshold {threshold:F2} violates minimum standards. Enforced standards require strict >= 0.99 for static 2D UI elements and tolerant >= 0.92 for dynamic 3D viewports.",
                    requiredStandard,
                    thresholdStandard = isStaticUi ? "strict_ui_0.99" : "tolerant_3d_0.92"
                },
                isError: true);
        }

        bool generateIfMissing = false;
        if (arguments.TryGetProperty("generate_fixture_if_missing", out JsonElement genEl) ||
            arguments.TryGetProperty("generateFixtureIfMissing", out genEl))
        {
            generateIfMissing = genEl.ValueKind == JsonValueKind.True ||
                                (genEl.ValueKind == JsonValueKind.String && bool.TryParse(genEl.GetString(), out bool b) && b);
        }

        string target = "hon_x64";
        if (arguments.TryGetProperty("target", out JsonElement tgtEl) && !string.IsNullOrWhiteSpace(tgtEl.GetString()))
        {
            target = tgtEl.GetString()!.Trim();
        }

        Rectangle? cropRect = null;
        if (arguments.TryGetProperty("crop", out JsonElement cropEl) && cropEl.ValueKind == JsonValueKind.Object)
        {
            int x = cropEl.TryGetProperty("x", out JsonElement xEl) && xEl.TryGetInt32(out int xv) ? Math.Max(0, xv) : 0;
            int y = cropEl.TryGetProperty("y", out JsonElement yEl) && yEl.TryGetInt32(out int yv) ? Math.Max(0, yv) : 0;
            int w = cropEl.TryGetProperty("width", out JsonElement wEl) && wEl.TryGetInt32(out int wv) ? Math.Max(1, wv) : 100;
            int h = cropEl.TryGetProperty("height", out JsonElement hEl) && hEl.TryGetInt32(out int hv) ? Math.Max(1, hv) : 100;
            cropRect = new Rectangle(x, y, w, h);
        }

        string resolvedFixturePath = ResolveFixturePath(fixtureParam);

        _logger?.Invoke($"Performing visual assertion against fixture '{resolvedFixturePath}' (Threshold: {threshold}, Target: '{target}', Crop: {cropRect?.ToString() ?? "none"}, Mask: {maskPathParam ?? "none"}, IgnoreViewport: {ignoreViewportRect?.ToString() ?? "none"})...");

        // 1. Acquire current viewport / widget capture
        using Image<Rgba32> currentImage = await AcquireCapturedImageAsync(arguments, target, cropRect, cancellationToken).ConfigureAwait(false);

        // 2. Handle missing fixture / generation
        if (!File.Exists(resolvedFixturePath))
        {
            if (generateIfMissing)
            {
                string? dir = Path.GetDirectoryName(resolvedFixturePath);
                if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
                await currentImage.SaveAsPngAsync(resolvedFixturePath, cancellationToken).ConfigureAwait(false);
                _logger?.Invoke($"[GENERATED] Generated baseline golden fixture at '{resolvedFixturePath}'.");

                sw.Stop();
                return ExecutionTruthEnvelope.CreateJsonResult(
                    status: "success",
                    executionMode: ExecutionMode.OfflineDirect,
                    targetStatus: BuildTargetStatus(),
                    metrics: new ExecutionMetrics
                    {
                        RoundtripLatencyMs = Math.Round(sw.Elapsed.TotalMilliseconds, 2),
                        EngineRenderFrame = 0,
                        Timestamp = _timeProvider.GetUtcNow().ToString("o")
                    },
                    data: new
                    {
                        match = true,
                        generated = true,
                        ssim = 1.0,
                        mse = 0.0,
                        threshold,
                        requiredStandard,
                        thresholdStandard = isStaticUi ? "strict_ui_0.99" : "tolerant_3d_0.92",
                        fixture = resolvedFixturePath,
                        message = $"Generated initial golden fixture '{Path.GetFileName(resolvedFixturePath)}'."
                    });
            }

            sw.Stop();
            return ExecutionTruthEnvelope.CreateJsonResult(
                status: "error",
                executionMode: ExecutionMode.OfflineDirect,
                targetStatus: BuildTargetStatus(),
                metrics: new ExecutionMetrics
                {
                    RoundtripLatencyMs = Math.Round(sw.Elapsed.TotalMilliseconds, 2),
                    EngineRenderFrame = 0,
                    Timestamp = _timeProvider.GetUtcNow().ToString("o")
                },
                data: new
                {
                    match = false,
                    error = $"Fixture not found: '{resolvedFixturePath}'. Set 'generate_fixture_if_missing': true to establish new golden baseline.",
                    fixture = resolvedFixturePath
                },
                isError: true);
        }

        // 3. Load baseline golden image
        using Image<Rgba32> goldenImage = await LoadImageWithRetryAsync(resolvedFixturePath, cancellationToken).ConfigureAwait(false);

        // Ensure matching dimensions
        int targetWidth = Math.Min(currentImage.Width, goldenImage.Width);
        int targetHeight = Math.Min(currentImage.Height, goldenImage.Height);

        if (currentImage.Width != targetWidth || currentImage.Height != targetHeight)
        {
            currentImage.Mutate(ctx => ctx.Resize(targetWidth, targetHeight));
        }
        if (goldenImage.Width != targetWidth || goldenImage.Height != targetHeight)
        {
            goldenImage.Mutate(ctx => ctx.Resize(targetWidth, targetHeight));
        }

        // Optional region mask loading (8-bit grayscale PNG mask, 255 = assert, 0 = ignore)
        string? resolvedMaskPath = ResolveMaskPath(maskPathParam);
        Image<Rgba32>? maskImage = null;
        if (!string.IsNullOrEmpty(resolvedMaskPath) && File.Exists(resolvedMaskPath))
        {
            maskImage = await LoadImageWithRetryAsync(resolvedMaskPath, cancellationToken).ConfigureAwait(false);
            if (maskImage.Width != targetWidth || maskImage.Height != targetHeight)
            {
                maskImage.Mutate(ctx => ctx.Resize(targetWidth, targetHeight));
            }
        }

        // 4. Compute MSE, Wang-Bovik SSIM, and maxDiffPercentage gate with optional region masking and viewport exclusion
        double sumSquaredError = 0.0;
        double sumA = 0.0;
        double sumB = 0.0;
        double sumSqA = 0.0;
        double sumSqB = 0.0;
        double sumAB = 0.0;
        int diffPixelCount = 0;
        int totalPixels = targetWidth * targetHeight;
        int activePixels = 0;

        for (int y = 0; y < targetHeight; y++)
        {
            for (int x = 0; x < targetWidth; x++)
            {
                // Viewport exclusion check
                if (ignoreViewportRect.HasValue && ignoreViewportRect.Value.Contains(x, y))
                {
                    continue;
                }

                // Region mask check (8-bit grayscale: 255 = assert, 0 = ignore; threshold at 128)
                if (maskImage != null)
                {
                    Rgba32 mp = maskImage[x, y];
                    int maskLum = (int)(0.299 * mp.R + 0.587 * mp.G + 0.114 * mp.B);
                    if (mp.A < 128 || maskLum < 128)
                    {
                        continue;
                    }
                }

                activePixels++;

                Rgba32 pA = goldenImage[x, y];
                Rgba32 pB = currentImage[x, y];

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

                if (pixelError > 4.0)
                {
                    diffPixelCount++;
                }
            }
        }

        maskImage?.Dispose();

        int effectiveTotal = Math.Max(1, activePixels);
        double mse = activePixels > 0 ? sumSquaredError / effectiveTotal : 0.0;
        double ssimApprox = 1.0;
        if (activePixels > 0)
        {
            double meanA = sumA / effectiveTotal;
            double meanB = sumB / effectiveTotal;
            double varA = (sumSqA / effectiveTotal) - (meanA * meanA);
            double varB = (sumSqB / effectiveTotal) - (meanB * meanB);
            double covarAB = (sumAB / effectiveTotal) - (meanA * meanB);
            const double c1 = 6.5025;
            const double c2 = 58.5225;
            double num = (2.0 * meanA * meanB + c1) * (2.0 * covarAB + c2);
            double den = (meanA * meanA + meanB * meanB + c1) * (varA + varB + c2);
            ssimApprox = Math.Clamp(den != 0 ? num / den : 1.0, 0.0, 1.0);
        }

        double diffPercentage = Math.Round((double)diffPixelCount / effectiveTotal * 100.0, 2);
        double maxDiffPercentage = isStaticUi ? 5.0 : 15.0;
        if ((arguments.TryGetProperty("max_diff_percentage", out var mdpEl) ||
             arguments.TryGetProperty("maxDiffPercentage", out mdpEl)) &&
            mdpEl.TryGetDouble(out double parsedMdp) && parsedMdp >= 0.0)
        {
            maxDiffPercentage = Math.Clamp(parsedMdp, 0.0, 100.0);
        }

        bool match = ssimApprox >= threshold && diffPercentage <= maxDiffPercentage;

        string? diffImagePath = null;
        if (!match)
        {
            try
            {
                Directory.CreateDirectory(_artifactDirectory);
                string diffFileName = $"assert_fail_{Path.GetFileNameWithoutExtension(resolvedFixturePath)}_{_timeProvider.GetUtcNow():yyyyMMdd_HHmmss}.png";
                diffImagePath = Path.Combine(_artifactDirectory, diffFileName);
                await currentImage.SaveAsPngAsync(diffImagePath, cancellationToken).ConfigureAwait(false);
                _logger?.Invoke($"[MISMATCH] Visual assertion failed (SSIM: {ssimApprox:F4} < {threshold:F4}). Saved candidate to '{diffImagePath}'.");
            }
            catch (Exception ex)
            {
                _logger?.Invoke($"[WARN] Could not save diff artifact: {ex.Message}");
            }
        }

        sw.Stop();

        string executionMode = GameServerManageHandler.IsTestExecution
            ? ExecutionMode.SyntheticFallback
            : ExecutionMode.LiveEngineRpc;

        return ExecutionTruthEnvelope.CreateJsonResult(
            status: match ? "success" : "mismatch",
            executionMode: executionMode,
            targetStatus: BuildTargetStatus(),
            metrics: new ExecutionMetrics
            {
                RoundtripLatencyMs = Math.Round(sw.Elapsed.TotalMilliseconds, 2),
                EngineRenderFrame = 0,
                Timestamp = _timeProvider.GetUtcNow().ToString("o")
            },
            data: new
            {
                match,
                ssim = Math.Round(ssimApprox, 4),
                mse = Math.Round(mse, 4),
                threshold,
                requiredStandard,
                thresholdStandard = isStaticUi ? "strict_ui_0.99" : "tolerant_3d_0.92",
                activePixels,
                maskedPixels = totalPixels - activePixels,
                diffPixels = diffPixelCount,
                diffPercentage = Math.Round((double)diffPixelCount / effectiveTotal * 100.0, 2),
                dimensions = new { width = targetWidth, height = targetHeight },
                fixturePath = resolvedFixturePath,
                maskPath = resolvedMaskPath,
                ignoreViewportRect = ignoreViewportRect.HasValue ? new { x = ignoreViewportRect.Value.X, y = ignoreViewportRect.Value.Y, width = ignoreViewportRect.Value.Width, height = ignoreViewportRect.Value.Height } : null,
                diffArtifactPath = diffImagePath,
                timestamp = _timeProvider.GetUtcNow().ToString("o")
            },
            isError: !match);
    }

    private string ResolveFixturePath(string fixtureParam)
    {
        if (Path.IsPathRooted(fixtureParam))
        {
            return fixtureParam;
        }

        string direct = Path.Combine(_fixtureDirectory, fixtureParam);
        if (File.Exists(direct)) return direct;

        if (!fixtureParam.EndsWith(".png", StringComparison.OrdinalIgnoreCase))
        {
            string withPng = Path.Combine(_fixtureDirectory, $"{fixtureParam}.png");
            if (File.Exists(withPng)) return withPng;
        }

        return direct;
    }

    private string? ResolveMaskPath(string? maskParam)
    {
        if (string.IsNullOrWhiteSpace(maskParam)) return null;
        if (Path.IsPathRooted(maskParam) && File.Exists(maskParam)) return maskParam;
        string direct = Path.Combine(Directory.GetCurrentDirectory(), maskParam);
        if (File.Exists(direct)) return direct;
        string inFixture = Path.Combine(_fixtureDirectory, maskParam);
        if (File.Exists(inFixture)) return inFixture;
        if (!maskParam.EndsWith(".png", StringComparison.OrdinalIgnoreCase))
        {
            string withPng = Path.Combine(_fixtureDirectory, $"{maskParam}.png");
            if (File.Exists(withPng)) return withPng;
        }
        return Path.IsPathRooted(maskParam) ? maskParam : direct;
    }

    private async Task<Image<Rgba32>> AcquireCapturedImageAsync(JsonElement arguments, string target, Rectangle? cropRect, CancellationToken cancellationToken)
    {
        // 1. Direct candidate image path in arguments (e.g. for deterministic unit test fixtures)
        string? candidatePath = null;
        if (arguments.TryGetProperty("candidate", out JsonElement cEl) ||
            arguments.TryGetProperty("candidate_path", out cEl) ||
            arguments.TryGetProperty("candidate_fixture", out cEl))
        {
            candidatePath = cEl.GetString()?.Trim();
        }
        if (!string.IsNullOrEmpty(candidatePath))
        {
            string resolvedCandidate = Path.IsPathRooted(candidatePath) ? candidatePath : Path.Combine(_fixtureDirectory, candidatePath);
            if (!File.Exists(resolvedCandidate))
            {
                resolvedCandidate = Path.Combine(Directory.GetCurrentDirectory(), candidatePath);
            }
            if (File.Exists(resolvedCandidate))
            {
                var candidateImg = await LoadImageWithRetryAsync(resolvedCandidate, cancellationToken).ConfigureAwait(false);
                if (cropRect.HasValue)
                {
                    Rectangle safeCrop = Rectangle.Intersect(cropRect.Value, new Rectangle(0, 0, candidateImg.Width, candidateImg.Height));
                    if (safeCrop.Width > 0 && safeCrop.Height > 0)
                    {
                        candidateImg.Mutate(ctx => ctx.Crop(safeCrop));
                    }
                }
                return candidateImg;
            }
        }

        // 2. Query capturer (supports mocked capturers in unit tests or live GPU capture)
        bool isCustomMockCapturer = _windowCapturer.GetType() != typeof(WindowCapturer);
        if (!GameServerManageHandler.IsTestExecution || isCustomMockCapturer)
        {
            try
            {
                var captureResult = await _windowCapturer.CaptureAsync(target: target, captureMethod: "auto", saveArtifact: false, cancellationToken: cancellationToken).ConfigureAwait(false);
                if (captureResult.Success && !string.IsNullOrEmpty(captureResult.Base64Png))
                {
                    byte[] bytes = Convert.FromBase64String(captureResult.Base64Png);
                    using var ms = new MemoryStream(bytes);
                    var img = await Image.LoadAsync<Rgba32>(ms, cancellationToken).ConfigureAwait(false);
                    if (cropRect.HasValue)
                    {
                        Rectangle safeCrop = Rectangle.Intersect(cropRect.Value, new Rectangle(0, 0, img.Width, img.Height));
                        if (safeCrop.Width > 0 && safeCrop.Height > 0)
                        {
                            img.Mutate(ctx => ctx.Crop(safeCrop));
                        }
                    }
                    return img;
                }
            }
            catch (Exception ex)
            {
                _logger?.Invoke($"[WARN] Window capture failed: {ex.Message}");
            }
        }

        // 3. Fast in-memory synthetic frame for automated test execution fallback
        if (GameServerManageHandler.IsTestExecution)
        {
            var testImg = new Image<Rgba32>(128, 128);
            for (int y = 0; y < 128; y++)
            {
                for (int x = 0; x < 128; x++)
                {
                    testImg[x, y] = new Rgba32((byte)(x * 2), (byte)(y * 2), 128, 255);
                }
            }
            if (cropRect.HasValue)
            {
                Rectangle safeCrop = Rectangle.Intersect(cropRect.Value, new Rectangle(0, 0, testImg.Width, testImg.Height));
                if (safeCrop.Width > 0 && safeCrop.Height > 0)
                {
                    testImg.Mutate(ctx => ctx.Crop(safeCrop));
                }
            }
            return testImg;
        }

        // 4. Fallback placeholder image if capture failed
        var fallback = new Image<Rgba32>(64, 64);
        for (int y = 0; y < 64; y++)
        {
            for (int x = 0; x < 64; x++)
            {
                fallback[x, y] = new Rgba32(32, 32, 32, 255);
            }
        }
        return fallback;
    }

    private async Task<Image<Rgba32>> LoadImageWithRetryAsync(string filePath, CancellationToken cancellationToken)
    {
        int retries = 0;
        while (retries < 10)
        {
            try
            {
                var fi = new FileInfo(filePath);
                if (fi.Exists && fi.Length > 0)
                {
                    using var fs = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                    return await Image.LoadAsync<Rgba32>(fs, cancellationToken).ConfigureAwait(false);
                }
            }
            catch (IOException)
            {
                // File locked by render thread or disk writer
            }
            await Task.Delay(20, cancellationToken).ConfigureAwait(false);
            retries++;
        }

        using var finalFs = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        return await Image.LoadAsync<Rgba32>(finalFs, cancellationToken).ConfigureAwait(false);
    }

    private TargetStatus BuildTargetStatus()
    {
        return new TargetStatus
        {
            Client = _clientManageHandler.GetLivenessStatus(),
            Server = _serverManageHandler.GetLivenessStatus()
        };
    }
}
