// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

using ASPIRE.Common.Prebaking.Generators;
using ASPIRE.Common.Prebaking.Parsers;
using Kongor.Modding.Core.Models;
using Kongor.Modding.Core.Packaging;
using Kongor.Modding.Core.Parsing;
using NEXUS.Contracts.EntityCache;

namespace ASPIRE.PrebakeTool;

/// <summary>
///     Bootstraps and generates all physical prebaked binary assets, mod packages, and icon textures across the
///     repository.
/// </summary>
public static class AssetBootstrapService
{
    public static readonly byte[] ValidPngBytes =
    [
        0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A,
        0x00, 0x00, 0x00, 0x0D, 0x49, 0x48, 0x44, 0x52,
        0x00, 0x00, 0x00, 0x20, 0x00, 0x00, 0x00, 0x20,
        0x08, 0x06, 0x00, 0x00, 0x00, 0x73, 0x7A, 0x7A,
        0xF4, 0x00, 0x00, 0x00, 0x18, 0x49, 0x44, 0x41,
        0x54, 0x78, 0x9C, 0x63, 0x60, 0x60, 0x60, 0x00,
        0x00, 0x00, 0x04, 0x00, 0x01, 0x6B, 0x26, 0x9B,
        0xCE, 0x00, 0x00, 0x00, 0x00, 0x49, 0x45, 0x4E,
        0x44, 0xAE, 0x42, 0x60, 0x82
    ];

    public static byte[] CreateTga(int width, int height, byte r, byte g, byte b, byte a)
    {
        byte[] buffer = new byte[18 + width * height * 4];
        buffer[2] = 2; // Uncompressed true-color
        buffer[12] = (byte)(width & 0xFF);
        buffer[13] = (byte)((width >> 8) & 0xFF);
        buffer[14] = (byte)(height & 0xFF);
        buffer[15] = (byte)((height >> 8) & 0xFF);
        buffer[16] = 32; // 32 bpp
        buffer[17] = 0x08; // Bottom-left origin, 8-bit alpha (standard K2 engine format)

        int offset = 18;
        for (int i = 0; i < width * height; i++)
        {
            buffer[offset++] = b; // B
            buffer[offset++] = g; // G
            buffer[offset++] = r; // R
            buffer[offset++] = a; // A
        }

        return buffer;
    }

    public static async Task BootstrapAllAssetsAsync(string repoRoot)
    {
        string clientDir = Path.Combine(repoRoot, "games", "client");
        string sharedDir = Path.Combine(repoRoot, "games", "shared");
        string modsDir = Path.Combine(clientDir, "mods");
        string modernHudDir = Path.Combine(modsDir, "ModernHUD_SmartPing");

        // 1. Ensure Loose Mod Icon and TGAs exist
        Directory.CreateDirectory(Path.Combine(modernHudDir, "ui", "icons"));
        await File.WriteAllBytesAsync(Path.Combine(modernHudDir, "icon.png"), ValidPngBytes);
        await File.WriteAllBytesAsync(Path.Combine(modernHudDir, "ui", "icons", "ping_danger.tga"),
            CreateTga(32, 32, 230, 40, 40, 255));
        await File.WriteAllBytesAsync(Path.Combine(modernHudDir, "ui", "icons", "ping_mana.tga"),
            CreateTga(32, 32, 40, 140, 240, 255));
        await File.WriteAllBytesAsync(Path.Combine(modernHudDir, "ui", "icons", "ping_ward.tga"),
            CreateTga(32, 32, 240, 210, 40, 255));
        await File.WriteAllBytesAsync(Path.Combine(modernHudDir, "ui", "icons", "ping_onmyway.tga"),
            CreateTga(32, 32, 40, 220, 100, 255));

        // 2. Package ModernHUD_SmartPing.honmod
        string modXmlPath = Path.Combine(modernHudDir, "mod.xml");
        if (File.Exists(modXmlPath))
        {
            string xmlContent = await File.ReadAllTextAsync(modXmlPath);
            ModManifest manifest = ModManifestParser.Parse(xmlContent);

            Dictionary<string, byte[]> payload = new(StringComparer.OrdinalIgnoreCase)
            {
                ["ui/fonts_sdf.package"] =
                    await File.ReadAllBytesAsync(Path.Combine(modernHudDir, "ui", "fonts_sdf.package")),
                ["ui/modern_hud_smartping.package"] =
                    await File.ReadAllBytesAsync(Path.Combine(modernHudDir, "ui", "modern_hud_smartping.package")),
                ["ui/scripts/modern_smartping.lua"] =
                    await File.ReadAllBytesAsync(Path.Combine(modernHudDir, "ui", "scripts", "modern_smartping.lua")),
                ["ui/scripts/modern_hud_controller.lua"] =
                    await File.ReadAllBytesAsync(Path.Combine(modernHudDir, "ui", "scripts",
                        "modern_hud_controller.lua")),
                ["ui/icons/ping_danger.tga"] = CreateTga(32, 32, 230, 40, 40, 255),
                ["ui/icons/ping_mana.tga"] = CreateTga(32, 32, 40, 140, 240, 255),
                ["ui/icons/ping_ward.tga"] = CreateTga(32, 32, 240, 210, 40, 255),
                ["ui/icons/ping_onmyway.tga"] = CreateTga(32, 32, 40, 220, 100, 255)
            };

            string honmodTarget = Path.Combine(modsDir, "ModernHUD_SmartPing.honmod");
            await HonModArchiveWriter.WritePackageAsync(honmodTarget, manifest, payload, ValidPngBytes);
        }

        // 3. Harvest Heroes & Generate Prebaked hero_descriptors.bin
        string heroesXmlPath = Path.Combine(clientDir, "game", "heroes.xml");
        if (File.Exists(heroesXmlPath) || File.Exists(Path.Combine(clientDir, "game", "resources0.s2z")))
        {
            MasterXmlParser parser = new();
            EntityCacheContainer container = await parser.ParseMasterDataAsync(
                clientDir,
                Directory.Exists(sharedDir) ? sharedDir : null);

            byte[] flatBufferBytes = HeroFlatBufferGenerator.Generate(container);

            string overrideBinPath = Path.Combine(clientDir, "override", "prebaked_assets", "hero_descriptors.bin");
            Directory.CreateDirectory(Path.GetDirectoryName(overrideBinPath)!);
            await File.WriteAllBytesAsync(overrideBinPath, flatBufferBytes);
        }
    }
}
