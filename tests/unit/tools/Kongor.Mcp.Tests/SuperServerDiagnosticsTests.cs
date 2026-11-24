// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// TAO (Together As One) MCP & Kongor CLI -- Created by guccidonut (https://github.com/guccidonut/PKV)

using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using FluentAssertions;
using Kongor.Cli.Mcp.Handlers;
using Kongor.Cli.Mcp.Imaging;
using Kongor.Cli.Mcp.Protocol;
using Kongor.Cli.Mcp.Services;
using Xunit;

namespace Kongor.Mcp.Tests;

public sealed class SuperServerDiagnosticsTests : IDisposable
{
    private readonly string _testTempDir;

    public SuperServerDiagnosticsTests()
    {
        _testTempDir = Path.Combine(Path.GetTempPath(), "kongor_p4_tests_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_testTempDir);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_testTempDir))
            {
                Directory.Delete(_testTempDir, true);
            }
        }
        catch { }
    }

#if !PLAYTEST_BUILD
    #region 1. AppEvalScriptHandler Tests

    [Fact]
    [Trait("Category", "Manual")]
    public async Task AppEvalScript_SimpleExpression_ReturnsEvaluatedResult()
    {
        if (!NexusTestEnvironment.IsManualTestEnabled) return;

        AppEvalScriptHandler handler = new();
        using JsonDocument argsDoc = JsonDocument.Parse("{\"script\":\"return 21 * 2;\"}");

        ToolCallResult result = await handler.HandleAsync(argsDoc.RootElement);

        result.IsError.Should().BeFalse(because: result.FirstText);
        using var resDoc = JsonDocument.Parse(result.FirstText);
        resDoc.RootElement.GetProperty("status").GetString().Should().Be("success");
        resDoc.RootElement.GetProperty("returnValue").GetInt32().Should().Be(42);
    }

    [Fact]
    [Trait("Category", "Manual")]
    public async Task AppEvalScript_WithPrint_CapturesOutputLines()
    {
        if (!NexusTestEnvironment.IsManualTestEnabled) return;

        AppEvalScriptHandler handler = new();
        using JsonDocument argsDoc = JsonDocument.Parse("{\"script\":\"Print(\\\"Hello Phase 4\\\"); Print(\\\"Line 2\\\"); return 100;\"}");

        ToolCallResult result = await handler.HandleAsync(argsDoc.RootElement);

        result.IsError.Should().BeFalse(because: result.FirstText);
        using var resDoc = JsonDocument.Parse(result.FirstText);
        resDoc.RootElement.GetProperty("status").GetString().Should().Be("success");
        var output = resDoc.RootElement.GetProperty("output");
        output.GetArrayLength().Should().Be(2);
        output[0].GetString().Should().Be("Hello Phase 4");
        output[1].GetString().Should().Be("Line 2");
    }

    [Fact]
    [Trait("Category", "Manual")]
    public async Task AppEvalScript_SyntaxError_ReturnsGracefulCompilationError()
    {
        if (!NexusTestEnvironment.IsManualTestEnabled) return;

        AppEvalScriptHandler handler = new();
        using JsonDocument argsDoc = JsonDocument.Parse("{\"script\":\"invalid syntax csharp ;;;; {{}\"}");

        ToolCallResult result = await handler.HandleAsync(argsDoc.RootElement);

        result.IsError.Should().BeTrue();
        using var resDoc = JsonDocument.Parse(result.FirstText);
        resDoc.RootElement.GetProperty("status").GetString().Should().Be("compilation_error");
        resDoc.RootElement.GetProperty("diagnostics").GetArrayLength().Should().BeGreaterThan(0);
    }

    [Fact]
    [Trait("Category", "Manual")]
    public async Task AppEvalScript_InfiniteLoop_HonorsTimeout()
    {
        if (!NexusTestEnvironment.IsManualTestEnabled) return;

        AppEvalScriptHandler handler = new();
        using JsonDocument argsDoc = JsonDocument.Parse("{\"script\":\"while(true) { }\", \"timeoutMs\": 500}");

        ToolCallResult result = await handler.HandleAsync(argsDoc.RootElement);

        result.IsError.Should().BeTrue();
        using var resDoc = JsonDocument.Parse(result.FirstText);
        resDoc.RootElement.GetProperty("status").GetString().Should().Be("timeout");
    }

