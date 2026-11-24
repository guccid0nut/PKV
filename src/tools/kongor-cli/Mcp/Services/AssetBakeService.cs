// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// TAO (Together As One) MCP & Kongor CLI -- Created by guccidonut (https://github.com/guccidonut/PKV)

using System.Text;
using System.Text.RegularExpressions;
using Kongor.Cli.Mcp.Imaging;

namespace Kongor.Cli.Mcp.Services;

/// <summary>
/// Semantic texture classification for K2 engine material bindings.
/// </summary>
public enum TextureSemantic
{
    Color,
    Normal,
    Specular,
    Glow,
    Unknown
}

/// <summary>
/// Inspection result for a texture file, recommending optimal texconv parameters.
/// </summary>
public sealed class TextureInspectionResult
{
    public required string InputFile { get; init; }
    public required TextureSemantic Semantic { get; init; }
    public required string TargetDdsFormat { get; init; }
    public required string MipmapFlag { get; init; }
    public bool InvertNormalY { get; init; }
    public required List<string> ExtraFlags { get; init; }
    public int? TargetWidth { get; init; }
    public int? TargetHeight { get; init; }
    public required string RecommendedTexconvCommand { get; init; }
}

/// <summary>
/// Recipe for generating a complete K2 .material XML definition.
/// </summary>
public sealed class MaterialBakeRecipe
{
    public required string MaterialName { get; init; }
    public required string ShaderType { get; init; }
    public required string VfsDestinationPath { get; init; }
    public required string XmlContent { get; init; }
    public required Dictionary<string, string> Samplers { get; init; }
    public required List<string> MissingSamplers { get; init; }
    public bool FlatNormFallbackUsed { get; init; }
    public required List<string> TexconvCommands { get; init; }
}

