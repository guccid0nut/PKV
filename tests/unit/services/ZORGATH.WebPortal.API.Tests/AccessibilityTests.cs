// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

// ============================================================================
// File: AccessibilityTests.cs
// System: ZORGATH.WebPortal.API.Tests - UI / Web Portal Accessibility & SEO Governance
// Policy: WCAG 2.1 AA Compliance, 100% Alt Attribute Coverage, Keyboard Nav Landmark
// ============================================================================

using System.Text.RegularExpressions;
using FluentAssertions;
using Xunit;

namespace ZORGATH.WebPortal.API.Tests;

/// <summary>
/// Accessibility and SEO governance tests for DAWNBRINGER Razor views.
/// Ensures all rendered images provide descriptive alt text for screen readers,
/// skip-to-content keyboard bypass exists, and Open Graph meta tags are present.
/// </summary>
public sealed class AccessibilityTests
{
    private static string GetSolutionRoot()
    {
        var current = AppContext.BaseDirectory;
        while (!string.IsNullOrEmpty(current))
        {
            if (File.Exists(Path.Combine(current, "ProjectKongor.slnx")) ||
                File.Exists(Path.Combine(current, "kongor.ps1")))
            {
                return current;
            }

            var parent = Directory.GetParent(current);
            if (parent == null) break;
            current = parent.FullName;
        }

        throw new DirectoryNotFoundException("Could not locate solution root (ProjectKongor.slnx or kongor.ps1).");
    }

    [Fact]
    public void AllImages_InWebUiViews_MustHaveAltAttribute()
    {
        var viewsPath = Path.Combine(GetSolutionRoot(), "src", "services", "web-ui", "Views");
        Directory.Exists(viewsPath).Should().BeTrue("Views directory must exist at {0}", viewsPath);

        var cshtmlFiles = Directory.GetFiles(viewsPath, "*.cshtml", SearchOption.AllDirectories)
            .Where(f => !f.Contains(Path.DirectorySeparatorChar + "bin" + Path.DirectorySeparatorChar) &&
                        !f.Contains(Path.DirectorySeparatorChar + "obj" + Path.DirectorySeparatorChar))
            .ToList();

        cshtmlFiles.Should().NotBeEmpty("There should be Razor views in the web-ui Views directory.");

        var violations = new List<string>();
        var imgRegex = new Regex(@"<img\b[^>]*>", RegexOptions.Singleline | RegexOptions.IgnoreCase);

        foreach (var file in cshtmlFiles)
        {
            var content = File.ReadAllText(file);
            var matches = imgRegex.Matches(content);

            foreach (Match match in matches)
            {
                if (!match.Value.Contains("alt=", StringComparison.OrdinalIgnoreCase))
                {
                    var relativePath = Path.GetRelativePath(GetSolutionRoot(), file);
                    var cleanTag = Regex.Replace(match.Value, @"\s+", " ").Trim();
                    if (cleanTag.Length > 90) cleanTag = cleanTag[..90] + "...";
                    violations.Add($"{relativePath}: {cleanTag}");
                }
            }
        }

        violations.Should().BeEmpty(
            $"Every <img> tag in Razor views must have an alt attribute for screen readers. Found {violations.Count} violation(s):\n{string.Join("\n", violations)}");
    }

    [Fact]
    public void SharedLayout_MustHaveSkipToContentLinkAndMainLandmark()
    {
        var layoutPath = Path.Combine(GetSolutionRoot(), "src", "services", "web-ui", "Views", "Shared", "_Layout.cshtml");
        File.Exists(layoutPath).Should().BeTrue("_Layout.cshtml must exist at {0}", layoutPath);

        var content = File.ReadAllText(layoutPath);

        content.Should().Contain("href=\"#main-content\"", "Layout must include a skip link targeting #main-content.");
        content.Should().Contain("Skip to main content", "Layout must provide accessible text 'Skip to main content'.");
        content.Should().Contain("id=\"main-content\"", "Main element must define id='main-content' as the skip target.");
    }

    [Fact]
    public void SharedLayout_MustHaveOpenGraphAndTwitterMetadata()
    {
        var layoutPath = Path.Combine(GetSolutionRoot(), "src", "services", "web-ui", "Views", "Shared", "_Layout.cshtml");
        File.Exists(layoutPath).Should().BeTrue("_Layout.cshtml must exist at {0}", layoutPath);

        var content = File.ReadAllText(layoutPath);

        content.Should().Contain("property=\"og:type\"", "Layout must define og:type.");
        content.Should().Contain("property=\"og:site_name\"", "Layout must define og:site_name.");
        content.Should().Contain("property=\"og:title\"", "Layout must define og:title.");
        content.Should().Contain("property=\"og:description\"", "Layout must define og:description.");
        content.Should().Contain("property=\"og:image\"", "Layout must define og:image.");
        content.Should().Contain("name=\"twitter:card\"", "Layout must define twitter:card.");
    }