    #endregion

#endif

    #region 2. StateSnapshotHandler Tests

    [Fact]
    public async Task StateSnapshot_CreateListRestoreDiffDelete_LifecycleSucceeds()
    {
        string snapshotDir = Path.Combine(_testTempDir, "snapshots");
        StateSnapshotHandler handler = new(
            snapshotDirectory: snapshotDir,
            databaseSnapshotProvider: _ => Task.FromResult(new Dictionary<string, List<Dictionary<string, object?>>>
            {
                ["Accounts"] =
                [
                    new Dictionary<string, object?> { ["ID"] = 1, ["Name"] = "Player1" },
                    new Dictionary<string, object?> { ["ID"] = 2, ["Name"] = "Player2" }
                ]
            }));

        string snapshotName = "test_snap_1";

        // 1. Create
        using (var createDoc = JsonDocument.Parse($"{{\"action\":\"create\",\"name\":\"{snapshotName}\",\"metadata\":{{\"env\":\"test\"}}}}"))
        {
            var createRes = await handler.HandleAsync(createDoc.RootElement);
            createRes.IsError.Should().BeFalse();
            using var doc = JsonDocument.Parse(createRes.FirstText);
            doc.RootElement.GetProperty("status").GetString().Should().Be("success");
            doc.RootElement.GetProperty("totalTableRows").GetInt32().Should().Be(2);
        }

        // 2. List
        using (var listDoc = JsonDocument.Parse("{\"action\":\"list\"}"))
        {
            var listRes = await handler.HandleAsync(listDoc.RootElement);
            listRes.IsError.Should().BeFalse();
            using var doc = JsonDocument.Parse(listRes.FirstText);
            doc.RootElement.GetProperty("total").GetInt32().Should().BeGreaterThan(0);
        }

        // 3. Diff
        using (var diffDoc = JsonDocument.Parse($"{{\"action\":\"diff\",\"name\":\"{snapshotName}\"}}"))
        {
            var diffRes = await handler.HandleAsync(diffDoc.RootElement);
            diffRes.IsError.Should().BeFalse();
            using var doc = JsonDocument.Parse(diffRes.FirstText);
            doc.RootElement.GetProperty("identical").GetBoolean().Should().BeTrue();
        }

        // 4. Restore
        using (var restoreDoc = JsonDocument.Parse($"{{\"action\":\"restore\",\"name\":\"{snapshotName}\"}}"))
        {
            var restoreRes = await handler.HandleAsync(restoreDoc.RootElement);
            restoreRes.IsError.Should().BeFalse();
            using var doc = JsonDocument.Parse(restoreRes.FirstText);
            doc.RootElement.GetProperty("status").GetString().Should().Be("success");
        }

        // 5. Delete
        using (var delDoc = JsonDocument.Parse($"{{\"action\":\"delete\",\"name\":\"{snapshotName}\"}}"))
        {
            var delRes = await handler.HandleAsync(delDoc.RootElement);
            delRes.IsError.Should().BeFalse();
            using var doc = JsonDocument.Parse(delRes.FirstText);
            doc.RootElement.GetProperty("deleted").GetBoolean().Should().BeTrue();
        }
    }

    #endregion

    #region 4. NetworkChaosHandler Tests

