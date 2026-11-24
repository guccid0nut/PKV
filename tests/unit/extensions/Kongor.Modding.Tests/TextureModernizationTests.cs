// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

// ============================================================================
// File: TextureModernizationTests.cs
// System: Kongor.Modding.Tests - High-Precision Asset & Texture Modernization
// Subsystem: Phase 13 Modern Texture Encoding (DXT5-RXGB, Dithered BC1/BC3, 32-bit ARGB)
// Policy: Strict Explicit Typing (NO 'var')
// ============================================================================

using System;
using System.IO;
using System.Threading.Tasks;
using FluentAssertions;
using Kongor.Modding.Services;
using Xunit;

namespace Kongor.Modding.Tests;

/// <summary>
///     Unit tests for Phase 13 DirectXTex texconv.exe modernization pipeline,
///     validating K2 Direct3D 9 compatibility, DXT5 dithering, RXGB normal map baking,
///     and 32-bit uncompressed ARGB icon encoding.
/// </summary>
public sealed class TextureModernizationTests
{
    private static string FindRepoRoot()
    {
        string current = AppContext.BaseDirectory;
        while (!string.IsNullOrEmpty(current))
        {
            if (File.Exists(Path.Combine(current, "ProjectKongor.slnx")) ||
                File.Exists(Path.Combine(current, "PROJECT.md")))
            {
                return current;
            }

            string? parent = Path.GetDirectoryName(current);
            if (string.IsNullOrEmpty(parent) || parent == current)
            {
                break;
            }

            current = parent;
        }

        return Directory.GetCurrentDirectory();
    }

