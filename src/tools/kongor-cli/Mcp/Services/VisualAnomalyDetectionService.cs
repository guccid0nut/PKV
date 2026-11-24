// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// TAO (Together As One) MCP & Kongor CLI -- Created by guccidonut (https://github.com/guccidonut/PKV)

using System.Diagnostics;
using System.IO;
using System.Text.RegularExpressions;
using Kongor.Cli.Mcp.Imaging;

namespace Kongor.Cli.Mcp.Services;

/// <summary>
/// Represents a specific visual anomaly found in a rendered frame or screenshot.
/// </summary>
public sealed record VisualAnomaly
{
    public string Type { get; init; } = string.Empty;
    public string Severity { get; init; } = "Warning";
    public string Description { get; init; } = string.Empty;
    public double Confidence { get; init; } = 1.0;
    public string? Details { get; init; }
}

/// <summary>
/// Summary report of all visual anomalies detected in a captured frame.
/// </summary>
public sealed record VisualAnomalyReport
{
    public bool HasAnomalies => Anomalies.Count > 0;
    public IReadOnlyList<VisualAnomaly> Anomalies { get; init; } = [];
    public double MissingTexturePercentage { get; init; }
    public bool HasMissingTextures { get; init; }
    public bool IsBlackScreen { get; init; }
    public bool IsInvertedViewport { get; init; }
    public bool HasTeapotFallback { get; init; }
    public string? TeapotFallbackDetails { get; init; }
    public bool HasBlankWhitePlaceholder { get; init; }
    public int BlankWhitePlaceholderCount { get; init; }
    public string Summary { get; init; } = "No visual anomalies detected.";
    public long AnalysisDurationMs { get; init; }
}

/// <summary>
/// Service interface for detecting graphical glitches, broken textures, and model fallbacks in screenshots.
/// </summary>
public interface IVisualAnomalyDetectionService
{
    VisualAnomalyReport AnalyzeFrame(Image<Rgba32> image, string? consoleLogSnippet = null);
    VisualAnomalyReport AnalyzePng(byte[] pngBytes, string? consoleLogSnippet = null);
    string GenerateIssueReport(VisualAnomalyReport report, string issueId, string subsystem, string screenContext);
}

/// <summary>
/// High-performance visual anomaly detector for K2 engine screen captures.
/// Detects:
/// 1. Missing textures (#FF00FF magenta / pink fallback material clusters).
/// 2. Utah Teapot fallback (/core/null/null.mdf) via log scraping and geometry indicators.
/// 3. Blank/Black screen render stalls (uninitialized swap chain or pipeline crash).
/// 4. Inverted/Upside-down viewports (flipped vertical luminance / coordinate inversions).
/// </summary>
public sealed class VisualAnomalyDetectionService : IVisualAnomalyDetectionService
{
    private static readonly Regex TeapotLogRegex = new(
        @"(null\.mdf|Failed to load model|Using default model|teapot)",
        RegexOptions.IgnoreCase | RegexOptions.Compiled
    );

    public VisualAnomalyReport AnalyzePng(byte[] pngBytes, string? consoleLogSnippet = null)
    {
        ArgumentNullException.ThrowIfNull(pngBytes);
        using Image<Rgba32> image = Image.Load<Rgba32>(pngBytes);
        return AnalyzeFrame(image, consoleLogSnippet);
    }

