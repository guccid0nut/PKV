// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

// ============================================================================
// File: TextureModernizationService.cs
// System: Kongor.Modding - Graphics & Asset Modernization Subsystem
// Subsystem: Phase 13 Modern Texture Encoding (DXT5-RXGB, Dithered BC1/BC3, 32-bit ARGB)
// Policy: Strict Explicit Typing (NO 'var')
// ============================================================================

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace Kongor.Modding.Services;

/// <summary>
///     Represents the conversion result of a single texture file.
/// </summary>
public sealed record TextureConversionResult(
    bool Success,
    string InputPath,
    string OutputPath,
    string TargetFormat,
    int MipLevels,
    long OutputSizeBytes,
    string Sha256Hash,
    string? ErrorMessage = null
);

/// <summary>
///     Metadata and status for an individual texture file within an avatar package.
/// </summary>
public sealed record TextureFileReport(
    string FileName,
    string RelativePath,
    string Format,
    int Width,
    int Height,
    int MipLevels,
    long SizeBytes,
    bool Modernized,
    string Sha256
);

/// <summary>
///     Comprehensive modernization report for a hero's alt avatar texture suite.
/// </summary>
public sealed record HeroTextureModernizationReport(
    string HeroName,
    string AvatarName,
    bool Success,
    IReadOnlyList<TextureFileReport> ProcessedTextures,
    string OutputDirectory,
    string? ErrorMessage = null
);

/// <summary>
///     Provides governance and high-precision DirectCompute texture modernization
///     wrapping DirectXTex texconv.exe for K2 engine Direct3D 9 compatibility.
/// </summary>
public sealed class TextureModernizationService(ILogger<TextureModernizationService>? logger = null)
{
    private readonly ILogger<TextureModernizationService>? _logger = logger;

    /// <summary>
    ///     Resolves the full absolute path to the bundled texconv.exe utility.
    /// </summary>
    public static string ResolveTexconvPath(string repoRoot)
    {
        string[] candidates =
        [
            Path.Combine(repoRoot, "artifacts", "native-binaries", "windows-x64", "texconv.exe"),
            Path.Combine(repoRoot, ".tools", "bin", "texconv", "texconv.exe"),
            Path.Combine(repoRoot, "src", "tools", "texconv", "texconv.exe"),
            Path.Combine(Directory.GetCurrentDirectory(), ".tools", "bin", "texconv", "texconv.exe"),
            Path.Combine(Directory.GetCurrentDirectory(), "artifacts", "native-binaries", "windows-x64", "texconv.exe"),
            Path.Combine(Directory.GetCurrentDirectory(), "src", "tools", "texconv", "texconv.exe")
        ];

        foreach (string cand in candidates)
        {
            if (File.Exists(cand)) return cand;
        }

        return candidates[1];
    }

    /// <summary>
    ///     Computes the lowercase hexadecimal SHA-256 hash of a file on disk.
    /// </summary>
    public static string ComputeSha256(string filePath)
    {
        if (!File.Exists(filePath)) return string.Empty;
        try
        {
            using SHA256 hasher = SHA256.Create();
            using FileStream stream = File.OpenRead(filePath);
            byte[] hashBytes = hasher.ComputeHash(stream);
            return Convert.ToHexString(hashBytes).ToLowerInvariant();
        }
        catch (Exception)
        {
            return "hash_error";
        }
    }

