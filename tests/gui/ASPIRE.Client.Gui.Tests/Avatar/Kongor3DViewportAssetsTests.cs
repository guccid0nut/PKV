// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

using System.Text.Json;
using System.Text.RegularExpressions;
using FluentAssertions;
using NEXUS.ControlPanel.GUI.Controls;
using Xunit;

namespace ASPIRE.Tests.Avatar;

public sealed class Kongor3DViewportAssetsTests
{
    [Fact]
    public void KongorViewerHtml_AllVirtualHostAssetUrls_ShouldResolveInBuiltAssetsDirectory()
    {
        // protects: standalone WebView2 https://nexus.local virtual-host asset contract for the bottom-left 3D Empath viewport
        // fails_when: any <script src>, importmap module, Draco decoder, empath_cn.glb model, or voice line referenced by kongor_viewer.html is missing from Assets/
        // seam: real MSBuild-staged AppContext.BaseDirectory/Assets directory + RFC 3986 Uri resolution from https://nexus.local/web/kongor_viewer.html

        string assetsDir = Kongor3DViewportControl.ResolveAndPrepareAssetsDirectory(AppContext.BaseDirectory);
        Directory.Exists(assetsDir).Should().BeTrue("NEXUS.ControlPanel.GUI must stage an Assets directory alongside its binaries");

        foreach (string relPath in Kongor3DViewportControl.RequiredViewerAssetRelativePaths)
        {
            string fullPath = Path.Combine(assetsDir, relPath);
            File.Exists(fullPath).Should().BeTrue($"required 3D viewport asset '{relPath}' must exist in '{assetsDir}'");
            new FileInfo(fullPath).Length.Should().BeGreaterThan(0, $"required 3D viewport asset '{relPath}' must be non-empty");
        }

        string viewerHtmlPath = Path.Combine(assetsDir, "web", "kongor_viewer.html");
        string html = File.ReadAllText(viewerHtmlPath);

        // Guard against dead HTML5 <script onerror="this.src='../../../../...'"> path-traversal fallback
        html.Should().NotContain("onerror=\"this.src=",
            "mutating script.src inside onerror does not re-execute in HTML5 and cannot escape the nexus.local virtual host root");

        // Guard 4-argument observeDevicePixelCanvasResize(container, canvas, renderer, camera) call
        Regex.IsMatch(
            html,
            @"KongorViewerCore\.observeDevicePixelCanvasResize\s*\(\s*container\s*,\s*canvas\s*,\s*renderer\s*,\s*camera")
            .Should().BeTrue("observeDevicePixelCanvasResize requires (container, canvas, renderer, camera)");

        Uri pageUri = new("https://nexus.local/web/kongor_viewer.html");

        // 1. Verify every <script src="..."> resolves inside assetsDir
        MatchCollection scriptSrcMatches = Regex.Matches(html, @"<script[^>]*\ssrc\s*=\s*[""']([^""']+)[""']", RegexOptions.IgnoreCase);
        scriptSrcMatches.Count.Should().BeGreaterThanOrEqualTo(2, "kongor_viewer.html must load Three.js r168 bundle and kongor-viewer-core.js");
        foreach (Match match in scriptSrcMatches)
        {
            AssertVirtualHostUrlExists(assetsDir, pageUri, match.Groups[1].Value);
        }

        // 2. Verify every importmap target resolves inside assetsDir
        Match importMapMatch = Regex.Match(html, @"<script\s+type=[""']importmap[""']\s*>\s*(\{[\s\S]*?\})\s*</script>", RegexOptions.IgnoreCase);
        importMapMatch.Success.Should().BeTrue("kongor_viewer.html must declare a Three.js r168 importmap");
        using (JsonDocument importDoc = JsonDocument.Parse(importMapMatch.Groups[1].Value))
        {
            JsonElement imports = importDoc.RootElement.GetProperty("imports");
            foreach (JsonProperty prop in imports.EnumerateObject())
            {
                string? targetUrl = prop.Value.GetString();
                targetUrl.Should().NotBeNullOrWhiteSpace();
                AssertVirtualHostUrlExists(assetsDir, pageUri, targetUrl!);
            }
        }

        // 3. Verify optional Empath GLB model and all 22 voice lines when local overlay is staged;
        //    in cleanroom source-only checkouts without games/control-panel-overlay, HasHeroModelAsset is false
        //    and Kongor3DViewportControl automatically activates the native SkiaCrestCanvas 2D emblem.
        if (Kongor3DViewportControl.HasHeroModelAsset(AppContext.BaseDirectory))
        {
            AssertVirtualHostUrlExists(assetsDir, pageUri, "../models/empath_cn.glb");

            MatchCollection voiceMatches = Regex.Matches(html, @"'(\.\./sounds/empath/[^']+\.ogg)'");
            voiceMatches.Count.Should().BeGreaterThanOrEqualTo(20, "kongor_viewer.html must wire Empath voice lines");
            foreach (Match voiceMatch in voiceMatches)
            {
                AssertVirtualHostUrlExists(assetsDir, pageUri, voiceMatch.Groups[1].Value);
            }
        }
    }

