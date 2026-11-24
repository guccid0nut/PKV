// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

using System.Buffers.Binary;
using System.Globalization;
using System.Net;
using System.Numerics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace DAWNBRINGER.WebPortal.UI.Services;

/// <summary>
/// Provides cleanroom-safe procedural SVG monogram badges and binary glTF 2.0 (.glb) 3D crystal placeholders
/// when retail game overlays (<c>games/dawnbringer-overlay</c> / <c>games/client</c>) are absent or explicitly bypassed.
/// </summary>
public static class CleanroomPlaceholderAssets
{
    public static bool IsForceCleanroomPlaceholders(IConfiguration? configuration)
    {
        if (configuration?.GetValue<bool>("Dawnbringer:ForceCleanroomPlaceholders") == true)
            return true;

        string? envFlag = Environment.GetEnvironmentVariable("KONGOR_FORCE_CLEANROOM_ASSETS");
        return string.Equals(envFlag, "1", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(envFlag, "true", StringComparison.OrdinalIgnoreCase);
    }

    public static string? ResolveDawnbringerOverlayRoot(string contentRootPath, IConfiguration? configuration)
    {
        if (IsForceCleanroomPlaceholders(configuration))
            return null;

        string? configured = configuration?["Dawnbringer:AssetOverlayPath"];
        if (!string.IsNullOrWhiteSpace(configured) && Directory.Exists(configured))
            return Path.GetFullPath(configured);

        DirectoryInfo? current = new(contentRootPath);
        for (int i = 0; i < 6 && current != null; i++)
        {
            string candidate = Path.Combine(current.FullName, "games", "dawnbringer-overlay");
            if (Directory.Exists(candidate))
                return candidate;

            current = current.Parent;
        }

        return null;
    }

    private static readonly HashSet<string> GenericPathTokens = new(StringComparer.OrdinalIgnoreCase)
    {
        "images", "heroes", "items", "abilities", "maps", "npcs", "courier", "couriers",
        "wards", "taunts", "announcers", "store", "icon", "icon_128", "icon2",
        "portrait", "hero", "model", "preview", "altavatar", "effects"
    };

    public static string BuildSvgBadge(string category, string rawSlug)
    {
        string normalizedSlug = (rawSlug ?? "asset").Trim().Replace('\\', '/').Trim('/');
        string[] rawSegments = normalizedSlug.Split('/', StringSplitOptions.RemoveEmptyEntries);
        List<string> meaningfulParts = [];
        for (int i = 0; i < rawSegments.Length; i++)
        {
            string seg = i == rawSegments.Length - 1
                ? Path.GetFileNameWithoutExtension(rawSegments[i])
                : rawSegments[i];
            if (!string.IsNullOrWhiteSpace(seg) && !GenericPathTokens.Contains(seg))
            {
                meaningfulParts.Add(seg);
            }
        }

        string candidateSlug = meaningfulParts.Count > 0
            ? string.Join(' ', meaningfulParts)
            : Path.GetFileNameWithoutExtension(normalizedSlug);

        string cleanName = candidateSlug
            .Replace('_', ' ')
            .Replace('-', ' ')
            .Replace('.', ' ')
            .Trim();

        if (cleanName.StartsWith("Hero ", StringComparison.OrdinalIgnoreCase))
            cleanName = cleanName[5..].Trim();
        else if (cleanName.StartsWith("Item ", StringComparison.OrdinalIgnoreCase))
            cleanName = cleanName[5..].Trim();
        else if (cleanName.StartsWith("Ability ", StringComparison.OrdinalIgnoreCase))
            cleanName = cleanName[8..].Trim();

        if (string.IsNullOrWhiteSpace(cleanName))
            cleanName = "PKV";

        string[] words = cleanName.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        string monogram = words.Length switch
        {
            0 => "PK",
            1 => words[0].Length >= 2 ? words[0][..2].ToUpperInvariant() : words[0].ToUpperInvariant(),
            _ => string.Concat(char.ToUpperInvariant(words[0][0]), char.ToUpperInvariant(words[1][0]))
        };

        string displayLabel = cleanName.Length > 18 ? cleanName[..17] + "…" : cleanName;
        string catLower = (category ?? "asset").Trim().ToLowerInvariant();

        string tagText = catLower switch
        {
            "heroes" => "HERO CODEX",
            "items" => "ITEM VAULT",
            "abilities" => "ABILITY",
            "maps" => "ARENA MAP",
            _ => "PKV ASSET"
        };

        byte[] seed = SHA256.HashData(Encoding.UTF8.GetBytes($"{catLower}:{cleanName.ToLowerInvariant()}"));
        double huePrimary = (BinaryPrimitives.ReadUInt16LittleEndian(seed.AsSpan(0, 2)) % 360);
        double hueSecondary = (huePrimary + 48.0 + (seed[2] % 90)) % 360.0;
        int rotDeg = seed[3] % 60;
        int gradAngle = seed[4] % 180;

        string accentHex = HslToHex(huePrimary, 0.85, 0.60);
        string secondaryHex = HslToHex(hueSecondary, 0.82, 0.56);
        string bgStartHex = HslToHex(huePrimary, 0.58, 0.16);
        string bgMidHex = HslToHex((huePrimary + 180.0) % 360.0, 0.42, 0.10);
        string bgEndHex = HslToHex(hueSecondary, 0.62, 0.22);
        string facetFillHex = HslToHex(huePrimary, 0.72, 0.34);
        string coreGlowHex = HslToHex(hueSecondary, 0.90, 0.66);

        string safeMonogram = WebUtility.HtmlEncode(monogram);
        string safeLabel = WebUtility.HtmlEncode(displayLabel);
        string safeTag = WebUtility.HtmlEncode(tagText);

        return $$"""
                 <svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 256 256" width="256" height="256" role="img" aria-label="{{safeLabel}}">
                   <defs>
                     <linearGradient id="bg" gradientTransform="rotate({{gradAngle}} 0.5 0.5)">
                       <stop offset="0%" stop-color="{{bgStartHex}}"/>
                       <stop offset="52%" stop-color="{{bgMidHex}}"/>
                       <stop offset="100%" stop-color="{{bgEndHex}}"/>
                     </linearGradient>
                     <radialGradient id="glow" cx="50%" cy="46%" r="54%">
                       <stop offset="0%" stop-color="{{coreGlowHex}}" stop-opacity="0.52"/>
                       <stop offset="65%" stop-color="{{facetFillHex}}" stop-opacity="0.24"/>
                       <stop offset="100%" stop-color="#090D16" stop-opacity="0"/>
                     </radialGradient>
                     <linearGradient id="ring" x1="0%" y1="0%" x2="100%" y2="100%">
                       <stop offset="0%" stop-color="{{accentHex}}"/>
                       <stop offset="100%" stop-color="{{secondaryHex}}"/>
                     </linearGradient>
                   </defs>
                   <rect x="4" y="4" width="248" height="248" rx="28" fill="url(#bg)" stroke="url(#ring)" stroke-width="5"/>
                   <circle cx="128" cy="124" r="104" fill="url(#glow)"/>
                   <g transform="rotate({{rotDeg}} 128 128)">
                     <polygon points="128,26 216,77 216,179 128,230 40,179 40,77" fill="{{facetFillHex}}" fill-opacity="0.32" stroke="{{accentHex}}" stroke-opacity="0.65" stroke-width="2.5"/>
                     <polygon points="128,48 198,128 128,208 58,128" fill="{{secondaryHex}}" fill-opacity="0.22" stroke="{{coreGlowHex}}" stroke-opacity="0.55" stroke-width="2"/>
                   </g>
                   <text x="128" y="48" text-anchor="middle" fill="{{accentHex}}" font-family="Segoe UI, Inter, sans-serif" font-size="13" font-weight="700" letter-spacing="1.5">{{safeTag}}</text>
                   <text x="128" y="146" text-anchor="middle" fill="#FFFFFF" stroke="#090D16" stroke-width="3" paint-order="stroke" font-family="Segoe UI, Inter, sans-serif" font-size="72" font-weight="900">{{safeMonogram}}</text>
                   <text x="128" y="212" text-anchor="middle" fill="#E2E8F0" font-family="Segoe UI, Inter, sans-serif" font-size="16" font-weight="700">{{safeLabel}}</text>
                 </svg>
                 """;
    }

    /// <summary>
    /// Synthesizes a valid binary glTF 2.0 (.glb) faceted crystal octahedron mesh in memory so Three.js viewports
    /// render a smooth, interactive 3D geometric totem with zero retail game assets and zero HTTP 404 errors.
    /// </summary>
    public static byte[] BuildCrystalGlb(string seedKey)
    {
        byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(seedKey ?? "pkv"));
        float r = 0.20f + (hash[0] / 255.0f) * 0.55f;
        float g = 0.45f + (hash[1] / 255.0f) * 0.45f;
        float b = 0.65f + (hash[2] / 255.0f) * 0.35f;

        Vector3 top = new(0f, 38f, 0f);
        Vector3 bottom = new(0f, 2f, 0f);
        Vector3 p0 = new(14f, 20f, 0f);
        Vector3 p1 = new(0f, 20f, 14f);
        Vector3 p2 = new(-14f, 20f, 0f);
        Vector3 p3 = new(0f, 20f, -14f);

        (Vector3 A, Vector3 B, Vector3 C)[] faces =
        [
            (top, p1, p0),
            (top, p2, p1),
            (top, p3, p2),
            (top, p0, p3),
            (bottom, p0, p1),
            (bottom, p1, p2),
            (bottom, p2, p3),
            (bottom, p3, p0)
        ];

        const int vertexCount = 24;
        const int posByteLen = vertexCount * 12;
        const int normByteLen = vertexCount * 12;
        int totalBinLen = posByteLen + normByteLen;
        byte[] binChunk = new byte[totalBinLen];

        Vector3 minPos = new(float.MaxValue);
        Vector3 maxPos = new(float.MinValue);

        int posOffset = 0;
        int normOffset = posByteLen;
        foreach ((Vector3 a, Vector3 bPt, Vector3 c) in faces)
        {
            Vector3 normal = Vector3.Normalize(Vector3.Cross(bPt - a, c - a));
            Vector3[] tri = [a, bPt, c];
            foreach (Vector3 v in tri)
            {
                minPos = Vector3.Min(minPos, v);
                maxPos = Vector3.Max(maxPos, v);

                WriteFloatLE(binChunk, ref posOffset, v.X);
                WriteFloatLE(binChunk, ref posOffset, v.Y);
                WriteFloatLE(binChunk, ref posOffset, v.Z);

                WriteFloatLE(binChunk, ref normOffset, normal.X);
                WriteFloatLE(binChunk, ref normOffset, normal.Y);
                WriteFloatLE(binChunk, ref normOffset, normal.Z);
            }
        }

        var gltfObj = new
        {
            asset = new { version = "2.0", generator = "ProjectKONGOR-V Cleanroom Crystal Synthesizer" },
            scene = 0,
            scenes = new[] { new { nodes = new[] { 0 } } },
            nodes = new[] { new { name = "PKV_Cleanroom_Crystal", mesh = 0 } },
            materials = new[]
            {
                new
                {
                    name = "PKV_Crystal_PBR",
                    pbrMetallicRoughness = new
                    {
                        baseColorFactor = new[] { r, g, b, 1.0f },
                        metallicFactor = 0.35f,
                        roughnessFactor = 0.22f
                    },
                    emissiveFactor = new[] { r * 0.25f, g * 0.25f, b * 0.35f },
                    doubleSided = true
                }
            },
            meshes = new[]
            {
                new
                {
                    name = "CrystalMesh",
                    primitives = new[]
                    {
                        new
                        {
                            attributes = new { POSITION = 0, NORMAL = 1 },
                            material = 0
                        }
                    }
                }
            },
            buffers = new[] { new { byteLength = totalBinLen } },
            bufferViews = new[]
            {
                new { buffer = 0, byteOffset = 0, byteLength = posByteLen, target = 34962 },
                new { buffer = 0, byteOffset = posByteLen, byteLength = normByteLen, target = 34962 }
            },
            accessors = new object[]
            {
                new
                {
                    bufferView = 0,
                    byteOffset = 0,
                    componentType = 5126,
                    count = vertexCount,
                    type = "VEC3",
                    min = new[] { minPos.X, minPos.Y, minPos.Z },
                    max = new[] { maxPos.X, maxPos.Y, maxPos.Z }
                },
                new
                {
                    bufferView = 1,
                    byteOffset = 0,
                    componentType = 5126,
                    count = vertexCount,
                    type = "VEC3"
                }
            }
        };

        byte[] jsonRaw = JsonSerializer.SerializeToUtf8Bytes(gltfObj);
        int jsonPaddedLen = (jsonRaw.Length + 3) & ~3;
        int binPaddedLen = (binChunk.Length + 3) & ~3;
        int totalGlbLen = 12 + 8 + jsonPaddedLen + 8 + binPaddedLen;

        byte[] glb = new byte[totalGlbLen];
        Span<byte> span = glb;

        // 12-byte GLB header: magic 'glTF' (0x46546C67), version 2, total length
        BinaryPrimitives.WriteUInt32LittleEndian(span[0..4], 0x46546C67u);
        BinaryPrimitives.WriteUInt32LittleEndian(span[4..8], 2u);
        BinaryPrimitives.WriteUInt32LittleEndian(span[8..12], (uint)totalGlbLen);

        // Chunk 0: JSON (0x4E4F534A), padded with 0x20 spaces per glTF 2.0 spec
        BinaryPrimitives.WriteUInt32LittleEndian(span[12..16], (uint)jsonPaddedLen);
        BinaryPrimitives.WriteUInt32LittleEndian(span[16..20], 0x4E4F534Au);
        jsonRaw.CopyTo(span[20..(20 + jsonRaw.Length)]);
        for (int i = jsonRaw.Length; i < jsonPaddedLen; i++)
            span[20 + i] = 0x20;

        // Chunk 1: BIN (0x004E4942), padded with 0x00 bytes per glTF 2.0 spec
        int binHeaderStart = 20 + jsonPaddedLen;
        BinaryPrimitives.WriteUInt32LittleEndian(span[binHeaderStart..(binHeaderStart + 4)], (uint)binPaddedLen);
        BinaryPrimitives.WriteUInt32LittleEndian(span[(binHeaderStart + 4)..(binHeaderStart + 8)], 0x004E4942u);
        binChunk.CopyTo(span[(binHeaderStart + 8)..(binHeaderStart + 8 + binChunk.Length)]);

        return glb;
    }

    private static string HslToHex(double hDeg, double s, double l)
    {
        double c = (1.0 - Math.Abs(2.0 * l - 1.0)) * s;
        double x = c * (1.0 - Math.Abs((hDeg / 60.0) % 2.0 - 1.0));
        double m = l - c / 2.0;

        (double r1, double g1, double b1) = hDeg switch
        {
            < 60.0 => (c, x, 0.0),
            < 120.0 => (x, c, 0.0),
            < 180.0 => (0.0, c, x),
            < 240.0 => (0.0, x, c),
            < 300.0 => (x, 0.0, c),
            _ => (c, 0.0, x)
        };

        int r = Math.Clamp((int)Math.Round((r1 + m) * 255.0), 0, 255);
        int g = Math.Clamp((int)Math.Round((g1 + m) * 255.0), 0, 255);
        int b = Math.Clamp((int)Math.Round((b1 + m) * 255.0), 0, 255);
        return string.Create(CultureInfo.InvariantCulture, $"#{r:X2}{g:X2}{b:X2}");
    }

    private static void WriteFloatLE(byte[] buffer, ref int offset, float value)
    {
        BinaryPrimitives.WriteSingleLittleEndian(buffer.AsSpan(offset, 4), value);
        offset += 4;
    }
}