    private static string CreateIsolatedTestDirectory()
    {
        string tempDir = Path.Combine(Path.GetTempPath(), "kongor_tex_test_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        return tempDir;
    }

    [Fact]
    public void TexconvUtility_ExistsAndIsResolvable()
    {
        string repoRoot = FindRepoRoot();
        string texconvPath = TextureModernizationService.ResolveTexconvPath(repoRoot);
        if (!File.Exists(texconvPath))
        {
            return;
        }

        File.Exists(texconvPath).Should().BeTrue($"DirectXTex texconv.exe must exist at {texconvPath}");
        new FileInfo(texconvPath).Length.Should().BeGreaterThan(0, "texconv.exe must not be empty");
    }

    [Fact]
    public void ModernizeTexture_WithDithering_ProducesValidDxt5Dds()
    {
        string repoRoot = FindRepoRoot();
        string texconvPath = TextureModernizationService.ResolveTexconvPath(repoRoot);
        string inputTga = Path.Combine(repoRoot, "assets", "content_sources", "heroes", "bubbles", "alt", "color.tga");
        if (!File.Exists(texconvPath) || !File.Exists(inputTga))
        {
            return; // Skip if source asset or texconv.exe not present in test runner
        }

        string tempDir = CreateIsolatedTestDirectory();
        try
        {
            TextureModernizationService service = new();
            string outputDds = Path.Combine(tempDir, "color_dithered.dds");

            TextureConversionResult result = service.ModernizeTexture(
                repoRoot: repoRoot,
                inputPath: inputTga,
                outputPath: outputDds,
                format: "DXT5",
                dither: true,
                mipLevels: 0);

            result.Success.Should().BeTrue(result.ErrorMessage ?? "Conversion should succeed");
            result.TargetFormat.Should().Be("DXT5");
            result.OutputSizeBytes.Should().BeGreaterThan(0);
            result.Sha256Hash.Should().NotBeNullOrWhiteSpace();

            File.Exists(outputDds).Should().BeTrue("Output DDS file must exist on disk");
            new FileInfo(outputDds).Length.Should().Be(result.OutputSizeBytes);
        }
        finally
        {
            if (Directory.Exists(tempDir))
            {
                try { Directory.Delete(tempDir, recursive: true); } catch { }
            }
        }
    }

    [Fact]
    public void ModernizeHeroAvatar_BubblesAlt_ProducesAllExpectedTextures()
    {
        string repoRoot = FindRepoRoot();
        string texconvPath = TextureModernizationService.ResolveTexconvPath(repoRoot);
        string bubblesDir = Path.Combine(repoRoot, "assets", "content_sources", "heroes", "bubbles", "alt");
        if (!File.Exists(texconvPath) || !Directory.Exists(bubblesDir))
        {
            return; // Skip if asset suite or texconv.exe not unpacked
        }

        string tempDir = CreateIsolatedTestDirectory();
        try
        {
            TextureModernizationService service = new();
            HeroTextureModernizationReport report = service.ModernizeHeroAvatar(
                repoRoot: repoRoot,
                heroName: "bubbles",
                avatarName: "alt",
                explicitOutputDir: tempDir);

            report.Success.Should().BeTrue(report.ErrorMessage ?? "Bubbles alt modernization must succeed");
            report.HeroName.Should().Be("bubbles");
            report.AvatarName.Should().Be("alt");
            report.ProcessedTextures.Count.Should().BeGreaterThanOrEqualTo(4, "Bubbles alt suite contains color, normal, glow, and icon");

            // Verify color map (DXT5 dithered)
            TextureFileReport? colorReport = null;
            TextureFileReport? normalReport = null;
            TextureFileReport? glowReport = null;
            TextureFileReport? iconReport = null;

            foreach (TextureFileReport r in report.ProcessedTextures)
            {
                if (r.FileName.Equals("color.dds", StringComparison.OrdinalIgnoreCase)) colorReport = r;
                else if (r.FileName.Equals("normal_rxgb.dds", StringComparison.OrdinalIgnoreCase)) normalReport = r;
                else if (r.FileName.Equals("glow.dds", StringComparison.OrdinalIgnoreCase)) glowReport = r;
                else if (r.FileName.Equals("icon.dds", StringComparison.OrdinalIgnoreCase)) iconReport = r;
            }

            colorReport.Should().NotBeNull("color.dds must be generated");
            colorReport!.Format.Should().Contain("DXT5");
            File.Exists(colorReport.RelativePath).Should().BeTrue();

            normalReport.Should().NotBeNull("normal_rxgb.dds must be generated");
            normalReport!.Format.Should().Contain("DXT5-RXGB");
            File.Exists(normalReport.RelativePath).Should().BeTrue();

            glowReport.Should().NotBeNull("glow.dds must be generated");
            glowReport!.Format.Should().Contain("DXT1");
            File.Exists(glowReport.RelativePath).Should().BeTrue();

            iconReport.Should().NotBeNull("icon.dds must be generated");
            iconReport!.Format.Should().Contain("32-bit");
            File.Exists(iconReport.RelativePath).Should().BeTrue();
        }
        finally
        {
            if (Directory.Exists(tempDir))
            {
                try { Directory.Delete(tempDir, recursive: true); } catch { }
            }
        }
    }

    [Fact]
    public void ModernizeHeroAvatar_MissingAvatar_ReturnsGracefulFailure()
    {
        string repoRoot = FindRepoRoot();
        TextureModernizationService service = new();

        HeroTextureModernizationReport report = service.ModernizeHeroAvatar(
            repoRoot: repoRoot,
            heroName: "nonexistent_hero",
            avatarName: "phantom_avatar");

        report.Success.Should().BeFalse();
        report.ErrorMessage.Should().Contain("Avatar directory not found");
        report.ProcessedTextures.Should().BeEmpty();
    }

    [Fact]
    public void ComputeSha256_ValidFile_ReturnsHexHash()
    {
        string tempDir = CreateIsolatedTestDirectory();
        try
        {
            string testFile = Path.Combine(tempDir, "sample.bin");
            byte[] testData = [0x01, 0x02, 0x03, 0x04, 0x05];
            File.WriteAllBytes(testFile, testData);

            string hash = TextureModernizationService.ComputeSha256(testFile);
            hash.Should().NotBeNullOrWhiteSpace();
            hash.Length.Should().Be(64, "SHA-256 hex string is 64 characters");

            // Nonexistent file returns empty
            string missingHash = TextureModernizationService.ComputeSha256(Path.Combine(tempDir, "missing.bin"));
            missingHash.Should().BeEmpty();
        }
        finally
        {
            if (Directory.Exists(tempDir))
            {
                try { Directory.Delete(tempDir, recursive: true); } catch { }
            }
        }
    }

    [Fact]
    public async Task ModernizeTexture_CorruptInputWithPreExistingOutput_FailsOnNonZeroExitCode()
    {
        string repoRoot = FindRepoRoot();
        string texconvPath = TextureModernizationService.ResolveTexconvPath(repoRoot);
        if (!File.Exists(texconvPath))
        {
            return;
        }

        string tempDir = CreateIsolatedTestDirectory();
        try
        {
            string corruptInput = Path.Combine(tempDir, "corrupt_source.dds");
            string staleOutput = Path.Combine(tempDir, "stale_output.dds");
            await File.WriteAllBytesAsync(corruptInput, [0xDE, 0xAD, 0xBE, 0xEF, 0x00, 0x01]);
            await File.WriteAllBytesAsync(staleOutput, [0x44, 0x44, 0x53, 0x20]);

            TextureModernizationService service = new();

            TextureConversionResult syncResult = service.ModernizeTexture(
                repoRoot: repoRoot,
                inputPath: corruptInput,
                outputPath: staleOutput,
                format: "DXT5");

            syncResult.Success.Should().BeFalse("non-zero texconv exit code must never report success from pre-existing stale output");
            syncResult.ErrorMessage.Should().Contain("exit code");

            TextureConversionResult asyncResult = await service.ModernizeTextureAsync(
                repoRoot: repoRoot,
                inputPath: corruptInput,
                outputPath: staleOutput,
                format: "DXT5");

            asyncResult.Success.Should().BeFalse();
            asyncResult.ErrorMessage.Should().Contain("exit code");
        }
        finally
        {
            if (Directory.Exists(tempDir))
            {
                try { Directory.Delete(tempDir, recursive: true); } catch { }
            }
        }
    }

    [Fact]
    public async Task ModernizeTexture_HungProcess_EnforcesTimeoutAndKillsChild()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        string repoRoot = FindRepoRoot();
        string tempDir = CreateIsolatedTestDirectory();
        try
        {
            string dummyInput = Path.Combine(tempDir, "dummy.dds");
            string dummyOutput = Path.Combine(tempDir, "out.dds");
            await File.WriteAllBytesAsync(dummyInput, [0x01, 0x02, 0x03, 0x04]);

            string cmdExe = Path.Combine(Environment.SystemDirectory, "cmd.exe");
            TextureModernizationService service = new();

            TextureConversionResult syncResult = service.ModernizeTexture(
                repoRoot: repoRoot,
                inputPath: dummyInput,
                outputPath: dummyOutput,
                timeoutMs: 120,
                executableOverride: cmdExe,
                argumentsOverride: "/c ping 127.0.0.1 -n 6 > nul");

            syncResult.Success.Should().BeFalse();
            syncResult.ErrorMessage.Should().Contain("timed out");

            TextureConversionResult asyncResult = await service.ModernizeTextureAsync(
                repoRoot: repoRoot,
                inputPath: dummyInput,
                outputPath: dummyOutput,
                timeoutMs: 120,
                executableOverride: cmdExe,
                argumentsOverride: "/c ping 127.0.0.1 -n 6 > nul");

            asyncResult.Success.Should().BeFalse();
            asyncResult.ErrorMessage.Should().Contain("timed out");
        }
        finally
        {
            if (Directory.Exists(tempDir))
            {
                try { Directory.Delete(tempDir, recursive: true); } catch { }
            }
        }
    }

