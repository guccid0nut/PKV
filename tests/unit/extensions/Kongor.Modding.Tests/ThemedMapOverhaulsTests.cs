// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

using System.Text;
using System.Xml.Linq;
using FluentAssertions;
using Kongor.Modding.Core.Models;
using Kongor.Modding.Core.Packaging;
using Kongor.Modding.Core.Parsing;
using Kongor.Modding.Resolution;
using Kongor.Modding.Vfs;
using Xunit;

namespace Kongor.Modding.Tests;

/// <summary>
///     Comprehensive verification fixture for the 6 Themed Map Overhaul Mods:
///     1. Neon Caldavar Theme (neon_caldavar_theme.honmod)
///     2. Frozen Caldavar Theme (frozen_caldavar_theme.honmod)
///     3. Clean Tournament Arena (clean_tournament_arena.honmod)
///     4. Cyberpunk Mid Wars (cyberpunk_midwars.honmod)
///     5. Frozen Mid Wars (frozen_midwars.honmod)
///     6. Cyberpunk Devo Wars (cyberpunk_devowars.honmod)
/// </summary>
[Collection("ModdingDiskCollection")]
public sealed class ThemedMapOverhaulsTests : IDisposable
{
    private readonly string _clientDir;
    private readonly string _modsDir;
    private readonly string _tempDir;

    public ThemedMapOverhaulsTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "ThemedMapsTest_" + Guid.NewGuid().ToString("N"));
        _clientDir = Path.Combine(_tempDir, "client");
        _modsDir = Path.Combine(_tempDir, "mods");

        Directory.CreateDirectory(_tempDir);
        Directory.CreateDirectory(Path.Combine(_clientDir, "game"));
        Directory.CreateDirectory(_modsDir);

        KongorModRemediationInitializer.EnsureRemediationApplied();
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
            try
            {
                Directory.Delete(_tempDir, true);
            }
            catch
            {
                /* ignore Windows file locks */
            }
    }

    private static string FindRepoRoot()
    {
        string current = AppContext.BaseDirectory;
        while (!string.IsNullOrEmpty(current))
        {
            if (File.Exists(Path.Combine(current, "ProjectKongor.slnx")) ||
                File.Exists(Path.Combine(current, "PROJECT.md")))
                return current;
            string? parent = Path.GetDirectoryName(current);
            if (string.IsNullOrEmpty(parent) || parent == current) break;
            current = parent;
        }

        return Directory.GetCurrentDirectory();
    }

    #region Asset Synthesizers and Fixture Generators

    public static class MapTextureSynthesizer
    {
        public static byte[] CreateValidPngBytes()
        {
            return
            [
                0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, // PNG signature
                0x00, 0x00, 0x00, 0x0D, // IHDR chunk length
                0x49, 0x48, 0x44, 0x52, // "IHDR"
                0x00, 0x00, 0x00, 0x40, // Width: 64
                0x00, 0x00, 0x00, 0x40, // Height: 64
                0x08, 0x06, 0x00, 0x00, 0x00, // 8-bit RGBA
                0xE2, 0x98, 0x61, 0x99, // CRC
                0x00, 0x00, 0x00, 0x0A, // IDAT length
                0x49, 0x44, 0x41, 0x54, // "IDAT"
                0x78, 0x9C, 0x63, 0x00, 0x01, 0x00, 0x00, 0x05, 0x00, 0x01,
                0x0D, 0x0A, 0x2D, 0xB4, // CRC
                0x00, 0x00, 0x00, 0x00, // IEND length
                0x49, 0x45, 0x4E, 0x44, // "IEND"
                0xAE, 0x42, 0x60, 0x82 // CRC
            ];
        }

        public static byte[] CreateTga(short width, short height, byte b, byte g, byte r, byte a = 255)
        {
            int pixelCount = width * height;
            byte[] buffer = new byte[18 + pixelCount * 4];

            buffer[2] = 2; // uncompressed true-color
            buffer[12] = (byte)(width & 0xFF);
            buffer[13] = (byte)((width >> 8) & 0xFF);
            buffer[14] = (byte)(height & 0xFF);
            buffer[15] = (byte)((height >> 8) & 0xFF);
            buffer[16] = 32; // 32 bits per pixel (BGRA)
            buffer[17] = 0x08; // 8-bit alpha, bottom-left origin (standard K2)

            int offset = 18;
            for (int i = 0; i < pixelCount; i++)
            {
                buffer[offset++] = b;
                buffer[offset++] = g;
                buffer[offset++] = r;
                buffer[offset++] = a;
            }

            return buffer;
        }

        public static byte[] CreateNormalMapTga(short width = 64, short height = 64)
        {
            // Tangent-space normal vector [0, 0, 1] encoded as RGB [128, 128, 255] -> B=255, G=128, R=128
            return CreateTga(width, height, 255, 128, 128);
        }

        public static byte[] CreateGridTga(short width = 64, short height = 64, byte gridR = 0, byte gridG = 220,
            byte gridB = 255, byte bgR = 17, byte bgG = 17, byte bgB = 24)
        {
            int pixelCount = width * height;
            byte[] buffer = new byte[18 + pixelCount * 4];

            buffer[2] = 2;
            buffer[12] = (byte)(width & 0xFF);
            buffer[13] = (byte)((width >> 8) & 0xFF);
            buffer[14] = (byte)(height & 0xFF);
            buffer[15] = (byte)((height >> 8) & 0xFF);
            buffer[16] = 32;
            buffer[17] = 0x08;

            int offset = 18;
            for (int y = 0; y < height; y++)
            for (int x = 0; x < width; x++)
            {
                bool isGrid = x % 8 == 0 || y % 8 == 0;
                buffer[offset++] = isGrid ? gridB : bgB;
                buffer[offset++] = isGrid ? gridG : bgG;
                buffer[offset++] = isGrid ? gridR : bgR;
                buffer[offset++] = 255;
            }

            return buffer;
        }

        public static byte[] CreateRadialGlowTga(short width = 64, short height = 64, byte r = 0, byte g = 200,
            byte b = 255)
        {
            int pixelCount = width * height;
            byte[] buffer = new byte[18 + pixelCount * 4];

            buffer[2] = 2;
            buffer[12] = (byte)(width & 0xFF);
            buffer[13] = (byte)((width >> 8) & 0xFF);
            buffer[14] = (byte)(height & 0xFF);
            buffer[15] = (byte)((height >> 8) & 0xFF);
            buffer[16] = 32;
            buffer[17] = 0x08;

            double cx = width / 2.0;
            double cy = height / 2.0;
            double maxR = Math.Sqrt(cx * cx + cy * cy);

            int offset = 18;
            for (int y = 0; y < height; y++)
            for (int x = 0; x < width; x++)
            {
                double dx = x - cx;
                double dy = y - cy;
                double dist = Math.Sqrt(dx * dx + dy * dy);
                double norm = Math.Max(0.0, 1.0 - dist / (cx * 0.95));
                byte alpha = (byte)(norm * norm * 255.0);

                buffer[offset++] = b;
                buffer[offset++] = g;
                buffer[offset++] = r;
                buffer[offset++] = alpha;
            }

            return buffer;
        }

        public static byte[] CreateSnowflakeTga(short width = 64, short height = 64)
        {
            int pixelCount = width * height;
            byte[] buffer = new byte[18 + pixelCount * 4];

            buffer[2] = 2;
            buffer[12] = (byte)(width & 0xFF);
            buffer[13] = (byte)((width >> 8) & 0xFF);
            buffer[14] = (byte)(height & 0xFF);
            buffer[15] = (byte)((height >> 8) & 0xFF);
            buffer[16] = 32;
            buffer[17] = 0x08;

            int cx = width / 2;
            int cy = height / 2;

            int offset = 18;
            for (int y = 0; y < height; y++)
            for (int x = 0; x < width; x++)
            {
                int dx = Math.Abs(x - cx);
                int dy = Math.Abs(y - cy);
                bool isBranch = (x == cx && dy < 28) || (y == cy && dx < 28) || (dx == dy && dx < 20);
                byte val = isBranch ? (byte)255 : (byte)0;

                buffer[offset++] = val; // B
                buffer[offset++] = val; // G
                buffer[offset++] = val; // R
                buffer[offset++] = val; // A
            }

            return buffer;
        }
    }

    internal static async Task EnsureAllSixModsOnDiskAsync(string modsRoot)
    {
        Directory.CreateDirectory(modsRoot);
        byte[] iconPng = MapTextureSynthesizer.CreateValidPngBytes();

        // 1. Neon Caldavar
        await EnsureNeonCaldavarAssetsOnDiskAsync(Path.Combine(modsRoot, "neon_caldavar_theme"), iconPng);

        // 2. Frozen Caldavar
        await EnsureFrozenCaldavarAssetsOnDiskAsync(Path.Combine(modsRoot, "frozen_caldavar_theme"), iconPng);

        // 3. Clean Tournament
        await EnsureCleanTournamentAssetsOnDiskAsync(Path.Combine(modsRoot, "clean_tournament_arena"), iconPng);

        // 4. Cyberpunk Mid Wars
        await EnsureCyberpunkMidWarsAssetsOnDiskAsync(Path.Combine(modsRoot, "cyberpunk_midwars"), iconPng);

        // 5. Frozen Mid Wars
        await EnsureFrozenMidWarsAssetsOnDiskAsync(Path.Combine(modsRoot, "frozen_midwars"), iconPng);

        // 6. Cyberpunk Devo Wars
        await EnsureCyberpunkDevoWarsAssetsOnDiskAsync(Path.Combine(modsRoot, "cyberpunk_devowars"), iconPng);
    }

    internal static async Task EnsureNeonCaldavarAssetsOnDiskAsync(string modDir, byte[] iconPng)
    {
        Directory.CreateDirectory(modDir);
        string waterMatDir = Path.Combine(modDir, "world", "water", "materials");
        string waterTexDir = Path.Combine(modDir, "world", "water", "textures");
        string terrainMatDir = Path.Combine(modDir, "world", "terrain", "materials");
        string terrainTexDir = Path.Combine(modDir, "world", "terrain", "textures");
        string runesEffectDir = Path.Combine(modDir, "world", "props", "runes", "effects");
        string runesMatDir = Path.Combine(modDir, "world", "props", "runes", "materials");
        string runesTexDir = Path.Combine(modDir, "world", "props", "runes", "textures");

        Directory.CreateDirectory(waterMatDir);
        Directory.CreateDirectory(waterTexDir);
        Directory.CreateDirectory(terrainMatDir);
        Directory.CreateDirectory(terrainTexDir);
        Directory.CreateDirectory(runesEffectDir);
        Directory.CreateDirectory(runesMatDir);
        Directory.CreateDirectory(runesTexDir);

        await File.WriteAllBytesAsync(Path.Combine(modDir, "icon.png"), iconPng);

        // Water Textures
        byte[] neonWaterDiff = MapTextureSynthesizer.CreateTga(64, 64, 255, 215, 0); // Cyan
        byte[] neonWaterNorm = MapTextureSynthesizer.CreateNormalMapTga();
        byte[] neonWaterSpec = MapTextureSynthesizer.CreateTga(64, 64, 255, 0, 230); // Magenta specular

        await File.WriteAllBytesAsync(Path.Combine(waterTexDir, "neon_water_diffuse.tga"), neonWaterDiff);
        await File.WriteAllBytesAsync(Path.Combine(waterTexDir, "neon_water_normal.tga"), neonWaterNorm);
        await File.WriteAllBytesAsync(Path.Combine(waterTexDir, "neon_water_specular.tga"), neonWaterSpec);

        // Terrain Textures
        byte[] cyberGridDiff = MapTextureSynthesizer.CreateGridTga();
        byte[] cyberGridNorm = MapTextureSynthesizer.CreateNormalMapTga();

        string[] terrainDiffs =
        [
            "reborn_dirtmix_d.tga", "reborn_dirt_d.tga", "reborn_mud_d.tga",
            "reborn_grass_d.tga", "reborn_mossypath_d.tga", "ancients_pathgrassy_d.tga",
            "reborn_cliff_d.tga", "reborn_base_d.tga", "reborn_base_hellbourne_d.tga",
            "reborn_base2_d.tga", "ancients_ground_river01_d.tga", "cyber_grid_d.tga"
        ];

        foreach (string td in terrainDiffs)
        {
            string path = Path.Combine(terrainTexDir, td);
            if (!File.Exists(path)) await File.WriteAllBytesAsync(path, cyberGridDiff);
        }

        string[] terrainNorms = ["reborn_cliff_n.tga", "ancients_ground_river03_n.tga", "cyber_grid_n.tga"];
        foreach (string tn in terrainNorms)
        {
            string path = Path.Combine(terrainTexDir, tn);
            if (!File.Exists(path)) await File.WriteAllBytesAsync(path, cyberGridNorm);
        }

        // Rune Textures
        await File.WriteAllBytesAsync(Path.Combine(runesTexDir, "rune_neon_d.tga"),
            MapTextureSynthesizer.CreateRadialGlowTga(64, 64, 0, 220));
    }

    internal static async Task EnsureFrozenCaldavarAssetsOnDiskAsync(string modDir, byte[] iconPng)
    {
        Directory.CreateDirectory(modDir);
        string waterMatDir = Path.Combine(modDir, "world", "water", "materials");
        string waterTexDir = Path.Combine(modDir, "world", "water", "textures");
        string terrainMatDir = Path.Combine(modDir, "world", "terrain", "materials");
        string terrainTexDir = Path.Combine(modDir, "world", "terrain", "textures");
        string effectsDir = Path.Combine(modDir, "world", "effects");
        string effectsMatDir = Path.Combine(modDir, "world", "effects", "materials");
        string effectsTexDir = Path.Combine(modDir, "world", "effects", "textures");

        Directory.CreateDirectory(waterMatDir);
        Directory.CreateDirectory(waterTexDir);
        Directory.CreateDirectory(terrainMatDir);
        Directory.CreateDirectory(terrainTexDir);
        Directory.CreateDirectory(effectsDir);
        Directory.CreateDirectory(effectsMatDir);
        Directory.CreateDirectory(effectsTexDir);

        await File.WriteAllBytesAsync(Path.Combine(modDir, "icon.png"), iconPng);

        // Water Textures
        byte[] iceDiff = MapTextureSynthesizer.CreateTga(64, 64, 255, 240, 210);
        byte[] iceNorm = MapTextureSynthesizer.CreateNormalMapTga();
        byte[] iceSpec = MapTextureSynthesizer.CreateTga(64, 64, 255, 255, 255);

        await File.WriteAllBytesAsync(Path.Combine(waterTexDir, "glacial_ice_diffuse.tga"), iceDiff);
        await File.WriteAllBytesAsync(Path.Combine(waterTexDir, "glacial_ice_normal.tga"), iceNorm);
        await File.WriteAllBytesAsync(Path.Combine(waterTexDir, "glacial_ice_specular.tga"), iceSpec);

        // Winter Terrain Textures
        byte[] snowDiff = MapTextureSynthesizer.CreateTga(64, 64, 255, 250, 245);
        byte[] snowNorm = MapTextureSynthesizer.CreateNormalMapTga();

        string[] winterTerrain =
        [
            "snow_powder_d.tga", "ice_cliff_d.tga", "frozen_dirt_d.tga",
            "reborn_dirtmix_d.tga", "reborn_grass_d.tga", "reborn_base_d.tga",
            "reborn_base_hellbourne_d.tga"
        ];

        foreach (string wt in winterTerrain)
        {
            string path = Path.Combine(terrainTexDir, wt);
            if (!File.Exists(path)) await File.WriteAllBytesAsync(path, snowDiff);
        }

        string[] winterNorms = ["snow_powder_n.tga", "ice_cliff_n.tga"];
        foreach (string wn in winterNorms)
        {
            string path = Path.Combine(terrainTexDir, wn);
            if (!File.Exists(path)) await File.WriteAllBytesAsync(path, snowNorm);
        }

        // Snowflake Texture
        await File.WriteAllBytesAsync(Path.Combine(effectsTexDir, "snowflake.tga"),
            MapTextureSynthesizer.CreateSnowflakeTga());
    }

    private static readonly SemaphoreSlim ModAssetDiskLock = new(1, 1);

    private static async Task WriteAssetIfMissingAsync(string path, Func<byte[]> contentFactory)
    {
        if (File.Exists(path)) return;
        await ModAssetDiskLock.WaitAsync();
        try
        {
            if (!File.Exists(path))
            {
                await File.WriteAllBytesAsync(path, contentFactory());
            }
        }
        finally
        {
            ModAssetDiskLock.Release();
        }
    }

    internal static async Task EnsureCleanTournamentAssetsOnDiskAsync(string modDir, byte[] iconPng)
    {
        Directory.CreateDirectory(modDir);
        string waterMatDir = Path.Combine(modDir, "world", "water", "materials");
        string waterTexDir = Path.Combine(modDir, "world", "water", "textures");
        string terrainMatDir = Path.Combine(modDir, "world", "terrain", "materials");
        string terrainTexDir = Path.Combine(modDir, "world", "terrain", "textures");
        string foliageMatDir = Path.Combine(modDir, "world", "foliage", "materials");
        string foliageTexDir = Path.Combine(modDir, "world", "foliage", "textures");

        Directory.CreateDirectory(waterMatDir);
        Directory.CreateDirectory(waterTexDir);
        Directory.CreateDirectory(terrainMatDir);
        Directory.CreateDirectory(terrainTexDir);
        Directory.CreateDirectory(foliageMatDir);
        Directory.CreateDirectory(foliageTexDir);

        await WriteAssetIfMissingAsync(Path.Combine(modDir, "icon.png"), () => iconPng);

        // Water Textures
        await WriteAssetIfMissingAsync(Path.Combine(waterTexDir, "clean_water_d.tga"),
            () => MapTextureSynthesizer.CreateTga(64, 64, 100, 90, 65));
        await WriteAssetIfMissingAsync(Path.Combine(waterTexDir, "clean_water_n.tga"),
            () => MapTextureSynthesizer.CreateNormalMapTga());

        // Flat Esports Matte Textures
        await WriteAssetIfMissingAsync(Path.Combine(terrainTexDir, "flat_n.tga"),
            () => MapTextureSynthesizer.CreateNormalMapTga());

        await WriteAssetIfMissingAsync(Path.Combine(terrainTexDir, "clean_dirt_d.tga"),
            () => MapTextureSynthesizer.CreateTga(64, 64, 53, 59, 62)); // #3E3B35
        await WriteAssetIfMissingAsync(Path.Combine(terrainTexDir, "clean_grass_d.tga"),
            () => MapTextureSynthesizer.CreateTga(64, 64, 59, 68, 62)); // #3E443B Legion
        await WriteAssetIfMissingAsync(Path.Combine(terrainTexDir, "clean_cliff_d.tga"),
            () => MapTextureSynthesizer.CreateTga(64, 64, 40, 43, 42)); // #2A2B28
        await WriteAssetIfMissingAsync(Path.Combine(terrainTexDir, "clean_base_d.tga"),
            () => MapTextureSynthesizer.CreateTga(64, 64, 78, 69, 64)); // #40454E
        await WriteAssetIfMissingAsync(Path.Combine(terrainTexDir, "clean_base_hellbourne_d.tga"),
            () => MapTextureSynthesizer.CreateTga(64, 64, 43, 46, 58)); // #3A2E2B Hellbourne
        await WriteAssetIfMissingAsync(Path.Combine(terrainTexDir, "clean_riverbed_d.tga"),
            () => MapTextureSynthesizer.CreateTga(64, 64, 42, 37, 34)); // #22252A

        await WriteAssetIfMissingAsync(Path.Combine(terrainTexDir, "reborn_dirtmix_d.tga"),
            () => MapTextureSynthesizer.CreateTga(64, 64, 53, 59, 62));
        await WriteAssetIfMissingAsync(Path.Combine(terrainTexDir, "reborn_dirt_d.tga"),
            () => MapTextureSynthesizer.CreateTga(64, 64, 53, 59, 62));
        await WriteAssetIfMissingAsync(Path.Combine(terrainTexDir, "reborn_grass_d.tga"),
            () => MapTextureSynthesizer.CreateTga(64, 64, 59, 68, 62));
        await WriteAssetIfMissingAsync(Path.Combine(terrainTexDir, "reborn_cliff_d.tga"),
            () => MapTextureSynthesizer.CreateTga(64, 64, 40, 43, 42));
        await WriteAssetIfMissingAsync(Path.Combine(terrainTexDir, "reborn_base_d.tga"),
            () => MapTextureSynthesizer.CreateTga(64, 64, 78, 69, 64));
        await WriteAssetIfMissingAsync(Path.Combine(terrainTexDir, "reborn_base_hellbourne_d.tga"),
            () => MapTextureSynthesizer.CreateTga(64, 64, 43, 46, 58));

        // Foliage Textures
        await WriteAssetIfMissingAsync(Path.Combine(foliageTexDir, "clean_leaf_d.tga"),
            () => MapTextureSynthesizer.CreateTga(64, 64, 60, 90, 70));
    }

    internal static async Task EnsureCyberpunkMidWarsAssetsOnDiskAsync(string modDir, byte[] iconPng)
    {
        Directory.CreateDirectory(modDir);
        string bridgeDir = Path.Combine(modDir, "world", "props", "midwars_bridge");
        string waterMatDir = Path.Combine(modDir, "world", "water", "materials");
        string waterTexDir = Path.Combine(modDir, "world", "water", "textures");
        string terrainMatDir = Path.Combine(modDir, "world", "terrain", "materials");
        string terrainTexDir = Path.Combine(modDir, "world", "terrain", "textures");

        Directory.CreateDirectory(bridgeDir);
        Directory.CreateDirectory(waterMatDir);
        Directory.CreateDirectory(waterTexDir);
        Directory.CreateDirectory(terrainMatDir);
        Directory.CreateDirectory(terrainTexDir);

        await WriteAssetIfMissingAsync(Path.Combine(modDir, "icon.png"), () => iconPng);

        // Bridge Textures
        await WriteAssetIfMissingAsync(Path.Combine(bridgeDir, "bridge_diffuse.tga"),
            () => MapTextureSynthesizer.CreateTga(64, 64, 70, 60, 50));
        await WriteAssetIfMissingAsync(Path.Combine(bridgeDir, "bridge_glow.tga"),
            () => MapTextureSynthesizer.CreateRadialGlowTga(64, 64, 0, 220));
        await WriteAssetIfMissingAsync(Path.Combine(bridgeDir, "bridge_normal.tga"),
            () => MapTextureSynthesizer.CreateNormalMapTga());

        // Water Textures
        await WriteAssetIfMissingAsync(Path.Combine(waterTexDir, "midwars_cyber_river_d.tga"),
            () => MapTextureSynthesizer.CreateTga(64, 64, 255, 200, 0));
        await WriteAssetIfMissingAsync(Path.Combine(waterTexDir, "midwars_cyber_river_n.tga"),
            () => MapTextureSynthesizer.CreateNormalMapTga());

        // Terrain Textures
        await WriteAssetIfMissingAsync(Path.Combine(terrainTexDir, "midwars_cyber_floor_d.tga"),
            () => MapTextureSynthesizer.CreateGridTga(64, 64, 0, 180, 255, 20, 22, 30));
        await WriteAssetIfMissingAsync(Path.Combine(terrainTexDir, "midwars_cyber_floor_n.tga"),
            () => MapTextureSynthesizer.CreateNormalMapTga());
        await WriteAssetIfMissingAsync(Path.Combine(terrainTexDir, "reborn_dirtmix_d.tga"),
            () => MapTextureSynthesizer.CreateGridTga(64, 64, 0, 180, 255, 20, 22, 30));
        await WriteAssetIfMissingAsync(Path.Combine(terrainTexDir, "reborn_grass_d.tga"),
            () => MapTextureSynthesizer.CreateGridTga(64, 64, 0, 180, 255, 20, 22, 30));
    }

    internal static async Task EnsureFrozenMidWarsAssetsOnDiskAsync(string modDir, byte[] iconPng)
    {
        Directory.CreateDirectory(modDir);
        string bridgeDir = Path.Combine(modDir, "world", "props", "midwars_bridge");
        string bridgeMatDir = Path.Combine(modDir, "world", "props", "midwars_bridge", "materials");
        string bridgeTexDir = Path.Combine(modDir, "world", "props", "midwars_bridge", "textures");
        string waterMatDir = Path.Combine(modDir, "world", "water", "materials");
        string waterTexDir = Path.Combine(modDir, "world", "water", "textures");
        string terrainMatDir = Path.Combine(modDir, "world", "terrain", "materials");
        string terrainTexDir = Path.Combine(modDir, "world", "terrain", "textures");

        Directory.CreateDirectory(bridgeDir);
        Directory.CreateDirectory(bridgeMatDir);
        Directory.CreateDirectory(bridgeTexDir);
        Directory.CreateDirectory(waterMatDir);
        Directory.CreateDirectory(waterTexDir);
        Directory.CreateDirectory(terrainMatDir);
        Directory.CreateDirectory(terrainTexDir);

        await WriteAssetIfMissingAsync(Path.Combine(modDir, "icon.png"), () => iconPng);

        // Bridge & Icicle Textures
        await WriteAssetIfMissingAsync(Path.Combine(bridgeDir, "bridge_diffuse.tga"),
            () => MapTextureSynthesizer.CreateTga(64, 64, 240, 230, 210));
        await WriteAssetIfMissingAsync(Path.Combine(bridgeTexDir, "icicle_d.tga"),
            () => MapTextureSynthesizer.CreateRadialGlowTga(64, 64, 200, 240));

        // Ice Sheet Water Textures
        await WriteAssetIfMissingAsync(Path.Combine(waterTexDir, "midwars_ice_sheet_d.tga"),
            () => MapTextureSynthesizer.CreateTga(64, 64, 255, 230, 190));
        await WriteAssetIfMissingAsync(Path.Combine(waterTexDir, "midwars_ice_cracks_n.tga"),
            () => MapTextureSynthesizer.CreateNormalMapTga());
        await WriteAssetIfMissingAsync(Path.Combine(waterTexDir, "midwars_ice_specular.tga"),
            () => MapTextureSynthesizer.CreateTga(64, 64, 255, 255, 245));

        // Snow Lane Textures
        await WriteAssetIfMissingAsync(Path.Combine(terrainTexDir, "midwars_snow_d.tga"),
            () => MapTextureSynthesizer.CreateTga(64, 64, 255, 245, 235));
        await WriteAssetIfMissingAsync(Path.Combine(terrainTexDir, "midwars_frosted_ramp_d.tga"),
            () => MapTextureSynthesizer.CreateTga(64, 64, 245, 225, 200));
        await WriteAssetIfMissingAsync(Path.Combine(terrainTexDir, "midwars_snow_n.tga"),
            () => MapTextureSynthesizer.CreateNormalMapTga());
    }

    internal static async Task EnsureCyberpunkDevoWarsAssetsOnDiskAsync(string modDir, byte[] iconPng)
    {
        Directory.CreateDirectory(modDir);
        string devoDir = Path.Combine(modDir, "heroes", "devourer", "ability_01");
        string devoEffectDir = Path.Combine(modDir, "heroes", "devourer", "ability_01", "effects");
        string sharedMatDir = Path.Combine(modDir, "shared", "effects", "materials");
        string sharedTexDir = Path.Combine(modDir, "shared", "effects", "textures");
        string waterMatDir = Path.Combine(modDir, "world", "water", "materials");
        string waterTexDir = Path.Combine(modDir, "world", "water", "textures");
        string padDir = Path.Combine(modDir, "world", "props", "devowars_pad");

        Directory.CreateDirectory(devoDir);
        Directory.CreateDirectory(devoEffectDir);
        Directory.CreateDirectory(sharedMatDir);
        Directory.CreateDirectory(sharedTexDir);
        Directory.CreateDirectory(waterMatDir);
        Directory.CreateDirectory(waterTexDir);
        Directory.CreateDirectory(padDir);

        await WriteAssetIfMissingAsync(Path.Combine(modDir, "icon.png"), () => iconPng);

        // Laser Hook Textures
        await WriteAssetIfMissingAsync(Path.Combine(sharedTexDir, "laser_core.tga"),
            () => MapTextureSynthesizer.CreateTga(64, 64, 255, 255, 255));
        await WriteAssetIfMissingAsync(Path.Combine(sharedTexDir, "laser_glow.tga"),
            () => MapTextureSynthesizer.CreateRadialGlowTga());
        await WriteAssetIfMissingAsync(Path.Combine(sharedTexDir, "laser_spark.tga"),
            () => MapTextureSynthesizer.CreateRadialGlowTga(64, 64, 255, 100, 220));

        // Neon Abyss Chasm Textures
        await WriteAssetIfMissingAsync(Path.Combine(waterTexDir, "neon_abyss_grid_d.tga"),
            () => MapTextureSynthesizer.CreateGridTga(64, 64, 0, 220, 255, 5, 5, 12));
        await WriteAssetIfMissingAsync(Path.Combine(waterTexDir, "neon_abyss_n.tga"),
            () => MapTextureSynthesizer.CreateNormalMapTga());

        // Bounce Pad Textures
        await WriteAssetIfMissingAsync(Path.Combine(padDir, "pad_diffuse.tga"),
            () => MapTextureSynthesizer.CreateTga(64, 64, 80, 70, 60));
        await WriteAssetIfMissingAsync(Path.Combine(padDir, "pad_glow.tga"),
            () => MapTextureSynthesizer.CreateRadialGlowTga(64, 64, 0, 220));
    }

    #endregion

    #region Tier 1: Manifest Parsing & Metadata Verification (6 Tests)

    [Fact]
    public async Task NeonCaldavar_ManifestAndPackage_ParsesValidAssetCounts()
    {
        if (!NexusTestEnvironment.HasGameAssets) return;
        string repoRoot = FindRepoRoot();
        string modDir = Path.Combine(repoRoot, "games", "client", "mods", "neon_caldavar_theme");
        await EnsureNeonCaldavarAssetsOnDiskAsync(modDir, MapTextureSynthesizer.CreateValidPngBytes());

        string xml = await File.ReadAllTextAsync(Path.Combine(modDir, "mod.xml"), Encoding.UTF8);
        ModManifest m = ModManifestParser.Parse(xml);

        m.Name.Should().Be("Neon Caldavar Theme");
        m.Version.Should().Be(NEXUS.Contracts.Domain.NexusVersionInfo.PlatformVersion);
        m.Author.Should().Be("guccidonut");
        m.AppVersion.Should().Be(">=4.10.1");
        m.Category.Should().Be("Maps");
        m.FileCopies.Count.Should().BeGreaterThanOrEqualTo(25);
    }

    [Fact]
    public async Task FrozenCaldavar_ManifestAndPackage_ParsesValidAssetCounts()
    {
        if (!NexusTestEnvironment.HasGameAssets) return;
        string repoRoot = FindRepoRoot();
        string modDir = Path.Combine(repoRoot, "games", "client", "mods", "frozen_caldavar_theme");
        await EnsureFrozenCaldavarAssetsOnDiskAsync(modDir, MapTextureSynthesizer.CreateValidPngBytes());

        string xml = await File.ReadAllTextAsync(Path.Combine(modDir, "mod.xml"), Encoding.UTF8);
        ModManifest m = ModManifestParser.Parse(xml);

        m.Name.Should().Be("Frozen Caldavar Theme");
        m.Version.Should().Be(NEXUS.Contracts.Domain.NexusVersionInfo.PlatformVersion);
        m.Author.Should().Be("guccidonut");
        m.AppVersion.Should().Be(">=4.10.1");
        m.Category.Should().Be("Maps");
        m.FileCopies.Count.Should().BeGreaterThanOrEqualTo(25);
    }

    [Fact]
    public async Task CleanTournament_ManifestAndPackage_ParsesValidAssetCounts()
    {
        if (!NexusTestEnvironment.HasGameAssets) return;
        string repoRoot = FindRepoRoot();
        string modDir = Path.Combine(repoRoot, "games", "client", "mods", "clean_tournament_arena");
        await EnsureCleanTournamentAssetsOnDiskAsync(modDir, MapTextureSynthesizer.CreateValidPngBytes());

        string xml = await File.ReadAllTextAsync(Path.Combine(modDir, "mod.xml"), Encoding.UTF8);
        ModManifest m = ModManifestParser.Parse(xml);

        m.Name.Should().Be("Clean Tournament Arena");
        m.Version.Should().Be(NEXUS.Contracts.Domain.NexusVersionInfo.PlatformVersion);
        m.Author.Should().Be("guccidonut");
        m.AppVersion.Should().Be(">=4.10.1");
        m.Category.Should().Be("Maps");
        m.FileCopies.Count.Should().BeGreaterThanOrEqualTo(20);
    }

    [Fact]
    public async Task CyberpunkMidWars_ManifestAndPackage_ParsesValidAssetCounts()
    {
        if (!NexusTestEnvironment.HasGameAssets) return;
        string repoRoot = FindRepoRoot();
        string modDir = Path.Combine(repoRoot, "games", "client", "mods", "cyberpunk_midwars");
        await EnsureCyberpunkMidWarsAssetsOnDiskAsync(modDir, MapTextureSynthesizer.CreateValidPngBytes());

        string xml = await File.ReadAllTextAsync(Path.Combine(modDir, "mod.xml"), Encoding.UTF8);
        ModManifest m = ModManifestParser.Parse(xml);

        m.Name.Should().Be("Cyberpunk Mid Wars");
        m.Version.Should().Be(NEXUS.Contracts.Domain.NexusVersionInfo.PlatformVersion);
        m.Author.Should().Be("guccidonut");
        m.AppVersion.Should().Be(">=4.10.1");
        m.Category.Should().Be("Maps");
        m.FileCopies.Count.Should().BeGreaterThanOrEqualTo(10);
    }

    [Fact]
    public async Task FrozenMidWars_ManifestAndPackage_ParsesValidAssetCounts()
    {
        if (!NexusTestEnvironment.HasGameAssets) return;
        string repoRoot = FindRepoRoot();
        string modDir = Path.Combine(repoRoot, "games", "client", "mods", "frozen_midwars");
        await EnsureFrozenMidWarsAssetsOnDiskAsync(modDir, MapTextureSynthesizer.CreateValidPngBytes());

        string xml = await File.ReadAllTextAsync(Path.Combine(modDir, "mod.xml"), Encoding.UTF8);
        ModManifest m = ModManifestParser.Parse(xml);

        m.Name.Should().Be("Frozen Mid Wars");
        m.Version.Should().Be(NEXUS.Contracts.Domain.NexusVersionInfo.PlatformVersion);
        m.Author.Should().Be("guccidonut");
        m.AppVersion.Should().Be(">=4.10.1");
        m.Category.Should().Be("Maps");
        m.FileCopies.Count.Should().BeGreaterThanOrEqualTo(10);
    }

    [Fact]
    public async Task CyberpunkDevoWars_ManifestAndPackage_ParsesValidAssetCounts()
    {
        if (!NexusTestEnvironment.HasGameAssets) return;
        string repoRoot = FindRepoRoot();
        string modDir = Path.Combine(repoRoot, "games", "client", "mods", "cyberpunk_devowars");
        await EnsureCyberpunkDevoWarsAssetsOnDiskAsync(modDir, MapTextureSynthesizer.CreateValidPngBytes());

        string xml = await File.ReadAllTextAsync(Path.Combine(modDir, "mod.xml"), Encoding.UTF8);
        ModManifest m = ModManifestParser.Parse(xml);

        m.Name.Should().Be("Cyberpunk Devo Wars");
        m.Version.Should().Be(NEXUS.Contracts.Domain.NexusVersionInfo.PlatformVersion);
        m.Author.Should().Be("guccidonut");
        m.AppVersion.Should().Be(">=4.10.1");
        m.Category.Should().Be("Maps");
        m.FileCopies.Count.Should().BeGreaterThanOrEqualTo(15);
    }

    #endregion

    #region Tier 2: Material Shaders, Effects, and Texture Integrity (5 Tests)

    [Fact]
    public async Task ThemedMaps_WaterMaterials_ValidXmlAndShaderParams()
    {
        if (!NexusTestEnvironment.HasGameAssets) return;
        string repoRoot = FindRepoRoot();
        string[] mapMods =
        [
            "neon_caldavar_theme", "frozen_caldavar_theme", "clean_tournament_arena",
            "cyberpunk_midwars", "frozen_midwars", "cyberpunk_devowars"
        ];

        foreach (string modName in mapMods)
        {
            string modDir = Path.Combine(repoRoot, "games", "client", "mods", modName);
            string waterDir = Path.Combine(modDir, "world", "water", "materials");
            Directory.Exists(waterDir).Should().BeTrue($"Mod '{modName}' must contain world/water/materials/");

            string[] matFiles = Directory.GetFiles(waterDir, "*.material");
            matFiles.Length.Should().BeGreaterThan(0);

            foreach (string matPath in matFiles)
            {
                string xml = await File.ReadAllTextAsync(matPath, Encoding.UTF8);
                XDocument doc = XDocument.Parse(xml);
                doc.Root.Should().NotBeNull();
                doc.Root!.Name.LocalName.Should().Be("material");

                XElement? phase = doc.Root.Element("phase");
                phase.Should().NotBeNull($"Material '{matPath}' must have at least one <phase>");
            }
        }
    }

    [Fact]
    public async Task ThemedMaps_TerrainMaterials_ValidXmlAndShaderParams()
    {
        if (!NexusTestEnvironment.HasGameAssets) return;
        string repoRoot = FindRepoRoot();
        string[] mapMods = ["neon_caldavar_theme", "frozen_caldavar_theme", "clean_tournament_arena"];

        foreach (string modName in mapMods)
        {
            string modDir = Path.Combine(repoRoot, "games", "client", "mods", modName);
            string terrainDir = Path.Combine(modDir, "world", "terrain", "materials");
            Directory.Exists(terrainDir).Should().BeTrue();

            string[] matFiles = Directory.GetFiles(terrainDir, "*.material");
            matFiles.Length.Should().BeGreaterThan(0);

            foreach (string matPath in matFiles)
            {
                string xml = await File.ReadAllTextAsync(matPath, Encoding.UTF8);
                XDocument doc = XDocument.Parse(xml);
                doc.Root!.Name.LocalName.Should().Be("material");
            }
        }
    }

    [Fact]
    public async Task ThemedMaps_EffectXmls_ValidateParticlesAndBeams()
    {
        if (!NexusTestEnvironment.HasGameAssets) return;
        string repoRoot = FindRepoRoot();
        string[] effectRelativePaths =
        [
            "neon_caldavar_theme/world/props/runes/effects/rune_spire_neon.effect",
            "frozen_caldavar_theme/world/effects/snow_ambient.effect",
            "frozen_caldavar_theme/world/effects/frost_spire.effect",
            "cyberpunk_midwars/world/props/midwars_bridge/neon_underglow.effect",
            "frozen_midwars/world/props/midwars_bridge/bridge_icicles.effect",
            "cyberpunk_devowars/heroes/devourer/ability_01/effects/trail.effect",
            "cyberpunk_devowars/heroes/devourer/ability_01/effects/impact.effect",
            "cyberpunk_devowars/world/props/devowars_pad/pad_glow.effect"
        ];

        foreach (string relPath in effectRelativePaths)
        {
            string fullPath = Path.Combine(repoRoot, "games", "client", "mods",
                relPath.Replace('/', Path.DirectorySeparatorChar));
            File.Exists(fullPath).Should().BeTrue($"Effect file '{relPath}' must exist at '{fullPath}'");

            string xml = await File.ReadAllTextAsync(fullPath, Encoding.UTF8);
            XDocument doc = XDocument.Parse(xml);
            doc.Root.Should().NotBeNull();
            doc.Root!.Name.LocalName.Should().Be("effect");

            XElement? defs = doc.Root.Element("definitions");
            defs.Should().NotBeNull();
            XElement? ps = defs!.Element("particlesystem");
            ps.Should().NotBeNull();
        }
    }

    [Fact]
    public async Task CyberpunkDevoWars_LaserHook_ValidBeamsAndTrackerEmitter()
    {
        if (!NexusTestEnvironment.HasGameAssets) return;
        string repoRoot = FindRepoRoot();
        string trailPath = Path.Combine(repoRoot, "games", "client", "mods", "cyberpunk_devowars", "heroes", "devourer",
            "ability_01", "effects", "trail.effect");
        File.Exists(trailPath).Should().BeTrue();

        string xml = await File.ReadAllTextAsync(trailPath, Encoding.UTF8);
        XDocument doc = XDocument.Parse(xml);

        XElement? ps = doc.Root!.Element("definitions")!.Element("particlesystem");
        List<XElement> beams = [.. ps!.Elements("beam")];
        beams.Count.Should()
            .BeGreaterThanOrEqualTo(2, "Laser tether hook must have inner core beam and outer glow beam");

        XElement? tracker = ps.Element("trackeremitter");
        tracker.Should().NotBeNull("Laser hook must utilize a trackeremitter for laser bolt projection");
        tracker!.Attribute("tracktype")?.Value.Should().Be("lerp");
        tracker.Attribute("speed")?.Value.Should().Be("6000");

        // Verify Projectile Entity
        string entityPath = Path.Combine(repoRoot, "games", "client", "mods", "cyberpunk_devowars", "heroes",
            "devourer", "ability_01", "projectile.entity");
        File.Exists(entityPath).Should().BeTrue();
        string entityXml = await File.ReadAllTextAsync(entityPath, Encoding.UTF8);
        entityXml.Should().Contain("traileffect=\"effects/trail.effect\"");
    }

    [Fact]
    public async Task ThemedMaps_TgaTextures_Valid32BitBgraHeaders()
    {
        if (!NexusTestEnvironment.HasGameAssets) return;
        string repoRoot = FindRepoRoot();
        string modsDir = Path.Combine(repoRoot, "games", "client", "mods");
        await EnsureAllSixModsOnDiskAsync(modsDir);

        string[] mapMods =
        [
            "neon_caldavar_theme", "frozen_caldavar_theme", "clean_tournament_arena",
            "cyberpunk_midwars", "frozen_midwars", "cyberpunk_devowars"
        ];

        foreach (string modName in mapMods)
        {
            string modDir = Path.Combine(modsDir, modName);
            string[] tgas = Directory.GetFiles(modDir, "*.tga", SearchOption.AllDirectories);
            tgas.Length.Should().BeGreaterThan(0, $"Mod '{modName}' must contain .tga textures");

            foreach (string tgaPath in tgas)
            {
                byte[] bytes = await File.ReadAllBytesAsync(tgaPath);
                bytes.Length.Should().BeGreaterThanOrEqualTo(18);

                bytes[2].Should().Be(2, $"TGA '{tgaPath}' must be uncompressed true-color (type 2)");
                short width = BitConverter.ToInt16(bytes, 12);
                short height = BitConverter.ToInt16(bytes, 14);
                byte bpp = bytes[16];

                width.Should().BeGreaterThan(0);
                height.Should().BeGreaterThan(0);
                bpp.Should().Be(32, $"TGA '{tgaPath}' must be 32-bit BGRA with alpha channel");
                bytes.Length.Should().Be(18 + width * height * 4);
            }
        }
    }

    #endregion

    #region Tier 3: Packaging & Archive Round-Trip Inspection (2 Tests)

    [Fact]
    public async Task ThemedMaps_WhenInvoked_PackageCreationAndArchiveRead()
    {
        if (!NexusTestEnvironment.HasGameAssets) return;
        string repoRoot = FindRepoRoot();
        string modsDir = Path.Combine(repoRoot, "games", "client", "mods");
        await EnsureAllSixModsOnDiskAsync(modsDir);

        string[] modNames =
        [
            "neon_caldavar_theme",
            "frozen_caldavar_theme",
            "clean_tournament_arena",
            "cyberpunk_midwars",
            "frozen_midwars",
            "cyberpunk_devowars"
        ];

        foreach (string name in modNames)
        {
            string srcDir = Path.Combine(modsDir, name);
            string targetHonmod = Path.Combine(_modsDir, name + ".honmod");

            string modXml = await File.ReadAllTextAsync(Path.Combine(srcDir, "mod.xml"), Encoding.UTF8);
            ModManifest manifest = ModManifestParser.Parse(modXml);

            Dictionary<string, byte[]> payloads = new(StringComparer.OrdinalIgnoreCase);
            foreach (string file in Directory.GetFiles(srcDir, "*", SearchOption.AllDirectories))
            {
                string rel = Path.GetRelativePath(srcDir, file).Replace('\\', '/');
                if (rel.Equals("mod.xml", StringComparison.OrdinalIgnoreCase)) continue;
                payloads[rel] = await File.ReadAllBytesAsync(file);
            }

            byte[] iconBytes = await File.ReadAllBytesAsync(Path.Combine(srcDir, "icon.png"));

            await HonModArchiveWriter.WritePackageAsync(targetHonmod, manifest, payloads, iconBytes);
            File.Exists(targetHonmod).Should().BeTrue();

            using ModPackage pkg = await HonModArchiveReader.ReadPackageAsync(targetHonmod);
            pkg.Manifest.Author.Should().Be("guccidonut");
            pkg.Manifest.Version.Should().Be(NEXUS.Contracts.Domain.NexusVersionInfo.PlatformVersion);
            pkg.Sha256Hash.Should().NotBeNullOrWhiteSpace();
            pkg.FileSizeBytes.Should().BeGreaterThan(0);
            pkg.IconData.Should().NotBeNull();
        }
    }

    [Fact]
    public async Task ThemedMaps_DAGResolution_TopologicalSortWithoutCycles()
    {
        if (!NexusTestEnvironment.HasGameAssets) return;
        string repoRoot = FindRepoRoot();
        string modsDir = Path.Combine(repoRoot, "games", "client", "mods");
        await EnsureAllSixModsOnDiskAsync(modsDir);

        string[] modNames =
        [
            "neon_caldavar_theme",
            "frozen_caldavar_theme",
            "clean_tournament_arena",
            "cyberpunk_midwars",
            "frozen_midwars",
            "cyberpunk_devowars"
        ];

        List<ModPackage> packages = [];
        foreach (string name in modNames)
        {
            string srcDir = Path.Combine(modsDir, name);
            ModPackage pkg = await HonModArchiveReader.ReadFromDirectoryAsync(srcDir);
            pkg.IsEnabled = true;
            packages.Add(pkg);
        }

        SortResult sortResult = TopologicalSorter.ResolveAndSort(packages);
        sortResult.Success.Should().BeTrue("The 6 Themed Map Overhaul mods must be cycle-free and resolve cleanly");
        sortResult.ExecutionPlan.Should().HaveCount(6);
        sortResult.CycleReports.Should().BeEmpty();
    }

    #endregion

    #region Tier 4: Layered VFS Injection and Rollback (2 Tests)

    [Fact]
    public async Task ThemedMaps_LayeredVfsInjection_AppliesAndRollsBackCleanly()
    {
        if (!NexusTestEnvironment.HasGameAssets) return;
        string repoRoot = FindRepoRoot();
        string modsDir = Path.Combine(repoRoot, "games", "client", "mods");
        await EnsureAllSixModsOnDiskAsync(modsDir);

        // Prepare baseline client resources0.s2z
        string gameDir = Path.Combine(_clientDir, "game");
        Directory.CreateDirectory(gameDir);
        string res0Path = Path.Combine(gameDir, "resources0.s2z");

        await S2zArchiveWriter.WriteArchiveAsync(res0Path, new Dictionary<string, byte[]>
        {
            ["world/water/materials/caldavar.material"] = Encoding.UTF8.GetBytes("<material base='true' />"),
            ["world/props/midwars_bridge/bridge.material"] = Encoding.UTF8.GetBytes("<material base='true' />"),
            ["heroes/devourer/ability_01/projectile.entity"] = Encoding.UTF8.GetBytes("<projectile base='true' />")
        });

        // Load all 6 mods
        string[] modNames =
        [
            "neon_caldavar_theme", "frozen_caldavar_theme", "clean_tournament_arena",
            "cyberpunk_midwars", "frozen_midwars", "cyberpunk_devowars"
        ];

        List<ModPackage> packages = [];
        foreach (string name in modNames)
        {
            string srcDir = Path.Combine(modsDir, name);
            ModPackage pkg = await HonModArchiveReader.ReadFromDirectoryAsync(srcDir);
            pkg.IsEnabled = true;
            packages.Add(pkg);
        }

        ModManager manager = new();

        // 1. Apply in VFS mode
        ApplyResult applyResult = await manager.ApplyModsAsync(new ApplyOptions
        {
            GameClientDirectory = _clientDir,
            ModsToApply = packages,
            Mode = InjectionMode.Vfs
        });

        applyResult.Success.Should().BeTrue();
        applyResult.AppliedMods.Should().HaveCount(6);

        string overrideDir = Path.Combine(_clientDir, "override");
        Directory.Exists(overrideDir).Should().BeTrue();

        // Check that override files exist
        File.Exists(Path.Combine(overrideDir, "world", "water", "materials", "caldavar.material")).Should().BeTrue();
        File.Exists(Path.Combine(overrideDir, "heroes", "devourer", "ability_01", "effects", "trail.effect")).Should()
            .BeTrue();
        File.Exists(Path.Combine(overrideDir, "world", "props", "midwars_bridge", "bridge.material")).Should().BeTrue();

        // 2. Test Rollback
        RollbackResult rollbackResult = await manager.RollbackAsync(_clientDir);
        rollbackResult.Success.Should().BeTrue();

        File.Exists(Path.Combine(overrideDir, "heroes", "devourer", "ability_01", "effects", "trail.effect")).Should()
            .BeFalse();
    }

    [Fact]
    public async Task ThemedMaps_DirectArchiveInjection_S2zIntegrity()
    {
        if (!NexusTestEnvironment.HasGameAssets) return;
        string repoRoot = FindRepoRoot();
        string modsDir = Path.Combine(repoRoot, "games", "client", "mods");
        await EnsureAllSixModsOnDiskAsync(modsDir);

        string[] modNames = ["neon_caldavar_theme", "cyberpunk_devowars"];
        List<ModPackage> packages = [];
        foreach (string name in modNames)
        {
            string srcDir = Path.Combine(modsDir, name);
            ModPackage pkg = await HonModArchiveReader.ReadFromDirectoryAsync(srcDir);
            pkg.IsEnabled = true;
            packages.Add(pkg);
        }

        ModManager manager = new();
        ApplyResult applyResult = await manager.ApplyModsAsync(new ApplyOptions
        {
            GameClientDirectory = _clientDir,
            ModsToApply = packages,
            Mode = InjectionMode.Archive,
            TargetArchiveName = "resources999.s2z"
        });

        applyResult.Success.Should().BeTrue();
        string s2zPath = Path.Combine(_clientDir, "game", "resources999.s2z");
        File.Exists(s2zPath).Should().BeTrue();

        using S2zArchiveReader s2z = S2zArchiveReader.Open(s2zPath);
        s2z.HasEntry("world/water/materials/caldavar.material").Should().BeTrue();
        s2z.HasEntry("heroes/devourer/ability_01/effects/trail.effect").Should().BeTrue();
    }

    #endregion
}