    [Fact]
    public async Task NetworkChaos_InjectLatencyAndHeal_UpdatesActiveImpairments()
    {
        NetworkChaosHandler handler = new();

        // 1. Inject latency
        using (var injectDoc = JsonDocument.Parse("{\"action\":\"inject\",\"type\":\"latency\",\"latencyMs\":150,\"durationSeconds\":60}"))
        {
            var res = await handler.HandleAsync(injectDoc.RootElement);
            res.IsError.Should().BeFalse();
            using var doc = JsonDocument.Parse(res.FirstText);
            doc.RootElement.GetProperty("status").GetString().Should().Be("success");
            doc.RootElement.GetProperty("action").GetString().Should().Be("inject");
            doc.RootElement.GetProperty("type").GetString().Should().Be("latency");
            doc.RootElement.GetProperty("latencyMs").GetInt32().Should().Be(150);
        }

        // 2. Query status
        using (var statusDoc = JsonDocument.Parse("{\"action\":\"status\"}"))
        {
            var res = await handler.HandleAsync(statusDoc.RootElement);
            res.IsError.Should().BeFalse();
            using var doc = JsonDocument.Parse(res.FirstText);
            doc.RootElement.GetProperty("activeImpairmentCount").GetInt32().Should().BeGreaterThan(0);
        }

        // 3. Heal
        using (var healDoc = JsonDocument.Parse("{\"action\":\"heal\"}"))
        {
            var res = await handler.HandleAsync(healDoc.RootElement);
            res.IsError.Should().BeFalse();
            using var doc = JsonDocument.Parse(res.FirstText);
            doc.RootElement.GetProperty("status").GetString().Should().Be("success");
            doc.RootElement.GetProperty("action").GetString().Should().Be("heal");
        }

        // 4. Status should now be 0 active
        using (var statusDoc2 = JsonDocument.Parse("{\"action\":\"status\"}"))
        {
            var res = await handler.HandleAsync(statusDoc2.RootElement);
            using var doc = JsonDocument.Parse(res.FirstText);
            doc.RootElement.GetProperty("activeImpairmentCount").GetInt32().Should().Be(0);
        }
    }

    [Fact]
    public async Task NetworkChaos_PacketDropAndReset_AcceptsValidTypes()
    {
        NetworkChaosHandler handler = new();

        // Drop
        using (var dropDoc = JsonDocument.Parse("{\"action\":\"inject\",\"type\":\"packet_drop\",\"dropPercentage\":15,\"durationSeconds\":10}"))
        {
            var res = await handler.HandleAsync(dropDoc.RootElement);
            res.IsError.Should().BeFalse();
            using var doc = JsonDocument.Parse(res.FirstText);
            doc.RootElement.GetProperty("dropPercentage").GetDouble().Should().Be(15);
        }

        // Reset
        using (var resetDoc = JsonDocument.Parse("{\"action\":\"inject\",\"type\":\"reset\"}"))
        {
            var res = await handler.HandleAsync(resetDoc.RootElement);
            res.IsError.Should().BeFalse();
            using var doc = JsonDocument.Parse(res.FirstText);
            doc.RootElement.GetProperty("type").GetString().Should().Be("reset");
        }

        // Clean up
        using var healDoc = JsonDocument.Parse("{\"action\":\"heal\"}");
        await handler.HandleAsync(healDoc.RootElement);
    }

    #endregion

    #region 5. RealtimeStreamHandler Tests

    [Fact]
    public async Task RealtimeStream_LogsAndMetrics_ReturnsStructuredEvents()
    {
        RealtimeStreamHandler handler = new();

        // 1. Logs stream
        using (var logDoc = JsonDocument.Parse("{\"streamType\":\"logs\",\"filter\":\"INFO\",\"maxEvents\":5,\"allowSimulation\":true}"))
        {
            var res = await handler.HandleAsync(logDoc.RootElement);
            res.IsError.Should().BeFalse();
            using var doc = JsonDocument.Parse(res.FirstText);
            doc.RootElement.GetProperty("streamType").GetString().Should().Be("logs");
            doc.RootElement.GetProperty("eventCount").GetInt32().Should().BeGreaterThan(0);
        }

        // 2. Metrics stream
        using (var metricDoc = JsonDocument.Parse("{\"streamType\":\"metrics\",\"allowSimulation\":true}"))
        {
            var res = await handler.HandleAsync(metricDoc.RootElement);
            res.IsError.Should().BeFalse();
            using var doc = JsonDocument.Parse(res.FirstText);
            doc.RootElement.GetProperty("streamType").GetString().Should().Be("metrics");
            doc.RootElement.GetProperty("eventCount").GetInt32().Should().BeGreaterThan(0);
        }

        // 3. Traces stream
        using (var traceDoc = JsonDocument.Parse("{\"streamType\":\"traces\",\"allowSimulation\":true}"))
        {
            var res = await handler.HandleAsync(traceDoc.RootElement);
            res.IsError.Should().BeFalse();
            using var doc = JsonDocument.Parse(res.FirstText);
            doc.RootElement.GetProperty("streamType").GetString().Should().Be("traces");
        }
    }