    public VisualAnomalyReport AnalyzeFrame(Image<Rgba32> image, string? consoleLogSnippet = null)
    {
        ArgumentNullException.ThrowIfNull(image);
        Stopwatch sw = Stopwatch.StartNew();

        int width = image.Width;
        int height = image.Height;
        long totalPixels = (long)width * height;

        long magentaCount = 0;
        long blackCount = 0;
        double topHalfLuminance = 0;
        double bottomHalfLuminance = 0;

        int midY = height / 2;

        List<WhiteBoxComponent> activeWhiteComponents = [];
        List<WhiteBoxComponent> finishedWhiteComponents = [];
        List<WhiteBoxComponent> validPlaceholders = [];

        int tpMinX = int.MaxValue, tpMaxX = int.MinValue, tpMinY = int.MaxValue, tpMaxY = int.MinValue;
        long tpPixelCount = 0;

        image.ProcessPixelRows(accessor =>
        {
            for (int y = 0; y < height; y++)
            {
                Span<Rgba32> pixelRow = accessor.GetRowSpan(y);
                int whiteRunStart = -1;

                for (int x = 0; x < width; x++)
                {
                    Rgba32 p = pixelRow[x];

                    // Pure black or near-black
                    if (p.R <= 2 && p.G <= 2 && p.B <= 2)
                    {
                        blackCount++;
                    }

                    // K2 Missing Texture (Magenta / Neon Pink)
                    // Classic #FF00FF: R > 220, G < 40, B > 220
                    if (p.R >= 210 && p.G <= 45 && p.B >= 210)
                    {
                        magentaCount++;
                    }

                    // Blank white placeholder quad (R, G, B >= 235)
                    bool isWhite = p.R >= 235 && p.G >= 235 && p.B >= 235;
                    if (isWhite)
                    {
                        if (whiteRunStart < 0)
                        {
                            whiteRunStart = x;
                        }
                    }
                    else
                    {
                        if (whiteRunStart >= 0)
                        {
                            int spanLen = x - whiteRunStart;
                            if (spanLen >= 10)
                            {
                                AddSpan(activeWhiteComponents, whiteRunStart, x - 1, y);
                            }
                            whiteRunStart = -1;
                        }
                    }

                    // Utah Teapot preview visual signature:
                    // /core/null/null.mdf renders with relief_rockwall diffuse texture (achromatic stone blocks)
                    // High luminance (p.R, p.G, p.B >= 135) with very low saturation (|max - min| <= 30)
                    // occurring in the 3D model viewport / preview region (x >= width * 0.35, y >= height * 0.20 && y <= height * 0.85)
                    if (x >= width * 0.35 && y >= height * 0.20 && y <= height * 0.85)
                    {
                        if (p.R >= 135 && p.G >= 135 && p.B >= 135)
                        {
                            int maxC = Math.Max(p.R, Math.Max(p.G, p.B));
                            int minC = Math.Min(p.R, Math.Min(p.G, p.B));
                            if (maxC - minC <= 30)
                            {
                                tpPixelCount++;
                                if (x < tpMinX) tpMinX = x;
                                if (x > tpMaxX) tpMaxX = x;
                                if (y < tpMinY) tpMinY = y;
                                if (y > tpMaxY) tpMaxY = y;
                            }
                        }
                    }

                    // Compute luminance for vertical balance check
                    double lum = 0.299 * p.R + 0.587 * p.G + 0.114 * p.B;
                    if (y < midY)
                    {
                        topHalfLuminance += lum;
                    }
                    else
                    {
                        bottomHalfLuminance += lum;
                    }
                }

                if (whiteRunStart >= 0)
                {
                    int spanLen = width - whiteRunStart;
                    if (spanLen >= 10)
                    {
                        AddSpan(activeWhiteComponents, whiteRunStart, width - 1, y);
                    }
                }

                // Retire components not seen on this row
                RetireComponents(activeWhiteComponents, finishedWhiteComponents, y);
            }

            finishedWhiteComponents.AddRange(activeWhiteComponents);
            activeWhiteComponents.Clear();

            // Validate white box components
            foreach (var comp in finishedWhiteComponents)
            {
                int w = comp.Width;
                int h = comp.Height;
                if (w < 14 || h < 14) continue;
                if (w >= width * 0.90 && h >= height * 0.90) continue;

                double aspect = (double)w / h;
                if (aspect < 0.35 || aspect > 2.8) continue;

                double fillRatio = (double)comp.PixelCount / (w * h);
                if (fillRatio < 0.70) continue;

                double sumLum = 0;
                double sumLumSq = 0;
                int sampleCount = 0;

                for (int cy = comp.MinY; cy <= comp.MaxY; cy++)
                {
                    Span<Rgba32> row = accessor.GetRowSpan(cy);
                    for (int cx = comp.MinX; cx <= comp.MaxX; cx++)
                    {
                        Rgba32 p = row[cx];
                        double lum = 0.299 * p.R + 0.587 * p.G + 0.114 * p.B;
                        sumLum += lum;
                        sumLumSq += lum * lum;
                        sampleCount++;
                    }
                }

                if (sampleCount > 0)
                {
                    double mean = sumLum / sampleCount;
                    double variance = (sumLumSq / sampleCount) - (mean * mean);
                    double stdDev = variance > 0 ? Math.Sqrt(variance) : 0;
                    if (stdDev <= 14.0 && mean >= 235)
                    {
                        validPlaceholders.Add(comp);
                    }
                }
            }

            void AddSpan(List<WhiteBoxComponent> list, int startX, int endX, int curY)
            {
                var match = list.FirstOrDefault(c =>
                    startX <= c.MaxX + 2 && endX >= c.MinX - 2 && c.LastSeenRow == curY - 1);

                if (match != null)
                {
                    match.MinX = Math.Min(match.MinX, startX);
                    match.MaxX = Math.Max(match.MaxX, endX);
                    match.MaxY = curY;
                    match.PixelCount += (endX - startX + 1);
                    match.LastSeenRow = curY;
                }
                else
                {
                    list.Add(new WhiteBoxComponent
                    {
                        MinX = startX,
                        MaxX = endX,
                        MinY = curY,
                        MaxY = curY,
                        PixelCount = (endX - startX + 1),
                        LastSeenRow = curY
                    });
                }
            }

            void RetireComponents(List<WhiteBoxComponent> active, List<WhiteBoxComponent> finished, int curY)
            {
                for (int i = active.Count - 1; i >= 0; i--)
                {
                    if (active[i].LastSeenRow < curY)
                    {
                        finished.Add(active[i]);
                        active.RemoveAt(i);
                    }
                }
            }
        });

        double missingTextureRatio = totalPixels > 0 ? (double)magentaCount / totalPixels : 0;
        double missingTexturePercent = Math.Round(missingTextureRatio * 100.0, 3);
        bool hasMissingTextures = missingTexturePercent >= 0.05 || magentaCount > 150;

        double blackRatio = totalPixels > 0 ? (double)blackCount / totalPixels : 0;
        bool isBlackScreen = blackRatio >= 0.985;

        bool hasBlankWhitePlaceholder = validPlaceholders.Count > 0;
        int blankWhitePlaceholderCount = validPlaceholders.Count;

        // Inverted viewport check: in standard HoN 3D scenes, top half (skybox/lighting) is significantly brighter
        // than bottom half (dark terrain/river). A massive flip where bottom is >3x brighter than top can indicate Vulkan Y-flip.
        long halfPixels = (long)width * Math.Max(1, midY);
        double avgTopLum = halfPixels > 0 ? topHalfLuminance / halfPixels : 0;
        double avgBottomLum = halfPixels > 0 ? bottomHalfLuminance / halfPixels : 0;
        bool isInvertedViewport = false;
        if (!isBlackScreen && avgTopLum > 5 && avgBottomLum > 5)
        {
            if (avgBottomLum > avgTopLum * 3.5 && avgBottomLum > 80)
            {
                isInvertedViewport = true;
            }
        }

        // Teapot detection requires explicit console log corroboration (e.g. /core/null/null.mdf).
        // Uncorroborated stone-relief / low-saturation pixel clusters on valid lit 3D models
        // (e.g. Beauty Empath white/gold dress, Banana Empath pedestal, Store 5 pedestals)
        // must never report HasTeapotFallback=true or emit a High-severity TeapotModelFallback anomaly.
        bool hasTeapot = false;
        string? teapotDetails = null;
        bool hasStoneReliefCandidate = false;
        string? stoneReliefDiagnostic = null;

        if (tpPixelCount >= 2500 && tpMinX <= tpMaxX)
        {
            int tpWidth = tpMaxX - tpMinX + 1;
            int tpHeight = tpMaxY - tpMinY + 1;
            if (tpWidth >= 80 && tpHeight >= 50)
            {
                double tpAspect = (double)tpWidth / tpHeight;
                if (tpAspect >= 1.1 && tpAspect <= 2.8)
                {
                    hasStoneReliefCandidate = true;
                    stoneReliefDiagnostic = $"{tpPixelCount} low-saturation lit pixels in {tpWidth}x{tpHeight} viewport bounds (aspect ratio {tpAspect:F2})";
                }
            }
        }

        if (!string.IsNullOrWhiteSpace(consoleLogSnippet))
        {
            Match match = TeapotLogRegex.Match(consoleLogSnippet);
            if (match.Success)
            {
                hasTeapot = true;
                teapotDetails = hasStoneReliefCandidate
                    ? $"Engine log triggered fallback to Utah Teapot: '{match.Value}' (corroborated by {stoneReliefDiagnostic}). A 3D model or effect failed to load."
                    : $"Engine log triggered fallback to Utah Teapot: '{match.Value}'. A 3D model or effect failed to load.";
            }
        }

        if (!hasTeapot && hasStoneReliefCandidate)
        {
            teapotDetails = $"[Diagnostic Note - Uncorroborated] Observed {stoneReliefDiagnostic}, suppressed because zero /core/null/null.mdf entries exist in console logs.";
        }

        List<VisualAnomaly> anomalies = [];

        if (hasMissingTextures)
        {
            anomalies.Add(new VisualAnomaly
            {
                Type = "MissingTexture",
                Severity = "High",
                Description = $"Detected {missingTexturePercent:F2}% magenta (#FF00FF) pixels ({magentaCount} px). K2 engine rendered missing/unresolved texture placeholder.",
                Confidence = 0.95,
                Details = $"{magentaCount} missing texture pixels detected in {width}x{height} frame."
            });
        }

        if (hasBlankWhitePlaceholder)
        {
            var firstBox = validPlaceholders[0];
            anomalies.Add(new VisualAnomaly
            {
                Type = "BlankWhitePlaceholder",
                Severity = "Medium",
                Description = $"Detected {blankWhitePlaceholderCount} untextured quad placeholder(s) (solid white box).",
                Confidence = 0.92,
                Details = $"{blankWhitePlaceholderCount} untextured quad(s) found. Example bounding box: {firstBox.Width}x{firstBox.Height} at [{firstBox.MinX},{firstBox.MinY}]."
            });
        }

        if (isBlackScreen)
        {
            anomalies.Add(new VisualAnomaly
            {
                Type = "BlackScreen",
                Severity = "Critical",
                Description = $"Detected {blackRatio * 100:F1}% pure black pixels. Graphics pipeline stalled or framebuffer not presenting.",
                Confidence = 0.99,
                Details = "Framebuffer contains near-zero luminance."
            });
        }

        if (isInvertedViewport)
        {
            anomalies.Add(new VisualAnomaly
            {
                Type = "InvertedViewport",
                Severity = "High",
                Description = "Vertical luminance distribution indicates upside-down or vertically flipped viewport (possible Vulkan/DirectX coordinate inversion).",
                Confidence = 0.75,
                Details = $"Top luminance: {avgTopLum:F1}, Bottom luminance: {avgBottomLum:F1}."
            });
        }

        if (hasTeapot)
        {
            anomalies.Add(new VisualAnomaly
            {
                Type = "TeapotModelFallback",
                Severity = "High",
                Description = "Utah Teapot fallback (/core/null/null.mdf) detected in 3D scene or engine logs.",
                Confidence = !string.IsNullOrWhiteSpace(consoleLogSnippet) ? 0.98 : 0.92,
                Details = teapotDetails
            });
        }

        sw.Stop();

        string summary = anomalies.Count switch
        {
            0 => "Frame visual inspection passed: No missing textures, black screen, or inverted viewport anomalies detected.",
            1 => $"1 visual anomaly detected: {anomalies[0].Description}",
            _ => $"{anomalies.Count} visual anomalies detected: " + string.Join("; ", anomalies.Select(a => a.Type))
        };

        return new VisualAnomalyReport
        {
            Anomalies = anomalies,
            MissingTexturePercentage = missingTexturePercent,
            HasMissingTextures = hasMissingTextures,
            IsBlackScreen = isBlackScreen,
            IsInvertedViewport = isInvertedViewport,
            HasTeapotFallback = hasTeapot,
            TeapotFallbackDetails = teapotDetails,
            HasBlankWhitePlaceholder = hasBlankWhitePlaceholder,
            BlankWhitePlaceholderCount = blankWhitePlaceholderCount,
            Summary = summary,
            AnalysisDurationMs = sw.ElapsedMilliseconds
        };
    }