    [Fact]
    public void ResolveAndPrepareAssetsDirectory_InCleanroomWithoutHeroGlb_ShouldStageCoreJsAndDetectCleanroom()
    {
        // protects: cleanroom GitHub checkouts where games/control-panel-overlay is absent
        // fails_when: ResolveAndPrepareAssetsDirectory throws or fails to stage Three.js + kongor-viewer-core.js without empath_cn.glb
        // seam: isolated temporary directory without any .glb or .ogg retail assets

        string scratchBase = Path.Combine(
            Path.GetTempPath(),
            "kongor3d_cleanroom_" + Guid.NewGuid().ToString("N"));
        string scratchAssets = Path.Combine(scratchBase, "Assets");

        try
        {
            Directory.CreateDirectory(Path.Combine(scratchAssets, "web"));
            string srcHtml = Path.Combine(AppContext.BaseDirectory, "Assets", "web", "kongor_viewer.html");
            File.Copy(srcHtml, Path.Combine(scratchAssets, "web", "kongor_viewer.html"));

            // Copy Three.js & kongor-viewer-core.js from built Assets to simulate cleanroom MSBuild output
            string builtAssets = Path.Combine(AppContext.BaseDirectory, "Assets");
            foreach (string relPath in Kongor3DViewportControl.RequiredViewerAssetRelativePaths)
            {
                string destFile = Path.Combine(scratchAssets, relPath);
                string? destDir = Path.GetDirectoryName(destFile);
                if (!string.IsNullOrEmpty(destDir)) Directory.CreateDirectory(destDir);
                if (!File.Exists(destFile))
                {
                    File.Copy(Path.Combine(builtAssets, relPath), destFile);
                }
            }

            string resolved = Kongor3DViewportControl.ResolveAndPrepareAssetsDirectory(scratchBase, allowRepoOverlaySync: false);
            resolved.Should().Be(scratchAssets);
            Kongor3DViewportControl.HasHeroModelAsset(scratchBase, allowRepoOverlaySync: false).Should().BeFalse();

            foreach (string relPath in Kongor3DViewportControl.RequiredViewerAssetRelativePaths)
            {
                File.Exists(Path.Combine(resolved, relPath)).Should().BeTrue();
            }
        }
        finally
        {
            if (Directory.Exists(scratchBase))
            {
                Directory.Delete(scratchBase, recursive: true);
            }
        }
    }

    [Fact]
    public void ResolveAndPrepareAssetsDirectory_WhenJsBundlesMissingInScratchDir_ShouldSelfHealFromRepoWwwroot()
    {
        // protects: dev-mode self-healing of Three.js r168 and kongor-viewer-core.js when running against an incomplete Assets directory
        // fails_when: ResolveAndPrepareAssetsDirectory leaves /lib/three or /js/kongor-viewer-core.js missing or mutates outside the scratch base directory
        // seam: isolated temporary directory under repo build tree with real file I/O

        string scratchBase = Path.Combine(
            AppContext.BaseDirectory,
            "_test_kongor3d_selfheal_" + Guid.NewGuid().ToString("N"));
        string scratchAssets = Path.Combine(scratchBase, "Assets");

        try
        {
            Directory.CreateDirectory(Path.Combine(scratchAssets, "web"));
            string srcHtml = Path.Combine(AppContext.BaseDirectory, "Assets", "web", "kongor_viewer.html");
            File.Copy(srcHtml, Path.Combine(scratchAssets, "web", "kongor_viewer.html"));

            // Place empath_cn.glb only under web/ to also verify models/empath_cn.glb fallback copy
            File.WriteAllBytes(Path.Combine(scratchAssets, "web", "empath_cn.glb"), [0x67, 0x6C, 0x54, 0x46]);

            string resolved = Kongor3DViewportControl.ResolveAndPrepareAssetsDirectory(scratchBase);
            resolved.Should().Be(scratchAssets);

            foreach (string relPath in Kongor3DViewportControl.RequiredViewerAssetRelativePaths)
            {
                File.Exists(Path.Combine(resolved, relPath))
                    .Should().BeTrue($"self-heal must populate '{relPath}' in '{resolved}'");
            }
        }
        finally
        {
            if (Directory.Exists(scratchBase))
            {
                Directory.Delete(scratchBase, recursive: true);
            }
        }
    }

    private static void AssertVirtualHostUrlExists(string assetsDir, Uri pageUri, string rawRelativeOrRootUrl)
    {
        Uri resolved = new(pageUri, rawRelativeOrRootUrl);
        resolved.Host.Should().Be("nexus.local", $"asset URL '{rawRelativeOrRootUrl}' must resolve on https://nexus.local");

        string relativeDiskPath = resolved.AbsolutePath.TrimStart('/')
            .Replace('/', Path.DirectorySeparatorChar);
        string fullDiskPath = Path.Combine(assetsDir, relativeDiskPath);

        File.Exists(fullDiskPath).Should().BeTrue(
            $"URL '{rawRelativeOrRootUrl}' -> '{resolved}' must map to existing file '{fullDiskPath}'");
        new FileInfo(fullDiskPath).Length.Should().BeGreaterThan(0,
            $"mapped file '{fullDiskPath}' must be non-empty");
    }
}