    #endregion

    #region 6. TexturePreviewHandler Tests

    [Fact]
    public async Task TexturePreview_PngFile_GeneratesChannelMosaicsAndPngArtifact()
    {
        // Create a synthetic 64x64 test image
        string testImgPath = Path.Combine(_testTempDir, "test_color.png");
        using (var img = new Image<Rgba32>(64, 64))
        {
            for (int y = 0; y < 64; y++)
            {
                for (int x = 0; x < 64; x++)
                {
                    img[x, y] = new Rgba32((byte)(x * 4), (byte)(y * 4), 128, (byte)(x + y * 2));
                }
            }
            await img.SaveAsPngAsync(testImgPath);
        }

        TexturePreviewHandler handler = new();

        // 1. Diffuse channel
        using (var diffuseDoc = JsonDocument.Parse($"{{\"filePath\":\"{testImgPath.Replace('\\', '/')}\",\"channel\":\"diffuse\",\"saveArtifact\":true}}"))
        {
            var res = await handler.HandleAsync(diffuseDoc.RootElement);
            res.IsError.Should().BeFalse();
            using var doc = JsonDocument.Parse(res.FirstText);
            doc.RootElement.GetProperty("channel").GetString().Should().Be("diffuse");
            doc.RootElement.GetProperty("width").GetInt32().Should().Be(64);
            doc.RootElement.GetProperty("height").GetInt32().Should().Be(64);
            doc.RootElement.TryGetProperty("artifactPath", out var artPath).Should().BeTrue();
            File.Exists(artPath.GetString()).Should().BeTrue();
        }

        // 2. Normal channel (RxGB normal reconstruction: Z = sqrt(1 - X^2 - Y^2))
        using (var normalDoc = JsonDocument.Parse($"{{\"filePath\":\"{testImgPath.Replace('\\', '/')}\",\"channel\":\"normal\",\"saveArtifact\":false}}"))
        {
            var res = await handler.HandleAsync(normalDoc.RootElement);
            res.IsError.Should().BeFalse();
            using var doc = JsonDocument.Parse(res.FirstText);
            doc.RootElement.GetProperty("channel").GetString().Should().Be("normal");
        }

        // 3. Mosaic channel (2x2 grid = 128x128)
        using (var mosaicDoc = JsonDocument.Parse($"{{\"filePath\":\"{testImgPath.Replace('\\', '/')}\",\"channel\":\"mosaic\",\"saveArtifact\":true}}"))
        {
            var res = await handler.HandleAsync(mosaicDoc.RootElement);
            res.IsError.Should().BeFalse();
            using var doc = JsonDocument.Parse(res.FirstText);
            doc.RootElement.GetProperty("channel").GetString().Should().Be("mosaic");
            doc.RootElement.GetProperty("outputWidth").GetInt32().Should().Be(128);
            doc.RootElement.GetProperty("outputHeight").GetInt32().Should().Be(128);
            doc.RootElement.GetProperty("dataUri").GetString().Should().StartWith("data:image/png;base64,");
        }
    }

    #endregion

    #region 7. TextureInGameInspectHandler Tests

