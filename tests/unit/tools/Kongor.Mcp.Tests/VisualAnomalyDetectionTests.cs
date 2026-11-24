// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// TAO (Together As One) MCP & Kongor CLI -- Created by guccidonut (https://github.com/guccidonut/PKV)

using System.IO;
using Kongor.Cli.Mcp.Imaging;
using Kongor.Cli.Mcp.Services;
using Xunit;

namespace Kongor.Mcp.Tests;

public sealed class VisualAnomalyDetectionTests
{
    private readonly VisualAnomalyDetectionService _detector = new();

    [Fact]
    public void AnalyzeFrame_MissingTexture_DetectsMagentaClusters()
    {
        // 100x100 image with a 20x20 magenta block (400 px = 4% missing textures)
        using Image<Rgba32> image = new(100, 100, new Rgba32(30, 40, 30));
        for (int y = 10; y < 30; y++)
        {
            for (int x = 10; x < 30; x++)
            {
                image[x, y] = new Rgba32(255, 0, 255);
            }
        }

        VisualAnomalyReport report = _detector.AnalyzeFrame(image);

        Assert.True(report.HasAnomalies);
        Assert.True(report.HasMissingTextures);
        Assert.True(report.MissingTexturePercentage > 0.05);
        Assert.Contains(report.Anomalies, a => a.Type == "MissingTexture");
    }