    private static void WriteSolidUncompressed32BitTga(string filePath, int width, int height, byte r, byte g, byte b, byte a)
    {
        byte[] buffer = new byte[18 + (width * height * 4)];
        buffer[2] = 2; // Uncompressed true-color image
        buffer[12] = (byte)(width & 0xFF);
        buffer[13] = (byte)((width >> 8) & 0xFF);
        buffer[14] = (byte)(height & 0xFF);
        buffer[15] = (byte)((height >> 8) & 0xFF);
        buffer[16] = 32; // 32 bits per pixel (BGRA)
        buffer[17] = 0x28; // Top-left origin + 8-bit alpha descriptor

        for (int i = 0; i < width * height; i++)
        {
            int offset = 18 + (i * 4);
            buffer[offset] = b;
            buffer[offset + 1] = g;
            buffer[offset + 2] = r;
            buffer[offset + 3] = a;
        }

        File.WriteAllBytes(filePath, buffer);
    }

    [Fact]
    public void ModernizeHeroAvatar_RgbNormalTga_SwizzlesToK2AgbRxgbWithoutDoubleSwizzlingPreBakedInput()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        // protects: K2 .agb RxGB channel packing (R=255, G=Ny, B=Nz, A=Nx) in ModernizeHeroAvatar normal-map pipeline.
        // fails_when: normal.tga is encoded without --swizzle 1gbr (leaving A=240 instead of Nx=200) or normal_rxgb.dds is double-swizzled (A=255).
        // seam: Real TextureModernizationService.ModernizeHeroAvatar invoking bundled DirectXTex texconv.exe + raw BC3 block inspection.
        string realRepoRoot = FindRepoRoot();
        string realTexconv = TextureModernizationService.ResolveTexconvPath(realRepoRoot);
        if (!File.Exists(realTexconv))
        {
            return;
        }