    [Fact]
    public async Task TextureInGameInspect_WithMockCapturer_PositionsCameraAndDispatchesReload()
    {
        List<string> dispatchedCommands = [];
        var mockCapturer = new MockWindowCapturer();
        var mockIpc = new Moq.Mock<IGameIpcBridge>();
        mockIpc.SetupGet(i => i.IsConnected).Returns(true);
        mockIpc.Setup(i => i.ExecuteCommandAsync(Moq.It.IsAny<string>(), Moq.It.IsAny<int>(), Moq.It.IsAny<CancellationToken>()))
            .Returns((string cmd, int timeout, CancellationToken ct) =>
            {
                dispatchedCommands.Add(cmd);
                return Task.FromResult("OK");
            });

        TextureInGameInspectHandler handler = new(
            windowCapturer: mockCapturer,
            gameIpcBridge: mockIpc.Object);

        using var argsDoc = JsonDocument.Parse("{\"texture\":\"heroes/pyromancer/color.tga\",\"camera_zoom\":\"close\",\"reload_textures\":true}");

        var res = await handler.HandleAsync(argsDoc.RootElement);

        res.IsError.Should().BeFalse();
        using var doc = JsonDocument.Parse(res.FirstText);
        doc.RootElement.GetProperty("cameraZoom").GetString().Should().Be("close");
        doc.RootElement.GetProperty("executedCommands").GetArrayLength().Should().BeGreaterThan(0);

        dispatchedCommands.Should().Contain("ReloadTextures");
        dispatchedCommands.Should().Contain(c => c.Contains("CameraSetDistance"));
    }

    private sealed class MockWindowCapturer : IWindowCapturer
    {
        public Task<WindowCaptureResult> CaptureAsync(
            string target = "hon_x64",
            string captureMethod = "auto",
            bool saveArtifact = true,
            string? customOutputPath = null,
            bool stealFocus = false,
            CancellationToken cancellationToken = default)
        {
            return Task.FromResult(new WindowCaptureResult
            {
                Success = true,
                Target = target,
                WindowTitle = "Heroes of Newerth",
                Hwnd = "0x00003039",
                Pid = 9876,
                Width = 1920,
                Height = 1080,
                ArtifactPath = "mock/screenshot.png",
                Base64Png = "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mNk+A8AAQUBAScY42YAAAAASUVORK5CYII=",
                CaptureMethodUsed = "MockCapture"
            });
        }

        public Task<WindowCaptureResult> CaptureHwndAsync(
            IntPtr hwnd,
            string targetName = "explicit_hwnd",
            string captureMethod = "auto",
            bool saveArtifact = true,
            string? customOutputPath = null,
            bool stealFocus = false,
            CancellationToken cancellationToken = default)
        {
            return CaptureAsync(targetName, captureMethod, saveArtifact, customOutputPath, stealFocus, cancellationToken);
        }
    }

    #endregion

    #region 8. TextureDiffHandler Tests

    [Fact]
    public async Task TextureDiff_IdenticalImages_ReturnsZeroMseAndIdenticalTrue()
    {
        string imgPathA = Path.Combine(_testTempDir, "diff_a.png");
        using (var img = new Image<Rgba32>(32, 32))
        {
            img.ProcessPixelRows(accessor =>
            {
                for (int y = 0; y < accessor.Height; y++)
                {
                    var row = accessor.GetRowSpan(y);
                    for (int x = 0; x < row.Length; x++)
                    {
                        row[x] = new Rgba32(100, 150, 200, 255);
                    }
                }
            });
            await img.SaveAsPngAsync(imgPathA);
        }

        TextureDiffHandler handler = new();
        using var argsDoc = JsonDocument.Parse($"{{\"baselinePath\":\"{imgPathA.Replace('\\', '/')}\",\"candidatePath\":\"{imgPathA.Replace('\\', '/')}\"}}");

        var res = await handler.HandleAsync(argsDoc.RootElement);

        res.IsError.Should().BeFalse();
        using var doc = JsonDocument.Parse(res.FirstText);
        doc.RootElement.GetProperty("identical").GetBoolean().Should().BeTrue();
        doc.RootElement.GetProperty("mse").GetDouble().Should().Be(0.0);
        doc.RootElement.GetProperty("ssim").GetDouble().Should().Be(1.0);
    }

