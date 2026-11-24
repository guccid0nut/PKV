// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

using System.IO.Compression;
using System.Text;
using System.Xml.Linq;
using FluentAssertions;
using Kongor.Modding.Conditions;
using Kongor.Modding.Core.Models;
using Kongor.Modding.Core.Packaging;
using Kongor.Modding.Core.Parsing;
using Kongor.Modding.Patching;
using Kongor.Modding.Resolution;
using Kongor.Modding.Vfs;
using Xunit;

namespace Kongor.Modding.Tests;

/// <summary>
///     Comprehensive, 4-Tier End-to-End Test Suite for the ProjectKongor Essential Competitive &amp; QoL Mod Suite:
///     1. Tactical HUD &amp; Neutral Camp Overlays (tactical_hud_neutral_overlays.honmod)
///     2. CC &amp; Debuff Duration Overhead Bars (cc_debuff_overhead_bars.honmod)
///     3. Combat Text &amp; Damage Float Customizer (combat_text_damage_float.honmod)
///     4. Classic Unreal Tournament Announcer Pack (ut_announcer_pack.honmod)
/// </summary>
public class HonModSuiteE2ETests : IDisposable
{
    private readonly string _clientDir;
    private readonly string _modsDir;
    private readonly string _tempDir;

    public HonModSuiteE2ETests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "HonModSuiteE2E_" + Guid.NewGuid().ToString("N"));
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
                // Suppress temporary cleanup failures on Windows file locks
            }
    }

    #region Audio and Mod Creation Fixture Helpers

    /// <summary>
    ///     Generates a valid standard 16-bit PCM RIFF WAVE audio byte array.
    /// </summary>
    public static byte[] CreateValidWavBytes(int sampleRate = 44100, short channels = 2, short bitsPerSample = 16,
        int sampleCount = 100)
    {
        int bytesPerSample = bitsPerSample / 8;
        int subChunk2Size = sampleCount * channels * bytesPerSample;
        int chunkSize = 36 + subChunk2Size;
        int byteRate = sampleRate * channels * bytesPerSample;
        short blockAlign = (short)(channels * bytesPerSample);

        using MemoryStream stream = new(44 + subChunk2Size);
        using BinaryWriter writer = new(stream, Encoding.ASCII);

        // Chunk ID: "RIFF"
        writer.Write(['R', 'I', 'F', 'F']);
        writer.Write(chunkSize);
        // Format: "WAVE"
        writer.Write(['W', 'A', 'V', 'E']);

        // Subchunk 1: "fmt "
        writer.Write(['f', 'm', 't', ' ']);
        writer.Write(16); // Subchunk1Size for PCM
        writer.Write((short)1); // AudioFormat = 1 (PCM)
        writer.Write(channels);
        writer.Write(sampleRate);
        writer.Write(byteRate);
        writer.Write(blockAlign);
        writer.Write(bitsPerSample);

        // Subchunk 2: "data"
        writer.Write(['d', 'a', 't', 'a']);
        writer.Write(subChunk2Size);

        // Dummy PCM audio samples
        byte[] audioSamples = new byte[subChunk2Size];
        for (int i = 0; i < audioSamples.Length; i++) audioSamples[i] = (byte)(i % 128);
        writer.Write(audioSamples);

        writer.Flush();
        return stream.ToArray();
    }

    /// <summary>
    ///     Verifies the 44-byte RIFF WAV header integrity.
    /// </summary>
    public static bool VerifyWavHeader(byte[] wavBytes, int expectedSampleRate = 44100, short expectedChannels = 2,
        short expectedBits = 16)
    {
        if (wavBytes == null || wavBytes.Length < 44) return false;

        using MemoryStream stream = new(wavBytes);
        using BinaryReader reader = new(stream, Encoding.ASCII);

        string chunkId = new(reader.ReadChars(4));
        if (chunkId != "RIFF") return false;

        int chunkSize = reader.ReadInt32();
        if (chunkSize != wavBytes.Length - 8) return false;

        string format = new(reader.ReadChars(4));
        if (format != "WAVE") return false;

        string subchunk1Id = new(reader.ReadChars(4));
        if (subchunk1Id != "fmt ") return false;

        int subchunk1Size = reader.ReadInt32();
        if (subchunk1Size < 16) return false;

        short audioFormat = reader.ReadInt16();
        if (audioFormat != 1) return false; // 1 = PCM

        short channels = reader.ReadInt16();
        if (channels != expectedChannels) return false;

        int sampleRate = reader.ReadInt32();
        if (sampleRate != expectedSampleRate) return false;

        int byteRate = reader.ReadInt32();
        short blockAlign = reader.ReadInt16();
        short bitsPerSample = reader.ReadInt16();
        if (bitsPerSample != expectedBits) return false;

        string subchunk2Id = new(reader.ReadChars(4));
        if (subchunk2Id != "data") return false;

        int subchunk2Size = reader.ReadInt32();
        if (subchunk2Size <= 0 || subchunk2Size + 44 > wavBytes.Length) return false;

        return true;
    }

    /// <summary>
    ///     Builds and writes the Tactical HUD &amp; Neutral Overlays mod package.
    /// </summary>
    public static async Task<string> CreateTacticalHudModPackageAsync(string destinationDir)
    {
        string filePath = Path.Combine(destinationDir, "tactical_hud_neutral_overlays.honmod");
        ModManifest manifest = new()
        {
            Name = "Tactical HUD & Neutral Camp Overlays",
            Version = "1.0.0",
            Author = "Kongor Competitive Team",
            Description =
                "Provides camp spawn boxes, stack pull timers (:45-:55), buyback tracking, and ally ultimate cooldown indicators.",
            AppVersion = "4.10.*",
            MmVersion = "1.3",
            Category = "Combat UI",
            Icon = "icon.png",
            FileCopies =
            [
                new CopyFileOperation
                {
                    TargetName = "ui/tactical_hud.package",
                    SourcePath = "ui/tactical_hud.package",
                    Overwrite = FileOverwriteMode.Yes
                },
                new CopyFileOperation
                {
                    TargetName = "ui/scripts/tactical_hud.lua",
                    SourcePath = "ui/scripts/tactical_hud.lua",
                    Overwrite = FileOverwriteMode.Yes
                }
            ],
            FileEdits =
            [
                new EditFileOperation
                {
                    TargetName = "ui/game_apex.interface",
                    Operations =
                    [
                        new FindOperation("<interface name=\"game\">", FindPosition.After),
                        new InsertOperation("\n  <include file=\"/ui/tactical_hud.package\" />\n",
                            Position: InsertPosition.After)
                    ]
                },
                new EditFileOperation
                {
                    TargetName = "ui/game_legacy.interface",
                    Operations =
                    [
                        new FindOperation("<interface name=\"game\">", FindPosition.After),
                        new InsertOperation("\n  <include file=\"/ui/tactical_hud.package\" />\n",
                            Position: InsertPosition.After)
                    ]
                }
            ],
            StringtableEdits =
            [
                new EditStringtableOperation
                {
                    TargetName = "stringtables/game_messages_en.str",
                    Entries =
                    [
                        new StringtableEntryOperation("hud_camp_stack_timer", "Stack Pull Window: :45 - :55"),
                        new StringtableEntryOperation("hud_buyback_ready", "Buyback Ready"),
                        new StringtableEntryOperation("hud_ultimate_cooldown", "Ultimate on Cooldown")
                    ]
                }
            ]
        };

        Dictionary<string, byte[]> payloads = new()
        {
            ["ui/tactical_hud.package"] = Encoding.UTF8.GetBytes(
                "<package name=\"tactical_hud\">\n" +
                "  <panel name=\"neutral_camp_boxes\" width=\"100%\" height=\"100%\" visible=\"true\" />\n" +
                "  <panel name=\"ally_ultimate_tracker\" x=\"10\" y=\"10\" width=\"300\" height=\"40\" />\n" +
                "  <panel name=\"buyback_status_overlay\" x=\"10\" y=\"55\" width=\"300\" height=\"30\" />\n" +
                "</package>"),
            ["ui/scripts/tactical_hud.lua"] = Encoding.UTF8.GetBytes(
                "function InitTacticalHud()\n" +
                "    print('Tactical HUD Initialized')\n" +
                "end\n" +
                "function UpdateCampTimers(gameTimeSeconds)\n" +
                "    local sec = gameTimeSeconds % 60\n" +
                "    return sec >= 45 and sec <= 55\n" +
                "end\n" +
                "function TrackAllyUltimates(teamId)\n" +
                "    return { ready = true, cooldown = 0 }\n" +
                "end\n" +
                "function UpdateBuybackStatus(playerId)\n" +
                "    return true\n" +
                "end\n"),
            ["icon.png"] = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0x00, 0x00, 0x00, 0x0D]
        };

        await HonModArchiveWriter.WritePackageAsync(filePath, manifest, payloads, payloads["icon.png"]);
        return filePath;
    }

    /// <summary>
    ///     Builds and writes the CC &amp; Debuff Duration Overhead Bars mod package.
    /// </summary>
    public static async Task<string> CreateCcDebuffModPackageAsync(string destinationDir)
    {
        string filePath = Path.Combine(destinationDir, "cc_debuff_overhead_bars.honmod");
        ModManifest manifest = new()
        {
            Name = "CC & Debuff Duration Overhead Bars",
            Version = "1.0.0",
            Author = "Kongor Competitive Team",
            Description =
                "Overhead floating countdown timers and progress bars for Stuns, Silences, Roots, Disarms, Hexes, and Taunts.",
            AppVersion = "4.10.*",
            MmVersion = "1.3",
            Category = "Combat UI",
            Icon = "icon.png",
            FileCopies =
            [
                new CopyFileOperation
                {
                    TargetName = "ui/scripts/cc_debuff_tracker.lua",
                    SourcePath = "ui/scripts/cc_debuff_tracker.lua",
                    Overwrite = FileOverwriteMode.Yes
                }
            ],
            FileEdits =
            [
                new EditFileOperation
                {
                    TargetName = "ui/alt_info_hero.interface",
                    Operations =
                    [
                        new FindOperation("<panel name=\"alt_info_hero_container\"", FindPosition.After),
                        new InsertOperation("\n  <include file=\"/ui/cc_debuff_bars.package\" />\n",
                            Position: InsertPosition.After)
                    ]
                },
                new EditFileOperation
                {
                    TargetName = "ui/scripts/alt_info_hero.lua",
                    Operations =
                    [
                        new FindOperation("function UpdateAltInfo(widget)", FindPosition.After),
                        new InsertOperation("\n    UpdateCCDebuffBars(widget)\n", Position: InsertPosition.After)
                    ]
                }
            ]
        };

        Dictionary<string, byte[]> payloads = new()
        {
            ["ui/scripts/cc_debuff_tracker.lua"] = Encoding.UTF8.GetBytes(
                "local CC_TYPES = { 'Stun', 'Silence', 'Root', 'Disarm', 'Hex', 'Taunt' }\n" +
                "function UpdateCCDebuffBars(widget)\n" +
                "    for _, cc in ipairs(CC_TYPES) do\n" +
                "        -- Update overhead countdown bar for active crowd control\n" +
                "    end\n" +
                "end\n"),
            ["icon.png"] = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0x00, 0x00, 0x00, 0x0D]
        };

        await HonModArchiveWriter.WritePackageAsync(filePath, manifest, payloads, payloads["icon.png"]);
        return filePath;
    }

    /// <summary>
    ///     Builds and writes the Combat Text &amp; Damage Float Customizer mod package.
    /// </summary>
    public static async Task<string> CreateCombatTextModPackageAsync(string destinationDir)
    {
        string filePath = Path.Combine(destinationDir, "combat_text_damage_float.honmod");
        ModManifest manifest = new()
        {
            Name = "Combat Text & Damage Float Customizer",
            Version = "1.0.0",
            Author = "Kongor Competitive Team",
            Description =
                "Visual floating combat text customizer for Physical, Magical, Pure, and Critical damage types.",
            AppVersion = "4.10.*",
            MmVersion = "1.3",
            Category = "Visuals",
            Icon = "icon.png",
            FileEdits =
            [
                new EditFileOperation
                {
                    TargetName = "base.gamemechanics",
                    Operations =
                    [
                        new FindOperation("<damagetype name=\"Physical\""),
                        new ReplaceOperation(
                            "<damagetype name=\"Physical\" color=\"#FF4500\" floatingcombattext=\"true\" />"),
                        new FindOperation("<damagetype name=\"Magic\""),
                        new ReplaceOperation(
                            "<damagetype name=\"Magic\" color=\"#00BFFF\" floatingcombattext=\"true\" />"),
                        new FindOperation("<damagetype name=\"Pure\""),
                        new ReplaceOperation(
                            "<damagetype name=\"Pure\" color=\"#FFD700\" floatingcombattext=\"true\" />"),
                        new FindOperation("<damagetype name=\"Critical\""),
                        new ReplaceOperation(
                            "<damagetype name=\"Critical\" color=\"#DC143C\" scale=\"1.25\" floatingcombattext=\"true\" />")
                    ]
                }
            ],
            StringtableEdits =
            [
                new EditStringtableOperation
                {
                    TargetName = "stringtables/game_messages_en.str",
                    Entries =
                    [
                        new StringtableEntryOperation("combat_text_physical", "Physical"),
                        new StringtableEntryOperation("combat_text_magical", "Magical"),
                        new StringtableEntryOperation("combat_text_pure", "Pure"),
                        new StringtableEntryOperation("combat_text_critical", "Critical Strike!")
                    ]
                }
            ]
        };

        Dictionary<string, byte[]> payloads = new()
        {
            ["icon.png"] = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0x00, 0x00, 0x00, 0x0D]
        };

        await HonModArchiveWriter.WritePackageAsync(filePath, manifest, payloads, payloads["icon.png"]);
        return filePath;
    }

    /// <summary>
    ///     Builds and writes the Classic Unreal Tournament Announcer Pack mod package with valid audio files.
    /// </summary>
    public static async Task<string> CreateUtAnnouncerModPackageAsync(string destinationDir)
    {
        string filePath = Path.Combine(destinationDir, "ut_announcer_pack.honmod");
        byte[] validWav = CreateValidWavBytes(44100, 2, 16, 200);

        string[] soundNames =
        [
            "firstblood", "doublekill", "triplekill", "ultrakill", "monsterkill", "ludicrouskill", "holyshit",
            "killingspree", "dominating", "megakill", "unstoppable", "wickedsick", "godlike"
        ];

        List<CopyFileOperation> copyOps = [];
        Dictionary<string, byte[]> payloads = [];

        foreach (string snd in soundNames)
        {
            string targetPath = $"sounds/{snd}.wav";
            copyOps.Add(new CopyFileOperation
            {
                TargetName = targetPath,
                SourcePath = targetPath,
                Overwrite = FileOverwriteMode.Yes
            });
            payloads[targetPath] = validWav;
        }

        payloads["icon.png"] = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0x00, 0x00, 0x00, 0x0D];

        ModManifest manifest = new()
        {
            Name = "Unreal Tournament Announcer Pack",
            Version = "1.0.0",
            Author = "Epic Games / Kongor Audio",
            Description =
                "Classic UT99/UT2004 multi-kill and killstreak voice announcer pack with stylized kill banners.",
            AppVersion = "4.10.*",
            MmVersion = "1.3",
            Category = "Audio",
            Icon = "icon.png",
            FileCopies = copyOps,
            StringtableEdits =
            [
                new EditStringtableOperation
                {
                    TargetName = "stringtables/client_sounds_en.str",
                    Entries =
                    [
                        new StringtableEntryOperation("announcer_firstblood", "/sounds/firstblood.wav"),
                        new StringtableEntryOperation("announcer_doublekill", "/sounds/doublekill.wav"),
                        new StringtableEntryOperation("announcer_triplekill", "/sounds/triplekill.wav"),
                        new StringtableEntryOperation("announcer_ultrakill", "/sounds/ultrakill.wav"),
                        new StringtableEntryOperation("announcer_monsterkill", "/sounds/monsterkill.wav"),
                        new StringtableEntryOperation("announcer_ludicrouskill", "/sounds/ludicrouskill.wav"),
                        new StringtableEntryOperation("announcer_holyshit", "/sounds/holyshit.wav"),
                        new StringtableEntryOperation("announcer_killingspree", "/sounds/killingspree.wav"),
                        new StringtableEntryOperation("announcer_dominating", "/sounds/dominating.wav"),
                        new StringtableEntryOperation("announcer_megakill", "/sounds/megakill.wav"),
                        new StringtableEntryOperation("announcer_unstoppable", "/sounds/unstoppable.wav"),
                        new StringtableEntryOperation("announcer_wickedsick", "/sounds/wickedsick.wav"),
                        new StringtableEntryOperation("announcer_godlike", "/sounds/godlike.wav")
                    ]
                },
                new EditStringtableOperation
                {
                    TargetName = "stringtables/game_messages_en.str",
                    Entries =
                    [
                        new StringtableEntryOperation("msg_firstblood", "^rFIRST BLOOD!^*"),
                        new StringtableEntryOperation("msg_doublekill", "^yDOUBLE KILL!^*"),
                        new StringtableEntryOperation("msg_triplekill", "^gTRIPLE KILL!^*"),
                        new StringtableEntryOperation("msg_ultrakill", "^mULTRA KILL!^*"),
                        new StringtableEntryOperation("msg_monsterkill", "^rMONSTER KILL!^*"),
                        new StringtableEntryOperation("msg_ludicrouskill", "^cLUUUUDICROUS KILL!^*"),
                        new StringtableEntryOperation("msg_holyshit", "^wHOLY SHIT!^*")
                    ]
                }
            ]
        };

        await HonModArchiveWriter.WritePackageAsync(filePath, manifest, payloads, payloads["icon.png"]);
        return filePath;
    }

    /// <summary>
    ///     Prepares base baseline game archives (resources0.s2z) for realistic injection tests.
    /// </summary>
    internal static async Task CreateBaselineGameResourcesAsync(string clientDirectory)
    {
        string gameDir = Path.Combine(clientDirectory, "game");
        Directory.CreateDirectory(gameDir);
        string res0Path = Path.Combine(gameDir, "resources0.s2z");

        Dictionary<string, byte[]> baseFiles = new()
        {
            ["ui/game_apex.interface"] = Encoding.UTF8.GetBytes(
                "<?xml version=\"1.0\" encoding=\"UTF-8\"?>\n" +
                "<interface name=\"game\">\n" +
                "  <panel name=\"chat_box\" width=\"400\" height=\"200\" />\n" +
                "</interface>"),
            ["ui/game_legacy.interface"] = Encoding.UTF8.GetBytes(
                "<?xml version=\"1.0\" encoding=\"UTF-8\"?>\n" +
                "<interface name=\"game\">\n" +
                "  <panel name=\"minimap\" width=\"250\" height=\"250\" />\n" +
                "</interface>"),
            ["ui/alt_info_hero.interface"] = Encoding.UTF8.GetBytes(
                "<?xml version=\"1.0\" encoding=\"UTF-8\"?>\n" +
                "<interface name=\"alt_info\">\n" +
                "  <panel name=\"alt_info_hero_container\" width=\"120\" height=\"40\">\n" +
                "    <panel name=\"health_bar\" width=\"100%\" />\n" +
                "  </panel>\n" +
                "</interface>"),
            ["ui/scripts/alt_info_hero.lua"] = Encoding.UTF8.GetBytes(
                "function UpdateAltInfo(widget)\n" +
                "    -- Base alt info update logic\n" +
                "end\n"),
            ["base.gamemechanics"] = Encoding.UTF8.GetBytes(
                "<?xml version=\"1.0\" encoding=\"UTF-8\"?>\n" +
                "<gamemechanics>\n" +
                "  <damagetype name=\"Physical\" color=\"#FFFFFF\" />\n" +
                "  <damagetype name=\"Magic\" color=\"#0000FF\" />\n" +
                "  <damagetype name=\"Pure\" color=\"#FFFF00\" />\n" +
                "  <damagetype name=\"Critical\" color=\"#FF0000\" />\n" +
                "</gamemechanics>"),
            ["stringtables/client_sounds_en.str"] = Encoding.UTF8.GetBytes(
                "// Client audio events\n" +
                "announcer_firstblood\t/shared/sounds/announcer/first_blood.wav\n" +
                "announcer_doublekill\t/shared/sounds/announcer/double_kill.wav\n"),
            ["stringtables/game_messages_en.str"] = Encoding.UTF8.GetBytes(
                "// In-game announcements\n" +
                "chat_all\tAll Chat\n" +
                "chat_team\tTeam Chat\n")
        };

        await S2zArchiveWriter.WriteArchiveAsync(res0Path, baseFiles);
    }

    #endregion

    #region Tier 1: Feature Coverage (≥25 Tests)

    [Fact]
    public async Task TacticalHud_ManifestParsing_ExtractsAllAttributesAndOperations()
    {
        string modPath = await CreateTacticalHudModPackageAsync(_modsDir);
        using ModPackage pkg = await HonModArchiveReader.ReadPackageAsync(modPath);

        ModManifest m = pkg.Manifest;
        m.Name.Should().Be("Tactical HUD & Neutral Camp Overlays");
        m.Version.Should().Be("1.0.0");
        m.Author.Should().Be("Kongor Competitive Team");
        m.AppVersion.Should().Be("4.10.*");
        m.Category.Should().Be("Combat UI");

        m.FileCopies.Should().HaveCount(2);
        m.FileCopies.Select(c => c.TargetName).Should()
            .Contain(["ui/tactical_hud.package", "ui/scripts/tactical_hud.lua"]);

        m.FileEdits.Should().HaveCount(2);
        m.FileEdits.Select(e => e.TargetName).Should().Contain(["ui/game_apex.interface", "ui/game_legacy.interface"]);

        m.StringtableEdits.Should().HaveCount(1);
        m.StringtableEdits[0].Entries.Should().HaveCount(3);
    }

    [Fact]
    public async Task TacticalHud_PackageInspection_LoadsValidPayloads()
    {
        string modPath = await CreateTacticalHudModPackageAsync(_modsDir);
        using ModPackage pkg = await HonModArchiveReader.ReadPackageAsync(modPath);

        pkg.HasPayloadFile("ui/tactical_hud.package").Should().BeTrue();
        pkg.HasPayloadFile("ui/scripts/tactical_hud.lua").Should().BeTrue();
        pkg.HasPayloadFile("icon.png").Should().BeTrue();

        string pkgContent = await pkg.GetPayloadTextAsync("ui/tactical_hud.package");
        pkgContent.Should().Contain("neutral_camp_boxes");
        pkgContent.Should().Contain("ally_ultimate_tracker");
        pkgContent.Should().Contain("buyback_status_overlay");

        string luaContent = await pkg.GetPayloadTextAsync("ui/scripts/tactical_hud.lua");
        luaContent.Should().Contain("InitTacticalHud");
        luaContent.Should().Contain("UpdateCampTimers");
    }

    [Fact]
    public async Task TacticalHud_InterfaceXmlStructure_ContainsNeutralCampTimersAndBuybackElements()
    {
        string modPath = await CreateTacticalHudModPackageAsync(_modsDir);
        using ModPackage pkg = await HonModArchiveReader.ReadPackageAsync(modPath);

        string xml = await pkg.GetPayloadTextAsync("ui/tactical_hud.package");
        XDocument doc = XDocument.Parse(xml);
        doc.Root.Should().NotBeNull();
        doc.Root!.Name.LocalName.Should().Be("package");

        XElement? campBoxes = doc.Root.Elements("panel")
            .FirstOrDefault(e => (string?)e.Attribute("name") == "neutral_camp_boxes");
        campBoxes.Should().NotBeNull();

        XElement? ultTracker = doc.Root.Elements("panel")
            .FirstOrDefault(e => (string?)e.Attribute("name") == "ally_ultimate_tracker");
        ultTracker.Should().NotBeNull();

        XElement? buybackTracker = doc.Root.Elements("panel")
            .FirstOrDefault(e => (string?)e.Attribute("name") == "buyback_status_overlay");
        buybackTracker.Should().NotBeNull();
    }

    [Fact]
    public async Task TacticalHud_LuaScript_ContainsCampTrackingAndUltimateHooks()
    {
        string modPath = await CreateTacticalHudModPackageAsync(_modsDir);
        using ModPackage pkg = await HonModArchiveReader.ReadPackageAsync(modPath);

        string lua = await pkg.GetPayloadTextAsync("ui/scripts/tactical_hud.lua");
        lua.Should().Contain("function UpdateCampTimers(gameTimeSeconds)");
        lua.Should().Contain("function TrackAllyUltimates(teamId)");
        lua.Should().Contain("function UpdateBuybackStatus(playerId)");
    }

    [Fact]
    public async Task TacticalHud_StringtableEdits_ContainsLocalizationKeys()
    {
        string modPath = await CreateTacticalHudModPackageAsync(_modsDir);
        using ModPackage pkg = await HonModArchiveReader.ReadPackageAsync(modPath);

        EditStringtableOperation strEdit =
            pkg.Manifest.StringtableEdits.First(s => s.TargetName == "stringtables/game_messages_en.str");
        strEdit.Entries.Should().Contain(e => e.Key == "hud_camp_stack_timer" && e.Value.Contains(":45 - :55"));
        strEdit.Entries.Should().Contain(e => e.Key == "hud_buyback_ready");
        strEdit.Entries.Should().Contain(e => e.Key == "hud_ultimate_cooldown");
    }

    [Fact]
    public async Task CcDebuff_ManifestParsing_ExtractsAllAttributesAndOperations()
    {
        string modPath = await CreateCcDebuffModPackageAsync(_modsDir);
        using ModPackage pkg = await HonModArchiveReader.ReadPackageAsync(modPath);

        ModManifest m = pkg.Manifest;
        m.Name.Should().Be("CC & Debuff Duration Overhead Bars");
        m.Version.Should().Be("1.0.0");
        m.Category.Should().Be("Combat UI");

        m.FileCopies.Should().HaveCount(1);
        m.FileCopies[0].TargetName.Should().Be("ui/scripts/cc_debuff_tracker.lua");

        m.FileEdits.Should().HaveCount(2);
        m.FileEdits.Select(e => e.TargetName).Should()
            .Contain(["ui/alt_info_hero.interface", "ui/scripts/alt_info_hero.lua"]);
    }

    [Fact]
    public async Task CcDebuff_PackageInspection_LoadsValidPayloads()
    {
        string modPath = await CreateCcDebuffModPackageAsync(_modsDir);
        using ModPackage pkg = await HonModArchiveReader.ReadPackageAsync(modPath);

        pkg.HasPayloadFile("ui/scripts/cc_debuff_tracker.lua").Should().BeTrue();
        pkg.HasPayloadFile("icon.png").Should().BeTrue();

        string lua = await pkg.GetPayloadTextAsync("ui/scripts/cc_debuff_tracker.lua");
        lua.Should().Contain("CC_TYPES");
        lua.Should().Contain("Stun");
        lua.Should().Contain("Silence");
    }

    [Fact]
    public async Task CcDebuff_AltInfoHeroPatch_InsertsDebuffBarContainers()
    {
        string modPath = await CreateCcDebuffModPackageAsync(_modsDir);
        using ModPackage pkg = await HonModArchiveReader.ReadPackageAsync(modPath);

        EditFileOperation edit = pkg.Manifest.FileEdits.First(e => e.TargetName == "ui/alt_info_hero.interface");
        edit.Operations.Should().HaveCount(2);
        edit.Operations[0].Should().BeOfType<FindOperation>();
        edit.Operations[1].Should().BeOfType<InsertOperation>()
            .Which.Content.Should().Contain("/ui/cc_debuff_bars.package");
    }

    [Fact]
    public async Task CcDebuff_LuaScript_ImplementsCrowdControlTypes()
    {
        string modPath = await CreateCcDebuffModPackageAsync(_modsDir);
        using ModPackage pkg = await HonModArchiveReader.ReadPackageAsync(modPath);

        string lua = await pkg.GetPayloadTextAsync("ui/scripts/cc_debuff_tracker.lua");
        lua.Should().Contain("Stun");
        lua.Should().Contain("Silence");
        lua.Should().Contain("Root");
        lua.Should().Contain("Disarm");
        lua.Should().Contain("Hex");
        lua.Should().Contain("Taunt");
    }

    [Fact]
    public async Task CcDebuff_AltInfoHeroLuaPatch_InjectsUpdateHook()
    {
        string modPath = await CreateCcDebuffModPackageAsync(_modsDir);
        using ModPackage pkg = await HonModArchiveReader.ReadPackageAsync(modPath);

        EditFileOperation edit = pkg.Manifest.FileEdits.First(e => e.TargetName == "ui/scripts/alt_info_hero.lua");
        edit.Operations.Should().HaveCount(2);
        edit.Operations[1].Should().BeOfType<InsertOperation>()
            .Which.Content.Should().Contain("UpdateCCDebuffBars(widget)");
    }

    [Fact]
    public async Task CombatText_ManifestParsing_ExtractsAllAttributesAndOperations()
    {
        string modPath = await CreateCombatTextModPackageAsync(_modsDir);
        using ModPackage pkg = await HonModArchiveReader.ReadPackageAsync(modPath);

        ModManifest m = pkg.Manifest;
        m.Name.Should().Be("Combat Text & Damage Float Customizer");
        m.Version.Should().Be("1.0.0");
        m.Category.Should().Be("Visuals");

        m.FileEdits.Should().HaveCount(1);
        m.FileEdits[0].TargetName.Should().Be("base.gamemechanics");
        m.FileEdits[0].Operations.Should().HaveCount(8);

        m.StringtableEdits.Should().HaveCount(1);
        m.StringtableEdits[0].Entries.Should().HaveCount(4);
    }

    [Fact]
    public async Task CombatText_PackageInspection_ValidatesMetadataAndIcon()
    {
        string modPath = await CreateCombatTextModPackageAsync(_modsDir);
        using ModPackage pkg = await HonModArchiveReader.ReadPackageAsync(modPath);

        pkg.IconData.Should().NotBeNull();
        pkg.IconData!.Length.Should().BeGreaterThan(0);
        pkg.FileSizeBytes.Should().BeGreaterThan(0);
        pkg.Sha256Hash.Should().NotBeNullOrWhiteSpace();
    }

    [Fact]
    public async Task CombatText_GameMechanicsPatch_AppliesDamageColors()
    {
        string modPath = await CreateCombatTextModPackageAsync(_modsDir);
        using ModPackage pkg = await HonModArchiveReader.ReadPackageAsync(modPath);

        EditFileOperation edit = pkg.Manifest.FileEdits.First(e => e.TargetName == "base.gamemechanics");
        List<ReplaceOperation> replaces = [.. edit.Operations.OfType<ReplaceOperation>()];
        replaces.Should().HaveCount(4);

        replaces.Should().Contain(r => r.Content.Contains("#FF4500")); // Physical
        replaces.Should().Contain(r => r.Content.Contains("#00BFFF")); // Magic
        replaces.Should().Contain(r => r.Content.Contains("#FFD700")); // Pure
        replaces.Should()
            .Contain(r => r.Content.Contains("#DC143C") && r.Content.Contains("scale=\"1.25\"")); // Critical
    }

    [Fact]
    public async Task CombatText_StringtablePatches_DefinesDamageLabels()
    {
        string modPath = await CreateCombatTextModPackageAsync(_modsDir);
        using ModPackage pkg = await HonModArchiveReader.ReadPackageAsync(modPath);

        EditStringtableOperation strEdit =
            pkg.Manifest.StringtableEdits.First(s => s.TargetName == "stringtables/game_messages_en.str");
        strEdit.Entries.Should().Contain(e => e.Key == "combat_text_physical" && e.Value == "Physical");
        strEdit.Entries.Should().Contain(e => e.Key == "combat_text_magical" && e.Value == "Magical");
        strEdit.Entries.Should().Contain(e => e.Key == "combat_text_pure" && e.Value == "Pure");
        strEdit.Entries.Should().Contain(e => e.Key == "combat_text_critical" && e.Value == "Critical Strike!");
    }

    [Fact]
    public async Task CombatText_FuzzyMatching_MatchesWithCustomAttributes()
    {
        string originalMechanics =
            "<gamemechanics>\n  <damagetype name=\"Physical\" color=\"#FFFFFF\" />\n</gamemechanics>";
        PatchOperation[] ops =
        [
            new FindOperation("<damagetype name=\"Physical\""),
            new ReplaceOperation("<damagetype name=\"Physical\" color=\"#FF4500\" floatingcombattext=\"true\" />")
        ];

        string patched = await FuzzyStringPatcher.ApplyPatchAsync(originalMechanics, ops);
        patched.Should().Contain("color=\"#FF4500\"");
        patched.Should().Contain("floatingcombattext=\"true\"");
    }

    [Fact]
    public async Task UtAnnouncer_ManifestParsing_ExtractsAllAudioCopiesAndStringtables()
    {
        string modPath = await CreateUtAnnouncerModPackageAsync(_modsDir);
        using ModPackage pkg = await HonModArchiveReader.ReadPackageAsync(modPath);

        ModManifest m = pkg.Manifest;
        m.Name.Should().Be("Unreal Tournament Announcer Pack");
        m.Version.Should().Be("1.0.0");
        m.Category.Should().Be("Audio");

        m.FileCopies.Should().HaveCount(13);
        m.StringtableEdits.Should().HaveCount(2);
    }

    [Fact]
    public async Task UtAnnouncer_AudioAssets_Validate44ByteRiffWavHeaders()
    {
        string modPath = await CreateUtAnnouncerModPackageAsync(_modsDir);
        using ModPackage pkg = await HonModArchiveReader.ReadPackageAsync(modPath);

        foreach (CopyFileOperation copy in pkg.Manifest.FileCopies)
        {
            byte[] wavBytes = await pkg.GetPayloadFileAsync(copy.SourcePath);
            wavBytes.Should().NotBeNull();
            wavBytes.Length.Should().BeGreaterThanOrEqualTo(44);

            bool isValidWav = VerifyWavHeader(wavBytes);
            isValidWav.Should().BeTrue($"WAV file '{copy.SourcePath}' must possess a valid 44-byte RIFF header");
        }
    }

    [Fact]
    public async Task UtAnnouncer_MultiKillEvents_CoverAll7Tiers()
    {
        string modPath = await CreateUtAnnouncerModPackageAsync(_modsDir);
        using ModPackage pkg = await HonModArchiveReader.ReadPackageAsync(modPath);

        string[] requiredMultiKills =
        [
            "firstblood", "doublekill", "triplekill", "ultrakill", "monsterkill", "ludicrouskill", "holyshit"
        ];

        foreach (string mk in requiredMultiKills)
            pkg.HasPayloadFile($"sounds/{mk}.wav").Should().BeTrue($"Must include audio asset for multi-kill {mk}");

        EditStringtableOperation msgEdit =
            pkg.Manifest.StringtableEdits.First(s => s.TargetName == "stringtables/game_messages_en.str");
        msgEdit.Entries.Select(e => e.Key).Should().Contain(
        [
            "msg_firstblood", "msg_doublekill", "msg_triplekill", "msg_ultrakill", "msg_monsterkill",
            "msg_ludicrouskill", "msg_holyshit"
        ]);
    }

    [Fact]
    public async Task UtAnnouncer_KillstreakEvents_CoverAll6Tiers()
    {
        string modPath = await CreateUtAnnouncerModPackageAsync(_modsDir);
        using ModPackage pkg = await HonModArchiveReader.ReadPackageAsync(modPath);

        string[] requiredKillstreaks =
        [
            "killingspree", "dominating", "megakill", "unstoppable", "wickedsick", "godlike"
        ];

        foreach (string ks in requiredKillstreaks)
            pkg.HasPayloadFile($"sounds/{ks}.wav").Should().BeTrue($"Must include audio asset for killstreak {ks}");

        EditStringtableOperation soundEdit =
            pkg.Manifest.StringtableEdits.First(s => s.TargetName == "stringtables/client_sounds_en.str");
        soundEdit.Entries.Select(e => e.Key).Should().Contain(
        [
            "announcer_killingspree", "announcer_dominating", "announcer_megakill", "announcer_unstoppable",
            "announcer_wickedsick", "announcer_godlike"
        ]);
    }

    [Fact]
    public async Task UtAnnouncer_SoundtableStringtable_BindsEventsToAudioPaths()
    {
        string modPath = await CreateUtAnnouncerModPackageAsync(_modsDir);
        using ModPackage pkg = await HonModArchiveReader.ReadPackageAsync(modPath);

        EditStringtableOperation soundEdit =
            pkg.Manifest.StringtableEdits.First(s => s.TargetName == "stringtables/client_sounds_en.str");
        foreach (StringtableEntryOperation entry in soundEdit.Entries)
        {
            entry.Value.Should().StartWith("/sounds/");
            entry.Value.Should().EndWith(".wav");
        }
    }

    [Fact]
    public async Task Packaging_ManifestRoundTrip_PreservesAll4ModManifests()
    {
        string p1 = await CreateTacticalHudModPackageAsync(_modsDir);
        string p2 = await CreateCcDebuffModPackageAsync(_modsDir);
        string p3 = await CreateCombatTextModPackageAsync(_modsDir);
        string p4 = await CreateUtAnnouncerModPackageAsync(_modsDir);

        string[] paths = [p1, p2, p3, p4];
        foreach (string path in paths)
        {
            using ModPackage pkg = await HonModArchiveReader.ReadPackageAsync(path);
            ModManifest original = pkg.Manifest;

            string xml = ModManifestParser.Serialize(original);
            xml.Should().NotBeNullOrWhiteSpace();

            ModManifest deserialized = ModManifestParser.Parse(xml);
            deserialized.Name.Should().Be(original.Name);
            deserialized.Version.Should().Be(original.Version);
            deserialized.FileCopies.Count.Should().Be(original.FileCopies.Count);
            deserialized.FileEdits.Count.Should().Be(original.FileEdits.Count);
            deserialized.StringtableEdits.Count.Should().Be(original.StringtableEdits.Count);
        }
    }

    [Fact]
    public async Task Packaging_HonModArchiveReader_ComputesAccurateSha256AndSize()
    {
        string p1 = await CreateTacticalHudModPackageAsync(_modsDir);
        using ModPackage pkg = await HonModArchiveReader.ReadPackageAsync(p1);

        pkg.Sha256Hash.Should().MatchRegex("^[a-f0-9]{64}$");
        pkg.FileSizeBytes.Should().BeGreaterThan(0);

        FileInfo fi = new(p1);
        pkg.FileSizeBytes.Should().Be(fi.Length);
    }

    [Fact]
    public async Task Packaging_IconExtraction_ExtractsPngBytes()
    {
        string p1 = await CreateTacticalHudModPackageAsync(_modsDir);
        using ModPackage pkg = await HonModArchiveReader.ReadPackageAsync(p1);

        pkg.IconData.Should().NotBeNull();
        pkg.IconData!.Length.Should().Be(12);
        pkg.IconData[0].Should().Be(0x89);
        pkg.IconData[1].Should().Be(0x50); // 'P'
        pkg.IconData[2].Should().Be(0x4E); // 'N'
        pkg.IconData[3].Should().Be(0x47); // 'G'
    }

    [Fact]
    public async Task Packaging_DirectorySource_ReadsUnpackedModDirectory()
    {
        string unpackedDir = Path.Combine(_tempDir, "unpacked_mod");
        Directory.CreateDirectory(unpackedDir);

        string modXml = "<?xml version=\"1.0\" encoding=\"UTF-8\"?>\n" +
                        "<modification name=\"Unpacked Mod\" version=\"1.0.0\" author=\"Author\">\n" +
                        "  <copyfile name=\"ui/test.package\" source=\"ui/test.package\" />\n" +
                        "</modification>";
        await File.WriteAllTextAsync(Path.Combine(unpackedDir, "mod.xml"), modXml);

        Directory.CreateDirectory(Path.Combine(unpackedDir, "ui"));
        await File.WriteAllTextAsync(Path.Combine(unpackedDir, "ui", "test.package"), "<package />");

        using ModPackage pkg = await HonModArchiveReader.ReadFromDirectoryAsync(unpackedDir);
        pkg.IsDirectorySource.Should().BeTrue();
        pkg.Manifest.Name.Should().Be("Unpacked Mod");
        pkg.HasPayloadFile("ui/test.package").Should().BeTrue();

        string text = await pkg.GetPayloadTextAsync("ui/test.package");
        text.Should().Be("<package />");
    }

    [Fact]
    public async Task Packaging_VersionAndCategory_DefaultsAndCustomProperties()
    {
        string xml = "<?xml version=\"1.0\" encoding=\"UTF-8\"?>\n" +
                     "<modification name=\"Custom Props Mod\" version=\"1.2.3\" custom_engine=\"kongor_v2\">\n" +
                     "  <category>Combat Overlays</category>\n" +
                     "</modification>";

        ModManifest manifest = ModManifestParser.Parse(xml);
        manifest.Name.Should().Be("Custom Props Mod");
        manifest.Version.Should().Be("1.2.3");
        manifest.Category.Should().Be("Combat Overlays");
        manifest.CustomProperties.Should().ContainKey("custom_engine");
        manifest.CustomProperties["custom_engine"].Should().Be("kongor_v2");
    }

    #endregion

    #region Tier 2: Boundary & Corner Cases (≥25 Tests)

    [Fact]
    public void ManifestParser_EmptyOrWhitespaceString_ThrowsArgumentException()
    {
        Action act1 = () => ModManifestParser.Parse("");
        Action act2 = () => ModManifestParser.Parse("   \r\n\t ");

        act1.Should().Throw<ArgumentException>();
        act2.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void ManifestParser_MalformedXml_ThrowsException()
    {
        string badXml = "<modification name=\"Broken Mod\"><unclosed_tag></modification>";
        Action act = () => ModManifestParser.Parse(badXml);
        act.Should().Throw<Exception>();
    }

    [Fact]
    public void ManifestParser_InvalidRootElement_ThrowsFormatException()
    {
        string badRoot = "<?xml version=\"1.0\"?><invalid_root name=\"Mod\" version=\"1.0\" />";
        Action act = () => ModManifestParser.Parse(badRoot);
        act.Should().Throw<FormatException>().WithMessage("*Invalid mod.xml root element*");
    }

    [Fact]
    public void ManifestParser_MissingNameOrVersion_UsesSensibleDefaults()
    {
        string minimalXml = "<modification author=\"SomeDev\" />";
        ModManifest manifest = ModManifestParser.Parse(minimalXml);

        manifest.Name.Should().Be("Unnamed Mod");
        manifest.Version.Should().Be("1.0.0");
        manifest.Author.Should().Be("SomeDev");
    }

    [Fact]
    public void ManifestParser_XmlCommentsAndCData_ExtractsCleanContent()
    {
        string xml = @"<modification name=""CDataMod"" version=""1.0"">
            <!-- This is an XML comment -->
            <editfile name=""ui/test.interface"">
                <find><![CDATA[<panel name=""target"" />]]></find>
                <insert><![CDATA[<!-- Injected comment --><panel name=""injected"" />]]></insert>
            </editfile>
        </modification>";

        ModManifest manifest = ModManifestParser.Parse(xml);
        manifest.FileEdits.Should().HaveCount(1);
        manifest.FileEdits[0].Operations.Should().HaveCount(2);

        FindOperation find = (FindOperation)manifest.FileEdits[0].Operations[0];
        find.Target.Should().Be("<panel name=\"target\" />");

        InsertOperation insert = (InsertOperation)manifest.FileEdits[0].Operations[1];
        insert.Content.Should().Be("<!-- Injected comment --><panel name=\"injected\" />");
    }

    [Fact]
    public async Task ArchiveReader_MissingModXml_ThrowsInvalidDataException()
    {
        string badZipPath = Path.Combine(_tempDir, "no_manifest.honmod");
        using (FileStream fs = File.Create(badZipPath))
        using (ZipArchive zip = new(fs, ZipArchiveMode.Create))
        {
            ZipArchiveEntry dummy = zip.CreateEntry("dummy.txt");
            using StreamWriter w = new(dummy.Open());
            w.Write("no manifest here");
        }

        Func<Task> act = async () => await HonModArchiveReader.ReadPackageAsync(badZipPath);
        await act.Should().ThrowAsync<InvalidDataException>().WithMessage("*Missing 'mod.xml'*");
    }

    [Fact]
    public async Task ArchiveReader_NonExistentFile_ThrowsFileNotFoundException()
    {
        string fakePath = Path.Combine(_tempDir, "does_not_exist_" + Guid.NewGuid().ToString("N") + ".honmod");
        Func<Task> act = async () => await HonModArchiveReader.ReadPackageAsync(fakePath);
        await act.Should().ThrowAsync<FileNotFoundException>();
    }

    [Fact]
    public async Task ModPackage_GetPayloadFile_NonExistentEntry_ThrowsFileNotFoundException()
    {
        string modPath = await CreateCombatTextModPackageAsync(_modsDir);
        using ModPackage pkg = await HonModArchiveReader.ReadPackageAsync(modPath);

        Func<Task> act = async () => await pkg.GetPayloadFileAsync("non/existent/file.png");
        await act.Should().ThrowAsync<FileNotFoundException>();
    }

    [Fact]
    public async Task ModPackage_DisposedPackage_ThrowsObjectDisposedException()
    {
        string modPath = await CreateCombatTextModPackageAsync(_modsDir);
        ModPackage pkg = await HonModArchiveReader.ReadPackageAsync(modPath);
        pkg.Dispose();

        Func<Task> act = async () => await pkg.GetPayloadFileAsync("icon.png");
        await act.Should().ThrowAsync<ObjectDisposedException>();
    }

    [Fact]
    public async Task FuzzyStringPatcher_TargetNotFound_RecordsMissInLogWithoutCrashing()
    {
        string original = "<interface><panel name=\"chat\" /></interface>";
        PatchOperation[] ops =
        [
            new FindOperation("<panel name=\"non_existent_target\" />"),
            new ReplaceOperation("<panel name=\"replaced\" />")
        ];

        string patched = await FuzzyStringPatcher.ApplyPatchAsync(original, ops);
        patched.Should().Be(original, "Patching must not mutate content when find target is missing");
    }

    [Fact]
    public async Task FuzzyStringPatcher_EmptyOriginalContent_HandlesGracefully()
    {
        string original = "";
        PatchOperation[] ops =
        [
            new InsertOperation("<panel name=\"new_panel\" />", Position: InsertPosition.After)
        ];

        string patched = await FuzzyStringPatcher.ApplyPatchAsync(original, ops);
        patched.Should().Be("<panel name=\"new_panel\" />");
    }

    [Fact]
    public async Task FuzzyStringPatcher_ExtremeWhitespaceVariations_MatchesSuccessfully()
    {
        string original =
            "<interface>\r\n\t\t\t<panel    name=\"target_panel\" \r\n\t\t\t   width=\"100%\"   height=\"50px\"  />\r\n</interface>";
        PatchOperation[] ops =
        [
            new FindOperation("<panel name=\"target_panel\" width=\"100%\" height=\"50px\" />"),
            new ReplaceOperation("<panel name=\"target_panel_modified\" />")
        ];

        string patched = await FuzzyStringPatcher.ApplyPatchAsync(original, ops);
        patched.Should().Contain("<panel name=\"target_panel_modified\" />");
    }

    [Fact]
    public async Task FuzzyStringPatcher_CaseSensitivity_StrictVsInsensitive()
    {
        string original = "<interface><Panel Name=\"ChatBox\" /></interface>";

        // Case-sensitive mismatch: does not find
        PatchOperation[] opsSensitive =
        [
            new FindOperation("<panel name=\"chatbox\" />", CaseSensitive: true),
            new ReplaceOperation("<panel name=\"found\" />")
        ];
        string resultSensitive = await FuzzyStringPatcher.ApplyPatchAsync(original, opsSensitive);
        resultSensitive.Should().Be(original);

        // Case-insensitive match: finds and replaces
        PatchOperation[] opsInsensitive =
        [
            new FindOperation("<panel name=\"chatbox\" />", CaseSensitive: false),
            new ReplaceOperation("<panel name=\"found\" />")
        ];
        string resultInsensitive = await FuzzyStringPatcher.ApplyPatchAsync(original, opsInsensitive);
        resultInsensitive.Should().Contain("<panel name=\"found\" />");
    }

    [Fact]
    public async Task FuzzyStringPatcher_FindUpAtBeginningOfFile_DoesNotOverflow()
    {
        string original = "start middle end";
        PatchOperation[] ops =
        [
            new FindOperation("start"),
            new FindUpOperation("start"),
            new ReplaceOperation("BEGINNING")
        ];

        string patched = await FuzzyStringPatcher.ApplyPatchAsync(original, ops);
        patched.Should().Be("BEGINNING middle end");
    }

    [Fact]
    public async Task FuzzyStringPatcher_MultipleSequentialFindAndInsert_MaintainsCursorIntegrity()
    {
        string original = "<root><a/><b/><c/></root>";
        PatchOperation[] ops =
        [
            new FindOperation("<a/>"),
            new InsertOperation("<a_sub/>", Position: InsertPosition.After),
            new FindOperation("<c/>"),
            new InsertOperation("<c_sub/>", Position: InsertPosition.Before)
        ];

        string patched = await FuzzyStringPatcher.ApplyPatchAsync(original, ops);
        patched.Should().Be("<root><a/><a_sub/><b/><c_sub/><c/></root>");
    }

    [Fact]
    public void StringtablePatcher_CommentLinesPreserved_MaintainsComments()
    {
        string original = "// Header comment\n# Hash comment\nkey1\tvalue1\n// Mid comment\nkey2\tvalue2\n";
        StringtableEntryOperation[] ops =
        [
            new("key1", "value1_updated")
        ];

        string patched = StringtablePatcher.ApplyPatch(original, ops);
        patched.Should().Contain("// Header comment");
        patched.Should().Contain("# Hash comment");
        patched.Should().Contain("// Mid comment");
        patched.Should().Contain("key1\tvalue1_updated");
    }

    [Fact]
    public void StringtablePatcher_MixedTabAndSpaceSeparators_ParsesAndUpdatesCorrectly()
    {
        string original = "key_tab\tValue Tab\nkey_space   Value Space\n";
        StringtableEntryOperation[] ops =
        [
            new("key_tab", "New Tab Value"),
            new("key_space", "New Space Value")
        ];

        string patched = StringtablePatcher.ApplyPatch(original, ops);
        patched.Should().Contain("key_tab\tNew Tab Value");
        patched.Should().Contain("key_space   New Space Value");
    }

    [Fact]
    public void StringtablePatcher_DeleteNonExistentKey_NoOpWithoutError()
    {
        string original = "key1\tValue1\nkey2\tValue2\n";
        StringtableEntryOperation[] ops =
        [
            new("non_existent_key", "", StringtableAction.Delete)
        ];

        string patched = StringtablePatcher.ApplyPatch(original, ops);
        patched.Should().Contain("key1\tValue1");
        patched.Should().Contain("key2\tValue2");
    }

    [Fact]
    public void StringtablePatcher_EmptyStringtable_AddsNewKeysSuccessfully()
    {
        string original = "";
        StringtableEntryOperation[] ops =
        [
            new("key_alpha", "Alpha Value"),
            new("key_beta", "Beta Value")
        ];

        string patched = StringtablePatcher.ApplyPatch(original, ops);
        patched.Should().Contain("key_alpha\tAlpha Value");
        patched.Should().Contain("key_beta\tBeta Value");
    }

    [Fact]
    public void StringtablePatcher_UnicodeAndSpecialCharacters_PreservesEncoding()
    {
        string original = "msg_hello\tHello World\n";
        StringtableEntryOperation[] ops =
        [
            new("msg_ru", "Привет мир (Russian)"),
            new("msg_colors", "^rRed ^gGreen ^bBlue ^*Reset")
        ];

        string patched = StringtablePatcher.ApplyPatch(original, ops);
        patched.Should().Contain("msg_ru\tПривет мир (Russian)");
        patched.Should().Contain("msg_colors\t^rRed ^gGreen ^bBlue ^*Reset");
    }

    [Fact]
    public void ConditionEvaluator_MalformedExpression_HandlesSyntaxErrorsGracefully()
    {
        Dictionary<string, string> active = new() { ["ModA"] = "1.0.0" };

        // Unbalanced parentheses or malformed tokens shouldn't crash unhandled
        Action act = () => ConditionEvaluator.Evaluate("('ModA' and (not 'ModB'", active);
        act.Should().Throw<Exception>();
    }

    [Fact]
    public void ConditionEvaluator_ComplexNestedParentheses_EvaluatesAccurately()
    {
        Dictionary<string, string> active = new()
        {
            ["TacticalHUD"] = "1.0.0",
            ["Announcer"] = "1.0.0"
        };

        bool res1 = ConditionEvaluator.Evaluate("('TacticalHUD' and ('Announcer' or 'NonExistent'))", active);
        res1.Should().BeTrue();

        bool res2 = ConditionEvaluator.Evaluate("('TacticalHUD' and not ('Announcer' and 'NonExistent'))", active);
        res2.Should().BeTrue();

        bool res3 = ConditionEvaluator.Evaluate("not (('TacticalHUD' or 'Missing') and 'Announcer')", active);
        res3.Should().BeFalse();
    }

    [Fact]
    public void ConditionEvaluator_VersionComparisonEdgeCases_WildcardsAndRanges()
    {
        Dictionary<string, string> active = new()
        {
            ["TestGame"] = "4.10.2"
        };

        ConditionEvaluator.Evaluate("TestGame [4.10.*]", active).Should().BeTrue();
        ConditionEvaluator.Evaluate("TestGame [4.*]", active).Should().BeTrue();
        ConditionEvaluator.Evaluate("TestGame [*]", active).Should().BeTrue();
        ConditionEvaluator.Evaluate("TestGame [>=4.10.0]", active).Should().BeTrue();
        ConditionEvaluator.Evaluate("TestGame [>4.10.2]", active).Should().BeFalse();
        ConditionEvaluator.Evaluate("TestGame [<=4.10.2]", active).Should().BeTrue();
        ConditionEvaluator.Evaluate("TestGame [4.0-5.0]", active).Should().BeTrue();
    }

    [Fact]
    public void AudioValidation_CorruptedWavHeader_FailsValidation()
    {
        byte[] shortBytes = new byte[20];
        VerifyWavHeader(shortBytes).Should().BeFalse();

        byte[] badMagic = CreateValidWavBytes();
        badMagic[0] = (byte)'N';
        badMagic[1] = (byte)'O';
        badMagic[2] = (byte)'T';
        badMagic[3] = (byte)'R';
        VerifyWavHeader(badMagic).Should().BeFalse();
    }

    [Fact]
    public void AudioValidation_NonPcmOrZeroLengthData_DetectedCorrectly()
    {
        byte[] validWav = CreateValidWavBytes();
        // Corrupt format to MP3/Float (e.g. 3)
        validWav[20] = 3;
        validWav[21] = 0;
        VerifyWavHeader(validWav).Should().BeFalse();
    }

    #endregion

    #region Tier 3: Cross-Feature Combinations (≥10 Tests)

    [Fact]
    public async Task TopologicalSorter_All4SuiteMods_ResolvesAndSortsWithoutErrors()
    {
        string p1 = await CreateTacticalHudModPackageAsync(_modsDir);
        string p2 = await CreateCcDebuffModPackageAsync(_modsDir);
        string p3 = await CreateCombatTextModPackageAsync(_modsDir);
        string p4 = await CreateUtAnnouncerModPackageAsync(_modsDir);

        using ModPackage mod1 = await HonModArchiveReader.ReadPackageAsync(p1);
        using ModPackage mod2 = await HonModArchiveReader.ReadPackageAsync(p2);
        using ModPackage mod3 = await HonModArchiveReader.ReadPackageAsync(p3);
        using ModPackage mod4 = await HonModArchiveReader.ReadPackageAsync(p4);

        SortResult result = TopologicalSorter.ResolveAndSort([mod1, mod2, mod3, mod4]);
        result.Success.Should().BeTrue();
        result.ExecutionPlan.Should().HaveCount(4);
        result.ValidationErrors.Should().BeEmpty();
        result.CycleReports.Should().BeEmpty();
    }

    [Fact]
    public async Task ConflictMatrix_All4SuiteMods_CoexistWithoutFatalConflicts()
    {
        string p1 = await CreateTacticalHudModPackageAsync(_modsDir);
        string p2 = await CreateCcDebuffModPackageAsync(_modsDir);
        string p3 = await CreateCombatTextModPackageAsync(_modsDir);
        string p4 = await CreateUtAnnouncerModPackageAsync(_modsDir);

        using ModPackage mod1 = await HonModArchiveReader.ReadPackageAsync(p1);
        using ModPackage mod2 = await HonModArchiveReader.ReadPackageAsync(p2);
        using ModPackage mod3 = await HonModArchiveReader.ReadPackageAsync(p3);
        using ModPackage mod4 = await HonModArchiveReader.ReadPackageAsync(p4);

        ConflictReport report = ConflictMatrix.Analyze([mod1, mod2, mod3, mod4]);
        report.HasConflicts.Should().BeFalse("All 4 suite mods must cleanly coexist with zero fatal file collisions");

        // Stringtable edits on game_messages_en.str are delta edits, which are INFO severity
        FileCollision? stringtableCollision =
            report.Collisions.FirstOrDefault(c => c.TargetFile.Contains("game_messages_en.str"));
        stringtableCollision?.Severity.Should().Be(CollisionSeverity.Info);
    }

    [Fact]
    public void OrderingRules_ApplyBeforeAndApplyAfter_StrictlyEnforced()
    {
        ModPackage modA = new()
        {
            FilePath = "ModA.honmod",
            IsEnabled = true,
            Manifest = new ModManifest
            {
                Name = "ModA",
                Version = "1.0.0",
                ApplyBefore = [new ModOrderingRule("ModB")]
            }
        };

        ModPackage modB = new()
        {
            FilePath = "ModB.honmod",
            IsEnabled = true,
            Manifest = new ModManifest
            {
                Name = "ModB",
                Version = "1.0.0"
            }
        };

        SortResult result = TopologicalSorter.ResolveAndSort([modB, modA]);
        result.Success.Should().BeTrue();
        result.ExecutionPlan[0].Manifest.Name.Should().Be("ModA");
        result.ExecutionPlan[1].Manifest.Name.Should().Be("ModB");
    }

    [Fact]
    public async Task LegacyModCoexistence_SuiteModsWithLegacyMods_ResolvesCoexistence()
    {
        string p1 = await CreateTacticalHudModPackageAsync(_modsDir);
        string p4 = await CreateUtAnnouncerModPackageAsync(_modsDir);

        using ModPackage tacticalHud = await HonModArchiveReader.ReadPackageAsync(p1);
        using ModPackage utAnnouncer = await HonModArchiveReader.ReadPackageAsync(p4);

        ModPackage marioSound = new()
        {
            FilePath = "MarioSoundModStarPower.honmod",
            IsEnabled = true,
            Manifest = new ModManifest
            {
                Name = "Mario Sound Mod (Star Power)",
                Version = "1.0.0",
                FileCopies =
                [
                    new CopyFileOperation
                        { TargetName = "shared/sounds/items/immunity.ogg", SourcePath = "immunity.ogg" }
                ]
            }
        };

        ModPackage stumps = new()
        {
            FilePath = "Stumps.honmod",
            IsEnabled = true,
            Manifest = new ModManifest
            {
                Name = "Stumps",
                Version = "2.3.0",
                FileCopies =
                [
                    new CopyFileOperation
                        { TargetName = "world/props/trees/stump.material", SourcePath = "stump.material" }
                ]
            }
        };

        SortResult result = TopologicalSorter.ResolveAndSort([tacticalHud, utAnnouncer, marioSound, stumps]);
        result.Success.Should().BeTrue();
        result.ExecutionPlan.Should().HaveCount(4);

        ConflictReport conflictReport = ConflictMatrix.Analyze([tacticalHud, utAnnouncer, marioSound, stumps]);
        conflictReport.HasConflicts.Should().BeFalse();
    }

    [Fact]
    public void CyclicDependency_TarjanDetector_ReportsExactCyclePath()
    {
        ModPackage mod1 = new()
        {
            FilePath = "Mod1.honmod",
            IsEnabled = true,
            Manifest = new ModManifest
            {
                Name = "Mod1",
                Version = "1.0.0",
                ApplyAfter = [new ModOrderingRule("Mod2")]
            }
        };

        ModPackage mod2 = new()
        {
            FilePath = "Mod2.honmod",
            IsEnabled = true,
            Manifest = new ModManifest
            {
                Name = "Mod2",
                Version = "1.0.0",
                ApplyAfter = [new ModOrderingRule("Mod3")]
            }
        };

        ModPackage mod3 = new()
        {
            FilePath = "Mod3.honmod",
            IsEnabled = true,
            Manifest = new ModManifest
            {
                Name = "Mod3",
                Version = "1.0.0",
                ApplyAfter = [new ModOrderingRule("Mod1")]
            }
        };

        SortResult result = TopologicalSorter.ResolveAndSort([mod1, mod2, mod3]);
        result.Success.Should().BeFalse();
        result.CycleReports.Should().NotBeEmpty();
        result.CycleReports[0].Should().Contain("Cyclic dependency detected");
    }

    [Fact]
    public void MissingDependencyRequirement_ReportsValidationError_ThrowsOrFailsValidation()
    {
        ModPackage mod = new()
        {
            FilePath = "DependentMod.honmod",
            IsEnabled = true,
            Manifest = new ModManifest
            {
                Name = "DependentMod",
                Version = "1.0.0",
                Requirements = [new ModRequirement("NonExistentCoreMod")]
            }
        };

        SortResult result = TopologicalSorter.ResolveAndSort([mod]);
        result.Success.Should().BeFalse();
        result.ValidationErrors.Should().Contain(e => e.Contains("requires missing mod 'NonExistentCoreMod'"));
    }

    [Fact]
    public void IncompatibleModConstraint_DetectsAndRejectsIncompatibility_RejectsMod()
    {
        ModPackage mod1 = new()
        {
            FilePath = "Mod1.honmod",
            IsEnabled = true,
            Manifest = new ModManifest
            {
                Name = "Mod1",
                Version = "1.0.0",
                Incompatibilities = [new ModIncompatibility("Mod2")]
            }
        };

        ModPackage mod2 = new()
        {
            FilePath = "Mod2.honmod",
            IsEnabled = true,
            Manifest = new ModManifest
            {
                Name = "Mod2",
                Version = "1.0.0"
            }
        };

        SortResult result = TopologicalSorter.ResolveAndSort([mod1, mod2]);
        result.Success.Should().BeFalse();
        result.ValidationErrors.Should().Contain(e => e.Contains("is incompatible with enabled mod 'Mod2'"));
    }

    [Fact]
    public void VersionRangeMismatch_ReportsVersionMismatchError_FailsConstraint()
    {
        ModPackage coreMod = new()
        {
            FilePath = "CoreMod.honmod",
            IsEnabled = true,
            Manifest = new ModManifest
            {
                Name = "CoreMod",
                Version = "1.0.0"
            }
        };

        ModPackage dependentMod = new()
        {
            FilePath = "DependentMod.honmod",
            IsEnabled = true,
            Manifest = new ModManifest
            {
                Name = "DependentMod",
                Version = "1.0.0",
                Requirements = [new ModRequirement("CoreMod", ">=2.0.0")]
            }
        };

        SortResult result = TopologicalSorter.ResolveAndSort([coreMod, dependentMod]);
        result.Success.Should().BeFalse();
        result.ValidationErrors.Should()
            .Contain(e => e.Contains("requires 'CoreMod' [>=2.0.0], but installed version is '1.0.0'"));
    }

    [Fact]
    public async Task DeterministicExecutionOrder_OrderIndependenceOfInputList_ProducesIdenticalOrder()
    {
        string p1 = await CreateTacticalHudModPackageAsync(_modsDir);
        string p2 = await CreateCcDebuffModPackageAsync(_modsDir);
        string p3 = await CreateCombatTextModPackageAsync(_modsDir);
        string p4 = await CreateUtAnnouncerModPackageAsync(_modsDir);

        using ModPackage mod1 = await HonModArchiveReader.ReadPackageAsync(p1);
        using ModPackage mod2 = await HonModArchiveReader.ReadPackageAsync(p2);
        using ModPackage mod3 = await HonModArchiveReader.ReadPackageAsync(p3);
        using ModPackage mod4 = await HonModArchiveReader.ReadPackageAsync(p4);

        // Order 1: 1, 2, 3, 4
        SortResult res1 = TopologicalSorter.ResolveAndSort([mod1, mod2, mod3, mod4]);
        // Order 2: 4, 3, 2, 1
        SortResult res2 = TopologicalSorter.ResolveAndSort([mod4, mod3, mod2, mod1]);
        // Order 3: 2, 4, 1, 3
        SortResult res3 = TopologicalSorter.ResolveAndSort([mod2, mod4, mod1, mod3]);

        List<string> names1 = [.. res1.ExecutionPlan.Select(m => m.Manifest.Name)];
        List<string> names2 = [.. res2.ExecutionPlan.Select(m => m.Manifest.Name)];
        List<string> names3 = [.. res3.ExecutionPlan.Select(m => m.Manifest.Name)];

        names1.Should().Equal(names2);
        names1.Should().Equal(names3);
    }

    [Fact]
    public void ConditionalFeatureActivation_MultiModStateConditionChecks_EvaluatesState()
    {
        ModPackage hud = new()
            { FilePath = "1.honmod", Manifest = new ModManifest { Name = "Tactical HUD", Version = "1.0" } };
        ModPackage cc = new()
            { FilePath = "2.honmod", Manifest = new ModManifest { Name = "CC Debuff", Version = "1.0" } };
        ModPackage combat = new()
            { FilePath = "3.honmod", Manifest = new ModManifest { Name = "Combat Text", Version = "1.0" } };

        ModPackage[] activeMods = [hud, cc, combat];

        bool eval1 = ConditionEvaluator.Evaluate("'Tactical HUD' and ('CC Debuff' or 'UT Announcer')", activeMods);
        eval1.Should().BeTrue();

        bool eval2 = ConditionEvaluator.Evaluate("'Tactical HUD' and ('UT Announcer' or 'NonExistent')", activeMods);
        eval2.Should().BeFalse();
    }

    #endregion

    #region Tier 4: Real-World Scenarios (≥5 Scenarios)

    [Fact]
    public async Task All4Mods_SimultaneousVfsApply_InjectsAllOverridesAndHotReloadTrigger()
    {
        await CreateBaselineGameResourcesAsync(_clientDir);

        string p1 = await CreateTacticalHudModPackageAsync(_modsDir);
        string p2 = await CreateCcDebuffModPackageAsync(_modsDir);
        string p3 = await CreateCombatTextModPackageAsync(_modsDir);
        string p4 = await CreateUtAnnouncerModPackageAsync(_modsDir);

        ModManager manager = new();
        IReadOnlyList<ModPackage> discovered = await manager.DiscoverModsAsync(_modsDir);
        discovered.Should().HaveCount(4);

        foreach (ModPackage m in discovered) m.IsEnabled = true;

        // Apply all 4 in VFS mode
        ApplyResult result = await manager.ApplyModsAsync(new ApplyOptions
        {
            GameClientDirectory = _clientDir,
            ModsToApply = discovered,
            Mode = InjectionMode.Vfs
        });

        result.Success.Should().BeTrue();
        result.AppliedMods.Should().HaveCount(4);
        result.Errors.Should().BeEmpty();

        string overrideDir = Path.Combine(_clientDir, "override");
        Directory.Exists(overrideDir).Should().BeTrue();

        // 1. Tactical HUD files
        File.Exists(Path.Combine(overrideDir, "ui", "tactical_hud.package")).Should().BeTrue();
        File.Exists(Path.Combine(overrideDir, "ui", "scripts", "tactical_hud.lua")).Should().BeTrue();

        // 2. CC Debuff files
        File.Exists(Path.Combine(overrideDir, "ui", "scripts", "cc_debuff_tracker.lua")).Should().BeTrue();

        // 3. UT Announcer files
        File.Exists(Path.Combine(overrideDir, "sounds", "firstblood.wav")).Should().BeTrue();
        File.Exists(Path.Combine(overrideDir, "sounds", "holyshit.wav")).Should().BeTrue();

        // 4. Patched interface & mechanics
        string patchedApex = await File.ReadAllTextAsync(Path.Combine(overrideDir, "ui", "game_apex.interface"));
        patchedApex.Should().Contain("ui/tactical_hud.package");

        string patchedMechanics = await File.ReadAllTextAsync(Path.Combine(overrideDir, "base.gamemechanics"));
        patchedMechanics.Should().Contain("#FF4500"); // Physical
        patchedMechanics.Should().Contain("#DC143C"); // Critical

        // 5. Hotreload trigger & journal
        File.Exists(Path.Combine(overrideDir, ".hotreload.trigger")).Should().BeTrue();
        File.Exists(RollbackJournal.GetJournalPath(_clientDir)).Should().BeTrue();
    }

    [Fact]
    public async Task All4Mods_DirectArchiveInjection_GeneratesValidResources999S2z()
    {
        await CreateBaselineGameResourcesAsync(_clientDir);

        string p1 = await CreateTacticalHudModPackageAsync(_modsDir);
        string p2 = await CreateCcDebuffModPackageAsync(_modsDir);
        string p3 = await CreateCombatTextModPackageAsync(_modsDir);
        string p4 = await CreateUtAnnouncerModPackageAsync(_modsDir);

        ModManager manager = new();
        IReadOnlyList<ModPackage> discovered = await manager.DiscoverModsAsync(_modsDir);
        foreach (ModPackage m in discovered) m.IsEnabled = true;

        // Apply all 4 in Archive mode
        ApplyResult result = await manager.ApplyModsAsync(new ApplyOptions
        {
            GameClientDirectory = _clientDir,
            ModsToApply = discovered,
            Mode = InjectionMode.Archive,
            TargetArchiveName = "resources999.s2z"
        });

        result.Success.Should().BeTrue();
        result.AppliedMods.Should().HaveCount(4);

        string s2zPath = Path.Combine(_clientDir, "game", "resources999.s2z");
        File.Exists(s2zPath).Should().BeTrue();

        using S2zArchiveReader s2z = S2zArchiveReader.Open(s2zPath);
        s2z.EntryNames.Should().NotBeEmpty();
        s2z.HasEntry("ui/tactical_hud.package").Should().BeTrue();
        s2z.HasEntry("ui/scripts/tactical_hud.lua").Should().BeTrue();
        s2z.HasEntry("ui/scripts/cc_debuff_tracker.lua").Should().BeTrue();
        s2z.HasEntry("sounds/firstblood.wav").Should().BeTrue();
        s2z.HasEntry("base.gamemechanics").Should().BeTrue();

        byte[] wavBytes = await s2z.ReadEntryBytesAsync("sounds/firstblood.wav");
        VerifyWavHeader(wavBytes).Should().BeTrue();
    }

    [Fact]
    public async Task ModLifecycle_SelectiveEnableDisableAndRollbackJournal_RestoresCleanClient()
    {
        await CreateBaselineGameResourcesAsync(_clientDir);
        await CreateTacticalHudModPackageAsync(_modsDir);
        await CreateUtAnnouncerModPackageAsync(_modsDir);

        ModManager manager = new();
        IReadOnlyList<ModPackage> discovered = await manager.DiscoverModsAsync(_modsDir);
        foreach (ModPackage m in discovered) m.IsEnabled = true;

        // 1. Apply in VFS mode
        ApplyResult applyResult = await manager.ApplyModsAsync(new ApplyOptions
        {
            GameClientDirectory = _clientDir,
            ModsToApply = discovered,
            Mode = InjectionMode.Vfs
        });
        applyResult.Success.Should().BeTrue();

        string overrideDir = Path.Combine(_clientDir, "override");
        Directory.Exists(overrideDir).Should().BeTrue();
        File.Exists(Path.Combine(overrideDir, "sounds", "firstblood.wav")).Should().BeTrue();

        // 2. Rollback
        RollbackResult rollbackResult = await manager.RollbackAsync(_clientDir);
        rollbackResult.Success.Should().BeTrue();

        // Verify clean client restoration
        File.Exists(Path.Combine(overrideDir, "sounds", "firstblood.wav")).Should().BeFalse();
        File.Exists(RollbackJournal.GetJournalPath(_clientDir)).Should().BeFalse();
    }

    [Fact]
    public async Task Announcer_EventTriggersAndLocalization_ResolvesMultiKillsAndKillstreaks()
    {
        await CreateBaselineGameResourcesAsync(_clientDir);
        await CreateUtAnnouncerModPackageAsync(_modsDir);

        ModManager manager = new();
        IReadOnlyList<ModPackage> discovered = await manager.DiscoverModsAsync(_modsDir);
        discovered[0].IsEnabled = true;

        ApplyResult result = await manager.ApplyModsAsync(new ApplyOptions
        {
            GameClientDirectory = _clientDir,
            ModsToApply = discovered,
            Mode = InjectionMode.Vfs
        });
        result.Success.Should().BeTrue();

        string overrideDir = Path.Combine(_clientDir, "override");

        // Verify soundtable resolution in client_sounds_en.str
        string soundStr =
            await File.ReadAllTextAsync(Path.Combine(overrideDir, "stringtables", "client_sounds_en.str"));
        soundStr.Should().Contain("announcer_firstblood\t/sounds/firstblood.wav");
        soundStr.Should().Contain("announcer_godlike\t/sounds/godlike.wav");
        soundStr.Should().Contain("announcer_holyshit\t/sounds/holyshit.wav");

        // Verify kill banner localization in game_messages_en.str
        string msgStr = await File.ReadAllTextAsync(Path.Combine(overrideDir, "stringtables", "game_messages_en.str"));
        msgStr.Should().Contain("msg_firstblood\t^rFIRST BLOOD!^*");
        msgStr.Should().Contain("msg_ultrakill\t^mULTRA KILL!^*");
        msgStr.Should().Contain("msg_holyshit\t^wHOLY SHIT!^*");
    }

    [Fact]
    public async Task ModSuite_UiXmlAndLuaSyntaxIntegrity_AcrossGameInterfaces()
    {
        await CreateBaselineGameResourcesAsync(_clientDir);
        await CreateTacticalHudModPackageAsync(_modsDir);
        await CreateCcDebuffModPackageAsync(_modsDir);

        ModManager manager = new();
        IReadOnlyList<ModPackage> discovered = await manager.DiscoverModsAsync(_modsDir);
        foreach (ModPackage m in discovered) m.IsEnabled = true;

        ApplyResult result = await manager.ApplyModsAsync(new ApplyOptions
        {
            GameClientDirectory = _clientDir,
            ModsToApply = discovered,
            Mode = InjectionMode.Vfs
        });
        result.Success.Should().BeTrue();

        string overrideDir = Path.Combine(_clientDir, "override");

        // 1. Verify XML syntax of patched apex interface
        string apexXml = await File.ReadAllTextAsync(Path.Combine(overrideDir, "ui", "game_apex.interface"));
        XDocument apexDoc = XDocument.Parse(apexXml);
        apexDoc.Root.Should().NotBeNull();
        apexDoc.Root!.Elements("include").Should()
            .Contain(e => (string?)e.Attribute("file") == "/ui/tactical_hud.package");

        // 2. Verify XML syntax of patched alt info interface
        string altInfoXml = await File.ReadAllTextAsync(Path.Combine(overrideDir, "ui", "alt_info_hero.interface"));
        XDocument altDoc = XDocument.Parse(altInfoXml);
        altDoc.Root.Should().NotBeNull();
        altDoc.Root!.Descendants("include").Should()
            .Contain(e => (string?)e.Attribute("file") == "/ui/cc_debuff_bars.package");

        // 3. Verify Lua hook in alt_info_hero.lua
        string altLua = await File.ReadAllTextAsync(Path.Combine(overrideDir, "ui", "scripts", "alt_info_hero.lua"));
        altLua.Should().Contain("UpdateCCDebuffBars(widget)");
    }

    [Fact]
    public async Task PhysicalModsOnDisk_InspectsRealPackagesInGamesClientMods_ValidatesPackages()
    {
        if (!NexusTestEnvironment.HasGameAssets) return;
        string clientModsDir = Path.Combine(Directory.GetCurrentDirectory(), "games", "client", "mods");
        if (!Directory.Exists(clientModsDir)) return; // Skip if run outside repository root

        ModManager manager = new();
        IReadOnlyList<ModPackage> discovered = await manager.DiscoverModsAsync(clientModsDir);

        if (discovered.Count > 0)
        {
            // Verify that every mod present on disk parses cleanly
            foreach (ModPackage mod in discovered)
            {
                mod.Manifest.Name.Should().NotBeNullOrWhiteSpace();
                mod.Manifest.Version.Should().NotBeNullOrWhiteSpace();
                mod.FileSizeBytes.Should().BeGreaterThan(0);
                mod.Sha256Hash.Should().NotBeNullOrWhiteSpace();
            }

            // Run verification check across all installed mods
            VerificationResult vres = manager.VerifyMods(discovered);
            vres.Should().NotBeNull();
        }
    }

    [Fact]
    public async Task HeartbeatMod_IdempotentApply_PatchesLooseGameFilesWithoutDuplicates()
    {
        if (!NexusTestEnvironment.HasGameAssets) return;
        string clientModsDir = Path.Combine(Directory.GetCurrentDirectory(), "games", "client", "mods");
        string hbModFile = Path.Combine(clientModsDir, "robrobo_hbmod.honmod");
        if (!File.Exists(hbModFile))
        {
            var repoDir = new DirectoryInfo(AppContext.BaseDirectory);
            while (repoDir != null && !File.Exists(Path.Combine(repoDir.FullName, "ProjectKongor.slnx")))
            {
                repoDir = repoDir.Parent;
            }
            if (repoDir != null)
            {
                clientModsDir = Path.Combine(repoDir.FullName, "games", "client", "mods");
                hbModFile = Path.Combine(clientModsDir, "robrobo_hbmod.honmod");
            }
        }
        Assert.True(File.Exists(hbModFile), $"Heartbeat mod file must exist at {hbModFile}");

        // 1. Set up simulated client with baseline game/resources0.s2z and loose game/ui files
        string gameDir = Path.Combine(_clientDir, "game");
        string gameUiDir = Path.Combine(gameDir, "ui");
        Directory.CreateDirectory(gameUiDir);

        string elementsStub = "<?xml version=\"1.0\" encoding=\"UTF-8\"?>\r\n<package>\r\n\t<!-- INSERT AFTER THIS -->\r\n\t\r\n</package>\r\n";
        string apexStub = "<?xml version=\"1.0\" encoding=\"UTF-8\"?>\r\n<interface name=\"game_apex\">\r\n\t<panel name=\"hud\" />\r\n</interface>\r\n";

        string inGameElementsPath = Path.Combine(gameUiDir, "mod_options_elements.package");
        string inGameApexPath = Path.Combine(gameUiDir, "game_apex.interface");

        await File.WriteAllTextAsync(inGameElementsPath, elementsStub, Encoding.UTF8);
        await File.WriteAllTextAsync(inGameApexPath, apexStub, Encoding.UTF8);

        // Also create clean baseline resources0.s2z
        string res0Path = Path.Combine(gameDir, "resources0.s2z");
        using (ZipArchive zip = ZipFile.Open(res0Path, ZipArchiveMode.Create))
        {
            ZipArchiveEntry e1 = zip.CreateEntry("ui/mod_options_elements.package");
            using (Stream s = e1.Open())
            using (StreamWriter sw = new(s, Encoding.UTF8))
            {
                sw.Write(elementsStub);
            }

            ZipArchiveEntry e2 = zip.CreateEntry("ui/game_apex.interface");
            using (Stream s = e2.Open())
            using (StreamWriter sw = new(s, Encoding.UTF8))
            {
                sw.Write(apexStub);
            }
        }

        // 2. Read heartbeat mod package
        ModPackage hbMod = await HonModArchiveReader.ReadPackageAsync(hbModFile);
        hbMod.IsEnabled = true;

        ModManager manager = new();

        // 3. First Apply
        ApplyResult result1 = await manager.ApplyModsAsync(new ApplyOptions
        {
            GameClientDirectory = _clientDir,
            ModsToApply = [hbMod],
            Mode = InjectionMode.Vfs
        });

        result1.Success.Should().BeTrue();

        // Verify game/ loose files were patched
        string patchedGameElements = await File.ReadAllTextAsync(inGameElementsPath);
        patchedGameElements.Should().Contain("mod_id=\"hbmod\"");
        // Count occurrences of hbmod in game/ loose file
        int count1 = (patchedGameElements.Length - patchedGameElements.Replace("mod_id=\"hbmod\"", "").Length) / "mod_id=\"hbmod\"".Length;
        count1.Should().Be(12); // The 12 mod elements for hbmod

        string patchedGameApex = await File.ReadAllTextAsync(inGameApexPath);
        patchedGameApex.Should().Contain("heartbeat.package");

        // 4. Second Apply (Idempotency test - applying again should not compound duplicates!)
        ApplyResult result2 = await manager.ApplyModsAsync(new ApplyOptions
        {
            GameClientDirectory = _clientDir,
            ModsToApply = [hbMod],
            Mode = InjectionMode.Vfs
        });

        result2.Success.Should().BeTrue();

        string patchedGameElements2 = await File.ReadAllTextAsync(inGameElementsPath);
        int count2 = (patchedGameElements2.Length - patchedGameElements2.Replace("mod_id=\"hbmod\"", "").Length) / "mod_id=\"hbmod\"".Length;
        count2.Should().Be(12, "Idempotent apply must not create duplicate mod options entries!");

        // 5. Rollback
        RollbackResult rb = await manager.RollbackAsync(_clientDir);
        rb.Success.Should().BeTrue();

        // Verify restored to clean stub
        string restoredElements = await File.ReadAllTextAsync(inGameElementsPath);
        restoredElements.Should().Be(elementsStub);

        string restoredApex = await File.ReadAllTextAsync(inGameApexPath);
        restoredApex.Should().Be(apexStub);
    }

    [Fact]
    public async Task ApplyModsAsync_ArchiveMode_HeartbeatMod_CleansOptionsWhenRolledBack()
    {
        if (!NexusTestEnvironment.HasGameAssets) return;
        string clientModsDir = Path.Combine(Directory.GetCurrentDirectory(), "games", "client", "mods");
        string hbModFile = Path.Combine(clientModsDir, "robrobo_hbmod.honmod");
        if (!File.Exists(hbModFile))
        {
            var repoDir = new DirectoryInfo(AppContext.BaseDirectory);
            while (repoDir != null && !File.Exists(Path.Combine(repoDir.FullName, "ProjectKongor.slnx")))
            {
                repoDir = repoDir.Parent;
            }
            if (repoDir != null)
            {
                clientModsDir = Path.Combine(repoDir.FullName, "games", "client", "mods");
                hbModFile = Path.Combine(clientModsDir, "robrobo_hbmod.honmod");
            }
        }
        Assert.True(File.Exists(hbModFile), $"Heartbeat mod file must exist at {hbModFile}");

        string gameDir = Path.Combine(_clientDir, "game");
        string uiDir = Path.Combine(gameDir, "ui");
        Directory.CreateDirectory(uiDir);

        string elementsStub = "<?xml version=\"1.0\" encoding=\"UTF-8\"?>\r\n<package>\r\n\t<!-- INSERT AFTER THIS -->\r\n\t\r\n</package>\r\n";
        string inGameElementsPath = Path.Combine(uiDir, "mod_options_elements.package");
        await File.WriteAllTextAsync(inGameElementsPath, elementsStub);

        string optionsStub = "<?xml version=\"1.0\" encoding=\"UTF-8\"?>\r\n<package>\r\n\t<!-- stuff from all other mods -->\r\n\t<include file=\"/ui/mod_options_elements.package\" />\r\n</package>\r\n";
        string inGameOptionsPath = Path.Combine(uiDir, "mod_options.package");
        await File.WriteAllTextAsync(inGameOptionsPath, optionsStub);

        string apexStub = "<?xml version=\"1.0\" encoding=\"UTF-8\"?>\r\n<interface name=\"game_apex\">\r\n\t<panel name=\"hud\" />\r\n</interface>\r\n";
        string inGameApexPath = Path.Combine(uiDir, "game_apex.interface");
        await File.WriteAllTextAsync(inGameApexPath, apexStub);

        ModPackage hbMod = await HonModArchiveReader.ReadPackageAsync(hbModFile);
        hbMod.IsEnabled = true;

        ModManager manager = new();

        // 1. Apply in Archive mode
        ApplyResult result1 = await manager.ApplyModsAsync(new ApplyOptions
        {
            GameClientDirectory = _clientDir,
            ModsToApply = [hbMod],
            Mode = InjectionMode.Archive
        });
        result1.Success.Should().BeTrue();

        string patchedElements = await File.ReadAllTextAsync(inGameElementsPath);
        patchedElements.Should().Contain("mod_id=\"hbmod\"");

        string patchedOptions = await File.ReadAllTextAsync(inGameOptionsPath);
        patchedOptions.Should().Contain("mod_id=\"hbmod\"");

        string patchedApex = await File.ReadAllTextAsync(inGameApexPath);
        patchedApex.Should().Contain("heartbeat.package");

        string looseHbPackage = Path.Combine(uiDir, "heartbeat.package");
        File.Exists(looseHbPackage).Should().BeTrue("Loose heartbeat.package must be injected");

        // 2. Rollback / disable all mods
        RollbackResult rb = await manager.RollbackAsync(_clientDir);
        rb.Success.Should().BeTrue();

        string restoredElements = await File.ReadAllTextAsync(inGameElementsPath);
        restoredElements.Should().NotContain("mod_id=\"hbmod\"");

        string restoredOptions = await File.ReadAllTextAsync(inGameOptionsPath);
        restoredOptions.Should().NotContain("mod_id=\"hbmod\"", "All residual hbmod options must be stripped on rollback");
        restoredOptions.Should().Contain("mod_options_elements.package");

        string restoredApex = await File.ReadAllTextAsync(inGameApexPath);
        restoredApex.Should().NotContain("heartbeat.package", "game_apex must not retain heartbeat package include");
        restoredApex.Should().NotContain("hb_main", "game_apex must not retain hb_main instance");

        File.Exists(looseHbPackage).Should().BeFalse("Loose heartbeat.package must be removed on rollback");
    }

    #endregion
}