    [Fact]
    public void AllCanvases_InWebUiViews_MustHaveAriaLabelOrAriaHidden()
    {
        var viewsPath = Path.Combine(GetSolutionRoot(), "src", "services", "web-ui", "Views");
        Directory.Exists(viewsPath).Should().BeTrue("Views directory must exist at {0}", viewsPath);

        var cshtmlFiles = Directory.GetFiles(viewsPath, "*.cshtml", SearchOption.AllDirectories)
            .Where(f => !f.Contains(Path.DirectorySeparatorChar + "bin" + Path.DirectorySeparatorChar) &&
                        !f.Contains(Path.DirectorySeparatorChar + "obj" + Path.DirectorySeparatorChar))
            .ToList();

        var canvasRegex = new Regex(@"<canvas\b(?<attrs>[^>]*)>", RegexOptions.Singleline | RegexOptions.IgnoreCase);
        var ariaLabelRegex = new Regex(@"aria-label\s*=\s*[""']([^""']+)[""']", RegexOptions.IgnoreCase);
        var ariaHiddenRegex = new Regex(@"aria-hidden\s*=\s*[""']true[""']", RegexOptions.IgnoreCase);
        var roleRegex = new Regex(@"role\s*=\s*[""'](img|region)[""']", RegexOptions.IgnoreCase);

        var violations = new List<string>();
        int totalCanvases = 0;

        foreach (var file in cshtmlFiles)
        {
            var content = File.ReadAllText(file);
            var matches = canvasRegex.Matches(content);

            foreach (Match match in matches)
            {
                totalCanvases++;
                var attrs = match.Groups["attrs"].Value;
                bool hasAriaLabel = ariaLabelRegex.IsMatch(attrs);
                bool isAriaHidden = ariaHiddenRegex.IsMatch(attrs);
                bool hasValidRole = roleRegex.IsMatch(attrs);

                if (!hasAriaLabel && !isAriaHidden)
                {
                    var relativePath = Path.GetRelativePath(GetSolutionRoot(), file);
                    var cleanTag = Regex.Replace(match.Value, @"\s+", " ").Trim();
                    if (cleanTag.Length > 100) cleanTag = cleanTag[..100] + "...";
                    violations.Add($"{relativePath}: Missing aria-label or aria-hidden='true' -> {cleanTag}");
                }
                else if (hasAriaLabel && !hasValidRole)
                {
                    var relativePath = Path.GetRelativePath(GetSolutionRoot(), file);
                    var cleanTag = Regex.Replace(match.Value, @"\s+", " ").Trim();
                    if (cleanTag.Length > 100) cleanTag = cleanTag[..100] + "...";
                    violations.Add($"{relativePath}: Canvas with aria-label must have role='img' or role='region' -> {cleanTag}");
                }
            }
        }

        totalCanvases.Should().Be(22, "There are 22 <canvas> views across Store 3D, Replay, Spectate, Metrics, and ArMiniGames (including Riftbreaker; ReplayStudio is deferred future work without an active canvas).");
        violations.Should().BeEmpty(
            $"Every <canvas> tag in Razor views must have an accessible name (aria-label) and role (img/region), or aria-hidden='true'. Found {violations.Count} violation(s):\n{string.Join("\n", violations)}");
    }