    [Fact]
    public void AnalyzeFrame_CleanImage_PassesWithoutAnomalies()
    {
        // Normal game terrain colors (greens and browns)
        using Image<Rgba32> image = new(100, 100, new Rgba32(50, 80, 40));

        VisualAnomalyReport report = _detector.AnalyzeFrame(image);

        Assert.False(report.HasAnomalies);
        Assert.False(report.HasMissingTextures);
        Assert.False(report.IsBlackScreen);
        Assert.False(report.IsInvertedViewport);
        Assert.False(report.HasTeapotFallback);
        Assert.Contains("passed", report.Summary, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void AnalyzeFrame_BlackScreen_DetectsBlackScreen()
    {
        using Image<Rgba32> image = new(100, 100, new Rgba32(0, 0, 0));

        VisualAnomalyReport report = _detector.AnalyzeFrame(image);

        Assert.True(report.HasAnomalies);
        Assert.True(report.IsBlackScreen);
        Assert.Contains(report.Anomalies, a => a.Type == "BlackScreen");
    }

    [Fact]
    public void AnalyzeFrame_TeapotFallbackInLog_DetectsTeapotFallback()
    {
        using Image<Rgba32> image = new(100, 100, new Rgba32(60, 60, 60));
        string logSnippet = "[K2] Failed to load model '/heroes/legionnaire/model.mdf': falling back to /core/null/null.mdf (teapot)";

        VisualAnomalyReport report = _detector.AnalyzeFrame(image, logSnippet);

        Assert.True(report.HasAnomalies);
        Assert.True(report.HasTeapotFallback);
        Assert.NotNull(report.TeapotFallbackDetails);
        Assert.Contains(report.Anomalies, a => a.Type == "TeapotModelFallback");
    }

    [Fact]
    public void SelfHealingToggle_WhenDisabled_DoesNotRemediateCommand()
    {
        ClientCommandRemediationService service = new();
        service.IsEnabled = false;

        var record = service.RecordAndRemediate("Windows.main", "attempt to index global 'Windows'", "lua");

        Assert.False(record.Remediation.CanAutoRetry);
        Assert.Null(record.Remediation.ImprovedCommand);
    }

    [Fact]
    public void SelfHealingToggle_WhenEnabled_RemediatesCommand()
    {
        ClientCommandRemediationService service = new();
        service.IsEnabled = true;

        var record = service.RecordAndRemediate("Windows.main", "attempt to index global 'Windows'", "lua");

        Assert.True(record.Remediation.CanAutoRetry);
        Assert.NotNull(record.Remediation.ImprovedCommand);
        Assert.Contains("UIManager.GetInterface('main')", record.Remediation.ImprovedCommand);
    }

    [Fact]
    public void AnalyzeFrame_BlankWhiteBox_DetectsUntexturedQuadPlaceholder()
    {
        // 100x100 image with a 20x20 pure white block (untextured quad placeholder)
        using Image<Rgba32> image = new(100, 100, new Rgba32(30, 40, 30));
        for (int y = 10; y < 30; y++)
        {
            for (int x = 10; x < 30; x++)
            {
                image[x, y] = new Rgba32(255, 255, 255);
            }
        }

        VisualAnomalyReport report = _detector.AnalyzeFrame(image);

        Assert.True(report.HasAnomalies);
        Assert.True(report.HasBlankWhitePlaceholder);
        Assert.Equal(1, report.BlankWhitePlaceholderCount);
        Assert.Contains(report.Anomalies, a => a.Type == "BlankWhitePlaceholder");
    }

    [Fact]
    public void GenerateIssueReport_WhenInvoked_ProducesStandardMarkdownIssue()
    {
        using Image<Rgba32> image = new(100, 100, new Rgba32(30, 40, 30));
        for (int y = 10; y < 30; y++)
        {
            for (int x = 10; x < 30; x++)
            {
                image[x, y] = new Rgba32(255, 255, 255);
            }
        }

        VisualAnomalyReport report = _detector.AnalyzeFrame(image);
        string markdown = _detector.GenerateIssueReport(report, "ISSUE-047", "Game Client", "Lobby Party Bar");

        Assert.Contains("[ISSUE-047]", markdown);
        Assert.Contains("Game Client", markdown);
        Assert.Contains("Lobby Party Bar", markdown);
        Assert.Contains("Subsystem", markdown);
        Assert.Contains("Symptom / Log Snippet", markdown);
        Assert.Contains("BlankWhitePlaceholder", markdown);
        Assert.Contains("[OPEN]", markdown);
        Assert.Contains("Remediation & Verification", markdown);
    }

    [Fact]
    public void AnalyzeFrame_UserLobbyScreenshot_DetectsWhiteSquareAnomaly()
    {
        string? screenshotPath = Environment.GetEnvironmentVariable("KONGOR_TEST_LOBBY_SCREENSHOT");
        if (string.IsNullOrWhiteSpace(screenshotPath) || !File.Exists(screenshotPath)) return;

        using Image<Rgba32> image = Image.Load<Rgba32>(screenshotPath);
        
        // Find white pixels or runs in bottom area (lobby party bar is at the bottom)
        int w = image.Width;
        int h = image.Height;
        System.Text.StringBuilder sb = new();
        sb.AppendLine($"Dimensions: {w}x{h}");
        
        int nearWhiteCount = 0;
        int minX = int.MaxValue, maxX = int.MinValue, minY = int.MaxValue, maxY = int.MinValue;
        for (int y = 0; y < h; y++)
        {
            for (int x = 0; x < w; x++)
            {
                var p = image[x, y];
                if (p.R >= 235 && p.G >= 235 && p.B >= 235)
                {
                    nearWhiteCount++;
                    if (x < minX) minX = x;
                    if (x > maxX) maxX = x;
                    if (y < minY) minY = y;
                    if (y > maxY) maxY = y;
                }
            }
        }
        sb.AppendLine($"Near white pixels count: {nearWhiteCount}, Box: [{minX},{minY}] to [{maxX},{maxY}], Width: {maxX - minX + 1}, Height: {maxY - minY + 1}");

        int bMinX = int.MaxValue, bMaxX = int.MinValue, bMinY = int.MaxValue, bMaxY = int.MinValue;
        int bNearWhite = 0;
        for (int y = 450; y < h; y++)
        {
            for (int x = 0; x < w; x++)
            {
                var p = image[x, y];
                if (p.R >= 220 && p.G >= 220 && p.B >= 220)
                {
                    bNearWhite++;
                    if (x < bMinX) bMinX = x;
                    if (x > bMaxX) bMaxX = x;
                    if (y < bMinY) bMinY = y;
                    if (y > bMaxY) bMaxY = y;
                }
            }
        }
        sb.AppendLine($"Bottom y>=450: {bNearWhite} near-white pixels. Bounding box: [{bMinX},{bMinY}] to [{bMaxX},{bMaxY}], W={bMaxX - bMinX + 1}, H={bMaxY - bMinY + 1}");
        double sumL = 0, sumL2 = 0;
        int countL = 0;
        for (int cy = bMinY; cy <= bMaxY; cy++)
        {
            for (int cx = bMinX; cx <= bMaxX; cx++)
            {
                var p = image[cx, cy];
                double lum = 0.299 * p.R + 0.587 * p.G + 0.114 * p.B;
                sumL += lum;
                sumL2 += lum * lum;
                countL++;
            }
        }
        double meanL = sumL / countL;
        double varL = (sumL2 / countL) - (meanL * meanL);
        double devL = varL > 0 ? Math.Sqrt(varL) : 0;
        sb.AppendLine($"Box stats: count={countL}, mean={meanL:F2}, stdDev={devL:F2}");

        VisualAnomalyReport report = _detector.AnalyzeFrame(image);
        sb.AppendLine($"HasAnomalies: {report.HasAnomalies}, Count: {report.BlankWhitePlaceholderCount}, Summary: {report.Summary}");

        Assert.True(report.HasBlankWhitePlaceholder, sb.ToString());
    }

    [Fact]
    public void AnalyzeFrame_UncorroboratedStoneReliefPixels_SuppressesTeapotFalsePositiveUntilLogCorroborates()
    {
        // Simulate lit white/gold dress or marble store pedestal in the 3D preview viewport
        // (100x60 = 6000 low-saturation bright pixels at x=[140..239], y=[60..119], aspect 1.67)
        using Image<Rgba32> image = new(300, 200, new Rgba32(45, 55, 45));
        for (int y = 60; y < 120; y++)
        {
            for (int x = 140; x < 240; x++)
            {
                image[x, y] = new Rgba32(182, 178, 174);
            }
        }

        // 1. Without null.mdf in console logs -> MUST NOT false-positive HasTeapotFallback or High anomaly
        VisualAnomalyReport uncorroborated = _detector.AnalyzeFrame(image, "[INFO] Loaded /heroes/empath/alt5/model.mdf");
        Assert.False(uncorroborated.HasTeapotFallback);
        Assert.False(uncorroborated.HasAnomalies);
        Assert.DoesNotContain(uncorroborated.Anomalies, a => a.Type == "TeapotModelFallback");
        Assert.NotNull(uncorroborated.TeapotFallbackDetails);
        Assert.Contains("suppressed because zero /core/null/null.mdf entries exist", uncorroborated.TeapotFallbackDetails);

        // 2. With null.mdf in console logs -> MUST report HasTeapotFallback = true & High severity TeapotModelFallback
        VisualAnomalyReport corroborated = _detector.AnalyzeFrame(
            image,
            "[K2] Model load failed: falling back to /core/null/null.mdf");
        Assert.True(corroborated.HasTeapotFallback);
        Assert.True(corroborated.HasAnomalies);
        var teapotAnomaly = Assert.Single(corroborated.Anomalies, a => a.Type == "TeapotModelFallback");
        Assert.Equal("High", teapotAnomaly.Severity);
        Assert.Contains("null.mdf", corroborated.TeapotFallbackDetails);
    }

    [Fact]
    public void AnalyzeFrame_StoreScreenshot_DetectsTeapotFallbackAnomaly()
    {
        string? screenshotPath = Environment.GetEnvironmentVariable("KONGOR_TEST_STORE_SCREENSHOT");
        if (string.IsNullOrWhiteSpace(screenshotPath) || !File.Exists(screenshotPath)) return;

        using Image<Rgba32> image = Image.Load<Rgba32>(screenshotPath);
        VisualAnomalyReport report = _detector.AnalyzeFrame(image, "[K2] /core/null/null.mdf");

        Assert.True(report.HasAnomalies, $"Expected anomalies in store screenshot with Utah Teapot: {report.Summary}");
        Assert.True(report.HasTeapotFallback, $"Expected Teapot fallback detected: {report.Summary}");
        Assert.Contains(report.Anomalies, a => a.Type == "TeapotModelFallback");
        Assert.NotNull(report.TeapotFallbackDetails);
        Assert.Contains("Utah Teapot", report.TeapotFallbackDetails);
    }
}
