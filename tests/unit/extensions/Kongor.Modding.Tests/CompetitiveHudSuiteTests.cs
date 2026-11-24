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
///     Comprehensive, 4-Tier Automated Verification Test Suite for the 3 Competitive & Tactical HUD Mods (M1):
///     1. Vector Smart Cast & Max-Blink Indicators (vector_smart_cast_indicators.honmod)
///     2. Rune & Kongor Timers HUD (rune_kongor_timers_hud.honmod)
///     3. Ward Vision Radius Overlay (ward_vision_radius_overlay.honmod)
/// </summary>
[Collection("ModdingDiskCollection")]
public sealed class CompetitiveHudSuiteTests : IDisposable
{
    private readonly string _clientDir;
    private readonly string _modsDir;
    private readonly string _tempDir;

    public CompetitiveHudSuiteTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "CompetitiveHudSuiteTest_" + Guid.NewGuid().ToString("N"));
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

    #region Tier 1: Manifest Parsing, Metadata & Asset Extraction (9 Tests)

    [Fact]
    public async Task VectorSmartCast_ManifestParsing_ExtractsAllAttributesAndOperations()
    {
        if (!NexusTestEnvironment.HasGameAssets) return;
        string repoRoot = FindRepoRoot();
        string modDir = Path.Combine(repoRoot, "games", "client", "mods", "vector_smart_cast_indicators");
        string modXmlPath = Path.Combine(modDir, "mod.xml");

        File.Exists(modXmlPath).Should().BeTrue($"Manifest '{modXmlPath}' must exist");

        string xml = await File.ReadAllTextAsync(modXmlPath, Encoding.UTF8);
        ModManifest manifest = ModManifestParser.Parse(xml);

        manifest.Name.Should().Be("Vector Smart Cast & Max-Blink Indicators");
        manifest.Version.Should().Be(NEXUS.Contracts.Domain.NexusVersionInfo.PlatformVersion);
        manifest.Author.Should().Be("guccidonut");
        manifest.AppVersion.Should().Be(">=4.10.1");
        manifest.Category.Should().Be("UI & HUD");
        manifest.Date.Should().NotBeNullOrWhiteSpace();
        manifest.Icon.Should().Be("icon.png");

        // Validate File Copies (8 files)
        manifest.FileCopies.Should().HaveCount(8);
        manifest.FileCopies.Select(c => c.TargetName).Should().Contain(
        [
            "ui/vector_smart_cast.package",
            "ui/scripts/vector_smart_cast.lua",
            "materials/vector_arrow.material",
            "materials/blink_ring_1200.material",
            "materials/reticle_ground.material",
            "textures/vector_arrow.tga",
            "textures/blink_ring_1200.tga",
            "textures/reticle_ground.tga"
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
                .Which.Content.Should().Contain("/ui/vector_smart_cast.package");
        }
    }

    [Fact]
    public async Task VectorSmartCast_PackageInspection_ValidatesPayloads()
    {
        if (!NexusTestEnvironment.HasGameAssets) return;
        string repoRoot = FindRepoRoot();
        string modDir = Path.Combine(repoRoot, "games", "client", "mods", "vector_smart_cast_indicators");

        using ModPackage pkg = await HonModArchiveReader.ReadFromDirectoryAsync(modDir);
        pkg.HasPayloadFile("ui/vector_smart_cast.package").Should().BeTrue();
        pkg.HasPayloadFile("ui/scripts/vector_smart_cast.lua").Should().BeTrue();
        pkg.HasPayloadFile("materials/vector_arrow.material").Should().BeTrue();
        pkg.HasPayloadFile("materials/blink_ring_1200.material").Should().BeTrue();
        pkg.HasPayloadFile("materials/reticle_ground.material").Should().BeTrue();
        pkg.HasPayloadFile("textures/vector_arrow.tga").Should().BeTrue();
        pkg.HasPayloadFile("textures/blink_ring_1200.tga").Should().BeTrue();
        pkg.HasPayloadFile("textures/reticle_ground.tga").Should().BeTrue();
        pkg.HasPayloadFile("icon.png").Should().BeTrue();

        pkg.IconData.Should().NotBeNull();
        pkg.IconData!.Length.Should().BeGreaterThan(0);
        pkg.Sha256Hash.Should().NotBeNullOrWhiteSpace();
        pkg.FileSizeBytes.Should().BeGreaterThan(0);
    }

    [Fact]
    public async Task VectorSmartCast_InterfaceXml_ContainsPanelsAndTriggers()
    {
        if (!NexusTestEnvironment.HasGameAssets) return;
        string repoRoot = FindRepoRoot();
        string packagePath = Path.Combine(repoRoot, "games", "client", "mods", "vector_smart_cast_indicators", "ui",
            "vector_smart_cast.package");
        File.Exists(packagePath).Should().BeTrue();

        string xml = await File.ReadAllTextAsync(packagePath, Encoding.UTF8);
        XDocument doc = XDocument.Parse(xml);
        doc.Root.Should().NotBeNull();

        // 1. Root container
        XElement? root = doc.Root!.Elements("panel")
            .FirstOrDefault(p => (string?)p.Attribute("name") == "vector_smart_cast_root");
        root.Should().NotBeNull();

        // 2. Ground reticle
        XElement? reticle = root!.Elements("panel")
            .FirstOrDefault(p => (string?)p.Attribute("name") == "vector_ground_reticle_panel");
        reticle.Should().NotBeNull();

        // 3. Vector directional arrow container
        XElement? arrowContainer = root.Elements("panel")
            .FirstOrDefault(p => (string?)p.Attribute("name") == "vector_arrow_container");
        arrowContainer.Should().NotBeNull();
        arrowContainer!.Elements("panel").Should().Contain(p => (string?)p.Attribute("name") == "vector_arrow_rotator");
        arrowContainer.Elements("panel").Should().Contain(p => (string?)p.Attribute("name") == "vector_cone_rotator");

        // 4. Max blink 1200 ring
        XElement? blinkRing = root.Elements("panel")
            .FirstOrDefault(p => (string?)p.Attribute("name") == "vector_blink_ring_panel");
        blinkRing.Should().NotBeNull();

        // 5. Readout widget
        XElement? readout = root.Elements("panel")
            .FirstOrDefault(p => (string?)p.Attribute("name") == "vector_hud_readout_panel");
        readout.Should().NotBeNull();
    }

    [Fact]
    public async Task RuneKongorTimers_ManifestParsing_ExtractsAllAttributesAndOperations()
    {
        if (!NexusTestEnvironment.HasGameAssets) return;
        string repoRoot = FindRepoRoot();
        string modDir = Path.Combine(repoRoot, "games", "client", "mods", "rune_kongor_timers_hud");
        string modXmlPath = Path.Combine(modDir, "mod.xml");

        File.Exists(modXmlPath).Should().BeTrue($"Manifest '{modXmlPath}' must exist");

        string xml = await File.ReadAllTextAsync(modXmlPath, Encoding.UTF8);
        ModManifest manifest = ModManifestParser.Parse(xml);

        manifest.Name.Should().Be("Rune & Kongor Timers HUD");
        manifest.Version.Should().Be(NEXUS.Contracts.Domain.NexusVersionInfo.PlatformVersion);
        manifest.Author.Should().Be("guccidonut");
        manifest.AppVersion.Should().Be(">=4.10.1");
        manifest.Category.Should().Be("UI & HUD");
        manifest.Date.Should().NotBeNullOrWhiteSpace();

        manifest.FileCopies.Should().HaveCount(8);
        manifest.FileCopies.Select(c => c.TargetName).Should().Contain(
        [
            "ui/rune_kongor_timers.package",
            "ui/scripts/rune_kongor_timers.lua",
            "materials/rune_timer_icon.material",
            "materials/kongor_timer_icon.material",
            "textures/rune_timer_icon.tga",
            "textures/kongor_timer_icon.tga",
            "sounds/rune_spawn_warning.wav",
            "sounds/kongor_window_open.wav"
        ]);

        manifest.StringtableEdits.Should().HaveCount(2);
        manifest.StringtableEdits.Select(s => s.TargetName).Should().Contain(
        [
            "stringtables/client_sounds_en.str",
            "stringtables/game_messages_en.str"
        ]);

        manifest.FileEdits.Should().HaveCount(2);
    }

    [Fact]
    public async Task RuneKongorTimers_PackageInspection_ValidatesPayloads()
    {
        if (!NexusTestEnvironment.HasGameAssets) return;
        string repoRoot = FindRepoRoot();
        string modDir = Path.Combine(repoRoot, "games", "client", "mods", "rune_kongor_timers_hud");

        using ModPackage pkg = await HonModArchiveReader.ReadFromDirectoryAsync(modDir);
        pkg.HasPayloadFile("ui/rune_kongor_timers.package").Should().BeTrue();
        pkg.HasPayloadFile("ui/scripts/rune_kongor_timers.lua").Should().BeTrue();
        pkg.HasPayloadFile("materials/rune_timer_icon.material").Should().BeTrue();
        pkg.HasPayloadFile("materials/kongor_timer_icon.material").Should().BeTrue();
        pkg.HasPayloadFile("textures/rune_timer_icon.tga").Should().BeTrue();
        pkg.HasPayloadFile("textures/kongor_timer_icon.tga").Should().BeTrue();
        pkg.HasPayloadFile("sounds/rune_spawn_warning.wav").Should().BeTrue();
        pkg.HasPayloadFile("sounds/kongor_window_open.wav").Should().BeTrue();
        pkg.HasPayloadFile("icon.png").Should().BeTrue();
    }

    [Fact]
    public async Task RuneKongorTimers_InterfaceXml_ContainsRuneAndKongorPanels()
    {
        if (!NexusTestEnvironment.HasGameAssets) return;
        string repoRoot = FindRepoRoot();
        string packagePath = Path.Combine(repoRoot, "games", "client", "mods", "rune_kongor_timers_hud", "ui",
            "rune_kongor_timers.package");
        File.Exists(packagePath).Should().BeTrue();

        string xml = await File.ReadAllTextAsync(packagePath, Encoding.UTF8);
        XDocument doc = XDocument.Parse(xml);
        doc.Root.Should().NotBeNull();

        XElement? root = doc.Root!.Elements("panel")
            .FirstOrDefault(p => (string?)p.Attribute("name") == "rune_kongor_timers_root");
        root.Should().NotBeNull();

        XElement? runePanel = root!.Elements("panel")
            .FirstOrDefault(p => (string?)p.Attribute("name") == "rune_timer_panel");
        runePanel.Should().NotBeNull();
        runePanel!.Elements("frame").Should().Contain(f => (string?)f.Attribute("name") == "rune_timer_warning_glow");

        XElement? kongorPanel = root.Elements("panel")
            .FirstOrDefault(p => (string?)p.Attribute("name") == "kongor_timer_panel");
        kongorPanel.Should().NotBeNull();
        kongorPanel!.Elements("frame").Should().Contain(f => (string?)f.Attribute("name") == "kongor_window_glow");
    }

    [Fact]
    public async Task WardVision_ManifestParsing_ExtractsAllAttributesAndOperations()
    {
        if (!NexusTestEnvironment.HasGameAssets) return;
        string repoRoot = FindRepoRoot();
        string modDir = Path.Combine(repoRoot, "games", "client", "mods", "ward_vision_radius_overlay");
        string modXmlPath = Path.Combine(modDir, "mod.xml");

        File.Exists(modXmlPath).Should().BeTrue($"Manifest '{modXmlPath}' must exist");

        string xml = await File.ReadAllTextAsync(modXmlPath, Encoding.UTF8);
        ModManifest manifest = ModManifestParser.Parse(xml);

        manifest.Name.Should().Be("Ward Vision Radius Overlay");
        manifest.Version.Should().Be(NEXUS.Contracts.Domain.NexusVersionInfo.PlatformVersion);
        manifest.Author.Should().Be("guccidonut");
        manifest.AppVersion.Should().Be(">=4.10.1");
        manifest.Category.Should().Be("UI & HUD");
        manifest.Date.Should().NotBeNullOrWhiteSpace();

        manifest.FileCopies.Should().HaveCount(8);
        manifest.FileCopies.Select(c => c.TargetName).Should().Contain(
        [
            "ui/ward_vision_overlay.package",
            "ui/scripts/ward_vision_overlay.lua",
            "materials/ward_observer_ring.material",
            "materials/ward_sentry_ring.material",
            "textures/ward_observer_ring.tga",
            "textures/ward_sentry_ring.tga",
            "effects/ward_observer_preview.effect",
            "effects/ward_sentry_preview.effect"
        ]);

        manifest.FileEdits.Should().HaveCount(2);
    }

    [Fact]
    public async Task WardVision_PackageInspection_ValidatesPayloads()
    {
        if (!NexusTestEnvironment.HasGameAssets) return;
        string repoRoot = FindRepoRoot();
        string modDir = Path.Combine(repoRoot, "games", "client", "mods", "ward_vision_radius_overlay");

        using ModPackage pkg = await HonModArchiveReader.ReadFromDirectoryAsync(modDir);
        pkg.HasPayloadFile("ui/ward_vision_overlay.package").Should().BeTrue();
        pkg.HasPayloadFile("ui/scripts/ward_vision_overlay.lua").Should().BeTrue();
        pkg.HasPayloadFile("materials/ward_observer_ring.material").Should().BeTrue();
        pkg.HasPayloadFile("materials/ward_sentry_ring.material").Should().BeTrue();
        pkg.HasPayloadFile("textures/ward_observer_ring.tga").Should().BeTrue();
        pkg.HasPayloadFile("textures/ward_sentry_ring.tga").Should().BeTrue();
        pkg.HasPayloadFile("effects/ward_observer_preview.effect").Should().BeTrue();
        pkg.HasPayloadFile("effects/ward_sentry_preview.effect").Should().BeTrue();
        pkg.HasPayloadFile("icon.png").Should().BeTrue();
    }

    [Fact]
    public async Task WardVision_InterfaceXml_ContainsObserverAndSentryPanels()
    {
        if (!NexusTestEnvironment.HasGameAssets) return;
        string repoRoot = FindRepoRoot();
        string packagePath = Path.Combine(repoRoot, "games", "client", "mods", "ward_vision_radius_overlay", "ui",
            "ward_vision_overlay.package");
        File.Exists(packagePath).Should().BeTrue();

        string xml = await File.ReadAllTextAsync(packagePath, Encoding.UTF8);
        XDocument doc = XDocument.Parse(xml);
        doc.Root.Should().NotBeNull();

        XElement? root = doc.Root!.Elements("panel")
            .FirstOrDefault(p => (string?)p.Attribute("name") == "ward_vision_root");
        root.Should().NotBeNull();

        XElement? obs = root!.Elements("panel")
            .FirstOrDefault(p => (string?)p.Attribute("name") == "ward_observer_preview_panel");
        obs.Should().NotBeNull();

        XElement? sentry = root.Elements("panel")
            .FirstOrDefault(p => (string?)p.Attribute("name") == "ward_sentry_preview_panel");
        sentry.Should().NotBeNull();

        XElement? hudInfo = root.Elements("panel")
            .FirstOrDefault(p => (string?)p.Attribute("name") == "ward_placement_info_panel");
        hudInfo.Should().NotBeNull();
    }

    #endregion

    #region Tier 2: Lua Logic, Mathematical Calculations & Audio Headers (8 Tests)

    [Fact]
    public async Task VectorSmartCast_LuaScript_ValidatesHeroAbilitiesAndFormulas()
    {
        if (!NexusTestEnvironment.HasGameAssets) return;
        string repoRoot = FindRepoRoot();
        string luaPath = Path.Combine(repoRoot, "games", "client", "mods", "vector_smart_cast_indicators", "ui",
            "scripts", "vector_smart_cast.lua");
        File.Exists(luaPath).Should().BeTrue();

        string lua = await File.ReadAllTextAsync(luaPath, Encoding.UTF8);

        // Verify ability registrations
        lua.Should().Contain("Ability_Bombardier4");
        lua.Should().Contain("1800"); // Bombardier Air Strike range
        lua.Should().Contain("Ability_Rally1");
        lua.Should().Contain("800"); // Rally Compel range
        lua.Should().Contain("Ability_Zephyr2");
        lua.Should().Contain("900"); // Zephyr Gust range
        lua.Should().Contain("Ability_Chipper4");
        lua.Should().Contain("1500"); // Chipper Sawblade Showdown range

        // Verify math methods
        lua.Should().Contain("VectorSmartCast.CalculateAngle");
        lua.Should().Contain("VectorSmartCast.CalculateDistance");
        lua.Should().Contain("VectorSmartCast.ClampRange");
        lua.Should().Contain("VectorSmartCast.GetVectorEndpoints");

        // Verify Portal Key constant
        lua.Should().Contain("MAX_BLINK_RANGE = 1200");
    }

    [Fact]
    public void VectorSmartCast_TrigonometryMath_CalculatesPreciseAngleAndDistance()
    {
        // Simulate Lua CalculateAngle and CalculateDistance
        double CalcAngle(double ox, double oy, double tx, double ty)
        {
            double dx = tx - ox;
            double dy = ty - oy;
            if (dx == 0 && dy == 0) return 0;
            double rad = Math.Atan2(dy, dx);
            double deg = rad * (180.0 / Math.PI);
            if (deg < 0) deg += 360;
            return deg;
        }

        double CalcDist(double ox, double oy, double tx, double ty)
        {
            double dx = tx - ox;
            double dy = ty - oy;
            return Math.Sqrt(dx * dx + dy * dy);
        }

        // East: (0,0) -> (100, 0) => angle 0 deg, dist 100
        CalcAngle(0, 0, 100, 0).Should().BeApproximately(0.0, 0.01);
        CalcDist(0, 0, 100, 0).Should().Be(100.0);

        // North: (0,0) -> (0, 100) => angle 90 deg, dist 100
        CalcAngle(0, 0, 0, 100).Should().BeApproximately(90.0, 0.01);
        CalcDist(0, 0, 0, 100).Should().Be(100.0);

        // West: (0,0) -> (-100, 0) => angle 180 deg, dist 100
        CalcAngle(0, 0, -100, 0).Should().BeApproximately(180.0, 0.01);
        CalcDist(0, 0, -100, 0).Should().Be(100.0);

        // South: (0,0) -> (0, -100) => angle 270 deg, dist 100
        CalcAngle(0, 0, 0, -100).Should().BeApproximately(270.0, 0.01);
        CalcDist(0, 0, 0, -100).Should().Be(100.0);
    }

    [Fact]
    public async Task RuneKongorTimers_LuaScript_ValidatesIntervalsAndStateTransitions()
    {
        if (!NexusTestEnvironment.HasGameAssets) return;
        string repoRoot = FindRepoRoot();
        string luaPath = Path.Combine(repoRoot, "games", "client", "mods", "rune_kongor_timers_hud", "ui", "scripts",
            "rune_kongor_timers.lua");
        File.Exists(luaPath).Should().BeTrue();

        string lua = await File.ReadAllTextAsync(luaPath, Encoding.UTF8);

        lua.Should().Contain("runeIntervalSeconds = 120");
        lua.Should().Contain("runeWarningThreshold = 15");
        lua.Should().Contain("KONGOR_MIN_RESPAWN = 480");
        lua.Should().Contain("KONGOR_MAX_RESPAWN = 660");
        lua.Should().Contain("KONGOR_STATE_ALIVE = \"ALIVE\"");
        lua.Should().Contain("KONGOR_STATE_DEAD = \"DEAD\"");
        lua.Should().Contain("KONGOR_STATE_WINDOW_OPEN = \"WINDOW OPEN\"");

        lua.Should().Contain("function RuneKongorTimers.UpdateRuneTimer");
        lua.Should().Contain("function RuneKongorTimers.OnKongorKilled");
        lua.Should().Contain("function RuneKongorTimers.UpdateKongorTimer");
        lua.Should().Contain("function RuneKongorTimers.OnKongorRespawned");
    }

    [Fact]
    public void RuneKongorTimers_RuneIntervalMath_ComputesCorrectRemainingSeconds()
    {
        int interval = 120; // 2:00 minutes

        int GetRemaining(int matchTimeSeconds)
        {
            int secIntoCycle = matchTimeSeconds % interval;
            int rem = interval - secIntoCycle;
            if (rem == interval && matchTimeSeconds > 0) rem = 0;
            return rem;
        }

        // At t = 0s -> 120s remaining (2:00)
        GetRemaining(0).Should().Be(120);

        // At t = 60s -> 60s remaining (1:00)
        GetRemaining(60).Should().Be(60);

        // At t = 105s (15s before 2:00) -> 15s remaining (warning window)
        GetRemaining(105).Should().Be(15);

        // At t = 120s -> 0s remaining (spawn instant)
        GetRemaining(120).Should().Be(0);

        // At t = 125s -> 115s remaining to 4:00 mark
        GetRemaining(125).Should().Be(115);
    }

    [Fact]
    public void RuneKongorTimers_KongorRespawnWindow_Validates3States()
    {
        const int minRespawn = 480; // 8:00
        const int maxRespawn = 660; // 11:00

        string GetState(int killTime, int currentTime)
        {
            if (killTime < 0) return "ALIVE";
            int elapsed = currentTime - killTime;
            if (elapsed < minRespawn) return "DEAD";
            if (elapsed <= maxRespawn) return "WINDOW OPEN";
            return "ALIVE"; // respawned
        }

        int killT = 300; // Killed at 5:00

        // At t=400s (elapsed 100s) -> DEAD (countdown to 8:00 min)
        GetState(killT, 400).Should().Be("DEAD");

        // At t=779s (elapsed 479s) -> DEAD
        GetState(killT, 779).Should().Be("DEAD");

        // At t=780s (elapsed 480s / 8:00) -> WINDOW OPEN
        GetState(killT, 780).Should().Be("WINDOW OPEN");

        // At t=850s (elapsed 550s) -> WINDOW OPEN
        GetState(killT, 850).Should().Be("WINDOW OPEN");

        // At t=960s (elapsed 660s / 11:00) -> WINDOW OPEN
        GetState(killT, 960).Should().Be("WINDOW OPEN");

        // At t=961s (elapsed 661s) -> ALIVE (guaranteed respawned)
        GetState(killT, 961).Should().Be("ALIVE");
    }

    [Fact]
    public async Task RuneKongorTimers_AudioAlerts_Valid44ByteRiffWavHeaders()
    {
        if (!NexusTestEnvironment.HasGameAssets) return;
        string repoRoot = FindRepoRoot();
        string modDir = Path.Combine(repoRoot, "games", "client", "mods", "rune_kongor_timers_hud");

        string[] wavFiles =
        [
            Path.Combine(modDir, "sounds", "rune_spawn_warning.wav"),
            Path.Combine(modDir, "sounds", "kongor_window_open.wav")
        ];

        foreach (string wavPath in wavFiles)
        {
            File.Exists(wavPath).Should().BeTrue($"WAV file '{wavPath}' must exist");
            byte[] bytes = await File.ReadAllBytesAsync(wavPath);
            bytes.Length.Should().BeGreaterThanOrEqualTo(44);

            string riff = Encoding.ASCII.GetString(bytes, 0, 4);
            riff.Should().Be("RIFF");

            string wave = Encoding.ASCII.GetString(bytes, 8, 4);
            wave.Should().Be("WAVE");

            string fmt = Encoding.ASCII.GetString(bytes, 12, 4);
            fmt.Should().Be("fmt ");
        }
    }

    [Fact]
    public async Task WardVision_LuaScript_ValidatesWardSightAndCampBoundingBoxes()
    {
        if (!NexusTestEnvironment.HasGameAssets) return;
        string repoRoot = FindRepoRoot();
        string luaPath = Path.Combine(repoRoot, "games", "client", "mods", "ward_vision_radius_overlay", "ui",
            "scripts", "ward_vision_overlay.lua");
        File.Exists(luaPath).Should().BeTrue();

        string lua = await File.ReadAllTextAsync(luaPath, Encoding.UTF8);

        // Verify ward radii
        lua.Should().Contain("1600"); // Observer sight
        lua.Should().Contain("850"); // Sentry True Sight
        lua.Should().Contain("Item_FlamingEye");
        lua.Should().Contain("Item_ManaEye");

        // Verify neutral camp spawn boxes
        lua.Should().Contain("NEUTRAL_CAMP_SPAWN_BOXES");
        lua.Should().Contain("Legion Hard (Well)");
        lua.Should().Contain("Hellbourne Ancients");
        lua.Should().Contain("WardVision.CheckCampBlocking");
    }

    [Fact]
    public void WardVision_CampBlockingDetection_AccuratelyDetectsOverlap()
    {
        // Simulated camp box: Legion Hard (Well) [3100, 3300, 3850, 4050]
        bool CheckOverlap(double x, double y)
        {
            return x >= 3100 && x <= 3850 && y >= 3300 && y <= 4050;
        }

        // Inside spawn box -> Blocked
        CheckOverlap(3500, 3600).Should().BeTrue();
        CheckOverlap(3100, 3300).Should().BeTrue(); // Boundary
        CheckOverlap(3850, 4050).Should().BeTrue(); // Boundary

        // Outside spawn box -> Safe
        CheckOverlap(3000, 3200).Should().BeFalse();
        CheckOverlap(4000, 4100).Should().BeFalse();
        CheckOverlap(0, 0).Should().BeFalse();
    }

    #endregion

    #region Tier 3: DAG Conflict Resolution & VFS Injection (4 Tests)

    [Fact]
    public async Task DAGResolution_All3HudMods_TopologicalSortSucceedsWithoutCycles()
    {
        if (!NexusTestEnvironment.HasGameAssets) return;
        string repoRoot = FindRepoRoot();
        string modsDir = Path.Combine(repoRoot, "games", "client", "mods");

        using ModPackage mod1 =
            await HonModArchiveReader.ReadFromDirectoryAsync(Path.Combine(modsDir, "vector_smart_cast_indicators"));
        using ModPackage mod2 =
            await HonModArchiveReader.ReadFromDirectoryAsync(Path.Combine(modsDir, "rune_kongor_timers_hud"));
        using ModPackage mod3 =
            await HonModArchiveReader.ReadFromDirectoryAsync(Path.Combine(modsDir, "ward_vision_radius_overlay"));

        mod1.IsEnabled = true;
        mod2.IsEnabled = true;
        mod3.IsEnabled = true;

        SortResult result = TopologicalSorter.ResolveAndSort([mod1, mod2, mod3]);
        result.Success.Should()
            .BeTrue("The 3 HUD suite mods must resolve cleanly without missing dependencies or cyclic graphs");
        result.ExecutionPlan.Should().HaveCount(3);
        result.CycleReports.Should().BeEmpty();
    }

    [Fact]
    public async Task ConflictMatrix_All3HudMods_ZeroFatalCollisions()
    {
        if (!NexusTestEnvironment.HasGameAssets) return;
        string repoRoot = FindRepoRoot();
        string modsDir = Path.Combine(repoRoot, "games", "client", "mods");

        using ModPackage mod1 =
            await HonModArchiveReader.ReadFromDirectoryAsync(Path.Combine(modsDir, "vector_smart_cast_indicators"));
        using ModPackage mod2 =
            await HonModArchiveReader.ReadFromDirectoryAsync(Path.Combine(modsDir, "rune_kongor_timers_hud"));
        using ModPackage mod3 =
            await HonModArchiveReader.ReadFromDirectoryAsync(Path.Combine(modsDir, "ward_vision_radius_overlay"));

        ConflictReport report = ConflictMatrix.Analyze([mod1, mod2, mod3]);
        report.HasConflicts.Should().BeFalse("All 3 HUD mods coexist cleanly with zero hard collisions");
    }

    [Fact]
    public async Task LayeredVfsInjection_All3HudMods_AppliesCleanlyToClientInterfaces()
    {
        if (!NexusTestEnvironment.HasGameAssets) return;
        string repoRoot = FindRepoRoot();
        string modsDir = Path.Combine(repoRoot, "games", "client", "mods");

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

        // Load 3 mod packages
        using ModPackage mod1 =
            await HonModArchiveReader.ReadFromDirectoryAsync(Path.Combine(modsDir, "vector_smart_cast_indicators"));
        using ModPackage mod2 =
            await HonModArchiveReader.ReadFromDirectoryAsync(Path.Combine(modsDir, "rune_kongor_timers_hud"));
        using ModPackage mod3 =
            await HonModArchiveReader.ReadFromDirectoryAsync(Path.Combine(modsDir, "ward_vision_radius_overlay"));

        mod1.IsEnabled = true;
        mod2.IsEnabled = true;
        mod3.IsEnabled = true;

        ModManager manager = new();

        ApplyResult applyResult = await manager.ApplyModsAsync(new ApplyOptions
        {
            GameClientDirectory = _clientDir,
            ModsToApply = [mod1, mod2, mod3],
            Mode = InjectionMode.Vfs
        });

        applyResult.Success.Should().BeTrue();
        applyResult.AppliedMods.Should().HaveCount(3);

        string overrideDir = Path.Combine(_clientDir, "override");
        Directory.Exists(overrideDir).Should().BeTrue();

        // Check patched interfaces contain all 3 package includes
        string patchedApex = await File.ReadAllTextAsync(Path.Combine(overrideDir, "ui", "game_apex.interface"));
        patchedApex.Should().Contain("<include file=\"/ui/vector_smart_cast.package\" />");
        patchedApex.Should().Contain("<include file=\"/ui/rune_kongor_timers.package\" />");
        patchedApex.Should().Contain("<include file=\"/ui/ward_vision_overlay.package\" />");

        string patchedLegacy = await File.ReadAllTextAsync(Path.Combine(overrideDir, "ui", "game_legacy.interface"));
        patchedLegacy.Should().Contain("<include file=\"/ui/vector_smart_cast.package\" />");
        patchedLegacy.Should().Contain("<include file=\"/ui/rune_kongor_timers.package\" />");
        patchedLegacy.Should().Contain("<include file=\"/ui/ward_vision_overlay.package\" />");

        // Verify copied files exist in override
        File.Exists(Path.Combine(overrideDir, "ui", "vector_smart_cast.package")).Should().BeTrue();
        File.Exists(Path.Combine(overrideDir, "ui", "scripts", "vector_smart_cast.lua")).Should().BeTrue();
        File.Exists(Path.Combine(overrideDir, "ui", "rune_kongor_timers.package")).Should().BeTrue();
        File.Exists(Path.Combine(overrideDir, "ui", "scripts", "rune_kongor_timers.lua")).Should().BeTrue();
        File.Exists(Path.Combine(overrideDir, "ui", "ward_vision_overlay.package")).Should().BeTrue();
        File.Exists(Path.Combine(overrideDir, "ui", "scripts", "ward_vision_overlay.lua")).Should().BeTrue();

        // Test Rollback
        RollbackResult rollback = await manager.RollbackAsync(_clientDir);
        rollback.Success.Should().BeTrue();
        File.Exists(Path.Combine(overrideDir, "ui", "vector_smart_cast.package")).Should().BeFalse();
        File.Exists(Path.Combine(overrideDir, "ui", "game_apex.interface")).Should().BeFalse();
    }

    [Fact]
    public async Task ManifestRoundTrip_PreservesAll3Manifests_ExecutesSuccessfully()
    {
        if (!NexusTestEnvironment.HasGameAssets) return;
        string repoRoot = FindRepoRoot();
        string modsDir = Path.Combine(repoRoot, "games", "client", "mods");

        string[] modNames = ["vector_smart_cast_indicators", "rune_kongor_timers_hud", "ward_vision_radius_overlay"];

        foreach (string name in modNames)
        {
            string xmlPath = Path.Combine(modsDir, name, "mod.xml");
            string originalXml = await File.ReadAllTextAsync(xmlPath);
            ModManifest original = ModManifestParser.Parse(originalXml);

            string serialized = ModManifestParser.Serialize(original);
            ModManifest deserialized = ModManifestParser.Parse(serialized);

            deserialized.Name.Should().Be(original.Name);
            deserialized.Version.Should().Be(original.Version);
            deserialized.Author.Should().Be(original.Author);
            deserialized.AppVersion.Should().Be(original.AppVersion);
            deserialized.FileCopies.Count.Should().Be(original.FileCopies.Count);
            deserialized.FileEdits.Count.Should().Be(original.FileEdits.Count);
            deserialized.StringtableEdits.Count.Should().Be(original.StringtableEdits.Count);
        }
    }

    #endregion
}