    public string GenerateIssueReport(VisualAnomalyReport report, string issueId, string subsystem, string screenContext)
    {
        ArgumentNullException.ThrowIfNull(report);
        ArgumentException.ThrowIfNullOrWhiteSpace(issueId);
        ArgumentException.ThrowIfNullOrWhiteSpace(subsystem);
        ArgumentException.ThrowIfNullOrWhiteSpace(screenContext);

        var sb = new System.Text.StringBuilder();
        sb.AppendLine($"### [{issueId}] Visual Anomaly Detected: {screenContext}");
        sb.AppendLine($"* **Subsystem**: `{subsystem}`");
        sb.AppendLine("* **Discovery Method**: Automated Screen Capture & Visual Anomaly Detection (`VisualAnomalyDetectionService`)");
        sb.AppendLine("* **Symptom / Log Snippet**:");
        sb.AppendLine("  ```");
        sb.AppendLine($"  Summary: {report.Summary}");
        foreach (var anomaly in report.Anomalies)
        {
            sb.AppendLine($"  - [{anomaly.Severity}] {anomaly.Type}: {anomaly.Description}");
            if (!string.IsNullOrWhiteSpace(anomaly.Details))
            {
                sb.AppendLine($"    Details: {anomaly.Details}");
            }
        }
        sb.AppendLine("  ```");
        sb.AppendLine($"* **Root Cause Diagnosis**: Automated visual inspection detected {report.Anomalies.Count} anomaly pattern(s) during capture of '{screenContext}'.");
        sb.AppendLine("* **Status**: `[OPEN]`");
        sb.AppendLine("* **Remediation & Verification**:");
        sb.AppendLine($"  - Investigate textures, UI XML bindings, and assets referenced in '{screenContext}'.");
        sb.AppendLine("  - Re-run visual inspection after applying asset or script fixes to confirm anomaly resolution.");

        return sb.ToString().TrimEnd();
    }

    private sealed class WhiteBoxComponent
    {
        public int MinX { get; set; }
        public int MaxX { get; set; }
        public int MinY { get; set; }
        public int MaxY { get; set; }
        public int PixelCount { get; set; }
        public int Width => MaxX - MinX + 1;
        public int Height => MaxY - MinY + 1;
        public int LastSeenRow { get; set; }
    }
}