/// <summary>
/// Service orchestrating suffix-aware texture compression, dimension validation,
/// normal-map Y inversion, and K2 .material XML synthesis.
/// </summary>
public sealed class AssetBakeService
{
    private static readonly Regex ColorSuffixRegex = new(@"(_color|_albedo|_diffuse|basecolor)\.[a-zA-Z0-9]+$", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex NormalSuffixRegex = new(@"(_norm|_normal|_nrm)\.[a-zA-Z0-9]+$", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex SpecularSuffixRegex = new(@"(_spec|_specular|roughness|gloss)\.[a-zA-Z0-9]+$", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex GlowSuffixRegex = new(@"(_glow|_emissive)\.[a-zA-Z0-9]+$", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private readonly string _workspaceRoot;

    public AssetBakeService(string? workspaceRoot = null)
    {
        _workspaceRoot = workspaceRoot ?? Directory.GetCurrentDirectory();
    }

    /// <summary>
    /// Inspects a texture and generates optimal texconv compression arguments.
    /// </summary>
    public TextureInspectionResult InspectTexture(
        string filename,
        int? width = null,
        int? height = null,
        bool? invertNormalY = null)
    {
        TextureSemantic semantic = ResolveSemantic(filename);
        string format;
        List<string> extraFlags = [];
        bool shouldInvertY = false;

        switch (semantic)
        {
            case TextureSemantic.Color:
                format = "BC7_UNORM_SRGB";
                break;

            case TextureSemantic.Normal:
                format = "BC5_UNORM";
                extraFlags.Add("-nocr"); // Linear tangent space, no sRGB gamma transform
                shouldInvertY = invertNormalY ?? true; // Default OpenGL -> DirectX Y-inversion
                if (shouldInvertY)
                {
                    extraFlags.Add("-inverty");
                }
                break;

            case TextureSemantic.Specular:
                format = "BC4_UNORM"; // 1-channel scalar specular/roughness
                break;

            case TextureSemantic.Glow:
                format = "BC4_UNORM"; // 1-channel scalar glow/emissive
                break;

            case TextureSemantic.Unknown:
            default:
                format = "BC7_UNORM";
                break;
        }

        int? targetWidth = null;
        int? targetHeight = null;

        if (width.HasValue && height.HasValue)
        {
            (targetWidth, targetHeight) = CalculatePotDimensions(width.Value, height.Value);
            if (targetWidth != width.Value || targetHeight != height.Value)
            {
                extraFlags.Add($"-w {targetWidth}");
                extraFlags.Add($"-h {targetHeight}");
            }
        }

        string extraStr = extraFlags.Count > 0 ? $" {string.Join(" ", extraFlags)}" : "";
        string outName = Path.ChangeExtension(filename, ".dds");
        string cmd = $"texconv -f {format} -m 0{extraStr} -o \"{Path.GetDirectoryName(filename) ?? "."}\" \"{filename}\"";

        return new TextureInspectionResult
        {
            InputFile = filename,
            Semantic = semantic,
            TargetDdsFormat = format,
            MipmapFlag = "-m 0",
            InvertNormalY = shouldInvertY,
            ExtraFlags = extraFlags,
            TargetWidth = targetWidth,
            TargetHeight = targetHeight,
            RecommendedTexconvCommand = cmd
        };
    }

    /// <summary>
    /// Determines semantic texture type from filename convention.
    /// </summary>
    public static TextureSemantic ResolveSemantic(string filename)
    {
        string norm = Path.GetFileName(filename).ToLowerInvariant();
        if (ColorSuffixRegex.IsMatch(norm)) return TextureSemantic.Color;
        if (NormalSuffixRegex.IsMatch(norm)) return TextureSemantic.Normal;
        if (SpecularSuffixRegex.IsMatch(norm)) return TextureSemantic.Specular;
        if (GlowSuffixRegex.IsMatch(norm)) return TextureSemantic.Glow;
        return TextureSemantic.Unknown;
    }

    /// <summary>
    /// Adjusts dimensions to standard Power-of-Two (POT) and multiple-of-4 boundaries.
    /// </summary>
    public static (int width, int height) CalculatePotDimensions(int width, int height)
    {
        int w = NextPotOrMultipleOf4(width);
        int h = NextPotOrMultipleOf4(height);
        return (w, h);
    }

    private static int NextPotOrMultipleOf4(int n)
    {
        if (n <= 0) return 256;
        if (n <= 64) return 64;
        if (n <= 128) return 128;
        if (n <= 256) return 256;
        if (n <= 512) return 512;
        if (n <= 1024) return 1024;
        if (n <= 2048) return 2048;
        if (n <= 4096) return 4096;

        // Above 4096, ensure multiple of 4
        return (n + 3) & ~3;
    }

    /// <summary>
    /// Synthesizes a K2 .material XML file conforming to engine shader specifications.
    /// </summary>
    public MaterialBakeRecipe GenerateMaterial(
        string materialName,
        string shaderType,
        Dictionary<string, string> samplers,
        string? vfsDestinationPath = null,
        bool enablePreflightGuard = true)
    {
        string normShader = (shaderType ?? "hero").Trim().ToLowerInvariant();
        string shaderFile = normShader switch
        {
            "hero" => "/shared/shaders/hero.shader",
            "mesh" => "/shared/shaders/mesh.shader",
            "effect" => "/shared/shaders/effect.shader",
            _ => "/shared/shaders/default.shader"
        };

        var resolvedSamplers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var kvp in samplers)
        {
            resolvedSamplers[kvp.Key] = kvp.Value;
        }

        List<string> missing = [];
        bool flatNormUsed = false;

        if (enablePreflightGuard)
        {
            // Preflight normalmap guard
            if (!resolvedSamplers.TryGetValue("normalmap", out string? normPath) || string.IsNullOrWhiteSpace(normPath))
            {
                resolvedSamplers["normalmap"] = "$flat_norm";
                flatNormUsed = true;
            }
            else
            {
                if (!normPath.StartsWith('$') && !FileExistsInVfsOrDisk(normPath))
                {
                    resolvedSamplers["normalmap"] = "$flat_norm";
                    flatNormUsed = true;
                    missing.Add($"normalmap: {normPath} (replaced with $flat_norm)");
                }
            }

            // Preflight specular guard
            if (!resolvedSamplers.TryGetValue("specular", out string? specPath) || string.IsNullOrWhiteSpace(specPath))
            {
                resolvedSamplers["specular"] = "$white";
            }
            else
            {
                if (!specPath.StartsWith('$') && !FileExistsInVfsOrDisk(specPath))
                {
                    resolvedSamplers["specular"] = "$white";
                    missing.Add($"specular: {specPath} (replaced with $white)");
                }
            }
        }

        string diffuse = resolvedSamplers.TryGetValue("diffuse", out string? diff) ? diff : $"{materialName}_color.dds";
        string normal = resolvedSamplers.TryGetValue("normalmap", out string? nrm) ? nrm : "$flat_norm";
        string specular = resolvedSamplers.TryGetValue("specular", out string? spc) ? spc : "$white";

        var sb = new StringBuilder();
        sb.AppendLine("<?xml version=\"1.0\" encoding=\"UTF-8\"?>");
        sb.AppendLine("<material>");
        sb.AppendLine($"\t<parameters shader=\"{shaderFile}\" vCheck=\"false\" />");
        sb.AppendLine("\t<phase name=\"shadow\" cull=\"back\" blend=\"none\">");
        sb.AppendLine($"\t\t<sampler name=\"diffuse\" file=\"{diffuse}\" srgb=\"true\" />");
        sb.AppendLine("\t</phase>");

        if (normShader == "effect")
        {
            sb.AppendLine("\t<phase name=\"color\" cull=\"back\" blend=\"translucent\">");
            sb.AppendLine($"\t\t<sampler name=\"diffuse\" file=\"{diffuse}\" srgb=\"true\" />");
            sb.AppendLine($"\t\t<sampler name=\"normalmap\" file=\"{normal}\" />");
            sb.AppendLine($"\t\t<sampler name=\"specular\" file=\"{specular}\" />");
            sb.AppendLine("\t</phase>");
        }
        else
        {
            sb.AppendLine("\t<phase name=\"color\" cull=\"back\" blend=\"none\">");
            sb.AppendLine($"\t\t<sampler name=\"diffuse\" file=\"{diffuse}\" srgb=\"true\" />");
            sb.AppendLine($"\t\t<sampler name=\"normalmap\" file=\"{normal}\" />");
            sb.AppendLine($"\t\t<sampler name=\"specular\" file=\"{specular}\" />");
            if (normShader == "hero")
            {
                sb.AppendLine("\t\t<sampler name=\"teamcolor\" file=\"$team\" />");
            }
            sb.AppendLine("\t</phase>");
        }

        sb.AppendLine("</material>");

        string vfsPath = vfsDestinationPath ?? $"/heroes/{materialName}/{materialName}.material";

        List<string> texconvCmds = [];
        if (!diffuse.StartsWith('$'))
            texconvCmds.Add($"texconv -f BC7_UNORM_SRGB -m 0 \"{diffuse}\"");
        if (!normal.StartsWith('$'))
            texconvCmds.Add($"texconv -f BC5_UNORM -m 0 -nocr -inverty \"{normal}\"");
        if (!specular.StartsWith('$'))
            texconvCmds.Add($"texconv -f BC4_UNORM -m 0 \"{specular}\"");

        return new MaterialBakeRecipe
        {
            MaterialName = materialName,
            ShaderType = normShader,
            VfsDestinationPath = vfsPath,
            XmlContent = sb.ToString(),
            Samplers = resolvedSamplers,
            MissingSamplers = missing,
            FlatNormFallbackUsed = flatNormUsed,
            TexconvCommands = texconvCmds
        };
    }

    private bool FileExistsInVfsOrDisk(string path)
    {
        if (File.Exists(path)) return true;
        string localPath = Path.Combine(_workspaceRoot, path.TrimStart('/', '\\'));
        if (File.Exists(localPath)) return true;
        return false;
    }

    // =========================================================================
    // UI Alpha Texture Sanitization & Halo Elimination Oracle
    // =========================================================================

    public sealed record AlphaSanitizeResult
    {
        public bool Success { get; init; }
        public string OutputPath { get; init; } = string.Empty;
        public int OriginalWidth { get; init; }
        public int OriginalHeight { get; init; }
        public int TargetWidth { get; init; }
        public int TargetHeight { get; init; }
        public int BorderPixelsCleared { get; init; }
        public double PremultipliedEdgeLuma { get; init; }
        public double NaiveEdgeLuma { get; init; }
        public double PremultipliedEdgeBrightnessDrop { get; init; }
        public double NaiveEdgeBrightnessDrop { get; init; }
        public bool HaloEliminated { get; init; }
    }

    public static Image<Rgba32> CreateSyntheticGoldIcon(int width, int height, bool solidDarkBorder)
    {
        var img = new Image<Rgba32>(width, height);
        double cx = (width - 1) * 0.5;
        double cy = (height - 1) * 0.5;
        double radius = Math.Min(width, height) * 0.36;

        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                double dx = x - cx;
                double dy = y - cy;
                double dist = Math.Sqrt(dx * dx + dy * dy);
                if (dist <= radius)
                {
                    img[x, y] = new Rgba32(255, 220, 80, 255);
                }
                else if (solidDarkBorder)
                {
                    img[x, y] = new Rgba32(8, 10, 12, 255);
                }
                else
                {
                    img[x, y] = new Rgba32(0, 0, 0, 0);
                }
            }
        }
        return img;
    }

    public static int FloodFillBorderCutout(Image<Rgba32> img, int tolerance = 20)
    {
        int w = img.Width;
        int h = img.Height;
        Rgba32 seed = img[0, 0];
        if (seed.A == 0) return 0;

        bool[,] visited = new bool[w, h];
        var q = new Queue<(int X, int Y)>();

        (int X, int Y)[] corners = [(0, 0), (w - 1, 0), (0, h - 1), (w - 1, h - 1)];
        foreach (var (cx, cy) in corners)
        {
            Rgba32 c = img[cx, cy];
            if (ColorDistance(c, seed) <= tolerance)
            {
                visited[cx, cy] = true;
                q.Enqueue((cx, cy));
            }
        }

        int cleared = 0;
        int[] dx = [1, -1, 0, 0];
        int[] dy = [0, 0, 1, -1];

        while (q.Count > 0)
        {
            var (curX, curY) = q.Dequeue();
            img[curX, curY] = new Rgba32(0, 0, 0, 0);
            cleared++;

            for (int d = 0; d < 4; d++)
            {
                int nx = curX + dx[d];
                int ny = curY + dy[d];
                if (nx >= 0 && nx < w && ny >= 0 && ny < h && !visited[nx, ny])
                {
                    Rgba32 nc = img[nx, ny];
                    if (ColorDistance(nc, seed) <= tolerance)
                    {
                        visited[nx, ny] = true;
                        q.Enqueue((nx, ny));
                    }
                }
            }
        }
        return cleared;
    }

    private static int ColorDistance(Rgba32 a, Rgba32 b)
    {
        return Math.Max(Math.Abs(a.R - b.R), Math.Max(Math.Abs(a.G - b.G), Math.Abs(a.B - b.B)));
    }

    public static Image<Rgba32> ResizePremultiplied(Image<Rgba32> src, int dstW, int dstH)
    {
        int srcW = src.Width;
        int srcH = src.Height;
        var dst = new Image<Rgba32>(dstW, dstH);

        double scaleX = (double)srcW / dstW;
        double scaleY = (double)srcH / dstH;

        for (int dy = 0; dy < dstH; dy++)
        {
            double sy0 = dy * scaleY;
            double sy1 = (dy + 1) * scaleY;
            int iy0 = Math.Max(0, (int)Math.Floor(sy0));
            int iy1 = Math.Min(srcH - 1, (int)Math.Ceiling(sy1) - 1);

            for (int dx = 0; dx < dstW; dx++)
            {
                double sx0 = dx * scaleX;
                double sx1 = (dx + 1) * scaleX;
                int ix0 = Math.Max(0, (int)Math.Floor(sx0));
                int ix1 = Math.Min(srcW - 1, (int)Math.Ceiling(sx1) - 1);

                double sumPreR = 0.0;
                double sumPreG = 0.0;
                double sumPreB = 0.0;
                double sumA = 0.0;
                int sampleCount = 0;

                for (int sy = iy0; sy <= iy1; sy++)
                {
                    for (int sx = ix0; sx <= ix1; sx++)
                    {
                        Rgba32 c = src[sx, sy];
                        double aNorm = c.A / 255.0;
                        if (c.A > 0)
                        {
                            sumPreR += c.R * aNorm;
                            sumPreG += c.G * aNorm;
                            sumPreB += c.B * aNorm;
                            sumA += aNorm;
                        }
                        sampleCount++;
                    }
                }

                if (sampleCount == 0 || sumA <= 1e-6)
                {
                    dst[dx, dy] = new Rgba32(0, 0, 0, 0);
                }
                else
                {
                    double avgA = sumA / sampleCount;
                    double avgPreR = sumPreR / sampleCount;
                    double avgPreG = sumPreG / sampleCount;
                    double avgPreB = sumPreB / sampleCount;

                    int outA = Math.Max(0, Math.Min(255, (int)Math.Round(avgA * 255.0)));
                    int outR = Math.Max(0, Math.Min(255, (int)Math.Round(avgPreR / avgA)));
                    int outG = Math.Max(0, Math.Min(255, (int)Math.Round(avgPreG / avgA)));
                    int outB = Math.Max(0, Math.Min(255, (int)Math.Round(avgPreB / avgA)));

                    dst[dx, dy] = outA == 0 ? new Rgba32(0, 0, 0, 0) : new Rgba32((byte)outR, (byte)outG, (byte)outB, (byte)outA);
                }
            }
        }
        return dst;
    }

    public static Image<Rgba32> ResizeNaiveStraightAlpha(Image<Rgba32> src, int dstW, int dstH)
    {
        var dst = new Image<Rgba32>(dstW, dstH);
        int srcW = src.Width;
        int srcH = src.Height;
        double scaleX = (double)srcW / dstW;
        double scaleY = (double)srcH / dstH;

        for (int dy = 0; dy < dstH; dy++)
        {
            int iy0 = Math.Max(0, (int)Math.Floor(dy * scaleY));
            int iy1 = Math.Min(srcH - 1, (int)Math.Ceiling((dy + 1) * scaleY) - 1);
            for (int dx = 0; dx < dstW; dx++)
            {
                int ix0 = Math.Max(0, (int)Math.Floor(dx * scaleX));
                int ix1 = Math.Min(srcW - 1, (int)Math.Ceiling((dx + 1) * scaleX) - 1);
                int sumR = 0, sumG = 0, sumB = 0, sumA = 0, cnt = 0;
                for (int sy = iy0; sy <= iy1; sy++)
                {
                    for (int sx = ix0; sx <= ix1; sx++)
                    {
                        Rgba32 c = src[sx, sy];
                        sumR += c.R;
                        sumG += c.G;
                        sumB += c.B;
                        sumA += c.A;
                        cnt++;
                    }
                }
                dst[dx, dy] = new Rgba32((byte)(sumR / cnt), (byte)(sumG / cnt), (byte)(sumB / cnt), (byte)(sumA / cnt));
            }
        }
        return dst;
    }

    public static void DilateRgbBleed(Image<Rgba32> image, int passes = 2)
    {
        int w = image.Width;
        int h = image.Height;
        int[] dx = [1, -1, 0, 0];
        int[] dy = [0, 0, 1, -1];

        for (int p = 0; p < passes; p++)
        {
            var changes = new List<(int X, int Y, byte R, byte G, byte B)>();
            for (int y = 0; y < h; y++)
            {
                for (int x = 0; x < w; x++)
                {
                    if (image[x, y].A == 0)
                    {
                        int sumR = 0, sumG = 0, sumB = 0, count = 0;
                        for (int d = 0; d < 4; d++)
                        {
                            int nx = x + dx[d];
                            int ny = y + dy[d];
                            if (nx >= 0 && nx < w && ny >= 0 && ny < h && image[nx, ny].A > 0)
                            {
                                Rgba32 n = image[nx, ny];
                                sumR += n.R;
                                sumG += n.G;
                                sumB += n.B;
                                count++;
                            }
                        }
                        if (count > 0)
                        {
                            changes.Add((x, y, (byte)(sumR / count), (byte)(sumG / count), (byte)(sumB / count)));
                        }
                    }
                }
            }
            foreach (var (cx, cy, cr, cg, cb) in changes)
            {
                image[cx, cy] = new Rgba32(cr, cg, cb, 0);
            }
        }
    }

    public static void MeasureEdgeLumaStats(
        Image<Rgba32> bmp,
        out double opaqueInteriorMeanLuma,
        out double semiTransparentEdgeMeanLuma,
        out int edgePixelCount)
    {
        double opSum = 0.0;
        int opCnt = 0;
        double edSum = 0.0;
        edgePixelCount = 0;

        for (int y = 0; y < bmp.Height; y++)
        {
            for (int x = 0; x < bmp.Width; x++)
            {
                Rgba32 c = bmp[x, y];
                double luma = 0.2126 * c.R + 0.7152 * c.G + 0.0722 * c.B;
                if (c.A >= 250)
                {
                    opSum += luma;
                    opCnt++;
                }
                else if (c.A >= 24 && c.A <= 230)
                {
                    edSum += luma;
                    edgePixelCount++;
                }
            }
        }
        opaqueInteriorMeanLuma = opCnt > 0 ? Math.Round(opSum / opCnt, 2) : 0.0;
        semiTransparentEdgeMeanLuma = edgePixelCount > 0 ? Math.Round(edSum / edgePixelCount, 2) : 0.0;
    }
}