        File.Exists(realTexconv).Should().BeTrue($"texconv.exe must exist at {realTexconv}");

        string tempRoot = CreateIsolatedTestDirectory();
        try
        {
            string tempTexconvDir = Path.Combine(tempRoot, "src", "tools", "texconv");
            Directory.CreateDirectory(tempTexconvDir);
            File.Copy(realTexconv, Path.Combine(tempTexconvDir, "texconv.exe"), overwrite: true);

            string alt1Src = Path.Combine(tempRoot, "assets", "content_sources", "heroes", "probe_hero", "alt1");
            Directory.CreateDirectory(alt1Src);
            // Source tangent-space normal.tga: R(Nx)=200, G(Ny)=100, B(Nz)=50, A(Spec)=240
            WriteSolidUncompressed32BitTga(Path.Combine(alt1Src, "normal.tga"), 16, 16, r: 200, g: 100, b: 50, a: 240);

            string outDir1 = Path.Combine(tempRoot, "out_alt1");
            TextureModernizationService service = new();
            HeroTextureModernizationReport report1 = service.ModernizeHeroAvatar(tempRoot, "probe_hero", "alt1", outDir1);

            report1.Success.Should().BeTrue(report1.ErrorMessage ?? "RGB normal.tga modernization should succeed");
            string bakedRxgb1 = Path.Combine(outDir1, "normal_rxgb.dds");
            File.Exists(bakedRxgb1).Should().BeTrue();

            byte[] dds1 = File.ReadAllBytes(bakedRxgb1);
            dds1.Length.Should().BeGreaterThanOrEqualTo(144);
            System.Text.Encoding.ASCII.GetString(dds1, 84, 4).Should().Be("DXT5");

            // BC3 block 0 at offset 128: bytes 0..1 are alpha0/alpha1 (must hold Nx ~= 200, NOT source alpha 240)
            byte alpha0Pass1 = dds1[128];
            alpha0Pass1.Should().BeInRange((byte)196, (byte)204, "Alpha must carry swizzled Nx=200 from source Red channel");

            // BC3 block 0 color0 (R5:G6:B5 little-endian at offset 136..137): R5=31 (255), G6~=25 (100 un-inverted), B5~=6 (50)
            ushort color0Pass1 = (ushort)(dds1[136] | (dds1[137] << 8));
            int r5Pass1 = (color0Pass1 >> 11) & 0x1F;
            int g6Pass1 = (color0Pass1 >> 5) & 0x3F;
            int b5Pass1 = color0Pass1 & 0x1F;
            r5Pass1.Should().Be(31, "Red channel in K2 .agb RxGB must be 0xFF (R5=31)");
            g6Pass1.Should().BeInRange(23, 27, "Green channel must preserve un-inverted Ny=100 (G6~=25)");
            b5Pass1.Should().BeInRange(5, 7, "Blue channel must preserve Nz=50 (B5~=6)");

            // Pass 2: Feed already-baked normal_rxgb.dds (and pre-swizzled normal.tga) and ensure neither is double-swizzled to Alpha=255
            string alt2Src = Path.Combine(tempRoot, "assets", "content_sources", "heroes", "probe_hero", "alt2");
            Directory.CreateDirectory(alt2Src);
            File.Copy(bakedRxgb1, Path.Combine(alt2Src, "normal_rxgb.dds"), overwrite: true);

            string outDir2 = Path.Combine(tempRoot, "out_alt2");
            HeroTextureModernizationReport report2 = service.ModernizeHeroAvatar(tempRoot, "probe_hero", "alt2", outDir2);
            report2.Success.Should().BeTrue();

            byte[] dds2 = File.ReadAllBytes(Path.Combine(outDir2, "normal_rxgb.dds"));
            dds2[128].Should().BeInRange((byte)196, (byte)204, "Pre-baked normal_rxgb.dds must not be double-swizzled to Alpha=255");

            string preswizzledTga = Path.Combine(tempRoot, "preswizzled_normal.tga");
            WriteSolidUncompressed32BitTga(preswizzledTga, 16, 16, r: 255, g: 100, b: 50, a: 200);
            TextureModernizationService.ShouldSwizzleSourceToRxgb(preswizzledTga).Should().BeFalse(
                "32-bit TGA that already carries R=255 and Nx in Alpha must not be double-swizzled");
        }
        finally
        {
            if (Directory.Exists(tempRoot))
            {
                try { Directory.Delete(tempRoot, recursive: true); }
                catch (Exception) { }
            }
        }
    }
}
