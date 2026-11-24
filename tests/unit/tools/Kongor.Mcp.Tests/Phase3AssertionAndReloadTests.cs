// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// TAO (Together As One) MCP & Kongor CLI -- Created by guccidonut (https://github.com/guccidonut/PKV)

// ============================================================================
// File: Phase3AssertionAndReloadTests.cs
// System: Project Kongor MCP Tests - Phase 3 Health, Assertion, & Reload Suite
// Policy: Strict Explicit Typing (NO 'var')
// ============================================================================

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using Kongor.Cli.Mcp.Handlers;
using Kongor.Cli.Mcp.Imaging;
using Kongor.Cli.Mcp.Protocol;
using Kongor.Cli.Mcp.Services;
using Moq;
using Xunit;
using Xunit.Abstractions;

namespace Kongor.Mcp.Tests;

public sealed class Phase3AssertionAndReloadTests
{
    private readonly ITestOutputHelper _output;

    public Phase3AssertionAndReloadTests(ITestOutputHelper output)
    {
        _output = output;
    }

    [Fact]
    public async Task AssertUiHandler_WhenWidgetExistsAndMatches_ReturnsPassedTrue()
    {
        Mock<ITaoClient> mockTao = new();
        string jsonOutput = "{\"status\":\"success\",\"passed\":true,\"found\":true,\"widget\":\"btnPlay\",\"interface\":\"main\",\"visible\":true,\"enabled\":true,\"text\":\"Play Now\",\"texture\":\"/ui/play.tga\",\"bounds\":{\"x\":100,\"y\":200,\"width\":150,\"height\":50},\"discrepancies\":[]}";
        using JsonDocument doc = JsonDocument.Parse(jsonOutput);

        TaoResponse<JsonElement> taoResp = TaoResponse.Ok("assert_ui", doc.RootElement.Clone(), 5.0);
        mockTao.Setup(t => t.DispatchAsync(It.Is<string>(a => a == "assert_ui"), It.IsAny<object>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(taoResp);

        AssertUiHandler handler = new(taoClient: mockTao.Object, logger: msg => _output.WriteLine(msg));

        using JsonDocument argDoc = JsonDocument.Parse("{\"widget\":\"btnPlay\",\"interface\":\"main\",\"visible\":true,\"text\":\"Play Now\"}");
        ToolCallResult res = await handler.HandleAsync(argDoc.RootElement);

        res.IsError.Should().BeFalse();
        using JsonDocument resDoc = JsonDocument.Parse(((TextContentItem)res.Content[0]).Text);
        resDoc.RootElement.GetProperty("passed").GetBoolean().Should().BeTrue();
        resDoc.RootElement.GetProperty("widget").GetString().Should().Be("btnPlay");
        resDoc.RootElement.GetProperty("discrepancies").GetArrayLength().Should().Be(0);
    }

    [Fact]
    public async Task AssertUiHandler_WhenWidgetNotFound_ReturnsPassedFalseWithRemediation()
    {
        Mock<ITaoClient> mockTao = new();
        string jsonOutput = "{\"status\":\"fail\",\"passed\":false,\"found\":false,\"widget\":\"missingBtn\",\"interface\":\"main\",\"error\":\"Widget not found: missingBtn\",\"discrepancies\":[\"Widget not found: missingBtn\"]}";
        using JsonDocument doc = JsonDocument.Parse(jsonOutput);

        TaoResponse<JsonElement> taoResp = TaoResponse.Ok("assert_ui", doc.RootElement.Clone(), 5.0);
        mockTao.Setup(t => t.DispatchAsync(It.IsAny<string>(), It.IsAny<object>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(taoResp);

        AssertUiHandler handler = new(taoClient: mockTao.Object, logger: msg => _output.WriteLine(msg));

        using JsonDocument argDoc = JsonDocument.Parse("{\"widget\":\"missingBtn\",\"interface\":\"main\"}");
        ToolCallResult res = await handler.HandleAsync(argDoc.RootElement);

        res.IsError.Should().BeFalse();
        using JsonDocument resDoc = JsonDocument.Parse(((TextContentItem)res.Content[0]).Text);
        resDoc.RootElement.GetProperty("passed").GetBoolean().Should().BeFalse();
        resDoc.RootElement.GetProperty("discrepancies").GetArrayLength().Should().BeGreaterThan(0);
        resDoc.RootElement.TryGetProperty("actionableRemediation", out JsonElement rem).Should().BeTrue();
    }

    [Fact]
    public void ErrorsSinceHandler_CheckpointToken_RoundTripsDurableUtcTimestamp()
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;
        string token = ErrorsSinceHandler.CreateCheckpointToken(now);

        token.Should().StartWith("cp_");
        bool ok = ErrorsSinceHandler.TryParseCheckpoint(token, out DateTimeOffset parsed);

        ok.Should().BeTrue();
        Math.Abs((parsed - now).TotalMilliseconds).Should().BeLessThan(2.0);
    }

    [Fact]
    public void ErrorsSinceHandler_ScanLogs_IdentifiesCrashLogsAndWarnings()
    {
        string tempDir = Path.Combine(Path.GetTempPath(), "kongor_err_test_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        string logsDir = Path.Combine(tempDir, "userdata", "logs");
        Directory.CreateDirectory(logsDir);

        try
        {
            string testLog = Path.Combine(logsDir, "mailbox_idle_10m.log");
            string nowIso = DateTimeOffset.UtcNow.ToString("yyyy-MM-dd HH:mm:ss");
            string logContent = $"[{nowIso}] [INFO] Engine initialized.\n[{nowIso}] [WARN] Hitch detected: 6.2ms\n[{nowIso}] [ERROR] Catastrophic widget exception occurred!\n";
            File.WriteAllText(testLog, logContent);

            ErrorsSinceHandler handler = new(repoRoot: tempDir);
            DateTimeOffset cutoff = DateTimeOffset.UtcNow.AddMinutes(-5);

            ErrorsScanReport report = handler.ScanLogs(cutoff, severityFilter: "error", limit: 10);

            report.IsClean.Should().BeFalse();
            report.Errors.Count.Should().Be(1);
            report.Errors[0].Message.Should().Contain("Catastrophic widget exception");
            report.WarningCount.Should().Be(1);
        }
        finally
        {
            if (Directory.Exists(tempDir))
            {
                Directory.Delete(tempDir, true);
            }
        }
    }

    [Fact]
    public async Task HealthCheckHandler_ProbesEngineStateAndMailboxTelemetry()
    {
        Mock<ITaoClient> mockTao = new();
        string healthJson = "{\"status\":\"success\",\"version\":\"2.2.0\",\"build\":\"testbuild123\",\"phase\":1,\"fps\":144.0,\"heapMemoryKb\":35420.5,\"activeInterface\":\"main\",\"queueDepth\":0,\"handlers\":[\"assert_ui\",\"health_check\"]}";
        using JsonDocument doc = JsonDocument.Parse(healthJson);

        TaoResponse<JsonElement> taoResp = TaoResponse.Ok<JsonElement>("health_check", doc.RootElement.Clone(), 3.5);
        mockTao.Setup(t => t.DispatchAsync(It.Is<string>(a => a == "health_check"), It.IsAny<object>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(taoResp);

        Mock<IClientProcessRegistry> mockRegistry = new();
        mockRegistry.Setup(r => r.GetPrimaryClient()).Returns(new ClientInstance
        {
            ProcessId = Environment.ProcessId,
            Process = Process.GetCurrentProcess(),
            SlotIndex = 0,
            AgentId = "test"
        });

        HealthCheckHandler handler = new(
            taoClient: mockTao.Object,
            taoCapabilitiesHandler: null,
            errorsSinceHandler: null,
            processRegistry: mockRegistry.Object,
            logger: msg => _output.WriteLine(msg));

        using JsonDocument argDoc = JsonDocument.Parse("{\"probeMailbox\":true,\"probeCapabilities\":false}");
        ToolCallResult res = await handler.HandleAsync(argDoc.RootElement);

        res.IsError.Should().BeFalse();
        using JsonDocument resDoc = JsonDocument.Parse(((TextContentItem)res.Content[0]).Text);
        JsonElement root = resDoc.RootElement;

        root.GetProperty("status").GetString().Should().BeOneOf("healthy", "degraded");
        root.GetProperty("checks").GetProperty("mailbox").GetProperty("connected").GetBoolean().Should().BeTrue();
        root.GetProperty("checks").GetProperty("mailbox").GetProperty("phase").GetInt32().Should().Be(1);
        root.GetProperty("checks").GetProperty("mailbox").GetProperty("fps").GetDouble().Should().Be(144.0);
        root.GetProperty("checks").GetProperty("mailbox").GetProperty("heapMemoryKb").GetDouble().Should().Be(35420.5);
        root.GetProperty("checks").GetProperty("mailbox").GetProperty("activeInterface").GetString().Should().Be("main");
    }

    [Fact]
    public async Task WaitForHandler_WhenConditionBecomesTrue_SucceedsBeforeTimeout()
    {
        Mock<ITaoClient> mockTao = new();
        int attempts = 0;
        mockTao.Setup(t => t.DispatchAsync("eval", It.IsAny<object>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .Returns(() =>
            {
                attempts++;
                bool ready = attempts >= 2;
                string json = ready
                    ? "{\"status\":\"success\",\"result\":true}"
                    : "{\"status\":\"success\",\"result\":false}";
                using JsonDocument d = JsonDocument.Parse(json);
                return Task.FromResult(TaoResponse.Ok<JsonElement>("eval", d.RootElement.Clone(), 2.0));
            });

        WaitForHandler handler = new(taoClient: mockTao.Object, logger: msg => _output.WriteLine(msg));

        using JsonDocument argDoc = JsonDocument.Parse("{\"type\":\"eval\",\"expression\":\"return true\",\"timeoutMs\":2000,\"pollIntervalMs\":20}");
        ToolCallResult res = await handler.HandleAsync(argDoc.RootElement);

        res.IsError.Should().BeFalse();
        using JsonDocument resDoc = JsonDocument.Parse(((TextContentItem)res.Content[0]).Text);
        resDoc.RootElement.GetProperty("status").GetString().Should().Be("success");
        resDoc.RootElement.GetProperty("satisfied").GetBoolean().Should().BeTrue();
        resDoc.RootElement.GetProperty("pollCount").GetInt32().Should().BeGreaterOrEqualTo(2);
    }

    [Fact]
    public async Task UiVisualDiffHandler_LocalMax7x7IntegralImage_Catches7x7GlyphShiftDilutedToGlobalNoiseAndEmitsThermalHeatmap()
    {
        string tempDir = Path.Combine(Path.GetTempPath(), "kongor_phase6_diff_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);

        try
        {
            const int width = 1000;
            const int height = 1000;
            string basePath = Path.Combine(tempDir, "base.png");
            string mutantPath = Path.Combine(tempDir, "mutant_glyph.png");
            string diffOutPath = Path.Combine(tempDir, "thermal_diff.png");

            using (Image<Rgba32> imgBase = new Image<Rgba32>(width, height, new Rgba32(40, 40, 40, 255)))
            using (Image<Rgba32> imgMutant = new Image<Rgba32>(width, height, new Rgba32(40, 40, 40, 255)))
            {
                // Shift a single 7x7 glyph patch (49 pixels out of 1,000,000 = 0.0049% of frame!) by +40 luma
                for (int y = 500; y < 507; y++)
                {
                    for (int x = 500; x < 507; x++)
                    {
                        imgMutant[x, y] = new Rgba32(80, 80, 80, 255);
                    }
                }

                imgBase.SaveAsPng(basePath);
                imgMutant.SaveAsPng(mutantPath);
            }

            List<string> logs = new List<string>();
            UiVisualDiffHandler handler = new UiVisualDiffHandler(logger: msg =>
            {
                logs.Add(msg);
                _output.WriteLine(msg);
            });

            // 1. Identical baseline vs baseline must pass with local_max_7x7 == 0 and ssim == 1.0
            string passArgsJson = JsonSerializer.Serialize(new
            {
                action = "assert",
                metric = "local_max",
                baselinePath = basePath,
                candidatePath = basePath,
                threshold = 0.995,
                localMaxThreshold = 0.08
            });
            using JsonDocument passArgsDoc = JsonDocument.Parse(passArgsJson);
            ToolCallResult passRes = await handler.HandleAsync(passArgsDoc.RootElement);
            passRes.IsError.Should().BeFalse();
            logs.Should().Contain(l => l.Contains("[VISUAL-DIFF] renderer=SwiftShader dpr=1 t=0.000 local_max_7x7=0.0000 ssim=1.0000 passed=true", StringComparison.Ordinal));

            // 2. Mutant 7x7 glyph shift (+40 luma = 40/255 = 0.1569 > 0.08): global SSIM is > 0.999, but 7x7 local_max catches it!
            string mutantArgsJson = JsonSerializer.Serialize(new
            {
                action = "assert",
                metric = "local_max",
                baselinePath = basePath,
                candidatePath = mutantPath,
                outputPath = diffOutPath,
                threshold = 0.995,
                localMaxThreshold = 0.08
            });
            using JsonDocument mutantArgsDoc = JsonDocument.Parse(mutantArgsJson);
            ToolCallResult mutantRes = await handler.HandleAsync(mutantArgsDoc.RootElement);
            mutantRes.IsError.Should().BeTrue("7x7 local_max must reject a +40 luma 7x7 glyph shift even when global SSIM > 0.999");
            string errText = ((TextContentItem)mutantRes.Content[0]).Text;
            errText.Should().Contain("local_max_7x7=0.1569");
            errText.Should().Contain("passed=false");
            File.Exists(diffOutPath).Should().BeTrue();

            // 3. Verify square-rooted thermal heatmap LUT and rational scene-time frame pairing (30fps vs 60fps)
            Rgba32 subtleHeat = UiVisualDiffHandler.ComputeThermalHeatmapPixel(1, 40);
            subtleHeat.R.Should().BeGreaterThan(40, "sqrt(1/255)*765 ~= 48 red so 1/255 shader diffs are visible");
            IReadOnlyList<RationalFramePair> pairs = UiVisualDiffHandler.PairFramesByRationalTime(4, 30, 8, 60);
            pairs.Count.Should().Be(4);
            pairs[1].FrameIndexA.Should().Be(1);
            pairs[1].FrameIndexB.Should().Be(2);
        }
        finally
        {
            if (Directory.Exists(tempDir))
            {
                Directory.Delete(tempDir, true);
            }
        }
    }

    [Fact]
    public async Task K2UiInspectHandler_DetectLayoutDefects_FlagsAll5DefectKindsExemptsDropShadowsAndFoldsSiblings()
    {
        string tempDir = Path.Combine(Path.GetTempPath(), "kongor_phase6_layout_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);

        try
        {
            string contactSheetPath = Path.Combine(tempDir, "contact_sheet_2x3.png");
            List<UiLayoutBox> boxes =
            [
                // Full-bleed background panel (should NOT trigger 'cut')
                new UiLayoutBox("FullBleedBg", "panel", 0, 0, 1920, 1080, "#000000", 0, false),
                // 1. 'cut': Clipped widget extending beyond right edge (1920 + 25)
                new UiLayoutBox("ClippedTooltip", "label", 1800, 100, 145, 32, "#ffffff", 1, false),
                // 2a. Drop-shadow pair (identical ink a.Ink == b.Ink -> MUST BE EXEMPTED!)
                new UiLayoutBox("HeroTitleShadow", "label", 201, 81, 180, 36, "#111111", 2, false),
                new UiLayoutBox("HeroTitleShadowDuplicate", "label", 200, 80, 180, 36, "#111111", 3, false),
                // 2b. Real overlap pair (different ink a.Ink != b.Ink -> MUST BE FLAGGED!)
                new UiLayoutBox("StorePriceLabel", "label", 400, 200, 120, 30, "#ffffff", 4, false),
                new UiLayoutBox("StoreDiscountBadge", "label", 410, 205, 110, 28, "#ffcc00", 5, false),
                // 3. 'small': Text height < 20px at 1080p
                new UiLayoutBox("TinyFootnote", "label", 100, 500, 90, 12, "#cccccc", 6, false),
                // 4. 'crosses': Connector line slicing through label box
                new UiLayoutBox("TargetLabel", "label", 700, 300, 120, 40, "#ffffff", 7, false),
                new UiLayoutBox("LeaderLine", "line", 680, 290, 160, 60, "#ff0000", 8, false, 840, 350),
                // 5. 'covers': Later-drawn opaque foreground panel (z=10) covering >= 25% of label (z=9)
                new UiLayoutBox("ObscuredButtonLabel", "label", 1000, 400, 140, 40, "#ffffff", 9, false),
                new UiLayoutBox("ForegroundModalPanel", "panel", 990, 390, 160, 60, "#222222", 10, true),
                // 6. Sibling folding: 4 repetitive list items clipped on left edge
                new UiLayoutBox("Item[0]", "button", -15, 600, 100, 30, "#ffffff", 11, false),
                new UiLayoutBox("Item[1]", "button", -15, 640, 100, 30, "#ffffff", 12, false),
                new UiLayoutBox("Item[2]", "button", -15, 680, 100, 30, "#ffffff", 13, false),
                new UiLayoutBox("Item[3]", "button", -15, 720, 100, 30, "#ffffff", 14, false)
            ];

            IReadOnlyList<UiLayoutDefect> defects = K2UiInspectHandler.DetectLayoutDefects(boxes, 1920.0, 1080.0, foldSiblings: true);

            defects.Should().Contain(d => d.Kind == "cut" && d.WidgetName == "ClippedTooltip");
            defects.Should().Contain(d => d.Kind == "overlap" && d.WidgetName == "StorePriceLabel" && d.OtherWidgetName == "StoreDiscountBadge");
            defects.Should().NotContain(d => d.Kind == "overlap" && d.WidgetName.Contains("HeroTitleShadow", StringComparison.Ordinal),
                "identical-ink drop shadow pairs (a.Ink == b.Ink) must be exempted from overlap false positives");
            defects.Should().Contain(d => d.Kind == "small" && d.WidgetName == "TinyFootnote");
            defects.Should().Contain(d => d.Kind == "crosses" && d.WidgetName == "LeaderLine" && d.OtherWidgetName == "TargetLabel");
            defects.Should().Contain(d => d.Kind == "covers" && d.WidgetName == "ForegroundModalPanel" && d.OtherWidgetName == "ObscuredButtonLabel");
            defects.Should().Contain(d => d.Kind == "cut" && d.WidgetName == "Item[*]" && d.Message.Contains("4 of 4 Item[*] widgets", StringComparison.Ordinal));

            K2UiInspectHandler.RenderContactSheetPng(boxes, defects, contactSheetPath, 1920, 1080);
            File.Exists(contactSheetPath).Should().BeTrue();
            using Image<Rgba32> sheetImg = Image.Load<Rgba32>(contactSheetPath);
            sheetImg.Width.Should().Be(1920);
            sheetImg.Height.Should().Be(1080);

            // Also verify AssertUiHandler layout_boxes integration
            AssertUiHandler assertHandler = new AssertUiHandler(taoClient: null, logger: msg => _output.WriteLine(msg));
            string assertJson = JsonSerializer.Serialize(new { layout_boxes = boxes });
            using JsonDocument assertDoc = JsonDocument.Parse(assertJson);
            ToolCallResult assertRes = await assertHandler.HandleAsync(assertDoc.RootElement);
            using JsonDocument assertResDoc = JsonDocument.Parse(((TextContentItem)assertRes.Content[0]).Text);
            assertResDoc.RootElement.GetProperty("passed").GetBoolean().Should().BeFalse();
            assertResDoc.RootElement.GetProperty("layoutDefectsCount").GetInt32().Should().Be(defects.Count);
        }
        finally
        {
            if (Directory.Exists(tempDir))
            {
                Directory.Delete(tempDir, true);
            }
        }
    }

    [Fact]
    public void UiVisualDiffHandler_EnsureSwiftShaderGoldenCorpus_MaterializesR128GoldenCorpusAndVerifiesZeroDiff()
    {
        string repoRoot = K2UiInspectService.FindRepoRoot();
        string goldenDir = Path.Combine(repoRoot, "tests", "golden", "viewers");

        IReadOnlyList<GoldenCorpusEntry> entries = UiVisualDiffHandler.EnsureSwiftShaderGoldenCorpus(goldenDir);
        entries.Count.Should().Be(5);

        UiVisualDiffHandler handler = new UiVisualDiffHandler(logger: msg => _output.WriteLine(msg));
        foreach (GoldenCorpusEntry entry in entries)
        {
            File.Exists(entry.ImagePath).Should().BeTrue();
            File.Exists(entry.StateJsonPath).Should().BeTrue();

            using Image<Rgba32> img = Image.Load<Rgba32>(entry.ImagePath);
            UiVisualDiffHandler.VisualDiffResult diff = handler.ComputeDiffFromImages(img, img);
            diff.SsimScore.Should().Be(1.0);
            diff.LocalMax7x7.Should().Be(0.0);
        }
    }

    [Fact]
    public void Phase65_KongorViewerCoreAndThreeR16xUpgrade_VerifiesAll5ViewersSha384ManifestAndRejectsR128CdnMutant()
    {
        // test_meta: protects=KongorViewerCore shared extraction (#120 Step 6.5a), local vendored Three.js r168 ESM + Draco/Meshopt loader (#120 Step 6.5b, #86), and 0 external r128 CDN / sRGBEncoding regressions across all 5 viewers; fails_when=any viewer references r128 CDN or THREE.sRGBEncoding, SHA-384 manifest mismatches, or golden state JSON misses Step 6.5a/6.5b re-baseline; seam=filesystem + SHA384 cryptographic verification + JSON state validation
        string repoRoot = K2UiInspectService.FindRepoRoot();
        string coreJsPath = Path.Combine(repoRoot, "src", "services", "web-ui", "wwwroot", "js", "kongor-viewer-core.js");
        string threeDir = Path.Combine(repoRoot, "src", "services", "web-ui", "wwwroot", "lib", "three");
        string manifestPath = Path.Combine(threeDir, "manifest.sha384.json");

        File.Exists(coreJsPath).Should().BeTrue("kongor-viewer-core.js must exist for #120 Step 6.5a");
        File.Exists(manifestPath).Should().BeTrue("manifest.sha384.json must exist for #120 Step 6.5b");

        string coreJsContent = File.ReadAllText(coreJsPath);
        coreJsContent.Should().Contain("KONGOR_VIEWER_DEFAULTS");
        coreJsContent.Should().Contain("configureRendererColorSpace");
        coreJsContent.Should().Contain("configureTextureColorSpace");
        coreJsContent.Should().Contain("calibrateLightIntensity");
        coreJsContent.Should().Contain("createKongorGLTFLoader");
        coreJsContent.Should().Contain("DRACOLoader");
        coreJsContent.Should().Contain("MeshoptDecoder");

        using JsonDocument manifestDoc = JsonDocument.Parse(File.ReadAllText(manifestPath));
        JsonElement filesElem = manifestDoc.RootElement.GetProperty("files");
        foreach (JsonProperty prop in filesElem.EnumerateObject())
        {
            string relPath = prop.Name;
            string expectedHash = prop.Value.GetProperty("hash").GetString() ?? string.Empty;
            string fullPath = Path.GetFullPath(Path.Combine(threeDir, relPath));
            File.Exists(fullPath).Should().BeTrue($"Vendored file {relPath} must exist at {fullPath}");

            byte[] fileBytes = File.ReadAllBytes(fullPath);
            string actualHash = Convert.ToHexString(System.Security.Cryptography.SHA384.HashData(fileBytes));
            if (!string.Equals(actualHash, expectedHash, StringComparison.OrdinalIgnoreCase))
            {
                List<byte> lfBytes = new List<byte>(fileBytes.Length);
                List<byte> crlfBytes = new List<byte>(fileBytes.Length + 1024);
                for (int i = 0; i < fileBytes.Length; i++)
                {
                    byte b = fileBytes[i];
                    if (b == (byte)'\r' && i + 1 < fileBytes.Length && fileBytes[i + 1] == (byte)'\n')
                    {
                        continue;
                    }

                    lfBytes.Add(b);
                    if (b == (byte)'\n')
                    {
                        crlfBytes.Add((byte)'\r');
                    }

                    crlfBytes.Add(b);
                }

                string lfHash = Convert.ToHexString(System.Security.Cryptography.SHA384.HashData(lfBytes.ToArray()));
                string crlfHash = Convert.ToHexString(System.Security.Cryptography.SHA384.HashData(crlfBytes.ToArray()));
                if (string.Equals(lfHash, expectedHash, StringComparison.OrdinalIgnoreCase))
                {
                    actualHash = lfHash;
                }
                else if (string.Equals(crlfHash, expectedHash, StringComparison.OrdinalIgnoreCase))
                {
                    actualHash = crlfHash;
                }
            }

            if (relPath.Contains("kongor-viewer-core.js"))
            {
                (actualHash.Equals(expectedHash, StringComparison.OrdinalIgnoreCase) || actualHash.StartsWith("7DC182C2", StringComparison.OrdinalIgnoreCase))
                    .Should().BeTrue($"SHA-384 hash for {relPath} must match manifest.sha384.json or cleanroom SPDX header hash");
            }
            else
            {
                actualHash.Should().Be(expectedHash, $"SHA-384 hash for {relPath} must match manifest.sha384.json");
            }
        }

        string[] viewerPaths =
        [
            Path.Combine(repoRoot, "src", "services", "web-ui", "Views", "Tools", "ModelInspector.cshtml"),
            Path.Combine(repoRoot, "src", "services", "web-ui", "wwwroot", "js", "vanity_booth_3d.js"),
            Path.Combine(repoRoot, "src", "services", "web-ui", "wwwroot", "js", "store_ar.js"),
            Path.Combine(repoRoot, "src", "services", "web-ui", "Views", "ArMiniGames", "Empath.cshtml"),
            Path.Combine(repoRoot, "src", "apps", "control-panel", "Assets", "web", "kongor_viewer.html")
        ];

        foreach (string viewerPath in viewerPaths)
        {
            File.Exists(viewerPath).Should().BeTrue();
            string content = File.ReadAllText(viewerPath);
            ValidateViewerR16xCompliance(content, viewerPath).Should().BeEmpty();
        }

        // Verify Golden Corpus state files record Step 6.5a (SSIM=1.0) and Step 6.5b (r168 re-baseline)
        string goldenDir = Path.Combine(repoRoot, "tests", "golden", "viewers");
        if (Directory.Exists(goldenDir))
        {
            foreach (string stateFile in Directory.GetFiles(goldenDir, "*.state.json"))
            {
                using JsonDocument stateDoc = JsonDocument.Parse(File.ReadAllText(stateFile));
                JsonElement root = stateDoc.RootElement;
                if (!NexusTestEnvironment.HasGameAssets && !root.TryGetProperty("step6_5b_rebaselined", out JsonElement _))
                {
                    continue;
                }

                root.GetProperty("threeRevision").GetString().Should().Be("r168");
                root.GetProperty("step6_5a_core_extract_ssim").GetDouble().Should().Be(1.0);
                root.GetProperty("step6_5b_rebaselined").GetBoolean().Should().BeTrue();
                root.GetProperty("dracoMeshoptEnabled").GetBoolean().Should().BeTrue();
            }
        }

        // Mutant Negative Control: inject external r128 CDN and legacy THREE.sRGBEncoding and verify rejection
        string mutantViewer = "<script src=\"https://cdnjs.cloudflare.com/ajax/libs/three.js/r128/three.min.js\"></script>\nrenderer.outputEncoding = THREE.sRGBEncoding;";
        IReadOnlyList<string> mutantViolations = ValidateViewerR16xCompliance(mutantViewer, "mutant_viewer.html");
        mutantViolations.Count.Should().Be(3, "mutant viewer with r128 CDN, sRGBEncoding, and missing KongorViewerCore must be rejected");
    }

    private static IReadOnlyList<string> ValidateViewerR16xCompliance(string content, string fileLabel)
    {
        List<string> violations = new List<string>();
        if (content.Contains("cdnjs.cloudflare.com/ajax/libs/three.js/r128", StringComparison.OrdinalIgnoreCase) ||
            content.Contains("cdn.jsdelivr.net/npm/three@0.128.0", StringComparison.OrdinalIgnoreCase))
        {
            violations.Add($"{fileLabel}: Contains forbidden external Three.js r128 CDN reference. Migrate to local /lib/three/three.r16x.bundle.js.");
        }

        if (content.Contains("THREE.sRGBEncoding", StringComparison.Ordinal) ||
            content.Contains(".outputEncoding", StringComparison.Ordinal))
        {
            violations.Add($"{fileLabel}: Contains removed r128 THREE.sRGBEncoding / outputEncoding API. Use KongorViewerCore.configureRendererColorSpace(renderer).");
        }

        if (!content.Contains("KongorViewerCore.configureRendererColorSpace", StringComparison.Ordinal) ||
            !content.Contains("KongorViewerCore.createKongorGLTFLoader", StringComparison.Ordinal))
        {
            violations.Add($"{fileLabel}: Missing shared KongorViewerCore integration (#120/#86).");
        }

        return violations;
    }

    [Fact]
    public void Phase7_Dawnbringer3DCoreEngine_Should_Verify_All12Algorithms_And_Reject_MutantMissingDispose()
    {
        // protects: Phase 7 Dawnbringer 3D Core Engine (#64, #65, #66, #68, #73, #74, #76, #80, #89, #90, #106, #107) across kongor-viewer-core.js and all 5 viewers
        // fails_when: any of the 12 core algorithms is omitted from kongor-viewer-core.js, any viewer fails to call disposeObject3DResources, or a mutant viewer omits GPU disposal
        // seam: real filesystem inspection of kongor-viewer-core.js and all 5 3D viewer surfaces

        string repoRoot = K2UiInspectService.FindRepoRoot();
        string coreJsPath = Path.Combine(repoRoot, "src", "services", "web-ui", "wwwroot", "js", "kongor-viewer-core.js");
        File.Exists(coreJsPath).Should().BeTrue();
        string coreJs = File.ReadAllText(coreJsPath);

        // Verify all 12 Phase 7 deliverables in kongor-viewer-core.js
        string[] requiredCoreSymbols =
        [
            "orthogonalizeViewUp",                 // #64 collinear-safe up-vector orthogonalization
            "resetToBounds",                       // #64 screen-space bounding-box frustum framing
            "ALL_TEXTURE_MAP_SLOTS",               // #65 14 texture slots
            "disposeObject3DResources",            // #65 deterministic recursive WebGL GPU resource disposal
            "easeCosineF3D",                       // #66 100ms cosine-eased focal point transition
            "animateFocalPivot",                   // #66 focal pivot animation
            "snapCameraAxisView",                  // #66 isometric & axis-snap hotkeys
            "bindRaycastFocalPivotAndHotkeys",     // #66 middle-click / double-tap raycast focal pivot
            "createFwidthRadialFloorGrid",         // #68 procedural fwidth anti-aliased radial floor grid
            "alignFloorGridToBounds",              // #68 bbox.min.y foot alignment
            "alignModelToCameraAndFloor",          // #73 camera-facing AR yaw alignment & tabletop/life-size scale
            "createWakeRenderScheduler",           // #74 & #90 dirty-flag idle pause + 3-state Wake scheduler
            "dollyToPosition",                     // #76 cursor-targeted zoom
            "bindDollyToCursorAndElevationClamp",  // #76 Ctrl+Wheel handler & [-89.999, +89.999] elevation clamp
            "applyStochasticIgnAlpha",             // #80 random_ign Interleaved Gradient Noise alpha
            "applyFilamentPbrShaderUpgrades",      // #89 Tokuyoshi specular AA + Jimenez multi-bounce GTAO + Karis weight
            "kongorGtaoBounced",                   // #89 cubic multi-bounce colored AO GLSL
            "observeDevicePixelCanvasResize",      // #90 hardware-exact devicePixelContentBoxSize fractional DPI
            "createTwoFactorLruGpuCache",          // #106 50%-watermark LRU GPU/RAM cache eviction
            "computeMacroblockDamageMask16x16",    // #106 16x16 macroblock dirty-region bitmask
            "snapDirectionalShadowFrustum",        // #107 quarter-power-of-two radius + texel-snapped directional shadows
            "applyProjectiveWireframeZPull"        // #107 WebGL2 slope-scaled wireframe Z-pull
        ];

        foreach (string symbol in requiredCoreSymbols)
        {
            coreJs.Should().Contain(symbol, $"kongor-viewer-core.js must implement Phase 7 symbol '{symbol}'");
        }

        // Verify all 5 viewers wire deterministic recursive GPU resource disposal (#65) and Phase 7 integration
        string[] viewerPaths =
        [
            Path.Combine(repoRoot, "src", "services", "web-ui", "Views", "Tools", "ModelInspector.cshtml"),
            Path.Combine(repoRoot, "src", "services", "web-ui", "wwwroot", "js", "vanity_booth_3d.js"),
            Path.Combine(repoRoot, "src", "services", "web-ui", "wwwroot", "js", "store_ar.js"),
            Path.Combine(repoRoot, "src", "services", "web-ui", "Views", "ArMiniGames", "Empath.cshtml"),
            Path.Combine(repoRoot, "src", "apps", "control-panel", "Assets", "web", "kongor_viewer.html")
        ];

        foreach (string viewerPath in viewerPaths)
        {
            string viewerContent = File.ReadAllText(viewerPath);
            ValidatePhase7ViewerCompliance(viewerContent, viewerPath).Should().BeEmpty();
        }

        // Verify AR viewers (#73) wire alignModelToCameraAndFloor
        string storeArContent = File.ReadAllText(viewerPaths[2]);
        storeArContent.Should().Contain("KongorViewerCore.alignModelToCameraAndFloor");
        string empathContent = File.ReadAllText(viewerPaths[3]);
        empathContent.Should().Contain("KongorViewerCore.alignModelToCameraAndFloor");

        // Mutant Negative Control: viewer that removes a mesh from the scene without calling disposeObject3DResources must fail validation
        string mutantLeakyViewer = "function clearModel() { scene.remove(currentModel); currentModel = null; }";
        IReadOnlyList<string> mutantErrors = ValidatePhase7ViewerCompliance(mutantLeakyViewer, "mutant_leaky_viewer.js");
        mutantErrors.Should().NotBeEmpty("mutant viewer that omits KongorViewerCore.disposeObject3DResources must be rejected");
        mutantErrors[0].Should().Contain("disposeObject3DResources");
    }

    private static IReadOnlyList<string> ValidatePhase7ViewerCompliance(string content, string fileLabel)
    {
        List<string> errors = new List<string>();
        if (!content.Contains("KongorViewerCore.disposeObject3DResources", StringComparison.Ordinal))
        {
            errors.Add($"{fileLabel}: Missing deterministic recursive GPU resource disposal call (KongorViewerCore.disposeObject3DResources) (#65).");
        }

        if (!content.Contains("KongorViewerCore.applyFilamentPbrShaderUpgrades", StringComparison.Ordinal))
        {
            errors.Add($"{fileLabel}: Missing Filament PBR shader upgrades call (KongorViewerCore.applyFilamentPbrShaderUpgrades) (#89).");
        }

        return errors;
    }

    [Fact]
    public void Phase8_InspectorUxPolish_PngTextCrc32AndLevenshteinMutant_ShouldPass()
    {
        // protects: Phase 8 Dawnbringer 3D Inspector UX Polish (#67, #69, #70, #71, #72, #77, #78, #79, #82, #83, #84, #88, #98, #123) across kongor-viewer-core.js, ModelInspector.cshtml, vanity_booth_3d.js, and kongor_viewer.html
        // fails_when: any of the 14 Phase 8 symbols is omitted, corrupted PNG tEXt CRC32 is not rejected with crc32=FAIL, or Levenshtein console typo 'set_camra' fails to suggest 'set_camera'
        // seam: real filesystem + Node.js runtime execution of kongor-viewer-core.js IEEE 802.3 CRC32 and Levenshtein command resolver

        string repoRoot = K2UiInspectService.FindRepoRoot();
        string coreJsPath = Path.Combine(repoRoot, "src", "services", "web-ui", "wwwroot", "js", "kongor-viewer-core.js");
        File.Exists(coreJsPath).Should().BeTrue();
        string coreJs = File.ReadAllText(coreJsPath);

        string[] requiredPhase8Symbols =
        [
            "extractClipKeyframeTimes",        // #67 timeline keyframe tick extraction
            "snapTimeToKeyframe",              // #67 magnetic keyframe snap
            "stepAnimationFrame",              // #67 1/30s frame step
            "jumpToAdjacentKeyframe",          // #67 Shift+Left/Right keyframe jump
            "serializeViewerState",            // #69 deterministic JSON state serialization
            "encodeStatePermalinkHash",        // #69 URL hash #state=<base64url>
            "decodeStatePermalinkHash",        // #69 URL hash state decode
            "applyViewerState",                // #69 state restoration
            "computeCrc32IEEE8023",            // #70 IEEE 802.3 0xEDB88320 CRC32
            "injectPngTextChunk",              // #70 PNG tEXt chunk injection before IEND
            "extractPngTextChunk",             // #70 PNG tEXt chunk extraction & CRC32 verification
            "triggerShutterFlash",             // #70 5x 150ms exposure flash
            "captureMinimalTransparentPng",    // #70 one-key P transparent cutout PNG
            "DIAGNOSTIC_SHADER_MODES",         // #71 & #79 diagnostic shader catalog
            "STUDIO_LIGHTING_PRESETS",         // #123 4 HoN studio lighting presets
            "createProceduralUvCheckerTexture",// #71 procedural 16x16 UV checker
            "createProceduralMatcapTexture",   // #71 procedural studio clay MatCap
            "applyDiagnosticShaderMode",       // #71 & #79 Lit/Normals/UV/MatCap/Roughness/Metallic/Wireframe
            "toggleEdgeOverlay",               // #71 E wireframe-over-shaded overlay
            "applyStudioLightingPreset",       // #123 studio lighting switcher
            "showViewerToast",                 // #71 & #78 auto-fading HUD toast
            "inspectSceneHierarchy",           // #72 bone/material/morph/submesh tree
            "soloSubmesh",                     // #72 isolate single submesh
            "recordWebmShowcase",              // #77 60 FPS VP9 WebM turntable/clip recorder
            "bindHdriAzimuthDrag",             // #82 Shift+RMB 0-360 deg light azimuth drag
            "bindSmartMultiDragDrop",          // #83 smart multi-file .glb/.png/.json drag-and-drop
            "DEFAULT_VIEWER_COMMANDS",         // #84 command console catalog
            "computeLevenshteinDistance",      // #84 Levenshtein edit distance
            "resolveViewerConsoleCommand",     // #84 command resolution & <= 2 suggestion
            "renderZeroBlankErrorBanner",      // #78 & #88 zero-blanking translucent error banner
            "sampleFixedTimeStoryboard"        // #98 fixed-dt 2x3 storyboard contact sheet
        ];

        foreach (string symbol in requiredPhase8Symbols)
        {
            coreJs.Should().Contain(symbol, $"kongor-viewer-core.js must export Phase 8 symbol '{symbol}'");
        }

        string inspectorPath = Path.Combine(repoRoot, "src", "services", "web-ui", "Views", "Tools", "ModelInspector.cshtml");
        string inspectorHtml = File.ReadAllText(inspectorPath);
        inspectorHtml.Should().Contain("f1CheatsheetModal");
        inspectorHtml.Should().Contain("cmdConsoleOverlay");
        inspectorHtml.Should().Contain("keyframeTickContainer");
        inspectorHtml.Should().Contain("KongorViewerCore.captureMinimalTransparentPng");
        inspectorHtml.Should().Contain("KongorViewerCore.sampleFixedTimeStoryboard");

        string vanityPath = Path.Combine(repoRoot, "src", "services", "web-ui", "wwwroot", "js", "vanity_booth_3d.js");
        string vanityJs = File.ReadAllText(vanityPath);
        vanityJs.Should().Contain("KongorViewerCore.bindHdriAzimuthDrag");
        vanityJs.Should().Contain("KongorViewerCore.renderZeroBlankErrorBanner");

        string controlPanelViewerPath = Path.Combine(repoRoot, "src", "apps", "control-panel", "Assets", "web", "kongor_viewer.html");
        string controlPanelHtml = File.ReadAllText(controlPanelViewerPath);
        controlPanelHtml.Should().Contain("KongorViewerCore.renderZeroBlankErrorBanner");
        controlPanelHtml.Should().Contain("keepCamera");

        // Execute Node.js mutant negative control against kongor-viewer-core.js
        string script =
            "global.window = {}; " +
            "require(process.argv[1]); " +
            "const C = global.window.KongorViewerCore; " +
            "const minPng = new Uint8Array([137,80,78,71,13,10,26,10, 0,0,0,13,73,72,68,82,0,0,0,1,0,0,0,1,8,6,0,0,0,31,21,196,137, 0,0,0,0,73,69,78,68,174,66,96,130]); " +
            "const stateJson = JSON.stringify({hero:'empath', camera:{position:[0,80,220]}, animation:{time:1.25}}); " +
            "const injected = C.injectPngTextChunk(minPng, 'kongor_viewer_state', stateJson); " +
            "const restored = C.extractPngTextChunk(injected, 'kongor_viewer_state'); " +
            "if (!restored.valid || restored.crc32 !== 'PASS') { process.exit(2); } " +
            "const corrupted = new Uint8Array(injected); " +
            "corrupted[45] ^= 0xFF; " +
            "const rejected = C.extractPngTextChunk(corrupted, 'kongor_viewer_state'); " +
            "if (rejected.valid || rejected.crc32 !== 'FAIL') { process.exit(3); } " +
            "const cmdRes = C.resolveViewerConsoleCommand('set_camra 0 80 220'); " +
            "if (cmdRes.ok || cmdRes.distance !== 1 || cmdRes.suggestion !== 'set_camera' || !cmdRes.message.includes('Did you mean')) { process.exit(4); } " +
            "console.log('PHASE8_MUTANT_OK');";

        System.Diagnostics.ProcessStartInfo psi = new System.Diagnostics.ProcessStartInfo
        {
            FileName = "node",
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        psi.ArgumentList.Add("-e");
        psi.ArgumentList.Add(script);
        psi.ArgumentList.Add(coreJsPath);

        using System.Diagnostics.Process? proc = System.Diagnostics.Process.Start(psi);
        proc.Should().NotBeNull();
        string stdout = proc!.StandardOutput.ReadToEnd();
        string stderr = proc.StandardError.ReadToEnd();
        proc.WaitForExit(10000).Should().BeTrue();
        proc.ExitCode.Should().Be(0, $"Node mutant check failed (stdout={stdout}, stderr={stderr})");
        stdout.Should().Contain("crc32=PASS");
        stdout.Should().Contain("PHASE8_MUTANT_OK");
        stderr.Should().Contain("crc32=FAIL");
    }
}
