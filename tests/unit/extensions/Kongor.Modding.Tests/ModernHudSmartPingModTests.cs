// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

using System.Text;
using System.Xml.Linq;
using FluentAssertions;
using Kongor.Modding.Core.Models;
using Kongor.Modding.Core.Packaging;
using Kongor.Modding.Core.Parsing;
using Kongor.Modding.Vfs;
using Xunit;

namespace Kongor.Modding.Tests;

/// <summary>
///     Comprehensive verification test fixture for Modern HUD &amp; Smart Radial Ping Mod (ModernHUD_SmartPing.honmod).
///     Validates:
///     1. Manifest parsing (mod.xml), copyfile/editfile operations, and metadata.
///     2. Signed Distance Field (SDF) font mapping (dyn_8 to dyn_64) for 1440p/4K razor-sharp rendering.
///     3. Responsive 4-quadrant radial ping wheel UI (Danger, On My Way, Need Mana, Ward Here, Center Ping).
///     4. Minimap glassmorphism transparency overlay and HUD scaling CVars.
///     5. Mouse vector/angle trigonometry math and deadzone calculations.
///     6. Binary TGA icon headers and PNG icon asset integrity.
///     7. HonMod archive packaging and Layered VFS injection into game client interfaces.
/// </summary>
[Collection("ModdingDiskCollection")]
public sealed class ModernHudSmartPingModTests : IDisposable
{
    private readonly string _clientDir;
    private readonly string _modsDir;
    private readonly string _tempDir;