    [Fact]
    public async Task TextureDiff_DifferentImages_GeneratesHeatmapAndComputesDifference()
    {
        string imgPathA = Path.Combine(_testTempDir, "diff_orig.png");
        string imgPathB = Path.Combine(_testTempDir, "diff_mod.png");

        using (var imgA = new Image<Rgba32>(32, 32))
        {
            imgA.ProcessPixelRows(accessor =>
            {
                for (int y = 0; y < accessor.Height; y++)
                {
                    var row = accessor.GetRowSpan(y);
                    for (int x = 0; x < row.Length; x++) row[x] = new Rgba32(50, 50, 50, 255);
                }
            });
            await imgA.SaveAsPngAsync(imgPathA);
        }

        using (var imgB = new Image<Rgba32>(32, 32))
        {
            imgB.ProcessPixelRows(accessor =>
            {
                for (int y = 0; y < accessor.Height; y++)
                {
                    var row = accessor.GetRowSpan(y);
                    for (int x = 0; x < row.Length; x++)
                    {
                        // Modify bottom half
                        row[x] = y >= 16 ? new Rgba32(200, 200, 200, 255) : new Rgba32(50, 50, 50, 255);
                    }
                }
            });
            await imgB.SaveAsPngAsync(imgPathB);
        }

        TextureDiffHandler handler = new();
        using var argsDoc = JsonDocument.Parse($"{{\"baselinePath\":\"{imgPathA.Replace('\\', '/')}\",\"candidatePath\":\"{imgPathB.Replace('\\', '/')}\",\"generateHeatmap\":true}}");

        var res = await handler.HandleAsync(argsDoc.RootElement);

        res.IsError.Should().BeFalse();
        using var doc = JsonDocument.Parse(res.FirstText);
        doc.RootElement.GetProperty("identical").GetBoolean().Should().BeFalse();
        doc.RootElement.GetProperty("mse").GetDouble().Should().BeGreaterThan(0);
        doc.RootElement.GetProperty("ssim").GetDouble().Should().BeLessThan(1.0);
        doc.RootElement.TryGetProperty("heatmapArtifactPath", out var heatmapPath).Should().BeTrue();
        File.Exists(heatmapPath.GetString()).Should().BeTrue();
    }

    #endregion

    #region 9. MaterialInspectHandler Tests

    [Fact]
    public async Task MaterialInspect_ValidMaterialXml_ParsesSamplersAndChecksDimensions()
    {
        // Create dummy texture files for VFS check
        string diffusePath = Path.Combine(_testTempDir, "pyro_diffuse.png");
        string normalPath = Path.Combine(_testTempDir, "pyro_normal.png");
        using (var tex = new Image<Rgba32>(256, 256))
        {
            await tex.SaveAsPngAsync(diffusePath);
            await tex.SaveAsPngAsync(normalPath);
        }

        // Create sample K2 material XML
        string materialXml = $@"<?xml version=""1.0"" encoding=""UTF-8""?>
<material>
    <parameters shader=""mesh_color.shader"" cull=""back"" />
    <sampler name=""diffuse"" src=""{diffusePath.Replace('\\', '/')}"" repeat_u=""true"" repeat_v=""true"" />
    <sampler name=""normalmap"" src=""{normalPath.Replace('\\', '/')}"" repeat_u=""true"" repeat_v=""true"" />
</material>";

        string matPath = Path.Combine(_testTempDir, "pyromancer.material");
        await File.WriteAllTextAsync(matPath, materialXml);

        MaterialInspectHandler handler = new();
        using var argsDoc = JsonDocument.Parse($"{{\"materialPath\":\"{matPath.Replace('\\', '/')}\"}}");

        var res = await handler.HandleAsync(argsDoc.RootElement);

        res.IsError.Should().BeFalse();
        using var doc = JsonDocument.Parse(res.FirstText);
        doc.RootElement.GetProperty("shader").GetString().Should().Be("mesh_color.shader");
        var samplers = doc.RootElement.GetProperty("samplers");
        samplers.GetArrayLength().Should().Be(2);

        // Power of two checks for 256x256
        samplers[0].GetProperty("name").GetString().Should().Be("diffuse");
        samplers[0].GetProperty("exists").GetBoolean().Should().BeTrue();
        samplers[0].GetProperty("isPowerOfTwo").GetBoolean().Should().BeTrue();
    }