    private static ProcessStartInfo CreateTexconvStartInfo(
        string texconvExe,
        string inputPath,
        string outDir,
        string format,
        bool dither,
        int mipLevels,
        string? argumentsOverride,
        string? swizzle = null)
    {
        if (argumentsOverride is not null)
        {
            return new ProcessStartInfo
            {
                FileName = texconvExe,
                Arguments = argumentsOverride,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
        }

        List<string> args =
        [
            "-nologo",
            "-f", format,
            "-m", mipLevels.ToString(),
            "-o", $"\"{outDir}\"",
            "-y"
        ];

        if (!string.IsNullOrWhiteSpace(swizzle))
        {
            args.Add("--swizzle");
            args.Add(swizzle);
        }

        if (dither && (format.Equals("DXT1", StringComparison.OrdinalIgnoreCase) || format.Equals("DXT5", StringComparison.OrdinalIgnoreCase)))
        {
            args.Add("-bc:d");
            args.Add("-bc:q");
        }

        args.Add($"\"{inputPath}\"");

        return new ProcessStartInfo
        {
            FileName = texconvExe,
            Arguments = string.Join(" ", args),
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
    }

    /// <summary>
    ///     Determines whether a candidate normal map requires DirectXTex <c>--swizzle 1gbr</c>
    ///     to pack K2's <c>.agb</c> RxGB channel layout (<c>R=255, G=Ny, B=Nz, A=Nx</c>),
    ///     guarding against double-swizzling inputs that are already RxGB-packed.
    /// </summary>
    public static bool ShouldSwizzleSourceToRxgb(string inputPath)
    {
        string fileName = Path.GetFileName(inputPath);
        if (fileName.EndsWith("_rxgb.dds", StringComparison.OrdinalIgnoreCase) ||
            fileName.EndsWith("_rxgb.tga", StringComparison.OrdinalIgnoreCase) ||
            fileName.EndsWith("_rxgb.png", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (!File.Exists(inputPath))
        {
            return true;
        }

        string ext = Path.GetExtension(inputPath);
        try
        {
            using FileStream fs = File.OpenRead(inputPath);
            if (ext.Equals(".dds", StringComparison.OrdinalIgnoreCase) && fs.Length >= 144)
            {
                byte[] headerAndBlocks = new byte[(int)Math.Min(fs.Length, 128 + (64 * 16))];
                int readBytes = fs.Read(headerAndBlocks, 0, headerAndBlocks.Length);
                if (readBytes >= 144 &&
                    headerAndBlocks[0] == (byte)'D' &&
                    headerAndBlocks[1] == (byte)'D' &&
                    headerAndBlocks[2] == (byte)'S' &&
                    headerAndBlocks[3] == (byte)' ' &&
                    headerAndBlocks[84] == (byte)'D' &&
                    headerAndBlocks[85] == (byte)'X' &&
                    headerAndBlocks[86] == (byte)'T' &&
                    headerAndBlocks[87] == (byte)'5')
                {
                    int blockCount = (readBytes - 128) / 16;
                    int rMaxBlocks = 0;
                    bool hasNonMaxAlpha = false;
                    for (int b = 0; b < blockCount; b++)
                    {
                        int off = 128 + (b * 16);
                        byte a0 = headerAndBlocks[off];
                        byte a1 = headerAndBlocks[off + 1];
                        if (a0 < 250 || a1 < 250)
                        {
                            hasNonMaxAlpha = true;
                        }

                        ushort c0 = (ushort)(headerAndBlocks[off + 8] | (headerAndBlocks[off + 9] << 8));
                        ushort c1 = (ushort)(headerAndBlocks[off + 10] | (headerAndBlocks[off + 11] << 8));
                        int r0 = (c0 >> 11) & 0x1F;
                        int r1 = (c1 >> 11) & 0x1F;
                        if (r0 >= 30 && r1 >= 30)
                        {
                            rMaxBlocks++;
                        }
                    }

                    if (blockCount > 0 && rMaxBlocks == blockCount && hasNonMaxAlpha)
                    {
                        return false;
                    }
                }

                return true;
            }

            if (ext.Equals(".tga", StringComparison.OrdinalIgnoreCase) && fs.Length > 18)
            {
                byte[] tgaHeader = new byte[18];
                if (fs.Read(tgaHeader, 0, 18) == 18 && tgaHeader[2] == 2 && tgaHeader[16] == 32)
                {
                    int idLength = tgaHeader[0];
                    int colorMapType = tgaHeader[1];
                    if (colorMapType == 0 && fs.Length > 18 + idLength + 16)
                    {
                        fs.Seek(18 + idLength, SeekOrigin.Begin);
                        byte[] sampleBytes = new byte[(int)Math.Min(fs.Length - fs.Position, 1024)];
                        int sampleRead = fs.Read(sampleBytes, 0, sampleBytes.Length);
                        int pixelCount = sampleRead / 4;
                        if (pixelCount >= 4)
                        {
                            byte minR = 255;
                            bool hasNonMaxAlpha = false;
                            for (int p = 0; p < pixelCount; p++)
                            {
                                byte r = sampleBytes[(p * 4) + 2];
                                byte a = sampleBytes[(p * 4) + 3];
                                if (r < minR) minR = r;
                                if (a < 250) hasNonMaxAlpha = true;
                            }

                            if (minR >= 250 && hasNonMaxAlpha)
                            {
                                return false;
                            }
                        }
                    }
                }
            }
        }
        catch (Exception)
        {
            return true;
        }

        return true;
    }

    private TextureConversionResult FinalizeTexconvOutput(
        string inputPath,
        string outputPath,
        string outDir,
        string format,
        int mipLevels,
        int exitCode,
        string stdout,
        string stderr)
    {
        if (exitCode != 0)
        {
            string details = $"{stderr} {stdout}".Trim();
            _logger?.LogWarning(
                "[TEXTURE_MODERNIZE] texconv exited with code {ExitCode} for '{Input}'. Output: {Details}",
                exitCode,
                inputPath,
                details);
            return new TextureConversionResult(
                false,
                inputPath,
                outputPath,
                format,
                0,
                0,
                string.Empty,
                $"texconv failed with exit code {exitCode} for '{inputPath}'. Run 'src/tools/texconv/texconv.exe -f {format} \"{inputPath}\"' to diagnose. Output: {details}".Trim());
        }

        // Texconv outputs the file with original name and .dds extension in outDir
        string generatedFileName = Path.ChangeExtension(Path.GetFileName(inputPath), ".dds");
        string generatedFilePath = Path.Combine(outDir, generatedFileName);

        if (File.Exists(generatedFilePath) && !generatedFilePath.Equals(outputPath, StringComparison.OrdinalIgnoreCase))
        {
            File.Copy(generatedFilePath, outputPath, overwrite: true);
            try
            {
                File.Delete(generatedFilePath);
            }
            catch (Exception cleanupEx)
            {
                _logger?.LogDebug(cleanupEx, "[TEXTURE_MODERNIZE] Temporary staging file '{TempFile}' could not be removed immediately.", generatedFilePath);
            }
        }

        if (!File.Exists(outputPath))
        {
            return new TextureConversionResult(
                false,
                inputPath,
                outputPath,
                format,
                0,
                0,
                string.Empty,
                $"texconv exited 0 but failed to produce '{outputPath}'. Output: {stdout} {stderr}".Trim());
        }

        FileInfo fi = new(outputPath);
        string hash = ComputeSha256(outputPath);
        _logger?.LogInformation("[TEXTURE_MODERNIZE] Successfully encoded {Out} ({Size:N0} bytes, format: {Fmt})", outputPath, fi.Length, format);

        return new TextureConversionResult(true, inputPath, outputPath, format, mipLevels, fi.Length, hash);
    }

    /// <summary>
    ///     Asynchronously modernizes a single texture file using DirectXTex DirectCompute compression
    ///     with concurrent stdout/stderr draining, bounded timeout enforcement, and exit-code verification.
    /// </summary>
    public async Task<TextureConversionResult> ModernizeTextureAsync(
        string repoRoot,
        string inputPath,
        string outputPath,
        string format = "DXT5",
        bool dither = true,
        int mipLevels = 0,
        int timeoutMs = 15000,
        string? executableOverride = null,
        string? argumentsOverride = null,
        string? swizzle = null,
        CancellationToken cancellationToken = default)
    {
        if (!File.Exists(inputPath))
        {
            return new TextureConversionResult(false, inputPath, outputPath, format, 0, 0, string.Empty, $"Input file not found: {inputPath}");
        }

        string texconvExe = executableOverride ?? ResolveTexconvPath(repoRoot);
        if (!File.Exists(texconvExe))
        {
            return new TextureConversionResult(false, inputPath, outputPath, format, 0, 0, string.Empty, $"texconv.exe not found at '{texconvExe}'.");
        }

        string outDir = Path.GetDirectoryName(outputPath) ?? ".";
        if (!Directory.Exists(outDir))
        {
            Directory.CreateDirectory(outDir);
        }

        ProcessStartInfo psi = CreateTexconvStartInfo(texconvExe, inputPath, outDir, format, dither, mipLevels, argumentsOverride, swizzle);

        try
        {
            using Process proc = new() { StartInfo = psi };
            proc.Start();

            using CancellationTokenSource timeoutCts = new(TimeSpan.FromMilliseconds(Math.Max(1, timeoutMs)));
            using CancellationTokenSource linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutCts.Token);

            Task<string> stdoutTask = proc.StandardOutput.ReadToEndAsync(linkedCts.Token);
            Task<string> stderrTask = proc.StandardError.ReadToEndAsync(linkedCts.Token);

            try
            {
                await proc.WaitForExitAsync(linkedCts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (timeoutCts.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
            {
                try
                {
                    proc.Kill(entireProcessTree: true);
                }
                catch (Exception killEx)
                {
                    _logger?.LogWarning(killEx, "[TEXTURE_MODERNIZE] Failed to terminate timed-out texconv process for '{Input}'.", inputPath);
                }

                _logger?.LogError("[TEXTURE_MODERNIZE] texconv timed out after {TimeoutMs}ms for '{Input}'.", timeoutMs, inputPath);
                return new TextureConversionResult(
                    false,
                    inputPath,
                    outputPath,
                    format,
                    0,
                    0,
                    string.Empty,
                    $"texconv timed out after {timeoutMs}ms while encoding '{inputPath}'. Inspect input dimensions or run 'src/tools/texconv/texconv.exe \"{inputPath}\"' manually.");
            }

            string stdout = await stdoutTask.ConfigureAwait(false);
            string stderr = await stderrTask.ConfigureAwait(false);

            return FinalizeTexconvOutput(inputPath, outputPath, outDir, format, mipLevels, proc.ExitCode, stdout, stderr);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "[TEXTURE_MODERNIZE] Async process execution failed: {Message}", ex.Message);
            return new TextureConversionResult(false, inputPath, outputPath, format, 0, 0, string.Empty, ex.Message);
        }
    }

    /// <summary>
    ///     Modernizes a single texture file using DirectXTex DirectCompute compression with dithering, full mipmaps,
    ///     non-blocking async pipe readers, 15s timeout kill, and exit-code verification.
    /// </summary>
    public TextureConversionResult ModernizeTexture(
        string repoRoot,
        string inputPath,
        string outputPath,
        string format = "DXT5",
        bool dither = true,
        int mipLevels = 0,
        int timeoutMs = 15000,
        string? executableOverride = null,
        string? argumentsOverride = null,
        string? swizzle = null)
    {
        if (!File.Exists(inputPath))
        {
            return new TextureConversionResult(false, inputPath, outputPath, format, 0, 0, string.Empty, $"Input file not found: {inputPath}");
        }

        string texconvExe = executableOverride ?? ResolveTexconvPath(repoRoot);
        if (!File.Exists(texconvExe))
        {
            return new TextureConversionResult(false, inputPath, outputPath, format, 0, 0, string.Empty, $"texconv.exe not found at '{texconvExe}'.");
        }

        string outDir = Path.GetDirectoryName(outputPath) ?? ".";
        if (!Directory.Exists(outDir))
        {
            Directory.CreateDirectory(outDir);
        }

        ProcessStartInfo psi = CreateTexconvStartInfo(texconvExe, inputPath, outDir, format, dither, mipLevels, argumentsOverride, swizzle);

        try
        {
            StringBuilder stdoutBuilder = new();
            StringBuilder stderrBuilder = new();
            using Process proc = new() { StartInfo = psi };
            proc.OutputDataReceived += (object sender, DataReceivedEventArgs e) =>
            {
                if (e.Data is not null)
                {
                    lock (stdoutBuilder)
                    {
                        stdoutBuilder.AppendLine(e.Data);
                    }
                }
            };
            proc.ErrorDataReceived += (object sender, DataReceivedEventArgs e) =>
            {
                if (e.Data is not null)
                {
                    lock (stderrBuilder)
                    {
                        stderrBuilder.AppendLine(e.Data);
                    }
                }
            };

            proc.Start();
            proc.BeginOutputReadLine();
            proc.BeginErrorReadLine();

            bool exited = proc.WaitForExit(Math.Max(1, timeoutMs));
            if (!exited)
            {
                try
                {
                    proc.Kill(entireProcessTree: true);
                }
                catch (Exception killEx)
                {
                    _logger?.LogWarning(killEx, "[TEXTURE_MODERNIZE] Failed to terminate timed-out texconv process for '{Input}'.", inputPath);
                }

                _logger?.LogError("[TEXTURE_MODERNIZE] texconv timed out after {TimeoutMs}ms for '{Input}'.", timeoutMs, inputPath);
                return new TextureConversionResult(
                    false,
                    inputPath,
                    outputPath,
                    format,
                    0,
                    0,
                    string.Empty,
                    $"texconv timed out after {timeoutMs}ms while encoding '{inputPath}'. Inspect input dimensions or run 'src/tools/texconv/texconv.exe \"{inputPath}\"' manually.");
            }

            proc.WaitForExit();
            string stdout = stdoutBuilder.ToString();
            string stderr = stderrBuilder.ToString();

            return FinalizeTexconvOutput(inputPath, outputPath, outDir, format, mipLevels, proc.ExitCode, stdout, stderr);
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "[TEXTURE_MODERNIZE] Process execution failed: {Message}", ex.Message);
            return new TextureConversionResult(false, inputPath, outputPath, format, 0, 0, string.Empty, ex.Message);
        }
    }

    /// <summary>
    ///     Performs canary texture modernization for a hero alt avatar (e.g. Bubbles alt, Valkyrie alt).
    /// </summary>
    public HeroTextureModernizationReport ModernizeHeroAvatar(
        string repoRoot,
        string heroName,
        string avatarName,
        string? explicitOutputDir = null)
    {
        string heroDir = Path.Combine(repoRoot, "assets", "content_sources", "heroes", heroName.ToLowerInvariant(), avatarName.ToLowerInvariant());
        if (!Directory.Exists(heroDir))
        {
            return new HeroTextureModernizationReport(heroName, avatarName, false, [], string.Empty, $"Avatar directory not found: {heroDir}");
        }

        string targetDir = explicitOutputDir ?? Path.Combine(repoRoot, "scratch", "modernized_textures", heroName.ToLowerInvariant(), avatarName.ToLowerInvariant());
        if (!Directory.Exists(targetDir))
        {
            Directory.CreateDirectory(targetDir);
        }

        List<TextureFileReport> reports = [];

        // 1. Diffuse / Color map (color.tga or color.dds)
        string[] colorCandidates = ["color.tga", "color.png", "color.dds"];
        foreach (string cand in colorCandidates)
        {
            string candPath = Path.Combine(heroDir, cand);
            if (File.Exists(candPath))
            {
                string destPath = Path.Combine(targetDir, "color.dds");
                TextureConversionResult res = ModernizeTexture(repoRoot, candPath, destPath, format: "DXT5", dither: true, mipLevels: 0);
                if (res.Success)
                {
                    reports.Add(new TextureFileReport("color.dds", destPath, "DXT5 (Dithered)", 0, 0, 10, res.OutputSizeBytes, true, res.Sha256Hash));
                }
                break;
            }
        }

        // 2. Normal Map (normal_rxgb.dds or normal.tga)
        string[] normCandidates = ["normal_rxgb.dds", "normal.tga", "normal.dds"];
        foreach (string cand in normCandidates)
        {
            string candPath = Path.Combine(heroDir, cand);
            if (File.Exists(candPath))
            {
                string destPath = Path.Combine(targetDir, "normal_rxgb.dds");
                bool needsRxgbSwizzle = ShouldSwizzleSourceToRxgb(candPath);
                string? swizzleArg = needsRxgbSwizzle ? "1gbr" : null;
                _logger?.LogInformation(
                    "[TEXTURE_MODERNIZE] Normal map '{Candidate}' selected for '{Hero}/{Avatar}' (needsRxgbSwizzle={NeedsSwizzle}, swizzle={Swizzle})",
                    cand,
                    heroName,
                    avatarName,
                    needsRxgbSwizzle,
                    swizzleArg ?? "none");
                TextureConversionResult res = ModernizeTexture(
                    repoRoot,
                    candPath,
                    destPath,
                    format: "DXT5",
                    dither: false,
                    mipLevels: 0,
                    swizzle: swizzleArg);
                if (res.Success)
                {
                    reports.Add(new TextureFileReport("normal_rxgb.dds", destPath, "DXT5-RXGB", 0, 0, 10, res.OutputSizeBytes, true, res.Sha256Hash));
                }
                break;
            }
        }

        // 3. Glow / Specular Map (glow.tga or glow.dds)
        string[] glowCandidates = ["glow.tga", "glow.dds", "glow.png"];
        foreach (string cand in glowCandidates)
        {
            string candPath = Path.Combine(heroDir, cand);
            if (File.Exists(candPath))
            {
                string destPath = Path.Combine(targetDir, "glow.dds");
                TextureConversionResult res = ModernizeTexture(repoRoot, candPath, destPath, format: "DXT1", dither: true, mipLevels: 0);
                if (res.Success)
                {
                    reports.Add(new TextureFileReport("glow.dds", destPath, "DXT1 (Dithered)", 0, 0, 10, res.OutputSizeBytes, true, res.Sha256Hash));
                }
                break;
            }
        }

        // 4. Portrait & Ability Icons (icon.tga, icon.png, icon.dds)
        string[] iconCandidates = ["icon.tga", "icon.png", "icon.dds"];
        foreach (string cand in iconCandidates)
        {
            string candPath = Path.Combine(heroDir, cand);
            if (File.Exists(candPath))
            {
                string destPath = Path.Combine(targetDir, "icon.dds");
                // Icons encoded with 32-bit B8G8R8A8_UNORM for zero compression artifacts on UI
                TextureConversionResult res = ModernizeTexture(repoRoot, candPath, destPath, format: "B8G8R8A8_UNORM", dither: false, mipLevels: 1);
                if (res.Success)
                {
                    reports.Add(new TextureFileReport("icon.dds", destPath, "B8G8R8A8 (32-bit ARGB)", 0, 0, 1, res.OutputSizeBytes, true, res.Sha256Hash));
                }
                break;
            }
        }

        bool success = reports.Count > 0;
        return new HeroTextureModernizationReport(
            HeroName: heroName,
            AvatarName: avatarName,
            Success: success,
            ProcessedTextures: reports,
            OutputDirectory: targetDir);
    }
}