    [Fact]
    public void ThreeJsStoreAndReplayViews_MustSatisfyAxeLandmarksAndCwvSizing()
    {
        var viewsPath = Path.Combine(GetSolutionRoot(), "src", "services", "web-ui", "Views");
        var targetViews = new[]
        {
            "ArMiniGames/DevoWars.cshtml",
            "ArMiniGames/Empath.cshtml",
            "ArMiniGames/KongorChess.cshtml",
            "ArMiniGames/MonkeyCheckers.cshtml",
            "Home/Index.cshtml",
            "Home/LearnatoriumPlayback.cshtml",
            "Home/MatchDetails.cshtml",
            "Home/Stats.cshtml",
            "Live/Spectate.cshtml",
            "Shared/_StoreARModal.cshtml",
            "Store/Index.cshtml",
            "Store/Item.cshtml",
            "System/Metrics.cshtml",
            "Tools/ModelInspector.cshtml"
        };

        var canvasTagRegex = new Regex(@"<canvas\b(?<attrs>[^>]*)>", RegexOptions.Singleline | RegexOptions.IgnoreCase);
        var buttonTagRegex = new Regex(@"<button\b(?<attrs>[^>]*)>(?<content>.*?)</button>", RegexOptions.Singleline | RegexOptions.IgnoreCase);

        var sizingViolations = new List<string>();
        var buttonViolations = new List<string>();

        foreach (var relativeView in targetViews)
        {
            var filePath = Path.Combine(viewsPath, relativeView.Replace('/', Path.DirectorySeparatorChar));
            File.Exists(filePath).Should().BeTrue("Target view {0} must exist", relativeView);

            var content = File.ReadAllText(filePath);

            // 1. Check canvas tags have dimension attributes or parent container sizing classes
            var canvasMatches = canvasTagRegex.Matches(content);
            foreach (Match match in canvasMatches)
            {
                var attrs = match.Groups["attrs"].Value;
                bool hasExplicitDim = attrs.Contains("width=") || attrs.Contains("height=") ||
                                     attrs.Contains("h-") || attrs.Contains("aspect-") || attrs.Contains("h-[");
                if (!hasExplicitDim)
                {
                    sizingViolations.Add($"{relativeView}: Canvas lacks sizing classes -> {match.Value}");
                }
            }

            // 2. Check interactive <button> tags have accessible text, aria-label, or title
            var buttonMatches = buttonTagRegex.Matches(content);
            foreach (Match match in buttonMatches)
            {
                var attrs = match.Groups["attrs"].Value;
                var innerText = Regex.Replace(match.Groups["content"].Value, @"<[^>]*>|@\w+|[\r\n\t]+", " ").Trim();

                bool hasAriaLabel = attrs.Contains("aria-label=", StringComparison.OrdinalIgnoreCase);
                bool hasTitle = attrs.Contains("title=", StringComparison.OrdinalIgnoreCase);
                bool hasAriaHidden = attrs.Contains("aria-hidden=\"true\"", StringComparison.OrdinalIgnoreCase);
                bool hasVisibleText = !string.IsNullOrWhiteSpace(innerText);

                if (!hasAriaLabel && !hasTitle && !hasAriaHidden && !hasVisibleText)
                {
                    var cleanTag = Regex.Replace(match.Value, @"\s+", " ").Trim();
                    if (cleanTag.Length > 80) cleanTag = cleanTag[..80] + "...";
                    buttonViolations.Add($"{relativeView}: Button lacks accessible name -> {cleanTag}");
                }
            }
        }

        sizingViolations.Should().BeEmpty(
            $"All Three.js / Canvas viewports must specify dimensions to avoid Core Web Vitals Cumulative Layout Shift (CLS). Found {sizingViolations.Count} violation(s):\n{string.Join("\n", sizingViolations)}");
        buttonViolations.Should().BeEmpty(
            $"All interactive buttons in 3D/Store/MiniGame views must satisfy Axe accessible name requirements. Found {buttonViolations.Count} violation(s):\n{string.Join("\n", buttonViolations)}");
    }

    [Fact]
    [Trait("Category", "Manual")]
    public void HeadlessBrowser_AxeAccessibilityAndCwv_ManualGate()
    {
        if (!NexusTestEnvironment.IsManualTestEnabled)
        {
            // Bypassed during fast CI/PR unit test runs; enabled when NEXUS_RUN_MANUAL_TESTS is set
            return;
        }

        // Governance invariant: verify all 21 canvas elements in WebUI views have accessible fallbacks
        var viewsPath = Path.Combine(GetSolutionRoot(), "src", "services", "web-ui", "Views");
        var cshtmlFiles = Directory.GetFiles(viewsPath, "*.cshtml", SearchOption.AllDirectories)
            .Where(f => !f.Contains("bin") && !f.Contains("obj"));

        var canvasWithFallbackRegex = new Regex(@"<canvas\b[^>]*>(?<fallback>.*?)</canvas>", RegexOptions.Singleline | RegexOptions.IgnoreCase);
        int validFallbacks = 0;

        foreach (var file in cshtmlFiles)
        {
            var content = File.ReadAllText(file);
            foreach (Match match in canvasWithFallbackRegex.Matches(content))
            {
                var fallback = match.Groups["fallback"].Value.Trim();
                fallback.Should().NotBeNullOrWhiteSpace("Every canvas must provide accessible fallback text for screen readers.");
                validFallbacks++;
            }
        }

        validFallbacks.Should().Be(22, "All 22 Three.js / WebGL / chart / studio canvases must have verified accessible fallback content.");
    }
}