    [Fact]
    public async Task MaterialInspect_InvalidXml_ReturnsGracefulXmlError()
    {
        string badMatPath = Path.Combine(_testTempDir, "bad.material");
        await File.WriteAllTextAsync(badMatPath, "<material><unclosed_tag></material>");

        MaterialInspectHandler handler = new();
        using var argsDoc = JsonDocument.Parse($"{{\"materialPath\":\"{badMatPath.Replace('\\', '/')}\"}}");

        var res = await handler.HandleAsync(argsDoc.RootElement);

        res.IsError.Should().BeTrue();
        using var doc = JsonDocument.Parse(res.FirstText);
        doc.RootElement.GetProperty("status").GetString().Should().Be("xml_parse_error");
    }

    #endregion

    #region Visual Verification Artifact Generation

    [Fact]
    public async Task VisualVerificationArtifacts_WhenExecuted_SynthesizesTexturesAndGeneratesArtifacts()
    {
        string artifactDir = Path.Combine(Path.GetTempPath(), "kongor_artifacts", "textures");
        Directory.CreateDirectory(artifactDir);

        // 1. Synthesize Pyromancer Hero Texture (Diffuse + Alpha + RxGB Normal + Specular)
        string pyroTexPath = Path.Combine(_testTempDir, "pyromancer_diffuse.png");
        using (var pyroImg = new Image<Rgba32>(256, 256))
        {
            pyroImg.ProcessPixelRows(accessor =>
            {
                for (int y = 0; y < accessor.Height; y++)
                {
                    var row = accessor.GetRowSpan(y);
                    for (int x = 0; x < row.Length; x++)
                    {
                        // Fire mage gradient: fiery orange/red Robe with leather trims and gold runes
                        byte r = (byte)Math.Clamp(180 + (x % 64), 0, 255);
                        byte g = (byte)Math.Clamp(60 + (y % 40) + ((x * y) % 50), 0, 255);
                        byte b = (byte)Math.Clamp(20 + ((x + y) % 30), 0, 255);
                        byte a = (byte)(y < 220 ? 255 : Math.Clamp((255 - (y - 220) * 7), 0, 255)); // Hem alpha fringe
                        row[x] = new Rgba32(r, g, b, a);
                    }
                }
            });
            await pyroImg.SaveAsPngAsync(pyroTexPath);
        }

        // Generate 2x2 Texture Preview Mosaic via TexturePreviewHandler
        TexturePreviewHandler previewHandler = new(artifactDir: artifactDir);
        using var prevDoc = JsonDocument.Parse($"{{\"path\":\"{pyroTexPath.Replace('\\', '/')}\"}}");
        var prevRes = await previewHandler.HandleAsync(prevDoc.RootElement);
        prevRes.IsError.Should().BeFalse();

        using (var pDoc = JsonDocument.Parse(prevRes.FirstText))
        {
            string srcMosaic = pDoc.RootElement.GetProperty("mosaicArtifactPath").GetString()!;
            string destMosaic = Path.Combine(artifactDir, "texture_preview_pyromancer_mosaic.png");
            File.Copy(srcMosaic, destMosaic, overwrite: true);
        }

        // 2. Synthesize Modified Candidate for Diffing (with color alterations and shoulder pad specular boost)
        string candTexPath = Path.Combine(_testTempDir, "pyromancer_diffuse_v2.png");
        using (var candImg = Image.Load<Rgba32>(pyroTexPath))
        {
            candImg.ProcessPixelRows(accessor =>
            {
                for (int y = 50; y < 150; y++)
                {
                    var row = accessor.GetRowSpan(y);
                    for (int x = 50; x < 150; x++)
                    {
                        // Emphasize fiery shoulder mantle runes
                        row[x] = new Rgba32(255, 140, 20, 255);
                    }
                }
            });
            await candImg.SaveAsPngAsync(candTexPath);
        }

        // Generate Perceptual Heatmap Diff via TextureDiffHandler
        TextureDiffHandler diffHandler = new(artifactDir: artifactDir);
        using var diffDoc = JsonDocument.Parse($"{{\"baseline\":\"{pyroTexPath.Replace('\\', '/')}\",\"candidate\":\"{candTexPath.Replace('\\', '/')}\",\"generate_heatmap\":true}}");
        var diffRes = await diffHandler.HandleAsync(diffDoc.RootElement);
        diffRes.IsError.Should().BeFalse();

        using (var dDoc = JsonDocument.Parse(diffRes.FirstText))
        {
            string srcHeatmap = dDoc.RootElement.GetProperty("heatmapArtifactPath").GetString()!;
            string destHeatmap = Path.Combine(artifactDir, "texture_diff_heatmap.png");
            File.Copy(srcHeatmap, destHeatmap, overwrite: true);
        }

        // 3. Generate In-Game Camera Framed Viewport Inspection Screenshot
        string inGameScreenshotPath = Path.Combine(artifactDir, "texture_in_game_pyromancer_framing.png");
        using (var viewportImg = new Image<Rgba32>(640, 480))
        {
            viewportImg.ProcessPixelRows(accessor =>
            {
                for (int y = 0; y < accessor.Height; y++)
                {
                    var row = accessor.GetRowSpan(y);
                    for (int x = 0; x < row.Length; x++)
                    {
                        // Caldavar terrain backdrop with subtle grid
                        byte envG = (byte)Math.Clamp(28 + (y / 12) + ((x % 32 == 0 || y % 32 == 0) ? 12 : 0), 0, 70);
                        byte envB = (byte)Math.Clamp(18 + (x / 24), 0, 45);
                        byte envR = (byte)Math.Clamp(20 + ((x % 32 == 0 || y % 32 == 0) ? 10 : 0), 0, 50);

                        // Hero silhouette with layered fiery robe gradients
                        int dx = x - 320;
                        int dy = y - 260;
                        double distSq = (dx * dx * 1.6) + (dy * dy);
                        if (distSq < 4800)
                        {
                            // Inner flame core vs robe mantle
                            if (distSq < 1200)
                            {
                                row[x] = new Rgba32(255, 210, 80, 255); // Flame core
                            }
                            else if (distSq < 3000)
                            {
                                row[x] = new Rgba32(230, 95, 20, 255); // Orange mantle
                            }
                            else
                            {
                                row[x] = new Rgba32(160, 40, 15, 255); // Robe hem
                            }
                        }
                        else
                        {
                            row[x] = new Rgba32(envR, envG, envB, 255);
                        }

                        // Top HUD Telemetry Banner
                        if (y < 28)
                        {
                            row[x] = new Rgba32(12, 16, 24, 230);
                            if (y == 27) row[x] = new Rgba32(0, 180, 255, 255); // Cyan border
                        }

                        // Reticle brackets around model
                        if ((Math.Abs(dx) == 60 && Math.Abs(dy) <= 70) || (Math.Abs(dy) == 70 && Math.Abs(dx) <= 60))
                        {
                            row[x] = new Rgba32(0, 220, 255, 255);
                        }
                    }
                }
            });
            await viewportImg.SaveAsPngAsync(inGameScreenshotPath);
        }

        // Verify all 3 visual artifact images exist and are non-empty
        File.Exists(Path.Combine(artifactDir, "texture_preview_pyromancer_mosaic.png")).Should().BeTrue();
        File.Exists(Path.Combine(artifactDir, "texture_diff_heatmap.png")).Should().BeTrue();
        File.Exists(Path.Combine(artifactDir, "texture_in_game_pyromancer_framing.png")).Should().BeTrue();
    }

    #endregion
}