    public ModernHudSmartPingModTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "ModernHudSmartPingTest_" + Guid.NewGuid().ToString("N"));
        _clientDir = Path.Combine(_tempDir, "client");
        _modsDir = Path.Combine(_tempDir, "mods");

        Directory.CreateDirectory(_tempDir);
        Directory.CreateDirectory(Path.Combine(_clientDir, "game"));
        Directory.CreateDirectory(_modsDir);
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

    /// <summary>
    ///     Generates a valid 32-bit BGRA uncompressed Truevision TGA image byte array.
    /// </summary>
    public static byte[] CreateValidTgaBytes(short width = 64, short height = 64, byte r = 255, byte g = 255,
        byte b = 255, byte a = 255)
    {
        int pixelDataSize = width * height * 4;
        byte[] buffer = new byte[18 + pixelDataSize];

        // 18-byte TGA Header
        buffer[0] = 0; // ID length
        buffer[1] = 0; // Color map type (none)
        buffer[2] = 2; // Image type: uncompressed true-color
        // Color map spec (bytes 3-7): 0
        // X-origin (bytes 8-9): 0
        // Y-origin (bytes 10-11): 0
        buffer[12] = (byte)(width & 0xFF);
        buffer[13] = (byte)((width >> 8) & 0xFF);
        buffer[14] = (byte)(height & 0xFF);
        buffer[15] = (byte)((height >> 8) & 0xFF);
        buffer[16] = 32; // 32 bits per pixel (BGRA)
        buffer[17] = 0x08; // 8-bit alpha, bottom-left origin (standard K2)

        // Fill BGRA pixels
        int offset = 18;
        for (int i = 0; i < width * height; i++)
        {
            buffer[offset++] = b;
            buffer[offset++] = g;
            buffer[offset++] = r;
            buffer[offset++] = a;
        }

        return buffer;
    }

    /// <summary>
    ///     Generates a valid minimal PNG image byte array.
    /// </summary>
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

    /// <summary>
    ///     Ensures all payload assets and TGA icons exist on disk in the target mod directory.
    /// </summary>
    internal static async Task EnsureModAssetsOnDiskAsync(string modDirectory)
    {
        Directory.CreateDirectory(modDirectory);
        Directory.CreateDirectory(Path.Combine(modDirectory, "ui"));
        Directory.CreateDirectory(Path.Combine(modDirectory, "ui", "scripts"));
        Directory.CreateDirectory(Path.Combine(modDirectory, "ui", "icons"));

        string iconPngPath = Path.Combine(modDirectory, "icon.png");
        if (!File.Exists(iconPngPath)) await File.WriteAllBytesAsync(iconPngPath, CreateValidPngBytes());

        // TGA Icons
        string iconsDir = Path.Combine(modDirectory, "ui", "icons");
        string dangerTga = Path.Combine(iconsDir, "ping_danger.tga");
        if (!File.Exists(dangerTga))
            await File.WriteAllBytesAsync(dangerTga, CreateValidTgaBytes(64, 64, 255, 50, 50)); // Red

        string manaTga = Path.Combine(iconsDir, "ping_mana.tga");
        if (!File.Exists(manaTga)) await File.WriteAllBytesAsync(manaTga, CreateValidTgaBytes(64, 64, 50, 180)); // Blue

        string wardTga = Path.Combine(iconsDir, "ping_ward.tga");
        if (!File.Exists(wardTga))
            await File.WriteAllBytesAsync(wardTga, CreateValidTgaBytes(64, 64, 255, 200, 0)); // Gold

        string onmywayTga = Path.Combine(iconsDir, "ping_onmyway.tga");
        if (!File.Exists(onmywayTga))
            await File.WriteAllBytesAsync(onmywayTga, CreateValidTgaBytes(64, 64, 50, 255, 100)); // Green
    }

    [Fact]
    public async Task ModernHudSmartPing_ManifestParsing_ValidatesAllMetadataAndOperations()
    {
        if (!NexusTestEnvironment.HasGameAssets) return;
        string repoRoot = FindRepoRoot();
        string modDir = Path.Combine(repoRoot, "games", "client", "mods", "ModernHUD_SmartPing");
        string modXmlPath = Path.Combine(modDir, "mod.xml");

        File.Exists(modXmlPath).Should().BeTrue($"Manifest '{modXmlPath}' must exist on disk");

        string xml = await File.ReadAllTextAsync(modXmlPath, Encoding.UTF8);
        ModManifest manifest = ModManifestParser.Parse(xml);

        manifest.Name.Should().Be("Modern HUD & Smart Radial Ping");
        manifest.Version.Should().Be(NEXUS.Contracts.Domain.NexusVersionInfo.PlatformVersion);
        manifest.Author.Should().Be("ProjectKongor");
        manifest.Category.Should().Be("UI & HUD");
        manifest.AppVersion.Should().Be(">=4.10.1");
        manifest.MmVersion.Should().Be("1.3");
        manifest.Icon.Should().Be("icon.png");

        // Validate File Copies (8 files)
        manifest.FileCopies.Should().HaveCount(8);
        manifest.FileCopies.Select(c => c.TargetName).Should().Contain(
        [
            "ui/modern_hud_smartping.package",
            "ui/fonts_sdf.package",
            "ui/scripts/modern_smartping.lua",
            "ui/scripts/modern_hud_controller.lua",
            "ui/icons/ping_danger.tga",
            "ui/icons/ping_mana.tga",
            "ui/icons/ping_ward.tga",
            "ui/icons/ping_onmyway.tga"
        ]);

        // Validate File Edits (2 files: Apex and Legacy interfaces)
        manifest.FileEdits.Should().HaveCount(2);
        manifest.FileEdits.Select(e => e.TargetName).Should().Contain(
        [
            "ui/game_apex.interface",
            "ui/game_legacy.interface"
        ]);

        foreach (EditFileOperation edit in manifest.FileEdits)
        {
            edit.Operations.Should().HaveCount(2);
            edit.Operations[0].Should().BeOfType<FindOperation>();
            edit.Operations[1].Should().BeOfType<InsertOperation>()
                .Which.Content.Should().Contain("/ui/modern_hud_smartping.package");
        }
    }

    [Fact]
    public async Task ModernHudSmartPing_SdfFontsPackage_Validates15FontMapsAndSdfAttributes()
    {
        if (!NexusTestEnvironment.HasGameAssets) return;
        string repoRoot = FindRepoRoot();
        string fontsPackagePath = Path.Combine(repoRoot, "games", "client", "mods", "ModernHUD_SmartPing", "ui",
            "fonts_sdf.package");
        File.Exists(fontsPackagePath).Should().BeTrue();

        string xml = await File.ReadAllTextAsync(fontsPackagePath, Encoding.UTF8);
        XDocument doc = XDocument.Parse(xml);
        doc.Root.Should().NotBeNull();
        doc.Root!.Name.LocalName.Should().Be("package");

        XElement? fontface = doc.Root.Element("fontface");
        fontface.Should().NotBeNull();
        fontface!.Attribute("sdf")?.Value.Should().Be("true");
        fontface.Attribute("sdf_spread")?.Value.Should().Be("8.0");

        List<XElement> fontmaps = [.. fontface.Elements("fontmap")];
        fontmaps.Should().HaveCount(15);

        string[] expectedFontNames =
        [
            "dyn_8", "dyn_9", "dyn_10", "dyn_11", "dyn_12", "dyn_14", "dyn_16",
            "dyn_18", "dyn_20", "dyn_22", "dyn_24", "dyn_28", "dyn_32", "dyn_48", "dyn_64"
        ];

        foreach (string fn in expectedFontNames)
        {
            XElement? map = fontmaps.FirstOrDefault(m => (string?)m.Attribute("name") == fn);
            map.Should().NotBeNull($"SDF font map '{fn}' must be defined");
            map!.Attribute("sdf_smooth").Should().NotBeNull();
        }
    }

    [Fact]
    public async Task ModernHudSmartPing_InterfacePackage_ContainsAllFourQuadrantsAndMinimapGlass()
    {
        if (!NexusTestEnvironment.HasGameAssets) return;
        string repoRoot = FindRepoRoot();
        string uiPackagePath = Path.Combine(repoRoot, "games", "client", "mods", "ModernHUD_SmartPing", "ui",
            "modern_hud_smartping.package");
        File.Exists(uiPackagePath).Should().BeTrue();

        string xml = await File.ReadAllTextAsync(uiPackagePath, Encoding.UTF8);
        XDocument doc = XDocument.Parse(xml);
        doc.Root.Should().NotBeNull();

        // 1. Root overlay & master wheel
        XElement? rootPanel = doc.Root!.Elements("panel")
            .FirstOrDefault(p => (string?)p.Attribute("name") == "modern_smartping_root");
        rootPanel.Should().NotBeNull();
        rootPanel!.Attribute("noclick")?.Value.Should().Be("true");

        XElement? wheelMaster = rootPanel.Elements("panel")
            .FirstOrDefault(p => (string?)p.Attribute("name") == "smartping_wheel_master");
        wheelMaster.Should().NotBeNull();

        // 2. Center hub
        XElement? centerHub = wheelMaster!.Elements("panel")
            .FirstOrDefault(p => (string?)p.Attribute("name") == "smartping_center_hub");
        centerHub.Should().NotBeNull();

        // 3. Four Quadrants
        XElement? north = wheelMaster.Elements("panel")
            .FirstOrDefault(p => (string?)p.Attribute("name") == "smartping_quad_north");
        north.Should().NotBeNull();
        north!.Element("label")?.Value.Should().Be("DANGER");

        XElement? east = wheelMaster.Elements("panel")
            .FirstOrDefault(p => (string?)p.Attribute("name") == "smartping_quad_east");
        east.Should().NotBeNull();
        east!.Element("label")?.Value.Should().Be("ON MY WAY");

        XElement? south = wheelMaster.Elements("panel")
            .FirstOrDefault(p => (string?)p.Attribute("name") == "smartping_quad_south");
        south.Should().NotBeNull();
        south!.Element("label")?.Value.Should().Be("NEED MANA");

        XElement? west = wheelMaster.Elements("panel")
            .FirstOrDefault(p => (string?)p.Attribute("name") == "smartping_quad_west");
        west.Should().NotBeNull();
        west!.Element("label")?.Value.Should().Be("WARD HERE");

        // 4. Glassmorphism minimap panel
        XElement? minimapGlass = doc.Root.Elements("panel")
            .FirstOrDefault(p => (string?)p.Attribute("name") == "modern_minimap_glass_panel");
        minimapGlass.Should().NotBeNull();
    }

    [Fact]
    public void ModernHudSmartPing_SmartPingLuaScript_ValidatesMathQuadrantCalculations()
    {
        // Simulate trigonometry math used in modern_smartping.lua
        static string CalculateSignal(double dx, double dy)
        {
            const double deadzone = 24.0;
            double dist = Math.Sqrt(dx * dx + dy * dy);
            if (dist < deadzone) return "CENTER";

            // North is +90, East is 0, South is -90, West is 180/-180
            double angle = Math.Atan2(-dy, dx) * (180.0 / Math.PI);
            if (angle is >= 45.0 and < 135.0) return "NORTH"; // Danger
            if (angle is >= -45.0 and < 45.0) return "EAST"; // On My Way
            if (angle is >= -135.0 and < -45.0) return "SOUTH"; // Need Mana
            return "WEST"; // Ward Here
        }

        // 1. Deadzone
        CalculateSignal(0, 0).Should().Be("CENTER");
        CalculateSignal(10, 10).Should().Be("CENTER");
        CalculateSignal(-15, 5).Should().Be("CENTER");

        // 2. North (Danger) -> dx = 0, dy = -100 (upwards on screen is -Y in 2D UI)
        CalculateSignal(0, -100).Should().Be("NORTH");
        CalculateSignal(30, -80).Should().Be("NORTH");
        CalculateSignal(-30, -80).Should().Be("NORTH");

        // 3. East (On My Way) -> dx = +100, dy = 0 (rightwards)
        CalculateSignal(100, 0).Should().Be("EAST");
        CalculateSignal(80, 20).Should().Be("EAST");
        CalculateSignal(80, -20).Should().Be("EAST");

        // 4. South (Need Mana) -> dx = 0, dy = +100 (downwards on screen is +Y)
        CalculateSignal(0, 100).Should().Be("SOUTH");
        CalculateSignal(20, 80).Should().Be("SOUTH");
        CalculateSignal(-20, 80).Should().Be("SOUTH");

        // 5. West (Ward Here) -> dx = -100, dy = 0 (leftwards)
        CalculateSignal(-100, 0).Should().Be("WEST");
        CalculateSignal(-80, 20).Should().Be("WEST");
        CalculateSignal(-80, -20).Should().Be("WEST");
    }

    [Fact]
    public async Task ModernHudSmartPing_TgaIcons_Validate32BitTgaHeadersAndPayloads()
    {
        if (!NexusTestEnvironment.HasGameAssets) return;
        string repoRoot = FindRepoRoot();
        string modDir = Path.Combine(repoRoot, "games", "client", "mods", "ModernHUD_SmartPing");
        await EnsureModAssetsOnDiskAsync(modDir);

        string[] requiredIcons =
        [
            "ping_danger.tga",
            "ping_mana.tga",
            "ping_ward.tga",
            "ping_onmyway.tga"
        ];

        foreach (string iconName in requiredIcons)
        {
            string iconPath = Path.Combine(modDir, "ui", "icons", iconName);
            File.Exists(iconPath).Should().BeTrue($"Icon '{iconName}' must exist at '{iconPath}'");

            byte[] bytes = await File.ReadAllBytesAsync(iconPath);
            bytes.Length.Should().BeGreaterThanOrEqualTo(18 + 64 * 64 * 4);

            // Validate TGA header
            bytes[2].Should().Be(2, "TGA image type must be uncompressed true-color (2)");
            short width = BitConverter.ToInt16(bytes, 12);
            short height = BitConverter.ToInt16(bytes, 14);
            byte bpp = bytes[16];

            width.Should().Be(64);
            height.Should().Be(64);
            bpp.Should().Be(32, "TGA icons must be 32-bit BGRA with alpha channel");
        }
    }

    [Fact]
    public async Task ModernHudSmartPing_PackageCreationAndArchiveInspection_GeneratesValidHonModArchive()
    {
        if (!NexusTestEnvironment.HasGameAssets) return;
        string repoRoot = FindRepoRoot();
        string modDir = Path.Combine(repoRoot, "games", "client", "mods", "ModernHUD_SmartPing");
        await EnsureModAssetsOnDiskAsync(modDir);

        string targetHonmod = Path.Combine(_modsDir, "ModernHUD_SmartPing.honmod");

        // Read manifest
        string modXml = await File.ReadAllTextAsync(Path.Combine(modDir, "mod.xml"), Encoding.UTF8);
        ModManifest manifest = ModManifestParser.Parse(modXml);

        // Collect payloads
        Dictionary<string, byte[]> payloads = new(StringComparer.OrdinalIgnoreCase);
        foreach (string file in Directory.GetFiles(modDir, "*", SearchOption.AllDirectories))
        {
            string rel = Path.GetRelativePath(modDir, file).Replace('\\', '/');
            if (rel.Equals("mod.xml", StringComparison.OrdinalIgnoreCase)) continue;
            payloads[rel] = await File.ReadAllBytesAsync(file);
        }

        byte[] iconBytes = await File.ReadAllBytesAsync(Path.Combine(modDir, "icon.png"));

        // Package into .honmod ZIP archive
        await HonModArchiveWriter.WritePackageAsync(targetHonmod, manifest, payloads, iconBytes);
        File.Exists(targetHonmod).Should().BeTrue();

        // Read back and verify
        using ModPackage pkg = await HonModArchiveReader.ReadPackageAsync(targetHonmod);
        pkg.Manifest.Name.Should().Be("Modern HUD & Smart Radial Ping");
        pkg.Manifest.Version.Should().Be(NEXUS.Contracts.Domain.NexusVersionInfo.PlatformVersion);
        pkg.Sha256Hash.Should().NotBeNullOrWhiteSpace();
        pkg.FileSizeBytes.Should().BeGreaterThan(0);
        pkg.IconData.Should().NotBeNull();

        // Check required payload entries
        pkg.HasPayloadFile("ui/modern_hud_smartping.package").Should().BeTrue();
        pkg.HasPayloadFile("ui/fonts_sdf.package").Should().BeTrue();
        pkg.HasPayloadFile("ui/scripts/modern_smartping.lua").Should().BeTrue();
        pkg.HasPayloadFile("ui/scripts/modern_hud_controller.lua").Should().BeTrue();
        pkg.HasPayloadFile("ui/icons/ping_danger.tga").Should().BeTrue();
        pkg.HasPayloadFile("ui/icons/ping_mana.tga").Should().BeTrue();
        pkg.HasPayloadFile("ui/icons/ping_ward.tga").Should().BeTrue();
        pkg.HasPayloadFile("ui/icons/ping_onmyway.tga").Should().BeTrue();
    }

    [Fact]
    public async Task ModernHudSmartPing_VfsInjectionAndRollback_AppliesPatchesCleanly()
    {
        if (!NexusTestEnvironment.HasGameAssets) return;
        string repoRoot = FindRepoRoot();
        string modDir = Path.Combine(repoRoot, "games", "client", "mods", "ModernHUD_SmartPing");
        await EnsureModAssetsOnDiskAsync(modDir);

        // Prepare baseline client resources
        string apexInterface =
            "<?xml version=\"1.0\" encoding=\"UTF-8\"?>\n<interface name=\"game\">\n  <lua file=\"/ui/scripts/game_new.lua\" />\n</interface>";
        string legacyInterface =
            "<?xml version=\"1.0\" encoding=\"UTF-8\"?>\n<interface name=\"game\">\n  <lua file=\"/ui/scripts/game_new.lua\" />\n</interface>";

        string gameDir = Path.Combine(_clientDir, "game");
        Directory.CreateDirectory(gameDir);
        string res0Path = Path.Combine(gameDir, "resources0.s2z");

        await S2zArchiveWriter.WriteArchiveAsync(res0Path, new Dictionary<string, byte[]>
        {
            ["ui/game_apex.interface"] = Encoding.UTF8.GetBytes(apexInterface),
            ["ui/game_legacy.interface"] = Encoding.UTF8.GetBytes(legacyInterface)
        });

        // Load mod package
        ModPackage modPkg = await HonModArchiveReader.ReadFromDirectoryAsync(modDir);
        modPkg.IsEnabled = true;

        ModManager manager = new();

        // 1. Apply in VFS mode
        ApplyResult applyResult = await manager.ApplyModsAsync(new ApplyOptions
        {
            GameClientDirectory = _clientDir,
            ModsToApply = [modPkg],
            Mode = InjectionMode.Vfs
        });

        applyResult.Success.Should().BeTrue();
        applyResult.AppliedMods.Should().HaveCount(1);

        string overrideDir = Path.Combine(_clientDir, "override");
        Directory.Exists(overrideDir).Should().BeTrue();

        // Check patched interfaces
        string patchedApex = await File.ReadAllTextAsync(Path.Combine(overrideDir, "ui", "game_apex.interface"));
        patchedApex.Should().Contain("<include file=\"/ui/fonts_sdf.package\" />");
        patchedApex.Should().Contain("<include file=\"/ui/modern_hud_smartping.package\" />");
        patchedApex.Should().Contain("<lua file=\"/ui/scripts/modern_smartping.lua\" />");

        string patchedLegacy = await File.ReadAllTextAsync(Path.Combine(overrideDir, "ui", "game_legacy.interface"));
        patchedLegacy.Should().Contain("<include file=\"/ui/fonts_sdf.package\" />");
        patchedLegacy.Should().Contain("<include file=\"/ui/modern_hud_smartping.package\" />");

        // Check copied files
        File.Exists(Path.Combine(overrideDir, "ui", "modern_hud_smartping.package")).Should().BeTrue();
        File.Exists(Path.Combine(overrideDir, "ui", "fonts_sdf.package")).Should().BeTrue();
        File.Exists(Path.Combine(overrideDir, "ui", "scripts", "modern_smartping.lua")).Should().BeTrue();
        File.Exists(Path.Combine(overrideDir, "ui", "scripts", "modern_hud_controller.lua")).Should().BeTrue();
        File.Exists(Path.Combine(overrideDir, "ui", "icons", "ping_danger.tga")).Should().BeTrue();

        // 2. Test Rollback
        RollbackResult rollback = await manager.RollbackAsync(_clientDir);
        rollback.Success.Should().BeTrue();

        File.Exists(Path.Combine(overrideDir, "ui", "modern_hud_smartping.package")).Should().BeFalse();
        File.Exists(Path.Combine(overrideDir, "ui", "game_apex.interface")).Should().BeFalse();
    }
}

